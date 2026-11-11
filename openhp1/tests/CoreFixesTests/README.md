# CoreFixesTests — Core fixes against shipped HP1 1.53

Each shipped body was re-read from the decompile and checked against the disasm (`disasm/Engine.asm`, objdump 1.53)
wherever a fix rests on a negative conclusion.

## Commands

    cd openhp1/tests/CoreFixesTests && DOTNET_ROLL_FORWARD=Major dotnet run -c Release
    sh openhp1/tests/CoreFixesTests/redtest.sh

Code 0 — success, 1 — failure with a named check. No game files needed: the UPolys package and the `.int` are built
synthetically in a temp directory.

## What was fixed and how it is proven

| # | fix (commit) | shipped | test input | what is proven |
|---|---|---|---|---|
| 1 | `UEBspCollision.cs` ReadPolys: `PolyFlags & ~0x10000000` (ca8ceca) | `operator<<(FArchive&,FPoly&)` Engine.dll.c:115984-115986, disasm 0x103bb142 `and [eax],0xefffffff` under ArIsLoading | `HeadlessBspModel.ReadPolys` on a synthetic v76 package (UELib, export of class Engine.Polys) — the same reader as MoverGeometry/MoverDecals/`--probe-moverflags` | 0x10000001→0x1, 0x40→0x40, 0xFFFFFFFF→0xEFFFFFFF, 0x10000000→0; the other record fields parse without shift |
| 2 | `UELocalization.Localize`: a hit is truncated to 1023 characters (ecd5bce) | GetString FUN_103d0220 (`*Value=0` :277417, `appStrncpy(Value,found,Size)` :277541), Size 0x400 from Localize (Core.dll.c:11186), appStrncpy = wcsncpy + `Dest[Max-1]=0` (Core.dll.c:42661) | the `Localize` native from `RegisterLocalizationOps` -> `GetByName` (the registration the VM calls) | 1500→1023, 1023→1023, 1024→1023, quoted value 1100→1023 (quotes removed before truncation), short — whole, miss — marker |
| 3 | `MoverMode3.MakePose/WorldPoint/WorldVector` via `BrushCoords.ToWorld` (41a5b49) | `ABrush::ToWorld` Engine.dll.c:60413: −Location, *=PostScale, *=Rotation, *=MainScale (shear), +PrePivot | `MoverMode3.*` (pure functions) | point = PostScale·(R·(MainScale·(p−PrePivot)))+Location: no rotation — exact (powers of two), yaw 90° — PostScale after rotation (tolerance 1e-3, sin/cos from the FGlobalMath table); vector — without translation; shear ≤0.05 quantises to 0; non-zero shear — `NotSupportedException` |
| 4 | `FinishAnimNative` with a bone: silent returns (19a11cf) | execFinishAnim Engine.dll.c:110531-110561, disasm 0x10408290..0x1040834d: no mesh / not IsA(USkeletalMesh) / no channel / channel == this — jump to the epilogue without calls or writes | `UELevelRuntime.FinishAnimNative` (registered by the frontend: `Pair(261, …)`) | three silent branches: no wait, bAnimLoop/bAnimFinished untouched, BoneIndex asked only of a skeletal mesh; control — the same actor without a bone sets the wait |
| 5 | `HasAnimNative`: without a mesh `HasAnim: No mesh` (5823046) | execHasAnim Engine.dll.c:114338-114358, disasm 0x1040a42f..0x1040a441: `Logf(Stack, "HasAnim: No mesh")`, ret without writing Result | `UELevelRuntime.HasAnimNative` (`Pair(263, …)`) | false and a line on EVERY call (2 calls → 2 lines); with a mesh there is no line |
| 6 | `DestroyActor`: refusal by role (a58e3f8) | FUN_103a72c0 Engine.dll.c:252973-252976, disasm 0x103a7353..0x103a7377: `Role(+0x31) != 4 && !bNetForce && !(+0x28 & 0x4000000)` → 0, AFTER the `&5` and `&0x200` checks; execDestroy passes bNetForce = 0 (`push 0`, 0x1040cc32); bit 0x4000000 = Actor.bNetTemporary (`audit/coverage_2026-10-02/field_coverage.tsv`) | `UELevelRuntime.DestroyActor` (behind native 279) | Role 2/3/0 → false, actor alive, no events; bNetTemporary lifts the refusal; Role 4 — the normal path; bStatic → false and bDeleteMe → true before the role |
| 7 | `UEInstallPath.File`: install files found like Windows, case-insensitively (D-22, Steam Deck 2026-10-30) | shipped runs on a case-insensitive file system (Win32 file API); references name `SkyBox`, `hp_fx`, `SGS Lev_Tut1b.bmp` for `Skybox.utx`, `HP_FX.utx`, `SGS lev_tut1b.bmp` | `UEInstallPath.File` on a synthetic `Textures/`/`Save/` tree | exact name found; on a case-sensitive file system (Linux) the on-disk spelling is returned (also through a differently cased directory); a missing file is null. On macOS/Windows the fallback does not execute and the test says so; mutation `installpath-no-case-fold` is red only on Linux |

Row 5 (kept verbatim above): `HasAnimNative` without a mesh logs `HasAnim: No mesh`; disasm 0x1040a42f..0x1040a441:
`Logf(Stack, "HasAnim: No mesh")`, ret without writing Result; proven: false and one line on EVERY call (2 calls → 2
lines); no line with a mesh.

