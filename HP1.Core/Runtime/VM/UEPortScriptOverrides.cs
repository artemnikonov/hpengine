// SCRIPT FUNCTIONS REPLACED BY PORT CODE — only by author decision, each with a
// reason. The original bytecode of these functions is not executed. (Not original.)
//
// FEOptionsPage.IsSupportedResolution (author decision): the original menu
// only lists 640x480/800x600/1024x768 (and 512x384 except on 2000/XP). The port
// feeds GetRes with the device's display modes (IViewportDevice.DisplayModes) and
// the filter would drop them, so every mode the device reports is accepted.
//
// HPMenuOptionComboList.BeforePaint (author decision, scrolling): the HP list does
// not scroll — every frame it sets VertSB.Pos = 0, hides the bar and divides a fixed
// height (89/54) among all items. The original body runs as-is, then with > 4 items
// the base UWindowComboList behaviour is restored: MaxVisible = 4, item height for
// four rows, a scroll bar (SetRange, room on the right), scroll position preserved.
//
// UWindowVScrollBar.Paint (author decision, parchment-style bar) — only for HP list
// bars: instead of the grey Win95 bar (LookAndFeel.SB_VDraw), a track in the item
// highlight colour (FEComboListBox) and the brass knob of the menu sliders
// (FESliderKnobTexture, 9x25). Arrows are hidden; thumb maths, dragging and clicks
// are the unchanged UWindowVScrollBar bytecode.
//
// FEOptionsPage, "Video" column (author decision): Display Mode instead of Color Depth, resolution = monitor modes
// (details at OptionsLoadSettings). Texture Detail and Object Detail are the shipped items, driven by the original
// bytecode (author decision 2026-10-15: the port's Frame Rate / Smooth Motion items are removed); the Original /
// Enhanced menu item is removed (author decision 2026-11-08); the Object Detail frame is "Interface Size" (author
// decision 2026-11-08, not original, D-44): Smaller / Standard / Larger, saved as [HPEngine] UIScaleStep.
//
// FEOptionsPage.LoadAvailableSettings on UK11 (author decision 2026-11-08, NOT ORIGINAL): the same every-mode list,
// rebuilt after the shipped body because the UK11 bytecode inlines the filter (details at UkResolutions). The same for
// any scripts without IsSupportedResolution (a mod's HPMenu.u; author decision 2026-11-13, NOT ORIGINAL).
//
// FEOptionsPage.LocalizeStrings (author decision) — fix of original data: Pickup2.int lacks key names
// 0-9, F1-F10, Unknown5B-5D, MouseWheelUp/Down and None, so the binding button showed the Localize marker
// "<?int?pickup2.all.localized_keyname_049?>". The original body runs, then missing names are taken from
// the game's EInputKey enum name without "IK_" (the other .int strings are named that way: F11, NumPad0, Unknown5E).
// Then (D-75 addition 2026-11-12, NOT ORIGINAL) the pad keys get player names ("Pad A", "D-pad Up"), display only, by
// the type of the pad last read (UEInput.PadLabelsFor).
//
// FEOptionsPage.LoadExistingKeys, ProcessKey, BeforePaint (D-75, author decision 2026-11-13, NOT ORIGINAL): the Controls
// column shows one device at a time. Details at OptionsLoadKeys.
//
// HPMenuOptionHSlider.BeforePaint, HPMenuOptionCheckBox.BeforePaint (IMPROVEMENT, not original; author decision):
// details at OwnFontBeforePaint below.
//
// Jellybean.killbean.Tick (author decision) — fix of an original bug: a fly-to-camera divisor with
// magnitude below 1 (the zero-crossing tick) is replaced by 1, otherwise at a steady frame rate the bean flies off
// and the pickup sound is lost; other ticks, including the first with divisor −1, run the bytecode as-is
// (details at JellybeanFlyTick).
//
// baseHarry.KeyDownEvent (author decision 2026-10-30, NOT ORIGINAL: no cheat codes, no developer console; D-77).
// The shipped body (baseHarry.uc:732-780) is only the key-string cheat matcher: it appends Chr(Key) to _CurrentString
// (reset after a 1 s pause) and on a match runs HarryTriggerCheat (TriggerEvent 'HarryCheat'), HarryDebugModeOn
// (TurnDebugModeOn -> HPConsole.bDebugMode = true, the one gate of the developer mode: Tab typing console, console
// key -> ShowConsole, F2-F5 GotoShortcut, F8 NextCamera, F11 HUD counters, Insert SShot, cutscene fast-forward, the
// menu's level select / language / version buttons, page skipping, the "Debug" label), HarryGetsFullHealth,
// HarrySuperJump, HarryNormalJump. Its only caller is HPConsole.KeyEvent on every key press. The override does
// nothing, so no typed string changes anything. The other bDebugMode source, the config key, is cut in
// UEObjectConfig.LoadConfig. Script-internal ConsoleCommand (options page, saves, travel) is untouched.
//
// SneakNorris.stateJump.HitWall (author decision 2026-11-11, NOT ORIGINAL: the cat climbs at any frame rate; D-81).
// Details at SneakJumpHitWall.
//
// FEBook.ChangePage (author decision 2026-11-12, IMPROVEMENT, NOT ORIGINAL: an open combo list closes on a page
// change; D-83). Details at ChangePageClosesComboList.
//
// FEBook.KeyEvent, FEStoryBookPage.KeyEvent (author decision 2026-11-15, IMPROVEMENT, NOT ORIGINAL; D-89 (7)): the
// splash skip and the story-book page skip of the shipped debug mode (bDebugMode, cut by D-77) for every player.
// Details at KeySkipsSplash / SpaceSkipsStoryPage.
using System;
using System.Collections.Generic;
using UELib;
using UELib.Core;

namespace HP1.UEScript
{
    public static class UEPortScriptOverrides
    {
        // original — the original function body (bytecode), for before/after overrides.
        public delegate object Handler(UEInterpreter interp, UEObject self, object[] args, Func<object> original);

        // The overrides of HP1's scripts. They bind by owner name + function name, and HP2's hgame.u has classes of the
        // same names (FEOptionsPage, FEBook, HPMenuOption*, ...) with different bytecode, so the table is the HP1 game
        // profile's (UEGameProfile.Overrides); HP2's is HP2Table below.
        internal static readonly (string Class, string Function, Handler Body)[] HP1Table =
        {
            ("FEOptionsPage", "IsSupportedResolution", (interp, self, args, original) => true),
            ("HPMenuOptionComboList", "BeforePaint", ComboListBeforePaint),
            ("UWindowVScrollBar", "Paint", ScrollBarPaint),
            ("killbean", "Tick", JellybeanFlyTick),
            ("FEOptionsPage", "LoadAvailableSettings", OptionsLoadSettings),
            ("FEOptionsPage", "SettingsChanged", OptionsSettingsChanged),
            ("FEOptionsPage", "ObjectDetailChanged", OptionsInterfaceSizeChanged),
            ("FEOptionsPage", "SensitivityChanged", SensitivitySaved),
            ("FEOptionsPage", "LocalizeStrings", OptionsLocalizeKeyNames),
            ("FEOptionsPage", "BeforePaint", OptionsBeforePaint),
            ("FEOptionsPage", "LoadExistingKeys", OptionsLoadKeys),
            ("FEOptionsPage", "ProcessKey", OptionsProcessKey),
            ("HPMenuOptionHSlider", "BeforePaint", OwnFontBeforePaint),
            ("HPMenuOptionCheckBox", "BeforePaint", OwnFontBeforePaint),
            ("baseHarry", "KeyDownEvent", (interp, self, args, original) => null),
            ("stateJump", "HitWall", SneakJumpHitWall),
            ("FEBook", "ChangePage", ChangePageClosesComboList),
            ("FEBook", "KeyEvent", KeySkipsSplash),
            ("FEStoryBookPage", "KeyEvent", SpaceSkipsStoryPage),
        };

