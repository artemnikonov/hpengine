// SHIPPED SOUND CATALOG — ONE FOR ALL FRONTENDS.
//
// Why here: a sound's duration is the answer of native GetSoundDuration, from which scripts compute dialog timing
// (baseDialog.DeliverDialog, SpellLearnTrigger.Say*). A single FSoundData::GetPeriod formula reads the SAME .u/.uax
// bytes as the original, instead of each frontend deriving it from its decoded clip (two bodies for one answer).
//
// Object paths are exact (Package.Group.Name, case-insensitive like FName); no lookup by leaf name.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UELib;
using UELib.Core;

namespace HP1.UEScript
{
    // Reference to a loaded sound. Derives from the resource marker so scripts hold it as an ordinary Sound value
    // while Core knows its duration.
    public sealed class UESoundRef : UEResourceRef
    {
        public float Duration { get; }
        // Sound data loop flag (FSoundData +0x54, assert "PSound->Data.IsLooping()" in UnAudio.cpp): at the end of the
        // data the stream returns to the start (Engine.dll FUN_1037e550 mp2 / FUN_1037e210 PCM). The loop is decided
        // by the sound DATA, not by a name list.
        public bool Looping { get; }

        public UESoundRef(string name, string objectPath, float duration, bool looping = false)
            : base("Sound", name, objectPath)
        {
            Duration = duration;
            Looping = looping;
        }
    }

    public sealed class UESoundCatalog
    {
        // The catalog belongs to the game INSTALLATION, not to a level: sound packages do not change between maps.
        // The root is set by the frontend / harness via SetSource (e.g. hpengine/SdlAudio.cs).
        public static readonly UESoundCatalog Shared = new UESoundCatalog();

        private readonly Dictionary<string, string> _packageFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, UnrealPackage> _packages = new Dictionary<string, UnrealPackage>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, UESoundRef> _sounds = new Dictionary<string, UESoundRef>(StringComparer.OrdinalIgnoreCase);
        private string _root;

        public string Root => _root;
        internal IReadOnlyDictionary<string, string> PackageFilesForTest => _packageFiles;

        // Changing the root resets the caches; the same root is a no-op.
        public void SetSource(string installRoot)
        {
            lock (_lock) SetSourceLocked(installRoot);
        }

        void SetSourceLocked(string installRoot)
        {
            if (installRoot is null) throw new ArgumentNullException(nameof(installRoot));
            string full = Path.GetFullPath(installRoot);
            // The same install through a symlinked directory is the same root (no reset).
            if (_root is not null && string.Equals(HP1.Core.UEClassRegistry.PhysicalPath(full),
                    HP1.Core.UEClassRegistry.PhysicalPath(_root), StringComparison.OrdinalIgnoreCase)) return;
            _packageFiles.Clear();
            _packages.Clear();
            _sounds.Clear();
            _leafPaths = null;
            _root = full;
            // The sound packages the game can open (UEInstallPath: with System/HPSounds.u and Sounds/HPSounds.uax both
            // present every `HPSounds.*` reference is served by System/HPSounds.u and the .uax is never opened).
            foreach (string file in UEInstallPath.PackageFiles(full, "System", "Sounds"))
                _packageFiles.Add(Path.GetFileNameWithoutExtension(file), file);
        }

        // The catalog is called from TWO threads: the Core step (GetSoundDuration, sound path) and the frontend's
        // playback thread. The dictionaries and the shared UELib package read stream are not thread-safe: concurrent
        // reads corrupted the stream position ("Trailing data for object Sound...") and hung the game on a line.
        readonly object _lock = new object();

        public UESoundRef Load(string objectPath, bool mayFail)
        {
            lock (_lock) return LoadLocked(objectPath, mayFail);
        }

