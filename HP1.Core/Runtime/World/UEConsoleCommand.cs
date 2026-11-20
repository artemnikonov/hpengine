using System;
using System.Collections.Generic;
using System.IO;

namespace HP1.UEScript
{
    // The engine .ini, as UE1 reads it — FConfigFile::Read (Engine
    // 0x103cfad0..0x103cfec0), the same parse as for .int
    // (UELocalization.ParseIni): no trimming and no comments, quotes are stripped,
    // the last duplicate wins, lines before the first section are not read. Core-owned
    // because ConsoleCommand("get ini:...") answers from it and the answer
    // feeds gameplay.
    public sealed class UEIniFile
    {
        readonly Dictionary<string, Dictionary<string, string>> _sections;
        // Every pair in TMultiMap Pairs order: FConfigFile::Read inserts with TMultiMap::Add (Engine FUN_103a2e20), so a
        // repeated key keeps all its values — what UObject::LoadConfig's MultiFind of a dynamic array reads.
        readonly Dictionary<string, List<KeyValuePair<string, string>>> _pairs =
            new Dictionary<string, List<KeyValuePair<string, string>>>(UEPropNameComparer.Instance);

        public UEIniFile(string path)
        {
            string text = UELocalization.LoadFileToString(path);
            _sections = UELocalization.ParseIni(text);
            foreach (var (name, pairs) in UELocalization.ParseIniPairs(text)) _pairs[name] = pairs;
        }

        public bool HasSection(string section) => _sections.ContainsKey(section);

        // All values of `key` in `section`, in Pairs order (appStricmp key match).
        public List<string> GetAll(string section, string key)
        {
            var all = new List<string>();
            if (_pairs.TryGetValue(section, out var pairs))
                foreach (var kv in pairs)
                    if (UEPropNameComparer.Instance.Equals(kv.Key, key)) all.Add(kv.Value);
            return all;
        }

        public string Get(string section, string key)
            => _sections.TryGetValue(section, out Dictionary<string, string> values)
               && values.TryGetValue(key, out string value) ? value : null;

        // A whole section — key bindings ([Engine.Input]) are read this way, where
        // EVERY key matters, not ones picked by name.
        // Section list — needed by the file config overlay to read saved data
        // with the same parse as the shipped files.
        public IEnumerable<string> Sections => _sections.Keys;

        public IEnumerable<KeyValuePair<string, string>> Section(string section)
            => _sections.TryGetValue(section, out Dictionary<string, string> values)
                ? values : System.Linq.Enumerable.Empty<KeyValuePair<string, string>>();
    }

    // AActor::ConsoleCommand.
    //
    // Core-owned because its RESULT is gameplay: Harry.PostBeginPlay does
    // b3DSound = bool(ConsoleCommand("get ini:Engine.Engine.AudioDevice
    // Use3dHardware")) — a frontend stub returning "" silently answers "no 3D
    // sound" and, being a stub, answers it for every other query too. A stub
    // installed on the player alone is worse still: every non-player caller
    // (Camera.Timer, Harry.Timer) then hits "unknown native" instead.
    //
    // A word a ported executor takes but whose answer the port cannot give raises a loud STOP rather than a
    // plausible default: an invented answer here is indistinguishable from a real one. A command no ported
    // executor takes answers "" with a log line — the original's fall-through (end of Execute).
    public static class UEConsoleCommand
    {
        // `config` — the SINGLE mutable config state (UELevelRuntime.Config):
        // both `get ini:` and `set ini:` work on it, so a written value is read by
        // the very next command. `input` — owner of key bindings (the same UEInput
        // that reads [Engine.Input] at bootstrap): needed by KEYBINDING and `SET Input`.
        // `viewport` — the FRONTEND viewport device: the only source of
        // GetCurrentRes/GetCurrentColorDepth answers and the only thing able to
        // really resize for SetRes. Config is no substitute: it holds the SAVED
        // value, while the command asks for the ACTUAL one.
        // SAVE/LOAD (D1). Shipped `SaveGame N` and `open <url>` are
        // UGameEngine::Exec commands (u_SAVEGAME 27134, Browse -> LoadMap).
        // The file system belongs to the frontend, so the host installs both
        // handlers: save writes the bytes of UELevelRuntime.SaveGame(interp) into
        // its Save directory; `open` queues a travel request
        // (UELevelRuntime.RequestOpen) taken by the regular level travel.
        // Without a handler the command fails loudly instead of a silent "".
        public static Func<int, string> SaveGameHandler;
        public static Func<string, string> OpenHandler;
        // HP2 SAVEPACTORS: the host binds it to MakeDirectory x2 + UELevelRuntime.SavePersistentActors (see the branch).
        public static Func<string> SavePActorsHandler;
        // HP2 LOADGAME n: the host binds it to LoadPersistentActorCache + the ServerTravel to the slot's save (see the branch).
        public static Func<int, string> LoadGameHandler;
        // EXIT / QUIT — UGameEngine::Exec (Engine.dll.c:244045-244066): logs
        // "Closing by request" and appRequestExit(0) — exit after the current frame.
        // The host (frontend) decides how to end the process. Not ported:
        // calls to vtbl+0x60/+0x74 on the +0xe60 object of two levels (+0x48/+0x50)
        // before exit — purpose not established.
        public static Action ExitHandler;
        // FLUSH — Engine->Flush(1) (see the FLUSH branch); the host binds it to the world's UELevelRuntime.FlushGCache.
        public static Action FlushHandler;

        // INTERNAL Core classes absent from Core.u — their properties are
        // registered by StaticConstructor in the DLL. USystem::StaticConstructor
        // (Core.dll.c:16112): PurgeCacheDays (int @0x30), SavePath (@0x34),
        // CachePath (@0x40), CacheExt (@0x4c) — all CPF_Config (0x4000);
        // Paths/Suppress are arrays (array ExportText is not ported here, so they
        // are not in the table). Config file is StaticConfigName: Core exports
        // ONE, UObject::StaticConfigName = "System" (:17665); USystem has none of
        // its own. Section is "Package.Class".
        // HP2: the DLL-only packages UGameEngine::Init binds only after Login — the viewport's D3DDrv and InitAudio's
        // ALAudio (live HP2 EN Game.log: "Bound to D3DDrv.dll" and "Bound to ALAudio.dll" after "Login: Player"; Entry and
        // the start map load before). OpenWindow binds D3DDrv after Login, InitAudio ALAudio (UELevelRuntime.InitClientTail
        // adds them to UEConfig.BoundDllPackages at those points).
        static readonly HashSet<string> Hp2BoundAfterLogin = new(StringComparer.OrdinalIgnoreCase) { "ALAudio", "D3DDrv" };

        // HP2 only: null when `ini:<ref>` does not resolve into an HP2 late-bound DLL package, else whether it is bound yet.
        static bool? Hp2LateBound(UEConfig config, string classTok)
        {
            if (UEGameProfile.Current.Game != UEEdition.Game.HP2) return null;
            if (config.TryResolveSection(UEConfig.ScopeSystem, classTok.Substring(4)) is not string late) return null;
            int pd = late.IndexOf('.');
            if (pd <= 0 || !Hp2BoundAfterLogin.Contains(late.Substring(0, pd))) return null;
            return config.BoundDllPackages.Contains(late.Substring(0, pd));
        }

        static readonly (string Pkg, string Cls, string Prop)[] IntrinsicConfigStrings =
        {
            ("Core", "System", "PurgeCacheDays"), ("Core", "System", "SavePath"),
            ("Core", "System", "CachePath"), ("Core", "System", "CacheExt"),
        };

