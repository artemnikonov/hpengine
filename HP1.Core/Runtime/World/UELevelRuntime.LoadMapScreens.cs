// WHAT THE PLAYER SEES ON A MAP CHANGE — UGameEngine::LoadMap (Engine.dll FUN_1039c3d0).
//
// Disasm 0x1039c5ed..0x1039c8c5 (before UObject::BeginLoad) and 0x1039cd1d..0x1039cd9f
// (after loading the new map's LevelInfo0). The live-game journal confirms the engine
// calling HPConsole.DrawLevelInfo (Engine.dll+0x9cd8c) on every map change.
//
// 1. If there is a client, a viewport and an old level: Level.Pauser = "",
//    Viewport.Actor.bShowMenu = 0 (+0x58c & ~0x400000), Level.LevelAction = 0.
// 2. Fade-out: Console.FadeoutTime (+0x774) > 0 and Console.bDrewWorld (+0x748
//    bit 8) — while Viewport.Actor.FlashFog.W (+0x504) > 0: Engine->Draw(Viewport)
//    (vtbl+0x7c), then W -= (real time of this iteration) / FadeoutTime.
//    Time is rdtsc/appSeconds, not level time; the world does not tick.
// 3. ALWAYS (even without a fade, 0x1039c891): W = 0 and one more Draw — the screen
//    goes to the FlashFog colour (usually black: screen' = rgb + screen·W).
// 4. After loading the new map's LevelInfo0: if LevelEnterText (+0x404) is empty —
//    LevelEnterText = URL.Map (written to the level object, later read by
//    HPConsole.doLevelSave); then Viewport->Lock, Console.DrawLevelInfo(
//    Viewport.Canvas, LevelEnterText) WITHOUT Canvas->Update/Reset, Unlock(1) —
//    the loading-screen frame stays on screen until the new map's first frame.
//
// 3a. Inside the same client gate, right after step 3 and BEFORE UObject::BeginLoad:
//    Audio->SetViewport(Audio->GetViewport()) (0x1039c8af-0x1039c8c7, Engine+0x54, vtbl
//    +0x84/+0x60) — Galaxy stops every channel except the song (UEGalaxyAudio.SetViewportSame),
//    so the old level's loops are silent over the load and the loading screen. The frontend
//    hears it at once: PresentAudioNow hands it the channel snapshot (the world does not tick
//    until the load is over).
//
// Not ported (presentation, loudly): the vtbl+0xb0 call on Viewport+0x5c (the RenDev, not the Canvas)
// before DrawLevelInfo (purpose not established).
// Not original (author 2026-11-07): the parchment is drawn at the menu's full brightness (BuildLevelInfoFrame).
using System;
using System.Collections.Generic;

namespace HP1.UEScript
{
    public sealed partial class UELevelRuntime
    {
        enum LoadMapFadeState { None, Check, Fading, Final, Done }
        LoadMapFadeState _fadeState;
        float _fadeTime;
        bool _loadMapClient;   // Client && Viewports && GLevel (0x1039c5f0-0x1039c606): fade and audio stop run

        // Step 1 and the fade decision. Called by the engine tick right after taking
        // the travel request (TryTakeLevelTravel), while the old world is alive.
        // LoadMap of a map/save file that does not exist. StaticLoadObject(ULevel "MyLevel", URL.Map, 0x1a) ->
        // GetPackageLinker: appFindPackageFile fails -> appThrowf(LocalizeError("FileNotFound"), InFilename)
        // (Core disasm 0x1015e5c4-0x1015e5e5); its catch rethrows through SafeLoadError, which with LOAD_Throw
        // throws the inner error itself (0x1015e3ad-0x1015e3b6, "%s", Error). The load happens AFTER the fade and
        // BEFORE the old level is torn down, so LoadMap's catch (Catch_1039ed44, Engine.dll.c:248265-248325) runs
        // with the old level alive: EndLoad; Error = message; SetProgress(LocalizeError("UrlFailed","Core"), Error,
        // 6.0) (vtbl+0x98); then, with a viewport: Level.LevelAction = 0, player FlashFog.W (+0x504) = 1,
        // ConstantGlowFog.W (+0x534) = 0, FadeRate (+0x548) = 0; returns NULL. Browse fails and UGameEngine::Tick
        // ignores the result (0x103a1622 -> 0x103a1628, eax untested): the old level plays on.
        public void LoadMapMissingFile(UEInterpreter interp, string urlMap)
            => LoadMapFailed(interp, urlMap, UELocalization.Localize("Errors", "FileNotFound", "Core").Replace("%s", urlMap));