        UESoundRef LoadLocked(string objectPath, bool mayFail)
        {
            if (_sounds.TryGetValue(objectPath ?? "", out UESoundRef cached)) return cached;
            USound sound = FindLocked(objectPath, mayFail, out UnrealPackage package, out byte[] payload);
            if (sound is null) return null;
            string format = sound.FileType?.ToString() ?? "None";
            if (IsHP2)
            {
                var h = ReadHP2Header(sound, objectPath);
                // Loop: RegisterSound opens a streamed lap as looping when (+0xb8 | +0xb4) & 2; +0xb4 is this data flag,
                // +0xb8 the PlaySound flags of the play (ALAudio PlaySound). No shipped sound carries 2.
                var hp2 = new UESoundRef(sound.Name, objectPath, HP2Duration(h, objectPath), (h.Flags & 2) != 0);
                _sounds.Add(objectPath, hp2);
                return hp2;
            }
            bool looping = ReadLoopFlag(sound, package, payload, objectPath);
            int periodBytes = ReadPeriodBytes(sound, package, objectPath);
            float duration = SoundPeriod(payload, format, objectPath, periodBytes);
            var loaded = new UESoundRef(sound.Name, objectPath, duration, looping);
            _sounds.Add(objectPath, loaded);
            return loaded;
        }

        // PCM of a sound by exact path: data from the user's package; mp2 — port of the Engine.dll decoder
        // (UEMpegAudio) and the MP2SoundStream loop (UEMp2SoundStream: skip 0x3c2 bytes, length iEndPosn = tail word
        // +0x4c); WAV — as is. Only the byte read is under the lock: decoding a long sound does not block the Core step.
        // The voice's stream (a new one per play, as USound::newSoundStream): mp2 — the MP2SoundStream lap, decoded as the
        // Galaxy feeder reads it (Read/Restart); WAV — the data chunk. Nothing is decoded here beyond the mp2 skip.
        public (UEMp2SoundStream Mp2, UEPcm Wav) OpenStream(string objectPath)
        {
            byte[] payload; string format; int endBytes = 0;
            lock (_lock)
            {
                USound sound = FindLocked(objectPath, mayFail: false, out UnrealPackage package, out payload);
                format = sound.FileType?.ToString() ?? "None";
                if (IsHP2) return (null, HP2Pcm(sound, payload, objectPath));
                if (format.Equals("mp2", StringComparison.OrdinalIgnoreCase)) endBytes = ReadPeriodBytes(sound, package, objectPath);
            }
            return format.Equals("mp2", StringComparison.OrdinalIgnoreCase)
                ? (new UEMp2SoundStream(payload, endBytes, objectPath), null)
                : (null, UESoundPcm.Decode(payload, format, objectPath));
        }

        // Frames of one lap, rate and channels for the Galaxy pool, without decoding the sound (perf/spikes: the whole
        // mp2 decode in the tick). null — not resolved. Cached by path.
        readonly Dictionary<string, (long Frames, int Rate, int Channels, bool Finishes)?> _info = new(StringComparer.OrdinalIgnoreCase);
        public (long Frames, int Rate, int Channels, bool Finishes)? LoadInfo(string objectPath)
        {
            lock (_info)
                if (_info.TryGetValue(objectPath, out var hit)) return hit;
            (long, int, int, bool)? info;
            try
            {
                if (!Exists(objectPath)) info = null;
                else
                {
                    var (mp2, wav) = OpenStream(objectPath);
                    info = mp2 != null ? (mp2.LoopSamples / mp2.Channels, mp2.SampleRate, mp2.Channels, mp2.Finishes) : (wav.Frames, wav.SampleRate, wav.Channels, false);
                }
            }
            catch (Exception e) { HP1.Core.UETrace.Warn($"[audio] sound '{objectPath}' not resolved: {e.Message}"); info = null; }
            lock (_info) _info[objectPath] = info;
            return info;
        }

        public UEPcm LoadPcm(string objectPath)
        {
            byte[] payload; string format; int endBytes = 0; bool looping = false;
            lock (_lock)
            {
                USound sound = FindLocked(objectPath, mayFail: false, out UnrealPackage package, out payload);
                format = sound.FileType?.ToString() ?? "None";
                if (IsHP2) return HP2Pcm(sound, payload, objectPath);
                if (format.Equals("mp2", StringComparison.OrdinalIgnoreCase))
                {
                    endBytes = ReadPeriodBytes(sound, package, objectPath);
                    looping = ReadLoopFlag(sound, package, payload, objectPath);
                }
            }
            if (format.Equals("mp2", StringComparison.OrdinalIgnoreCase))
                return UEMp2SoundStream.ShippedPcm(payload, endBytes, looping, objectPath);
            return UESoundPcm.Decode(payload, format, objectPath);
        }

        // Whether a Sound export with this exact path exists (without reading the USound tail: duration and loop
        // are not involved).
        public bool Exists(string objectPath)
        {
            lock (_lock) return FindLocked(objectPath, mayFail: true, out _, out _) is not null;
        }

