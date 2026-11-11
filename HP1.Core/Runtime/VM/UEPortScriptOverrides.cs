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
// decision 2026-11-08, not original, D-44): Smaller / Standard / Larger, saved as [OpenHP1] UIScaleStep.
//
// FEOptionsPage.LoadAvailableSettings on UK11 (author decision 2026-11-08, NOT ORIGINAL): the same every-mode list,
// rebuilt after the shipped body because the UK11 bytecode inlines the filter (details at UkResolutions).
//
// FEOptionsPage.LocalizeStrings (author decision) — fix of original data: Pickup2.int lacks key names
// 0-9, F1-F10, Unknown5B-5D, MouseWheelUp/Down and None, so the binding button showed the Localize marker
// "<?int?pickup2.all.localized_keyname_049?>". The original body runs, then missing names are taken from
// the game's EInputKey enum name without "IK_" (the other .int strings are named that way: F11, NumPad0, Unknown5E).
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
            ("HPMenuOptionHSlider", "BeforePaint", OwnFontBeforePaint),
            ("HPMenuOptionCheckBox", "BeforePaint", OwnFontBeforePaint),
            ("baseHarry", "KeyDownEvent", (interp, self, args, original) => null),
            ("stateJump", "HitWall", SneakJumpHitWall),
        };

        // FEOptionsPage, "Video" column (author decision). The four list frames and four slider tracks
        // are PAINTED INTO THE PAGE BACKGROUND (HPMenu.Icons.FEOptionsBackTexture1..6); the controls are transparent
        // overlays in their places: they cannot move and every frame stays occupied. So items are not hidden but
        // repurposed:
        //   Resolution    — monitor modes (the frontend window device, IViewportDevice.DisplayModes);
        //   Color Depth   -> "Display Mode": Fullscreen / Windowed — stock ToggleFullscreen (WinDrv.dll.c:5090) and
        //                    StartupFullscreen (startup mode, UEViewportStartup), not a setting of our own.
        //   Object Detail -> "Interface Size" (author decision 2026-11-08, NOT ORIGINAL, D-44): Smaller / Standard /
        //                    Larger = [OpenHP1] UIScaleStep -1 / 0 / +1 (UEConfig.UIScaleFor). The shipped
        //                    ObjectDetailChanged (writes PlayerPawn.ObjectDetail, no reader in 1.53, D-27/D-33) is
        //                    replaced, not run: the item no longer means Object Detail.
        // Texture Detail is NOT replaced: the shipped bytecode fills and handles it (FEOptionsPage.uc:182-200, 414,
        // 578-583) -> `set ini:…ViewportManager TextureDetail`, read by the OpenHP1 frame (UETextureLod).
        // The original LoadAvailableSettings body runs in full (Created calls it, FEOptionsPage.uc:294), the edit
        // follows it (the lists are refilled every time there; it is also called after a resolution change).
        const string DisplayModeText = "Display Mode", InterfaceSizeText = "Interface Size";
        static readonly string[] InterfaceSizes = { "Smaller", "Standard", "Larger" };   // UIScaleStep -1 / 0 / +1
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
            return r;
        }

        static object OptionsLoadSettings(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
            object r = original();
            if ((interp.Natives as UEGameEngineNatives)?.DataSet == UEEdition.DataSet.UK11) Quiet(self, () => UkResolutions(interp, self));
            Quiet(self, () => SelectVideo(interp, self));
            return r;
        }

        // UK11 (author decision 2026-11-08, NOT ORIGINAL; D-70): the UK11 HPMenu.u has no IsSupportedResolution — its
        // LoadAvailableSettings inlines the 512x384/640x480/800x600/1024x768 filter — so the US override cannot reach it.
        // After the shipped body the resolution list is rebuilt as the US body builds it with the filter overridden:
        // every GetRes mode in order (AddItem), then SetValue(GetCurrentRes) (FEOptionsPage.uc:371-394).
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
            Fill(interp, size, InterfaceSizeText, Array.ConvertAll(InterfaceSizes, s => (s, s)));
            Call(interp, size, "SetSelectedIndex", (UEConfig.Global?.UIScaleStep ?? 0) + 1);
        }

        // FEOptionsPage.BeforePaint (every frame the page is shown): the page lives for the whole session and fills Display
        // Mode only in LoadAvailableSettings, so a mode switched elsewhere (Alt+Enter = ToggleFullscreen) left the item stale.
        // The shipped body runs; then, if the item disagrees with the device, it is re-selected quietly (no Notify handler).
        static object OptionsBeforePaint(UEInterpreter interp, UEObject self, object[] args, Func<object> original)
        {
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
            int step = UEInterpreter.ToInt(Call(interp, Combo(self, "ObjectDetailCombo"), "GetSelectedIndex")) - 1;
            cfg.Set(UEConfig.ScopeSystem, UEConfig.OpenHP1Section, UEConfig.UIScaleStepKey,
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
