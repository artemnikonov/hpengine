# OpenHP1 input: SDL3 -> Core (openhp1/Input)

Input on SDL3 events, as the WinDrv window procedure (UWindowsViewport) delivers them; everything after
`CauseInputEvent` is Core's (`UEInput`: console `KeyEvent` first, PreProcess dedup, bindings; `UpdateInput` poll and
`ReadInput` at the player's tick slot; `ResetInputFull` on focus and LoadMap). Source: `audit/A7_INPUT_PATH_SPEC.md`.

## What is here

| function | code |
|---|---|
| key table -> EInputKey names (order significant) | `SdlKeyMap.cs` (`Keys`, SDL scancodes) |
| key down (autorepeat too) / up, mouse buttons -> `CauseInput` Press/Release; Alt+Enter -> ToggleFullscreen | `InputStage.OnControl` |
| a frame's mouse moves -> one MouseX + one MouseY Axis at the first move's place (WM_MOUSEMOVE batch) | `InputStage.OnMouseMotion`, `RunFrame` |
| wheel per event -> MouseW Axis ±120, MouseWheelUp/Down Press+Release (+ combo scroll, author decision) | `InputStage.OnMouseWheel`, `RunFrame` |
| text and Backspace/Tab/Escape -> `CharInput` (Console.KeyType) | `InputStage.OnTextInput`, `OnKey` |
| focus lost/gained -> `Focus` (UInput::ResetInput) | `InputStage.OnWindowEvent` |
| GetKeyState for Core's `UpdateInput` | `InputStage.IsKeyDown` via `BindDevice` |

The sink is the `IInputSink` interface (tested without a world); the Core implementation is `CoreInputSink`. Events are
queued by `Feed` in OS order and handed to Core by `RunFrame` before the tick — nothing runs between, so the order is the
window procedure's.

## Decisions

1. **Digits.** Physical digit i -> `IK_i`, as VK '0'..'9' in WinDrv. Mutation `keymap_shift_as_digit` is red.
2. **Alt+Enter autorepeat** does not toggle again (author improvement, not original).
3. **Wheel** also scrolls an open combo list (author decision, not original; `UEPortScriptOverrides.ScrollOpenComboList`).
4. **Gamepad** (author decision 2026-10-30, improvement, not original; D-75): `SdlGamepad` passes the raw SDL gamepad state;
   Core (`UEInput.Gamepad.cs`) turns it into the ported WinDrv joystick input plus default Joy* bindings.
5. **Open (author):** the OS mouse thresholds shipped sets while capturing (SPI_SETMOUSE {0,0,0x10000}) are not modelled
   beyond the SDL system-scale hint.

## NOT here, and why

* Cursor capture: `InputStage` decides (SetMouseCapture events: console ready, focus, mode switch via
  `ViewportChanged`); `SdlMouseCapture.Apply` executes it on the window (raise, relative mode = hidden + confined).
  Windowed capture without a click is not original (author 2026-11-07, DEFERRED_DEBT improvements).
  Released (focus loss), a move is `MousePosition` (Core `UEInput.MousePosition` -> `Viewport.WindowsMouseX/Y`, what
  HPConsole draws while `bWindowsMouseAvailable`), fed from the host's `CursorPosition`, also right after the release.
* Device loss: Core's `UpdateInput` poll releases a key the device reports up (GetKeyState via `IsKeyDown`).
* `SDL_Keycode`: letters, digit row and ASCII signs follow the OS layout (`SdlKeyMap.LayoutScancode`, as Windows VK in
  WinDrv).

## Not checked without game files

* That every table name exists in the loaded `EInputKey` (`UEConsoleCommand.FindKeyName`). An unresolved name is a
  loud counter `CoreInputSink.UnresolvedKeys`; on the game path `UEInput.InputEvent` itself throws on an unknown key.
* `CoreInputSink` branches with the console (`Console.KeyEvent`, `OpenBook`, `ScrollOpenComboList`,
  `ViewportCharInput`, `bWindowsMouseAvailable`, `Pauser`): need a running world.