        // `interp` — owner of the class default objects that GET of a non-config property exports.
        // `alAudio` — HP2's Engine->Audio (null before InitAudio).
        public static string Execute(UEConfig config, string command, UEInput input = null,
            IViewportDevice viewport = null, UEInterpreter interp = null, UEALAudio alAudio = null,
            IEnumerable<UEObject> liveActors = null)
        {
            command = command ?? "";
            // HP2: only the judged commands run. GET (plan 11.4: UGameEngine::Exec [HP2 0x103a20f0] has no GET, UEngine::Exec
            // consts, StaticExec's GET HP1's). KEYNAME / KEYBINDING: UInput::Exec [HP1 0x103a5200] [HP2 0x103aec60],
            // UInput::GetKeyName [HP1 0x103a6150] [HP2 0x103afbb0] and FindKeyName [HP1 0x103a6280] [HP2 0x103afce0] are
            // HP1's bodies (native_match @: identical but the EH throw info), and the route to them is HP1's
            // (tools/hp2/exec_chain_words.py: UWindowsViewport::Exec and UViewport::Exec add only IsFullscreen, THUMBNAIL,
            // SAVETHUMBNAIL, SHOWCOLL, SHOWPATHS, none before Input->Exec). Every other HP2 command stops: HP2 adds
            // IsFullscreen, THUMBNAIL/SAVETHUMBNAIL, LOADGAME/SAVEPACTORS (SAVEPACTORS below; and changes SAVEGAME), and its audio words are
            // ALAudio's, and the other words' bodies are not compared.
            // SET ini: StaticExec's SET branch is HP1's (the HP2 body differs only in the OBJ MARKCHECK texts and assert
            // line constants) and GlobalSetProperty's closure is HP1's but appUnwindf (unwind only); its ResolveName is
            // GET's (the late-bound DLL rule below). A SET into a bound HP2 device section (ALAudio, D3DDrv) stops:
            // GlobalSetProperty writes the live device object, which the port's HP2 devices are not judged to read.
            // SET <Class> <Property> <Value>: the same StaticExec branch and GlobalSetProperty (ExecSet below;
            // tools/hp2/console_set_judge.py). SET Input: class Input's key properties come from UInput::StaticInitInput
            // [HP1 0x103a44c0] over EInputKey — the body and both enums are HP1's (audit/hp2_set_input_judge.sh), so the
            // HP1 route below (live binding, then [Engine.Input] in the user ini) is HP2's.
            bool hp2 = UEGameProfile.Current.Game == UEEdition.Game.HP2;
            // SETVOLUMES / MUTESOUNDS / UNMUTESOUNDS: no executor before UEngine::Exec's Audio takes them (exec_chain_words:
            // nor the Game.exe hook, whose words are HEAT, MPLAYER, Preferences, HideLog, EditActor, TakeFocus, ShowLog), and
            // UEngine::Exec [HP2 0x1039bdc3] asks Audio only when there is one: UALAudioSubsystem::Exec (UEALAudio.Exec);
            // with no device nobody takes them (output "").
            if (hp2 && (ParseCommand(command, "SETVOLUMES", out _) || ParseCommand(command, "MUTESOUNDS", out _)
                        || ParseCommand(command, "UNMUTESOUNDS", out _) || ParseCommand(command, "RESTARTSOUNDS", out _)))
            {
                if (alAudio is null || !alAudio.Exec(command))
                    HP1.Core.UETrace.Warn($"ConsoleCommand(\"{command}\"): no audio device takes it");
                return "";
            }
            // Original: Engine.dll ?Exec@UGameEngine@@UAEHPBGAAVFOutputDevice@@@Z [HP2 0x103a20f0], the LOADGAME branch
            // 0x103a29b5..0x103a2be7 (HP1: no such word): the remainder's first char not '0'..'9' -> 1 and nothing; else
            // LoadPersistentActorCache(), MakeDirectory(SavePath), MakeDirectory(GSys+0x40), "%s\Save%i.usa"
            // (slot dir, appAtoi), Logf, GetLevelInfo()->eventServerTravel(that, bItems 0); 1.
            if (hp2 && ParseCommand(command, "LOADGAME", out string loadTail))
            {
                if (loadTail.Length == 0 || loadTail[0] < '0' || loadTail[0] > '9') return "";
                if (LoadGameHandler is null)
                    throw new InvalidOperationException("ConsoleCommand(\"" + command + "\"): the host did not set LoadGameHandler");
                return LoadGameHandler(AppAtoi(loadTail)) ?? "";
            }
            // Original: Engine.dll ?Exec@UGameEngine@@UAEHPBGAAVFOutputDevice@@@Z [HP2 0x103a20f0], the SAVEPACTORS branch
            // 0x103a2bef..0x103a2cde (HP1: no such word): ParseCommand(SAVEPACTORS); GLevel NULL -> 0; MakeDirectory(GSys->SavePath),
            // MakeDirectory(GSys+0x40), GLevel->SavePersistentActors(GetLevelInfo()->LevelEnterText (+0x458)); 1. Issued by hgame's
            // harry.LoadLevel (ConsoleCommand("SavePActors")) before it changes the level.
            if (hp2 && ParseCommand(command, "SAVEPACTORS", out _))
            {
                if (SavePActorsHandler is null)
                    throw new InvalidOperationException("ConsoleCommand(\"" + command + "\"): the host did not set SavePActorsHandler");
                return SavePActorsHandler() ?? "";
            }
            // The resolution words: UWindowsViewport::Exec [HP1 WinDrv 0x11107bf0] [HP2 WinDrv 0x11107e90] differs from
            // HP1's only by the IsFullscreen insertion [HP2 0x11107f23..0x11107f74] and the GetRes enumeration's temp-string
            // frees [HP2 0x1110817e..0x11108256] (the port answers GetRes with the improvement below either way);
            // GetCurrentRes, GetCurrentColorDepth, GetColorDepths and SetRes are HP1's instructions (body_diff, masked
            // addresses; their strings pinned by exec_chain_words), and ResizeViewport's step 4 writes the same client
            // fields (+0x58..+0x6c, UClient::StaticConstructor native_match identical) then SaveConfig.
            // FLUSH (HP2's FESoundVideoPage.BrightnessChanged sends it): UEngine::Exec [HP1 0x103932e0] [HP2 0x1039bcf0] and
            // UEngine::Flush [HP1 0x103930b0] [HP2 0x1039bab0] differ from HP1's only by member offsets (tools/hp2/body_diff.py: Exec — Client/Audio fields
            // +0x24/+0x28/+0x2c -> +0x2c/+0x30/+0x34; Flush — Client +0x4c -> +0x54; callees identical); the FLUSH branch is
            // the same `push 1; call [vtbl+0x70]` then Log("Flushed engine caches") [HP2 Engine.dll.c:54371..54477].
            if (hp2 && !ParseCommand(command, "GET", out _) && !ParseCommand(command, "KEYNAME", out _)
                && !ParseCommand(command, "KEYBINDING", out _)
                && !ParseCommand(command, "GetRes", out _) && !ParseCommand(command, "IsFullscreen", out _)
                && !ParseCommand(command, "GetCurrentRes", out _) && !ParseCommand(command, "GetCurrentColorDepth", out _)
                && !ParseCommand(command, "GetColorDepths", out _) && !ParseCommand(command, "SetRes", out _)
                && !ParseCommand(command, "MLOD", out _) && !ParseCommand(command, "GETPING", out _) && !ParseCommand(command, "GETLOSS", out _)
                && !ParseCommand(command, "SAVEGAME", out _) && !ParseCommand(command, "FLUSH", out _)
                && !ParseCommand(command, "EXIT", out _) && !ParseCommand(command, "QUIT", out _)
                && !ParseCommand(command, "SET", out _))
                UEGameProfile.Stop(UEEdition.Game.HP2, $"ConsoleCommand(\"{command}\") (only GET, SET, KEYNAME, KEYBINDING, MLOD, GETPING, GETLOSS, SAVEGAME, EXIT, QUIT and the resolution words of HP2's exec chain are judged)");
            // UGameEngine::Exec SAVEGAME (Engine.dll.c:244086-244098; HP2 0x103a28a3..0x103a29ad the same test and appAtoi):
            // ParseCommand(SAVEGAME);
            // if the first char of the remainder is not '0'..'9' the command is ACCEPTED silently, with no save and no
            // output (return 1); otherwise appAtoi(remainder) — leading digits — and SaveGame(N) (vtbl +0xb8).
            if (ParseCommand(command, "SaveGame", out string saveTail))
            {
                if (saveTail.Length == 0 || saveTail[0] < '0' || saveTail[0] > '9') return "";
                if (SaveGameHandler is null)
                    throw new InvalidOperationException("ConsoleCommand(\"" + command + "\"): the host did not set SaveGameHandler");
                string saved = SaveGameHandler(AppAtoi(saveTail)) ?? "";
                // HP2 [0x103a28d0..0x103a29ad]: MakeDirectory x2 and a log before (the handler), Exec("RestartSounds") after.
                if (hp2) Execute(config, "RestartSounds", input, viewport, interp, alAudio);
                return saved;
            }
            // Original: Engine.dll ?Exec@UGameEngine@@UAEHPBGAAVFOutputDevice@@@Z [HP1 0x10399c27..0x1039a104] [HP2 0x103a2785..0x103a3049] (EXIT/QUIT)
            // HP2's branch is HP1's instruction for instruction but the two level members (+0x48/+0x50 -> +0x50/+0x58) and the
            // register choice (tools/hp2/console_words_judge.py); the menu's Quit (UWindowRootWindow.DoQuitGame) sends "exit".
            // EXIT/QUIT (Engine.dll.c:244045): ParseCommand — leading spaces/tabs
            // are skipped, the word is followed by any non-[A-Za-z0-9] char or the end.
            if (ParseCommand(command, "EXIT", out _) || ParseCommand(command, "QUIT", out _))
            {
                if (ExitHandler is null)
                    throw new InvalidOperationException("ConsoleCommand(\"" + command + "\"): the host did not set ExitHandler");
                ExitHandler();
                return "Closing by request";
            }
            // OPEN (Engine.dll.c:243987, first Exec branch): ParseCommand(OPEN), the remainder is
            // the URL text. The port trims trailing spaces (how FURL::FURL parses them
            // is not verified here).
            if (ParseCommand(command, "OPEN", out string openTail))
            {
                if (OpenHandler is null)
                    throw new InvalidOperationException("ConsoleCommand(\"" + command + "\"): the host did not set OpenHandler");
                return OpenHandler(openTail.Trim()) ?? "";
            }
            // HP2: the same (UViewport::Exec [HP2 0x1038c195] ParseCommand(GETPING) -> Log("0" @0x104d8a88); no executor of HP2's
            // chain takes GETLOSS: exec_chain_words).
            // GETPING — UViewport::Exec (0x103845f5): ParseCommand(GETPING), outputs the string @0x104d5990 — 4 bytes before
            // the word GETPING, L"0" (per .rdata layout). GETLOSS in the Engine disasm is only UNetConnection::Exec
            // (0x1043a348): in single player nobody handles it, ConsoleCommand output stays EMPTY (FStringOutputDevice
            // with no write). Engine.u bytecode calls it paired with GETPING; int("") = 0 — the same PacketLoss as "0".
            if (ParseCommand(command, "GETPING", out _))
                return "0";
            if (ParseCommand(command, "GETLOSS", out _))
                return "";

            // GET/SET — UObject::StaticExec (Core.dll.c:3373-3419): ParseCommand(GET|SET), then ParseToken of the class
            // (StaticFindObject; "ini:"/"usr:" resolved by ResolveName) and ParseToken of the property; for SET — spaces
            // and the whole rest of the line as the value.
            // Original: UObject::StaticExec misses (disasm 0x1015ce7d..0x1015d086): both write to the command's output
            // device with Logf(NAME_ExecWarning, ...) and return 1 —
            //   GET: class token empty / StaticFindObject NULL -> "Unrecognized class %s" (0x101c0498, the class token);
            //        property token empty / FindField NULL     -> "Unrecognized property %s" (0x101c0464, the property token);
            //   SET: the same pair (0x101c0500 with SET's class token, 0x101c04cc with its property token).
            // ParseToken always terminates its buffer (Core.dll.c:50928), so an absent token prints as "".
            bool isGet = ParseCommand(command, "GET", out string getTail);
            int gpos = 0;
            string getClassTok = isGet ? UEExecChain.ParseToken(getTail, ref gpos, 0x100, useEscape: true) : null;
            string getPropTok = isGet ? UEExecChain.ParseToken(getTail, ref gpos, 0x100, useEscape: true) : null;
            if (isGet && getClassTok.StartsWith("ini:", StringComparison.OrdinalIgnoreCase))
            {
                if (config is null)
                    throw new InvalidOperationException(
                        "ConsoleCommand(\"" + command + "\") before config state is up");
                // HP2: StaticFindObject's ResolveName [HP2 Core 0x10161db0, HP1's body] finds no package object for a
                // DLL-only package UGameEngine::Init has not bound yet, so StaticExec answers "Unrecognized class".
                if (Hp2LateBound(config, getClassTok) == false) return UnrecognizedClass + getClassTok;
                string section = config.TryResolveSection(UEConfig.ScopeSystem, getClassTok.Substring(4));
                if (section is null) return UnrecognizedClass + getClassTok;
                if (getPropTok.Length == 0) return UnrecognizedProperty;
                string property = getPropTok;
                // FindField first: a DLL-only class's undeclared property is "Unrecognized property" whatever the ini holds
                // (HP2's `get ini:Engine.Engine.AudioDevice Use3dHardware`: ALAudioSubsystem declares no Use3dHardware).
                if (UENativeClassDefaults.Undeclared(section, property)) return UnrecognizedProperty + property;
                string value = config.Get(UEConfig.ScopeSystem, section, property);
                // ── A MISSING KEY IS NOT BROKEN DATA ─────────────────────────────
                //
                // Shipped path: UObject::ResolveName (Core.dll.c:52103-52131)
                // REPLACES the name with the ini value — `ini:Engine.Engine.ViewportManager`
                // becomes the class name `WinDrv.WindowsClient` — and GET takes the
                // property value from THAT CLASS's DEFAULTS, not from the ini. A key
                // missing from the ini is therefore NORMAL: LoadConfig simply does not
                // override the default, and the default is the answer.
                //
                // Native class defaults are held by UENativeClassDefaults, which
                // `config.Get` queries as its last layer (e.g. TextureDetail, derived
                // from shipped code step by step and recorded there with its source).
                //
                // Only the UNPROVEN stays loud: a class whose defaults nobody has
                // taken from the binary. A plausible number must not be substituted —
                // an invented answer is indistinguishable from a real one.
                if (value is null)
                    throw new NotSupportedException(
                        "get ini: [" + section + "] " + property + " (STOP) — the key is absent from the ini, and that is NORMAL: "
                        + "shipped answers with the CLASS DEFAULT (ResolveName substitutes the name, "
                        + "Core.dll.c:52103; UClass::Register raises the CDO and only then calls "
                        + "LoadConfig). The default of this particular property was not taken from the binary, so "
                        + "there is no answer. Procedure: prove the value from shipped code and "
                        + "add a line with its source to UENativeClassDefaults; inventing it is forbidden.");
                return value;
            }

            // ── GET <Class> <Property> without an ini:/usr: prefix (boot A6) ─────
            //
            // UObject::StaticExec GET (Core.dll.c:3389-3419): ParseToken (0x100,
            // escape) -> StaticFindObject(UClass, ANY_PACKAGE, name) -> ParseToken
            // -> FindField<UProperty> -> if the class Defaults are non-empty
            // ExportText(0, Temp, Defaults, Defaults) -> Ar.Log(Temp). Live
            // caller — FEFilePage.ShowWindowFile (`get Core.System savepath`,
            // slot page). The default of a config property is the value from the
            // class ini (LoadConfig puts it into Defaults): section "Package.Class",
            // file by the class ConfigName. Other cases (non-config property, value
            // not from ini) fail loudly: ExportText of script class defaults is not ported.
            if (isGet)
            {
                if (config is null)
                    throw new InvalidOperationException(
                        "ConsoleCommand(\"" + command + "\") before config state is up");
                if (getClassTok.Length == 0) return UnrecognizedClass;
                string classRef = getClassTok, propName = getPropTok;
                int dot = classRef.LastIndexOf('.');
                string pkg = dot > 0 ? classRef.Substring(0, dot) : null;
                string clsName = dot > 0 ? classRef.Substring(dot + 1) : classRef;
                UELib.Core.UClass cls = HP1.Core.UEClassRegistry.Shared?.Find(clsName);
                if (cls is not null && pkg is not null
                    && !string.Equals(cls.Package?.PackageName, pkg, StringComparison.OrdinalIgnoreCase))
                    cls = null;
                if (cls is null)
                {
                    // A class absent from the packages is an INTERNAL DLL class.
                    foreach ((string ipkg, string icls, string iprop) in IntrinsicConfigStrings)
                        if (string.Equals(icls, clsName, StringComparison.OrdinalIgnoreCase)
                            && (pkg is null || string.Equals(ipkg, pkg, StringComparison.OrdinalIgnoreCase))
                            && string.Equals(iprop, propName, StringComparison.OrdinalIgnoreCase))
                            return config.Get(UEConfig.ScopeSystem, ipkg + "." + icls, iprop)
                                ?? throw new KeyNotFoundException("get " + classRef + " " + propName
                                    + ": [" + ipkg + "." + icls + "] has no key, and the constructor default was not taken");
                    throw new NotSupportedException("ConsoleCommand: " + command + " — class " + classRef + " not found (shipped: Unrecognized class)");
                }
                UELib.Core.UProperty prop = null;
                UELib.Core.UClass owner = null;
                for (UELib.Core.UClass c = cls; c is not null && prop is null; c = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(c))
                    foreach (UELib.Core.UProperty f in c.EnumerateFields<UELib.Core.UProperty>())
                        if (string.Equals(f.Name?.ToString(), propName, StringComparison.OrdinalIgnoreCase)) { prop = f; owner = c; break; }
                if (prop is null || propName.Length == 0) return UnrecognizedProperty + propName;
                bool global = prop.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.GlobalConfig);
                // Not a config property: ExportText(0, Temp, Defaults, Defaults, PortFlags 1) of the class default object
                // (disasm 0x1015cf0d..0x1015cf59; Data == Delta, so always ExportTextItem), then Log(Temp). Temp holds 256
                // TCHARs: a longer text overruns the original's stack — not emulable, loud.
                if (!global && !prop.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Config))
                {
                    if (interp is null)
                        throw new InvalidOperationException("ConsoleCommand(\"" + command + "\") without the interpreter that owns class defaults");
                    UEObject defs = interp.ClassDefaultsBag(cls);
                    object v = defs.GetProp(UEObject.PropKey(cls, owner.Name, prop.Name.ToString()));
                    if (prop.ArrayDim > 1) v = v is System.Collections.IList l && l.Count > 0 ? l[0] : null;
                    string text = UEObjectConfig.ExportTextItem(prop, v, portFlags: 1);
                    if (text.Length >= 256)
                        throw new NotSupportedException("ConsoleCommand: " + command + " — the exported default (" + text.Length
                            + " chars) overruns StaticExec's 256-TCHAR buffer in the original — STOP, §4");
                    return text;
                }
                // CPF_GlobalConfig — section of the declaring class, otherwise of the class itself.
                UELib.Core.UClass sectionOf = global ? owner : cls;
                string scope = string.Equals(cls.ConfigName?.ToString(), "User", StringComparison.OrdinalIgnoreCase)
                    ? UEConfig.ScopeUser : UEConfig.ScopeSystem;
                string section = sectionOf.Package?.PackageName + "." + sectionOf.Name;
                return config.Get(scope, section, prop.Name.ToString())
                    ?? throw new KeyNotFoundException("get " + classRef + " " + propName + ": [" + section
                        + "] (" + scope + ") has no key, and the class default from the script was not taken — inventing it is forbidden");
            }

