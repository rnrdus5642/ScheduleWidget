using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static void RenderRecoveryChecks()
        {
            Run("Render recovery persists a bounded restart budget without touching schedule data", () =>
            {
                string directory = Path.Combine(root, "render-recovery-limit");
                string history = Path.Combine(directory, "restarts.txt");
                DateTime now = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
                Require(RenderRecovery.TryReserveRestart(history, now), "The first restart was rejected.");
                Require(RenderRecovery.TryReserveRestart(history, now.AddSeconds(30)), "The second restart was rejected.");
                Require(!RenderRecovery.TryReserveRestart(history, now.AddMinutes(1)), "A rapid crash loop was allowed.");
                Require(File.ReadAllLines(history).Length == 2, "Rejecting recovery rewrote its history.");
                Require(RenderRecovery.TryReserveRestart(history, now.AddMinutes(6)), "Old failures permanently disabled recovery.");
                Require(File.ReadAllLines(history).Length == 1 && Directory.GetFiles(directory).Length == 1,
                    "The restart budget retained stale records or temporary files.");
                File.WriteAllText(history, "not-a-time\n9999999999999999999");
                Require(RenderRecovery.TryReserveRestart(history, now), "A malformed history prevents recovery.");
            });

            Run("Render recovery only terminates its exact failed widget and waits before restarting", () =>
            {
                string executable = Path.Combine(root, "Widget Folder", "ScheduleWidget.exe");
                var normal = new RecoveryProcessFixture(executable) { ExitsNormally = true };
                Require(RenderRecovery.WaitForParent(normal, executable, 123) && normal.Waits == 1 && normal.Kills == 0,
                    "Normal shutdown was terminated or not awaited.");
                var hung = new RecoveryProcessFixture(executable);
                Require(RenderRecovery.WaitForParent(hung, executable, 123) && hung.Kills == 1 && hung.Waits == 2,
                    "A hung widget did not exit before recovery continued.");
                var other = new RecoveryProcessFixture(Path.Combine(root, "Other.exe"));
                Require(!RenderRecovery.WaitForParent(other, executable, 123) && other.Kills == 0 && other.Waits == 0,
                    "Recovery touched another executable.");
                var reused = new RecoveryProcessFixture(executable) { StartUtcTicks = 456 };
                Require(!RenderRecovery.WaitForParent(reused, executable, 123) && reused.Kills == 0, "Recovery terminated a reused process ID.");
                var stubborn = new RecoveryProcessFixture(executable) { ExitOnKill = false };
                Require(!RenderRecovery.WaitForParent(stubborn, executable, 123), "Recovery could start a duplicate while the old process remains alive.");
            });

            Run("Render restart history tolerates a brief sharing lock without reserving twice", () =>
            {
                string history = Path.Combine(root, "render-history-lock", "restarts.txt");
                DateTime now = DateTime.UtcNow;
                Require(RenderRecovery.TryReserveRestart(history, now), "Initial restart reservation failed.");
                var held = new FileStream(history, FileMode.Open, FileAccess.Read, FileShare.Read);
                using (var release = new System.Threading.Timer(_ => held.Dispose(), null, 50, System.Threading.Timeout.Infinite))
                {
                    try { Require(RenderRecovery.TryReserveRestart(history, now.AddSeconds(1)), "A brief sharing lock disabled automatic recovery."); }
                    finally { held.Dispose(); }
                }
                Require(File.ReadAllLines(history).Length == 2, "Retry reserved a restart more than once.");
            });

            Run("Render recovery bootstrap rejects malformed requests and does not launch WPF while waiting", () =>
            {
                Require(RenderRecovery.PrepareStartup(new string[0]) && RenderRecovery.PrepareStartup(new[] { "--startup" }),
                    "Ordinary startup was intercepted.");
                foreach (var args in new[] {
                    new[] { RenderRecovery.RecoveryArgument },
                    new[] { RenderRecovery.RecoveryArgument, "0", "1", "restart" },
                    new[] { RenderRecovery.RecoveryArgument, "123", "-1", "restart" },
                    new[] { RenderRecovery.RecoveryArgument, "123", "1", "unknown" },
                    new[] { RenderRecovery.RecoveryArgument, Process.GetCurrentProcess().Id.ToString(), "1", "restart" }
                }) Require(!RenderRecovery.PrepareStartup(args), "An invalid recovery request was accepted.");
                string executable = Path.Combine(root, "Widget Folder", "ScheduleWidget.exe");
                var start = RenderRecovery.RecoveryStartInfo(executable, 123, 456, true);
                Require(start.FileName == executable && !start.UseShellExecute && start.CreateNoWindow &&
                    start.WorkingDirectory == Path.GetDirectoryName(executable) && start.Arguments.EndsWith("123 456 restart"),
                    "Recovery startup lost its process identity or requires a shell.");
                Require(RenderRecovery.RecoveryStartInfo(executable, 123, 456, false).Arguments.EndsWith(" stop"),
                    "Exhausting the restart budget would still restart the widget.");
                Require(typeof(App).Assembly.EntryPoint.DeclaringType.FullName == "ScheduleWidget.Program", "Recovery is not dispatched before WPF startup.");
            });
        }

        private sealed class RecoveryProcessFixture : RenderRecovery.IParentProcess
        {
            public string ExecutablePath { get; }
            public long StartUtcTicks { get; set; } = 123;
            public bool HasExited { get; private set; }
            internal bool ExitsNormally, ExitOnKill = true;
            internal int Waits, Kills;
            internal RecoveryProcessFixture(string path) { ExecutablePath = path; }
            public bool WaitForExit(int milliseconds) { Waits++; HasExited |= ExitsNormally; return HasExited; }
            public void Kill() { Kills++; HasExited = ExitOnKill; }
        }
    }
}