        // LoadMap of a save file the linker cannot read. Shipped ULinkerLoad (FUN_10102086, Core.dll.c:29522-29528): a bad
        // package tag logs LocalizeError("BinaryFormat") to GWarn and throws LocalizeError("Aborted") into the same LoadMap
        // catch as above: the player sees "Failed launching URL" / "Aborted" for 6 s and the old level plays on.
        // The port's save body is its own format (D-01): a damaged file, or one written by a NEWER port build (version
        // above SaveVersion), takes this route — ValidateSavedGame runs before the old world is torn down.
        public void LoadMapBadSave(UEInterpreter interp, string urlMap, string reason)
        {
            Log($"loadmap_bad_save warn=\"{UELocalization.Localize("Errors", "BinaryFormat", "Core").Replace("%s", urlMap)}\" reason=\"{reason}\"");
            LoadMapFailed(interp, urlMap, UELocalization.Localize("Errors", "Aborted", "Core"));
        }

        void LoadMapFailed(UEInterpreter interp, string urlMap, string error)
        {
            SetProgress(interp, UELocalization.Localize("Errors", "UrlFailed", "Core"), error, 6f);
            if (Viewport?.GetProp(PK.Actor) is UEObject player)
            {
                if (interp.GetLevelInfo() is UEObject level) level.Props["LevelAction"] = (byte)0;
                UEPlane ff = player.GetProp(PK.FlashFog) is UEPlane f ? f : default;
                player.Props["FlashFog"] = new UEPlane(ff.X, ff.Y, ff.Z, 1f);
                UEPlane cg = player.GetProp(PK.ConstantGlowFog) is UEPlane c ? c : default;
                player.Props["ConstantGlowFog"] = new UEPlane(cg.X, cg.Y, cg.Z, 0f);
                player.Props["FadeRate"] = 0f;
            }
            Log($"loadmap_failed map={urlMap} error=\"{error}\"");
        }

        // UGameEngine::SetProgress (Engine.dll.c:16180-16265, export 0x149c): on Viewports(0)->Actor —
        // ProgressMessage[0] = Str1, ProgressColor[0].RGB = 255, ProgressMessage[1] = Str2, ProgressColor[1].RGB = 255,
        // ProgressTimeOut = Seconds + Actor.Level.TimeSeconds (offsets by audit/prop_offsets.py: +0x6f4/+0x700,
        // +0x754/+0x758, +0x774). Seconds == -1 calls ShowUpgradeMenu first (not reached here).
        void SetProgress(UEInterpreter interp, string str1, string str2, float seconds)
        {
            if (Viewport?.GetProp(PK.Actor) is not UEObject player) return;
            if (seconds == -1f)
                throw new NotSupportedException("SetProgress(-1): ShowUpgradeMenu branch not ported");
            if (player.GetProp("ProgressMessage") is not System.Collections.IList msg || msg.Count < 2
                || player.GetProp("ProgressColor") is not System.Collections.IList col || col.Count < 2
                || col[0] is not UEObject c0 || col[1] is not UEObject c1)
                throw new InvalidOperationException("SetProgress: player has no ProgressMessage/ProgressColor static arrays");
            msg[0] = str1;
            foreach (string k in new[] { "R", "G", "B" }) c0.Props[k] = (byte)255;
            msg[1] = str2;
            foreach (string k in new[] { "R", "G", "B" }) c1.Props[k] = (byte)255;
            UEObject lvl = player.GetProp(PK.Level) as UEObject;
            player.Props["ProgressTimeOut"] = seconds + UEInterpreter.AsFloat(lvl?.GetProp(PK.TimeSeconds));
        }

        public void BeginLoadMapFade(UEInterpreter interp)
        {
            UEObject level = interp?.GetLevelInfo();
            UEObject player = Viewport?.GetProp(PK.Actor) as UEObject;
            _loadMapClient = Viewport is not null && level is not null;
            if (_loadMapClient)
            {
                level.Props["Pauser"] = "";
                if (player is not null) player.Props["bShowMenu"] = false;
                level.Props["LevelAction"] = (byte)0;
            }
            UEObject console = ViewportConsole;
            _fadeTime = console is null ? 0f : UEInterpreter.AsFloat(console.GetProp(PK.FadeoutTime));
            bool fade = console is not null && _fadeTime > 0f && UEInterpreter.ToBool(console.GetProp(PK.bDrewWorld));
            _fadeState = fade ? LoadMapFadeState.Check : LoadMapFadeState.Final;
            Log($"loadmap_fade begin fade={fade} FadeoutTime={_fadeTime} W={FlashFogW(player)}");
        }