            // ── set ini:<ClassRef> <Property> <Value…> ──────────────────────────
            //
            // Shipped path:
            //   Core.dll.c:3373-3387 — SET parses class and property as tokens,
            //     then SKIPS SPACES and passes the WHOLE REST OF THE LINE as the
            //     value (hence the split on the FIRST space after the property,
            //     not the last as for `get`);
            //   Core.dll.c:56604 UObject::GlobalSetProperty — three steps in a row:
            //     live objects of the class + notification (vtable +0x40), then
            //     class defaults, then SaveConfig — WRITTEN TO DISK IMMEDIATELY, in
            //     the command itself, not when the menu closes.
            //
            // In the port the live value and the defaults are one UEConfig state
            // (consumers read it), and UEConfig.Set saves in the same call. Order
            // is kept: value first, then the write.
            //
            // WHAT THIS IS NOT YET (honest status, not "closed"):
            // the real GlobalSetProperty resolves the REAL class and UProperty,
            // parses the value with typed ImportText by property type, updates live
            // instances AND class defaults and fires the notification (vtable +0x40).
            // Here the class reference is reduced to a section name and the value is
            // stored as a string without type parsing.
            //
            // For the FIVE keys the campaign uses this is sufficient and proven by
            // fixtures; as a GENERAL port of GlobalSetProperty it is not.
            // Status by slice:
            //   MusicVolume/SoundVolume — frontend-linked: volume consumers read the
            //     value, slider and sound move together;
            //   Brightness — frontend-linked: the HPEngine frame reads the value and
            //     applies the D3DDrv gamma ramp (Flush 0x10002f42, ScreenEffects);
            //     menu slider and screen brightness move together;
            //   TextureDetail — frontend-linked: the HPEngine frame reads the value
            //     and picks the first mip via UETextureLod (UTexture::Lock, Render skins);
            //   arbitrary `set ini:` of any class — NOT closed: the port has no typing,
            //     live instances or notification.
            bool isSet = ParseCommand(command, "SET", out string setTail);
            if (isSet && setTail.StartsWith("ini:", StringComparison.OrdinalIgnoreCase))
            {
                if (config is null)
                    throw new InvalidOperationException(
                        "ConsoleCommand(\"" + command + "\") before config state is up");
                // ParseToken class, ParseToken property, then only SPACES are skipped (Core.dll.c:3381) and the rest is
                // the value.
                int spos = 0;
                string classTok = UEExecChain.ParseToken(setTail, ref spos, 0x100, useEscape: true);
                // HP2: the same ResolveName as GET: an unbound late DLL package is "Unrecognized class"; a bound one stops.
                bool? bound = Hp2LateBound(config, classTok);
                if (bound == false) return UnrecognizedClass + classTok;
                string section = config.TryResolveSection(UEConfig.ScopeSystem, classTok.Substring(4));
                if (section is null) return UnrecognizedClass + classTok;
                string property = UEExecChain.ParseToken(setTail, ref spos, 0x100, useEscape: true);
                if (property.Length == 0) return UnrecognizedProperty;
                while (spos < setTail.Length && setTail[spos] == ' ') spos++;
                string value = setTail.Substring(spos);
                // HP2, a bound late DLL class: GlobalSetProperty [HP2 Core 0x1015f300] ImportTexts the value into every live
                // object of the class, calls its PostEditChange (vtable +0x40), then into the defaults and SaveConfig (the
                // config write below). Ported for the live ALAudio device's MusicVolume / SoundVolume (UFloatProperty:
                // appAtof; UEALAudio.PostEditChange); any other live device property stops.
                if (bound == true)
                {
                    bool music = string.Equals(property, "MusicVolume", StringComparison.OrdinalIgnoreCase);
                    if (!section.StartsWith("ALAudio.", StringComparison.OrdinalIgnoreCase) || alAudio is null
                        || !(music || string.Equals(property, "SoundVolume", StringComparison.OrdinalIgnoreCase)))
                        UEGameProfile.Stop(UEEdition.Game.HP2, $"ConsoleCommand(\"{command}\") (SET into a live HP2 device object)");
                    if (music) alAudio.MusicVolume = UEValue.LeadingAtof(value); else alAudio.SoundVolume = UEValue.LeadingAtof(value);
                    alAudio.PostEditChange();
                }
                config.Set(UEConfig.ScopeSystem, section, property, value);
                return "";
            }

