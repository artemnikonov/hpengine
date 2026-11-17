using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    // THE SINGLE MUTABLE CONFIG STATE OF THE GAME.
    //
    // WHY. In shipped, `ConsoleCommand("set ini:…")` is not a file write but a change of the LIVE value followed
    // by a save. Full path, from the decompile:
    //
    //   Core.dll.c:3373-3387  SET <class> <property> <value…>
    //       ParseToken -> class name -> StaticFindObject(UClass, ANY_PACKAGE)
    //       ParseToken -> property name -> FindField
    //       skip spaces; the VALUE is THE WHOLE REST OF THE LINE
    //       GlobalSetProperty(value, class, property, offset, bAllInstances=1)
    //
    //   Core.dll.c:56604  UObject::GlobalSetProperty — THREE STEPS IN A ROW:
    //       1) with bAllInstances=1 walk ALL live objects of the class:
    //          ImportText(value) + call of vtable slot +0x40 on each
    //          (this is "the subsystem learned the new value");
    //       2) ImportText into the class DEFAULTS;
    //       3) SaveConfig(defaults, 0x4000, NULL) — WRITE TO DISK IMMEDIATELY,
    //          inside SET itself, not on menu exit.
    //
    // The port has no per-subsystem UObject defaults, so the three steps map as:
    //   * steps 1 and 2 — one write HERE: this state IS the live value, because consumers (music/sound volume)
    //     read it from here, not from constants;
    //   * step 3 — Store.Save, called RIGHT HERE, exactly as in shipped.
    //
    // IMMUTABLE. Shipped files (`System/0/Default.ini`, `DefUser.ini`) are SOURCE DATA and are never rewritten:
    // they are read as the base, changes go into an overlay saved to a writable directory supplied by the
    // frontend. Headless keeps the overlay in memory.
    //
    // PREFIXES. UObject::ResolveName (Core.dll.c:52103) parses `ini:` and `usr:` the same way but uses DIFFERENT
    // files: `ini:` — System, `usr:` — User. Section indirection is kept: `ini:Engine.Engine.AudioDevice` means
    // "read [Engine.Engine] AudioDevice, get the section name and work with it" — in shipped data that is
    // `Galaxy.GalaxyAudioSubsystem`, which holds MusicVolume=128 (not [Audio.GenericAudioSubsystem], which has
    // the same number).
    public sealed class UEConfig
    {
        // Where saves go. The frontend supplies the implementation (a file in a writable directory, or memory for
        // headless). The scope separates System and User, as GConfig does in shipped.
        public interface IStore
        {
            // A key is its list of values, as in FConfigSection (a TMultiMap of pairs): the list replaces every saved
            // value of the key. Whether it is read as an array or a scalar is decided by the reader (the property).
            // An empty list = the key has no pairs (SaveConfig of an empty array), which shadows the install pairs.
            void Save(string scope, string section, string key, IReadOnlyList<string> values);

            // Restore after restart: the saved overlay comes back as (scope, section, key, values).
            IEnumerable<(string Scope, string Section, string Key, IReadOnlyList<string> Values)> Load();

            // Drop saved keys so the shipped file shows through again: one key, or the whole section when key is null.
            // No default body: a store that kept reset keys would bring them back on the next start.
            void Remove(string scope, string section, string key);
        }

        // How a test store shows one write: "scope:section.key=v" for one value, "=[a|b]" for any other count.
        internal static string FormatWrite(string scope, string section, string key, IReadOnlyList<string> values)
            => scope + ":" + section + "." + key + "=" + (values.Count == 1 ? values[0] : "[" + string.Join("|", values) + "]");

        sealed class MemoryStore : IStore
        {
            public readonly List<string> Writes = new List<string>();
            readonly (string, string, string, string)[] _seed;
            public MemoryStore(params (string, string, string, string)[] seed) { _seed = seed; }
            public void Save(string scope, string section, string key, IReadOnlyList<string> values)
                => Writes.Add(FormatWrite(scope, section, key, values));
            public void Remove(string scope, string section, string key)
                => Writes.Add("remove " + scope + ":" + section + (key is null ? "" : "." + key));
            IEnumerable<(string, string, string, IReadOnlyList<string>)> IStore.Load()
            {
                foreach ((string scope, string section, string key, string value) in _seed)
                    yield return (scope, section, key, new[] { value });
            }
        }

        // In-memory store whose "saved" overlay is `seed`: a snapshot or probe launched with chosen settings (HPEngine
        // --visual); nothing reaches disk.
        public static IStore SeededMemory(params (string Scope, string Section, string Key, string Value)[] seed)
            => new MemoryStore(seed);

        // ── FILE STORE FOR THE OVERLAY ──────────────────────────────────────
        //
        // Writes ONLY changed values and ONLY into the directory given by the frontend: shipped files
        // (`System/0/Default.ini`, `DefUser.ini`) are source data and are never rewritten.
        //
        // Format: plain ini with the ORIGINAL sections and keys, one file per scope: the System overlay goes to
        // `Default.ini`, the User overlay to `DefUser.ini`. Saved data is read by the same parser as the shipped
        // files and is human-readable. A key is written as FConfigFile::Write writes it (Engine 0x103cf190: "[%s]"
        // for every section, then "%s=%s" per pair in Pairs order): a value list is one line per value.
        //
        // NOT ORIGINAL (storage only): a key with NO values (SaveConfig of an empty array) is one bare `Key` line. The
        // original rewrites the whole file, so the removed key is simply absent; the overlay is a diff over the install
        // file and must say "no pairs here" to shadow the install pairs of the key. FConfigFile::Read skips a line
        // without '=' (appStrstr miss, Engine 0x103cfad0), so to the original's parser the overlay reads as the
        // original's own file would: the section present, the key absent.
        public sealed class FileStore : IStore
        {
            readonly string _dir;
            readonly Dictionary<string, Dictionary<string, Dictionary<string, List<string>>>> _byScope =
                new Dictionary<string, Dictionary<string, Dictionary<string, List<string>>>>(
                    StringComparer.OrdinalIgnoreCase);

            public FileStore(string writableDir)
            {
                _dir = writableDir ?? throw new ArgumentNullException(nameof(writableDir));
                System.IO.Directory.CreateDirectory(_dir);
            }

            // Any other config file (config(Name), a PerObjectConfig package file) goes under OtherFilesDir by its
            // normalised name: "Default.ini" of config(Default) is a file of its own, not the System overlay.
            public const string OtherFilesDir = "Config";

            public string PathFor(string scope)
                => string.Equals(scope, ScopeUser, StringComparison.OrdinalIgnoreCase) ? System.IO.Path.Combine(_dir, "DefUser.ini")
                 : string.Equals(scope, ScopeSystem, StringComparison.OrdinalIgnoreCase) ? System.IO.Path.Combine(_dir, "Default.ini")
                 : System.IO.Path.Combine(_dir, OtherFilesDir, scope);

            Dictionary<string, Dictionary<string, List<string>>> SectionsOf(string scope)
            {
                if (!_byScope.TryGetValue(scope, out var sections))
                    _byScope[scope] = sections =
                        new Dictionary<string, Dictionary<string, List<string>>>(UEPropNameComparer.Instance);
                return sections;
            }

            Dictionary<string, List<string>> KeysOf(Dictionary<string, Dictionary<string, List<string>>> sections, string section)
            {
                if (!sections.TryGetValue(section, out var keys))
                    sections[section] = keys = new Dictionary<string, List<string>>(UEPropNameComparer.Instance);
                return keys;
            }

            public void Save(string scope, string section, string key, IReadOnlyList<string> values)
            {
                var sections = SectionsOf(scope);
                KeysOf(sections, section)[key] = new List<string>(values);
                Rewrite(scope, sections);
            }

            public void Remove(string scope, string section, string key)
            {
                if (!_byScope.TryGetValue(scope, out var sections) || !sections.TryGetValue(section, out var keys)) return;
                if (key is null) sections.Remove(section); else keys.Remove(key);
                Rewrite(scope, sections);
            }

            void Rewrite(string scope, Dictionary<string, Dictionary<string, List<string>>> sections)
            {
                // The file is rewritten whole: the overlay is small, and partial ini edits cause drift between
                // memory and disk. A value list is one line per value, in order.
                var sb = new System.Text.StringBuilder();
                foreach (var sec in sections)
                {
                    sb.Append('[').Append(sec.Key).Append("]\n");
                    foreach (var kv in sec.Value)
                    {
                        if (kv.Value.Count == 0) sb.Append(kv.Key).Append('\n');   // no pairs (see the format note)
                        foreach (string v in kv.Value) sb.Append(kv.Key).Append('=').Append(v).Append('\n');
                    }
                    sb.Append('\n');
                }
                // ANSI, as FConfigFile::Read reads it (ISO-8859-1 in the parser).
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFor(scope)));
                System.IO.File.WriteAllText(PathFor(scope), sb.ToString(), System.Text.Encoding.GetEncoding(28591));
            }

            public IEnumerable<(string, string, string, IReadOnlyList<string>)> Load()
            {
                var scopes = new List<string> { ScopeSystem, ScopeUser };
                if (UEInstallPath.Dir(System.IO.Path.Combine(_dir, OtherFilesDir)) is { } other)
                    foreach (string f in System.IO.Directory.GetFiles(other))
                        scopes.Add(System.IO.Path.GetFileName(f));
                foreach (string scope in scopes)
                {
                    string path = UEInstallPath.File(PathFor(scope));
                    if (path is null) continue;
                    string text = UELocalization.LoadFileToString(path);
                    var sections = SectionsOf(scope);
                    foreach (var (section, pairs) in UELocalization.ParseIniPairs(text))
                    {
                        var keys = KeysOf(sections, section);
                        foreach (var kv in pairs)
                        {
                            if (!keys.TryGetValue(kv.Key, out var list)) keys[kv.Key] = list = new List<string>();
                            list.Add(kv.Value);
                        }
                    }
                    // Bare `Key` lines: a key with no pairs, unless the same section also has pairs of it.
                    string current = null;
                    foreach (string line in text.Split('\r', '\n'))
                    {
                        if (line.Length == 0) continue;
                        if (line[0] == '[' && line[line.Length - 1] == ']') current = line.Substring(1, line.Length - 2);
                        else if (current is not null && line.IndexOf('=') < 0 && !KeysOf(sections, current).ContainsKey(line))
                            KeysOf(sections, current)[line] = new List<string>();
                    }
                    foreach (var sec in sections)
                        foreach (var kv in sec.Value)
                            yield return (scope, sec.Key, kv.Key, kv.Value);
                }
            }
        }

        public const string ScopeSystem = "System";
        public const string ScopeUser = "User";

        readonly UEIniFile _system, _user;
        // The saved/live overlay: every key as its list of values (FConfigSection pairs of the key, in Pairs order). A
        // key here shadows the install file's pairs of that key entirely; an empty list = the key has no pairs.
        readonly Dictionary<string, List<string>> _overlay =
            new Dictionary<string, List<string>>(UEPropNameComparer.Instance);
        IStore _store;

        public UEConfig(UEIniFile system, UEIniFile user, IStore store = null)
        {
            _system = system;
            _user = user;
            _store = store ?? new MemoryStore();
        }

        // The frontend swaps the store after load (it knows its writable directory only at runtime). State is
        // kept, and PREVIOUSLY SAVED values are lifted into the overlay — that is settings restore after restart.
        public void BindStore(IStore store, bool restoreSaved = true)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            if (!restoreSaved) return;
            foreach ((string scope, string section, string key, IReadOnlyList<string> values) in _store.Load())
                _overlay[Key(scope, section, key)] = new List<string>(values);
        }

        // ── PRESENTATION SETTINGS ───────────────────────────────────────────
        //
        // `set ini:Engine.Engine.ViewportManager Brightness <float>` and `… TextureDetail <string>`
        // (FEOptionsPage.uc:526/583) are frontend quantities, not gameplay, so Core STORES them and exposes them
        // typed, but does nothing with them itself.
        //
        // CONSUMER — the HPEngine frontend: `Brightness` is read every frame (HPEngineProgram.FrameDraws ->
        // Renderer.GammaExponent) and applied as the D3DDrv device gamma ramp on the whole output frame — HPEngine
        // behaves as the first-run hardware device (selectHardwareRenderDevice). Formula: UD3DRenderDevice::Flush,
        // disasm 0x10002f42..0x10002fd6: ramp[i] = fistp(pow(i/255, 1/(Brightness·2.5))·65535)
        // (hpengine/Hud/ScreenEffects.GammaExponent). Retail SoftDrv instead multiplies the pixel by
        // (Brightness+0.5) (InitMMXFlashes 0x10d09fc6..0x10d0a02f);
        // that path does NOT apply to HPEngine.
        // `TextureDetail` is read by the HPEngine frame: the first mip per UTexture::Lock and the Render skin rule —
        // the formula lives in UETextureLod, the frontend does not repeat it.
        public const string ViewportRef = "Engine.Engine.ViewportManager";

        public float Brightness
        {
            get
            {
                // The value comes from the data: the shipped Default.ini ([WinDrv.WindowsClient] Brightness=0.400000) or
                // the player's saved menu setting. No invented fallback: a missing or unreadable key is a loud stop.
                string section = ResolveSection(ScopeSystem, ViewportRef);
                string raw = Get(ScopeSystem, section, "Brightness");
                return float.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v) ? v
                    : throw new System.IO.InvalidDataException(
                        $"[{section}] Brightness is missing or not a number ('{raw}') in both the install ini and the saved settings");
            }
        }

        public string TextureDetail
            => Get(ScopeSystem, ResolveSection(ScopeSystem, ViewportRef), "TextureDetail");

        // UClient.SkinDetail (@0x80, same enum) — TextureLODSet[2], the skin LODSet (UETextureLod). The menu does
        // not write it; null means the key exists nowhere.
        public string SkinDetail
            => Get(ScopeSystem, ResolveSection(ScopeSystem, ViewportRef), "SkinDetail");

        // Fixture seam (InternalsVisibleTo): the config's whole mutable state — overlay and bound store. The process
        // GConfig is shared by a test run; a case that rebinds the store or writes keys puts it back exactly, including
        // "the key was absent", which a `set ini:` of the old value cannot express.
        // Lists are never changed in place (Set and SetSectionValues store a new one), so a shallow copy is a snapshot.
        internal (Dictionary<string, List<string>> Overlay, IStore Store) SnapshotForTest()
            => (new Dictionary<string, List<string>>(_overlay, UEPropNameComparer.Instance), _store);

        internal void RestoreForTest((Dictionary<string, List<string>> Overlay, IStore Store) s)
        {
            _overlay.Clear();
            foreach (KeyValuePair<string, List<string>> kv in s.Overlay) _overlay[kv.Key] = kv.Value;
            _store = s.Store;
        }

        // Whether the value is in the saved/live overlay (not in the install ini).
        public bool HasOverlay(string scope, string section, string key)
            => _overlay.ContainsKey(Key(scope, section, key));

        static string Key(string scope, string section, string key)
            => scope + "\0" + section + "\0" + key;

        // A scope other than System/User is a config file by its normalised name (ScopeForFile): read from the install's
        // System directory (the original's working directory) when the file exists, else no file. Loaded once.
        readonly Dictionary<string, UEIniFile> _files = new Dictionary<string, UEIniFile>(UEPropNameComparer.Instance);

        UEIniFile Source(string scope)
        {
            if (string.Equals(scope, ScopeUser, StringComparison.OrdinalIgnoreCase)) return _user;
            if (string.Equals(scope, ScopeSystem, StringComparison.OrdinalIgnoreCase)) return _system;
            if (_files.TryGetValue(scope, out UEIniFile f)) return f;
            string path = InstallSystemDir is null ? null : UEInstallPath.File(System.IO.Path.Combine(InstallSystemDir, scope));
            return _files[scope] = path is null ? null : new UEIniFile(path);
        }

        // Original: FConfigCacheIni::Find (Engine.dll.c:276918, FUN_103cf5a0) — the file-name part.
        //   Filename = Filename ? Filename : SystemIni;  appStrcpy(buf, Filename);
        //   if (len < 5 || (buf[len-4] != '.' && buf[len-5] != '.')) appStrcat(buf, ".ini");
        //   appStricmp(buf, "User.ini") == 0   -> the process user ini   (+0x24)
        //   appStricmp(buf, "System.ini") == 0 -> the process system ini (+0x18)
        //   otherwise the name itself, relative to the working directory (System); the file is created by the
        //   create flag (SetString, Detach) or when GFileManager->FileSize >= 0, so GetString on a missing file answers
        //   "no key" and SetString creates it.
        // A name with a path separator or drive is refused loudly: config(Name) and a PerObjectConfig package name are
        // identifiers, so no script route passes one, and the overlay store keeps files flat.
        public static string ScopeForFile(string name)
        {
            string buf = name ?? "";
            int n = buf.Length;
            if (n < 5 || (buf[n - 4] != '.' && buf[n - 5] != '.')) buf += ".ini";
            if (WcsicmpEq(buf, "User.ini")) return ScopeUser;
            if (WcsicmpEq(buf, "System.ini")) return ScopeSystem;
            if (buf.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                throw new NotSupportedException("config file '" + buf + "': a path in a config file name is not ported "
                    + "(no script route names one) — STOP, §4");
            return buf;
        }

        // `Engine.Engine.AudioDevice` -> `Galaxy.GalaxyAudioSubsystem` — UObject::ResolveName (Core.dll.c:52103-52131):
        // the last dot separates the key from the pointer section; the key value is a class name. No pointer in the
        // data — ResolveName fails, and GET/SET answer "class not found" (Logf 0x8f) (D-14 item 10).
        // null where ResolveName fails (no dot, or no pointer key): StaticExec GET/SET then answer "Unrecognized class".
        public string TryResolveSection(string scope, string classRef)
        {
            int dot = classRef?.LastIndexOf('.') ?? -1;
            if (dot <= 0) return null;
            string concrete = Get(scope, classRef.Substring(0, dot), classRef.Substring(dot + 1));
            return string.IsNullOrEmpty(concrete) ? null : concrete;
        }

        public string ResolveSection(string scope, string classRef)
        {
            int dot = classRef?.LastIndexOf('.') ?? -1;
            if (dot <= 0)
                throw new System.IO.InvalidDataException("ini:" + classRef + " — ResolveName: no pointer section (shipped: Unrecognized class)");
            string metaSection = classRef.Substring(0, dot);
            string metaKey = classRef.Substring(dot + 1);
            string concrete = Get(scope, metaSection, metaKey);
            if (string.IsNullOrEmpty(concrete))
                throw new System.IO.InvalidDataException("ini:" + classRef + " — ResolveName: [" + metaSection + "] has no " + metaKey
                    + " (shipped: Unrecognized class)");
            return concrete;
        }

        // ── MASTER VOLUMES ARE VALUES, NOT CONSTANTS ─────────────────────────
        //
        // The menu changes them (`set ini:Engine.Engine.AudioDevice MusicVolume …`), so they cannot come from
        // compile-time constants: the slider would move but the volume would not. The section uses THE SAME
        // indirection as the command — in shipped data `MusicVolume=128` sits in both [Audio.GenericAudioSubsystem]
        // and [Galaxy.GalaxyAudioSubsystem], and `[Engine.Engine] AudioDevice` chooses between them.
        //
        // BYTE TO MULTIPLIER is byte/255, proven by disasm. SurrealEngine's reimplementation additionally multiplies sound by 0.5
        // (USubsystem.cpp:247), but the HP1 Galaxy path has NO such operand: SetVolumes (Galaxy.dll .text
        // 0x10606664-0x106066c9) does `fild SoundVolume; fmul (1/255); fmul 127.0; ftol` for glxSetSampleVolume, where
        // 0x1063a400 = 1/255 (double), 0x1063a3e8 = 127.0 (float) — Galaxy's own range; the clamp to [0, 1] between
        // them applies to MusicVolume (+0x69), not SoundVolume. So the linear master = byte/255 (integer level:
        // hpengine GalaxyMixGain.MasterLevel); SE's 0.5 is a choice of its OpenAL layer.
        public const string AudioDeviceRef = "Engine.Engine.AudioDevice";

        public float MusicMaster => ByteSetting("MusicVolume", 128f);
        public float SoundMaster => ByteSetting("SoundVolume", 200f);

        float ByteSetting(string key, float shippedDefault)
        {
            string raw = Get(ScopeSystem, ResolveSection(ScopeSystem, AudioDeviceRef), key);
            // A missing value is a data defect, not a reason to silently pick a number: the shipped ini carries
            // both keys, and their absence must be visible.
            if (string.IsNullOrEmpty(raw))
                throw new KeyNotFoundException(
                    "config: [" + ResolveSection(ScopeSystem, AudioDeviceRef) + "] " + key
                    + " is missing — nowhere to take the master volume from");
            return (float.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v)
                    ? v : shippedDefault) / 255f;
        }

        // THREE VALUE LAYERS, IN SHIPPED ORDER:
        //   1) saved overlay — what `set ini:` rewrote;
        //   2) shipped ini — what LoadConfig overrides the default with;
        //   3) native class default — what GET answers when the key is absent from the ini entirely
        //      (UClass::Register builds the CDO and only THEN calls LoadConfig, Core.dll.c:27436).
        // The third layer covers only PROVEN values; it knows nothing else, and the caller must raise STOP
        // rather than substitute its own.
        public string Get(string scope, string section, string key)
        {
            HP1.Core.UECoreOwner.Check("UEConfig.Get");
            if (_overlay.TryGetValue(Key(scope, section, key), out List<string> list))
                return list.Count > 0 ? list[list.Count - 1] : null;
            string fromIni = Source(scope)?.Get(section, key);
            if (fromIni is not null) return fromIni;
            return string.Equals(scope, ScopeSystem, StringComparison.OrdinalIgnoreCase)
                   && UENativeClassDefaults.TryGet(section, key, out string fromClass)
                ? fromClass : null;
        }

        // A whole section WITH THE OVERLAY APPLIED. Key bindings ([Engine.Input]) are read this way: EVERY key
        // matters, not ones picked by name, and a saved rebinding must win over the shipped one.
        //
        // Keys absent from the shipped file but present in the overlay are enumerated too: `SET Input` may bind
        // a key that was empty in the file.
        //
        // Native class defaults are deliberately NOT mixed in: they answer a NAMED property request, and the
        // binary offers no way to enumerate "all class properties" — the section contents must not be invented.
        public IEnumerable<KeyValuePair<string, string>> Section(string scope, string section)
        {
            var seen = new HashSet<string>(UEPropNameComparer.Instance);
            foreach (KeyValuePair<string, string> kv in
                     Source(scope)?.Section(section)
                     ?? System.Linq.Enumerable.Empty<KeyValuePair<string, string>>())
            {
                seen.Add(kv.Key);
                string value = Get(scope, section, kv.Key);
                if (value is null) continue;   // removed by an empty value list
                yield return new KeyValuePair<string, string>(kv.Key, value);
            }
            string prefix = Key(scope, section, "");
            foreach (KeyValuePair<string, List<string>> kv in _overlay)
            {
                if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string name = kv.Key.Substring(prefix.Length);
                if (seen.Contains(name) || kv.Value.Count == 0) continue;
                yield return new KeyValuePair<string, string>(name, kv.Value[kv.Value.Count - 1]);
            }
        }

        // GConfig->GetString (FConfigCacheIni, slot +0x10) as called by UObject::LoadConfig (Core.dll.c:37239) and
        // appInit ([Engine.Engine] Language, Core.dll.c:56313-56321): answers ONLY from the ini layers — saved
        // overlay and install file. The native class default is NOT mixed in: GetString without the key answers
        // "no", and the caller keeps the value as it was. UENativeClassDefaults belongs to the GET command, not
        // to GConfig.
        public bool TryGetIni(string scope, string section, string key, out string value)
        {
            if (_overlay.TryGetValue(Key(scope, section, key), out List<string> list))
            {
                value = list.Count > 0 ? list[list.Count - 1] : null;   // Find: the newest pair of the key
                return value is not null;
            }
            value = Source(scope)?.Get(section, key);
            return value is not null;
        }

        // GConfig->GetSectionPrivate(Section, Force 0, Const 1, File) + FConfigSection::MultiFind(Key), as UObject::LoadConfig
        // reads a dynamic array (Core disasm 0x1015acb5..0x1015ac73). false: no such section in the file (GetSectionPrivate
        // answers NULL). Otherwise every value of the key in Pairs order: MultiFind walks the hash chain newest first
        // (TMultiMap::Add links the new pair at the head, Core FUN_1016a540; Rehash FUN_1016b4b0 relinks in index order),
        // and LoadConfig imports the found list from its end, so element i is the i-th pair.
        public bool TryGetSectionValues(string scope, string section, string key, out List<string> values)
        {
            values = null;
            if (_overlay.TryGetValue(Key(scope, section, key), out List<string> list))
            {
                values = new List<string>(list);
                return true;
            }
            UEIniFile src = Source(scope);
            if (src?.HasSection(section) != true && !HasPrefix(_overlay.Keys, Key(scope, section, ""))) return false;
            values = src?.GetAll(section, key) ?? new List<string>();
            return true;
        }

        static bool HasPrefix(IEnumerable<string> keys, string prefix)
        {
            foreach (string k in keys)
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // UObject::SaveConfig of a dynamic array (Core disasm 0x1015b2ab..0x1015b3d2): GetSectionPrivate(Section, Force 1,
        // Const 0, File) creates the section; every pair whose key appStricmp-equals the property name is removed (walk
        // from the last pair down), then one TMultiMap::Add(Name, ExportTextItem(element)) per element, in element order.
        // The key's values are therefore exactly `values`; an empty list leaves the key absent but the section present.
        public void SetSectionValues(string scope, string section, string key, IReadOnlyList<string> values)
        {
            _overlay[Key(scope, section, key)] = new List<string>(values);
            _store.Save(scope, section, key, values);
        }

        // ── PROCESS GCONFIG ─────────────────────────────────────────────────
        //
        // In shipped the config is ONE process global (GConfig), created by appInit (Core.dll.c:56309) BEFORE the
        // first package loads, and all levels of the process live on it: UClass::Serialize calls LoadConfig for
        // EVERY class's defaults when a package loads (Core.dll.c:4240). So the state must exist before any
        // LoadDefaults and survive level changes. It is created by UELevelRuntime.AppInit — one entry for both
        // frontends.
        public static UEConfig Global { get; internal set; }
        // HP2: the DLL-only packages (D3DDrv, ALAudio) UGameEngine::Init has bound so far (UEConsoleCommand's GET of ini:).
        internal readonly HashSet<string> BoundDllPackages = new(StringComparer.OrdinalIgnoreCase);
        internal string InstallSystemDir;

        // HPEngine's own settings — the original has no such section.
        public const string HPEngineSection = "HPEngine";

        // NOT ORIGINAL (improvement): frame interpolation between 60 Hz logic ticks, always on
        // (draw rate and its 120 cap — hpengine FramePacer.DrawHz). No menu item; an explicit "False"
        // turns it off (one frame per logic tick).
        public const string SmoothMotionKey = "SmoothMotion";
        public bool SmoothMotion => !string.Equals(Get(ScopeSystem, HPEngineSection, SmoothMotionKey), "False", StringComparison.OrdinalIgnoreCase);

        // NOT ORIGINAL (D-53): anisotropic filtering of 3D-world textures.
        // D3DDrv does not set MAXANISOTROPY. Missing key or not a number — 16; 0 and 1 — off; above 16 — 16. No menu item.
        public const string AnisotropyKey = "Anisotropy";
        public int Anisotropy
        {
            get
            {
                string raw = Get(ScopeSystem, HPEngineSection, AnisotropyKey);
                if (!int.TryParse(raw, out int v)) return 16;
                return v <= 1 ? 0 : Math.Min(v, 16);
            }
        }

        // NOT ORIGINAL (D-44): the interface scale. Standard = the
        // integer max(1, H/480) (640x480 proportions); the Options item "Interface Size" (the Object Detail frame,
        // UEPortScriptOverrides) saves a step: Smaller -1 / Standard 0 / Larger +1, never below 1 — integer only, pixels
        // stay crisp. Missing or any other value = 0. Core owns the choice and the formula; the frontend only reads the scale.
        public const string UIScaleStepKey = "UIScaleStep";
        public int UIScaleStep
            => Get(ScopeSystem, HPEngineSection, UIScaleStepKey) switch { "-1" => -1, "1" => 1, _ => 0 };
        public static int UIScaleFor(int screenHeight, int step) => Math.Max(1, Math.Max(1, screenHeight / 480) + step);

        // NOT ORIGINAL: the one source of the Original / Enhanced presentation
        // (UEWorldFrameProducer.EnhancedVisuals). No menu item: the player always gets Enhanced; only "Original" in
        // [HPEngine] VisualMode (ini, or HPEngine --visual for snapshots, judges and the campaign sweep) selects Original.
        public const string VisualModeKey = "VisualMode";
        public const string VisualModeOriginal = "Original";
        public bool VisualModeIsOriginal
            => string.Equals(Get(ScopeSystem, HPEngineSection, VisualModeKey), VisualModeOriginal, StringComparison.OrdinalIgnoreCase);

        // Steps 1-3 of shipped GlobalSetProperty: live value, then save RIGHT HERE. The order matters — consumers
        // read the state immediately.
        //
        // FConfigCacheIni::SetString (Engine 0x103d0da0, same template in HP.exe — unwind string 0x1092da34): the
        // key is looked up with FString::operator== (Core 0x10107a20 — appStricmp = _wcsicmp in the "C" locale);
        // if the key exists and appStricmp(old, new) == 0, NOTHING changes: neither the value (the old spelling
        // stays) nor Dirty, so there is nothing to write to disk.
        // The port's form of the original's "replace the live section with the file's" (UObject::ResetConfig copies the
        // class section of Default.ini / DefUser.ini over the live config): the shipped files are never rewritten here, so
        // dropping the saved overlay of that section (or of one key) makes the shipped values the answer again.
        public void Reset(string scope, string section, string key = null)
        {
            string prefix = Key(scope, section, key ?? "");
            var drop = new List<string>();
            foreach (string k in _overlay.Keys)
                if (key is null ? k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) : string.Equals(k, prefix, StringComparison.OrdinalIgnoreCase))
                    drop.Add(k);
            foreach (string k in drop) _overlay.Remove(k);
            _store.Remove(scope, section, key);
        }

        public void Set(string scope, string section, string key, string value)
        {
            if (TryGetIni(scope, section, key, out string old) && WcsicmpEq(old, value ?? ""))
                return;
            // FConfigCacheIni::SetString: the pair Find answers (the newest = the last) takes the value, or one is added.
            string k = Key(scope, section, key);
            var list = _overlay.TryGetValue(k, out List<string> cur) ? new List<string>(cur)
                : Source(scope)?.GetAll(section, key) ?? new List<string>();
            if (list.Count == 0) list.Add(value ?? ""); else list[list.Count - 1] = value ?? "";
            _overlay[k] = list;
            _store.Save(scope, section, key, list);
        }

        // _wcsicmp in the "C" locale (the game never calls setlocale): only A-Z are folded.
        static bool WcsicmpEq(string a, string b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                char x = a[i], y = b[i];
                if (x >= 'A' && x <= 'Z') x = (char)(x + 0x20);
                if (y >= 'A' && y <= 'Z') y = (char)(y + 0x20);
                if (x != y) return false;
            }
            return true;
        }
    }
}
