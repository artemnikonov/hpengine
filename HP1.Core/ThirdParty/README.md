# Eliot.UELib.dll — canonical patched build

The ONE canonical patched UELib binary. Every consumer references THIS copy:

- `HP1.Core` (HintPath `ThirdParty/Eliot.UELib.dll` — the Core folder is
  self-contained and builds in isolation);
- the HPEngine frontend (`hpengine/HPEngine.csproj`);
- the headless runner (`runtime_poc/HP1.Headless.Smoke.csproj`);
- `se_harness/bsp_diff/BspDiffRunner.csproj`.

Provenance: built from the UELib fork at `HP1-decompile/UELib-src` (upstream
Eliot.UELib with local HP1/KnowWonder patches: DefaultEngineBranch, UObject,
CastTokens, ContextTokens et al). Two UTexture.Deserialize patches matter for data:

- mip order for UE1 v<84: shipped UTexture::Serialize (Engine.dll.c:30276) writes Mips FIRST and CompMips after
  when bHasComp — the upstream order read 30 campaign textures as NO_MIPS;
- the stream position is restored after `Properties.Find("bHasComp").Value` (the lazy tag decode left the stream on
  the property bytes, so CompMips were read from offset 4 and threw EndOfStream on all 30 bHasComp textures — D-29).

HP2 build (2026-11-15, audit/hp2_plan_stage0_1_2026-11-15.md stage 1): fork branch `hp2`, commit 76ac149 on top of
15466dc, adds `BuildName.HP2` — the HP2 (79/000) token map from the HP2 Core.dll disassembly (0x38 DebugInfo, 0x39
GlobalFunction, casts 0x3A..0x46 one slot up, no DebugInfo peek after native 0x82 in Engine.Teleporter). It is never
auto-detected (no Build attribute; last enum member): only HPEngine's HP2 game profile selects it (UEPackageOpen).
HP1 reads are unchanged: the token dumps of US153, UK11, RU_EA and MODDED_EN (every struct of System/*.u and
Maps/*.unr) are byte-identical with the previous image (sha256 8b54fae8...); judge `audit/hp2_token_map_judge.sh`
pins the US dump.

HP2 USound (2026-11-15, stage 2): fork commit 850326e on 76ac149 reads the HP2 USound header (+0xb4, +0x68, the
v77..v79 ints) for BuildName.HP2 as the BuildGeneration.HP block does, and the second lazy array when flags & 0x80
(HP2 Engine.dll USound::Serialize body 0x103854e0); judged by tools/hp2/sound_serialize_judge.py (audit/hp2_asset_judge.sh).
HP1 reads unchanged (the US token dump of audit/hp2_token_map_judge.sh is identical; HP1 builds never take the block).

HP2 FPoly (2026-11-15, stage 2): fork commit 17ce36f keeps PanU/PanV for BuildName.HP2 (UELib drops them from v78; HP2's
operator<<(FArchive&, FPoly&) has no version gate and is HP1's body). Judged by the census's second reader: all 20616
HP2 EN UPolys equal the port's own FPoly reader (19314 differed before).

REPRODUCIBLE from fork commit 17ce36f (branch hp2; 850326e / 76ac149 / 15466dc for the previous images) with exactly

    dotnet build -c Release -f netstandard2.1 src/Eliot.UELib.csproj -p:SourceRevisionId=ab85a6ecff497abd98d28261ec656b985e463448

(the DLL embeds the revision id in its informational version; the canonical image was built when the fork HEAD was
ab85a6e with the later patches in the working tree, so the id is pinned, not taken from git)

which yields this byte image
(sha256 023cdcd5f90613c98f161f1e6b41fbe1cd82ffd40373535c1067037c2fa87ff2);
other TFMs (e.g. net8.0/net9.0) produce different bytes — always build and
compare THIS TFM, and re-run the full gate after any refresh.
