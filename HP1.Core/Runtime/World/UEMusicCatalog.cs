// MUSIC CATALOG FROM THE USER'S INSTALL.
//
// The rule runs over the install's Music/*.umx packages: exact object path (Package.Name,
// case-insensitive like FName), no leaf search; PCM comes from the ported Engine.dll mp2 decoder
// (UEMpegAudio).
//
// Lookup rule — StaticLoadObject (Core 0x1015ec70: ResolveName -> GetPackageLinker -> StaticFindObject),
// no fallback paths. The literal Music'Engine.JS_HP_Title_Screen_v2' (FEBook.uc:159) is a top-level
// export of System/Engine.u itself, so the index also includes Engine.u Music exports. A rule like
// "not found in the named package — load a package named after the object" is absent from shipped.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UELib;
using UELib.Core;

namespace HP1.UEScript
{
    public sealed class UEMusicCatalog
    {
        public static readonly UEMusicCatalog Shared = new UEMusicCatalog();

        readonly object _lock = new object();
        string _root;
        Dictionary<string, (UnrealPackage Package, UExportTableItem Export)> _byPath;

        public string Root => _root;

        public void SetSource(string installRoot)
        {
            if (installRoot is null) throw new ArgumentNullException(nameof(installRoot));
            string full = Path.GetFullPath(installRoot);
            lock (_lock)
            {
                // The same install through a symlinked directory is the same root (no re-index).
                if (_root is not null && string.Equals(HP1.Core.UEClassRegistry.PhysicalPath(full),
                        HP1.Core.UEClassRegistry.PhysicalPath(_root), StringComparison.OrdinalIgnoreCase)) return;
                _root = full;
                _byPath = null;
            }
        }

        void EnsureIndexLocked()
        {
            if (_byPath is not null) return;
            if (_root is null)
                throw new InvalidOperationException("music catalog without an install root — the frontend did not set the source");
            string dir = UEInstallPath.Dir(Path.Combine(_root, "Music"))
                ?? throw new DirectoryNotFoundException($"the install has no Music directory under {_root}");
            var index = new Dictionary<string, (UnrealPackage, UExportTableItem)>(StringComparer.OrdinalIgnoreCase);
            string engine = UEInstallPath.File(Path.Combine(_root, "System", "Engine.u"))
                ?? throw new FileNotFoundException($"the install has no System/Engine.u under {_root}");
            foreach (string file in Directory.EnumerateFiles(dir)
                         .Where(x => Path.GetExtension(x).Equals(".umx", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                         .Append(engine))
            {
                // Tables only (LoadPackage reads names/imports/exports): the index needs each Music export's path.
                // Its object is constructed with its package on the first Payload of that package (perf/spikes:
                // constructing every .umx and all of Engine.u here cost 30-100 ms on the first song).
                UnrealPackage package = UnrealLoader.LoadPackage(file, FileAccess.Read);
                foreach (var export in package.Exports)
                {
                    if (!string.Equals(export.Class?.ObjectName?.ToString(), "Music", StringComparison.OrdinalIgnoreCase)) continue;
                    string path = $"{package.PackageName}.{export.GetPath()}";
                    if (!index.TryAdd(path, (package, export)))
                        throw new InvalidDataException($"duplicate exact music path {path}");
                }
            }
            _byPath = index;
        }

        // Builds the export index now (UELevelRuntime.WarmFirstUseCaches: at level load, not on the first song).
        public void WarmIndex() { lock (_lock) EnsureIndexLocked(); }

        public IReadOnlyList<string> Paths
        {
            get { lock (_lock) { EnsureIndexLocked(); return _byPath.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(); } }
        }

        // Exact catalog path for a script request (or a refusal).
        public string Canonical(string objectPath)
        {
            if (string.IsNullOrEmpty(objectPath)) throw new ArgumentException("empty music path");
            lock (_lock)
            {
                EnsureIndexLocked();
                if (_byPath.ContainsKey(objectPath)) return objectPath;
                throw new InvalidOperationException($"Music '{objectPath}' — not a shipped export");
            }
        }

        // Galaxy plays music as a temporary USound(UMusic*, loop) through the same MP2SoundStream as
        // sounds (Galaxy.dll.c:7407, Engine.dll.c:146468): skip 0x3c2 bytes, loop = iEndPosn.
        // LoadPcm — one loop (check --mp2check), OpenStream — the stream for MusicVoice (loop = Restart).
        public UEPcm LoadPcm(string objectPath)
        {
            byte[] payload = Payload(objectPath, out string path, out int endBytes);
            return UEMp2SoundStream.ShippedPcm(payload, endBytes, looping: false, path);
        }

        public UEMp2SoundStream OpenStream(string objectPath)
        {
            var s = new UEMp2SoundStream(Payload(objectPath, out string path, out int endBytes), endBytes, path);
            // An odd stereo loop shifts the channel parity on every loop — MusicVoice (Restart at the
            // seam) cannot reproduce that per frame. Shipped data has no such music (all stereo loops are multiples of 4 bytes).
            if (s.Channels == 2 && (s.LoopSamples & 1) != 0)
                throw new NotSupportedException($"{path}: stereo music loop — odd number of samples (STOP)");
            return s;
        }

        // UMusic::Serialize (Engine.dll.c:80249-80312): after the data — +0x48 (version > 0x46), +0x44 (> 0x47),
        // +0x50 (> 0x49), +0x4c (> 0x4a, Data.mUncompressedLength; 0 at that version — assert 0x80f).
        byte[] Payload(string objectPath, out string path, out int endBytes)
        {
            path = Canonical(objectPath);
            byte[] payload; string format;
            lock (_lock)
            {
                var (package, export) = _byPath[path];
                if (export.Object is null) package.InitializePackage(UnrealPackage.InitFlags.RegisterClasses | UnrealPackage.InitFlags.Construct);
                UMusic music = export.Object as UMusic
                    ?? throw new InvalidDataException($"{package.FullPackageName}: Music export {export.ObjectName} parsed as {export.Object?.GetType().Name}");
                if (!string.Equals(music.GetPath(), path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"music index path {path} is not the object path {music.GetPath()}");
                music.Load<UObjectRecordStream>();
                music.RawData.LoadData(package.Stream);
                payload = music.RawData.ElementData
                    ?? throw new InvalidDataException($"{path}: the music has no data");
                format = music.FileType?.ToString() ?? "None";
                if (package.Version <= 0x4a)
                    throw new NotSupportedException($"{path}: package version {package.Version} without mUncompressedLength — STOP");
                byte[] tail = new byte[16];
                long saved = package.Stream.AbsolutePosition;
                try
                {
                    package.Stream.AbsolutePosition = checked(music.RawData.StorageOffset + music.RawData.StorageSize);
                    if (package.Stream.EndianAgnosticRead(tail, 0, 16) != 16) throw new EndOfStreamException($"{path}: UMusic tail");
                }
                finally { package.Stream.AbsolutePosition = saved; }
                endBytes = BitConverter.ToInt32(tail, 12);
                if (endBytes == 0) throw new InvalidDataException($"{path}: Data.mUncompressedLength == 0 (shipped assert UnAudio.cpp:0x80f)");
            }
            if (!format.Equals("mp2", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"{path}: music format {format}");
            return payload;
        }
    }
}