* Mouse delta units (closed per WinDrv and the live original): the original takes the OS cursor delta (WM_MOUSEMOVE,
  WinDrv.dll.c:3682-3778, UseDirectInput=False), and the engine is linear after that (0.06·px, then ×20/dt, measured by
  `tools/origprobe/hp1mouse.c`; the port's `--input-probe --mouse-scale` gives the same numbers). SDL3 yields raw device
  counts by default, so `InputStage` sets `SDL_HINT_MOUSE_RELATIVE_SYSTEM_SCALE=1` (test `mouse_batch`, mutations
  `mouse_raw_units`/`mouse_hint_dropped`). On Windows/Deck hardware there is nothing to compare the SDL acceleration
  curve with the OS against (under CrossOver SendInput is doubled by CrossOver itself: dx=3 -> 6 px). The Y sign is
  flipped by the axis convention ("up = +").
* A wheel with `SDL_MOUSEWHEEL_FLIPPED` is taken as is (sign unchanged) and counted in `InputStage.FlippedWheelEvents`.

## Tests

    DOTNET_ROLL_FORWARD=Major dotnet run --project openhp1/tests/InputTests -c Release
    bash openhp1/tests/InputTests/redtest.sh

`DOTNET_ROLL_FORWARD=Major` is needed only where there is no .NET 9 runtime; it is not in the csproj.
Expectations in the tests are written by hand from the source rules (each `Expect` cites them), not from the output of the
port.

Test output:

```
PASS keymap_table
PASS keymap_digits_not_modifiers
PASS game_press_hold_release
PASS game_repeat_dedup
PASS game_press_release_same_frame
PASS game_two_physical_one_name
PASS game_lost_transition_level_pass_order
PASS game_mouse_axes
PASS game_pause_no_holdframe
PASS game_alt_enter
PASS game_no_player_keeps_queue
PASS menu_open_pending_page
PASS menu_open_startup_no_openbook
PASS escape_owned_by_menu
PASS menu_keys_mouse_axes_order
PASS menu_wheel
PASS text_input
PASS unknown_keys_loud
PASS sdl_event_dispatch
PASS console_wait_warning
PASS menu_region_width
PASS core_sink_ueinput
TOTAL: passed 22, failed 0
```

Output of `redtest.sh` (each mutation changes exactly one line of the port; the base runs before and after):

```
base before mutations: TOTAL: passed 22, failed 0
RED keymap_tilde — FAIL keymap_table: SDL_SCANCODE_GRAVE -> 'Tilde', got 'Grave'
RED keymap_shift_as_digit — FAIL keymap_table: SDL_SCANCODE_LSHIFT -> 'Shift', got '1'
RED keymap_digit_swap — FAIL keymap_table: names 100, got 99
RED repeat_not_dedup — FAIL game_repeat_dedup: call stream differs
RED name_count_release — FAIL game_two_physical_one_name: call stream differs
RED altenter_transition — FAIL game_alt_enter: call stream differs
RED mouse_y_sign — FAIL game_mouse_axes: call stream differs
RED unknown_not_counted — FAIL unknown_keys_loud: LGUI counted 2 times (presses), got 0
RED feed_text_dropped — FAIL sdl_event_dispatch: call stream differs
RED feed_motion_axes — FAIL sdl_event_dispatch: call stream differs
RED menu_snapshot_late — FAIL escape_owned_by_menu: call stream differs
RED wheel_sign — FAIL menu_wheel: call stream differs
RED wheel_without_menu — FAIL menu_wheel: call stream differs
RED menu_axes_when_closed — FAIL menu_wheel: call stream differs
RED menu_mouse_in_keys — FAIL menu_keys_mouse_axes_order: call stream differs
RED menu_lmb_action — FAIL menu_keys_mouse_axes_order: call stream differs
RED menu_escape_missing — FAIL escape_owned_by_menu: call stream differs
RED menu_close_mirror — FAIL escape_owned_by_menu: menu closed
RED startup_opens_book — FAIL menu_open_startup_no_openbook: call stream differs
RED chars_without_console — FAIL text_input: call stream differs
RED console_wait_frame — FAIL console_wait_warning: one warning on frame 600
RED region_formula — FAIL menu_region_width: integer division: 1081*4/3 = 1441
RED no_player_applies — FAIL game_no_player_keeps_queue: call stream differs
RED dt_zero_applies — FAIL game_lost_transition_level_pass_order: call stream differs
RED pause_not_skipped — FAIL game_pause_no_holdframe: call stream differs
RED esc_not_owned — FAIL menu_open_pending_page: after ClearActiveInput Escape belongs to the menu
RED esc_owned_ignored — FAIL escape_owned_by_menu: call stream differs
RED transitions_dropped — FAIL game_repeat_dedup: call stream differs
RED level_press_missing — FAIL game_lost_transition_level_pass_order: call stream differs
RED altenter_snapshot — FAIL game_alt_enter: call stream differs
RED absent_pass_missing — FAIL game_alt_enter: call stream differs
RED mouse_axis_kind — FAIL game_mouse_axes: call stream differs
RED holdframe_missing — FAIL game_press_hold_release: call stream differs
RED core_release_as_press — FAIL core_sink_ueinput: UEInput: Up released by ClearActiveInput
RED core_axes_not_reset — FAIL core_sink_ueinput: aBroomPitch reset
BASE GREEN — TOTAL: passed 22, failed 0
mutations: 35, defects: 0
```

The first `redtest.sh` run found two defects, both fixed: (1) the "no repeat dedup" mutation stayed green because the
level sweep masked it; the test now also releases after autorepeat, whose order distinguishes a transition from a
re-send; (2) restoring via `cp -p` kept the old mtime, the incremental build did not rebuild the file, and the base ran
on the mutated binary; now `cp` + `cmp`.

## Status

Checked by local synthetic tests (no game files, no gate): nothing above `ACTIVATED` can be claimed; no `CLOSED`.

| Native/system | Status | Owner | Activated | Oracle | Dependencies |
|---|---|---|---|---|---|
| `SdlKeyMap` (name table) | PORTED | frontend (platform) | tests `keymap_*` | none | package `EInputKey` (not checked) |
| `InputStage` game half | ACTIVATED (synthetic) | frontend -> Core | tests `game_*`, `escape_owned_by_menu` | no independent oracle | `UEInput` (Core) |
| `InputStage` menu | ACTIVATED (synthetic) | frontend -> Core | tests `menu_*`, `text_input` | none | HPConsole/FEBook/UWindow (bytecode) |
| `CoreInputSink` game path without console | ACTIVATED (real `UEInput`, pawn without class) | Core | `core_sink_ueinput` | none | DefUser.ini bindings not checked |
| `CoreInputSink` console/menu/pause | PORTED | Core | no (needs a world) | none | running world, packages |
| Wiring into `OpenHP1Program` | wired | — | — | — | — |
