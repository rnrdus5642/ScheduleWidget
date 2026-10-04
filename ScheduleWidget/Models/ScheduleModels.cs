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
        public CommunicationSettings Communication { get; set; } = new CommunicationSettings();
        public ReminderSettings Reminders { get; set; } = new ReminderSettings();
        public MusicSettings Music { get; set; } = new MusicSettings();
        public string CharacterManifest { get; set; } = CharacterCatalog.DefaultManifest;
        public MonitorStateData MiniPosition { get; set; }
        // 미니 창 달력(음악 막대 포함) 영역의 화면 위치·크기. 창은 이 영역과 주변 펫을 감싸도록 맞춰집니다.
        public MonitorStateData MiniBoard { get; set; }
        // Kept for older data readers. The calendar is now the primary window; the list opens beside it.
        public bool MiniMode { get; set; } = true;
        // 미니 달력에 한 번에 보여 줄 날짜 수(1~7일).
        public int MiniDayCount { get; set; } = 7;
        // 미니 창 캐릭터 크기 배율(%). 100%는 달력 높이의 절반, 50~300%.
        public int MiniCharacterScale { get; set; } = 100;
        // 미니 창에 캐릭터를 표시할지 여부. 숨기면 달력만 표시합니다.
        public bool MiniCharacterVisible { get; set; } = true;
        // 미니 창 달력 아래 음악 조작 막대를 표시할지 여부.
        public bool MiniPlayerVisible { get; set; } = true;
        // 원본 일정 창·미니 창을 모든 창 위에 표시할지 여부. 기본은 다른 앱 뒤(바탕화면 바로 위).
        public bool AlwaysOnTop { get; set; }
        // 미니 달력 일정 블록 윗줄에 남은 날짜(D-3·D-day·D+2)를 표시할지 여부.
        public bool MiniBlockDDayVisible { get; set; } = true;
        // 미니 달력 ‹ / › 넘김 애니메이션: 1 = 위로 넘기기(날짜 바를 축으로 한 장 넘김), 2 = 모서리 넘기기(대각선으로 말려 넘어감),
        // 3·4 = 1·2와 같되 링 위로 넘어간 뒷면은 종이 높이의 1/3까지만(끝부분은 흐려지며 사라짐), 5·6 = 1·2와 같되 링 위로는
        // 뒷면이 보이지 않음, 0 = 애니메이션 없음(바로 바뀜).
        public int MiniFlipEffect { get; set; } = 1;
        // 단축키(기본 Ctrl+G)로 어디서든 일정 창·미니 창을 모든 창 위로 한 번 올릴지 여부와 그 단축키.
        public bool BringToFrontHotKeyEnabled { get; set; } = true;
        public string BringToFrontHotKey { get; set; } = HotKeyGesture.Default;
        // 구글 캘린더 양방향 연동 (설정의 ON/OFF 스위치, 구글 로그인).
        public GoogleCalendarSettings GoogleCalendar { get; set; } = new GoogleCalendarSettings();
        public string CharacterAnimation { get; set; } = "idle";
        // 미니 창에 함께 띄울 캐릭터(두 번째·세 번째, 최대 2개). 첫 번째는 CharacterManifest/CharacterAnimation.
        public List<MiniCharacterSlot> MiniExtraCharacters { get; set; } = new List<MiniCharacterSlot>();
        // 첫 번째 캐릭터를 잠시 숨김(두 번째·세 번째는 MiniCharacterSlot.Hidden).
        public bool MiniFirstPetHidden { get; set; }
        // 첫 번째 캐릭터를 좌우 반전해서 그림(두 번째·세 번째는 MiniCharacterSlot.Flipped). 예전 파일에는 없습니다(= 반전 안 함).
        public bool MiniFirstPetFlipped { get; set; }
        // 원본 일정(TODO) 창 옆에도 펫을 띄울지(👤 버튼).
        public bool MainPetsVisible { get; set; }
        // 캐릭터 위치(달력 기준): "Left" 또는 "Right" 쪽, 세로 위치(0 = 달력 위쪽 끝, 100 = 아래쪽 끝, %),
        // 달력과의 간격(px, 음수면 달력 위로 겹침).
        public string MiniCharacterSide { get; set; } = "Left";
        public int MiniCharacterVertical { get; set; } = 100;
        public int MiniCharacterGap { get; set; } = 8;
        // 드래그로 정한 펫별 위치(순서대로 1~3번째). 달력(음악 막대 포함) 왼쪽 위 기준, 달력 폭·높이에 대한 %.
        // 음수·100 초과면 달력 바깥(왼쪽·위·오른쪽·아래). 비어 있으면 설정의 캐릭터 위치로 기본 배치.
        public List<MiniPetSpot> MiniPetSpots { get; set; } = new List<MiniPetSpot>();
        // 캐릭터별 마지막 위치: a character put back (바꾸기 / 추가) returns to where it last stood. Key = CharacterCatalog.SelectionKey.
        public Dictionary<string, MiniPetSpot> MiniPetSpotsByCharacter { get; set; } = new Dictionary<string, MiniPetSpot>();
        // TODO 창 옆 캐릭터(👤)의 배치: 달력 대신 TODO 창을 기준으로 하므로 미니 창과 따로 저장합니다(처음에는 기본 배치).
        // 캐릭터·동작·크기·보이기는 미니 창과 같습니다.
        public string CompanionCharacterSide { get; set; } = "Left";
        public int CompanionCharacterVertical { get; set; } = 100;
        public int CompanionCharacterGap { get; set; } = 8;
        public List<MiniPetSpot> CompanionPetSpots { get; set; } = new List<MiniPetSpot>();
        public Dictionary<string, MiniPetSpot> CompanionPetSpotsByCharacter { get; set; } = new Dictionary<string, MiniPetSpot>();
        // 업데이트 알림 말풍선의 "하루 동안 안 보기": 이 시각까지 말풍선을 띄우지 않습니다(없으면 바로 표시).
        public DateTime? UpdateNoticeSnoozedUntil { get; set; }
        // (Data files of earlier versions may hold an "Update" object — 업데이트 settings no longer exist; it is ignored on load.)
    }

    public class MiniPetSpot
    {
        // 달력 위/안쪽이면 달력 폭·높이에 대한 %.
        public double? Left { get; set; }
        public double? Top { get; set; }
        // 달력 바깥이면 가까운 가장자리에서의 거리(px). EdgeX: "L"(펫 오른쪽 끝 ↔ 달력 왼쪽) / "R"(펫 왼쪽 끝 ↔ 달력 오른쪽),
        // EdgeY: "T"(펫 아래 끝 ↔ 달력 위) / "B"(펫 위 끝 ↔ 달력 아래). 달력 크기를 바꿔도 간격이 그대로 유지됩니다.
        public string EdgeX { get; set; }
        public double OffsetX { get; set; }
        public string EdgeY { get; set; }
        public double OffsetY { get; set; }
        // 이 자리의 캐릭터(MiniWindow.SlotKeys의 키). 다른 창에서 캐릭터를 바꾸거나 빼도 이 자리가 누구 것인지 알 수 있습니다.
        // 예전 파일에는 없습니다(null = 지금 그 자리의 캐릭터 것으로 봅니다).
        public string Key { get; set; }
    }

    public class MiniCharacterSlot
    {
        public string Manifest { get; set; }
        public string Animation { get; set; } = "idle";
        // 이 캐릭터의 크기(%). 비어 있으면 첫 번째 캐릭터(MiniCharacterScale)와 같은 크기.
        public int? Scale { get; set; }
        // 이 캐릭터를 잠시 숨김(목록에는 남음).
        public bool Hidden { get; set; }
        // 이 캐릭터를 좌우 반전해서 그림(캐릭터 설정의 좌우 반전).
        public bool Flipped { get; set; }
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
        // 여러 날 일정(10/8~10/10)의 마지막 날 "yyyy-MM-dd". 없으면(null) 예전처럼 Period 하루짜리 일정입니다. 저장 시
        // DataManager가 정리합니다: 시작일보다 이르거나 같으면 null, 최대 MaxRangeDays일까지. 예전 파일·모바일 앱에는 없습니다.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string EndPeriod { get; set; }
        public string Time { get; set; }
        public string Color { get; set; }
        public bool IsCompleted { get; set; }
        public Dictionary<string, string> ReminderReceipts { get; set; } = new Dictionary<string, string>();
        // 구글 캘린더 연동: the matching Google event, what both sides looked like at the last sync (to tell who changed),
        // the date it had then, and the event's length in minutes (kept when the app moves it).
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string GoogleEventId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string GoogleSyncedHash { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string GoogleSyncedPeriod { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? GoogleEndMinutes { get; set; }
        // What the schedule looked like when Google last refused it (a 400 / 403 for that one event): not sent again until
        // it is edited here, so one odd event never stops the others from syncing.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string GoogleRefusedHash { get; set; }

        [JsonIgnore]
        public bool HasCustomColor => FeatureRules.IsColor(Color);

        [JsonIgnore]
        public string ReadableColor => FeatureRules.ReadableText(Color);

        private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR"); // shared, read-only
        private string parsedPeriod;
        private DateTime? parsedDate;

        // Period as a date, parsed once per value (the TODO sort asks for it many times): "yyyy-MM-dd", or any date this
        // PC's culture reads (an old or hand-edited file; saving writes it back as yyyy-MM-dd).
        private DateTime? PeriodDate
        {
            get
            {
                if (!string.Equals(parsedPeriod, Period, StringComparison.Ordinal))
                {
                    parsedPeriod = Period;
                    parsedDate = DateTime.TryParseExact(Period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exact) ? exact
                        : DateTime.TryParse(Period, out DateTime loose) ? loose.Date : (DateTime?)null;
                }
                return parsedDate;
            }
        }

        // ---- 여러 날 일정 ----
        // The longest range: the last day is at most this many days after the first.
        public const int MaxRangeDays = 366;

        private string parsedEnd, parsedEndFor;
        private DateTime? parsedEndDate;

        /// <summary>The first day (Period as a date), or null when Period does not read as one.</summary>
        [JsonIgnore]
        public DateTime? StartDate => PeriodDate;

        /// <summary>The last day of a range (after the first day), or null for a one-day schedule (or an end that is not valid).</summary>
        [JsonIgnore]
        public DateTime? EndDate
        {
            get
            {
                if (!string.Equals(parsedEnd, EndPeriod, StringComparison.Ordinal) || !string.Equals(parsedEndFor, Period, StringComparison.Ordinal))
                {
                    parsedEnd = EndPeriod;
                    parsedEndFor = Period;
                    string normalized = NormalizeEndPeriod(Period, EndPeriod);
                    parsedEndDate = normalized == null ? (DateTime?)null
                        : DateTime.ParseExact(normalized, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                return parsedEndDate;
            }
        }

        [JsonIgnore]
        public bool IsMultiDay => EndDate.HasValue;

        /// <summary>The last day the schedule covers: the range's end, else its one day.</summary>
        [JsonIgnore]
        public DateTime? LastDate => EndDate ?? PeriodDate;

        /// <summary>Whether the schedule is on <paramref name="day"/> (any day of a range).</summary>
        public bool Covers(DateTime day)
        {
            DateTime? start = PeriodDate;
            if (!start.HasValue) return false;
            day = day.Date;
            return day >= start.Value && day <= (EndDate ?? start.Value);
        }

        /// <summary>Whether the schedule is on any day from <paramref name="from"/> through <paramref name="to"/>.</summary>
        public bool Overlaps(DateTime from, DateTime to)
        {
            DateTime? start = PeriodDate;
            if (!start.HasValue) return false;
            return start.Value <= to.Date && (EndDate ?? start.Value) >= from.Date;
        }

        /// <summary>
        /// The stored form of a range's end for <paramref name="period"/>: "yyyy-MM-dd" when it is a later day (at most
        /// <see cref="MaxRangeDays"/> after the start; a longer one is cut there), else null (one-day schedule).
        /// </summary>
        public static string NormalizeEndPeriod(string period, string endPeriod)
        {
            if (string.IsNullOrWhiteSpace(endPeriod) ||
                !DateTime.TryParseExact(period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start)) return null;
            DateTime end;
            if (!DateTime.TryParseExact(endPeriod.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end) &&
                !DateTime.TryParse(endPeriod, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out end)) return null;
            end = end.Date;
            if (end <= start) return null;
            if ((end - start).TotalDays > MaxRangeDays) end = start.AddDays(MaxRangeDays);
            return end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Editor check for a range picked as <paramref name="start"/> … <paramref name="end"/> (end null = one day): the
        /// EndPeriod to store, or false with the message the editors show.
        /// </summary>
        public static bool TryRange(DateTime start, DateTime? end, out string endPeriod, out string error)
        {
            endPeriod = null;
            error = null;
            if (!end.HasValue || end.Value.Date == start.Date) return true;
            if (end.Value.Date < start.Date) { error = "종료 날짜는 시작 날짜와 같거나 이후여야 합니다."; return false; }
            if ((end.Value.Date - start.Date).TotalDays > MaxRangeDays) { error = "여러 날 일정은 최대 " + MaxRangeDays + "일까지 정할 수 있습니다."; return false; }
            endPeriod = end.Value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>A range's short label for the mini calendar blocks ("10.8~10.10"); empty for a one-day schedule.</summary>
        [JsonIgnore]
        public string RangeLabel
        {
            get
            {
                DateTime? start = PeriodDate, end = EndDate;
                if (!start.HasValue || !end.HasValue) return string.Empty;
                return start.Value.ToString("M.d", CultureInfo.InvariantCulture) + "~" + end.Value.ToString("M.d", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Period for messages (알림, 연락): "2026-10-08 ~ 2026-10-10" for a range, else Period as it is.</summary>
        [JsonIgnore]
        public string PeriodText => IsMultiDay ? Period + " ~ " + EndDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Period;

        /// <summary>
        /// Days until the schedule (D-n): before a range, to its first day; during it 0 (D-day); after it, negative from its
        /// last day (D+n). int.MaxValue when Period is not a date.
        /// </summary>
        [JsonIgnore]
        public int RemainingDays
        {
            get
            {
                DateTime? date = PeriodDate;
                if (!date.HasValue) return int.MaxValue;
                DateTime today = DateTime.Today;
                int toStart = (date.Value - today).Days;
                if (toStart >= 0) return toStart;
                DateTime? end = EndDate;
                if (!end.HasValue) return toStart;
                int toEnd = (end.Value - today).Days;
                return toEnd >= 0 ? 0 : toEnd;
            }
        }

        [JsonIgnore]
        public string DDay
        {
            get
            {
                int diff = RemainingDays;
                if (IsCompleted) return "완료";
                if (diff == int.MaxValue) return "날짜 확인";
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
                // A range: "10.08 (목) ~ 10.10 (토)" (the time, if any, is when it starts).
                if (DateTime.TryParseExact(Period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                    return date.ToString("MM.dd (ddd)", Korean) +
                        (FeatureRules.TryScheduleTime(Time, out string time) ? " " + time : string.Empty) +
                        (EndDate.HasValue ? " ~ " + EndDate.Value.ToString(EndDate.Value.Year != date.Year ? "yyyy.MM.dd (ddd)" : "MM.dd (ddd)", Korean) : string.Empty);
                return string.Empty;
            }
        }
    }
}
