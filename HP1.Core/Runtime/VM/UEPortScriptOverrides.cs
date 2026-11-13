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
using System;
using System.Collections.Generic;
using UELib.Core;

namespace HP1.UEScript
{
    public static class UEPortScriptOverrides
    {
        // original — the original function body (bytecode), for before/after overrides.
        public delegate object Handler(UEInterpreter interp, UEObject self, object[] args, Func<object> original);

        static readonly (string Class, string Function, Handler Body)[] Table =
        {
            ("FEOptionsPage", "IsSupportedResolution", (interp, self, args, original) => true),
            ("HPMenuOptionComboList", "BeforePaint", ComboListBeforePaint),
            ("UWindowVScrollBar", "Paint", ScrollBarPaint),
            ("killbean", "Tick", JellybeanFlyTick),
            ("FEOptionsPage", "LoadAvailableSettings", OptionsLoadSettings),
            ("FEOptionsPage", "SettingsChanged", OptionsSettingsChanged),
            ("FEOptionsPage", "ObjectDetailChanged", OptionsInterfaceSizeChanged),
            ("FEOptionsPage", "LocalizeStrings", OptionsLocalizeKeyNames),
            ("FEOptionsPage", "BeforePaint", OptionsBeforePaint),
            ("FEOptionsPage", "LoadExistingKeys", OptionsLoadKeys),
            ("FEOptionsPage", "ProcessKey", OptionsProcessKey),
            ("HPMenuOptionHSlider", "BeforePaint", OwnFontBeforePaint),
            ("HPMenuOptionCheckBox", "BeforePaint", OwnFontBeforePaint),
            ("baseHarry", "KeyDownEvent", (interp, self, args, original) => null),
            ("stateJump", "HitWall", SneakJumpHitWall),
            ("FEBook", "ChangePage", ChangePageClosesComboList),
        };

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
            Call(interp, combo, "SetText", text);
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

        // Pad view only: two lines naming the pad's fixed, not rebindable buttons (UEInput.GamepadFixedHint), English in
        // every edition like the pad key names. UWindowLabelControls like the page's other labels (font 0, PurpleColour)
        // under the left column: the strip between the Sound Volume slider (ends at y 403) and the page's bottom (430)
        // holds no control (the page's 528x430 units). The page object has no variable for them: kept here per page.
        const float PadHintX = 0f, PadHintY = 404f, PadHintW = 280f, PadHintH = 13f;
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UEObject, UEObject[]> PadHints = new();
        internal static UEObject[] PadHintLabels(UEObject page) => PadHints.TryGetValue(page, out UEObject[] h) ? h : null;
        static void PadHint(UEInterpreter interp, UEObject page, bool pad, string[] lines)
        {
            UEObject[] hints = PadHintLabels(page);
            if (hints is null)
            {
                if (!pad) return;
                UEObject like = page.GetProp("ControlLabel") as UEObject ?? throw new InvalidOperationException("FEOptionsPage: no ControlLabel");
                hints = new UEObject[lines.Length];
                for (int i = 0; i < hints.Length; i++)
                {
                    hints[i] = Call(interp, page, "CreateControl", like.Class, PadHintX, PadHintY + i * PadHintH, PadHintW, PadHintH) as UEObject
                        ?? throw new InvalidOperationException("FEOptionsPage: the pad hint label was not created");
                    Call(interp, hints[i], "SetFont", 0);
                    hints[i].Props["TextColor"] = page.GetProp("PurpleColour");
                }
                PadHints.Add(page, hints);
            }
            for (int i = 0; i < hints.Length; i++)
            {
                bool shown = UEInterpreter.ToBool(hints[i].GetProp("bWindowVisible"));
                if (!pad) { if (shown) Call(interp, hints[i], "HideWindow"); continue; }
                if (hints[i].GetProp("Text") as string != lines[i]) Call(interp, hints[i], "SetText", lines[i]);
                if (!shown) Call(interp, hints[i], "ShowWindow");
            }
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
            UEObject depth = Combo(self, "ColorDepthCombo");
            Fill(interp, depth, DisplayModeText, (Fullscreen, Fullscreen), (Windowed, Windowed));
            Call(interp, depth, "SetSelectedIndex", ViewportOf(interp).Fullscreen ? 0 : 1);
            UEObject size = Combo(self, "ObjectDetailCombo");
            int[] steps = InterfaceSizeSteps(ViewportOf(interp).SizeY);
            Fill(interp, size, InterfaceSizeText, Array.ConvertAll(steps, st => (InterfaceSizes[st + 1], InterfaceSizes[st + 1])));
            int at = Array.IndexOf(steps, UEConfig.Global?.UIScaleStep ?? 0);   // a saved step not offered here = Standard
            Call(interp, size, "SetSelectedIndex", at >= 0 ? at : Array.IndexOf(steps, 0));
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
                PadHint(interp, self, input.ControlsShowsPad(self), input.GamepadFixedHint());
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

        // Display Mode change applies at once (ToggleFullscreen saves the key pair of the new mode itself) plus
        // StartupFullscreen for the next launch; resolution change — original body (SetRes, confirm dialog, revert on WindowDone).
        static object OptionsSettingsChanged(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            if (!UEInterpreter.ToBool(self.GetProp("bInitialized"))) return original();
            IViewportDevice vp = ViewportOf(interp);
            UEConfig cfg = UEConfig.Global ?? throw new InvalidOperationException("FEOptionsPage: config not initialized");
            bool wantFull = Call(interp, Combo(self, "ColorDepthCombo"), "GetValue2") as string == Fullscreen;
            if (wantFull != vp.Fullscreen)
            {
                UEConsoleCommand.Execute(cfg, "ToggleFullscreen", null, vp);
                UEConsoleCommand.Execute(cfg, "set ini:Engine.Engine.ViewportManager StartupFullscreen " + (vp.Fullscreen ? "True" : "False"), null, vp);
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
            Call(interp, self, "DrawStretchedTexture", canvas, w / 2f - 1f, bh, 2f, h - 2f * bh, Tex("HPMenu.Icons.FEComboListBox"));
            if (UEInterpreter.ToBool(self.GetProp(PK.bDisabled))) return null;
            float ts = UEInterpreter.AsFloat(self.GetProp(PK.ThumbStart)), th = UEInterpreter.AsFloat(self.GetProp(PK.ThumbHeight));
            // The knob at native size, centred in the thumb area: stretched
            // brass looks distorted.
            float ky = ts + (th - 25f) / 2f;
            Call(interp, self, "DrawStretchedTextureSegment", canvas, (w - 9f) / 2f, ky, 9f, 25f, 0f, 0f, 9f, 25f,
                Tex("HPMenu.Icons.FESliderKnobTexture"));
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
        {
            string name = fn.Name.ToString();
            string cls = (fn.Outer as UClass)?.Name.ToString() ?? fn.Outer?.Name.ToString();
            foreach (var e in Table)
                if (string.Equals(e.Function, name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Class, cls, StringComparison.OrdinalIgnoreCase)) return e.Body;
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
