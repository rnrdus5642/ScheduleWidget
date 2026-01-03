using System;
using System.Threading;
using System.Windows;

namespace ScheduleWidget
{
    /// <summary>
    /// App.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class App : Application
    {
        // 앱이 실행되는 동안 유지되어야 하므로 필드로 선언합니다.
        private static Mutex _mutex = null;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 앱 고유의 이름 (다른 앱과 겹치지 않도록 설정)
            const string mutexName = "ScheduleWidget_Unique_Mutex_5642";
            bool createdNew;

            // Mutex를 사용하여 현재 이 이름으로 실행 중인 프로세스가 있는지 확인합니다.
            _mutex = new Mutex(true, mutexName, out createdNew);

            if (!createdNew)
            {
                // 이미 실행 중인 경우 경고창을 띄우고 즉시 종료합니다.
                MessageBox.Show("프로그램이 이미 실행 중입니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);

                Application.Current.Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 앱이 종료될 때 Mutex 자원을 해제합니다.
            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
            base.OnExit(e);
        }
    }
}