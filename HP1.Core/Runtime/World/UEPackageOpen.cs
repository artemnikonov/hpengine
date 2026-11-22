// ONE OPENER OF PACKAGE FILES (audit/hp2_plan_stage0_1_2026-11-15.md, stage 0.3). UELib's reader of a package is chosen
// by its build; HP1 and HP2 bytecode differ in the token map (stage 1), so every read of a package's bytes goes through
// here and the game profile decides the build. Locked by runtime_poc/verify_architecture.py ("package opener").
using System.IO;
using UELib;

namespace HP1.UEScript
{
    public static class UEPackageOpen
    {
        public static UnrealPackage Load(string path, FileAccess access = FileAccess.Read)
        {
            using (var f = File.OpenRead(path))
            {
                var b = new byte[6];
                if (f.Read(b, 0, 6) == 6 && System.BitConverter.ToUInt32(b, 0) == 0x9E2A83C1u) RefuseNewer(path, System.BitConverter.ToUInt16(b, 4));
            }
            return UnrealLoader.LoadPackage(path, UEGameProfile.Current.PackageBuild, access);
        }

        // A package newer than the game profile reads (UEGameProfile.MaxPackageVersion): a named refusal before any reader
        // parses it, never an InvalidDataException from the middle of a reader. Also UEPackageReader's constructor.
        public static void RefuseNewer(string path, int version)
        {
            if (UEGameProfile.Current.MaxPackageVersion is int max && version > max)
                throw new UEPackageTooNewException(path, version, max);
        }
    }

    // THE VERIFY PASS OF A MAP LOAD: which imported package file is missing. UGameEngine::LoadMap first loads "MyLevel"
    // with LOAD_Verify|LOAD_Throw|LOAD_NoWarn (0x1a; push 0x1a [HP2 0x103a5a12], [HP1 Engine.dll.c:246643]) before the old
    // level is torn down. The new ULinkerLoad's ValidateImports runs Verify (FUN_1014ab10 [HP2]): VerifyImport for every
    // import in table order; an import inside another import verifies that one first, so each import reaches its top-level
    // package, whose linker comes from GetPackageLinker(Pkg, NULL, LOAD_Throw) (push 8 [HP2 0x1014af05]). A linker already
    // made is reused; a new one verifies its own imports the same way. No file: appThrowf(LocalizeError("PackageNotFound"),
    // Pkg->GetName()) [HP2 0x10161954..0x1016197b]; every catch on the way rethrows that text unchanged (SafeLoadError with
    // LOAD_Throw: appThrowf("%s", Error) [HP2 0x1016177d..0x10161786]), except VerifyImport's for a package named UnrealI,
    // which retries the import as UnrealShare [HP2 0x1014af28..0x1014af52]. So the error is the first missing package of
    // this depth-first walk. Returns that text, or null when every imported package has its file.
    // Not ported: VerifyImport's "Can't find %s in file '%s'" (FailedImport) for an object missing from a present package.
    // Original: Core.dll not exported: FUN_10149590 [HP1 0x10149590] / FUN_1014ae20 [HP2 0x1014ae20] (ULinkerLoad::VerifyImport: its guard string L"ULinkerLoad::VerifyImport"; called in a loop by ULinkerLoad::Verify)
    public static class UEPackageVerify
    {
        public static string FirstMissingPackage(string root, string packagePath)
            => Walk(root, packagePath, new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
                { Path.GetFileNameWithoutExtension(packagePath) });