            // ── SET Input <Key> [<Command>] ─────────────────────────────────────
            //
            // The same shipped SET command but WITHOUT the `ini:` prefix: class `Input`,
            // "property" is the key name, value is the binding. An empty value
            // removes it: FEOptionsPage.uc:704 sends `"SET Input " $ KeyName`
            // precisely to unbind, and :727 sends `("SET Input" @ Name) @ Alias`
            // to bind.
            //
            // Target is the SAME UEInput that answers KEYBINDING and reads
            // [Engine.Input] at bootstrap. A second binding dictionary must not be
            // created here: menu and game would silently diverge.
            //
            // A REBIND MUST SURVIVE A RESTART. A live dictionary write is not enough:
            // the next bootstrap rereads the shipped DefUser.ini. Shipped persists it
            // because `SET Input` goes through the same GlobalSetProperty: UInput
            // declares each key binding as a config property, so the third step
            // (SaveConfig, Core.dll.c:56604) writes `[Engine.Input] <Key>=` to disk
            // RIGHT AWAY.
            //
            // The port repeats both steps in the same order: live value first
            // (UEInput), then the save (UEConfig.Set in the User scope — where
            // DefUser.ini lives, from which the section is read at bootstrap).
            // An EMPTY value is saved AS EMPTY, not deleted: the empty string in the
            // overlay overrides the shipped binding and makes the unbind persistent
            // (LoadBindings skips empty ones).
            if (isSet && Tokens(setTail) is { Length: > 0 } st && string.Equals(st[0], "Input", StringComparison.OrdinalIgnoreCase))
            {
                if (input is null)
                    throw new InvalidOperationException(
                        "ConsoleCommand(\"" + command + "\") without a key binding owner");
                string tail = setTail.Substring(setTail.IndexOf(st[0], StringComparison.OrdinalIgnoreCase) + st[0].Length).TrimStart(' ', '\t');
                int keyEnd = tail.IndexOf(' ');
                string key = keyEnd < 0 ? tail : tail.Substring(0, keyEnd);
                string bound = keyEnd < 0 ? "" : tail.Substring(keyEnd + 1).TrimStart();
                if (key.Length == 0) throw new InvalidDataException("bad SET Input: " + command);
                input.KeepPadDefaults(config, key);   // improvement D-75 (not original): pad defaults become bindings first
                input.SetBinding(key, bound);
                if (config is null)
                    throw new InvalidOperationException(
                        "SET Input without config state: the rebinding would not survive a restart");
                config.Set(UEConfig.ScopeUser, InputSection, key, bound);
                return "";
            }
            // ── SET <Class> <Property> <Value…> (every other SET) ───────────────
            // Original: Core.dll ?StaticExec@UObject@@SAHPBGAAVFOutputDevice@@@Z [HP1 0x1015cf96..0x1015d086] [HP2 0x10160356..0x10160446] (SET branch)
            //   ParseToken(0x100, escape) -> StaticFindObject(UClass, ANY_PACKAGE) -> ParseToken -> FindField (thunk 0x101011f9,
            //   FUN_10147c30: the class's properties, then each GetInheritanceSuper's, appStricmp) -> skip ' ' only -> the rest
            //   is the value -> GlobalSetProperty(value, Class, Property, Property->Offset, 1). No property-flag test anywhere
            //   on this path (disasm): edit-only, const, native and config properties are all set alike.
            if (isSet)
            {
                if (interp is null || liveActors is null)
                    throw new InvalidOperationException("ConsoleCommand(\"" + command + "\") without the live objects and class defaults it writes");
                // AActor::PostEditChange [HP1 Engine 0x10379aa0] [HP2 0x103049e4 thunk]: UObject::PostEditChange -> Modify (GUndo
                // NULL in the game: nothing) and the RF flag only under GIsEditor — no effect in the game.
                return ExecSet(command, interp, liveActors, _ => { },
                    defaults => UEObjectConfig.SaveConfig(config, defaults, interp, isInstance: false));
            }
            // ── KEYNAME <n> (Engine.dll.c:251664 -> UInput::GetKeyName :72765) ──
            //
            // Shipped branch: `ParseCommand(KEYNAME)` -> `appAtoi(remainder)` ->
            // vtable slot +0x74 (UInput::GetKeyName); the result is logged, i.e. it
            // is the command's return value.
            //
            // Full body of GetKeyName:
            //     if (0 <= key && key < 0xFF) {
            //         name = EInputKey->Names(key);        // engine table
            //         if (appStrlen(name + 0xc) > 3)       // LONGER THAN THREE
            //             return name + 0x12;              // OFFSET BY 3 CHARS
            //     }
            //     return "";
            //
            // The offsets 0xc/0x12 differ by 6 bytes = 3 wide chars: the name is
            // returned WITHOUT its first three characters, i.e. without the `IK_`
            // prefix, and a name shorter than four characters yields an empty string.
            // This is a direct reading of both constants, not a guess.
            //
            // Called by FEOptionsPage.uc:471 — the options page lists key names.
            if (ParseCommand(command, "KEYNAME", out string keynameTail))
                return KeyName(AppAtoi(keynameTail));

