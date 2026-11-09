using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScheduleWidget
{
    public partial class MiniWindow : Window, IPetSettingsHost
    {
        private readonly AppData data;
        private readonly Func<bool> changed;
        private readonly Action music;
        private readonly Action settings; // the calendar's gear opens unified settings
        private readonly IMusicControls player;
        private bool closed;
        private string characterPayload;
        private Point? pressPoint;
        private int pressedPetSlot = -1;
        private int pressedPetClickCount;
        private int previousPetClickSlot = -1;
        private DateTime weekStart;
        private Guid? miniEditId;
        private DateTime? selectedDay;
        private bool miniDateInvalid;
        private bool keepDayPopupState;

        public event Action<int> CharacterSettingsRequested;
        public event Action<ScheduleItem> ScheduleColorRequested;
        public event Action<ScheduleItem> ScheduleMessageRequested;

        // The visible calendar and its pets, in device pixels for placement on monitors with different DPI.
        internal bool TryGetWorkspaceScreenBounds(out Rect bounds)
        {
            var content = BoardRect;
            if (PetsShown)
                foreach (var pet in PetRects()) if (pet.Width > 0 && pet.Height > 0) content.Union(pet);
            return TryDeviceRect(content, out bounds);
        }

        /// <param name="musicPlaying">Whether music plays, for pets in 음악 반복 when there is no <paramref name="player"/>
        /// (the TODO window's pets: no music bar, but they still dance to the music).</param>
        public MiniWindow(AppData data, Func<bool> changed, Action music,
            IMusicControls player = null, Action settings = null, bool companion = false,
            Func<bool> musicPlaying = null)
        {
            this.data = data;
            this.companion = companion;
            this.musicPlaying = musicPlaying ?? (player != null ? (Func<bool>)(() => player.IsPlaying) : null);
            if (data.MiniExtraCharacters == null) data.MiniExtraCharacters = new System.Collections.Generic.List<MiniCharacterSlot>();
            this.changed = changed;
            this.music = music;
            this.player = player;
            this.settings = settings;
            InitializeComponent();
            if (companion)
            {
                // Pets only, around the TODO window: no calendar or music bar; empty areas stay click-through.
                WeekCalendar.Visibility = Visibility.Collapsed;
                PlayerBar.Visibility = Visibility.Collapsed;
                ShowActivated = false;
            }
            ApplyAppearance(data.Appearance, refresh: false);
            weekStart = DefaultRangeStart();
            CaptureMonitorPlacementSeed();
            InitializeCalendarMode();
            // The pets are sized from the board minus the music bar's room: know whether the bar shows before the first sizing
            // (it used to be counted as shown here and the pets jumped in size right after opening when it is hidden).
            if (!companion) PlayerBar.Visibility = player != null ? Visibility.Visible : Visibility.Collapsed;
            UpdateCharacterSize();
            UpdateCharacterVisibility();
            UpdatePlayerBar();
            if (player != null || this.musicPlaying != null) MusicWindow.PlaybackChanged += OnPlaybackChanged;
            TrackPopupClose(PlaylistPopup, VolumePopup, RangePopup, QueuePopup, BlockPopup, DayPopup);
            BlockPopup.CustomPopupPlacementCallback = PlaceBlockPopup;
            InitUpcomingPreview(); // › hover: a summary of the schedules after this page (MiniWindow.UpcomingPreview.cs)
            // Restore the calendar board where it was; the window then grows around it for the pets (ApplyPetLayout).
            // (The TODO window's pets use that window as their board instead: see FollowBoard.)
            var board = companion ? null : SavedBoard(data);
            restoredPlace = board != null && IsFinite(board.Left) && IsFinite(board.Top);
            if (board != null && IsFinite(board.Left) && IsFinite(board.Top))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                // The pets' room is already laid out round the default-size board: the board goes where it was saved
                // (a saved size too small to use keeps that room, and the board used to land a pet's width off).
                Left = board.Left - Edge - petPads.Left;
                Top = board.Top - Edge - petPads.Top;
            }
            if (board != null && IsFinite(board.Width) && IsFinite(board.Height) && board.Width >= MinCalendarOnlyWidth && board.Height >= 120)
            {
                Width = board.Width + 2 * Edge;
                Height = board.Height + 2 * Edge;
                requestedSize = new Size(Width, Height);
                if (IsFinite(board.Left) && IsFinite(board.Top)) { Left = board.Left - Edge; Top = board.Top - Edge; } // no room round it yet
                petPads = new Thickness(); // the window is exactly the saved board now; fit it around the pets
                UpdateCharacterSize();
            }
            Refresh();
            UpdateActivePetState();
            SourceInitialized += (s, e) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE,
                    NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE) | NativeMethods.WS_EX_TOOLWINDOW);
                var source = HwndSource.FromHwnd(handle);
                if (source != null)
                {
                    source.AddHook(AllowLargerThanScreen);
                    if (!companion) source.AddHook(WatchScreens); // monitors / work area changed: the calendar is kept reachable
                    AskSizeLimits(handle); // WPF keeps the limit from the window's creation (before this hook) until asked again
                    // A size larger than the screen (the calendar with the pets' room around it) was already cut down while
                    // the window was being made: set the size it was given again.
                    bool resized = false;
                    if (IsFinite(requestedSize.Width) && requestedSize.Width > 0 && Math.Abs(Width - requestedSize.Width) > 0.5) { Width = requestedSize.Width; resized = true; }
                    if (IsFinite(requestedSize.Height) && requestedSize.Height > 0 && Math.Abs(Height - requestedSize.Height) > 0.5) { Height = requestedSize.Height; resized = true; }
                    // Centred (first run, no saved place) on the cut-down size: keep the calendar on the screen at its real size.
                    if (resized && WindowStartupLocation != WindowStartupLocation.Manual && IsFinite(Left) && IsFinite(Top)) KeepOnScreen();
                }
                if (!companion) NativeMethods.SetWidgetStacking(this, data.AlwaysOnTop); // behind other apps unless 모든 창 위에 표시
                InitializeMonitorPlacement();
            };
            Loaded += async (s, e) =>
            {
                StartConnectivityWatch();
                InitializeMonitorPlacement();
                // Attach to the desktop like the main window so Win+D (바탕화면 보기) leaves it on screen;
                // it still comes to the front when clicked. Re-apply the 모든 창 위에 표시 choice afterwards.
                if (!companion) // the TODO window's pets are owned by that window instead
                {
                    NativeMethods.SetToDesktop(new WindowInteropHelper(this).Handle);
                    NativeMethods.SetWidgetStacking(this, data.AlwaysOnTop);
                    // Restored from a saved place (the window or only the board): the screens may have changed since (a remote
                    // session's resolution / scale, a virtual display gone). Checked again a moment later for a late DPI change.
                    if (restoredPlace || data.MiniPosition != null)
                    {
                        double left = Left, top = Top;
                        KeepOnScreen();
                        // Corrected: the place is saved (EnsureBoardVisible saves what it moves itself).
                        if (!EnsureBoardVisible("startup") && (Math.Abs(Left - left) > 0.5 || Math.Abs(Top - top) > 0.5)) SavePosition();
                        ScheduleEnsureVisible("startup-late", 1500);
                    }
                }
                // Build the popups' visuals while idle, so the first click on a date, the band, 달력 or a menu opens without a hitch.
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(PrewarmPopups));
                await InitCharacterViewAsync();
            };
            Closing += (s, e) => { FinishCalendarModeTransition(); CloseCalendarModeOverlay(); };
            Closed += (s, e) => { FlushSave(); closed = true; StopConnectivityWatch(); browserInputGuard?.Dispose(); browserInputGuard = null; MusicWindow.PlaybackChanged -= OnPlaybackChanged; recoveryTimer?.Stop(); healthTimer?.Stop(); pixelTimer?.Stop(); petWatchTimer?.Stop(); displayResumeTimer?.Stop(); CharacterView.Dispose(); };
            // Pets vanishing: the view's capture can stall after the screen sleeps, the session is locked, the PC resumes
            // or a (virtual) monitor comes and goes — check the view and repaint it (or reload it) when that happens.
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            SpriteSharpener.SheetReady += OnSharpSheetReady;
            // 타이핑 반응 listens only while a pet on screen uses it: checked again whenever pets, actions or visibility change.
            TypingInput.Subscribe(this, WantsTyping, OnTypingKeys);
            PetsChanged += TypingInput.Refresh;
            IsVisibleChanged += (s, e) => TypingInput.Refresh();
            Closed += (s, e) =>
            {
                TypingInput.Unsubscribe(this);
                Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; // static events: unhook or the window leaks
                Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
                SpriteSharpener.SheetReady -= OnSharpSheetReady;
            };
            IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) ScheduleHealthCheck("shown"); else { FinishCalendarModeTransition(); HideCalendarModeOverlay(); } UpdatePetWatch(); };
            // Desktop-owned (survives Win+D) windows are not raised by Windows on click; raise like a normal app.
            PreviewMouseDown += (s, e) => { FinishCalendarModeTransition(); if (!companion) NativeMethods.RaiseAboveOtherApps(this); };
            // Pet clicks that would miss the pets' own handlers (see RescuePetClick). Before them: preview events tunnel from here.
            AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) => RescuePetClick(e, right: false)), true);
            AddHandler(PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler((s, e) => RescuePetClick(e, right: true)), true);
            // Hidden (Esc → tray): the music bar's moving parts rest until it shows again. Shown: catch up on a desktop
            // re-attach asked for meanwhile, and on a music source app that went away.
            IsVisibleChanged += (s, e) => OnMiniVisibleChanged((bool)e.NewValue);
            Closed += (s, e) => { StopTitleMarquee(); seekTimer?.Stop(); lostSourceTimer?.Stop(); };
            // Safety nets so no half-finished press keeps the mouse captured.
            Deactivated += (s, e) => { pressPoint = null; if (draggingPet < 0 && CharacterHost.IsMouseCaptured) CharacterHost.ReleaseMouseCapture(); };
            Deactivated += (s, e) => EnsureViewClickThrough(); // e.g. the desktop activated: what raised the view's own window above this one
            CharacterHost.LostMouseCapture += (s, e) => pressPoint = null;
            // Moving the window by a drag (MoveWindowByDrag): the window holds the mouse until the button is let go.
            PreviewMouseMove += WindowDrag_PreviewMouseMove;
            PreviewMouseLeftButtonUp += WindowDrag_PreviewMouseUp;
            LostMouseCapture += (s, e) => { if (windowDragging && !IsMouseCaptured) EndWindowDrag(); };
            LocationChanged += (s, e) => PlacePlacementBanner(); // moved while placing: above / below the calendar may swap
            // Display / DPI changes (monitors plugged, moved between, scaling changed): Explorer may rebuild the desktop host,
            // so re-attach like the main window does, and keep the window on a screen.
            Microsoft.Win32.SystemEvents.DisplaySettingsChanging += OnDisplayChanging;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Closed += (s, e) =>
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanging -= OnDisplayChanging;
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            };
            Activated += (s, e) => { if (!companion) NativeMethods.RaiseAboveOtherApps(this); };
        }

        private static DateTime StartOfWeek(DateTime date) => date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private int DayCount => Math.Max(1, Math.Min(7, data.MiniDayCount));
        // 7일 보기는 월요일부터 시작하는 주 단위, 그보다 짧으면 오늘부터 보여 줍니다.
        private DateTime DefaultRangeStart() => DayCount == 7 ? StartOfWeek(DateTime.Today) : DateTime.Today;

        // The range follows "today" across midnight (or a resume / clock change on another day) only while it is the default
        // one (this week, or from today); a range the user moved to stays where it is.
        private DateTime rangeDay = DateTime.Today;

        private void FollowToday()
        {
            DateTime today = DateTime.Today;
            if (today == rangeDay || refreshingForFlip) return;
            if (weekStart == (DayCount == 7 ? StartOfWeek(rangeDay) : rangeDay)) weekStart = DefaultRangeStart();
            rangeDay = today;
        }

        // Counts refreshes, so a change saved through `changed` (which refreshes this window from the TODO list) is not
        // refreshed a second time here.
        private int refreshCount;

        public void Refresh()
        {
            if (closed || WeekDays == null) return;
            if (!refreshingForFlip) FinishWeekFlip(); // any other change (day count, data, midnight, theme) shows at once
            refreshCount++;
            FollowToday();
            int count = DayCount;
            // At DateTime.MaxValue the final calendar week is still Monday-aligned, but may show fewer than seven days.
            int visibleCount = Math.Min(count, (DateTime.MaxValue.Date - weekStart.Date).Days + 1);
            var end = weekStart.AddDays(visibleCount - 1);
            var korean = CultureInfo.GetCultureInfo("ko-KR");
            WeekLabel.Text = visibleCount == 1
                ? weekStart.ToString("M.d (ddd)", korean)
                : weekStart.ToString("M.d", CultureInfo.InvariantCulture) + " — " + end.ToString("M.d", CultureInfo.InvariantCulture);
            // No hover texts on the band or the arrows (only the days show theirs); screen readers still get the arrows' names.
            string unit = count == 7 ? "주" : count + "일";
            System.Windows.Automation.AutomationProperties.SetName(PreviousWeek, "이전 " + unit);
            System.Windows.Automation.AutomationProperties.SetName(NextWeek, "다음 " + unit);
            PreviousWeek.IsEnabled = weekStart >= DateTime.MinValue.AddDays(count);
            // The next page starts count days later. That start must be representable; its trailing days may be partial.
            NextWeek.IsEnabled = weekStart <= DateTime.MaxValue.Date.AddDays(-count);
            // The ↺ beside the style drop-down: only when today is not on this page.
            double fromStart = (DateTime.Today - weekStart).TotalDays;
            TodayButton.IsEnabled = fromStart < 0 || fromStart >= count;
            // One pass over the schedules for the whole range instead of one pass per day.
            var byDay = data.Schedules.Where(s => s.Period != null && !s.IsMultiDay).ToLookup(s => s.Period, StringComparer.Ordinal);
            // Multi-day schedules get a single spanning bar, while day details retain the complete list.
            var ranges = data.Schedules.Where(s => s.IsMultiDay && s.Overlaps(weekStart, end)).ToList();
            weekRangeSegments = CalendarRangeLayout.Build(ranges, weekStart, visibleCount, visibleCount);
            var cells = Enumerable.Range(0, visibleCount).Select(offset =>
            {
                DateTime date = weekStart.AddDays(offset);
                int weekday = ((int)date.DayOfWeek + 6) % 7;
                var tasks = DayTasks(byDay[date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)], ranges, date);
                var dayColors = BlockColors(tasks);
                string holiday = KoreanHolidays.NameOf(date);
                string label = date.ToString("M월 d일 (ddd)", korean);
                var blocks = tasks.Where(s => !s.IsMultiDay).Select(s =>
                {
                    string color = dayColors[s];
                    bool hasTime = !string.IsNullOrWhiteSpace(s.Time);
                    // 여러 날: the top line shows the range ("10.8~10.10") on each day it covers, after the time on its first
                    // day and after ↳ on the days that continue it.
                    string range = s.IsMultiDay ? s.RangeLabel : "";
                    bool continues = range.Length > 0 && s.StartDate != date;
                    return new
                    {
                        Item = s, s.Title, Range = range, Continues = continues,
                        Time = range.Length == 0 ? s.Time : continues ? "↳ " + range : (hasTime ? s.Time + " " : "") + range,
                        s.IsCompleted, HasTime = hasTime,
                        // D-3 / D-day / D+2 in the block's top line (next to the time); none once it is done, or when
                        // 설정's "일정 블록에 D-day 표시" is off.
                        DDay = s.IsCompleted || !data.MiniBlockDDayVisible ? "" : s.DDay,
                        ShowTopLine = hasTime || range.Length > 0 || (!s.IsCompleted && data.MiniBlockDDayVisible),
                        BlockColor = color, BlockInk = FeatureRules.ReadableText(color),
                        Hint = (range.Length == 0 ? "" : range + " · ") + (string.IsNullOrWhiteSpace(s.Time) ? "" : s.Time + " · ") + s.Title
                    };
                }).ToList();
                return new DayCell
                {
                    // Keep every schedule in the day; the list scrolls instead of dropping items at smaller heights.
                    BlockKey = string.Join("\u0001", blocks.Select(p => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Item) + "|" +
                        p.Title + "|" + p.Time + "|" + p.IsCompleted + "|" + p.BlockColor + "|" + p.DDay + "|" + p.ShowTopLine)),
                    Date = date, Day = date.Day, Weekday = "월화수목금토일"[weekday].ToString(), Holiday = holiday,
                    DayInk = holiday != null || weekday == 6 ? theme.Holiday : theme.Ink,
                    IsToday = date == DateTime.Today,
                    TaskBlocks = blocks,
                    Ink = holiday != null || weekday == 6 ? theme.Holiday : weekday == 5 ? theme.Saturday : theme.Weekday,
                    Hint = label + (holiday != null ? " · " + holiday : "") + "\n" + (tasks.Count == 0 ? "할 일 없음" : string.Join("\n", tasks.Select(s =>
                        (string.IsNullOrWhiteSpace(s.Time) ? "" : s.Time + " ") + (s.IsCompleted ? "✓ " : "• ") + s.Title +
                        (s.IsMultiDay ? " (" + s.RangeLabel + ")" : ""))))
                };
            }).ToList();
            SyncDayCells(cells);
            UpdateWeekRangeLayout();
            RefreshMonthCalendar();
            if (selectedDay.HasValue && DayPopup != null && DayPopup.IsOpen) RefreshDaySchedules();
        }

        // ---- Day cells: kept and updated in place, so moving the range or saving a schedule does not rebuild
        // every day button (that rebuild was most of the ~25 ms each refresh took). Blocks are rebuilt only for days that changed.
        public sealed class DayCell : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            internal string BlockKey;
            private DateTime date; private int day; private string weekday, holiday, dayInk, ink, hint;
            private bool isToday; private object taskBlocks;
            private void Set<T>(ref T field, T value, string name)
            {
                if (Equals(field, value)) return;
                field = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }
            public DateTime Date { get => date; set => Set(ref date, value, nameof(Date)); }
            public int Day { get => day; set => Set(ref day, value, nameof(Day)); }
            public string Weekday { get => weekday; set => Set(ref weekday, value, nameof(Weekday)); }
            public string Holiday { get => holiday; set => Set(ref holiday, value, nameof(Holiday)); }
            public string DayInk { get => dayInk; set => Set(ref dayInk, value, nameof(DayInk)); }
            public string Ink { get => ink; set => Set(ref ink, value, nameof(Ink)); }
            public string Hint { get => hint; set => Set(ref hint, value, nameof(Hint)); }
            public bool IsToday { get => isToday; set => Set(ref isToday, value, nameof(IsToday)); }
            public object TaskBlocks { get => taskBlocks; set => Set(ref taskBlocks, value, nameof(TaskBlocks)); }
            private Thickness rangeInset = new Thickness(0, 5, 0, 0);
            private int hiddenRangeCount;
            public Thickness RangeInset { get => rangeInset; set => Set(ref rangeInset, value, nameof(RangeInset)); }
            public int HiddenRangeCount
            {
                get => hiddenRangeCount;
                set { if (hiddenRangeCount == value) return; hiddenRangeCount = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(null)); }
            }
            public string RangeOverflowLabel => "+" + HiddenRangeCount + "개 기간 일정";

            internal void CopyFrom(DayCell next)
            {
                Date = next.Date; Day = next.Day; Weekday = next.Weekday; Holiday = next.Holiday; DayInk = next.DayInk; Ink = next.Ink;
                Hint = next.Hint; IsToday = next.IsToday;
                if (BlockKey != next.BlockKey) { BlockKey = next.BlockKey; TaskBlocks = next.TaskBlocks; }
            }
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<DayCell> dayCells =
            new System.Collections.ObjectModel.ObservableCollection<DayCell>();

        private void SyncDayCells(System.Collections.Generic.List<DayCell> cells)
        {
            if (WeekDays.ItemsSource != dayCells) WeekDays.ItemsSource = dayCells;
            while (dayCells.Count > cells.Count) dayCells.RemoveAt(dayCells.Count - 1);
            for (int i = 0; i < cells.Count; i++)
            {
                if (i < dayCells.Count) dayCells[i].CopyFrom(cells[i]);
                else dayCells.Add(cells[i]);
            }
        }

        // ---- Theme: the mini window follows the app theme (Light / Dark / Blue / Pink / Modern) ----
        // Paper calendar colors are Mini* brushes; the popups/menus use the shared Aux* brushes, overridden here so only
        // this window changes. Light keeps the original lilac paper look.
        private sealed class MiniTheme
        {
            public string Paper, Frame, Header, Ink, Muted, Hover, Day, Ring, TodayBorder, Holiday, Weekday, Saturday;
            public string Top, TopInk, TopHover; // calendar top bar (period label row); null = Header / Ink / Hover
            public string Canvas, Surface, AuxInk, AuxMuted, Hairline, Action;
        }

        private static readonly System.Collections.Generic.Dictionary<string, MiniTheme> MiniThemes =
            new System.Collections.Generic.Dictionary<string, MiniTheme>(StringComparer.OrdinalIgnoreCase)
        {
            ["Light"] = new MiniTheme
            {
                Paper = "#FFFFFAF2", Frame = "#635277", Header = "#EDE3FA", Ink = "#514461", Muted = "#8A7B9C", Hover = "#DCCFF3",
                Day = "#F6F0EA", Ring = "#F5B5AF", TodayBorder = "#635277", Holiday = "#BD656C", Weekday = "#84758F", Saturday = "#637FA5",
                Canvas = "#F5F5F7", Surface = "#FFFFFF", AuxInk = "#1D1D1F", AuxMuted = "#6E6E73", Hairline = "#E0E0E0", Action = "#1D1D1F"
            },
            ["Dark"] = new MiniTheme
            {
                Paper = "#FF1B2233", Frame = "#5C6F96", Header = "#27324B", Ink = "#E6EAF2", Muted = "#9AA6BC", Hover = "#34436A",
                Day = "#232C41", Ring = "#8295FF", TodayBorder = "#8295FF", Holiday = "#FF8A94", Weekday = "#AEB8CC", Saturday = "#8FB4FF",
                Canvas = "#1F2636", Surface = "#151D2C", AuxInk = "#F8FAFC", AuxMuted = "#C3CDDC", Hairline = "#2A3A53", Action = "#5B6CE0"
            },
            ["Blue"] = new MiniTheme
            {
                Paper = "#FFF8FCFF", Frame = "#2F5E96", Header = "#DCEBFF", Ink = "#102A4A", Muted = "#466B92", Hover = "#C4DCFA",
                Day = "#EAF4FF", Ring = "#8FB8E7", TodayBorder = "#2477E6", Holiday = "#C4505B", Weekday = "#466B92", Saturday = "#2477E6",
                Canvas = "#EAF4FF", Surface = "#FFFFFF", AuxInk = "#102A4A", AuxMuted = "#466B92", Hairline = "#A8C8ED", Action = "#2477E6"
            },
            ["Pink"] = new MiniTheme
            {
                Paper = "#FFFFF9FB", Frame = "#8E4A67", Header = "#FDE4EF", Ink = "#4A2035", Muted = "#A56A82", Hover = "#F8CFE0",
                Day = "#FFEDF4", Ring = "#F4B6CE", TodayBorder = "#D05A8A", Holiday = "#C8455F", Weekday = "#A56A82", Saturday = "#637FA5",
                Canvas = "#FFF4F8", Surface = "#FFFFFF", AuxInk = "#4A2035", AuxMuted = "#A56A82", Hairline = "#F4CBDC", Action = "#D05A8A"
            },
            // Modern: black and white. Light gray paper with white day cards, black frame and text, black top bar with white text; day names in color (weekdays black, Sat blue, Sun/holiday red).
            ["Modern"] = new MiniTheme
            {
                Paper = "#FFF7F7F7", Frame = "#111111", Header = "#F2F2F2", Ink = "#111111", Muted = "#6B6B6B", Hover = "#E4E4E4",
                Top = "#111111", TopInk = "#FFFFFF", TopHover = "#3A3A3A",
                Day = "#FFFFFF", Ring = "#FFFFFF", TodayBorder = "#111111", Holiday = "#D93025", Weekday = "#111111", Saturday = "#1A73E8",
                Canvas = "#F2F2F2", Surface = "#FFFFFF", AuxInk = "#111111", AuxMuted = "#6B6B6B", Hairline = "#D9D9D9", Action = "#111111"
            }
        };

        private MiniTheme theme = MiniThemes["Light"];
        public string ThemeName { get; private set; } = "Light";

        /// <summary>Recolors the mini window for a theme preset (unknown names fall back to Light).</summary>
        public void ApplyTheme(string preset, bool refresh = true)
        {
            if (preset == null || !MiniThemes.TryGetValue(preset, out MiniTheme next)) { preset = "Light"; next = MiniThemes["Light"]; }
            preset = MiniThemes.Keys.First(k => string.Equals(k, preset, StringComparison.OrdinalIgnoreCase));
            theme = next;
            ThemeName = preset;
            void Set(string key, string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); Resources[key] = brush; }
            Set("MiniPaperBrush", next.Paper); Set("MiniFrameBrush", next.Frame); Set("MiniHeaderBrush", next.Header);
            Set("MiniInkBrush", next.Ink); Set("MiniMutedBrush", next.Muted); Set("MiniHoverBrush", next.Hover);
            Set("MiniDayBrush", next.Day); Set("MiniRingBrush", next.Ring);
            Set("MiniTodayBorderBrush", next.TodayBorder); Set("MiniHolidayBrush", next.Holiday); Set("MiniSaturdayBrush", next.Saturday);
            Set("AuxCanvasBrush", next.Canvas); Set("AuxSurfaceBrush", next.Surface); Set("AuxInkBrush", next.AuxInk);
            Set("AuxMutedBrush", next.AuxMuted); Set("AuxHairlineBrush", next.Hairline);
            Set("AuxActionBrush", next.Action); Set("AuxFocusBrush", next.Action);
            // The action color as text (outline buttons, the chosen music source): this window's own theme, readable at 4.5:1.
            Set("AuxActionTextBrush", AuxTheme.Palettes.TryGetValue(preset, out AuxTheme.Palette aux) ? aux.ActionText : next.AuxInk);
            // The top bar gets its own colors (Modern: black bar, white text); popups sit outside it and keep the theme ink.
            if (CalendarHeader != null)
            {
                void Top(string key, string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); CalendarHeader.Resources[key] = brush; }
                Top("MiniHeaderBrush", next.Top ?? next.Header); Top("MiniInkBrush", next.TopInk ?? next.Ink);
                Top("AuxInkBrush", next.TopInk ?? next.AuxInk); Top("MiniHoverBrush", next.TopHover ?? next.Hover);
            }
            // Theme-only callers also get matching agenda colors. ApplyAppearance may then supply custom colors.
            var agenda = new AppearanceSettings { ThemePreset = preset, Opacity = scheduleAppearance.Opacity,
                CompactTitleFontSize = scheduleAppearance.CompactTitleFontSize, CompactDDayFontSize = scheduleAppearance.CompactDDayFontSize,
                MonthTitleFontSize = scheduleAppearance.EffectiveMonthTitleFontSize,
                TitleFontSize = scheduleAppearance.TitleFontSize, DDayFontSize = scheduleAppearance.DDayFontSize };
            agenda.CopyColorsFrom(AppearanceSettings.Presets[preset]);
            ApplyAppearance(agenda, refresh: false);
            if (refresh) Refresh(); // day cells carry their ink colors as data
        }

        // One day's schedules in the order the day shows them: not done first; 여러 날 ones (earliest first day first) before
        // that day's own; then by time (untimed last).
        internal static System.Collections.Generic.List<ScheduleItem> DayTasks(System.Collections.Generic.IEnumerable<ScheduleItem> sameDay,
            System.Collections.Generic.IEnumerable<ScheduleItem> ranges, DateTime date) =>
            sameDay.Concat(ranges.Where(s => s.Covers(date)))
                .OrderBy(s => s.IsCompleted).ThenBy(s => s.IsMultiDay ? 0 : 1).ThenBy(s => s.StartDate ?? DateTime.MaxValue)
                .ThenBy(s => s.Time ?? "99:99", StringComparer.Ordinal).ToList();

        // Vivid colors for schedules without a custom card color.
        private static readonly string[] BlockPalette =
            { "#E8505B", "#2E86DE", "#F39C12", "#27AE60", "#8E44AD", "#E84393", "#16A5A5", "#D35400" };

        // Each schedule keeps a stable color (from its ID); blocks on the same day get different colors when possible.
        private static System.Collections.Generic.Dictionary<ScheduleItem, string> BlockColors(System.Collections.Generic.IList<ScheduleItem> tasks)
        {
            var result = new System.Collections.Generic.Dictionary<ScheduleItem, string>();
            var used = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in tasks.Where(t => FeatureRules.IsColor(t.Color))) { result[s] = s.Color; used.Add(s.Color); }
            foreach (var s in tasks.Where(t => !FeatureRules.IsColor(t.Color)))
            {
                int start = (int)((uint)s.Id.GetHashCode() % (uint)BlockPalette.Length);
                string color = BlockPalette[start];
                for (int i = 0; i < BlockPalette.Length && used.Contains(color); i++)
                    color = BlockPalette[(start + i) % BlockPalette.Length];
                result[s] = color; used.Add(color);
            }
            return result;
        }

        // ---- Player bar under the calendar ----
        private void OnPlaybackChanged() => Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdatePlayerBar();
            SendMusicState();
            if (QueuePopup.IsOpen) RefreshQueue();
        }));

        // Whether music plays (the bar's player, or what the TODO window's pets are handed): null = no music at all.
        private readonly Func<bool> musicPlaying;
        private bool MusicPlaying => musicPlaying?.Invoke() == true;

        // Tells the pet whether music is playing, for the 음악 반복 mode.
        // Only a change is sent: PlaybackChanged also fires for volume, track and list changes, and the page restarts its
        // music pets on each message. musicSent: what the current page was told (its load carries it; null: not known).
        private bool? musicSent;
        private void SendMusicState()
        {
            if (closed || musicPlaying == null) return;
            bool playing = MusicPlaying;
            if (musicSent == playing) return;
            musicSent = playing;
            // Through PostToPage: a view whose browser process just died throws COMException, which must not crash the app.
            PostToPage(JsonConvert.SerializeObject(new { action = "music", playing }));
        }

        private CharacterEntry currentCharacter;

        // ---- 타이핑 반응: the pets react to typing anywhere (TypingInput counts key presses, never which keys) ----
        internal const string TypingMode = "typing";

        // Whether this window has a pet in 타이핑 반응 on screen now: the window shown, its pets shown, that pet shown and drawn
        // from a sprite sheet. Only then does TypingInput listen at all.
        internal bool WantsTyping()
        {
            if (closed || !IsVisible || !PetsShown) return false;
            for (int i = 0; i < SlotCount && i < slotCharacters.Count; i++)
                if (SlotAnimation(i) == TypingMode && !PetHidden(i) && slotCharacters[i].Rows > 0 && !unreadablePets.Contains(i) && !emptyPets.Contains(i))
                    return true;
            return false;
        }

        // A batch of key presses (just how many) for the page's pets in 타이핑 반응.
        private void OnTypingKeys(int count) => PostToPage("{\"action\":\"typing\",\"count\":" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");

        // Keep the selected pet and its settings page synchronized after a character or slot changes.
        private void UpdateActivePetState()
        {
            if (activeSlot >= SlotCount) activeSlot = 0;
            if (activeSlot < slotCharacters.Count) currentCharacter = slotCharacters[activeSlot];
            PetsChanged?.Invoke();
        }

        // The actions a pet offers: the standard ones, its pet.json extra actions (music, keyboard), 랜덤 순회, 음악 반복 (only with
        // music actions) and 타이핑 반응 (not for a still image: it has no frames to type with).
        private static System.Collections.Generic.List<(string Key, string Label)> AnimationOptions(CharacterEntry character)
        {
            var options = new System.Collections.Generic.List<(string Key, string Label)>
            {
                ("idle", "대기"), ("waving", "인사"), ("jumping", "점프"), ("running", "활동"), ("running-left", "왼쪽 걷기"),
                ("running-right", "오른쪽 걷기"), ("waiting", "기다리기"), ("review", "집중"), ("failed", "당황")
            };
            if (character != null)
                options.AddRange(CharacterCatalog.ExtraAnimationKeys.Where(k => character.Animations.ContainsKey(k.Key)).Select(k => (k.Key, k.Label)));
            options.Add(("random", "랜덤 순회"));
            if (CharacterCatalog.HasMusicActions(character)) options.Add(("music", "음악 반복"));
            if (character == null || character.Rows > 0) options.Add((TypingMode, "타이핑 반응"));
            return options;
        }

        private bool syncingRangeCalendar;

        private void RangeCalendarButton_Click(object sender, RoutedEventArgs e)
        {
            bool show = RangeCalendar.Visibility != Visibility.Visible;
            if (show)
            {
                syncingRangeCalendar = true;
                try
                {
                    RangeCalendar.SelectedDate = null;
                    RangeCalendar.DisplayDate = weekStart;
                }
                finally { syncingRangeCalendar = false; }
            }
            RangeCalendar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        // Jump to the chosen date, showing the current day count from there (7 days = that date's Monday week).
        private void RangeCalendar_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
        {
            if (syncingRangeCalendar || !RangeCalendar.SelectedDate.HasValue) return;
            ShowRangeFrom(RangeCalendar.SelectedDate.Value);
            Mouse.Capture(null); // Calendar keeps mouse capture after a pick, which would swallow the next click
            RangeCalendar.Visibility = Visibility.Collapsed;
            RangePopup.IsOpen = false;
        }

        public void ShowRangeFrom(DateTime date)
        {
            CloseDayPopup();
            EndRush(); // a page chosen here stays, even mid-오늘로 이동 (see FinishWeekFlip)
            if (IsMonthViewOpen) displayedMonth = MonthStart(date);
            else weekStart = DayCount == 7 ? StartOfWeek(date) : date.Date;
            Refresh();
        }

        private void RangeLabel_Click(object sender, RoutedEventArgs e)
        {
            if (RecentlyDragged) return;
            CloseDayPopup();
            if (!RangePopup.IsOpen) RangeCalendar.Visibility = Visibility.Collapsed;
            if (!RangePopup.IsOpen) UpdateDayCountButtons();
            TogglePopup(RangePopup);
            e.Handled = true;
        }

        // First-time template creation (the Calendar alone builds ~50 buttons) used to happen on the first click.
        // Measuring each popup's content once while idle does that work up front; nothing is shown.
        private void PrewarmPopups()
        {
            if (closed) return;
            UpdateDayCountButtons();
            var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
            RangeCalendar.Visibility = Visibility.Visible;
            foreach (var popup in new[] { RangePopup, DayPopup, BlockPopup, PlaylistPopup, VolumePopup, QueuePopup })
            {
                if (popup == null || popup.IsOpen || !(popup.Child is FrameworkElement content)) continue;
                content.ApplyTemplate();
                content.Measure(infinite);
                // Leave it needing a fresh measure: measured while detached, it would otherwise keep this (empty) size
                // even after its items are filled in, and open as a zero-size box (e.g. the style drop-down).
                content.InvalidateMeasure();
            }
            if (!RangePopup.IsOpen) RangeCalendar.Visibility = Visibility.Collapsed;
            PrewarmWeekFlip();
        }

        private void UpdateDayCountButtons() => DayCountButtons.ItemsSource = Enumerable.Range(1, 7)
            .Select(n => new { Count = n, Label = n + "일", Selected = n == DayCount }).ToList();

        private void DayCount_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is int count)) return;
            SetDayCount(count);
            e.Handled = true;
        }

        public void SetDayCount(int count, bool persist = true)
        {
            RangePopup.IsOpen = false;
            EndRush(); // the new day count lays out its own page, even mid-오늘로 이동
            count = Math.Max(1, Math.Min(7, count));
            bool countChanged = count != data.MiniDayCount;
            data.MiniDayCount = count;
            weekStart = DefaultRangeStart(); // before the save, whose refresh then shows the new range right away
            if (countChanged && persist) SaveAndRefresh();
            else Refresh();
        }

        private void DaySchedules_DateChanged(object sender, System.Windows.Data.DataTransferEventArgs e)
        {
            // The day cells are reused across weeks. A different date starts at its first schedule.
            if (e.Property != TagProperty || !(sender is ItemsControl list)) return;
            (list.Template?.FindName("DayScheduleScroll", list) as ScrollViewer)?.ScrollToTop();
        }

        private void WeekDays_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // A flip's old page is a picture of the day area at its old size: a resize (corner grip, music bar rows) ends it.
            FinishWeekFlip();
            UpdateWeekRangeLayout();
        }


        // The pets are sized from the board, not from the calendar (whose height follows the music bar's rows): only the
        // calendar's first real size (none before the first layout) or a new size basis (the bar shown / hidden, the board
        // resized) re-fits them — not every song line coming and going.
        private double petBasisSeen = double.NaN;

        private void WeekCalendar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.HeightChanged || resizingBoard || changingCalendarMode) return; // pets re-fit once per mode-transition frame
            double basis = PetSizeBasis();
            if (e.PreviousSize.Height > 0 && basis == petBasisSeen) return;
            petBasisSeen = basis;
            UpdateCharacterSize();
        }

        // The fixed player strip and its gaps reserve the same space in every playback state.
        private const double RestingBarRoom = PlayerBarHeight + 12, CalendarTopMargin = 4, CalendarBottomGapNoBar = 8;

        /// <summary>Calendar height the pets are sized from: the board minus a resting music bar. Changes only when the
        /// board is resized or the bar is shown / hidden — not when the bar's title or seek rows come and go.</summary>
        internal double PetSizeBasis() =>
            companion ? 260 // the TODO window's pets use a fixed base (a 260 px calendar) — its window height is not a calendar
                : Math.Max(80, (IsMonthViewOpen ? (ValidCalendarBoard(data.MiniBoard) ? data.MiniBoard.Height : 312) : BoardHeight)
                    - CalendarTopMargin - (PlayerBar != null && PlayerBar.Visibility == Visibility.Visible ? RestingBarRoom : CalendarBottomGapNoBar));

        // Character height follows the calendar: 100% = half the calendar height, scaled by the user's setting (50~300%).
        public void UpdateCharacterSize()
        {
            if (closed || CharacterView == null) return;
            // Before the first layout (or while collapsed) the board has no real size yet: keep the last good size.
            if (!companion && IsLoaded && WeekCalendar.ActualHeight <= 0) return;
            characterBaseHeight = PetSizeBasis() * 0.5; // 100% = half the calendar; each pet scales this by its own size
            var first = PetSize(0);
            characterCellHeight = first.Height;
            characterCellWidth = first.Width;
            ApplyPetLayout();
        }

        private void ResizeGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            CloseDayPopup();
            resizingBoard = true; // the board follows the grip; pets re-fit when the drag ends
            string corner = (string)((FrameworkElement)sender).Tag ?? "BR";
            double maxWidth = SystemParameters.VirtualScreenWidth, maxHeight = SystemParameters.VirtualScreenHeight;
            if (NativeMethods.TryGetWorkArea(new WindowInteropHelper(this).Handle, out NativeMethods.Rect area))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                maxWidth = (area.Right - area.Left) / dpi.DpiScaleX;
                maxHeight = (area.Bottom - area.Top) / dpi.DpiScaleY;
            }
            // The limit is for the calendar, not the window with the pets' room round it: a window already larger than the
            // screen (a big calendar and big pets) was cut to the screen on the grip's first move — the calendar jumped smaller.
            maxWidth += 2 * Edge + petPads.Left + petPads.Right;
            maxHeight += 2 * Edge + petPads.Top + petPads.Bottom;
            // Size from where the mouse is now vs. where the drag began (screen coordinates). The grip's own
            // HorizontalChange/VerticalChange are relative to the grip, which moves while the window re-fits; adding
            // those up could jump the calendar to its minimum size.
            Point mouse = PointToScreenDip(System.Windows.Forms.Control.MousePosition);
            double dx = mouse.X - gripStartMouse.X, dy = mouse.Y - gripStartMouse.Y;
            if (corner[1] == 'R')
                Width = Clamp(gripStartBounds.Width + dx, Math.Min(MinWidthNow, maxWidth), maxWidth);
            else
            {
                double next = Clamp(gripStartBounds.Width - dx, Math.Min(MinWidthNow, maxWidth), maxWidth);
                Left = gripStartBounds.Right - next;
                Width = next;
            }
            if (corner[0] == 'B')
                Height = Clamp(gripStartBounds.Height + dy, Math.Min(MinHeightNow, maxHeight), maxHeight);
            else
            {
                double next = Clamp(gripStartBounds.Height - dy, Math.Min(MinHeightNow, maxHeight), maxHeight);
                Top = gripStartBounds.Bottom - next;
                Height = next;
            }
            e.Handled = true;
        }

        private Rect gripStartBounds;
        private System.Collections.Generic.List<Rect> gripStartPets;
        private Point gripStartMouse;

        private void ResizeGrip_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            FinishCalendarModeTransition();
            resizingBoard = true;
            gripStartPets = PetBoardRects(BoardWidth, BoardHeight); // pets that grow with the calendar are kept apart at the end
            gripStartBounds = new Rect(Left, Top, WindowWidthNow, WindowHeightNow);
            gripStartMouse = PointToScreenDip(System.Windows.Forms.Control.MousePosition);
        }

        // Physical screen pixels → the DIPs that Left/Top/Width/Height use.
        private Point PointToScreenDip(System.Drawing.Point pixels)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Point(pixels.X / dpi.DpiScaleX, pixels.Y / dpi.DpiScaleY);
        }

        private void ResizeGrip_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            if (resettingScreenInput) { e.Handled = true; return; }
            resizingBoard = false;
            UpdateCharacterSize();
            KeepPetsApart(gripStartPets);
            gripStartPets = null;
            UpdateLayout();
            KeepOnScreen();
            if (e.Canceled || !CommitUserMonitorPlacement(true)) SavePosition();
        }

        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(Math.Max(min, max), value));
        private void Settings_Click(object sender, RoutedEventArgs e) { if (RecentlyDragged) return; CloseDayPopup(); settings?.Invoke(); }


        /// <summary>
        /// Pet clicks the pets' hit boxes never got: (a) another element still held the mouse (a press then goes to it, not to
        /// the pet — the pets stayed dead until a drag of the window released it), or (b) the press landed on the pet's drawn
        /// edge (head, arms) outside its inset hit box, on the window's bare background. Clicks on the calendar stay its own.
        /// </summary>
        private void RescuePetClick(MouseButtonEventArgs e, bool right)
        {
            if (e.Handled || closed || !PetsShown || CharacterHost.Visibility != Visibility.Visible) return;
            var source = e.OriginalSource as DependencyObject;
            var captured = Mouse.Captured as DependencyObject;
            if (!ShouldRescuePetClick(source, captured, e.GetPosition(CharacterHost))) return;
            bool foreignCapture = captured != null && !IsWithin(captured, CharacterHost);
            PetLog.Write("pet-click", (right ? "right" : "left") + " rescued: " + (foreignCapture ? "capture held by " + captured.GetType().Name : "outside the hit box"));
            if (foreignCapture) Mouse.Capture(null);
            if (right) Character_RightClick(CharacterHost, e);
            else Character_MouseDown(CharacterHost, e);
        }

        private bool ShouldRescuePetClick(DependencyObject source, DependencyObject captured, Point at)
        {
            // Popup events can route through their logical owner while PopupRoot holds mouse capture.
            // They belong to a separate visual tree: never reinterpret a popup button/input as a pet click,
            // even if its screen position overlaps the pet. This includes a DatePicker's nested popup.
            if (source != null && source != this && !IsWithin(source, OuterRoot)) return false;
            bool foreignCapture = captured != null && !IsWithin(captured, CharacterHost);
            if (!foreignCapture && IsWithin(source, CharacterHost)) return false; // the normal way in
            var rects = PetRects();
            bool inPet = rects.Any(r => r.Width > 0 && r.Contains(at));
            if (!inPet) return false;
            bool inHitBox = new[] { PetHit0, PetHit1, PetHit2 }.Any(h => h.Visibility == Visibility.Visible &&
                new Rect(Canvas.GetLeft(h), Canvas.GetTop(h), h.Width, h.Height).Contains(at));
            bool bareBackground = source == null || source == this || source == OuterRoot;
            return (foreignCapture && inHitBox) || bareBackground;
        }

        private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
        {
            for (; node != null; node = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if (node == ancestor) return true;
            return false;
        }

        private void Character_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            CloseDayPopup();
            if (placingPets) { previousPetClickSlot = -1; BeginPetDrag(e); return; } // while placing, presses only drag
            pressPoint = e.GetPosition(this);
            pressedPetSlot = SlotAt(e.GetPosition(CharacterHost));
            pressedPetClickCount = e.ClickCount;
            CharacterHost.CaptureMouse();
            e.Handled = true;
        }

        private void Character_MouseMove(object sender, MouseEventArgs e)
        {
            if (draggingPet >= 0) { DragPet(e); return; }
            if (pressPoint.HasValue && PetPressMoved(e.GetPosition(this), e.LeftButton == MouseButtonState.Pressed))
            {
                e.Handled = true;
                PetDragged(); // the calendar's band off screen: the pet moves the window instead
            }
        }

        /// <summary>
        /// A press on a pet that moved past the drag distance (window coordinates) ends there: true when it did. It is then
        /// not a click (no character settings), and the window stays put — only the calendar's top band moves it (unless that band is
        /// off screen: then PetDragged moves the window). Pets are dragged
        /// into place with 드래그로 위치 설정. (Also the tests' way in: the pointer and the button are handed in.)
        /// </summary>
        internal bool PetPressMoved(Point current, bool leftPressed)
        {
            if (!pressPoint.HasValue || !leftPressed) return false;
            if (Math.Abs(current.X - pressPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - pressPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return false;
            pressPoint = null;
            previousPetClickSlot = -1;
            CharacterHost.ReleaseMouseCapture();
            return true;
        }

        // A single click does nothing; a completed double-click opens the clicked pet's settings.
        // Wait for mouse-up so a drag or a press released over another pet does not open a window.
        private void Character_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (draggingPet >= 0) { EndPetDrag(e); return; }
            if (!pressPoint.HasValue) return;
            int pet = SlotAt(e.GetPosition(CharacterHost));
            int pressed = pressedPetSlot, clicks = pressedPetClickCount;
            pressPoint = null;
            pressedPetSlot = -1;
            pressedPetClickCount = 0;
            CharacterHost.ReleaseMouseCapture(); // a press must never leave the mouse captured (it swallowed later clicks)
            e.Handled = true;
            if (RecentlyDragged || pet != pressed) { previousPetClickSlot = -1; return; }
            Dispatcher.BeginInvoke(new Action(() => HandlePetClick(pet, clicks))); // after this press has finished
        }

        internal void HandlePetClick(int pet, int clicks)
        {
            if (closed || placingPets || RecentlyDragged || !PetsShown || !IsPet(pet) || PetHidden(pet))
            {
                previousPetClickSlot = -1;
                return;
            }
            bool openSettings = clicks == 2 && previousPetClickSlot == pet;
            previousPetClickSlot = clicks == 1 ? pet : -1;
            if (openSettings) OpenPetSettings(pet);
        }

        private void Character_RightClick(object sender, MouseButtonEventArgs e)
        {
            previousPetClickSlot = -1;
            e.Handled = true;
            if (placingPets) { EndPetPlacement(); return; } // right-click ends the drag placement
            if (RecentlyDragged) return;
            CloseRightClickMenus();
            CloseDayPopup();
            int pet = SlotAt(e.GetPosition(CharacterHost));
            // Right-clicking the same pet again while its 캐릭터 설정 is open closes it (like every other menu).
            if (petSettings != null && petSettings.SelectedPet == pet) { petSettings.Close(); return; }
            OpenPetSettings(pet);
        }

        // The default pet selected in settings: the first that shows, or the first pet when none does.
        internal int FirstShownSlot()
        {
            for (int i = 0; i < SlotCount; i++) if (!PetHidden(i)) return i;
            return 0;
        }

        // The calendar's top band (including its date label and buttons) moves the window when dragged.
        // An ordinary click acts only on the date label or a button; the blank band has no click action.
        private Point? calendarPress;

        private void Calendar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            calendarPress = IsOnCalendarTop(e.OriginalSource as DependencyObject) ? (Point?)e.GetPosition(this) : null;
        }

        internal bool IsOnCalendarTop(DependencyObject source)
        {
            for (var node = source; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                if (node is System.Windows.Controls.Primitives.Thumb || node is Slider) return false; // resize grips
                if (node == CalendarHeader) return true;
                if (node == WeekCalendar) return false;
            }
            return false;
        }

        private void Calendar_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!calendarPress.HasValue) return;
            if (e.LeftButton != MouseButtonState.Pressed) { calendarPress = null; return; }
            Point current = e.GetPosition(this);
            if (Math.Abs(current.X - calendarPress.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - calendarPress.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            calendarPress = null;
            Mouse.Capture(null); // releases the pressed day button so no click fires after the move
            e.Handled = true;
            MoveWindowByDrag();
        }

        // A drag that moved the window must not end in a click that opens a bubble, menu or the day-count choice.
        private DateTime lastDragEnd = DateTime.MinValue;
        private bool RecentlyDragged => (DateTime.Now - lastDragEnd).TotalMilliseconds < 400;

        private void MoveWindowByDrag()
        {
            if (companion) { Mouse.Capture(null); return; } // the TODO window's pets don't move that window
            CloseDayPopup();
            RangePopup.IsOpen = false;
            // The volume bar stays open by itself and would be left behind where the window was.
            CloseVolumeBar();
            // Our own drag, not DragMove: Windows' move loop never lets the WINDOW's top above the screen's top (with the pets'
            // room above the calendar — large while 드래그로 위치 설정 — the calendar stopped well below the top), and it
            // let the calendar leave the screen until the release snapped it back. Here the calendar stops at the edges live.
            BeginWindowDrag(NativeMethods.CursorPosition());
            if (Mouse.LeftButton != MouseButtonState.Pressed) EndWindowDrag(); // the button was already let go
        }

        // ---- Moving the window by dragging (the band, or a pet while the band is off screen) ----
        private bool windowDragging, beginningDrag;
        private Vector windowDragGrab; // the pointer's offset from the board's top-left, in DIPs (kept across monitors)
        /// <summary>Tests: the work area (device pixels) at a point, standing in for the real monitors.</summary>
        internal Func<System.Drawing.Point, Rect?> workAreaAtOverride = null;
        internal bool WindowDragging => windowDragging;

        internal void BeginWindowDrag(System.Drawing.Point cursor)
        {
            if (closed || companion || windowDragging) return;
            if (!TryDeviceRect(BoardRect, out Rect board)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1, sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
            bool reachable = BandReachable;
            windowDragGrab = new Vector((cursor.X - board.Left) / sx, (cursor.Y - board.Top) / sy);
            windowDragging = movingWindow = true;
            beginningDrag = true;
            try { CaptureMouse(); } // (WPF sends a mouse move of its own as the capture changes: not the drag's end)
            finally { beginningDrag = false; }
            if (!reachable) DragWindowTo(cursor); // off screen: onto the monitor under the pointer at once
        }

        /// <summary>
        /// The window follows the pointer (device pixels), its calendar board held inside the work area of the monitor under
        /// the pointer — it stops at the edges while dragging; the pets' room may hang off. Moving onto a monitor with
        /// another scale: the board keeps its DIP size (WPF rescales the window) and the place is set again at that scale.
        /// </summary>
        internal void DragWindowTo(System.Drawing.Point cursor)
        {
            if (!windowDragging || closed) return;
            NoteMonitorDragTarget(cursor);
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            Rect area;
            double scale = 0;
            Rect? fake = workAreaAtOverride?.Invoke(cursor);
            if (fake.HasValue) area = fake.Value;
            else if (NativeMethods.TryGetWorkAreaAt(cursor, out NativeMethods.Rect work, out scale)) area = ToRect(work);
            else return;
            for (int pass = 0; pass < 2; pass++)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                double s = scale > 0 ? scale : (dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1);
                var board = BoardRect;
                double width = board.Width * s, height = board.Height * s;
                double x = cursor.X - windowDragGrab.X * s, y = cursor.Y - windowDragGrab.Y * s;
                x = Math.Max(area.Left, Math.Min(x, Math.Max(area.Left, area.Right - width)));
                y = Math.Max(area.Top, Math.Min(y, Math.Max(area.Top, area.Bottom - height)));
                int left = (int)Math.Round(x - board.Left * s), top = (int)Math.Round(y - board.Top * s);
                if (NativeMethods.TryGetWindowRect(handle, out NativeMethods.Rect window) && window.Left == left && window.Top == top) break;
                NativeMethods.SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, SwpNoSize | NativeMethods.SWP_NOACTIVATE | SwpNoZOrder | SwpNoOwnerZOrder);
                // Onto a monitor with another scale: WPF took Windows' suggested place with the new DPI — set ours again.
                var now = VisualTreeHelper.GetDpi(this);
                if (Math.Abs(now.DpiScaleX - dpi.DpiScaleX) < 0.001) break;
                SyncWindowPixels();
            }
        }

        internal void EndWindowDrag()
        {
            if (!windowDragging) return;
            windowDragging = false;
            movingWindow = false;
            if (IsMouseCaptured) ReleaseMouseCapture(); // nothing may keep the mouse after a move
            lastDragEnd = DateTime.Now;
            if (!CommitUserMonitorPlacement(false)) SavePosition();
            EnsureBoardVisible("drag"); // (a no-op: the board was kept on a monitor all along)
        }

        private void WindowDrag_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!windowDragging) return;
            e.Handled = true;
            if (beginningDrag) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndWindowDrag(); return; }
            DragWindowTo(NativeMethods.CursorPosition());
        }

        private void WindowDrag_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!windowDragging || e.ChangedButton != MouseButton.Left) return;
            e.Handled = true; // no click (band → day-count choice) at the end of a drag
            EndWindowDrag();
        }

        // Width of the character column plus its gap, for a window of the given height.
        public static double CharacterColumnWidth(AppData data, double windowHeight)
        {
            double calendar = Math.Max(0, windowHeight - 20);
            double height = Math.Max(60, Math.Min(Math.Max(60, windowHeight - 16), calendar * 0.5 * Math.Max(50, Math.Min(300, data.MiniCharacterScale)) / 100.0));
            int pets = 1 + Math.Min(MaxCharacters - 1, data.MiniExtraCharacters?.Count ?? 0);
            return Math.Round(height * 12 / 13) * pets + Math.Max(-80, Math.Min(60, data.MiniCharacterGap));
        }

        /// <summary>The saved calendar board. Older versions saved only the window, which held a pet column on one side.</summary>
        public static MonitorStateData SavedBoard(AppData data)
        {
            return data.MiniMonthView && ValidCalendarBoard(data.MiniMonthBoard) ? data.MiniMonthBoard : SavedCompactBoard(data);
        }

        private static MonitorStateData SavedCompactBoard(AppData data)
        {
            if (data.MiniBoard != null) return data.MiniBoard;
            var window = data.MiniPosition;
            if (window == null) return null;
            // The old layout sized pets from the real calendar, about 50 px shorter than the window with the music bar.
            double column = data.MiniCharacterVisible && IsFinite(window.Height) ? CharacterColumnWidth(data, window.Height - (data.MiniPlayerVisible ? 50 : 0)) : 0;
            bool right = string.Equals(data.MiniCharacterSide, "Right", StringComparison.OrdinalIgnoreCase);
            return new MonitorStateData
            {
                Left = window.Left + Edge + (right ? 0 : column), Top = window.Top + Edge,
                Width = window.Width - 2 * Edge - column, Height = window.Height - 2 * Edge
            };
        }

        // Showing or hiding the pets grows/shrinks the window around the calendar, which keeps its size and place.
        public void SetCharacterVisible(bool visible)
        {
            if (companion) { if (!visible) CompanionHideRequested?.Invoke(); return; } // the TODO window's 👤 decides
            data.MiniCharacterVisible = visible;
            UpdateCharacterVisibility();
        }

        private const double MinCalendarOnlyWidth = 220, MinBoardHeight = 190;
        private double MinWidthNow => MinCalendarOnlyWidth + 2 * Edge + petPads.Left + petPads.Right;
        private double MinHeightNow => (IsMonthViewOpen ? MonthMinimumHeight : MinBoardHeight) + 2 * Edge + petPads.Top + petPads.Bottom;

        public void UpdateCharacterVisibility()
        {
            if (closed || CharacterHost == null) return;
            bool shown = PetsShown;
            if (!shown) EndPetPlacement(); // nothing left to drag
            ApplyPetLayout();
            PetsChanged?.Invoke();
        }

        private void KeepOnScreen()
        {
            if (companion) return; // follows the TODO window
            if (!NativeMethods.TryGetWorkArea(new WindowInteropHelper(this).Handle, out NativeMethods.Rect area)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            // The calendar board stays inside the work area — not the whole window: the pets' room round it may hang off the
            // screen, so the calendar can be dragged flush to every edge (it used to stop a pet's width short of the edge on
            // the pets' side, and was pushed back there again on every start). The size as set, not as last laid out:
            // ApplyPetLayout grows the window right before calling this.
            Left = KeepIn(Left, area.Left / dpi.DpiScaleX, area.Right / dpi.DpiScaleX, Edge + petPads.Left, BoardWidth, dpi.DpiScaleX);
            Top = KeepIn(Top, area.Top / dpi.DpiScaleY, area.Bottom / dpi.DpiScaleY, Edge + petPads.Top, BoardHeight, dpi.DpiScaleY);
        }

        // The window's left (top) edge that keeps the board [at + boardOffset, + boardSize] within [low, high] (its start at low
        // when it is larger), on a whole device pixel.
        private static double KeepIn(double at, double low, double high, double boardOffset, double boardSize, double scale)
        {
            double board = at + boardOffset;
            double clamped = Math.Max(low, Math.Min(board, Math.Max(low, high - boardSize)));
            if (Math.Abs(clamped - board) < 1e-6) return at; // already inside: left exactly where it is
            double edge = clamped - boardOffset;
            return scale > 0 ? Math.Round(edge * scale) / scale : edge;
        }

        /// <summary>
        /// 트레이의 위치 초기화 in mini mode: the window goes to the middle of the main screen's work area, and that place is
        /// saved — the window and its calendar board as they really are now.
        /// </summary>
        public void CenterOnPrimaryScreen()
        {
            if (CenterPreferredMonitorPlacement()) return;
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (closed || companion || screen == null) return;
            var work = screen.WorkingArea; // physical pixels
            for (int pass = 0; pass < 2; pass++)
            {
                // In this window's own scale: Left / Top are read in the DPI of the monitor it is on now (once more when the
                // move took it to a monitor with another scale).
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1, scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
                double width = WindowWidthNow, height = WindowHeightNow;
                if (!IsFinite(width) || !IsFinite(height)) return;
                Left = work.Left / scaleX + Math.Max(0, (work.Width / scaleX - width) / 2);
                Top = work.Top / scaleY + Math.Max(0, (work.Height / scaleY - height) / 2);
                DpiScale now = VisualTreeHelper.GetDpi(this);
                if (now.DpiScaleX == dpi.DpiScaleX && now.DpiScaleY == dpi.DpiScaleY) break;
            }
            SavePosition();
            FlushSave();
        }

        private void SavePosition()
        {
            if (changingCalendarMode || applyingMonitorPlacement) return;
            if (!IsFinite(Left) || !IsFinite(Top)) return;
            if (companion)
            {
                // Never overwrite the mini window's own place; the TODO window's pets keep their spots in their own memory.
                if (loadedSlotKeys != null) RememberSpots();
                SaveSoon();
                return;
            }
            var board = new MonitorStateData { Left = Left + Edge + petPads.Left, Top = Top + Edge + petPads.Top, Width = BoardWidth, Height = BoardHeight };
            if (IsMonthViewOpen)
            {
                data.MiniMonthBoard = board;
                // An explicit resize/move in either mode updates their shared horizontal geometry.
                if (ValidCalendarBoard(data.MiniBoard)) { data.MiniBoard.Left = board.Left; data.MiniBoard.Width = board.Width; }
            }
            else
            {
                data.MiniPosition = new MonitorStateData { Left = Left, Top = Top, Width = Width, Height = Height };
                data.MiniBoard = board;
            }
            if (loadedSlotKeys != null) RememberSpots(); // after a drag / placement: each character keeps its new spot
            MiniSettingsStateChanged?.Invoke();
            SaveSoon();
        }

        // Position saves come in bursts (slider ticks, layout passes); write the data file once they settle.
        private System.Windows.Threading.DispatcherTimer saveTimer;

        private void SaveSoon()
        {
            if (saveTimer == null)
            {
                saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                saveTimer.Tick += (s, e) => { saveTimer.Stop(); changed?.Invoke(); };
            }
            saveTimer.Stop();
            saveTimer.Start();
        }

        private void FlushSave()
        {
            if (saveTimer == null || !saveTimer.IsEnabled) return;
            saveTimer.Stop();
            changed?.Invoke();
        }

        private void OnDisplayChanging(object sender, EventArgs e)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Action prepare = () =>
            {
                if (closed || !IsLoaded) return;
                PrepareCharacterDisplayChange();
                ScheduleCharacterDisplayResume(3000); // also recover if Windows never sends the matching Changed event
            };
            if (Dispatcher.CheckAccess()) prepare();
            else Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, prepare);
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, new Action(() =>
            {
                if (closed || !IsLoaded) return;
                PrepareCharacterDisplayChange();
                ScheduleCharacterDisplayResume();
                ReattachToDesktop();
            }));
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            string userMove = MonitorMoveTargetForDpi();
            if (IsLoaded) PrepareCharacterDisplayChange();
            base.OnDpiChanged(oldDpi, newDpi);
            if (IsLoaded) ScheduleCharacterDisplayResume();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (userMove != null && !closed) CommitUserMonitorPlacement(false, userMove);
                ReattachToDesktop();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Hands the window to the desktop again (Explorer restarted; a display or DPI change) and keeps it on a screen.
        /// Hidden (Esc → tray), that waits until it shows again.
        /// </summary>
        public void ReattachToDesktop()
        {
            if (closed) return;
            if (!IsVisible) { reattachWhenShown = true; return; }
            ScheduleHealthCheck("display"); // the view's capture may need a nudge after a display / DPI change
            SyncWindowPixels();
            if (companion) return;
            NativeMethods.SetToDesktop(new WindowInteropHelper(this).Handle);
            NativeMethods.SetWidgetStacking(this, data.AlwaysOnTop);
            if (!monitorPlacementReady) InitializeMonitorPlacement();
            RestoreMonitorPlacement("display");
            double left = Left, top = Top;
            KeepOnScreen();
            // KeepOnScreen trusts WPF's Left / Top, which a DPI flip can leave out of step with where the window really is:
            // check the real pixels too, now and once the work area / DPI have settled. A corrected place is saved.
            if (!EnsureBoardVisible("display") && HasSavedPlace && (Math.Abs(Left - left) > 0.5 || Math.Abs(Top - top) > 0.5)) SavePosition();
            ScheduleEnsureVisible("display-late", 1500, 4000);
        }

        // ---- The calendar kept reachable: its top band (the drag handle) fully on some monitor's work area ----
        private bool restoredPlace;   // the window opened at a saved place
        private bool ensureWhenShown; // the screens changed while hidden: checked on the next show
        private bool movingWindow;    // the user is dragging the window (DragMove): never moved under them
        private bool HasSavedPlace => SavedBoard(data) != null;

        // Monitors / work area changed (seen by the window itself as well as through SystemEvents): checked once they settle.
        private IntPtr WatchScreens(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0, WM_SETTINGCHANGE = 0x001A, SPI_SETWORKAREA = 0x002F;
            if ((msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED) && IsLoaded && !closed)
            {
                PrepareCharacterDisplayChange();
                ScheduleCharacterDisplayResume();
            }
            if (msg == WM_DISPLAYCHANGE || (msg == WM_SETTINGCHANGE && wParam.ToInt64() == SPI_SETWORKAREA))
                ScheduleEnsureVisible(msg == WM_DISPLAYCHANGE ? "displaychange" : "workarea", 300, 1500);
            return IntPtr.Zero;
        }

        private void ScreensMayHaveChanged(string reason)
        {
            if (closed || companion) return;
            if (!IsVisible) { ensureWhenShown = true; return; }
            EnsureBoardVisible(reason);
            ScheduleEnsureVisible(reason + "-late", 1500, 4000);
        }

        private void ScheduleEnsureVisible(string reason, params int[] delays)
        {
            if (closed || companion) return;
            if (ensureTimers == null)
            {
                ensureTimers = new System.Collections.Generic.List<System.Windows.Threading.DispatcherTimer>();
                Closed += (s, e) => { foreach (var pending in ensureTimers) pending.Stop(); ensureTimers.Clear(); }; // a closed window is let go
            }
            foreach (int delay in delays)
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
                timer.Tick += (s, e) => { timer.Stop(); ensureTimers.Remove(timer); EnsureBoardVisible(reason); };
                ensureTimers.Add(timer);
                timer.Start();
            }
        }

        private System.Collections.Generic.List<System.Windows.Threading.DispatcherTimer> ensureTimers;

        // The calendar's top band in window DIPs: the board's width, down to the bottom of CalendarHeader.
        private Rect BandRect
        {
            get
            {
                var board = BoardRect;
                double height = 40;
                try
                {
                    if (CalendarHeader != null && CalendarHeader.IsVisible && CalendarHeader.ActualHeight > 0)
                        height = CalendarHeader.TransformToAncestor(this).Transform(new Point(0, CalendarHeader.ActualHeight)).Y - board.Top;
                }
                catch (InvalidOperationException) { }
                return new Rect(board.Left, board.Top, board.Width, Math.Max(16, Math.Min(board.Height, height)));
            }
        }

        // Window DIPs → device pixels of where Windows really has the window.
        private bool TryDeviceRect(Rect inWindow, out Rect device)
        {
            device = Rect.Empty;
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (!NativeMethods.TryGetWindowRect(handle, out NativeMethods.Rect window)) return false;
            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1, sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
            device = new Rect(window.Left + inWindow.Left * sx, window.Top + inWindow.Top * sy, inWindow.Width * sx, inWindow.Height * sy);
            return true;
        }

        private static Rect ToRect(NativeMethods.Rect area) => new Rect(area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top);

        // Fully on this work area (a band wider than the area: starting at its left edge), within a pixel.
        private static bool BandFits(Rect band, Rect area) =>
            band.Left >= area.Left - 1 && band.Top >= area.Top - 1 && band.Bottom <= area.Bottom + 1 &&
            (band.Right <= area.Right + 1 || (band.Width > area.Width && band.Left <= area.Left + 1));

        /// <summary>True while the calendar's top band lies fully inside some monitor's work area (so it can be dragged).</summary>
        internal bool BandReachable
        {
            get
            {
                if (closed || companion) return true;
                if (!TryDeviceRect(BandRect, out Rect band)) return true; // no window yet: nothing to rescue
                return NativeMethods.GetWorkAreas().Select(ToRect).Any(area => BandFits(band, area));
            }
        }

        /// <summary>
        /// When the calendar's top band is not fully on any monitor (the window was restored onto a monitor that is gone or
        /// smaller now, a remote session changed the resolution / scale, or WPF's Left / Top no longer match the window's
        /// pixels), moves the window — by its real pixels — so the board fits the nearest monitor's work area (its top-left
        /// first when it is larger), and saves that place. True when it moved. Never while the user drags or resizes.
        /// </summary>
        internal bool EnsureBoardVisible(string reason)
        {
            if (closed || companion) return false;
            if (!IsVisible) { ensureWhenShown = true; return false; }
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return false;
            if (movingWindow || resizingBoard || placingPets || draggingPet >= 0)
            {
                ScheduleEnsureVisible(reason, 1000); // the user is busy with the window: look again afterwards
                return false;
            }
            if (!monitorPlacementReady) InitializeMonitorPlacement();
            if (RestoreMonitorPlacement(reason)) return true;
            SyncWindowPixels();
            bool moved = false;
            string from = null;
            for (int pass = 0; pass < 3; pass++)
            {
                var areas = NativeMethods.GetWorkAreas().Select(ToRect).ToList();
                if (areas.Count == 0 || !TryDeviceRect(BandRect, out Rect band) || !TryDeviceRect(BoardRect, out Rect board)) break;
                if (areas.Any(area => BandFits(band, area))) break;
                if (from == null) from = band.ToString();
                // The monitor the calendar is most on, else the nearest one.
                Rect target = areas.OrderByDescending(a => Overlap(a, band)).ThenByDescending(a => Overlap(a, board)).ThenBy(a => Distance(a, band)).First();
                double x = Math.Max(target.Left, Math.Min(board.Left, Math.Max(target.Left, target.Right - board.Width)));
                double y = Math.Max(target.Top, Math.Min(board.Top, Math.Max(target.Top, target.Bottom - board.Height)));
                NativeMethods.TryGetWindowRect(handle, out NativeMethods.Rect window);
                int left = (int)Math.Round(window.Left + x - board.Left), top = (int)Math.Round(window.Top + y - board.Top);
                if (left == window.Left && top == window.Top) break;
                NativeMethods.SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, SwpNoSize | NativeMethods.SWP_NOACTIVATE | SwpNoZOrder | SwpNoOwnerZOrder);
                moved = true;
                SyncWindowPixels(); // moving to a monitor with another scale may change the DPI: then look again
            }
            if (!moved) return false;
            TryDeviceRect(BandRect, out Rect now);
            PetLog.Write("window", "calendar off screen (" + reason + "): band " + from + " -> " + now);
            SavePosition();
            return true;
        }

        private static double Overlap(Rect a, Rect b)
        {
            var both = Rect.Intersect(a, b);
            return both.IsEmpty ? 0 : both.Width * both.Height;
        }

        private static double Distance(Rect area, Rect band)
        {
            double cx = band.Left + band.Width / 2, cy = band.Top + band.Height / 2;
            double dx = Math.Max(0, Math.Max(area.Left - cx, cx - area.Right)), dy = Math.Max(0, Math.Max(area.Top - cy, cy - area.Bottom));
            return dx * dx + dy * dy;
        }

        /// <summary>
        /// A pet press dragged past the drag distance: while the calendar's top band cannot be reached (off screen), the
        /// pet moves the whole window instead (then kept on screen and saved). Otherwise it does nothing, as before.
        /// True when it moved the window.
        /// </summary>
        internal bool PetDragged()
        {
            if (closed || companion || placingPets || BandReachable) return false;
            PetLog.Write("window", "moved by a pet (the calendar's top band is off screen)");
            MoveWindowByDrag();
            return true;
        }

        /// <summary>
        /// After a DPI / display change (e.g. a virtual display coming and going at another scale) Windows can leave the window
        /// at its old pixel size — the DIP numbers as pixels, 2/3 of the size at 150 %: the calendar's right part and the
        /// pets' lower part were cut off. Width / Height still hold the right DIPs, so setting them again does nothing and
        /// WPF never resizes: put the window's pixels back to its size.
        /// </summary>
        internal void SyncWindowPixels()
        {
            if (applyingMonitorPlacement) return;
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (closed || handle == IntPtr.Zero || !IsFinite(Width) || !IsFinite(Height)) return;
            if (!NativeMethods.TryGetWindowSize(handle, out int width, out int height)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            int wantWidth = (int)Math.Round(Width * dpi.DpiScaleX), wantHeight = (int)Math.Round(Height * dpi.DpiScaleY);
            if (Math.Abs(width - wantWidth) <= 1 && Math.Abs(height - wantHeight) <= 1) return;
            PetLog.Write("window", "pixels " + width + "x" + height + " -> " + wantWidth + "x" + wantHeight + " (dpi " + dpi.PixelsPerDip + ")");
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, 0, 0, wantWidth, wantHeight,
                SwpNoMove | NativeMethods.SWP_NOACTIVATE | SwpNoZOrder | SwpNoOwnerZOrder);
        }

        private bool reattachWhenShown; // asked while hidden: done on the next show (OnMiniVisibleChanged)
        internal bool ReattachPending => reattachWhenShown;

        // Esc with nothing open: the mini window goes away but stays the 'last window', so the tray icon reopens it as it was.
        private void HideToTray()
        {
            if (companion) return; // the TODO window handles its own Esc
            CloseDayPopup();
            petSettings?.Close();
            Hide();
        }

        private void Mini_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (windowDragging) { EndWindowDrag(); return; } // Esc ends a window drag where it is
                if (placingPets) { EndPetPlacement(); return; }
                if (CloseUpcomingPreview()) return; // the › hover summary goes first
                if (MiniDateInput != null && MiniDateInput.IsDropDownOpen) MiniDateInput.IsDropDownOpen = false;
                else if (DayPopup.IsOpen) DayPopup.IsOpen = false;
                else if (RangePopup.IsOpen) RangePopup.IsOpen = false;
                else if (QueuePopup.IsOpen) QueuePopup.IsOpen = false;
                else if (VolumePopup.IsOpen) VolumePopup.IsOpen = false;
                else if (PlaylistPopup.IsOpen) PlaylistPopup.IsOpen = false;
                else if (BlockPopup.IsOpen) BlockPopup.IsOpen = false;
                else if (IsMonthViewOpen) SetMonthViewOpen(false);
                else HideToTray(); // nothing open: hide the widget; the tray icon (열기) brings it back
                return;
            }
        }

        private void ShowCharacterError(string message, int pet = 0)
        {
            characterErrorPet = pet;
            CharacterError.Text = message;
            CharacterError.Visibility = Visibility.Visible;
            PlaceCharacterError(PetRects());
        }

        private int characterErrorPet; // the pet the error text stands at
        // Pets not drawn on the current page (their image failed, or their files could not be read just now): the pixel
        // check leaves them out. Cleared when a new page loads.
        private readonly System.Collections.Generic.HashSet<int> undrawnPets = new System.Collections.Generic.HashSet<int>();

        // The page could not draw one pet's image (after one retry): say why, at that pet — not always at the first one.
        internal void ShowPetImageError(string reason, int pet)
        {
            undrawnPets.Add(pet);
            string name = pet >= 0 && pet < slotCharacters.Count ? slotCharacters[pet].Name + ": " : "";
            ShowCharacterError(name + (reason == "size" ? "이미지 크기가 캐릭터 형식과 맞지 않습니다." : "이미지를 읽지 못했습니다.") +
                " 설정 → 캐릭터에서 다시 선택해 주세요.", pet);
        }

        // At the pet it is about; when that pet is hidden (보이기 off), at the first pet that shows.
        private void PlaceCharacterError(System.Collections.Generic.IList<Rect> inView)
        {
            if (inView == null || inView.Count == 0) return;
            int pet = characterErrorPet >= 0 && characterErrorPet < inView.Count && inView[characterErrorPet].Width > 0 ? characterErrorPet : -1;
            for (int i = 0; pet < 0 && i < inView.Count; i++) if (inView[i].Width > 0) pet = i;
            if (pet < 0) pet = 0; // none shows: the board's top-left corner
            Canvas.SetLeft(CharacterError, inView[pet].X);
            Canvas.SetTop(CharacterError, inView[pet].Y);
        }

        public void ReloadCharacter()
        {
            if (closed) return;
            // Each character comes back to its own last spot (even before the view is ready).
            var core = LiveCore;
            if (SyncSpotMemory() && core == null) ApplyPetLayout();
            if (core == null)
            {
                // Starting, or waiting for the new view after a crash (that one loads the pets itself). An open 캐릭터 설정
                // still hears of it (a pet added or removed meanwhile), so its list never points past the pets.
                UpdateActivePetState();
                return;
            }
            try
            {
                slotCharacters.Clear();
                unreadablePets.Clear();
                emptyPets.Clear();
                undrawnPets.Clear();
                bool passing = false, dropped = false;
                // Each pet's character. Files that cannot be read just now (a sync or antivirus holding them, a character
                // being replaced) keep the pet in its place with an error there, and are read again soon; files broken for
                // good drop that pet (its spot too) — never breaking the others. The first pet always stays.
                fallbackPets.Clear();
                for (int i = 0; i < SlotCount; i++)
                {
                    string saved = SlotManifest(i);
                    string manifest = CharacterCatalog.ResolveSelection(saved);
                    // A saved character that is not available (deleted, not brought over from the old app-folder Pet yet) shows
                    // 흰 모찌 in its place and its saved selection stays (it comes back once that character is here again).
                    // Only an old way of naming a character that is here (an older install's path) is saved anew.
                    bool gone = string.Equals(manifest, CharacterCatalog.DefaultManifest, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(saved) && CharacterCatalog.DefaultKey(saved) == null;
                    if (!gone && !string.Equals(manifest, saved, StringComparison.OrdinalIgnoreCase) && !CharacterCatalog.KeepSavedSelection(saved))
                    {
                        SetSlotManifest(i, manifest);
                        changed?.Invoke();
                    }
                    if (CharacterCatalog.IsUnavailable(manifest))
                    {
                        // A Codex character while Codex is not installed (or a default one whose files are missing): 흰 모찌 in its
                        // place (no error, never dropped), its saved selection kept for when that character is back.
                        if (!ShowFallback(i, null))
                        {
                            emptyPets.Add(i); // not even 흰 모찌 is here: no character, as before
                            slotCharacters.Add(new CharacterEntry { Name = "캐릭터 없음", ManifestPath = manifest });
                        }
                        continue;
                    }
                    try
                    {
                        slotCharacters.Add(CharacterCatalog.Read(manifest));
                        if (gone) fallbackPets.Add(i);
                    }
                    catch (Exception ex) when (CharacterCatalog.IsImportError(ex))
                    {
                        bool passingError = IsPassingReadError(ex, manifest);
                        // Broken for good: 흰 모찌 in its place, its saved selection kept (never dropped while 흰 모찌 can show).
                        if (!passingError && ShowFallback(i, "unreadable " + ex.GetType().Name + ": " + ex.Message)) continue;
                        if (gone && !passingError)
                        {
                            // Its own character is gone and 흰 모찌 cannot be read either: no character there (its saved one kept).
                            emptyPets.Add(i);
                            slotCharacters.Add(new CharacterEntry { Name = "캐릭터 없음", ManifestPath = manifest });
                            continue;
                        }
                        PetLog.Write(passingError || i == 0 ? "pet-unreadable" : "pet-dropped", (i + 1) + ": " + ex.GetType().Name + ": " + ex.Message);
                        if (i > 0 && !passingError) { DropSlot(i); dropped = true; i--; continue; }
                        passing |= passingError;
                        unreadablePets.Add(i);
                        slotCharacters.Add(new CharacterEntry { Name = "읽지 못한 캐릭터", ManifestPath = manifest });
                    }
                }
                if (dropped) { loadedSlotKeys = SlotKeys(); changed?.Invoke(); }
                for (int i = 0; i < slotCharacters.Count; i++)
                    if (!unreadablePets.Contains(i) && !emptyPets.Contains(i) && !fallbackPets.Contains(i) && SlotAnimation(i) == "music" && !CharacterCatalog.HasMusicActions(slotCharacters[i]))
                    {
                        SetSlotAnimation(i, "random"); // no music actions: 음악 반복 is hidden, keep the pet moving like before
                        changed?.Invoke();
                    }
                currentCharacter = slotCharacters[0];
                UpdateActivePetState();
                UpdateCharacterSize(); // the view is as wide as the number of pets
                lastLayoutJson = null; // a fresh page needs the layout again
                characterPayload = JsonConvert.SerializeObject(new
                {
                    action = "load", selected = 0, state = SlotAnimation(0), music = MusicPlaying,
                    states = Enumerable.Range(0, slotCharacters.Count).Select(SlotAnimation).ToArray(),
                    flips = Enumerable.Range(0, slotCharacters.Count).Select(PetFlipped).ToArray(),
                    layout = PetLayout(),
                    // A pet that could not be read keeps its place in the list with no image: the page draws nothing there.
                    characters = slotCharacters.Select((c, i) => unreadablePets.Contains(i) || emptyPets.Contains(i)
                        ? new { name = c.Name, group = "", rows = 0, extra = new { }, image = "" }
                        : CharacterCatalog.BrowserEntry(core, c, i, sharpen: true)).ToArray()
                });
                undrawnPets.UnionWith(unreadablePets);
                undrawnPets.UnionWith(emptyPets);
                if (unreadablePets.Count > 0) ShowCharacterError("캐릭터 파일을 읽지 못했습니다. 설정 → 캐릭터에서 다시 선택해 주세요.", unreadablePets.Min());
                else CharacterError.Visibility = Visibility.Collapsed;
                ScheduleReadRetry(passing, unreadablePets.Count > 0);
                pageReadyAt = DateTime.MinValue; // loading: the pixel check waits for the new page
                navigatedAt = DateTime.Now;
                core.Navigate(EmbeddedBrowser.Origin + "character.html?mini&character=" + Guid.NewGuid().ToString("N"));
                if (dropped) SlotsChanged?.Invoke(); // the other pets window (mini ↔ TODO window) drops that pet too
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex))
            {
                for (int i = 0; i < SlotCount; i++) undrawnPets.Add(i); // the page was not reloaded: what it shows says nothing
                // slotCharacters (and maybe the stored load) already describe the new pets while the page still shows the
                // old ones: no sheet may be sent by index to that page (it would draw another character's frames), and no
                // stale load handed to it. The next reload builds both again.
                characterPayload = null;
                ShowCharacterError("설정 → 캐릭터에서 다시 선택해 주세요.");
            }
            // The view's browser process is gone (Navigate / the folder mapping throw): build a new view instead of crashing
            // the app — this runs from clicks (바꾸기, 추가, 빼기) that can come before the ProcessFailed recovery has run.
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ObjectDisposedException)
            { RequestCharacterRecovery(true, "reload-" + ex.GetType().Name); }
        }

        // Pets whose files could not be read at the last load (kept in their place, not drawn).
        private readonly System.Collections.Generic.HashSet<int> unreadablePets = new System.Collections.Generic.HashSet<int>();
        // Pets whose character is not here and not even 흰 모찌 can show (DefaultPets missing): kept in their place, nothing
        // drawn, no error.
        private readonly System.Collections.Generic.HashSet<int> emptyPets = new System.Collections.Generic.HashSet<int>();
        // Pets showing 흰 모찌 in place of a saved character that is not available (deleted, Codex not installed, unreadable):
        // their saved selection (and its action) stays as it is.
        private readonly System.Collections.Generic.HashSet<int> fallbackPets = new System.Collections.Generic.HashSet<int>();

        // Adds 흰 모찌 as pet i's character in place of its own; false when 흰 모찌 itself cannot be read (nothing added).
        private bool ShowFallback(int i, string why)
        {
            try
            {
                slotCharacters.Add(CharacterCatalog.Read(CharacterCatalog.DefaultManifest));
                fallbackPets.Add(i);
                if (why != null) PetLog.Write("pet-fallback", (i + 1) + ": " + why);
                return true;
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex))
            {
                PetLog.Write("pet-fallback", (i + 1) + ": no default character either: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }
        private System.Windows.Threading.DispatcherTimer readRetryTimer;
        private bool readRetried; // files that could not be read just now are read once more by themselves, a moment later

        private void ScheduleReadRetry(bool passing, bool anyUnreadable)
        {
            if (!anyUnreadable) { readRetried = false; return; }
            if (!passing || readRetried) return; // broken for good, or already tried again: wait for the user (or the next load)
            readRetried = true;
            if (readRetryTimer == null)
            {
                readRetryTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                readRetryTimer.Tick += (s, e) => { readRetryTimer.Stop(); ReloadCharacter(); };
            }
            readRetryTimer.Stop();
            readRetryTimer.Start();
        }

        // A read that failed because the files were busy (a sync or antivirus holding them) or were being replaced just
        // then (gone since the selection was looked up) — not a broken character.
        internal static bool IsPassingReadError(Exception ex, string manifest)
        {
            if (ex is System.IO.IOException || ex is UnauthorizedAccessException) return true;
            try
            {
                string path = CharacterCatalog.FullManifestPath(manifest);
                return !System.IO.File.Exists(path);
            }
            catch (Exception pathError) when (CharacterCatalog.IsImportError(pathError)) { return false; }
        }

        // A 2nd/3rd pet whose character is broken for good leaves the list, and its spot goes with it (in both windows'
        // placements) so the pets after it keep theirs.
        private void DropSlot(int i)
        {
            if (i <= 0 || i > data.MiniExtraCharacters.Count) return;
            data.MiniExtraCharacters.RemoveAt(i - 1);
            RemoveSpotSlot(i);
        }

        // One picker for every way in (left-clicking a pet, 캐릭터 설정's 바꾸기 / 추가), always with 드래그로 위치 설정.
        internal Action pickerOverride = null; // tests: stands in for the modal picker

        /// <summary>Raised when this window changed which characters the pets are (picker, 추가, 빼기, a broken pet dropped):
        /// the app reloads the other pets window (mini window ↔ TODO window's pets), which shows the same pets.</summary>
        public event Action SlotsChanged;

        // Character settings act on activeSlot; after pets were removed it may point past them.
        private void ClampActiveSlot() => activeSlot = Math.Max(0, Math.Min(SlotCount - 1, activeSlot));

        // The characters of every pet but this one (the picker says when a character it deletes is one of them).
        private System.Collections.Generic.List<string> OtherSlotManifests(int except) =>
            Enumerable.Range(0, SlotCount).Where(i => i != except).Select(SlotManifest).ToList();

        /// <summary>드래그로 위치 설정 has something to drag: the pets show and at least one of them is not hidden.</summary>
        internal bool CanPlacePets => !closed && PetsShown && Enumerable.Range(0, SlotCount).Any(i => !PetHidden(i));

        private void OpenCharacterPicker()
        {
            if (closed) return; // picking a replacement is an explicit action in character settings
            if (pickerOverride != null) { pickerOverride(); return; }
            CloseDayPopup();
            ClampActiveSlot();
            int slot = activeSlot;
            var picker = new CharacterWindow(SlotManifest(slot), offerPlacement: CanPlacePets, otherSlots: OtherSlotManifests(slot)) { Owner = this };
            bool chosen = picker.ShowDialog() == true;
            bool reload = picker.DeletedAny; // even when cancelled: a pet may have shown the deleted character
            if (chosen && !closed && slot < SlotCount)
            {
                SetSlotManifest(slot, picker.Result);
                changed();
                reload = true;
            }
            if (reload && !closed) { ReloadCharacter(); SlotsChanged?.Invoke(); }
            if (picker.PlacementRequested) StartPetPlacement();
        }

        // ---- Several pets (up to 3) side by side in the one character view ----
        public const int MaxCharacters = 3;
        private readonly System.Collections.Generic.List<CharacterEntry> slotCharacters = new System.Collections.Generic.List<CharacterEntry>();
        private int activeSlot; // the pet currently selected in character settings
        private double characterCellWidth = 120;

        private int SlotCount => 1 + Math.Min(MaxCharacters - 1, data.MiniExtraCharacters?.Count ?? 0);
        private string SlotManifest(int i) => i == 0 ? data.CharacterManifest : data.MiniExtraCharacters[i - 1].Manifest;
        private string SlotAnimation(int i) => (i <= 0 || i > (data.MiniExtraCharacters?.Count ?? 0) ? data.CharacterAnimation : data.MiniExtraCharacters[i - 1].Animation) ?? "idle";
        private void SetSlotManifest(int i, string manifest) { if (i == 0) data.CharacterManifest = manifest; else data.MiniExtraCharacters[i - 1].Manifest = manifest; }
        // A slot past the pets (a stale index) changes nothing: it used to overwrite the first pet's action.
        private void SetSlotAnimation(int i, string state) { if (i <= 0) data.CharacterAnimation = state; else if (i <= data.MiniExtraCharacters.Count) data.MiniExtraCharacters[i - 1].Animation = state; TypingInput.Refresh(); }

        // ---- Size per pet (50~300 %, 100 % = half the calendar height) ----
        private double characterBaseHeight = 130;
        private int SlotScale(int i)
        {
            int first = Math.Max(50, Math.Min(300, data.MiniCharacterScale));
            if (i <= 0 || i > (data.MiniExtraCharacters?.Count ?? 0)) return first;
            return Math.Max(50, Math.Min(300, data.MiniExtraCharacters[i - 1].Scale ?? first)); // unset: same as the first pet
        }

        private void SetSlotScale(int i, int scale)
        {
            scale = Math.Max(50, Math.Min(300, scale));
            if (i <= 0 || i > data.MiniExtraCharacters.Count)
            {
                // Pets that follow the first one keep their current size when the first pet changes.
                foreach (var slot in data.MiniExtraCharacters) if (slot.Scale == null) slot.Scale = data.MiniCharacterScale;
                data.MiniCharacterScale = scale;
            }
            else data.MiniExtraCharacters[i - 1].Scale = scale;
        }

        /// <summary>설정 패널의 `미니 창 캐릭터 크기`: every pet to the same size.</summary>
        public void SetAllCharacterScales(int scale)
        {
            var before = PetBoardRects(BoardWidth, BoardHeight);
            data.MiniCharacterScale = Math.Max(50, Math.Min(300, scale));
            foreach (var slot in data.MiniExtraCharacters) slot.Scale = null;
            UpdateCharacterSize();
            KeepPetsApart(before);
            PetsChanged?.Invoke();
        }

        /// <summary>The extra pets' own sizes (null = same as the first), for settings 취소.</summary>
        public System.Collections.Generic.List<int?> ExtraPetScales() => data.MiniExtraCharacters.Select(s => s.Scale).ToList();

        public void RestorePetScales(int firstScale, System.Collections.Generic.IList<int?> extras)
        {
            var restoreExtras = Enumerable.Repeat(true, data.MiniExtraCharacters?.Count ?? 0).ToList();
            RestorePetScalesOwned(firstScale, extras, true, restoreExtras);
        }

        /// <summary>Cancel only the scale slots that still contain the settings panel's last preview.</summary>
        internal void RestorePetScalesOwned(int firstScale, System.Collections.Generic.IList<int?> extras, bool restoreFirst,
            System.Collections.Generic.IList<bool> restoreExtras)
        {
            var before = PetBoardRects(BoardWidth, BoardHeight);
            if (restoreFirst) data.MiniCharacterScale = Math.Max(50, Math.Min(300, firstScale));
            extras = extras ?? new System.Collections.Generic.List<int?>();
            for (int i = 0; i < (data.MiniExtraCharacters?.Count ?? 0); i++)
            {
                if (restoreExtras != null && i < restoreExtras.Count && restoreExtras[i] && i < extras.Count) data.MiniExtraCharacters[i].Scale = extras[i];
                else if (restoreFirst && i >= extras.Count) data.MiniExtraCharacters[i].Scale = null; // a pet added during preview follows the restored first pet
            }
            UpdateCharacterSize();
            KeepPetsApart(before);
            PetsChanged?.Invoke();
        }

        // ---- Show / hide each pet (캐릭터 설정's 보이기) ----
        private bool PetHidden(int i) => i <= 0 ? data.MiniFirstPetHidden : i <= data.MiniExtraCharacters.Count && data.MiniExtraCharacters[i - 1].Hidden;

        private void SetPetHidden(int i, bool hidden)
        {
            if (i <= 0) data.MiniFirstPetHidden = hidden;
            else if (i <= data.MiniExtraCharacters.Count) data.MiniExtraCharacters[i - 1].Hidden = hidden;
        }

        // ---- 좌우 반전 per pet (캐릭터 설정): the page mirrors the pet's drawing; its spot and size stay the same ----
        private bool PetFlipped(int i) => i <= 0 ? data.MiniFirstPetFlipped : i <= (data.MiniExtraCharacters?.Count ?? 0) && data.MiniExtraCharacters[i - 1].Flipped;

        private void SetPetFlipped(int i, bool flipped)
        {
            if (i <= 0) data.MiniFirstPetFlipped = flipped;
            else if (i <= data.MiniExtraCharacters.Count) data.MiniExtraCharacters[i - 1].Flipped = flipped;
        }

        private void SendPetFlips()
        {
            for (int i = 0; i < SlotCount; i++) PostToPage(JsonConvert.SerializeObject(new { action = "flip", index = i, flipped = PetFlipped(i) }));
        }

        private Size PetSize(int i)
        {
            // Not capped by the calendar: pets can stand outside it, so up to 300 % may be taller than the calendar itself.
            double height = Math.Max(40, characterBaseHeight * SlotScale(i) / 100.0);
            return new Size(Math.Round(height * 12 / 13), height); // sprite cell aspect 192:208
        }

        /// <summary>Which pet is under this point of the character view (the one drawn on top wins; else the nearest).</summary>
        private int SlotAt(Point point)
        {
            var rects = PetRects();
            for (int i = rects.Count - 1; i >= 0; i--) if (rects[i].Width > 0 && rects[i].Contains(point)) return i;
            int best = 0; double bestDistance = double.MaxValue;
            for (int i = 0; i < rects.Count; i++)
            {
                if (rects[i].Width <= 0) continue; // hidden
                double dx = rects[i].X + rects[i].Width / 2 - point.X, dy = rects[i].Y + rects[i].Height / 2 - point.Y, d = dx * dx + dy * dy;
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return best;
        }

        // ---- 드래그로 위치 설정 (캐릭터 설정, or 캐릭터 선택 opened for a pet): drag each pet anywhere around the calendar ----
        private double characterCellHeight = 130;
        private bool placingPets;
        private int placingPet = -1; // -1: all pets; otherwise only the pet whose settings opened placement
        private int draggingPet = -1;
        private Vector dragGrab;

        // Spot storage: outside the calendar a pet keeps its distance (px) from the nearest calendar edge, so resizing the
        // calendar never pushes it away; over the calendar it keeps its place as % of the calendar.
        private static bool HasX(MiniPetSpot s) => s != null && (s.EdgeX != null || s.Left != null);
        private static bool HasY(MiniPetSpot s) => s != null && (s.EdgeY != null || s.Top != null);
        private static double SpotX(MiniPetSpot s, double w, double boardW) =>
            s.EdgeX == "L" ? s.OffsetX - w : s.EdgeX == "R" ? boardW + s.OffsetX : s.Left.Value * boardW / 100;
        private static double SpotY(MiniPetSpot s, double h, double boardH) =>
            s.EdgeY == "T" ? s.OffsetY - h : s.EdgeY == "B" ? boardH + s.OffsetY : s.Top.Value * boardH / 100;

        private static void StoreX(MiniPetSpot s, double x, double w, double boardW)
        {
            if (x + w <= 0) { s.EdgeX = "L"; s.OffsetX = x + w; s.Left = null; }
            else if (x >= boardW) { s.EdgeX = "R"; s.OffsetX = x - boardW; s.Left = null; }
            else { s.EdgeX = null; s.Left = x * 100 / Math.Max(1, boardW); }
        }

        private static void StoreY(MiniPetSpot s, double y, double h, double boardH)
        {
            if (y + h <= 0) { s.EdgeY = "T"; s.OffsetY = y + h; s.Top = null; }
            else if (y >= boardH) { s.EdgeY = "B"; s.OffsetY = y - boardH; s.Top = null; }
            else { s.EdgeY = null; s.Top = y * 100 / Math.Max(1, boardH); }
        }

        public static MiniPetSpot CopySpot(MiniPetSpot s) => new MiniPetSpot
            { Left = s.Left, Top = s.Top, EdgeX = s.EdgeX, OffsetX = s.OffsetX, EdgeY = s.EdgeY, OffsetY = s.OffsetY, Key = s.Key };

        private MiniPetSpot SpotOf(int i)
        {
            var spots = Spots;
            while (spots.Count <= i) spots.Add(new MiniPetSpot());
            return spots[i];
        }

        // ---- Where the pets stand: the mini window's pets and the TODO window's pets (👤) each keep their own placement
        // (side, height, gap, dragged spots and each character's last spot) — one stands around a calendar, the other around
        // the TODO window. The pets themselves, their actions, sizes and 보이기 are the same in both.
        private System.Collections.Generic.List<MiniPetSpot> Spots
        {
            get => companion ? data.CompanionPetSpots ?? (data.CompanionPetSpots = new System.Collections.Generic.List<MiniPetSpot>())
                             : data.MiniPetSpots ?? (data.MiniPetSpots = new System.Collections.Generic.List<MiniPetSpot>());
            set { if (companion) data.CompanionPetSpots = value; else data.MiniPetSpots = value; }
        }

        private string PlacementSide
        {
            get => companion ? data.CompanionCharacterSide : data.MiniCharacterSide;
            set { if (companion) data.CompanionCharacterSide = value; else data.MiniCharacterSide = value; }
        }

        private int PlacementVertical
        {
            get => companion ? data.CompanionCharacterVertical : data.MiniCharacterVertical;
            set { if (companion) data.CompanionCharacterVertical = value; else data.MiniCharacterVertical = value; }
        }

        private int PlacementGap
        {
            get => companion ? data.CompanionCharacterGap : data.MiniCharacterGap;
            set { if (companion) data.CompanionCharacterGap = value; else data.MiniCharacterGap = value; }
        }

        // A pet added or removed moves the spots after it in both windows' placements (the pets are the same in both).
        // Whose spot a just-added pet's empty spot is: no character's yet, so each window gives the new pet its own memory.
        private const string NewPetKey = "+";

        private void InsertSpotSlot(int index)
        {
            foreach (var spots in new[] { data.MiniPetSpots, data.CompanionPetSpots, data.MiniCompactPetLayout?.Spots, data.MiniMonthPetLayout?.Spots })
                if (spots != null && spots.Count > index) spots.Insert(index, new MiniPetSpot { Key = NewPetKey });
        }

        private void RemoveSpotSlot(int index)
        {
            foreach (var spots in new[] { data.MiniPetSpots, data.CompanionPetSpots, data.MiniCompactPetLayout?.Spots, data.MiniMonthPetLayout?.Spots })
                if (spots != null && index < spots.Count) spots.RemoveAt(index);
        }

        // Where the character view sits in the window (pet spots are sent to the page relative to it).
        private Rect characterViewFrame;
        private string lastLayoutJson; // last layout posted to the page; identical layouts are not re-sent

        // Each pet's spot inside the view. "hidden" comes from the pet's 보이기 setting itself, never from a size that
        // happens to be 0 for a moment (that used to hide pets until the next layout). A pet that could not be read keeps
        // its spot (its error text and click stand there) but has nothing to draw.
        private object[] PetLayoutEntries() => PetRects().Select((r, i) => (object)new
        {
            x = r.X - characterViewFrame.X, y = r.Y - characterViewFrame.Y, w = r.Width, h = r.Height,
            hidden = PetHidden(i) || !PetsShown || unreadablePets.Contains(i) || emptyPets.Contains(i)
        }).ToArray();

        private object PetLayout() => new { w = characterCellWidth, h = characterCellHeight, pets = PetLayoutEntries() };

        private void SendPetLayout()
        {
            if (closed || pagePostOverride == null && LiveCore == null) return;
            // The view's own size is part of the message, so a resize with the same relative spots still reaches the page.
            string json = JsonConvert.SerializeObject(new
            {
                action = "layout", w = characterCellWidth, h = characterCellHeight, vw = characterViewFrame.Width, vh = characterViewFrame.Height,
                pets = PetLayoutEntries()
            });
            if (json == lastLayoutJson) return; // several SizeChanged paths lay out the same thing
            lastLayoutJson = json;
            PostToPage(json);
        }

        /// <summary>Starts 드래그로 위치 설정: until 완료 (or Esc / right-click), dragging a pet moves only that pet.</summary>
        public void StartPetPlacement()
            => StartPetPlacement(-1);

        public void StartPetPlacement(int index)
        {
            if (closed || !PetsShown) return;
            if (index != -1 && (!IsPet(index) || PetHidden(index))) return;
            if (Enumerable.Range(0, SlotCount).All(PetHidden)) return; // nothing to drag
            CloseDayPopup();
            petSettings?.Close(); // the pets are dragged on the mini window itself
            placingPets = true;
            placingPet = index;
            PlacementBannerText.Text = index < 0 ? "캐릭터를 끌어서 원하는 자리에 놓으세요"
                : ((IPetSettingsHost)this).PetName(index) + " · 이 캐릭터를 끌어서 이동하세요";
            PlacementBanner.Visibility = Visibility.Visible;
            ApplyPetLayout(); // move cursor on the pets
        }

        public void EndPetPlacement()
        {
            if (!placingPets) return;
            placingPets = false;
            placingPet = -1;
            draggingPet = -1;
            Mouse.Capture(null);
            PlacementBanner.Visibility = Visibility.Collapsed;
            ApplyPetLayout();
            KeepOnScreen();
            SavePosition();
        }

        private void PlacementDone_Click(object sender, RoutedEventArgs e) => EndPetPlacement();

        private void PlacementReset_Click(object sender, RoutedEventArgs e)
        {
            if (placingPet >= 0) { ResetPetPosition(placingPet); return; }
            Spots.Clear(); // back to the default spots from 캐릭터 위치
            RememberSpots(forgetUnplaced: true); // these characters forget their old spots too
            ApplyPetLayout();
        }

        private void BeginPetDrag(MouseButtonEventArgs e)
        {
            e.Handled = true;
            Point at = e.GetPosition(CharacterHost);
            var rects = PetRects();
            draggingPet = -1;
            for (int i = rects.Count - 1; i >= 0; i--) if (CanDragPet(i) && rects[i].Width > 0 && rects[i].Contains(at)) { draggingPet = i; break; } // hidden: empty rect
            if (draggingPet < 0) return;
            dragGrab = at - rects[draggingPet].TopLeft;
            CharacterHost.CaptureMouse();
        }

        private void DragPet(MouseEventArgs e)
        {
            e.Handled = true;
            if (e.LeftButton != MouseButtonState.Pressed) { EndPetDrag(null); return; }
            // Board coordinates: the board stays put on screen while the window grows around the pets.
            Point at = e.GetPosition(MiniRoot) - dragGrab;
            var size = PetSize(draggingPet);
            double boardW = BoardWidth, boardH = BoardHeight, w = size.Width, h = size.Height;
            double x = Math.Max(-3 * w - 60, Math.Min(boardW + 2 * w + 60, at.X));
            double y = Math.Max(-h - 20, Math.Min(boardH + 20, at.Y));
            // Pin the other pets where they are now, so moving one never shuffles the rest.
            var rects = PetBoardRects(boardW, boardH);
            for (int i = 0; i < rects.Count; i++)
            {
                if (rects[i].Width <= 0) continue; // hidden pets keep their own spot
                var other = SpotOf(i);
                if (!HasX(other)) StoreX(other, rects[i].X, rects[i].Width, boardW);
                if (!HasY(other)) StoreY(other, rects[i].Y, rects[i].Height, boardH);
            }
            var spot = SpotOf(draggingPet);
            StoreX(spot, x, w, boardW);
            StoreY(spot, y, h, boardH);
            ApplyPetLayout(save: false);
        }

        private void EndPetDrag(MouseButtonEventArgs e)
        {
            if (e != null) e.Handled = true;
            draggingPet = -1;
            CharacterHost.ReleaseMouseCapture();
            SavePosition();
        }

        private void AddCharacter_Click(object sender, RoutedEventArgs e)
        {
            CloseDayPopup();
            if (SlotCount >= MaxCharacters) return;
            ClampActiveSlot();
            // A new pet has no character yet: none is "the current one" (deleting one the pets use says so instead).
            var picker = new CharacterWindow(null, offerPlacement: CanPlacePets, otherSlots: OtherSlotManifests(-1)) { Owner = this };
            bool chosen = picker.ShowDialog() == true;
            if (closed) return;
            bool added = chosen && SlotCount < MaxCharacters;
            if (added)
            {
                data.MiniExtraCharacters.Add(new MiniCharacterSlot { Manifest = picker.Result, Animation = "idle", Scale = SlotScale(activeSlot) });
                InsertSpotSlot(SlotCount - 1); // a spot left over past the last pet is not the new pet's
                activeSlot = SlotCount - 1;
                changed();
                UpdateCharacterSize();
            }
            if (added || picker.DeletedAny) { ReloadCharacter(); SlotsChanged?.Invoke(); } // a pet may have shown a deleted character
            if (picker.PlacementRequested) StartPetPlacement();
        }

        private void RemoveCharacter_Click(object sender, RoutedEventArgs e)
        {
            CloseDayPopup();
            if (SlotCount <= 1) return;
            petSettings?.Close(); // a shifted slot must never edit a different character
            EndPetPlacement();
            ClampActiveSlot();
            int removed = activeSlot;
            if (activeSlot <= 0)
            {
                // Removing the first pet: the second one moves into its place.
                foreach (var slot in data.MiniExtraCharacters) if (slot.Scale == null) slot.Scale = data.MiniCharacterScale; // followers keep their size
                var next = data.MiniExtraCharacters[0];
                data.CharacterManifest = next.Manifest;
                data.CharacterAnimation = next.Animation ?? "idle";
                data.MiniCharacterScale = next.Scale ?? data.MiniCharacterScale; // the second pet keeps its size in first place
                data.MiniFirstPetHidden = next.Hidden;
                data.MiniFirstPetFlipped = next.Flipped;
                data.MiniExtraCharacters.RemoveAt(0);
            }
            else data.MiniExtraCharacters.RemoveAt(activeSlot - 1);
            RemoveSpotSlot(removed); // in both windows' placements: the pets after it keep their spots
            activeSlot = 0;
            changed();
            UpdateCharacterSize();
            ReloadCharacter();
            SlotsChanged?.Invoke();
        }

        // ---- Pet layout: pets float anywhere around the board (calendar + music bar) ----
        // Each pet's spot is kept relative to the board (% of its width/height, outside = negative or over 100).
        // The window is the board plus whatever room the pets need around it; the board never moves on screen.
        private const double Edge = 4;
        private Thickness petPads; // room around the board taken by pets
        private bool layingOutPets, resizingBoard;

        private bool CharactersOnRight => string.Equals(PlacementSide, "Right", StringComparison.OrdinalIgnoreCase);
        private int CharacterGapPx => Math.Max(-80, Math.Min(60, PlacementGap));
        private double WindowWidthNow => double.IsNaN(Width) ? ActualWidth : Width;
        private double WindowHeightNow => double.IsNaN(Height) ? ActualHeight : Height;
        private double BoardWidth => Math.Max(1, WindowWidthNow - 2 * Edge - petPads.Left - petPads.Right);
        private double BoardHeight => Math.Max(1, WindowHeightNow - 2 * Edge - petPads.Top - petPads.Bottom);

        /// <summary>Pets' rectangles relative to the board's top-left (unset spots follow 캐릭터 위치 in settings).</summary>
        private System.Collections.Generic.List<Rect> PetBoardRects(double boardW, double boardH, bool fitToScreen = true,
            System.Collections.Generic.IList<MiniPetSpot> placement = null, MonitorStateData screenBoard = null)
        {
            int count = SlotCount, gap = CharacterGapPx;
            // Each pet has its own size; a hidden pet takes no room (an empty rect that nothing lays out, draws or clicks).
            var sizes = Enumerable.Range(0, count).Select(i => PetHidden(i) ? new Size(0, 0) : PetSize(i)).ToList();
            var rects = new System.Collections.Generic.List<Rect>();
            var spots = placement ?? Spots;
            bool precisePlacement = !companion && (placement != null || petLayoutScreenFitting);
            for (int i = 0; i < count; i++)
            {
                if (PetHidden(i)) { rects.Add(new Rect(0, 0, 0, 0)); continue; }
                double w = sizes[i].Width, h = sizes[i].Height;
                var spot = i < spots.Count ? spots[i] : null;
                // Default: side by side next to the calendar (widths add up; the first pet is the farthest out on the left).
                double x = HasX(spot) ? SpotX(spot, w, boardW)
                    : CharactersOnRight ? boardW + gap + sizes.Take(i).Sum(s => s.Width) : -gap - sizes.Skip(i).Sum(s => s.Width);
                double y = HasY(spot) ? SpotY(spot, h, boardH)
                    : Math.Max(0, boardH - h - 8) * Math.Max(0, Math.Min(100, PlacementVertical)) / 100 + 4;
                // A mode switch can move the board by a fractional DIP. Rounding the pet's local offset each time
                // accumulates screen-position drift; retain it and let WPF align the final visual to device pixels.
                rects.Add(new Rect(precisePlacement ? x : Math.Round(x), precisePlacement ? y : Math.Round(y), w, h));
            }
            // A pet on the default placement never lands on a dragged one: it goes on outward past it (a pet added, or shown
            // again, after the others were dragged — they are pinned then — used to stand right on the one by the calendar).
            var taken = Enumerable.Range(0, count).Where(i => rects[i].Width > 0 && HasX(i < spots.Count ? spots[i] : null)).Select(i => rects[i]).ToList();
            if (taken.Count > 0)
            {
                bool right = CharactersOnRight;
                var unplaced = Enumerable.Range(0, count).Where(i => rects[i].Width > 0 && !HasX(i < spots.Count ? spots[i] : null)).ToList();
                if (!right) unplaced.Reverse(); // the one nearest the calendar first: the others follow it out
                foreach (int i in unplaced)
                {
                    var r = rects[i];
                    for (int guard = 0; guard <= count; guard++)
                    {
                        var hit = taken.Where(t => Overlaps(t, r)).ToList();
                        if (hit.Count == 0) break;
                        r.X = right ? hit.Max(t => t.Right) : hit.Min(t => t.Left) - r.Width;
                    }
                    rects[i] = r;
                    taken.Add(r);
                }
            }
            if (fitToScreen) FitCalendarPetsToScreen(rects, boardW, boardH, screenBoard);
            return rects;
        }

        // Two pets' pictures really overlap (touching, or a pixel of rounding, does not count).
        private static bool Overlaps(Rect a, Rect b)
        {
            if (a.Width <= 0 || b.Width <= 0 || a.Height <= 0 || b.Height <= 0) return false;
            var o = Rect.Intersect(a, b);
            return !o.IsEmpty && o.Width > 1 && o.Height > 1;
        }

        /// <summary>
        /// After pets grew (one pet's size, every pet's, or all of them with the calendar's height): dragged pets each keep
        /// their distance from the calendar, so neighbours that stood apart came to overlap. The one farther from the
        /// calendar moves on out by the overlap (only along the way they stand side by side). Pets that already overlapped
        /// (put on each other on purpose) stay as they are; nothing moves when pets shrink.
        /// </summary>
        private void KeepPetsApart(System.Collections.Generic.IList<Rect> before)
        {
            if (before == null || closed || draggingPet >= 0) return;
            double boardW = BoardWidth, boardH = BoardHeight;
            // How far a pet stands out of the calendar (negative: over it).
            double Out(Rect r) => Math.Max(Math.Abs(r.X + r.Width / 2 - boardW / 2) - boardW / 2, Math.Abs(r.Y + r.Height / 2 - boardH / 2) - boardH / 2);
            bool moved = false;
            for (int pass = 0; pass < MaxCharacters * MaxCharacters; pass++)
            {
                var now = PetBoardRects(boardW, boardH);
                if (now.Count != before.Count) return;
                var order = Enumerable.Range(0, now.Count).Where(i => now[i].Width > 0 && before[i].Width > 0).OrderBy(i => Out(now[i])).ToList();
                bool any = false;
                for (int a = 0; a < order.Count && !any; a++)
                    for (int b = a + 1; b < order.Count && !any; b++)
                    {
                        int near = order[a], far = order[b];
                        if (Overlaps(before[near], before[far]) || !Overlaps(now[near], now[far])) continue;
                        var spot = SpotOf(far);
                        // Along the way the two stood apart before (side by side, or one above the other), clear of the near one.
                        double dx = before[far].X + before[far].Width / 2 - (before[near].X + before[near].Width / 2);
                        double dy = before[far].Y + before[far].Height / 2 - (before[near].Y + before[near].Height / 2);
                        bool alongX = Math.Abs(dx) >= Math.Abs(dy) ? HasX(spot) || !HasY(spot) : !HasY(spot) && HasX(spot);
                        if (alongX && HasX(spot))
                            StoreX(spot, dx >= 0 ? now[near].Right : now[near].X - now[far].Width, now[far].Width, boardW);
                        else if (!alongX && HasY(spot))
                            StoreY(spot, dy >= 0 ? now[near].Bottom : now[near].Y - now[far].Height, now[far].Height, boardH);
                        else continue;
                        any = moved = true;
                    }
                if (!any) break;
            }
            if (!moved) return;
            ApplyPetLayout();
            if (loadedSlotKeys != null) RememberSpots();
            SaveSoon();
        }

        /// <summary>Pets' rectangles in the character view (= window) coordinates.</summary>
        private System.Collections.Generic.List<Rect> PetRects()
        {
            var offset = new Vector(Edge + petPads.Left, Edge + petPads.Top);
            return PetBoardRects(BoardWidth, BoardHeight).Select(r => Rect.Offset(r, offset)).ToList();
        }

        /// <summary>Re-fits the window around the board and the pets, keeping the board where it is on screen.</summary>
        private void ApplyPetLayout(bool save = true, MonitorStateData board = null)
        {
            if (closed || CharacterHost == null || MiniRoot == null || layingOutPets) return;
            if (modeTransitionTo != null && board == null) return; // only the destination's explicit layout runs during a transition
            layingOutPets = true;
            try
            {
                // A DPI change may have left the window's pixels out of step with Width / Height: put them back first. Sizing
                // it here otherwise made WPF read the wrong pixel size back into Width — and the calendar shrank by half.
                if (!resizingBoard && board == null) SyncWindowPixels();
                double boardW = board?.Width ?? BoardWidth, boardH = board?.Height ?? BoardHeight;
                double boardLeft = board?.Left ?? (Left + Edge + petPads.Left), boardTop = board?.Top ?? (Top + Edge + petPads.Top);
                petLayoutBoard = new MonitorStateData { Left = boardLeft, Top = boardTop, Width = boardW, Height = boardH };
                petLayoutScreenFitting = data.MiniCompactPetLayout != null || data.MiniMonthPetLayout != null;
                bool shown = PetsShown;
                var rects = shown ? PetBoardRects(boardW, boardH).Where(r => r.Width > 0).ToList() : new System.Collections.Generic.List<Rect>();
                var pads = new Thickness(
                    rects.Count == 0 ? 0 : Math.Max(0, -rects.Min(r => r.Left)),
                    rects.Count == 0 ? 0 : Math.Max(0, -rects.Min(r => r.Top)),
                    rects.Count == 0 ? 0 : Math.Max(0, rects.Max(r => r.Right) - boardW),
                    rects.Count == 0 ? 0 : Math.Max(0, rects.Max(r => r.Bottom) - boardH));
                if (placingPets && shown && rects.Count > 0)
                {
                    // While dragging pets into place, keep generous room all round so the window does not resize on
                    // every mouse move (it shrinks back to fit when the placement ends). (Larger than the screen is fine:
                    // AllowLargerThanScreen keeps Windows from cutting the window, and the calendar in it, down.)
                    double w = rects.Max(r => r.Width), h = rects.Max(r => r.Height);
                    // The room above also holds the 완료 banner, right above the calendar's top band (see PlacePlacementBanner).
                    double room = PlacementBannerRoom();
                    pads = new Thickness(Math.Max(pads.Left, 3 * w + 60), Math.Max(pads.Top, Math.Max(h + 20, room)),
                        Math.Max(pads.Right, 3 * w + 60), Math.Max(pads.Bottom, Math.Max(h + 20, room)));
                }
                if (resizingBoard) pads = petPads; // a corner-grip drag resizes the board; the room around it is re-fitted when it ends
                else pads = SnapPadsToPixels(pads, boardW, boardH);
                bool padsChanged = pads != petPads;
                petPads = pads;
                MiniRoot.Margin = new Thickness(Edge + pads.Left, Edge + pads.Top, Edge + pads.Right, Edge + pads.Bottom);
                if ((padsChanged || board != null) && !resizingBoard)
                {
                    // Grow/shrink around the board so it stays still (before the first placement only the size matters).
                    if (board != null)
                        SetCalendarWindowBounds(new Rect(boardLeft - Edge - pads.Left, boardTop - Edge - pads.Top,
                            boardW + 2 * Edge + pads.Left + pads.Right, boardH + 2 * Edge + pads.Top + pads.Bottom));
                    else
                    {
                        if (IsFinite(Left) && IsFinite(Top)) { Left = boardLeft - Edge - pads.Left; Top = boardTop - Edge - pads.Top; }
                        Width = boardW + 2 * Edge + pads.Left + pads.Right;
                        Height = boardH + 2 * Edge + pads.Top + pads.Bottom;
                        requestedSize = new Size(Width, Height);
                    }
                }
                CharacterHost.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                var inView = PetRects();
                // The WebView only needs to cover the pets (its frames are copied into the window on every animation step),
                // not the whole window. While dragging pets into place it covers the window so the view never moves under them.
                var visibleInView = inView.Where(r => r.Width > 0).ToList();
                var frame = placingPets || visibleInView.Count == 0
                    ? new Rect(0, 0, Math.Max(1, WindowWidthNow), Math.Max(1, WindowHeightNow))
                    : visibleInView.Aggregate(Rect.Union);
                // The view's capture is rebuilt a moment after its size changes; meanwhile it may show the old-size picture
                // cut off (a pet's head only) — until the next health check. Nudge it once the new size is laid out.
                bool viewResized = Math.Abs(frame.Width - characterViewFrame.Width) > 0.5 || Math.Abs(frame.Height - characterViewFrame.Height) > 0.5;
                characterViewFrame = frame;
                if (viewResized && !placingPets) RepaintPetsSoon();
                Canvas.SetLeft(CharacterView, frame.X);
                Canvas.SetTop(CharacterView, frame.Y);
                CharacterView.Width = Math.Max(1, frame.Width);
                CharacterView.Height = Math.Max(1, frame.Height);
                var hits = new[] { PetHit0, PetHit1, PetHit2 };
                for (int i = 0; i < hits.Length; i++)
                {
                    bool used = shown && i < inView.Count && inView[i].Width > 0; // hidden pets get no hit box
                    hits[i].Visibility = used ? Visibility.Visible : Visibility.Collapsed;
                    hits[i].Cursor = placingPets ? (CanDragPet(i) ? Cursors.SizeAll : Cursors.Arrow) : Cursors.Hand;
                    if (!used) continue;
                    // A little inset so the empty corners of a sprite cell do not catch clicks meant for the calendar.
                    Canvas.SetLeft(hits[i], inView[i].X + inView[i].Width * 0.12);
                    Canvas.SetTop(hits[i], inView[i].Y + inView[i].Height * 0.1);
                    hits[i].Width = inView[i].Width * 0.76;
                    hits[i].Height = inView[i].Height * 0.86;
                }
                PlaceCharacterError(inView); // at the pet whose image failed
                if (placingPets) PlacePlacementBanner();
                SendPetLayout();
                if (padsChanged && save && !resizingBoard && !placingPets) { KeepOnScreen(); SavePosition(); }
            }
            finally { layingOutPets = false; }
        }

        // Windows (and WPF with it) keeps a window no bigger than the screen (the maximum tracking size). The window is the
        // board plus the room its pets take around it: a big calendar with big pets beside it went past that, the window was
        // cut down and the calendar inside squeezed — then saved smaller. The largest size a window may have lifts that limit
        // (the pets may stand partly off the screen, as they always could; the calendar keeps its size).
        private const int WmGetMinMaxInfo = 0x0024, MaxWindowSide = 32000;
        private Size requestedSize = new Size(double.NaN, double.NaN); // the window size last set here (before it was shown too)

        private static IntPtr AllowLargerThanScreen(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmGetMinMaxInfo || lParam == IntPtr.Zero) return IntPtr.Zero;
            // MINMAXINFO: ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize (POINTs of two ints).
            System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 32, MaxWindowSide);
            System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 36, MaxWindowSide);
            return IntPtr.Zero; // not handled: WPF's own handler then reads (and keeps) this limit
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        // Windows asks for the size limits only at creation and on some resizes (not when WPF sets Width / Height here), so
        // WPF would go on using the screen-sized limit it got at creation: ask once more, through the hook, with the
        // system's own values in the other fields.
        private static void AskSizeLimits(IntPtr hwnd)
        {
            IntPtr info = System.Runtime.InteropServices.Marshal.AllocHGlobal(40);
            try
            {
                for (int offset = 0; offset < 40; offset += 4) System.Runtime.InteropServices.Marshal.WriteInt32(info, offset, 0);
                const int SmCxMaxTrack = 59, SmCyMaxTrack = 60, SmCxMinTrack = 34, SmCyMinTrack = 35, SmCxMaximized = 61, SmCyMaximized = 62;
                System.Runtime.InteropServices.Marshal.WriteInt32(info, 8, GetSystemMetrics(SmCxMaximized));
                System.Runtime.InteropServices.Marshal.WriteInt32(info, 12, GetSystemMetrics(SmCyMaximized));
                System.Runtime.InteropServices.Marshal.WriteInt32(info, 24, GetSystemMetrics(SmCxMinTrack));
                System.Runtime.InteropServices.Marshal.WriteInt32(info, 28, GetSystemMetrics(SmCyMinTrack));
                System.Runtime.InteropServices.Marshal.WriteInt32(info, 32, GetSystemMetrics(SmCxMaxTrack));
                System.Runtime.InteropServices.Marshal.WriteInt32(info, 36, GetSystemMetrics(SmCyMaxTrack));
                SendMessage(hwnd, WmGetMinMaxInfo, IntPtr.Zero, info);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(info); }
        }

        /// <summary>
        /// The room round the board, grown by less than a device pixel so the board's corner and the window's size fall on
        /// whole pixels. Windows sizes a window in whole pixels and WPF writes that back into Width / Height; the board is the
        /// window less this room, so at 150 % the rounding (up to 2/3 DIP) went into the calendar — which then changed size by
        /// a pixel with pet or music bar changes, and was saved so.
        /// </summary>
        private Thickness SnapPadsToPixels(Thickness pads, double boardW, double boardH)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1, sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
            double Up(double dips, double scale) => Math.Ceiling(dips * scale - 1e-6) / scale;
            double left = Up(Edge + pads.Left, sx) - Edge, top = Up(Edge + pads.Top, sy) - Edge;
            double right = Up(boardW + 2 * Edge + left + pads.Right, sx) - boardW - 2 * Edge - left;
            double bottom = Up(boardH + 2 * Edge + top + pads.Bottom, sy) - boardH - 2 * Edge - top;
            return new Thickness(left, top, Math.Max(pads.Right, right), Math.Max(pads.Bottom, bottom));
        }

        /// <summary>This window's monitor work area in DIPs (the main one before the window has a handle).</summary>
        private Rect WorkAreaDip()
        {
            if (NativeMethods.TryGetWorkArea(new WindowInteropHelper(this).Handle, out NativeMethods.Rect area))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
                    Math.Max(0, area.Right - area.Left) / dpi.DpiScaleX, Math.Max(0, area.Bottom - area.Top) / dpi.DpiScaleY);
            }
            return SystemParameters.WorkArea;
        }

        private const double PlacementBannerGap = 6;

        // Height the 완료 banner needs (measured while shown, at the width it gets over the calendar: its text may wrap).
        private double PlacementBannerRoom()
        {
            var margin = PlacementBanner.Margin;
            PlacementBanner.Measure(new Size(Math.Max(120, BoardWidth - 16) + margin.Left + margin.Right, double.PositiveInfinity));
            // DesiredSize holds the banner's margin too (its offset from the last placing): only its own height counts.
            return Math.Max(40, PlacementBanner.DesiredSize.Height - margin.Top - margin.Bottom) + PlacementBannerGap;
        }

        /// <summary>
        /// 드래그로 위치 설정's banner (처음 배치로 / 완료) sits right above the calendar, over the period label between the two
        /// binder rings (centred on the board); right below the calendar when the screen has no room above it, and on the
        /// calendar as before only when neither shows on the screen.
        /// </summary>
        private void PlacePlacementBanner()
        {
            if (!placingPets) return;
            double room = PlacementBannerRoom(), boardTop = Edge + petPads.Top, boardBottom = boardTop + BoardHeight;
            Rect area = WorkAreaDip();
            double screenTop = IsFinite(Top) ? area.Top - Top : double.NegativeInfinity;
            double screenBottom = IsFinite(Top) ? area.Bottom - Top : double.PositiveInfinity;
            double above = boardTop - room, below = boardBottom + PlacementBannerGap;
            double y;
            if (above >= 0 && above >= screenTop) y = above;
            else if (below + room - PlacementBannerGap <= WindowHeightNow && below + room - PlacementBannerGap <= screenBottom) y = below;
            else y = boardTop + 44;
            // Centered over the board: the period label between the rings.
            PlacementBanner.Margin = new Thickness(Edge + petPads.Left + 8, Math.Max(0, y), Edge + petPads.Right + 8, 0);
        }

        // ---- Pets beside the TODO window (the 👤 button there): the same pets, settings and clicks, no calendar; they
        // stand around the TODO window with a placement of their own ----
        private readonly bool companion;
        /// <summary>Raised when 캐릭터 설정's "캐릭터 표시" is turned off on the TODO window's pets (the 👤 button then turns off).</summary>
        public event Action CompanionHideRequested;
        private bool PetsShown => companion || data.MiniCharacterVisible;

        /// <summary>TODO window's pets: stand around this screen rectangle (DIPs) — the TODO window — instead of a calendar.</summary>
        public void FollowBoard(Rect board)
        {
            if (closed || !companion || board.Width <= 0 || board.Height <= 0) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = board.X - Edge - petPads.Left;
            Top = board.Y - Edge - petPads.Top;
            Width = board.Width + 2 * Edge + petPads.Left + petPads.Right;
            Height = board.Height + 2 * Edge + petPads.Top + petPads.Bottom;
            requestedSize = new Size(Width, Height);
            UpdateCharacterSize();
        }

        // ---- 업데이트 알림 (MainWindow.UpdateNotice.cs): where its speech bubble points ----

        /// <summary>The bubble stays away meanwhile: 드래그로 위치 설정, a pet being dragged, a week flip, or closed.</summary>
        internal bool UpdateNoticeBusy => closed || placingPets || draggingPet >= 0 || weekFlip != null;

        /// <summary>The first shown pet's picture in screen DIPs (false: pets off, or every pet hidden).</summary>
        internal bool TryGetUpdateNoticePet(out Rect screen)
        {
            screen = Rect.Empty;
            if (closed || !PetsShown || CharacterHost == null) return false;
            foreach (var r in PetRects())
            {
                if (r.Width <= 0 || r.Height <= 0) continue;
                screen = new Rect(Left + r.X, Top + r.Y, r.Width, r.Height);
                return true;
            }
            return false;
        }

        /// <summary>The calendar's top band (the period label bar) in screen DIPs; the TODO window's pets have none.</summary>
        internal bool TryGetUpdateNoticeCalendar(out Rect screen)
        {
            screen = Rect.Empty;
            if (closed || companion || CalendarHeader == null || !CalendarHeader.IsVisible || CalendarHeader.ActualWidth <= 0) return false;
            try
            {
                var band = CalendarHeader.TransformToAncestor(this).TransformBounds(new Rect(0, 0, CalendarHeader.ActualWidth, CalendarHeader.ActualHeight));
                screen = new Rect(Left + band.X, Top + band.Y, band.Width, band.Height);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        internal Rect UpdateNoticeWorkArea => WorkAreaDip();

        // ---- 캐릭터 설정 window (right-click a pet): IPetSettingsHost ----
        public event Action PetsChanged;
        private PetSettingsWindow petSettings;

        int IPetSettingsHost.PetCount => SlotCount;
        int IPetSettingsHost.MaxPets => MaxCharacters;
        string IPetSettingsHost.PetName(int index) => index >= 0 && index < slotCharacters.Count ? slotCharacters[index].Name : (index + 1) + "번째 캐릭터";
        string IPetSettingsHost.PetAnimation(int index) => SlotAnimation(index);
        // A still image / GIF has no sprite rows to act out. Not loaded yet: assumed to animate.
        bool IPetSettingsHost.PetAnimates(int index) => index < 0 || index >= slotCharacters.Count || slotCharacters[index].Rows > 0;
        System.Collections.Generic.IReadOnlyList<(string Key, string Label)> IPetSettingsHost.PetAnimationOptions(int index) =>
            AnimationOptions(index >= 0 && index < slotCharacters.Count ? slotCharacters[index] : null);

        // 캐릭터 설정 acts on the pets its list shows. A list from before a pet was removed (by the other pets window, or a
        // broken pet dropped) may point past them: such a click does nothing but bring the list up to date.
        private bool IsPet(int index)
        {
            if (index >= 0 && index < SlotCount) return true;
            PetsChanged?.Invoke();
            return false;
        }

        void IPetSettingsHost.SetPetAnimation(int index, string key)
        {
            if (!IsPet(index)) return;
            SetSlotAnimation(index, key);
            changed?.Invoke();
            PostToPage(JsonConvert.SerializeObject(new { action = "animation", index, state = key }));
            UpdateActivePetState();
        }
        void IPetSettingsHost.ChangePet(int index) { if (!IsPet(index)) return; petSettings?.Close(); EndPetPlacement(); activeSlot = index; OpenCharacterPicker(); }
        void IPetSettingsHost.AddPet()
        {
            // The new pet starts at the size of the pet 캐릭터 설정 shows, not of the last one right-clicked.
            if (petSettings != null) activeSlot = Math.Max(0, Math.Min(SlotCount - 1, petSettings.SelectedPet));
            AddCharacter_Click(this, new RoutedEventArgs());
        }
        void IPetSettingsHost.RemovePet(int index) { if (!IsPet(index)) return; activeSlot = index; RemoveCharacter_Click(this, new RoutedEventArgs()); }
        int IPetSettingsHost.PetScale(int index) => SlotScale(index);
        void IPetSettingsHost.SetPetScale(int index, int percent)
        {
            if (!IsPet(index)) return;
            PinOtherPetPositions(index);
            SetSlotScale(index, percent);
            UpdateCharacterSize();
            SaveSoon();
            MiniSettingsStateChanged?.Invoke();
        }
        /// <summary>캐릭터 설정's 초기화: the pets stay, everything about how they look and stand goes back to the defaults.
        /// From the TODO window's pets it resets their own placement and leaves the mini window's (and its 캐릭터 표시) alone.</summary>
        public void ResetPetDefaults()
        {
            if (!companion) data.MiniCharacterVisible = true; // the TODO window's pets are shown by its 👤, not this
            data.MiniFirstPetHidden = false;
            data.CharacterAnimation = "idle";
            data.MiniFirstPetFlipped = false;
            foreach (var slot in data.MiniExtraCharacters) { slot.Hidden = false; slot.Scale = null; slot.Animation = "idle"; slot.Flipped = false; }
            data.MiniCharacterScale = 100;
            Spots.Clear();
            RememberSpots(forgetUnplaced: true); // 초기화: the characters in the slots forget their spots; others keep theirs
            PlacementSide = "Left";
            PlacementVertical = 100;
            PlacementGap = 8;
            UpdateCharacterVisibility();
            UpdateCharacterSize();
            for (int i = 0; i < SlotCount; i++)
                PostToPage(JsonConvert.SerializeObject(new { action = "animation", index = i, state = "idle" }));
            SendPetFlips();
            UpdateActivePetState();
            SaveSoon();
        }

        bool IPetSettingsHost.PetFlipped(int index) => PetFlipped(index);
        void IPetSettingsHost.SetPetFlipped(int index, bool flipped)
        {
            if (!IsPet(index)) return;
            SetPetFlipped(index, flipped);
            PostToPage(JsonConvert.SerializeObject(new { action = "flip", index, flipped }));
            SaveSoon();
            PetsChanged?.Invoke();
        }

        bool IPetSettingsHost.PetVisible(int index) => !PetHidden(index);
        void IPetSettingsHost.SetPetVisible(int index, bool visible)
        {
            if (!IsPet(index)) return;
            // The TODO window's pets have no calendar to right-click: the last one showing there stays (see PetSettingsWindow).
            if (companion && !visible && !PetHidden(index) && Enumerable.Range(0, SlotCount).Count(i => !PetHidden(i)) <= 1)
            { PetsChanged?.Invoke(); return; }
            PinOtherPetPositions(index);
            SetPetHidden(index, !visible);
            ApplyPetLayout();
            SaveSoon();
            PetsChanged?.Invoke();
        }
        // 캐릭터 설정 saves at once (the public ones also serve 설정's live preview, which saves only on 저장).
        void IPetSettingsHost.SetCharacterVisible(bool visible) { SetCharacterVisible(visible); if (!closed) SaveSoon(); }
        void IPetSettingsHost.SetCharacterPlacement(string side, int vertical, int gap) { SetCharacterPlacement(side, vertical, gap); if (!closed) SaveSoon(); }
        // Whether these pets show: the mini window's 캐릭터 표시, or — for the TODO window's pets — always while they exist
        // (turning it off there turns off the 👤 button).
        bool IPetSettingsHost.CharactersVisible => PetsShown;
        bool IPetSettingsHost.BesideTodoWindow => companion;
        string IPetSettingsHost.CharacterSide => PlacementSide;
        int IPetSettingsHost.CharacterVertical => PlacementVertical;
        int IPetSettingsHost.CharacterGap => PlacementGap;

        /// <summary>Opens (or switches) the 캐릭터 설정 window for this pet, next to the pets.</summary>
        public void OpenPetSettings(int pet)
        {
            if (closed) return;
            CloseDayPopup();
            activeSlot = Math.Max(0, Math.Min(SlotCount - 1, pet));
            UpdateActivePetState();
            if (CharacterSettingsRequested != null) { CharacterSettingsRequested(activeSlot); return; }
            if (petSettings != null) { petSettings.Select(activeSlot); petSettings.Activate(); return; }
            petSettings = new PetSettingsWindow(this, activeSlot) { Owner = this };
            petSettings.Closed += (s, e) => petSettings = null;
            // Beside the window, on the side with more room — within the work area of the monitor the pets are on (in
            // this window's DIPs, like KeepOnScreen), not the main monitor's.
            var screen = PetsWorkArea();
            double width = petSettings.Width, height = petSettings.Height;
            petSettings.Left = Left + WindowWidthNow + 8 + width <= screen.Right ? Left + WindowWidthNow + 8 : Math.Max(screen.Left, Left - width - 8);
            petSettings.Top = Math.Max(screen.Top, Math.Min(Top, screen.Bottom - height));
            petSettings.Show();
        }

        // The work area (DIPs) of the monitor this window is on; the main monitor's when that cannot be told.
        internal Rect PetsWorkArea()
        {
            if (!NativeMethods.TryGetWorkArea(new WindowInteropHelper(this).Handle, out NativeMethods.Rect area)) return SystemParameters.WorkArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
                Math.Max(0, area.Right - area.Left) / dpi.DpiScaleX, Math.Max(0, area.Bottom - area.Top) / dpi.DpiScaleY);
        }

        /// <summary>Puts back dragged pet spots (settings 취소 after a preview reset them).</summary>
        public void RestorePetSpots(System.Collections.Generic.IEnumerable<MiniPetSpot> spots)
        {
            Spots = spots.Select(CopySpot).ToList();
            RememberSpots(forgetUnplaced: true);
            ApplyPetLayout();
            MiniSettingsStateChanged?.Invoke();
        }

        // ---- 캐릭터별 마지막 위치: each character remembers its own spot, and gets it back when it returns to a slot ----
        private System.Collections.Generic.List<string> loadedSlotKeys; // character ids in the slots at the last reload

        // (The TODO window's pets remember theirs apart from the mini window's.)
        private System.Collections.Generic.Dictionary<string, MiniPetSpot> SpotMemory => companion
            ? data.CompanionPetSpotsByCharacter ?? (data.CompanionPetSpotsByCharacter = new System.Collections.Generic.Dictionary<string, MiniPetSpot>())
            : data.MiniPetSpotsByCharacter ?? (data.MiniPetSpotsByCharacter = new System.Collections.Generic.Dictionary<string, MiniPetSpot>());

        private static bool IsPlaced(MiniPetSpot s) => HasX(s) || HasY(s);

        private static void CopyInto(MiniPetSpot target, MiniPetSpot source)
        {
            target.Left = source?.Left; target.Top = source?.Top;
            target.EdgeX = source?.EdgeX; target.OffsetX = source?.OffsetX ?? 0;
            target.EdgeY = source?.EdgeY; target.OffsetY = source?.OffsetY ?? 0;
        }

        // What a character remembers: the place only (not whose slot it was).
        private static MiniPetSpot MemoryCopy(MiniPetSpot s) { var copy = CopySpot(s); copy.Key = null; return copy; }

        private static bool SamePlace(MiniPetSpot a, MiniPetSpot b) =>
            a != null && b != null && a.Left == b.Left && a.Top == b.Top && a.EdgeX == b.EdgeX && a.EdgeY == b.EdgeY &&
            (a.EdgeX == null || a.OffsetX == b.OffsetX) && (a.EdgeY == null || a.OffsetY == b.OffsetY);

        // "Pet/abc#2" → "Pet/abc": a copy's number follows the slot order, so it changes when an earlier copy goes.
        private static string CharacterOfKey(string key)
        {
            int mark = key?.LastIndexOf('#') ?? -1;
            return mark > 0 && mark < key.Length - 1 && key.Skip(mark + 1).All(char.IsDigit) ? key.Substring(0, mark) : key;
        }

        // Whether slot i's spot is its character's own: a spot knows whose it is; an old one (no Key) is judged by the
        // characters at the last load. The same character still in the slot counts even when its copy number changed.
        private bool SpotBelongs(MiniPetSpot spot, int i, string key)
        {
            if (spot?.Key != null) return string.Equals(CharacterOfKey(spot.Key), CharacterOfKey(key), StringComparison.Ordinal);
            return loadedSlotKeys == null || i < loadedSlotKeys.Count && loadedSlotKeys[i] == key;
        }

        // The memory key of each slot's character. The same character in two slots gets "#2" on the second one, so the
        // two copies keep two spots (one key for both made the second copy land exactly on top of the first).
        internal System.Collections.Generic.List<string> SlotKeys() => SlotKeysOf(data);

        /// <summary>The mini window's placement changed with the window closed (settings 저장 / 초기화): like
        /// RememberSpots(forgetUnplaced) there, the characters in the slots keep their spot or forget it, copies ("#2") too.</summary>
        public static void RememberMiniSpots(AppData data)
        {
            if (data.MiniPetSpotsByCharacter == null) data.MiniPetSpotsByCharacter = new System.Collections.Generic.Dictionary<string, MiniPetSpot>();
            var keys = SlotKeysOf(data);
            for (int i = 0; i < keys.Count; i++)
            {
                var spot = data.MiniPetSpots != null && i < data.MiniPetSpots.Count ? data.MiniPetSpots[i] : null;
                if (spot?.Key != null && !string.Equals(CharacterOfKey(spot.Key), CharacterOfKey(keys[i]), StringComparison.Ordinal)) continue; // another's
                if (IsPlaced(spot)) data.MiniPetSpotsByCharacter[keys[i]] = MemoryCopy(spot);
                else data.MiniPetSpotsByCharacter.Remove(keys[i]);
            }
        }

        public static System.Collections.Generic.List<string> SlotKeysOf(AppData data)
        {
            var keys = new System.Collections.Generic.List<string>();
            int count = 1 + Math.Min(MaxCharacters - 1, data.MiniExtraCharacters?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                string key = CharacterCatalog.SelectionKey(i == 0 ? data.CharacterManifest : data.MiniExtraCharacters[i - 1].Manifest);
                int copy = keys.Count(k => k == key || k.StartsWith(key + "#", StringComparison.Ordinal));
                keys.Add(copy == 0 ? key : key + "#" + (copy + 1));
            }
            return keys;
        }

        /// <summary>Stores each slot's spot under its character. With forgetUnplaced, a slot back on its default
        /// placement also forgets that character's old spot (초기화 / 처음 배치로 / a new 옆·간격).</summary>
        private void RememberSpots(bool forgetUnplaced = false)
        {
            var memory = SpotMemory;
            var slotKeys = SlotKeys();
            for (int i = 0; i < slotKeys.Count; i++)
            {
                string key = slotKeys[i];
                var spot = i < Spots.Count ? Spots[i] : null;
                // A slot whose character was just changed (and not reloaded yet) still holds the previous one's spot.
                if (!SpotBelongs(spot, i, key)) continue;
                if (spot != null) spot.Key = key;
                if (IsPlaced(spot)) memory[key] = MemoryCopy(spot);
                else if (forgetUnplaced) memory.Remove(key);
            }
        }

        /// <summary>
        /// Called on every (re)load: a slot whose character changed (바꾸기, 추가, 빼기, a deleted pet falling back to Codex)
        /// takes that character's remembered spot, or the default placement; unchanged slots record their current spot.
        /// Returns true when a spot changed.
        /// </summary>
        internal bool SyncSpotMemory()
        {
            var memory = SpotMemory;
            var keys = SlotKeys();
            bool changedAny = false;
            // Each spot knows whose it is (Key), so a character changed or a pet removed by the other pets window — even while
            // this one was closed — is told apart from a pet that stayed (one without a Key: the old way, by the last load).
            var same = new bool[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                bool fresh = i >= Spots.Count; // a pet this placement never had a spot for (added while this window was closed)
                var spot = SpotOf(i);
                same[i] = !fresh && SpotBelongs(spot, i, keys[i]);
                if (!same[i]) continue;
                spot.Key = keys[i];
                if (IsPlaced(spot)) memory[keys[i]] = MemoryCopy(spot);
            }
            for (int i = 0; i < keys.Count; i++)
            {
                if (same[i]) continue;
                var spot = SpotOf(i);
                memory.TryGetValue(keys[i], out MiniPetSpot remembered);
                // A copy's memory ("#2") may be where another pet stands now: never put this one on top of it.
                for (int j = 0; j < keys.Count && remembered != null; j++)
                    if (j != i && same[j] && SamePlace(Spots[j], remembered)) remembered = null;
                spot.Key = keys[i];
                if (IsPlaced(spot) == IsPlaced(remembered) && (!IsPlaced(spot) || SamePlace(spot, remembered))) continue;
                CopyInto(spot, remembered); // null → back to the default placement
                changedAny = true;
            }
            loadedSlotKeys = keys;
            if (changedAny) changed?.Invoke();
            return changedAny;
        }

        /// <summary>Settings (live preview too): side "Left"/"Right", vertical 0~100 %, gap -80~60 px — of this window's pets.
        /// These give the default spots; changing side or gap puts dragged pets back to them, 세로 위치 resets heights.</summary>
        public void SetCharacterPlacement(string side, int vertical, int gap)
        {
            side = string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase) ? "Right" : "Left";
            vertical = Math.Max(0, Math.Min(100, vertical));
            gap = Math.Max(-80, Math.Min(60, gap));
            if (side == PlacementSide && vertical == PlacementVertical && gap == PlacementGap) return;
            if (side != PlacementSide || gap != PlacementGap) Spots.Clear();
            else foreach (var spot in Spots) { spot.Top = null; spot.EdgeY = null; }
            PlacementSide = side;
            PlacementVertical = vertical;
            PlacementGap = gap;
            RememberSpots(forgetUnplaced: true);
            ApplyPetLayout();
            MiniSettingsStateChanged?.Invoke();
        }

        // Settings drafts use this to notice placement or drag changes made by the other pets window while they are open.
        public event Action MiniSettingsStateChanged;

        private void MiniRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Not in the middle of a layout pass (ApplyPetLayout sets Left / Top / Width / Height one at a time: in between the
            // board's height reads half-updated, and the pets were re-sized from it — then left larger than the window, cut
            // off, or shrunk) nor while a corner grip drags (the pets re-fit when it ends).
            if (e.HeightChanged && !layingOutPets && !resizingBoard && !changingCalendarMode) UpdateCharacterSize();
        }
    }
}

