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

REPRODUCIBLE from fork commit 15466dc (all four source patches are committed there) with exactly

    dotnet build -c Release -f netstandard2.1 src/Eliot.UELib.csproj -p:SourceRevisionId=ab85a6ecff497abd98d28261ec656b985e463448

(the DLL embeds the revision id in its informational version; the canonical image was built when the fork HEAD was
ab85a6e with the later patches in the working tree, so the id is pinned, not taken from git)

which yields this byte image
(sha256 8b54fae82d01d4a9b1a0a3ca883f2b7757895fcfdc686d8fec51913e680b35fb);
other TFMs (e.g. net8.0/net9.0) produce different bytes — always build and
compare THIS TFM, and re-run the full gate after any refresh.