            // ── KEYBINDING <key name> (Engine.dll.c:251690-251702) ──────────────
            //
            // Shipped branch when KEYNAME did not match and KEYBINDING did:
            //     if (!FindKeyName(arg, out key)) return 1;      // NO VALUE
            //     result = Bindings[key].Len == 0 ? "" : Bindings[key];
            //
            // FindKeyName (:99386, body FUN_103a6280) builds "IK_%s", looks up an
            // EXISTING FName (flag 0 = find, do not create) and compares linearly
            // with the names of the same enum EInputKey. UE1 name comparison is
            // case-insensitive.
            //
            // Bindings come from the SINGLE owner — UEInput, which loads
            // [Engine.Input] at bootstrap. An unknown key name and an unbound key
            // give THE SAME observable answer — an empty string — as in shipped.
            // The ParseCommand remainder is not tail-trimmed (shipped does no Trim).
            if (ParseCommand(command, "KEYBINDING", out string wanted))
            {
                if (FindKeyName(wanted) < 0) return "";
                // improvement D-75 (not original): while the Controls page loads its list, one device's keys (UEInput.ControlsLoad)
                return input is not null && input.TryGetKeyBindingAnswer(wanted, out string bound)
                    ? bound : "";
            }

            // ── RESOLUTION FAMILY (WinDrv.dll.c, UWindowsClient::Exec) ──────────
            //
            // The menu builds its list as: `GetRes` -> split on spaces -> filter
            // IsSupportedResolution -> `GetCurrentRes` for the selected item
            // (FEOptionsPage.uc:371-393).
            //
            // GetRes (WinDrv.dll.c:5126) DOES NOT QUERY THE DEVICE IN A WINDOW:
            //     if (!(flags & 8)) {                  // not fullscreen
            //         if (!(flags & 4)) return 1;      // no answer at all
            //         Log("<fixed list>");             // constant from the binary
            //         return 1;
            //     }
            //     ...enumerate driver modes for the current colour depth...
            // Enumeration happens only in fullscreen. The faithful windowed answer is
            // the constant, extracted from WinDrv.dll (UTF-16 at offset 0x1df0c).
            // NOT ORIGINAL: the device's screen modes in any window
            // mode, see IViewportDevice.DisplayModes; the menu filter
            // (FEOptionsPage.IsSupportedResolution) is replaced — UEPortScriptOverrides.
            // HP2 too (author decision 2026-11-16, DECISIONS.md): the same improvement; HP2's FESoundVideoPage list is
            // rebuilt with every mode after its shipped filter (UEPortScriptOverrides.HP2Table).
            if (ParseCommand(command, "GetRes", out _))
                return RequireViewport(viewport, "GetRes").DisplayModes;

