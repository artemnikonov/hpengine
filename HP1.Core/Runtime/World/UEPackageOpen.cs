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

    public sealed class UEPackageTooNewException : System.NotSupportedException
    {
        public UEPackageTooNewException(string path, int version, int max)
            : base($"{Path.GetFileName(path)} is package version {version}; this game's packages are read up to version {max}: "
                + "it was made for a newer engine (a community HP2 engine) and does not run in HPEngine")
        { }
    }
}
