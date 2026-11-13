# Travel — holding the frame across a map change

Port of the state machine from the former Unity frontend `HP1TravelFrameHold.cs` (no longer in the repo; "reference
:line" below points into it) to plain C#. Each frame the module decides WHAT to show — the world, black instead of
the 3D world (the "gate"), the `Console.DrawLevelInfo` loading screen, or the held last frame — and emits the
loading-screen quads through `HudBatcher`. It makes no GPU calls and measures no time (the clock only feeds the log
line).

## What is ported

| reference | port | what |
|---|---|---|
| `HP1TravelFrameHold.cs:41` `Holding` | `Travel/TravelFrameHold.cs:70` | hold active |
| `:42`, `:66` `Armed/Released/GateReleased` | `:44` | counters |
| `:68-74` `InstallGate` | `:63-67` (constructor) | gate closed from install; marker frame = the presented one |
| `:73` `activeSceneChanged` → `:76-80` `CloseGate` | `:80-86` `SceneChanged` | gate closes on world change |
| `:86-97` `ArmScreen` | `:89-99` | loading screen from this frame on; `null` → `Arm` (`:88`) |
| `:107-121` `Arm` | `:102-110` | capture the last frame: `CaptureRequested` |
| `:123-152` `CaptureAtEndOfFrame` | `:113-120` `Captured` | backend reports the frame was copied |
| `:154-164` `LateUpdate`, gate | `:126-136` `Update` | release: frame ≠ null, ≠ gate-closing frame, `Camera.Valid \|\| !DrawWorld` |
| `:165` | `:137` | nothing held (including capture not yet arrived) — return |
| `:166-173` payload | `:139-145` | `payloadPending` — wait; else remember the frame and return |
| `:174-177` release | `:146-149` | frame ≠ null, ≠ payload frame, `Camera.Valid` |
| `:180-188` `Release` | `:153-160` | drop both holds, counter, log |
| `:190-208` `OnGUI` | `:163-172` `View` | order: gate (only without holds) → screen → frame → world |
| `:200-202` `FillBlack` + `Rasterize(_screen)` | `:177-181` `ScreenQuads` → `HudBatcher.BuildLevelInfoScreen` | full-screen black plus screen commands |

The state machine has NO durations, by design of the reference: no splash, no delay; the release condition is
OBSERVABLE, not "after N frames" (`:29-31`). The only time is the log-line timestamp (`:93`, `:118`, `:177`); here an
injected clock (`Func<double>`) that never affects decisions.

Layers (reference `GUI.depth`): the gate is `int.MaxValue` (`:194`, below any IMGUI: it covers only 3D; HUD,
coronas, flash draw over it); the loading screen and the held frame are `int.MinValue` (`:199`, `:206`, above
everything, including `HP1ScreenFade` at `-1000`).

## What is NOT ported, and why

- **MonoBehaviour singleton, `DontDestroyOnLoad`, `RuntimeInitializeOnLoadMethod`, `ResetStatics`**
  (`:39`, `:50-51`, `:99-105`) — Unity lifecycle. Here a plain instance, one per window; static counters became
  instance fields.
- **Frame capture `ScreenCapture.CaptureScreenshotAsTexture` + gamma table** (`:123-149`). The table compensated for
  the extra sRGB encode of `GUI.DrawTexture` in Unity IMGUI (`:126-134`); SDL_GPU has no such layer, frame bytes are
  copied as is. The capture itself is backend work; the state machine emits `CaptureRequested` and waits for
  `Captured(w,h)`.
- **`HP1LevelPresentation.Pending`** (`:169`) — taken as the `payloadPending` value ("the new world has not yet
  taken the transition payload").
- **`UEScriptRuntime.Instance.Runtime.PresentedFrame`** — passed as the `presented` argument.

## Divergences and findings

1. **In HPEngine the loading screen cannot currently be shown on time — loud, not solved here.** The transition runs
   synchronously inside `host.Tick` (`runtime_poc/HeadlessSmoke.cs:7356-7396`): fade (`RunEngineFrame "fade"`),
   `Runtime.Travel` (builds `LastLoadMapScreen` in `UELevelRuntime.Bootstrap.cs:1315`); that tick's frame is then the
   held loading screen (`UELevelRuntime.HoldLoadMapScreen`: the browsing engine frame draws nothing, as in shipped) and
   the new world's first frame comes after its first tick, in the next one. The window presents nothing during the
   synchronous fade, so the player does not see the fade frames. Arming after `host.Tick` would show the screen AFTER loading — an invented
   delay; not allowed. A synchronous carrier is needed at the point where the screen is built (like
   `PresentProgressNow` for the save frame, `UELevelRuntime.SaveGame.cs:207-213`) — a Core/`runtime_poc` change,
   outside this module.
2. **The `return` after taking the payload (reference `:172`) is unobservable.** In the same call
   `now == presented == _frameAtPayload` (including `null`), and the check at `:175`
   `ReferenceEquals(now, _frameAtPayload)` rejects the frame even without `return`. The "remove return" mutation is
   equivalent, so `redtest.sh` omits it (the form follows the source; there is no distinguishability proof because
   there is no difference).
3. **No hold before the capture arrives.** In the reference `_held == null` until the end of the arming frame
   (`CaptureAtEndOfFrame`), so `Holding` is false, `OnGUI` draws the world, and `LateUpdate` returns at `:165` — the
   payload is not taken in this window. Ported the same way and pinned by the test `capture-no-payload-before`.
4. **Re-arming does not cancel a capture** (the reference `Arm` coroutine survives to the end of the frame even after
   `ArmScreen`): screen and frame may coexist; `OnGUI` shows the screen (`:197` before `:205`), `Release` drops both.
   Ported; test `prio-screen+held`.
5. **`--video`** (`HPEngineProgram.cs`, `Video`) skips frames without a camera (`continue`), while the gate draws
   black with the HUD at that time. This is recording behaviour, not the state machine's.

## Tests

    DOTNET_ROLL_FORWARD=Major dotnet run --project hpengine/tests/TravelTests -c Release

Expectations are written by hand from the reference lines (in test comments), not from port output. Covered: gate
(install, same frame, `null`, no camera, menu `!DrawWorld`, re-close on world change); world change with a loading
screen — frame tape `LLLLLLW` (payload waits ×3 → taken with `return` → same frame → no camera → release); world
change with capture — `HHHW` and early return before the capture arrives; re-arm (drop the previous one, reset
payload, screen → capture); decision priority; screen quads (black 1920×1080 + tile at scale 2); loud refusals
(`ScreenQuads` without a screen, `Captured` without `Arm`), a `null` frame during a hold.

Output:

    TravelTests: 65 passed, 0 failed

## Red mutations

    sh hpengine/tests/TravelTests/redtest.sh

Each mutation is a `sed` of exactly one line of `Travel/TravelFrameHold.cs` (checked with `diff`),
then a test run, restore with `cp` (no `-p`) + `cmp`; the base is green before and after. Output (verbatim):

    == base
    TravelTests: 65 passed, 0 failed
    == mutations
    RED    gate-install-open: FAIL gate-install-closed: expected GateBlack, got World (+8)
    RED    scene-change-noclose: FAIL scene-closed: expected GateBlack, got World (+2)
    RED    gate-flag-false: FAIL gate-install-closed: expected GateBlack, got World (+10)
    RED    gate-frame-forgotten: FAIL gate-same-frame: expected G, got W (+4)
    RED    gate-same-frame: FAIL gate-same-frame: expected G, got W (+4)
    RED    gate-no-drawworld: FAIL gate-menu-frame: expected W, got G (+1)
    RED    gate-ignore-camera: FAIL gate-new-camera: expected W, got G (+12)
    RED    gate-uncounted: FAIL gate-released-count-1: expected 1, got 0 (+3)
    RED    armscreen-null-noarm: FAIL capture-armed: expected 1, got 0 (+1)
    RED    armscreen-norelease: FAIL rearm-released: expected 1, got 0 (+1)
    RED    armscreen-keep-payload: FAIL rearm-payload-reset (+1)
    RED    armscreen-noscreen: FAIL screen-view-armed: expected LoadingScreen, got World (+14)
    RED    armscreen-uncounted: FAIL screen-armed: expected 1, got 0 (+2)
    RED    arm-norelease: FAIL rearm-arm-released: expected 2, got 1 (+1)
    RED    arm-keep-payload: FAIL rearm-arm-payload-reset (+0)
    RED    arm-nocapture: FAIL capture-requested (+0)
    RED    arm-uncounted: FAIL capture-armed: expected 1, got 0 (+1)
    RED    captured-noheld: FAIL capture-holding (+3)
    RED    captured-noguard: FAIL refuse-captured-without-arm (+0)
    RED    update-no-early-return: FAIL capture-no-payload-before (+1)
    RED    payload-pending-ignored: FAIL screen-payload-pending (+3)
    RED    release-null-frame: FAIL hold-null-frame: expected L, got exception NullReferenceException (+0)
    RED    release-same-frame: FAIL screen-released-before: expected 0, got 1 (+3)
    RED    release-no-camera: FAIL screen-released-before: expected 0, got 1 (+2)
    RED    release-noguard: FAIL screen-released-0: expected 0, got 1 (+9)
    RED    release-keep-screen: FAIL screen-tape: expected LLLLLLW, got LLLLLLL (+5)
    RED    release-keep-held: FAIL capture-tape: expected HHHW, got HHHH (+1)
    RED    release-uncounted: FAIL screen-released-1: expected 1, got 0 (+5)
    RED    view-gate-over-hold: FAIL prio-gate+screen: expected LoadingScreen, got GateBlack (+1)
    RED    view-held-over-screen: FAIL prio-screen+held: expected LoadingScreen, got HeldFrame (+0)
    RED    view-no-held: FAIL capture-tape: expected HHHW, got WWWW (+1)
    RED    quads-noguard: FAIL refuse-quads-without-screen (+0)
    == total: mutations 32
    base after: TravelTests: 65 passed, 0 failed
    REDTEST PASS — all 32 mutations red

To see one transition fail by hand: in `TravelFrameHold.cs:147` remove `|| !now.Camera.Valid` — the
`screen-released-before` test shows a release on the new world's camera-less frame (expected 0, got 1).

## Status (C1)

Verified at the "synthetic module test" level; no project gate (`targeted`…`full`) was run, so the `ACTIVATED`
ceiling is not reached.

| Native/system | State (C1) | Owner | Headless | Unity | Activated | Oracle | Dependencies |
|---|---|---|---|---|---|---|---|
| `TravelFrameHold` (gate, screen, hold) | SOURCE_PORTED | HPEngine frontend (presentation) | — (headless shows no frames) | reference `HP1TravelFrameHold.cs` | yes: `HPEngineProgram` (level transition, `new TravelFrameHold`) | no; synthetic tests from the reference + 32 red mutations | loading-screen carrier inside the synchronous transition (divergence 1); frame capture in the SDL_GPU backend |
| `ScreenQuads` → `HudBatcher.BuildLevelInfoScreen` | SOURCE_PORTED | HPEngine frontend | — | reference `:200-202` | no | `quads-*` tests | `UELevelRuntime.LastLoadMapScreen` (Core, done) |
