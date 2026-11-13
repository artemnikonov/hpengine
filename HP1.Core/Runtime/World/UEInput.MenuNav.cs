// D-PAD MENU NAVIGATION — IMPROVEMENT, NOT ORIGINAL (author decision 2026-11-12; D-75 addition in
// audit/DEFERRED_DEBT_2026-10-04.md).
//
// While the console's UWindow is up (MenuActive), a d-pad press moves the menu cursor to the centre of the nearest clickable
// control in that direction; A (LeftMouse) clicks there, as with a mouse. The d-pad's JoyPov keys still go through the
// ported joystick block unchanged (so the Controls page can still bind them); in game nothing here runs.
//
// The cursor is HPConsole.MouseX/MouseY in Root coordinates, moved ONLY by the normal input path: MouseX/MouseY axis events
// through InputEvent -> console KeyEvent, where WindowConsole state UWindow (UWindow.u, IST_Axis) does
// MouseX += MouseScale*Delta, MouseY -= MouseScale*Delta; HPConsole.RenderUWindow clamps it and Root.MoveMouse follows it.
//
// Targets come from Core's own UWindow tree (read, never written by this code):
//   * the shown windows: Root's child lists (FirstChildWindow/NextSiblingWindow, recursively). HideChildWindow unlinks a
//     hidden window from its parent's list (UWindowWindow.HideChildWindow), so the lists ARE the shown tree;
//   * absolute rectangle = WinLeft/WinTop summed up the ParentWindow chain (Root is at 0,0), size WinWidth/WinHeight;
//   * clickable = its class handles a left click: Click, LMouseDown or LMouseUp resolves below UWindowWindow (whose own
//     bodies only activate the window / turn down-up into Click(), which is empty there). In the shipped packages that is
//     UWindowButton (Click -> Notify(DE_Click); with HPMenuButton/HPMenuRaisedButton/UWindowSmallButton, hudSpellButton),
//     UWindowCheckbox, UWindowComboButton, UWindowEditBox (combo text), UWindowComboList, UWindowListBox,
//     UWindowHSliderControl, UWindowVScrollBar. bAcceptsFocus is NOT the rule: the Controls page key buttons are clickable
//     with bAcceptsFocus = false (FEOptionsPage.uc:274);
//   * a button whose click is only UWindowButton.Click (Notify(DE_Click) + sound) does something only through the window it
//     notifies: UWindowDialogControl.Notify sends NotifyWindow.Notify(self, E) and nothing else. So it is a target only
//     if its NotifyWindow's Notify (any override up its class chain) reads a property of that window that holds the
//     button (directly, in a static array, or in a struct of one: SaveSlots[I].Button). Titles and labels made of buttons
//     drop out: FEOptionsPage.OptionsLabel, FESlotPage.WindowTitle / SlotNumberDisplays, FEQuidMatchPage.PageTitle (the
//     empty text under the snitch), FEFolioPage.LargeCardBmp (never registered);
//   * a button that draws nothing is not a target (UWindowButton.Paint draws a state texture and Text, nothing else;
//     UWindowSmallButton.Paint adds LookAndFeel.Button_DrawSmallButton, an empty body in UWindowWin95LookAndFeel): the
//     folio's small cards on Harry's page (DisplayHarryPage clears their textures) and FEBook.DismissButton, the 10x10
//     corner hot spot that closes the book (B / Escape does that);
//   * not bDisabled;
//   * a click at its centre reaches it: Root.FindWindowUnder(centre) (the script function the click path uses, through
//     MessageClients) returns this window — so covered, pass-through and off-Root controls drop out;
//   * while a modal window is up (Root.ModalWindow visible) only its tree: FEBook and baseFEPage drop every event but
//     Paint then (their WindowEvent gates on Root.WaitModal()).
// Choice: candidates whose centre is in front of the cursor in the pressed direction (the half-plane), least (distance
// along the axis + 2 x distance across it); ties go to tree order. Nothing in that direction: nothing happens. (A strict
// 45-degree cone first skipped Brightness going down the Options page: its centre is 49 px across, 41.5 px along.) If the
// previous target is no longer a candidate (page change, first press), the press goes to the control nearest the cursor,
// whatever the direction.
// Sliders: while the cursor rests on a slider target, Left/Right change its value through UWindowHSliderControl.KeyDown
// (IK_Left / IK_Right: SetValue(Value -/+ Step), the slider's keyboard path); Up/Down move on.
// An open combo list is modal: Up/Down move the cursor onto the previous/next item, so UWindowComboList.MouseMove ->
// SetSelected highlights it as the mouse would; past the visible rows the list scrolls by VertSB.Scroll(+-1) (the call
// MouseMove makes when the mouse leaves the list). A clicks (LeftMouse -> LMouseUp -> ExecuteItem: SetValue + CloseUp);
// B closes it through UWindowComboList.CloseUp (Owner.CloseUp, the path a focus change takes) without a value change and
// is not sent as Escape. When a list opens, the cursor goes onto the item of the combo's current value.
// Repeat while held: first after NavRepeatDelay, then every NavRepeatInterval (level delta, the dt ReadInput gets). A
// direction already held when the menu opens does nothing until released.
using System;
using System.Collections.Generic;
using UELib.Core;

