using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ScheduleWidget
{
    // 한국 관공서 공휴일 계산.
    // 양력 고정 공휴일, 음력 공휴일(설날·부처님오신날·추석, KoreanLunisolarCalendar로 변환), 대체공휴일 규칙을 계산하고
    // 계산할 수 없는 선거일·임시공휴일은 아래 표로 보충합니다. 표에 없는 새 임시공휴일은 추가해야 표시됩니다.
    public static class KoreanHolidays
    {
        private static readonly KoreanLunisolarCalendar Lunar = new KoreanLunisolarCalendar();
        private static readonly Dictionary<int, Dictionary<DateTime, string>> Cache = new Dictionary<int, Dictionary<DateTime, string>>();

        // 선거일·임시공휴일 (규칙으로 알 수 없는 날).
        private static readonly Dictionary<DateTime, string> Special = new Dictionary<DateTime, string>
        {
            { new DateTime(2020, 4, 15), "국회의원 선거" },
            { new DateTime(2020, 8, 17), "임시공휴일" },
            { new DateTime(2022, 3, 9), "대통령 선거" },
            { new DateTime(2022, 6, 1), "지방선거" },
            { new DateTime(2023, 10, 2), "임시공휴일" },
            { new DateTime(2024, 4, 10), "국회의원 선거" },
            { new DateTime(2024, 10, 1), "국군의 날" },
            { new DateTime(2025, 1, 27), "임시공휴일" },
            { new DateTime(2025, 6, 3), "대통령 선거" },
            { new DateTime(2026, 6, 3), "지방선거" },
            { new DateTime(2028, 4, 12), "국회의원 선거" }, // 제23대, 공직선거법으로 정해진 날
        };

        /// <summary>그 날의 공휴일 이름, 공휴일이 아니면 null.</summary>
        public static string NameOf(DateTime date)
        {
            date = date.Date;
            Dictionary<DateTime, string> year;
            lock (Cache)
            {
                if (!Cache.TryGetValue(date.Year, out year)) Cache[date.Year] = year = Build(date.Year);
            }
            return year.TryGetValue(date, out string name) ? name : null;
        }

        public static bool IsHoliday(DateTime date) => NameOf(date) != null;

        private static Dictionary<DateTime, string> Build(int year)
        {
            var names = new Dictionary<DateTime, List<string>>();
            void Add(DateTime? day, string name)
            {
                if (!day.HasValue || day.Value.Year != year) return;
                if (!names.TryGetValue(day.Value, out var list)) names[day.Value] = list = new List<string>();
                if (!list.Contains(name)) list.Add(name);
            }
            DateTime? Solar(int month, int day) => year >= 1 && year <= 9999 ? new DateTime(year, month, day) : (DateTime?)null;

            Add(Solar(1, 1), "신정");
            Add(Solar(3, 1), "삼일절");
            Add(Solar(5, 5), "어린이날");
            Add(Solar(6, 6), "현충일");
            if (year >= 2026) Add(Solar(7, 17), "제헌절");
            Add(Solar(8, 15), "광복절");
            Add(Solar(10, 3), "개천절");
            Add(Solar(10, 9), "한글날");
            Add(Solar(12, 25), "성탄절");

            DateTime? seollal = FromLunar(year, 1, 1), buddha = FromLunar(year, 4, 8), chuseok = FromLunar(year, 8, 15);
            var seollalDays = seollal.HasValue ? new[] { seollal.Value.AddDays(-1), seollal.Value, seollal.Value.AddDays(1) } : new DateTime[0];
            var chuseokDays = chuseok.HasValue ? new[] { chuseok.Value.AddDays(-1), chuseok.Value, chuseok.Value.AddDays(1) } : new DateTime[0];
            foreach (var d in seollalDays) Add(d, d == seollal ? "설날" : "설날 연휴");
            Add(buddha, "부처님오신날");
            foreach (var d in chuseokDays) Add(d, d == chuseok ? "추석" : "추석 연휴");
            foreach (var special in Special.Where(s => s.Key.Year == year)) Add(special.Key, special.Value);

            // 대체공휴일: 날짜 순서대로 처리하고, 이미 정한 대체공휴일과도 겹치지 않는 다음 평일을 고릅니다.
            var holidays = new HashSet<DateTime>(names.Keys);
            var substitutes = new List<Tuple<DateTime, string>>();
            void Substitute(DateTime after, string reason)
            {
                DateTime day = after.AddDays(1);
                while (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday || holidays.Contains(day)) day = day.AddDays(1);
                holidays.Add(day);
                substitutes.Add(Tuple.Create(day, "대체공휴일(" + reason + ")"));
            }
            bool Overlaps(DateTime day, string own) => names.TryGetValue(day, out var list) && list.Any(n => n != own && !n.StartsWith(own.Substring(0, 2), StringComparison.Ordinal));
            bool Weekend(DateTime day) => day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday;

            var checks = new List<Tuple<DateTime, Action>>();
            if (year >= 2014)
            {
                // 설날·추석 연휴: 일요일 또는 다른 공휴일과 겹치면 연휴 다음 첫 평일.
                if (seollalDays.Length > 0 && seollalDays.Any(d => d.DayOfWeek == DayOfWeek.Sunday || Overlaps(d, "설날")))
                    checks.Add(Tuple.Create(seollalDays.Last(), (Action)(() => Substitute(seollalDays.Last(), "설날"))));
                if (chuseokDays.Length > 0 && chuseokDays.Any(d => d.DayOfWeek == DayOfWeek.Sunday || Overlaps(d, "추석")))
                    checks.Add(Tuple.Create(chuseokDays.Last(), (Action)(() => Substitute(chuseokDays.Last(), "추석"))));
                // 어린이날: 토·일 또는 다른 공휴일과 겹치면.
                var children = new DateTime(year, 5, 5);
                if (Weekend(children) || Overlaps(children, "어린이날")) checks.Add(Tuple.Create(children, (Action)(() => Substitute(children, "어린이날"))));
            }
            void Single(DateTime day, string name, DateTime since)
            {
                if (day < since || !names.ContainsKey(day)) return;
                if (Weekend(day) || Overlaps(day, name)) checks.Add(Tuple.Create(day, (Action)(() => Substitute(day, name))));
            }
            if (year >= 2021)
            {
                var national = new DateTime(2021, 8, 4); // 국경일 대체공휴일 확대 시행
                Single(new DateTime(year, 3, 1), "삼일절", national);
                Single(new DateTime(year, 8, 15), "광복절", national);
                Single(new DateTime(year, 10, 3), "개천절", national);
                Single(new DateTime(year, 10, 9), "한글날", national);
                if (year >= 2026) Single(new DateTime(year, 7, 17), "제헌절", national);
                var extended = new DateTime(2023, 5, 4); // 부처님오신날·성탄절 대체공휴일 시행
                if (buddha.HasValue) Single(buddha.Value, "부처님오신날", extended);
                Single(new DateTime(year, 12, 25), "성탄절", extended);
            }
            // 같은 날 겹친 공휴일(예: 어린이날·부처님오신날)은 대체공휴일 하루만 생깁니다.
            foreach (var group in checks.GroupBy(c => c.Item1).OrderBy(g => g.Key)) group.First().Item2();

            var result = names.ToDictionary(p => p.Key, p => string.Join("·", p.Value));
            foreach (var s in substitutes.Where(s => s.Item1.Year == year))
                result[s.Item1] = result.TryGetValue(s.Item1, out string existing) ? existing + "·" + s.Item2 : s.Item2;
            return result;
        }

        // 음력 날짜(윤달 아님)를 양력으로. 지원 범위를 벗어나면 null.
        private static DateTime? FromLunar(int year, int month, int day)
        {
            try
            {
                int leap = Lunar.GetLeapMonth(year);
                int index = leap > 0 && month >= leap ? month + 1 : month;
                return Lunar.ToDateTime(year, index, day, 0, 0, 0, 0).Date;
            }
            catch (ArgumentOutOfRangeException) { return null; }
        }
    }
}
