// UGameEngine::LoadMap's play-world bootstrap, Core-owned.
//
// There is exactly one player-creation mechanism: the pawn is chosen by the shipped bytecode
// `Engine.GameInfo.Login`, and the order is the shipped LoadMap order. No frontend builds its own player.
//
// Source, line by line from the decompile (SurrealEngine is insufficient here):
//   UGameEngine::LoadMap body   Engine.dll.c:246351..247900
//                               (located via appUnwindf(L"UGameEngine::LoadMap") 248377;
//                                the LoadMap label at 97527 belongs to unrelated code)
//   GameInfo class choice       247249..247266
//   spawn GameInfo → Level.Game 247277  (assert Info->Game!=NULL, UnGame.cpp:0x469)
//   TimeSeconds = 0             247341
//   clear Touching[4]           247342..247351
//   InitExecution on all        247376..247381
//   bBegunPlay                  247383
//   GameInfo.InitGame           247417   ← BEFORE the first PreBeginPlay
//   PreBeginPlay/BeginPlay/
//   SetActorZone/PostBeginPlay/
//   SetInitialState             247428..247464
//   AttachTag→SetBase, FindBase 247466..247500, 247873..247884
//   ULevel::SpawnPlayActor      51436..51700
//   APlayerPawn::SetPlayer      111909..111935 (writes Player, calls Possess)
using System;
using System.Collections.Generic;
using HP1.Core;
using UELib.Core;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        // Values that shipped takes from ini/URL. The FRONTEND reads them (it owns the file system); Core only
        // executes, otherwise Core would have to know where the game is installed.
        public sealed class PlayWorldConfig
        {
            // URL after '?': shipped passes this same string to both InitGame and Login.
            public string Options = "";
            public string Portal = "";
            // ini Engine.Engine.DefaultGame (HP1: Engine.GameInfo) — the last
            // step of the choice after ?Game= and LevelInfo.DefaultGameType (247249..247266).
            public string DefaultGameClass;
            // Shipped SpawnClass chain (51536..51560): ?Class= →
            // "usr:DefaultPlayer.Class" → "ini:URL.Class". In this install
            // the first is empty, the second is [URL] Class=Engine.Playerpawn.
            public string DefaultPlayerClass;
            public string UrlPlayerClass;
            // ini Engine.Engine.Console — the class attached to the viewport.
            public string ConsoleClass;

            // Travel actors carried over from the previous level. In shipped this is a
            // LoadMap argument (`TMap<FString,FString>* TravelInfo`, 247074:
            // `GLevel->TravelInfo = *param_4`), so here it also arrives TOGETHER with the
            // rest of the bootstrap input, not through a separate setter: a world raised
            // without it is a different world.
            public Dictionary<string, List<TravelActor>> TravelInfo;
        }

        // Why Login returns harry0 and not an editor camera: Tut1 carries seven placed
        // Engine.Camera (Camera35..41, Tag="TextureBrowser", bHiddenEd) — UnrealEd viewport
        // cameras saved in the map. They are flagged RF_NotForClient|RF_NotForServer (flags
        // 0x02340000 vs 0x02070001 on gameplay actors), and `ULinkerLoad::CreateExport`
        // (Core.dll.c:6111-6113, UnLinker.cpp:0x289) creates an export ONLY when
        // `ObjectFlags & LinkerContextFlags != 0`, where the running game's context is
        // RF_LoadForClient|RF_LoadForServer (Core.dll.c:111046-111052). So these cameras do not
        // exist in the game world at all: the linker never creates them. UEActorGraphLoader
        // walks ULevel::Actors and filters by the linker context, so Login runs as is and on a
        // bare Tut1 load returns the only bIsPlayer pawn, harry0.

        // Both install ini files, parsed by ONE piece of code for every frontend: the startup world
        // config and the mutable config state (UEConfig) both take their files from here, otherwise
        // `get ini:` and `set ini:` would drift to different sources. The caller supplies paths (it owns
        // the file system); the meaning of the keys is shared.
        // Shipped appInit (Core.dll.c:56252-56271): the system ini is `%s%s.ini` (HP.ini), falling back to a
        // copy of `Default.ini` from the System directory. The port never writes its own HP.ini (UEConfig
        // overlay), so the base is `System/Default.ini`, as in an installed game. The US copy in the repository
        // is the unpacked distribution: language files live in `System/0/`, and so does `Default.ini`.
        public static string DefaultIniPath(string systemDir)
        {
            string installed = System.IO.Path.Combine(systemDir, "Default.ini");
            if (UEInstallPath.File(installed) is { } i) return i;
            string extracted = System.IO.Path.Combine(systemDir, "0", "Default.ini");
            if (UEInstallPath.File(extracted) is { } e) return e;
            throw new System.IO.FileNotFoundException(
                $"appInit: no Default.ini in either '{installed}' or '{extracted}' (MisingIni shipped)");
        }

        // ── STARTUP SPLASH (HP.exe WinMain, decrypted image, 2026-10-15) ──────
        //
        // 0x1090a7da: Language = GConfig->GetStr("Engine.Engine", "Language"); 0x1090a8f8: path =
        // "..\Help\Splash%s.bmp" with that Language; if FileSize < 0 (0x1090aa4a) -> "..\Help" * "splashint.bmp"
        // (0x1090ab33), and again (0x1090ad1f) -> the literal "..\Help\splashint.bmp" (the same file). InitSplash
        // (0x10902960) calls LoadFileToBitmap and check(Bitmap) (0x109029e8) — a missing bitmap is FATAL.
        // The unpacked distribution keeps the language files in Help/0 (like System/0/Default.ini); that layout is
        // searched after the installed one. Splash0..4.bmp are never referenced by HP.exe.
        public static string SplashBitmapPath(string root, UEConfig config)
        {
            string language = config?.Get(UEConfig.ScopeSystem, "Engine.Engine", "Language") ?? "";
            string help = System.IO.Path.Combine(root, "Help");
            var dirs = new List<string> { help };
            string defaultIni = DefaultIniPath(System.IO.Path.Combine(root, "System"));
            string langDir = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(defaultIni));
            if (langDir != "System") dirs.Add(System.IO.Path.Combine(help, langDir));   // unpacked layout: Help/0
            foreach (string dir in dirs)
                foreach (string name in new[] { "Splash" + language + ".bmp", "splashint.bmp" })
                {
                    // Shipped files are lowercase (Help/0/splashint.bmp): the lookup ignores case, like Windows.
                    if (UEInstallPath.File(System.IO.Path.Combine(dir, name)) is { } path) return path;
                }
            throw new System.IO.FileNotFoundException(
                $"InitSplash: check(Bitmap) — neither Help/Splash{language}.bmp nor Help/splashint.bmp exists under '{root}' "
                + "(fatal in shipped, HP.exe 0x109029e8)");
        }

        // WinMain 0x1090ae1c..0x1090aea5: the splash is shown unless the command line has -LOG or -server
        // (ParseParam) or contains TestRenDev (appStrfind, case-insensitive substring).
        public static bool ShowsSplash(string commandLine)
            => !UEViewportStartup.ParseParam(commandLine, "LOG")
            && !UEViewportStartup.ParseParam(commandLine, "server")
            && (commandLine ?? "").IndexOf("TestRenDev", StringComparison.OrdinalIgnoreCase) < 0;

        // Inverse of DefaultIniPath: the System directory from a Default.ini path (both layouts).
        public static string SystemDirOfDefaultIni(string defaultIni)
        {
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(defaultIni));
            return string.Equals(System.IO.Path.GetFileName(dir), "0", StringComparison.Ordinal)
                ? System.IO.Path.GetDirectoryName(dir) : dir;
        }

        public static (UEIniFile System, UEIniFile User) InstallInis(string systemDir)
        {
            string defaultIni = DefaultIniPath(systemDir);
            string userIni = System.IO.Path.Combine(systemDir, "DefUser.ini");
            return (new UEIniFile(defaultIni),
                    System.IO.File.Exists(userIni) ? new UEIniFile(userIni) : null);
        }

        // ── RENDER DEVICE VALUE FOR THE bHighDetail GATE ────────────────────
        //
        // Shipped reads `RenDev->HighDetailActors` (Render.dll.c:10843, a field
        // resolved by the native class registration Engine.dll.c:153242-153248 —
        // UBoolProperty "HighDetailActors", offset 100). The device is named by the
        // ini key Engine.Engine.GameRenderDevice, and the value lives in that device's
        // section. Selection details are in the method body.
        public const string RenderDeviceRef = "Engine.Engine.GameRenderDevice";

        public static bool ReadHighDetailActors(UEConfig config, out string note)
            => ReadRenderDeviceFlag(config, "HighDetailActors", "high_detail_actors", out note);

        // RenDev->Coronas (+0x60, UBoolProperty "Coronas" of URenderDevice) — the DrawFrame corona gate (UECoronas);
        // read exactly like HighDetailActors.
        public static bool ReadRenderDeviceCoronas(UEConfig config, out string note)
            => ReadRenderDeviceFlag(config, "Coronas", "render_device_coronas", out note);

        // RenDev->VolumetricLighting (+0x58) — OccludeBsp's LeafVolumetricLighting gate (UEOcclusion.LeafVolumetricLighting).
        public static bool ReadRenderDeviceVolumetricLighting(UEConfig config, out string note)
            => ReadRenderDeviceFlag(config, "VolumetricLighting", "render_device_volumetric", out note);

        static bool ReadRenderDeviceFlag(UEConfig config, string key, string noteTag, out string note)
        {
            // ── THE VALUE IS RESOLVED IN THE SAME ORDER AS GConfig ────────────
            //
            // Shipped asks for the device with an ordinary GConfig query:
            // WinDrv.dll.c:6344 calls vtable slot +0x14 (GetString) with
            // ("Engine.Engine", "GameRenderDevice", filename=0). A null file name is the
            // ORDINARY config lookup, not a requirement that the key live in the user ini,
            // and it addresses the SYSTEM scope, not `usr:`.
            //
            // DefUser.ini holds user BINDINGS and need not name the device; the shipped data
            // names it unambiguously: System/0/Default.ini:20 = SoftDrv.SoftwareRenderDevice,
            // and that section (line 221) has HighDetailActors=False.
            //
            // Read THROUGH UEConfig, with the same three layers as shipped: the saved overlay
            // (`set ini:` may change the device), then the shipped ini, then the native class
            // default. Raw UEIniFile reads would miss the overlay and become a second reader.
            //
            // Section indirection is the same as for AudioDevice: the key's value IS the
            // device section name.
            string device = config?.ResolveSection(UEConfig.ScopeSystem, RenderDeviceRef);
            if (string.IsNullOrEmpty(device) || device == RenderDeviceRef)
                throw new System.IO.InvalidDataException(
                    "the configuration names GameRenderDevice neither in the overlay nor "
                    + "in the shipped data — there is nothing to decide the bHighDetail gate by.");
            string raw = config.Get(UEConfig.ScopeSystem, device, key);
            if (string.IsNullOrEmpty(raw))
                throw new System.IO.InvalidDataException(
                    $"the device section '{device}' has no key {key} — "
                    + "the device gate cannot be decided without it.");
            bool value = string.Equals(raw.Trim(), "True", StringComparison.OrdinalIgnoreCase);
            note = noteTag + " device=" + device + " " + key + "=" + value;
            return value;
        }

        // The value lives on the world because the frame carries it (WorldFrame).
        //
        // BEFORE the install is read there is no device at all — a bare fixture runtime has
        // no ini, viewport or RenDev, and does NOT apply the gate; that is a test-bench state,
        // not a claim about the game's configuration. Every frontend reads the install with
        // exactly one call (LoadInputBindingsFromInstall), so on the LIVE path the value is
        // always resolved from data or fails loudly.
        public bool HighDetailActors { get; private set; } = true;
        public bool RenderDeviceCoronas { get; private set; } = true;
        public bool RenderDeviceVolumetricLighting { get; private set; } = true;

        void ReadRenderDeviceFlags()
        {
            HighDetailActors = ReadHighDetailActors(Config, out string hdNote);
            Log(hdNote);
            RenderDeviceCoronas = ReadRenderDeviceCoronas(Config, out _);
            RenderDeviceVolumetricLighting = ReadRenderDeviceVolumetricLighting(Config, out _);
        }

        public static PlayWorldConfig ConfigFromInstall(string systemDir)
        {
            (UEIniFile ini, UEIniFile user) = InstallInis(systemDir);
            // UGameEngine::Init (Engine.dll.c:38246-38247): DefaultURL
            // .LoadURLConfig("DefaultPlayer", "User") — every line of the section
            // becomes an option (AddOption). The startup URL is FURL(&DefaultURL,
            // map, TRAVEL_Partial) (38303): base options are copied; the command-line
            // map has no options of its own in the port. Hence in the shipped data
            // "?Name=Player?Class=Harrypotter.harry?team=255" — seen by
            // InitGame and Login (247220-247235, 51560-51575).
            var defaultUrl = new UEUrl();
            if (user is not null)
                foreach (KeyValuePair<string, string> kv in user.Section("DefaultPlayer"))
                    defaultUrl.AddOption(kv.Key + "=" + kv.Value);
            return new PlayWorldConfig
            {
                Options = defaultUrl.OptionsString,
                Portal = "",
                DefaultGameClass = ini.Get("Engine.Engine", "DefaultGame"),
                DefaultPlayerClass = user?.Get("DefaultPlayer", "Class"),   // usr:DefaultPlayer.Class (51548)
                UrlPlayerClass = ini.Get("URL", "Class"),                   // ini:URL.Class (51556)
                ConsoleClass = ini.Get("Engine.Engine", "Console"),
            };
        }

        // Key bindings come from the install's REAL DefUser.ini, parsed once for every frontend.
        // The method also raises the mutable config state: it is the ONLY "read the install" entry
        // point; a second one just for UEConfig is one a frontend could forget to call.
        public void LoadInputBindingsFromInstall(string systemDir)
        {
            string userIni = System.IO.Path.Combine(systemDir, "DefUser.ini");
            if (!System.IO.File.Exists(userIni))
                throw new System.IO.FileNotFoundException(
                    "DefUser.ini missing: input bindings have no source", userIni);
            // PROCESS config (GConfig), not one per world load: in shipped appInit
            // creates it once and every level uses it.
            Config = AppInit(systemDir);
            ReadRenderDeviceFlags();
            // Bindings are read THROUGH the config state, otherwise a saved rebinding
            // (`SET Input`) would not reach the game although it is on disk. Until a store
            // is bound the overlay is empty and the answer is exactly the shipped DefUser.ini.
            Input.LoadBindings(Config.Section(UEConfig.ScopeUser, UEConsoleCommand.InputSection));
        }

        // ── appInit: PROCESS GConfig AND LANGUAGE ───────────────────────────
        //
        // Shipped appInit (Core.dll.c:56270-56321): raises GConfig from the
        // System and User ini, then
        //   GConfig->GetString("Engine.Engine", "Language", buf, 0x100, 0)
        //   and on a hit UObject::SetLanguage(buf).
        // Only AFTER that are packages loaded: UClass::Serialize (Core.dll.c:4240)
        // calls LoadConfig on every class's defaults, so the config must exist before
        // the first LoadDefaults. Frontends therefore call THIS before loading packages;
        // a repeat call (next level load) returns the same object.
        //
        // `store` is where the overlay is written (platform boundary: a writable
        // directory or memory). Binding loads the saved overlay BEFORE LoadConfig of
        // the defaults reads it, so what SaveConfig stored survives a restart.
        public static UEConfig AppInit(string systemDir, UEConfig.IStore store = null)
        {
            if (string.IsNullOrEmpty(systemDir)) throw new ArgumentNullException(nameof(systemDir));
            string full = System.IO.Path.GetFullPath(systemDir);
            UEConfig g = UEConfig.Global;
            if (g is not null && !string.Equals(g.InstallSystemDir, full, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"appInit: process GConfig is already up from '{g.InstallSystemDir}', "
                    + $"a second install '{full}' in one process does not happen");
            if (g is null)
            {
                (UEIniFile sys, UEIniFile user) = InstallInis(systemDir);
                g = new UEConfig(sys, user) { InstallSystemDir = full };
                if (store is not null) g.BindStore(store);
                UEConfig.Global = g;
                UEEdition.NativeOrder = UEEdition.OfSystem(full).Data;
                UELocalization.SystemDir = systemDir;
                if (g.TryGetIni(UEConfig.ScopeSystem, "Engine.Engine", "Language", out string lang))
                    UELocalization.SetLanguage(lang);
            }
            else if (store is not null) g.BindStore(store);
            return g;
        }

        // Fixture seam (InternalsVisibleTo): a "process restart" is a NEW GConfig. A fixture
        // modelling a restart removes the current one, builds its own and RESTORES the previous
        // one afterwards: the process config is state shared by the whole run.
        internal static UEConfig SwapGlobalConfigForTest(UEConfig next)
        {
            UEConfig prev = UEConfig.Global;
            UEConfig.Global = next;
            return prev;
        }

        // MUTABLE SETTINGS STORE + BINDINGS RELOAD.
        //
        // The order matters and ONE piece of code owns it: `BindStore` loads the saved
        // overlay, and only AFTER that do the bindings know about rebinds. A frontend that
        // forgot the second step would silently play with shipped keys while saved ones sit on disk.
        public void BindConfigStore(UEConfig.IStore store)
        {
            if (Config is null)
                throw new InvalidOperationException(
                    "BindConfigStore before LoadInputBindingsFromInstall: no config state yet");
            Config.BindStore(store);
            Input.LoadBindings(Config.Section(UEConfig.ScopeUser, UEConsoleCommand.InputSection));
            // The device value is THE SAME CASE AS THE BINDINGS, and sits here for the same
            // reason: a saved `set ini:` (which changed the device
            // or the key itself) is raised into the overlay ONLY now, while it was read
            // earlier — when reading the install. Without the re-read the game would run
            // with the shipped value while a different one is saved on disk, and silently.
            ReadRenderDeviceFlags();
        }

        // ── FIRST RUN: HARDWARE DEVICE ──────────────────────────────────────
        //
        // System/0/Default.ini is the shipped TEMPLATE with SoftDrv; on first run the original
        // detects the video card and writes a hardware device into the working config — for
        // players that is D3D. Running on the template would show the software-renderer world:
        // no shadows (bHighDetail, HighDetailActors=False for SoftDrv) and no decals. If no
        // device is chosen in the saved settings, D3DDrv is selected and saved, like the
        // original's first run; afterwards the menu edits THAT section.
        public const string HardwareRenderDevice = "D3DDrv.D3DRenderDevice";
        public static bool SelectHardwareRenderDeviceIfUnset(UEConfig config)
        {
            if (config.HasOverlay(UEConfig.ScopeSystem, "Engine.Engine", "GameRenderDevice")) return false;
            config.Set(UEConfig.ScopeSystem, "Engine.Engine", "GameRenderDevice", HardwareRenderDevice);
            return true;
        }

        public bool SelectHardwareRenderDeviceIfUnset()
        {
            if (Config is null) throw new InvalidOperationException("device selection before the install is read");
            if (!SelectHardwareRenderDeviceIfUnset(Config)) return false;
            HighDetailActors = ReadHighDetailActors(Config, out string hdNote);
            RenderDeviceCoronas = ReadRenderDeviceCoronas(Config, out _);
            RenderDeviceVolumetricLighting = ReadRenderDeviceVolumetricLighting(Config, out _);
            if (FrameProducer is not null)
            {
                FrameProducer.HighDetailActors = HighDetailActors; FrameProducer.RenderDeviceCoronas = RenderDeviceCoronas;
                FrameProducer.RenderDeviceVolumetricLighting = RenderDeviceVolumetricLighting;
            }
            Log("render_device_first_run " + HardwareRenderDevice + " " + hdNote);
            return true;
        }

        // Viewport (UPlayer) and its console. In shipped UClient owns them; for the port only one
        // thing matters: the local player HAS a Player and there is exactly one. Kept here so every
        // frontend sees the same object rather than its own.
        public UEObject Viewport { get; private set; }
        public UEObject ViewportConsole { get; private set; }

        // Single source of truth for what the local player controls.
        // Equals what Login returned, not what a frontend chose.
        public UEObject PlayerPawn { get; private set; }

        // The whole play-world bootstrap in ONE call, in shipped order — the single Core local-player
        // session mechanism; frontends call THIS rather than assembling their own sequence.
        // By now the world is loaded (LoadWorld), the clock is zeroed
        // (InitializeLevelClock = TimeSeconds=0, 247341) and the hash is built.
        public UEObject BootstrapPlayWorld(UEInterpreter interp, PlayWorldConfig cfg)
        {
            // Per-level frame state is cleared on LOAD, not only on teardown: a world is not
            // guaranteed to be torn down (a frame producer can survive into a new session), and stale
            // state such as _ambientPlaying would then make Core send only AmbientUpdate and never
            // AmbientStart, so ambient loops would never start.
            FrameProducer?.ResetForNewLevel();
            ArmFirstWindowFrame();             // see UELevelRuntime.EngineFrameOwedAfterLoad
            ArmLoadStutter();                  // see UELevelRuntime.EmulateLoadStutter
            WriteLoadMapComputerName(interp);           // 246880..246917
            AdoptLoadedUrl(cfg);
            BuildLoadMapZoneDist();                     // 247117..247188
            SpawnGameInfoThenHighDetailMode(interp, cfg, withViewport: true);
            ClearTouching(interp);                      // 247342..247351
            RunInitExecutionPass(interp);               // 247376..247381 — BEFORE InitGame
            SetBegunPlay(interp);                       // 247382
            RunGameInfoInitGame(interp, cfg);           // 247417 — BEFORE PreBeginPlay
            RunLoadedLevelLifecycle(interp);            // 247428..247464
            AttachTagAndBasePass(interp);               // 247466..247500 + 247873..247884
            ClearStartup(interp);                       // 247503
            SortActorsForPlay();                        // 247517..247590
            UEObject player = SpawnPlayActorThenBrushTracker(interp, cfg);
            DetailChange(interp);                       // 0x1039e628..0x1039e644
            WarmFirstUseCaches();                       // port: data caches only (UELevelRuntime.LoadWarm.cs)
            return player;
        }

        // InitGameInfo, then bHighDetailMode — LoadMap order per disassembly:
        // SpawnActor(GameInfo) at 0x1039da73 (`call [edx+0xa0]`, result into
        // Info+0x490 = Game), then the Listen branch, and only at 0x1039db7e..
        // 0x1039dbb8 — `Info+0x438 |= 8`, with the bit cleared when
        // Viewports(0)->RenDev->HighDetailActors == 0. GameInfo's Spawned/PreBeginPlay/
        // BeginPlay/PostBeginPlay see bHighDetailMode from the map.
        void SpawnGameInfoThenHighDetailMode(UEInterpreter interp, PlayWorldConfig cfg, bool withViewport)
        {
            SpawnGameInfo(interp, cfg);                 // InitGameInfo (247249..247281)
            SetHighDetailMode(interp, withViewport);    // 0x1039db7e..0x1039dbb8
        }

        // LoadMap's `if (Client)` block (disasm 0x1039e541..0x1039e5ab): FIRST
        // MatchViewportsToActors -> SpawnPlayActor (0x1039e56c), THEN, if the level
        // package is not "Entry" (appStricmp 0x1039e58f), Level->BrushTracker =
        // GNewBrushTracker (0x1039e5a0, GLevel+0xfc written at 0x1039e5ab).
        // A mover moved during Login/PostLogin gets SavedPos/SavedRot
        // from its moved position.
        UEObject SpawnPlayActorThenBrushTracker(UEInterpreter interp, PlayWorldConfig cfg)
        {
            UEObject player = SpawnPlayActor(interp, cfg); // ClientInit (51436..51700)
            CreateBrushTrackerUnlessEntry();               // 0x1039e589..0x1039e5ab
            // Audio->SetViewport(Audio->GetViewport()) (0x1039e5b1-0x1039e5c6): non-music channels started by the startup
            // events and Login are stopped.
            FrameProducer?.Audio.SetViewportSame();
            Input.ResetInputFull(interp, ViewportConsole, player);   // 247644..247651: Viewport->Input->ResetInput()
            return player;
        }

        // ── LevelInfo.ComputerName <- appComputerName() (LoadMap 246880..246917) ──
        // appComputerName (Core.dll.c, export ?appComputerName@@YAPBGXZ) fills a static buffer
        // (DAT_1024ebc8) once per process with the GetComputerNameW machine name (buffer 0x100),
        // keeping ONLY [a-z], [A-Z], [0-9] (0x61..0x7a, 0x41..0x5a, 0x30..0x39); later calls return
        // the same buffer. LoadMap copies the string into Info+0x468 (FString ComputerName) — the
        // shipped pointer comparison always says "different".
        // The machine name is a platform boundary (like LocalTimeSource): the host
        // sets the source before the first level load in the process.
        public static Func<string> ComputerNameSource = () => Environment.MachineName;
        static string s_appComputerName;

        internal static string AppComputerName()
        {
            if (s_appComputerName is not null) return s_appComputerName;
            string raw = ComputerNameSource() ?? "";
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw)
                if ((c > 0x60 && c < 0x7b) || (c > 0x40 && c < 0x5b) || (c > 0x2f && c < 0x3a))
                    sb.Append(c);
            return s_appComputerName = sb.ToString();
        }

        void WriteLoadMapComputerName(UEInterpreter interp)
        {
            UEObject level = interp.GetLevelInfo();
            if (level is null) return;
            level.Props["ComputerName"] = AppComputerName();
        }

        // ── ULevel.ZoneDist[64][64] (+0x11c) — LoadMap 247117..247188, disasm
        // 0x1039d6d4..0x1039d853 ──────────────────────────────────────────────
        //   dist[i][j] = (i == j) ? 0 : 0xff;  conn[i] = Model.Zones[i].Connectivity
        //   for n = 1..63:
        //     dist[i][j] = n if dist[i][j] > n and conn[i] & (1 << j);
        //     next[j] = OR of conn[k] over all k with conn[j] & (1 << k);  conn = next
        // The shift is __allshl (0x10459b70) of 1 by j/k: a 64-bit mask. This is NOT an
        // edge count: at step n conn is reachability within 2^(n-1) edges (conn∘conn),
        // and dist is written from it, as in shipped. Row = i (source zone),
        // column = j: index i*64 + j, like `i<<6 + j` at 0x1039d75d.
        // No readers in the port (shipped readers 0x1034d12c, 0x1034da58 are not
        // identified; Galaxy — needs disasm): the write is ported, not a consumer.
        public byte[] ZoneDist { get; private set; }

        public static byte[] BuildZoneDist(ulong[] zoneConnectivity)
        {
            if (zoneConnectivity is null) throw new ArgumentNullException(nameof(zoneConnectivity));
            if (zoneConnectivity.Length > 64)
                throw new System.IO.InvalidDataException(
                    $"ZoneDist: {zoneConnectivity.Length} zones > 64 (UModel.Zones is a 64-entry array)");
            // Zones beyond NumZones live in the same UModel.Zones[64] array in shipped, but the
            // package does not write them. They do not affect the result as long as real zones'
            // connectivity does not reference them (the caller checks via
            // ZoneConnectivityReferencesUnloadedZone); here they are zero.
            int numZones = zoneConnectivity.Length;
            var conn = new ulong[64];
            Array.Copy(zoneConnectivity, conn, numZones);
            var dist = new byte[64 * 64];
            for (int i = 0; i < 64; i++)
                for (int j = 0; j < 64; j++)
                    dist[i * 64 + j] = i == j ? (byte)0 : (byte)0xff;
            var next = new ulong[64];
            for (int n = 1; n < 64; n++)
            {
                for (int i = 0; i < 64; i++)
                    for (int j = 0; j < 64; j++)
                        if (dist[i * 64 + j] > n && (conn[i] & (1UL << j)) != 0)
                            dist[i * 64 + j] = (byte)n;
                for (int j = 0; j < 64; j++)
                {
                    next[j] = 0;
                    for (int k = 0; k < 64; k++)
                        if ((conn[j] & (1UL << k)) != 0) next[j] |= conn[k];
                }
                Array.Copy(next, conn, 64);
            }
            return dist;
        }

        void BuildLoadMapZoneDist()
        {
            ulong[] connectivity = Bsp?.ParseTables?.ZoneConnectivity;
            if (connectivity is null)
            {
                // No model (synthetic fixture world) — nothing to compute from.
                ZoneDist = null;
                Log("zone_dist model=none");
                return;
            }
            int bad = ZoneConnectivityReferencesUnloadedZone(connectivity);
            if (bad >= 0)
            {
                // The result would depend on unloaded Zones[] entries — no value is
                // substituted: ZoneDist is not built, loudly.
                ZoneDist = null;
                string msg = $"zone_dist STOP: Zones[{bad}].Connectivity=0x{connectivity[bad]:x16} "
                    + $"references a zone >= NumZones ({connectivity.Length})";
                Log(msg);
                HP1.Core.UETrace.Warn("[Core] " + msg);
                return;
            }
            ZoneDist = BuildZoneDist(connectivity);
        }

        // -1: connectivity of every loaded zone references only loaded zones.
        public static int ZoneConnectivityReferencesUnloadedZone(ulong[] zoneConnectivity)
        {
            int numZones = zoneConnectivity.Length;
            if (numZones >= 64) return -1;
            for (int i = 0; i < numZones; i++)
                if ((zoneConnectivity[i] >> numZones) != 0) return i;
            return -1;
        }

        // The same LoadMap WITHOUT a player: the Entry world is loaded in UGameEngine::Init,
        // when the client has no viewports yet, so the SpawnPlayActor loop over them is empty
        // (Engine.dll.c:38221-38243; LoadMap 247650-247660).
        internal void BootstrapLevelWithoutPlayer(UEInterpreter interp, PlayWorldConfig cfg)
        {
            FrameProducer?.ResetForNewLevel();
            WriteLoadMapComputerName(interp);
            BuildLoadMapZoneDist();
            SpawnGameInfoThenHighDetailMode(interp, cfg, withViewport: false);
            ClearTouching(interp);
            RunInitExecutionPass(interp);
            SetBegunPlay(interp);
            RunGameInfoInitGame(interp, cfg);
            RunLoadedLevelLifecycle(interp);
            AttachTagAndBasePass(interp);
            ClearStartup(interp);
            SortActorsForPlay();
            DetailChange(interp);
        }

        // ── Actors REORDER AFTER THE BeginPlay PHASE (Engine.dll.c:247517..247590) ──
        //
        // Shipped LoadMap rebuilds the array: Actors[0] (LevelInfo) and Actors[1] stay in place,
        // then STATIC actors (flag +0x28 bit 0x1, bStatic) without bAlwaysRelevant (0x100000) —
        // their count goes to ULevel+0x114, then static WITH bAlwaysRelevant — count into
        // ULevel+0x110 (iFirstDynamicActor, where ULevel::Tick 262665 and ULevel::CompactActors
        // 55713 start), then all NON-static ones; empty slots drop out. Result: a static actor
        // does NOT TICK at all — no script, latent code or physics (e.g. the static pawn
        // MirrorOuterCylObj0 on Lev5_Final: bStatic, !bMovable, !bCollideWorld, is never patrolled).
        public int FirstNetRelevantActor { get; private set; }
        public int FirstDynamicActor { get; private set; }

        // Whether an actor was loaded into the map's ULevel::Actors(1) slot (UEActorGraphLoader).
        // If the slot is empty, the next actor shifts to the port's index 1 and must not be
        // pinned: it would drop out of the tick (e.g. harry0 on Lev2_Fire2/HogFront/Inc_B,
        // CutScene0, broom0, Mover35…).
        internal bool _levelSlotOneLoaded = true;

        internal void SortActorsForPlay()
        {
            UEObject[] src = Actors.ToArray();
            var dst = new List<UEObject>(src.Length);
            int pinned = Math.Min(src.Length, _levelSlotOneLoaded ? 2 : 1);
            for (int i = 0; i < pinned; i++) dst.Add(src[i]);
            static bool Static(UEObject a) => UEInterpreter.ToBool(a.GetProp(PK.bStatic));
            static bool AlwaysRelevant(UEObject a) => UEInterpreter.ToBool(a.GetProp(PK.bAlwaysRelevant));
            for (int i = pinned; i < src.Length; i++)
                if (src[i] is not null && Static(src[i]) && !AlwaysRelevant(src[i])) dst.Add(src[i]);
            FirstNetRelevantActor = dst.Count;
            for (int i = pinned; i < src.Length; i++)
                if (src[i] is not null && Static(src[i]) && AlwaysRelevant(src[i])) dst.Add(src[i]);
            FirstDynamicActor = dst.Count;
            for (int i = pinned; i < src.Length; i++)
                if (src[i] is not null && !Static(src[i])) dst.Add(src[i]);
            Actors.Clear();
            Actors.AddRange(dst);
            Log($"phase sort_actors actors={Actors.Count} slot1={(_levelSlotOneLoaded && Actors.Count > 1 ? Actors[1].GetProp(PK.Name) : "empty")} firstNetRelevant={FirstNetRelevantActor} firstDynamic={FirstDynamicActor}");
        }

        // Last pass of the BeginPlay phase (247466..247500, tail 247873..247884).
        // Two branches per actor:
        //   AttachTag != None  → find the actor whose Tag equals this AttachTag and
        //                        base onto it (SetBase);
        //   AttachTag == None  → if there is no base, the actor is bCollideWorld and it is
        //                        Decoration/Inventory/Pawn — with Physics None(0) or
        //                        Rotating(5) probe the floor with FindBase,
        //                        and if the base turned out to be LevelInfo itself, clear it.
        // Same predicate as at spawn (20169-20172, 20292-20306), so exactly that one is
        // used rather than rewritten.
        void AttachTagAndBasePass(UEInterpreter interp)
        {
            UEObject level = interp.GetLevelInfo();
            UEObject[] actors = Actors.ToArray();
            int attached = 0, probed = 0;
            foreach (UEObject actor in actors)
            {
                if (actor is null || actor.Destroyed) continue;
                int tagged = AttachByTag(interp, actor, actors);
                if (tagged >= 0) { attached += tagged; continue; }
                if (actor.GetProp(PK.Base) is not null) continue;
                if (!UEInterpreter.ToBool(actor.GetProp(PK.bCollideWorld))) continue;
                if (!UEInterpreter.ClassIsA(actor.Class, "Decoration")
                    && !UEInterpreter.ClassIsA(actor.Class, "Inventory")
                    && !UEInterpreter.ClassIsA(actor.Class, "Pawn")) continue;
                int physics = UEInterpreter.ToInt(actor.GetProp(PK.Physics));
                if (physics != 0 && physics != 5) continue;
                FindBase(interp, actor);
                probed++;
                if (ReferenceEquals(actor.GetProp(PK.Base), level))
                    SetBase(interp, actor, null, notify: false);
            }
            Log($"phase attach_base attached={attached} probed={probed}");
        }

        // AttachTag branch of the pass (0x1039dff1..0x1039e026): -1 — the actor has no
        // Actor.AttachTag (FindBase branch runs), otherwise the number of SetBase calls (0/1).
        // Reads Actor.AttachTag (+0xac), not the same-named subclass field: movers keep the map
        // tag in Mover.AttachTag (+0x2f8), and this pass does not touch them (live probe
        // tools/origprobe/hp1setbase.c on Lev2_HogFront: no call returning to 0x1039e025).
        internal int AttachByTag(UEInterpreter interp, UEObject actor, UEObject[] actors)
        {
            object attachTag = actor.GetDeclared("Actor", "AttachTag");
            string tagName = attachTag?.ToString();
            if (string.IsNullOrEmpty(tagName) || tagName.Equals("None", StringComparison.OrdinalIgnoreCase))
                return -1;
            foreach (UEObject other in actors)
            {
                if (other is null || other.Destroyed) continue;
                if (!string.Equals(other.GetProp(PK.Tag)?.ToString(), tagName,
                        StringComparison.OrdinalIgnoreCase)) continue;
                SetBase(interp, actor, other, notify: false);
                return 1;
            }
            return 0;
        }

        // 247342..247351 (Touching[4] on all actors). A no-op on a fresh load, but after
        // returning to a hub the level is raised from previously live state, and then it is not.
        void ClearTouching(UEInterpreter interp)
        {
            foreach (UEObject actor in Actors)
            {
                if (actor is null) continue;
                UEObject[] touching = actor.TouchList();
                for (int i = 0; i < UEObject.TouchingArraySize; i++) touching[i] = null;
            }
        }

        // 247383 — AFTER the InitExecution pass, not together with clearing Touching.
        // 247382: `LevelInfo+0x438 |= 0x42` — bBegunPlay (0x2) AND bStartup (0x40).
        void SetBegunPlay(UEInterpreter interp)
        {
            UEObject level = interp.GetLevelInfo();
            if (level is null) return;
            level.Props["bBegunPlay"] = true;
            level.Props["bStartup"] = true;
        }

        // 247503: bStartup is cleared after the Attach pass.
        void ClearStartup(UEInterpreter interp)
        {
            UEObject level = interp.GetLevelInfo();
            if (level is null) return;
            Log($"phase startup_end was={UEInterpreter.ToBool(level.GetProp(PK.bStartup))}");
            level.Props["bStartup"] = false;
        }

        // 247315..247322: bHighDetailMode = 1, then 0 if the client has a viewport with a
        // device whose HighDetailActors == 0. In UGameEngine::Init (Entry world) there are
        // no viewports yet — it stays 1.
        void SetHighDetailMode(UEInterpreter interp, bool withViewport)
        {
            UEObject level = interp.GetLevelInfo();
            if (level is null) return;
            level.Props["bHighDetailMode"] = !withViewport || HighDetailActors;
        }

        // ULevel::DetailChange (Engine.dll.c:89736-89760, vtable +0xd8), called by LoadMap
        // with the current bHighDetailMode (0x1039e628..0x1039e644): writes the bit and fires
        // the Game.DetailChange event (which destroys bHighDetail && !bGameRelevant actors
        // and does LinkToSkybox for zones).
        void DetailChange(UEInterpreter interp)
        {
            UEObject level = interp.GetLevelInfo();
            if (level?.GetProp(PK.Game) is not UEObject game) return;
            int before = 0;
            foreach (UEObject a in Actors) if (a is not null && !a.Destroyed) before++;
            UFunction fn = interp.FindEventFunction(game, "DetailChange");
            if (fn is not null) interp.Invoke(game, fn);
            int after = 0;
            foreach (UEObject a in Actors) if (a is not null && !a.Destroyed) after++;
            Log($"detail_change high={UEInterpreter.ToBool(level.GetProp(PK.bHighDetailMode))} "
                + $"game={game.Class?.Name} destroyed={before - after}");
        }

        // 247417: GameInfo.InitGame(Options, out Error) runs BEFORE the first
        // PreBeginPlay. It also spawns BaseMutator (`BaseMutator = Spawn(MutatorClass)`
        // in the shipped InitGame bytecode), so the mutator is a real actor in the world.
        void RunGameInfoInitGame(UEInterpreter interp, PlayWorldConfig cfg)
        {
            UEObject game = interp.GetLevelInfo()?.GetProp(PK.Game) as UEObject
                ?? throw new InvalidOperationException("InitGame before Level.Game exists");
            UFunction fn = interp.FindEventFunction(game, "InitGame")
                ?? throw new InvalidOperationException("GameInfo.InitGame missing");
            var args = new object[] { cfg.Options ?? "", "" };
            Log($"init_game options={cfg.Options}");
            interp.Invoke(game, fn, args);
            if (args[1] is string err && err.Length > 0)
                throw new InvalidOperationException($"GameInfo.InitGame failed: {err}");
        }

        // ── InitGameInfo (247249..247281) ────────────────────────────────────
        // Class: ?Game= → LevelInfo.DefaultGameType → ini DefaultGame. Then a
        // REAL SpawnActor (not "make an object and assign it"): GameInfo is an
        // actor; it gets Spawned/PreBeginPlay/BeginPlay/PostBeginPlay/
        // SetInitialState, a name from the class counter and a slot in the actor list.
        public UEObject SpawnGameInfo(UEInterpreter interp, PlayWorldConfig cfg)
        {
            if (interp is null) throw new ArgumentNullException(nameof(interp));
            if (cfg is null) throw new ArgumentNullException(nameof(cfg));
            UEObject level = interp.GetLevelInfo()
                ?? throw new InvalidOperationException("SpawnGameInfo: no LevelInfo");

            string requested = GameOption(cfg.Options)
                ?? level.GetProp(PK.DefaultGameType) switch
                {
                    UClass placed => placed.Name,
                    string name when name.Length > 0 => name,
                    _ => null
                }
                ?? cfg.DefaultGameClass;
            if (string.IsNullOrEmpty(requested))
                throw new System.IO.InvalidDataException(
                    "SpawnGameInfo: no game class — ?Game=, LevelInfo.DefaultGameType and "
                    + "ini Engine.Engine.DefaultGame are all empty");

            // "Engine.GameInfo" → "GameInfo": UE1 loads by package.class path;
            // this port's registry is indexed by the class FName.
            string leaf = requested.Substring(requested.LastIndexOf('.') + 1);
            UClass gameClass = UEClassRegistry.Shared.Find(leaf)
                ?? throw new System.IO.InvalidDataException($"SpawnGameInfo: game class not found: {requested}");

            Log($"init_game_info class={gameClass.Name} requested={requested}");
            // Shipped calls SpawnActor with ONLY the class (247277): no owner,
            // name or tag, location is the default zero. LevelInfo must not be
            // passed as spawner: it would become the owner, and in the original
            // GameInfo has no owner.
            UEObject game = SpawnActor(interp, null, gameClass, null, null, default, default);
            if (game is null)
                // Shipped: appFailAssert("Info->Game!=NULL") — fatal, not silent.
                throw new InvalidOperationException("SpawnGameInfo: GameInfo spawn failed (Info->Game==NULL)");
            level.Props["Game"] = game;
            Log($"init_game_info spawned={game.GetProp(PK.Name)}");
            return game;
        }

        // The viewport is a real Engine.Viewport (native UPlayer subclass; the stub class
        // is exported by Engine.u with script-visible fields). The class matters:
        // PlayerPawn.PreRender gates the lazy myHUD spawn on the cast `Viewport(Player) != none`.
        //
        // A separate method because fixtures build a controlled world WITHOUT the lifecycle:
        // they need the viewport and possession but not Login. So a fixture has no reason to
        // build ITS OWN viewport — it uses this one.
        public void CreateLocalViewport(UEInterpreter interp, PlayWorldConfig cfg)
        {
            // VIEWPORT AND CONSOLE OUTLIVE THE LEVEL: shipped creates them once in
            // UGameEngine::Init, and LoadMap/travel only reassigns Viewport.Actor. Recreating
            // them would lose HPConsole state (bLoadNewLevel → slot 99 autosave at level start,
            // Root/menuBook UWindow) and silence Harry's death
            // (baseHarry.stateDead → Player.Console.LoadSelectedSlot).
            if (ViewportConsole is not null && Viewport is not null)
            {
                if (!ReferenceEquals(ViewportConsole.Interp, interp)) ViewportConsole.Interp = interp;
                if (!ReferenceEquals(Viewport.Interp, interp)) Viewport.Interp = interp;
                Log("viewport_reuse console=" + ViewportConsole.Class?.Name);
                return;
            }
            UClass viewportClass = UEClassRegistry.Shared.Find("Viewport")
                ?? UEClassRegistry.Shared.FindImportedClass("Viewport")
                ?? throw new System.IO.InvalidDataException("Engine.Viewport class missing (no export, no import witness)");
            Viewport = new UEObject { Class = viewportClass, Interp = interp };
            Viewport.LoadDefaults();
            Viewport.Props["Name"] = new UEName("Viewport0");

            UClass consoleClass = string.IsNullOrEmpty(cfg.ConsoleClass) ? null
                : UEClassRegistry.Shared.Find(cfg.ConsoleClass.Substring(cfg.ConsoleClass.LastIndexOf('.') + 1));
            if (consoleClass is null)
                throw new System.IO.InvalidDataException($"CreateLocalViewport: console class not found: {cfg.ConsoleClass}");
            ViewportConsole = new UEObject { Class = consoleClass, Interp = interp };
            ViewportConsole.LoadDefaults();
            // The console is an object of package Transient named by MakeUniqueObjectName:
            // the live original prints "HPConsole Transient.HPConsole0" (HP.log,
            // ScriptWarning HPConsole.Tick). The counter is the same ClassUnique.
            ViewportConsole.OuterPath = "Transient";
            ViewportConsole.Props["Name"] = new UEName(MintObjectName(consoleClass.Name, () =>
            {
                int number = interp.ClassUnique.TryGetValue(consoleClass, out int old) ? old : 0;
                interp.ClassUnique[consoleClass] = number + 1;
                return number;
            }, interp.TransientObjectNames));
            ViewportConsole.Props["Viewport"] = Viewport;
            Viewport.Props["Console"] = ViewportConsole;
            // UConsole::_Init (Engine 0x1038b0b0): Viewport (+0x2c), TopLine = 63 (+0x110),
            // BorderSize = 1 (+0x720), InitExecution (the port has the frame since creation), then Logf to
            // the console: LocalizeGeneral("Engine"), ("Copyright"), L" ", L" " — each line through
            // UConsole::Serialize -> eventMessage(None, line, 0) (Engine.dll.c:12806).
            // The third and fourth lines are DAT_104d32ec = L" " (.data bytes 20 00 00 00): Core.int gives
            // Engine/Copyright empty, Message skips them, and the two spaces land in the log (TopLine 63 -> 1).
            ViewportConsole.Props["TopLine"] = 63;
            ViewportConsole.Props["BorderSize"] = 1;
            foreach (string line in new[] { UELocalization.Localize("General", "Engine", "Core"),
                                            UELocalization.Localize("General", "Copyright", "Core"), " ", " " })
                UELifecycle.CallEventArgs(interp, ViewportConsole, "Message", new object[] { null, line, new UEName("None") });

            // ── CONSOLE FONTS — VIA THE REAL SetupLanguage ──────────────────
            //
            // The HUD takes its font from the player's console:
            // baseHud.uc:279 `Canvas.Font = baseConsole(...Player.Console)
            // .LocalIconMessageFont` (also :307, :394 — Tiny/Med). ONLY
            // HPConsole.SetupLanguage (HPConsole.uc:634) fills these fields; without it
            // Canvas.Font is None and DrawText takes the shipped "No font" branch.
            //
            // WHY HERE, NOT WHERE SHIPPED CALLS IT. The original calls it from
            // RenderUWindow (HPConsole.uc:745) under `if(!bCreatedRoot)`, reached only in the
            // UWindow state (WindowConsole.uc:454) entered by LaunchUWindow. In the original that
            // happens on the startup map: the menu comes up before any gameplay, so fonts are set
            // by the first HUD frame. The port loads the level directly, without a startup map —
            // the MOMENT differs, not the values: the shipped code picks the branch by
            // GetLanguage(), and for "int" that is Huge/Big/Med/Small +
            // LocalIconMessageFont=LocalSmallFont. Here because this is the only place the port
            // creates the very console the HUD reads.
            //
            // THE BYTECODE IS CALLED, NOT A HAND COPY: the language branching, all FIVE fields and
            // SaveConfig stay shipped.
            UFunction setupLanguage = interp.FindEventFunction(ViewportConsole, "SetupLanguage")
                ?? throw new System.IO.InvalidDataException(
                    $"CreateLocalViewport: console {consoleClass.Name} has no SetupLanguage — "
                    + "HUD fonts would stay empty and DrawText would silently take the "
                    + "\"No font\" branch. The install prescribes HPMenu.HPConsole (Default.ini:25).");
            interp.Invoke(ViewportConsole, setupLanguage, System.Array.Empty<object>());
        }

        // APlayerPawn::SetPlayer (Engine.dll.c:111909..111935): unlink the previous pair,
        // link Player↔Actor, THEN Possess. Public for the same reason as CreateLocalViewport:
        // a fixture needs the REAL shipped possession semantics, not an imitation; it picks
        // the pawn itself — that is fixture input, not a game mechanism.
        public void SetPlayer(UEInterpreter interp, UEObject pawn)
        {
            if (Viewport is null) throw new InvalidOperationException("SetPlayer before CreateLocalViewport");
            if (Viewport.GetProp(PK.Actor) is UEObject previous) previous.Props["Player"] = null;
            pawn.Props["Player"] = Viewport;
            Viewport.Props["Actor"] = pawn;
            UFunction possess = interp.FindEventFunction(pawn, "Possess");
            if (possess is not null) interp.Invoke(pawn, possess);
            PlayerPawn = pawn;
            LocalPlayer = pawn;
        }

        // ── ULevel::SpawnPlayActor (51436..51700), ClientInit phase ─────────
        // This code does NOT choose the pawn: `Engine.GameInfo.Login` (1230 bytes of
        // shipped bytecode) first searches Level.PawnList for a ready PlayerPawn with
        // Player==none, a live PlayerReplicationInfo, bIsPlayer and a still-default PRI
        // name, and reuses it; otherwise it spawns a new one at FindPlayerStart.
        // Tut1 has both branches live: the placed harry0 (its PRI appears in its
        // PostBeginPlay, i.e. BEFORE this point) and PlayerStart0.
        public UEObject SpawnPlayActor(UEInterpreter interp, PlayWorldConfig cfg)
        {
            if (interp is null) throw new ArgumentNullException(nameof(interp));
            if (cfg is null) throw new ArgumentNullException(nameof(cfg));
            UEObject level = interp.GetLevelInfo();
            UEObject game = level?.GetProp(PK.Game) as UEObject
                ?? throw new InvalidOperationException("SpawnPlayActor before Level.Game exists");

            // SpawnClass: ?Class= → ini [DefaultPlayer] Class → ini [URL] Class
            // (51548..51560). Irrelevant to the reuse branch, but Login reads
            // SpawnClass.default.bIsHuman before spawning.
            string spawnClassName = UrlOption(cfg.Options, "Class")
                ?? (string.IsNullOrEmpty(cfg.DefaultPlayerClass) ? null : cfg.DefaultPlayerClass)
                ?? cfg.UrlPlayerClass;
            UClass spawnClass = string.IsNullOrEmpty(spawnClassName) ? null
                : UEClassRegistry.Shared.Find(spawnClassName.Substring(spawnClassName.LastIndexOf('.') + 1));
            if (spawnClass is null)
                throw new System.IO.InvalidDataException($"SpawnPlayActor: player class not found: {spawnClassName}");

            CreateLocalViewport(interp, cfg);

            // Login's candidates in THE order it will see them: AddPawn
            // (execAddPawn, Engine.dll.c:46972..47005) inserts AT THE HEAD, and
            // PreBeginPlay walks the actor array — so the list head is the level's
            // highest-index pawn. The order is logged because it decides the outcome.
            var pawnOrder = new List<string>();
            for (UEObject p = level.GetProp(PK.PawnList) as UEObject;
                 p is not null && pawnOrder.Count < 32; p = p.GetProp(PK.nextPawn) as UEObject)
                pawnOrder.Add($"{p.GetProp(PK.Name)}:{p.Class?.Name}"
                    + $":player={(p.GetProp(PK.Player) is null ? "none" : "set")}"
                    + $":pri={((p.GetProp(PK.PlayerReplicationInfo) as UEObject)?.GetProp(PK.PlayerName) ?? "none")}"
                    + $":isPlayer={UEInterpreter.ToBool(p.GetProp(PK.bIsPlayer))}");
            Log($"client_init pawnlist {string.Join(' ', pawnOrder)}");

            // GameInfo.Login(Portal, Options, out Error, SpawnClass) — 51649.
            UFunction login = interp.FindEventFunction(game, "Login")
                ?? throw new InvalidOperationException("GameInfo.Login missing");
            var args = new object[] { cfg.Portal ?? "", cfg.Options ?? "", "", spawnClass };
            Log($"client_init login game={game.GetProp(PK.Name)} spawnClass={spawnClass.Name}");
            object result = interp.Invoke(game, login, args);
            string error = args[2] as string ?? "";
            if (result is not UEObject pawn)
            {
                // Shipped logs and returns NULL (51665..51669). For the port this is not
                // "continue without a player": there is nothing to play with.
                throw new InvalidOperationException(
                    $"SpawnPlayActor: Login returned none (error={error})");
            }

            // Login's result is NEVER substituted. Before returning, `GameInfo.Login` runs on
            // the chosen pawn SetMultiSkin, PlayerID, ClientSetRotation, ChangeName/ChangeTeam,
            // Password/Admin/GameReplicationInfo, the start teleport effect and the NumPlayers
            // increment; substituting AFTER would give a world where login side effects belong
            // to one actor and possession to another.
            SetPlayer(interp, pawn);                    // 51671 → Possess
            Log($"client_init possessed={pawn.GetProp(PK.Name)}:{pawn.Class?.Name}");

            // 51672..51681 (disasm 0x103a8684/0x103a8688): Role = ROLE_Authority,
            // RemoteRole = the ENetRole argument. The caller is MatchViewportsToActors
            // (0x1039bec0: `push 2` into ULevel vtable +0xac SpawnPlayActor), i.e.
            // ROLE_SimulatedProxy. Indistinguishable on shipped data: Harry's default
            // RemoteRole is already 2 (--dumpdefaults Harry) — the form follows the source.
            pawn.Props["Role"] = (byte)4;                 // ROLE_Authority
            pawn.Props["RemoteRole"] = (byte)2;           // ROLE_SimulatedProxy
            pawn.Props["ShowFlags"] = 0x480c;
            pawn.Props["RendMap"] = 5;                    // REN_DynLight
            pawn.Props["bAdmin"] = true;                  // NetDriver==0 in standalone
            // LevelAction is not written here: ULevel::SpawnPlayActor 0x103a8270..0x103a915f has no access to
            // +0x45c (0x1043e17d is the network UNetConnection::HandleClientPlayer). After a .usa load the field
            // is the class default, because it is CPF_Transient and not written to the package
            // (UELevelRuntime.SaveGame, TransientProps).

            // NewPlayer.TravelPreAccept right after the bAdmin block (0x103a86cc),
            // before the travel string is parsed.
            // Events go through ProcessEvent (eventTravelPreAccept etc. — the same gate).
            void Fire(UEObject target, string evt, params object[] a)
                => UELifecycle.CallEventArgs(interp, target, evt, a);
            Fire(pawn, "TravelPreAccept");

            // Accept travel actors (51748..51900). The event chain below runs
            // UNCONDITIONALLY: the "no travel string" path (0x103a8921 → 0x103a8bf4 →
            // 0x103a8e56) passes through it with an empty set.
            var targets = ApplyTravelInfo(interp, pawn, cfg);

            // Tail (0x103a8e56..0x103a9005), FName globals by export:
            // 0x105e9dd4 TravelPreAccept, 0x105e9e00 AcceptInventory,
            // 0x105e9dd0 TravelPostAccept, 0x105e9d7c PostLogin.
            //   travel actors in REVERSE order → TravelPreAccept
            //   Level.Game → AcceptInventory(NewPlayer)
            //   travel actors in REVERSE order → TravelPostAccept
            //   NewPlayer → TravelPostAccept (a pawn from the travel set receives
            //   it twice — as in shipped)
            //   Level.Game → PostLogin(NewPlayer) (level start music)
            for (int i = targets.Count - 1; i >= 0; i--) Fire(targets[i], "TravelPreAccept");
            if (game is not null) Fire(game, "AcceptInventory", pawn);
            for (int i = targets.Count - 1; i >= 0; i--) Fire(targets[i], "TravelPostAccept");
            Fire(pawn, "TravelPostAccept");
            if (game is not null) Fire(game, "PostLogin", pawn);
            return pawn;
        }

        // ── Level travel ────────────────────────────────────────────────────
        //
        // The script half of the chain runs by ITSELF: shipped
        // `LevelInfo.ServerTravel(URL, bItems)` (65 bytes), with empty `NextURL`,
        // writes `bNextItems`/`NextURL` and calls `GameInfo.ProcessServerTravel`
        // (452 bytes), which in standalone drives `NextSwitchCountdown` to 0.
        // The port adds exactly the half the script lacks — the engine half:
        // notice the request and perform the load.
        //
        // UGameEngine::Tick (disasm 0x103a0c0d..0x103a0cbd): with a non-empty
        // NextURL — `NextSwitchCountdown -= DeltaSeconds` (raw engine delta) and
        // travel when the result is <= 0 (`test ah,0x41`: NaN travels too). Then
        // (0x103a0d6c) NextURL ~= "?RESTART" takes the level TravelInfo as is,
        // otherwise with bNextItems — a copy of the level TravelInfo plus exported
        // players; URL — FURL(&LastURL, NextURL,
        // TRAVEL_Relative) (249256), and Browse with the "restart" option replaces it
        // (Browse replaces it entirely with LastURL when the "restart" option is present, 0x1039ae4e..0x1039aebd).
        //
        // The output `url` is the RESOLVED "map?options" URL: the frontend splits it with
        // SplitTravelUrl and passes the options in PlayWorldConfig.Options.
        public bool TryTakeLevelTravel(UEInterpreter interp, float deltaSeconds, out string url, out bool items)
        {
            url = null; items = false;
            UEObject level = interp.GetLevelInfo();
            if (level?.GetProp(PK.NextURL) is not string next || next.Length == 0) return TryTakeClientTravel(out url, out items);
            float countdown = UEInterpreter.AsFloat(level.GetProp(PK.NextSwitchCountdown)) - deltaSeconds;
            level.Props["NextSwitchCountdown"] = countdown;
            // Countdown not expired — no server travel; fall through to the client block (je 0x103a1346 from 0x103a0cbd).
            if (countdown > 0f) return TryTakeClientTravel(out url, out items);
            // TAKEN EXACTLY ONCE — but NextURL is NOT cleared while the old world lives.
            // Shipped UGameEngine::Tick (FUN_103a0900) order: level tick (Engine.dll.c:249095)
            // → NextURL handling: Browse/LoadMap and only then NextURL="" (:249317-249372)
            // → client tick with Console.Tick (:249388). So the console sees either the old
            // level with a NON-EMPTY NextURL or the new one. Clearing it here would let
            // HPConsole.Tick write the slot 99 autosave (bLoadNewLevel) from the OLD world.
            if (ReferenceEquals(level, _travelTakenLevel) && string.Equals(next, _travelTakenUrl, StringComparison.Ordinal))
                return false;
            items = UEInterpreter.ToBool(level.GetProp(PK.bNextItems));
            UEUrl target = UEUrl.Parse(LastUrl, next, UEUrl.Relative);
            if (target.HasOption("restart")) target = LastUrl.Copy();
            if (target.Portal.Length != 0)
                throw new NotSupportedException($"travel {next}: portal '#{target.Portal}' is not passed by the frontends in PlayWorldConfig");
            _travelRestart = string.Equals(next, "?RESTART", StringComparison.OrdinalIgnoreCase);
            _capturedUrl = target;
            url = target.Map + target.OptionsString;
            _travelTakenLevel = level; _travelTakenUrl = next;
            // The new map's package is named after URL.Map (see LevelPackageName).
            _pendingLevelPackage = System.IO.Path.GetFileNameWithoutExtension(target.Map);
            Log($"travel_take next={next} url={url} items={items} restart={_travelRestart}");
            return true;
        }
        UEObject _travelTakenLevel; string _travelTakenUrl;

        // Client travel block of UGameEngine::Tick (0x103a1346-0x103a1622): Viewport(0).TravelURL non-empty ->
        // Browse(FURL(LastURL, TravelURL, TravelType)) and TravelURL = "". The request is queued by OPEN
        // (RequestOpen, TRAVEL_Partial, bTravelItems 0); `?RESTART` (compared with string 0x104d88b8) — as for server travel.
        bool TryTakeClientTravel(out string url, out bool items)
        {
            url = null; items = false;
            if (string.IsNullOrEmpty(_openRequestUrl)) return false;
            string next = _openRequestUrl;
            _openRequestUrl = null;   // TravelURL = "" after Browse
            UEUrl target = UEUrl.Parse(LastUrl, next, UEUrl.Partial);
            if (target.HasOption("restart")) target = LastUrl.Copy();
            if (target.Portal.Length != 0)
                throw new NotSupportedException($"travel {next}: portal '#{target.Portal}' is not passed by the frontends in PlayWorldConfig");
            _travelRestart = string.Equals(next, "?RESTART", StringComparison.OrdinalIgnoreCase);
            _capturedUrl = target;
            url = target.Map + target.OptionsString;
            _pendingLevelPackage = System.IO.Path.GetFileNameWithoutExtension(target.Map);
            Log($"travel_take client={next} url={url} items=False restart={_travelRestart}");
            return true;
        }

        // UGameEngine::LastURL (+0x7c) and GLevel->TravelInfo (+0xf4) of the current level.
        // Both are written by a successful load: LastURL is the URL the level was loaded from,
        // TravelInfo is the map passed to LoadMap (247074).
        internal UEUrl LastUrl { get; set; }
        internal Dictionary<string, List<TravelActor>> LevelTravelInfo { get; set; } =
            new(UEPropNameComparer.Instance);
        UEUrl _capturedUrl;
        bool _travelRestart;
        string _openRequestUrl;

        // Fixture seam: drop a captured but not yet loaded travel.
        internal void ClearTravelCapture() { _capturedUrl = null; _travelRestart = false; _openRequestUrl = null; _travelTakenLevel = null; _travelTakenUrl = null; }

        void AdoptLoadedUrl(PlayWorldConfig cfg)
        {
            LastUrl = _capturedUrl
                ?? UEUrl.FromOptions(System.IO.Path.GetFileName(LoadedMapPath ?? ""), cfg.Portal, cfg.Options);
            _capturedUrl = null;
            LevelTravelInfo = cfg.TravelInfo is null
                ? new Dictionary<string, List<TravelActor>>(UEPropNameComparer.Instance)
                : new Dictionary<string, List<TravelActor>>(cfg.TravelInfo, UEPropNameComparer.Instance);
            LevelEntryTravel = LevelTravelInfo;   // port record (D-82): what travel carried in; a save load replaces it
        }

        // ── Travel actors (Engine.dll.c:249160-249230 collect, 51720-51900 accept) ──
        //
        // One carried-over actor: class, name and ONLY the CPF_Travel properties
        // (0x00010000 — the bit shipped tests as `PropertyFlags & 0x10000` at 51890).
        //
        // Shipped keeps this as a `TMap<FString,FString>` whose value is the text
        // "Class=%s Name=%s\r\n" + "Key[i]=Value" lines (export 151478-151520, parse
        // 51775-51830). The port stores the PARSED form, not the text: shipped serializes
        // because the TMap survives the level change, and observable behaviour is set by the
        // SET of actors and values, not their printing. Boundary: if travel info ever leaves
        // the process (saved to .usa), the format becomes observable and the text must be
        // reproduced byte for byte.
        public sealed class TravelActor
        {
            public UClass Class;
            public string Name;
            public readonly Dictionary<string, object> Props =
                new(UEPropNameComparer.Instance);
        }

        // A reference to an ACTOR inside the travel set — by NAME, not by pointer.
        //
        // This is literal shipped semantics. Export (Engine.dll.c:151525-151545): for a property
        // whose PropertyClass derives from AActor, the NAME of the referenced actor or the literal
        // 'None' is printed. Accept (52008-52024, LAB_103a8dbb): the name is compared with every
        // entry of THE SAME set (appStricmp on Name), and on a match the property gets the pointer
        // to the NEW, just accepted actor; on a miss the property is not written at all.
        // A live UEObject of the old world must not be stored: by accept time that world is gone.
        public sealed class TravelActorRef
        {
            public readonly string Name;
            public TravelActorRef(string name) { Name = name; }
            public override string ToString() => Name ?? "None";
        }

        static bool IsActorReferenceProperty(UELib.Core.UProperty p)
            => p is UELib.Core.UObjectProperty op
               && op.Object is UClass propertyClass
               && UEInterpreter.ClassIsA(propertyClass, "Actor");

        // Collect TravelInfo (UGameEngine::Tick 249160-249246). NextURL ?RESTART —
        // copy of the level TravelInfo without export; otherwise gated by `LevelInfo.bNextItems`
        // (249177: bit 0 at Actors(0)+0x4a8; layout confirmed by neighbours — NextURL is an
        // FString at +0x49c, NextSwitchCountdown at +0x4ac, exactly declaration order) — collection
        // STARTS from a copy of the level TravelInfo (249176-249178), with exported players on top.
        // Exported: pawns with a LIVE Player (+0x4bc, written by APlayerPawn::SetPlayer 111930)
        // and their whole Inventory chain (+0x98). Key: PlayerReplicationInfo.PlayerName
        // (+0x4b8 → +0x25c). Viewport->TravelURL = "" (249228-249246) has no target in the port:
        // ClientTravel/TravelURL are not ported and the value is always empty.
        public Dictionary<string, List<TravelActor>> GetTravelInfo(UEInterpreter interp)
        {
            var info = new Dictionary<string, List<TravelActor>>(UEPropNameComparer.Instance);
            UEObject level = interp.GetLevelInfo();
            if (_travelRestart)
            {
                foreach (var kv in LevelTravelInfo) info[kv.Key] = kv.Value;
                Log($"travel_info restart keys={info.Count}");
                return info;
            }
            if (!UEInterpreter.ToBool(level?.GetProp(PK.bNextItems)))
            {
                Log("travel_info items=false — nothing is carried over");
                return info;
            }
            foreach (var kv in LevelTravelInfo) info[kv.Key] = kv.Value;
            var defaults = new Dictionary<UClass, UEObject>();
            foreach (UEObject actor in Actors)
            {
                if (actor is null || actor.Destroyed) continue;
                if (!UEInterpreter.ClassIsA(actor.Class, "PlayerPawn")) continue;
                if (actor.GetProp(PK.Player) is null) continue;
                var records = new List<TravelActor>();
                void Export(UEObject a)
                {
                    TravelActor rec = ExportTravelActor(interp, a, defaults);
                    if (rec is not null) records.Add(rec);
                }
                Export(actor);
                // Inventory chain up to NULL — no limit and no deleted check (0x103a0f04-0x103a0f24, D-14 item 3).
                // A cycle in the chain is an infinite loop in shipped; here a loud failure.
                var visited = new HashSet<UEObject>(ReferenceEqualityComparer.Instance) { actor };
                for (UEObject inv = actor.GetProp(PK.Inventory) as UEObject; inv is not null;
                     inv = inv.GetProp(PK.Inventory) as UEObject)
                {
                    if (!visited.Add(inv))
                        throw new InvalidOperationException($"travel: cycle in the Inventory chain of {actor.GetProp(PK.Name)} at {inv.GetProp(PK.Name)}");
                    Export(inv);
                }
                string key = (actor.GetProp(PK.PlayerReplicationInfo) as UEObject)
                    ?.GetProp(PK.PlayerName)?.ToString() ?? "";
                info[key] = records;
                Log($"travel_info key={key} actors={records.Count} "
                    + $"list={string.Join(',', records.ConvertAll(r => r.Name))}");
            }
            return info;
        }

        // ExportTravel (thunk_FUN_103a0210, Engine.dll.c:151450-151540).
        //   * gate Actor.bTravel (+0x198 bit 0x10000): without it the actor is NOT written
        //     at all — neither the "Class=%s Name=%s" header nor properties;
        //   * for EVERY element of a CPF_Travel (0x10000) property:
        //     ExportText(i, buf, actor, Class->Defaults, 0) — UProperty::ExportText
        //     (Core.dll.c:26856) writes the element only if it is NOT Identical to the
        //     class default;
        //   * a property whose class derives from Actor is written by NAME (151525-151545);
        //   * a struct — UStructProperty::ExportTextItem (Core.dll.c:21735):
        //     only fields that differ from the default struct's field.
        // Comparison is the type's Identical (Core.dll.c: Int 4817, Float 39655,
        // Bool 61774, Byte 58116, Object 47875, Struct 5675, Str 36474, Name 49024).
        static TravelActor ExportTravelActor(UEInterpreter interp, UEObject actor, Dictionary<UClass, UEObject> defaults)
        {
            if (!UEInterpreter.ToBool(actor.GetDeclared("Actor", "bTravel"))) return null;
            if (!defaults.TryGetValue(actor.Class, out UEObject cdo))
            {
                cdo = new UEObject { Class = actor.Class, Interp = interp };
                cdo.LoadDefaults();
                defaults[actor.Class] = cdo;
            }
            var rec = new TravelActor { Class = actor.Class, Name = actor.GetProp(PK.Name)?.ToString() };
            var seen = new HashSet<string>(UEPropNameComparer.Instance);
            for (UClass c = actor.Class; c is not null; c = UEClassRegistry.Shared.CanonicalSuper(c))
                foreach (UELib.Core.UProperty p in c.EnumerateFields<UELib.Core.UProperty>())
                {
                    if (!p.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Travel)) continue;
                    string name = p.Name;
                    if (!seen.Add(name)) continue;   // the nearest class overrides
                    int dim = Math.Max(1, p.ArrayDim);
                    object value = actor.GetProp(name), def = cdo.GetProp(name);
                    for (int i = 0; i < dim; i++)
                    {
                        object v = dim > 1 ? ElementAt(value, i) : value;
                        object d = dim > 1 ? ElementAt(def, i) : def;
                        if (TravelIdentical(p, v, d)) continue;
                        string key = dim > 1 ? $"{name}[{i}]" : name;
                        if (IsActorReferenceProperty(p))
                            rec.Props[key] = new TravelActorRef((v as UEObject)?.GetProp(PK.Name)?.ToString());
                        else if (p is UELib.Core.UStructProperty sp)
                            rec.Props[key] = StructDelta(sp.Struct, v, d, key);
                        else rec.Props[key] = TravelText(p, v);
                    }
                }
            return rec;
        }

        // Travel goes as TEXT: ExportText -> ExportTextItem (float — "%.7g", 0x101c2ad4) -> on accept
        // ImportText (float — appAtof, name — ReadToken). Here the same round trip for float (up to 1 ulp) and
        // name (truncated at the first char outside [A-Za-z0-9_-]); for other types text does not change the value.
        static object TravelText(UELib.Core.UProperty p, object v)
        {
            if (p.Type is not (UELib.Types.PropertyType.FloatProperty or UELib.Types.PropertyType.NameProperty)) return v;
            object back = v;
            UEObjectConfig.ImportText(p, UEObjectConfig.ExportTextItem(p, v), ref back);
            return back;
        }

        static object ElementAt(object list, int i)
            => list is System.Collections.IList l && i < l.Count ? l[i] : null;

        // Per-field struct delta — exactly the fields ExportTextItem writes.
        // Nested structs and object fields do not occur in shipped travel data
        // and fail loudly.
        public sealed class TravelStructDelta
        {
            public readonly Dictionary<string, object> Fields = new(UEPropNameComparer.Instance);
        }

        static TravelStructDelta StructDelta(UELib.Core.UStruct st, object v, object d, string where)
        {
            if (v is not null && v is not UEObject)
                throw new NotSupportedException($"travel: struct {where} ({v.GetType().Name}) — export of a non-bag struct is not ported");
            var delta = new TravelStructDelta();
            for (UELib.Core.UStruct s = st; s is not null; s = s.Super as UELib.Core.UStruct)
                foreach (UELib.Core.UProperty f in s.EnumerateFields<UELib.Core.UProperty>())
                {
                    if (!Port(f)) continue;
                    if (f is UELib.Core.UStructProperty || f is UELib.Core.UObjectProperty || f.ArrayDim > 1)
                        throw new NotSupportedException($"travel: field {where}.{f.Name} ({f.GetType().Name}) — export not ported");
                    object fv = (v as UEObject)?.GetProp(f.Name), fd = (d as UEObject)?.GetProp(f.Name);
                    if (!TravelIdentical(f, fv, fd)) delta.Fields[f.Name] = TravelText(f, fv);
                }
            return delta;
        }

        // UProperty::Port (Core.dll.c:26415, export 0x1eab): ElementSize·ArrayDim != 0, AND (Category != None OR not
        // Transient|Native, 0x3000), AND name != Names[0x62] — `Class` (hardcoded name 98: shipped Core.dll FNameEntry
        // table in order, checked against NAME_Package = 0x17c/4 and NAME_Core = 0x40/4 in UnLinker).
        static bool Port(UELib.Core.UProperty f)
        {
            bool hasCategory = f.CategoryName is { } cat && !string.Equals(cat.ToString(), "None", StringComparison.OrdinalIgnoreCase);
            bool transientOrNative = f.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Transient)
                                     || f.PropertyFlags.HasFlag(UELib.Flags.PropertyFlag.Native);
            if (!hasCategory && transientOrNative) return false;
            return !string.Equals(f.Name?.ToString(), "Class", StringComparison.OrdinalIgnoreCase);
        }

        // UStructProperty::Identical (Core.dll.c:5684): ALL property fields of the struct, without the Port filter.
        static bool StructIdentical(UELib.Core.UStruct st, object a, object b, string where)
        {
            for (UELib.Core.UStruct s = st; s is not null; s = s.Super as UELib.Core.UStruct)
                foreach (UELib.Core.UProperty f in s.EnumerateFields<UELib.Core.UProperty>())
                {
                    if (f is UELib.Core.UObjectProperty || f.ArrayDim > 1)
                        throw new NotSupportedException($"travel: Identical of field {where}.{f.Name} ({f.GetType().Name}) not ported");
                    if (!TravelIdentical(f, (a as UEObject)?.GetProp(f.Name), (b as UEObject)?.GetProp(f.Name))) return false;
                }
            return true;
        }

        static bool TravelIdentical(UELib.Core.UProperty p, object a, object b)
        {
            switch (p)
            {
                case UELib.Core.UBoolProperty: return UEInterpreter.ToBool(a) == UEInterpreter.ToBool(b);
                case UELib.Core.UFloatProperty: return UEInterpreter.AsFloat(a) == UEInterpreter.AsFloat(b);
                case UELib.Core.UIntProperty: return UEInterpreter.ToInt(a) == UEInterpreter.ToInt(b);
                case UELib.Core.UByteProperty: return (UEInterpreter.ToInt(a) & 0xff) == (UEInterpreter.ToInt(b) & 0xff);
                case UELib.Core.UObjectProperty: return ReferenceEquals(a, b) || (a is null && b is null);
                case UELib.Core.UStrProperty:
                    return string.Equals(a?.ToString() ?? "", b?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                case UELib.Core.UNameProperty:
                    return string.Equals(a?.ToString() ?? "None", b?.ToString() ?? "None", StringComparison.OrdinalIgnoreCase);
                case UELib.Core.UStructProperty sp:
                    return StructIdentical(sp.Struct, a, b, p.Name);
                default:
                    throw new NotSupportedException($"travel: Identical for {p.Name} ({p.GetType().Name}) not ported");
            }
        }

        // Accept (51748-51900), RIGHT after SetPlayer: the record is looked up by ?Name= from
        // the URL; if absent and standalone, the FIRST map entry is taken (51760-51772). An actor
        // of a PlayerPawn subclass is NOT spawned: it reuses the pawn Login returned (51786-51792),
        // otherwise the map would have two players. The rest (inventory) are spawned.
        // Properties are applied in a SECOND pass, once all actors exist (51850+), otherwise
        // a reference from one travel actor to another would point to a not-yet-existing object.
        internal List<UEObject> ApplyTravelInfo(UEInterpreter interp, UEObject pawn, PlayWorldConfig cfg)
        {
            var accepted = new List<UEObject>();
            if (cfg.TravelInfo is null || cfg.TravelInfo.Count == 0) return accepted;
            string wanted = UrlOption(cfg.Options, "Name");
            List<TravelActor> records = null;
            if (wanted is not null) cfg.TravelInfo.TryGetValue(wanted, out records);
            if (records is null)
            {
                // NM_Standalone (LevelInfo+0x464 == 0) is the only mode this port
                // reaches; there shipped takes the first entry.
                foreach (var kv in cfg.TravelInfo) { records = kv.Value; break; }
            }
            if (records is null) return accepted;

            var targets = new List<(TravelActor Rec, UEObject Actor)>(records.Count);
            foreach (TravelActor rec in records)
            {
                UEObject target;
                if (UEInterpreter.ClassIsA(rec.Class, "PlayerPawn")) target = pawn;
                else
                {
                    // Owner = the pawn, Instigator = NULL, bNoCollisionFail = 1
                    // (Engine 0x103a8a19-0x103a8a6b).
                    target = SpawnActor(interp, pawn, rec.Class, pawn, null,
                        UEInterpreter.AsVector3(pawn.GetProp(PK.Location)),
                        UEInterpreter.AsRotator(pawn.GetProp(PK.Rotation)),
                        noCollisionFail: true);
                    if (target is null)
                    {
                        // Shipped logs the failure and continues (51806).
                        Log($"travel_accept spawn_failed class={rec.Class?.Name} name={rec.Name}");
                        continue;
                    }
                }
                targets.Add((rec, target));
            }
            foreach ((TravelActor rec, UEObject actor) in targets)
            {
                int unresolved = 0;
                foreach (var kv in rec.Props)
                {
                    // A static array element arrives as a "Name[i]=" line
                    // (151515-151520): ONLY that element is written.
                    int br = kv.Key.IndexOf('[');
                    if (br > 0)
                    {
                        string arrName = kv.Key.Substring(0, br);
                        int idx = int.Parse(kv.Key.Substring(br + 1, kv.Key.Length - br - 2), System.Globalization.CultureInfo.InvariantCulture);
                        if (actor.GetProp(arrName) is not System.Collections.IList arr || idx >= arr.Count)
                            throw new InvalidOperationException($"travel_accept: {actor.GetProp(PK.Name)}.{arrName} — no static array for {kv.Key}");
                        if (kv.Value is TravelStructDelta ed) ApplyStructDelta(arr[idx], ed, kv.Key);
                        else if (kv.Value is TravelActorRef)
                            throw new NotSupportedException($"travel_accept: actor reference in array element {kv.Key} is not ported");
                        else arr[idx] = kv.Value;
                        continue;
                    }
                    if (kv.Value is TravelStructDelta sd)
                    {
                        ApplyStructDelta(actor.GetProp(kv.Key), sd, kv.Key);
                        continue;
                    }
                    if (kv.Value is TravelActorRef aref)
                    {
                        // Scan over the set of ACCEPTED actors (52008-52024).
                        UEObject resolved = null;
                        foreach ((TravelActor other, UEObject candidate) in targets)
                            if (string.Equals(other.Name, aref.Name, StringComparison.OrdinalIgnoreCase))
                            { resolved = candidate; break; }
                        // Miss — the property is NOT written at all (shipped jumps to
                        // LAB_103a8e16 without a write), so the actor keeps the value
                        // from its defaults.
                        if (resolved is null) { unresolved++; continue; }
                        actor.Props[kv.Key] = resolved;
                        // This line distinguishes "reference moved" from "reference points
                        // into the torn-down world": the right side must name an actor of
                        // the NEW world.
                        Log($"travel_ref actor={actor.GetProp(PK.Name)} prop={kv.Key} "
                            + $"was={aref.Name} now={resolved.GetProp(PK.Name)}");
                        continue;
                    }
                    actor.Props[kv.Key] = kv.Value;
                }
                Log($"travel_accept actor={actor.GetProp(PK.Name)}:{actor.Class?.Name} "
                    + $"from={rec.Name} props={rec.Props.Count}"
                    + (unresolved > 0 ? $" unresolved_refs={unresolved}" : ""));
            }

            // The accept event chain runs in SpawnPlayActor.
            foreach ((TravelActor _, UEObject actor) in targets) accepted.Add(actor);
            return accepted;
        }

        // Struct ImportText writes ONLY the listed fields; the rest keep the
        // receiving actor's values.
        static void ApplyStructDelta(object target, TravelStructDelta delta, string where)
        {
            if (target is not UEObject bag)
                throw new InvalidOperationException($"travel_accept: {where} — the receiving actor has no struct");
            foreach (var f in delta.Fields) bag.Props[f.Key] = f.Value;
        }

        // A travel URL is `Map.unr?options`. The map name is split off here;
        // the frontend turns it into a file path (it owns the file system).
        public static (string Map, string Options) SplitTravelUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return ("", "");
            int q = url.IndexOf('?');
            return q < 0 ? (url, "") : (url.Substring(0, q), url.Substring(q));
        }

        // ── Travel split into PRIMITIVES ─────────────────────────────────────
        //
        // Travel() below stays one call composed of these three steps. A frontend whose
        // presentation switch is asynchronous may have one or more frames between "collect the
        // set from the live old world" and "load the new one", so a single transaction is not
        // possible there.
        //
        // The primitives do NOT permit a custom sequence: the order is fixed here and is the
        // same for every frontend. The only allowed addition is a presentation switch between
        // steps 1 and 3.

        // Step 1: take the request (TryTakeLevelTravel) and collect the travel set (GetTravelInfo).
        // MUST be collected before teardown: export walks the live actors of the old level
        // (shipped does it in Browse, before LoadMap), and the gate inside is LevelInfo.bNextItems.

        // Step 2: tear down the previous world.
        // ResetWorld WITHOUT interp does not clear interp.SpawnLog (it explains there why that
        // is required). Player references must die with the world too: the pawn belonged to the
        // TORN-DOWN level, and a surviving reference would make an actor of the previous map the
        // player's owner.
        public void TeardownPlayWorld(UEInterpreter interp)
        {
            // With a live GLevel, UGameEngine::LoadMap calls NotifyLevelChange
            // (vtable +0xcc, call 0x1039c9fe) — after loading the new map's package and
            // BEFORE detaching the old level from the viewport; the travel set export
            // (UGameEngine::Tick) is already done by then. In the port that is the start of
            // teardown, and only when teardown is part of travel (request taken by
            // TryTakeLevelTravel): tearing down a previous frontend session is not a shipped
            // level change and fires no event.
            if (_travelTakenUrl is not null) NotifyLevelChange(interp);
            _travelTakenLevel = null; _travelTakenUrl = null;   // the marker belongs to the torn-down world
            PlayerPawn = null;
            LocalPlayer = null;
            // Viewport/ViewportConsole outlive the level (see CreateLocalViewport);
            // the old pawn is detached from the viewport, as shipped does before SpawnPlayActor.
            if (Viewport is not null) Viewport.Props["Actor"] = null;
            if (interp is not null) interp.PlayerHarry = null;
            ResetWorld(interp);
        }

        // ── UGameEngine::NotifyLevelChange (Engine.dll 0x1039c050) ──────────
        // if (Client && Client->Viewports.Num() && Viewports(0)->Console)
        //     Console->ProcessEvent(FindFunctionChecked(NAME_NotifyLevelChange))
        // (0x1039c06b..0x1039c0b3: +0x4c Client, +0x30 Num, +0x2c Viewports,
        // +0x34 Console, FName 0xc0). The script is UWindowConsole.NotifyLevelChange:
        // Console.NotifyLevelChange (bRestartTimeDemo, ClearMessages), then
        // bLevelChange = True and Root.NotifyBeforeLevelChange().
        // The second shipped caller — the AbortToEntry branch in Browse (0x1039b78b,
        // URL ?failed/?closed) — is not reached by the campaign.
        internal void NotifyLevelChange(UEInterpreter interp)
        {
            if (interp is null || Viewport?.GetProp(PK.Console) is not UEObject console) return;
            UFunction fn = interp.FindEventFunction(console, "NotifyLevelChange")
                ?? throw new InvalidOperationException(
                    $"NotifyLevelChange: console {console.Class?.Name} has no event (FindFunctionChecked)");
            interp.Invoke(console, fn);
            Log($"notify_level_change console={console.GetProp(PK.Name)} "
                + $"bLevelChange={UEInterpreter.ToBool(console.GetProp(PK.bLevelChange))}");
        }

        // Step 3: load the new map and run the SHARED bootstrap (the same as on the first
        // level load) with the accepted travel set in the config.
        public UEObject LoadAndBootstrapPlayWorld(UEInterpreter interp, string mapPath,
            PlayWorldConfig cfg)
        {
            LoadWorld(mapPath, interp);
            BuildLoadMapScreenOfLoadedLevel(interp);
            InitializeLevelClock(interp);
            InitCollisionHash();
            return BootstrapPlayWorld(interp, cfg);
        }

        // Travel as ONE transaction — composition of the three primitives above.
        public UEObject Travel(UEInterpreter interp, string mapPath, PlayWorldConfig cfg)
        {
            Log($"travel_begin map={mapPath}");
            cfg.TravelInfo = GetTravelInfo(interp);
            TeardownPlayWorld(interp);
            UEObject pawn = LoadAndBootstrapPlayWorld(interp, mapPath, cfg);
            Log($"travel_end map={mapPath} actors={Actors.Count} player={pawn?.GetProp(PK.Name)}");
            return pawn;
        }

        // LoadMap's ?Game= is NOT FURL::GetOption: a loop over URL.Op (disasm
        // 0x1039d910..0x1039d98b) calls Parse(Op[i], "GAME=", buf, 256) (Core
        // 0x10151b70) on EVERY option, so the LAST match wins, and the value is cut
        // at space/CR/LF/TAB/comma (or taken in quotes).
        // An empty buffer after the loop means there was no option (247253).
        internal static string GameOption(string options)
        {
            string game = "";
            foreach (string op in (options ?? "").Split('?'))
                if (UEValue.ParseString(op, "GAME=", 0x100, out string v)) game = v;
            return game.Length == 0 ? null : game;
        }

        // URL option "?Key=Value" — shipped FURL::GetOption. The key is
        // case-insensitive (UE1 FName semantics); the value is returned as is.
        internal static string UrlOption(string options, string key)
        {
            if (string.IsNullOrEmpty(options) || string.IsNullOrEmpty(key)) return null;
            foreach (string part in options.Split('?'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (!part.AsSpan(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                return part.Substring(eq + 1);
            }
            return null;
        }
    }
}
