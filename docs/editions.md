# Game editions and how they differ

Harry Potter and the Philosopher's Stone (PC, 2001) shipped in several releases whose DLLs and scripts are not identical.
OpenHP1 treats **US 1.53** as the reference and identifies the install it is given from SHA-1 hashes, so that Core can select
the matching native behaviour and the launcher can name what it found and warn when it is not verified. UK 1.1 and the Russian EA release
are supported but not yet proven exact; the European English release is unknown; the fan "1.55" patch and other modified copies run with a warning.

## Support tiers

| edition | tier | note |
|---|---|---|
| US English 1.53 | exact | the reference every port body is checked against |
| UK English 1.1 | supported | plays through; not proven exact (no journal of the running original for this build) |
| Russian EA release | supported | plays through; menu text in a substitute font (not original); several native bodies not yet judged |
| European English | unknown | no reference copy; a player is asked to send hashes (below) |
| fan "1.55" patch, modded re-releases | modified | installed and run with a warning (author decision 2026-11-10) |

(`../README.md`, `../launcher/HP1Setup/Program.cs`.)

## How an install is identified

- `UEEdition.Identify(root)` (`../HP1.Core/Runtime/World/UEEdition.cs`) hashes `System/Engine.dll`, `System/Render.dll` and
  `System/HPBase.u`. All three must match one row of the table, otherwise the data set is `Unknown`: a mixed root is never
  guessed. HPBase.u stands for the whole script build, since `Engine.u` and `HarryPotter.u` differ together with it in every
  known release.
- The game executable (`System/HP.exe`, or one of the long names some releases use) only decides the `_NOCD` variant: it
  separates the SafeDisc retail executable from a community No-CD one. The port never runs it and it carries no game code
  the port uses. An executable outside the table leaves the data set's plain id.
- The launcher (`HP1Setup`) links this same class, so setup and Core cannot disagree. A recognised supported edition must
  then match its full manifest (`us153.sha1`, `uk11.sha1`, `ru_ea.sha1`: size and SHA-1 of every file the port loads by
  package name); any difference is reported as "modified". Hashes only: no game bytes are in the repository.
- Core asks the one owner for the Render.dll build (US, UK or Russian) and for the float-evaluation order of a few bodies;
  an unknown render build runs the US bodies.

| data set | Engine.dll | Render.dll | HPBase.u |
|---|---|---|---|
| US 1.53 | 318353ee6c160093a8c756d61c6742a2f16fe4df | 8364fe75196806385aba117ce5cca3a0af186be5 | 88cb1a9544570d0df481f8ce395ae73027dc83b1 |
| UK 1.1 | 6e42bc9998d84e5eec1b13043c622809ef2f2ac9 | 901ff016056447b1d646051eca542b3b2ecf77cc | 5095512b496ce0c0b508747ccf684d795c614658 |
| Russian EA | 4f7c99a64b4e4a2ee62af841bb9ac1462944b3ca | 2fffbb05ff66d9a8fa1225894e0c03cbbf1f5267 | 9fa9d84870bb0a545b8378cc92fe26c5a0015a42 |

Core.dll differs too: US 8a68e024344061ace21b1f0ae1c664a3a0572639, UK e7d81291b493bd15efd81d690ae985fa2ad707e9, Russian
d625f98f9af8b81d287248bec4db4b29bbf213d9. The DLL hashes alone separate three native sets (US, UK, Russian).

Retail executables the table knows: US 1.53 SafeDisc `f7abfd7d56a6792e119964aa40af2190b8fe4ec4`, Russian EA SafeDisc
`5d7ff458f6a9c3f6ff70725ecb3dada3b320998e`; several No-CD executables; and executables of European, Southern-European and
other discs whose data sets are not on hand, so an install with only those executables identifies as `Unknown`.

- **UK 1.1 is a label, not a proven fact.** This build has earlier DLL stamps (2001-10-19..22 against 2001-10-31 for the
  Russian one) and language tables for English, German and French; that it is the UK 1.1 retail data is an assumption.
- **European English** (author 2026-11-08): no copy is available. Such an install stays `Unknown` and HP1Setup prints
  the SHA-1 of `System/HP.exe` and `System/Engine.dll` for a bug report. With a hash, one table row and one DLL comparison
  (as below) would add it.
- **Fan "1.55"**: README and the launcher treat it as a modification (not an official release). Two re-releases with a
  patched HPBase.u (one Polish, one a modded English build) are recognised by name and run with the "modified" warning.

## UK 1.1 against US 1.53

Every function of the UK DLLs was paired with the US one (export name, normalised hash, call graph) and the differing pairs
were read in the disassembly (`../HP1.Core/Runtime/World/UEEdition.cs` header lists the sources).