            // Original: WinDrv.dll ?Exec@UWindowsViewport@@UAEHPBGAAVFOutputDevice@@@Z [HP2 0x11107e90] (IsFullscreen branch 0x11107f23..0x11107f74, HP2 only)
            // IsFullscreen: Logf("True" @0x1111e140 / "False" @0x1111e130) on BlitFlags & 1 ([this+0x190], IsFullscreen()'s
            // bit, IViewportDevice.Fullscreen); returns that bit, which execConsoleCommand discards [HP2 Engine 0x103e2d76].
            if (hp2 && ParseCommand(command, "IsFullscreen", out _))
                return RequireViewport(viewport, "IsFullscreen").Fullscreen ? "True" : "False";

            // ToggleFullscreen (UWindowsViewport::Exec, WinDrv.dll.c:5090 -> ToggleFullscreen :697): from a window —
            // TryRenderDevice(GameRenderDevice, -1, -1, -1, Fullscreen=1), size from the Fullscreen* keys (ResizeViewport
            // at -1, :15133); from fullscreen — EndFullscreen, window at Windowed*. A successful resize saves the key
            // pair of the new mode (:15366-15379) — the same step 4 as SetRes.
            if (ParseCommand(command, "ToggleFullscreen", out _))
            {
                ToggleFullscreen(config, viewport);
                return "";
            }

            // GetCurrentRes (:5096) — size of the LIVE viewport, `%ix%i`.
            // ONLY the frontend answers: config holds the saved value, the command
            // asks for the actual one, and they must not be swapped — the whole menu
            // branch rests on this comparison (FEOptionsPage.uc:539).
            if (ParseCommand(command, "GetCurrentRes", out _))
            {
                IViewportDevice vp = RequireViewport(viewport, "GetCurrentRes");
                return vp.SizeX.ToString(System.Globalization.CultureInfo.InvariantCulture)
                     + "x" + vp.SizeY.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            // GetColorDepths (:5113) — the constant "16 32" from the binary.
            if (ParseCommand(command, "GetColorDepths", out _))
                return "16 32";

            // GetCurrentColorDepth (:5102) — `(colour bytes ?: 2) * 8`. Colour
            // bytes are client state; THIS reader substitutes two for zero, not the
            // device — that is how the binary does it.
            if (ParseCommand(command, "GetCurrentColorDepth", out _))
            {
                int bytes = RequireViewport(viewport, "GetCurrentColorDepth").ColorBytes;
                return ((bytes == 0 ? 2 : bytes) * 8)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            // ── SetRes <W>x<H>[x<Depth>] (WinDrv.dll.c:5238) ───────────────────
            //
            // The menu calls it on "apply" (FEOptionsPage.uc:545) and on revert
            // (:568, HPConsole.uc:926). Step order is shipped's, in full:
            //
            //   1) appAtoi(string) -> W; after the first 'x'/'X' -> H; after the
            //      second -> Depth. The parse is EXACTLY this: appAtoi takes the
            //      leading integer and does not treat garbage as an error;
            //   2) ZERO WIDTH OR HEIGHT — DO NOTHING. Shipped just returns on
            //      `if (X && Y)`, touching neither viewport nor config. No exception
            //      here, and there must not be one;
            //   3) real resize through the frontend device;
            //   4) ONLY ON SUCCESS — write the ACTUALLY applied values to config and
            //      save (WinDrv.dll.c:15370-15379):
            //          WindowedViewportX = AppliedX
            //          WindowedViewportY = AppliedY
            //          WindowedColorBits = AppliedBytes * 8
            //          SaveConfig()
            //      The applied values come from the device, not the command: it may
            //      apply something other than requested, and the ini gets what
            //      happened. Failure leaves no trace anywhere.
            if (ParseCommand(command, "SetRes", out string setResTail))
            {
                if (config is null)
                    throw new InvalidOperationException("SetRes before config state is up");
                IViewportDevice vp = RequireViewport(viewport, "SetRes");

                string tail = setResTail;
                int sizeX = AppAtoi(tail);
                string afterX = AfterSeparator(tail);
                int sizeY = AppAtoi(afterX);
                int colorBits = AppAtoi(AfterSeparator(afterX));

                if (sizeX == 0 || sizeY == 0) return "";        // step 2
                // step 3 + step 4: save ONLY once applied.
                //
                // THE COMMAND DOES NOT CHANGE THE MODE: shipped passes
                // `IsFullscreen()` as is into the resize (WinDrv.dll.c:5326-5330 ->
                // RenDev->SetRes(X, Y, Bytes, IsFullscreen())), i.e. the SIZE of the
                // current mode changes, not the mode.
                if (vp.Resize(sizeX, sizeY, colorBits / 8, vp.Fullscreen)
                    == ViewportResizeOutcome.Applied)
                    CommitAppliedResize(config, vp);
                return "";
            }

            // ── FLUSH (Engine.dll.c:56454, UGameEngine::Exec) ──────────────────
            //
            // Disassembly 0x103933e7-0x103933ef: `push 1; call [vtbl+0x70]` on the game engine (vtable 0x104714fc,
            // stored by the ctor at 0x1034fc43; slot 0x1047156c = 0x10302063 -> UEngine::Flush, not overridden),
            // then Ar.Log("Flushed engine caches"). UEngine::Flush (Engine.dll.c:46718-46735): GCache.Flush(0, ~0, 0)
            // — its game-visible part is Core's UELevelRuntime.FlushGCache — then Client->Flush(1) (client vtbl
            // +0x5c): render-device caches; nothing the frame shows depends on them (the frontend reads
            // Brightness/TextureDetail from config every frame). The menu calls FLUSH right after a brightness
            // change (FEOptionsPage.uc:527).
            if (ParseCommand(command, "FLUSH", out _))
            {
                if (FlushHandler is null)
                    throw new InvalidOperationException("ConsoleCommand(\"" + command + "\"): the host did not set FlushHandler");
                FlushHandler();
                return "Flushed engine caches";
            }

            // Original: Render.dll ?Exec@URender@@UAEHPBGAAVFOutputDevice@@@Z [HP1 0x10b1f240] [HP2 body_diff identical] (MLOD branch)
            // MLOD: no executor before URender::Exec takes it (exec_chain_words). Tail "" (appStrcmp, 0x10b719c8) -> Logf("%f",
            // GlobalShapeLOD); else appAtof(tail) > 0.0 (.rdata 0x10b385bc; fcomp, test ah 0x41) -> GlobalShapeLOD (URender+0x4c:
            // FExec at +0x28, field +0x24) = (float)appAtof, then Logf("Global mesh shape LOD distance %f"). HP2 only: no HP1
            // script sends it. ponytail: LeadingAtof is float, so a positive tail below the float range (1e-50) compares as 0
            // and is not stored where the double compare would store 0.0f; no shipped trigger (HP2 sends 0.1..1.0).
            if (hp2 && ParseCommand(command, "MLOD", out string mlodTail))
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (mlodTail.Length == 0) return UEMeshLod.ConsoleGlobalShapeLOD.ToString("F6", inv);
                float v = UEValue.LeadingAtof(mlodTail);
                if (v > 0f) UEMeshLod.ConsoleGlobalShapeLOD = v;
                return "Global mesh shape LOD distance " + ((double)UEMeshLod.ConsoleGlobalShapeLOD).ToString("F6", inv);
            }

            // ── SNAP <n> (Engine.dll.c:50313, UViewport::Exec) ─────────────────
            //
            // Shipped: SnapX = SizeX/2^n, SnapY = SizeY/2^n (viewport +0xd0/+0xd4)
            // and a frame capture into the snap buffer — the save slot thumbnail
            // (paired with SAVESNAP, :50408; read by FESlotPage/FEFilePage). NO WORLD
            // WRITES — the operation is entirely viewport-side.
            //
            // The menu calls it as the FIRST line of FEBook.OpenBook ("Snap 3"); without
            // a handler OpenBook fails before LaunchUWindow and no menu opens.
            //
            // NO FRAME CAPTURE — AND THE PLAYER DOES NOT SEE IT. The snapshot buffer is
            // read ONLY by the SAVESNAP command (Engine.dll.c:233080, writes a BMP) and
            // native 322 CreateTextureFromScreenShot; campaign scripts call neither
            // (the only SNAP is FEBook.uc:607). Slot thumbnails come from Save/SGS <level>.bmp
            // and DefaultGameSnap.bmp via native 321 (FESlotPage), which the port does.
            // Hence one informational line per process.
            if (ParseCommand(command, "SNAP", out _))
            {
                if (!_snapNoted)
                {
                    _snapNoted = true;
                    HP1.Core.UETrace.Info("[ConsoleCommand] SNAP: the frame is not captured — the snapshot buffer "
                        + "has no consumer in the campaign (SAVESNAP/native 322 are not called); command: " + command);
                }
                return "";
            }

            // ── A COMMAND NO EXECUTOR TAKES: "" ──────────────────────────────────
            //
            // AActor::execConsoleCommand (Engine 0x10406d50) and APlayerPawn::execConsoleCommand (0x103d5770) call
            // Engine->Exec(Cmd, StrOut) and DISCARD its return value (no test of eax after the calls at 0x10406e34,
            // 0x103d587b, 0x103d5916): the result is whatever the executors wrote. UGameEngine::Exec ends in
            // UEngine::Exec (0x1039a062) and returns 0 at 0x1039a06f; UEngine::Exec, after GSys, UObject::StaticExec,
            // GCache, GExec, Client, Audio and its own FLUSH/CRACKURL, returns 0 at 0x1039367f writing nothing. So an
            // unrecognised command answers "" silently. "Unrecognized command" (Core.int [Errors] Exec) is printed
            // only by the typed console — Engine.u Console state Typing, when UConsole.ConsoleCommand returns false —
            // and by the log window, WTerminal::TypeChar (Window.dll); never by this native.
            // A word a listed native executor takes (UEExecChain tables, UEngine::Exec's sub-executors included) but the
            // port does not answer stays a loud STOP. Unknown: HP.exe's GExec (FExecHook::Exec) — the shipped exe is
            // packed, its words are not readable; a word only it takes would land on "" here (logged; kept by the author's
            // decision 2026-11-15, D-89 (3)).
            if (UEExecChain.KnownNativeWord(command) is string word)
                throw new NotSupportedException("ConsoleCommand: " + command + " — word " + word + " of a native executor the port does not answer");
            if (_unrecognisedNoted.Add(command))   // once per distinct command: script may send it every tick
                HP1.Core.UETrace.Warn("[ConsoleCommand] \"" + command + "\": taken by no ported executor; answers \"\" "
                    + "(UEngine::Exec falls through to 0 with no output, Engine 0x1039367f)");
            return "";
        }