        // The overrides of HP2's scripts (hgame.u), by author decision 2026-11-16 (NOT ORIGINAL): FESoundVideoPage's
        // "Video" column as HP1's — the resolution list offers every device mode (rebuilt after the shipped body: its filter
        // IsSupportedResolution keeps 640x480/800x600/1024x768, and its split loop adds only tokens followed by a space, so
        // the last GetRes mode — the largest — never shows; GetRes ends without one [HP2 WinDrv: LeftChop of the trailing
        // space], the windowed constant too) and Color Depth becomes Display Mode: Fullscreen / Windowed (the
        // port's ToggleFullscreen + StartupFullscreen; Game.exe's launcher dialog had windowed and resolution, HPEngine's HP2
        // launcher has no Video page). HP2's page differs from HP1's FEOptionsPage: its own label control (ColorDepthLabel)
        // names the frame, it has no BeforePaint (UWindowWindow's is empty, and the interpreter skips empty bodies before overrides), no Object Detail combo (a slider that
        // sends MLOD, shipped), and its LoadAvailableSettings shows the saved Windowed*/Fullscreen* size per IsFullscreen.
        // HP2 (author decision 2026-11-16: every HP1 improvement applies to HP2; NOT ORIGINAL). The same handlers where
        // HP2's hgame.u has the same function with the same defect (read side by side in the --hp2-script-text dump;
        // inventory the maintainer's notes hp2_improvements_parity.md):
        //   HPMenuOptionComboList.BeforePaint — HP1's body but MaxWidth 187 / small height 61 / hgame.Icons: no scrolling;
        //   UWindowVScrollBar.Paint — the parchment bar of those lists (HP2's textures, ScrollBarPaint);
        //   HPMenuOptionHSlider/CheckBox.BeforePaint — neither sets C.Font either (the foreign font);
        //   FEInputPage.LocalizeStrings — HPMenu.int lacks Localized_kn_ 0, 91-93, 112-121 (F1-F10), 236/237; the Controls
        //   page is FEInputPage (KEYBINDING for 255 keys into BoundKey1/2 as FEOptionsPage), so LoadExistingKeys, ProcessKey
        //   and the device check of BeforePaint are D-75's;
        //   harry.KeyDownEvent — the cheat matcher (ChrisMode, EliJump, GoyleMode, ...; D-77);
        //   FEBook.KeyEvent — Shift+F7 toggles HPConsole.bDebugMode for every player in HP2 (D-77; Hp2KeyEventNoDebugToggle).
        // Not taken (HP2 has no such defect or function): FEBook.ChangePage (HP2's FEInputPage/FESoundVideoPage.HideWindow
        // close their lists: CloseUpWithNoSound), killbean (HProp.FlyToNewPosition clamps the divisor to 1),
        // SneakNorris (no script class), FEStoryBookPage, the splash skip (HP2 FEBook.bShowSplash is never set). The
        // Options/Video page's rows are the first comment's (line hp2/ingame-video).
        internal static readonly (string Class, string Function, Handler Body)[] HP2Table =
        {
            ("FESoundVideoPage", "LoadAvailableSettings", Hp2VideoLoadSettings),
            ("FESoundVideoPage", "SettingsChanged", OptionsSettingsChanged),
            ("UWindowWindow", "PaintClients", Hp2VideoPaintClients),
            ("HPMenuOptionComboList", "BeforePaint", ComboListBeforePaint),
            ("UWindowVScrollBar", "Paint", ScrollBarPaint),
            ("HPMenuOptionHSlider", "BeforePaint", OwnFontBeforePaint),
            ("HPMenuOptionCheckBox", "BeforePaint", OwnFontBeforePaint),
            ("FEInputPage", "LocalizeStrings", OptionsLocalizeKeyNames),
            ("FEInputPage", "LoadExistingKeys", OptionsLoadKeys),
            ("FEInputPage", "ProcessKey", OptionsProcessKey),
            ("FEInputPage", "BeforePaint", OptionsBeforePaint),
            ("harry", "KeyDownEvent", (interp, self, args, original) => null),
            ("FEBook", "KeyEvent", Hp2KeyEventNoDebugToggle),
            ("FEInputPage", "ShowWindow", Hp2InputShowsDifficulty),
        };

        static (string Class, string Function, Handler Body)[] Table => UEGameProfile.Current.Overrides;

        // HP2 FEInputPage.ShowWindow (author decision 2026-11-16, NOT ORIGINAL, improvement): the shipped body shows the
        // Difficulty label and combo only when HPConsole.bDebugMode is set (and hides them otherwise); Created gives them their
        // own row below Auto-drink potion. They are shown for every player after the shipped body. The combo's handler is
        // the shipped DifficultyChanged (PlayerPawn.Difficulty, read by harry.TakeDamage's fDamageMultiplier_*, DoJump,
        // AdjustAim, the duel spells, Pawn.PreBeginPlay); saved by HideWindow's SaveConfig.
        static object Hp2InputShowsDifficulty(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            object r = original();
            foreach (string name in new[] { "DifficultyLabel", "DifficultyCombo" })
                Call(interp, self.GetProp(name) as UEObject ?? throw new InvalidOperationException("FEInputPage: no " + name), "ShowWindow");
            return r;
        }


        // The replaced functions by name (owner class or state, function), for the binding census of a mod's scripts
        // (HP1_PROBE_OVERRIDES, audit/mods_saves_e2e.sh).
        public static IEnumerable<(string Class, string Function)> Targets
        {
            get { foreach (var e in Table) yield return (e.Class, e.Function); }
        }

        // FEOptionsPage, "Video" column (author decision). The four list frames and four slider tracks
        // are PAINTED INTO THE PAGE BACKGROUND (HPMenu.Icons.FEOptionsBackTexture1..6); the controls are transparent
        // overlays in their places: they cannot move and every frame stays occupied. So items are not hidden but
        // repurposed:
        //   Resolution    — monitor modes (the frontend window device, IViewportDevice.DisplayModes);
        //   Color Depth   -> "Display Mode": Fullscreen / Windowed — stock ToggleFullscreen (WinDrv.dll.c:5090) and
        //                    StartupFullscreen (startup mode, UEViewportStartup), not a setting of our own.
        //   Object Detail -> "Interface Size" (author decision 2026-11-08, NOT ORIGINAL, D-44): Smaller / Standard /
        //                    Larger = [HPEngine] UIScaleStep -1 / 0 / +1 (UEConfig.UIScaleFor). The shipped
        //                    ObjectDetailChanged (writes PlayerPawn.ObjectDetail, no reader in 1.53, D-27/D-33) is
        //                    replaced, not run: the item no longer means Object Detail.
        // Texture Detail is NOT replaced: the shipped bytecode fills and handles it (FEOptionsPage.uc:182-200, 414,
        // 578-583) -> `set ini:…ViewportManager TextureDetail`, read by the HPEngine frame (UETextureLod).
        // The original LoadAvailableSettings body runs in full (Created calls it, FEOptionsPage.uc:294), the edit
        // follows it (the lists are refilled every time there; it is also called after a resolution change).
        const string DisplayModeText = "Display Mode", InterfaceSizeText = "Interface Size";
        static readonly string[] InterfaceSizes = { "Smaller", "Standard", "Larger" };   // UIScaleStep -1 / 0 / +1

        // The Interface Size steps offered at this screen height (author decision 2026-11-12): only items that change the
        // scale. The scale is an integer >= 1 (UEConfig.UIScaleFor), so below 960 px (Steam Deck 800, 720p, 768) Smaller
        // equals Standard and is not offered; Standard is always there. Index in the combo = index in this array.
        public static int[] InterfaceSizeSteps(int screenHeight)
        {
            int standard = UEConfig.UIScaleFor(screenHeight, 0);
            var steps = new List<int>();
            foreach (int step in new[] { -1, 0, 1 })
                if (step == 0 || UEConfig.UIScaleFor(screenHeight, step) != standard) steps.Add(step);
            return steps.ToArray();
        }
        const string Fullscreen = "Fullscreen", Windowed = "Windowed";

        static UEObject Combo(UEObject page, string name)
            => page.GetProp(name) as UEObject ?? throw new InvalidOperationException("FEOptionsPage: no " + name);

        static IViewportDevice ViewportOf(UEInterpreter interp)
            => (interp.Natives as UEGameEngineNatives)?._viewport
               ?? throw new InvalidOperationException("FEOptionsPage: the native table has no viewport device");

        // List changes send Notify(DE_Change); while the page rebuilds them, handlers are off (bInitialized).
        static void Quiet(UEObject page, Action body)
        {
            object init = page.GetProp("bInitialized");
            page.Props["bInitialized"] = false;
            try { body(); } finally { page.Props["bInitialized"] = init; }
        }

        static void Fill(UEInterpreter interp, UEObject combo, string text, params (string Show, string Value)[] items)
        {
            Call(interp, combo, "Clear");
            if (text is not null) Call(interp, combo, "SetText", text);   // null: the page labels the frame with its own control (HP2)
            foreach (var (show, value) in items) Call(interp, combo, "AddItem", show, value, 0);
        }

        static object OptionsLocalizeKeyNames(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            object r = original();
            if (self.GetProp("LocalizedKeyName") is not List<object> names)
                throw new InvalidOperationException("FEOptionsPage: LocalizedKeyName — not a static array");
            IReadOnlyList<string> keys = HP1.Core.UEClassRegistry.Shared?.EnumNames("EInputKey", "Actor")
                ?? throw new InvalidOperationException("FEOptionsPage: enum EInputKey not found in the loaded packages");
            for (int i = 0; i < names.Count && i < keys.Count; i++)
                if (names[i] is string n && n.StartsWith("<?", StringComparison.Ordinal) && keys[i].StartsWith("IK_", StringComparison.Ordinal))
                    names[i] = keys[i].Substring(3);
            PadKeyNames(interp, names);
            return r;
        }

        // D-75 addition (NOT ORIGINAL): player names of the pad keys, display only (details at UEInput.GamepadKeyNames).
        static void PadKeyNames(UEInterpreter interp, List<object> names)
        {
            foreach (var (key, name) in InputOf(interp)?.GamepadKeyNames() ?? UEInput.GamepadKeyNames(null, null))
                names[UEConsoleCommand.FindKeyName(key)] = name;
        }

        static UEInput InputOf(UEInterpreter interp) => (interp.Natives as UEGameEngineNatives)?.Runtime?.Input;

        // The Controls column shows ONE DEVICE AT A TIME (D-75, author decision 2026-11-13, NOT ORIGINAL; the model is at
        // the top of UEInput.Gamepad.cs). The shipped LoadExistingKeys (uc:440-520) asks KEYBINDING for all 255 keys and
        // puts the first two that carry a row's alias into BoundKey1/2; it runs unchanged, and for its duration KEYBINDING
        // answers for the keys of one device (UEInput.ControlsLoad): in the pad view the pad keys' effective bindings
        // (defaults included), otherwise the original list without the pad keys. The page lives for the whole session, so
        // BeforePaint reloads the list whenever the player's device differs from the one shown, and keeps the hint lines.
        static object OptionsLoadKeys(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            UEInput input = InputOf(interp);
            if (input is null) return original();
            object r = null;
            input.ControlsLoad(self, () => r = original());
            return r;
        }

        // Key capture takes only the shown device's keys: any other key ends the wait as the keys the shipped body refuses
        // do (Escape, digits, F1-F10: click sound, nothing bound; uc:787-793).
        static object OptionsProcessKey(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            UEInput input = InputOf(interp);
            string key = (self.GetProp("RealKeyName") as List<object>)?[UEInterpreter.ToInt(args[0])] as string;
            if (input is null || string.IsNullOrEmpty(key) || UEInput.IsPadKey(key) == input.ControlsShowsPad(self)) return original();
            Call(interp, self, "PlayClick");
            return null;
        }