- Core: 3023 functions, 2980 equal; the differences are net-code thunks and build-time strings, none that the port cites. The
  VM, `FCoords` and math are equal. Engine: 6035 functions, 6005 equal; differences are register allocation and absent
  Windows-version helpers. The mp2 decoder, physics, collision, animation and Engine-side particles are equal. Galaxy:
  one swapped independent load in `Update`. Fire: identical.
- **Render.dll is the one DLL whose ported behaviour differs.** The UK particle billboard drawer (`FUN_10b18390`, US
  `FUN_10b184d0`) only culls: US adds a density fade that scales Alpha down when `(Priority+1) * area * 0.25` exceeds a
  budget, so big close particles are not faded on UK. The UK particle walk (`FUN_10b15000`) skips a particle before drawing when
  `Distribution != 1` and `Priority > LOD * 9` (0x10b1531c-0x10b15341); on shipped data `LOD` stays 1.0 and `Priority` is
  at most 9, so nothing is cut in practice. Both are ported and selected by the Render.dll build. Checked against the UK
  DLL's machine code on several maps: 647 drawer calls and 990 LOD-cut cases, no divergence.
- UK `URender::DrawWorld` adapts `GlobalShapeLODAdjust` where US does it in `PostRender`; the port keeps 1.0 in both (an
  improvement, see `original-bugs.md`).
- Scripts: the decompiled UK bytecode equals US except that `FEOptionsPage.IsSupportedResolution` does not exist; UK inlines a
  filter (512x384, 640x480, 800x600, 1024x768) into `LoadAvailableSettings`. On UK the port rebuilds the resolution list with
  every device mode (D-70, not original).
- Other script differences come from the different `.u` build: fewer `Accessed None` errors (`HPConsole.Tick` and
  `PotCam.CheckPosition` are absent), one actor per tick uses `SetLocation` where 1.53 uses `MoveSmooth`, Dungeon has 1113
  actors against 1115. The campaign therefore needs its own reference journal and playthrough.

## Russian EA against US 1.53

- **Font.** The Russian `HPConsole.SetupLanguage` asks for native fonts (`CreateNativeFont('MS Sans Serif', 24/16/10/12)`),
  which the original draws with GDI through `UWindowsViewport::DrawString` (box-measuring `DrawText`, word-break, optional
  centre) for `UCanvas::WrappedPrint` (native-font branch 0x10387d80..0x10387e08) and `FUN_10388530`. The port substitutes Liberation Sans only for that face
  (D-39, not original); wrap points can differ, and the underline of an `&` mnemonic is not drawn. WinDrv selects the GDI
  charset by language and converts text with the OS code page on a non-Unicode OS; the substitute makes this moot.
- **Sounds.** 1368 sounds have an `iEndPosn` that is odd or beyond the decoder's output. `MP2SoundStream::FillBuffer`
  (0x1037e550) then treats the short decoder read (0x1037e65d) as the end of the stream after exactly the decoded bytes,
  looping or not, because the decoder fails deterministically at the data end (`FUN_104529c0` clamps through
  `FSoundData::Buffer`). The Russian `HPSounds.u` also has script exports with overlapping offsets (16617..16620, one byte
  apart) that the game never loads; the port reads sound exports on demand for this reason.
- **Text.** `Language=rus`; `.rus` files are UTF-16LE with a BOM.
- **DLLs.** Of the Core, Engine, Render, Galaxy, Fire and WinDrv functions compared one by one, most are identical; 135 player-relevant
  ones differ. A static read classified 125 as inlining or scheduling, 2 as logic and 8 as another association of a three-term
  x87 sum. The two logic ones: `URender::PostRender` computes a particle budget `MaxParticle` from the frame time (US keeps
  it at 1.0; the port keeps 1.0 on Russian too, decided improvement), and `URender::DrawFrame` adds a span-visibility test for
  span-based render devices (unreachable on D3D). Core selects the Russian summation order for `TwoWallAdjust`,
  `FBox(FBox, FCoords)`, `BoundVisible`, `DrawLodMesh` and `execAttachDecal`.
- **Checked.** Against the Russian Engine, Core and Render code run on the port's campaign data (40 maps, 300 ticks): physics
  2943 samples and traces 2122 samples, no divergence.
- **Open.** `USkeletalMesh::GetFrame` skinning (Russian `(P.y*Y + P.x*X) + P.z*Z`, US `(P.z*Z + P.y*Y) + P.x*X`) makes 4
  owner-mesh particle samples differ by 1-27 ulp; the Russian order is not applied (it made the judge worse), so this is open.
  Render, `LoadMap`, `TraceTexture`, `GetWind` and `FindSpot` are classified statically only. The Russian engine has not been
  compared as a whole running game.