        // The SET branch of StaticExec (above): the output line ("" when GlobalSetProperty ran). `postEditChange` is the
        // object's vtable +0x40 call, `saveConfig` SaveConfig(Defaults, CPF_Config, NULL). tools/hp2/console_set_judge.py
        // runs it through runtime_poc --hp2-setcase.
        internal static string ExecSet(string command, UEInterpreter interp, IEnumerable<UEObject> liveActors,
            Action<UEObject> postEditChange, Action<UEObject> saveConfig)
        {
            ParseCommand(command, "SET", out string setTail);
            int spos = 0;
            string classTok = UEExecChain.ParseToken(setTail, ref spos, 0x100, useEscape: true);
            if (classTok.Length == 0) return UnrecognizedClass;
            UELib.Core.UClass cls = FindScriptClass(classTok)
                ?? throw new NotSupportedException("ConsoleCommand: " + command + " — class " + classTok + " not found in the packages "
                    + "(shipped: Unrecognized class unless a DLL-only class has that name) — STOP, §4");
            string propTok = UEExecChain.ParseToken(setTail, ref spos, 0x100, useEscape: true);
            UELib.Core.UProperty prop = propTok.Length == 0 ? null : FindProperty(cls, propTok, out _);
            if (prop is null) return UnrecognizedProperty + propTok;
            while (spos < setTail.Length && setTail[spos] == ' ') spos++;
            GlobalSetProperty(setTail.Substring(spos), cls, prop, liveActors, interp.ClassDefaultsBag(cls), postEditChange, saveConfig);
            return "";
        }

        // StaticFindObject(UClass, ANY_PACKAGE, "[Package.]Class") over the loaded script classes.
        static UELib.Core.UClass FindScriptClass(string classRef)
        {
            int dot = classRef.LastIndexOf('.');
            string pkg = dot > 0 ? classRef.Substring(0, dot) : null;
            UELib.Core.UClass cls = HP1.Core.UEClassRegistry.Shared?.Find(dot > 0 ? classRef.Substring(dot + 1) : classRef);
            return cls is not null && pkg is not null && !string.Equals(cls.Package?.PackageName, pkg, StringComparison.OrdinalIgnoreCase)
                ? null : cls;
        }

