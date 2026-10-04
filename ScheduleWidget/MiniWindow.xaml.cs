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
            // The pets are sized from the board minus the music bar's room: know whether the bar shows before the first sizing
            // (it used to be counted as shown here and the pets jumped in size right after opening when it is hidden).
            if (!companion) PlayerBar.Visibility = player != null && data.MiniPlayerVisible ? Visibility.Visible : Visibility.Collapsed;
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
            };
            Loaded += async (s, e) =>
            {
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
            Closed += (s, e) => { FlushSave(); closed = true; MusicWindow.PlaybackChanged -= OnPlaybackChanged; recoveryTimer?.Stop(); healthTimer?.Stop(); pixelTimer?.Stop(); petWatchTimer?.Stop(); CharacterView.Dispose(); };
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
            IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) ScheduleHealthCheck("shown"); UpdatePetWatch(); };
            // Desktop-owned (survives Win+D) windows are not raised by Windows on click; raise like a normal app.
            PreviewMouseDown += (s, e) => { if (!companion) NativeMethods.RaiseAboveOtherApps(this); };
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
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Closed += (s, e) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
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
            // 여러 날 schedules on this page: shown on every day they cover.
            var ranges = data.Schedules.Where(s => s.IsMultiDay && s.Overlaps(weekStart, end)).ToList();
            var cells = Enumerable.Range(0, visibleCount).Select(offset =>
            {
                DateTime date = weekStart.AddDays(offset);
                int weekday = ((int)date.DayOfWeek + 6) % 7;
                var tasks = DayTasks(byDay[date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)], ranges, date);
                var dayColors = BlockColors(tasks);
                string holiday = KoreanHolidays.NameOf(date);
                string label = date.ToString("M월 d일 (ddd)", korean);
                var blocks = tasks.Select(s =>
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
            RefreshAllSchedules();
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
            Set("MiniTodayBorderBrush", next.TodayBorder); Set("MiniHolidayBrush", next.Holiday);
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

        // ---- Right-click menu on a single schedule block (수정 / 완료 / 삭제) ----
        private ScheduleItem blockItem;

        private void Block_RightClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (RecentlyDragged) return;
            var block = sender as FrameworkElement;
            var context = block?.DataContext;
            var item = context?.GetType().GetProperty("Item")?.GetValue(context) as ScheduleItem;
            if (item == null || !data.Schedules.Contains(item)) return;
            if (CloseRightClickMenus()) return;
            CloseDayPopup();
            RangePopup.IsOpen = false;
            blockItem = item;
            BlockPopupTitle.Text = (string.IsNullOrWhiteSpace(item.Time) ? "" : item.Time + " · ") + item.Title;
            BlockPopupTitle.ToolTip = item.Title;
            BlockDone.Content = item.IsCompleted ? "미완료" : "완료";
            ResetBlockDelete();
            BlockPopup.IsOpen = false;
            BlockPopup.PlacementTarget = block;
            RemeasurePopup(BlockPopup);
            BlockPopup.IsOpen = true;
        }

        // Centered above the block, the tail pointing down at it.
        private System.Windows.Controls.Primitives.CustomPopupPlacement[] PlaceBlockPopup(Size popupSize, Size targetSize, Point offset) =>
            new[] { new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, -popupSize.Height),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal) };

        private void ResetBlockDelete()
        {
            BlockDelete.Tag = null;
            BlockDelete.Content = "삭제";
            BlockDelete.ClearValue(BackgroundProperty);
            BlockDelete.ClearValue(BorderBrushProperty);
            ShowDeleteNote(BlockDeleteNote, null);
        }

        /// <summary>
        /// What deleting a schedule linked to Google Calendar does there (null when it is not linked): an event this app made
        /// (no guests) goes from Google too; any other stays there (see GoogleCalendarSync.DeletesOnGoogle). With 구글 캘린더
        /// 연동 off nothing happens there now, but the deletion is still carried out once it is on again: that is said for an
        /// event it will delete; one that stays gets no note, like an unlinked schedule (nothing will happen on Google).
        /// Shared with the TODO window's 삭제 confirmation.
        /// </summary>
        internal static string GoogleDeleteNote(GoogleCalendarSettings settings, ScheduleItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.GoogleEventId) || settings == null) return null;
            bool deletes = GoogleCalendarSync.DeletesOnGoogle(settings, item);
            if (settings.Enabled) return deletes ? "구글 캘린더에서도 삭제됩니다." : "구글 캘린더에는 남습니다.";
            return deletes ? "구글 캘린더 연동을 다시 켜면 구글에서도 삭제됩니다." : null;
        }

        // Under a 삭제 button waiting for its second press: the Google note for that schedule (hidden when there is none).
        private void ShowDeleteNote(TextBlock note, ScheduleItem item)
        {
            if (note == null) return;
            string text = GoogleDeleteNote(data.GoogleCalendar, item);
            note.Text = text ?? string.Empty;
            note.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // Saves through `changed`, which in the app also refreshes this window (via the TODO list): refreshed here only when
        // it did not (the day bubble comes along with Refresh).
        private bool SaveAndRefresh()
        {
            int before = refreshCount;
            bool saved = changed == null || changed();
            if (refreshCount == before) Refresh();
            return saved;
        }

        private void SaveBlockChange() => SaveAndRefresh();

        private void BlockDone_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var item = blockItem;
            BlockPopup.IsOpen = false;
            if (item == null || !data.Schedules.Contains(item)) return;
            item.IsCompleted = !item.IsCompleted;
            SaveBlockChange();
        }

        private void BlockColor_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            BlockPopup.IsOpen = false;
            if (blockItem != null && data.Schedules.Contains(blockItem)) ScheduleColorRequested?.Invoke(blockItem);
        }

        private void BlockDefaultColor_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            BlockPopup.IsOpen = false;
            var item = blockItem;
            if (item == null || !data.Schedules.Contains(item)) return;
            string previous = item.Color;
            item.Color = null;
            if (!SaveAndRefresh()) { item.Color = previous; Refresh(); }
        }

        private void BlockMessage_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            BlockPopup.IsOpen = false;
            if (blockItem != null && data.Schedules.Contains(blockItem)) ScheduleMessageRequested?.Invoke(blockItem);
        }

        // First press arms ("정말 삭제", darker red), second press deletes — same as the day bubble.
        private void BlockDelete_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var item = blockItem;
            if (item == null || !data.Schedules.Contains(item)) { BlockPopup.IsOpen = false; return; }
            if (!Equals(BlockDelete.Tag, "armed"))
            {
                BlockDelete.Tag = "armed";
                BlockDelete.Content = "정말 삭제";
                BlockDelete.Background = BlockDelete.BorderBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x1C, 0x28));
                ShowDeleteNote(BlockDeleteNote, item);
                return;
            }
            BlockPopup.IsOpen = false;
            if (miniEditId == item.Id)
            {
                miniEditId = null;
                MiniEditorPanel.Visibility = Visibility.Collapsed;
            }
            data.Schedules.Remove(item);
            blockItem = null;
            SaveBlockChange();
        }

        // Opens that day's bubble with the editor for this schedule.
        private void BlockEdit_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var item = blockItem;
            BlockPopup.IsOpen = false;
            if (item == null || !data.Schedules.Contains(item)) return;
            if ((BlockPopup.PlacementTarget as FrameworkElement)?.DataContext is AllScheduleRow)
            {
                OpenAllScheduleEditor(item);
                return;
            }
            if (!DateTime.TryParseExact(item.Period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day)) return;
            var dayButton = FindDayButton(day.Date);
            // 여러 날 begun before this page: the first day of it the page shows.
            for (int i = 0; dayButton == null && item.IsMultiDay && i < dayCells.Count; i++)
                if (item.Covers(dayCells[i].Date)) dayButton = FindDayButton(dayCells[i].Date);
            if (dayButton == null) return;
            Day_Click(dayButton, new RoutedEventArgs(Button.ClickEvent));
            EditMiniSchedule_Click(new Button { DataContext = item }, new RoutedEventArgs(Button.ClickEvent));
        }

        private void BlockPopup_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            BlockPopup.IsOpen = false;
        }

        private Button FindDayButton(DateTime day)
        {
            Button Search(DependencyObject node)
            {
                for (int i = 0; node != null && i < VisualTreeHelper.GetChildrenCount(node); i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    if (child is Button b && b.Tag is DateTime d && d.Date == day) return b;
                    var found = Search(child);
                    if (found != null) return found;
                }
                return null;
            }
            return Search(WeekDays);
        }

        private void CompleteMiniSchedule_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as FrameworkElement)?.DataContext as ScheduleItem;
            if (item == null) return;
            item.IsCompleted = !item.IsCompleted;
            CommitDayChange(item.IsCompleted ? "완료로 표시했습니다." : "완료 표시를 취소했습니다.");
            e.Handled = true;
        }

        // First click arms the button ("정말 삭제"), second click deletes. The list refresh resets unconfirmed buttons.
        private void DeleteMiniSchedule_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button?.DataContext as ScheduleItem;
            if (item == null) return;
            e.Handled = true;
            if (!Equals(button.Tag, "armed"))
            {
                button.Tag = "armed";
                button.Content = "정말 삭제";
                // Darker red while waiting for the confirming second click.
                button.Background = button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x1C, 0x28));
                ShowDeleteNote(DayDeleteNote, item);
                return;
            }
            if (miniEditId == item.Id)
            {
                miniEditId = null;
                MiniEditorPanel.Visibility = Visibility.Collapsed;
            }
            data.Schedules.Remove(item);
            CommitDayChange("‘" + item.Title + "’ 일정을 삭제했습니다.");
        }

        private void CommitDayChange(string message)
        {
            bool saved;
            int before = refreshCount;
            keepDayPopupState = true;
            try { saved = changed == null || changed(); }
            finally { keepDayPopupState = false; }
            bool reopened = !DayPopup.IsOpen && selectedDay.HasValue;
            if (reopened) DayPopup.IsOpen = true;
            // The save's own refresh (through the TODO list) covers the calendar and the open bubble's list; refresh here
            // when it did not run, and the list when the bubble was closed meanwhile.
            if (refreshCount == before) Refresh();
            else if (reopened) RefreshDaySchedules();
            MiniEditorMessage.Text = saved ? message : "저장하지 못했습니다. 다시 시도해 주세요.";
        }

        private void RefreshDaySchedules()
        {
            if (!selectedDay.HasValue || DaySchedules == null) return;
            DateTime day = selectedDay.Value.Date;
            string period = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            // The day's own schedules and the 여러 날 ones covering it (their rows show the range: "10.08 (목) ~ 10.10 (토)").
            var tasks = DayTasks(data.Schedules.Where(s => s.Period == period && !s.IsMultiDay), data.Schedules.Where(s => s.IsMultiDay), day);
            string holidayName = KoreanHolidays.NameOf(day);
            DayPopupTitle.Text = day.ToString("M월 d일 (ddd)", CultureInfo.GetCultureInfo("ko-KR")) + (holidayName != null ? " · " + holidayName : "") + " · " + tasks.Count + "개 일정";
            DaySchedules.ItemsSource = tasks;
            MiniScheduleEmpty.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowDeleteNote(DayDeleteNote, null); // the rebuilt rows start unarmed
        }

        private void ResetMiniEditor(DateTime date)
        {
            miniEditId = null;
            miniDateInvalid = false;
            MiniTitleInput.Text = string.Empty;
            MiniDateInput.SelectedDate = date.Date;
            MiniTimeToggle.IsChecked = false;
            MiniTimeInput.Text = "09:00";
            MiniTimeInput.IsEnabled = false;
            SetMiniRange(null);
            MiniEditorMessage.Text = string.Empty;
        }

        // ---- 여러 날 (10/8 ~ 10/10) in the bubble's editor ----
        private bool miniEndInvalid;

        // Shows a range's last day (여러 날 on, 종료 날짜 open), or none (one day).
        private void SetMiniRange(DateTime? end)
        {
            miniEndInvalid = false;
            MiniEndDateInput.SelectedDate = end;
            MiniRangeToggle.IsChecked = end.HasValue;
            MiniEndPanel.Visibility = end.HasValue ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MiniRangeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (MiniEndPanel == null || MiniEndDateInput == null) return;
            bool on = MiniRangeToggle.IsChecked == true;
            MiniEndPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            // Turned on: the day after the start, to change from there.
            if (on && !MiniEndDateInput.SelectedDate.HasValue && MiniDateInput.SelectedDate.HasValue && MiniDateInput.SelectedDate.Value < DateTime.MaxValue.Date)
                MiniEndDateInput.SelectedDate = MiniDateInput.SelectedDate.Value.Date.AddDays(1);
            if (!on) { miniEndInvalid = false; MiniEndDateInput.IsDropDownOpen = false; }
        }

        private void MiniEndDateInput_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MiniEndDateInput.SelectedDate.HasValue) miniEndInvalid = false;
        }

        private void MiniEndDateInput_EditStarted(object sender, TextCompositionEventArgs e)
        {
            if (miniEndInvalid) MiniEditorMessage.Text = string.Empty;
            miniEndInvalid = false;
        }

        private void MiniEndDateInput_DateValidationError(object sender, DatePickerDateValidationErrorEventArgs e)
        {
            e.ThrowException = false;
            miniEndInvalid = true;
            MiniEditorMessage.Text = "유효한 종료 날짜를 입력해 주세요.";
        }

        /// <summary>
        /// The EndPeriod the bubble's editor saves for <paramref name="start"/>: null with 여러 날 off; false with the message
        /// (shown in the editor) when the 종료 날짜 is missing, not a date, before the start or too far.
        /// </summary>
        private bool TryGetMiniEnd(DateTime start, out string endPeriod, out string error)
        {
            endPeriod = null;
            error = null;
            if (MiniRangeToggle.IsChecked != true) return true;
            var editor = MiniEndDateInput.Template?.FindName("PART_TextBox", MiniEndDateInput) as TextBox;
            string text = editor?.Text ?? MiniEndDateInput.Text;
            if (!miniEndInvalid && !string.IsNullOrWhiteSpace(text)) MiniEndDateInput.Text = text; // commit typed text (DatePicker's own parsing)
            if (miniEndInvalid || string.IsNullOrWhiteSpace(text) || !MiniEndDateInput.SelectedDate.HasValue)
            {
                error = string.IsNullOrWhiteSpace(text) ? "종료 날짜를 입력해 주세요." : "유효한 종료 날짜를 입력해 주세요.";
                return false;
            }
            return ScheduleItem.TryRange(start, MiniEndDateInput.SelectedDate.Value, out endPeriod, out error);
        }

        private void CloseDayPopup()
        {
            if (MiniDateInput != null) MiniDateInput.IsDropDownOpen = false;
            if (MiniEndDateInput != null) MiniEndDateInput.IsDropDownOpen = false;
            if (DayPopup != null)
            {
                DayPopup.StaysOpen = false;
                DayPopup.IsOpen = false;
            }
            miniEditId = null;
            selectedDay = null;
            miniDateInvalid = false;
            if (MiniEditorPanel != null) MiniEditorPanel.Visibility = Visibility.Collapsed;
        }

        private bool TryGetMiniDate(out DateTime date)
        {
            date = default(DateTime);
            if (miniDateInvalid) return false;
            var editor = MiniDateInput.Template?.FindName("PART_TextBox", MiniDateInput) as TextBox;
            string text = editor?.Text ?? MiniDateInput.Text;
            if (string.IsNullOrWhiteSpace(text)) return false;
            // Commit pending keyboard input using DatePicker's own culture and validation.
            MiniDateInput.Text = text;
            if (miniDateInvalid || !MiniDateInput.SelectedDate.HasValue) return false;
            date = MiniDateInput.SelectedDate.Value.Date;
            return true;
        }

        private void MiniDateInput_EditStarted(object sender, TextCompositionEventArgs e) => ClearMiniDateError();
        private void MiniDateInput_Pasting(object sender, DataObjectPastingEventArgs e) => ClearMiniDateError();
        private void MiniDateInput_EditKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Back || e.Key == Key.Delete || e.Key == Key.X && Keyboard.Modifiers == ModifierKeys.Control)
                ClearMiniDateError();
        }
        private void ClearMiniDateError()
        {
            if (miniDateInvalid) MiniEditorMessage.Text = string.Empty;
            miniDateInvalid = false;
        }

        private void Day_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || !(button.Tag is DateTime) || RecentlyDragged) return;
            e.Handled = true;
            DateTime day = ((DateTime)button.Tag).Date;
            // Clicking the same day again while its bubble shows (or the press that just closed it) only closes it.
            bool sameDay = dayPopupDay == day;
            if (sameDay && DayPopup.IsOpen) { CloseDayPopup(); return; }
            if (sameDay && popupClosedAt.TryGetValue(DayPopup, out DateTime closedAt) && (DateTime.Now - closedAt).TotalMilliseconds < 300) return;
            dayPopupDay = day;
            selectedDay = day;
            ResetMiniEditor(selectedDay.Value);
            MiniEditorPanel.Visibility = Visibility.Collapsed;
            RefreshDaySchedules();
            OpenDayPopupAt(button);
            e.Handled = true;
        }

        private void OpenDayPopupAt(FrameworkElement anchor)
        {
            DayPopup.PlacementTarget = WeekCalendar;
            double center = anchor.TranslatePoint(new Point(anchor.ActualWidth / 2, 0), WeekCalendar).X;
            DayPopup.HorizontalOffset = Math.Max(0, Math.Min(center - DayBubble.Width / 2, WeekCalendar.ActualWidth - DayBubble.Width));
            DayBubbleTail.Margin = new Thickness(Math.Max(16, Math.Min(center - DayPopup.HorizontalOffset - 12, DayBubble.Width - 40)), 0, 0, 0);
            DayPopup.StaysOpen = false;
            RemeasurePopup(DayPopup);
            DayPopup.IsOpen = true;
            CloseDayBubble.Focus();
        }

        private DateTime? dayPopupDay; // day of the bubble last opened (kept after it closes, for the toggle above)

        private void CloseDayPopup_Click(object sender, RoutedEventArgs e) { CloseDayPopup(); e.Handled = true; }

        private void DayPopup_Opened(object sender, EventArgs e)
        {
            bool below = DayBubble.PointToScreen(new Point()).Y >= WeekCalendar.PointToScreen(new Point()).Y;
            DayBubbleFace.Margin = below ? new Thickness(0, 10, 0, 0) : new Thickness(0, 0, 0, 10);
            DayBubbleTail.VerticalAlignment = below ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            DayBubbleTail.Data = Geometry.Parse(below ? "M0,11 L12,0 L24,11" : "M0,0 L12,11 L24,0");
        }

        private void DayBubble_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (DayPopup.IsOpen) Dispatcher.BeginInvoke(new Action(() =>
            {
                if (DayPopup.IsOpen) DayPopup_Opened(null, EventArgs.Empty);
            }));
        }

        private void EditMiniSchedule_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as FrameworkElement)?.DataContext as ScheduleItem;
            if (item == null) return;
            MiniEditorPanel.Visibility = Visibility.Visible;
            miniEditId = item.Id;
            // The schedule's own date; the bubble's day (or today) when it has none that reads as a date.
            DateTime date = selectedDay ?? DateTime.Today;
            if (DateTime.TryParseExact(item.Period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exact)) date = exact;
            else if (DateTime.TryParse(item.Period, out DateTime parsed)) date = parsed;
            MiniTitleInput.Text = item.Title ?? string.Empty;
            MiniDateInput.SelectedDate = date.Date;
            if (!string.IsNullOrWhiteSpace(item.Time) && FeatureRules.TryScheduleTime(item.Time, out string normalizedTime))
            {
                MiniTimeToggle.IsChecked = true;
                MiniTimeInput.Text = normalizedTime;
                MiniTimeInput.IsEnabled = true;
            }
            else
            {
                MiniTimeToggle.IsChecked = false;
                MiniTimeInput.Text = "09:00";
                MiniTimeInput.IsEnabled = false;
            }
            SetMiniRange(item.EndDate); // 여러 날: its last day
            miniDateInvalid = false;
            MiniEditorMessage.Text = string.Empty;
            MiniTitleInput.Focus();
            MiniTitleInput.SelectAll();
            e.Handled = true;
        }

        private void NewMiniSchedule_Click(object sender, RoutedEventArgs e)
        {
            ResetMiniEditor(selectedDay ?? DateTime.Today);
            MiniEditorPanel.Visibility = Visibility.Visible;
            MiniTitleInput.Focus();
            e.Handled = true;
        }

        // Cancel discards the input and folds the editor away; the day's bubble stays open.
        private void CancelMiniSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (MiniDateInput.IsDropDownOpen) MiniDateInput.IsDropDownOpen = false;
            if (MiniEndDateInput.IsDropDownOpen) MiniEndDateInput.IsDropDownOpen = false;
            ResetMiniEditor(selectedDay ?? DateTime.Today);
            MiniEditorPanel.Visibility = Visibility.Collapsed;
            RefreshDaySchedules();
            CloseDayBubble.Focus();
            e.Handled = true;
        }

        // The bubble is a Popup: its own HWND, while Win32 focus stays on the mini window. The IME then has no
        // composition context for the text box, so Korean arrives as separate jamo (ㅎㅏㄴ) or raw letters.
        // Moving Win32 focus to the popup's HWND lets the IME compose syllables normally.
        private void DayBubble_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!(e.NewFocus is TextBox box)) return;
            var source = PresentationSource.FromVisual(DayBubble) as HwndSource;
            if (source == null || source.Handle == IntPtr.Zero || NativeMethods.GetFocus() == source.Handle) return;
            // Run after WPF finishes this focus change; doing it inside the event leaves the IME detached.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (source.IsDisposed || !DayPopup.IsOpen || NativeMethods.GetFocus() == source.Handle) return;
                NativeMethods.SetFocus(source.Handle);
                // Re-enter the box so WPF binds the IME to the popup's HWND (a no-op Focus() would skip that).
                int caret = box.CaretIndex;
                Keyboard.ClearFocus();
                box.Focus();
                box.CaretIndex = caret;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void SaveMiniSchedule_Click(object sender, RoutedEventArgs e)
        {
            string title = MiniTitleInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                MiniEditorMessage.Text = "제목을 입력해 주세요.";
                MiniTitleInput.Focus();
                return;
            }
            if (!TryGetMiniDate(out DateTime date))
            {
                MiniEditorMessage.Text = "유효한 날짜를 입력해 주세요.";
                MiniDateInput.Focus();
                return;
            }
            string time = null;
            if (MiniTimeToggle.IsChecked == true && !FeatureRules.TryScheduleTime(MiniTimeInput.Text, out time))
            {
                MiniEditorMessage.Text = "시간을 00:00~23:59 형식으로 입력해 주세요.";
                MiniTimeInput.Focus();
                return;
            }
            if (!TryGetMiniEnd(date, out string endPeriod, out string endError))
            {
                MiniEditorMessage.Text = endError;
                MiniEndDateInput.Focus();
                return;
            }

            ScheduleItem item = miniEditId.HasValue ? data.Schedules.FirstOrDefault(s => s.Id == miniEditId.Value) : null;
            if (item == null)
            {
                if (miniEditId.HasValue)
                {
                    MiniEditorMessage.Text = "수정할 일정을 찾을 수 없습니다.";
                    return;
                }
                item = new ScheduleItem { Id = Guid.NewGuid() };
                data.Schedules.Add(item);
                miniEditId = item.Id;
            }
            item.Title = title;
            item.Period = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            item.EndPeriod = endPeriod;
            item.Time = time;

            bool saved;
            int before = refreshCount;
            keepDayPopupState = true;
            try { saved = changed == null || changed(); }
            finally { keepDayPopupState = false; }
            if (!saved)
            {
                MiniEditorMessage.Text = "저장하지 못했습니다. 변경 내용을 유지했으니 다시 저장해 주세요.";
                RemeasurePopup(DayPopup);
                DayPopup.IsOpen = true;
                return;
            }

            if (refreshCount == before) Refresh(); // the save's refresh (through the TODO list) already showed it
            DateTime listDay = selectedDay ?? date;
            ResetMiniEditor(listDay);
            MiniEditorMessage.Text = date.ToString("M월 d일", CultureInfo.GetCultureInfo("ko-KR")) +
                (item.EndDate.HasValue ? "~" + item.EndDate.Value.ToString("M월 d일", CultureInfo.GetCultureInfo("ko-KR")) : "") + "에 저장했습니다.";
            RefreshDaySchedules();
            MiniEditorPanel.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        private void MiniTimeToggle_Changed(object sender, RoutedEventArgs e)
        {
            MiniTimeInput.IsEnabled = MiniTimeToggle.IsChecked == true;
            if (MiniTimeInput.IsEnabled && string.IsNullOrWhiteSpace(MiniTimeInput.Text)) MiniTimeInput.Text = "09:00";
        }

        private void DayPopup_Closed(object sender, EventArgs e)
        {
            if (keepDayPopupState)
            {
                DayPopup.StaysOpen = false;
                return;
            }
            miniEditId = null;
            selectedDay = null;
            miniDateInvalid = false;
            MiniEditorPanel.Visibility = Visibility.Collapsed;
            DayPopup.StaysOpen = false;
        }

        private void MiniDateInput_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MiniDateInput.SelectedDate.HasValue)
                miniDateInvalid = false;
        }

        private void MiniDateInput_DateValidationError(object sender, DatePickerDateValidationErrorEventArgs e)
        {
            e.ThrowException = false;
            miniDateInvalid = true;
            MiniEditorMessage.Text = "유효한 날짜를 입력해 주세요.";
        }

        private void DayPopup_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (MiniDateInput.IsDropDownOpen) MiniDateInput.IsDropDownOpen = false;
            else if (MiniEndDateInput.IsDropDownOpen) MiniEndDateInput.IsDropDownOpen = false;
            else DayPopup.IsOpen = false;
        }

        private void MiniDateInput_CalendarOpened(object sender, RoutedEventArgs e)
        {
            ClearMiniDateError();
            DayPopup.StaysOpen = true;
        }

        private void MiniDateInput_CalendarClosed(object sender, RoutedEventArgs e)
        {
            DayPopup.StaysOpen = false;
        }

        private void Week_Click(object sender, RoutedEventArgs e)
        {
            CloseDayPopup();
            FinishWeekFlip(); // a flip still running jumps to its end first: the picture shows the settled page, the arrows its limits
            int direction = int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
            if (!(direction < 0 && PreviousWeek.IsEnabled || direction > 0 && NextWeek.IsEnabled)) { Refresh(); return; }
            DateTime target = weekStart.AddDays(direction * DayCount);
            if (!WeekFlipAnimates) { weekStart = target; Refresh(); return; }
            if (!FlipTo(target, direction > 0)) Refresh();
        }

        /// <summary>Turns the page to the one starting at <paramref name="target"/> (any distance away) with the chosen
        /// 넘김 애니메이션, forward (›) or back (‹). False when the flip could not start (the week is then already set).</summary>
        private bool FlipTo(DateTime target, bool next)
        {
            int effect = FlipEffect;
            string oldLabel = WeekLabel.Text;
            // ›: this page is the sheet that lifts away; the next week already lies underneath. ‹: the calendar shows the
            // earlier week at once, hidden under a picture of this page until the earlier sheet has come down over it; that
            // sheet's own picture is taken right after the first frame, once its week is laid out (it comes in unseen: from
            // behind the band, fading in), so a click lays out one week and draws one picture either way.
            var sheet = SnapshotPage(effect, !next);
            weekStart = target;
            RefreshForFlip();
            return sheet != null && (IsPeel(effect) ? StartPeelFlip(sheet, next, WeekLabel.Text, effect) : StartWeekFlip(sheet, next, oldLabel, WeekLabel.Text, effect));
        }

        // ---- 오늘로 이동 with a flip effect on: the pages rush by (촤라라락), quick flips one after another, to today's page ----
        // At most RushMaxFlips pages turn; farther away, the first flip lands on a page that many before today's (the page
        // underneath a turning sheet can be any week, so nothing jumps). The rush runs in about RushTotalMs, each flip a
        // share of it (never quicker than RushMinMs); a single page turns at the normal speed. Any ‹ / ›, a move, hiding the
        // window or another change ends the rush on today's page at once.
        private const int RushMaxFlips = 8;
        private const double RushTotalMs = 900, RushMinMs = 110;
        private double flipMs = FlipMs;               // this flip's length: FlipMs, shorter while rushing
        private double LabelFade => Math.Min(LabelFadeMs, flipMs * LabelFadeMs / FlipMs);
        private System.Collections.Generic.Queue<DateTime> rushPages; // the pages still to turn to (the last one today's)
        private DateTime rushTarget;
        private DateTime rushAt;                      // the page the rush last turned to (weekStart unless someone else set it)
        private int rushId;                           // which rush a queued next flip belongs to
        private bool rushNext;
        internal bool Rushing => rushPages != null;

        // The ↺ beside the style drop-down: the same as 오늘로 이동 (not the end of a drag on the band).
        private void TodayButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecentlyDragged) return;
            Today_Click(sender, e);
        }

        private void Today_Click(object sender, RoutedEventArgs e)
        {
            CloseDayPopup();
            RangePopup.IsOpen = false;
            FinishWeekFlip(); // a flip (or rush) still running ends first
            DateTime target = DefaultRangeStart();
            int days = (target - weekStart).Days;
            if (days == 0 || !WeekFlipAnimates || DayCount <= 0) { weekStart = target; Refresh(); return; }
            int pages = (int)Math.Ceiling(Math.Abs(days) / (double)DayCount), shown = Math.Max(1, Math.Min(RushMaxFlips, pages));
            int direction = Math.Sign(days);
            rushPages = new System.Collections.Generic.Queue<DateTime>();
            for (int i = shown - 1; i >= 0; i--) rushPages.Enqueue(target.AddDays(-direction * i * DayCount));
            rushTarget = target;
            rushNext = direction > 0;
            flipMs = shown == 1 ? FlipMs : Math.Max(RushMinMs, Math.Min(FlipMs, RushTotalMs / shown));
            NextRushFlip();
        }

        private void NextRushFlip()
        {
            if (rushPages == null) return;
            if (rushPages.Count == 0 || closed) { EndRush(); return; }
            DateTime page = rushPages.Dequeue(); // the last one taken: still rushing (an empty queue) until it lands
            rushAt = page;
            if (WeekFlipAnimates && FlipTo(page, rushNext)) return;
            // No flip possible now (hidden, 애니메이션 없음 chosen meanwhile, …): straight to today's page.
            EndRush();
            weekStart = rushTarget;
            Refresh();
        }

        // A rush flip landed: the next one right after this frame (the landed page drawn first).
        private void ContinueRush()
        {
            if (rushPages == null) return;
            if (rushPages.Count == 0) { EndRush(); return; }
            int rush = rushId;
            // Only for this rush: one ended (and maybe a new one started) before this runs must not be turned on by it.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { if (rush == rushId) NextRushFlip(); }));
        }

        private void EndRush()
        {
            rushPages = null;
            rushId++;
            flipMs = FlipMs;
        }

        private void RefreshForFlip()
        {
            refreshingForFlip = true;
            try { Refresh(); }
            finally { refreshingForFlip = false; }
        }

        // ---- ‹ / › page flip: a real calendar page turning on its binding ----
        // The page is the paper below the date-range band; the band's lower edge is the hinge. The page turns around that
        // line in 3D with perspective, drawn in a small transparent click-through window owned by this one (so it can pass
        // over the band and above the calendar's top edge), made while the calendar is idle after opening (or on first use),
        // reused, and parked off the screen with nothing in it when idle (hiding and showing it again for every flip cost a
        // dropped frame each time).
        // › (next): the page lifts toward the viewer — its lower edge coming closer (bigger) and up to the hinge — then its
        //   back swings up over the date-range band and above the calendar, and it vanishes behind. The next week is already
        //   underneath. ‹ (previous): the reverse — the earlier page comes from above the band back side first, swings down
        //   over the band, passes edge-on at the hinge and lands on this page, covering it. The calendar already shows the
        //   earlier week underneath, hidden under a picture of this page until then, so the landing changes nothing.
        // The period label changes when the page is edge-on. One picture per click, nothing runs when idle; instant (no flip)
        // when 넘김 애니메이션 is 애니메이션 없음, Windows animations are off, the window isn't showing, or for the TODO window's pets.
        // That is effect 1 (설정 › 넘김 애니메이션). Effect 2 peels the whole sheet off from its lower-right corner instead, its
        // top edge held by the binder rings (see StartPeelFlip); the setting is read when each flip starts. Effects 3 and 5
        // turn the page like 1, effects 4 and 6 peel it like 2, but they show less of the sheet's back above the ring line
        // (the paper's top edge, where the rings hold it): 3/4 up to a third of the sheet's height, fading out toward that,
        // 5/6 none of it — the paper goes behind the binding there, the rings drawn over it (see LimitAboveRings).
        private sealed class WeekFlip
        {
            public System.Windows.Media.Animation.Storyboard Storyboard;
            public bool Next;
            public bool Pictured;        // ‹: the earlier sheet's picture is taken (see PictureIncoming)
            public string NewLabel;
            public int Effect = 1;       // 1, 3, 5: the page turned up over the band (3D); 2, 4, 6: the sheet peeled from its corner (2D)
        }

        private WeekFlip weekFlip;
        private bool refreshingForFlip;   // the Refresh that belongs to a flip must not end that flip
        private bool weekFlipHooked;
        internal bool? weekFlipAnimationsOverride = null; // tests: force the flip on / off regardless of the Windows setting

        private Window flipWindow;
        private System.Windows.Controls.Viewport3D flipViewport;
        private System.Windows.Media.Media3D.PerspectiveCamera flipCamera;
        private System.Windows.Media.Media3D.MeshGeometry3D flipMesh;
        private System.Windows.Media.Media3D.DiffuseMaterial flipFront, flipBack;
        private System.Windows.Media.Media3D.AxisAngleRotation3D flipAngle;
        private Canvas flipUnderRoot;                 // ‹: the picture of this page under the sheet coming down (below all else)
        private System.Windows.Shapes.Rectangle flipPets; // the pets, live, drawn over the turning page (above all else)
        private Image flipUnder;
        private bool flipParked = true;               // the page window is off the screen with nothing in it

        // The page's turn (degrees), animated here on the window: a Storyboard does not apply its clock to a free-standing
        // 3D rotation object, so this property carries the angle and hands it to the rotation on every change.
        private static readonly DependencyProperty FlipTurnProperty = DependencyProperty.Register("FlipTurn", typeof(double), typeof(MiniWindow),
            new PropertyMetadata(0.0, (d, e) => { if (((MiniWindow)d).flipAngle is System.Windows.Media.Media3D.AxisAngleRotation3D rotation) rotation.Angle = (double)e.NewValue; }));

        private static readonly CubicBezierEase FlipEase = Frozen(new CubicBezierEase(0.4, 0, 0.2, 1));   // the turn
        private static readonly CubicBezierEase FlipFadeOut = Frozen(new CubicBezierEase(0.4, 0, 1, 1));  // vanishing behind
        private static readonly CubicBezierEase FlipFadeIn = Frozen(new CubicBezierEase(0, 0, 0.2, 1));   // appearing from behind
        private static CubicBezierEase Frozen(CubicBezierEase ease) { ease.Freeze(); return ease; }

        private const double FlipMs = 520, FlipEndAngle = 172, LabelFadeMs = 70, FlipParkDelayMs = 300;
        private const double CameraDepth = 5;         // camera distance in page heights: a gentle, clearly visible perspective
        private const double FlipRoomX = 0.16;        // room each side for the page growing toward the viewer (1 / (5 − 1) + a bit)
        private const double FlipRoomY = 0.12;        // extra room above the swung-over page (and the same below, to keep the hinge centred)
        private const int WsExTransparent = 0x00000020, WsExNoActivate = 0x08000000;
        private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoOwnerZOrder = 0x0200;

        private bool WeekFlipAnimates =>
            !IsAllSchedulesOpen && // keep the agenda stationary while its calendar changes weeks
            data.MiniFlipEffect != 0 && // 애니메이션 없음: the week changes at once (and no page window is ever made)
            (weekFlipAnimationsOverride ?? SystemParameters.ClientAreaAnimation) && !companion && !closed && IsLoaded && IsVisible &&
            PresentationSource.FromVisual(this) != null && WeekDays.Parent is FrameworkElement host && host.ActualWidth >= 2 &&
            host.ActualHeight - CalendarHeader.ActualHeight >= 2;

        private int FlipEffect => data.MiniFlipEffect >= 1 && data.MiniFlipEffect <= 6 ? data.MiniFlipEffect : 1;
        private static bool IsPeel(int effect) => effect % 2 == 0;

        // How much of the sheet's back may show above the ring line, in sheet heights (effects 1/2: all of it).
        private static double BackAboveRings(int effect) => effect <= 2 ? double.PositiveInfinity : effect <= 4 ? 1.0 / 3 : 0;
        private const double BackFadeShare = 0.25;    // 3/4: the last part toward the cut fades out (a share of the band shown)

        internal System.Windows.Media.Animation.Storyboard WeekFlipStoryboard => weekFlip?.Storyboard;
        internal Window WeekFlipWindow => flipWindow;
        internal bool WeekFlipShowing => flipWindow != null && flipWindow.IsVisible && !flipParked; // on the screen (not parked)
        internal System.Windows.Controls.Viewport3D WeekFlipViewport => flipViewport;
        internal double WeekFlipAngle => flipAngle?.Angle ?? 0;
        internal bool WeekFlipRunning => weekFlip != null;
        internal int WeekFlipEffect => weekFlip?.Effect ?? 0;
        internal FrameworkElement WeekPeelLayer => peelRoot;
        internal Vector WeekPeelNormal => new Vector(peelNx, peelNy);
        internal double WeekPeelFold => peelFold;       // the fold line n·p = fold, in sheet coordinates (origin: the paper's top-left)

        // The page: the paper below the date-range band, in the band's grid (whose lower edge is the hinge).
        private Rect PageRegion()
        {
            var host = (FrameworkElement)WeekDays.Parent;
            double top = CalendarHeader.ActualHeight;
            return new Rect(0, top, host.ActualWidth, Math.Max(0, host.ActualHeight - top));
        }

        // The page as it looks now, at the screen's real pixel density, painted on the paper colour (as opaque as a real
        // sheet — nothing behind it shows through) and cut to the frame's rounded lower corners, with SheetMargin clear
        // pixels all round: the turning page's edges come from the picture, smooth, instead of the 3D polygon's edge, jagged
        // (effect 1 runs without the 3D anti-aliasing, which kept ~60 MB more). A VisualBrush draws its visual together with
        // that visual's own offset in its parent, so the Viewbox starts there. Every click's picture is copied into the same
        // bitmap: the renderer keeps one texture for it instead of piling up a new one per click (a fresh bitmap per flip
        // grew the process by hundreds of MB over a few dozen flips).
        private System.Windows.Media.Imaging.WriteableBitmap flipSheet, flipBackSheet, flipUnderSheet;
        private const int SheetMargin = 2;
        private Rect flipUnderRegion;                 // ‹: where the picture of this page lies (the band's grid's coordinates)

        // Effects 1/3/5 turn the page (below the band); 2/4/6 peel the whole sheet (band included, rounded all round).
        // `under`: this page as it is, to lie under the earlier sheet coming down (‹), in a bitmap of its own.
        private System.Windows.Media.Imaging.BitmapSource SnapshotPage(int effect, bool under = false)
        {
            UpdateLayout(); // a refresh just before (same dispatcher turn) must be laid out, or the picture shows the old cells
            var host = (FrameworkElement)WeekDays.Parent;
            bool peel = IsPeel(effect);
            Rect page = peel ? new Rect(0, 0, host.ActualWidth, host.ActualHeight) : PageRegion();
            var dpi = VisualTreeHelper.GetDpi(host);
            // Laid flat over the real page (the peel's sheet, ‹'s picture under it): drawn at the page's own sub-pixel position
            // on screen, so its pixels match the screen's, and placed at whole pixels in the page window.
            Vector phase = new Vector();
            if (peel || under)
            {
                Point at = host.PointToScreen(new Point(page.X, page.Y));
                phase = new Vector(at.X - Math.Floor(at.X), at.Y - Math.Floor(at.Y));
                if (under) flipUnderRegion = page;
                else peelPhase = phase;
            }
            int pixelWidth = (int)Math.Ceiling(page.Width * dpi.DpiScaleX + phase.X), pixelHeight = (int)Math.Ceiling(page.Height * dpi.DpiScaleY + phase.Y);
            if (pixelWidth < 2 || pixelHeight < 2 || pixelWidth > 8192 || pixelHeight > 8192) return null;
            Vector offset = VisualTreeHelper.GetOffset(host);
            var paper = host.Parent as Border;
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.PushTransform(new TranslateTransform((SheetMargin + phase.X) / dpi.DpiScaleX, (SheetMargin + phase.Y) / dpi.DpiScaleY));
                context.PushClip(peel ? SheetOutline(page.Size) : PageOutline(page.Size));
                context.DrawRectangle(paper?.Background ?? Brushes.White, null, new Rect(0, 0, page.Width, page.Height));
                context.DrawRectangle(new VisualBrush(host)
                {
                    Viewbox = new Rect(offset.X + page.X, offset.Y + page.Y, page.Width, page.Height), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill
                }, null, new Rect(0, 0, page.Width, page.Height));
                context.Pop();
                context.Pop();
            }
            return under ? SheetFrom(drawing, pixelWidth + 2 * SheetMargin, pixelHeight + 2 * SheetMargin, dpi, ref flipUnderSheet)
                : SheetFrom(drawing, pixelWidth + 2 * SheetMargin, pixelHeight + 2 * SheetMargin, dpi, ref flipSheet);
        }

        // The turning page's picture in pixels, clear margin included (its sub-pixel position is not followed: see above).
        private Size TurningSheetPixels()
        {
            Rect page = PageRegion();
            var dpi = VisualTreeHelper.GetDpi((Visual)WeekDays.Parent);
            return new Size(Math.Ceiling(page.Width * dpi.DpiScaleX) + 2 * SheetMargin, Math.Ceiling(page.Height * dpi.DpiScaleY) + 2 * SheetMargin);
        }

        // Effect 1's back of the page: its colour in the page's outline, with the same clear margin as the front.
        private System.Windows.Media.Imaging.BitmapSource PageBackSheet(Color color)
        {
            var dpi = VisualTreeHelper.GetDpi((Visual)WeekDays.Parent);
            var fill = new SolidColorBrush(color);
            fill.Freeze();
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.PushTransform(new TranslateTransform(SheetMargin / dpi.DpiScaleX, SheetMargin / dpi.DpiScaleY));
                context.DrawGeometry(fill, null, PageOutline(PageRegion().Size));
                context.Pop();
            }
            Size pixels = TurningSheetPixels();
            return SheetFrom(drawing, (int)pixels.Width, (int)pixels.Height, dpi, ref flipBackSheet);
        }

        // Draws `drawing` into `sheet` (kept and reused while its size and density stay the same).
        private static System.Windows.Media.Imaging.BitmapSource SheetFrom(DrawingVisual drawing, int pixelWidth, int pixelHeight, DpiScale dpi,
            ref System.Windows.Media.Imaging.WriteableBitmap sheet)
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(pixelWidth, pixelHeight, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            if (sheet == null || sheet.PixelWidth != pixelWidth || sheet.PixelHeight != pixelHeight ||
                Math.Abs(sheet.DpiX - bitmap.DpiX) > 0.01 || Math.Abs(sheet.DpiY - bitmap.DpiY) > 0.01)
                sheet = new System.Windows.Media.Imaging.WriteableBitmap(pixelWidth, pixelHeight, bitmap.DpiX, bitmap.DpiY, PixelFormats.Pbgra32, null);
            var all = new Int32Rect(0, 0, pixelWidth, pixelHeight);
            sheet.Lock();
            try
            {
                bitmap.CopyPixels(all, sheet.BackBuffer, sheet.BackBufferStride * pixelHeight, sheet.BackBufferStride);
                sheet.AddDirtyRect(all);
            }
            finally { sheet.Unlock(); }
            return sheet;
        }

        // The page's outline in page coordinates: square where it meets the band, the frame's rounded corners below.
        private Geometry PageOutline(Size size)
        {
            var paper = (WeekDays.Parent as FrameworkElement)?.Parent as Border;
            double radius = paper == null ? 0 : Math.Max(0, paper.CornerRadius.BottomLeft - paper.BorderThickness.Bottom);
            var outline = new CombinedGeometry(GeometryCombineMode.Union,
                new RectangleGeometry(new Rect(0, 0, size.Width, size.Height), radius, radius),
                new RectangleGeometry(new Rect(0, 0, size.Width, size.Height / 2)));
            outline.Freeze();
            return outline;
        }

        // The whole sheet's outline (band included): the frame's rounded corners all round.
        private Geometry SheetOutline(Size size)
        {
            var paper = (WeekDays.Parent as FrameworkElement)?.Parent as Border;
            double radius = paper == null ? 0 : Math.Max(0, paper.CornerRadius.TopLeft - paper.BorderThickness.Top);
            var outline = new RectangleGeometry(new Rect(0, 0, size.Width, size.Height), radius, radius);
            outline.Freeze();
            return outline;
        }

        // The page's back: the paper colour a little darker.
        private Color PageBackColor(double shade = 0.86)
        {
            var paper = (WeekDays.Parent as FrameworkElement)?.Parent as Border;
            Color color = (paper?.Background as SolidColorBrush)?.Color ?? Colors.White;
            return Color.FromRgb((byte)(color.R * shade), (byte)(color.G * shade), (byte)(color.B * shade));
        }

        private bool EnsureFlipWindow()
        {
            if (flipWindow != null) return true;
            try
            {
                flipCamera = new System.Windows.Media.Media3D.PerspectiveCamera
                {
                    LookDirection = new System.Windows.Media.Media3D.Vector3D(0, 0, -1), UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 1, 0),
                    NearPlaneDistance = 1, FarPlaneDistance = 1000000
                };
                flipMesh = new System.Windows.Media.Media3D.MeshGeometry3D
                {
                    // Corners: 0 top-left, 1 top-right (both on the hinge), 2 bottom-right, 3 bottom-left; front faces the viewer.
                    TriangleIndices = new Int32Collection { 0, 3, 2, 0, 2, 1 },
                    TextureCoordinates = new PointCollection { new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1) }
                };
                flipFront = new System.Windows.Media.Media3D.DiffuseMaterial();
                flipBack = new System.Windows.Media.Media3D.DiffuseMaterial();
                // Turning on the hinge line (y = 0); positive angles bring the lower edge toward the viewer.
                flipAngle = new System.Windows.Media.Media3D.AxisAngleRotation3D(new System.Windows.Media.Media3D.Vector3D(-1, 0, 0), 0);
                var sheet = new System.Windows.Media.Media3D.GeometryModel3D(flipMesh, flipFront)
                {
                    BackMaterial = flipBack, Transform = new System.Windows.Media.Media3D.RotateTransform3D(flipAngle)
                };
                var scene = new System.Windows.Media.Media3D.Model3DGroup();
                // Ambient + a light from the viewer add up to exactly white: lying flat the page shows its true colours, and
                // turning away it darkens like paper catching less light.
                scene.Children.Add(new System.Windows.Media.Media3D.AmbientLight(Color.FromRgb(0x5C, 0x5C, 0x5C)));
                scene.Children.Add(new System.Windows.Media.Media3D.DirectionalLight(Color.FromRgb(0xA3, 0xA3, 0xA3), new System.Windows.Media.Media3D.Vector3D(0, 0, -1)));
                scene.Children.Add(sheet);
                // Fills the window at whatever DPI Windows gives it (mostly on another monitor it may differ from this one's):
                // the camera, not the viewport's size, maps the page onto the window's pixels. No 3D anti-aliasing: the
                // pictures' clear margins give the page smooth edges (see SnapshotPage).
                flipViewport = new System.Windows.Controls.Viewport3D { Camera = flipCamera, IsHitTestVisible = false, ClipToBounds = false };
                RenderOptions.SetEdgeMode(flipViewport, EdgeMode.Aliased);
                flipViewport.Children.Add(new System.Windows.Media.Media3D.ModelVisual3D { Content = scene });
                // The turning effects show the 3D view, the peeling ones the peel (one of them at a time); under either, for ‹,
                // the picture of this page the sheet comes down onto.
                EnsurePeelParts();
                flipUnder = new Image { Stretch = Stretch.Fill };
                flipUnderRoot = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
                flipUnderRoot.Children.Add(flipUnder);
                var content = new Grid { IsHitTestVisible = false };
                content.Children.Add(flipUnderRoot);
                content.Children.Add(flipViewport);
                content.Children.Add(peelRoot);
                // The page window lies over this whole window, pets too: a pet standing on the calendar vanished under the
                // turning page. The pets' live picture goes on top of the page (see PlacePetsInWindow).
                flipPets = new System.Windows.Shapes.Rectangle { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
                var petsLayer = new Canvas { IsHitTestVisible = false };
                petsLayer.Children.Add(flipPets);
                content.Children.Add(petsLayer);
                flipWindow = new Window
                {
                    WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
                    ShowActivated = false, Topmost = false, ResizeMode = ResizeMode.NoResize, Focusable = false, IsHitTestVisible = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, SizeToContent = SizeToContent.Manual,
                    Left = -32000, Top = -32000, Width = 1, Height = 1, Title = "", Owner = this, Content = content, Opacity = MiniRoot.Opacity
                };
                var handle = new WindowInteropHelper(flipWindow).EnsureHandle();
                // Click-through, never activated, not in Alt+Tab.
                NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE,
                    NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE) | WsExTransparent | WsExNoActivate | NativeMethods.WS_EX_TOOLWINDOW);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                flipWindow = null;
                return false;
            }
        }

        private void HookWeekFlipEvents()
        {
            if (weekFlipHooked) return;
            weekFlipHooked = true;
            Closed += (s, e) =>
            {
                FinishWeekFlip();
                flipParkTimer?.Stop();
                flipWindow?.Close();
                flipWindow = null;
                flipSheet = flipBackSheet = flipRingSheet = flipUnderSheet = null;
            };
            // Hidden (Esc → tray) or moved mid-flip: end it now — the page window is placed on the screen, not in this window.
            IsVisibleChanged += (s, e) => { if (!(bool)e.NewValue) FinishWeekFlip(); };
            LocationChanged += (s, e) => FinishWeekFlip();
        }

        // The page window, in screen pixels: the page centred horizontally with room for its perspective growth, and the
        // hinge exactly in the middle vertically (a page's height of room above it for the swing, and the same below).
        private bool PlaceFlipWindow(out Rect pageInWindow)
        {
            pageInWindow = Rect.Empty;
            var host = (FrameworkElement)WeekDays.Parent;
            Rect page = PageRegion();
            var dpi = VisualTreeHelper.GetDpi(host);
            Point hinge = host.PointToScreen(new Point(page.X, page.Y));
            int roomX = (int)Math.Ceiling(page.Width * FlipRoomX * dpi.DpiScaleX);
            int half = (int)Math.Ceiling(page.Height * (1 + FlipRoomY) * dpi.DpiScaleY);
            int pageWidth = (int)Math.Round(page.Width * dpi.DpiScaleX);
            int left = (int)Math.Round(hinge.X) - roomX, top = (int)Math.Round(hinge.Y) - half, width = pageWidth + 2 * roomX, height = 2 * half;
            if (width < 4 || height < 4 || width > 16384 || height > 16384) return false;
            var handle = new WindowInteropHelper(flipWindow).Handle;
            // Twice: a first move onto a monitor with another DPI makes WPF rescale the window; the second puts it back.
            for (int i = 0; i < 2; i++)
                NativeMethods.SetWindowPos(handle, IntPtr.Zero, left, top, width, height, NativeMethods.SWP_NOACTIVATE | SwpNoZOrder | SwpNoOwnerZOrder);
            double w = width / dpi.DpiScaleX, h = height / dpi.DpiScaleY;
            double depth = page.Height * CameraDepth;
            // Camera straight in front of the hinge's middle, its view exactly as wide as the window at the page's plane: lying
            // flat (0°) the page covers the real one pixel for pixel.
            flipCamera.Position = new System.Windows.Media.Media3D.Point3D(0, 0, depth);
            flipCamera.FieldOfView = 2 * Math.Atan(w / 2 / depth) * 180 / Math.PI;
            // The picture lies on the page pixel for pixel: the page's top-left on the hinge's left end, its clear margin round it.
            // (‹ has no picture of the earlier page yet: it is taken after the first frame, at this same size.)
            Size pixels = TurningSheetPixels();
            double marginX = SheetMargin / dpi.DpiScaleX, marginY = SheetMargin / dpi.DpiScaleY;
            double sheetWidth = pixels.Width / dpi.DpiScaleX, sheetHeight = pixels.Height / dpi.DpiScaleY;
            double left3D = -pageWidth / dpi.DpiScaleX / 2 - marginX, right3D = left3D + sheetWidth, bottom3D = marginY - sheetHeight;
            flipMesh.Positions = new System.Windows.Media.Media3D.Point3DCollection
            {
                new System.Windows.Media.Media3D.Point3D(left3D, marginY, 0), new System.Windows.Media.Media3D.Point3D(right3D, marginY, 0),
                new System.Windows.Media.Media3D.Point3D(right3D, bottom3D, 0), new System.Windows.Media.Media3D.Point3D(left3D, bottom3D, 0)
            };
            pageInWindow = new Rect(roomX / dpi.DpiScaleX, h / 2, page.Width, page.Height);
            PlaceSheetInWindow(left, top);
            // Effects 3/5: the rings drawn over the turning page, and only so much of its back above them.
            if (flipShownEffect == 1) LimitAboveRings(flipViewport, 1, 0, 0);
            else
            {
                double own = VisualTreeHelper.GetDpi(flipWindow).DpiScaleY;
                double ring = (host.PointToScreen(new Point(0, RingLineInSheet())).Y - top) / own; // in the 3D view's own DIPs
                LimitAboveRings(flipViewport, flipShownEffect, ring, page.Height * dpi.DpiScaleY / own);
            }
            return true;
        }

        private int flipShownEffect = 1;              // the effect the page window is set up for

        // `sheet`: › the page lifting away; ‹ the picture of this page the earlier one comes down onto (the earlier page's own
        // picture follows after the first frame: see PictureIncoming — until then only its back shows, fading in).
        private bool StartWeekFlip(System.Windows.Media.Imaging.BitmapSource sheet, bool next, string oldLabel, string newLabel, int effect)
        {
            if (!EnsureFlipWindow()) return false;
            HookWeekFlipEvents();
            flipShownEffect = effect;
            flipViewport.Visibility = Visibility.Visible;
            // 3/5 draw copies of the rings over the page (the peel's parts then hold only those).
            peelRoot.Visibility = effect == 1 ? Visibility.Collapsed : Visibility.Visible;
            peelFlat.Visibility = peelLifted.Visibility = Visibility.Collapsed;
            ShowUnder(next ? null : sheet);
            if (!ShowFlipWindow(() => PlaceFlipWindow(out _))) { ParkFlipWindow(); return false; } // never left on the screen over the pets
            if (effect != 1) PlaceRings((FrameworkElement)WeekDays.Parent, VisualTreeHelper.GetDpi((Visual)WeekDays.Parent));
            flipFront.Brush = next ? new ImageBrush(sheet) : null;
            flipBack.Brush = new ImageBrush(PageBackSheet(PageBackColor()));
            BeginAnimation(FlipTurnProperty, null);
            SetValue(FlipTurnProperty, next ? 0.0 : FlipEndAngle);
            flipAngle.Angle = next ? 0 : FlipEndAngle;
            flipViewport.BeginAnimation(UIElement.OpacityProperty, null);
            flipViewport.Opacity = next ? 1 : 0;

            var flip = new WeekFlip { Storyboard = new System.Windows.Media.Animation.Storyboard(), Next = next, NewLabel = newLabel, Effect = effect };
            var board = flip.Storyboard;
            AddToBoard(board, new System.Windows.Media.Animation.DoubleAnimation(next ? 0 : FlipEndAngle, next ? FlipEndAngle : 0, TimeSpan.FromMilliseconds(flipMs))
            {
                EasingFunction = FlipEase, FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            }, this, new PropertyPath(FlipTurnProperty));
            // › vanishes behind once it is past the band; ‹ appears from behind the same way.
            AddToBoard(board, new System.Windows.Media.Animation.DoubleAnimation(next ? 1 : 0, next ? 0 : 1, TimeSpan.FromMilliseconds(flipMs * 0.2))
            {
                BeginTime = TimeSpan.FromMilliseconds(next ? flipMs * 0.8 : 0), EasingFunction = next ? FlipFadeOut : FlipFadeIn,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            }, flipViewport, OpacityPath);

            // The period label changes when the page is edge-on.
            AddLabelSwitch(board, oldLabel, newLabel, EdgeOnMs(next) * flipMs / FlipMs);

            weekFlip = flip;
            board.Completed += (s, e) => { if (weekFlip == flip) { EndWeekFlip(); ContinueRush(); } };
            board.Begin(this, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace, true);
            if (!next) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => PictureIncoming(flip)));
            return true;
        }

        // ‹: the earlier sheet's picture, once its week is laid out and drawn — right after the flip's first frame, while that
        // sheet is still behind the band, fading in (tests call it too, to have it at once).
        internal void PictureIncoming() { if (weekFlip != null && !weekFlip.Next) PictureIncoming(weekFlip); }

        private void PictureIncoming(WeekFlip flip)
        {
            if (weekFlip != flip || flip.Next || flip.Pictured) return;
            flip.Pictured = true;
            var sheet = SnapshotPage(flip.Effect);
            if (sheet == null) { FinishWeekFlip(); return; }
            if (!IsPeel(flip.Effect)) { flipFront.Brush = new ImageBrush(sheet); return; }
            SetPeelPicture(sheet);
            UpdatePeel((double)GetValue(FlipPeelProperty));
        }

        // ‹: this page's picture under the sheet coming down, where it was taken from (nothing for ›).
        private void ShowUnder(System.Windows.Media.Imaging.BitmapSource picture)
        {
            flipUnder.Source = picture;
            flipUnderRoot.Visibility = picture == null ? Visibility.Collapsed : Visibility.Visible;
            if (picture == null) return;
            var dpi = VisualTreeHelper.GetDpi((Visual)WeekDays.Parent);
            flipUnder.Width = picture.PixelWidth / dpi.DpiScaleX;
            flipUnder.Height = picture.PixelHeight / dpi.DpiScaleY;
        }

        // Brings the page window up, placed by `place`: the first time it is shown (off the screen, empty) before being placed,
        // then placed again (showing it on a monitor with another DPI may have rescaled it); later it only moves in from where
        // it is parked.
        private bool ShowFlipWindow(Func<bool> place)
        {
            flipParkTimer?.Stop(); // it is wanted here now
            if (!flipWindow.IsVisible)
            {
                flipWindow.Show();
                if (!place()) return false;
            }
            flipParked = false;
            return place();
        }

        // While the calendar is idle after opening (see PrewarmPopups): the page window made and shown once, parked (off the
        // screen, a pixel in size, empty), and the flip's own code compiled — so the first ‹ / › starts like any other. Not
        // when flips don't run (애니메이션 없음, Windows animations off, the TODO window's pets).
        private void PrewarmWeekFlip()
        {
            if (closed || companion || flipWindow != null || data.MiniFlipEffect == 0 || !(weekFlipAnimationsOverride ?? SystemParameters.ClientAreaAnimation)) return;
            if (!EnsureFlipWindow()) return;
            HookWeekFlipEvents();
            flipWindow.Show(); // made off the screen, a pixel in size
            // Sized for the chosen effect (empty, where it will be used, then parked off the screen): the first flip then only
            // moves it in.
            int effect = FlipEffect;
            var host = (FrameworkElement)WeekDays.Parent;
            flipShownEffect = effect;
            peelWidth = host.ActualWidth;
            peelHeight = host.ActualHeight;
            if (host.ActualWidth >= 2 && host.ActualHeight - CalendarHeader.ActualHeight >= 2 && (IsPeel(effect) ? PlacePeelWindow() : PlaceFlipWindow(out _)))
                NativeMethods.SetWindowPos(new WindowInteropHelper(flipWindow).Handle, IntPtr.Zero, -32000, -32000, 0, 0,
                    SwpNoSize | NativeMethods.SWP_NOACTIVATE | SwpNoZOrder | SwpNoOwnerZOrder);
            ClearFlipPets(); // parked: no live copy of the pets drawn off the screen
            var flipCode = new[]
            {
                nameof(Week_Click), nameof(RefreshForFlip), nameof(SnapshotPage), nameof(SheetFrom), nameof(TurningSheetPixels), nameof(PageBackSheet),
                nameof(PageOutline), nameof(SheetOutline), nameof(PageBackColor), nameof(PlaceFlipWindow), nameof(StartWeekFlip), nameof(AddLabelSwitch),
                nameof(PictureIncoming), nameof(ShowUnder), nameof(ShowFlipWindow), nameof(ParkFlipWindow), nameof(StartPeelFlip), nameof(SetPeelPicture),
                nameof(PlacePeelWindow), nameof(PlaceSheetInWindow), nameof(RingLineInSheet), nameof(LimitAboveRings), nameof(FindRings), nameof(PlaceRings),
                nameof(UpdatePeel), nameof(PeelLine), nameof(HalfPlane), nameof(EndWeekFlipAnimations), nameof(FinishWeekFlip), nameof(AddToBoard),
                nameof(PlacePetsInWindow), nameof(ClearFlipPets), nameof(FlipTo), nameof(NextRushFlip), nameof(ContinueRush), nameof(EndWeekFlip)
            };
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (var type in new[] { typeof(MiniWindow) }.Concat(typeof(MiniWindow).GetNestedTypes(System.Reflection.BindingFlags.NonPublic)))
                foreach (var method in type.GetMethods(all))
                    if (!method.IsAbstract && !method.ContainsGenericParameters && flipCode.Any(name => method.Name == name || method.Name.Contains("<" + name + ">")))
                        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
        }

        // Idle: the page window with nothing in it at once, and a moment later off the screen, still shown (so the next flip
        // needn't show it again). Not moved away right at the landing: moving (or hiding) a see-through window waits for the
        // frame being drawn to finish (~34 ms at 30 Hz), which then is a dropped frame; a moment later nothing is being drawn
        // and the move is free (< 1 ms).
        private System.Windows.Threading.DispatcherTimer flipParkTimer;

        private void ParkFlipWindow()
        {
            if (flipWindow == null) return;
            BeginAnimation(FlipTurnProperty, null);
            BeginAnimation(FlipPeelProperty, null);
            flipViewport.BeginAnimation(UIElement.OpacityProperty, null);
            peelLifted.BeginAnimation(UIElement.OpacityProperty, null);
            flipFront.Brush = null; // let the pictures go
            flipBack.Brush = null;
            peelFlat.Source = peelFlapHint.Source = peelRings.Source = flipUnder.Source = null;
            ClearFlipPets();
            peelRoot.Visibility = flipUnderRoot.Visibility = flipViewport.Visibility = Visibility.Collapsed;
            if (flipParked) return;
            flipParked = true;
            if (flipParkTimer == null)
            {
                flipParkTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FlipParkDelayMs) };
                flipParkTimer.Tick += (s, e) =>
                {
                    flipParkTimer.Stop();
                    if (flipParked && flipWindow != null && flipWindow.IsVisible)
                        NativeMethods.SetWindowPos(new WindowInteropHelper(flipWindow).Handle, IntPtr.Zero, -32000, -32000, 0, 0,
                            SwpNoSize | NativeMethods.SWP_NOACTIVATE | SwpNoZOrder | SwpNoOwnerZOrder);
                };
            }
            flipParkTimer.Stop();
            flipParkTimer.Start();
        }

        // The period label's change at `at` ms: a quick fade out, the new text, a quick fade in.
        private void AddLabelSwitch(System.Windows.Media.Animation.Storyboard board, string oldLabel, string newLabel, double at)
        {
            WeekLabel.Text = oldLabel;
            var fade = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames { FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd };
            fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Math.Max(0, at - LabelFade)))));
            fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at))));
            fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at + LabelFade))));
            AddToBoard(board, fade, WeekLabel, OpacityPath);
            var text = new System.Windows.Media.Animation.StringAnimationUsingKeyFrames { FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd };
            text.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteStringKeyFrame(newLabel, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at))));
            AddToBoard(board, text, WeekLabel, new PropertyPath(TextBlock.TextProperty));
        }

        /// <summary>When (ms into the flip) the page is edge-on at the hinge (90°).</summary>
        internal static double EdgeOnMs(bool next) => FlipTimeAt(next ? 90 / FlipEndAngle : (FlipEndAngle - 90) / FlipEndAngle);

        // When (ms) the eased flip reaches `progress` (0~1).
        private static double FlipTimeAt(double progress)
        {
            double low = 0, high = 1;
            for (int i = 0; i < 40; i++)
            {
                double mid = (low + high) / 2;
                if (CubicBezierEase.Value(0.4, 0, 0.2, 1, mid) < progress) low = mid; else high = mid;
            }
            return (low + high) / 2 * FlipMs;
        }

        // ---- Effect 2 (모서리 넘기기): the sheet peels off from its lower-right corner, held by the binder rings ----
        // The sheet is the whole calendar paper, band included; its top edge hangs on the rings and stays there to the very
        // end. The fold runs through two points: B climbs the right edge from the lower-right corner to the top-right one,
        // and C runs from the lower-right corner left along the bottom edge, then up the left edge to the top-left one (B
        // travels the height, C the width plus the height, on the same eased progress). So the fold starts as a small
        // dog-ear, becomes a long diagonal and ends exactly on the top edge — the ring line — never cutting it before. The
        // part past the fold (holding the lower-right corner) is mirrored over it: the paper's back (its colour a little
        // darker, the print faintly through, a dark crease and a lit bend), with a soft shadow along the fold on the sheet
        // revealed underneath. At the end the whole sheet is folded over the ring line, above the calendar, and goes behind
        // it (fading out in the last quarter). ‹ is the exact reverse: the earlier sheet comes from above the ring line,
        // fading in, and comes down with its top-left part first; the last dog-ear lays down at the lower-right corner, then
        // it is shown for real. Drawn in the page window (the sheet reaches above the calendar) with copies of the rings on
        // top, so they hold it. The band belongs to the sheet, so the period label simply turns with the page.
        private Canvas peelRoot;                      // in the page window, in this window's DIPs (scaled if that one's DPI differs)
        private Canvas peelSheet;                     // sheet coordinates: origin at the sheet's top-left (the ring line's left end)
        private Image peelFlat, peelFlapHint, peelRings;
        private Canvas peelLifted, peelFlap;          // the lifted part with its shadow (they fade together); the mirrored part
        private System.Windows.Shapes.Path peelFlapBack, peelFlapShade;
        private System.Windows.Shapes.Rectangle peelShadow;
        private LinearGradientBrush peelShadowBrush, peelShadeBrush;
        private MatrixTransform peelMirror;
        private System.Windows.Media.Imaging.WriteableBitmap flipRingSheet;
        private double peelWidth, peelHeight, peelNx, peelNy, peelFold, peelMarginX, peelMarginY;
        private Vector peelPhase;                     // the sheet's sub-pixel position on screen when its picture was taken (pixels)
        private const double PeelShadowWidth = 22, PeelShadeWidth = 90, PeelFadeShare = 0.25;

        // The fold's progress: 0 = nothing lifted, 1 = the whole sheet folded over the ring line.
        private static readonly DependencyProperty FlipPeelProperty = DependencyProperty.Register("FlipPeel", typeof(double), typeof(MiniWindow),
            new PropertyMetadata(0.0, (d, e) => ((MiniWindow)d).UpdatePeel((double)e.NewValue)));

        private void EnsurePeelParts()
        {
            if (peelRoot != null) return;
            peelFlat = new Image { Stretch = Stretch.Fill };
            peelShadowBrush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute };
            peelShadowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0));
            peelShadowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0, 0, 0), 0.03));
            peelShadowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x1E, 0, 0, 0), 0.35));
            peelShadowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
            peelShadow = new System.Windows.Shapes.Rectangle { Fill = peelShadowBrush };
            // The back, bent at the fold: a dark crease, a lit bend, then evenly a little shaded (the rest, past the gradient).
            peelShadeBrush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute };
            peelShadeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, 0, 0, 0), 0));
            peelShadeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF), 0.1));
            peelShadeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.45));
            peelShadeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x16, 0, 0, 0), 1));
            var edge = new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0));
            edge.Freeze();
            peelFlapBack = new System.Windows.Shapes.Path { Stroke = edge, StrokeThickness = 1 };
            peelFlapHint = new Image { Stretch = Stretch.Fill, Opacity = 0.08 };
            peelFlapShade = new System.Windows.Shapes.Path { Fill = peelShadeBrush };
            peelMirror = new MatrixTransform();
            peelFlap = new Canvas { RenderTransform = peelMirror };
            peelFlap.Children.Add(peelFlapBack);
            peelFlap.Children.Add(peelFlapHint);
            peelFlap.Children.Add(peelFlapShade);
            peelLifted = new Canvas();
            peelLifted.Children.Add(peelShadow);
            peelLifted.Children.Add(peelFlap);
            peelRings = new Image { Stretch = Stretch.Fill };
            peelSheet = new Canvas();
            peelSheet.Children.Add(peelFlat);
            peelSheet.Children.Add(peelLifted);
            peelSheet.Children.Add(peelRings); // on top: the rings hold the sheet
            peelRoot = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            peelRoot.Children.Add(peelSheet);
        }

        // `sheet`: › the sheet peeling off; ‹ the picture of this sheet the earlier one comes down onto (the earlier sheet's
        // own picture follows after the first frame: see PictureIncoming — until then it is behind the rings, fading in).
        private bool StartPeelFlip(System.Windows.Media.Imaging.BitmapSource sheet, bool next, string newLabel, int effect)
        {
            if (!EnsureFlipWindow()) return false;
            HookWeekFlipEvents();
            var host = (FrameworkElement)WeekDays.Parent;
            var dpi = VisualTreeHelper.GetDpi(host);
            peelWidth = host.ActualWidth;
            peelHeight = host.ActualHeight;
            flipShownEffect = effect;
            flipViewport.Visibility = Visibility.Collapsed;
            peelRoot.Visibility = peelFlat.Visibility = peelLifted.Visibility = Visibility.Visible;
            LimitAboveRings(peelLifted, effect, RingLineInSheet(), peelHeight); // 4/6: only so much of the back above the rings
            ShowUnder(next ? null : sheet);
            if (next) SetPeelPicture(sheet);
            else peelFlat.Source = peelFlapHint.Source = null;
            if (!ShowFlipWindow(PlacePeelWindow)) { ParkFlipWindow(); return false; } // never left on the screen over the pets
            Geometry outline = SheetOutline(new Size(peelWidth, peelHeight));
            var back = new SolidColorBrush(PageBackColor(0.95));
            back.Freeze();
            peelFlapBack.Fill = back;
            peelFlapBack.Data = peelFlapShade.Data = outline;
            peelShadow.Width = peelWidth;
            peelShadow.Height = peelHeight;
            peelShadow.Clip = outline; // the fold's shadow falls on the sheet underneath only
            PlaceRings(host, dpi);
            BeginAnimation(FlipPeelProperty, null);
            SetValue(FlipPeelProperty, next ? 0.0 : 1.0);
            UpdatePeel(next ? 0 : 1);
            peelLifted.BeginAnimation(UIElement.OpacityProperty, null);
            peelLifted.Opacity = next ? 1 : 0;

            var flip = new WeekFlip { Storyboard = new System.Windows.Media.Animation.Storyboard(), Next = next, NewLabel = newLabel, Effect = effect };
            var board = flip.Storyboard;
            AddToBoard(board, new System.Windows.Media.Animation.DoubleAnimation(next ? 0 : 1, next ? 1 : 0, TimeSpan.FromMilliseconds(flipMs))
            {
                EasingFunction = FlipEase, FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            }, this, new PropertyPath(FlipPeelProperty));
            // Folded over the ring line, the sheet goes behind the calendar in the last quarter (‹: comes from behind it).
            AddToBoard(board, new System.Windows.Media.Animation.DoubleAnimation(next ? 1 : 0, next ? 0 : 1, TimeSpan.FromMilliseconds(flipMs * PeelFadeShare))
            {
                BeginTime = TimeSpan.FromMilliseconds(next ? flipMs * (1 - PeelFadeShare) : 0), EasingFunction = next ? FlipFadeOut : FlipFadeIn,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            }, peelLifted, OpacityPath);

            weekFlip = flip;
            board.Completed += (s, e) => { if (weekFlip == flip) { EndWeekFlip(); ContinueRush(); } };
            board.Begin(this, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace, true);
            if (!next) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => PictureIncoming(flip)));
            return true;
        }

        // The sheet's picture, flat and (faintly) through its back; it has a clear margin round the sheet and was drawn at
        // the sheet's sub-pixel position (see SnapshotPage), so it starts that much up-left of the sheet.
        private void SetPeelPicture(System.Windows.Media.Imaging.BitmapSource sheet)
        {
            var dpi = VisualTreeHelper.GetDpi((Visual)WeekDays.Parent);
            peelMarginX = (SheetMargin + peelPhase.X) / dpi.DpiScaleX;
            peelMarginY = (SheetMargin + peelPhase.Y) / dpi.DpiScaleY;
            peelFlat.Source = peelFlapHint.Source = sheet;
            peelFlat.Width = peelFlapHint.Width = sheet.PixelWidth / dpi.DpiScaleX;
            peelFlat.Height = peelFlapHint.Height = sheet.PixelHeight / dpi.DpiScaleY;
            foreach (var picture in new[] { peelFlat, peelFlapHint })
            {
                Canvas.SetLeft(picture, -peelMarginX);
                Canvas.SetTop(picture, -peelMarginY);
            }
        }

        // The page window for effect 2, in screen pixels: the sheet (its picture at whole pixels, so it stays sharp) and the
        // room its mirrored part needs over the whole peel (sampled: a sheet's height above, a little to the left for narrow
        // sheets).
        private bool PlacePeelWindow()
        {
            var host = (FrameworkElement)WeekDays.Parent;
            var dpi = VisualTreeHelper.GetDpi(host);
            double minX = 0, minY = 0, maxX = peelWidth;
            var corners = new[] { new Point(0, 0), new Point(peelWidth, 0), new Point(peelWidth, peelHeight), new Point(0, peelHeight) };
            for (int i = 1; i <= 64; i++)
            {
                PeelLine(i / 64.0, out Vector n, out double fold);
                foreach (var corner in corners)
                {
                    double past = n.X * corner.X + n.Y * corner.Y - fold;
                    if (past <= 0) continue;
                    Point mirrored = corner - 2 * past * n;
                    minX = Math.Min(minX, mirrored.X);
                    minY = Math.Min(minY, mirrored.Y);
                    maxX = Math.Max(maxX, mirrored.X);
                }
            }
            // 4/6 show the back only so far above the rings: no room beyond that (the rings' copies still fit).
            double share = BackAboveRings(flipShownEffect);
            if (!double.IsInfinity(share)) minY = Math.Max(minY, RingLineInSheet() - share * peelHeight);
            var rings = FindRings();
            if (rings != null) minY = Math.Min(minY, rings.TranslatePoint(new Point(), host).Y);
            Point origin = host.PointToScreen(new Point(0, 0));
            int x0 = (int)Math.Floor(origin.X), y0 = (int)Math.Floor(origin.Y), pad = 4 + SheetMargin;
            int left = x0 + (int)Math.Floor(minX * dpi.DpiScaleX) - pad, top = y0 + (int)Math.Floor(minY * dpi.DpiScaleY) - pad;
            int width = x0 + (int)Math.Ceiling(maxX * dpi.DpiScaleX) + 1 + pad - left, height = y0 + (int)Math.Ceiling(peelHeight * dpi.DpiScaleY) + 1 + pad - top;
            if (width < 4 || height < 4 || width > 16384 || height > 16384) return false;
            var handle = new WindowInteropHelper(flipWindow).Handle;
            // Twice: a first move onto a monitor with another DPI makes WPF rescale the window; the second puts it back.
            for (int i = 0; i < 2; i++)
                NativeMethods.SetWindowPos(handle, IntPtr.Zero, left, top, width, height, NativeMethods.SWP_NOACTIVATE | SwpNoZOrder | SwpNoOwnerZOrder);
            PlaceSheetInWindow(left, top);
            return true;
        }

        // The pets over the turning page: a live picture of the character view (their animation keeps playing), exactly
        // where the view is on the screen (the page window's top-left at left, top), in the page window's own DIPs.
        private void PlacePetsInWindow(int left, int top)
        {
            if (flipPets == null) return;
            double width = CharacterView.ActualWidth, height = CharacterView.ActualHeight;
            if (!PetsShown || !CharacterView.IsVisible || width <= 0 || height <= 0) { ClearFlipPets(); return; }
            var dpi = VisualTreeHelper.GetDpi(this);
            var own = VisualTreeHelper.GetDpi(flipWindow);
            double ownX = own.DpiScaleX > 0 ? own.DpiScaleX : 1, ownY = own.DpiScaleY > 0 ? own.DpiScaleY : 1;
            Point at = CharacterView.PointToScreen(new Point(0, 0));
            Canvas.SetLeft(flipPets, (at.X - left) / ownX);
            Canvas.SetTop(flipPets, (at.Y - top) / ownY);
            flipPets.Width = width * dpi.DpiScaleX / ownX;
            flipPets.Height = height * dpi.DpiScaleY / ownY;
            // An absolute Viewbox is in the view's parent's coordinates: the view's own offset on the canvas included.
            Vector offset = VisualTreeHelper.GetOffset(CharacterView);
            var box = new Rect(offset.X, offset.Y, width, height);
            if (!(flipPets.Fill is VisualBrush brush) || brush.Viewbox != box)
                flipPets.Fill = new VisualBrush(CharacterView)
                {
                    Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = box
                };
            flipPets.Visibility = Visibility.Visible;
        }

        private void ClearFlipPets()
        {
            if (flipPets == null) return;
            flipPets.Fill = null;
            flipPets.Visibility = Visibility.Collapsed;
        }

        // The sheet's coordinates in the page window (its top-left at left, top on the screen): this window's DIPs, scaled
        // should Windows have given the page window another DPI, the sheet exactly where the real one is (its picture then
        // starts at a whole pixel: see SnapshotPage).
        private void PlaceSheetInWindow(int left, int top)
        {
            var host = (FrameworkElement)WeekDays.Parent;
            var dpi = VisualTreeHelper.GetDpi(host);
            double own = VisualTreeHelper.GetDpi(flipWindow).DpiScaleX, scale = own > 0 ? dpi.DpiScaleX / own : 1;
            var fit = new ScaleTransform(scale, scale);
            fit.Freeze();
            peelRoot.RenderTransform = flipUnderRoot.RenderTransform = fit;
            PlacePetsInWindow(left, top);
            Point origin = host.PointToScreen(new Point(0, 0));
            var place = new TranslateTransform((origin.X - left) / dpi.DpiScaleX, (origin.Y - top) / dpi.DpiScaleY);
            place.Freeze();
            peelSheet.RenderTransform = place;
            // ‹: the picture of this page at whole pixels, where it was taken from (drawn at its sub-pixel position there).
            if (flipUnder.Source == null) return;
            Point at = host.PointToScreen(flipUnderRegion.TopLeft);
            Canvas.SetLeft(flipUnder, (Math.Floor(at.X) - SheetMargin - left) / dpi.DpiScaleX);
            Canvas.SetTop(flipUnder, (Math.Floor(at.Y) - SheetMargin - top) / dpi.DpiScaleY);
        }

        // The ring line — the paper's top edge, where the rings hold the sheet — in sheet coordinates (a frame's width above
        // the sheet's own top).
        private double RingLineInSheet()
        {
            var host = (FrameworkElement)WeekDays.Parent;
            return host.Parent is FrameworkElement paper ? paper.TranslatePoint(new Point(), host).Y : 0;
        }

        // Effects 3–6: nothing of the sheet above `ring` less the share of `unit` (the sheet's height) the effect allows, in
        // the element's own coordinates (y down). 3/4 fade their last part out toward that line, smoothly, so the paper seems
        // to go behind the binding rather than be cut; 5/6 end at the ring line itself, with only the clip's anti-aliased
        // edge. Effects 1/2 show all of it.
        private static void LimitAboveRings(UIElement element, int effect, double ring, double unit)
        {
            double share = BackAboveRings(effect);
            if (double.IsInfinity(share)) { element.Clip = null; element.OpacityMask = null; return; }
            double end = ring - share * unit;
            var clip = new RectangleGeometry(new Rect(-100000, end, 200000, 200000));
            clip.Freeze();
            element.Clip = clip;
            if (share <= 0) { element.OpacityMask = null; return; }
            var fade = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, end), EndPoint = new Point(0, end + share * unit * BackFadeShare) };
            foreach (double t in new[] { 0, 0.25, 0.5, 0.75, 1 }) // smoothstep: soft at both ends
                fade.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(255 * t * t * (3 - 2 * t)), 0, 0, 0), t));
            fade.Freeze();
            element.OpacityMask = fade;
        }

        private Grid FindRings() => WeekCalendar.Children.OfType<Grid>().FirstOrDefault(g => !g.IsHitTestVisible && g.Children.Count == 2 &&
            g.Children.OfType<Border>().Count() == 2);

        // Copies of the binder rings over the sheet, where the real ones are (they are not part of the sheet's picture).
        private void PlaceRings(FrameworkElement host, DpiScale dpi)
        {
            var rings = FindRings();
            if (rings == null || rings.ActualWidth < 1 || rings.ActualHeight < 1) { peelRings.Source = null; return; }
            // Drawn at the rings' own sub-pixel position and placed at whole pixels, like the sheet's picture.
            Point sheetAt = host.PointToScreen(new Point(0, 0)), ringsAt = rings.PointToScreen(new Point(0, 0));
            var phase = new Vector(ringsAt.X - Math.Floor(ringsAt.X), ringsAt.Y - Math.Floor(ringsAt.Y));
            int pixelWidth = (int)Math.Ceiling(rings.ActualWidth * dpi.DpiScaleX + phase.X), pixelHeight = (int)Math.Ceiling(rings.ActualHeight * dpi.DpiScaleY + phase.Y);
            Vector offset = VisualTreeHelper.GetOffset(rings);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.PushTransform(new TranslateTransform(phase.X / dpi.DpiScaleX, phase.Y / dpi.DpiScaleY));
                context.DrawRectangle(new VisualBrush(rings)
                {
                    Viewbox = new Rect(offset.X, offset.Y, rings.ActualWidth, rings.ActualHeight), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill
                }, null, new Rect(0, 0, rings.ActualWidth, rings.ActualHeight));
                context.Pop();
            }
            peelRings.Source = SheetFrom(drawing, pixelWidth, pixelHeight, dpi, ref flipRingSheet);
            peelRings.Width = pixelWidth / dpi.DpiScaleX;
            peelRings.Height = pixelHeight / dpi.DpiScaleY;
            Canvas.SetLeft(peelRings, (Math.Floor(ringsAt.X) - sheetAt.X) / dpi.DpiScaleX);
            Canvas.SetTop(peelRings, (Math.Floor(ringsAt.Y) - sheetAt.Y) / dpi.DpiScaleY);
        }

        // Puts the fold where the progress says: the flat part this side of it, the lifted part mirrored over it, their light
        // and shadow along it.
        private void UpdatePeel(double progress)
        {
            if (peelFlap == null || peelWidth <= 0) return;
            PeelLine(progress, out Vector n, out double fold);
            peelNx = n.X;
            peelNy = n.Y;
            peelFold = fold;
            double reach = 4 * (peelWidth + peelHeight);
            // The flat picture starts a clear margin up-left of the sheet: in its own coordinates the fold is that much further.
            peelFlat.Clip = HalfPlane(n.X, n.Y, fold + n.X * peelMarginX + n.Y * peelMarginY, false, reach);
            peelFlap.Clip = HalfPlane(n.X, n.Y, fold, true, reach);
            peelMirror.Matrix = new Matrix(1 - 2 * n.X * n.X, -2 * n.X * n.Y, -2 * n.X * n.Y, 1 - 2 * n.Y * n.Y, 2 * fold * n.X, 2 * fold * n.Y);
            var foot = new Point(n.X * fold, n.Y * fold);
            peelShadeBrush.StartPoint = foot;
            peelShadeBrush.EndPoint = foot + n * PeelShadeWidth;
            peelShadowBrush.StartPoint = foot - n * 0.5;
            peelShadowBrush.EndPoint = foot + n * PeelShadowWidth;
        }

        // The fold at `progress` through B (up the right edge) and C (left along the bottom edge, then up the left edge), as
        // n·p = fold in sheet coordinates, n pointing past it (toward the lower-right corner). Before it starts it lies just
        // past that corner; at the end it is the top edge.
        private void PeelLine(double progress, out Vector n, out double fold)
        {
            double w = peelWidth, h = peelHeight;
            if (progress <= 1e-6)
            {
                n = new Vector(w, h);
                n.Normalize();
                fold = n.X * w + n.Y * h + 1;
                return;
            }
            progress = Math.Min(1, progress);
            var b = new Point(w, h * (1 - progress));
            double along = progress * (w + h);
            var c = along <= w ? new Point(w - along, h) : new Point(0, h - (along - w));
            Vector d = b - c;
            n = new Vector(d.Y, -d.X);
            n.Normalize();
            if (n.X * (w - c.X) + n.Y * (h - c.Y) < 0) n = -n;
            fold = n.X * c.X + n.Y * c.Y;
        }

        // One side of the line n·p = fold (the far side, toward the lower-right corner, when beyond) as a large quadrilateral.
        private static Geometry HalfPlane(double nx, double ny, double fold, bool beyond, double reach)
        {
            var foot = new Point(nx * fold, ny * fold);
            var along = new Vector(-ny, nx) * reach;
            var away = new Vector(nx, ny) * (beyond ? reach : -reach);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(foot + along, true, true);
                context.PolyLineTo(new[] { foot - along, foot - along + away, foot + along + away }, false, false);
            }
            geometry.Freeze();
            return geometry;
        }

        private void EndWeekFlipAnimations(WeekFlip flip)
        {
            flip.Storyboard.Stop(this);
            flip.Storyboard.Remove(this);
            // Hold the page where the flip ends (› gone behind the band, ‹ lying on the calendar) while its window is still up.
            if (IsPeel(flip.Effect))
            {
                SetValue(FlipPeelProperty, flip.Next ? 1.0 : 0.0);
                if (peelLifted != null) peelLifted.Opacity = flip.Next ? 0 : 1;
            }
            else
            {
                SetValue(FlipTurnProperty, flip.Next ? FlipEndAngle : 0.0);
                if (flipViewport != null) flipViewport.Opacity = flip.Next ? 0 : 1;
            }
            WeekLabel.BeginAnimation(TextBlock.TextProperty, null);
            WeekLabel.BeginAnimation(UIElement.OpacityProperty, null);
            WeekLabel.ClearValue(UIElement.OpacityProperty);
            WeekLabel.Text = flip.NewLabel;
        }

        /// <summary>Ends a running flip at once and leaves the calendar exactly as without animation (tests call it too). The
        /// calendar already shows the week the flip goes to (‹ too: see Week_Click), so landing or being cut short changes
        /// nothing in it.</summary>
        internal void FinishWeekFlip()
        {
            bool rushing = rushPages != null;
            EndRush();
            EndWeekFlip();
            // Cut short mid-rush (‹ / ›, a move, hidden, …): today's page at once — unless weekStart was set to another page
            // meanwhile (a range pick or day count change end the rush first themselves; this guards any other caller).
            if (rushing && weekStart == rushAt && weekStart != rushTarget) { weekStart = rushTarget; Refresh(); }
        }

        // The running flip jumps to its end (its page settled) and the page window is parked.
        private void EndWeekFlip()
        {
            var flip = weekFlip;
            if (flip == null) { ParkFlipWindow(); return; }
            weekFlip = null;
            EndWeekFlipAnimations(flip);
            ParkFlipWindow();
        }

        // Property paths built from the properties themselves (type names in a path string need XAML context to resolve).
        private static readonly PropertyPath OpacityPath = new PropertyPath(UIElement.OpacityProperty);

        private static void AddToBoard(System.Windows.Media.Animation.Storyboard board, System.Windows.Media.Animation.AnimationTimeline animation,
            DependencyObject target, PropertyPath path)
        {
            System.Windows.Media.Animation.Storyboard.SetTarget(animation, target);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(animation, path);
            board.Children.Add(animation);
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

        public void UpdatePlayerBar()
        {
            if (closed || PlayerBar == null) return;
            bool shown = player != null && data.MiniPlayerVisible;
            PlayerBar.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            UpdateCalendarBottom();
            if (!shown)
            {
                // Hidden bar: its title stops scrolling and the seek line stops ticking.
                ApplyMarqueePlaying();
                seekTimer?.Stop();
                return;
            }
            CheckExternalSource();
            bool hasTracks = player.HasTracks, playing = player.IsPlaying, external = player.SelectedExternal != null;
            string title = player.NowPlaying;
            // The line under the controls shows the current song (YouTube video title or the file name) while one is loaded.
            PlayerTitleHost.ToolTip = title;
            PlayerTitleHost.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
            // Reachable with Tab too: Enter / Space do what a click does.
            System.Windows.Automation.AutomationProperties.SetName(PlayerTitleHost, title ?? string.Empty);
            System.Windows.Automation.AutomationProperties.SetHelpText(PlayerTitleHost, external ? "재생 중인 창으로 이동" : "지금 재생 목록 열기");
            ApplySeekHover();
            if (PlayerTitle.Text != (title ?? string.Empty)) { PlayerTitle.Text = title ?? string.Empty; StartTitleMarquee(); }
            else ApplyMarqueePlaying();
            UpdateVolumeIcon();
            string source = player.SourceName ?? "목록 없음";
            PlayerPlaylistText.Text = source;
            PlayerPlaylist.ToolTip = "지금 조작하는 곳: " + source + " · 눌러서 재생목록이나 다른 앱 선택";
            if (PlaylistPopup.IsOpen) BuildSourceChoices();
            PlayIcon.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            PauseIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            PlayerPlay.ToolTip = playing ? "일시정지" : hasTracks ? "재생" : "플레이리스트가 비어 있어요";
            PlayerPlay.IsEnabled = PlayerPrevious.IsEnabled = PlayerNext.IsEnabled = hasTracks;
            // 순차 / 랜덤 / 1곡 is the widget's own playlist order: another app keeps its own, so the button goes away meanwhile.
            PlayerMode.Visibility = external ? Visibility.Collapsed : Visibility.Visible;
            PlayMode mode = player.Mode;
            PlayerModeText.Text = mode == PlayMode.Shuffle ? "랜덤" : mode == PlayMode.RepeatOne ? "1곡" : "순차";
            ModeSequentialIcon.Visibility = mode == PlayMode.Sequential ? Visibility.Visible : Visibility.Collapsed;
            ModeShuffleIcon.Visibility = mode == PlayMode.Shuffle ? Visibility.Visible : Visibility.Collapsed;
            ModeRepeatOneIcon.Visibility = mode == PlayMode.RepeatOne ? Visibility.Visible : Visibility.Collapsed;
            PlayerMode.ToolTip = (mode == PlayMode.Shuffle ? "랜덤 재생" : mode == PlayMode.RepeatOne ? "한 곡 반복" : "순차 재생") +
                " · 누르면 " + (mode == PlayMode.Sequential ? "랜덤" : mode == PlayMode.Shuffle ? "한 곡 반복" : "순차") + "으로";
        }

        // The calendar ends just above the bar, whose height changes when the song line appears.
        private void UpdateCalendarBottom()
        {
            bool shown = PlayerBar.Visibility == Visibility.Visible;
            double bar = shown ? (PlayerBar.ActualHeight > 0 ? PlayerBar.ActualHeight : 40) + 6 + 6 : 8;
            if (Math.Abs(WeekCalendar.Margin.Bottom - bar) > 0.5) WeekCalendar.Margin = new Thickness(0, 4, 2, bar);
        }

        private void PlayerBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCalendarBottom();

        private void PlayerPlay_Click(object sender, RoutedEventArgs e) { if (!RecentlyDragged) player?.TogglePlay(); }
        private void PlayerNext_Click(object sender, RoutedEventArgs e) { if (!RecentlyDragged) player?.Next(); }
        private void PlayerPrevious_Click(object sender, RoutedEventArgs e) { if (!RecentlyDragged) player?.Previous(); }
        private void PlayerOpen_Click(object sender, RoutedEventArgs e)
        {
            if (RecentlyDragged) return;
            CloseDayPopup();
            if (player != null) player.ToggleSettings(); else music?.Invoke();
        }

        // ---- Scrolling song title: enters from the right edge and leaves on the left, then repeats. ----
        private const double MarqueeSpeed = 45; // px per second

        // A new title (or a new width): measured here; ApplyMarqueePlaying starts it entering from the right if music plays.
        private void StartTitleMarquee()
        {
            StopTitleMarquee();
            marqueeHost = PlayerTitleHost.ActualWidth;
            if (string.IsNullOrEmpty(PlayerTitle.Text) || marqueeHost <= 0) { marqueeHost = 0; PlayerTitleShift.X = 0; return; }
            PlayerTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            marqueeText = PlayerTitle.DesiredSize.Width;
            marqueeStart = marqueeHost / MarqueeSpeed; // the moment the title's start reaches the left edge
            marqueeFromRight = true;
            ApplyMarqueePlaying();
        }

        internal const int MarqueeFrameRate = 30;

        // Stopped and taken off the title: a clock left running (it repeats forever) would keep ticking, closed window and all.
        private void StopTitleMarquee()
        {
            var controller = marqueeClock?.Controller;
            if (controller != null) { controller.Stop(); controller.Remove(); }
            PlayerTitleShift.BeginAnimation(TranslateTransform.XProperty, null);
            marqueeClock = null;
        }

        // The title only scrolls while music plays and it can be seen (not hidden to the tray, not with the bar hidden).
        // Otherwise it rests with its start at the left edge, with no clock at all: even a paused animation clock keeps WPF
        // redrawing the whole transparent window every frame. Playing again scrolls on from the rest.
        private System.Windows.Media.Animation.AnimationClock marqueeClock;
        private double marqueeStart, marqueeHost, marqueeText; // marqueeHost 0: no title to move
        private bool marqueeFromRight;                          // a new title enters from the right edge; after a rest it moves on

        private void ApplyMarqueePlaying()
        {
            if (marqueeHost <= 0) return;
            if (player?.IsPlaying == true && IsVisible && !closed && PlayerBar.Visibility == Visibility.Visible)
            {
                if (marqueeClock != null) return; // already scrolling
                var run = new System.Windows.Media.Animation.DoubleAnimation(marqueeHost, -marqueeText, TimeSpan.FromSeconds((marqueeHost + marqueeText) / MarqueeSpeed))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                // 30 frames a second is smooth at this speed (1.5 px a frame) and halves the redraws of the whole transparent window.
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(run, MarqueeFrameRate);
                marqueeClock = run.CreateClock();
                PlayerTitleShift.ApplyAnimationClock(TranslateTransform.XProperty, marqueeClock);
                if (!marqueeFromRight) marqueeClock.Controller.Seek(TimeSpan.FromSeconds(marqueeStart), System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
                marqueeFromRight = false;
                return;
            }
            StopTitleMarquee();
            PlayerTitleShift.X = 0; // the title's start at the left edge
            marqueeFromRight = false;
        }

        private void OnMiniVisibleChanged(bool shown)
        {
            if (closed) return;
            ApplyMarqueePlaying();
            if (!shown)
            {
                seekTimer?.Stop(); // picked up again below when shown
                // 캐릭터 설정 and a placement belong to pets on screen: hidden (the TODO window's pets go with that window; the
                // mini window to the tray), they end — never left floating, or placing pets nobody sees.
                petSettings?.Close();
                EndPetPlacement();
                return;
            }
            ApplySeekHover();
            CheckExternalSource();
            // The TODO window's pets were only hidden meanwhile: actions changed in the mini window (or its 초기화) reach their
            // page now (sizes, 보이기 and spots come with the next layout).
            if (companion) for (int i = 0; i < SlotCount; i++) PostToPage(JsonConvert.SerializeObject(new { action = "animation", index = i, state = SlotAnimation(i) }));
            if (companion) SendPetFlips(); // and 좌우 반전 changed there meanwhile
            if (reattachWhenShown)
            {
                reattachWhenShown = false;
                Dispatcher.BeginInvoke(new Action(ReattachToDesktop), System.Windows.Threading.DispatcherPriority.Background);
            }
            // Shown again (from the tray): the screens may have changed meanwhile — the calendar must be reachable.
            if (!companion && (ensureWhenShown || HasSavedPlace))
            {
                ensureWhenShown = false;
                Dispatcher.BeginInvoke(new Action(() => EnsureBoardVisible("shown")), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void PlayerTitleHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged) StartTitleMarquee();
        }

        // ---- Volume: hovering the speaker shows a vertical volume bar above it; a click mutes / unmutes; the mouse wheel
        // (on the speaker or the bar) and ↑/↓ on the focused speaker change the volume by 5%. ----
        private bool syncingVolume;
        // What 음소거 해제 brings back: the last audible level, kept in the saved music settings so it survives a restart
        // while muted (50% when there is none).
        private double lastAudibleVolume
        {
            get => data.Music != null && data.Music.VolumeBeforeMute > 0.001 ? data.Music.VolumeBeforeMute : 0.5;
            set { if (data.Music != null && value > 0.001) data.Music.VolumeBeforeMute = Math.Min(1, value); }
        }
        private System.Windows.Threading.DispatcherTimer volumeBarTimer;

        // With another app as the source, its own last audible level (not saved): the widget's 음소거 해제 level is not
        // overwritten by the browser's.
        private double externalAudibleVolume;
        private string externalAudibleApp;

        private void UpdateVolumeIcon()
        {
            if (player == null) return;
            double volume = player.Volume;
            bool muted = volume <= 0.001;
            if (!muted) RememberAudible(volume); // also follows changes made in the other app / the volume mixer
            VolumeMuted.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
            VolumeWaveLow.Visibility = !muted ? Visibility.Visible : Visibility.Collapsed;
            VolumeWaveHigh.Visibility = volume > 0.5 ? Visibility.Visible : Visibility.Collapsed;
            PlayerVolume.ToolTip = null; // the bar above the speaker shows the level
            System.Windows.Automation.AutomationProperties.SetHelpText(PlayerVolume,
                "음량 " + Math.Round(volume * 100) + "% · 누르면 " + (muted ? "음소거 해제" : "음소거") + ", 휠이나 ↑↓로 5%씩");
            if (VolumePopup.IsOpen) SyncVolumeSlider();
        }

        // Keyboard focus keeps the bar only when it came from the keyboard (Tab / ↑↓): a mouse click also focuses the speaker,
        // and the bar must still go away once the pointer leaves.
        private bool volumeKeyboardShown;

        internal Func<bool> volumeHoverOverride = null; // tests: stands in for the pointer (a real one resting on a window on screen counts)

        private bool VolumeHovered => volumeHoverOverride?.Invoke() ??
            (PlayerVolume.IsMouseOver || VolumeBar.IsMouseOver || VolumeSlider.IsMouseCaptureWithin ||
            volumeKeyboardShown && PlayerVolume.IsKeyboardFocusWithin);

        // Pointer (or keyboard focus) on the speaker or on the bar: show the bar. Left both: hide it a moment later, so the
        // pointer can cross from the speaker onto the bar.
        private void PlayerVolume_HoverChanged(object sender, RoutedEventArgs e)
        {
            if (player == null) return;
            if (e.RoutedEvent == UIElement.GotKeyboardFocusEvent) volumeKeyboardShown = InputManager.Current.MostRecentInputDevice is KeyboardDevice;
            else if (e.RoutedEvent == UIElement.LostKeyboardFocusEvent) volumeKeyboardShown = false;
            if (VolumeHovered) ShowVolumeBar();
            else HideVolumeBarSoon();
        }

        private double volumeAtOpen = -1; // the level when the bar opened: closing it saves only when that changed
        private bool volumeBarHooked;

        internal void ShowVolumeBar()
        {
            volumeBarTimer?.Stop();
            if (VolumePopup.IsOpen || player == null) return;
            if (!volumeBarHooked)
            {
                volumeBarHooked = true;
                VolumePopup.CustomPopupPlacementCallback = CenterAboveSpeaker;
                // A StaysOpen popup does not go away with its window: hidden to the tray or closed, the bar must close too
                // (it would otherwise stay on the desktop, like the style drop-down did).
                IsVisibleChanged += (s, e) => { if (!(bool)e.NewValue) CloseVolumeBar(); };
                Closed += (s, e) => CloseVolumeBar();
                // Dragging the bar's knob and letting go outside the bar: releasing the capture raises no MouseLeave.
                VolumeBar.LostMouseCapture += (s, e) => HideVolumeBarSoon();
                // Save when the bar closes — at IsOpen = false itself: Popup.Closed only comes after the fade, and a quick
                // re-hover in between would reset the baseline first, losing the change.
                var isOpen = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                    System.Windows.Controls.Primitives.Popup.IsOpenProperty, typeof(System.Windows.Controls.Primitives.Popup));
                EventHandler closedNow = (s, e) => { if (!VolumePopup.IsOpen) SaveBarVolumeIfChanged(); };
                isOpen.AddValueChanged(VolumePopup, closedNow);
                Closed += (s, e) => isOpen.RemoveValueChanged(VolumePopup, closedNow);
            }
            volumeAtOpen = player.Volume;
            SyncVolumeSlider();
            RemeasurePopup(VolumePopup);
            VolumePopup.IsOpen = true;
        }

        private void CloseVolumeBar()
        {
            volumeBarTimer?.Stop();
            VolumePopup.IsOpen = false;
        }

        // Volume changes from a click / the wheel / ↑↓ are saved once they settle — a spin of the wheel is one save, not one
        // per notch (each rewrote schedules.json and refreshed the TODO list) — and not again when the bar closes. Another
        // app's level is that app's own: nothing of ours to save.
        private void SaveVolume()
        {
            if (player?.SelectedExternal == null) SaveSoon();
            if (VolumePopup.IsOpen && player != null) volumeAtOpen = player.Volume;
        }

        private void RememberAudible(double volume)
        {
            if (player?.SelectedExternal == null) { lastAudibleVolume = volume; return; }
            externalAudibleVolume = volume;
            externalAudibleApp = player.SelectedExternal;
        }

        // 음소거 해제: back to the level before the mute (the widget's saved one, or the picked app's own), else 50%.
        private double UnmuteLevel()
        {
            string app = player?.SelectedExternal;
            if (app == null) return lastAudibleVolume > 0.001 ? lastAudibleVolume : 0.5;
            return string.Equals(app, externalAudibleApp, StringComparison.OrdinalIgnoreCase) && externalAudibleVolume > 0.001 ? externalAudibleVolume : 0.5;
        }

        private void HideVolumeBarSoon()
        {
            if (!VolumePopup.IsOpen) return;
            if (volumeBarTimer == null)
            {
                volumeBarTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                volumeBarTimer.Tick += (s, e) => { volumeBarTimer.Stop(); HideVolumeBarIfLeft(); };
            }
            volumeBarTimer.Stop();
            volumeBarTimer.Start();
        }

        internal void HideVolumeBarIfLeft()
        {
            if (!VolumeHovered) VolumePopup.IsOpen = false;
        }

        // Centred right above the speaker (its transparent bottom strip touching the button); below it when there is no room.
        private static System.Windows.Controls.Primitives.CustomPopupPlacement[] CenterAboveSpeaker(Size popupSize, Size targetSize, Point offset) => new[]
        {
            new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, -popupSize.Height),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
            new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)
        };

        /// <summary>음소거 ↔ 음소거 해제 (back to the level it had before).</summary>
        internal void ToggleMute()
        {
            if (player == null) return;
            if (player.Volume > 0.001) { RememberAudible(player.Volume); SetVolume(0); }
            else SetVolume(UnmuteLevel());
            SaveVolume(); // saved like any other volume change
        }

        private void SyncVolumeSlider()
        {
            syncingVolume = true;
            try
            {
                VolumeSlider.Value = Math.Round(player.Volume * 100);
                VolumeText.Text = Math.Round(player.Volume * 100) + "%";
            }
            finally { syncingVolume = false; }
        }

        private void PlayerVolume_Click(object sender, RoutedEventArgs e)
        {
            if (player == null || RecentlyDragged) return;
            ToggleMute();
        }

        private void PlayerVolume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (player == null) return;
            e.Handled = true;
            SetVolume(player.Volume + (e.Delta > 0 ? 0.05 : -0.05));
            SaveVolume();
        }

        private void PlayerVolume_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (player == null || (e.Key != Key.Up && e.Key != Key.Down)) return;
            e.Handled = true;
            volumeKeyboardShown = true;
            ShowVolumeBar();
            SetVolume(player.Volume + (e.Key == Key.Up ? 0.05 : -0.05));
            SaveVolume();
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (syncingVolume || player == null) return;
            SetVolume(VolumeSlider.Value / 100);
        }

        private void SetVolume(double value)
        {
            value = Math.Max(0, Math.Min(1, Math.Round(value * 100) / 100));
            player.SetVolume(value);
            VolumeText.Text = Math.Round(value * 100) + "%";
            UpdateVolumeIcon();
        }

        // Save once after adjusting with the bar — not after every hover: each save rewrites schedules.json (and rotates its
        // backup) and refreshes the TODO list, and the bar now opens whenever the pointer passes over the speaker.
        private void SaveBarVolumeIfChanged()
        {
            if (player != null && player.SelectedExternal == null && volumeAtOpen >= 0 && Math.Abs(player.Volume - volumeAtOpen) > 0.001)
            {
                saveTimer?.Stop(); // this save covers a wheel step still waiting to be saved
                changed?.Invoke();
            }
            volumeAtOpen = -1;
        }

        // A StaysOpen=False popup closes on the mouse-down of the click that hits its own toggle button, and the
        // click would then reopen it. Remember when each popup closed so a second press on its button just closes it.
        private readonly System.Collections.Generic.Dictionary<System.Windows.Controls.Primitives.Popup, DateTime> popupClosedAt =
            new System.Collections.Generic.Dictionary<System.Windows.Controls.Primitives.Popup, DateTime>();

        private void TrackPopupClose(params System.Windows.Controls.Primitives.Popup[] popups)
        {
            // Watch IsOpen itself: the Closed event can arrive after the fade, too late for the click that caused it.
            var isOpen = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                System.Windows.Controls.Primitives.Popup.IsOpenProperty, typeof(System.Windows.Controls.Primitives.Popup));
            foreach (var popup in popups)
            {
                EventHandler closedNow = (s, e) => { if (!popup.IsOpen) popupClosedAt[popup] = DateTime.Now; };
                isOpen.AddValueChanged(popup, closedNow);
                // The descriptor is shared app-wide and holds its handlers strongly: unhooked, a closed window stays in memory.
                Closed += (s, e) => isOpen.RemoveValueChanged(popup, closedNow);
            }
        }

        /// <summary>Opens the popup, or closes it when it is open / was just closed by this same press.</summary>
        // A popup's content is laid out while it is detached (closed, or pre-built in PrewarmPopups), and WPF then keeps
        // stale sizes: a list filled in later still measured 0 px high, so the style drop-down opened as an empty box.
        // Mark the whole content for a fresh measure right before opening.
        private static void RemeasurePopup(System.Windows.Controls.Primitives.Popup popup)
        {
            if (popup?.Child != null) RemeasureTree(popup.Child);
        }

        private static void RemeasureTree(DependencyObject node)
        {
            (node as UIElement)?.InvalidateMeasure();
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) RemeasureTree(VisualTreeHelper.GetChild(node, i));
        }

        private bool TogglePopup(System.Windows.Controls.Primitives.Popup popup)
        {
            RemeasurePopup(popup); // its content may have changed since it was last shown
            if (popup.IsOpen) { popup.IsOpen = false; return false; }
            if (popupClosedAt.TryGetValue(popup, out DateTime closedAt) && (DateTime.Now - closedAt).TotalMilliseconds < 300) return false;
            popup.IsOpen = true;
            return true;
        }

        /// <summary>
        /// True when the schedule block menu is open or was just closed by this same press.
        /// A right-click while its buttons are showing should only make them go away.
        /// </summary>
        private bool CloseRightClickMenus()
        {
            if (BlockPopup.IsOpen) { BlockPopup.IsOpen = false; return true; }
            return popupClosedAt.TryGetValue(BlockPopup, out DateTime closedAt) && (DateTime.Now - closedAt).TotalMilliseconds < 300;
        }

        // ---- Playlist picker (dropdown next to ⏮) ----
        private void PlayerPlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (player == null || RecentlyDragged) return;
            BuildSourceChoices();
            TogglePopup(PlaylistPopup);
        }

        // Dropdown rows: the widget's playlists, then every other app that is playing media now.
        private void BuildSourceChoices()
        {
            if (player == null) return;
            bool external = player.SelectedExternal != null;
            var current = player.CurrentPlaylist;
            PlaylistChoices.ItemsSource = player.Playlists.Select(p => new
            {
                List = p, AppId = (string)null, p.Name, Sub = "", IsCurrent = !external && p == current, CountLabel = p.Tracks.Count + "곡"
            }).ToList();
            var apps = player.ExternalSources;
            AppChoicesSection.Visibility = apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AppChoices.ItemsSource = apps.Select(a => new
            {
                List = (MusicPlaylist)null, a.AppId, Name = a.SourceName, Sub = a.Display ?? "",
                IsCurrent = external && string.Equals(a.AppId, player.SelectedExternal, StringComparison.OrdinalIgnoreCase),
                CountLabel = a.IsPlaying ? "재생 중" : "일시정지"
            }).ToList();
        }

        private void PlaylistChoice_Click(object sender, RoutedEventArgs e)
        {
            var context = (sender as FrameworkElement)?.DataContext;
            var list = context?.GetType().GetProperty("List")?.GetValue(context) as MusicPlaylist;
            var appId = context?.GetType().GetProperty("AppId")?.GetValue(context) as string;
            PlaylistPopup.IsOpen = false;
            if (player == null) return;
            if (appId != null) player.SelectExternal(appId);
            else if (list != null && (list != player.CurrentPlaylist || player.SelectedExternal != null)) player.SelectPlaylist(list);
            else return;
            UpdatePlayerBar();
            if (QueuePopup.IsOpen) RefreshQueue();
        }

        // The app picked as the source went away (its tab or player closed): the bar would stay on it with dead buttons. Once
        // it has been missing from the playing apps for a few polls (read every 1.5 s while an app is the source), the bar goes
        // back to the widget's own playlist. While the window is hidden nothing is polled: it is looked at again when shown.
        private System.Windows.Threading.DispatcherTimer lostSourceTimer;
        internal TimeSpan lostSourceDelay = TimeSpan.FromSeconds(5);

        private bool SelectedSourceMissing()
        {
            string app = player?.SelectedExternal;
            return app != null && !(player.ExternalSources ?? new SystemMediaService.NowPlaying[0])
                .Any(s => string.Equals(s.AppId, app, StringComparison.OrdinalIgnoreCase));
        }

        private void CheckExternalSource()
        {
            if (player == null || closed) return;
            if (!SelectedSourceMissing()) { lostSourceTimer?.Stop(); return; }
            if (lostSourceTimer == null)
            {
                lostSourceTimer = new System.Windows.Threading.DispatcherTimer();
                lostSourceTimer.Tick += (s, e) => { lostSourceTimer.Stop(); FallBackFromLostSource(); };
            }
            if (lostSourceTimer.IsEnabled) return; // counting already
            lostSourceTimer.Interval = lostSourceDelay;
            lostSourceTimer.Start();
        }

        private void FallBackFromLostSource()
        {
            if (player == null || closed || !IsVisible || !SelectedSourceMissing()) return;
            var list = player.CurrentPlaylist ?? player.Playlists?.FirstOrDefault();
            if (list == null) return; // no playlist of ours to go back to
            player.SelectPlaylist(list);
            UpdatePlayerBar();
            if (QueuePopup.IsOpen) RefreshQueue();
        }

        // ---- Now-playing list (song title click) ----
        // Another app plays (browser YouTube, Spotify, …): clicking the title brings its playing tab / window to the front.
        // Tests replace the activation so no real window moves.
        internal static Func<string, string, System.Threading.Tasks.Task<bool>> activateExternalOverride = null;
        private bool activatingExternal; // one activation at a time (double clicks while the tab search runs are ignored)

        private async void ActivateExternalSource()
        {
            if (activatingExternal) return;
            string appId = player.SelectedExternal;
            var source = player.ExternalSources?.FirstOrDefault(s => string.Equals(s.AppId, appId, StringComparison.OrdinalIgnoreCase));
            string title = source?.Title ?? player.NowPlaying;
            activatingExternal = true;
            try { await (activateExternalOverride ?? MediaAppActivator.ActivateAsync)(appId, title); }
            // async void: anything escaping here would take the whole app down (there is no global handler), and a failed
            // "bring forward" is never worth that — UI Automation on a closing/hung browser can throw many kinds of errors.
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { }
            finally { activatingExternal = false; }
        }

        private void PlayerTitle_Click(object sender, MouseButtonEventArgs e)
        {
            if (player == null || RecentlyDragged) return;
            e.Handled = true;
            OpenPlayerTitle();
        }

        // The title with the keyboard (Tab to it): Enter or Space does what a click does.
        private void PlayerTitle_KeyDown(object sender, KeyEventArgs e)
        {
            if (player == null || e.Key != Key.Enter && e.Key != Key.Space) return;
            e.Handled = true;
            OpenPlayerTitle();
        }

        private void OpenPlayerTitle()
        {
            if (player.SelectedExternal != null) { ActivateExternalSource(); return; } // other apps keep their own queue
            CloseDayPopup();
            RangePopup.IsOpen = false;
            RefreshQueue();
            if (TogglePopup(QueuePopup)) ScrollQueueToCurrent();
        }

        // ---- Seek bar: the line under the scrolling title while a song is loaded ----
        private System.Windows.Threading.DispatcherTimer seekTimer;
        private bool seekDragging, updatingSeek;

        // The position only moves while music plays and can be seen: the timer runs only then (a pause, the tray or a hidden
        // bar stop it; playing again or showing the window starts it).
        private bool SeekTicks => player != null && !closed && IsVisible && PlayerBar.Visibility == Visibility.Visible &&
            SeekRow.Visibility == Visibility.Visible && player.IsPlaying;

        private void StartSeekTimer()
        {
            if (seekTimer == null)
            {
                seekTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                seekTimer.Tick += (s, e) =>
                {
                    if (!SeekTicks) seekTimer.Stop();
                    UpdateSeek();
                };
            }
            seekTimer.Start();
        }

        internal bool SeekTimerRunning => seekTimer?.IsEnabled == true;

        private void PlayerBar_HoverChanged(object sender, MouseEventArgs e) => ApplySeekHover();

        // With a song loaded the bar always shows the scrolling title and, under it, the seek row — both at once, so the title
        // (click → now-playing list) is always reachable and the bar keeps one height (nothing moves on hover).
        private void ApplySeekHover()
        {
            if (player == null || SeekRow == null) return;
            bool songLoaded = !string.IsNullOrEmpty(player.NowPlaying);
            SeekRow.Visibility = songLoaded ? Visibility.Visible : Visibility.Collapsed;
            PlayerTitleHost.Visibility = songLoaded ? Visibility.Visible : Visibility.Collapsed;
            if (songLoaded) UpdateSeek(); // once now (a pause, a seek, a new song); then every 500 ms while it plays
            if (SeekTicks) { if (seekTimer == null || !seekTimer.IsEnabled) StartSeekTimer(); }
            else seekTimer?.Stop();
        }

        private static string Clock(TimeSpan t) =>
            t.TotalHours >= 1 ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                              : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);

        // Shows position / length; the bar stays still while the user is dragging it.
        public void UpdateSeek()
        {
            if (player == null || SeekSlider == null) return;
            TimeSpan? duration = player.Duration, position = player.Position;
            bool known = duration.HasValue && duration.Value > TimeSpan.Zero;
            SeekSlider.IsEnabled = known;
            SeekTotal.Text = known ? Clock(duration.Value) : "--:--";
            if (seekDragging) return;
            double seconds = Math.Max(0, Math.Min(known ? duration.Value.TotalSeconds : 0, position?.TotalSeconds ?? 0));
            updatingSeek = true;
            try
            {
                SeekSlider.Maximum = known ? duration.Value.TotalSeconds : 1;
                SeekSlider.Value = seconds;
            }
            finally { updatingSeek = false; }
            SeekCurrent.Text = Clock(TimeSpan.FromSeconds(seconds));
        }

        private void SeekSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => seekDragging = true;

        private void SeekSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            seekDragging = false;
            player?.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
            ApplySeekHover();
        }

        // A click on the track (IsMoveToPointEnabled) or the mouse wheel moves the value without a drag: seek right away.
        private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (updatingSeek || SeekCurrent == null) return;
            SeekCurrent.Text = Clock(TimeSpan.FromSeconds(SeekSlider.Value));
            if (!seekDragging) player?.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
        }

        // One row of the now-playing list, updated in place.
        public sealed class QueueRow : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            private MusicTrack track; private string title, marker; private bool isCurrent, canUp, canDown;
            private void Set<T>(ref T field, T value, string name)
            {
                if (Equals(field, value)) return;
                field = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }
            public MusicTrack Track { get => track; set => Set(ref track, value, nameof(Track)); }
            public string Title { get => title; set => Set(ref title, value, nameof(Title)); }
            public bool IsCurrent { get => isCurrent; set => Set(ref isCurrent, value, nameof(IsCurrent)); }
            public string Marker { get => marker; set => Set(ref marker, value, nameof(Marker)); }
            public bool CanUp { get => canUp; set => Set(ref canUp, value, nameof(CanUp)); }
            public bool CanDown { get => canDown; set => Set(ref canDown, value, nameof(CanDown)); }
        }

        // The list is virtualized and its rows are kept: a refresh (every playback change, each ▲▼) updates them in place, so
        // a long playlist opens fast and the list stays where the user scrolled it. Another length (a different playlist,
        // songs added or removed) builds it anew.
        private System.Collections.ObjectModel.ObservableCollection<QueueRow> queueRows = new System.Collections.ObjectModel.ObservableCollection<QueueRow>();

        public void RefreshQueue()
        {
            if (player == null || QueueList == null) return;
            var tracks = player.Queue;
            var current = player.Current;
            QueueTitle.Text = "지금 재생 목록 · " + tracks.Count + "곡";
            if (queueRows.Count != tracks.Count)
            {
                queueRows = new System.Collections.ObjectModel.ObservableCollection<QueueRow>(tracks.Select(t => new QueueRow()));
                QueueList.ItemsSource = queueRows;
            }
            else if (QueueList.ItemsSource != queueRows) QueueList.ItemsSource = queueRows;
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                var row = queueRows[i];
                row.Track = t;
                row.Title = t.Title ?? t.Source;
                row.IsCurrent = t == current;
                row.Marker = t == current ? (player.IsPlaying ? "▶" : "❚❚") : (i + 1).ToString(CultureInfo.InvariantCulture);
                row.CanUp = i > 0;
                row.CanDown = i < tracks.Count - 1;
            }
        }

        // Opened: the song playing now is in view, a couple of rows from the top.
        private void ScrollQueueToCurrent()
        {
            int index = -1;
            for (int i = 0; i < queueRows.Count && index < 0; i++) if (queueRows[i].IsCurrent) index = i;
            if (index < 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (QueuePopup.IsOpen) QueueScroll?.ScrollToVerticalOffset(Math.Max(0, index - 2)); // offsets count rows here
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        internal ScrollViewer QueueScroll => QueueList?.Template?.FindName("QueueScroll", QueueList) as ScrollViewer;

        private static MusicTrack QueueTrack(object sender) =>
            (sender as FrameworkElement)?.DataContext?.GetType().GetProperty("Track")?.GetValue(((FrameworkElement)sender).DataContext) as MusicTrack;

        private void QueuePlay_Click(object sender, RoutedEventArgs e)
        {
            var track = QueueTrack(sender);
            if (track != null) player?.Play(track);
        }

        private void QueueMove_Click(object sender, RoutedEventArgs e)
        {
            var track = QueueTrack(sender);
            if (track == null || player == null) return;
            int index = player.Queue.ToList().IndexOf(track);
            if (index < 0) return;
            player.Move(track, index + int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture));
            RefreshQueue();
        }

        // 순차 → 랜덤 → 1곡 → 순차
        private void PlayerMode_Click(object sender, RoutedEventArgs e)
        {
            if (player == null || RecentlyDragged || player.SelectedExternal != null) return; // another app keeps its own order
            PlayMode mode = player.Mode;
            player.SetMode(mode == PlayMode.Sequential ? PlayMode.Shuffle : mode == PlayMode.Shuffle ? PlayMode.RepeatOne : PlayMode.Sequential);
            UpdatePlayerBar();
        }

        public void SetPlayerVisible(bool visible)
        {
            data.MiniPlayerVisible = visible;
            UpdatePlayerBar();
            changed?.Invoke();
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
            weekStart = DayCount == 7 ? StartOfWeek(date) : date.Date;
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
        }


        // The pets are sized from the board, not from the calendar (whose height follows the music bar's rows): only the
        // calendar's first real size (none before the first layout) or a new size basis (the bar shown / hidden, the board
        // resized) re-fits them — not every song line coming and going.
        private double petBasisSeen = double.NaN;

        private void WeekCalendar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.HeightChanged || resizingBoard) return; // a corner-grip drag: the pets re-fit when it ends
            double basis = PetSizeBasis();
            if (e.PreviousSize.Height > 0 && basis == petBasisSeen) return;
            petBasisSeen = basis;
            UpdateCharacterSize();
        }

        // The music bar at rest (buttons only) and the calendar's other margins: the pets' size basis leaves this much of
        // the board for the bar, so the bar growing (song title / seek line appearing between tracks) never resizes pets.
        private const double RestingBarRoom = 41 + 12, CalendarTopMargin = 4, CalendarBottomGapNoBar = 8;

        /// <summary>Calendar height the pets are sized from: the board minus a resting music bar. Changes only when the
        /// board is resized or the bar is shown / hidden — not when the bar's title or seek rows come and go.</summary>
        internal double PetSizeBasis() =>
            companion ? 260 // the TODO window's pets use a fixed base (a 260 px calendar) — its window height is not a calendar
                : Math.Max(80, BoardHeight - CalendarTopMargin - (PlayerBar != null && PlayerBar.Visibility == Visibility.Visible ? RestingBarRoom : CalendarBottomGapNoBar));

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
                Width = Clamp(gripStartBounds.Width + dx, MinWidthNow, maxWidth);
            else
            {
                double next = Clamp(gripStartBounds.Width - dx, MinWidthNow, maxWidth);
                Left = gripStartBounds.Right - next;
                Width = next;
            }
            if (corner[0] == 'B')
                Height = Clamp(gripStartBounds.Height + dy, MinHeightNow, maxHeight);
            else
            {
                double next = Clamp(gripStartBounds.Height - dy, MinHeightNow, maxHeight);
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
            resizingBoard = false;
            UpdateCharacterSize();
            KeepPetsApart(gripStartPets);
            gripStartPets = null;
            UpdateLayout();
            KeepOnScreen();
            SavePosition();
        }

        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(Math.Max(min, max), value));
        private void Settings_Click(object sender, RoutedEventArgs e) { if (RecentlyDragged) return; CloseDayPopup(); settings?.Invoke(); }

        public void SetReminderWarning(string message) => HeaderSettingsButton.ToolTip = string.IsNullOrWhiteSpace(message) ? "설정" : message;

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
            bool foreignCapture = captured != null && !IsWithin(captured, CharacterHost);
            if (!foreignCapture && IsWithin(source, CharacterHost)) return; // the normal way in
            Point at = e.GetPosition(CharacterHost);
            var rects = PetRects();
            bool inPet = rects.Any(r => r.Width > 0 && r.Contains(at));
            if (!inPet) return;
            bool inHitBox = new[] { PetHit0, PetHit1, PetHit2 }.Any(h => h.Visibility == Visibility.Visible &&
                new Rect(Canvas.GetLeft(h), Canvas.GetTop(h), h.Width, h.Height).Contains(at));
            bool bareBackground = source == null || source == this || source == OuterRoot;
            if (!(foreignCapture && inHitBox) && !bareBackground) return; // e.g. the calendar under a pet's corner keeps its click
            PetLog.Write("pet-click", (right ? "right" : "left") + " rescued: " + (foreignCapture ? "capture held by " + captured.GetType().Name : "outside the hit box"));
            if (foreignCapture) Mouse.Capture(null);
            if (right) Character_RightClick(CharacterHost, e);
            else Character_MouseDown(CharacterHost, e);
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
            SavePosition();
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
        private double MinHeightNow => MinBoardHeight + 2 * Edge + petPads.Top + petPads.Bottom;

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
            if (!IsFinite(Left) || !IsFinite(Top)) return;
            if (companion)
            {
                // Never overwrite the mini window's own place; the TODO window's pets keep their spots in their own memory.
                if (loadedSlotKeys != null) RememberSpots();
                SaveSoon();
                return;
            }
            data.MiniPosition = new MonitorStateData { Left = Left, Top = Top, Width = Width, Height = Height };
            data.MiniBoard = new MonitorStateData { Left = Left + Edge + petPads.Left, Top = Top + Edge + petPads.Top, Width = BoardWidth, Height = BoardHeight };
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

        private void OnDisplayChanged(object sender, EventArgs e) =>
            Dispatcher.BeginInvoke(new Action(ReattachToDesktop), System.Windows.Threading.DispatcherPriority.Background);

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Dispatcher.BeginInvoke(new Action(ReattachToDesktop), System.Windows.Threading.DispatcherPriority.Background);
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
        private bool HasSavedPlace => data.MiniBoard != null || data.MiniPosition != null;

        // Monitors / work area changed (seen by the window itself as well as through SystemEvents): checked once they settle.
        private IntPtr WatchScreens(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_DISPLAYCHANGE = 0x007E, WM_SETTINGCHANGE = 0x001A, SPI_SETWORKAREA = 0x002F;
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
                else if (IsAllSchedulesOpen) SetAllSchedulesOpen(false);
                else HideToTray(); // nothing open: hide the widget; the tray icon (열기) brings it back
                return;
            }
        }

        // ---- The pets' web view: start it, and bring it back when it dies or stops drawing ----
        // Pets live only inside the web page; if its renderer / browser process exits (sleep, GPU reset, runtime update,
        // memory pressure) or the composition capture stalls, the calendar stays but the pets vanish. Recovery runs on the
        // view's own ProcessFailed event and on display / power / session / show events, plus a quiet look every 2 minutes
        // while the window shows (a stall that came with no event at all; see WatchPetsAsync).
        private System.Windows.Threading.DispatcherTimer recoveryTimer, healthTimer;
        private bool recoveryRecreate;              // the pending recovery must build a new view (browser process gone)
        private string recoveryReason;
        private readonly System.Collections.Generic.List<DateTime> recoveries = new System.Collections.Generic.List<DateTime>();
        private bool viewReady;                     // CoreWebView2 initialised on the current CharacterView
        internal Action<bool, string> recoveryOverride = null;   // tests: (recreate, reason) instead of reloading a real view
        internal Func<System.Threading.Tasks.Task<bool>> healthCheckOverride = null; // tests: the view's answer to the health check
        internal Action<string> pagePostOverride = null;         // tests: messages that would go to the page
        internal Func<bool?> petPixelsOverride = null;           // tests: what the pixel check would see (null = could not tell)
        private System.Windows.Threading.DispatcherTimer pixelTimer;
        private System.Windows.Threading.DispatcherTimer petWatchTimer;   // the quiet look every PetWatchInterval (shown only)
        internal static readonly TimeSpan PetWatchDefault = TimeSpan.FromMinutes(2);
        internal static TimeSpan PetWatchInterval = PetWatchDefault;      // checks stretch it: no look in the middle of theirs
        private bool pixelConfirming;                     // the scheduled pixel check is the second look after an empty one
        private DateTime pageReadyAt;                     // when the current page said "ready" (MinValue: loading)
        // When the current page was asked for (MinValue: none yet). A page that never says "ready" (navigation failed, a
        // script error before it, the message lost) still answers the health check, so it is reloaded after NeverReadyAfter.
        private DateTime navigatedAt = DateTime.MinValue;
        internal static TimeSpan NeverReadyAfter = TimeSpan.FromSeconds(15);
        private bool PageNeverReady => pageReadyAt == DateTime.MinValue && navigatedAt != DateTime.MinValue && DateTime.Now - navigatedAt > NeverReadyAfter;
        // A view rebuilt because it looked empty is looked at again once its new page is ready. If that fresh view looks
        // empty too, "empty" means nothing for that layout (a black pet, a pet whose part outside the calendar is only its
        // transparent margin): it is not rebuilt again for it — no rebuild on every reopen — until it shows or pets move.
        private bool pixelVerifyAfterRebuild;
        private readonly System.Collections.Generic.HashSet<string> pixelDistrust = new System.Collections.Generic.HashSet<string>();

        private async System.Threading.Tasks.Task InitCharacterViewAsync()
        {
            viewReady = false;
            try
            {
                var view = CharacterView;
                view.DefaultBackgroundColor = System.Drawing.Color.Transparent;
                await EmbeddedBrowser.InitializeAsync(view);
                if (closed || view != CharacterView) return;
                view.CoreWebView2.WebMessageReceived += (sender, args) =>
                {
                    // A page message must never take the app down: odd values are ignored, a view that just died too.
                    try { OnPageMessage(view, args.Source, args.WebMessageAsJson); }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException || ex is ArgumentException)
                    { PetLog.Write("page-message", ex.GetType().Name + ": " + ex.Message); }
                };
                view.CoreWebView2.NewWindowRequested += (sender, args) => args.Handled = true;
                view.CoreWebView2.ProcessFailed += (sender, args) => { if (view == CharacterView) HandleCharacterProcessFailed(args.ProcessFailedKind); };
                viewReady = true;
                ReloadCharacter();
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex) || ex is System.Runtime.InteropServices.COMException ||
                                       ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException || ex is ObjectDisposedException)
            { CharacterViewStartFailed(ex); }
        }

        internal void CharacterViewStartFailed(Exception ex)
        {
            if (closed) return;
            ShowCharacterError("캐릭터를 표시하려면 WebView2 설치를 확인해 주세요.");
            PetLog.Write("init-failed", ex.GetType().Name + ": " + ex.Message);
            // A start that failed once (the runtime busy updating, a browser process that died while starting) is tried
            // again with a new view — at most 3 times a minute, then once a minute.
            RequestCharacterRecovery(true, "init-failed");
        }

        private void OnPageMessage(Microsoft.Web.WebView2.Wpf.WebView2CompositionControl view, string source, string json)
        {
            if (closed || view != CharacterView || source == null || !source.StartsWith(EmbeddedBrowser.Origin, StringComparison.Ordinal)) return;
            JObject message;
            try { message = JObject.Parse(json); } catch (JsonException) { return; }
            string type = PageMessage.Text(message, "type");
            if (type == "ready")
            {
                pageReadyAt = DateTime.Now;
                EnsureViewClickThrough();
                // Once more when the page's title (what finds its window) has surely settled.
                var again = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                again.Tick += (s, e) => { again.Stop(); EnsureViewClickThrough(); };
                again.Start();
                string load = FreshCharacterPayload();
                if (load != null) view.CoreWebView2?.PostWebMessageAsJson(load);
                lastLayoutJson = null;
                SendPetLayout(); // + the layout as it is now
                AfterPageReady();
            }
            else if (type == "invalid")
                ShowPetImageError(PageMessage.Text(message, "reason"), Math.Max(0, PageMessage.Index(message, Math.Max(SlotCount, slotCharacters.Count))));
        }

        /// <summary>
        /// The stored load with the music state and each pet's action as they are now: a music start / stop or an action
        /// picked while the page was loading went to the old page and was lost, and the new page must not start from the
        /// values of when the reload began. Remembers the music state the page now has (SendMusicState sends only changes).
        /// </summary>
        internal string FreshCharacterPayload()
        {
            if (characterPayload == null) return null;
            JObject payload;
            try { payload = JObject.Parse(characterPayload); } catch (JsonException) { return characterPayload; }
            bool playing = MusicPlaying;
            payload["music"] = playing;
            int count = payload["characters"] is JArray list ? list.Count : 0;
            payload["state"] = SlotAnimation(0);
            payload["states"] = new JArray(Enumerable.Range(0, count).Select(i => (object)SlotAnimation(i)).ToArray());
            payload["flips"] = new JArray(Enumerable.Range(0, count).Select(i => (object)PetFlipped(i)).ToArray()); // 좌우 반전 as it is now
            musicSent = musicPlaying == null ? (bool?)null : playing;
            characterPayload = payload.ToString(Formatting.None);
            return characterPayload;
        }

        internal void HandleCharacterProcessFailed(Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind kind)
        {
            switch (kind)
            {
                case Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.BrowserProcessExited:
                    RequestCharacterRecovery(true, "browser-exited"); break;
                case Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.RenderProcessExited:
                case Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                case Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                case Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.GpuProcessExited:
                    RequestCharacterRecovery(false, kind.ToString()); break;
                default:
                    PetLog.Write("process-failed", kind.ToString()); break; // utility processes etc.: the page keeps drawing
            }
        }

        /// <summary>Reload the page (or build a new view) about a second from now; at most 3 times a minute.</summary>
        internal void RequestCharacterRecovery(bool recreate, string reason)
        {
            if (closed) return;
            PetLog.Write(recreate ? "recover-view" : "recover-page", reason);
            recoveryRecreate |= recreate;
            recoveryReason = reason;
            if (recoveryTimer == null)
            {
                recoveryTimer = new System.Windows.Threading.DispatcherTimer();
                recoveryTimer.Tick += (s, e) => { recoveryTimer.Stop(); RunPendingRecovery(); };
            }
            // Already on its way: keep that time and just merge what it has to do. Restarting the countdown here let failure
            // events that repeat (a hung renderer reports "unresponsive" every few seconds, every display change runs a
            // health check) postpone the recovery for good — with the one-minute backoff the pets never came back.
            if (recoveryTimer.IsEnabled) return;
            recoveries.RemoveAll(t => DateTime.Now - t > TimeSpan.FromMinutes(1));
            // Backoff: a view that keeps dying is tried again after a minute instead of in a tight loop.
            recoveryTimer.Interval = recoveries.Count >= 3 ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(1);
            recoveryDue = DateTime.Now + recoveryTimer.Interval;
            recoveryTimer.Start();
        }

        private DateTime recoveryDue; // when the pending recovery runs (tests)

        internal void RunPendingRecovery()
        {
            if (closed) return;
            recoveryTimer?.Stop();
            bool recreate = recoveryRecreate;
            string reason = recoveryReason;
            recoveryRecreate = false;
            recoveries.Add(DateTime.Now);
            if (recoveryOverride != null) { recoveryOverride(recreate, reason); return; }
            if (recreate || !viewReady) { var _ = RecreateCharacterViewAsync(); return; }
            try { ReloadCharacter(); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException || ex is ObjectDisposedException)
            { var _ = RecreateCharacterViewAsync(); } // the view itself is gone: build a new one
        }

        // A dead browser process cannot be reused: drop the view and put a fresh one in the same place.
        private async System.Threading.Tasks.Task RecreateCharacterViewAsync()
        {
            if (closed) return;
            EmbeddedBrowser.ResetEnvironment();
            var old = CharacterView;
            int index = CharacterHost.Children.IndexOf(old);
            var fresh = new Microsoft.Web.WebView2.Wpf.WebView2CompositionControl
            {
                Width = old.Width, Height = old.Height, IsHitTestVisible = false, Background = System.Windows.Media.Brushes.Transparent,
                AllowExternalDrop = false
            };
            System.Windows.Automation.AutomationProperties.SetName(fresh, System.Windows.Automation.AutomationProperties.GetName(old));
            Canvas.SetLeft(fresh, Canvas.GetLeft(old));
            Canvas.SetTop(fresh, Canvas.GetTop(old));
            CharacterHost.Children.Remove(old);
            CharacterHost.Children.Insert(Math.Max(0, index), fresh); // same z-order: under the hit boxes and the error text
            CharacterView = fresh;
            pageReadyAt = DateTime.MinValue;
            navigatedAt = DateTime.MinValue; // the new view asks for its page itself (ReloadCharacter)
            // FindName("CharacterView") must find the new view too, not the disposed one.
            try { UnregisterName("CharacterView"); } catch (ArgumentException) { }
            RegisterName("CharacterView", fresh);
            try { old.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException) { }
            lastLayoutJson = null;
            await InitCharacterViewAsync();
        }

        // Display / DPI change, resume, unlock, shown again: ask the page for a sign of life a second later. No answer →
        // reload it; an answer → resend the layout and make it repaint (restarts a stalled capture).
        internal void ScheduleHealthCheck(string reason)
        {
            if (closed) return;
            if (healthTimer == null)
            {
                healthTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                healthTimer.Tick += async (s, e) =>
                {
                    healthTimer.Stop();
                    // An async void timer handler: anything thrown here would take the whole app down (there is no
                    // unhandled-exception handler), for a check that only exists to help. Log it and carry on.
                    try { await RunHealthCheckAsync((string)healthTimer.Tag); }
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { PetLog.Write("check-failed", ex.GetType().Name + ": " + ex.Message); }
                };
            }
            healthTimer.Tag = reason;
            healthTimer.Stop();
            healthTimer.Start();
        }

        internal async System.Threading.Tasks.Task RunHealthCheckAsync(string reason)
        {
            if (closed || !IsVisible && healthCheckOverride == null) return; // hidden: checked again when shown
            if (recoveryTimer != null && recoveryTimer.IsEnabled) return; // a reload / new view is already on its way
            bool? alive = await PageAnswersAsync();
            if (alive == null || closed) return; // still starting (or already recovering)
            if (alive == false) { RequestCharacterRecovery(false, "no-answer-" + reason); return; }
            if (PageNeverReady) { RequestCharacterRecovery(false, "never-ready-" + reason); return; }
            RepaintPets();
            SchedulePetPixelCheck(reason, false); // the page answered; make sure the window really shows it
        }

        // The page's sign of life: it runs a script within 3 s. Null: nothing to ask yet (still starting, or recovering).
        private async System.Threading.Tasks.Task<bool?> PageAnswersAsync()
        {
            if (healthCheckOverride != null) return await healthCheckOverride();
            if (!viewReady || LiveCore == null) return null;
            try
            {
                var answer = CharacterView.ExecuteScriptAsync("1");
                return await System.Threading.Tasks.Task.WhenAny(answer, System.Threading.Tasks.Task.Delay(3000)) == answer && !answer.IsFaulted;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException || ex is ObjectDisposedException)
            { return false; }
        }

        // The quiet watch: runs while the window shows (stopped while hidden or closed). A view can also stop drawing with no
        // Windows event at all (a GPU hiccup, a remote-desktop tool switching its capture); every PetWatchInterval it asks the
        // page for a sign of life and looks at the pets' pixels. Healthy → nothing is repainted and nothing is logged; no
        // answer → reload; empty → repaint, look again, then a new view — the same steps as after a display change.
        private void UpdatePetWatch()
        {
            if (closed || !IsVisible) { petWatchTimer?.Stop(); return; }
            if (petWatchTimer == null)
            {
                petWatchTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background);
                petWatchTimer.Tick += async (s, e) =>
                {
                    try { await WatchPetsAsync(); } // async void: never let it crash the app
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { PetLog.Write("check-failed", ex.GetType().Name + ": " + ex.Message); }
                };
            }
            petWatchTimer.Interval = PetWatchInterval;
            if (!petWatchTimer.IsEnabled) petWatchTimer.Start();
        }

        internal async System.Threading.Tasks.Task WatchPetsAsync()
        {
            if (closed || !IsVisible || !PetsShown || placingPets || weekFlip != null) return;
            // A check or recovery already on its way (an event came just now) does the job.
            if (recoveryTimer != null && recoveryTimer.IsEnabled || healthTimer != null && healthTimer.IsEnabled ||
                pixelTimer != null && pixelTimer.IsEnabled) return;
            bool? alive = await PageAnswersAsync();
            if (alive == null || closed) return;
            if (alive == false) { RequestCharacterRecovery(false, "no-answer-watch"); return; }
            if (PageNeverReady) { RequestCharacterRecovery(false, "never-ready-watch"); return; }
            await CheckPetPixelsAsync("watch", false);
        }

        // After a display change (Parsec adding or removing its virtual monitor, a monitor waking up, a DPI change), resume or
        // unlock, the page can answer while the view's capture stays empty: the pets are gone although nothing failed. So a
        // moment after the repaint, look at what the window really shows where the pets stand. Empty → repaint and look once
        // more; still empty → build a new view (a stalled capture needs a new controller; a page reload may not restart it).
        // Runs after those events and from the quiet watch (WatchPetsAsync).
        private void SchedulePetPixelCheck(string reason, bool confirming)
        {
            if (closed) return;
            if (pixelTimer == null)
            {
                pixelTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                pixelTimer.Tick += async (s, e) =>
                {
                    pixelTimer.Stop();
                    bool second = pixelConfirming;
                    pixelConfirming = false;
                    try { await CheckPetPixelsAsync((string)pixelTimer.Tag, second); } // async void: never let it crash the app
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { PetLog.Write("check-failed", ex.GetType().Name + ": " + ex.Message); }
                };
            }
            pixelTimer.Tag = reason;
            pixelConfirming = confirming;
            pixelTimer.Stop();
            pixelTimer.Start();
        }

        /// <summary>What the window shows where the pets stand: true drawn, false empty, null skipped / could not tell.</summary>
        internal async System.Threading.Tasks.Task<bool?> CheckPetPixelsAsync(string reason, bool confirming)
        {
            // A pet whose image failed (its error shows) is left out below; the other pets are still looked at.
            if (closed || !IsVisible || !PetsShown || placingPets) return null;
            if (recoveryTimer != null && recoveryTimer.IsEnabled) return null; // a reload / new view is already on its way
            // A page still loading (or loaded a moment ago) may not have drawn its pets yet: that is not a stalled view.
            if (pageReadyAt == DateTime.MinValue || DateTime.Now - pageReadyAt < TimeSpan.FromSeconds(2)) return null;
            var areas = PetAreasOutsideBoard();
            string key = PixelAreasKey(areas);
            bool? drawn = null;
            if (areas.Count > 0)
            {
                if (petPixelsOverride != null) drawn = petPixelsOverride();
                else
                {
                    IntPtr hwnd = new WindowInteropHelper(this).Handle;
                    var dpi = VisualTreeHelper.GetDpi(this);
                    Int32Rect Device(Rect r) => new Int32Rect(
                        (int)Math.Round(r.X * dpi.DpiScaleX), (int)Math.Round(r.Y * dpi.DpiScaleY),
                        (int)Math.Round(r.Width * dpi.DpiScaleX), (int)Math.Round(r.Height * dpi.DpiScaleY));
                    var device = areas.Select(Device).ToList();
                    // The calendar is WPF's own drawing: if even it reads empty, the capture shows nothing at all (monitors
                    // asleep, DWM not composing this window) and says nothing about the pets. The TODO window's pets have
                    // no calendar to compare with.
                    var reference = companion ? null : new System.Collections.Generic.List<Int32Rect> { Device(BoardRect) };
                    drawn = await System.Threading.Tasks.Task.Run(() => ReadPetPixels(hwnd, device, reference));
                    // Back on the window's thread whatever called this (the rest touches the view and the timers).
                    if (!Dispatcher.CheckAccess())
                    {
                        bool? seen = drawn;
                        return await Dispatcher.InvokeAsync(() => ConcludePixelCheck(reason, confirming, seen, key)).Task;
                    }
                }
            }
            return ConcludePixelCheck(reason, confirming, drawn, key);
        }

        private bool? ConcludePixelCheck(string reason, bool confirming, bool? drawn, string key)
        {
            if (closed) return drawn;
            bool distrusted = drawn == false && key != null && pixelDistrust.Contains(key);
            // "shown" comes with every reopen from the tray, the watch every 2 minutes: not worth a log line while the pets show
            // (the watch's second look is logged either way: it tells whether a repaint brought them back).
            if (reason != "shown" && !(reason == "watch" && !confirming && drawn != false))
                PetLog.Write("check", reason + (confirming ? " again" : "") + " " + (drawn == true ? "drawn" : drawn == false ? "blank" : "unknown") +
                    (distrusted ? " (ignored: a rebuilt view looked the same here)" : ""));
            if (drawn == true && key != null) pixelDistrust.Remove(key);
            if (drawn != false || distrusted) return drawn;
            if (!confirming) { RepaintPets(); SchedulePetPixelCheck(reason, true); return drawn; } // a first frame may just be late
            if (reason == "rebuilt")
            {
                // The view was rebuilt because it looked empty, and the fresh one looks empty too: the pixels cannot tell
                // here (a black pet, only a transparent margin outside the calendar). No more rebuilds for this layout.
                if (key != null) { if (pixelDistrust.Count >= 20) pixelDistrust.Clear(); pixelDistrust.Add(key); }
                return drawn;
            }
            pixelVerifyAfterRebuild = true; // look at the new view once its page is ready (AfterPageReady)
            RequestCharacterRecovery(true, "blank-" + reason);
            return drawn;
        }

        /// <summary>
        /// The pets' view (WebView2 composition) keeps a see-through browser window of its own over the pets. Windows could
        /// raise it above this window (e.g. when the desktop was activated), and from then on it took every click on the pets
        /// and much of the calendar — the pets stayed dead until the window was dragged. Click-through, it never can.
        /// </summary>
        private void EnsureViewClickThrough()
        {
            if (closed) return;
            var core = LiveCore;
            if (core == null) return;
            uint pid;
            try { pid = core.BrowserProcessId; }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException) { return; }
            int changed = NativeMethods.MakeBrowserWindowsClickThrough(pid, "character.html?mini");
            if (changed > 0) PetLog.Write("view", "browser window made click-through (" + changed + ")");
        }

        /// <summary>Called when a new page says "ready": after a rebuild for looking empty, check that the new view shows.</summary>
        internal void AfterPageReady()
        {
            if (!pixelVerifyAfterRebuild) return;
            pixelVerifyAfterRebuild = false;
            ScheduleHealthCheck("rebuilt"); // health check → repaint → pixel look, with the ready grace already passed
        }

        private static string PixelAreasKey(System.Collections.Generic.IEnumerable<Rect> areas) =>
            string.Join(";", areas.Select(r => (int)Math.Round(r.X) + "," + (int)Math.Round(r.Y) + "," + (int)Math.Round(r.Width) + "," + (int)Math.Round(r.Height)));

        private Rect BoardRect => new Rect(Edge + petPads.Left, Edge + petPads.Top, BoardWidth, BoardHeight);

        // The pets' rectangles (window coordinates, DIPs) minus the board: the calendar and the music bar draw there
        // themselves, so their pixels would make an empty pet look drawn (a pet may overlap the calendar's edge). A pet
        // with less than a quarter of itself outside the calendar is left out: what sticks out is then mostly its
        // transparent margin, which would read "empty" even while the pet shows. A pet the page could not draw (its image
        // failed) or the app could not read is left out too: its empty area says nothing about the view.
        private System.Collections.Generic.List<Rect> PetAreasOutsideBoard()
        {
            var board = BoardRect;
            var result = new System.Collections.Generic.List<Rect>();
            var rects = PetRects();
            for (int i = 0; i < rects.Count; i++)
            {
                var pet = rects[i];
                if (pet.Width < 8 || pet.Height < 8 || undrawnPets.Contains(i)) continue;
                var outside = Subtract(pet, board).Where(part => part.Width >= 4 && part.Height >= 4).ToList();
                if (outside.Sum(part => part.Width * part.Height) >= 0.25 * pet.Width * pet.Height) result.AddRange(outside);
            }
            return result;
        }

        /// <summary>A rectangle minus another: the parts of <paramref name="a"/> above, below, left and right of the overlap.</summary>
        internal static System.Collections.Generic.List<Rect> Subtract(Rect a, Rect b)
        {
            var parts = new System.Collections.Generic.List<Rect>();
            var overlap = Rect.Intersect(a, b);
            if (overlap.IsEmpty || overlap.Width <= 0 || overlap.Height <= 0) { parts.Add(a); return parts; }
            if (overlap.Top > a.Top) parts.Add(new Rect(a.Left, a.Top, a.Width, overlap.Top - a.Top));
            if (overlap.Bottom < a.Bottom) parts.Add(new Rect(a.Left, overlap.Bottom, a.Width, a.Bottom - overlap.Bottom));
            if (overlap.Left > a.Left) parts.Add(new Rect(a.Left, overlap.Top, overlap.Left - a.Left, overlap.Height));
            if (overlap.Right < a.Right) parts.Add(new Rect(overlap.Right, overlap.Top, a.Right - overlap.Right, overlap.Height));
            return parts;
        }

        internal static bool? PetPixelsDrawn(IntPtr hwnd, System.Collections.Generic.IList<Int32Rect> areas) => ReadPetPixels(hwnd, areas, null);

        /// <summary>
        /// What the window shows in these areas (device pixels, window coordinates): true when at least 0.5 % of the sampled
        /// pixels are not pure black, false when they all are, null when it could not look. PrintWindow with
        /// PW_RENDERFULLCONTENT reads the window's own content even while other windows cover it; a layered window's
        /// transparent parts come out exactly black (0,0,0 — alpha is always 255, so it cannot tell), which is why any
        /// non-zero channel counts: a dark pet (outlines of 5,5,5, a 4 % white glow) must not read as empty. Only pure
        /// black cannot be told from nothing. <paramref name="reference"/>: areas that must show something for the look
        /// to count (the calendar); when they read empty too the capture itself is blank and the answer is null.
        /// Also null while DWM cloaks the window (another virtual desktop). Thread-agnostic: called off the UI thread.
        /// </summary>
        internal static bool? ReadPetPixels(IntPtr hwnd, System.Collections.Generic.IList<Int32Rect> areas, System.Collections.Generic.IList<Int32Rect> reference)
        {
            if (hwnd == IntPtr.Zero || areas == null || areas.Count == 0) return null;
            if (NativeMethods.IsCloaked(hwnd)) return null;
            if (!NativeMethods.TryGetWindowSize(hwnd, out int width, out int height) || width <= 0 || height <= 0 || (long)width * height > 16L * 1024 * 1024)
                return null;
            try
            {
                using (var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    {
                        IntPtr dc = graphics.GetHdc();
                        bool printed;
                        try { printed = NativeMethods.PrintWindow(hwnd, dc, 2); }
                        finally { graphics.ReleaseHdc(dc); }
                        if (!printed) return null;
                    }
                    var bounds = new System.Drawing.Rectangle(0, 0, width, height);
                    var data = bitmap.LockBits(bounds, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        var row = new byte[Math.Abs(data.Stride)];
                        // Share of sampled pixels (every step-th row and column) with any colour; -1 when nothing was sampled.
                        double Lit(System.Collections.Generic.IList<Int32Rect> list, int step)
                        {
                            long seen = 0, lit = 0;
                            foreach (var area in list)
                            {
                                var part = System.Drawing.Rectangle.Intersect(new System.Drawing.Rectangle(area.X, area.Y, area.Width, area.Height), bounds);
                                for (int y = part.Top; y < part.Bottom; y += step)
                                {
                                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                                    for (int x = part.Left; x < part.Right; x += step)
                                    {
                                        int i = x * 4; // B, G, R, A
                                        seen++;
                                        if (row[i] != 0 || row[i + 1] != 0 || row[i + 2] != 0) lit++;
                                    }
                                }
                            }
                            return seen == 0 ? -1 : (double)lit / seen;
                        }
                        if (reference != null && reference.Count > 0)
                        {
                            double calendar = Lit(reference, 6);
                            if (calendar < 0.005) return null; // not even the calendar shows: the capture itself is empty
                        }
                        double pets = Lit(areas, 2);
                        if (pets < 0) return null;
                        return pets >= 0.005;
                    }
                    finally { bitmap.UnlockBits(data); }
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception ||
                                       ex is System.Runtime.InteropServices.ExternalException || ex is OutOfMemoryException)
            { return null; } // GDI+ reports a bitmap it cannot make as OutOfMemory: just "could not tell"
        }

        // Resend the layout and ask the page to redraw every pet; nudge the view's size so the capture restarts.
        private bool repaintQueued;

        // One RepaintPets after the layout in progress (several resizes in a row give one).
        private void RepaintPetsSoon()
        {
            if (repaintQueued || closed || !IsLoaded) return;
            repaintQueued = true;
            Dispatcher.BeginInvoke(new Action(() => { repaintQueued = false; if (!closed && IsVisible) RepaintPets(); }),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        internal void RepaintPets()
        {
            lastLayoutJson = null;
            SendPetLayout();
            PostToPage(JsonConvert.SerializeObject(new { action = "repaint" }));
            if (pagePostOverride != null || CharacterView.Width <= 1) return;
            double width = CharacterView.Width;
            CharacterView.Width = width + 1;
            Dispatcher.BeginInvoke(new Action(() => { if (!closed && Math.Abs(CharacterView.Width - (width + 1)) < 0.01) CharacterView.Width = width; }),
                System.Windows.Threading.DispatcherPriority.Render);
        }

        /// <summary>
        /// The view's CoreWebView2, or null while it is starting — and also once its browser process has died: from then on
        /// even reading CharacterView.CoreWebView2 throws ("The WebView control is no longer valid because the browser
        /// process crashed"), and layout / music / animation updates run all the time until the new view is built.
        /// </summary>
        private Microsoft.Web.WebView2.Core.CoreWebView2 LiveCore
        {
            get
            {
                try { return CharacterView.CoreWebView2; }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException || ex is System.Runtime.InteropServices.COMException)
                { return null; } // ProcessFailed has already asked for a new view
            }
        }

        private void PostToPage(string json)
        {
            if (pagePostOverride != null) { pagePostOverride(json); return; }
            var core = closed ? null : LiveCore;
            if (core == null) return;
            try { core.PostWebMessageAsJson(json); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException || ex is ObjectDisposedException) { }
        }

        // A pet's sharper sheet was made in the background (SpriteSharpener, on its own thread): the page switches that pet
        // over in place — no reload, its action and frame go on — and the stored load carries it for a reload of the page.
        private void OnSharpSheetReady(string imagePath, string address) =>
            Dispatcher.BeginInvoke(new Action(() => UseSharpSheet(imagePath, address)));

        internal void UseSharpSheet(string imagePath, string address)
        {
            if (closed || characterPayload == null) return;
            JObject payload;
            try { payload = JObject.Parse(characterPayload); } catch (JsonException) { return; }
            var list = payload["characters"] as JArray;
            bool any = false;
            for (int i = 0; list != null && i < slotCharacters.Count && i < list.Count; i++)
            {
                if (unreadablePets.Contains(i) || emptyPets.Contains(i) || !SameFile(slotCharacters[i].ImagePath, imagePath) || !(list[i] is JObject entry)) continue;
                entry["sharp"] = address;
                any = true;
                // A page still loading gets it with the load (the stored payload); a message now would reach the old page.
                if (pageReadyAt != DateTime.MinValue) PostToPage(JsonConvert.SerializeObject(new { action = "sheet", index = i, image = address }));
            }
            if (any) characterPayload = payload.ToString(Formatting.None);
        }

        private static bool SameFile(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { return false; }
        }

        private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume)
                Dispatcher.BeginInvoke(new Action(() => { PetLog.Write("resume"); ScheduleHealthCheck("resume"); ScreensMayHaveChanged("resume"); }));
        }

        private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock || e.Reason == Microsoft.Win32.SessionSwitchReason.ConsoleConnect ||
                e.Reason == Microsoft.Win32.SessionSwitchReason.RemoteConnect)
                Dispatcher.BeginInvoke(new Action(() => { ScheduleHealthCheck("session-" + e.Reason); ScreensMayHaveChanged("session-" + e.Reason); }));
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
                " 더블클릭해 설정에서 캐릭터를 다시 선택해 주세요.", pet);
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
                if (unreadablePets.Count > 0) ShowCharacterError("캐릭터 파일을 읽지 못했습니다. 더블클릭해 설정에서 다시 선택해 주세요.", unreadablePets.Min());
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
                ShowCharacterError("더블클릭해 설정에서 캐릭터를 다시 선택해 주세요.");
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
            foreach (var spots in new[] { data.MiniPetSpots, data.CompanionPetSpots })
                if (spots != null && spots.Count > index) spots.Insert(index, new MiniPetSpot { Key = NewPetKey });
        }

        private void RemoveSpotSlot(int index)
        {
            foreach (var spots in new[] { data.MiniPetSpots, data.CompanionPetSpots })
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
        {
            if (closed || !PetsShown) return;
            if (Enumerable.Range(0, SlotCount).All(PetHidden)) return; // nothing to drag
            CloseDayPopup();
            petSettings?.Close(); // the pets are dragged on the mini window itself
            placingPets = true;
            PlacementBanner.Visibility = Visibility.Visible;
            ApplyPetLayout(); // move cursor on the pets
        }

        public void EndPetPlacement()
        {
            if (!placingPets) return;
            placingPets = false;
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
            for (int i = rects.Count - 1; i >= 0; i--) if (rects[i].Width > 0 && rects[i].Contains(at)) { draggingPet = i; break; } // hidden: empty rect
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
        private System.Collections.Generic.List<Rect> PetBoardRects(double boardW, double boardH)
        {
            int count = SlotCount, gap = CharacterGapPx;
            // Each pet has its own size; a hidden pet takes no room (an empty rect that nothing lays out, draws or clicks).
            var sizes = Enumerable.Range(0, count).Select(i => PetHidden(i) ? new Size(0, 0) : PetSize(i)).ToList();
            var rects = new System.Collections.Generic.List<Rect>();
            var spots = Spots;
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
                rects.Add(new Rect(Math.Round(x), Math.Round(y), w, h));
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
        private void ApplyPetLayout(bool save = true)
        {
            if (closed || CharacterHost == null || MiniRoot == null || layingOutPets) return;
            layingOutPets = true;
            try
            {
                // A DPI change may have left the window's pixels out of step with Width / Height: put them back first. Sizing
                // it here otherwise made WPF read the wrong pixel size back into Width — and the calendar shrank by half.
                if (!resizingBoard) SyncWindowPixels();
                double boardW = BoardWidth, boardH = BoardHeight;
                double boardLeft = Left + Edge + petPads.Left, boardTop = Top + Edge + petPads.Top;
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
                if (padsChanged && !resizingBoard)
                {
                    // Grow/shrink around the board so it stays still (before the first placement only the size matters).
                    if (IsFinite(Left) && IsFinite(Top)) { Left = boardLeft - Edge - pads.Left; Top = boardTop - Edge - pads.Top; }
                    Width = boardW + 2 * Edge + pads.Left + pads.Right;
                    Height = boardH + 2 * Edge + pads.Top + pads.Bottom;
                    requestedSize = new Size(Width, Height);
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
                    hits[i].Cursor = placingPets ? Cursors.SizeAll : Cursors.Hand;
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
        void IPetSettingsHost.ChangePet(int index) { if (!IsPet(index)) return; activeSlot = index; OpenCharacterPicker(); }
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
            var before = PetBoardRects(BoardWidth, BoardHeight);
            SetSlotScale(index, percent);
            UpdateCharacterSize();
            KeepPetsApart(before);
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
            if (e.HeightChanged && !layingOutPets && !resizingBoard) UpdateCharacterSize();
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
        string SourceName { get; }         // what the dropdown shows
        void SelectExternal(string appId);
        // Current playlist in play order, the track playing now, and edits from the mini queue popup.
        System.Collections.Generic.IReadOnlyList<MusicTrack> Queue { get; }
        MusicTrack Current { get; }
        void Play(MusicTrack track);
        void Move(MusicTrack track, int index);
    }
}
