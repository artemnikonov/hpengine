// Checks of the Windows/Linux self-update (SelfUpdate.cs), "Add to Steam" (SteamShortcuts.cs) and the title by edition (GameTitle).
// The signing here uses a throw-away key; cross-compatibility with the release signer (launcher/update_sign.swift, CryptoKit) is
// checked when test_desktop.sh passes HP1_XSIGN (public key, file, signature made by update_sign.swift).
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using HPEngine.Launcher;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

static partial class T
{
    static (byte[] Pub, Ed25519PrivateKeyParameters Priv) NewKey()
    {
        var g = new Ed25519KeyPairGenerator(); g.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var kp = g.GenerateKeyPair();
        return (((Ed25519PublicKeyParameters)kp.Public).GetEncoded(), (Ed25519PrivateKeyParameters)kp.Private);
    }
    static byte[] Sign(Ed25519PrivateKeyParameters k, string file)
    {
        var d = File.ReadAllBytes(file); var s = new Ed25519Signer(); s.Init(true, k); s.BlockUpdate(d, 0, d.Length);
        return Encoding.ASCII.GetBytes(Convert.ToBase64String(s.GenerateSignature()));
    }

    // A package as make_launcher.sh lays it out: <launcher> + README.txt + HPEngine/HPEngine; the "launcher" is a shell script
    // that prints its version (and, run from the install folder, leaves a marker: the restart check).
    static string MakePackage(string dir, string version, string launcherName, bool withEngine = true)
    {
        Directory.CreateDirectory(dir);
        var l = Path.Combine(dir, launcherName);
        File.WriteAllText(l, $"#!/bin/sh\nif [ \"$1\" = --version ]; then echo {version}; else touch \"$(dirname \"$0\")/restarted\"; fi\n");
        File.SetUnixFileMode(l, (UnixFileMode)0b111_101_101);
        File.WriteAllText(Path.Combine(dir, "README.txt"), "readme " + version);
        if (withEngine) { Directory.CreateDirectory(Path.Combine(dir, "HPEngine")); File.WriteAllText(Path.Combine(dir, "HPEngine", "HPEngine"), "engine " + version); }
        return dir;
    }
    static string Tgz(string dir, string archive)
    {
        using var o = File.Create(archive); using var z = new GZipStream(o, CompressionLevel.Fastest);
        TarFile.CreateFromDirectory(dir, z, false); return archive;
    }