        // FindField<UProperty> (FUN_10147c30): the class's own properties, then each super's, case-insensitive.
        static UELib.Core.UProperty FindProperty(UELib.Core.UClass cls, string name, out UELib.Core.UClass owner)
        {
            for (owner = cls; owner is not null; owner = HP1.Core.UEClassRegistry.Shared.CanonicalSuper(owner))
                foreach (UELib.Core.UProperty f in owner.EnumerateFields<UELib.Core.UProperty>())
                    if (string.Equals(f.Name?.ToString(), name, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        // Native actor classes whose PostEditChange (vtable +0x40) is not AActor's: AZoneInfo's (ZoneInfo, LevelInfo,
        // SkyZoneInfo, WarpZoneInfo), AMover's, AParticleFX's — the vtables of HP1 1.53 and HP2 Engine.dll (every other
        // exported actor vtable holds ?PostEditChange@AActor@@UAEXXZ). Their bodies are not ported.
        static readonly string[] OwnPostEditChange = { "ZoneInfo", "Mover", "ParticleFX" };

        // Original: Core.dll ?GlobalSetProperty@UObject@@SAXPBGPAVUClass@@PAVUProperty@@HH@Z [HP1 0x1015bf40] [HP2 0x1015f300]
        //   bNotify (always 1 from StaticExec): for every GObjObjects entry that IsA(Class): Property->ImportText(Value,
        //   Object + Offset, 1) (vtable +0x88: element 0 only, PortFlags 1 — no ImportText body tests bit 1, only bit 2) and
        //   Object->PostEditChange() (vtable +0x40); then ImportText(Value, Class->Defaults + Offset, 1) and
        //   SaveConfig(Defaults, CPF_Config, NULL). A refused text (ImportText's NULL) leaves the element as it was.
        //   PostEditChange of an actor is AActor's [HP1 Engine 0x10379aa0]: UObject::PostEditChange -> Modify (GUndo NULL
        //   in the game: nothing), and the RF flag only under GIsEditor — no effect, so the walk order is unobservable. The
        //   port's live objects of a class are its actors (the game level's and Entry's); a non-actor class stops.
        internal static void GlobalSetProperty(string value, UELib.Core.UClass cls, UELib.Core.UProperty prop,
            IEnumerable<UEObject> liveActors, UEObject defaults, Action<UEObject> postEditChange, Action<UEObject> saveConfig)
        {
            var reg = HP1.Core.UEClassRegistry.Shared;
            if (!reg.IsChildOfClass(cls, reg.Find("Actor")))
                throw new NotSupportedException("SET " + cls.Name + " " + prop.Name + ": live objects of a non-actor class are not "
                    + "enumerable in the port (GlobalSetProperty walks GObjObjects) — STOP, §4");
            string owner = (prop.Outer as UELib.Core.UClass)?.Name ?? prop.Outer?.Name.ToString();
            foreach (UEObject a in liveActors)
            {
                if (a?.Class is null || !reg.IsChildOfClass(a.Class, cls)) continue;
                foreach (string n in OwnPostEditChange)
                    if (reg.Find(n) is { } k && reg.IsChildOfClass(a.Class, k))
                        throw new NotSupportedException("SET " + cls.Name + " " + prop.Name + ": " + a.GetProp(PK.Name)
                            + " is a " + n + ", whose native PostEditChange is not ported — STOP, §4");
                ImportElement0(a, owner, prop, value);
                postEditChange(a);
            }
            ImportElement0(defaults, owner, prop, value);
            saveConfig(defaults);
        }

        static void ImportElement0(UEObject obj, string owner, UELib.Core.UProperty prop, string value)
        {
            string key = UEObject.PropKey(obj.Class, owner, prop.Name.ToString());
            object slot = obj.GetProp(key);
            if (prop.ArrayDim > 1)
            {
                if (slot is not System.Collections.IList list || list.Count == 0)
                    throw new InvalidOperationException("SET: static array " + owner + "." + prop.Name + " not preallocated");
                object cur = list[0];
                UEObjectConfig.ImportItem(prop, value, 0, 1, ref cur, out bool w);
                if (w) list[0] = cur;
                return;
            }
            UEObjectConfig.ImportItem(prop, value, 0, 1, ref slot, out bool wrote);
            if (wrote) obj.SetProp(key, slot);
        }

        // Core .rdata 0x101c0498 / 0x101c0500 and 0x101c0464 / 0x101c04cc, "%s" filled by the caller.
        const string UnrecognizedClass = "Unrecognized class ", UnrecognizedProperty = "Unrecognized property ";

        static bool _snapNoted;
        static readonly HashSet<string> _unrecognisedNoted = new(StringComparer.OrdinalIgnoreCase);

        // ── STEP 4 OF SHIPPED RESIZE: SAVE WHAT WAS APPLIED ────────────────────
        //
        // WinDrv.dll.c:15366-15379, verbatim and in the same order. Values are read
        // FROM THE DEVICE, not from command arguments: shipped writes the resize
        // result into the client, and the device may apply something other than
        // requested.
        // THE MODE PICKS THE KEY PAIR. In shipped it is the same branch as when
        // reading the size (:15133-15144): in fullscreen the applied values go to
        // the Fullscreen* keys, in a window to Windowed*.
        // The toggle itself, also called without the console word: Alt+Enter (WinDrv calls ToggleFullscreen directly,
        // WinDrv.dll.c:3335-3343) and the Display Mode menu item (UEPortScriptOverrides) — HP2 included, whose exec chain
        // does not take the word from a script (no shipped HP2 script sends it).
        public static void ToggleFullscreen(UEConfig config, IViewportDevice viewport)
        {
            if (config is null) throw new InvalidOperationException("ToggleFullscreen before config state is up");
            IViewportDevice vp = RequireViewport(viewport, "ToggleFullscreen");
            bool toFull = !vp.Fullscreen;
            (int x, int y) = UEViewportStartup.SavedSize(config, toFull);
            if (vp.Resize(x, y, 0, toFull) == ViewportResizeOutcome.Applied) CommitAppliedResize(config, vp);
        }

        // The player's mode switch — Alt+Enter (CoreInputSink) and the options page's Display Mode item, HP1 and HP2 — is
        // also the next start's mode: StartupFullscreen, the key UEViewportStartup opens the window with (author 2026-11-18,
        // NOT ORIGINAL, with D-70's Display Mode; the console word ToggleFullscreen stays the original's toggle alone).
        public static void ToggleDisplayMode(UEConfig config, IViewportDevice viewport)
        {
            ToggleFullscreen(config, viewport);
            Execute(config, "set ini:Engine.Engine.ViewportManager StartupFullscreen " + (viewport.Fullscreen ? "True" : "False"), null, viewport);
        }

        internal static void CommitAppliedResize(UEConfig config, IViewportDevice vp)
        {
            string section = config.ResolveSection(UEConfig.ScopeSystem, UEConfig.ViewportRef);
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            bool fullscreen = vp.Fullscreen;
            config.Set(UEConfig.ScopeSystem, section,
                fullscreen ? "FullscreenViewportX" : "WindowedViewportX",
                vp.SizeX.ToString(invariant));
            config.Set(UEConfig.ScopeSystem, section,
                fullscreen ? "FullscreenViewportY" : "WindowedViewportY",
                vp.SizeY.ToString(invariant));
            config.Set(UEConfig.ScopeSystem, section,
                fullscreen ? "FullscreenColorBits" : "WindowedColorBits",
                (vp.ColorBytes * 8).ToString(invariant));
        }

        // Fixed windowed mode list from WinDrv.dll — exactly what shipped
        // logs for GetRes outside fullscreen.
        public const string WindowedResolutions = "320x240 400x300 512x384 640x480 800x600";

        // Key binding section. The same for reading (bootstrap reads it from
        // DefUser.ini) and writing (`SET Input` saves there) — the two must not
        // diverge.
        public const string InputSection = "Engine.Input";

        // A missing device fails LOUDLY. Answering with the saved config value
        // would return exactly the lie this boundary exists to remove: the menu
        // would compare its wish with itself.
        static IViewportDevice RequireViewport(IViewportDevice viewport, string command)
            => viewport ?? throw new InvalidOperationException(
                command + ": the frontend did not provide a viewport device. This is the ACTUAL screen "
                + "state (WinDrv.dll.c:5096), and replacing it with the one saved in the ini is forbidden — "
                + "the menu branches on a comparison with it (FEOptionsPage.uc:539).");

        // Tail after the first 'x' or 'X'; empty string if there is no separator.
        // Exactly as shipped: appStrchr(Str,'x') ?: appStrchr(Str,'X'), and the
        // result is advanced by one char (WinDrv.dll.c:5240-5299).
        // ParseCommand (Core.dll.c:51670): spaces/tabs before the word are skipped, the word is
        // case-insensitive, followed by a NON ASCII letter/digit (or the end), then spaces/tabs
        // are skipped; `tail` is the rest after them.
        // ParseToken (Core FUN_1016c5e0, UseEscape) — only the "word up to space/tab" form here; quoted tokens
        // do not occur in campaign commands.
        static string[] Tokens(string s) => (s ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        internal static bool ParseCommand(string command, string word, out string tail)
        {
            tail = null;
            int i = 0;
            while (i < command.Length && (command[i] == ' ' || command[i] == '\t')) i++;
            if (command.Length - i < word.Length
                || string.Compare(command, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            i += word.Length;
            if (i < command.Length)
            {
                char c = command[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return false;
            }
            while (i < command.Length && (command[i] == ' ' || command[i] == '\t')) i++;
            tail = command.Substring(i);
            return true;
        }

        static string AfterSeparator(string text)
        {
            int at = text.IndexOf('x');
            if (at < 0) at = text.IndexOf('X');
            return at < 0 ? "" : text.Substring(at + 1);
        }

        // UInput::FindKeyName (Engine.dll.c:99386): "IK_" + name, searched among
        // the names of enum EInputKey. -1 if no such name.
        public static int FindKeyName(string name)
        {
            IReadOnlyList<string> names = HP1.Core.UEClassRegistry.Shared?.EnumNames("EInputKey", "Actor");
            if (names is null)
                throw new InvalidOperationException(
                    "KEYBINDING: enum EInputKey not found in the loaded packages");
            string want = "IK_" + (name ?? "");
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], want, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        static int AppAtoi(string text) => UEValue.LeadingAtoi(text);

        public static string KeyName(int key)
        {
            if (key < 0 || key >= 0xFF) return "";
            IReadOnlyList<string> names = HP1.Core.UEClassRegistry.Shared?.EnumNames("EInputKey", "Actor");
            // LOUD, NOT AN EMPTY STRING: an empty answer here is indistinguishable
            // from an honest "this index has no name", and the menu would silently
            // lose key names.
            if (names is null)
                throw new InvalidOperationException(
                    "KEYNAME: enum EInputKey not found in the loaded packages — "
                    + "the key name table comes from the data (Engine.dll.c:72765), "
                    + "it must not be invented");
            if (key >= names.Count) return "";
            string name = names[key];
            // The `Names(key) == 0` branch in shipped goes to HardcodedNames; it never
            // happens with the full EInputKey, and the contents of that table cannot be
            // guessed here — so the case is LOUD, not invented.
            if (name is null)
                throw new InvalidOperationException(
                    "KEYNAME: EInputKey has no name for index " + key
                    + " — shipped falls back to HardcodedNames, and that behavior has not been read");
            return name.Length > 3 ? name.Substring(3) : "";
        }
    }
}