        // The 2–3 loop (0x1039c76c..0x1039c88c): the clock before the loop and after every Engine->Draw; the next
        // step is delta = now − prev, so the fade lasts FadeoutTime of real time. Headless (no presenter, no wall clock
        // of its own) steps by its fixed delta. frameNumber: the fade frames' own numbers. Returns the frames built.
        public int RunLoadMapFade(UEInterpreter interp, float deltaSeconds, Func<int> frameNumber, Func<double> wallSeconds)
        {
            BeginLoadMapFade(interp);
            int frames = 0;
            float step = deltaSeconds;
            double prev = wallSeconds();
            while (LoadMapFadeFrame(step))
            {
                RunEngineFrame(interp, frameNumber(), "fade", deltaSeconds); frames++;
                if (LatestFrame is { } ff) PresentFadeFrameNow?.Invoke(ff);   // presenting is the frontend's job
                if (PresentFadeFrameNow is not null)
                {
                    double now = wallSeconds();
                    step = (float)(now - prev);
                    prev = now;
                }
            }
            // 3a: after the final Draw, before BeginLoad (0x1039c8af-0x1039c8c7).
            if (_loadMapClient && FrameProducer?.Audio is { } audio)
            {
                audio.SetViewportSame();
                Log("loadmap_audio set_viewport channels=" + audio.BusyCount());
                PresentAudioNow?.Invoke(audio.Snapshot(), audio.ResumeSerial);
            }
            return frames;
        }

        // One step of the 2–3 loop. true — the frontend must build an engine frame
        // (RunEngineFrame) right now, without ticking the world; false — the fade
        // is over. realDelta — real time elapsed since the previous frame
        // (shipped: duration of the loop iteration).
        public bool LoadMapFadeFrame(float realDelta)
        {
            UEObject player = Viewport?.GetProp(PK.Actor) as UEObject;
            switch (_fadeState)
            {
                case LoadMapFadeState.Check:
                    if (player is not null && FlashFogW(player) > 0f) { _fadeState = LoadMapFadeState.Fading; return true; }
                    goto case LoadMapFadeState.Final;
                case LoadMapFadeState.Fading:
                    if (player is not null)
                    {
                        float w = (float)(FlashFogW(player) - (double)realDelta / _fadeTime);
                        SetFlashFogW(player, w);
                        if (w > 0f) return true;
                    }
                    goto case LoadMapFadeState.Final;
                case LoadMapFadeState.Final:
                    if (player is not null) SetFlashFogW(player, 0f);
                    _fadeState = LoadMapFadeState.Done;
                    Log("loadmap_fade final W=0");
                    return true;
                default:
                    return false;
            }
        }

        static float FlashFogW(UEObject player)
            => player is null ? 0f : UEValue.AsPlane(player.GetProp(PK.FlashFog)).W;
        static void SetFlashFogW(UEObject player, float w)
        {
            UEPlane p = UEValue.AsPlane(player.GetProp(PK.FlashFog));
            player.Props["FlashFog"] = new UEPlane(p.X, p.Y, p.Z, w);
        }

        // Step 4, text: LevelEnterText of the LevelInfo0 object from the map package (raw
        // tag, no object is created); empty — URL.Map.
        public static string LevelEnterTextForLoad(string packagePath, string urlMap)
        {
            var reader = new UEPackageReader(packagePath);
            PackageExport info = null;
            foreach (PackageExport e in reader.Exports)
                if (string.Equals(e.Name, "LevelInfo0", StringComparison.OrdinalIgnoreCase)) { info = e; break; }
            if (info is null)
                throw new InvalidOperationException($"LoadMap {packagePath}: no LevelInfo0 (shipped StaticLoadObject LevelInfo0)");
            foreach (UEPackageReader.RawPropertyTag t in reader.ReadInstanceProperties(info))
                if (t.ArrayIndex == 0 && string.Equals(t.Name, "LevelEnterText", StringComparison.OrdinalIgnoreCase))
                {
                    int pos = 0;
                    string s = UEActorGraphLoader.ReadString(t.Value, ref pos);
                    if (s.Length != 0) return s;
                }
            return urlMap ?? "";
        }

