using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace ScheduleWidget.Updater
{
    /// <summary>
    /// Applies a downloaded, already verified ScheduleWidget update (no window).
    ///   ScheduleWidget.Updater.exe --pid &lt;app pid&gt; --app "&lt;app dir&gt;" --staging "&lt;dir&gt;" [--exe ScheduleWidget.exe] [--log "&lt;file&gt;"] [--wait 60]
    /// Waits for the app to exit, backs up every app file the update will overwrite, copies the staging folder over the app
    /// folder (retrying locked files), then starts the new exe. Any failure restores the backup and restarts the old exe.
    /// Files in the app folder that the update does not contain are never touched; %LocalAppData% is never touched.
    /// Exit codes: 0 updated, 1 failed and rolled back, 2 app did not exit (nothing changed), 3 bad arguments, 4 rollback failed.
    /// </summary>
    internal static class Program
    {
        private static string _logPath;
        private const int LockRetryMs = 500;
        private const int LockRetryCount = 40;          // ~20 s per file
        private const int StartupWatchMs = 5000;        // a new exe that dies with an error inside this window counts as a failure

        private static int Main(string[] args)
        {
            var opts = ParseArgs(args);
            string appDir = Get(opts, "app"), staging = Get(opts, "staging"), exeName = Get(opts, "exe") ?? "ScheduleWidget.exe";
            _logPath = Get(opts, "log");
            if (string.IsNullOrEmpty(_logPath))
            {
                try { _logPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(staging.TrimEnd('\\', '/'))), "updater-log.txt"); }
                catch { _logPath = Path.Combine(Path.GetTempPath(), "ScheduleWidget-updater-log.txt"); }
            }
            Log("---- updater start: " + string.Join(" ", args.Select(a => a.Contains(" ") ? "\"" + a + "\"" : a)));

            try
            {
                if (string.IsNullOrEmpty(appDir) || string.IsNullOrEmpty(staging)) { Log("missing --app or --staging"); return 3; }
                appDir = Path.GetFullPath(appDir.TrimEnd('\\', '/'));
                staging = Path.GetFullPath(staging.TrimEnd('\\', '/'));
                if (exeName.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) { Log("bad --exe"); return 3; }
                if (!Directory.Exists(appDir)) { Log("app dir not found: " + appDir); return 3; }
                if (!File.Exists(Path.Combine(staging, exeName))) { Log("staging has no " + exeName + ": " + staging); return 3; }
                if (string.Equals(appDir, staging, StringComparison.OrdinalIgnoreCase)) { Log("app dir == staging"); return 3; }
            }
            catch (Exception ex) { Log("bad arguments: " + ex.Message); return 3; }

            // One updater at a time: a second one (another install started meanwhile) waits, then gives up without touching files.
            using (var gate = new Mutex(false, @"Local\ScheduleWidget.Updater"))
            {
                bool owned;
                try { owned = gate.WaitOne(TimeSpan.FromSeconds(120)); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) { Log("another updater is still running; nothing changed"); return 2; }
                try { return Run(opts, appDir, staging, exeName); }
                finally { gate.ReleaseMutex(); }
            }
        }

        private static int Run(Dictionary<string, string> opts, string appDir, string staging, string exeName)
        {
            int waitSec;
            if (!int.TryParse(Get(opts, "wait") ?? "60", NumberStyles.Integer, CultureInfo.InvariantCulture, out waitSec) || waitSec <= 0) waitSec = 60;
            int pid;
            if (int.TryParse(Get(opts, "pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) && pid > 0 && !WaitForExit(pid, waitSec))
            {
                Log("app (pid " + pid + ") did not exit within " + waitSec + " s; nothing changed");
                return 2;
            }

            string appExe = Path.Combine(appDir, exeName);
            string oldVersion = ReadVersion(appExe);
            string newVersion = ReadVersion(Path.Combine(staging, exeName));
            Log("app " + appDir + "  old " + oldVersion + " -> new " + newVersion);

            var tx = new UpdateTransaction(appDir, staging);
            try
            {
                tx.Backup(ChooseBackupDir(appDir, staging, oldVersion));
                tx.Apply();
                Log("files copied: " + tx.Written.Count);
                StartAndWatch(appExe, appDir);
                Log("started new " + exeName + " (" + newVersion + ")");
                CleanOldBackups(appDir, staging, tx.BackupDir);
                CleanStaging(staging);
                Log("---- update OK");
                return 0;
            }
            catch (Exception ex)
            {
                Log("update FAILED: " + ex.GetType().Name + ": " + ex.Message);
                bool restored = tx.Rollback();
                Log(restored ? "rolled back to " + oldVersion : "rollback INCOMPLETE; backup kept at " + tx.BackupDir);
                try
                {
                    if (NoStart) Log("restart of old exe skipped (" + NoStartVariable + ")");
                    else if (File.Exists(appExe)) { Process.Start(new ProcessStartInfo(appExe) { WorkingDirectory = appDir, UseShellExecute = false }); Log("restarted old " + exeName); }
                }
                catch (Exception ex2) { Log("restart of old exe failed: " + ex2.Message); }
                return restored ? 1 : 4;
            }
        }

        private sealed class UpdateTransaction
        {
            private readonly string _app, _staging;
            private readonly List<string> _backedUp = new List<string>();   // relative paths copied into BackupDir
            public readonly List<string> Written = new List<string>();      // relative paths overwritten or created in the app dir
            private readonly HashSet<string> _created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> _createdDirs = new List<string>();
            public string BackupDir;

            public UpdateTransaction(string app, string staging) { _app = app; _staging = staging; }

            private IEnumerable<string> StagingFiles()
            {
                // The main exe goes last so a half-applied update never has a new exe with old libraries.
                var all = Directory.GetFiles(_staging, "*", SearchOption.AllDirectories).Select(f => f.Substring(_staging.Length).TrimStart('\\', '/')).ToList();
                return all.Where(r => r.IndexOf('\\') >= 0 || !r.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                          .Concat(all.Where(r => r.IndexOf('\\') < 0 && r.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
            }

            public void Backup(string backupDir)
            {
                BackupDir = backupDir;
                if (Directory.Exists(backupDir) || File.Exists(backupDir))
                    throw new IOException("backup destination already exists: " + backupDir);
                Directory.CreateDirectory(backupDir);
                try
                {
                    foreach (var rel in StagingFiles())
                    {
                        string src = Path.Combine(_app, rel);
                        if (!File.Exists(src)) continue;
                        string dst = Path.Combine(backupDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        Retry(() => File.Copy(src, dst, true), "backup " + rel);
                        _backedUp.Add(rel);
                    }
                }
                catch
                {
                    // This directory is unique to this attempt. Remove only its incomplete copy; older backups survive.
                    try { Directory.Delete(backupDir, true); } catch { }
                    throw;
                }
                Log("backup: " + _backedUp.Count + " files -> " + backupDir);
            }

            public void Apply()
            {
                string self = SelfPath();
                foreach (var rel in StagingFiles())
                {
                    string src = Path.Combine(_staging, rel), dst = Path.Combine(_app, rel);
                    if (string.Equals(Path.GetFullPath(dst), self, StringComparison.OrdinalIgnoreCase)) { Log("skip (running updater itself): " + rel); continue; }
                    string dir = Path.GetDirectoryName(dst);
                    if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); _createdDirs.Add(dir); }
                    bool existed = File.Exists(dst);
                    Retry(() => File.Copy(src, dst, true), "copy " + rel);
                    if (!existed) _created.Add(rel);
                    Written.Add(rel);
                }
            }

            public bool Rollback()
            {
                bool ok = true;
                foreach (var rel in Written)
                {
                    if (!_created.Contains(rel)) continue;
                    try { Retry(() => File.Delete(Path.Combine(_app, rel)), "remove new " + rel); } catch (Exception ex) { ok = false; Log("rollback delete failed " + rel + ": " + ex.Message); }
                }
                var written = new HashSet<string>(Written, StringComparer.OrdinalIgnoreCase);
                foreach (var rel in _backedUp)
                {
                    if (!written.Contains(rel)) continue;   // never overwritten (e.g. the locked file that stopped the update)
                    try { Retry(() => File.Copy(Path.Combine(BackupDir, rel), Path.Combine(_app, rel), true), "restore " + rel); }
                    catch (Exception ex) { ok = false; Log("rollback restore failed " + rel + ": " + ex.Message); }
                }
                foreach (var dir in Enumerable.Reverse(_createdDirs))
                {
                    try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
                }
                return ok;
            }
        }

        // Isolated end-to-end tests set this so the updated app is not launched against the real user data.
        private const string NoStartVariable = "SCHEDULEWIDGET_UPDATER_NO_START";
        private static bool NoStart => Environment.GetEnvironmentVariable(NoStartVariable) == "1";

        private static void StartAndWatch(string exe, string workDir)
        {
            if (NoStart) { Log("start of new exe skipped (" + NoStartVariable + ")"); return; }
            var p = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = workDir, UseShellExecute = false });
            if (p == null) throw new InvalidOperationException("new exe did not start");
            if (p.WaitForExit(StartupWatchMs) && p.ExitCode != 0)
                throw new InvalidOperationException("new exe exited with code " + p.ExitCode + " right after start");
        }

        private static string ChooseBackupDir(string appDir, string staging, string oldVersion)
        {
            string safe = new string((oldVersion ?? "old").Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_').ToArray());
            string suffix = safe + "-" + Guid.NewGuid().ToString("N");
            string sibling = appDir + ".backup-" + suffix;
            string probe = null;
            try
            {
                if (Directory.Exists(sibling) || File.Exists(sibling)) throw new IOException("backup destination already exists");
                probe = Path.Combine(Path.GetDirectoryName(appDir), ".schedulewidget-backup-probe-" + Guid.NewGuid().ToString("N"));
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                File.Delete(probe);
                return sibling;
            }
            catch
            {
                if (probe != null) try { File.Delete(probe); } catch { }
                // App folder's parent not writable (e.g. Program Files): put it beside the outer staging folder.
                return Path.Combine(FallbackBackupRoot(staging), "backup-" + suffix);
            }
        }

        private static string FindStagingRoot(string staging)
        {
            string dir = Path.GetFullPath(staging.TrimEnd('\\', '/'));
            if (Path.GetFileName(dir).StartsWith("staging-", StringComparison.OrdinalIgnoreCase)) return dir;
            string parent = Path.GetDirectoryName(dir);
            return !string.IsNullOrEmpty(parent) && Path.GetFileName(parent).StartsWith("staging-", StringComparison.OrdinalIgnoreCase)
                ? parent : null;
        }

        private static string FallbackBackupRoot(string staging)
        {
            string stagingRoot = FindStagingRoot(staging);
            return stagingRoot == null ? Path.GetDirectoryName(Path.GetFullPath(staging)) : Path.GetDirectoryName(stagingRoot);
        }

        // The unpacked package is no longer needed once the new version runs (only a folder the client named staging-*).
        private static void CleanStaging(string staging)
        {
            try
            {
                string dir = FindStagingRoot(staging);
                if (dir == null) return;
                if (SelfPath().StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
                Directory.Delete(dir, true);
                Log("removed staging " + dir);
            }
            catch (Exception ex) { Log("staging cleanup skipped: " + ex.Message); }
        }

        private static void CleanOldBackups(string appDir, string staging, string keep)
        {
            try
            {
                string parent = Path.GetDirectoryName(appDir), name = Path.GetFileName(appDir) + ".backup-";
                foreach (var d in Directory.GetDirectories(parent, name + "*"))
                {
                    if (string.Equals(d, keep, StringComparison.OrdinalIgnoreCase)) continue;
                    try { Directory.Delete(d, true); Log("removed old backup " + d); } catch { }
                }

                // Fallback backups live only in the updater's dedicated staging root. Limit cleanup to its
                // updater-generated backup-* entries so adjacent user data is never traversed.
                string stagingRoot = FindStagingRoot(staging);
                string fallbackRoot = stagingRoot == null ? null : FallbackBackupRoot(staging);
                if (fallbackRoot == null) return;
                foreach (var d in Directory.GetDirectories(fallbackRoot, "backup-*"))
                {
                    string backupName = Path.GetFileName(d);
                    if (!System.Text.RegularExpressions.Regex.IsMatch(backupName, @"\Abackup-(?:[0-9]+(?:\.[0-9]+){1,3}|none|unknown)(?:-[0-9a-f]{32})?\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                    if (string.Equals(Path.GetFullPath(d), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                    try { Directory.Delete(d, true); Log("removed old fallback backup " + d); } catch { }
                }
            }
            catch { }
        }

        private static bool WaitForExit(int pid, int seconds)
        {
            Process p;
            try { p = Process.GetProcessById(pid); }
            catch (ArgumentException) { return true; }   // already gone
            using (p)
            {
                try { return p.WaitForExit(seconds * 1000); }
                catch (Exception ex) { Log("wait error: " + ex.Message); return false; }
            }
        }

        private static void Retry(Action action, string what)
        {
            for (int i = 1; ; i++)
            {
                try { action(); return; }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && i < LockRetryCount)
                {
                    if (i == 1) Log(what + ": " + ex.Message + " (retrying)");
                    Thread.Sleep(LockRetryMs);
                }
            }
        }

        private static string ReadVersion(string exe)
        {
            try
            {
                if (!File.Exists(exe)) return "none";
                var v = FileVersionInfo.GetVersionInfo(exe);
                return string.IsNullOrWhiteSpace(v.ProductVersion) ? (v.FileVersion ?? "unknown") : v.ProductVersion.Trim();
            }
            catch { return "unknown"; }
        }

        private static string SelfPath()
        {
            try { return Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName); } catch { return ""; }
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) continue;
                string key = args[i].Substring(2);
                d[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
            }
            return d;
        }

        private static string Get(Dictionary<string, string> d, string key)
        {
            string v;
            return d.TryGetValue(key, out v) && v.Length > 0 ? v : null;
        }

        private static void Log(string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath));
                File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
            }
            catch { }
        }
    }
}