        // UE path by LEAF name — only for a clip the scene importer stored in a property without a path. Rule: all
        // Sound exports from System/HPSounds.u and Sounds/*.uax; a leaf seen more than once is NOT resolved (null —
        // the caller is loud).
        public string PathForUniqueLeaf(string leaf)
        {
            if (string.IsNullOrEmpty(leaf)) return null;
            lock (_lock)
            {
                if (_leafPaths is null) BuildLeafPathsLocked();
                return _leafPaths.TryGetValue(leaf, out string path) ? path : null;
            }
        }

        // Every Sound export path of the install (System/*.u and Sounds/*.uax) — harness census (--soundcensus).
        public List<string> AllSoundPaths()
        {
            lock (_lock)
            {
                var paths = new List<string>();
                foreach (string name in _packageFiles.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    foreach (var e in OpenPackage(name).Exports)
                        if (string.Equals(e.Class?.ObjectName?.ToString(), "Sound", StringComparison.OrdinalIgnoreCase)
                            && e.Object?.GetPath() is string path)
                            paths.Add(path);
                return paths;
            }
        }

        Dictionary<string, string> _leafPaths;
        void BuildLeafPathsLocked()
        {
            if (_root is null) throw new InvalidOperationException("sound catalog without an install root");
            var seen = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            if (_packageFiles.ContainsKey("HPSounds")) names.Add("HPSounds");
            names.AddRange(_packageFiles.Where(kv => Path.GetExtension(kv.Value).EndsWith("uax", StringComparison.OrdinalIgnoreCase))   // .uax or .<lang>_uax
                .Select(kv => kv.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            foreach (string name in names)
                foreach (var e in OpenPackage(name).Exports)
                {
                    if (!string.Equals(e.Class?.ObjectName?.ToString(), "Sound", StringComparison.OrdinalIgnoreCase)) continue;
                    string leaf = e.ObjectName.ToString();
                    if (!seen.TryGetValue(leaf, out var list)) seen[leaf] = list = new List<string>();
                    list.Add(e.Object?.GetPath());
                }
            _leafPaths = seen.Where(kv => kv.Value.Count == 1 && !string.IsNullOrEmpty(kv.Value[0]))
                .ToDictionary(kv => kv.Key, kv => kv.Value[0], StringComparer.OrdinalIgnoreCase);
        }

        USound FindLocked(string objectPath, bool mayFail, out UnrealPackage package, out byte[] payload)
        {
            package = null; payload = null;
            if (_root is null)
                throw new InvalidOperationException(
                    $"Sound'{objectPath}': sound catalog without an install root — the frontend did not set the source");
            if (string.IsNullOrWhiteSpace(objectPath))
                return Missing<USound>(objectPath, mayFail, "empty object path");
            int dot = objectPath.IndexOf('.');
            if (dot <= 0 || dot == objectPath.Length - 1)
                return Missing<USound>(objectPath, mayFail, "path must be Package.Object");

            string packageName = objectPath.Substring(0, dot);
            UObject found = null;
            if (_packageFiles.ContainsKey(packageName))
            {
                package = OpenPackage(packageName);
                found = package.FindObjectByGroup(objectPath);
            }
            // StaticLoadObject searches mounted linkers by the complete object
            // path. HP1's authored root (for example HPSounds.FootSteps.Events)
            // is not necessarily the .uax filename, so a filename-only lookup is
            // invalid. Search every shipped linker with the SAME full path and
            // reject ambiguity; never fall back to a leaf-name match.
            if (found is null)
            {
                foreach (string mountedName in _packageFiles.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.Equals(mountedName, packageName, StringComparison.OrdinalIgnoreCase)) continue;
                    UnrealPackage mounted = OpenPackage(mountedName);
                    UObject candidate = mounted.FindObjectByGroup(objectPath);
                    if (candidate is null) continue;
                    if (found is not null)
                        throw new InvalidDataException($"duplicate mounted Sound path {objectPath}");
                    found = candidate;
                    package = mounted;
                }
            }
            if (found is null)
                return Missing<USound>(objectPath, mayFail,
                    "Sound export not found" + DescribeExactLeafCandidates(objectPath));
            if (!string.Equals(found.GetPath(), objectPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Sound path mismatch: requested {objectPath}, found {found.GetPath()}");
            USound sound = found as USound
                ?? throw new InvalidDataException($"{objectPath} is {found.GetType().Name}, not USound");
            sound.Load<UObjectRecordStream>();
            sound.RawData.LoadData(package.Stream);
            payload = sound.RawData.ElementData
                ?? throw new InvalidDataException($"{objectPath} has no serialized sound payload");
            return sound;
        }

        private UnrealPackage OpenPackage(string packageName)
        {
            if (_packages.TryGetValue(packageName, out UnrealPackage package)) return package;
            string file = _packageFiles[packageName];
            package = HP1.UEScript.UEPackageOpen.Load(file, FileAccess.Read);
            // Objects are constructed, not deserialized: a Sound is read when it is requested (FindLocked ->
            // USound.Load), as ULinkerLoad creates an export only on demand. The RU HPSounds.u (EA) carries the script
            // exports WavSounds/ScriptText/ScriptText/PackSounds with SerialOffset 16617..16620 one byte apart and their
            // US sizes (85/2547/376/85), i.e. overlapping garbage: the game never loads them; reading every export did.
            package.InitializePackage(UnrealPackage.InitFlags.RegisterClasses | UnrealPackage.InitFlags.Construct);
            _packages.Add(packageName, package);
            return package;
        }

        private string DescribeExactLeafCandidates(string objectPath)
        {
            string leaf = objectPath.Substring(objectPath.LastIndexOf('.') + 1);
            string[] candidates = _packages.Values
                .SelectMany(p => p.Exports)
                .Where(e => string.Equals(e.ObjectName?.ToString(), leaf, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Class?.ObjectName?.ToString(), "Sound", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Object?.GetPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return candidates.Length == 0 ? "" : $"; exact-leaf exports=[{string.Join(",", candidates)}]";
        }

        private static T Missing<T>(string path, bool mayFail, string reason) where T : class
        {
            if (mayFail) return null;
            throw new FileNotFoundException($"DynamicLoadObject Sound'{path}': {reason}");
        }

        // Shipped FSoundData::GetPeriod (Engine.dll.c:121995-122038) asks
        // FWaveModInfo for data bytes, channels, sample rate and bits/sample, then
        // returns dataBytes*8 / (channels*sampleRate*bitsPerSample).
        // Package version 76 writes these dwords after FSoundData's lazy payload
        // (shipped USound::Serialize asm 0x1038013d-0x103801ae): +54, +6c, +44,
        // +48, +70, +4c, +50. The sixth is USound+0x4c == FSoundData+0x24,
        // exactly the byte count consumed by the mp2 GetPeriod branch.
        // LOOP FLAG. A package of version > 0x45 writes it as the first tail word after the data
        // (USound::Serialize, disasm 0x1038013f: [edi+0x54]). A package of version < 0x46 does not carry it —
        // USound::SetupVarsForVersion70 (Engine.dll.c:132972) sets +0x54 = 1 if the WAV has smpl loops
        // (FWaveModInfo: loop count > 0). The flag is set on 167 shipped sounds, so a name list cannot replace it.
        private static bool ReadLoopFlag(USound sound, UnrealPackage package, byte[] payload, string objectPath)
        {
            NotHP2Tail(objectPath);
            if (package.Version < 0x46) return WavHasSampleLoops(payload);
            long tail = checked(sound.RawData.StorageOffset + sound.RawData.StorageSize);
            byte[] bytes = new byte[4];
            long saved = package.Stream.AbsolutePosition;
            try
            {
                package.Stream.AbsolutePosition = tail;
                if (package.Stream.EndianAgnosticRead(bytes, 0, 4) != 4)
                    throw new EndOfStreamException($"{objectPath} USound loop flag");
            }
            finally { package.Stream.AbsolutePosition = saved; }
            return BitConverter.ToInt32(bytes, 0) != 0;
        }

        // RIFF/WAVE: "smpl" chunk, field cSampleLoops (offset 28 in the chunk data).
        private static bool WavHasSampleLoops(byte[] d)
        {
            if (d.Length < 12 || d[0] != 'R' || d[1] != 'I' || d[2] != 'F' || d[3] != 'F') return false;
            int pos = 12;
            while (pos + 8 <= d.Length)
            {
                int size = BitConverter.ToInt32(d, pos + 4);
                if (size < 0) return false;
                if (d[pos] == 's' && d[pos + 1] == 'm' && d[pos + 2] == 'p' && d[pos + 3] == 'l')
                    return size >= 32 && pos + 8 + 32 <= d.Length && BitConverter.ToInt32(d, pos + 8 + 28) > 0;
                pos += 8 + size + (size & 1);
            }
            return false;
        }

        static bool IsHP2 => HP1.UEScript.UEGameProfile.Current.Game == HP1.UEScript.UEEdition.Game.HP2;

        // HP2 USound header: +0xb4 flags, +0x68 duration, then +0x6c NumSamples (Ver > 76), +0x70 bits and +0x74
        // channels (Ver > 77), +0x78 rate (Ver > 78) — read by UELib's HP2 build (judged against the HP2 USound::Serialize
        // body 0x103854e0, tools/hp2/sound_serialize_judge.py); the record names are UELib's.
        public readonly record struct HP2SoundHeader(uint Flags, float Duration, int NumSamples, int Bits, int Channels, int Rate);

        public static HP2SoundHeader ReadHP2Header(USound sound, string objectPath)
        {
            var f = sound.BinaryMetaData?.Fields ?? throw new InvalidDataException($"{objectPath}: no HP2 USound header records");
            object V(string n) => f.Where(x => x.Field == n).Select(x => x.Value).SingleOrDefault()
                ?? throw new NotSupportedException($"{objectPath}: HP2 USound header without {n} (package older than v79: not shipped, not ported)");
            return new HP2SoundHeader((uint)V("flags"), (float)V("duration"), (int)V("numSamples"),
                (int)V("bitsPerSample"), (int)V("numChannels"), (int)V("sampleRate"));
        }

        // Original: Engine.dll ?GetDuration@USound@@UAEMXZ [HP2 0x10359400] (thunk 0x10304e6c)
        // +0x68 when it is not below 0.0 (fcomp [0x10474598] = 0.0); otherwise FSoundData::GetPeriod (HP2 body not ported).
        static float HP2Duration(HP2SoundHeader h, string objectPath)
            => h.Duration >= 0f ? h.Duration
                : throw new NotSupportedException($"{objectPath}: HP2 duration {h.Duration} < 0 asks FSoundData::GetPeriod (not ported, STOP)");

        // The PCM UALAudioSubsystem::RegisterSound [HP2 ALAudio 0x10003400] gives OpenAL for one lap (looping streams:
        // UEXaStream). Flags & 4 clear: FWaveModInfo::ReadWaveInfo [HP2 Engine 0x10301c80] over the data — the data chunk,
        // rate, channels, bits of the WAV. Flags & 4 with FileType "ogg": a file stream through vorbisfile (OGG: STOP in
        // DECISIONS.md, not ported). Flags & 4 otherwise: the Core XA stream of NumSamples samples.
        static UEPcm HP2Pcm(USound sound, byte[] payload, string objectPath)
        {
            var h = ReadHP2Header(sound, objectPath);
            if ((h.Flags & 4) == 0) return UESoundPcm.Decode(payload, "WAV", objectPath);
            if (string.Equals(sound.FileType?.ToString(), "ogg", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"{objectPath}: HP2 OGG stream (vorbisfile) is not ported (STOP, DECISIONS.md)");
            if (h.Bits == 8)
                throw new NotSupportedException($"{objectPath}: HP2 8-bit XA stream (AL_FORMAT_*8 over 16-bit decoder output; not shipped)");
            var pcm = new UEPcm { SampleRate = h.Rate, Channels = h.Channels == 1 ? 1 : 2, Samples = new short[h.NumSamples] };
            new UEXaStream(payload, h.NumSamples, looping: false, objectPath).Read(pcm.Samples, h.NumSamples);
            return pcm;
        }

        // HP2: what UALAudioSubsystem::RegisterSound [HP2 ALAudio 0x10003400] reads of a USound for one lap: +0xb4 flags;
        // flags & 4 clear: the WAV data chunk (FWaveModInfo::ReadWaveInfo [HP2 Engine 0x10301c80]: frames, rate, channels) and
        // whether it has smpl loops (buffer handle flags 2); flags & 4: the XA stream's NumSamples, rate, channels (header,
        // not decoded). An OGG FileType in a package refuses as in HP2Pcm.
        public (uint Flags, int Frames, int Rate, int Channels, bool SampleLoops) HP2Registration(string objectPath)
        {
            if (!IsHP2) throw new InvalidOperationException($"{objectPath}: HP2Registration outside HP2");
            USound sound; byte[] payload;
            lock (_lock) sound = FindLocked(objectPath, mayFail: false, out _, out payload);
            var h = ReadHP2Header(sound, objectPath);
            if ((h.Flags & 4) != 0)
            {
                if (string.Equals(sound.FileType?.ToString(), "ogg", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException($"{objectPath}: HP2 OGG stream in a package (vorbisfile file stream) is not ported");
                return (h.Flags, h.NumSamples, h.Rate, h.Channels == 1 ? 1 : 2, false);
            }
            UEPcm pcm = UESoundPcm.Decode(payload, "WAV", objectPath);
            return (h.Flags, (int)pcm.Frames, pcm.SampleRate, pcm.Channels, WavHasSampleLoops(payload));
        }

        public HP2SoundHeader HP2HeaderOf(string objectPath)
        {
            lock (_lock) return ReadHP2Header(FindLocked(objectPath, mayFail: false, out _, out _), objectPath);
        }

        // HP2: the lip-sync amplitudes of a sound whose +0xb4 has 0x80 — the FLipSyncData at USound+0x44, a lazy byte array
        // (TLazyArray<BYTE>: data +0xc, count +0x10) serialized after the sample data (USound::Serialize [HP2 Engine
        // 0x103854e0], seek targets judged by tools/hp2/sound_serialize_judge.py); null without 0x80.
        public byte[] HP2LipSync(string objectPath)
        {
            if (!IsHP2) throw new InvalidOperationException($"{objectPath}: HP2LipSync outside HP2");
            lock (_lock)
            {
                USound sound = FindLocked(objectPath, mayFail: false, out UnrealPackage package, out _);
                if ((sound.HP2Flags & 0x80) == 0) return null;
                sound.HP2SecondData.LoadData(package.Stream);
                return sound.HP2SecondData.ElementData ?? Array.Empty<byte>();
            }
        }

        // HP2: the stream a streamed XA sound plays from (a new one per play, as RegisterSound creates one); null for a
        // non-streamed sound (its PCM is LoadPcm's) and outside HP2. OGG refuses as in HP2Pcm.
        public UEXaStream OpenXaStream(string objectPath)
        {
            if (!IsHP2) return null;
            USound sound; byte[] payload;
            lock (_lock) sound = FindLocked(objectPath, mayFail: false, out _, out payload);
            var h = ReadHP2Header(sound, objectPath);
            if ((h.Flags & 4) == 0) return null;
            if (string.Equals(sound.FileType?.ToString(), "ogg", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"{objectPath}: HP2 OGG stream (vorbisfile) is not ported");
            if (h.Bits == 8)
                throw new NotSupportedException($"{objectPath}: HP2 8-bit XA stream (not shipped)");
            return new UEXaStream(payload, h.NumSamples, looping: false, objectPath)
            { SampleRate = h.Rate, Channels = h.Channels == 1 ? 1 : 2 };
        }

        // HP2 has no tail after the data: its USound header precedes it (UELib HP2 build, judged by
        // tools/hp2/sound_serialize_judge.py); HP2 reads loop and duration from that header (ReadHP2Header, HP2Duration),
        // so these HP1 tail readers must never see an HP2 sound.
        // (USound::Serialize: HP1 Engine.dll body 0x103800a0, HP2 Engine.dll body 0x103854e0.)
        internal static void NotHP2Tail(string objectPath)
        {
            if (HP1.UEScript.UEGameProfile.Current.Game == HP1.UEScript.UEEdition.Game.HP2)
                throw new NotSupportedException($"{objectPath}: the HP1 USound tail reader was reached for an HP2 sound");
        }

        private static int ReadPeriodBytes(USound sound, UnrealPackage package, string objectPath)
        {
            NotHP2Tail(objectPath);
            // USound::Serialize (Engine.dll.c:71492-71503): the tail after the data depends on package version —
            // +54/+6c/+44/+48 at version > 0x45, +70 — > 0x46, +4c — > 0x48, +50 — > 0x49. A package of version
            // < 0x49 has no +0x4c counter in the file: Serialize computes it itself (branch < 0x49 with +0x6c != 0 —
            // runs mp2 decoder FUN_1037e550, 71537-71558), and GetPeriod reads it ONLY in the mp2 branch.
            // At < 0x46 (0x10380138 `cmp eax, 0x46; jl`) StartPos/Length/loop are set by SetupVarsForVersion70
            // from the WAV. Shipped Ambient.uax and Critters.uax are version 69 (Lev2_fire1, Lev3_PreDungeon,
            // Lev3_PreTroll). Versions 0x46..0x48 also lack the +0x4c word (no such shipped packages — not
            // distinguishable on shipped data).
            if (package.Version < 0x49)   // no v76 tail
            {
                if (sound.FileType?.ToString().Equals("mp2", StringComparison.OrdinalIgnoreCase) == true)
                    throw new NotSupportedException(
                        $"{objectPath}: mp2 in a package of version {package.Version} — the +0x4c counter is computed by decoder FUN_1037e550 (not ported, STOP)");
                return 0;   // WAV: GetPeriod takes the period from the WAV header, does not read +0x4c
            }
            long tail = checked(sound.RawData.StorageOffset + sound.RawData.StorageSize);
            long exportEnd = checked((long)sound.GetBufferPosition() + sound.GetBufferSize());
            const int tailBytes = 7 * 4;
            if (tail < sound.GetBufferPosition() || tail > exportEnd - tailBytes)
                throw new InvalidDataException($"{objectPath} invalid version-76 USound tail");
            byte[] bytes = new byte[tailBytes];
            long saved = package.Stream.AbsolutePosition;
            try
            {
                package.Stream.AbsolutePosition = tail;
                int read = package.Stream.EndianAgnosticRead(bytes, 0, bytes.Length);
                if (read != bytes.Length) throw new EndOfStreamException($"{objectPath} USound tail");
            }
            finally { package.Stream.AbsolutePosition = saved; }
            int isMp2 = BitConverter.ToInt32(bytes, 4); // USound+0x6c
            int periodBytes = BitConverter.ToInt32(bytes, 20); // USound+0x4c
            if (sound.FileType?.ToString().Equals("mp2", StringComparison.OrdinalIgnoreCase) == true
                && isMp2 == 0)
                throw new InvalidDataException($"{objectPath} format mp2 but serialized bIsMP2 is false");
            // Shipped FSoundData::GetPeriod (Engine.dll.c:121995-122038) RETURNS 0.0 on a zero product / zero bytes
            // instead of refusing: a sound with an empty recording (Lev2_fire1:
            // HPSounds.critters_sfx.critters.pig_squeal1) gets period 0. Negative is data corruption — refuse.
            if (periodBytes < 0) throw new InvalidDataException($"{objectPath} invalid period byte count {periodBytes}");
            // Only the mp2 branch of GetPeriod reads the counter (SoundPeriod below); WAV takes the period from its header — no warning.
            if (periodBytes == 0 && sound.FileType?.ToString().Equals("mp2", StringComparison.OrdinalIgnoreCase) == true)
                HP1.Core.UETrace.Warn($"[Sound] {objectPath}: zero period byte counter — shipped GetPeriod gives 0.0");
            return periodBytes;
        }

        // DIVISION — AS ON x87, NOT float32.
        // Disasm of GetPeriod 0x1037ed4d-0x1037ed57 / 0x1037ed85-0x1037ed90:
        //   fild dword [bytes]; fmul dword [8.0]; fidiv dword [divisor]
        // — the quotient is in a PC64 register (64-bit mantissa), then the caller USound::GetDuration (0x10352c8b)
        // does `fstp dword [+0x60]`. Result: TWO roundings (64, then 24 bits); a single float `bytes*8f/den`
        // rounding differs at rare midpoints.
        public static float X87Period(int dataBytes, int denominator)
        {
            HP1.Core.X87Ext q = HP1.Core.X87Ext.Div64(
                HP1.Core.X87Ext.From(dataBytes) * HP1.Core.X87Ext.From(8f),
                HP1.Core.X87Ext.From(denominator));
            return q.ToFloat();
        }

        public static float SoundPeriod(byte[] data, string format, string objectPath, int periodBytes)
        {
            // Disasm of FSoundData::GetPeriod (0x1037ec80): field +0x24 (byte counter) is read ONLY by the mp2 branch
            // (fild [esi+0x24]); the WAV branch (after 0x1037ed5f) takes the period from the WAV header
            // (data·8 / (ch·rate·bits)) and does not read +0x24, so +0x24 = 0 must not zero a WAV period.
            if (format.Equals("mp2", StringComparison.OrdinalIgnoreCase))
                return periodBytes == 0 ? 0f : MpegPeriod(data, objectPath, periodBytes);
            // WAV branch — any non-mp2 format. FWaveModInfo::ReadWaveInfo (Engine.dll.c:143355-143430):
            // checks ONLY "WAVE" at +8 (otherwise 0 -> period 0.0); walks while a chunk header (8 bytes) fits;
            // "fmt " is read without checking length or encoding; for "data" the DECLARED size is taken even if the
            // chunk is truncated; step is (size + 1) & ~1. Reading fmt fields past the end of the array is a read of
            // foreign memory in shipped; here it is a loud STOP.
            if (data.Length < 12 || FourCc(data, 8) != "WAVE")
            {
                HP1.Core.UETrace.Warn($"[Sound] {objectPath}: no \"WAVE\" at +8 — shipped ReadWaveInfo 0, period 0.0");
                return 0f;
            }

            int channels = 0, sampleRate = 0, bitsPerSample = 0, dataBytes = -1;
            for (long pos = 12; pos + 8 <= data.Length;)
            {
                int ip = (int)pos;
                string id = FourCc(data, ip);
                uint size = BitConverter.ToUInt32(data, ip + 4);
                int body = ip + 8;
                if (id == "fmt ")
                {
                    if (body + 16 > data.Length)
                        throw new InvalidDataException($"{objectPath} WAV fmt past the end of data (shipped reads foreign memory)");
                    channels = BitConverter.ToUInt16(data, body + 2);
                    sampleRate = BitConverter.ToInt32(data, body + 4);
                    bitsPerSample = BitConverter.ToUInt16(data, body + 14);
                }
                else if (id == "data")
                    dataBytes = (int)size;
                pos = pos + 8 + ((size + 1u) & 0xfffffffeu);
            }
            int denominator = checked(channels * sampleRate * bitsPerSample);
            // Shipped: a header parse failure or a zero product gives 0.0 (0x1037ed6b..0x1037ed7f, branch at
            // 0x1037ed98), not a refusal. Loud — in the log.
            if (dataBytes < 0 || denominator == 0)
            {
                HP1.Core.UETrace.Warn($"[Sound] {objectPath}: WAV without fmt/data or a zero divisor — shipped GetPeriod gives 0.0");
                return 0f;
            }
            return X87Period(dataBytes, denominator);
        }

        // The mp2 branch in FSoundData::GetPeriod reads the first MPEG header with
        // FUN_1044acf0 (Engine.dll.c:84179-84259), then uses the parsed sample-rate
        // word at +4 and channel byte at +14: DataSize*8/(rate*channels*16).
        private static float MpegPeriod(byte[] data, string objectPath, int periodBytes)
        {
            if (data.Length < 4) throw new InvalidDataException($"{objectPath} short mp2 payload");
            uint header = ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
            if ((header & 0xfff00000u) != 0xfff00000u)
                throw new InvalidDataException($"{objectPath} invalid mp2 sync header 0x{header:x8}");
            int layer = 4 - (int)((header >> 17) & 3);
            int version = (int)((header >> 19) & 3);
            int bitrateIndex = (int)((header >> 12) & 15);
            int rateIndex = (int)((header >> 10) & 3);
            // bitrateIndex == 0 (free bitrate): FUN_1044acf0 returns failure but has already written rate and
            // channels, and GetPeriod ignores the return code (Engine.dll.c:84179-84260) — the period is computed.
            if (layer == 4 || version == 1 || bitrateIndex == 15 || rateIndex == 3)
                throw new InvalidDataException($"{objectPath} rejected mp2 header 0x{header:x8}");

            // Engine.dll .data 0x104de96c: 44100, 48000, 32000, 0.
            int sampleRate = rateIndex == 0 ? 44100 : rateIndex == 1 ? 48000 : rateIndex == 2 ? 32000 : 0;
            if (version == 2) sampleRate >>= 1;
            else if (version == 0) sampleRate >>= 2;
            int mode = (int)((header >> 6) & 3);
            int channels = mode == 3 ? 1 : 2;
            return X87Period(periodBytes, checked(sampleRate * channels * 16));
        }

        private static string FourCc(byte[] data, int offset)
            => System.Text.Encoding.ASCII.GetString(data, offset, 4);
    }
}