        static object OptionsLoadSettings(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            object r = original();
            if (!Declares(self.Class, "IsSupportedResolution")) Quiet(self, () => UkResolutions(interp, self));
            Quiet(self, () => SelectVideo(interp, self));
            return r;
        }

        // UK11 (author decision 2026-11-08, NOT ORIGINAL; D-70): the UK11 HPMenu.u has no IsSupportedResolution — its
        // LoadAvailableSettings inlines the 512x384/640x480/800x600/1024x768 filter — so the US override cannot reach it.
        // After the shipped body the resolution list is rebuilt as the US body builds it with the filter overridden:
        // every GetRes mode in order (AddItem), then SetValue(GetCurrentRes) (FEOptionsPage.uc:371-394).
        // The same rebuild serves any FEOptionsPage whose scripts have no IsSupportedResolution (author decision 2026-11-13,
        // NOT ORIGINAL): the Widescreen Mod's HPMenu.u dropped the function and its LoadAvailableSettings adds no resolution
        // at all, so the page would show an empty list. The condition is the absence of the function in the loaded class
        // (UK11 included), never an edition, mod or package name.
        static bool Declares(UClass cls, string function)
        {
            foreach (UFunction f in cls.EnumerateFields<UFunction>())
                if (string.Equals(f.Name.ToString(), function, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The override targets whose owner class is loaded but no longer has the function (a mod's scripts): the override
        // silently does not run there. For the game's log (HPEngineProgram); state-owned targets are not classes and are not
        // listed.
        public static List<(string Class, string Function)> AbsentTargets()
        {
            var absent = new List<(string, string)>();
            foreach (var e in Table)
                if (HP1.Core.UEClassRegistry.Shared?.Find(e.Class) is UClass c && !Declares(c, e.Function)) absent.Add((e.Class, e.Function));
            return absent;
        }

        // SHADOWED MOD CHANGES (author decision 2026-11-13: log line only, behaviour unchanged). The overrides bind by
        // name, so when a mod's package brings its own body of a target, the override still replaces (or wraps) it and
        // the mod's change is lost without a word. Every function of the install's System/*.u an override binds to
        // (Lookup's rule: owner class or state name + function name, read from the export tables, so packages a level
        // loads later are covered at start) is hashed — FNV-1a 64 of its compared form (FunctionForm: the bytecode with
        // references resolved to names, flags, parameters; not the source position) — and compared with the hashes of
        // the supported shipped editions, so a package recompiled with the function untouched is not reported.
        // The table is printed by the tool (HP1_PROBE_OVERRIDES=1 on an unmodified install: `override_hash` lines);
        // one row per edition (UEEdition.DataSet name) and bound function. A recognised install is compared with ITS OWN
        // edition's rows only (UEEdition.OfSystem of the scripts directory); an unrecognised one (a mod's scripts, another
        // release) with those of the unmodified editions (US153, UK11, RU_EA). The modified re-releases (VERSIONS.md R4 MODDED_EN, R5 CITY_PL) have rows of their
        // own, so their stock scripts are not reported as a mod's; that they change overridden functions is said once
        // (ReleaseChangedTargets). No mod or package is named in the comparison.
        static readonly (string Edition, string Target, string Hash)[] ShippedScriptHashes =
        {
            ("US153", "baseHarry.KeyDownEvent", "bea2b0e874257aae"),
            ("US153", "FEBook.ChangePage", "347c82b260c97a42"),
            ("US153", "FEBook.KeyEvent", "96a7f23e710f65e8"),
            ("US153", "FEStoryBookPage.KeyEvent", "85aa17df40b5f021"),
            ("US153", "HPMenuOptionHSlider.BeforePaint", "b9d30f390599db05"),
            ("US153", "HPMenuOptionComboList.BeforePaint", "fe6e3e900e8a42bb"),
            ("US153", "HPMenuOptionCheckBox.BeforePaint", "640ad1d86e3e9c29"),
            ("US153", "FEOptionsPage.LocalizeStrings", "fbdf0776c4750f3d"),
            ("US153", "FEOptionsPage.BeforePaint", "a2f2c84cee974ed3"),
            ("US153", "FEOptionsPage.IsSupportedResolution", "9e78cc9cd320187d"),
            ("US153", "FEOptionsPage.LoadAvailableSettings", "6face598af5e3fab"),
            ("US153", "FEOptionsPage.LoadExistingKeys", "abf07bb1b46e42b5"),
            ("US153", "FEOptionsPage.SettingsChanged", "7004e9f5fc323487"),
            ("US153", "FEOptionsPage.ObjectDetailChanged", "80b85efbe1882f29"),
            ("US153", "FEOptionsPage.SensitivityChanged", "559d0e814283eb7b"),
            ("US153", "FEOptionsPage.ProcessKey", "c3c6be9e2d597dfe"),
            ("US153", "killbean.Tick", "7705e1c7b0f236c0"),
            ("US153", "stateJump.HitWall", "2658214266400307"),
            ("US153", "UWindowVScrollBar.Paint", "81d8b2a37bc6ba69"),
            ("UK11", "baseHarry.KeyDownEvent", "bea2b0e874257aae"),
            ("UK11", "FEBook.ChangePage", "347c82b260c97a42"),
            ("UK11", "FEBook.KeyEvent", "96a7f23e710f65e8"),
            ("UK11", "FEStoryBookPage.KeyEvent", "85aa17df40b5f021"),
            ("UK11", "HPMenuOptionHSlider.BeforePaint", "b9d30f390599db05"),
            ("UK11", "HPMenuOptionComboList.BeforePaint", "fe6e3e900e8a42bb"),
            ("UK11", "FEOptionsPage.LocalizeStrings", "fbdf0776c4750f3d"),
            ("UK11", "FEOptionsPage.BeforePaint", "a2f2c84cee974ed3"),
            ("UK11", "FEOptionsPage.LoadAvailableSettings", "5f995447666786fa"),
            ("UK11", "FEOptionsPage.LoadExistingKeys", "abf07bb1b46e42b5"),
            ("UK11", "FEOptionsPage.SettingsChanged", "7004e9f5fc323487"),
            ("UK11", "FEOptionsPage.ObjectDetailChanged", "80b85efbe1882f29"),
            ("UK11", "FEOptionsPage.SensitivityChanged", "559d0e814283eb7b"),
            ("UK11", "FEOptionsPage.ProcessKey", "c3c6be9e2d597dfe"),
            ("UK11", "HPMenuOptionCheckBox.BeforePaint", "640ad1d86e3e9c29"),
            ("UK11", "killbean.Tick", "7705e1c7b0f236c0"),
            ("UK11", "stateJump.HitWall", "2658214266400307"),
            ("UK11", "UWindowVScrollBar.Paint", "81d8b2a37bc6ba69"),
            ("RU_EA", "baseHarry.KeyDownEvent", "bea2b0e874257aae"),
            ("RU_EA", "FEBook.ChangePage", "347c82b260c97a42"),
            ("RU_EA", "FEBook.KeyEvent", "96a7f23e710f65e8"),
            ("RU_EA", "FEStoryBookPage.KeyEvent", "85aa17df40b5f021"),
            ("RU_EA", "HPMenuOptionHSlider.BeforePaint", "b9d30f390599db05"),
            ("RU_EA", "HPMenuOptionComboList.BeforePaint", "fe6e3e900e8a42bb"),
            ("RU_EA", "HPMenuOptionCheckBox.BeforePaint", "640ad1d86e3e9c29"),
            ("RU_EA", "FEOptionsPage.LocalizeStrings", "fbdf0776c4750f3d"),
            ("RU_EA", "FEOptionsPage.BeforePaint", "a2f2c84cee974ed3"),
            ("RU_EA", "FEOptionsPage.IsSupportedResolution", "9e78cc9cd320187d"),
            ("RU_EA", "FEOptionsPage.LoadAvailableSettings", "6face598af5e3fab"),
            ("RU_EA", "FEOptionsPage.LoadExistingKeys", "abf07bb1b46e42b5"),
            ("RU_EA", "FEOptionsPage.SettingsChanged", "7004e9f5fc323487"),
            ("RU_EA", "FEOptionsPage.ObjectDetailChanged", "80b85efbe1882f29"),
            ("RU_EA", "FEOptionsPage.SensitivityChanged", "559d0e814283eb7b"),
            ("RU_EA", "FEOptionsPage.ProcessKey", "c3c6be9e2d597dfe"),
            ("RU_EA", "killbean.Tick", "7705e1c7b0f236c0"),
            ("RU_EA", "stateJump.HitWall", "2658214266400307"),
            ("RU_EA", "UWindowVScrollBar.Paint", "81d8b2a37bc6ba69"),
            // R4 modded EN re-release (its v3 and v4 share the .u files): its bound functions are the US ones recompiled, so
            // the compared form gives the US153 hashes (its raw export bytes differed for 16 of them)
            ("MODDED_EN", "baseHarry.KeyDownEvent", "bea2b0e874257aae"),
            ("MODDED_EN", "HPMenuOptionCheckBox.BeforePaint", "640ad1d86e3e9c29"),
            ("MODDED_EN", "FEBook.ChangePage", "347c82b260c97a42"),
            ("MODDED_EN", "FEBook.KeyEvent", "96a7f23e710f65e8"),
            ("MODDED_EN", "FEStoryBookPage.KeyEvent", "85aa17df40b5f021"),
            ("MODDED_EN", "HPMenuOptionComboList.BeforePaint", "fe6e3e900e8a42bb"),
            ("MODDED_EN", "HPMenuOptionHSlider.BeforePaint", "b9d30f390599db05"),
            ("MODDED_EN", "FEOptionsPage.LocalizeStrings", "fbdf0776c4750f3d"),
            ("MODDED_EN", "FEOptionsPage.BeforePaint", "a2f2c84cee974ed3"),
            ("MODDED_EN", "FEOptionsPage.IsSupportedResolution", "9e78cc9cd320187d"),
            ("MODDED_EN", "FEOptionsPage.LoadAvailableSettings", "6face598af5e3fab"),
            ("MODDED_EN", "FEOptionsPage.LoadExistingKeys", "abf07bb1b46e42b5"),
            ("MODDED_EN", "FEOptionsPage.SettingsChanged", "7004e9f5fc323487"),
            ("MODDED_EN", "FEOptionsPage.ObjectDetailChanged", "80b85efbe1882f29"),
            ("MODDED_EN", "FEOptionsPage.SensitivityChanged", "559d0e814283eb7b"),
            ("MODDED_EN", "FEOptionsPage.ProcessKey", "c3c6be9e2d597dfe"),
            ("MODDED_EN", "killbean.Tick", "7705e1c7b0f236c0"),
            ("MODDED_EN", "stateJump.HitWall", "2658214266400307"),
            ("MODDED_EN", "UWindowVScrollBar.Paint", "81d8b2a37bc6ba69"),
            // R5 City disc: only HPBase.u of its scripts differs from US 1.53, and none of the bound functions in it
            ("CITY_PL", "baseHarry.KeyDownEvent", "bea2b0e874257aae"),
            ("CITY_PL", "FEBook.ChangePage", "347c82b260c97a42"),
            ("CITY_PL", "FEBook.KeyEvent", "96a7f23e710f65e8"),
            ("CITY_PL", "FEStoryBookPage.KeyEvent", "85aa17df40b5f021"),   // HPMenu.u byte-identical to US (sha1 3d9aa801)
            ("CITY_PL", "HPMenuOptionHSlider.BeforePaint", "b9d30f390599db05"),
            ("CITY_PL", "HPMenuOptionComboList.BeforePaint", "fe6e3e900e8a42bb"),
            ("CITY_PL", "HPMenuOptionCheckBox.BeforePaint", "640ad1d86e3e9c29"),
            ("CITY_PL", "FEOptionsPage.LocalizeStrings", "fbdf0776c4750f3d"),
            ("CITY_PL", "FEOptionsPage.BeforePaint", "a2f2c84cee974ed3"),
            ("CITY_PL", "FEOptionsPage.IsSupportedResolution", "9e78cc9cd320187d"),
            ("CITY_PL", "FEOptionsPage.LoadAvailableSettings", "6face598af5e3fab"),
            ("CITY_PL", "FEOptionsPage.LoadExistingKeys", "abf07bb1b46e42b5"),
            ("CITY_PL", "FEOptionsPage.SettingsChanged", "7004e9f5fc323487"),
            ("CITY_PL", "FEOptionsPage.ObjectDetailChanged", "80b85efbe1882f29"),
            ("CITY_PL", "FEOptionsPage.ProcessKey", "c3c6be9e2d597dfe"),
            ("CITY_PL", "killbean.Tick", "7705e1c7b0f236c0"),
            ("CITY_PL", "stateJump.HitWall", "2658214266400307"),
            ("CITY_PL", "UWindowVScrollBar.Paint", "81d8b2a37bc6ba69"),
            // HP2 (the HP2Table targets; every HP2 release has the same functions: `override_hash` lines of
            // --hp2-improvements with HP2IMPR_OTHER_SYSTEM).
            ("HP2_EN", "FEBook.KeyEvent", "4938db64bbcd5172"),
            ("HP2_EN", "FEInputPage.BeforePaint", "355070a3b24bc824"),
            ("HP2_EN", "FEInputPage.LoadExistingKeys", "b4778f4a3cfdd758"),
            ("HP2_EN", "FEInputPage.LocalizeStrings", "735bb7b13e5f428b"),
            ("HP2_EN", "FEInputPage.ProcessKey", "6508a442387d311d"),
            ("HP2_EN", "HPMenuOptionCheckBox.BeforePaint", "634f695fbf467320"),
            ("HP2_EN", "HPMenuOptionComboList.BeforePaint", "090ab08587f244e2"),
            ("HP2_EN", "HPMenuOptionHSlider.BeforePaint", "78a32b08095ea006"),
            ("HP2_EN", "UWindowVScrollBar.Paint", "81d8b2a37bc6ba69"),
            ("HP2_EN", "harry.KeyDownEvent", "d18c80144eeb7831"),
            ("HP2_EN", "FESoundVideoPage.LoadAvailableSettings", "c6660162f025d6fe"),
            ("HP2_EN", "FESoundVideoPage.SettingsChanged", "64b6d4f7b0eac176"),
            ("HP2_EN", "UWindowWindow.PaintClients", "16a84e55917cbaf8"),
            ("HP2_EN", "FEInputPage.ShowWindow", "57c4f29b4950ad9f"),
            ("HP2_RU_EA", "FEBook.KeyEvent", "4938db64bbcd5172"),
            ("HP2_RU_EA", "FEInputPage.BeforePaint", "355070a3b24bc824"),
            ("HP2_RU_EA", "FEInputPage.LoadExistingKeys", "b4778f4a3cfdd758"),
            ("HP2_RU_EA", "FEInputPage.LocalizeStrings", "735bb7b13e5f428b"),
            ("HP2_RU_EA", "FEInputPage.ProcessKey", "6508a442387d311d"),
            ("HP2_RU_EA", "HPMenuOptionCheckBox.BeforePaint", "634f695fbf467320"),
            ("HP2_RU_EA", "HPMenuOptionComboList.BeforePaint", "090ab08587f244e2"),
            ("HP2_RU_EA", "HPMenuOptionHSlider.BeforePaint", "78a32b08095ea006"),
            ("HP2_RU_EA", "UWindowVScrollBar.Paint", "81d8b2a37bc6ba69"),
            ("HP2_RU_EA", "harry.KeyDownEvent", "d18c80144eeb7831"),
            ("HP2_RU_EA", "FESoundVideoPage.LoadAvailableSettings", "c6660162f025d6fe"),
            ("HP2_RU_EA", "FESoundVideoPage.SettingsChanged", "64b6d4f7b0eac176"),
            ("HP2_RU_EA", "UWindowWindow.PaintClients", "16a84e55917cbaf8"),
            ("HP2_RU_EA", "FEInputPage.ShowWindow", "57c4f29b4950ad9f"),
            ("HP2_MODIFIED", "FEBook.KeyEvent", "4938db64bbcd5172"),
            ("HP2_MODIFIED", "FEInputPage.BeforePaint", "355070a3b24bc824"),
            ("HP2_MODIFIED", "FEInputPage.LoadExistingKeys", "b4778f4a3cfdd758"),
            ("HP2_MODIFIED", "FEInputPage.LocalizeStrings", "735bb7b13e5f428b"),
            ("HP2_MODIFIED", "FEInputPage.ProcessKey", "6508a442387d311d"),
            ("HP2_MODIFIED", "HPMenuOptionCheckBox.BeforePaint", "634f695fbf467320"),
            ("HP2_MODIFIED", "HPMenuOptionComboList.BeforePaint", "090ab08587f244e2"),
            ("HP2_MODIFIED", "HPMenuOptionHSlider.BeforePaint", "78a32b08095ea006"),
            ("HP2_MODIFIED", "UWindowVScrollBar.Paint", "81d8b2a37bc6ba69"),
            ("HP2_MODIFIED", "harry.KeyDownEvent", "d18c80144eeb7831"),
            ("HP2_MODIFIED", "FESoundVideoPage.LoadAvailableSettings", "c6660162f025d6fe"),
            ("HP2_MODIFIED", "FESoundVideoPage.SettingsChanged", "64b6d4f7b0eac176"),
            ("HP2_MODIFIED", "UWindowWindow.PaintClients", "16a84e55917cbaf8"),
            ("HP2_MODIFIED", "FEInputPage.ShowWindow", "57c4f29b4950ad9f"),
        };

        // systemDir: the scripts to read; null = the directory of the loaded scripts (the game). A package that cannot be
        // read (a mod's damaged .u) is skipped and named in `unhashed` ("<path>: <error>"): a log path never stops the game.
        public static List<(string Class, string Function, string Hash)> TargetScriptHashes(string systemDir = null,
            List<string> unhashed = null)
        {
            var hashes = new List<(string, string, string)>();
            IReadOnlyList<UnrealPackage> loaded = HP1.Core.UEClassRegistry.Shared?.Packages;
            if (loaded is null || loaded.Count == 0) return hashes;
            systemDir ??= System.IO.Path.GetDirectoryName(loaded[0].FullPackageName);
            foreach (string path in UEInstallPath.PackageFiles(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(systemDir.TrimEnd('/', '\\'))), "System"))
            {
                // Tables only (UELib reads them in LoadPackage); the loaded instance's tables where there is one.
                UnrealPackage own = null, pkg = null;
                foreach (UnrealPackage l in loaded)
                    if (string.Equals(l.FullPackageName, path, StringComparison.OrdinalIgnoreCase)) pkg = l;
                try
                {
                    pkg ??= own = HP1.UEScript.UEPackageOpen.Load(path, System.IO.FileAccess.Read);
                    System.IO.FileStream file = null;   // an own handle: the package's stream may be in use by a reader
                    try
                    {
                        foreach (UExportTableItem e in pkg.Exports)
                        {
                            if (!string.Equals(e.Class?.ObjectName.ToString(), "Function", StringComparison.OrdinalIgnoreCase)) continue;
                            string cls = e.Outer?.ObjectName.ToString(), function = e.ObjectName.ToString();
                            if (!Array.Exists(Table, t => string.Equals(t.Function, function, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(t.Class, cls, StringComparison.OrdinalIgnoreCase))) continue;
                            var bytes = new byte[e.SerialSize];
                            file ??= System.IO.File.OpenRead(path);
                            file.Seek(e.SerialOffset, System.IO.SeekOrigin.Begin);
                            file.ReadExactly(bytes);
                            byte[] form;
                            try { form = System.Text.Encoding.UTF8.GetBytes(FunctionForm.Of(pkg, e, bytes, file)); }
                            catch (Exception x) when (x is System.IO.InvalidDataException or IndexOutOfRangeException or ArgumentException)
                            { form = bytes; }   // not decodable: the raw bytes match no pinned row, so the function is reported
                            ulong h = 14695981039346656037UL;                 // FNV-1a 64: change detection, not identity
                            foreach (byte x in form) h = (h ^ x) * 1099511628211UL;
                            hashes.Add((cls, function, h.ToString("x16")));
                        }
                    }
                    finally { file?.Dispose(); }
                }
                catch (Exception e) { unhashed?.Add($"{path}: {e.GetType().Name}: {e.Message}"); }
                finally { own?.Dispose(); }
            }
            return hashes;
        }

        // The compared form of a function export (0.4.0, author 2026-11-15: a package recompiled with the function
        // untouched is not reported). The export's bytes hold the package's own name and object indices and the source
        // position, so the raw bytes differed for every recompiled function (9 of the Unofficial Widescreen Patch's 14
        // HPMenu targets). The form: SuperField, ScriptSize, the bytecode token by token (UE1 token set, package version
        // 64-99) with every object reference as its full path and every name as its text, the function's iNative /
        // OperPrecedence / FunctionFlags (/ RepOffset), and each parameter and local in its Children/Next order (class,
        // name, ArrayDim, PropertyFlags, Category (/ RepOffset), its type's references). Left out: the function's own
        // Next, ScriptText, FriendlyName, Line, TextPos (not behaviour). Same rule, written separately, in
        // audit/mod_compat_override_expect.py (the judge of audit/mod_compat_e2e.sh O). A token stream that does not end
        // exactly at ScriptSize (references count 4 bytes in memory) throws: the caller hashes the raw bytes instead, which
        // match no pinned row, so such a function is reported.
        sealed class FunctionForm
        {
            readonly UnrealPackage _pkg; readonly byte[] _b; int _p, _mem; readonly System.Text.StringBuilder _s = new();
            FunctionForm(UnrealPackage pkg, byte[] bytes) { _pkg = pkg; _b = bytes; }

            int Index()
            {
                byte x = _b[_p++]; bool neg = (x & 0x80) != 0; int v = x & 0x3f;
                if ((x & 0x40) != 0)
                    for (int shift = 6; ; shift += 7) { x = _b[_p++]; v |= (x & 0x7f) << shift; if ((x & 0x80) == 0) break; }
                return neg ? -v : v;
            }
            string Path(int r)
            {
                if (r == 0) return "none";
                UObjectTableItem item = _pkg.IndexToObjectResource(r);
                var parts = new List<string>();
                for (UObjectTableItem t = item; t != null; t = t.Outer) parts.Add(t.ObjectName.ToString());
                if (r > 0) parts.Add(_pkg.PackageName);
                parts.Reverse();
                string cls = item is UImportTableItem imp ? imp.ClassName.ToString() : ((UExportTableItem)item).Class?.ObjectName.ToString() ?? "Class";
                return (cls + "'" + string.Join(".", parts) + "'").ToLowerInvariant();
            }
            void Put(object v) => _s.Append(v).Append('\n');
            byte U8() { _mem += 1; return _b[_p++]; }
            ushort U16() { _mem += 2; ushort v = BitConverter.ToUInt16(_b, _p); _p += 2; return v; }
            uint U32() { _mem += 4; uint v = BitConverter.ToUInt32(_b, _p); _p += 4; return v; }
            void Obj() { _mem += 4; Put(Path(Index())); }
            string Name() { _mem += 4; string n = _pkg.Names[Index()].Name; Put("n:" + n.ToLowerInvariant()); return n; }
            void Args() { while (Expr() != 0x16) { } }

            int Expr()
            {
                int t = U8(); Put("t" + t.ToString("x2"));
                switch (t)
                {
                    case 0x00: case 0x01: case 0x02: case 0x20: case 0x29: Obj(); break;          // variables, object const, native parm
                    case 0x04: case 0x0D: case 0x0E: case 0x2D: case 0x37: Expr(); break;       // return, goto label, eat string, bool var, length
                    case 0x05: Put(U8()); Expr(); break;                                       // switch
                    case 0x06: Put(U16()); break;                                              // jump
                    case 0x07: case 0x09: case 0x15: case 0x18: Put(U16()); Expr(); break;     // jump if not, assert, line number, skip
                    case 0x08: case 0x0B: case 0x16: case 0x17: case 0x25: case 0x26: case 0x27: case 0x28: case 0x2A: case 0x30: case 0x31: break;
                    case 0x0A: { ushort o = U16(); Put(o); if (o != 0xffff) Expr(); break; }    // case
                    case 0x0C: while (true) { string n = Name(); Put(U32()); if (n.Equals("None", StringComparison.OrdinalIgnoreCase)) break; } break;
                    case 0x0F: case 0x10: case 0x14: case 0x1A: Expr(); Expr(); break;         // let, array elements, let bool
                    case 0x11: for (int i = 0; i < 4; i++) Expr(); break;                      // new
                    case 0x12: case 0x19: Expr(); Put(U16()); Put(U8()); Expr(); break;        // (class) context
                    case 0x13: case 0x2E: case 0x36: Obj(); Expr(); break;                     // meta cast, dynamic cast, struct member
                    case 0x1B: case 0x38: Name(); Args(); break;                               // virtual / global function
                    case 0x1C: Obj(); Args(); break;                                           // final function
                    case 0x1D: case 0x1E: Put(U32()); break;                                   // int / float const
                    case 0x1F: { int e = Array.IndexOf(_b, (byte)0, _p); Put(Convert.ToHexString(_b, _p, e - _p)); _mem += e + 1 - _p; _p = e + 1; break; }
                    case 0x21: Name(); break;                                                  // name const
                    case 0x22: case 0x23: Put(U32()); Put(U32()); Put(U32()); break;           // rotator / vector const
                    case 0x24: case 0x2C: Put(U8()); break;                                    // byte consts
                    case 0x2F: Expr(); Put(U16()); break;                                      // iterator
                    case 0x32: case 0x33: Obj(); Expr(); Expr(); break;                        // struct compare
                    case 0x34: { int q = _p; while (_b[q] != 0 || _b[q + 1] != 0) q += 2; Put(Convert.ToHexString(_b, _p, q - _p)); _mem += q + 2 - _p; _p = q + 2; break; }
                    default:
                        if (t >= 0x39 && t <= 0x5f) Expr();                                    // UE1 conversion tokens
                        else if (t >= 0x60 && t <= 0x6f) { Put(U8()); Args(); }               // extended native
                        else if (t >= 0x70) Args();                                            // native
                        else throw new System.IO.InvalidDataException($"token 0x{t:x2}");
                        break;
                }
                return t;
            }

            void NoneProps() { if (!_pkg.Names[Index()].Name.Equals("None", StringComparison.OrdinalIgnoreCase)) throw new System.IO.InvalidDataException("tagged properties on a script export"); }

            public static string Of(UnrealPackage pkg, UExportTableItem fn, byte[] bytes, System.IO.Stream file)
            {
                var f = new FunctionForm(pkg, bytes);
                f.NoneProps();
                int super = f.Index(); f.Index(); f.Index(); int child = f.Index(); f.Index();   // SuperField; Next, ScriptText left out; Children; FriendlyName left out
                f._p += 8;                                                              // Line, TextPos
                int scriptSize = BitConverter.ToInt32(bytes, f._p); f._p += 4;
                f.Put("super:" + f.Path(super)); f.Put("size:" + scriptSize);
                while (f._mem < scriptSize) f.Expr();
                if (f._mem != scriptSize) throw new System.IO.InvalidDataException("bytecode ran past ScriptSize");
                ushort native = BitConverter.ToUInt16(bytes, f._p); byte prec = bytes[f._p + 2]; uint flags = BitConverter.ToUInt32(bytes, f._p + 3); f._p += 7;
                f.Put($"fn:{native}:{prec}:{flags:x}");
                if ((flags & 0x40) != 0) { f.Put("rep:" + BitConverter.ToUInt16(bytes, f._p)); f._p += 2; }   // FUNC_Net
                if (f._p != bytes.Length) throw new System.IO.InvalidDataException($"function export has {bytes.Length - f._p} trailing bytes");
                while (child != 0)                                                      // parameters and locals, Children/Next order
                {
                    if (child < 0) throw new System.IO.InvalidDataException("an imported function child");
                    UExportTableItem e = pkg.Exports[child - 1];
                    var b = new byte[e.SerialSize];
                    file.Seek(e.SerialOffset, System.IO.SeekOrigin.Begin);
                    file.ReadExactly(b);
                    var p = new FunctionForm(pkg, b);
                    p.NoneProps(); p.Index(); child = p.Index();                            // SuperField; Next: the following one
                    ushort dim = BitConverter.ToUInt16(b, p._p); uint pflags = BitConverter.ToUInt32(b, p._p + 2); p._p += 6;
                    string cat = pkg.Names[p.Index()].Name;
                    f._s.Append($"prop|{e.Class?.ObjectName}|{e.ObjectName}|{dim}|{pflags:x}|{cat}".ToLowerInvariant());
                    if ((pflags & 0x20) != 0) { f._s.Append('|').Append(BitConverter.ToUInt16(b, p._p)); p._p += 2; }   // CPF_Net
                    while (p._p < b.Length) f._s.Append('|').Append(p.Path(p.Index()));    // the type's references
                    if (p._p != b.Length) throw new System.IO.InvalidDataException($"property {e.ObjectName} ran past its export");
                    f._s.Append('\n');
                }
                return f._s.ToString();
            }
        }

        static bool Modified(string edition) => UEEdition.TierOf(Enum.Parse<UEEdition.DataSet>(edition)) == UEEdition.Support.Modified;

        // The edition whose rows a scripts directory is compared with: its own when recognised, else null (the rows of the
        // unmodified editions: a re-release's modded function is a mod's change for any other install).
        static string RowsOf(string systemDir)
        {
            if (systemDir is null && HP1.Core.UEClassRegistry.Shared?.Packages is { Count: > 0 } loaded)
                systemDir = System.IO.Path.GetDirectoryName(loaded[0].FullPackageName);
            UEEdition.DataSet data = systemDir is null ? UEEdition.DataSet.Unknown : UEEdition.OfSystem(systemDir).Data;
            return data == UEEdition.DataSet.Unknown ? null : data.ToString();
        }

        // The targets whose function in the install's scripts is not a shipped one. For the game's log (HPEngineProgram).
        public static List<(string Class, string Function)> ChangedTargets(string systemDir = null, List<string> unhashed = null)
        {
            var changed = new List<(string, string)>();
            string edition = RowsOf(systemDir);
            var rows = Array.FindAll(ShippedScriptHashes, r => edition is null ? !Modified(r.Edition) : r.Edition == edition);
            foreach (var (cls, function, hash) in TargetScriptHashes(systemDir, unhashed))
                if (!Array.Exists(rows, r => r.Hash == hash && string.Equals(r.Target, cls + "." + function, StringComparison.OrdinalIgnoreCase))
                    && !changed.Contains((cls, function)))
                    changed.Add((cls, function));
            return changed;
        }

        // A recognised modified re-release: the bound functions its own scripts changed from US 1.53 / HP2 EN (by the table rows, not
        // the files). The port's overrides replace them there too. Empty for the reference editions and unrecognised installs.
        public static List<string> ReleaseChangedTargets(string systemDir = null)
        {
            var changed = new List<string>();
            string edition = RowsOf(systemDir);
            if (edition is null || !Modified(edition)) return changed;
            string reference = UEGameProfile.Current.Game == UEEdition.Game.HP2 ? "HP2_EN" : "US153";   // the game's reference release
            foreach (var r in ShippedScriptHashes)
                if (r.Edition == edition && !Array.Exists(ShippedScriptHashes, u => u.Edition == reference && u.Target == r.Target && u.Hash == r.Hash))
                    changed.Add(r.Target);
            return changed;
        }

        static void UkResolutions(UEInterpreter interp, UEObject self)
        {
            UEObject res = Combo(self, "ResolutionCombo");
            UEObject owner = Call(interp, self, "GetPlayerOwner") as UEObject
                ?? throw new InvalidOperationException("FEOptionsPage: no player owner");
            Call(interp, res, "Clear");
            foreach (string mode in (Call(interp, owner, "ConsoleCommand", "GetRes") as string ?? "").Split(' '))
                Call(interp, res, "AddItem", mode, "", 0);
            Call(interp, res, "SetValue", Call(interp, owner, "ConsoleCommand", "GetCurrentRes") as string, "");
        }

        // Display Mode is filled here: the original LoadAvailableSettings puts "16/32 bit" into this frame every time.
        static void SelectVideo(UEInterpreter interp, UEObject self)
        {
            SelectDisplayMode(interp, self, DisplayModeText);
            UEObject size = Combo(self, "ObjectDetailCombo");
            int[] steps = InterfaceSizeSteps(ViewportOf(interp).SizeY);
            Fill(interp, size, InterfaceSizeText, Array.ConvertAll(steps, st => (InterfaceSizes[st + 1], InterfaceSizes[st + 1])));
            int at = Array.IndexOf(steps, UEConfig.Global?.UIScaleStep ?? 0);   // a saved step not offered here = Standard
            Call(interp, size, "SetSelectedIndex", at >= 0 ? at : Array.IndexOf(steps, 0));
        }

        static void SelectDisplayMode(UEInterpreter interp, UEObject self, string comboText)
        {
            UEObject depth = Combo(self, "ColorDepthCombo");
            Fill(interp, depth, comboText, (Fullscreen, Fullscreen), (Windowed, Windowed));
            Call(interp, depth, "SetSelectedIndex", ViewportOf(interp).Fullscreen ? 0 : 1);
        }

        // HP2 FESoundVideoPage.LoadAvailableSettings (author decision 2026-11-16): the shipped body (filtered GetRes list, the
        // saved size selected, 16/32 bit into the Color Depth frame, the other items), then the resolution list rebuilt with
        // every GetRes mode in order and the size in use selected (GetCurrentRes, as HP1's shipped body; the saved
        // Windowed*/Fullscreen* pair need not be the live size — 2026-11-18, Alt+Enter), the Display Mode items in the Color
        // Depth frame and its label control renamed.
        static object Hp2VideoLoadSettings(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            object r = original();
            Quiet(self, () =>
            {
                UEObject res = Combo(self, "ResolutionCombo");
                UEObject owner = Call(interp, self, "GetPlayerOwner") as UEObject
                    ?? throw new InvalidOperationException("FESoundVideoPage: no player owner");
                Call(interp, res, "Clear");
                foreach (string mode in (Call(interp, owner, "ConsoleCommand", "GetRes") as string ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    Call(interp, res, "AddItem", mode, "", 0);
                Call(interp, res, "SetValue", Call(interp, owner, "ConsoleCommand", "GetCurrentRes") as string, "");
                SelectDisplayMode(interp, self, null);
            });
            Call(interp, self.GetProp("ColorDepthLabel") as UEObject ?? throw new InvalidOperationException("FESoundVideoPage: no ColorDepthLabel"),
                "SetText", DisplayModeText);
            return r;
        }

        // HP2 (author decision 2026-11-16): the page has no BeforePaint of its own (UWindowWindow's is empty, never dispatched
        // to an override), so UWindowWindow.PaintClients — final, run by every painted window after its Paint — is wrapped
        // for it: the mode or size switched elsewhere (Alt+Enter) left Display Mode and Resolution stale, and a later pick
        // would toggle it back (SettingsChanged compares the items with the device). Refilled as on opening, before the
        // children paint, when either differs from the device (HP1's OptionsBeforePaint rule; a refill also re-sends MLOD).
        static object Hp2VideoPaintClients(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            if (UEInterpreter.ClassIsA(self.Class, "FESoundVideoPage") && self.GetProp("ColorDepthCombo") is UEObject depth
                && self.GetProp("ResolutionCombo") is UEObject res)
            {
                IViewportDevice vp = ViewportOf(interp);
                if (UEInterpreter.ToInt(Call(interp, depth, "GetSelectedIndex")) != (vp.Fullscreen ? 0 : 1)
                    || Call(interp, res, "GetValue") as string != vp.SizeX + "x" + vp.SizeY)
                    Call(interp, self, "LoadAvailableSettings");
            }
            return original();
        }

        // FEOptionsPage.BeforePaint (every frame the page is shown): the page lives for the whole session and fills Display
        // Mode only in LoadAvailableSettings, so a mode switched elsewhere (Alt+Enter = ToggleFullscreen) left the item stale.
        // The shipped body runs; then, if the item disagrees with the device, it is re-selected quietly (no Notify handler).
        static object OptionsBeforePaint(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            // D-75 (NOT ORIGINAL): the Controls column follows the device in use (OptionsLoadKeys); before the shipped body,
            // which writes the key buttons' texts from BoundKey1/2.
            if (InputOf(interp) is UEInput input)
            {
                if (input.PadView != input.ControlsShowsPad(self))
                {
                    if (input.PadView && self.GetProp("LocalizedKeyName") is List<object> names) PadKeyNames(interp, names);
                    Call(interp, self, "LoadExistingKeys");
                }
            }
            object r = original();
            if (self.GetProp("ColorDepthCombo") is not UEObject depth || self.GetProp("ResolutionCombo") is not UEObject res) return r;
            IViewportDevice vp = ViewportOf(interp);
            // Mode or size changed elsewhere (Alt+Enter: the resolution list and the current resolution differ between a
            // window and fullscreen): refill the page as on opening — the shipped LoadAvailableSettings (GetRes list,
            // SetValue(GetCurrentRes), bInitialized off during the refill) plus this override's Display Mode.
            bool modeStale = UEInterpreter.ToInt(Call(interp, depth, "GetSelectedIndex")) != (vp.Fullscreen ? 0 : 1);
            bool resStale = Call(interp, res, "GetValue") as string != vp.SizeX + "x" + vp.SizeY;
            if (modeStale || resStale) Call(interp, self, "LoadAvailableSettings");
            return r;
        }

        // "Interface Size" (author decision 2026-11-08, NOT ORIGINAL, D-44): a player change (bInitialized; the page's own
        // refills run Quiet) saves the step; the frontend's next frame reads the new scale. The shipped body is not run.
        static object OptionsInterfaceSizeChanged(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            if (!UEInterpreter.ToBool(self.GetProp("bInitialized"))) return null;
            UEConfig cfg = UEConfig.Global ?? throw new InvalidOperationException("FEOptionsPage: config not initialized");
            int[] steps = InterfaceSizeSteps(ViewportOf(interp).SizeY);
            int step = steps[UEInterpreter.ToInt(Call(interp, Combo(self, "ObjectDetailCombo"), "GetSelectedIndex"))];
            cfg.Set(UEConfig.ScopeSystem, UEConfig.HPEngineSection, UEConfig.UIScaleStepKey,
                step.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return null;
        }

        // Display Mode change applies at once (ToggleDisplayMode: ToggleFullscreen saves the key pair of the new mode itself,
        // plus StartupFullscreen for the next launch, as Alt+Enter); resolution change — original body (SetRes, confirm dialog, revert on WindowDone).
        static object OptionsSettingsChanged(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            if (!UEInterpreter.ToBool(self.GetProp("bInitialized"))) return original();
            IViewportDevice vp = ViewportOf(interp);
            UEConfig cfg = UEConfig.Global ?? throw new InvalidOperationException("FEOptionsPage: config not initialized");
            bool wantFull = Call(interp, Combo(self, "ColorDepthCombo"), "GetValue2") as string == Fullscreen;
            if (wantFull != vp.Fullscreen)
            {
                UEConsoleCommand.ToggleDisplayMode(cfg, vp);
                Call(interp, self, "LoadAvailableSettings");
                return null;
            }
            string res = Call(interp, Combo(self, "ResolutionCombo"), "GetValue") as string;
            if (res == vp.SizeX + "x" + vp.SizeY) return null;
            return original();
        }

        // Jellybean.killbean.Tick (author decision: fix original bugs): the script
        // (Jellybean.uc:110-111) does fPickupFlyTime -= Delta; Move((target - Location) / (fPickupFlyTime / Delta)).
        // The distance lags by a tick, and on the tick where the remainder crosses zero the divisor is ~0: at a steady
        // rate where 0.25/dt is an integer (60/120/144 Hz) the float remainder is 1.1e-8 — the bean flies millions of
        // units away and the pickup sound (the Galaxy voice position follows the actor) is lost. Proven by measurement:
        // the live original with dt exactly 1/60 (patched in ULevel::Tick) flies off the same way
        // (fly=1.11758709e-08); the author's original runs at ~20 fps with uneven dt, where the remainder is random
        // and the bug is invisible. Fix: a divisor with magnitude below 1 (the zero-crossing tick) is replaced by 1 —
        // the last step lands exactly on the target. The original body runs: the remainder is temporarily raised so
        // that after the script's subtraction the divisor is exactly 1, then restored.
        // All other ticks run the bytecode as-is. That includes the FIRST tick: Touch -> GotoState('killbean'), and
        // 0.25 is written only by state code (Begin), which ProcessState runs AFTER the Tick event (Engine.dll.c:128700,
        // vtbl+0x14), so the first Tick runs with fPickupFlyTime=0 and divisor −1: the bean bounces off the target by
        // |T−L| and the pickup sound (Begin, after Tick) plays from the new point. Decision D-30 kept; the condition
        // is |divisor| < 1, not divisor < 1 (that would also catch the −1 and snap the bean to the camera at once).
        // Only this matches the Lev2_HogFront_2 journals (points 4, 5, 8) against the original.
        static object JellybeanFlyTick(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            if (!UEInterpreter.ClassIsA(self.Class, "Jellybean") || args is not { Length: > 0 }) return original();
            float delta = UEInterpreter.AsFloat(args[0]);
            float fly = UEInterpreter.AsFloat(self.GetProp("fPickupFlyTime"));
            float after = fly - delta;
            if (!(delta > 0f) || Math.Abs(after / delta) >= 1f) return original();
            self.Props["fPickupFlyTime"] = 2f * delta;
            object r = original();
            self.Props["fPickupFlyTime"] = after;
            return r;
        }

        // SneakNorris.stateJump.HitWall (author decision 2026-11-11, NOT ORIGINAL, D-81) — fix of a frame-rate
        // dependent original bug. The jump is script Euler (stateJump.Tick: MoveSmooth(JumpMoveVector*dt), then
        // gravity). The shipped HitWall ends the jump on ANY hit: SetPhysics(PHYS_Walking), navP = JumpDest,
        // GotoState('patrol'). On a bookcase the cat meets the side face below the top, so the climb is left to the
        // first walking substep's stepUp, whose forward step is AccelRate*dt^2 from rest (PHYS_None leaves Velocity
        // 0): ~2 units at the ~22-30 fps the original ran at, ~0.3 at 60 Hz, where the step-down misses the top and
        // the cat falls and walks into the bookcase for good. Proven by measurement (Lev4_Sneak2, Dispatcher5 ->
        // SneakNorris2, PatrolPoint63 -> 64): the live original at its own dt (~0.046 on the hit frame) lands on the
        // top (z 1129.47 -> 1142.50); the live original with dt fixed to 1/60 falls and ends at
        // (2592.02,-1863.10,918.50), exactly where the port ends at 60 Hz.
        // Rule: a hit that is NOT a walkable floor (HitNormal.Z <= 0.7, the physWalking floor test) does not end
        // the jump; MoveSmooth has already slid along that face, and the jump goes on until it lands (walkable
        // floor) or reaches JumpDest, where the shipped bodies run unchanged. A jump whose apex is too low still
        // slides back to the floor and ends as in the original. Bump (caught by Harry) is untouched.
        static object SneakJumpHitWall(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            if (!UEInterpreter.ClassIsA(self.Class, "SneakNorris") || args is not { Length: > 0 }) return original();
            return UEInterpreter.AsVector3(args[0]).z > 0.7f ? original() : null;
        }

        static object Tex(string path)
            => UENativesCore.ResolveDynamicLoad(path, "Texture", null)
               ?? throw new InvalidOperationException("UEPortScriptOverrides: no texture " + path);

        static object ScrollBarPaint(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            UEObject owner = self.GetProp(PK.ParentWindow) as UEObject;
            if (owner is null || !UEInterpreter.ClassIsA(owner.Class, "HPMenuOptionComboList")) return original();
            object canvas = args[0];
            float w = UEInterpreter.AsFloat(self.GetProp(PK.WinWidth)), h = UEInterpreter.AsFloat(self.GetProp(PK.WinHeight));
            float bh = UEInterpreter.AsFloat((self.GetProp(PK.LookAndFeel) as UEObject)?.GetProp(PK.Size_ScrollbarButtonHeight));
            // The menu textures' package: HP1 HPMenu.Icons, HP2 hgame.Icons (HP2's ComboList_DrawItem / HSlider.Created).
            string icons = UEGameProfile.Current.Game == UEEdition.Game.HP2 ? "hgame.Icons." : "HPMenu.Icons.";
            Call(interp, self, "DrawStretchedTexture", canvas, w / 2f - 1f, bh, 2f, h - 2f * bh, Tex(icons + "FEComboListBox"));
            if (UEInterpreter.ToBool(self.GetProp(PK.bDisabled))) return null;
            float ts = UEInterpreter.AsFloat(self.GetProp(PK.ThumbStart)), th = UEInterpreter.AsFloat(self.GetProp(PK.ThumbHeight));
            // The knob at native size, centred in the thumb area: stretched
            // brass looks distorted.
            float ky = ts + (th - 25f) / 2f;
            Call(interp, self, "DrawStretchedTextureSegment", canvas, (w - 9f) / 2f, ky, 9f, 25f, 0f, 0f, 9f, 25f,
                Tex(icons + "FESliderKnobTexture"));
            return null;
        }

        // C.Font = Root.Fonts[Font] (UWindowDialogControl.BeforePaint), then the original body.
        //
        // HPMenuOptionHSlider/HPMenuOptionCheckBox.BeforePaint (IMPROVEMENT, not original): neither body calls
        // super.BeforePaint, so UWindowDialogControl.BeforePaint's `C.Font = Root.Fonts[Font]` never runs, and
        // UWindowWindow.PaintClients does not reset the font between windows — the slider caption is drawn with the
        // PREVIOUS window's font (Music/Sound Volume after the "Audio" label — bold TahomaB10; after a click the window
        // moves to the end of the order — Tahoma10, X 72 -> 79; improvements list D-70). The port sets the control's font before the
        // original body, as other UWindowDialogControls do. The checkbox text is drawn with its own font in the
        // original too (UWindowButton.Paint sets it), but was measured with the foreign one (Checkbox_SetupSizes: height, TextY).
        static object OwnFontBeforePaint(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            if ((self.GetProp(PK.Root) as UEObject)?.GetProp("Fonts") is not List<object> fonts)
                throw new InvalidOperationException("UEPortScriptOverrides: Root has no Fonts");
            ((UEObject)args[0]).Props["Font"] = fonts[UEInterpreter.ToInt(self.GetProp(PK.Font))];
            return original();
        }

        public const int ComboVisible = 4;

        // HP1 FEOptionsPage.SensitivityChanged (NOT ORIGINAL, improvement; author 2026-11-20): the shipped body sets the player's
        // MouseSensitivity and never saves it (no SaveConfig in FEOptionsPage), so the slider reverted at every start. The
        // player's config is saved after the shipped body, as HP2's FEInputPage.HideWindow does.
        static object SensitivitySaved(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            UEObject player = Call(interp, self, "GetPlayerOwner") as UEObject ?? throw new InvalidOperationException("FEOptionsPage: no player owner");
            float before = UEInterpreter.AsFloat(player.GetProp("MouseSensitivity"));
            object r = original();
            // Only a real change: the page's own refill (LoadAvailableSettings -> Notify) sets the value it just read.
            if (UEInterpreter.AsFloat(player.GetProp("MouseSensitivity")) != before) Call(interp, player, "SaveConfig");
            return r;
        }

        static object Call(UEInterpreter interp, UEObject o, string fn, params object[] a)
            => interp.Invoke(o, interp.FindGlobalFunction(o, fn)
                ?? throw new InvalidOperationException("UEPortScriptOverrides: no function " + fn), a);

        static object ComboListBeforePaint(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            UEObject sb = self.GetProp(PK.VertSB) as UEObject;
            float pos = sb is null ? 0f : UEInterpreter.AsFloat(sb.GetProp(PK.Pos));
            object result = original();
            if (sb is null) return result;
            int count = UEInterpreter.ToInt(Call(interp, self.GetProp(PK.Items) as UEObject, "Count"));
            self.Props["MaxVisible"] = ComboVisible;
            if (count <= ComboVisible) return result;   // original behavior
            float winH = UEInterpreter.AsFloat(self.GetProp(PK.WinHeight));
            // Item height chosen so the fifth item does not peek out below (HP window 89,
            // VBorder 3): ceil((89-3)/4) = 22, the fourth is tightened by 2 pixels.
            int vbi = UEInterpreter.ToInt(self.GetProp(PK.VBorder));
            self.Props["ItemHeight"] = (int)Math.Ceiling((winH - vbi) / ComboVisible);
            float sbw = UEInterpreter.AsFloat((self.GetProp(PK.LookAndFeel) as UEObject)?.GetProp(PK.Size_ScrollbarWidth));
            float hb = UEInterpreter.ToInt(self.GetProp(PK.HBorder)), vb = UEInterpreter.ToInt(self.GetProp(PK.VBorder));
            float winW = UEInterpreter.AsFloat(self.GetProp(PK.WinWidth)) + sbw;
            self.Props["WinWidth"] = winW;
            Call(interp, sb, "ShowWindow");
            // Win95 arrows do not suit the parchment: scrolling is by knob, track
            // click and wheel.
            if (sb.GetProp(PK.UpButton) is UEObject up) Call(interp, up, "HideWindow");
            if (sb.GetProp(PK.DownButton) is UEObject down) Call(interp, down, "HideWindow");
            sb.Props["Pos"] = pos;
            Call(interp, sb, "SetRange", 0f, (float)count, (float)ComboVisible, 0f);
            sb.Props["WinLeft"] = winW - sbw - hb;
            sb.Props["WinTop"] = hb;
            sb.Props["WinWidth"] = sbw;
            sb.Props["WinHeight"] = winH - 2f * vb;
            return result;
        }

        // Mouse wheel (port): UWindow of this version has no wheel handlers.
        // Scrolls the open list (window under Root, visible, with a visible bar).
        public static bool ScrollOpenComboList(UEInterpreter interp, UEObject console, int delta)
        {
            UEObject root = console?.GetProp(PK.Root) as UEObject;
            for (UEObject w = root?.GetProp(PK.FirstChildWindow) as UEObject; w is not null; w = w.GetProp(PK.NextSiblingWindow) as UEObject)
            {
                if (!UEInterpreter.ClassIsA(w.Class, "UWindowComboList") || !UEInterpreter.ToBool(w.GetProp(PK.bWindowVisible))) continue;
                if (w.GetProp(PK.VertSB) is not UEObject sb || !UEInterpreter.ToBool(sb.GetProp(PK.bWindowVisible))) continue;
                Call(interp, sb, "Scroll", (float)delta);
                return true;
            }
            return false;
        }

        // FEBook.ChangePage (author decision 2026-11-12, IMPROVEMENT, NOT ORIGINAL; D-83). In the shipped bytecode the
        // drop-down list is a child of Root, not of the page (HPMenuOptionCombo.CreateComboList:
        // List = Root.CreateWindow(...)), and ChangePage only hides the old page (HideWindow -> WindowHidden down the
        // page's own children). The list is closed only by UWindowComboControl.CloseUp (from Close(bByParent),
        // FocusOtherWindow on a click elsewhere, or the combo button), so in the original a list left open on Options
        // stays drawn over the next page after Esc, and is drawn again when the book is reopened in game. Before the
        // shipped body runs, every open list is closed through its own script CloseUp (-> Owner.CloseUp: bListVisible,
        // EditBox, List.HideWindow; the combo button has no DownSound, so no sound). Then the original body runs.
        static object ChangePageClosesComboList(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            UEObject w = (self.GetProp(PK.Root) as UEObject)?.GetProp(PK.FirstChildWindow) as UEObject;
            while (w is not null)
            {
                UEObject next = w.GetProp(PK.NextSiblingWindow) as UEObject;   // CloseUp unlinks w from the sibling list
                if (UEInterpreter.ClassIsA(w.Class, "UWindowComboList") && UEInterpreter.ToBool(w.GetProp(PK.bWindowVisible))
                    && UEInterpreter.ToBool((w.GetProp(PK.Owner) as UEObject)?.GetProp("bListVisible")))
                    Call(interp, w, "CloseUp");
                w = next;
            }
            return original();
        }

        // FEBook.KeyEvent (FEBook.uc:767-781): with no modal window, a key press during the splash sets fShowSplashTime
        // to -1 (handleSplash then goes to the next screen) — shipped only `&& HPConsole.bDebugMode`; here without that
        // gate. Everything else is the shipped body.
        static object KeySkipsSplash(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            if ((self.GetProp(PK.Root) as UEObject)?.GetProp("ModalWindow") is null
                && UEInterpreter.ToBool(self.GetProp("bShowSplash")) && UEInterpreter.ToInt(args[1]) == 1)
            {
                self.Props["fShowSplashTime"] = -1f;
                return true;
            }
            return original();
        }

        // HP2 FEBook.KeyEvent (NOT ORIGINAL, D-77 for HP2, author decision 2026-11-16): its first branch, F7 pressed (Key 118,
        // Action IST_Press), returns true and with Shift down first flips HPConsole.bDebugMode — unlike HP1's F7
        // (HPConsole.ToggleDebugMode, gated by Version.bDebugEnabled), it needs no debug build, so every player could turn on
        // the developer mode (the cheat matcher's gate, the Tab console). Here the press is taken as shipped (true) without
        // the flip; every other key runs the shipped body.
        static object Hp2KeyEventNoDebugToggle(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
            => UEInterpreter.ToInt(args[1]) == 1 && UEInterpreter.ToInt(args[0]) == 118 ? true : original();

        // FEStoryBookPage.KeyEvent (FEStoryBookPage.uc:466-503): Space pressed goes to the next page once the page has
        // been shown longer than _MinTimeOnPage — the shipped debug-mode branch (bAllowPageSkipping = bDebugMode) for
        // every player, without its `iCurrentPage != 0` condition (author decision 2026-11-15, NOT ORIGINAL: with it,
        // page 0 and so every one-page story could not be skipped). GotoNextPage is the same call the page timer makes
        // (Tick), so the story's end, the bonus card of story 6 and the travel are unchanged. The debug "NextPage" button
        // and the F fast-skip stay debug-only (not built). Other keys: the shipped body.
        static object SpaceSkipsStoryPage(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            if (UEInterpreter.ToInt(args[1]) != 1 || UEInterpreter.ToInt(args[0]) != 32) return original();
            float now = UEInterpreter.AsFloat((Call(interp, self, "GetLevel") as UEObject)?.GetProp("TimeSeconds"));
            if (now - UEInterpreter.AsFloat(self.GetProp("_TimeSecondsSave")) > UEInterpreter.AsFloat(self.GetProp("_MinTimeOnPage")))
                Call(interp, self, "GotoNextPage");
            return true;
        }

        // Cache keyed by the function itself: the check runs on every script call, so it
        // is read WITHOUT a lock — a published snapshot never changes, a new function
        // publishes a copy.
        static Dictionary<UFunction, Handler> _byFunction = new(UERefComparer<UFunction>.Instance);
        static readonly object _lock = new object();

        // Test seam (headless selftest only, InternalsVisibleTo): a temporary body for one function; Dispose restores it.
        internal static IDisposable OverrideForTest(UFunction fn, Handler body)
        {
            lock (_lock)
            {
                var snap = new Dictionary<UFunction, Handler>(_byFunction, UERefComparer<UFunction>.Instance) { [fn] = body };
                System.Threading.Volatile.Write(ref _byFunction, snap);
            }
            return new Restore(fn);
        }
        sealed class Restore : IDisposable
        {
            readonly UFunction _fn; public Restore(UFunction fn) => _fn = fn;
            public void Dispose()
            {
                lock (_lock)
                {
                    var snap = new Dictionary<UFunction, Handler>(_byFunction, UERefComparer<UFunction>.Instance);
                    snap.Remove(_fn);
                    System.Threading.Volatile.Write(ref _byFunction, snap);
                }
            }
        }

        public static bool TryGet(UFunction fn, out Handler body)
        {
            if (System.Threading.Volatile.Read(ref _byFunction).TryGetValue(fn, out body)) return body is not null;
            lock (_lock)
            {
                if (_byFunction.TryGetValue(fn, out body)) return body is not null;
                body = Lookup(fn);
                var snap = new Dictionary<UFunction, Handler>(_byFunction, UERefComparer<UFunction>.Instance);
                snap[fn] = body;
                System.Threading.Volatile.Write(ref _byFunction, snap);
                return body is not null;
            }
        }

        static Handler Lookup(UFunction fn)
            => LookupName((fn.Outer as UClass)?.Name.ToString() ?? fn.Outer?.Name.ToString(), fn.Name.ToString());

        // Lookup's rule by names (owner class or state, function) in the current profile's table.
        internal static Handler LookupName(string owner, string function)
        {
            foreach (var e in Table)
                if (string.Equals(e.Function, function, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Class, owner, StringComparison.OrdinalIgnoreCase)) return e.Body;
            return null;
        }

        // TryGet of many functions with ONE snapshot copy (UELevelRuntime.WarmFirstUseCaches, at level load).
        public static void Warm(IEnumerable<UFunction> functions)
        {
            lock (_lock)
            {
                Dictionary<UFunction, Handler> snap = null;
                foreach (UFunction fn in functions)
                {
                    if (fn is null || _byFunction.ContainsKey(fn) || snap?.ContainsKey(fn) == true) continue;
                    (snap ??= new Dictionary<UFunction, Handler>(_byFunction, UERefComparer<UFunction>.Instance))[fn] = Lookup(fn);
                }
                if (snap is not null) System.Threading.Volatile.Write(ref _byFunction, snap);
            }
        }
    }
}