        static string Walk(string root, string path, System.Collections.Generic.HashSet<string> made)
        {
            var (names, imports) = ReadImports(path);
            for (int i = 0; i < imports.Length; i++)
            {
                int k = i;
                while (imports[k].Outer < 0) k = -imports[k].Outer - 1;
                // check(Import.PackageIndex<0) (UnLinker.cpp:0x18d): an import inside an export is a failed assertion.
                if (imports[k].Outer > 0) throw new InvalidDataException($"{Path.GetFileName(path)}: import {i} lies in an export (Import.PackageIndex<0 assertion)");
                string pkg = names[imports[k].Name];
                if (!made.Add(pkg)) continue;
                string file = UEInstallPath.PackageFile(root, pkg);
                if (file is null && string.Equals(pkg, "UnrealI", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (!made.Add(pkg = "UnrealShare")) continue;
                    file = UEInstallPath.PackageFile(root, pkg);
                }
                if (file is null) return UELocalization.Localize("Errors", "PackageNotFound", "Core").Replace("%s", pkg);
                if (Walk(root, file, made) is string missing) return missing;
            }
            return null;
        }

        // Header, name table and import table only (a texture package can be 150 MB).
        static (string[] Names, (int Outer, int Name)[] Imports) ReadImports(string path)
        {
            using var r = new BinaryReader(new BufferedStream(File.OpenRead(path)));
            if (r.ReadUInt32() != 0x9E2A83C1u) throw new InvalidDataException("not an Unreal package: " + path);
            int version = r.ReadUInt16();
            UEPackageOpen.RefuseNewer(path, version);
            r.ReadUInt16(); r.ReadUInt32();
            int nameCount = r.ReadInt32(), nameOffset = r.ReadInt32();
            r.ReadInt32(); r.ReadInt32();
            int importCount = r.ReadInt32(), importOffset = r.ReadInt32();
            if (nameCount < 0 || importCount < 0) throw new InvalidDataException("bad table counts: " + path);
            r.BaseStream.Position = nameOffset;
            var names = new string[nameCount];
            for (int i = 0; i < nameCount; i++)
            {
                if (version < 64) { var b = new System.Collections.Generic.List<byte>(); for (byte c; (c = r.ReadByte()) != 0;) b.Add(c); names[i] = System.Text.Encoding.GetEncoding(28591).GetString(b.ToArray()); }
                else
                {
                    int n = Compact(r);
                    string s = n < 0 ? System.Text.Encoding.Unicode.GetString(r.ReadBytes(-n * 2)) : System.Text.Encoding.GetEncoding(28591).GetString(r.ReadBytes(n));
                    names[i] = s.IndexOf('\0') is int z and >= 0 ? s.Substring(0, z) : s;
                }
                r.ReadUInt32();
            }
            r.BaseStream.Position = importOffset;
            var imports = new (int, int)[importCount];
            for (int i = 0; i < importCount; i++)
            {
                Compact(r); Compact(r);
                int outer = r.ReadInt32();
                imports[i] = (outer, Compact(r));
            }
            return (names, imports);
        }

        static int Compact(BinaryReader r)
        {
            int first = r.ReadByte(), value = first & 0x3f;
            if ((first & 0x40) != 0)
                for (int i = 0, shift = 6; i < 4; i++, shift += 7) { int next = r.ReadByte(); value |= (next & 0x7f) << shift; if ((next & 0x80) == 0) break; }
            return (first & 0x80) != 0 ? -value : value;
        }
    }

    // The start map's verify failed at boot. UGameEngine::Init: Browse(URL) fails with Error set (LoadMap's catch), so the
    // LocalMap retry (only for an empty Error) is skipped and GError->Logf(LocalizeError("FailedBrowse"), URL, Error) is
    // fatal [HP2 0x103a13b8..0x103a1474] [HP1 Engine.dll.c:38311-38334].
    public sealed class UEFailedBrowseException : System.Exception
    {
        public UEFailedBrowseException(string url, string error)
            : base(ReplaceTwo(UELocalization.Localize("Errors", "FailedBrowse", "Engine"), url, error)) { }
        static string ReplaceTwo(string fmt, string a, string b)
        {
            int i = fmt.IndexOf("%s", System.StringComparison.Ordinal);
            if (i >= 0) fmt = fmt.Substring(0, i) + a + fmt.Substring(i + 2);
            int j = fmt.IndexOf("%s", i < 0 ? 0 : i + a.Length, System.StringComparison.Ordinal);
            return j >= 0 ? fmt.Substring(0, j) + b + fmt.Substring(j + 2) : fmt;
        }
    }

    public sealed class UEPackageTooNewException : System.NotSupportedException
    {
        public UEPackageTooNewException(string path, int version, int max)
            : base($"{Path.GetFileName(path)} is package version {version}; this game's packages are read up to version {max}: "
                + "it was made for a newer engine (a community HP2 engine) and does not run in HPEngine")
        { }
    }
}
