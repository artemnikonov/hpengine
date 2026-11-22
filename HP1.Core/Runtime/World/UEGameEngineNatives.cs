// PLATFORM NATIVE TABLE AND ENGINE GAME LOOP.
//
// The bodies below were moved from `runtime_poc/HeadlessSmoke.cs` (class HeadlessNatives)
// without changing behaviour or the journal, so the own engine (hpengine) boots and ticks
// the world through Core without referencing the test harness.
//
// HeadlessNatives (runtime_poc) derives from this class: it adds probes, comparison
// journals and fixture seams through explicit hooks (TickBegin, InputHook,
// BeforeActorsPass, BeforeFrame, AfterFrame, TickWorldEnd, JournalStream,
// SetLocationProbe, ActorTickBoundaryProbe) and by overriding OnUnknownNative.
// The hook order in TickWorld is exactly the order those pieces had in the former
// body, so the run journal is byte-identical.
//
// Class members keep the HeadlessSmoke.cs indentation (the class is not indented inside
// the namespace block) so `git diff --color-moved` reads the move as moved lines.
// Block namespace rather than file-scoped (netstandard2.1 + C# 9 profile).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HP1.Core;
using UELib.Core;
// Script vectors are the Core value type.
using Vector3 = HP1.UEScript.UEVector;

namespace HP1.UEScript
{
    // HP1 shipped Core.dll routes appRand/appFrand/appRandRange through the MSVC CRT
    // rand() (Core.dll.c:3578, 28148, 59731, 64932, 65436). The deterministic smoke
    // fixture uses the MSVCRT recurrence with an explicit seed of 1; replay code in the
    // shipped Engine also resets srand(1) (Engine.dll.c:129546). Production gameplay
    // must seed this state from the same startup/replay source as the native runtime.
    public class UEGameEngineNatives : UENativesCore
    {
        // The Core-owned lifecycle: actor list, tick skeleton, spawn/
        // destroy, owner/base, zones and the level clock all live in
        // UELevelRuntime; this frontend supplies the BSP/anim/physics hooks.
        // Main session runtime; a native asks for the CURRENT world — the game world or,
        // while Entry code runs, the resident Entry (UELevelRuntime.Entry.cs).
        internal readonly UELevelRuntime MainRuntime = new();
        internal UELevelRuntime Runtime => MainRuntime.CurrentWorld;

        // UGameEngine::Init -> LoadMap(FURL("Entry")) before the first game map.
        internal void EnsureEntryWorld(UEInterpreter interp) => MainRuntime.EnsureEntryWorld(interp, _systemDir);
        // The release the install holds (UEEdition.OfSystem, the one owner): selects per-edition script overrides (UEPortScriptOverrides).
        internal UEEdition.DataSet DataSet => UEEdition.OfSystem(_systemDir).Data;
        internal List<UEObject> _actors => Runtime.Actors;
        // Install root: level travel must turn the map name from the URL into a path,
        // and the frontend owns paths.
        internal string _gameRoot;
        // The install root also sets the Core sound catalog (UESoundCatalog: one
        // duration formula for every frontend).
        public void SetGameRoot(string root)
        {
            _gameRoot = root;
            UESoundCatalog.Shared.SetSource(root);
            UEMusicCatalog.Shared.SetSource(root);   // Galaxy pool songs (UEGalaxyAudio) — Core, even without a window
        }

        // ── Saves (D1): Save directory under the game root, like shipped `..\Save` ──
        // Saves elsewhere than <root>/Save: test fixtures (a temp folder, so checks never write into the install's Save)
        // and a game root that is not the install (the mod working copy: UEGameEngine.Boot saveDir).
        internal string SaveDirOverride;
        // HP2 (DECISIONS 2026-11-16, save dir (b), coordinator): the install's Save holds the player's REAL HP2 saves, so the
        // port's HP2 saves live only in a directory the host names (HPEngine --hp2-savedir); none -> a loud refusal on use.
        string SaveDir => SaveDirOverride ?? (UEGameProfile.Current.Game == UEEdition.Game.HP2
            ? throw new InvalidOperationException("HP2: no port save directory was given (HPEngine --hp2-savedir); the install's Save folder holds the player's own HP2 saves and is never written")
            : Path.Combine(_gameRoot, "Save"));
        string SaveFilePath(string stem) => UELevelRuntime.SaveBodyPath(GameSaveDir, stem);
        // HP2's save slot: the original's Game.exe launcher passes `-SAVESLOT=<n>` (null: none given). The frontend sets it.
        internal int? SaveSlot;
        // The directory the GameSaveInfo/SaveGameExists natives read: HP1 GSys->SavePath (+0x34), HP2 GSys+0x40
        // (UELevelRuntime.SaveSlotDir).
        string GameSaveDir => UEGameProfile.Current.Game == UEEdition.Game.HP2 ? UELevelRuntime.SaveSlotDir(SaveDir, SaveSlot) : SaveDir;

        // The enabled HPEngine mods (UEGameEngine.Boot; the game root is then ModOverlay's working copy): recorded in saves
        // and compared on load (UELevelRuntime.SaveScriptMismatch).
        internal IReadOnlyList<string> ActiveMods = Array.Empty<string>();

