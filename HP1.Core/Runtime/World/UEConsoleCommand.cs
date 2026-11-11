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

        public UEIniFile(string path)
        {
            _sections = UELocalization.ParseIni(UELocalization.LoadFileToString(path));
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
    // Anything not covered raises a loud STOP rather than a plausible default
    // (§8): an invented answer here is indistinguishable from a real one.
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
        static readonly (string Pkg, string Cls, string Prop)[] IntrinsicConfigStrings =
        {
            ("Core", "System", "PurgeCacheDays"), ("Core", "System", "SavePath"),
            ("Core", "System", "CachePath"), ("Core", "System", "CacheExt"),
        };

        public static string Execute(UEConfig config, string command, UEInput input = null,
            IViewportDevice viewport = null)
        {
            command = command ?? "";
            // UGameEngine::Exec SAVEGAME (Engine.dll.c:244086-244098): ParseCommand(SAVEGAME);
            // if the first char of the remainder is not '0'..'9' the command is ACCEPTED silently, with no save and no
            // output (return 1); otherwise appAtoi(remainder) — leading digits — and SaveGame(N) (vtbl +0xb8).
            if (ParseCommand(command, "SaveGame", out string saveTail))
            {
                if (saveTail.Length == 0 || saveTail[0] < '0' || saveTail[0] > '9') return "";
                if (SaveGameHandler is null)
                    throw new InvalidOperationException("ConsoleCommand(\"" + command + "\"): the host did not set SaveGameHandler");
                return SaveGameHandler(AppAtoi(saveTail)) ?? "";
            }
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
            bool isGet = ParseCommand(command, "GET", out string getTail);
            string[] getTok = isGet ? Tokens(getTail) : null;
            if (isGet && getTok.Length > 0 && getTok[0].StartsWith("ini:", StringComparison.OrdinalIgnoreCase))
            {
                if (config is null)
                    throw new InvalidOperationException(
                        "ConsoleCommand(\"" + command + "\") before config state is up");
                if (getTok.Length < 2)
                    throw new NotSupportedException("ConsoleCommand: " + command + " — no property (shipped: Unrecognized)");
                (string section, string property) = SplitIniRef(config, getTok[0].Substring(4) + " " + getTok[1]);
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
                // an invented answer is indistinguishable from a real one (§8).
                if (value is null)
                    throw new KeyNotFoundException(
                        "get ini: [" + section + "] " + property + " — the key is absent from the ini, and that is NORMAL: "
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
                string[] tok = getTok;
                if (tok.Length < 2)
                    throw new NotSupportedException("ConsoleCommand: " + command + " — no class or property (shipped: Unrecognized)");
                string classRef = tok[0], propName = tok[1];
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
                if (prop is null)
                    throw new NotSupportedException("ConsoleCommand: " + command + " — " + classRef + " has no property " + propName + " (shipped: Unrecognized property)");
                bool global = prop.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.GlobalConfig);
                if (!global && !prop.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Config))
                    throw new NotSupportedException("ConsoleCommand: " + command + " — property is not config; ExportText of class defaults is not ported");
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
            //   Brightness — frontend-linked: the OpenHP1 frame reads the value and
            //     applies the D3DDrv gamma ramp (Flush 0x10002f42, ScreenEffects);
            //     menu slider and screen brightness move together;
            //   TextureDetail — frontend-linked: the OpenHP1 frame reads the value
            //     and picks the first mip via UETextureLod (UTexture::Lock, Render skins);
            //   arbitrary `set ini:` of any class — NOT closed: the port has no typing,
            //     live instances or notification.
            bool isSet = ParseCommand(command, "SET", out string setTail);
            if (isSet && setTail.StartsWith("ini:", StringComparison.OrdinalIgnoreCase))
            {
                if (config is null)
                    throw new InvalidOperationException(
                        "ConsoleCommand(\"" + command + "\") before config state is up");
                string tail = setTail.Substring(4).TrimStart();
                int classEnd = tail.IndexOf(' ');
                if (classEnd <= 0) throw new InvalidDataException("bad set ini command: " + command);
                string classRef = tail.Substring(0, classEnd);
                string rest = tail.Substring(classEnd + 1).TrimStart();
                int propEnd = rest.IndexOf(' ');
                string property = propEnd < 0 ? rest : rest.Substring(0, propEnd);
                string value = propEnd < 0 ? "" : rest.Substring(propEnd + 1).TrimStart();
                string section = config.ResolveSection(UEConfig.ScopeSystem, classRef);
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
                input.SetBinding(key, bound);
                if (config is null)
                    throw new InvalidOperationException(
                        "SET Input without config state: the rebinding would not survive a restart");
                config.Set(UEConfig.ScopeUser, InputSection, key, bound);
                return "";
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
                return input is not null && input.TryGetBinding(wanted, out string bound)
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
            // NOT ORIGINAL (author decision): the device's screen modes in any window
            // mode, see IViewportDevice.DisplayModes; the menu filter
            // (FEOptionsPage.IsSupportedResolution) is replaced — UEPortScriptOverrides.
            if (ParseCommand(command, "GetRes", out _))
                return RequireViewport(viewport, "GetRes").DisplayModes;

            // ToggleFullscreen (UWindowsViewport::Exec, WinDrv.dll.c:5090 -> ToggleFullscreen :697): from a window —
            // TryRenderDevice(GameRenderDevice, -1, -1, -1, Fullscreen=1), size from the Fullscreen* keys (ResizeViewport
            // at -1, :15133); from fullscreen — EndFullscreen, window at Windowed*. A successful resize saves the key
            // pair of the new mode (:15366-15379) — the same step 4 as SetRes.
            if (ParseCommand(command, "ToggleFullscreen", out _))
            {
                if (config is null) throw new InvalidOperationException("ToggleFullscreen before config state is up");
                IViewportDevice vp = RequireViewport(viewport, "ToggleFullscreen");
                bool toFull = !vp.Fullscreen;
                (int x, int y) = UEViewportStartup.SavedSize(config, toFull);
                if (vp.Resize(x, y, 0, toFull) == ViewportResizeOutcome.Applied) CommitAppliedResize(config, vp);
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

            throw new NotSupportedException("ConsoleCommand: " + command);
        }

        static bool _snapNoted;

        // ── STEP 4 OF SHIPPED RESIZE: SAVE WHAT WAS APPLIED ────────────────────
        //
        // WinDrv.dll.c:15366-15379, verbatim and in the same order. Values are read
        // FROM THE DEVICE, not from command arguments: shipped writes the resize
        // result into the client, and the device may apply something other than
        // requested.
        // THE MODE PICKS THE KEY PAIR. In shipped it is the same branch as when
        // reading the size (:15133-15144): in fullscreen the applied values go to
        // the Fullscreen* keys, in a window to Windowed*.
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

        static bool ParseCommand(string command, string word, out string tail)
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

        // Parse `<ClassRef> <Property>` of the `get` form: the property name is the
        // LAST token, everything before it is the class reference. Section indirection
        // (`Engine.Engine.AudioDevice` -> `Galaxy.GalaxyAudioSubsystem`) is resolved by
        // config: in shipped data the same name MusicVolume lives in both
        // [Audio.GenericAudioSubsystem] and [Galaxy.GalaxyAudioSubsystem], and the
        // choice is made by that data, not by code.
        static (string Section, string Property) SplitIniRef(UEConfig config, string tail)
        {
            int split = tail.LastIndexOf(' ');
            if (split <= 0) throw new InvalidDataException("bad ini console command: " + tail);
            string classRef = tail.Substring(0, split);
            string property = tail.Substring(split + 1);
            if (classRef.LastIndexOf('.') <= 0)
                throw new InvalidDataException("bad ini class reference: " + classRef);
            return (config.ResolveSection(UEConfig.ScopeSystem, classRef), property);
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
            // guessed here — so the case is LOUD, not invented (§8).
            if (name is null)
                throw new InvalidOperationException(
                    "KEYNAME: EInputKey has no name for index " + key
                    + " — shipped falls back to HardcodedNames, and that behavior has not been read");
            return name.Length > 3 ? name.Substring(3) : "";
        }
    }
}