        // Step 4, write to the world: after the level loads, its LevelInfo gets
        // LevelEnterText = URL.Map if empty.
        // LevelInfo is Actors[0] of THIS world (the Entry world has its own), not the level
        // the interpreter currently sees.
        internal void ApplyLoadMapLevelEnterText(string urlMap)
        {
            UEObject level = Actors.Count > 0 ? Actors[0] : null;
            if (level is null || !UEInterpreter.ClassIsA(level.Class, "LevelInfo"))
                throw new InvalidOperationException("LoadMap LevelEnterText: the world's Actors[0] is not a LevelInfo");
            if (level.GetProp(PK.LevelEnterText) is string s && s.Length != 0) return;
            level.Props["LevelEnterText"] = urlMap ?? "";
            Log($"loadmap level_enter_text={urlMap}");
        }

        // The last built loading screen (step 4 frame) — the frontend keeps it
        // on screen until the new world's first frame.
        public WorldFrame LastLoadMapScreen { get; private set; }

        // Step 4 on an already loaded level (headless/Travel): LevelInfo loaded,
        // LevelEnterText applied, level scripts not yet run — as in
        // shipped, where DrawLevelInfo comes before loading the rest of the level.
        internal void BuildLoadMapScreenOfLoadedLevel(UEInterpreter interp)
        {
            string text = (Actors.Count > 0 ? Actors[0].GetProp(PK.LevelEnterText) as string : null) ?? "";
            LastLoadMapScreen = BuildLevelInfoScreen(interp, EngineFrameCount, text);
            // Shipped presents DrawLevelInfo immediately (Viewport->Unlock/Present before loading the rest of the
            // level); a synchronous frontend shows the screen right here, otherwise the previous frame stays
            // up for the whole load (same trick as PresentProgressNow of the save frame).
            if (LastLoadMapScreen is not null) PresentLoadMapScreenNow?.Invoke(LastLoadMapScreen);
        }

        // Carrier for presenting the loading screen and the LoadMap fade frames (synchronous frontend).
        // Does not change game state: only shows an already built frame.
        public Action<WorldFrame> PresentLoadMapScreenNow;
        public Action<WorldFrame> PresentFadeFrameNow;
        // Carrier for the LoadMap audio stop (3a): the Galaxy channel snapshot and resume serial, as WorldFrame.AudioChannels /
        // AudioResumeSerial carry them; the frontend only plays from it.
        public Action<IReadOnlyList<UEGalaxyAudio.Playback>, int> PresentAudioNow;
        // Carrier for EVERY engine frame's audio (K2): each Galaxy Update — the tick's gate frame and the frames built
        // inside a tick (LoadMap fade, the post-load frame, the save-progress frame) — goes to the mixer when Core builds
        // it, in Core's order (RunEngineFrame). The frontend only mixes the frame (AudioSystem.Update).
        public Action<WorldFrame> PresentAudioFrameNow;
        // The browsing engine frame (HoldLoadMapScreen) ran NO Galaxy Update: UGameEngine::Tick returns right after
        // Browse. Its tick has no audio frame by design — told to the frontend so it is not mistaken for a lost frame.
        // Nothing is lost in it: Galaxy PlaySound only fills a channel slot (handle 0, the sample starts in Update; the
        // body 0x10607730 calls no glx start), and LoadMap's closing SetViewport (0x1039e5b1, UEGalaxyAudio.SetViewportSame)
        // stops every non-music channel the new level's BeginPlay/login started before any Update runs.
        public Action<int> PresentAudioNoDrawNow;
        // Window responsiveness carrier (D-17): map loading is synchronous, like UGameEngine::LoadMap,
        // and takes seconds; macOS/Windows declare a window that does not pump OS events hung.
        // Core calls it during loading: per actor in the map and save graph loader and in the
        // startup event passes (PreBeginPlay..SetInitialState). The frontend only pumps the OS
        // queue in it; touching Core or game state is forbidden — VM/RNG order does not change
        // (audit/load_progress_redtest.sh).
        public Action LoadProgress;

        // Step 4, frame: Console.DrawLevelInfo(Canvas, text) on black, without the world.
        public WorldFrame BuildLevelInfoScreen(UEInterpreter interp, int frameNumber, string text)
        {
            if (FrameProducer is null || interp is null) return null;
            FrameProducer.Console = ViewportConsole;
            WorldFrame f = FrameProducer.BuildLevelInfoFrame(interp, frameNumber, text);
            Log($"loadmap_levelinfo text={text} hud={f?.Hud.Count ?? -1}");
            return f;
        }
    }
}
