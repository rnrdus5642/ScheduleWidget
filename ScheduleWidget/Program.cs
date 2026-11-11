using System;

namespace ScheduleWidget
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            args = AppProfile.Configure(args);
            // Recovery waits outside WPF, before any window, render thread, or single-instance mutex is created.
            if (!RenderRecovery.PrepareStartup(args)) return;
            var application = new App();
            application.InitializeComponent();
            application.Run();
        }
    }
}
