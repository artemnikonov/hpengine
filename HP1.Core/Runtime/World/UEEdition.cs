// EDITION OF THE PLAYER'S INSTALL — which release of HP1 a root holds, from SHA-1 of its files (author 2026-11-08: run
// every English edition, not only US 1.53). The port never runs HP.exe; it identifies the release so that Core can
// select the native behaviour of that release's DLLs and the launcher can name (or refuse) what it found.
//   native set  System/Engine.dll + System/Render.dll (the bodies Core ports);
//   script set  System/HPBase.u (the .u build; Engine.u/HarryPotter.u differ together with it in every known release);
//   exe         System/HP.exe or one of the long names some releases use (Flipendo docs/engine-hooks.md, patch 0001):
//               only the protection (SafeDisc retail / community No-CD) — it carries no game code the port runs.
// Sources: the project's release inventory (hashes of R1..R5, 2026-11-03) and Flipendo's HP.exe table
// (src/surreal-patches/0001-game-detection.patch, 2026-11-08). The data set of the exe-only rows (EU, IT, disc #18) is
// not on hand: such a root identifies as Unknown (Tier Unverified: installed and run with a warning) unless its DLLs/.u
// match a known set.
// Native differences between the sets: audit/editions_uk_2026-11-08.md. A parallel RU branch may keep its own table;
// this file is the one owner for the editions it lists.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace HP1.UEScript
{
    public static class UEEdition
    {
        public enum Id { Unknown, US153, US153_NOCD, UK11, UK11_NOCD, RU_EA, RU_EA_NOCD, CITY_PL, MODDED_EN }

        // A native + script set (DLLs and .u of one build). UK11 = the "Fix" EN zip (R3): an earlier build than US 1.53
        // (DLL stamps 2001-10-19..22) with eng/0407/040c/0809 tables; that it is the UK 1.1 retail data is an assumption.
        public enum DataSet { Unknown, US153, UK11, RU_EA, CITY_PL, MODDED_EN }

        // Which DLL bodies Core ports for a root: the Render.dll build (the only DLL whose ported behaviour differs
        // between US153 and UK11, audit/editions_uk_2026-11-08.md). Unknown runs the US bodies.
        public enum RenderSet { Unknown, US153, UK11, RU_EA }

        public readonly record struct ExeInfo(bool NoCD, string Note);

        public sealed record Info(Id Edition, DataSet Data, string ExeName, string ExeSha1, ExeInfo? Exe,
                                  string EngineSha1, string RenderSha1, string HPBaseSha1)
        {
            // Which DLL bodies Core ports for this root: by Render.dll alone (a mixed root keeps its Render build).
            public RenderSet Render => RenderSha1 is not null && Renders.TryGetValue(RenderSha1, out RenderSet r) ? r : RenderSet.Unknown;

            // How far the port stands behind this root. An unlisted set whose Engine.dll + Render.dll are a listed
            // build is that build with a changed HPBase.u (a script mod such as the fan 1.55): Modified, as the
            // listed mods are. Any other unlisted set (another release, e.g. EU EN/FR/DE, ES/IT/PT) is Unverified.
            public Support Tier => Data != DataSet.Unknown ? TierOf(Data)
                : Array.Exists(Sets, s => s.Engine == EngineSha1 && s.Render == RenderSha1) ? Support.Modified : Support.Unverified;

            // For an Unverified or Modified root: the warning HP1Setup and OpenHP1 print (author decisions 2026-11-09 and
            // 2026-11-10: run it, loudly).
            public string Warning => Tier switch
            {
                Support.Unverified => "This release of the game is not recognised by OpenHP1 and has not been verified: the "
                    + "game may not work correctly. Please report it with these hashes: " + Hashes,
                Support.Modified => ModifiedWarning,
                _ => null,
            };

            string Hashes => $"System/{ExeName ?? "HP.exe"} {ExeSha1 ?? "(not found)"}, "
                + $"System/Engine.dll {EngineSha1 ?? "(not found)"}, System/Render.dll {RenderSha1 ?? "(not found)"}, "
                + $"System/HPBase.u {HPBaseSha1 ?? "(not found)"} — {Issues}";
        }

        // The game scripts or data differ from the release OpenHP1 is verified against (a patch, a mod such as the
        // unofficial 1.55, a modded re-release, or a damaged copy).
        public const string ModifiedWarning = "This copy of the game is modified (a patch, a mod such as the unofficial 1.55, "
            + "or a modded re-release): OpenHP1 is verified only against the unmodified game, so the game may not work "
            + "correctly. Before reporting a bug, please check it with the unmodified game.";

        // Exact: the reference the port is proven against. Supported: runs, not proven exact. Unverified: not
        // recognised, runs with a warning. Modified: game scripts changed by a re-release or a mod, runs with a warning.
        public enum Support { Exact, Supported, Unverified, Modified }

        public static Support TierOf(DataSet data) => data switch
        {
            DataSet.US153 => Support.Exact,
            DataSet.UK11 or DataSet.RU_EA => Support.Supported,
            DataSet.Unknown => Support.Unverified,
            _ => Support.Modified,
        };

        public const string Issues = "https://github.com/artemnikonov/openhp1/issues";

        // System/<name> of the game executable, first match wins (Flipendo: some releases rename HP.exe).
        public static readonly string[] ExeNames =
        {
            "HP.exe", "Harry Potter and the Philosophers Stone.exe", "Harry Potter and the Philosopher's Stone.exe",
            "Harry Potter and the Sorcerers Stone.exe", "Harry Potter and the Sorcerer's Stone.exe",
        };

        public static readonly IReadOnlyDictionary<string, ExeInfo> Exes = new Dictionary<string, ExeInfo>
        {
            ["f7abfd7d56a6792e119964aa40af2190b8fe4ec4"] = new(false, "US 1.53 retail, SafeDisc (R1; Flipendo: EN retail 1.0)"),
            ["9aad58812940d27c7cd96d2920c726cc2d2fdde9"] = new(true, "community No-CD, EN (early EN build archive, US no-CD, modded EN build v4)"),
            ["7d5664c62634596fecf09013aa1aaf902a50cb27"] = new(true, "No-CD of the RU EA Hit-Parade release and of an EN image (modded EN build v3)"),
            ["5d7ff458f6a9c3f6ff70725ecb3dada3b320998e"] = new(false, "RU EA Hit-Parade retail, SafeDisc (Flipendo)"),
            ["7e675aa863f55049f3cf530dfea0b241b112d03a"] = new(true, "UK, post-patched, archive.org cracked exe (SurrealEngine upstream table)"),
            ["aac3b600dfc3ee689a2323021d3cf9f5c862cce9"] = new(false, "EU EN/FR/DE retail (Flipendo issue #11; SafeDisc or No-CD not known)"),
            ["526232593aa0a5ebb57beed89ddecc9b992ab04f"] = new(false, "ES/IT/PT CD, Italian install (Flipendo issue #15)"),
            ["061afa5b6a71a717fe0b4d52e00bdf34adac4847"] = new(false, "ES/IT/PT CD, Italian exe with a community widescreen patch (Flipendo issue #15)"),
            ["597899fabeeb52e943356cf9134fa11ad657d29f"] = new(false, "original disc, language and release not known (Flipendo issue #18)"),
            ["97882c5533b8eae434b10fab743f812a2993cfe4"] = new(false, "City disc (2002 re-release of US 1.53, Language=pol slot holding English or Russian text), patched SafeDisc exe (R5)"),
        };

        // (Engine.dll, Render.dll, HPBase.u) -> data set. All three must agree: a mixed root is Unknown.
        static readonly (string Engine, string Render, string HPBase, DataSet Set)[] Sets =
        {
            ("318353ee6c160093a8c756d61c6742a2f16fe4df", "8364fe75196806385aba117ce5cca3a0af186be5", "88cb1a9544570d0df481f8ce395ae73027dc83b1", DataSet.US153),
            ("6e42bc9998d84e5eec1b13043c622809ef2f2ac9", "901ff016056447b1d646051eca542b3b2ecf77cc", "5095512b496ce0c0b508747ccf684d795c614658", DataSet.UK11),
            ("4f7c99a64b4e4a2ee62af841bb9ac1462944b3ca", "2fffbb05ff66d9a8fa1225894e0c03cbbf1f5267", "9fa9d84870bb0a545b8378cc92fe26c5a0015a42", DataSet.RU_EA),
            // US DLLs with a modified HPBase.u (and more .u): modded releases, VERSIONS.md R5 / R4 (v3 and v4 share it).
            ("318353ee6c160093a8c756d61c6742a2f16fe4df", "8364fe75196806385aba117ce5cca3a0af186be5", "ca1107bcbf313e722d277bbc92411aad6cc2cb61", DataSet.CITY_PL),
            ("318353ee6c160093a8c756d61c6742a2f16fe4df", "8364fe75196806385aba117ce5cca3a0af186be5", "377a9c32b1589bc321fe3cfaa1fb121c0f4d55d8", DataSet.MODDED_EN),
        };

        static readonly Dictionary<string, RenderSet> Renders = new()
        {
            ["8364fe75196806385aba117ce5cca3a0af186be5"] = RenderSet.US153,
            ["901ff016056447b1d646051eca542b3b2ecf77cc"] = RenderSet.UK11,
            ["2fffbb05ff66d9a8fa1225894e0c03cbbf1f5267"] = RenderSet.RU_EA,
        };

        public static DataSet DataOf(string engineSha1, string renderSha1, string hpbaseSha1)
        {
            foreach (var s in Sets)
                if (s.Engine == engineSha1 && s.Render == renderSha1 && s.HPBase == hpbaseSha1) return s.Set;
            return DataSet.Unknown;
        }

        // The edition is the data set; the _NOCD ids only where that release has a retail/No-CD pair and the exe is a
        // known No-CD. An exe outside the table (or missing) leaves the data set's plain id: the port does not run it.
        public static Id Classify(DataSet data, ExeInfo? exe)
        {
            bool nocd = exe is { NoCD: true };
            return data switch
            {
                DataSet.US153 => nocd ? Id.US153_NOCD : Id.US153,
                DataSet.UK11 => nocd ? Id.UK11_NOCD : Id.UK11,
                DataSet.RU_EA => nocd ? Id.RU_EA_NOCD : Id.RU_EA,
                DataSet.CITY_PL => Id.CITY_PL,
                DataSet.MODDED_EN => Id.MODDED_EN,
                _ => Id.Unknown,
            };
        }

        // THE ONE OWNER of "which release is this install": every Core reader (AppInit's NativeOrder, the natives'
        // DataSet, the producer's Render build) asks Of/OfSystem; the files are hashed once per root per process.
        static readonly Dictionary<string, Info> _installs = new(StringComparer.OrdinalIgnoreCase);
        // Identify runs per root (fixture: the root is hashed exactly once however many readers ask).
        internal static readonly Dictionary<string, int> IdentifyRuns = new(StringComparer.OrdinalIgnoreCase);
        public static Info Of(string installRoot)
        {
            string k = Path.GetFullPath(installRoot.TrimEnd('/', '\\'));
            lock (_installs)
            {
                if (!_installs.TryGetValue(k, out Info i)) _installs[k] = i = Identify(k);
                return i;
            }
        }
        public static Info OfSystem(string systemDir) => Of(Path.GetDirectoryName(Path.GetFullPath(systemDir.TrimEnd('/', '\\'))));

        public static Info Identify(string installRoot)
        {
            string key = Path.GetFullPath(installRoot.TrimEnd('/', '\\'));
            lock (IdentifyRuns) IdentifyRuns[key] = IdentifyRuns.GetValueOrDefault(key) + 1;
            string sys = UEInstallPath.Dir(Path.Combine(installRoot, "System"));
            string exeName = null, exeSha = null;
            if (sys is not null)
                foreach (string n in ExeNames)
                    if (UEInstallPath.File(Path.Combine(sys, n)) is { } p) { exeName = Path.GetFileName(p); exeSha = Sha1(p); break; }
            string engine = Sha1Of(sys, "Engine.dll"), render = Sha1Of(sys, "Render.dll"), hpbase = Sha1Of(sys, "HPBase.u");
            ExeInfo? exe = exeSha is not null && Exes.TryGetValue(exeSha, out ExeInfo e) ? e : null;
            DataSet data = DataOf(engine, render, hpbase);
            return new Info(Classify(data, exe), data, exeName, exeSha, exe, engine, render, hpbase);
        }

        // The x87 evaluation order Core follows where the builds' bodies differ only in it (audit/editions_ru_judge_2026-11-08.md,
        // "Float association"; author 2026-11-08: match RU bit for bit). RU_EA: its Engine/Core/Render reassociate the
        // three-term sums of TwoWallAdjust, FBox(FBox, FCoords), operator<<(FBox, FCoords), BoundVisible, DrawLodMesh and
        // execAttachDecal; every other set runs the US 1.53 order (UK11 = US in these bodies). GetFrame skinning is NOT
        // switched: the RU order from the disasm made the RU engine judge worse (STOP, report). One install
        // per process: set by UELevelRuntime.AppInit from the install's data set.
        public static DataSet NativeOrder { get; internal set; } = DataSet.US153;
        internal static bool RuOrder => NativeOrder == DataSet.RU_EA;

        static string Sha1Of(string dir, string name)
            => dir is not null && UEInstallPath.File(Path.Combine(dir, name)) is { } p ? Sha1(p) : null;

        static string Sha1(string path)
        {
            using var s = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA1.HashData(s));
        }
    }
}