namespace HP1.UEScript
{
    public sealed partial class UEInput
    {
        // UI tuning, not from a source (author asked to tune, 2026-11-13): delay as a keyboard's autorepeat (Windows default
        // ~0.5 s), then 10 steps/s, the pace of console menu lists; slower than a keyboard (~30/s) so a hold through a column
        // of buttons stays readable.
        const float NavRepeatDelay = 0.4f, NavRepeatInterval = 0.1f;
        static readonly (float X, float Y)[] NavDirs = { (0, -1), (1, 0), (0, 1), (-1, 0) };   // up, right, down, left (Root y down)

        float _pollDelta;
        int _navDir = -1;
        float _navRepeatIn;
        UEObject _navTarget;

        // dir: 0 up, 1 right, 2 down, 3 left, -1 none (the POV order PollGamepad reports).
        void MenuNavPoll(UEInterpreter interp, UEObject console, UEObject pawn, bool menu, int dir)
        {
            if (!menu) { _navDir = dir; _navRepeatIn = float.PositiveInfinity; return; }
            if (dir != _navDir)
            {
                _navDir = dir;
                _navRepeatIn = NavRepeatDelay;
                if (dir >= 0) NavStep(interp, console, pawn, dir);
                return;
            }
            if (dir < 0) return;
            _navRepeatIn -= _pollDelta;
            if (_navRepeatIn > 0f) return;
            _navRepeatIn += NavRepeatInterval;
            NavStep(interp, console, pawn, dir);
        }

        // Once per poll in a menu, before the d-pad: a list that opened gets the cursor on the current value's item. Placed on
        // the second poll that sees it open: the list takes its place and size in its first BeforePaint after DropDown
        // (HPMenuOptionComboList.BeforePaint), which runs in the frame between the two polls.
        bool _listWasOpen, _listPlaced;
        void MenuListPoll(UEInterpreter interp, UEObject console, UEObject pawn, bool menu)
        {
            UEObject list = menu ? OpenComboList(console?.GetProp("Root") as UEObject) : null;
            bool open = list is not null;
            // Closed (A picked, B or a click elsewhere): the cursor goes back onto the control that opened it, if still there.
            if (!open && _listWasOpen && _navTarget is UEObject t
                && MenuTargets(interp, console.GetProp("Root") as UEObject).Find(c => ReferenceEquals(c.W, t)) is var back && back.W is not null)
                MoveCursor(interp, console, pawn, back.X, back.Y);
            if (!open) _listPlaced = false;
            else if (_listWasOpen && !_listPlaced)
            {
                _listPlaced = true;
                List<UEObject> items = ListItems(list);
                string value = Call(interp, list.GetProp("Owner") as UEObject, "GetValue") as string;
                int at = items.FindIndex(i => string.Equals(i.GetProp("Value") as string, value, StringComparison.Ordinal));
                if (at >= 0) ShowListItem(interp, console, pawn, list, items.Count, at);
            }
            _listWasOpen = open;
        }

        // B in a menu: an open combo list is closed (UWindowComboList.CloseUp) instead of sending Escape.
        static bool CloseOpenList(UEInterpreter interp, UEObject console)
        {
            if (OpenComboList(console?.GetProp("Root") as UEObject) is not UEObject list) return false;
            Call(interp, list, "CloseUp");
            return true;
        }

