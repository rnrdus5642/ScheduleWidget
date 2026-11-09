using System;
using System.Collections.Generic;

namespace ScheduleWidget
{
    // Keep live schedules (and therefore Google sync/reminders) free of rejected drafts.
    // Save callbacks must not enter a modal message loop before rollback; callers report errors afterwards.
    internal static class ScheduleChanges
    {
        private static bool Commit(Action apply, Action undo, Func<bool> save)
        {
            bool saved = false;
            try { apply(); saved = save == null || save(); return saved; }
            finally { if (!saved) undo(); }
        }

        internal static bool Add(IList<ScheduleItem> schedules, ScheduleItem item, Func<bool> save) =>
            Commit(() => schedules.Add(item), () => schedules.Remove(item), save);

        internal static bool Remove(IList<ScheduleItem> schedules, ScheduleItem item, Func<bool> save)
        {
            int index = schedules.IndexOf(item);
            if (index < 0) return false;
            return Commit(() => schedules.Remove(item), () =>
            {
                if (!schedules.Contains(item)) schedules.Insert(Math.Min(index, schedules.Count), item);
            }, save);
        }

        internal static bool Update(ScheduleItem item, Action apply, Func<bool> save)
        {
            string title = item.Title, period = item.Period, end = item.EndPeriod, time = item.Time, color = item.Color;
            bool complete = item.IsCompleted;
            return Commit(apply, () =>
            {
                item.Title = title; item.Period = period; item.EndPeriod = end; item.Time = time;
                item.IsCompleted = complete; item.Color = color;
            }, save);
        }
    }
}
