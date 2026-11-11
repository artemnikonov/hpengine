# Saving, loading and map changes

The original saves the whole live level as an Unreal package and loads it back through the same `UGameEngine::LoadMap` it
uses for every map change. Slot pages, autosaves and level travel are driven by UnrealScript (`HPConsole`, `FESlotPage`,
`LevelInfo.ServerTravel`) around a few engine natives. OpenHP1 runs those scripts unchanged and ports the engine half in
Core. The one deliberate difference is the file: the port writes its own save format and does not read the original's
`.usa` files.

## What the original does on save

- Console `SaveGame N` ends in `UGameEngine::SaveGame` (Engine.dll), which writes the entire level with `UObject::SavePackage`
  to `Save\SaveN.usa`. The order, from the disassembly (0x103a2379 onward): `LevelAction = LEVACT_Saving (2)`, then
  `PaintProgress()`, then the level's brush tracker is deleted, `ULevel::CleanupDestroyed(1)` runs, the package is written,
  every `Mover` gets `SavedPos = (-1,-1,-1)`, the brush tracker is rebuilt, `LevelAction = 0` and `GCache.Flush` empties the
  process-wide cache (skeleton headers, mesh vertex tween caches).
- `LevelAction = 2` never reaches the file: the property is `CPF_Transient`, and a persistent archive skips transient
  properties. Only the player sees it, for one frame: `PaintProgress` temporarily sets the player's `FlashFog` to
  (0, 0.1, 0.25, 0.2), draws once and restores it; `baseConsole.DrawLevelAction` prints the Localize key `nearly_nick_40`,
  which is **absent** from the shipped `.int`, so the original prints the Localize placeholder text.
- `PaintProgress` is skipped entirely when the save is made from inside a frame: the gate is the byte at `UGameEngine+0x108`,
  `Draw`'s own "drawing" flag. The level-start autosave is ticked by `UConsole::PreRender`, so it paints no progress frame
  and consumes no script or RNG step.
- `CleanupDestroyed(1)` zeroes every object-property reference to a pending-kill actor, walking each class's reference
  chain and recursing into structs, so destroyed actors do not reach the package. References held by non-actors or by
  latent-frame locals are left alone.
- Not saved by the original (they are not properties): particle buffers, the RNG state, animation caches, the viewport and
  console objects.
- Slots: the slot page lists a slot from its `GameSaveInfo<N>` file alone. Natives 325/326 (`execSaveGameSaveInfo` /
  `execLoadGameSaveInfo`) write and read `SavePath * Name` with no extension: four ints (`numBeans`, `numStars`,
  `numPoints`, `savePointID`) and a compact-index string (`currentLevelString`). A short file is not an error the script sees:
  the reader copies what is left, logs "ReadFile beyond EOF" to a null device and still returns "opened"; fields cut short keep
  their previous value. `SavePath` is `../Save` relative to `System`.
- Who saves: `HPConsole.Tick` writes slot 99 at every level start (`bLoadNewLevel`); checkpoint books call
  `SaveSelectedSlot`; the menu saves a chosen slot.

## What the original does on load

- Loading is `open saveN.usa`, i.e. `UGameEngine::Exec OPEN` then `SetClientTravel` (0x103a0480) then `Browse` then `LoadMap`
  with the save package as the map. The actors come from the package and `bBegunPlay` is already set, so LoadMap **skips**
  the init block (FixUpLevel, InitExecution, InitGame, BeginPlay/PostBeginPlay, Attach/Base). Every `PlayerPawn` gets
  `Player` zeroed, and `GameInfo.Login` finds the unclaimed player (Player None, has a PRI, `bIsPlayer`, name not the
  default) and gives it the viewport. `PostLogin` follows (music: see `music-and-galaxy.md`).
- The level clock is taken from the saved float: `TimeSeconds` becomes `ftol(saved * 2^32)` (0x1039e17b), so fractions below
  float precision are lost. The `Actors` array keeps slots 0 and 1; slot 1 (the editor brush) is empty in a saved level.
- Restart after death takes the same path: `baseHarry` state dead, `LoadSelectedSlot`, `FESlotPage.LoadSelectedSlot`, then
  `open save99.usa` (or the chosen slot). The slot-99 autosave is written at level start, so dying before the first
  checkpoint reloads a state whose saved `Song` is `None`.
- A missing or unreadable file fails inside LoadMap's catch **after** the fade but **before** the old level is torn down:
  `SetProgress("UrlFailed", message, 6.0)` shows "Failed launching URL" for 6 s, the player's `FlashFog.W` is set to 1 and the
  old level keeps playing. `UGameEngine::Tick` ignores the failed browse.

## The LoadMap transition

(Engine.dll `UGameEngine::LoadMap`, `FUN_1039c3d0`; port: `../HP1.Core/Runtime/World/UELevelRuntime.LoadMapScreens.cs`.)