        void NavStep(UEInterpreter interp, UEObject console, UEObject pawn, int dir)
        {
            if (console?.GetProp("Root") is not UEObject root) return;
            if (OpenComboList(root) is UEObject list) { ListStep(interp, console, pawn, list, dir); return; }
            if ((dir == 1 || dir == 3) && _navTarget is UEObject t && UEInterpreter.ClassIsA(t.Class, "UWindowHSliderControl")
                && MenuTargets(interp, root).Find(c => ReferenceEquals(c.W, t)) is var at && at.W is not null
                && Math.Abs(at.X - UEInterpreter.AsFloat(console.GetProp("MouseX"))) < 0.5f
                && Math.Abs(at.Y - UEInterpreter.AsFloat(console.GetProp("MouseY"))) < 0.5f)
            {
                Call(interp, t, "KeyDown", dir == 3 ? 37 : 39, 0f, 0f);   // IK_Left / IK_Right
                return;
            }
            MenuNavigate(interp, console, pawn, dir);
        }

        void ListStep(UEInterpreter interp, UEObject console, UEObject pawn, UEObject list, int dir)
        {
            if (dir != 0 && dir != 2) return;
            List<UEObject> items = ListItems(list);
            int cur = list.GetProp("Selected") is UEObject sel ? items.IndexOf(sel) : -1;
            int next = cur < 0 ? 0 : Math.Clamp(cur + (dir == 0 ? -1 : 1), 0, items.Count - 1);
            if (items.Count == 0 || next == cur) return;
            ShowListItem(interp, console, pawn, list, items.Count, next);
        }

        // Scroll item `at` into the visible rows (VertSB.Scroll), then put the cursor on its row (UWindowComboList.Paint /
        // SetSelected geometry: row = index - VertSB.Pos, y = VBorder + ItemHeight*row, visible rows = Min(Count, MaxVisible)).
        void ShowListItem(UEInterpreter interp, UEObject console, UEObject pawn, UEObject list, int count, int at)
        {
            UEObject sb = list.GetProp("VertSB") as UEObject;
            int visible = Math.Min(count, UEInterpreter.ToInt(list.GetProp("MaxVisible")));
            float pos = sb is null ? 0f : UEInterpreter.AsFloat(sb.GetProp("Pos"));
            if (sb is not null && at < pos) Call(interp, sb, "Scroll", at - pos);
            else if (sb is not null && at > pos + visible - 1) Call(interp, sb, "Scroll", at - (pos + visible - 1));
            pos = sb is null ? 0f : UEInterpreter.AsFloat(sb.GetProp("Pos"));
            var (ox, oy) = Origin(list);
            float itemH = UEInterpreter.AsFloat(list.GetProp("ItemHeight")), hb = UEInterpreter.AsFloat(list.GetProp("HBorder"));
            float w = UEInterpreter.AsFloat(list.GetProp("WinWidth")) - 2f * hb
                - (sb is not null && UEInterpreter.ToBool(sb.GetProp("bWindowVisible")) ? UEInterpreter.AsFloat(sb.GetProp("WinWidth")) : 0f);
            float x = ox + hb + w / 2f, y = oy + UEInterpreter.AsFloat(list.GetProp("VBorder")) + itemH * (at - pos) + itemH / 2f;
            // A scroll that leaves the cursor where it was gets no MouseMove from Root.MoveMouse (it fires on a moved
            // cursor only), so the list's own hover step runs here: UWindowComboList.MouseMove -> SetSelected.
            if (Math.Abs(x - UEInterpreter.AsFloat(console.GetProp("MouseX"))) < 0.5f && Math.Abs(y - UEInterpreter.AsFloat(console.GetProp("MouseY"))) < 0.5f)
                Call(interp, list, "MouseMove", x - ox, y - oy);
            else MoveCursor(interp, console, pawn, x, y);
        }

        static List<UEObject> ListItems(UEObject list)
        {
            var items = new List<UEObject>();
            for (UEObject i = (list.GetProp("Items") as UEObject)?.GetProp("Next") as UEObject; i is not null; i = i.GetProp("Next") as UEObject)
                items.Add(i);
            return items;
        }

        static (float X, float Y) Origin(UEObject w)
        {
            float x = 0f, y = 0f;
            for (; w is not null && w.GetProp("ParentWindow") is UEObject; w = w.GetProp("ParentWindow") as UEObject)
            { x += UEInterpreter.AsFloat(w.GetProp("WinLeft")); y += UEInterpreter.AsFloat(w.GetProp("WinTop")); }
            return (x, y);
        }

        static object Call(UEInterpreter interp, UEObject o, string fn, params object[] a)
            => interp.Invoke(o, interp.FindGlobalFunction(o, fn)
                ?? throw new InvalidOperationException($"menu navigation: {o?.Class?.Name} has no {fn}"), a);

