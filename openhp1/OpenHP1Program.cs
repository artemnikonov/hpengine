// Own engine: Core brings up and ticks the world, this process draws the static BSP with Core lightmaps via SDL_GPU.
// Modes: --shot <png> (no window: frame + timing), --selftest-gpu <png> (no game files: synthetic scene against the
// shader formulas), otherwise a window.
using System.Diagnostics;
using System.Runtime.InteropServices;
using HP1.UEScript;
using SDL;
using static SDL.SDL3;

namespace OpenHP1
{
    static unsafe class Program
    {
        static int Main(string[] args)
        {
            HarnessEnv.WarnUnread();   // harness probes do not affect the engine: loud (D-19)
            // macOS: a held key repeats its character (WM_CHAR repeat, as Windows gives the original) instead of opening the
            // system accent menu over the game (held W on a Russian layout showed a numbered popup of its Cyrillic letter). Before the first SDL_Init
            // (the splash): Cocoa reads it when the app registers.
            SDL_SetHint("SDL_MAC_PRESS_AND_HOLD", "0");
            string root = null, map = null, shot = null, video = null, selftest = null, visual = null;
            int videoFrames = 300;
            string lookAt = null;
            int warm = 50, bench = 300, w = 1280, h = 720, post = 0, probeSettle = 0, kill = -1; bool sizeFromArgs = false;
            var causeEvents = new List<string>();
            var consoleAt = new List<(int Tick, string Cmd)>();
            var consoleCmds = new List<string>();   // --console "<command>": same entry as the menu ConsoleCommand (UEConsoleCommand.Execute)
            var keys = new List<(int Tick, string Key)>();   // --key <post tick>:<EInputKey name>: Press before that post tick, Release before the next (CoreInputSink, the window's route)
            PerfTitle = FramePacer.PerfTitleFromArgs(args);   // --perf-title: fps/timings in the window title
            for (int i = 0; i < args.Length; i++)
                switch (args[i])
                {
                    case "--root": root = args[++i]; break;
                    case "--map": map = args[++i]; break;
                    case "--shot": shot = args[++i]; break;
                    case "--selftest-gpu": selftest = args[++i]; break;
                    case "--video": video = args[++i]; break;      // directory of PNG frames (no window)
                    case "--frames": videoFrames = int.Parse(args[++i]); break;
                    case "--look-at": lookAt = args[++i]; break;
                    case "--cam": CamOverride = args[++i].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); break;
                    case "--look-dist": LookDist = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;   // snapshot: camera 300 units from the actor, along the actor's yaw
                    
                    case "--debug-cam": DebugCam = true; break;
                    case "--warm": warm = int.Parse(args[++i]); break;
                    case "--fps": TargetFps = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); FpsFromArgs = true; break;   // window pacing (Core ticks per second); otherwise 60
                    case "--dt": WarmDt = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;   // snapshot warm-up step (s)
                    case "--bench": bench = int.Parse(args[++i]); break;
                    case "--size": var p = args[++i].Split('x'); w = int.Parse(p[0]); h = int.Parse(p[1]); sizeFromArgs = true; break;
                    case "--console": consoleCmds.Add(args[++i]); break;
                    case "--console-at": var ca = args[++i]; int cc = ca.IndexOf(':'); consoleAt.Add((int.Parse(ca[..cc]), ca[(cc + 1)..])); break;   // <post tick>:<command>, before that post tick
                    case "--drawlog": DrawLog.Path = args[++i]; break;
                    case "--probe-settle": probeSettle = int.Parse(args[++i]); break;   // snapshot camera only for the last N warm-up ticks   // snapshot draw-call log (DrawLog.cs)
                    case "--key": var kp = args[++i].Split(':'); keys.Add((int.Parse(kp[0]), kp[1])); break;
                    case "--post": post = int.Parse(args[++i]); break;   // ticks after --console (snapshot after loading a save etc.)
                    case "--causeevent": causeEvents.Add(args[++i]); break;   // snapshot: Trigger(player, player) on actors with that Tag after --console (UEGameEngine.CauseEventProbe)
                    case "--kill": kill = int.Parse(args[++i]); break;   // snapshot: baseHarry.KillHarry(true) before that post tick (death -> LoadSelectedSlot; HeadlessSmoke HP1_KILL_AT)
                    case "--visual": visual = args[++i]; break;   // snapshot/probe: [OpenHP1] VisualMode (Enhanced|Original) from the start
                }

