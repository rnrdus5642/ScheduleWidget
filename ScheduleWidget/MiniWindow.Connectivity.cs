using System;
using System.Windows;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        private IDisposable connectivityWatch;
        private int connectivityGeneration;
        internal Func<Action<bool?>, IDisposable> connectivitySubscribe = InternetConnectionMonitor.Subscribe;

        private void StartConnectivityWatch()
        {
            if (closed || companion || appearancePreviewOnly || connectivityWatch != null) return;
            int generation = ++connectivityGeneration;
            connectivityWatch = connectivitySubscribe(online =>
            {
                if (closed || generation != connectivityGeneration || Dispatcher.HasShutdownStarted) return;
                Action apply = () =>
                {
                    if (!closed && generation == connectivityGeneration)
                        OfflineIndicator.Visibility = online == false ? Visibility.Visible : Visibility.Collapsed;
                };
                if (Dispatcher.CheckAccess()) apply();
                else Dispatcher.BeginInvoke(apply);
            });
        }

        private void StopConnectivityWatch()
        {
            ++connectivityGeneration;
            connectivityWatch?.Dispose();
            connectivityWatch = null;
        }
    }
}