        // The cursor moves only as a mouse moves it: MouseX/MouseY axis events through the console.
        void MoveCursor(UEInterpreter interp, UEObject console, UEObject pawn, float x, float y)
        {
            float scale = UEInterpreter.AsFloat(console.GetProp("MouseScale"));
            if (scale == 0f) return;
            float mx = UEInterpreter.AsFloat(console.GetProp("MouseX")), my = UEInterpreter.AsFloat(console.GetProp("MouseY"));
            IReadOnlyList<string> names = KeyNamesByIndex();
            float ax = (x - mx) / scale, ay = -(y - my) / scale;   // MouseY -= MouseScale*Delta
            if (ax != 0f) InputEvent(interp, console, pawn, names[0xe4], InputAction.Axis, ax);
            if (ay != 0f) InputEvent(interp, console, pawn, names[0xe5], InputAction.Axis, ay);
        }

        void MenuNavigate(UEInterpreter interp, UEObject console, UEObject pawn, int dir)
        {
            if (console?.GetProp("Root") is not UEObject root) return;
            float mx = UEInterpreter.AsFloat(console.GetProp("MouseX")), my = UEInterpreter.AsFloat(console.GetProp("MouseY"));
            List<(UEObject W, float X, float Y)> found = MenuTargets(interp, root);
            int pick = -1;
            if (_navTarget is null || !found.Exists(c => ReferenceEquals(c.W, _navTarget)))
            {
                float best = float.MaxValue;
                for (int i = 0; i < found.Count; i++)
                {
                    float dx = found[i].X - mx, dy = found[i].Y - my, d = dx * dx + dy * dy;
                    if (d < best) { best = d; pick = i; }
                }
            }
            else pick = PickInDirection(found, mx, my, dir);
            if (pick < 0) return;
            _navTarget = found[pick].W;
            MoveCursor(interp, console, pawn, found[pick].X, found[pick].Y);
        }

        internal static int PickInDirection(List<(UEObject W, float X, float Y)> found, float mx, float my, int dir)
        {
            var (ux, uy) = NavDirs[dir];
            int pick = -1;
            float best = float.MaxValue;
            for (int i = 0; i < found.Count; i++)
            {
                float vx = found[i].X - mx, vy = found[i].Y - my;
                float along = vx * ux + vy * uy, across = Math.Abs(vx * uy - vy * ux);
                if (along <= 0f) continue;
                float score = along + 2f * across;
                if (score < best) { pick = i; best = score; }
            }
            return pick;
        }

        // The clickable controls of the shown UWindow tree with their centres in Root coordinates (rules in the header).
        internal static List<(UEObject W, float X, float Y)> MenuTargets(UEInterpreter interp, UEObject root)
        {
            var found = new List<(UEObject, float, float)>();
            UFunction under = interp.FindGlobalFunction(root, "FindWindowUnder")
                ?? throw new InvalidOperationException("menu navigation: Root has no FindWindowUnder");
            float rootW = UEInterpreter.AsFloat(root.GetProp("WinWidth")), rootH = UEInterpreter.AsFloat(root.GetProp("WinHeight"));
            void Walk(UEObject parent, float left, float top)
            {
                for (UEObject w = parent.GetProp("FirstChildWindow") as UEObject; w is not null; w = w.GetProp("NextSiblingWindow") as UEObject)
                {
                    float x = left + UEInterpreter.AsFloat(w.GetProp("WinLeft")), y = top + UEInterpreter.AsFloat(w.GetProp("WinTop"));
                    float cx = x + UEInterpreter.AsFloat(w.GetProp("WinWidth")) / 2f, cy = y + UEInterpreter.AsFloat(w.GetProp("WinHeight")) / 2f;
                    if (HandlesClick(interp, w) && !UEInterpreter.ToBool(w.GetProp("bDisabled"))
                        && (!IsNotifyOnlyButton(interp, w) || (NotifiedWindowReadsIt(w) && ButtonDraws(interp, w)))
                        && cx >= 0f && cy >= 0f && cx <= rootW && cy <= rootH
                        && ReferenceEquals(interp.Invoke(root, under, cx, cy), w))
                        found.Add((w, cx, cy));
                    Walk(w, x, y);
                }
            }
            // A visible modal window (the condition of UWindowWindow.WaitModal, read without its write) blocks clicks to the
            // book and its pages (FEBook.WindowEvent, baseFEPage.WindowEvent: only Paint passes): only its own tree is walked.
            if (root.GetProp("ModalWindow") is UEObject modal && UEInterpreter.ToBool(modal.GetProp("bWindowVisible")))
            {
                float ox = 0f, oy = 0f;
                for (UEObject w = modal; w is not null && !ReferenceEquals(w, root); w = w.GetProp("ParentWindow") as UEObject)
                { ox += UEInterpreter.AsFloat(w.GetProp("WinLeft")); oy += UEInterpreter.AsFloat(w.GetProp("WinTop")); }
                Walk(modal, ox, oy);
            }
            else Walk(root, 0f, 0f);
            return found;
        }

