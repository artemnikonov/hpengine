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
            => UnrealLoader.LoadPackage(path, UEGameProfile.Current.PackageBuild, access);
    }
}