1. With a client, a viewport and an old level: `Level.Pauser = ""`, the menu flag cleared, `LevelAction = 0`.
2. Fade out: while `Console.FadeoutTime > 0` and the console drew the world, `FlashFog.W` falls by real elapsed time over
   `FadeoutTime` and the engine draws a frame each iteration. The world does not tick; time is `appSeconds`, not level time.
3. Always, even without a fade: `W = 0` and one more draw, so the screen goes to the flash colour (usually black).
4. `Audio->SetViewport(same viewport)` (0x1039c8af): every channel except music stops, so old loops are silent over the load.
5. After the new map's `LevelInfo0` loads: if `LevelEnterText` is empty it becomes `URL.Map` (later read by
   `HPConsole.doLevelSave`), then `Console.DrawLevelInfo` draws the loading screen and presents it. It stays up until the new
   map's first frame. A second `SetViewport(same)` runs at the end of the client block.
6. `LoadMap` also writes `ComputerName`, builds `ULevel.ZoneDist[64][64]`, calls `ULevel::DetailChange` and rebuilds `Actors`.

Open: the call through the viewport's render device just before `DrawLevelInfo` (vtable +0xb0) is not ported; its purpose is
not established. The port also draws the loading parchment at the menu's full brightness (not original, author 2026-11-07).

## Travel and the resident Entry level

- The game keeps **two** live levels. `UGameEngine::Init` calls `LoadMap(Entry)` before any viewport exists (no player), then
  swaps `GLevel` and `GEntry`; `Tick` ticks the game level, then Entry. Observed on the running original: `GameInfo1`,
  `Mutator1` and `GameReplicationInfo1` exist on every map, and `GameInfo.Timer` and `GameReplicationInfo.Timer` fire twice
  as often as in a port without Entry (398 vs 200 on Lev_Tut1). The port keeps a second, resident runtime for Entry
  (`../HP1.Core/Runtime/World/UELevelRuntime.Entry.cs`).
- Level travel starts in script: `LevelInfo.ServerTravel(URL, bItems)` writes `NextURL` / `bNextItems` and
  `GameInfo.ProcessServerTravel` runs `NextSwitchCountdown` down. The engine half is `UGameEngine::Tick` (0x103a0c0d):
  while `NextURL` is non-empty, subtract the raw engine delta from the countdown and travel when it is <= 0 (NaN travels too).
  `?RESTART` takes the level's travel info as is; otherwise the URL is `FURL(LastURL, NextURL, TRAVEL_Relative)`, and the
  `restart` option replaces it with `LastURL`. `NextURL` is cleared only **after** `Browse`/`LoadMap`, which is why the
  slot-99 autosave is not written from the old world (`../HP1.Core/Runtime/World/UELevelRuntime.Bootstrap.cs`).
- The new player runs `TravelPreAccept`, `AcceptInventory`, `TravelPostAccept` and `PostLogin` in the shipped order; travel
  actors get `TravelPreAccept` / `TravelPostAccept` in reverse order.
- URLs: `FURL` parsing (`?` options, `#` portal, a first `/` in the map separating the portal, a space invalidating the URL)
  is ported for the local subset only; network forms are refused loudly (`../HP1.Core/Runtime/World/UEUrl.cs`).
  `GetLocalURL` returns `FURL::String` of the level's URL; `HPConsole.RenderUWindow` tests it for `STARTUP`.

## What the port does differently

- **Own save format (D-01, not original).** `SaveGame N` writes `save<N>.hp1sav` and then the original-named
  `GameSaveInfo<N>`; `open saveN.usa` reads the port's body. It keeps the semantics: all objects with properties, the state
  and its code position, latent action, timers, probe mask, `Touching` lists, the `Actors` order, placed-actor export
  indices and the level clock. An unknown property type is a loud stop, not a skip
  (`../HP1.Core/Runtime/World/UELevelRuntime.SaveGame.cs`).
- **Original saves are not shown (2026-11-01, not original).** A slot that has only the original's `GameSaveInfo` and
  `.usa` reads as empty, so the menu can never send `open save<N>.usa`. A port save to that slot overwrites the shared
  `GameSaveInfo` name.
- **Compatibility across port builds.** The body carries a version (4 now); every older version has a read branch. Version 3
  introduced state-frame positions as bytecode offsets and latent actions by name; version 4 writes class values with
  their package so a fresh process can load any slot.
- A damaged file, or one written by a newer build, is validated end to end **before** the old world is torn down and then
  takes the same "Failed launching URL" route as a missing file.
- `.usa` writing is not planned; read-only `.usa` support is the stated option if ever wanted (D-01).
- Load-time warm-up of lazily decoded caches (meshes, music index, locals layouts) runs behind the loading screen and
  touches no script, RNG or actor (`../HP1.Core/Runtime/World/UELevelRuntime.LoadWarm.cs`).
- Open: a save-to-load cycle with a fade in progress was not covered by a test (D-43 remainder).
