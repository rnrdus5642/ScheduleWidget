using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ScheduleWidget
{
    /// <summary>
    /// App.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class App : Application
    {
        // 앱이 실행되는 동안 유지되어야 하므로 필드로 선언합니다.
        private static Mutex _mutex = null;
        private static bool _ownsMutex;

        // 앱 고유의 이름. Windows 사용자(SID)마다 따로라서, 같은 세션의 다른 사용자(다른 사용자로 실행)와 겹치지 않습니다.
        private const string MutexBaseName = "ScheduleWidget_Unique_Mutex_5642";

        public static string SingleInstanceMutexName()
        {
            string sid = null;
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                    sid = identity.User?.Value;
            }
            catch (System.Security.SecurityException) { }
            return string.IsNullOrEmpty(sid) ? MutexBaseName : MutexBaseName + "_" + sid;
        }

        // The name older versions use (fixed, no SID). This app holds it too, for as long as it runs: an older build started
        // meanwhile finds it taken and refuses to start (two copies would write over each other's schedules.json), and one
        // already running makes this one refuse. Any other program could still take this name first and keep the app from
        // starting — that can't be prevented while older builds must be kept out.
        private static Mutex _legacyMutex; // static: the handle (and so the name) stays until OnExit, never lost to the GC
        internal static string LegacyMutexName = MutexBaseName; // checks use a name of their own

        // True when the old name is now held by this app, or can't be ours to take (another Windows user's, or not a mutex):
        // start. False when an older version (or anything else holding that name) runs.
        internal static bool HoldLegacyName()
        {
            try
            {
                var legacy = new Mutex(false, LegacyMutexName, out bool createdNew);
                if (!createdNew)
                {
                    legacy.Dispose();
                    return false;
                }
                _legacyMutex = legacy;
                return true;
            }
            catch (UnauthorizedAccessException) { return true; } // another Windows user's copy
            catch (WaitHandleCannotBeOpenedException) { return true; }
        }

        internal static void ReleaseLegacyName()
        {
            _legacyMutex?.Dispose();
            _legacyMutex = null;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;

            // Mutex를 사용하여 현재 이 이름으로 실행 중인 프로세스가 있는지 확인합니다.
            try
            {
                _mutex = new Mutex(true, SingleInstanceMutexName(), out createdNew);
            }
            catch (UnauthorizedAccessException) { createdNew = false; } // the name is held by someone we may not open
            _ownsMutex = createdNew;

            if (!createdNew || !HoldLegacyName())
            {
                // 이미 실행 중인 경우 경고창을 띄우고 즉시 종료합니다.
                MessageBox.Show("프로그램이 이미 실행 중입니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);

                Application.Current.Shutdown();
                return;
            }

            // Anything unexpected is written to error-log.txt. Once the TODO window has finished loading the app keeps running
            // (only the action that failed is lost); an error while starting up closes it with a message.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, args) => ErrorLog.Write("background", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, args) => { ErrorLog.Write("task", args.Exception); args.SetObserved(); };

            UseAppIconForAllWindows();
            // Characters an older version kept in the app folder's Pet are copied into %LocalAppData%/ScheduleWidget/Pet
            // (then the old copies are deleted); the Google Drive backup, when on, uploads them right after the app opens.
            CharacterCatalog.MigrateLegacyPets();
            // The Codex characters are no longer shipped: an older version's app-folder Characters goes, and they are copied
            // from the installed Codex instead (removed when Codex is gone) — both in the background.
            System.Threading.Tasks.Task.Run(() => DeleteBundledCharacters());
            CodexPets.RefreshInBackground();
            SpriteSharpener.PruneInBackground(); // sharper sheets of pets deleted or changed since (on its low-priority thread)
            EnableSliderMouseWheel();
            base.OnStartup(e);
        }

        /// <summary>
        /// Deletes the Characters folder an older version shipped next to the exe (the bundled Codex characters) — only that
        /// exact folder, never through a link. Best effort: a file in use stays until the next start.
        /// </summary>
        public static void DeleteBundledCharacters(string appDirectory = null)
        {
            try
            {
                string folder = System.IO.Path.Combine(appDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "Characters");
                if (!System.IO.Directory.Exists(folder)) return;
                if ((System.IO.File.GetAttributes(folder) & System.IO.FileAttributes.ReparsePoint) != 0) return;
                System.IO.Directory.Delete(folder, true);
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }
        }

        // The mouse wheel moves only the 음량 (VolumeSlider) and 재생 위치 (SeekSlider) bars while the pointer is over them — no
        // click needed; one notch = the slider's tick step or small change. Every other bar (캐릭터 크기·위치, 투명도, 글꼴 크기 …)
        // ignores the wheel so scrolling past it never changes a setting by accident; the wheel scrolls the page instead.
        public static void EnableSliderMouseWheel()
        {
            EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Slider), UIElement.PreviewMouseWheelEvent,
                new System.Windows.Input.MouseWheelEventHandler((sender, args) =>
                {
                    if (!(sender is System.Windows.Controls.Slider slider) || !slider.IsEnabled || args.Delta == 0) return;
                    if (slider.Name != "VolumeSlider" && slider.Name != "SeekSlider") return; // not handled: bubbles to the ScrollViewer
                    double step = slider.IsSnapToTickEnabled && slider.TickFrequency > 0 ? slider.TickFrequency
                        : slider.SmallChange > 0 ? slider.SmallChange : (slider.Maximum - slider.Minimum) / 20;
                    double next = slider.Value + Math.Sign(args.Delta) * step;
                    slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, next));
                    args.Handled = true;
                }));
        }

        private int recentErrors;
        private DateTime recentErrorsSince = DateTime.MinValue;
        private bool closingForErrors;

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            if (closingForErrors) return;
            ErrorLog.Write("ui", e.Exception);
            // A dead WPF render thread cannot recover by handling the next twenty window messages. Close once;
            // the error dialog's message pump must not flood the log while the user is reading the message.
            bool renderFailure = IsRenderThreadFailure(e.Exception);
            // Keep going after startup, unless errors keep coming (the same failure every frame): then stop instead of looping.
            if ((DateTime.Now - recentErrorsSince).TotalMinutes >= 1) { recentErrorsSince = DateTime.Now; recentErrors = 0; }
            bool running = this.MainWindow is ScheduleWidget.MainWindow main && main.StartupCompleted;
            if (!renderFailure && running && ++recentErrors <= 20) return;
            closingForErrors = true;
            string message = renderFailure ? "화면을 그리는 중 오류가 발생해 앱을 종료합니다."
                : running ? "오류가 계속 발생해 앱을 종료합니다." : "앱을 시작하는 중 오류가 발생해 종료합니다.";
            MessageBox.Show(message + Environment.NewLine +
                e.Exception.Message + Environment.NewLine + Environment.NewLine + "자세한 내용: " + ErrorLog.FilePath,
                "일정 위젯", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }

        public static bool IsRenderThreadFailure(Exception error)
        {
            for (Exception current = error; current != null; current = current.InnerException)
                if (current is System.Runtime.InteropServices.COMException && current.HResult == unchecked((int)0x88980406)) return true;
            return false;
        }

        // Every window (연락·알림, 음악, 캐릭터 선택 등) shows the app's calendar icon instead of the default one.
        public static void UseAppIconForAllWindows()
        {
            var icon = TrayService.CreateWindowIcon();
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, args) =>
            {
                if (sender is Window window && window.Icon == null) window.Icon = icon;
                // Extra windows (설정, 캐릭터 설정·선택, 연락 · 알림, 음악) take the app theme's colors.
                if (sender is Window themed && AuxTheme.IsExtraWindow(themed)) AuxTheme.ApplyTo(themed);
            }));
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 앱이 종료될 때 Mutex 자원을 해제합니다.
            if (_mutex != null)
            {
                try
                {
                    if (_ownsMutex)
                    {
                        _mutex.ReleaseMutex();
                    }
                }
                finally
                {
                    _ownsMutex = false;
                    _mutex.Dispose();
                    _mutex = null;
                }
            }
            ReleaseLegacyName();
            base.OnExit(e);
        }
    }
}