        // The dropped-down list of a combo box: a shown UWindowComboList in the tree (UWindowComboControl.DropDown shows it).
        internal static UEObject OpenComboList(UEObject root)
        {
            UEObject Find(UEObject parent)
            {
                for (UEObject w = parent?.GetProp("FirstChildWindow") as UEObject; w is not null; w = w.GetProp("NextSiblingWindow") as UEObject)
                {
                    if (UEInterpreter.ClassIsA(w.Class, "UWindowComboList")) return w;
                    if (Find(w) is UEObject found) return found;
                }
                return null;
            }
            return Find(root);
        }

        static string OwnerOf(UEInterpreter interp, UEObject w, string fn) => interp.FindGlobalFunction(w, fn)?.Outer?.Name?.ToString();

        // Its click is UWindowButton.Click and nothing of its own: down/up are the base UWindowWindow ones.
        static bool IsNotifyOnlyButton(UEInterpreter interp, UEObject w)
            => OwnerOf(interp, w, "Click") == "UWindowButton" && OwnerOf(interp, w, "LMouseDown") == "UWindowWindow"
               && OwnerOf(interp, w, "LMouseUp") == "UWindowWindow";

        // The NotifyWindow's Notify bodies (every override up the class chain) read a property that holds this button.
        static readonly Dictionary<UClass, HashSet<string>> _notifyReads = new();
        static bool NotifiedWindowReadsIt(UEObject w)
        {
            if (w.GetProp("NotifyWindow") is not UEObject nw || nw.Class is null) return false;
            if (!_notifyReads.TryGetValue(nw.Class, out HashSet<string> reads))
            {
                reads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (UClass c = nw.Class; c is not null; c = UEInterpreter.CanonicalSuper(c))
                    foreach (UFunction fn in c.EnumerateFields<UFunction>())
                        if (string.Equals(fn.Name?.ToString(), "Notify", StringComparison.OrdinalIgnoreCase))
                            foreach (var tok in UEInterpreter.LoadTokens(fn))
                                if (tok is UStruct.UByteCodeDecompiler.InstanceVariableToken iv && iv.Object?.Name?.ToString() is string n)
                                    reads.Add(n);
                _notifyReads[nw.Class] = reads;
            }
            foreach (string prop in reads)
                if (Holds(nw.GetProp(prop), w, 0)) return true;
            return false;
        }

        static bool Holds(object v, UEObject w, int depth)
        {
            if (ReferenceEquals(v, w)) return true;
            if (depth > 2) return false;
            if (v is System.Collections.IList list) { foreach (object e in list) if (Holds(e, w, depth + 1)) return true; }
            else if (v is UEObject s && s.IsStruct) { foreach (var kv in s.Props) if (Holds(kv.Value, w, depth + 1)) return true; }
            return false;
        }

        // UWindowButton.Paint draws the texture of its state and Text; UWindowSmallButton.Paint first calls
        // LookAndFeel.Button_DrawSmallButton. Any other Paint override counts as drawing.
        static bool ButtonDraws(UEInterpreter interp, UEObject w)
        {
            string paint = OwnerOf(interp, w, "Paint");
            if (paint == "UWindowSmallButton")
            {
                UEObject laf = w.GetProp("LookAndFeel") as UEObject;
                UFunction draw = laf is null ? null : interp.FindGlobalFunction(laf, "Button_DrawSmallButton");
                if (draw is not null && !UEInterpreter.IsEmptyScriptBody(draw)) return true;
            }
            else if (paint != "UWindowButton") return true;
            if (!string.IsNullOrEmpty(w.GetProp("Text") as string)) return true;
            foreach (string t in new[] { "UpTexture", "DownTexture", "OverTexture", "DisabledTexture" })
                if (w.GetProp(t) is not null) return true;
            return false;
        }

        static bool HandlesClick(UEInterpreter interp, UEObject w)
        {
            foreach (string fn in new[] { "Click", "LMouseDown", "LMouseUp" })
                if (interp.FindGlobalFunction(w, fn)?.Outer?.Name?.ToString() is string owner && owner != "UWindowWindow") return true;
            return false;
        }
    }
}
