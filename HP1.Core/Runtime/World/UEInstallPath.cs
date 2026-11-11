// INSTALL FILE LOOKUP LIKE WINDOWS. Shipped HP1 ran on a case-insensitive file system (appFindPackageFile,
// appLoadFileToArray, appFileSize go to the Win32 API), so references in scripts and packages name files whose case on
// disk differs: `SkyBox` -> Textures/Skybox.utx, `hp_fx` -> Textures/HP_FX.utx, `..\Save\SGS Lev_Tut1b.bmp` ->
// Save/SGS lev_tut1b.bmp. macOS and Windows resolve them natively; Linux (Steam Deck, ext4) does not — measured
// 2026-10-30: Lev2_Quid1 threw "TraceTexture: package SkyBox not found" on the Deck (D-22).
// Exact path first (one stat, nothing else where the file system already ignores case); otherwise each component that
// does not exist is matched OrdinalIgnoreCase in its parent directory. null when there is no such file (Dir: directory).
// THE ONE OWNER of install file lookups (review 2026-10-31 F6; lock in runtime_poc/verify_architecture.py).
using System;
using System.IO;

namespace HP1.UEScript
{
    public static class UEInstallPath
    {
        public static string File(string path)
        {
            string r = Entry(path);
            return r is not null && System.IO.File.Exists(r) ? r : null;
        }

        public static string Dir(string path)
        {
            string r = Entry(path);
            return r is not null && Directory.Exists(r) ? r : null;
        }

        static string Entry(string path)
        {
            if (System.IO.File.Exists(path) || Directory.Exists(path)) return path;
            string parent = Path.GetDirectoryName(path), leaf = Path.GetFileName(path);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return null;
            parent = Entry(parent);
            if (parent is null || !Directory.Exists(parent)) return null;
            foreach (string e in Directory.EnumerateFileSystemEntries(parent))
                if (string.Equals(Path.GetFileName(e), leaf, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }
    }
}