namespace ScheduleWidget
{
    // WPF may wrap Korean between any two syllables ("캡스톤신 / 청서"). For display, glue the characters of each
    // Korean/CJK word with invisible WORD JOINERs so lines break at spaces, like CSS `word-break: keep-all`.
    // A word wider than the whole line is still broken by TextWrapping.Wrap.
    public sealed class KeepAllConverter : System.Windows.Data.IValueConverter
    {
        internal const char WordJoiner = '\u2060';

        public static string Apply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var result = new System.Text.StringBuilder(text.Length * 2);
            for (int i = 0; i < text.Length; i++)
            {
                result.Append(text[i]);
                if (i + 1 < text.Length && !char.IsWhiteSpace(text[i]) && !char.IsWhiteSpace(text[i + 1]) &&
                    (IsCjk(text[i]) || IsCjk(text[i + 1])))
                    result.Append(WordJoiner);
            }
            return result.ToString();
        }

        private static bool IsCjk(char c) =>
            c >= '\u1100' && c <= '\u11FF' || c >= '\u3130' && c <= '\u318F' || c >= '\uAC00' && c <= '\uD7AF' ||
            c >= '\u3040' && c <= '\u30FF' || c >= '\u4E00' && c <= '\u9FFF';

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Apply(value as string);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            (value as string)?.Replace(WordJoiner.ToString(), string.Empty);
    }
}

