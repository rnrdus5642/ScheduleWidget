using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;

namespace ScheduleWidget
{
    public class WindowStateData
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public class AppData
    {
        public WindowStateData WindowState { get; set; } = new WindowStateData();
        public List<ScheduleItem> Schedules { get; set; } = new List<ScheduleItem>();
    }

    public class ScheduleItem
    {
        public string Title { get; set; }
        public string Period { get; set; }

        [JsonIgnore]
        public int RemainingDays
        {
            get
            {
                if (DateTime.TryParse(Period, out DateTime date))
                    return (date - DateTime.Today).Days;
                return int.MaxValue;
            }
        }

        [JsonIgnore]
        public string DDay
        {
            get
            {
                int diff = RemainingDays;
                if (diff == 0) return "D-day";
                else if (diff > 0) return $"D-{diff}";
                else return $"D+{Math.Abs(diff)}";
            }
        }

        [JsonIgnore]
        public string Status
        {
            get
            {
                if (RemainingDays == 0) return "Today";
                else if (RemainingDays > 0) return "Future";
                else return "Past";
            }
        }

        [JsonIgnore]
        public string DateString
        {
            get
            {
                if (DateTime.TryParseExact(Period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                    return date.ToString("MM.dd (ddd)", new CultureInfo("ko-KR"));
                return string.Empty;
            }
        }
    }
}