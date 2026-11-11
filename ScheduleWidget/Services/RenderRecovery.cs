using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ScheduleWidget
{
    internal static class RenderRecovery
    {
        internal const string RecoveryArgument = "--recover-render";
        private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(5);
        private const int MaxRestarts = 2;
        private static string Executable => typeof(RenderRecovery).Assembly.Location;
        private static string HistoryPath => Path.Combine(ErrorLog.Root ?? AppProfile.DataDirectory, "render-restarts.txt");

        internal static bool TryReserveRestart(string historyPath, DateTime nowUtc)
        {
            string temporary = historyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(historyPath));
                var recent = (File.Exists(historyPath) ? File.ReadAllLines(historyPath) : new string[0])
                    .Select(line => long.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : 0)
                    .Where(ticks => ticks > 0 && ticks <= DateTime.MaxValue.Ticks && nowUtc.Ticks - ticks < RestartWindow.Ticks).ToList();
                if (recent.Count >= MaxRestarts) return false;
                recent.Add(nowUtc.Ticks);
                File.WriteAllLines(temporary, recent.Select(ticks => ticks.ToString(CultureInfo.InvariantCulture)));
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(historyPath)) File.Replace(temporary, historyPath, null);
                        else File.Move(temporary, historyPath);
                        break;
                    }
                    catch (IOException) when (attempt < 3 && File.Exists(temporary))
                    { System.Threading.Thread.Sleep(25 * (attempt + 1)); } // Brief scanner/backup locks must not disable recovery.
                }
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException)
            { ErrorLog.Write("render-recovery-history", error); return false; }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
            }
        }

        internal static ProcessStartInfo RecoveryStartInfo(string executable, int parentId, long parentStartUtc, bool restart)
        {
            return new ProcessStartInfo(executable, RecoveryArgument + " " + parentId.ToString(CultureInfo.InvariantCulture) + " " +
                parentStartUtc.ToString(CultureInfo.InvariantCulture) + (restart ? " restart" : " stop") +
                (AppProfile.IsDemo ? " " + AppProfile.DemoArgument : ""))
            {
                // No console is created. After the wait this process becomes the visible widget (or a native error notice).
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Normal,
                WorkingDirectory = Path.GetDirectoryName(executable)
            };
        }

        internal static bool TryStartRecovery()
        {
            bool restart = TryReserveRestart(HistoryPath, DateTime.UtcNow);
            try
            {
                using (var current = Process.GetCurrentProcess())
                using (var next = Process.Start(RecoveryStartInfo(Executable, current.Id, current.StartTime.ToUniversalTime().Ticks, restart)))
                {
                    if (next == null) return false;
                    PetLog.Write("render-recovery", restart ? "restart scheduled" : "restart limit reached; stop scheduled");
                    return true;
                }
            }
            catch (Exception error) when (error is Win32Exception || error is IOException || error is InvalidOperationException || error is UnauthorizedAccessException)
            { ErrorLog.Write("render-recovery-launch", error); return false; }
        }

        internal interface IParentProcess
        {
            string ExecutablePath { get; }
            long StartUtcTicks { get; }
            bool HasExited { get; }
            bool WaitForExit(int milliseconds);
            void Kill();
        }

        private sealed class ParentProcess : IParentProcess
        {
            private readonly Process process;
            internal ParentProcess(Process process) { this.process = process; }
            public string ExecutablePath => process.MainModule.FileName;
            public long StartUtcTicks => process.StartTime.ToUniversalTime().Ticks;
            public bool HasExited => process.HasExited;
            public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);
            public void Kill() => process.Kill();
        }

        internal static bool WaitForParent(IParentProcess parent, string executable, long startUtcTicks)
        {
            if (parent.HasExited) return true;
            // A reused PID or another program must never be terminated by this helper.
            if (!string.Equals(Path.GetFullPath(parent.ExecutablePath), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase) ||
                parent.StartUtcTicks != startUtcTicks) return false;
            if (parent.WaitForExit(6000) || parent.HasExited) return true;
            if (parent.StartUtcTicks != startUtcTicks) return false;
            parent.Kill();
            return parent.WaitForExit(2000);
        }

        internal static bool PrepareStartup(string[] args)
        {
            if (args == null || args.Length == 0 || args[0] != RecoveryArgument) return true;
            if (args.Length != 4 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parentId) || parentId <= 0 ||
                !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks ||
                (args[3] != "restart" && args[3] != "stop") || parentId == Process.GetCurrentProcess().Id) return false;
            try
            {
                Process parent = null;
                try { parent = Process.GetProcessById(parentId); }
                catch (ArgumentException) { } // The failing instance already exited after saving.
                if (parent != null)
                {
                    using (parent)
                    {
                        try { if (!WaitForParent(new ParentProcess(parent), Executable, ticks)) return false; }
                        catch (InvalidOperationException) { if (!parent.HasExited) throw; }
                    }
                }
                if (args[3] == "stop")
                {
                    // This is a fresh, native dialog in a separate process, never the broken WPF dispatcher.
                    System.Windows.Forms.MessageBox.Show("화면 처리 오류가 짧은 시간에 반복되어 위젯을 종료했습니다.\n잠시 뒤 다시 실행해 주세요.\n\n오류 기록: " + ErrorLog.FilePath,
                        "일정 위젯", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
                    return false;
                }
                PetLog.Write("render-recovered", "previous process exited; starting a new WPF instance");
                return true;
            }
            catch (Exception error) when (error is Win32Exception || error is InvalidOperationException || error is IOException || error is UnauthorizedAccessException)
            { ErrorLog.Write("render-recovery-wait", error); return false; }
        }
    }
}