        // Reads and validates a save body before the old world is touched — its grammar, its map in this game, and that it
        // fits the current scripts (mods) — false (with the reason) when it cannot be loaded; otherScripts: the body and its
        // map are fine, only the scripts differ (the restart-with-progress fallback applies).
        bool TryReadSave(UEInterpreter interp, string path, out byte[] data, out string map, out string mapPath, out string reason,
            out bool otherScripts)
        {
            data = null; map = null; mapPath = null; reason = null; otherScripts = false;
            try
            {
                data = File.ReadAllBytes(path);
                map = UELevelRuntime.ValidateSavedGame(data);
                mapPath = Path.Combine(_gameRoot, "Maps", map + ".unr");
                mapPath = UEInstallPath.File(mapPath) ?? mapPath;
                int packagesBefore = UEClassRegistry.Shared.Packages.Count;
                reason = !File.Exists(mapPath) ? $"the save's map {map} is not in this game"
                    : UELevelRuntime.SaveScriptMismatch(data, mapPath, interp, ActiveMods);
                // The check resolves the save's classes the linker way, so it is the step that raises their packages now
                // (witness for verify_savegame_packages, next to LoadSavedWorld's own packages_raised).
                var raised = new List<string>();
                for (int p = packagesBefore; p < UEClassRegistry.Shared.Packages.Count; p++) raised.Add(UEClassRegistry.Shared.Packages[p].PackageName);
                _journal.Add($"load_savegame_check packages_raised={(raised.Count == 0 ? "-" : string.Join(",", raised))} "
                    + $"verdict={(reason is null ? "fits" : "refused")}");
                otherScripts = reason is not null && File.Exists(mapPath);
                return reason is null;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { reason = e.Message; return false; }
        }

        // ── LOAD NOTICE — IMPROVEMENT, NOT ORIGINAL (author decision 2026-11-13, D-82) ──
        // A save of other scripts restarts its level, an old-format save is refused while mods are on: the player is told
        // why by the shipped tutorial warning — what WarnTrigger.Touch does (Lev_Tut1's hints): baseHud.ShowPopup(
        // class'baseWarning'), then curPopup.DisplayText and LifeSpan. The popup is drawn (baseWarning.Draw: top of the
        // screen on the HPBase.Icons.leftPanel tile), timed (LifeSpan) and dismissed (basePopup.popstate: ClearMessages,
        // set when the player makes Harry run or aim: Harry.PlayRunning, state playeraiming) by the shipped bytecode. The
        // text waits here — engine state, in no save — for the first tick the player's baseHud exists (the level's first
        // frame spawned it) and the level plays: not paused, no LevelAction, the menu book closed (HPHud.PostRender draws
        // nothing under it), no popup of the game's own (ShowPopup would orphan it). A cut-scene does not hold it back:
        // HPHud/BroomHud/QuidHud.PostRender call DrawPopup in cut-scene mode too, and no cut-scene code sets ClearMessages.
        string _loadNotice;
        UEObject _loadNoticePopup;
        // `durration` of every WarnTrigger placed in the tutorial (Lev_Tut1, Lev_Tut1b: 6.0).
        const float LoadNoticeSeconds = 6f;
        // baseWarning.Draw falls to its smallest font (LocalSmallFont) while the text is wider than Canvas.SizeX - 32;
        // 640 is the narrowest canvas. A text naming the save's mods is used only if it fits that.
        const int LoadNoticeMaxWidth = 640 - 32;

        // Width of the text in LocalSmallFont; -1 without the font. Canvas.TextSize on a scratch canvas (HUDSetup's
        // SpaceX = 0): no live canvas state is touched.
        float LoadNoticeWidth(UEInterpreter interp, string text)
        {
            object font = ((_localPlayer?.GetProp("Player") as UEObject)?.GetProp("Console") as UEObject)?.GetProp("LocalSmallFont");
            var canvas = new UEObject { Interp = interp };
            canvas.Props["Font"] = font; canvas.Props["ClipX"] = 0f; canvas.Props["ClipY"] = 0f;
            return font is not null && FrameProducer.TextSize(canvas, text, out float w, out _) ? w : -1f;
        }

        void TryShowLoadNotice(UEInterpreter interp)
        {
            if (_loadNotice is null) return;
            UEObject level = interp.GetLevelInfo();
            if (_localPlayer?.GetProp(PK.myHUD) is not UEObject hud || !UEInterpreter.ClassIsA(hud.Class, "baseHud")) return;
            if (hud.GetProp("curPopup") is UEObject) return;
            if (level is null || UEInterpreter.ToInt(level.GetProp("LevelAction")) != 0
                || !string.IsNullOrEmpty(level.GetProp(PK.Pauser)?.ToString())) return;
            UEObject book = ((_localPlayer.GetProp("Player") as UEObject)?.GetProp("Console") as UEObject)?.GetProp(PK.menuBook) as UEObject;
            if (book is not null && UEValue.ToBool(book.GetProp("bIsOpen"))) return;
            string text = _loadNotice;
            _loadNotice = null;
            UClass cls = UEClassRegistry.Shared.Find("HPBase", "baseWarning");
            UFunction show = interp.FindEventFunction(hud, "ShowPopup");
            if (cls is not null && show is not null) interp.Invoke(hud, show, cls);
            if (hud.GetProp("curPopup") is not UEObject popup)
            {
                // Scripts without the shipped popup (a mod): nothing to show it with; the reason is in the run log.
                _journal.Add("load_notice not_shown no_popup");
                UETrace.Info("[Save] load notice not shown: these scripts have no HPBase.baseWarning popup");
                return;
            }
            popup.Props["DisplayText"] = text;
            popup.Props["LifeSpan"] = LoadNoticeSeconds;
            _loadNoticePopup = popup;
            _journal.Add($"load_notice shown text=\"{text}\" seconds={LoadNoticeSeconds.ToString(CultureInfo.InvariantCulture)} "
                + $"small_font_width={LoadNoticeWidth(interp, text).ToString(CultureInfo.InvariantCulture)} max={LoadNoticeMaxWidth}");
        }

        // The level-start autosave's slot: FESlotPage.SaveSelectedSlot / LoadSelectedSlot with no slot selected
        // (doLevelSave(99), `open save99.usa`).
        const string LevelStartSaveStem = "save99";

        // D-82 restart (NOT ORIGINAL): the map entered from its beginning as a level travel enters it, with `carried`.
        void RestartLevelWithProgress(UEInterpreter interp, UELevelRuntime.PlayWorldConfig cfg, string map, string mapPath,
            Dictionary<string, List<UELevelRuntime.TravelActor>> carried)
        {
            cfg.TravelInfo = carried;
            Runtime.RetargetTravelToMap(map);
            Runtime.TeardownPlayWorld(interp);
            _localPlayer = Runtime.LoadAndBootstrapPlayWorld(interp, mapPath, cfg);
            RunRestartLevelStartSave(interp);
        }

        // The rest of a level entry (2026-11-17, 0.4.1 F1): a script level change arms HPConsole's level-start autosave
        // (ChangeLevel: bLoadNewLevel; HPConsole.Tick: SaveSelectedSlot), and the death reload's state is what that save
        // wrote — FESlotPage.SaveSelectedSlot first raises lifePotions to MaxLifePotions / 2. The restart's set skipped it,
        // so a set carried from other scripts (a Restored entry with lifePotions 0) killed Harry on the first tick
        // (Harry.PlayerTick). Here the console's shipped SaveSelectedSlot runs on the restarted world before its first
        // tick, as from inside a frame (no progress frame, UELevelRuntime.PaintProgress), and its files go to a scratch
        // directory that is deleted: the player's slots are not overwritten (D-82). Scripts without the function: nothing.
        void RunRestartLevelStartSave(UEInterpreter interp)
        {
            UEObject console = Runtime.ViewportConsole;
            UFunction save = console is null ? null : interp.FindEventFunction(console, "SaveSelectedSlot");
            if (save is null) { _journal.Add("restart_level_start_save none"); return; }
            string keep = SaveDirOverride;
            string scratch = Path.Combine(Path.GetTempPath(), "hpengine-restart-" + Guid.NewGuid().ToString("N"));
            SaveDirOverride = scratch;
            try { Runtime.RunAsInsideFrame(() => interp.Invoke(console, save)); }
            finally
            {
                SaveDirOverride = keep;
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            _journal.Add("restart_level_start_save done");
        }

        // The notice is not part of the game: a save written while it is up does not carry it.
        void DropLoadNoticeForSave(UEInterpreter interp)
        {
            _loadNotice = null;
            UEObject popup = _loadNoticePopup;
            _loadNoticePopup = null;
            if (popup is null || UEValue.ToBool(popup.GetProp("bDeleteMe"))) return;
            if (_localPlayer?.GetProp(PK.myHUD) is UEObject hud && ReferenceEquals(hud.GetProp("curPopup"), popup))
                interp.Invoke(hud, interp.FindEventFunction(hud, "DestroyPopup"));
            else DestroyActorCore(interp, popup);
            _journal.Add("load_notice dropped_for_save");
        }

        // HP2's save model (DECISIONS.md 2026-11-16, HP2 save model (a); the maintainer's notes 
        // hp2_saves_disasm.md): the port's body file (D-01) in the slot directory, plus what UGameEngine::SaveGame
        // [HP2 0x103ab430] keeps besides the level: the hub-stack copies, the PA cache snapshot, the thumbnail, the
        // Pre/PostSaveGame events (UELevelRuntime.SaveGame, Hp2SaveGameTail).
        // HP2 UGameEngine::SaveGame after a written package [HP2 0x103ab6c3..0x103ab7ba]: for i < LevelInfo.HubStackLevel
        // (+0x4b4) GFileManager->Copy(dest "%s\Game%i.usa", src "%s\Save%i%i.usa") (Copy's arg1 is the destination,
        // Game.exe 0x10909d4e); then from i = HubStackLevel on, Save<P><i> while FileSize > 0 is deleted. The port's names
        // (D-01): game<i> and save<P><i> bodies. No shipped map or script uses ?push/?pop/?peer: HubStackLevel stays 0, the
        // copy loop never runs and the delete loop finds no save<P>0 — unless slot P's body save<P>0 exists (SaveGame(1)
        // and save10: the original's names collide the same way). HP1's SaveGame has the same loops, not ported (no trigger).
        void Hp2HubStackAfterSave(UEInterpreter interp, int slot)
        {
            int hub = UEInterpreter.ToInt(interp.GetLevelInfo()?.GetProp("HubStackLevel"));
            for (int i = 0; i < hub; i++)
                if (File.Exists(SaveFilePath($"save{slot}{i}"))) File.Copy(SaveFilePath($"save{slot}{i}"), SaveFilePath($"game{i}"), true);
            for (int i = hub; ; i++)
            {
                string f = SaveFilePath($"save{slot}{i}");
                if (!File.Exists(f) || new FileInfo(f).Length <= 0) break;
                File.Delete(f);
                _journal.Add($"savegame_hub_delete file={Path.GetFileName(f)}");
            }
        }

        // HP2 appInit after the slot directory (Core [HP2 0x10157249..0x1015768a]): MakeDirectory(slot), then
        // LoadPersistentActorCache() — the session's cache starts as the slot's snapshot (0x1015768a).
        internal void AppInitSaveSlot()
        {
            if (UEGameProfile.Current.Game != UEEdition.Game.HP2 || SaveDirOverride is null) return;   // no port save dir: nothing to restore
            Directory.CreateDirectory(GameSaveDir);
            UELevelRuntime.LoadPersistentActorCache(GameSaveDir);
        }

        // Core appPreExit's ClearCache call [HP2 0x10158108] (HP1: none): the session's PA cache does not outlive the process.
        internal void PreExit()
        {
            if (UEGameProfile.Current.Game == UEEdition.Game.HP2 && SaveDirOverride is not null) UELevelRuntime.ClearPersistentActorCache(GameSaveDir);
        }

        public void InstallSaveGameHandlers(UEInterpreter interp)
        {
            UEConsoleCommand.SaveGameHandler = slot =>
            {
                bool hp2 = UEGameProfile.Current.Game == UEEdition.Game.HP2;
                DropLoadNoticeForSave(interp);
                if (hp2) Directory.CreateDirectory(SaveDir);   // HP2: MakeDirectory(SavePath), MakeDirectory(GSys+0x40)
                Directory.CreateDirectory(GameSaveDir);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                byte[] data = Runtime.SaveGame(interp, ActiveMods);
                double serMs = sw.Elapsed.TotalMilliseconds;
                string path = SaveFilePath($"save{slot}");
                File.WriteAllBytes(path, data);
                _journal.Add($"savegame slot={slot} file={Path.GetFileName(path)} bytes={data.Length} "
                    + $"ser_ms={serMs:F1} write_ms={sw.Elapsed.TotalMilliseconds - serMs:F1}");
                _journal.Add($"savegame_channels {Runtime.AnimChannelDigest()}");
                if (hp2)
                {
                    Hp2HubStackAfterSave(interp, slot);
                    Runtime.Hp2SaveGameTail(interp, GameSaveDir, Path.Combine(_gameRoot, "System"));
                }
                return "";
            };
            // HP2 LOADGAME n (UEConsoleCommand's branch, UGameEngine::Exec [HP2 0x103a29b5..0x103a2be7]).
            UEConsoleCommand.LoadGameHandler = slot =>
            {
                UELevelRuntime.LoadPersistentActorCache(GameSaveDir);           // 0x103a29e2
                LoadGameTravel(interp, slot);
                return "";
            };
            UEConsoleCommand.SavePActorsHandler = () =>
            {
                Directory.CreateDirectory(SaveDir);
                Directory.CreateDirectory(GameSaveDir);
                Runtime.SavePersistentActors(Runtime.Actors[0].GetProp(PK.LevelEnterText) as string);
                return "";
            };
            UEConsoleCommand.OpenHandler = url => { Runtime.RequestOpen(interp, url); return ""; };
            UEConsoleCommand.ExitHandler = () => _journal.Add("exit_request");
            UEConsoleCommand.FlushHandler = () => Runtime.FlushGCache();
        }

        internal void LoadGameTravel(UEInterpreter interp, int slot)
        {
            Directory.CreateDirectory(SaveDir); Directory.CreateDirectory(GameSaveDir);
            // The original's URL is "%s\Save%i.usa" (0x104dace0: the slot directory's platform path). The port's save
            // directory is its own (D-01) and its travel finds a ".usa" body there by name (SaveFilePath), so the URL
            // carries the file name only; URL.Map's name part, all LoadPersistentActors takes of it, is the same.
            string file = $"Save{slot}.usa";
            _journal.Add($"loadgame slot={slot} file={file}");
            UEObject lvl = interp.GetLevelInfo();
            interp.Invoke(lvl, interp.FindEventFunction(lvl, "ServerTravel")
                ?? throw new InvalidDataException("LevelInfo.ServerTravel missing"), file, false);
        }
        private readonly Dictionary<int, (string Url, bool Items)> _travelProbe = new();
        // appSeconds for the LoadMap fade (UELevelRuntime.RunLoadMapFade); a test may replace it.
        public Func<double> WallSeconds = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        public void SetTravelProbe(int tick, string url, bool items) => _travelProbe[tick] = (url, items);
        internal readonly List<string> _journal;
        // The player host (UEGameEngine.Boot without a harness factory) keeps no comparison journal: nothing reads it there
        // (UEGameEngine.Tick cleared it unread), and its per-actor lines (tick_actor, walking_floor, walking_move, decal_*)
        // cost ~70 KiB of strings and ~0.4 ms per tick. Journal text only, never behaviour; the harness keeps every line.
        bool _journalOff;
        internal void DisableJournal() { _journalOff = true; Runtime.Journal = null; Runtime.DetailedJournal = false; }
        private readonly IniFile _ini;
        private readonly IniFile _userIni;
        private readonly UEIniFile _iniCore;
        internal readonly Hp1SkeletalMeshCatalog _skeletalMeshes;
        internal UEObject _localPlayer;
        private static readonly bool DetailedJournal =
            Environment.GetEnvironmentVariable("HP1_COMPACT_JOURNAL") != "1";
        // CFSkelHeader's root-motion cache (+0x6c..+0x94) is kept in the gameplay
        // runtime; the frontend only displays the actor location produced by this state.
        // The collision WORLD lives in Core (UELevelRuntime.Bsp) —
        // headless keeps only this proxy so the ~28 existing `_bsp` uses stay put
        // while the state itself is Core-owned.
        internal HeadlessBspModel _bsp
        {
            get => Runtime.Bsp;
            set => Runtime.Bsp = value;
        }
        internal int _currentTick = -1;

        // Provenance of every registered handler: the registry starts EMPTY
        // (base(false) skips the frontend-backed set). Only the
        // proven-pure intrinsic groups are imported from the shared VM, everything
        // else must be a headless handler, and an unregistered native THROWS via
        // OnUnknownNative instead of logging and continuing.
        internal readonly Dictionary<string, string> _provider = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> _journaledNatives = new(StringComparer.OrdinalIgnoreCase);
        private string _registering = "headless";

        // Color = R,G,B,A bytes (Core.u); the bag holds them as ints. The result is a copy of the operand whose A survives.
        // UE1 zero-fills a struct: the VM's unassigned Color is null and a partly assigned one is a bag without the unset
        // channels (UEValue.SetStructMember) — both read as 0.
        static UEObject ColorOperand(object v) => v as UEObject ?? UEObject.MakeColorBag(0, 0, 0, 0);

        internal static UEObject ColorOp(object first, object second, Func<int, int, int> op)
        {
            var a = ColorOperand(first); var b = ColorOperand(second); var r = a.CloneStruct();
            foreach (string ch in new[] { "R", "G", "B" })
                r.Props[ch] = op(UEInterpreter.ToInt(a.GetProp(ch)), UEInterpreter.ToInt(b.GetProp(ch))) & 0xff;
            return r;
        }

        internal static UEObject ColorScale(object color, float f)
        {
            var c = ColorOperand(color); var r = c.CloneStruct();
            foreach (string ch in new[] { "R", "G", "B" })
                r.Props[ch] = X87.MulIntFloatFtol(UEInterpreter.ToInt(c.GetProp(ch)) & 0xff, f) & 0xff;
            return r;
        }

        protected override void Pair(int idx, string name, UENativeHandler handler)
        {
            string provider = _registering;
            if (!string.IsNullOrEmpty(name)) _provider[name] = provider;
            base.Pair(idx, name, (interp, frame, args) =>
            {
                if (!string.IsNullOrEmpty(name) && _journaledNatives.Add(name))
                    _journal.Add($"native {name} provider={provider}");
                return handler(interp, frame, args);
            });
        }

        public UEGameEngineNatives(uint seed, List<string> journal, string defaultIni,
            Hp1SkeletalMeshCatalog skeletalMeshes)
            : base(seed)
        {
            _journal = journal;
            MainRuntime.Hp2SlotDir = () => SaveDirOverride is null && UEGameProfile.Current.Game == UEEdition.Game.HP2 ? null : GameSaveDir;
            _ini = new IniFile(defaultIni);
            _iniCore = new UEIniFile(defaultIni);
            // Shipped SpawnPlayActor reads "usr:DefaultPlayer.Class" FIRST
            // (Engine.dll.c:51548) and only then "ini:URL.Class" (51556). The first is the
            // user ini, whose template in the install is DefUser.ini
            // ([DefaultPlayer] Class=Harrypotter.harry). Without it the second
            // step, Engine.Playerpawn, would give the wrong SpawnClass.
            string userIni = Path.Combine(UELevelRuntime.SystemDirOfDefaultIni(defaultIni), "DefUser.ini");
            _userIni = File.Exists(userIni) ? new IniFile(userIni) : null;
            _skeletalMeshes = skeletalMeshes;
            // system dir (fonts live in the .u packages there); the shipped
            // viewport resolution feeds the canvas dims (retail Default.ini
            // [WinDrv.WindowsClient] WindowedViewportX/Y = 640x480).
            _systemDir = UELevelRuntime.SystemDirOfDefaultIni(defaultIni);
            _viewportX = int.Parse(_ini.Get("WinDrv.WindowsClient", "WindowedViewportX")
                ?? throw new InvalidDataException("WindowedViewportX missing from Default.ini"));
            _viewportY = int.Parse(_ini.Get("WinDrv.WindowsClient", "WindowedViewportY")
                ?? throw new InvalidDataException("WindowedViewportY missing from Default.ini"));
            BindFrameProducer(new UEWorldFrameProducer(_systemDir, _viewportX, _viewportY));
            // ACTUAL viewport state of this frontend. The window opens at the
            // saved SIZE (shipped substitutes Client->WindowedViewportX/Y
            // when NewX == -1, WinDrv.dll.c:15133-15144); after that the answer to
            // GetCurrentRes lives HERE and only here — not in the config that
            // SetRes writes.
            //
            // Colour DEPTH is NOT taken from the ini. Shipped:
            //   :5810-5825   the constructor sets ColorBytes from the desktop;
            //   :4907-4919   opening the window passes NewColorBytes = -1;
            //   :15062-15069 on -1 the ALREADY SET Viewport->ColorBytes is used.
            // The shipped `WindowedColorBits=16` is a SAVED value, not live viewport
            // state. The value is single and lives in Core with its source (C5).
            _viewport = new HeadlessViewportDevice(FrameProducer, _viewportX, _viewportY,
                UEViewport.DesktopColorBytes);
            FrameProducer.DestroyActor = fx => DestroyActorCore(fx.Interp ?? _localPlayer?.Interp, fx);

            // Wire the Core lifecycle skeleton to this frontend's BSP/anim/physics
            // layers. The skeleton (UELevelRuntime) is the ONE owner of the tick
            // order, spawn/destroy, owner/base and zones; everything below is the
            // headless implementation of the genuinely frontend-specific parts.
            Runtime.Journal = _journal.Add;
            Runtime.DetailedJournal = DetailedJournal;
            UELevelRuntime.ProfileTicks = Environment.GetEnvironmentVariable("HP1_TICK_PROF") == "1";
            // The walking/falling GEOMETRY hooks for the Core physics bodies:
            // the shipped SingleLineCheck trace (unpadded,
            // actor-aware, ETraceFlags-selected — the hover check's stationary
            // floor state also owns the walking_floor journal line, whose BSP
            // node/surfFlags detail only this host knows), FarMoveActor for the
            // ledge-stop restore, the shipped stepUp Mount probe, the shared
            // appFrand stream, and the trace gate.
            // No WalkMoveHook (PF-7): the caller reads its OWN FCheckResult (MoveCheck),
            // which the Core WalkMove path maintains itself; the fast path and a
            // MoveActor refusal publish no _lastMove* answer.
            Runtime.LineTraceHook = (i2, actor, start, end, extent, traceFlags) =>
            {
                UELevelRuntime.LineCheckHit hit = SingleLineCheckResult(i2, actor, ownerToSkip: actor,
                    start, end, extent, traceFlags);
                // The stationary hover check (point trace, flags 6, straight
                // down 20) reports the standing pawn's floor state each tick —
                // the successor of the old stationary-probe journal line.
                if (traceFlags == 6 && extent == Vector3.zero && end.z == start.z - 20f
                    && end.x == start.x && end.y == start.y
                    && !_journalOff && (DetailedJournal || ReferenceEquals(actor, _localPlayer)))
                    _journal.Add($"walking_floor actor={actor.GetProp("Name")} from={start} blocked={hit.Blocked.ToString().ToLowerInvariant()} fraction={hit.Time:R} normal={hit.Normal} node={hit.Node} surfFlags={_bsp.SurfFlags(hit.Node)} hit={hit.Actor?.GetProp("Name") ?? (object)"None"}");
                return hit;
            };
            Runtime.FarMoveHook = (i2, actor, newLoc) =>
                FarMoveActor(i2, actor, newLoc, bTest: false, bNoCheck: false);
            Runtime.FrandHook = NextRandomFrandSt0;
            Runtime.FrameProducer = FrameProducer;
            FrameProducer.AudioRuntime = Runtime;   // Galaxy pool world — this level's runtime
            Runtime.EmulateLoadStutter = AsPlayer;
            // The mesh catalog is Core: it also resolves
            // sequences and root tracks by the exact Mesh+AnimSequence pair.
            Runtime.LoadMeshCatalogFromInstall(_systemDir);
            // bAnimMove is written by Core (actor gameplay flag); root-motion state is Core's
            // and headless has no track representation of its own.
            // Core ticks logical animation itself (TickActorBody); headless needs no
            // AnimationHook — it has no visual half.
            // No PhysicsHook: the dispatcher, its rotation tail and PostTouch are
            // Core-owned now (UELevelRuntime.PerformPhysics). Headless deliberately
            // leaves the override unset so its physics runs through bare Core —
            // that is what makes the self-sufficiency claim testable.
            // Root motion is entirely Core: the decision, track data and the
            // mesh coordinate system.
            // (No PointRegionHook: Core finds zone and leaf itself from its own loaded model.)
            Runtime.DestroyCleanupHook = self =>
            {
                // The collision-hash removal moved into Core's own DestroyActor at
                // the same shipped position (Engine.dll.c:253027-253031) — the hash
                // is Core's structure. Only the frontend's own caches die here.
            };
            // (No SpawnEncroachHook: Core calls Runtime.CheckEncroachment itself.)
            // The one frontend part of the extent box: the UMesh package read.
            //
            // ObjectPath IS PASSED, AND THIS IS NOT COSMETIC: ambiguous names are
            // resolved by path — the same mesh name ships in two packages, and without
            // the package segment the query must stay loud (UEMeshBounds.Resolve).
            // E.g. `skbarrelMesh` has 2 exports; losing the path failed resolution with path=''.
            Runtime.MeshHashFieldsHook = meshRef =>
                _skeletalMeshes.GetHashFields(meshRef.Name, meshRef.ObjectPath);
            Runtime.DescribeCandidates = NativeCandidates;

            // Proven-pure intrinsic groups from the shared VM (verified: no
            // UnityActor/transform/GameObject/bridge access). Random intrinsics use
            // the shared HP1.Core MSVCRT stream seeded above.
            _registering = "base";
            RegisterBoolOps();
            RegisterIntOps();
            RegisterByteOps();
            RegisterFloatOps();
            RegisterStringOpsPure();
            RegisterTypeOpsPure();
            RegisterStateOpsPure();
            RegisterEntryLevelOps(() => MainRuntime);   // Core-owned GetEntryLevel
            RegisterLocalizationOps(() => GameSaveDir);   // Localize/GetLanguage/SaveConfig/GameSaveInfo — Core bodies
            RegisterMathOps();
            RegisterVectorOps();
            _registering = "headless";

            Pair(231, "Log", (interp, frame, args) =>
            {
                journal.Add("log " + (args.Length > 0 ? args[0]?.ToString() ?? "" : ""));
                return null;
            });
            // UObject::execWarn 232 (Core 0x10140b50): arg eval, FOutputDevice::Logf, FString free, asserts — no world effect.
            Pair(232, "Warn", (interp, frame, args) =>
            {
                journal.Add("warn " + (args.Length > 0 ? args[0]?.ToString() ?? "" : ""));
                return null;
            });
            // Original: Engine.dll ?execError@AActor@@QAEXAAUFFrame@@QAX@Z [HP1 0x10407010] [HP2 0x10414680]
            // AActor::execError 233 (Engine.dll.c:27538): FOutputDevice::Log(Stack, S), then
            // XLevel(+0x80) vtable +0x98 DestroyActor(this, 0) — the same call as Destroy (279).
            Pair(233, "Error", (interp, frame, args) =>
            {
                journal.Add("error " + (args.Length > 0 ? args[0]?.ToString() ?? "" : ""));
                DestroyActorCore(interp, frame.Context ?? frame.Object);
                return null;
            });
            // UObject::execDynamicLoadObject (Core.dll.c:63931): StaticLoadObject
            // with LOAD_NoWarn only when MayFail is true. The current gameplay path
            // requests Engine.Sound, so resolve the exact package/export path from
            // the shipped .uax; an arbitrary non-existent resource must not become
            // a truthy marker.
            Pair(0, "DynamicLoadObject", (interp, frame, args) =>
            {
                string path = args.Length > 0 ? NameArg(args, 0) : null;
                UClass requestedClass = args.Length > 1 ? args[1] as UClass : null;
                if (requestedClass is null)
                    throw new InvalidDataException($"DynamicLoadObject missing class for '{path}'");
                // NON-SOUND RESOURCE — the shared Core resolver (one body for all
                // frontends, see UENativesCore.ResolveDynamicLoad).
                if (!string.Equals(requestedClass.Name?.ToString(), "Sound", StringComparison.OrdinalIgnoreCase))
                {
                    object resolved = ResolveDynamicLoad(path, requestedClass.Name?.ToString(),
                        pkg => _journal.Add($"dynamic_load_unverifiable package={pkg} path={path}"
                            + " (the script registry does not load this package — existence cannot be checked)"));
                    _journal.Add($"dynamic_load class={requestedClass.Name} path={path}"
                        + $" found={(resolved is not null ? "true" : "false")}");
                    return resolved;
                }
                // StaticLoadObject flags are LOAD_NoWarn | (MayFail ? LOAD_Quiet : 0) (Core.dll.c:63984);
                // without -STRICT SafeLoadError (14713-14763) only logs and returns None,
                // so a miss is NOT fatal for any MayFail (baseDialog.FindDialog expects None).
                // The object is found by the same linker rule as any class (ResolveDynamicLoad: name + exact class, the
                // path's group not compared, the last export by index). Live HP2 Duel01 (SwARc level host):
                // Duellist.fillCommentArray's "AllDialog.PC_WCG_DuelIntro_03" is AllDialog.usa.PC_WCG_DuelIntro_03.
                if (ResolveDynamicLoad(path, "Sound", null) is UEResourceRef linked) path = linked.ObjectPath;
                UESoundRef sound = UESoundCatalog.Shared.Load(path, mayFail: true);
                _journal.Add($"dynamic_load class=Sound path={path} found={(sound is not null).ToString().ToLowerInvariant()}"
                    + (sound is null ? "" : $" duration={sound.Duration:R}"));
                return sound;
            });
            // AActor::execGetSoundDuration — body in Core (UELevelRuntime.
            // GetSoundDurationNative + UESoundCatalog).
            Pair(0, "GetSoundDuration", (interp, frame, args) =>
                Runtime.GetSoundDurationNative(interp, frame, args));
            // Bodies of these natives live in Core (UELevelRuntime.AnimNatives.cs): they write
            // exactly the engine properties Core TickAnimation reads. Only the mesh
            // catalog (LookupAnimSequence below) stays here.
            Pair(259, "PlayAnim", Runtime.PlayAnimNative);
            Pair(260, "LoopAnim", Runtime.LoopAnimNative);
            Pair(294, "TweenAnim", Runtime.TweenAnimNative);
            Pair(261, "FinishAnim", Runtime.FinishAnimNative);
            Pair(0, "LinkSkelAnim", Runtime.LinkSkelAnimNative);
            Pair(282, "IsAnimating", Runtime.IsAnimatingNative);
            Pair(263, "HasAnim", Runtime.HasAnimNative);
            Pair(293, "GetAnimGroup", Runtime.GetAnimGroupNative);
            Pair(312, "VisibleCollidingActors",
                (interp, frame, args) =>
                    Runtime.VisibleCollidingActorsNative(interp, frame, args));
            // HP1 native(286) GetWorldCollisionBox(bool bVisual) — shipped
            // AActor::execGetWorldCollisionBox (Engine.dll.c:134732): bVisual →
            // the MESH primitive's bounding box (Mesh ?: Brush ?: collision
            // primitive), else the collision cylinder box Location ± (R,R,H).
            // The mesh/brush boxes reuse the oracle-validated HashBoxes formulas
            // (validate_collisionhash layer 3). Returns the BoundingBox struct
            // bag {Min,Max,IsValid}. Reached by baseSpell.Timer's homing aim
            // (TargetArea center, stage-7 hit fixture).
            Pair(286, "GetWorldCollisionBox", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                bool bVisual = args is { Length: > 0 } && UEInterpreter.ToBool(args[0]);
                // Core owns the answer (UELevelRuntime.WorldCollisionBox): with neither mesh nor
                // brush shipped picks the primitive by CollideType (virtual slot 0xa4), and an
                // oriented cylinder, a box and a nonzero CollisionWidth give DIFFERENT answers
                // than a plain loc±(r,r,h).
                var (min, max) = Runtime.WorldCollisionBox(self, bVisual);
                // A struct bag like every other struct value (copied by value, saved by the Struct encoding).
                var box = new UEObject(UEPropLayout.ForStructName("BoundingBox")) { IsStruct = true };
                box.Props["Min"] = min; box.Props["Max"] = max; box.Props["IsValid"] = (byte)1;
                return box;
            });
            // Core owns the candidate set (UELevelRuntime.AllActorsNative) — the single
            // answer to "what is Level.Actors"; see the comment in Core.
            Pair(304, "AllActors", (interp, frame, args) =>
                Runtime.AllActorsNative(interp, frame, args));
            Pair(305, "ChildActors", (interp, frame, args) => Runtime.ChildActorsNative(interp, frame, args));
            Pair(306, "BasedActors", (interp, frame, args) => Runtime.BasedActorsNative(interp, frame, args));
            // APlayerPawn::execResetKeyboard (Engine.dll.c, export 0x23a1): Player (+0x4bc) IsA Viewport and its Input (+0x58)
            // set -> UObject::ResetConfig(Input->Class) (Core 0x1ac3: the class section of DefUser.ini over the live config,
            // then LoadConfig of the class defaults and of every Input object). Otherwise nothing.
            Pair(544, "ResetKeyboard", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self?.GetProp(PK.Player) is UEObject player && UEInterpreter.ClassIsA(player.Class, "Viewport") && Runtime.Input is { } input)
                    input.ResetKeyboard(Runtime.Config);
                return null;
            });
            // 542 ScreenToWorld(vector ScreenLoc): the view is the pawn's own PlayerCalcView (ProcessEvent with ViewActor = self,
            // CameraLocation = Location, CameraRotation = Rotation pre-filled, 0x103d5b54..0x103d5bd8 — ProjectCamera does
            // the same call), the viewport is XLevel->Engine->Client->Viewports(0) (+0x80 +0x40 +0x4c +0x2c [0]): the
            // viewport size Core owns. Body: ScreenToWorld below.
            Pair(542, "ScreenToWorld", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                UEVector s = UEValue.AsVector3(args[0]);
                float fov = UEValue.AsFloat(self.GetProp(PK.FOVAngle));   // read before the event (0x103d5b1b)
                CameraSnapshot cam = WorldFrameProjector.ProjectCamera(interp, self);
                return ScreenToWorld(s, FrameProducer.ViewportX, FrameProducer.ViewportY, fov, cam.Location, cam.Rotation,
                    UEWorldFrameProducer.FastMathWindow);
            });
            // UObject::execResetConfig (Core.dll.c:19367, export 0x1a5f): ResetConfig(this->Class) (UEObjectConfig.ResetConfig).
            Pair(0, "ResetConfig", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                UEObjectConfig.ResetConfig(UEConfig.Global, self?.Class, interp, Runtime.Actors);
                return null;
            });
            // APlayerPawn::execGetDefaultURL (export 0x4ca5): FURL u(NULL); u.LoadURLConfig("DefaultPlayer", "User") (every line of
            // the live section — AddOption); return u.GetOption(Option + "=", "").
            Pair(0, "GetDefaultURL", (interp, frame, args) =>
            {
                var u = new UEUrl();
                foreach (KeyValuePair<string, string> kv in Runtime.Config.Section(UEConfig.ScopeUser, "DefaultPlayer"))
                    u.AddOption(kv.Key + "=" + kv.Value);
                return u.GetOption(((args.Length > 0 ? args[0] : null)?.ToString() ?? "") + "=", "");
            });
            // APlayerPawn::execUpdateURL (Engine 546, Engine.dll.c ~24620-24690): XLevel->Engine (IsA UGameEngine) LastURL.
            // AddOption(NewOption + "=" + NewValue); bSaveDefault -> LastURL.SaveURLConfig("DefaultPlayer", NewOption, "User").
            Pair(546, "UpdateURL", (interp, frame, args) =>
            {
                string opt = (args.Length > 0 ? args[0] : null)?.ToString() ?? "", val = (args.Length > 1 ? args[1] : null)?.ToString() ?? "";
                bool save = args.Length > 2 && UEInterpreter.ToBool(args[2]);
                // CastChecked<UGameEngine>(XLevel->Engine): a non-game engine is a GError (0x103d3c25); the port's twin is
                // no adopted LastURL — loud, as GetURLMap and LoginPortal.
                UEUrl last = Runtime.LastUrl ?? throw new InvalidOperationException("UpdateURL: LastURL not set (level not loaded)");
                last.AddOption(opt + "=" + val);
                if (save) last.SaveURLConfig(Runtime.Config, UEConfig.ScopeUser, "DefaultPlayer", opt);
                return null;
            });
            // Original: Engine.dll ?execClientTravel@APlayerPawn@@QAEXAAUFFrame@@QAX@Z [HP1 0x104071f0] [HP2 0x10414890]
            // APlayerPawn::execClientTravel (URL, TravelType, bItems): with a Player — ProcessEvent PreClientTravel, then
            // Engine->SetClientTravel(Player, URL, bItems, TravelType) (vtable +0x8c). Without a Player nothing.
            Pair(0, "ClientTravel", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self?.GetProp(PK.Player) is not UEObject) return null;
                UELifecycle.CallEventArgs(interp, self, "PreClientTravel", Array.Empty<object>());
                Runtime.RequestClientTravel((args.Length > 0 ? args[0] : null)?.ToString() ?? "",
                    args.Length > 1 ? UEInterpreter.ToInt(args[1]) : UEUrl.Absolute, args.Length > 2 && UEInterpreter.ToBool(args[2]));
                return null;
            });
            // AActor::execGetNextInt (Engine.dll.c:86454, export 0x304e): MetaClass = StaticFindObjectChecked(UClass,
            // ANY_PACKAGE, ClassName); List = GetRegistryObjects(UClass::StaticClass(), MetaClass, 0); Num < List.Num ?
            // List(Num).Object : "". execGetNextIntDesc (Engine.dll.c:6343, export 0x1096): the same list; out Entry =
            // List(Num).Object, out Description = List(Num).Description, both "" past the end. No return value.
            Pair(0, "GetNextInt", (interp, frame, args) =>
                NextInt(args) is UERegistry.ObjectInfo e ? e.Object : "");
            Pair(0, "GetNextIntDesc", (interp, frame, args) =>
            {
                UERegistry.ObjectInfo e = NextInt(args);
                if (args.Length > 2) args[2] = e?.Object ?? "";
                if (args.Length > 3) args[3] = e?.Description ?? "";
                return null;
            });
            Pair(547, "GetURLMap", (interp, frame, args) => Runtime.UrlMapNative());
            // AActor::execGetMapName (539): the body is UEInstallPath.GetMapName (the [Core.System] path list owner).
            Pair(539, "GetMapName", (interp, frame, args) =>
                UEInstallPath.GetMapName(_gameRoot, StrArg(args, 0), StrArg(args, 1), args.Length > 2 ? UEInterpreter.ToInt(args[2]) : 0));
            // AActor::execGetNextSkin (545): UERegistry.NextSkin; out SkinName, out SkinDesc. No return value.
            Pair(545, "GetNextSkin", (interp, frame, args) =>
            {
                var (name, desc) = UERegistry.NextSkin(StrArg(args, 0), StrArg(args, 1), args.Length > 2 ? UEInterpreter.ToInt(args[2]) : 0);
                if (args.Length > 3) args[3] = name;
                if (args.Length > 4) args[4] = desc;
                return null;
            });
            // Original: Engine.dll ?execSaveGameExists@AActor@@QAEXAAUFFrame@@QAX@Z [HP1 0x1040c420] [HP2 0x1041aa70]
            // (Engine.dll.c:107023): P_FINISH; GFileManager->FileSize(FString::Printf("%s\\Save%i.usa", dir, 9)) > 0 —
            // slot 9 is a constant (push 9, HP1 0x1040c470, HP2 0x1041aadd). dir: HP1 GSys->SavePath, HP2 GSys+0x40
            // (appSaveSlotDir inlined, HP2 0x1041aabe..0x1041aad7: "" when its Num is 0). NOT ORIGINAL file (D-01, the
            // port's own save format): the slot-9 body is save9.hp1sav in that directory, the file `SaveGame 9` writes.
            Pair(3972, "SaveGameExists", (interp, frame, args) => SaveGameExists(GameSaveDir));
            // Original: Engine.dll.c:76300 AActor::execAutonomousPhysics (export 0x2c6b, body 0x103e4b40): P_GET_FLOAT,
            // P_FINISH, then UELevelRuntime.AutonomousPhysics.
            Pair(3971, "AutonomousPhysics", (interp, frame, args) =>
            {
                Runtime.AutonomousPhysics(interp, frame.Context ?? frame.Object, UEInterpreter.AsFloat(args.Length > 0 ? args[0] : null));
                return null;
            });
            Pair(425, "GetWind", (interp, frame, args) =>
                Runtime.WindNative(frame.Context ?? frame.Object, UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null)));
            // ParticleFX 431-435: bodies in UEWorldFrameProducer.ParticleNatives (Core owns the particle container).
            // AddParticle writes no result (null); GetParticleParams writes its out struct only when found.
            Pair(431, "NumParticles", (interp, frame, args) => FrameProducer.NativeNumParticles(frame.Context ?? frame.Object));
            Pair(432, "AddParticle", (interp, frame, args) =>
            {
                FrameProducer.NativeAddParticle(frame.Context ?? frame.Object, UEInterpreter.ToInt(args[0]),
                    UEInterpreter.AsVector3(args[1]), NextRandomInt15);
                return null;
            });
            Pair(433, "GetParticleParams", (interp, frame, args) =>
            {
                UEObject pp = FrameProducer.NativeGetParticleParams(frame.Context ?? frame.Object, UEInterpreter.ToInt(args[0]));
                if (pp is null) return false;
                args[1] = pp;
                return true;
            });
            Pair(434, "SetParticleParams", (interp, frame, args) =>
                FrameProducer.NativeSetParticleParams(frame.Context ?? frame.Object, UEInterpreter.ToInt(args[0]), args[1] as UEObject));
            Pair(435, "RecomputeDeltas", (interp, frame, args) =>
                FrameProducer.NativeRecomputeDeltas(frame.Context ?? frame.Object, UEInterpreter.ToInt(args[0])));
            Pair(307, "TouchingActors", (interp, frame, args) => Runtime.TouchingActorsNative(interp, frame, args));
            Pair(308, "ZoneActors", (interp, frame, args) => Runtime.ZoneActorsNative(interp, frame, args));
            Pair(262, "SetCollision", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                // AActor::SetCollision (Engine.dll.c:30526): remove from the collision
                // hash under the OLD bCollideActors, set bCollideActors/bBlockActors/
                // bBlockPlayers, re-add under the NEW one. Each optional arg defaults
                // to the current value (NActor.cpp:554-560).
                bool? Flag(int i) => i < args.Length && args[i] is not null
                    ? UEInterpreter.ToBool(args[i]) : (bool?)null;
                Runtime.SetCollision(self, Flag(0), Flag(1), Flag(2));
                return null;
            });
            Pair(283, "SetCollisionSize", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return true;

                // Body entirely in Core (UELevelRuntime.SetCollisionSize): the exec thunk
                // (disasm 0x1040a870) writes the third optional argument to
                // CollisionWidth BEFORE calling the body and returns true unconditionally.
                // The Ghidra pseudocode loses this write.
                //
                // An absent optional differs from a passed zero: `null` means
                // "bytecode gave no value" and Core keeps the current one — like
                // shipped, which seeds the argument buffer with the current property.
                Runtime.SetCollisionSize(self,
                    UEInterpreter.AsFloat(args is { Length: > 0 } ? args[0] : null),
                    UEInterpreter.AsFloat(args is { Length: > 1 } ? args[1] : null),
                    args is { Length: > 2 } && args[2] is not null
                        ? UEInterpreter.AsFloat(args[2]) : null);
                return true;
            });
            // TraceTexture (285) — body ENTIRELY in Core (UELevelRuntime.
            // TraceTextureNative), including resource resolution: two same-shaped
            // implementations are not delegation (C5).
            Pair(285, "TraceTexture", (interp, frame, args) =>
                Runtime.TraceTextureNative(interp, frame, args));
            Pair(266, "Move", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return false;
                // AActor::execMove (Engine.dll.c:119068) passes the actor's CURRENT
                // rotation to MoveActor (Move never rotates) and all trailing flags 0.
                return MoveActor(interp, self, UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null),
                    UEInterpreter.AsRotator(self.GetProp("Rotation")),
                    bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
            });
            Pair(267, "SetLocation", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return false;
                // AActor::execSetLocation (Engine.dll.c:73485) is
                // FarMoveActor(this, NewLocation, 0, 0) — not a bare field write.
                // HP1_TRACE_SETLOC diagnostics — probe hook (runtime_poc), no body here.
                SetLocationProbe?.Invoke(self, args);
                return FarMoveActor(interp, self, UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null),
                    bTest: false, bNoCheck: false);
            });
            Pair(299, "SetRotation", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return false;
                // AActor::execSetRotation (Engine.dll.c:15944) is MoveActor(this,
                // delta=(0,0,0), NewRotation, ...) — not a bare field write. The
                // zero delta hits the tiny-delta guard; a same-rotation SetRotation
                // returns true immediately, a real rotation goes the full path.
                return MoveActor(interp, self, Vector3.zero,
                    UEInterpreter.AsRotator(args.Length > 0 ? args[0] : null),
                    bTest: false, bIgnorePawns: false, bIgnoreBases: false, bNoFail: false);
            });
            // Color operators (Engine.Actor 549-552). Add 0x10413410 / Sub 0x104134b0: R,G,B bytewise with 8-bit wrap,
            // A kept from the FIRST operand. Color*Float 0x10413230 / Float*Color 0x10413320: per R,G,B channel
            // `fild dword(byte); fmul dword f; call _ftol 0x10459c18`, low byte stored; A kept from the color.
            Pair(551, "Add_ColorColor", (interp, frame, args) => ColorOp(args[0], args[1], (a, b) => a + b));
            Pair(549, "Subtract_ColorColor", (interp, frame, args) => ColorOp(args[0], args[1], (a, b) => a - b));
            Pair(552, "Multiply_ColorFloat", (interp, frame, args) => ColorScale(args[0], UEInterpreter.AsFloat(args[1])));
            Pair(550, "Multiply_FloatColor", (interp, frame, args) => ColorScale(args[1], UEInterpreter.AsFloat(args[0])));

            // Original: Engine.dll ?execPlayMusic@AActor@@QAEXAAUFFrame@@QAX@Z [HP2 0x10418ee0] (HP1: none)
            // Original: Engine.dll ?execStopAllMusic@AActor@@QAEXAAUFFrame@@QAX@Z [HP2 0x10419210] (HP1: none)
            // Original: Engine.dll ?execStopMusic@AActor@@QAEXAAUFFrame@@QAX@Z [HP2 0x10419140] (HP1: none)
            // After P_FINISH both test XLevel->Engine->Audio (+0x40, +0x5c): NULL -> PlayMusic's result 0
            // [HP2 0x10418f72..0x10418fba], StopAllMusic nothing [0x10419261..0x10419271]; otherwise Audio vtable +0x7c /
            // +0x84: UALAudioSubsystem::PlayMusic(Song, FadeInTime) (its answer the result) / StopAllMusic(FadeOutTime);
            // StopMusic: NULL -> nothing, else vtable +0x80 StopMusic(Source, FadeOutTime) [HP2 0x104191b5..0x104191d3]. The
            // port's HP2 Audio exists only after InitAudio (UEWorldFrameProducer.ALAudio).
            if (UEGameProfile.Current.Game == UEEdition.Game.HP2)
            {
                Pair(0, "PlayMusic", (interp, frame, args) => FrameProducer.ALAudio is { } al
                    ? al.PlayMusic(args.Length > 0 ? args[0] as string : "", args.Length > 1 ? UEInterpreter.AsFloat(args[1]) : 0f, out _) : 0);
                Pair(0, "StopMusic", (interp, frame, args) =>
                {
                    FrameProducer.ALAudio?.StopMusic(args.Length > 0 ? UEInterpreter.ToInt(args[0]) : 0,
                        args.Length > 1 ? UEInterpreter.AsFloat(args[1]) : 0f);
                    return null;
                });
                Pair(0, "StopAllMusic", (interp, frame, args) =>
                {
                    FrameProducer.ALAudio?.StopAllMusic(args.Length > 0 ? UEInterpreter.AsFloat(args[0]) : 0f);
                    return null;
                });
            }

            // Sound natives — BODIES IN CORE (UELevelRuntime.SoundNatives.cs): the
            // PlaySound/PlayOwnedSound listener walk, AActor::CheckHearSound, the
            // ClientHearSound event and its native (APawn::execClientHearSound, by name),
            // StopSound, ModifySound.
            Pair(264, "PlaySound", (interp, frame, args) =>
                Runtime.PlaySoundNative(interp, frame, args));
            Pair(0, "PlayOwnedSound", (interp, frame, args) =>
                Runtime.PlayOwnedSoundNative(interp, frame, args));
            Pair(0, "DemoPlaySound", (interp, frame, args) =>
                Runtime.DemoPlaySoundNative(interp, frame, args));
            Pair(0, "ClientHearSound", (interp, frame, args) =>
                Runtime.ClientHearSoundNative(interp, frame, args));
            Pair(568, "StopSound", (interp, frame, args) =>
                Runtime.StopSoundNative(interp, frame, args));
            Pair(567, "ModifySound", (interp, frame, args) =>
                Runtime.ModifySoundNative(interp, frame, args));
            // Noise records land on the instigator's slots, the delivery walks the
            // Core PawnList and CanHear writes the listener's Stimulus — all Core
            // world state (AActor::CheckNoiseHearing, Engine.dll.c:90601).
            Pair(512, "MakeNoise", (interp, frame, args) =>
                Runtime.MakeNoiseNative(interp, frame, args));
            Pair(298, "SetBase", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                SetBaseActor(interp, self, args.Length > 0 ? args[0] as UEObject : null, notify: true);
                return null;
            });
            // Pawn.uc native(553) FindPath(startPoint, dest). The shipped HP1 body
            // (Engine.dll.c APawn::findPath, 0x10402560) is not UE1's weighted
            // FindPathTo: it walks startPoint.Paths[16] with a fixed 100-entry DFS
            // stack/visited set and returns the first hop toward the first actor
            // whose FName equals dest. ReachSpecs come directly from ULevel in the
            // shipped .unr; no Unity scene graph, JSON export, Dijkstra weighting,
            // pruning or collision-size filter participates in this native.
            Pair(553, "FindPath", (interp, frame, args) => Runtime.FindPath(
                args.Length > 0 ? args[0] as UEObject : null,
                NameArg(args, 1)));
            // Path search natives (UELevelRuntime.NavNatives: ClearPaths, RequireNoPathNetwork, NoPathTail).
            // execFindPathTo 0x103d7f80 / execFindPathToward 0x103d8130: a null target returns None before any write;
            // otherwise the search, then NoPathTail. execFindRandomDest 0x103d82e0: clearPaths when bClearPaths
            // (default 1), then the search. execFindBestInventoryPath 0x103d4b70: weight 0.0f (0x1040230f) replaces
            // MinWeight only when strictly greater (`fcom; test ah,0x41; jne`, NaN keeps it), then NoPathTail.
            Pair(517, "FindPathToward", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null || args.Length == 0 || args[0] is not UEObject) return null;
                Runtime.RequireNoPathNetwork(interp, "FindPathToward");
                UELevelRuntime.NoPathTail(self);
                return null;
            });
            Pair(518, "FindPathTo", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                Runtime.RequireNoPathNetwork(interp, "FindPathTo");
                UELevelRuntime.NoPathTail(self);
                return null;
            });
            Pair(525, "FindRandomDest", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                if (args.Length == 0 || args[0] is null || UEInterpreter.ToBool(args[0])) Runtime.ClearPaths(interp, self);
                Runtime.RequireNoPathNetwork(interp, "FindRandomDest");
                return null;
            });
            Pair(540, "FindBestInventoryPath", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                Runtime.RequireNoPathNetwork(interp, "FindBestInventoryPath");
                if (args.Length > 0 && 0f > UEInterpreter.AsFloat(args[0]))
                {
                    args[0] = 0f;
                    UELevelRuntime.NoPathTail(self);
                }
                return null;
            });
            Pair(522, "ClearPaths", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is not null) Runtime.ClearPaths(interp, self);
                return null;
            });
            Pair(519, "describeSpec", (interp, frame, args) =>
            {
                Runtime.DescribeSpecNative(args);
                return null;
            });
            // EAdjustJump (523): execEAdjustJump 0x103d78d0 — SuggestJumpVelocity(Destination, a COPY of Velocity);
            // Velocity itself is not written.
            Pair(523, "EAdjustJump", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return UEVector.zero;
                return Runtime.SuggestJumpVelocity(self, UEInterpreter.AsVector3(self.GetProp(PK.Destination)),
                    UEInterpreter.AsVector3(self.GetProp(PK.Velocity)).z);
            });
            // Pawn.uc native(521) final function bool pointReachable(Vector aPoint).
            // APawn::execpointReachable (Engine.dll.c:99183) is the whole wrapper:
            // read the vector, call the mode dispatcher with bKnowVisible = 0, and
            // store what came back. It stores the MODE MASK, not a normalised 0/1 —
            // the declaration says bool but the exec body writes the int straight
            // into the result slot, so a caller can still learn HOW the point is
            // reachable. Nothing here belongs to a frontend: the query walks the
            // Core world and moves the Core pawn.
            // Pawn.uc native(520) final function bool actorReachable(Actor anActor).
            // The whole exec body lives in Core (ActorReachableNative) because it is
            // gameplay: an Inventory unwrap, a proximity shortcut over the level's
            // NavigationPointList and the reachability query itself. Nothing about
            // it is frontend-shaped.
            // Actor.uc native(257) final function vector BonePos(name Bone) —
            // AActor::execBonePos (Engine.dll.c:91802); body is Core
            // (UELevelRuntime.SkeletalPose.BonePosNative). Callers in the campaign:
            // Hog2/SausageRollKidOnAStick, Hog2/DevilsSnareNew, Hub5/HarryL502, Hub5/Fluffy.
            Pair(257, "BonePos", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return UEVector.zero;
                return Runtime.BonePosNative(self, args.Length > 0 ? args[0]?.ToString() : null);
            });
            // Actor.uc (HP2) native(328) final function rotator BoneRot(name Bone) (HP1: index 328 unregistered); body
            // UELevelRuntime.SkeletalPose.BoneRotNative.
            if (UEGameProfile.Current.Game == UEEdition.Game.HP2)
                Pair(328, "BoneRot", (interp, frame, args) =>
                    Runtime.BoneRotNative(frame.Context ?? frame.Object, args.Length > 0 ? args[0]?.ToString() : null));
            Pair(520, "actorReachable", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return 0;
                return Runtime.ActorReachableNative(interp, self,
                    args.Length > 0 ? args[0] as UEObject : null);
            });
            Pair(521, "pointReachable", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return 0;
                return Runtime.PointReachable(interp, self,
                    UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null),
                    bKnowVisible: false);
            });
            // Pawn.uc native(524) final function int FindStairRotation(float DeltaTime).
            // The ONLY unregistered native statically reachable from the player's normal
            // Tut1 state: `Engine.PlayerWalking.PlayerMove` calls it on itself. Body
            // entirely in Core — three or four world traces and an actor-field
            // interpolation, nothing frontend-shaped.
            Pair(524, "FindStairRotation", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return 0;
                return Runtime.FindStairRotation(interp, self,
                    UEInterpreter.AsFloat(args.Length > 0 ? args[0] : null));
            });
            // Gesture.uc native(426) final function float CompareGesture(
            //     array<vector> Drawn, float Accuracy)
            // and native(427) final function float CompareGesturePoint(
            //     vector Point, float Accuracy).
            // Both are gameplay: they are the spell-lesson judge
            // (HPBase.SpellLearnTrigger.Judge :757 and HPBase.Judge.Tick :705), and
            // the whole body — dedup, densify, score — is Core's.
            Pair(426, "CompareGesture", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return 0f;
                return Runtime.CompareGestureNative(self,
                    args.Length > 0 ? args[0] : null,
                    UEValue.AsFloat(args.Length > 1 ? args[1] : null));
            });
            Pair(427, "CompareGesturePoint", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return 0f;
                return Runtime.CompareGesturePointNative(self,
                    UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null));
            });
            Pair(272, "SetOwner", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                SetOwnerActor(interp, self, args.Length > 0 ? args[0] as UEObject : null);
                return null;
            });
            // SetPhysics: Actor.SetPhysics native=3970 in HP1 Engine.u (--dumpclass Actor).
            // Single arg (NewPhysics); the shipped floor arg is null here.
            Pair(3970, "SetPhysics", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                SetPhysicsActor(interp, self, UEInterpreter.ToInt(args.Length > 0 ? args[0] : null), newFloor: null);
                return null;
            });
            // MoveTo (500, latent): APawn::execMoveTo (Engine.dll.c:85918). Set
            // Destination/Focus, DesiredSpeed = clamp(speed, 0, MaxDesiredSpeed),
            // setMoveTimer(dist), install the latent poll and prime one rotate+move.
            Pair(500, "MoveTo", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                Vector3 dest = UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null);
                float speed = args.Length > 1 && args[1] is not null ? UEInterpreter.AsFloat(args[1]) : 1f;
                UEPawnMovement.EnterMoveTo(self, frame, dest, speed, interp);
                return null;
            });
            // MoveToward (502): shipped APawn::execMoveToward (Engine.dll.c:80783).
            // Latent move toward a live ACTOR: bReducedSpeed=false, DesiredSpeed =
            // clamp(min(speed, MaxDesiredSpeed), >=0); a PAWN target (disasm
            // 0x103d8880) uses the fixed MoveTimer=1.2f instead of setMoveTimer(dist); MoveTarget is
            // kept and Destination/Focus start at its current Location (the poll
            // re-tracks it each tick). A null target starts nothing.
            Pair(502, "MoveToward", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                if (args.Length == 0 || args[0] is not UEObject target) return null;
                float speed = args.Length > 1 && args[1] is not null ? UEInterpreter.AsFloat(args[1]) : 1f;
                UEPawnMovement.EnterMoveToward(self, frame, target, speed, interp);
                return null;
            });
            // StrafeTo (504) / StrafeFacing (506) / WaitForLanding (527), latent: bodies in UEPawnMovement
            // (EnterStrafeTo / EnterStrafeFacing / EnterWaitForLanding), polls in ProcessState.
            Pair(504, "StrafeTo", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                UEPawnMovement.EnterStrafeTo(self, frame, UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null),
                    UEInterpreter.AsVector3(args.Length > 1 ? args[1] : null), interp);
                return null;
            });
            Pair(506, "StrafeFacing", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                UEPawnMovement.EnterStrafeFacing(self, frame, UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null),
                    args.Length > 1 ? args[1] as UEObject : null, interp);
                return null;
            });
            Pair(527, "WaitForLanding", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is not null) UEPawnMovement.EnterWaitForLanding(self);
                return null;
            });
            // MoveSmooth (3969, bound by name): shipped execMoveSmooth
            // (Engine.dll.c:62637) clears bJustTeleported, then returns
            // AActor::moveSmooth's first-move result verbatim.
            Pair(3969, "MoveSmooth", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return false;
                return Runtime.ExecMoveSmooth(interp, self, UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null));
            });
            // Destroy (279): execDestroy -> ULevel::DestroyActor (Engine.dll.c:252940..253089).
            // Mark deleted (the VM then reads it as Accessed-None), drop the base,
            // fire Destroyed, UnTouch everything, orphan owner and children. The
            // collision/light/bsp de-registration is implicit in the headless model.
            Pair(279, "Destroy", (interp, frame, args) =>
                DestroyActorCore(interp, frame.Context ?? frame.Object));
            Pair(278, "Spawn", Spawn);
            Pair(268, "BoneNumber", (interp, frame, args) =>
                Runtime.BoneNumberNative(interp, frame, args));
            Pair(269, "BoneName", (interp, frame, args) =>
                Runtime.BoneNameNative(interp, frame, args));
            Pair(274, "GetRenderExtent", (interp, frame, args) =>
                Runtime.GetRenderExtentNative(interp, frame, args));
            Pair(314, "Warp", (interp, frame, args) =>
                Runtime.WarpNative(interp, frame, args));
            Pair(315, "UnWarp", (interp, frame, args) =>
                Runtime.UnWarpNative(interp, frame, args));
            Pair(265, "CreateAnimChannel", CreateAnimChannel);
            Pair(277, "Trace", HeadlessTrace);
            Pair(309, "TraceActors", HeadlessTraceActors);
            Pair(310, "RadiusActors", (interp, frame, args) =>
                Runtime.RadiusActorsNative(interp, frame, args));
            Pair(311, "VisibleActors", (interp, frame, args) =>
                Runtime.VisibleActorsNative(interp, frame, args));
            Pair(548, "FastTrace", (interp, frame, args) =>
                Runtime.FastTraceNative(interp, frame, args));
            Pair(514, "LineOfSightTo", (interp, frame, args) =>
                Runtime.LineOfSightToNative(interp, frame, args));
            Pair(533, "CanSee", (interp, frame, args) =>
                Runtime.CanSeeNative(interp, frame, args));
            Pair(532, "PlayerCanSeeMe", (interp, frame, args) =>
                Runtime.PlayerCanSeeMeNative(interp, frame, args));
            Pair(534, "PickAnyTarget", (interp, frame, args) =>
                Runtime.PickAnyTargetNative(interp, frame, args));
            // ALevelInfo::execGetLocalURL — FURL::String of the level URL (UELevelRuntime.LevelUrl.cs).
            Pair(0, "GetLocalURL", (interp, frame, args) => Runtime.LocalUrlNative());
            Pair(0, "GetAddressURL", (interp, frame, args) => Runtime.AddressUrlNative());
            // Original: Engine.dll ?execGetNetworkNumber@AGameInfo@@QAEXAAUFFrame@@QAX@Z [HP1 0x1040feb0] [HP2 0x1041e6b0]
            // P_FINISH; XLevel->NetDriver (ULevel +0x3c) ? NetDriver->LowLevelGetNetworkNumber() (vtable +0x60) : "".
            // The port has no net driver (standalone only): "".
            Pair(0, "GetNetworkNumber", (interp, frame, args) => "");
            // Original: Engine.dll ?execGetPlayerNetworkAddress@APlayerPawn@@QAEXAAUFFrame@@QAX@Z [HP1 0x10407470] [HP2 0x10414b30]
            // P_FINISH; Player IsA UNetConnection ? Player->LowLevelGetRemoteAddress() (vtable +0x5c) : "". The port's
            // Player is the viewport or none, never a connection: "".
            Pair(0, "GetPlayerNetworkAddress", (interp, frame, args) => "");
            // Original: Engine.dll ?execParseKillMessage@AGameInfo@@QAEXAAUFFrame@@QAX@Z [HP1 0x10410160] [HP2 0x1041e970]
            Pair(0, "ParseKillMessage", (interp, frame, args) =>
                ParseKillMessage(StrArg(args, 0), StrArg(args, 1), StrArg(args, 2), StrArg(args, 3)));
            // Original: Engine.dll ?execGetCurrentKeyState@AActor@@QAEXAAUFFrame@@QAX@Z [HP2 0x10422c60] (thunk 0x1030345e;
            // HP1: index 330 unregistered). P_GET_BYTE(Key), P_FINISH; XLevel(+0x94)->Engine(+0x40)->Client(+0x54)->
            // Viewports(0) (+0x2c): none -> false; else that viewport's Input(+0x58) KeyDownTable[Min(Key, 0xfe)] (+0xeb0,
            // UInput::KeyDown inlined) into the result byte. The one viewport is the runtime's, its UInput the port's UEInput.
            if (UEGameProfile.Current.Game == UEEdition.Game.HP2)
                Pair(330, "GetCurrentKeyState", (interp, frame, args) =>
                    Runtime.Viewport is not null && Runtime.Input.KeyDownTable(UEInterpreter.ToInt(args[0])));
            // 321 CreateTextureFromBMP — Core body (RegisterLocalizationOps ->
            // UETextureImport). 322 CreateTextureFromScreenShot: a raster frame
            // snapshot is a frontend boundary; headless has no frame. LOUD debt: None
            // instead of a texture.
            Pair(322, "CreateTextureFromScreenShot", (interp, frame, args) =>
            { _journal.Add($"stop_texture_from_screenshot name={args[0]}"); return null; });
            // Actor.uc:1059 native(280) SetTimer(float NewTimerRate, bool bLoop):
            // schedule the Timer() event after NewTimerRate seconds, repeating if
            // bLoop; NewTimerRate<=0 cancels. Pure field writes; the count-up and
            // Timer() dispatch live in the deterministic actor tick below.
            // Decal.AttachDecal / Decal.DetachDecal — name natives (declared
            // `native simulated function` WITHOUT an index, `--dumpclass Decal`).
            // BOTH legs go to Core: the shadow builds the request itself from the decal
            // actor properties set by the `ActorShadow.Update` bytecode.
            Pair(0, "AttachDecal", (interp, frame, args) =>
            {
                object result = Runtime.AttachDecalNative(interp, frame, args);
                if (DetailedJournal && !_journalOff)
                {
                    UEObject d = frame.Context ?? frame.Object;
                    string verdict = result is not null && d?.GetProp("SurfList") is System.Collections.IList sl
                        ? $"surfs=[{string.Join(",", System.Linq.Enumerable.Cast<object>(sl))}]"
                        : "refused";
                    _journal.Add($"decal_attach actor={d?.GetProp("Name")} {verdict}");
                }
                return result;
            });
            Pair(0, "DetachDecal", (interp, frame, args) =>
            {
                if (DetailedJournal && !_journalOff)
                    _journal.Add($"decal_detach actor={(frame.Context ?? frame.Object)?.GetProp("Name")}");
                return Runtime.DetachDecalNative(interp, frame, args);
            });
            // APawn::execTurnTo / execPollTurnTo (Engine.dll.c:71384, 48528):
            // clear MoveTarget, store the fixed Focus, install the latent action,
            // update flying/swimming acceleration, and call rotateToward once. The
            // initial return value is deliberately ignored — even an already-facing
            // pawn resumes only when the poll runs.
            Pair(508, "TurnTo", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                UEPawnMovement.EnterTurnTo(self, frame,
                    UEInterpreter.AsVector3(args.Length > 0 ? args[0] : null));
                return null;
            });
            // APawn::execTurnToward (Engine.dll.c:40203): a null target is a true
            // no-op. Otherwise store FaceTarget, install the latent action, and aim
            // at its current Location; the poll refreshes that Location every tick.
            Pair(510, "TurnToward", (interp, frame, args) =>
            {
                UEObject self = frame.Context ?? frame.Object;
                if (self is null) return null;
                UEPawnMovement.EnterTurnToward(self, frame,
                    args.Length > 0 ? args[0] as UEObject : null);
                return null;
            });
            // FinishInterpolation (301, latent): AActor::execFinishInterpolation
            // (disasm 0x104083b0-0x104083c5) — P_FINISH and LatentAction = 302 UNCONDITIONALLY.
            // Only the poll (0x104084c0) checks bInterpolating on the next
            // ProcessState, so an already stopped mover continues its code one tick later.
            Pair(301, "FinishInterpolation", (interp, frame, args) =>
            {
                frame.LatentState = LatentRunState.FinishInterpolation;
                return null;
            });
            // Sleep/SetTimer/AddPawn/RemovePawn — Core bodies (C7).
            RegisterActorTimerAndPawnListOps();
            // PickTarget (531) and the whole sight family are Core world queries:
            // they walk Core-owned actor/pawn lists, mutate Pawn sight state and
            // consume the shared appFrand stream.
            Pair(531, "PickTarget", (interp, frame, args) =>
                Runtime.PickTargetNative(interp, frame, args));
            // Original: Engine.dll ?execConsoleCommand@APlayerPawn@@QAEXAAUFFrame@@QAX@Z [HP1 0x103d5770] [HP2 0x103e2c50]
            // Original: Engine.dll ?execConsoleCommand@AActor@@QAEXAAUFFrame@@QAX@Z [HP1 0x10406d50] [HP2 0x104143d0]
            // UConsole::execConsoleCommand [HP1 0x1038b460] [HP2 0x10393840] (UBOOL Viewport->Exec, its text to the console) is NOT
            // this body, yet binds here by its name: a Console.ConsoleCommand call gets this string answer (not ported).
            Pair(0, "ConsoleCommand", (interp, frame, args) =>
            {
                // Core-owned (UEConsoleCommand.Execute): the RESULT is gameplay —
                // Harry.PostBeginPlay derives b3DSound from it — so it cannot be a
                // per-frontend stub.
                // Key bindings are owned by the runtime's UEInput (the bootstrap loads it
                // from [Engine.Input]); KEYBINDING needs it. The viewport device is the
                // frontend's: GetCurrentRes/SetRes must see the ACTUAL canvas, not what the ini saved.
                return UEConsoleCommand.Execute(Runtime?.Config, args.Length > 0 ? args[0]?.ToString() : null,
                    Runtime?.Input, _viewport, interp, FrameProducer?.ALAudio, LiveActors());
            });
        }

        // The port's live objects of an actor class (GlobalSetProperty's GObjObjects walk): the game level's and the
        // resident Entry's actors.
        internal IEnumerable<UEObject> LiveActors()
            => MainRuntime.EntryWorld is { } e ? System.Linq.Enumerable.Concat(MainRuntime.Actors, e.Actors) : MainRuntime.Actors;

        private static string NameArg(object[] args, int index)
            => args.Length <= index || args[index] is null ? "None"
             : args[index] is UEName name ? name.Value : args[index].ToString();

        static string StrArg(object[] args, int i) => (i < args.Length ? args[i] : null)?.ToString() ?? "";

        // Original: Engine.dll ?execParseKillMessage@AGameInfo@@QAEXAAUFFrame@@QAX@Z [HP1 0x10410160] [HP2 0x1041e970]
        // Temp = DeathMessage; for (%k Killer, %o Victim, %w Weapon): the first occurrence (appStrstr / InStr, exact) ->
        // Message = Temp.Left(i) + name + Temp.Right(Len - i - 2); then Temp = Message UNCONDITIONALLY (HP1 0x104103b9 is
        // reached from both misses of the %k test, 0x104104c7 from both of %o): no "%k" in the message leaves Message
        // empty and the result is "". Result = Message. Corpus: se_harness/bsp_diff/data/parsekill.tsv (SelfTest parsekill_corpus).
        internal static string ParseKillMessage(string killer, string victim, string weapon, string death)
        {
            string temp = death, message = "";
            foreach (var (tag, name) in new[] { ("%k", killer), ("%o", victim), ("%w", weapon) })
            {
                int i = temp.IndexOf(tag, StringComparison.Ordinal);
                if (i >= 0) message = temp.Substring(0, i) + name + temp.Substring(i + 2);
                temp = message;
            }
            return message;
        }

        internal static bool SaveGameExists(string saveDir)
        {
            var f = new FileInfo(UELevelRuntime.SaveBodyPath(saveDir, "save9"));
            return f.Exists && f.Length > 0;
        }

        // Original: Engine.dll APlayerPawn::execScreenToWorld [HP1 0x103d5ac0] (export 0x36b1; Engine.dll.c:103011, disasm
        // 0x103d5ac0..0x103d5cd6). The body sets no control word: PC64 in game logic, PC24 inside the render window
        // (HUD/console script), every register operation rounding at that precision (pc24).
        //   tan = appTan(double(FovAngle·0.008726646f))       fld; fmul dword 0x10474588; fstp qword; call; fstp qword
        //   f   = float(Z / (SizeX / tan))                    fild SizeX; fdiv qword tan; fld Z; fdiv st,st(1); fstp dword
        //   V   = (float((2X − SizeX)·f), float((2Y − SizeY)·f), Z)   fadd st,st; fisub; fmul dword f; fstp dword
        //   C   = GMath.UnitCoords *= CameraRotation            (Core FCoords::operator*=(FRotator), import 0x10608cd0)
        //   R   = (float((V.x·C.X.x + V.y·C.X.y) + V.z·C.X.z), the same with C.Y and C.Z)   in the register, 0x103d5c40..
        //   out = CameraLocation + R, each component fadd dword then fstp dword            0x103d5c97..0x103d5cc6
        // CameraLocation/Rotation are PlayerCalcView's out values; FovAngle and the viewport size are read before it.
        internal static UEVector ScreenToWorld(UEVector s, int sizeX, int sizeY, float fov, UEVector camLoc, UERotator camRot, bool pc24)
        {
            X87Ext R(X87Ext v) => pc24 ? X87Ext.Round24(v) : X87Ext.Round64(v);
            X87Ext L(double v) => X87Ext.From(v);
            double tanArg = pc24 ? (double)(fov * 0.008726646f) : (double)fov * (double)0.008726646f;
            double tan = UEX87Transcendental.AppTan(L(tanArg)).ToDouble();
            X87Ext q = pc24 ? X87Ext.Div24(L(sizeX), L(tan)) : X87Ext.Div64(L(sizeX), L(tan));
            float f = (pc24 ? X87Ext.Div24(L(s.z), q) : X87Ext.Div64(L(s.z), q)).ToFloat();
            float vy = R(R(R(L(s.y) + L(s.y)) - L(sizeY)) * L(f)).ToFloat();
            float vx = R(R(R(L(s.x) + L(s.x)) - L(sizeX)) * L(f)).ToFloat();
            float vz = s.z;
            float[] ax = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
            if (pc24) HeadlessBspModel.MovingBrushTracker.MulRotator<FpuPc24>(ax, camRot.Pitch, camRot.Yaw, camRot.Roll);
            else HeadlessBspModel.MovingBrushTracker.MulRotator<FpuPc64>(ax, camRot.Pitch, camRot.Yaw, camRot.Roll);
            float Row(int r) => R(R(R(L(vx) * L(ax[r])) + R(L(vy) * L(ax[r + 1]))) + R(L(vz) * L(ax[r + 2]))).ToFloat();
            float rx = Row(0), ry = Row(3), rz = Row(6);
            return new UEVector(R(L(camLoc.x) + L(rx)).ToFloat(), R(L(camLoc.y) + L(ry)).ToFloat(), R(L(camLoc.z) + L(rz)).ToFloat());
        }

        // execGetNextInt/execGetNextIntDesc: the class lookup, the registry list and List(Num) (null = past the end).
        // StaticFindObjectChecked(UClass, ANY_PACKAGE, Name): "Pkg.Class" resolves the package (ResolveName); a bare name
        // gets Outer NULL, so the GObjHash chain (hash Name ^ 0) holds no class (their Outer is a package), and the
        // ANY_PACKAGE scan of StaticFindObject (Core 0x1015ba69..0x1015bb34) returns the object with the LOWEST
        // GObjObjects index whose Name matches and whose class IsA UClass (D06). A miss is appErrorf (fatal). The port
        // keeps no GObjObjects index (object creation order, indices reused from GObjAvailable after a purge), so a bare
        // name naming classes in two packages is refused (STOP), as is a miss. Num < 0 reads outside the TArray (no
        // check in the shipped body): refused.
        // The class resolution and its filtered list are cached per name (case-insensitive, like the FName lookup) while the
        // class catalogue (UEClassRegistry.Generation) and the driver lists (UERegistry.Generation) are unchanged.
        private static readonly Dictionary<string, (UEClassRegistry Reg, int RegGen, int DrvGen, List<UERegistry.ObjectInfo> List)> _nextIntCache
            = new(StringComparer.OrdinalIgnoreCase);

        private static UERegistry.ObjectInfo NextInt(object[] args)
        {
            string name = (args.Length > 0 ? args[0] : null)?.ToString() ?? "";
            int num = args.Length > 1 ? UEInterpreter.ToInt(args[1]) : 0;
            UERegistry.CacheDrivers(false);
            UEClassRegistry reg = UEClassRegistry.Shared;
            if (_nextIntCache.TryGetValue(name, out var hit) && ReferenceEquals(hit.Reg, reg) && hit.RegGen == reg.Generation
                && hit.DrvGen == UERegistry.Generation)
            {
                if (num < 0) throw new InvalidOperationException($"GetNextInt('{name}', {num}): negative Num reads outside the list in the shipped body");
                return num < hit.List.Count ? hit.List[num] : null;
            }
            UClass meta = UEObjectConfig.FindClassAnyPackage(name)
                ?? throw new InvalidOperationException($"GetNextInt('{name}'): StaticFindObjectChecked(UClass, ANY_PACKAGE) found "
                    + "no class (shipped: appErrorf, fatal)");
            if (num < 0) throw new InvalidOperationException($"GetNextInt('{name}', {num}): negative Num reads outside the list in the shipped body");
            List<UERegistry.ObjectInfo> list = UERegistry.GetRegistryObjects("Class", UEObjectConfig.ClassPath(meta));
            _nextIntCache[name] = (reg, reg.Generation, UERegistry.Generation, list);
            return num < list.Count ? list[num] : null;
        }

        // Arg parsing only — AActor::CreateAnimChannel itself is Core
        // (UELevelRuntime.CreateAnimChannel): a channel is a spawned actor, so the
        // spawn order, encroachment, hash residency and destroy path are world
        // state.
        // Original: Engine.dll ?execCreateAnimChannel@AActor@@QAEXAAUFFrame@@QAX@Z [HP1 0x10408C50] [HP2 0x104165B0]
        private object CreateAnimChannel(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UEObject owner = frame.Context ?? frame.Object;
            UClass cls = args.Length > 0 ? args[0] as UClass : null;
            int animType = args.Length > 1 ? UEInterpreter.ToInt(args[1]) : 0;
            string boneName = args.Length > 2 && args[2] is UEName ueName
                ? ueName.Value : args.Length > 2 ? args[2]?.ToString() : null;
            bool autoDestroy = args.Length > 3 && UEInterpreter.ToBool(args[3]);
            // HP2's fifth parameter, optional bool bNotReplaceable (HP1 declares four).
            bool notReplaceable = args.Length > 4 && UEInterpreter.ToBool(args[4]);
            // HP2's exec does not zero the EAnimType dword before the byte argument lands in it [HP2 0x10416620] and the body
            // compares the whole dword with 1 [HP2 0x1041611a]: a byte 1 (AT_Combine) depends on the stack bytes above it,
            // a byte 0 or >= 2 never equals 1 (every shipped HP2 script call passes ByteConst 0). Undefined: a loud STOP.
            if (UEGameProfile.Current.Game == UEEdition.Game.HP2 && animType == 1)
                UEGameProfile.Stop(UEEdition.Game.HP2, "execCreateAnimChannel AT_Combine (the original compares an EAnimType dword "
                    + "whose upper bytes its exec never writes)");
            return Runtime.CreateAnimChannel(interp, owner, cls, animType, boneName, autoDestroy, notReplaceable);
        }

        private object Spawn(UEInterpreter interp, UEFrame frame, object[] args)
        {
            UClass cls = args.Length > 0 ? args[0] as UClass : null;
            if (cls is null) return null;
            // (Import-stub canonicalisation lives in UELevelRuntime.SpawnActor, one step
            //  for all frontends. The stub and the real class share a name, so the
            //  tag computed below does not change.)
            UEObject self = frame.Context ?? frame.Object;
            Vector3 location = args.Length > 3 && args[3] is not null
                ? UEInterpreter.AsVector3(args[3])
                : UEInterpreter.AsVector3(self?.GetProp("Location"));
            UERotator rotation = args.Length > 4 && args[4] is not null
                ? UEInterpreter.AsRotator(args[4])
                : UEInterpreter.AsRotator(self?.GetProp("Rotation"));
            UEObject owner = args.Length > 1 ? args[1] as UEObject : null;
            string tag = (args.Length > 2) switch
            {
                true when args[2] is UEName name => name.Value,
                true => args[2]?.ToString(),
                false => null
            };
            // execSpawn defaults SpawnTag to NAME_None and writes it unconditionally
            // (Engine 0x1040ca74, 0x1040cb87) — no class-name fallback here.
            if (string.IsNullOrEmpty(tag)) tag = "None";
            return SpawnCore(interp, self, cls, owner, tag, location, rotation);
        }

        // The spawn body behind the Spawn native (arg parsing stays in the lambda).
        // Split out so the selftest can drive spawns without a script frame.
        // ULevel::SpawnActor — Core-owned (UELevelRuntime.SpawnActor); FindSpot,
        // encroachment, hash residency and the base probe ride the spawn hooks.
        internal UEObject SpawnCore(UEInterpreter interp, UEObject self, UClass cls, UEObject owner,
            string tag, Vector3 location, UERotator rotation)
            => Runtime.SpawnActor(interp, self, cls, owner, tag, location, rotation,
                instigator: self?.GetProp("Instigator") as UEObject);

        // ULevel::DestroyActor — Core-owned (UELevelRuntime.DestroyActor); the
        // collision-hash/root-motion teardown rides the DestroyCleanupHook.
        internal bool DestroyActorCore(UEInterpreter interp, UEObject self)
            => Runtime.DestroyActor(interp, self);

        // DELEGATE. The query body lives in Core (UELevelRuntime.TraceNative). Only the
        // headless precondition and the journal line stay here: the journal is pinned by SHA
        // and belongs to this frontend.
        private object HeadlessTrace(UEInterpreter interp, UEFrame frame, object[] args)
        {
            if (_bsp is null) throw new InvalidOperationException("headless BSP not loaded");
            UEObject self = frame.Context ?? frame.Object;
            Vector3 end = UEInterpreter.AsVector3(args.Length > 2 ? args[2] : null);
            Vector3 start = args.Length > 3 && args[3] is not null
                ? UEInterpreter.AsVector3(args[3])
                : UEInterpreter.AsVector3(self?.GetProp("Location"));
            bool traceActors = args.Length > 4 && args[4] is not null
                ? UEInterpreter.ToBool(args[4])
                : UEInterpreter.ToBool(self?.GetProp("bCollideActors"));
            Vector3 extent = args.Length > 5 && args[5] is not null
                ? UEInterpreter.AsVector3(args[5]) : Vector3.zero;

            UEObject bestObject = Runtime.TraceNative(interp, frame, args, out var hit);
            bool blocked = bestObject is not null;
            float bestFraction = blocked ? hit.Time : 1f;
            int node = blocked ? hit.Node : -1;
            _journal.Add($"trace self={self?.GetProp("Name")} start={start} end={end} extent={extent} actors={traceActors.ToString().ToLowerInvariant()} blocked={blocked.ToString().ToLowerInvariant()} fraction={bestFraction:R} object={bestObject?.GetProp("Name")} node={node}");
            return bestObject;
        }

        // DELEGATE — see HeadlessTrace. Engine order, clipping the actor ray by the
        // world and recording a world hit (Actor = LevelInfo) belong to Core.
        private object HeadlessTraceActors(UEInterpreter interp, UEFrame frame, object[] args)
        {
            if (_bsp is null) throw new InvalidOperationException("headless BSP not loaded");
            UEObject self = frame.Context ?? frame.Object;
            string baseName = (args.Length > 0) switch
            {
                true when args[0] is UClass cls => cls.Name,
                true when args[0] is UEName name => name.Value,
                true => args[0]?.ToString(),
                false => null
            };
            Vector3 end = UEInterpreter.AsVector3(args.Length > 4 ? args[4] : null);
            Vector3 start = args.Length > 5 && args[5] is not null
                ? UEInterpreter.AsVector3(args[5])
                : UEInterpreter.AsVector3(self?.GetProp("Location"));
            Vector3 extent = args.Length > 6 && args[6] is not null
                ? UEInterpreter.AsVector3(args[6]) : Vector3.zero;

            Runtime.TraceActorsNative(interp, frame, args);
            int hits = interp.PendingIterator is UETraceActorsIterator it ? it.Count : 0;
            _journal.Add($"trace_actors self={self?.GetProp("Name")} base={baseName} start={start} end={end} extent={extent} hits={hits}");
            return null;
        }

        // ── PROBE HOOKS ────────────────────────────────────────────────────────────
        //
        // Probes, comparison journals and route checkpoints live in runtime_poc
        // (HeadlessNatives) and plug in here, at the SAME loop positions their bodies
        // occupied before the move. The engine (hpengine) needs no hooks: an empty hook is
        // exactly the behaviour with no HP1_* variables set.
        //   TickBegin         — tick start, after _currentTick (HP1_TRACE, sampler);
        //   InputHook         — after the "tick N begin" line, before the travel probe;
        //   BeforeActorsPass  — after the travel probe, before the actor pass;
        //   BeforeFrame       — before the engine frame (HP1_SURF_LM_POKE);
        //   AfterFrame        — after the frame, before the "tick N end" line;
        //   JournalStream     — journal flush when streamJournal;
        //   TickWorldEnd      — after the last tick of the call;
        //   SetLocationProbe  — in native 267 before FarMoveActor (HP1_TRACE_SETLOC);
        //   ActorTickBoundaryProbe — sink for actor-pass exceptions (HP1_SURVEY).
        public Action<int> TickBegin;
        public Action<UEInterpreter, int, float> InputHook;
        public Action<UEInterpreter, int> BeforeActorsPass;
        public Action<int> BeforeFrame;
        public Action<UEInterpreter, int, WorldFrame, float> AfterFrame;
        public Action JournalStream;
        public Action<UEInterpreter> TickWorldEnd;
        public Action<UEObject, object[]> SetLocationProbe;
        public Action<Exception> ActorTickBoundaryProbe;

        // `firstTick` — number of this call's first tick. The engine frame is cached BY
        // TICK NUMBER (UEWorldFrameProducer.Build): calling TickWorld(1) in a loop
        // without it would build "tick 0" every time and get the same frame —
        // no new audio commands, particles or HUD PostRender. Regular callers make one
        // call from zero and pass 0.
        public void TickWorld(UEInterpreter interp, int tickCount, float deltaSeconds,
            bool streamJournal = false, int firstTick = 0)
        {
            for (int tick = firstTick; tick < firstTick + tickCount; tick++)
            {
                _currentTick = tick;
                TickBegin?.Invoke(tick);   // probe hook: sampler tick 50, HP1_TRACE
                if (HP1.Core.UEPlaythroughLog.Enabled) HP1.Core.UEPlaythroughLog.Tick = tick;
                // The first boot's frame — before the start level's first tick (window opening;
                // (UELevelRuntime.EngineFrameOwedAfterLoad). It takes its number from the inside-tick
                // sequence (UELevelRuntime.NextInsideTickFrameNumber): the producer cache is keyed by number, so neither the
                // tick frame nor a fade / progress frame of the same tick may share it.
                if (Runtime.EngineFrameOwedAfterLoad && Runtime.FrameProducer is not null)
                    Runtime.RunEngineFrame(interp, Runtime.NextInsideTickFrameNumber(), "load", deltaSeconds);
                // HP2: the top of UGameEngine::Tick, the device's appSeconds (UEWorldFrameProducer.AdvanceAudioClock).
                if (Runtime.FrameProducer?.ALAudio is not null) Runtime.FrameProducer.AdvanceAudioClockForTick(deltaSeconds);
                float levelDelta = AdvanceLevelClock(interp, deltaSeconds);
                _journal.Add($"tick {tick} begin dt={levelDelta:R} actors={_actors.Count}");
                // Input — hook point (harness: HP1_TRACE_SETLOC and the fixture input
                // scenario; the engine feeds Core.Input before Tick itself).
                InputHook?.Invoke(interp, tick, levelDelta);
                // Travel probe: HP1_TRAVEL_AT=<tick>:<URL>[:0] calls the REAL shipped
                // function LevelInfo.ServerTravel — the same one a cutscene CHANGELEVEL
                // reaches. Only the call moment differs. This checks the ENGINE half,
                // it does not replace the scripted one.
                //
                // bItems defaults to TRUE because that is the game route:
                // CutScene.uc:745, CutScriptII.uc:360, TriggerChangeLevel.uc:34,
                // FEBook.uc:411 and BroomPracticeReferee.uc:769 all call
                // ChangeLevel(..., true). The ":0" suffix keeps the rare no-carry branch available.
                if (_travelProbe.TryGetValue(tick, out (string Url, bool Items) probe))
                {
                    UEObject lvl = interp.GetLevelInfo();
                    UFunction st = interp.FindEventFunction(lvl, "ServerTravel")
                        ?? throw new InvalidDataException("LevelInfo.ServerTravel missing");
                    _journal.Add($"travel_probe tick={tick} url={probe.Url} items={probe.Items}");
                    interp.Invoke(lvl, st, probe.Url, probe.Items);
                }
                // Probes before the actor pass (runtime_poc: HP1_CHANGELEVEL_AT,
                // HP1_TELEPORT, HP1_KILL_AT, HP1_SAVE_AT, HP1_LOAD_AT).
                BeforeActorsPass?.Invoke(interp, tick);
                TickActorsPass(interp, levelDelta);
                // GEntry->Tick right after the game level (FUN_103a0900,
                // Engine.dll.c:249090-249096) — with the same raw DeltaSeconds.
                MainRuntime.TickEntryWorld(interp, deltaSeconds);
                // HP2 UGameEngine::Tick [HP2 0x103a9b53..0x103a9ce4], right after the level ticks: with a client viewport whose
                // Actor has bQueuedToSaveGame (+0x958 & 1, PlayerPawn.SaveGame sets it): MakeDirectory x2, the log, the bit
                // cleared, SaveGame(0), Exec("RestartSounds"), Flush(1), and Tick returns — no Browse (0x103aa323) and no
                // Client->Tick this frame (the frame: as a browsed one's, nothing new drawn).
                bool queuedSave = UEGameProfile.Current.Game == UEEdition.Game.HP2 && _viewport is not null
                    && Runtime.Viewport?.GetProp(PK.Actor) is UEObject qa && UEInterpreter.ToBool(qa.GetProp("bQueuedToSaveGame"));
                if (queuedSave)
                {
                    Directory.CreateDirectory(SaveDir); Directory.CreateDirectory(GameSaveDir);
                    _journal.Add($"queued_savegame file={Path.GetFileName(SaveFilePath("save0"))}");
                    ((UEObject)Runtime.Viewport.GetProp(PK.Actor)).Props["bQueuedToSaveGame"] = false;
                    UEConsoleCommand.SaveGameHandler(0);
                    UEConsoleCommand.Execute(Runtime?.Config, "RestartSounds", Runtime?.Input, _viewport, interp, FrameProducer?.ALAudio);
                    UEConsoleCommand.FlushHandler?.Invoke();
                }
                // Engine half of level travel: the script has already put the request
                // in LevelInfo (ServerTravel -> ProcessServerTravel), Core takes it, and
                // this runner turns the map name into a path — the file system belongs
                // to the frontend, not Core.
                // bItems is kept: it is the main game route (CutScene.uc:745 and all
                // ChangeLevel calls pass true). The carry gate is read from LevelInfo.bNextItems
                // inside Runtime.GetTravelInfo — exactly where shipped reads it
                // (Engine.dll.c:249177) — so here it is only journalled, not passed by a
                // second path (one source of truth).
                string travelUrl = null; bool travelItems = false;
                bool browsed = !queuedSave && Runtime.TryTakeLevelTravel(interp, deltaSeconds, out travelUrl, out travelItems);
                if (browsed)
                {
                    _journal.Add($"travel_request url={travelUrl} items={travelItems}");
                    // UGameEngine::LoadMap before loading: fade frames without a world tick (UELevelRuntime.RunLoadMapFade).
                    int fadeFrames = Runtime.RunLoadMapFade(interp, deltaSeconds, Runtime.NextInsideTickFrameNumber, WallSeconds);
                    _journal.Add($"loadmap_fade frames={fadeFrames}");
                    (string map, string options) = UELevelRuntime.SplitTravelUrl(travelUrl);
                    var cfg = PlayWorldConfigFromIni();
                    cfg.Options = options;
                    bool isSave = map.EndsWith(".usa", StringComparison.OrdinalIgnoreCase);
                    byte[] data = null; string savedMap = null, savedMapPath = null, badReason = null;
                    _loadNotice = null; _loadNoticePopup = null;   // a notice belongs to one load
                    // HP2's LOADGAME names the save "Save<N>.usa" and SaveGame writes "save<N>": found as Windows finds it.
                    string targetPath = isSave ? (UEGameProfile.Current.Game == UEEdition.Game.HP2
                            ? UEInstallPath.File(SaveFilePath(Path.GetFileNameWithoutExtension(map))) : null) ?? SaveFilePath(Path.GetFileNameWithoutExtension(map))
                        : UEInstallPath.File(Path.Combine(_gameRoot, "Maps", map.EndsWith(".unr", StringComparison.OrdinalIgnoreCase) ? map : map + ".unr"))
                          ?? Path.Combine(_gameRoot, "Maps", map.EndsWith(".unr", StringComparison.OrdinalIgnoreCase) ? map : map + ".unr");
                    if (!File.Exists(targetPath))
                    {
                        // LoadMap's catch: the old level plays on (UELevelRuntime.LoadMapMissingFile). E.g. the death
                        // reload `open save99.usa` with no level-start save.
                        Runtime.LoadMapMissingFile(interp, map);
                        _journal.Add($"loadmap_failed url_map={map} file={Path.GetFileName(targetPath)}");
                    }
                    else if (!isSave && UEPackageVerify.FirstMissingPackage(_gameRoot, targetPath) is string missingPackage)
                    {
                        // LoadMap's verify pass (UEPackageVerify): its catch, the old level plays on.
                        Runtime.LoadMapFailed(interp, map, missingPackage);
                        _journal.Add($"loadmap_failed url_map={map} file={Path.GetFileName(targetPath)} error={missingPackage}");
                        UETrace.Info($"[Map] {map} not loaded: {missingPackage}");   // the original's GLog line (SafeLoadError)
                    }
                    else if (isSave && !TryReadSave(interp, targetPath, out data, out savedMap, out savedMapPath, out badReason,
                                 out bool otherScripts))
                    {
                        var dropped = new List<string>();
                        Dictionary<string, List<UELevelRuntime.TravelActor>> carried = null;
                        // The decode runs before teardown; a save it cannot read stays a refusal with the cause appended.
                        try
                        {
                            carried = otherScripts ? Runtime.TravelInfoFromSave(data, savedMapPath, interp, dropped) : null;
                            // Format 1..4 has no level-entry set: no guessing (author 2026-11-12).
                            if (otherScripts && carried is null)
                                badReason = $"saved by an older version with different scripts ({badReason})";
                        }
                        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException)
                        { badReason += $"; restart with progress impossible: {e.Message}"; }
                        // DEATH RELOAD OF AN UNUSABLE LEVEL-START SAVE — IMPROVEMENT, NOT ORIGINAL (2026-11-17, 0.4.1 F1): slot
                        // 99 is the level-start autosave the death reload opens (FESlotPage.LoadSelectedSlot with no slot
                        // selected: `open save99.usa`; baseHarry stateDead retries it every 0.5 s). The original's refusal lets
                        // the old level play on, so a dead player retries forever (here: slot 99 holds a save of a map or mod
                        // no longer in the game). The port restarts the CURRENT level with the travel set it was entered with
                        // — what that slot would hold had this level written it.
                        if (carried is null && string.Equals(Path.GetFileNameWithoutExtension(map), LevelStartSaveStem, StringComparison.OrdinalIgnoreCase)
                            && Runtime.LoadedMapPath is string currentMapPath && File.Exists(currentMapPath))
                        {
                            string currentMap = Path.GetFileNameWithoutExtension(currentMapPath);
                            var entry = Runtime.LevelEntryTravel;
                            _journal.Add($"load_savegame_restart_current file={Path.GetFileName(targetPath)} map={currentMap} reason={badReason}");
                            UETrace.Info($"[Save] {map} not loaded: {badReason}; restarting {currentMap} with the progress it was entered with");
                            RestartLevelWithProgress(interp, cfg, currentMap, currentMapPath, entry);
                            _loadNotice = "Level restarted from its beginning.";
                        }
                        else if (carried is null)
                        {
                            // Damaged or newer-build save, or one of other scripts without a level-entry set: LoadMap's catch,
                            // the old level plays on (UELevelRuntime.LoadMapBadSave). The reason also goes to the run log.
                            Runtime.LoadMapBadSave(interp, map, badReason);
                            Runtime.ReleaseRefusedSaveMap(savedMap, interp);
                            _journal.Add($"loadmap_failed url_map={map} file={Path.GetFileName(targetPath)} reason={badReason}");
                            UETrace.Info($"[Save] {map} not loaded: {badReason}");
                            // Old-format save of other scripts while mods are on: the old level plays on, with the notice.
                            if (otherScripts && ActiveMods.Count > 0)
                                _loadNotice = "This save cannot be loaded with mods on. Turn the mods off to load it.";
                        }
                        else
                        {
                            // RESTART WITH PROGRESS — IMPROVEMENT, NOT ORIGINAL (author decision 2026-11-12): the save fits
                            // other scripts (mods on/off). The saved map is entered from its beginning as a level travel
                            // enters it, with the travel set the level was ENTERED with (UELevelRuntime.TravelInfoFromSave) —
                            // the death reload's progress, so the level's pickups cannot be collected twice; mid-level state
                            // is lost. The player is told by the load notice (TryShowLoadNotice). The engine-level
                            // travel does not arm HPConsole's level-start autosave (bLoadNewLevel): the slot is not overwritten.
                            string carriedList = string.Join(",", carried.Values.SelectMany(l => l).Select(t => $"{t.Name}:{t.Props.Count}"));
                            _journal.Add($"load_savegame_restart file={Path.GetFileName(targetPath)} map={savedMap} carried={carriedList} "
                                + $"dropped={dropped.Count} reason={badReason}");
                            foreach (string d in dropped) _journal.Add($"load_savegame_restart_dropped {d}");
                            UETrace.Info($"[Save] {map}: {badReason}; restarting {savedMap} with the progress it was entered with ({carriedList})"
                                + (dropped.Count == 0 ? "" : "; dropped: " + string.Join("; ", dropped)));
                            RestartLevelWithProgress(interp, cfg, savedMap, savedMapPath, carried);
                            const string restarted = "Level restarted from its beginning.";
                            string named = UELevelRuntime.SavedWithMods(data, ActiveMods) is { } savedWith ? $"{savedWith}. {restarted}" : null;
                            _loadNotice = named is not null && LoadNoticeWidth(interp, named) is >= 0f and <= LoadNoticeMaxWidth
                                ? named : $"Saved with other mods. {restarted}";
                        }
                    }
                    else if (isSave)
                    {
                        // `open saveN.usa` — world from a save (UELevelRuntime.SaveGame).
                        string savePath = targetPath;
                        _journal.Add($"load_savegame file={Path.GetFileName(savePath)} map={savedMap} bytes={data.Length}");
                        _localPlayer = Runtime.LoadSavedWorldAndBootstrap(interp, savedMapPath, data, cfg, map);
                        _journal.Add($"load_savegame_done actors={Runtime.Actors.Count} player={_localPlayer?.GetProp("Name")}");
                        _journal.Add($"load_savegame_channels {Runtime.AnimChannelDigest()}");
                        _journal.Add($"load_savegame_level_action saved={Runtime.LoadedSaveLevelAction} "
                            + $"after_spawn_play_actor={UEInterpreter.ToInt(interp.GetLevelInfo()?.GetProp("LevelAction"))}");
                        // Music request seen by the first sound Update (original: PostLogin's None with MTRAN_Fade, D-43).
                        _journal.Add($"load_savegame_music song={(_localPlayer?.GetProp("Song") as UEResourceRef)?.ObjectPath ?? "None"} "
                            + $"transition={UEInterpreter.AsByte(_localPlayer?.GetProp("Transition"))}");
                    }
                    else
                    {
                        _localPlayer = Runtime.Travel(interp, targetPath, cfg);
                    }
                    Runtime.EndServerTravelBrowse(interp);
                    WorldFrame screen = Runtime.LastLoadMapScreen;
                    _journal.Add($"loadmap_levelinfo hud={screen?.Hud.Count ?? -1}");
                }
                else if (!queuedSave) TryShowLoadNotice(interp);
                // EVERY tick, not only when a frontend asks for the snapshot.
                //
                // UEWorldFrameProducer.Build IS the port of UGameEngine::Draw: it
                // runs the view actor's PreRender/PostRender bytecode (which lazily
                // spawns myHUD and drives myHUD.PostRender) and Update(0) on every
                // live ParticleFX. Both draw from the SHARED MSVCRT stream, exactly
                // as shipped — the original has no frame in which they do not run.
                //
                // Gating that on a `--frames` flag invented a mode the original
                // does not have, and it showed up as the review's P0: building a
                // frame "perturbed gameplay spawns", because the plain mode was the
                // one running a world where the HUD never drew. The frame is now
                // always executed; the flag only decides whether the SNAPSHOT is
                // journalled.
                // Engine frame phase — one Core call for all frontends. The frame number
                // here is the gate tick, because the journal prints it.
                long frameT0 = UELevelRuntime.ProfileTicks ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                if (_publishWorldStaticForLightmaps && Runtime.LatestWorldStatic is null) Runtime.PublishWorldStaticForDiagnostics();
                BeforeFrame?.Invoke(tick);   // HP1_SURF_LM_POKE probe
                                             // A frame that browsed draws nothing more: UGameEngine::Tick returns right after Browse — server travel
                                             // 0x103a11ea -> ret 0x103a1313, client travel 0x103a1622 -> jmp 0x103a12fe -> ret — so neither
                                             // Client->Tick (Draw, Console.Tick: the slot 99 save) nor the pending level runs; the loading screen
                                             // stays up and the new level is first drawn after its first ULevel::Tick, in the next frame.
                WorldFrame frame = browsed || queuedSave ? Runtime.HoldLoadMapScreen(tick) : Runtime.RunEngineFrame(interp, tick, "gate", deltaSeconds);
                if (UELevelRuntime.ProfileTicks) UELevelRuntime.ProfileFrameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - frameT0;
                // Comparison journals and route checkpoints (runtime_poc).
                AfterFrame?.Invoke(interp, tick, frame, deltaSeconds);
                _journal.Add($"tick {tick} end actors={_actors.Count}");
                if (streamJournal) JournalStream?.Invoke();
            }
            TickWorldEnd?.Invoke(interp);   // segment acceptance, ledger journal flush
        }

        // One actor pass of ULevel::Tick (also the stage-3 fixtures' entry) —
        // Core-owned; Survey mode swallows per-actor boundaries to enumerate them.
        internal void TickActorsPass(UEInterpreter interp, float levelDelta)
            => Runtime.TickActorsPass(interp, levelDelta, ActorTickBoundaryProbe);

        // Shipped ULevel::Tick (Engine.dll.c:262612-262641) clamps the real frame
        // delta, applies LevelInfo.TimeDilation, converts it to 32.32 fixed point,
        // and publishes the accumulated high-precision time as a float BEFORE any
        // actor is ticked. Repeated float addition is observably different on long
        // routes and breaks TimeSeconds-based script guards.
        // The shipped fixed-point level clock — Core-owned (UELevelRuntime).
        internal void InitializeLevelClock(UEInterpreter interp)
            => Runtime.InitializeLevelClock(interp);

        internal float AdvanceLevelClock(UEInterpreter interp, float realDelta)
            => Runtime.AdvanceLevelClock(interp, realDelta);

        public void SetBsp(HeadlessBspModel bsp) => _bsp = bsp;
        public void SetReachSpecs(IReadOnlyList<LevelReachSpecRef> reachSpecs)
            => Runtime.SetReachSpecs(reachSpecs);
        // Layout and alias table come from the user ini, exactly the
        // [Engine.Input] section shipped UInput reads. Nothing is invented:
        // no file means a loud error, not a "default layout".
        internal void LoadInputBindings()
        {
            Runtime.Input.Journal = _journalOff ? null : _journal.Add;
            Runtime.LoadInputBindingsFromInstall(_systemDir);
            // "AS PLAYER" MODE for comparison with the original (HP1_AS_PLAYER=1); the headless
            // gate skips it so pins do not shift: first run selects the hardware device
            // (D3DDrv, HighDetailActors=True — bHighDetail trees and shadows live) and the
            // first ticks after load stutter (EmulateLoadStutter).
            if (AsPlayer || SelectHardwareRenderDevice) Runtime.SelectHardwareRenderDeviceIfUnset();
        }
        // The player (UEGameEngine.Boot(selectHardwareRenderDevice: true)) always selects it.
        internal bool SelectHardwareRenderDevice;
        internal static readonly bool AsPlayer = Environment.GetEnvironmentVariable("HP1_AS_PLAYER") == "1";

        // Config is read by CORE (UELevelRuntime.ConfigFromInstall) — the ini key set and
        // their priority order live there, once for all frontends.
        internal UELevelRuntime.PlayWorldConfig PlayWorldConfigFromIni()
            => UELevelRuntime.ConfigFromInstall(_systemDir);

        // Player login events (SpawnPlayActor 0x103a86cc..0x103a9005), recorded by
        // EXECUTION through the VM on this world boot (login_events journal).
        internal readonly List<string> LoginEventsForTest = new();
        static readonly HashSet<string> LoginEventNames = new(StringComparer.OrdinalIgnoreCase)
        { "Login", "TravelPreAccept", "AcceptInventory", "TravelPostAccept", "PostLogin" };

        public void BootstrapPlayWorld(UEInterpreter interp)
        {
            LoadInputBindings();
            var prevProbe = UEInterpreter.CallProbe;
            UEInterpreter.CallProbe = (o, f, a) =>
            {
                prevProbe?.Invoke(o, f, a);
                string n = f?.Name?.ToString();
                if (n is not null && LoginEventNames.Contains(n))
                    LoginEventsForTest.Add($"{n}@{o?.Class?.Name}");
            };
            UEObject playerPawn;
            try { playerPawn = Runtime.BootstrapPlayWorld(interp, PlayWorldConfigFromIni()); }
            finally { UEInterpreter.CallProbe = prevProbe; }
            _journal.Add("login_events " + string.Join(",", LoginEventsForTest));
            _localPlayer = playerPawn;
            _journal.Add($"local_player actor={playerPawn.GetProp("Name")}:{playerPawn.Class?.Name}"
                + $" player={Runtime.Viewport?.Class?.Name} console={Runtime.ViewportConsole?.Class?.Name}");
        }

        internal bool _publishWorldStaticForLightmaps;
        // Per-frame surface lightmaps; the first tick publishes the world static (TickWorld). Off by default:
        // the game turns it on via UEGameEngine.Boot(forceSurfaceLightmapsInFrame), the harness via its HP1_SURF_LM.
        internal void EnableSurfaceLightmapsInFrame()
        {
            Runtime.ComputeSurfaceLightmapsInFrame = true;
            _publishWorldStaticForLightmaps = true;
        }
        internal readonly string _systemDir;
        private readonly int _viewportX;
        private readonly int _viewportY;

        // ── HEADLESS FRONTEND VIEWPORT DEVICE ─────────────────────────────────────
        //
        // Headless has no window but has a VIEWPORT: a canvas whose size
        // UEWorldFrameProducer holds and puts into Canvas.SizeX/SizeY — exactly what the
        // script sees. So resize here is REAL: it changes that canvas, not a number in a
        // side field.
        //
        // This is the frontend half of the boundary: Core parses the command, decides
        // order and saves; the actual values live here.
        internal sealed class HeadlessViewportDevice : HP1.UEScript.IViewportDevice
        {
            private readonly UEWorldFrameProducer _producer;

            public HeadlessViewportDevice(UEWorldFrameProducer producer,
                int sizeX, int sizeY, int colorBytes)
            {
                _producer = producer;
                SizeX = sizeX;
                SizeY = sizeY;
                ColorBytes = colorBytes;
            }

            public int SizeX { get; private set; }
            public int SizeY { get; private set; }
            public int ColorBytes { get; private set; }
            // Headless has no screen: modes are the shipped windowed list.
            public string DisplayModes => HP1.UEScript.UEConsoleCommand.WindowedResolutions;

            // FULLSCREEN HERE IS STATE, NOT A WINDOW. Headless has no window, so the
            // "mode" means exactly what it is in shipped: the BlitFlags bit that picks
            // the key pair on save (WinDrv.dll.c:1619 -> :15366-15379). This frontend does
            // not run the startup chain (UEViewportStartup) — it opens no window — so the
            // bit stays false and saving goes to Windowed*.
            public bool Fullscreen { get; private set; }

            // Shipped windowed resize rejects nothing: the driver mode is enumerated
            // only in fullscreen (WinDrv.dll.c:5126), and the window gets the requested
            // size. Zero width/height is filtered by Core itself — where shipped does it.
            //
            // It is also SYNCHRONOUS here, like shipped: behind the headless viewport there
            // is no window or swapchain, only two numbers in the frame producer that change
            // within this call. So the outcome is always Applied and the device never has an
            // open transaction.
            public ViewportResizeOutcome Resize(int sizeX, int sizeY, int colorBytes, bool fullscreen)
            {
                // Width rounds up to even — shipped does this INSIDE
                // ResizeViewport, before applying and on every path
                // (WinDrv.dll.c:15152 -> Viewport->SizeX :15297). Height is untouched.
                sizeX = UEViewport.NormalizeSizeX(sizeX);
                SizeX = sizeX;
                SizeY = sizeY;
                Fullscreen = fullscreen;
                // Zero means "keep depth" — that is how `SetRes 800x600` without a
                // third part arrives.
                if (colorBytes != 0) ColorBytes = colorBytes;
                _producer?.SetViewport(sizeX, sizeY);
                return ViewportResizeOutcome.Applied;
            }
        }

        internal HP1.UEScript.IViewportDevice _viewport;
        // Window device of a frontend with a real window (HPEngine): size, mode and mode list come from the
        // window and monitor, not the headless canvas. The frontend still sets the script canvas itself (UEWorldFrameProducer.SetViewport).
        public void UseViewportDevice(HP1.UEScript.IViewportDevice device)
            => _viewport = device ?? throw new ArgumentNullException(nameof(device));

        // Frame building is owned by the SHARED Core producer (UEWorldFrameProducer).
        // Headless supplies its actor list and local player.
        // Same producer bindings as RunEngineFrame (corona line check, particle queries, ...): a frame built here must not
        // depend on an earlier engine frame having bound them.
        public WorldFrame BuildWorldFrame(int tick, string label)
        {
            Runtime.BindFrameProducerHooks(_localPlayer?.Interp);
            return FrameProducer.Build(tick, label, _actors, _localPlayer?.Interp, _localPlayer);
        }

        internal List<UEObject> ActorsLive => _actors;
        // Serialized UPrimitive bounds (FBox + FSphere) per Model export, read from
        // the .unr — the data source for the conservative brush gates below. The
        // MAP is Core state (UELevelRuntime.BrushBoundsByExport, layer C); the
        // package read that fills it stays here.
        // Core-owned (UELevelRuntime.SetBrushSource): the map path, the per-export
        // model cache and the parse all live there now, so a frontend gets brush
        // geometry by loading the world rather than by re-implementing the lookup.
        public void SetBrushSource(string mapPath, Dictionary<int, BrushBounds> bounds)
        {
            _brushMapPath = mapPath;          // kept: BuildWorldStatic still reports it
            Runtime.SetBrushSource(mapPath, bounds);
        }

        internal string _brushMapPath;

        // Shipped ULevel::MoveActor (Engine.dll.c:123689, 0x103aa390..0x103ab231)
        // (UActor.cpp:1367): the atomic sweep-move-resolve primitive behind the Move
        // native. Orchestration follows the shipped control flow: tiny-delta fast
        // path, 2-unit padded sweep with the hit time re-based to the unpadded span,
        // rider carry (with yaw orbit) BEFORE the commit, unconditional rotation
        // commit, then Bump/Touch/UnTouch/re-zone. Returns "moved at all"
        // (Result.Time > 0), not "unblocked" — a partial move is TRUE.
        // Core-owned (UELevelRuntime.MoveActor) — layer C. MoveActor,
        // MoveSmooth and CheckEncroachment are MUTUALLY RECURSIVE (MoveActor's
        // pre-commit encroachment check pushes riders with moveSmooth, which moves
        // again), so they moved as ONE unit — splitting them would have needed a
        // hook back into the frontend for each leg of the cycle.
        internal bool MoveActor(UEInterpreter interp, UEObject actor, Vector3 delta, UERotator newRot,
            bool bTest, bool bIgnorePawns, bool bIgnoreBases, bool bNoFail)
            => Runtime.MoveActor(interp, actor, delta, newRot, bTest, bIgnorePawns, bIgnoreBases, bNoFail);

        // Core-owned (UELevelRuntime.FarMove) — layer C. Runtime.FarMoveHook
        // remains: it is part of the required PhysWalking hook set
        // (WalkMoveHook/LineTraceHook/FarMoveHook) and an active test seam
        // (fixtures count and intercept far moves).
        internal bool FarMoveActor(UEInterpreter interp, UEObject actor, Vector3 newLoc, bool bTest, bool bNoCheck)
            => Runtime.FarMove(interp, actor, newLoc, bTest, bNoCheck);

        // AActor::SetBase — Core-owned (UELevelRuntime.SetBase).
        private void SetBaseActor(UEInterpreter interp, UEObject actor, UEObject newBase, bool notify)
            => Runtime.SetBase(interp, actor, newBase, notify);

        // AActor::SetOwner — Core-owned (UELevelRuntime.SetOwner).
        private void SetOwnerActor(UEInterpreter interp, UEObject actor, UEObject newOwner)
        {
            Runtime.SetOwner(interp, actor, newOwner);
        }

        // AActor::setPhysics — Core-owned (UELevelRuntime.SetPhysics, the shipped
        // Engine.dll.c:134160 helper). Its base (re)probe is Core's own FindBase.
        private void SetPhysicsActor(UEInterpreter interp, UEObject actor, int newPhysics, UEObject newFloor)
            => Runtime.SetPhysics(interp, actor, newPhysics, newFloor);

        private UELevelRuntime.LineCheckHit SingleLineCheckResult(UEInterpreter interp, UEObject self,
            UEObject ownerToSkip, Vector3 start, Vector3 end, Vector3 extent, int traceFlags)
            => Runtime.SingleLineCheck(interp, self, ownerToSkip, start, end, extent, traceFlags);

        // Core-owned (UELevelRuntime.InitCollisionHash) — layer C.
        internal void InitCollisionHash() => Runtime.InitCollisionHash();

        internal static string NativeCandidates(UFunction fn)
        {
            var method = typeof(UEInterpreter).GetMethod("LoadTokens",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (method?.Invoke(null, new object[] { fn }) is not System.Collections.IEnumerable tokens) return "?";
            var names = new List<string>();
            foreach (object token in tokens)
            {
                var type = token.GetType();
                object item = type.GetField("NativeItem")?.GetValue(token);
                if (item is not null)
                {
                    string name = item.GetType().GetField("Name")?.GetValue(item)?.ToString()
                               ?? item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                    string index = item.GetType().GetField("ByteToken")?.GetValue(item)?.ToString()
                                ?? item.GetType().GetProperty("ByteToken")?.GetValue(item)?.ToString();
                    names.Add($"native:{name ?? "?"}/{index ?? "?"}");
                }
                object virtualName = type.GetField("FunctionName")?.GetValue(token);
                if (virtualName is not null) names.Add("virtual:" + virtualName);
                if (type.GetField("Function")?.GetValue(token) is UFunction final)
                    names.Add("final:" + final.Name);
            }
            return string.Join(",", names.Distinct());
        }

        public void SetActors(IEnumerable<UEObject> actors) => Runtime.SetActors(actors);
    }

    internal sealed class IniFile
    {
        private readonly Dictionary<string, Dictionary<string, string>> _sections =
            new(StringComparer.OrdinalIgnoreCase);

        public IniFile(string path)
        {
            string section = "";
            foreach (string sourceLine in File.ReadLines(path))
            {
                string line = sourceLine.Trim();
                if (line.Length == 0 || line.StartsWith(';')) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1];
                    if (!_sections.ContainsKey(section))
                        _sections[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                if (!_sections.TryGetValue(section, out var values))
                    _sections[section] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
        }

        public string Get(string section, string key)
            => _sections.TryGetValue(section, out var values) && values.TryGetValue(key, out string value)
                ? value : null;

        // Whole section in file order — needed for [Engine.Input]: both the alias table
        // and the layout are read as a list of pairs, not by key name.
        public IEnumerable<KeyValuePair<string, string>> Section(string section)
            => _sections.TryGetValue(section, out var values)
                ? values : Enumerable.Empty<KeyValuePair<string, string>>();
    }
}