## Test output

```
ok   1 FPoly.PolyFlags
ok   2 Localize
ok   3 MoverMode3
ok   4 FinishAnim(bone)
ok   5 HasAnim
ok   6 Destroy(role)
CoreFixesTests: passed 58, failed 0
```

(The `Package Version…` lines are printed by UELib itself when loading the synthetic package; the line
`Localization: no entry T.S.Missing (int)` is the miss log, the case under test.)

## Red mutations (`redtest.sh`)

Each mutation is a `sed -i.bak` of exactly one line (checked: +1/−1 line); the mutant must
build (otherwise BROKEN, not RED), and the test must fail; the file is restored with `cp` (no `-p`) and checked with
`cmp`. Run output (verbatim):

```
RED     polys-no-mask (rc=1): FAIL polys[0].PolyFlags 0x10000001: expected 0x00000001, got 0x10000001
RED     polys-wrong-bit (rc=1): FAIL polys[0].PolyFlags 0x10000001: expected 0x00000001, got 0x10000001
RED     localize-no-trunc (rc=1): FAIL Localize S.Long (length 1500): expected length 1023, got 1500
RED     localize-1024 (rc=1): FAIL Localize S.Long (length 1500): expected length 1023, got 1024
RED     mover-no-postscale (rc=1): FAIL WorldPoint (PostScale, MainScale, PrePivot): expected (108.00, -42.00, 89.00), got (104.00, -42.00, 153.00)
RED     mover-sheer-silent (rc=1): FAIL shift Main=0.3 Post=0 — loud refusal: no refusal — shift silently ignored
RED     mover-vector-as-point (rc=1): FAIL WorldVector (PostScale, MainScale): expected (4.00, 1.00, 6.00), got (72.00, -51.00, 35.00)
RED     finishanim-throw (rc=1): FAIL 4 FinishAnim(bone): exception NotSupportedException: FinishAnim on channel bone=Bip01 Head
RED     finishanim-vertex-mesh (rc=1): FAIL FinishAnim(bone) skeletal: BoneIndex asked once: 2
RED     finishanim-no-channel-falls (rc=1): FAIL FinishAnim(bone) skeletal, no channel: no wait: FinishAnim
RED     hasanim-silent (rc=1): FAIL HasAnim without a mesh: line "HasAnim: No mesh" on every call: lines 0, total 0: 
RED     destroy-no-role (rc=1): FAIL Destroy SimProxy: result False: got True
RED     destroy-no-nettemporary (rc=1): FAIL Destroy NetTemporary: result True: got False
RED     destroy-role-before-deleteme (rc=1): FAIL Destroy DeadSim (bDeleteMe before role): result true: got false
BASE    PASS (CoreFixesTests: passed 58, failed 0)
redtest: all mutations red, base PASS
```

| mutation | what it catches |
|---|---|
| polys-no-mask | mask removed |
| polys-wrong-bit | a mask, but on the wrong bit |
| localize-no-trunc | no truncation |
| localize-1024 | limit 1024 instead of 1023 (`Dest[Max-1] = 0`) |
| mover-no-postscale | PostScale lost |
| mover-sheer-silent | shear silently zeroed instead of a loud refusal |
| mover-vector-as-point | vector transformed as a point (with translation) |
| finishanim-throw | exception on any bone |
| finishanim-vertex-mesh | vertex mesh not filtered before BoneIndex |
| finishanim-no-channel-falls | "no channel" falls through to the boneless tail (wait on the actor itself) |
| hasanim-silent | no log line |
| destroy-no-role | no role check |
| destroy-no-nettemporary | bNetTemporary does not lift the refusal |
| destroy-role-before-deleteme | role checked before bDeleteMe (a destroyed actor with Role≠4 would give false) |

## What is NOT done, and why

- **FinishAnim, "channel found" branch** — a loud `NotSupportedException`. Shipped runs the tail on the CHANNEL:
  clears its bAnimLoop/bAnimFinished and sets `LatentAction = 0x181` in the CHANNEL's StateFrame (0x10408343), not
  the caller's. Channels are created only by `CreateAnimChannel` in a live world — the branch cannot be checked
  without it; in the campaign all 162 calls have no argument.
- **MoverMode3, non-zero shear** — `BrushCoords.MulScale` rejects it loudly: BrushCoords has no shear matrix
  (`SheerAxis`, switch at Engine.dll.c:60519). None of the 58 mode-3 movers has a shear.
- **MoverMode3 signature.** `MakePose` takes three parameters (mainSheer, postScale, postSheer); callers must pass
  the brush's PostScale/SheerRate (like `UELevelRuntime.BrushToWorldCoords`). Deliberately no overload with the old
  signature: it would silently substitute PostScale = 1.
- **FPoly mask, carriers** — bit 0x10000000 in campaign Polys is not measured (needs the maps): if it is absent, the
  fix is "form from the source, without proof on data".
- **Destroy (2): EndState without StateFrame** — does not reach the game; untouched.
- **Not run** (no game files): headless SelfTest, gate, campaign fixtures. Builds of `HP1.Core`,
  `runtime_poc/HP1.Headless.Smoke.csproj`, `openhp1/OpenHP1.csproj` were checked — no errors. Risk of fix 6: an
  actor without a `Role` property (null → 0) is now not destroyed. Port actors get Role from class defaults
  (`LoadDefaults`; Actor = 4), but this is not re-checked over the campaign.