    static void SelfUpdateCases()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hp1-selfupdate-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        try
        {
            const string L = "HPEngine-Launcher";
            var (pub, priv) = NewKey(); var (_, other) = NewKey();
            var pkg = MakePackage(Path.Combine(tmp, "pkg"), "9.9.9", L);
            var tgz = Tgz(pkg, Path.Combine(tmp, "good.tar.gz")); var sig = Sign(priv, tgz);
            string stage = Path.Combine(tmp, "stage");
            string V(string archive, byte[] s, string ver = "9.9.9", byte[] key = null) => SelfUpdate.Verify(archive, s, key ?? pub, ver, L, stage);

            Check("verify: signed archive accepted", V(tgz, sig) == null, V(tgz, sig));
            Check("verify: unpacked into the stage, exec bit kept", File.Exists(Path.Combine(stage, L)) && File.Exists(Path.Combine(stage, "HPEngine", "HPEngine"))
                && (File.GetUnixFileMode(Path.Combine(stage, L)) & UnixFileMode.UserExecute) != 0);
            Directory.Delete(stage, true);
            fired.Add("upd:signed-good");

            const string notSigned = "The download is not signed by HPEngine, so it was not installed.";
            var tampered = Path.Combine(tmp, "tampered.tar.gz"); File.Copy(tgz, tampered); File.AppendAllText(tampered, "x");
            Eq("verify: tampered archive refused", notSigned, V(tampered, sig));
            var half = Path.Combine(tmp, "half.tar.gz"); File.WriteAllBytes(half, File.ReadAllBytes(tgz)[..(int)(new FileInfo(tgz).Length / 2)]);
            Eq("verify: partial download refused", notSigned, V(half, sig));
            Eq("verify: signature of another key refused", notSigned, V(tgz, Sign(other, tgz)));
            Eq("verify: garbage signature refused", notSigned, V(tgz, Encoding.ASCII.GetBytes("garbage")));
            Eq("verify: empty signature refused", notSigned, V(tgz, Array.Empty<byte>()));
            var junk = Path.Combine(tmp, "junk.tar.gz"); File.WriteAllText(junk, "not an archive at all");
            Eq("verify: unsigned junk is refused as unsigned (signature before the archive is opened)", notSigned, V(junk, Sign(other, junk)));
            Check("verify: a refused archive leaves no stage", !Directory.Exists(stage));
            Eq("verify: no key in this build", "This Launcher's update key is invalid.", V(tgz, sig, key: Array.Empty<byte>()));
            var signedJunk = V(junk, Sign(priv, junk));
            Check("verify: signed but not an archive refused", signedJunk != null && signedJunk.StartsWith("The downloaded file could not be unpacked"), signedJunk);
            Eq("verify: wrong version (an old signed archive under a new tag)", "The downloaded Launcher is not version 9.9.8.", V(tgz, sig, "9.9.8"));
            var noEngine = Tgz(MakePackage(Path.Combine(tmp, "noeng"), "9.9.9", L, withEngine: false), Path.Combine(tmp, "noeng.tar.gz"));
            Eq("verify: signed but not a package refused", "The downloaded file is not an HPEngine package.", V(noEngine, Sign(priv, noEngine)));
            Check("verify: a package refused after unpacking leaves no stage", !Directory.Exists(stage));
            fired.Add("upd:refused");

            // zip (Windows): the unpack and the layout; the version run needs an exec bit a zip does not carry, so it ends there.
            var zip = Path.Combine(tmp, "good.zip"); ZipFile.CreateFromDirectory(pkg, zip);
            var zr = V(zip, Sign(priv, zip));
            Check("verify: zip is signature-checked, unpacked and laid out", (zr == null || zr == "The downloaded Launcher is not version 9.9.9.") && File.Exists(Path.Combine(stage, L)), zr);
            Eq("verify: tampered zip refused", notSigned, V(zip, Sign(other, zip)));
            fired.Add("upd:zip");

            // Apply: an own game folder (System/, Save/ and the game's own README.txt next to the Launcher) is never touched
            string Install(string name)
            {
                var d = Path.Combine(tmp, name); MakePackage(d, "1.0.0", L);
                foreach (var f in new[] { "System/a.u", "Save/s.sav", "Mods/m/x.txt", "HPEngineData/Mods.ini" }) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(d, f))); File.WriteAllText(Path.Combine(d, f), "game:" + f); }
                File.WriteAllText(Path.Combine(d, "README.txt"), "the game's own readme");
                File.WriteAllText(Path.Combine(d, "HPEngine", "old-only"), "old");
                return d;
            }
            string Snap(string d) => string.Join("|", Directory.EnumerateFileSystemEntries(d, "*", SearchOption.AllDirectories).Where(f => !f.Contains(StageName(d))).OrderBy(f => f, StringComparer.Ordinal).Select(f => Path.GetRelativePath(d, f) + (File.Exists(f) ? "=" + Rd(f).Replace("\n", "/") : "")));
            string StageName(string d) => SelfUpdate.StageName;
            var inst = Install("inst"); var st1 = Path.Combine(inst, SelfUpdate.StageName); MakePackage(st1, "2.0.0", L);
            SelfUpdate.Apply(st1, inst, L);
            Check("apply: launcher and HPEngine/ replaced", Rd(Path.Combine(inst, "HPEngine", "HPEngine")) == "engine 2.0.0" && Rd(Path.Combine(inst, L)).Contains("2.0.0") && !File.Exists(Path.Combine(inst, "HPEngine", "old-only")));
            Check("apply: the game folder is untouched (System, Save, Mods, settings, its own README.txt)",
                Rd(Path.Combine(inst, "System/a.u")) == "game:System/a.u" && Rd(Path.Combine(inst, "Save/s.sav")) == "game:Save/s.sav" && Rd(Path.Combine(inst, "Mods/m/x.txt")) == "game:Mods/m/x.txt"
                && Rd(Path.Combine(inst, "HPEngineData/Mods.ini")) == "game:HPEngineData/Mods.ini" && Rd(Path.Combine(inst, "README.txt")) == "the game's own readme");
            Check("apply: no backup left after success", !File.Exists(Path.Combine(inst, L + SelfUpdate.BackupSuffix)) && !Directory.Exists(Path.Combine(inst, "HPEngine" + SelfUpdate.BackupSuffix)));
            fired.Add("upd:apply");

            // failure while placing HPEngine/: everything is as before (launcher already swapped is put back)
            inst = Install("inst2"); st1 = Path.Combine(inst, SelfUpdate.StageName); MakePackage(st1, "2.0.0", L);
            var before = Snap(inst);
            bool threw = false;
            try { SelfUpdate.Apply(st1, inst, L, n => { if (n == "HPEngine") throw new IOException("simulated"); }); } catch (Exception e) { threw = e is IOException; }
            Check("apply: failure is raised", threw);
            Eq("apply: failure leaves the old install exactly as it was (no half-replaced install, no backup)", before, Snap(inst));
            Check("apply: the rollback returns the placed launcher to the stage (a retry can still place it)", File.Exists(Path.Combine(st1, L)) && Directory.Exists(Path.Combine(st1, "HPEngine")));
            Exception retry = null; try { SelfUpdate.Apply(st1, inst, L); } catch (Exception e) { retry = e; }
            Check("apply: the retry after a rollback succeeds", retry == null && Rd(Path.Combine(inst, "HPEngine", "HPEngine")) == "engine 2.0.0" && Rd(Path.Combine(inst, L)).Contains("2.0.0"));
            fired.Add("upd:rollback");

            // failure while backing up HPEngine/ (the old item could not be renamed): the launcher goes back, the failure is the original one
            inst = Install("inst2b"); st1 = Path.Combine(inst, SelfUpdate.StageName); MakePackage(st1, "2.0.0", L);
            before = Snap(inst);
            Exception got = null;
            try { SelfUpdate.Apply(st1, inst, L, n => { if (n == "backup HPEngine") throw new IOException("simulated backup"); }); } catch (Exception e) { got = e; }
            Check("apply: a failed backup rename raises its own failure, not one from the rollback", got is IOException { Message: "simulated backup" }, got?.ToString());
            Eq("apply: a failed backup rename leaves the old install exactly as it was", before, Snap(inst));
            Check("apply: ... and the stage whole", File.Exists(Path.Combine(st1, L)) && Directory.Exists(Path.Combine(st1, "HPEngine")));
            fired.Add("upd:rollback-backup");

            // a rollback that cannot put HPEngine/ back: every item is still tried, nothing is deleted, the state is marked and
            // no later Apply (the helper's retry, the next update) deletes the backup that is now the only copy of the old HPEngine/
            inst = Install("inst2c"); st1 = Path.Combine(inst, SelfUpdate.StageName); MakePackage(st1, "2.0.0", L);
            var bakE = Path.Combine(inst, "HPEngine" + SelfUpdate.BackupSuffix);
            got = null;
            try { SelfUpdate.Apply(st1, inst, L, n => { if (n is "HPEngine" or "restore HPEngine") throw new IOException("simulated " + n); }); } catch (Exception e) { got = e; }
            Check("apply: an unfinished rollback raises RollbackIncomplete, keeps the backup, marks the install, still restores the launcher",
                got is SelfUpdate.RollbackIncomplete && Rd(Path.Combine(bakE, "HPEngine")) == "engine 1.0.0" && File.Exists(Path.Combine(inst, SelfUpdate.IncompleteName))
                && Rd(Path.Combine(inst, L)).Contains("1.0.0"), got?.ToString());
            Exception again = null; try { SelfUpdate.Apply(st1, inst, L); } catch (Exception e) { again = e; }
            Check("apply: after an unfinished rollback a retry refuses and the only copy of the old HPEngine/ is kept",
                again is SelfUpdate.RollbackIncomplete && Rd(Path.Combine(bakE, "HPEngine")) == "engine 1.0.0", again?.ToString());
            var logI = Path.Combine(Path.GetTempPath(), "hpengine-update.log");
            long lenI = File.Exists(logI) ? new FileInfo(logI).Length : 0;
            int irc = SelfUpdate.Helper(new[] { "--apply-update", "2147483000", inst, st1, L }, restart: false);
            var tailI = File.Exists(logI) ? File.ReadAllText(logI)[(int)lenI..] : "";
            Check("helper: an unfinished rollback is logged as such (not 'the old install is back'), backup and stage kept",
                irc == 1 && tailI.Contains("could NOT be put back") && !tailI.Contains("the old install is back") && Directory.Exists(bakE) && Directory.Exists(st1), tailI.Trim());
            fired.Add("upd:rollback-incomplete");
            // the Launcher reads the marker: a message saying what to do, and no Update button while it exists
            var rel = new Release("2.0.0", "https://example.invalid/r", "https://example.invalid/a", "https://example.invalid/a.sig");
            var notice = SelfUpdate.IncompleteNotice(inst);
            Check("marker: the notice names the marker file and what to do", notice != null && notice.Contains(Path.Combine(inst, SelfUpdate.IncompleteName)) && notice.Contains("release page"), notice);
            Check("marker: no self-update offered while it exists, offered without it", !SelfUpdate.Offered(rel, pub, inst) && SelfUpdate.Offered(rel, pub, Install("inst2d"))
                && SelfUpdate.IncompleteNotice(Path.Combine(tmp, "inst2d")) == null);
            var mw = new MainWindow(Path.Combine(tmp, "nogame"), inst, "1.0.0", false); mw.Show(); Dispatcher.UIThread.RunJobs();
            Check("marker: the window shows the notice at start", Find<TextBlock>(mw, "ErrorText") is { IsVisible: true } et && et.Text == notice && Find<Border>(mw, "UpdateBanner") is { IsVisible: false },
                Find<TextBlock>(mw, "ErrorText")?.Text);
            mw.Close();
            fired.Add("upd:marker-shown");

            // a log write that fails (here the log path is a folder) does not stop the helper: swapped and restarted
            var oldTmp = Environment.GetEnvironmentVariable("TMPDIR");
            var badTmp = Path.Combine(tmp, "badtmp"); Directory.CreateDirectory(Path.Combine(badTmp, "hpengine-update.log"));
            inst = Install("inst3b"); st1 = Path.Combine(inst, SelfUpdate.StageName); MakePackage(st1, "2.0.0", L);
            int lrc = -1; Exception lex = null;
            Environment.SetEnvironmentVariable("TMPDIR", badTmp + "/");
            try { lrc = SelfUpdate.Helper(new[] { "--apply-update", "2147483000", inst, st1, L }); } catch (Exception e) { lex = e; }
            finally { Environment.SetEnvironmentVariable("TMPDIR", oldTmp); }
            for (int i = 0; i < 100 && !File.Exists(Path.Combine(inst, "restarted")); i++) Thread.Sleep(50);
            Check("helper: a failed log write: no throw, rc 0, swapped, the Launcher restarted",
                lex == null && lrc == 0 && Rd(Path.Combine(inst, "HPEngine", "HPEngine")) == "engine 2.0.0" && File.Exists(Path.Combine(inst, "restarted")), $"rc={lrc} ex={lex?.Message}");
            fired.Add("upd:log-denied");

            // helper copies left in temp by earlier updates: removed at the next start once old; a recent one (maybe running) stays
            var tdir = Path.Combine(tmp, "temp"); Directory.CreateDirectory(tdir);
            var oldH = Path.Combine(tdir, SelfUpdate.HelperPrefix + "old"); var newH = Path.Combine(tdir, SelfUpdate.HelperPrefix + "new"); var other2 = Path.Combine(tdir, "other");
            foreach (var d in new[] { oldH, newH, other2 }) { Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, L), "x"); }
            var now = DateTime.UtcNow; Directory.SetLastWriteTimeUtc(oldH, now - SelfUpdate.HelperMaxAge - TimeSpan.FromMinutes(1));
            int cleaned = SelfUpdate.CleanHelpers(tdir, now);
            Check("cleanup: an old helper folder removed, a recent one and other folders kept", cleaned == 1 && !Directory.Exists(oldH) && Directory.Exists(newH) && Directory.Exists(other2), $"cleaned={cleaned}");
            fired.Add("upd:helper-cleanup");

            // the helper: the Launcher has exited; swap, restart the (new) Launcher
            inst = Install("inst3"); st1 = Path.Combine(inst, SelfUpdate.StageName); MakePackage(st1, "2.0.0", L);
            int rc = SelfUpdate.Helper(new[] { "--apply-update", "2147483000", inst, st1, L });
            for (int i = 0; i < 100 && !File.Exists(Path.Combine(inst, "restarted")); i++) Thread.Sleep(50);
            Check("helper: swapped, stage removed, the new Launcher restarted", rc == 0 && Rd(Path.Combine(inst, "HPEngine", "HPEngine")) == "engine 2.0.0" && !Directory.Exists(st1) && File.Exists(Path.Combine(inst, "restarted")));
            fired.Add("upd:helper");
            // the helper waits for the process to exit
            var sleeper = Process.Start(new ProcessStartInfo("sleep", "2") { UseShellExecute = false });
            inst = Install("inst4"); st1 = Path.Combine(inst, SelfUpdate.StageName); MakePackage(st1, "2.0.0", L);
            var sw = Stopwatch.StartNew(); SelfUpdate.Helper(new[] { "--apply-update", sleeper.Id.ToString(), inst, st1, L }, restart: false); sw.Stop();
            Check("helper: waits for the Launcher process to exit", sw.ElapsedMilliseconds >= 1500 && Rd(Path.Combine(inst, "HPEngine", "HPEngine")) == "engine 2.0.0", sw.ElapsedMilliseconds + " ms");
            fired.Add("upd:helper-waits");
            // the Launcher does not exit in time: nothing swapped, the stage removed, the failure logged, no second Launcher started
            var hung = Process.Start(new ProcessStartInfo("sleep", "10") { UseShellExecute = false });
            inst = Install("inst5"); st1 = Path.Combine(inst, SelfUpdate.StageName); MakePackage(st1, "2.0.0", L);
            var log = Path.Combine(Path.GetTempPath(), "hpengine-update.log");
            long logLen = File.Exists(log) ? new FileInfo(log).Length : 0;
            int hrc = -1; Exception hex = null;
            try { hrc = SelfUpdate.Helper(new[] { "--apply-update", hung.Id.ToString(), inst, st1, L }, waitMs: 300); } catch (Exception e) { hex = e; }
            hung.Kill(); hung.Dispose();
            for (int i = 0; i < 20 && !File.Exists(Path.Combine(inst, "restarted")); i++) Thread.Sleep(50);
            var tail = File.Exists(log) ? File.ReadAllText(log)[(int)logLen..] : "";
            Check("helper: a Launcher that does not exit: no throw, rc 1, old install kept, stage removed, logged, no second Launcher",
                hex == null && hrc == 1 && Rd(Path.Combine(inst, "HPEngine", "HPEngine")) == "engine 1.0.0" && !Directory.Exists(st1)
                && tail.Contains("did not exit") && tail.Contains(inst) && !File.Exists(Path.Combine(inst, "restarted")), $"rc={hrc} ex={hex?.Message} log='{tail.Trim()}'");
            fired.Add("upd:helper-timeout");

            // Cleanup that cannot delete (a folder the account may not empty: a read-only subfolder, as Windows' access rules
            // or a scanner's lock): after a finished swap it must not fail the update; the helper's stage removal neither.
            // Unix only (chmod); as root nothing is denied, so the branch does not fire there.
            if (!OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess)
            {
                string Lock(string d) { var l = Path.Combine(d, "locked"); Directory.CreateDirectory(l); File.WriteAllText(Path.Combine(l, "f"), "x"); File.SetUnixFileMode(l, (UnixFileMode)0b101_101_101); return l; }
                void Unlock(string root) { foreach (var d in Directory.EnumerateDirectories(root, "locked", SearchOption.AllDirectories)) File.SetUnixFileMode(d, (UnixFileMode)0b111_101_101); }
                var logC = Path.Combine(Path.GetTempPath(), "hpengine-update.log");
                // the old HPEngine/ holds an undeletable folder: its backup stays after the swap
                inst = Install("inst6"); Lock(Path.Combine(inst, "HPEngine")); st1 = Path.Combine(inst, SelfUpdate.StageName); MakePackage(st1, "2.0.0", L);
                long lenC = File.Exists(logC) ? new FileInfo(logC).Length : 0;
                int crc = -1; Exception cex = null;
                try { crc = SelfUpdate.Helper(new[] { "--apply-update", "2147483000", inst, st1, L }); } catch (Exception e) { cex = e; }
                for (int i = 0; i < 100 && !File.Exists(Path.Combine(inst, "restarted")); i++) Thread.Sleep(50);
                var tailC = File.Exists(logC) ? File.ReadAllText(logC)[(int)lenC..] : "";
                var aside = Directory.GetDirectories(inst, "HPEngine" + SelfUpdate.BackupSuffix + "-*");
                Check("helper: a backup that cannot be deleted after the swap: rc 0, logged 'updated' with the leftover named, new install kept, restarted",
                    cex == null && crc == 0 && tailC.Contains(" updated ") && !tailC.Contains("FAILED") && tailC.Contains("could not be deleted")
                    && Rd(Path.Combine(inst, "HPEngine", "HPEngine")) == "engine 2.0.0" && Rd(Path.Combine(inst, L)).Contains("2.0.0") && File.Exists(Path.Combine(inst, "restarted"))
                    && aside.Length == 1 && !Directory.Exists(Path.Combine(inst, "HPEngine" + SelfUpdate.BackupSuffix)), $"rc={crc} ex={cex?.Message} log='{tailC.Trim()}'");
                // the next update finds an undeletable backup under the backup name: it is moved aside, the update goes on
                Directory.CreateDirectory(Path.Combine(inst, "HPEngine" + SelfUpdate.BackupSuffix)); Lock(Path.Combine(inst, "HPEngine" + SelfUpdate.BackupSuffix));
                st1 = Path.Combine(inst, SelfUpdate.StageName); MakePackage(st1, "3.0.0", L);
                string note = null; cex = null;
                try { note = SelfUpdate.Apply(st1, inst, L); } catch (Exception e) { cex = e; }
                Check("apply: an undeletable backup left by an earlier update does not stop the next one",
                    cex == null && Rd(Path.Combine(inst, "HPEngine", "HPEngine")) == "engine 3.0.0" && note != null && note.Contains("could not be deleted"), $"ex={cex?.Message} note={note}");
                // the stage cannot be removed after the swap: logged, restarted, no throw
                inst = Install("inst7"); st1 = Path.Combine(inst, SelfUpdate.StageName); MakePackage(st1, "2.0.0", L); Lock(st1);
                lenC = File.Exists(logC) ? new FileInfo(logC).Length : 0; crc = -1; cex = null;
                try { crc = SelfUpdate.Helper(new[] { "--apply-update", "2147483000", inst, st1, L }); } catch (Exception e) { cex = e; }
                for (int i = 0; i < 100 && !File.Exists(Path.Combine(inst, "restarted")); i++) Thread.Sleep(50);
                tailC = File.Exists(logC) ? File.ReadAllText(logC)[(int)lenC..] : "";
                Check("helper: a stage that cannot be removed: no throw, rc 0, logged with the stage named, restarted",
                    cex == null && crc == 0 && tailC.Contains(" updated ") && tailC.Contains("the stage") && File.Exists(Path.Combine(inst, "restarted")), $"rc={crc} ex={cex?.GetType().Name}: {cex?.Message} log='{tailC.Trim()}'");
                // Verify's refusal with a stage it cannot remove: the refusal text, no throw
                Directory.CreateDirectory(stage); Lock(stage); string vr = null; cex = null;
                try { vr = V(tgz, sig, key: new byte[1]); } catch (Exception e) { cex = e; }
                Check("verify: a refusal whose stage cannot be removed still returns its text", cex == null && vr == "This Launcher's update key is invalid.", cex?.GetType().Name + " " + cex?.Message);
                Unlock(tmp);
                fired.Add("upd:cleanup-denied");
            }
            else Console.WriteLine("note: Windows or root: the denied-cleanup cases are not covered in this run");

            // release choice: this platform's archive and signature assets
            var feed = "[{\"tag_name\":\"v0.3.0\",\"draft\":false,\"prerelease\":false,\"html_url\":\"https://example.invalid/3\",\"assets\":["
                + "{\"name\":\"HPEngine-linux-x64.tar.gz\",\"browser_download_url\":\"https://example.invalid/l.tgz\"},{\"name\":\"HPEngine-linux-x64.tar.gz.sig\",\"browser_download_url\":\"https://example.invalid/l.sig\"},"
                + "{\"name\":\"HPEngine-win-x64.zip\",\"browser_download_url\":\"https://example.invalid/w.zip\"}]}]";
            var r = Updates.Pick(feed, "0.2.0", "HPEngine-linux-x64.tar.gz");
            Eq("pick: archive and signature of this platform", "https://example.invalid/l.tgz https://example.invalid/l.sig", r.Archive + " " + r.Sig);
            var w = Updates.Pick(feed, "0.2.0", "HPEngine-win-x64.zip");
            Check("pick: an archive without a signature has no Sig (the page opens instead)", w.Archive == "https://example.invalid/w.zip" && w.Sig == null);
            Check("pick: no archive name, no assets", Updates.Pick(feed, "0.2.0") is { Archive: null, Sig: null });
            var noUrl = "[{\"tag_name\":\"v0.3.0\",\"html_url\":\"https://example.invalid/3\",\"assets\":[{\"name\":\"HPEngine-linux-x64.tar.gz\"},"
                + "{\"name\":\"HPEngine-linux-x64.tar.gz.sig\",\"browser_download_url\":\"https://example.invalid/l.sig\"}]}]";
            Release nr = null; Exception ne = null;
            try { nr = Updates.Pick(noUrl, "0.2.0", "HPEngine-linux-x64.tar.gz"); } catch (Exception e) { ne = e; }
            Check("pick: an asset without browser_download_url is absent, the release still found", ne == null && nr is { Archive: null, Version: "0.3.0" }, ne?.Message);
            fired.Add("upd:pick");

            // the release signer (CryptoKit, launcher/update_sign.swift) and this verifier agree
            var x = Environment.GetEnvironmentVariable("HP1_XSIGN")?.Split('|');
            if (x is { Length: 3 })
            {
                Check("cross: a signature made by update_sign.swift verifies", SelfUpdate.SignatureOk(File.ReadAllBytes(x[1]), File.ReadAllBytes(x[2]), Convert.FromBase64String(x[0])));
                var bad = Path.Combine(tmp, "x.bin"); File.WriteAllBytes(bad, File.ReadAllBytes(x[1]).Append((byte)1).ToArray());
                Check("cross: and not for another file", !SelfUpdate.SignatureOk(File.ReadAllBytes(bad), File.ReadAllBytes(x[2]), Convert.FromBase64String(x[0])));
                fired.Add("upd:cross");
            }
            else Console.WriteLine("note: HP1_XSIGN not set: the signature compatibility with update_sign.swift is not covered in this run");
        }
        finally { Directory.Delete(tmp, true); }
    }

    // ---- Add to Steam ----
    static byte[] Vdf(params (string Exe, string Name)[] entries)
    {
        var ms = new MemoryStream();
        void Z(string s) { ms.Write(Encoding.UTF8.GetBytes(s)); ms.WriteByte(0); }
        ms.WriteByte(0); Z("shortcuts");
        for (int i = 0; i < entries.Length; i++)
        {
            ms.WriteByte(0); Z(i.ToString());
            ms.WriteByte(2); Z("appid"); ms.Write(BitConverter.GetBytes((uint)(0x80001234 + i)));
            ms.WriteByte(1); Z("AppName"); Z(entries[i].Name);
            ms.WriteByte(1); Z("Exe"); Z(entries[i].Exe);
            ms.WriteByte(2); Z("IsHidden"); ms.Write(new byte[4]);
            ms.WriteByte(0); Z("tags"); ms.WriteByte(1); Z("0"); Z("favorite"); ms.WriteByte(8);
            ms.WriteByte(8);
        }
        ms.WriteByte(8); ms.WriteByte(8);
        return ms.ToArray();
    }

    static void SteamCases()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hp1-steam-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        try
        {
            var old = Vdf(("\"/games/Other/run.sh\"", "Other"), ("\"/opt/x\"", "X"));
            var added = SteamShortcuts.Add(old, "HPEngine", "/home/u/HP/HPEngine-Launcher", "/home/u/HP");
            var (_, end) = SteamShortcuts.Parse(old);
            Check("vdf: existing entries preserved byte for byte (prefix and the two closing bytes)", added.AsSpan(0, end).SequenceEqual(old.AsSpan(0, end)) && added.AsSpan(added.Length - 2).SequenceEqual(old.AsSpan(old.Length - 2)));
            var (e2, _) = SteamShortcuts.Parse(added);
            Seq("vdf: the new entry is index 2 with the quoted Exe", new[] { "0=\"/games/Other/run.sh\"", "1=\"/opt/x\"", "2=\"/home/u/HP/HPEngine-Launcher\"" }, e2.Select(e => e.Index + "=" + e.Exe));
            Check("vdf: Has finds it, not an unrelated path", SteamShortcuts.Has(added, "/home/u/HP/HPEngine-Launcher") && !SteamShortcuts.Has(old, "/home/u/HP/HPEngine-Launcher"));
            var ai = IndexOf(added, new byte[] { 2, (byte)'a', (byte)'p', (byte)'p', (byte)'i', (byte)'d', 0 }, end) + 7;
            Check("vdf: appid has the non-Steam bit", ai > 7 && (BitConverter.ToUInt32(added, ai) & 0x80000000) != 0);
            var fresh = SteamShortcuts.Add(SteamShortcuts.Empty, "HPEngine", "/e", "/");
            Check("vdf: an empty shortcuts file gets index 0", SteamShortcuts.Parse(fresh).Entries is [{ Index: 0 }]);
            Check("vdf: a file that is not a shortcuts.vdf is refused", Throws(() => SteamShortcuts.Parse(Encoding.ASCII.GetBytes("\0other\0\b\b"))) && Throws(() => SteamShortcuts.Parse(old[..^1])) && Throws(() => SteamShortcuts.Parse(old.Append((byte)0).ToArray())));
            fired.Add("steam:vdf");

            string Home(string name, params string[] ids)
            {
                var h = Path.Combine(tmp, name); foreach (var i in ids) Directory.CreateDirectory(Path.Combine(h, ".steam", "steam", "userdata", i, "config")); return h;
            }
            var flat = Path.Combine(tmp, "hf", ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
            Directory.CreateDirectory(Path.Combine(flat, "userdata", "1001", "config"));
            Check("Flatpak Steam: its folder is found", SteamShortcuts.FindRoot(Path.Combine(tmp, "hf")) == flat, SteamShortcuts.FindRoot(Path.Combine(tmp, "hf")));
            Check("no Steam folder: null", SteamShortcuts.FindRoot(Path.Combine(tmp, "nosteam")) == null);
            var home = Home("h1", "1001", "2002", "0");
            var root = SteamShortcuts.FindRoot(home);
            var f1 = Path.Combine(root, "userdata", "1001", "config", "shortcuts.vdf"); var f2 = Path.Combine(root, "userdata", "2002", "config", "shortcuts.vdf");
            File.WriteAllBytes(f1, old);   // 2002 has none yet; 0 is not an account
            const string exe = "/home/u/HP/HPEngine-Launcher";
            var msg = SteamShortcuts.AddTo(root, "HPEngine", exe, "/home/u/HP", () => true);
            Check("steam running: refused, nothing written", msg.Contains("Steam is running") && File.ReadAllBytes(f1).SequenceEqual(old) && !File.Exists(f2));
            fired.Add("steam:running");
            msg = SteamShortcuts.AddTo(root, "HPEngine", exe, "/home/u/HP", () => false);
            Check("steam closed: every account gets the entry (not userdata/0)", msg.StartsWith("Added HPEngine to Steam (2 accounts)") && SteamShortcuts.Has(File.ReadAllBytes(f1), exe) && SteamShortcuts.Has(File.ReadAllBytes(f2), exe)
                && !File.Exists(Path.Combine(root, "userdata", "0", "config", "shortcuts.vdf")), msg);
            var backups = Directory.GetFiles(Path.GetDirectoryName(f1), "shortcuts.vdf.hpengine-backup-*");
            Check("a backup of the existing file is kept (and none for a file that did not exist)", backups.Length == 1 && File.ReadAllBytes(backups[0]).SequenceEqual(old) && Directory.GetFiles(Path.GetDirectoryName(f2), "*backup*").Length == 0);
            var once = File.ReadAllBytes(f1);
            msg = SteamShortcuts.AddTo(root, "HPEngine", exe, "/home/u/HP", () => false);
            Check("repeat: no duplicate, files unchanged, no new backup", msg.Contains("already") && File.ReadAllBytes(f1).SequenceEqual(once) && SteamShortcuts.Parse(File.ReadAllBytes(f1)).Entries.Count(e => e.Exe.Contains("HPEngine-Launcher")) == 1
                && Directory.GetFiles(Path.GetDirectoryName(f1), "*backup*").Length == 1);
            msg = SteamShortcuts.AddTo(root, "HPEngine", "/elsewhere/HPEngine-Launcher", "/elsewhere", () => false);
            Check("another install path: its own entry", msg.StartsWith("Added") && SteamShortcuts.Parse(File.ReadAllBytes(f1)).Entries.Count == 4);
            fired.Add("steam:add");
            File.WriteAllBytes(f2, new byte[] { 1, 2, 3 });
            msg = SteamShortcuts.AddTo(root, "HPEngine", "/third/Launcher", "/third", () => false);
            Check("a damaged file is left alone and named", msg.Contains("2002") && File.ReadAllBytes(f2).SequenceEqual(new byte[] { 1, 2, 3 }) && SteamShortcuts.Has(File.ReadAllBytes(f1), "/third/Launcher"), msg);
            Check("no Steam: said, not made up", SteamShortcuts.AddTo(null, "HPEngine", exe, "/", () => false).StartsWith("Steam was not found"));
            fired.Add("steam:damaged");
            msg = SteamShortcuts.AddTo(root, "HPEngine", exe, "/home/u/HP", () => throw new UnauthorizedAccessException("no access to /proc"));
            Check("the Steam process check denied: a message, not an exception", msg.StartsWith("Nothing was added: Steam's folders could not be read") && msg.Contains("/proc"), msg);
            var locked = Path.Combine(root, "userdata");
            File.SetUnixFileMode(locked, UnixFileMode.None);
            try { msg = SteamShortcuts.AddTo(root, "HPEngine", exe, "/home/u/HP", () => false); }
            finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            Check("an unreadable userdata folder: a message, not an exception", msg.StartsWith("Nothing was added: Steam's folders could not be read"), msg);
            fired.Add("steam:denied");
        }
        finally { Directory.Delete(tmp, true); }
    }

    static int IndexOf(byte[] h, byte[] n, int from) { for (int i = from; i <= h.Length - n.Length; i++) if (h.AsSpan(i, n.Length).SequenceEqual(n)) return i; return -1; }

    static void TitleCases()
    {
        Eq("title: US 1.53 -> Sorcerer's Stone", "Harry Potter and the Sorcerer's Stone", GameTitle.For("US 1.53: exact"));
        Eq("title: US 1.53 modified -> Sorcerer's Stone", "Harry Potter and the Sorcerer's Stone", GameTitle.For("US 1.53: modified"));
        Eq("title: UK 1.1 -> Philosopher's Stone", "Harry Potter and the Philosopher's Stone", GameTitle.For("UK 1.1: supported"));
        Eq("title: RU EA keeps today's title", "Harry Potter and the Philosopher's Stone", GameTitle.For("RU EA: supported"));
        Eq("title: City re-release (US 1.53 DLLs) -> Sorcerer's Stone", "Harry Potter and the Sorcerer's Stone", GameTitle.For("City re-release: modified"));
        Eq("title: Modded re-release (US 1.53 DLLs) -> Sorcerer's Stone", "Harry Potter and the Sorcerer's Stone", GameTitle.For("Modded re-release: modified"));
        Eq("title: Modified release (DLL set not in the text) -> neutral", "Harry Potter", GameTitle.For("Modified release: modified"));
        Eq("title: Unknown release -> neutral", "Harry Potter", GameTitle.For("Unknown release: unverified"));
        Eq("title: no game yet -> neutral", "Harry Potter", GameTitle.For(null));
        fired.Add("title:rules");
        var tmp = Path.Combine(Path.GetTempPath(), "hp1-title-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        try
        {
            string Title(string marker)
            {
                var g = Path.Combine(tmp, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(g);
                if (marker != null) File.WriteAllText(Path.Combine(g, ".hp1-installed"), "data=x\nedition=" + marker + "\n");
                var w = new MainWindow(g, tmp, "0.1.0-beta", false); w.Show(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var t = Find<Avalonia.Controls.TextBlock>(w, "Title").Text; w.Close(); return t;
            }
            Eq("window: UK game -> Philosopher's Stone", "Harry Potter and the Philosopher's Stone", Title("UK 1.1: exact"));
            Eq("window: US game -> Sorcerer's Stone", "Harry Potter and the Sorcerer's Stone", Title("US 1.53: exact"));
            Eq("window: RU EA game -> unchanged title", "Harry Potter and the Philosopher's Stone", Title("RU EA: exact"));
            Eq("window: no game installed -> neutral", "Harry Potter", Title(null));
            fired.Add("title:window");

            // Install update fails on a URL HttpClient refuses (InvalidOperationException, not an HttpRequestException): the task
            // does not fault, the player sees the error, the stage is removed.
            var ug = Path.Combine(tmp, "upd"); Directory.CreateDirectory(Path.Combine(ug, SelfUpdate.StageName));
            var uw = new MainWindow(ug, ug, "0.1.0-beta", false); uw.Show(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            typeof(MainWindow).GetField("_newer", flags).SetValue(uw, new Release("9.9.9", "p", "not a url", "not a url"));
            var task = (Task)typeof(MainWindow).GetMethod("InstallUpdate", flags).Invoke(uw, null);
            while (!task.IsCompleted) Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var shown = Find<Avalonia.Controls.TextBlock>(uw, "ErrorText").Text;
            Check("update: a refused URL is shown to the player, the task does not fault, the stage is removed",
                !task.IsFaulted && shown?.StartsWith("Could not update") == true && !Directory.Exists(Path.Combine(ug, SelfUpdate.StageName)), task.Exception?.InnerException?.GetType().Name + " " + shown);
            uw.Close();
            fired.Add("upd:install-error");
        }
        finally { Directory.Delete(tmp, true); }
    }
}