namespace ScheduleWidget
{
    // What the mini window's player bar needs from the music player (implemented by MainWindow).
    public enum PlayMode { Sequential, Shuffle, RepeatOne }

    public interface IMusicControls
    {
        bool HasTracks { get; }
        bool IsPlaying { get; }
        string NowPlaying { get; }
        PlayMode Mode { get; }
        void TogglePlay();
        void Next();
        void Previous();
        void SetMode(PlayMode mode);
        void ToggleSettings(); // playlist settings (music window): open, or close when it is already open on screen
        // Seek bar: position and length of what the bar controls now (widget song or the chosen app); null = unknown.
        TimeSpan? Position { get; }
        TimeSpan? Duration { get; }
        void Seek(TimeSpan position);
        double Volume { get; }            // 0~1
        void SetVolume(double value);
        System.Collections.Generic.IReadOnlyList<MusicPlaylist> Playlists { get; }
        MusicPlaylist CurrentPlaylist { get; }
        void SelectPlaylist(MusicPlaylist list);
        // Other apps playing media (browser YouTube / YouTube Music, Spotify, …); choosing one makes the bar control it.
        System.Collections.Generic.IReadOnlyList<SystemMediaService.NowPlaying> ExternalSources { get; }
        string SelectedExternal { get; }   // null while a playlist is the source
        bool AutomaticSource { get; }
        void SelectAutomaticSource();
        string SourceName { get; }         // what the dropdown shows
        void SelectExternal(string appId);
        // Current playlist in play order, the track playing now, and edits from the mini queue popup.
        System.Collections.Generic.IReadOnlyList<MusicTrack> Queue { get; }
        MusicTrack Current { get; }
        void Play(MusicTrack track);
        void Move(MusicTrack track, int index);
    }
}
