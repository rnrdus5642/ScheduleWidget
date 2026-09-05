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
        public string MonitorId { get; set; }
        public Dictionary<string, MonitorStateData> MonitorStates { get; set; } =
            new Dictionary<string, MonitorStateData>();
    }

    public class MonitorStateData
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
        public AppearanceSettings Appearance { get; set; } = new AppearanceSettings();
        public bool StartupEnabled { get; set; } = true;
    }

    // 모니터 선택 UI에서 사용하는 런타임 표시 모델입니다. 저장 파일에는 포함되지 않습니다.
    public sealed class MonitorOption
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Detail { get; set; }
        public string StatusText { get; set; }
    }

    public class ScheduleItem
    {
        // 일정의 제목·날짜가 같아도 서로 다른 항목으로 식별할 수 있도록 사용합니다.
        public Guid Id { get; set; } = Guid.NewGuid();
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
