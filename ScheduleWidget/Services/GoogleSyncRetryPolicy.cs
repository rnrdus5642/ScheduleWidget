using System;

namespace ScheduleWidget
{
    // Shared scheduling rules for Calendar and Drive. Persisted quota deadlines survive
    // restarts and cannot be bypassed by manual sync, signing in or toggling a feature.
    public static class GoogleSyncRetryPolicy
    {
        private static double Sample(double value) => double.IsNaN(value) ? 0 : Math.Max(0, Math.Min(1, value));

        public static int NextFailure(int failures) => Math.Min(9, Math.Max(0, failures)) + 1;
        public static TimeSpan StartupDelay(double sample) => TimeSpan.FromSeconds(4 + 16 * Sample(sample));
        public static TimeSpan PeriodicDelay(double sample) => TimeSpan.FromSeconds(270 + 60 * Sample(sample));

        public static TimeSpan Backoff(int failures, bool rateLimited, bool dailyLimit, TimeSpan? retryAfter, double sample)
        {
            sample = Sample(sample);
            int attempt = Math.Max(1, Math.Min(10, failures));
            double seconds = rateLimited ? Math.Min(900, 60 * Math.Pow(2, attempt - 1))
                : attempt == 1 ? 30 : attempt == 2 ? 120 : 300;
            if (dailyLimit) seconds = Math.Max(3600, seconds);
            TimeSpan delay = TimeSpan.FromSeconds(seconds * (1 + 0.25 * sample));
            if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
            {
                // Positive jitter after Google's requested delay; never shorten that delay.
                long extra = TimeSpan.FromSeconds(15 * sample).Ticks;
                long ticks = retryAfter.Value.Ticks;
                TimeSpan server = TimeSpan.FromTicks(ticks > TimeSpan.MaxValue.Ticks - extra ? TimeSpan.MaxValue.Ticks : ticks + extra);
                if (server > delay) delay = server;
            }
            return delay;
        }

        public static DateTime AddDelay(DateTime utcNow, TimeSpan delay)
        {
            long ticks = Math.Max(0, delay.Ticks);
            return new DateTime(utcNow.Ticks + Math.Min(ticks, DateTime.MaxValue.Ticks - utcNow.Ticks), DateTimeKind.Utc);
        }

        public static DateTime QuotaDeadline(GoogleCalendarSettings settings)
        {
            DateTime value = settings?.RateLimitRetryAtUtc ?? DateTime.MinValue;
            return value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        public static DateTime Deadline(DateTime transient, GoogleCalendarSettings settings)
        {
            DateTime quota = QuotaDeadline(settings);
            return quota > transient ? quota : transient;
        }

        // DispatcherTimer has a bounded native interval. Long Retry-After values keep their
        // absolute deadline; a timer wake-up only rechecks it and does not send an early request.
        public static TimeSpan TimerInterval(TimeSpan remaining) =>
            TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(TimeSpan.FromHours(1).TotalMilliseconds, remaining.TotalMilliseconds)));
    }
}
