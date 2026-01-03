using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;

namespace ScheduleWidget
{
    // 창 위치와 크기 저장용
    public class WindowStateData
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    // 전체 JSON 데이터
    public class AppData
    {
        public WindowStateData WindowState { get; set; } = new WindowStateData();
        public List<ScheduleItem> Schedules { get; set; } = new List<ScheduleItem>();
    }

    public class ScheduleItem
    {
        public string Title { get; set; }      // 일정 제목
        public string Period { get; set; }     // 일정 날짜 (yyyy-MM-dd 형식)

        [JsonIgnore] // JSON 저장할 때는 제외
        public int RemainingDays
        {
            get
            {
                if (DateTime.TryParse(Period, out DateTime date))
                {
                    return (date - DateTime.Today).Days;
                }
                return int.MaxValue; // 파싱 실패 시 맨 뒤로 보내기
            }
        }

        [JsonIgnore]
        public string DDay
        {
            get
            {
                int diff = RemainingDays;

                if (diff == 0) return "D-day";          // 오늘
                else if (diff > 0) return $"D-{diff}";  // 미래
                else return $"D+{Math.Abs(diff)}";      // 지난 일정
            }
        }

        [JsonIgnore]
        public string Status
        {
            get
            {
                if (RemainingDays == 0) return "Today";      // 오늘
                else if (RemainingDays > 0) return "Future"; // 미래
                else return "Past";                          // 지난 일정
            }
        }

        [JsonIgnore]
        public string DateString
        {
            get
            {
                // Period가 null 또는 잘못된 형식이면 예외 방지
                if (DateTime.TryParseExact(Period, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                {
                    // 원하는 형식으로 반환
                    return date.ToString("MM.dd (ddd)", new CultureInfo("ko-KR"));
                }
                return string.Empty;
            }
        }

    }
}
