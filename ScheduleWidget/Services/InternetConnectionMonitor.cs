using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;

namespace ScheduleWidget
{
    // Windows' Internet connectivity state, rather than merely an available LAN adapter.
    // This observes the OS and makes no HTTP requests or changes to sync/retry policy.
    internal sealed class InternetConnectionMonitor : IDisposable
    {
        private readonly Action<bool?> changed;
        private readonly object gate = new object();
        private bool disposed, reading, requested, subscribed;
        private Timer fallback;

        private InternetConnectionMonitor(Action<bool?> changed)
        {
            this.changed = changed ?? throw new ArgumentNullException(nameof(changed));
            try
            {
                NetworkInformation.NetworkStatusChanged += NetworkChanged;
                subscribed = true;
            }
            catch (Exception error) when (IsStatusError(error))
            {
                // A notification subscription may be temporarily unavailable; still read local OS state.
                fallback = new Timer(_ => RequestRead(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            }
            RequestRead();
        }

        internal static IDisposable Subscribe(Action<bool?> changed) => new InternetConnectionMonitor(changed);

        internal static bool? Read()
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                return profile != null && profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
            catch (Exception error) when (IsStatusError(error)) { return null; } // unknown is not evidence of being offline
        }

        private static bool IsStatusError(Exception error) => error is COMException || error is InvalidOperationException ||
            error is UnauthorizedAccessException || error is TypeLoadException || error is NotSupportedException;

        private void NetworkChanged(object sender) => RequestRead();

        private void RequestRead()
        {
            lock (gate)
            {
                if (disposed) return;
                requested = true;
                if (reading) return;
                reading = true;
            }
            Task.Run(ReadChanges);
        }

        private void ReadChanges()
        {
            while (true)
            {
                lock (gate)
                {
                    if (disposed || !requested) { reading = false; return; }
                    requested = false;
                }
                bool? online = Read();
                lock (gate) { if (disposed) { reading = false; return; } }
                try { changed(online); }
                catch (Exception error) when (IsStatusError(error)) { } // the window's dispatcher may already be shutting down
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
            }
            fallback?.Dispose();
            if (subscribed)
            {
                try { NetworkInformation.NetworkStatusChanged -= NetworkChanged; }
                catch (Exception error) when (IsStatusError(error)) { }
            }
        }
    }
}