            if (selftest is not null)   // no game files: synthetic scene (SdlSelfTest.cs)
            {
                SDL_SetHint("SDL_VIDEO_ALLOW_SCREENSAVER", "1");
                if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO)) { Console.Error.WriteLine(SDL_GetError()); return 2; }
                return GpuSelfTest.Run(selftest);
            }

            // Game installation (openhp1/packaging/README.md): --root, otherwise the directory next to the executable
            // (package over the installation), otherwise a clear error and exit code 4; no hard-coded path is substituted.
            try { root = OpenHP1.Packaging.InstallRoot.Resolve(root, AppContext.BaseDirectory); }
            catch (OpenHP1.Packaging.InstallRootException e)
            {
                Console.Error.WriteLine(e.Message);
                return OpenHP1.Packaging.InstallRoot.ExitCode;
            }
            // Startup map without --map, as in the original: UGameEngine::Init reads `[URL] LocalMap`
            // (Default.ini: LocalMap=Startup.unr, the main menu with the intro).
            map ??= System.IO.Path.GetFileNameWithoutExtension(
                new UEIniFile(UELevelRuntime.DefaultIniPath(Path.Combine(root, "System"))).Get("URL", "LocalMap") ?? "")
                is { Length: > 0 } localMap ? localMap
                : throw new InvalidDataException("Default.ini: no [URL] LocalMap — the start map is not set");
            string[] probes = { "--audio-probe", "--ice-png", "--input-probe", "--mesh-dump", "--mover-lmatlas", "--movers", "--travel-at", "--travel-png", "--weapon" };
            bool windowedGame = shot is null && video is null && !args.Any(probes.Contains);
            if (windowedGame)   // only the windowed game
            {
                StartRunLog(Path.Combine(OpenHP1.Packaging.InstallRoot.DataDir(root, out string moveError), "logs"));
                if (moveError is not null) Console.Error.WriteLine("[Log] " + moveError);
            }
            // An edition UEEdition does not recognise runs, with its warning in the console and the run log.
            if (UEEdition.Of(root).Warning is string editionWarning) Console.Error.WriteLine("[Edition] WARNING: " + editionWarning);
            // Startup splash (HP.exe WinMain): before the engine comes up, closed once the first map is loaded.
            if (windowedGame && UELevelRuntime.ShowsSplash(string.Join(" ", args)))
            {
                var inis = UELevelRuntime.InstallInis(Path.Combine(root, "System"));
                var splashConfig = new UEConfig(inis.System, inis.User);
                splashConfig.BindStore(new UEConfig.FileStore(OpenHP1.Packaging.InstallRoot.DataDir(root)));
                Splash.Show(UELevelRuntime.SplashBitmapPath(root, splashConfig));
            }
            if (args.Contains("--prof")) Environment.SetEnvironmentVariable("HP1_TICK_PROF", "1");
            var sw = Stopwatch.StartNew();
            // Settings survive a restart (author decision): the changed menu is written as an overlay into
            // <install>/OpenHP1Data (Default.ini/DefUser.ini with the original sections); shipped files are untouched.
            // Only the windowed game owns the player's settings: snapshots and probes (--shot, --video, probe modes) get
            // the in-memory store (null), so a `--console "set ini:..."` in a check never rewrites the player's overlay.
            UEGameEngine host = UEGameEngine.Boot(root, map, selectHardwareRenderDevice: true,
                configStore: windowedGame ? new UEConfig.FileStore(OpenHP1.Packaging.InstallRoot.DataDir(root))
                    : visual is null ? null : UEConfig.SeededMemory((UEConfig.ScopeSystem, UEConfig.OpenHP1Section, UEConfig.VisualModeKey, visual)));
            Host = host;
            // Windowed game (not a snapshot): the first ticks after the world comes up use the original's load-time delta
            // (≥0.1 s, UELevelRuntime.EmulateLoadStutter). Otherwise Trigger.Touch rejects touches before level time 0.25 s,
            // and the startup music MU1 (Tut1b, Tut3, Lev3_Intro) does not start. Snapshots go without it (frame determinism).
            if (shot is null) host.SetRealFramePacing(true);
            Console.WriteLine($"core boot {sw.ElapsedMilliseconds} ms");
            int tIdx = Array.IndexOf(args, "--travel-at");   // travel probe: --travel-at <tick> <map>
            if (tIdx >= 0) host.SetTravelProbe(int.Parse(args[tIdx + 1]), args[tIdx + 2]);
            Metrics = new UETextureMetrics(root);
            Anim = new UETextureAnim(root);
            // Fire and water (openhp1/ProcTex). The procedural-texture RNG ring is seeded from a SEPARATE presentation
            // stream, not from the game appRand (render has its own RNG stream; in the original the ring
            // pulled the shared rand() on the first UFractalTexture). Seed: launch time (MsvcRandom.UnixTimeSeed);
            // snapshots (--shot) use 1 so the frame is reproducible.
            var procData = new UEProceduralTextureData(root);
            var procRng = new HP1.Core.MsvcRandom(args.Contains("--shot") ? 1u : HP1.Core.MsvcRandom.UnixTimeSeed());
            if (!FractalRandom.Shared.Seeded) FractalRandom.Shared.Seed(procRng.NextInt15);
            Proc = new OpenHP1.ProcTex.ProcTextures(procData.FireTextures(), procData.WetTextures());
            if (Environment.GetEnvironmentVariable("HP1_PROCDBG") == "1")
            {
                var firePkg = UELib.UnrealLoader.LoadPackage(UEInstallPath.File(Path.Combine(root, "System", "Fire.u")) ?? Path.Combine(root, "System", "Fire.u"), FileAccess.Read);
                firePkg.InitializePackage(UELib.UnrealPackage.InitFlags.All);
                foreach (var e in firePkg.Exports)
                    if (e.Object is UELib.Core.UClass c && (e.ObjectName.ToString() is "IceTexture" or "FractalTexture" or "WaterTexture" or "WetTexture"))
                    {
                        try { c.BeginDeserializing(); } catch { }
                        Console.WriteLine($"[Ice] class {e.ObjectName}: " + string.Join(" ", new[] { "TimeMethod", "MinFrameRate", "MaxFrameRate", "PrimeCount", "HorizPanSpeed", "VertPanSpeed", "Frequency", "Amplitude", "MoveIce", "DropType", "WaveAmp" }.Select(k => $"{k}={UEObject.ClassDefaultScalar(c, k) ?? "-"}")));
                    }
            }
            IceDefs = new Dictionary<string, UEProceduralTextureData.Ice>(StringComparer.OrdinalIgnoreCase);
            foreach (var ice in procData.IceTextures())
            {
                IceDefs[ice.Path] = ice;
                if (ice.Dropped.Length > 0 || ice.SourceP8 == null || ice.GlassP8 == null)
                    Console.WriteLine($"[Ice] {ice.Path}: not drawn as ice — {(ice.Dropped.Length > 0 ? ice.Dropped : "")}{(ice.SourceP8 == null ? "no source; " : "")}{(ice.GlassP8 == null ? "no glass; " : "")}(RenderIce requires both)");
                if (ice.Defaulted.Count > 0 && Environment.GetEnvironmentVariable("HP1_PROCDBG") == "1")
                    Console.WriteLine($"[Ice] {ice.Path}: no tag, taken from UIceTexture::Init/0: {string.Join(",", ice.Defaulted)}");
            }
            foreach (string pr in procData.Problems) Console.WriteLine("[ProcTex] " + pr);
            if (Environment.GetEnvironmentVariable("HP1_PROCDBG") == "1")
            {
                foreach (var fd in procData.FireTextures().Take(5)) Console.WriteLine($"procdbg fire {fd.Path} / {fd.Name}");
                foreach (var wd in procData.WetTextures()) Console.WriteLine($"procdbg wet {wd.Path} src={wd.SourcePath}");
                Console.WriteLine($"procdbg fires={procData.FireTextures().Count} wets={procData.WetTextures().Count}");
            }
            if (args.Contains("--movers")) return MoverProbe(host);
            int mdIdx = Array.IndexOf(args, "--mesh-dump");   // skeletal mesh data: --mesh-dump <path>[,<path>...]
            if (mdIdx >= 0)
            {
                var ms = host.Runtime.MeshSource;
                foreach (string mp in args[mdIdx + 1].Split(','))
                {
                    var rm = ms.Skeletal(mp);
                    if (rm is null) { Console.WriteLine($"mesh {mp}: none"); continue; }
                    var wsum = new float[rm.numPoints]; var wcnt = new int[rm.numPoints];
                    for (int b = 0; b < rm.numBones; b++)
                        for (int k = rm.weightIdxStart[b]; k < rm.weightIdxStart[b] + rm.weightIdxCount[b]; k++) { wsum[rm.weightPointIndex[k]] += rm.weightValue[k]; wcnt[rm.weightPointIndex[k]]++; }
                    int bad = wsum.Count(x => Math.Abs(x - 1f) > 0.01f), zero = wsum.Count(x => x == 0f);
                    var scales = Enumerable.Range(0, rm.numBones).Where(b => rm.boneScale is { } bs && (Math.Abs(bs[b * 3] - 1) > 1e-4 || Math.Abs(bs[b * 3 + 1] - 1) > 1e-4 || Math.Abs(bs[b * 3 + 2] - 1) > 1e-4)).Select(b => $"{rm.boneNames[b]}({rm.boneScale[b * 3]:F3},{rm.boneScale[b * 3 + 1]:F3},{rm.boneScale[b * 3 + 2]:F3})").ToList();
                    Console.WriteLine($"mesh {mp}: points {rm.numPoints} bones {rm.numBones} weights {rm.numWeights} localPoints {rm.numLocalPoints} root={rm.rootConvention} scale=({string.Join(",", rm.scale ?? new float[0])}) origin=({string.Join(",", rm.origin ?? new float[0])}) rotOrigin=({string.Join(",", rm.rotationOrigin ?? new int[0])})");
                    Console.WriteLine($"   weight sum != 1: {bad}, without weights: {zero}, max weights per point {wcnt.Max()}; bones with scale != 1: {(scales.Count == 0 ? "none" : string.Join(" ", scales.Take(12)))}");
                    Console.WriteLine($"   bones: {string.Join(" ", rm.boneNames.Take(12))}");
                    var hm = host.Runtime.MeshCatalog.Get(mp.Substring(mp.LastIndexOf('.') + 1), mp);
                    var rp = hm.ComputeReferencePosePoints();
                    double mx = 0, sum = 0; int n = 0;
                    for (int i = 0; i < rp.Length && i < hm.Points.Length; i++) { double d = (rp[i] - hm.Points[i]).magnitude; mx = Math.Max(mx, d); sum += d; n++; }
                    Console.WriteLine($"   reference pose against the package Points: max {mx:F4}, mean {sum / Math.Max(1, n):F4} ({n} points)");
                }
                return 0;
            }
            if (args.Contains("--input-probe"))   // minimal input: right button + mouse motion, reading player fields
            {
                var stage = new OpenHP1.Input.InputStage { Warn = Console.Error.WriteLine };
                var isink = new OpenHP1.Input.CoreInputSink(host.Interp, host.Runtime) { Warn = Console.Error.WriteLine };
                for (int t = 0; t < 400; t++) { PrepareStep(w, h); stage.RunFrame(isink, 1f / 60f); host.Tick(1, 1f / 60f); }
                UEObject pl = host.Runtime.PlayerPawn;
                if (args.Contains("--spell-state")) { Console.WriteLine("GotoState SpellLearning: " + host.Interp.GotoStateCore(pl, "SpellLearning")); host.Tick(1, 1f / 60f); }
                if (args.Contains("--mouse-scale"))   // same deltas and dt as in the live original (tools/origprobe/hp1mouse.c)
                {
                    foreach (var (dx, dy, dt) in new[] { (80f, 0f, 0.03484f), (-80f, 0f, 0.04199f), (0f, 50f, 0.03540f), (0f, -50f, 0.03687f), (6f, 0f, 0.05501f), (200f, 0f, 0.03713f) })
                    {
                        stage.OnMouseMotion(dx, dy);   // SDL: yrel "down = +", as WM_MOUSEMOVE lParam
                        PrepareStep(w, h); stage.RunFrame(isink, dt);   // HoldFrame = UInput::ReadInput(dt): the axis is already ×20/dt
                        float ax = UEInterpreter.AsFloat(pl.GetProp("aMouseX")), ay = UEInterpreter.AsFloat(pl.GetProp("aMouseY"));
                        Console.WriteLine($"mouse-scale dx={dx} dy={dy} dt={dt:R} postX={ax:F4} postY={ay:F4} preX={ax * dt / 20f:F5} preY={ay * dt / 20f:F5}");
                        host.Tick(1, dt);
                    }
                    return 0;
                }
                string Dump(string tag) => $"{tag}: aMouseX={pl.GetProp("aMouseX")} aMouseY={pl.GetProp("aMouseY")} SmoothMouseX={pl.GetProp("SmoothMouseX")} SmoothMouseY={pl.GetProp("SmoothMouseY")} bAltFire={pl.GetProp("bAltFire")} bFire={pl.GetProp("bFire")} state={pl.CurrentStateName}";
                Console.WriteLine(Dump("before"));
                stage.OnMouseButton(1, true);   // SDL_BUTTON_LEFT (DefUser.ini: LeftMouse=AltFire)
                for (int t = 0; t < 10; t++) { stage.OnMouseMotion(8f, -5f); PrepareStep(w, h); stage.RunFrame(isink, 1f / 60f); host.Tick(1, 1f / 60f); if (t == 0 || t == 9) Console.WriteLine(Dump("right+motion tick " + t)); }
                stage.OnMouseButton(1, false);
                PrepareStep(w, h); stage.RunFrame(isink, 1f / 60f); host.Tick(1, 1f / 60f);
                Console.WriteLine(Dump("released"));
                for (int t = 0; t < 20; t++) { PrepareStep(w, h); stage.RunFrame(isink, 1f / 60f); host.Tick(1, 1f / 60f); if (t % 5 == 4) Console.WriteLine(Dump("idle tick " + t)); }
                return 0;
            }
            int iceIdx = Array.IndexOf(args, "--ice-png");   // ice probe: --ice-png <texture path> <out.png>, frames 0, 30, 60 (step 1/60 s) in a row
            if (iceIdx >= 0)
            {
                var d = IceDefs[args[iceIdx + 1]];
                var ice = new IceTexture(d.USize, d.VSize, d.SourceP8, d.GlassP8, d.PanningStyle, d.Frequency, d.Amplitude, d.HorizPanSpeed,
                    d.VertPanSpeed, d.UPosition, d.VPosition, d.MasterCount, d.MoveIce, d.TimeMethod, d.MinFrameRate, d.MaxFrameRate, (byte)d.PrimeCount);
                var strip = new byte[d.USize * 3 * d.VSize * 4]; var one = new byte[d.USize * d.VSize * 4];
                int col = 0;
                for (int fr = 0; fr <= 60; fr++)
                {
                    ice.Update(fr / 60.0, fr);
                    if (fr % 30 != 0) continue;
                    ice.WriteRgba(d.SourcePalette, one);
                    for (int y = 0; y < d.VSize; y++) Buffer.BlockCopy(one, y * d.USize * 4, strip, (y * d.USize * 3 + col * d.USize) * 4, d.USize * 4);
                    col++;
                }
                Png.Write(args[iceIdx + 2], d.USize * 3, d.VSize, strip);
                Console.WriteLine($"ice {d.Path} {d.USize}x{d.VSize} MoveIce={d.MoveIce} pan={d.PanningStyle} hs={d.HorizPanSpeed} vs={d.VertPanSpeed} freq={d.Frequency} amp={d.Amplitude} updates={ice.Updates} blits={ice.Blits}");
                return 0;
            }
            int apIdx = Array.IndexOf(args, "--audio-probe");
            if (apIdx >= 0) return GameAudio.Probe(host, root, int.Parse(args[apIdx + 1]));
            if (args.Contains("--weapon"))
            {
                host.Tick(200, 0.02f);
                foreach (UEObject o in host.Runtime.Actors)
                    if (o?.GetProp("Weapon") is UEObject wpn)
                        Console.WriteLine($"pawn {o.GetProp("Name")} weapon {wpn.GetProp("Name")} class={wpn.Class?.Name} mesh={wpn.GetProp("Mesh")} 3rd={wpn.GetProp("ThirdPersonMesh")} 3rdScale={wpn.GetProp("ThirdPersonScale")} hidden={wpn.GetProp("bHidden")} loc={wpn.GetProp("Location")} pawnWeaponLoc={o.GetProp("WeaponLoc")}");
                Console.WriteLine($"WeaponThirdPersonDraws={host.Runtime.WeaponThirdPersonDraws}");
                return 0;
            }
            int mlmIdx = Array.IndexOf(args, "--mover-lmatlas");
            if (mlmIdx >= 0) { host.RunOwedLoadFrame(); return MoverLmAtlas(host, args[mlmIdx + 1], int.Parse(args[mlmIdx + 2])); }


            // Key character as Windows VK (SdlKeyMap.LayoutScancode): the French digit row yields digits, non-Latin
            // letters yield Latin by US position. This is the SDL3 default; set explicitly so as not to depend on it.
            SDL_SetHint(SDL_HINT_KEYCODE_OPTIONS, "french_numbers,latin_letters");
            // An offscreen capture (--shot) must not keep the display awake; the game window still disables the screensaver.
            if (shot is not null) SDL_SetHint("SDL_VIDEO_ALLOW_SCREENSAVER", "1");
            if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO)) { Console.Error.WriteLine(SDL_GetError()); return 2; }
            var r = new Renderer { Anisotropy = host.Runtime.Config?.Anisotropy ?? 16 };
            if (!r.Create(shot is null, w, h)) { Console.Error.WriteLine("GPU: " + SDL_GetError()); return 2; }
            if (shot is null)
            {
                // The window is Core's viewport device (Resolution/Display Mode menu, SetRes, ToggleFullscreen). Startup
                // mode as UGameEngine::Init -> OpenWindow (UEViewportStartup: StartupFullscreen and the mode key pair,
                // -WINDOW for windowed); --size gives a window of the given size (debug).
                SetWindowIcon(r, root);
                var vp = new SdlViewportDevice(r.Window);
                host.Natives.UseViewportDevice(vp);
                WindowViewport = vp;
                SdlViewportDevice.OpenStartup(vp, host.Runtime.Config, Environment.CommandLine, sizeFromArgs ? (w, h) : null);
                Console.WriteLine($"[Viewport] {vp.SizeX}x{vp.SizeY}{(vp.Fullscreen ? " fullscreen" : " window")}");
            }
            Src = new UETextureSource(root);
            Hud = new OpenHP1.Hud.HudBatcher(
                // Runtime textures (CreateTextureFromBMP, SavePics package: save-slot pictures): Core registry.
                path => UERuntimeTextures.TryGet(path, out var rt) ? (rt.USize, rt.VSize)
                      : Src.Get(path) is { } td && td.Width > 0 ? (td.Width, td.Height) : null,
                // Native fonts (CreateNativeFont, RU edition): glyph page from Core's registry (runtime texture).
                key => UENativeFonts.PagePaths(key) ?? Src.GetFont(key)?.PagePaths);

            // The load frame emits static tiles of all lightmaps; the scene is built right after it.
            host.RunOwedLoadFrame();
            LoadScene(r);
            Splash.Close();   // ExitSplash after InitEngine (HP.exe 0x1090b0eb)
            // Windowless travel probe: --travel-png <dir>, the same Core carriers as the window (fade frames and the
            // loading screen), but the frame goes to PNG. Together with --travel-at <tick> <map>.
            int tpIdx = Array.IndexOf(args, "--travel-png");
            if (tpIdx >= 0)
            {
                string tdir = args[tpIdx + 1]; Directory.CreateDirectory(tdir);
                int fadeN = 0, screenN = 0;
                host.Runtime.PresentFadeFrameNow = ff =>
                {
                    FrameDraws(r, ff);
                    var c = Camera.From(ff.Camera);
                    r.RenderOffscreen(c, out byte[] px);
                    if (fadeN % 4 == 0) Png.Write(Path.Combine(tdir, $"fade_{fadeN:D2}.png"), r.Width, r.Height, px);
                    fadeN++;
                };
                host.Runtime.PresentLoadMapScreenNow = scr =>
                {
                    var quads = Hud.Build(scr, r.Width, r.Height, host.Runtime.FrameProducer?.LevelInfoOffsetX ?? 0);
                    r.SetHud(quads);
                    r.HideWorld = true;
                    r.RenderOffscreen(Camera.From(host.Runtime.LatestFrame.Camera), out byte[] px);
                    r.HideWorld = false;
                    Png.Write(Path.Combine(tdir, $"screen_{screenN++}.png"), r.Width, r.Height, px);
                    Console.WriteLine($"travel-png: {ParchmentProbe.Measure(Src, quads, px, r.Width, r.GammaExponent)}");
                    Console.WriteLine($"travel-png: loading screen, HUD commands {scr.Hud.Count}, fade frames before it {fadeN}");
                };
            }
            sw.Restart();
            // Window audio comes up BEFORE warm-up and receives each of its frames, as Galaxy from the first engine frame
            // (sound commands live one frame; music fades over frames).
            GameAudio audio = video is null && shot is null ? new GameAudio(host, root) : null;
            // --look-at <actor>: snapshot camera at LookDist from the actor (along its yaw, raised by a third), looking at
            // it; the same substitution as --cam.
            if (shot is not null && lookAt is not null)
                foreach (ActorSnapshot a in host.Runtime.LatestFrame.Actors)
                    if (string.Equals(a.Name, lookAt, StringComparison.OrdinalIgnoreCase))
                    {
                        float yaw = a.Rotation.Yaw * MathF.PI / 32768f + MathF.PI * 0.75f;
                        float cx = a.Location.x + LookDist * MathF.Cos(yaw), cy = a.Location.y + LookDist * MathF.Sin(yaw), cz = a.Location.z + LookDist / 3f;
                        float dx = a.Location.x - cx, dy = a.Location.y - cy, dz = a.Location.z - cz;
                        CamOverride = new[] { cx, cy, cz, MathF.Atan2(dz, MathF.Sqrt(dx * dx + dy * dy)) * 180f / MathF.PI, MathF.Atan2(dy, dx) * 180f / MathF.PI };
                        Console.WriteLine($"look-at {a.Name} {a.Class} @ {a.Location}");
                        break;
                    }
            // Snapshot with --cam: Core substitutes the render camera (like the original probe hp1drawlog), otherwise the
            // occlusion child frames (sky, mirrors, warp) would be built for the game camera and drawn for the snapshot
            // camera. With --probe-settle N the substitution starts N ticks before the snapshot: the original probe visits
            // points in turn and holds each point's camera for settle frames (hp1drawlog.c); before that the view is the game camera.
            (UEVector, UERotator)? probe = shot is not null && CamOverride is { Length: 5 } pc
                ? (new UEVector(pc[0], pc[1], pc[2]), new UERotator((int)MathF.Round(pc[3] * 65536f / 360f), (int)MathF.Round(pc[4] * 65536f / 360f), 0))
                : null;
            for (int i = 0; i < warm; i++)
            {
                if (probe is not null && i == Math.Max(0, warm - (probeSettle > 0 ? probeSettle : warm)) && host.Runtime.FrameProducer is { } fp)
                    fp.ProbeCamera = probe;
                if (DrawLog.Path != null && i == 1) DrawLog.MarkFirst(host.Runtime.LatestFrame);
                if (DrawLog.Path != null && i == Math.Max(0, warm - DrawLog.StillTicks)) DrawLog.MarkState(host.Runtime.LatestFrame);
                PrepareStep(w, h); var scene = Cur; host.Tick(1, WarmDt); AfterTick(r);
                audio?.Update(!ReferenceEquals(scene, Cur));
            }
            if (probe is not null && host.Runtime.FrameProducer is { ProbeCamera: null } fp0) fp0.ProbeCamera = probe;   // --warm 0
            Console.WriteLine($"core warm {warm} ticks {sw.ElapsedMilliseconds} ms");
            foreach (string cmd in consoleCmds)
                Console.WriteLine($"console '{cmd}' -> '{UEConsoleCommand.Execute(host.Runtime.Config, cmd)}'");
            foreach (string ev in causeEvents)
                Console.WriteLine($"causeevent {ev} -> {host.CauseEventProbe(ev)} actor(s)");
            var keySink = keys.Count > 0 ? new OpenHP1.Input.CoreInputSink(host.Interp, host.Runtime) { Warn = Console.Error.WriteLine } : null;
            for (int i = 0; i < post; i++)
            {
                // Death probe between ticks through Core (UEGameEngine.KillPlayerProbe; the console has no route to KillHarry).
                if (kill == i && host.KillPlayerProbe() is string victim) Console.WriteLine($"kill {victim}@{i}");
                // A load or travel in --post raises a new frame producer: the probe camera stays the snapshot camera
                // (as hp1drawlog keeps substituting it across the original's load).
                if (probe is not null && host.Runtime.FrameProducer is { ProbeCamera: null } fpp) fpp.ProbeCamera = probe;
                foreach (var (ct, cmd) in consoleAt)
                    if (ct == i) Console.WriteLine($"console@{i} '{cmd}' -> '{UEConsoleCommand.Execute(host.Runtime.Config, cmd)}'");
                foreach (var (kt, key) in keys)
                    if (kt == i || kt + 1 == i) keySink.CauseInput(key, kt == i ? UEInput.InputAction.Press : UEInput.InputAction.Release, 0f);
                PrepareStep(w, h); host.Tick(1, WarmDt); AfterTick(r);
                foreach (var (kt, key) in keys)
                    if (kt == i) Console.WriteLine($"key {key}@{i} -> uwindow={keySink.UWindowActive}");
            }

            if (video is not null) return Video(host, r, video, videoFrames);
            if (shot is not null) return Shot(host, r, shot, bench);
            return Window(host, r, audio);
        }

        // ── LEVEL SCENE ── everything built from the Core world; rebuilt on travel.
        sealed class Scene
        {
            public WorldStatic World;
            public WorldMesh Mesh;
            public LightmapAtlas Atlas;
            public ActorBuilder Actors;
            // Frame non-meshes (openhp1/Sprites): sprites, particles, shadows/marks, coronas.
            public OpenHP1.Sprites.SpriteBuilder Sprites;
            public OpenHP1.Sprites.ParticleBuilder Particles;
            public OpenHP1.Sprites.DecalBuilder Decals;
            public OpenHP1.Sprites.CoronaBuilder Coronas;
            public int[] SurfGroup;   // frame group of a level surface (WorldMesh.SurfGroup), for decals
        }
        static Scene Cur;
        static UETextureMetrics Metrics;
        static UETextureSource Src;
        public static int SceneLoads;

        static void LoadScene(Renderer r)
        {
            Host.Runtime.LoadProgress?.Invoke();   // D-17: the scene is built right after the level loads
            var sw = Stopwatch.StartNew();
            UELevelRuntime rt = Host.Runtime;
            WorldStatic world = rt.LatestFrameWorld;
            StaticGeometry g = world.Geometry;
            WorldFrame frame = rt.LatestFrame;
            var noMetrics = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            WorldMesh.TextureInfo texInfo = (string mat, out float tw, out float th, out int pf) =>
                { bool ok = Metrics.TryGet(mat, out tw, out th, out float ds, out pf); tw *= ds; th *= ds; if (!ok) noMetrics.Add(mat); return ok; };
            var atlas = new LightmapAtlas(g, nearBack: true);
            int tiles = 0;
            if (rt.SurfaceLightmaps is { } slm) foreach (var t in slm.Tiles) if (atlas.Write(t, out _, out _, out _, out _)) tiles++;
            var pan = Sky.PanSpeeds(g, frame);
            string defaultTexture = (Host.Interp.GetLevelInfo()?.GetProp("DefaultTexture") as UEResourceRef)?.ObjectPath;
            Movers = new MoverScene(rt, frame, texInfo, pan, defaultTexture);
            // Unhandled accounting (RenderUnhandled): mover faces do not animate the AnimNext chain (BSP via AnimateWorld,
            // particles/sprites/decals/skins via the builders' FrameOf).
            foreach (Batch mb in Movers.Mesh.Batches)
                if (Anim.IsAnimated(mb.Material)) RenderUnhandled.Note("mover", "AnimNext", mb.Material);
            int moverTiles = 0;
            foreach (var t in rt.MoverLightmaps.Tiles) if (Movers.Atlas.Write(t, out _, out _, out _, out _)) moverTiles++;
            LevelFogMaps fogMaps = rt.FogMaps;
            HashSet<int> fogZones = LevelFogMaps.Zones(g, frame);
            HashSet<int> warpZones = Warp.ZoneSet(g, frame);
            WorldMesh mesh = WorldMesh.Build(g, texInfo, atlas.Place, LightmapAtlas.Width, atlas.Height, coplanarPieces: Host.Runtime.LevelCoplanarPieces, coplanarBackPieces: Host.Runtime.LevelCoplanarBackPieces, coplanarNearBackPieces: Host.Runtime.LevelCoplanarNearBackPieces, zonePan: pan, fogOf: (node, zone, pf) => fogMaps?.FogRecord(node, zone, pf) ?? -1, defaultTexture: defaultTexture, warpZones: warpZones, backZones: true);
            Fog = new FogScene(mesh);   // BEFORE upload: rewrites vertex UV3 into the fog atlas
            r.Proc ??= Proc; r.IceDefs ??= IceDefs;
            r.Upload(mesh, atlas, Src);
            r.CreateFogAtlas(Fog);
            r.CreateMoverAtlas(Movers.Atlas);
            Cur = new Scene { World = world, Mesh = mesh, Atlas = atlas, Actors = new ActorBuilder(rt), SurfGroup = mesh.SurfGroup };
            // Sprite providers (Sprites/README.md): size and bMasked are Core metrics, IceTexture is the metrics row
            // class, the AnimNext chain is UETextureAnim, Default.LifeSpan is the Core class default, visibility uses the
            // same gate as meshes.
            OpenHP1.Sprites.TextureSizeFn size = (string path, out int u, out int v) =>
            {
                u = v = 0;
                if (!Metrics.TryGet(path, out float fu, out float fv, out _, out _) && !(fu > 0 && fv > 0)) return false;
                u = (int)fu; v = (int)fv; return u > 0 && v > 0;
            };
            Func<string, int> texFlags = path => { Metrics.TryGet(path, out _, out _, out _, out int tpf); return tpf; };
            Func<string, bool> isIce = path => Metrics.Find(path)?.Class == "IceTexture";
            Func<string, float> lifeSpan = cls =>
            {
                var c = HP1.Core.UEClassRegistry.Shared?.Find(cls);
                return c is null ? 0f : UEInterpreter.AsFloat(Host.Interp.GetClassDefault(c, "LifeSpan"));
            };
            Cur.Sprites = new OpenHP1.Sprites.SpriteBuilder(size, texFlags, isIce, p => Anim.ChainLength(p), (p, k) => Anim.ChainAt(p, k), lifeSpan);
            Cur.Particles = new OpenHP1.Sprites.ParticleBuilder(size, texFlags);
            Cur.Decals = new OpenHP1.Sprites.DecalBuilder(size);
            // AnimNext chain frame: UETextureAnim.CurrentFrame (clock via BeginFrame in AnimateWorld, before the builders).
            Cur.Sprites.FrameOf = Cur.Particles.FrameOf = Cur.Decals.FrameOf = Cur.Actors.FrameOf = Anim.CurrentFrame;
            // Coronas: Core decides (UECoronas -> WorldFrame.Coronas); the builder only projects and emits tiles.
            Cur.Coronas = new OpenHP1.Sprites.CoronaBuilder();
            SceneLoads++;
            Console.WriteLine($"scene #{SceneLoads} {Path.GetFileNameWithoutExtension(rt.LoadedMapPath)}: nodes {mesh.Nodes}, batches {mesh.Batches.Count}, "
                + $"atlas {LightmapAtlas.Width}x{atlas.Height} (tiles {tiles}), mirror polygons {mesh.SkippedMirror} (planes {mesh.MirrorPlanes.Count}), portals as surfaces {mesh.PortalsAsSurface}, "
                + $"without metrics {mesh.SkippedNoMetrics}; fog: zones {fogZones.Count}, surfaces {Fog.Surfaces.Count}, atlas {FogScene.W}x{Fog.H}; movers {Movers.Geo.Movers.Count} (tiles {moverTiles}, refusals {Movers.Geo.Problems.Count}, mirror planes {Movers.Mesh.MirrorPlanes.Count}); "
                + $"textures {r.TextureCount} (no pixels {r.MissingTextures.Count}); {sw.ElapsedMilliseconds} ms");
            foreach (string pr in Movers.Geo.Problems) Console.WriteLine("  mover: " + pr);
            foreach (var (mp, mn) in mesh.MirrorPlanes) Console.WriteLine($"  mirror: point {mp} normal {mn}");
            foreach (WarpPortal wp in mesh.WarpPortals) Console.WriteLine($"  warp portal: surf {wp.Surf} zones {wp.Zone0}/{wp.Zone1} plane {wp.Normal}·p={wp.Dist} (level warp zones {warpZones.Count})");
            if (mesh.WarpVisiblePortals > 0) Console.Error.WriteLine($"[Warp] STOP: visible warp-zone portals {mesh.WarpVisiblePortals} — the view through them is not ported (drawn as a surface)");
            if (noMetrics.Count > 0) Console.WriteLine($"  no metrics for {noMetrics.Count}: {string.Join(", ", noMetrics.Take(8))}");
            foreach (string m in r.MissingTextures.Take(10)) Console.WriteLine("  no pixels: " + m);
        }

        // Before the Core step: the screen size to Core (it picks the interface scale and the canvas, D-44), 4:3 menu
        // region, Hor+ (author decision).
        static void PrepareStep(int screenW, int screenH, int consoleRegion = 0)
        {
            var fp = Host.Runtime.FrameProducer;
            if (fp is null) return;
            fp.SetScreen(screenW, screenH);
            fp.SetConsoleRegion(consoleRegion / fp.CanvasScale);
            fp.SetWideScreen(horPlus: true, levelInfo43: true);
        }

        // The OS cursor in the canvas pixels PrepareStep gives Core (window points -> pixels -> / scale, minus the menu region).
        static unsafe (float X, float Y) CanvasCursor(Renderer r)
        {
            float x, y;
            SDL_GetMouseState(&x, &y);
            float d = SDL_GetWindowPixelDensity(r.Window);
            if (!(d > 0)) d = 1f;
            int scale = Host.Runtime.FrameProducer?.CanvasScale ?? 1;
            return (x * d / scale - (Host.Runtime.FrameProducer?.ConsoleRegionOffsetX ?? 0), y * d / scale);
        }

        // After the Core tick: world change (travel) rebuilds the scene; otherwise frame lightmap tiles go to the GPU.
        static void AfterTick(Renderer r)
        {
            UELevelRuntime rt = Host.Runtime;
            // The scene follows the world of Core's latest frame (UELevelRuntime.LatestFrameWorld): the held loading screen
            // of a browsing tick carries none, so the old scene stays (unused) until the new world's first frame.
            if (rt.LatestFrameWorld is null) return;
            if (!ReferenceEquals(rt.LatestFrameWorld, Cur.World) && rt.LatestFrameWorld.Geometry is not null) { LoadScene(r); return; }
            WorldFrame f = rt.LatestFrame;
            r.UpdateTiles(Cur.Atlas, rt.SurfaceLightmaps);
            if (rt.MoverLightmaps is { } ml) r.UpdateTiles(Movers.Atlas, ml.Tiles, moverAtlas: true);
        }

        static MoverScene Movers;
        static readonly List<OpenHP1.Hud.HudQuad> _hud2 = new();
        static readonly List<OpenHP1.Hud.HudQuad> _coronaQuads = new();
        static float LastRealDt = 1f / 60f;
        static readonly List<ActorDraw> _spritePool = new();

        static ActorDraw SpriteToDraw(OpenHP1.Sprites.SpriteDraw s, int i)
        {
            while (_spritePool.Count <= i) _spritePool.Add(new ActorDraw());
            ActorDraw d = _spritePool[i];
            d.Vertices.Clear(); d.Indices.Clear();
            d.ActorId = s.ActorId; d.Texture = s.Texture; d.Blend = (ActorBlend)(int)s.Blend; d.Unlit = false; d.WorldMode = -1; d.Lightmapped = false; d.MoverAtlas = false; d.UvOffU = d.UvOffV = 0;
            // Render locks particles with LOD 0 (0x10b1514a); sprites, coronas, decals with -1 (UCanvas::DrawTile, decal 0x10b2643d).
            d.Lod = TextureDetail.OfSprite(s.Kind);
            d.NoSmooth = s.NoSmooth; d.AlphaTest = s.Blend == OpenHP1.Sprites.SpriteBlend.Masked; d.FrameView = s.FrameView;
            // Sprites, particles and decals carry the frame that drew them (FrameView, one batch per frame);
            // coronas are 2D.
            d.LogKind = s.Kind switch { OpenHP1.Sprites.SpriteKind.Particle => "PARTICLE", OpenHP1.Sprites.SpriteKind.Decal => "DECAL", OpenHP1.Sprites.SpriteKind.Corona => "CORONA", _ => "SPRITE" };
            d.LogOwner = DrawLog.Path != null ? Host.Runtime.FindActorById(s.ActorId)?.GetProp("Name")?.ToString() : null;
            d.Group = s.Kind != OpenHP1.Sprites.SpriteKind.Decal ? RenderRules.ActorGroup(s.LatePass)
                : RenderRules.DecalGroup(s.Surf, Cur.SurfGroup, s.SurfPolyFlags, s.SurfTexturePolyFlags);
            foreach (var v in s.Vertices) d.Vertices.Add(new Vertex { X = v.X, Y = v.Y, Z = v.Z, U = v.U, V = v.V, R = v.R, G = v.G, B = v.B, A = 1f });
            foreach (int k in s.Indices) d.Indices.Add((uint)k);
            return d;
        }
        static FogScene Fog;
        static UEGameEngine Host;
        static UETextureAnim Anim;
        static OpenHP1.ProcTex.ProcTextures Proc;
        static Dictionary<string, UEProceduralTextureData.Ice> IceDefs;
        static int RenderFrame;
        static float WarmDt = 0.02f;
        static double TargetFps = 60;
        // Monitor rate of the window (0 = unknown: render at the logic rate).
        // Windowed game log (author decision): all console output is duplicated into
        // <install>/OpenHP1Data/logs/run_<date>_<time>.log; survives rebuilds, independent of the launch method.
        // The last 20 logs are kept.
        static void StartRunLog(string dir)
        {
            Directory.CreateDirectory(dir);
            foreach (var old in new DirectoryInfo(dir).GetFiles("run_*.log").OrderByDescending(f => f.Name).Skip(19)) old.Delete();
            var file = new StreamWriter(Path.Combine(dir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.log")) { AutoFlush = true };
            Console.SetOut(TextWriter.Synchronized(new Tee(Console.Out, file)));
            Console.SetError(TextWriter.Synchronized(new Tee(Console.Error, file)));
            // The runtime prints an unhandled exception to fd 2 directly, past Console.Error: without this the journal of a
            // crashed run just stops (author's crash 2026-11-09, Lev_Tut3 mirror room).
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine($"[Crash] unhandled exception: {e.ExceptionObject}");
            Console.WriteLine($"[Log] journal: {((FileStream)file.BaseStream).Name}");
        }

        // The package reader library (UELib) prints its own read diagnostics for every package and object: headers,
        // "Trailing data", "PropertyTag value size error", the ThaiFontMed font it cannot read. Known and harmless
        // (~80% of a playthrough log); they stay on the console and are left out of the log file.
        static readonly string[] LibraryNoise =
        {
            "PropertyTag value size error", "Trailing data for object", "Package Version:", "Build:", "Branch:UELib",
            "Package Flags:", "Names Count:", "GUID:", "Generations Count:", "UELib.DeserializationException", " ---> System.IO.EndOfStreamException",
        };
        static bool IsLibraryNoise(string s) => s is not null && Array.Exists(LibraryNoise, p => s.StartsWith(p, StringComparison.Ordinal));

        sealed class Tee(TextWriter a, TextWriter b) : TextWriter
        {
            public override System.Text.Encoding Encoding => a.Encoding;
            public override void Write(char c) { a.Write(c); b.Write(c); }
            public override void Write(string s) { a.Write(s); b.Write(s); }
            public override void WriteLine(string s) { a.WriteLine(s); if (!IsLibraryNoise(s)) b.WriteLine(s); }
            public override void Flush() { a.Flush(); b.Flush(); }
        }

        static double DisplayHz(Renderer r)
        {
            SDL_DisplayMode* m = SDL_GetCurrentDisplayMode(SDL_GetDisplayForWindow(r.Window));
            return m != null && m->refresh_rate > 0 ? m->refresh_rate : 0;
        }
        static bool FpsFromArgs;
        const double MinLoadScreenSeconds = 2.0;
        static readonly Stopwatch Wall = Stopwatch.StartNew();
        static OpenHP1.Hud.HudBatcher Hud;   // animated texture frames (Core, UTexture::Tick)
        static float LookDist = 300f;
        static float[] CamOverride;
        static bool DebugCam;   // --cam x,y,z,pitch,yaw (degrees)
        static bool PerfTitle;
        static readonly List<ActorDraw> _dyn = new(), _built = new();

        // Texture Detail menu item (`set ini:Engine.Engine.ViewportManager TextureDetail <name>`, FEOptionsPage.uc:583):
        // Core's config holds the value, parsing and formula are HP1.Core UETextureLod; polled every frame (the port needs
        // no Flush: the LOD is part of the cache key, like LOD in Lock's CacheID). An unrecognised value shipped does not
        // accept — the level stays, loudly.
        static readonly HashSet<string> _detailRejected = new(StringComparer.OrdinalIgnoreCase);
        static void ApplyTextureDetail(Renderer r)
        {
            UEConfig cfg = Host.Runtime.Config;
            if (cfg is null) return;
            string td = cfg.TextureDetail, sd = cfg.SkinDetail;
            int t = UETextureLod.Resolve(td, r.TextureDetailLevel, out bool badT);
            int s = UETextureLod.Resolve(sd, r.SkinDetailLevel, out bool badS);
            if (badT && _detailRejected.Add("T:" + td)) Console.Error.WriteLine($"[Texture] TextureDetail '{td}' is not a name of enum Details (UByteProperty::ImportText refuses it); level unchanged");
            if (badS && _detailRejected.Add("S:" + sd)) Console.Error.WriteLine($"[Texture] SkinDetail '{sd}' is not a name of enum Details; level unchanged");
            if (t != r.TextureDetailLevel || s != r.SkinDetailLevel)
            {
                r.ApplyTextureDetail(t, s);
                Console.WriteLine($"[Texture] TextureDetail={UETextureLod.DetailNames[t]} SkinDetail={UETextureLod.DetailNames[s]}");
            }
        }

        // Frame dynamic batches: actors + mover surfaces (their lightmap tiles go into the mover atlas).
        // alpha/levelTime: frame between logic ticks (FramePacing.cs); by default the current tick.
        // full = false — a frame between logic ticks: visibility, light and decals stay the tick's; sprite tiles follow the frame camera.
        static void FrameDraws(Renderer r, WorldFrame f, float alpha = 1f, float? levelTime = null, bool full = true)
        {
            float lt = levelTime ?? Host.Runtime.PresentedLevelTime;
            ApplyTextureDetail(r);
            r.EnhancedVisuals = f.EnhancedVisuals;   // Original / Enhanced world sampler — Core's decision for this frame
            ActorBuilder actors = Cur.Actors;
            actors.Alpha = alpha;
            Fog.Fill(Host.Runtime.FogMaps); r.UploadFog(Fog);   // Core's fog maps of the tick, per frame of the tree
            r.SetDrawn(f.Drawn, f.DrawnTextures);   // Core's world polygons per view and the textures DrawFrame updated
            r.AnimateWorld(lt, Anim, Wall.Elapsed.TotalSeconds);
            if (full)
            {
                actors.Build(f, Wall.Elapsed.TotalSeconds);
            }
            else actors.Reemit(alpha);
            Movers.Build(f, lt, f.Tick, alpha);
            // Core occlusion child frames (WorldFrame.Views): a mirror batch is a level or mover plane.
            WorldMesh levelMesh = Cur.Mesh; MoverScene movers = Movers;
            r.SetViews(f.Views, src => src.Surf >= 0
                ? (levelMesh.MirrorPlaneOfSurf.TryGetValue(src.Surf, out int lp) ? lp : -1)
                : movers.MirrorPlaneOf(src.MoverId, src.MoverPoly) is int mp and >= 0 ? levelMesh.MirrorPlanes.Count + mp : -1);
            // 2D frame: coronas, flash (ScreenEffects.FlashQuad), HUD/menu/sides (Hud.Build), ScreenEffects.Overlay.
            var (pw, ph) = r.PixelSize();
            var quads = Hud.Build(f, pw, ph, Host.Runtime.FrameProducer?.ConsoleRegionOffsetX ?? 0);
            // Coronas are screen quads after the world and before the HUD, Translucent without depth (DrawIcon, PolyFlags 0x104).
            Cur.Coronas.Build(f, pw, ph, alpha < 1f ? f.Camera : null);   // between ticks: tiles at the frame's lerped master camera
            _coronaQuads.Clear();
            foreach (var cd in Cur.Coronas.Draws)
            {
                var v0 = cd.Vertices[0]; var v2 = cd.Vertices[2];
                _coronaQuads.Add(OpenHP1.Hud.ScreenEffects.CoronaQuad(v0.X, v0.Y, v2.X, v2.Y, v0.U, v0.V, v2.U, v2.V,
                    v0.R, v0.G, v0.B, cd.Texture, cd.NoSmooth));
            }
            OpenHP1.Hud.ScreenEffects.Overlay(_hud2, _coronaQuads, OpenHP1.Hud.ScreenEffects.FlashQuad(f.ViewFlash, pw, ph), quads);
            r.SetHud(_hud2);
            // Menu brightness (UClient.Brightness, FEOptionsPage.BrightnessChanged): D3DDrv gamma ramp over the whole frame.
            r.GammaExponent = OpenHP1.Hud.ScreenEffects.GammaExponent(Host.Runtime.Config.Brightness);
            _built.Clear(); foreach (var d in actors.Draws) if (d.Indices.Count > 0) _built.Add(d);
            _built.AddRange(Movers.Draws);
            // Sprites, particles, shadows use the actor path (texel × vertex colour).
            // Sprite tiles face the camera of the frame being drawn (DrawActorSprite uses Frame->Coords every frame); the
            // set of sprites stays the tick's (WorldFrame.SpriteActors). Decal pieces are world-space: built per tick.
            Cur.Sprites.Build(f);
            if (full) Cur.Decals.Build(f);
            Cur.Particles.Build(f);
            int sd = 0;
            foreach (var list in new[] { Cur.Sprites.Draws, Cur.Particles.Draws, Cur.Decals.Draws })
                foreach (var sdraw in list) _built.Add(SpriteToDraw(sdraw, sd++));
            // Actors in Core's DrawActorSprite order (WorldFrame.DrawOrder); the renderer draws the list as given.
            OpenHP1.Sprites.SpriteMath.InCoreOrder(_built, f, d => RenderRules.IsActorGroup(d.Group),
                d => d.ActorId, d => d.FrameView, _dyn);
            r.SetActors(_dyn);
            r.TickProc(++RenderFrame, Wall.Elapsed.TotalSeconds);
            foreach (string pr in Proc.Problems.Skip(_procReported)) Console.WriteLine(pr);
            _procReported = Proc.Problems.Count;
        }
        static int _procReported;

        static int Shot(UEGameEngine host, Renderer r, string path, int bench)
        {
            WorldFrame f = host.Runtime.LatestFrame;
            if (!f.Camera.Valid) { Console.Error.WriteLine("no camera in the frame"); return 3; }
            // Windowless interpolation check (FramePacing.cs): HP1_SHOT_ALPHA=<α> runs one more logic tick and a frame at
            // fraction α between it and the previous tick (α = 1 is a regular snapshot of the next tick).
            float shotAlpha = 1f; float? shotLt = null; bool shotFull = true;
            if (float.TryParse(Environment.GetEnvironmentVariable("HP1_SHOT_ALPHA"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float sa))
            {
                FrameDraws(r, f);   // last tick's poses go into the history
                WorldFrame prev = f; float prevLt = host.Runtime.PresentedLevelTime;
                host.Tick(1, 1f / 60f); AfterTick(r);
                // As the window: the first frame after a tick is full (α ≈ 0), the following ones light (positions from interpolation).
                WorldFrame f0 = Interp.Frame(prev, host.Runtime.LatestFrame, 0f, 1f / 60f);
                FrameDraws(r, f0, 0f, prevLt);
                shotAlpha = sa; shotFull = false;
                shotLt = Interp.Lerp(prevLt, host.Runtime.PresentedLevelTime, sa);
                f = Interp.Frame(prev, host.Runtime.LatestFrame, sa, 1f / 60f);
            }
            FrameDraws(r, f, shotAlpha, shotLt, shotFull);
            Console.WriteLine("actors: " + Cur.Actors.Stats);
            Console.WriteLine($"fatness: actors displaced per frame {Cur.Actors.FatActors}, in the frame with Fatness != 128: {string.Join(", ", f.Actors.Where(x => !x.Destroyed && x.Fatness != 128 && x.DrawType == 2).Select(x => x.Name + "=" + x.Fatness).Take(10))}");
            foreach (string pr in Cur.Actors.Stats.Problems.Take(8)) Console.WriteLine("  " + pr);
            Console.WriteLine($"  textures total {r.TextureCount}, without pixels {r.MissingTextures.Count}");
            foreach (string m in r.MissingTextures) Console.WriteLine("  no pixels: " + m);
            var cam = Camera.From(f.Camera);
            if (CamOverride is { Length: 5 } co)
            {
                // HP1_FOGDUMP=<file>: Core's fog-map bytes of the master frame per lightmap (built for the snapshot camera: Core
                // occlusion runs at the --cam probe camera) for comparison with the original probe's FOGDATA.
                var cso = new CameraSnapshot(true, f.Camera.ViewActorId, new UEVector(co[0], co[1], co[2]),
                    new UERotator((int)MathF.Round(co[3] * 65536f / 360f), (int)MathF.Round(co[4] * 65536f / 360f), 0), f.Camera.FovAngle, f.Camera.BehindView);
                cam = Camera.From(cso);
                string fogDump = Environment.GetEnvironmentVariable("HP1_FOGDUMP");
                if (fogDump is { Length: > 0 } && Host.Runtime.FogMaps is { Tiles.Count: > 0 } fdm)
                {
                    // Per lightmap the tile of its first record with a fog map in the walk (the original journals FOGDATA
                    // once per iLightMap); `fogrec <lm> <polygons>` for every such record of the master frame (the merge
                    // rule, judged against the journal's SURF records by tools/origprobe/compare_fog_records.py).
                    var dt = new Dictionary<int, byte[]>();
                    foreach (int key in fdm.RecordPolys[0].Keys) dt.TryAdd(fdm.Records[key].Of.LightMap, fdm.Tiles[0][key]);
                    using var fw = new StreamWriter(fogDump) { NewLine = "\n" };
                    foreach (var kv in dt.OrderBy(k => k.Key))
                    {
                        LevelFogMaps.Surf fs = fdm.ByLightMap[kv.Key];
                        fw.WriteLine($"fog {kv.Key} {fs.UClamp} {fs.VClamp}");
                        fw.WriteLine(string.Concat(Enumerable.Range(0, kv.Value.Length / 4).Select(i => BitConverter.ToUInt32(kv.Value, i * 4).ToString("x8"))));
                    }
                    foreach (var kv in fdm.RecordPolys[0]) fw.WriteLine($"fogrec {fdm.Records[kv.Key].Of.LightMap} {kv.Value}");
                    int other = fdm.RecordPolys[0].Keys.Count(k => !fdm.Tiles[0][k].AsSpan().SequenceEqual(dt[fdm.Records[k].Of.LightMap]));
                    Console.WriteLine($"fogdump: tiles {dt.Count}, records {fdm.RecordPolys[0].Count} ({other} with bytes other than their lightmap's first record), sources {fdm.LastLights} -> {fogDump}");
                }
            }
            DrawLog.Begin(path);
            DrawLog.Camera(cam.X, cam.Y, cam.Z, cam.Pitch, cam.Yaw, cam.Roll, cam.Fov, r.Width, r.Height);
            DrawLog.States(f);
            // FX: emitter state read from Core (read only), the counterpart of the original hp1drawlog FX line (vtx=).
            if (DrawLog.Path != null)
                foreach (ActorSnapshot a in f.Actors)
                    if (Host.Runtime.FindActorById(a.ObjectId) is { } fx && UEInterpreter.ClassIsA(fx.Class, "ParticleFX"))
                        DrawLog.Line(FormattableString.Invariant($"FX\tname={a.Name}\talive={Host.Runtime.FrameProducer.FxStateOf(fx).Particles.Count}\tlod={UEInterpreter.AsFloat(fx.GetProp("LOD"))}\tage={UEInterpreter.AsFloat(fx.GetProp("Age"))}\telapsed={UEInterpreter.AsFloat(fx.GetProp("ElapsedTime"))}\temitted={UEInterpreter.ToInt(fx.GetProp("ParticlesEmitted"))}\tresidue={UEInterpreter.AsFloat(fx.GetProp("EmissionResidue"))}"));
            DrawLog.MeshLods(f);
            r.RenderOffscreen(cam, out byte[] rgba);
            DrawLog.Coronas(Cur.Coronas.Draws, f);
            DrawLog.End();
            Png.Write(path, r.Width, r.Height, rgba);
            Console.WriteLine($"shot -> {path}  cam {f.Camera.Location} rot {f.Camera.Rotation} fov {f.Camera.FovAngle}");
            Console.WriteLine($"shot views: mirror {r.MirrorPasses}, warp {r.WarpPasses}, sky {r.SkyPasses}, sampled {r.ViewsSampled}; Core frames "
                + string.Join(",", f.Views.Select(v => v.Kind + "@" + v.Parent)));

            // Timing: Core tick+frame, lightmaps and render with GPU wait, separately.
            double core = 0, tiles = 0, render = 0, lmMs = 0;
            long pa0 = UELevelRuntime.ProfileActorsTicks, pf0 = UELevelRuntime.ProfileFrameTicks, pp0 = UELevelRuntime.ProfileProjectTicks;
            var sw = new Stopwatch();
            // HP1_MEMLOG=N: managed heap after a full collection and resident size every N bench frames (audit/memory_growth.sh)
            int memEvery = int.TryParse(Environment.GetEnvironmentVariable("HP1_MEMLOG"), out int me) ? me : 0;
            // HP1_MEMROUTE="<frames>;<console command>;...": every <frames> bench frames the heap line (tagged with the
            // leg number), then the next command of the cycle ("open Lev_Tut1b", "open save5.usa") — travels and loads.
            string[] route = Environment.GetEnvironmentVariable("HP1_MEMROUTE")?.Split(';');
            int routeEvery = route is { Length: > 1 } ? int.Parse(route[0]) : 0, leg = 0;
            static void MemLine(string tag, int n)
            {
                long total = GC.GetTotalMemory(true); var gi = GC.GetGCMemoryInfo(GCKind.FullBlocking);
                Console.WriteLine($"{tag} {n} gc {total >> 20} MB live {(gi.HeapSizeBytes - gi.FragmentedBytes) >> 20} MB frag {gi.FragmentedBytes >> 20} MB rss {Environment.WorkingSet >> 20} MB");
            }
            for (int i = 0; i < bench; i++)
            {
                if (memEvery > 0 && i % memEvery == 0) MemLine("mem", i);
                if (routeEvery > 0 && i % routeEvery == 0)
                {
                    MemLine("memleg", leg);
                    string cmd = route[1 + leg++ % (route.Length - 1)];
                    Console.WriteLine($"memroute '{cmd}' -> '{UEConsoleCommand.Execute(host.Runtime.Config, cmd)}' scene {host.Runtime.LatestFrame.Camera.Valid}");
                }
                PrepareStep(r.Width, r.Height); sw.Restart(); host.Tick(1, 1f / 60f); core += sw.Elapsed.TotalMilliseconds;
                lmMs += host.Runtime.LastLightmapMs;
                sw.Restart(); AfterTick(r);
                FrameDraws(r, host.Runtime.LatestFrame); tiles += sw.Elapsed.TotalMilliseconds;
                // A travel's loading frames carry no camera (the window keeps the last one, line 888): nothing to render.
                sw.Restart(); if (host.Runtime.LatestFrame.Camera.Valid) r.RenderOffscreen(Camera.From(host.Runtime.LatestFrame.Camera), out _, download: false); render += sw.Elapsed.TotalMilliseconds;
            }
            Console.WriteLine($"bench {bench} frames: core {core / bench:F2} ms (of which lightmaps {lmMs / bench:F2}), "
                + $"tiles+actors {tiles / bench:F2} ms, render+GPU {render / bench:F2} ms, total {(core + tiles + render) / bench:F2} ms "
                + $"=> {1000.0 * bench / (core + tiles + render):F0} fps");
            if (UELevelRuntime.ProfileTicks && bench > 0)
            {
                double k = 1000.0 / Stopwatch.Frequency / bench;
                Console.WriteLine($"core profile per frame: actors {(UELevelRuntime.ProfileActorsTicks - pa0) * k:F2} ms, engine frame {(UELevelRuntime.ProfileFrameTicks - pf0) * k:F2} ms (of which projection {(UELevelRuntime.ProfileProjectTicks - pp0) * k:F2})");
            }
            Console.WriteLine("actors (last frame): " + Cur.Actors.Stats);
            Console.WriteLine($"sprites: sprites {Cur.Sprites.Draws.Count}, particles {Cur.Particles.Draws.Count}, shadows {Cur.Decals.Draws.Count}, coronas {Cur.Coronas.Draws.Count}; refusals: {Cur.Sprites.Report} | {Cur.Particles.Report} | {Cur.Decals.Report} | {Cur.Coronas.Report}");
            Console.WriteLine("proctex: " + Proc.Journal());
            if (Environment.GetEnvironmentVariable("HP1_PROCDBG") == "1")
                foreach (string k in r.TextureKeys)
                {
                    string cls = Metrics.Find(k.Split('|')[0])?.Class;
                    if (cls != null && cls != "Texture") Console.WriteLine($"procdbg used {k} class={cls}");
                }
            Console.WriteLine($"hud: quads {r.HudQuads}, tiles {Hud.Tiles}, texts {Hud.Texts}, missing textures {Hud.TexMiss}+{r.HudMissing}, missing fonts {Hud.FontMiss}");
            Console.WriteLine($"scenes loaded: {SceneLoads}; lightmaps with dynamic lights (+0x48): {Host.Runtime.SurfaceLightmaps?.DynamicLightmaps ?? 0}");
            int nz = 0; byte[] fa = Fog.Atlases.Count > 0 ? Fog.Atlases[0] : Array.Empty<byte>();
            for (int i = 0; i < fa.Length; i += 4) if ((fa[i] | fa[i + 1] | fa[i + 2] | fa[i + 3]) != 0) nz++;
            Console.WriteLine($"fog: sources in the frame {Host.Runtime.FogMaps?.LastLights}, tiles {Fog.Surfaces.Count}, non-zero texels {nz} (master atlas)");
            Console.WriteLine($"movers: zone changes (lightmap layer) during the measurement {Host.Runtime.MoverLightmaps?.ZoneSwitches}; world: panned batches {r.PannedBatches}, animated {r.AnimatedBatches}");
            return 0;
        }

        // Windowless recording: Core tick of 1/30 s per frame, frame by frame to PNG.
        static int Video(UEGameEngine host, Renderer r, string dir, int frames)
        {
            Directory.CreateDirectory(dir);
            for (int i = 0; i < frames; i++)
            {
                PrepareStep(r.Width, r.Height); host.Tick(1, 1f / 30f);
                WorldFrame f = host.Runtime.LatestFrame;
                if (!f.Camera.Valid) continue;
                AfterTick(r); f = host.Runtime.LatestFrame;
                var cam = Camera.From(f.Camera);
                FrameDraws(r, f);
                    r.RenderOffscreen(cam, out byte[] rgba);
                Png.Write(Path.Combine(dir, $"f{i:D4}.png"), r.Width, r.Height, rgba);
            }
            Console.WriteLine($"video: {frames} frames -> {dir}");
            return 0;
        }

        // Probe: what the brush model of each frame mover carries (nodes/surfaces/lightmaps/polygons).
        static int MoverProbe(UEGameEngine host)
        {
            WorldFrame f = host.Runtime.LatestFrame;
            int n = 0;
            foreach (ActorSnapshot a in f.Actors)
            {
                if (a.DrawType != 3) continue;   // DT_Brush
                UEObject o = host.Runtime.FindActorById(a.ObjectId);
                if (o is null) continue;
                try
                {
                    var m = host.Runtime.BrushModelOf(o);
                    StaticGeometry g = m.BuildStaticGeometry();
                    int lmSurf = g.Surfaces.Count(x => x.LightMap >= 0);
                    Console.WriteLine($"{a.Name,-22} {a.Class,-16} brush={a.Mover.BrushName} nodes={g.Nodes.Length} surfs={g.Surfaces.Length} lightmaps={g.LightMaps.Length} surfsWithLm={lmSurf} polysRef={m.ParseTables?.PolysRef} zone={a.ZoneNumber}");
                }
                catch (Exception ex) { Console.WriteLine($"{a.Name} {a.Class}: REFUSED {ex.Message}"); }
                n++;
            }
            Console.WriteLine($"brush actors in the frame: {n}");
            return 0;
        }

        // Mover lightmap tiles in --lmatlas format (for validate_lightmap_atlas.py): all nodes use zone `zone`.
        static int MoverLmAtlas(UEGameEngine host, string outPath, int zone)
        {
            UEMoverGeometry geo = host.Runtime.BuildMoverGeometry(_ => zone);
            var lm = new LevelSurfLightmapRuntime(geo.Geometry, geo.LightBits);
            lm.Update(host.Runtime.LatestFrame, host.Runtime.FrameProducer?.LightmapFx, host.Runtime.ZoneAmbientHSB);
            StaticGeometry g = geo.Geometry;
            var w = new System.Text.StringBuilder();
            w.Append("lm ").Append(g.LightMaps.Length).Append('\n');
            var hasSurf = new bool[g.LightMaps.Length];
            foreach (StaticSurface sf in g.Surfaces) if (sf.LightMap >= 0) hasSurf[sf.LightMap] = true;
            for (int i = 0; i < g.LightMaps.Length; i++)
                w.Append(g.LightMaps[i].UClamp).Append(' ').Append(g.LightMaps[i].VClamp).Append(' ').Append(hasSurf[i] ? 1 : 0).Append('\n');
            foreach (LevelSurfLightmapRuntime.Tile t in lm.Tiles)
                w.Append("tile ").Append(t.LightMap).Append(' ').Append(t.Width).Append(' ').Append(t.Height)
                 .Append(' ').Append(t.Frame ? 1 : 0).Append(' ').Append(t.Zone).Append('\n').Append(Convert.ToHexString(t.Rgb)).Append('\n');
            File.WriteAllText(outPath, w.ToString());
            Console.WriteLine($"mover-lmatlas: lightmaps {g.LightMaps.Length}, tiles {lm.Tiles.Count}, zone {zone} -> {outPath}");
            return 0;
        }

        // Window icon (on macOS also the Dock) from the installation: System/hp.ico, largest entry (packaging/GameIcon.cs).
        // Missing file or unparsed format: loud on stderr, the window stays without an icon.
        static void SetWindowIcon(Renderer r, string root)
        {
            string path = UEInstallPath.File(Path.Combine(root, "System", "hp.ico")) ?? Path.Combine(root, "System", "hp.ico");
            try
            {
                var (w, h, rgba) = OpenHP1.Packaging.GameIcon.DecodeLargest(File.ReadAllBytes(path));
                fixed (byte* px = rgba)
                {
                    SDL_Surface* s = SDL_CreateSurfaceFrom(w, h, SDL_PixelFormat.SDL_PIXELFORMAT_ABGR8888, (IntPtr)px, w * 4);
                    if (s == null || !SDL_SetWindowIcon(r.Window, s)) Console.Error.WriteLine($"[Icon] {path}: {SDL_GetError()}");
                    else Console.WriteLine($"[Icon] {path} {w}x{h}");
                    if (s != null) SDL_DestroySurface(s);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or NotSupportedException or ArgumentOutOfRangeException)
            {
                Console.Error.WriteLine($"[Icon] {path}: {e.Message} — window without an icon");
            }
        }

        static SdlViewportDevice WindowViewport;   // window device of the Core viewport (Alt+Enter)

        static int Window(UEGameEngine host, Renderer r, GameAudio audio)
        {
            int lastUnderruns = 0, lastDropped = 0;
            HP1.Core.UETrace.Playthrough = Console.WriteLine;   // cutscene starts etc. in the playthrough log
            // Input: openhp1/Input: SDL events -> InputStage -> CoreInputSink before the Core tick. Escape is a game key.
            // Free camera (F) only with --debug-cam.
            var input = new OpenHP1.Input.InputStage { Warn = Console.Error.WriteLine };
            var sink = new OpenHP1.Input.CoreInputSink(host.Interp, host.Runtime) { Warn = Console.Error.WriteLine, Viewport = WindowViewport };
            SDL_StartTextInput(r.Window);
            // Mouse capture: InputStage decides (SetMouseCapture events), the window carries it out; a mode switch re-applies.
            OpenHP1.Input.SdlMouseCapture.Init();
            input.OsCapture = captured => OpenHP1.Input.SdlMouseCapture.Apply(r.Window, captured);
            input.CursorPosition = () => CanvasCursor(r);
            if (WindowViewport is not null) WindowViewport.ModeApplied = input.ViewportChanged;
            CameraSnapshot followCam = host.Runtime.LatestFrame.Camera;
            Camera fly = Camera.From(followCam);
            var keys = new HashSet<SDL_Keycode>();
            bool follow = true, running = true, debugCam = DebugCam;
            using var audioOwner = audio;
            // Level travel (openhp1/Travel, Travel/README.md): the gate is black with 2D until the first frame of the new
            // world with a camera; LoadMap fade frames and the DrawLevelInfo loading screen are presented SYNCHRONOUSLY from
            // Core (PresentFadeFrameNow / PresentLoadMapScreenNow) while host.Tick loads the world.
            var hold = new OpenHP1.Travel.TravelFrameHold(host.Runtime.LatestFrame) { Log = Console.WriteLine };
            Camera presentCam = Camera.From(host.Runtime.LatestFrame.Camera);
            // D-17: map loading is synchronous (UGameEngine::LoadMap) and takes up to ~3 s; meanwhile the window pumps
            // the OS queue from the Core carrier, otherwise macOS/Windows declare it hung. Events stay in the SDL queue
            // and are handled after loading. 50 ms step, so the OS is not called per actor.
            double lastPump = 0;
            host.Runtime.LoadProgress = () =>
            {
                double t = Wall.Elapsed.TotalSeconds;
                if (t - lastPump < 0.05) return;
                lastPump = t;
                SDL_PumpEvents();
            };
            host.Runtime.PresentAudioNow = (channels, resumeSerial) => audioOwner?.ApplyChannels(channels, resumeSerial);
            host.Runtime.PresentFadeFrameNow = ff =>
            {
                host.Runtime.LoadProgress();
                if (ff.Camera.Valid) presentCam = Camera.From(ff.Camera);
                FrameDraws(r, ff);
                r.RenderToWindow(presentCam);
            };
            // The loading screen is held at least MinLoadScreenSeconds (author decision): a level loads in a fraction of a
            // second and the screen merely flickered. The hold is after loading; the world does not tick meanwhile.
            // The screen is built once, when presented from Core, and held without rebuilding.
            double loadShownAt = -1; WorldFrame loadScreen = null; List<OpenHP1.Hud.HudQuad> loadHud = null;
            host.Runtime.PresentLoadMapScreenNow = screen =>
            {
                if (loadShownAt < 0) loadShownAt = Wall.Elapsed.TotalSeconds;
                if (!ReferenceEquals(screen, loadScreen))
                {
                    var (sw2, sh2) = r.PixelSize();
                    // Centred 4:3 with bars, like the menu and pause (Core LevelInfo43).
                    loadHud = new(Hud.Build(screen, sw2, sh2, host.Runtime.FrameProducer?.LevelInfoOffsetX ?? 0));
                    loadScreen = screen;
                }
                r.SetHud(loadHud);
                r.HideWorld = true;
                r.RenderToWindow(presentCam);
                r.HideWorld = false;
            };
            // EXIT/QUIT console command (menu Exit button): Core calls the handler from the world step, the window closes
            // after the frame (flag, checked in the window loop).
            bool exitRequested = false;
            UEConsoleCommand.ExitHandler = () => exitRequested = true;
            var sw = Stopwatch.StartNew();
            double last = 0, lastRender = 0, acc = 0, core = 0, rend = 0; int frames = 0, ticks = 0; bool tickedSinceDraw = false;
            FramePacer pacer = null; double pacerDrawHz = -1;
            WorldFrame prevFrame = null; float prevLevelTime = 0f, logicDt = 0f;
            while (running)
            {
                // PACING (author decision, FramePacing.cs). Logic: Core tick 60 times per second (--fps overrides);
                // drawing at the monitor rate, at most FramePacer.MaxDrawHz. The original has NO limit: HP.exe MainLoop (0x10914a60)
                // sleeps only when GetMaxTickRate > 0, and in single player it is 0 (UGameEngine::GetMaxTickRate 0x103a06d0).
                // The limit fixes the original's problems on fast hardware: (1) Core, like ULevel::Tick, clamps the delta
                // from below to 0.005 s, so above 200 ticks/s the game would outrun the clock; (2) float data clocks: Tut1
                // torches have Age ≈ 199836 s, an increment dt < 1/128 is lost, so above 128 ticks/s the flame freezes.
                // Rendering runs at monitor rate: between ticks an interpolation of the previous and current tick is shown
                // (one tick of latency). Input is delivered right before the tick, as in the original (MessagePump -> UpdateWorld).
                // Logic is ALWAYS 60 Hz (author decision): the game is the same on any monitor and OS; about 45 places in
                // the bytecode count a step per tick (cameras, aiming, levitation, frogs) and behaved differently at 120.
                // Frames beyond 60 are drawn by interpolation (always on; [OpenHP1] SmoothMotion=False in the ini turns it off).
                double logicHz = FpsFromArgs ? TargetFps : 60;
                double drawHz = FramePacer.DrawHz(DisplayHz(r), Host.Runtime.Config?.SmoothMotion ?? true);
                if (pacer is null || Math.Abs(1.0 / pacer.LogicStep - logicHz) > 1e-9 || drawHz != pacerDrawHz)
                {
                    pacerDrawHz = drawHz;
                    pacer = new FramePacer(logicHz, drawHz, pacer);
                    Console.WriteLine($"[Pace] logic {logicHz:F0} Hz, drawing {1.0 / pacer.RenderStep:F0} Hz{(pacer.Interpolates ? " (interpolation)" : "")}");
                }
                double wait = pacer.SleepUntil(sw.Elapsed.TotalSeconds);
                if (wait > 0) SDL_DelayPrecise(Math.Max(100_000, (ulong)(wait * 1e9) - 500_000));
                SDL_Event e;
                while (SDL_PollEvent(&e))
                {
                    var type = (SDL_EventType)e.type;
                    if (!input.OnWindowEvent(e, sink, audio.Focus)) { running = false; break; }
                    if (debugCam && type == SDL_EventType.SDL_EVENT_KEY_DOWN && e.key.key == SDL_Keycode.SDLK_F)
                    {
                        follow = !follow;
                        if (!follow) fly = Camera.From(host.Runtime.LatestFrame.Camera);
                        continue;
                    }
                    if (debugCam && !follow)
                    {
                        if (type == SDL_EventType.SDL_EVENT_KEY_DOWN) keys.Add(e.key.key);
                        else if (type == SDL_EventType.SDL_EVENT_KEY_UP) keys.Remove(e.key.key);
                        else if (type == SDL_EventType.SDL_EVENT_MOUSE_MOTION)
                        { fly.Yaw += e.motion.xrel * 0.15f; fly.Pitch = Math.Clamp(fly.Pitch - e.motion.yrel * 0.15f, -89f, 89f); fly.HasAxes = false; }
                        continue;   // in free-camera mode the game receives no input
                    }
                    input.Feed(e);
                }
                double now = sw.Elapsed.TotalSeconds;
                bool ticked = false;
                if (pacer.LogicDue(now))
                {
                    // dt is raw, unclamped: HP.exe MainLoop (0x10914a60) passes the clock difference to Engine->Tick as is;
                    // the [0.005, 0.1]·TimeDilation clamp applies only to the level clock (ULevel::Tick 0x103b7062, same in Core).
                    float dt = (float)(now - last); last = now; logicDt = dt;
                    long t0 = Stopwatch.GetTimestamp();
                    var (pxw, pxh) = r.PixelSize();
                    PrepareStep(pxw, pxh, input.MenuRegionWidth(pxw, pxh));
                    input.RunFrame(sink, dt);
                    WorldFrame before = host.Runtime.LatestFrame;
                    prevFrame = before; prevLevelTime = host.Runtime.PresentedLevelTime;
                    host.Tick(1, dt);
                    var scene = Cur;
                    AfterTick(r);
                    if (!ReferenceEquals(scene, Cur)) { hold.SceneChanged(before); prevFrame = null; }   // activeSceneChanged
                    hold.Update(host.Runtime.LatestFrame, payloadPending: false);
                    audio.Update(!ReferenceEquals(scene, Cur));
                    // Device-queue gaps the player hears as crackle (AudioSystem.Fill zero-fills an underrun, drops past
                    // half a second of backlog): logged when they change, with the Core tick, so a playthrough log shows them.
                    int ur = audio.System.QueueUnderruns, qd = audio.System.QueueDropped;
                    if (ur != lastUnderruns || qd != lastDropped)
                    {
                        Console.WriteLine($"[audio] device queue: underruns {ur}, dropped {qd} (tick {host.Runtime.LatestFrame?.Tick})");
                        lastUnderruns = ur; lastDropped = qd;
                    }
                    if (loadShownAt >= 0)
                    {
                        while (running && Wall.Elapsed.TotalSeconds - loadShownAt < MinLoadScreenSeconds)
                        {
                            SDL_Event ev;
                            while (SDL_PollEvent(&ev))
                                if (!input.PumpLoadHold(ev, sink, audio.Focus)) running = false;
                            host.Runtime.PresentLoadMapScreenNow(loadScreen);
                            SDL_Delay(16);
                        }
                        loadShownAt = -1; loadScreen = null; loadHud = null;
                        now = sw.Elapsed.TotalSeconds; last = now;   // hold time is not the next tick's delta
                    }
                    if (exitRequested) { Console.WriteLine("[Exit] Closing by request"); running = false; }
                    if (!follow) fly.Move(keys, dt);
                    pacer.LogicDone(now);
                    ticked = true; ticks++;
                    core += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                }
                tickedSinceDraw |= ticked;
                if (!pacer.DrawNow(now, ticked)) continue;
                LastRealDt = (float)(now - lastRender); lastRender = now;
                long t1 = Stopwatch.GetTimestamp();
                WorldFrame cur = host.Runtime.LatestFrame;
                float alpha = prevFrame is null ? 1f : pacer.Alpha(now);
                WorldFrame show = Interp.Frame(prevFrame, cur, alpha, logicDt);
                float levelTime = prevFrame is null ? host.Runtime.PresentedLevelTime : Interp.Lerp(prevLevelTime, host.Runtime.PresentedLevelTime, alpha);
                if (follow) { followCam = Interp.Follow(followCam, show.Camera); fly = Camera.From(followCam); }
                FrameDraws(r, show, alpha, levelTime, full: tickedSinceDraw); tickedSinceDraw = false;
                // Gate: GateBlack without the 3D world, 2D as usual; LoadingScreen/HeldFrame do not occur here: the loading
                // screen is presented synchronously and no frame is held (no swapchain copy).
                r.HideWorld = hold.View == OpenHP1.Travel.TravelView.GateBlack;
                if (hold.View is OpenHP1.Travel.TravelView.LoadingScreen or OpenHP1.Travel.TravelView.HeldFrame)
                    Console.Error.WriteLine($"[Travel] view {hold.View} not supported by the window (screen — synchronous, no frame copy)");
                r.RenderToWindow(fly);
                r.HideWorld = false;
                pacer.RenderDone(now);
                rend += (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
                frames++; acc += LastRealDt;
                if (acc >= 1.0)
                {
                    string title = FramePacer.PerfTitle(PerfTitle, frames / acc, ticks / acc, core / Math.Max(1, ticks), rend / frames,
                        $"{(follow ? "" : " (free camera, F)")}{(sink.UWindowActive ? " [menu]" : "")}");
                    if (title is not null) SDL_SetWindowTitle(r.Window, title);
                    acc = 0; frames = 0; ticks = 0; core = rend = 0;
                }
            }
            return 0;
        }
    }

    public partial struct Camera
    {
        public void Move(HashSet<SDL_Keycode> keys, float dt)
        {
            float s = 600f * dt * (keys.Contains(SDL_Keycode.SDLK_LSHIFT) ? 3f : 1f);
            Axes(out var fx, out var fy, out var fz, out var rx, out var ry, out var rz, out _, out _, out _);
            float f = (keys.Contains(SDL_Keycode.SDLK_W) ? 1 : 0) - (keys.Contains(SDL_Keycode.SDLK_S) ? 1 : 0);
            float rgt = (keys.Contains(SDL_Keycode.SDLK_D) ? 1 : 0) - (keys.Contains(SDL_Keycode.SDLK_A) ? 1 : 0);
            X += (fx * f + rx * rgt) * s; Y += (fy * f + ry * rgt) * s; Z += (fz * f + rz * rgt) * s;
        }
    }

    static class ParchmentProbe
    {
        // Loading-screen brightness against the menu path (audit/loadscreen_brightness_check.sh): the capture inside the
        // first parchment tile (HPLevelInfoBackground1, no text over it) over the same texels drawn as the menu draws a
        // picture (RenderUWindow colour 255: texel x 1, then the device gamma ramp). 1.000 = menu brightness.
        public static string Measure(UETextureSource src, IReadOnlyList<OpenHP1.Hud.HudQuad> quads, byte[] px, int width, float gamma)
        {
            foreach (OpenHP1.Hud.HudQuad q in quads)
            {
                if (q.Texture == null || !q.Texture.EndsWith("HPLevelInfoBackground1", StringComparison.OrdinalIgnoreCase)) continue;
                var d = src.Get(q.Texture);
                var (tw, th, rgba) = Renderer.ExpandP8(d, Renderer.TexDraw.Masked)[0];
                double cap = 0, menu = 0;
                for (int y = (int)q.Y0 + 2; y < (int)q.Y1 - 2; y++)
                    for (int x = (int)q.X0 + 2; x < (int)q.X1 - 2; x++)
                    {
                        float u = q.U0 + (x + 0.5f - q.X0) / (q.X1 - q.X0) * (q.U1 - q.U0);
                        float v = q.V0 + (y + 0.5f - q.Y0) / (q.Y1 - q.Y0) * (q.V1 - q.V0);
                        int t = (Math.Clamp((int)(v * th), 0, th - 1) * tw + Math.Clamp((int)(u * tw), 0, tw - 1)) * 4;
                        int o = (y * width + x) * 4;
                        for (int c = 0; c < 3; c++)
                        {
                            cap += px[o + c];
                            menu += 255.0 * Math.Pow(rgba[t + c] * (rgba[t + 3] / 255.0) / 255.0, gamma);
                        }
                    }
                return $"parchment brightness capture/menu = {cap / menu:F3} (tile colour {q.R:F3}, gamma {gamma:F3})";
            }
            return "parchment brightness: no HPLevelInfoBackground1 tile";
        }
    }

    static class Png
    {
        public static void Write(string path, int w, int h, byte[] rgba)
        {
            using var fs = File.Create(path);
            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var hdr = new byte[13];
            BE(hdr, 0, w); BE(hdr, 4, h); hdr[8] = 8; hdr[9] = 6;
            Chunk(fs, "IHDR", hdr);
            using var ms = new MemoryStream();
            using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
                for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgba, y * w * 4, w * 4); }
            Chunk(fs, "IDAT", ms.ToArray());
            Chunk(fs, "IEND", Array.Empty<byte>());
        }
        static uint[] _t;
        static uint Crc(byte[] d)
        {
            if (_t is null)
            {
                _t = new uint[256];
                for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; _t[n] = c; }
            }
            uint x = 0xFFFFFFFFu;
            foreach (byte b in d) x = _t[(x ^ b) & 0xFF] ^ (x >> 8);
            return x ^ 0xFFFFFFFFu;
        }
        static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; BE(len, 0, data.Length); s.Write(len);
            var td = new byte[4 + data.Length];
            System.Text.Encoding.ASCII.GetBytes(type, 0, 4, td, 0); data.CopyTo(td, 4);
            s.Write(td);
            var crc = new byte[4]; BE(crc, 0, (int)Crc(td)); s.Write(crc);
        }
    }
}
