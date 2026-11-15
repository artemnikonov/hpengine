// Self-update of the Windows/Linux Launcher (the same design as Launcher.verifiedApp in launcher/Launcher.swift): the release
// archive (HPEngine-win-x64.zip / HPEngine-linux-x64.tar.gz) is used only if its Ed25519 signature (<archive>.sig, base64, made by
// launcher/update_sign.swift) verifies against the public key pinned into this build; the signature is checked before a byte of the
// archive is opened. Then: unpack into a staging folder inside the install, check the layout and the version, swap (Apply), restart.
// Only the Launcher file and HPEngine/ are replaced; everything else in the folder (a player's own game, Save/, Mods/) is never touched.
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace HPEngine.Launcher;

public static class SelfUpdate
{
    public static string LauncherName => OperatingSystem.IsWindows() ? "HPEngine Launcher.exe" : "HPEngine-Launcher";
    public const string StageName = ".hpengine-update", BackupSuffix = ".update-old";

    // The release asset this platform updates from; null: no package for it (the release page opens instead).
    public static string ArchiveName =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64 ? null
        : OperatingSystem.IsWindows() ? "HPEngine-win-x64.zip" : OperatingSystem.IsLinux() ? "HPEngine-linux-x64.tar.gz" : null;

    public static bool SignatureOk(byte[] archive, byte[] sigBase64, byte[] key)
    {
        try
        {
            var sig = Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(sigBase64).Trim());
            var v = new Ed25519Signer(); v.Init(false, new Ed25519PublicKeyParameters(key, 0));
            v.BlockUpdate(archive, 0, archive.Length);
            return sig.Length == 64 && v.VerifySignature(sig);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or Org.BouncyCastle.Crypto.CryptoException) { return false; }
    }

    // null: the archive is signed, unpacked into `stage` and is the advertised version; else the text for the player (stage removed).
    // The whole archive is read to check the signature (Ed25519 is not streamed): ponytail: a few hundred MB of memory at most.
    public static string Verify(string archive, byte[] sig, byte[] key, string version, string launcherName, string stage)
    {
        string No(string t) { try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } return t; }
        if (key is not { Length: 32 }) return No("This Launcher's update key is invalid.");
        if (!SignatureOk(File.ReadAllBytes(archive), sig, key)) return No("The download is not signed by HPEngine, so it was not installed.");
        try
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ZipFile.ExtractToDirectory(archive, stage);
            else { using var f = File.OpenRead(archive); TarFile.ExtractToDirectory(new GZipStream(f, CompressionMode.Decompress), stage, false); }   // keeps the exec bits
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return No("The downloaded file could not be unpacked: " + e.Message); }
        var exe = Path.Combine(stage, launcherName);
        if (!File.Exists(exe) || !Directory.Exists(Path.Combine(stage, "HPEngine"))) return No("The downloaded file is not an HPEngine package.");
        if (RunVersion(exe) != version) return No($"The downloaded Launcher is not version {version}.");
        return null;
    }

    // The unpacked Launcher's own --version (as Launcher.swift compares the plist's HPEngineVersion): an old signed archive
    // replayed under a new tag is refused.
    static string RunVersion(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            var o = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(20000)) { p.Kill(true); return null; }
            return o.Result.Trim();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    static void Move(string a, string b) { if (Directory.Exists(a)) Directory.Move(a, b); else File.Move(a, b); }
    static void Delete(string p) { if (Directory.Exists(p)) Directory.Delete(p, true); else if (File.Exists(p)) File.Delete(p); }
    // A leftover backup (the new item is in place): deleted, or, when it cannot be (a file the player's account may not
    // delete), renamed aside so the next swap still has its backup name free. null: gone; else what was left where.
    static string Clear(string bak)
    {
        try { Delete(bak); return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var aside = bak + "-" + Guid.NewGuid().ToString("N")[..8];
            Move(bak, aside);
            return $"{Path.GetFileName(aside)} could not be deleted ({e.Message})";
        }
    }

    // Stage then swap: each old item is renamed to <name>.update-old, the staged one moved in; any failure puts everything back
    // (newly placed items returned to the stage, so a retry can place them again; backups renamed back) and rethrows; the backups
    // are deleted only after every item is in place; one that cannot be deleted then does not fail the update (the new
    // install is in place): it is renamed aside (or left for the next update, which does the same) and named in the
    // returned note for the log (null: nothing left). A rollback that cannot finish (every item is still tried) leaves the
    // backups and the stage as they are, writes IncompleteName into the install and throws RollbackIncomplete; while that file exists no
    // Apply runs, so a backup that may be the only copy of the old install is never deleted. `step(what)` is a test hook:
    // "backup <name>" before an old item is renamed, "<name>" before the staged one is moved in, "restore <name>" before the
    // rollback renames a backup back. Staged items must be on the same volume (the stage is inside installDir).
    public const string IncompleteName = ".hpengine-update-incomplete";
    public sealed class RollbackIncomplete(string message, Exception inner) : Exception(message, inner);

    static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    // What the Launcher shows while IncompleteName exists (null: it does not); the self-update is then not offered, since every
    // Apply refuses until the player has repaired the install by hand.
    public static string IncompleteNotice(string installDir)
    {
        var mark = Path.Combine(installDir, IncompleteName);
        if (!File.Exists(mark)) return null;
        string state; try { state = File.ReadAllText(mark).Trim(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { state = e.Message; }
        return $"An earlier Launcher update could not put the old files back ({state}). Updating is off until this is repaired: "
            + $"download HPEngine again from the release page, unpack it over {installDir}, then delete the file {mark}.";
    }

    // The Update button: a signed package, a pinned key, and no unfinished rollback in the install.
    public static bool Offered(Release r, byte[] key, string installDir)
        => r is { Archive: not null, Sig: not null } && key != null && !File.Exists(Path.Combine(installDir, IncompleteName));

    public static string Apply(string stage, string installDir, string launcherName, Action<string> step = null)
    {
        var mark = Path.Combine(installDir, IncompleteName);
        if (File.Exists(mark)) throw new RollbackIncomplete("an earlier update could not put the old install back: " + File.ReadAllText(mark).Trim(), null);
        var placed = new List<(string n, string src, string dst, string bak, bool hadOld, bool done)>();
        var notes = new List<string>();
        try
        {
            foreach (var n in new[] { launcherName, "HPEngine" })
            {
                var src = Path.Combine(stage, n); var dst = Path.Combine(installDir, n); var bak = dst + BackupSuffix;
                // A backup left by an earlier update: a leftover when the item is in place, the only copy when it is missing.
                if (Exists(bak)) { if (Exists(dst)) { if (Clear(bak) is string l) notes.Add(l); } else Move(bak, dst); }
                bool had = Exists(dst);
                step?.Invoke("backup " + n);
                if (had) Move(dst, bak);
                placed.Add((n, src, dst, bak, had, false));   // only after the backup exists: the rollback renames it back
                step?.Invoke(n);
                Move(src, dst);
                placed[^1] = (n, src, dst, bak, had, true);
            }
        }
        catch (Exception first)
        {
            var left = new List<string>();
            foreach (var (n, src, dst, bak, had, done) in Enumerable.Reverse(placed))
            {
                try
                {
                    if (done) Move(dst, src);
                    step?.Invoke("restore " + n);
                    if (had) Move(bak, dst);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add($"{n}: {e.Message}"); }
            }
            if (left.Count == 0) throw;
            var state = $"{string.Join("; ", left)} (the old items are in *{BackupSuffix}, the new ones in {stage}; nothing was deleted)";
            try { File.WriteAllText(mark, state + "\n"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw new RollbackIncomplete(state, first);
        }
        foreach (var (_, _, _, bak, _, _) in placed)
            try { if (Clear(bak) is string l) notes.Add(l); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notes.Add($"{Path.GetFileName(bak)} left ({e.Message}); the next update deletes it"); }
        return notes.Count == 0 ? null : "the new install is in place; " + string.Join("; ", notes);
    }

    // The helper (launched from a copy of the Launcher in a temp folder): `--apply-update <pid> <install dir> <stage> <launcher name>`.
    // Waits for the Launcher to exit (Windows cannot replace a running exe), swaps (retrying: virus scanners hold files),
    // starts the Launcher again — the new one, or the old one restored when the swap failed — and writes what happened to a log.
    // waitMs: a test hook. A Launcher that has not exited in time: nothing is swapped, the stage removed, the failure logged, and
    // no second Launcher is started (the old one is still running). A rollback that did not finish: no retry, nothing deleted.
    public static int Helper(string[] a, bool restart = true, int waitMs = 60000)
    {
        var log = Path.Combine(Path.GetTempPath(), "hpengine-update.log");
        string dir = a[2], stage = a[3], name = a[4];
        string err = null, note = null; bool exited = true, incomplete = false;
        try { using var p = Process.GetProcessById(int.Parse(a[1])); if (!p.WaitForExit(waitMs)) { exited = false; err = "the Launcher did not exit"; } }
        catch (ArgumentException) { /* already gone */ }
        for (int i = 0; exited && i < 10; i++)
        {
            try { note = Apply(stage, dir, name); err = null; break; }
            catch (RollbackIncomplete e) { err = e.Message; incomplete = true; break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { err = e.Message; Thread.Sleep(500); }
        }
        if (!incomplete) try { Delete(stage); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { note = (note == null ? "" : note + "; ") + $"the stage {stage} was left ({e.Message})"; }
        string what = err == null ? "updated" : !exited ? "FAILED, nothing was changed (no second Launcher started): " + err
            : incomplete ? "FAILED, the old install could NOT be put back: " + err : "FAILED, the old install is back: " + err;
        // The log is only a record: a write that fails (a full disk, a locked file) must not stop the Launcher's restart.
        try { File.AppendAllText(log, $"{DateTime.Now:s} {what} ({dir})" + (note == null ? "" : " [" + note + "]") + "\n"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var exe = Path.Combine(dir, name);
        if (restart && exited && File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = false });
        return err == null ? 0 : 1;
    }

    // Start the helper from a temp copy of this Launcher; the caller then exits.
    public const string HelperPrefix = "hpengine-update-";
    public static void StartHelper(string installDir, string stage)
    {
        var tmp = Path.Combine(Path.GetTempPath(), HelperPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var copy = Path.Combine(tmp, Path.GetFileName(Environment.ProcessPath));
        File.Copy(Environment.ProcessPath, copy);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(copy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Process.Start(new ProcessStartInfo(copy) { ArgumentList = { "--apply-update", Environment.ProcessId.ToString(), installDir, stage, LauncherName }, UseShellExecute = false });
    }

    // The helper copies left by earlier updates (a running exe cannot delete its own folder on Windows): removed at the next
    // Launcher start. Only folders untouched for HelperMaxAge: a helper is done well within it (60 s wait for the Launcher, 10
    // swap tries 0.5 s apart), so a helper that is still running is never pulled from under itself. A folder that cannot be
    // deleted is left for the next start. Returns how many were removed.
    public static readonly TimeSpan HelperMaxAge = TimeSpan.FromMinutes(10);
    public static int CleanHelpers(string tempDir, DateTime utcNow)
    {
        int n = 0;
        try
        {
            foreach (var d in Directory.EnumerateDirectories(tempDir, HelperPrefix + "*"))
                try { if (utcNow - Directory.GetLastWriteTimeUtc(d) > HelperMaxAge) { Directory.Delete(d, true); n++; } }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return n;
    }
}
