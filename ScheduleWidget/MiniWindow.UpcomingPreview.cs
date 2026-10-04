using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ScheduleWidget
{
    // ---- › 호버 미리보기: hovering the next-page arrow shows a small speech bubble summing up the schedules after the
    // page on show (from the day after its last day), grouped by date. It opens after a short delay (a mouse just passing
    // over the arrow shows nothing), stays while the mouse is on the arrow or on the bubble, and closes when it leaves both,
    // on a click (the page turns; the bubble comes back with the new page's summary if the mouse stays), during a page
    // flip / rush, when the window hides, moves or is deactivated, and on Esc. It is a Popup (never in the taskbar or
    // Alt+Tab, never takes the focus or the mouse: StaysOpen) declared next to the other bubbles, so the window's theme
    // brushes (Aux* / Mini*) reach it.
    public partial class MiniWindow
    {
        internal static int UpcomingOpenDelayMs = 250;   // hover this long before the bubble opens
        internal const int UpcomingCloseGraceMs = 160;   // time to move from the arrow onto the bubble (over the tail's gap)
        internal const int UpcomingMaxItems = 12;
        internal const int UpcomingMaxDays = 28;         // about four weeks after the page on show
        internal const int UpcomingExpandedDays = int.MaxValue;  // "+N개 더" clicked: every later one
        private const double UpcomingExpandedMaxHeight = 420; // the expanded list scrolls past this
        private bool upcomingExpanded;
        private ScrollViewer upcomingScroll;

        private DispatcherTimer upcomingOpenTimer, upcomingCloseTimer;
        private Grid upcomingBubble;
        private Border upcomingFace;
        private StackPanel upcomingList;
        private Path upcomingTailUp, upcomingTailDown;
        private Point upcomingOffset;                    // the bubble's place relative to the arrow, in device pixels
        private bool upcomingHooked;
        private bool upcomingKeyboard;                   // › got the focus by keyboard (Tab): the focus keeps the bubble too

        internal Popup UpcomingPreviewPopup => UpcomingPopup;
        internal bool UpcomingPreviewOpen => UpcomingPopup != null && UpcomingPopup.IsOpen;
        internal bool UpcomingPreviewPending => upcomingOpenTimer != null && upcomingOpenTimer.IsEnabled;
        internal bool UpcomingTailBelow => upcomingTailDown != null && upcomingTailDown.Visibility == Visibility.Visible; // shown above the arrow
        internal UpcomingSummary UpcomingPreviewSummary { get; private set; }

        public sealed class UpcomingLine
        {
            public ScheduleItem Item { get; set; }
            public string Time { get; set; }      // "" when none
            public string Title { get; set; }
            public string Range { get; set; }     // "10.8~10.10" for a schedule over several days, else null
            public string Color { get; set; }
        }

        public sealed class UpcomingDay
        {
            public DateTime Date { get; set; }
            public string Header { get; set; }    // "10.12 (월)"
            public string Holiday { get; set; }
            public string DDay { get; set; }      // D-day / D-3 / D+2, as on the calendar's blocks
            public bool IsHoliday { get; set; }   // Sunday or a public holiday: the calendar's holiday ink
            public List<UpcomingLine> Lines { get; set; } = new List<UpcomingLine>();
        }

        public sealed class UpcomingSummary
        {
            public DateTime From { get; set; }
            public List<UpcomingDay> Days { get; set; } = new List<UpcomingDay>();
            public int More { get; set; }         // future schedules not listed ("+N개 더")
            public bool IsEmpty => Days.Count == 0 && More == 0;
            public int Count => Days.Sum(d => d.Lines.Count);
        }

        private static readonly CultureInfo UpcomingKorean = CultureInfo.GetCultureInfo("ko-KR");

        // Multi-day schedules: read an end date when the model has one (EndDate / EndPeriod, a DateTime? or a "yyyy-MM-dd"
        // string), so this compiles and works whether or not that property exists.
        private static readonly System.Reflection.PropertyInfo UpcomingEndProperty =
            typeof(ScheduleItem).GetProperty("EndDate") ?? typeof(ScheduleItem).GetProperty("EndPeriod");

        private static DateTime? UpcomingParse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exact)) return exact;
            return DateTime.TryParse(text, out DateTime loose) ? loose.Date : (DateTime?)null;
        }

        internal static DateTime? UpcomingEndOf(ScheduleItem item)
        {
            if (item == null || UpcomingEndProperty == null) return null;
            object value;
            try { value = UpcomingEndProperty.GetValue(item); } catch (Exception) { return null; }
            if (value is DateTime date) return date.Date;
            return UpcomingParse(value as string);
        }

        private static string UpcomingDDay(DateTime date, DateTime today)
        {
            int diff = (date - today).Days;
            return diff == 0 ? "D-day" : diff > 0 ? "D-" + diff : "D+" + -diff;
        }

        /// <summary>
        /// The schedules from <paramref name="from"/> on, not done, grouped by date (by time within a day, untimed last as on
        /// the calendar): at most <see cref="UpcomingMaxItems"/> within <see cref="UpcomingMaxDays"/> days — or, when none
        /// fall in those days, the first later date's — and how many more future ones there are.
        /// </summary>
        internal static UpcomingSummary BuildUpcomingSummary(IEnumerable<ScheduleItem> schedules, DateTime from, DateTime today,
            int maxItems = UpcomingMaxItems, int maxDays = UpcomingMaxDays)
        {
            from = from.Date;
            var summary = new UpcomingSummary { From = from };
            var all = (schedules ?? Enumerable.Empty<ScheduleItem>()).Where(s => s != null)
                .Select(s => new { Item = s, Start = s.StartDate ?? UpcomingParse(s.Period) })
                .Where(x => x.Start.HasValue).ToList();
            var future = all.Where(x => !x.Item.IsCompleted && x.Start.Value >= from)
                .OrderBy(x => x.Start.Value).ThenBy(x => x.Item.Time ?? "99:99", StringComparer.Ordinal).ToList();
            if (future.Count == 0) return summary;
            int days = Math.Max(1, maxDays);
            int daysUntilMaxDate = (DateTime.MaxValue.Date - from).Days;
            bool reachesMaxDate = days > daysUntilMaxDate;
            DateTime until = reachesMaxDate ? DateTime.MaxValue.Date : from.AddDays(days);
            var listed = future.Where(x => reachesMaxDate || x.Start.Value < until).Take(maxItems).ToList();
            if (listed.Count == 0) listed = future.Where(x => x.Start.Value == future[0].Start.Value).Take(maxItems).ToList();
            summary.More = future.Count - listed.Count;
            // Block colors as the calendar gives them: per day, the first blocks shown first (see Refresh / BlockColors).
            var listedDates = new HashSet<DateTime>(listed.Select(x => x.Start.Value));
            var byDay = new Dictionary<DateTime, List<ScheduleItem>>();
            foreach (var x in all)
            {
                DateTime date = x.Start.Value;
                if (!listedDates.Contains(date)) continue;
                if (!byDay.TryGetValue(date, out List<ScheduleItem> dayItems)) byDay[date] = dayItems = new List<ScheduleItem>();
                dayItems.Add(x.Item);
            }
            foreach (var group in listed.GroupBy(x => x.Start.Value))
            {
                DateTime date = group.Key;
                var dayTasks = byDay[date].OrderBy(s => s.IsCompleted).ThenBy(s => s.Time ?? "99:99", StringComparer.Ordinal).ToList();
                var shownColors = BlockColors(dayTasks.Take(3).ToList());
                var allColors = BlockColors(dayTasks);
                string holiday = KoreanHolidays.NameOf(date);
                var day = new UpcomingDay
                {
                    Date = date, Header = date.ToString("M.d (ddd)", UpcomingKorean), Holiday = holiday,
                    DDay = UpcomingDDay(date, today.Date), IsHoliday = holiday != null || date.DayOfWeek == DayOfWeek.Sunday
                };
                foreach (var x in group)
                {
                    var s = x.Item;
                    string time = FeatureRules.TryScheduleTime(s.Time, out string normalized) ? normalized : (s.Time ?? "").Trim();
                    DateTime? end = UpcomingEndOf(s);
                    day.Lines.Add(new UpcomingLine
                    {
                        Item = s, Time = time, Title = string.IsNullOrWhiteSpace(s.Title) ? "(제목 없음)" : s.Title.Trim(),
                        Range = end.HasValue && end.Value > date ? date.ToString("M.d", CultureInfo.InvariantCulture) + "~" + end.Value.ToString("M.d", CultureInfo.InvariantCulture) : null,
                        Color = shownColors.TryGetValue(s, out string color) ? color : allColors.TryGetValue(s, out color) ? color : BlockPalette[0]
                    });
                }
                summary.Days.Add(day);
            }
            return summary;
        }

        // ---- Hooking up (called once from the constructor) ----
        private void InitUpcomingPreview()
        {
            if (upcomingHooked || companion || NextWeek == null || UpcomingPopup == null) return;
            upcomingHooked = true;
            UpcomingPopup.CustomPopupPlacementCallback = PlaceUpcomingPopup;
            upcomingOpenTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(UpcomingOpenDelayMs) };
            upcomingOpenTimer.Tick += (s, e) => { upcomingOpenTimer.Stop(); TryOpenUpcomingPreview(); };
            upcomingCloseTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(UpcomingCloseGraceMs) };
            upcomingCloseTimer.Tick += (s, e) => { upcomingCloseTimer.Stop(); if (!UpcomingHovered) CloseUpcomingPreview(); };

            NextWeek.MouseEnter += (s, e) => { upcomingCloseTimer.Stop(); ArmUpcomingPreview(); };
            NextWeek.MouseLeave += (s, e) => LeaveUpcomingPreview();
            // Tab onto ›: the summary shows too; leaving the arrow by keyboard closes it.
            NextWeek.GotKeyboardFocus += (s, e) => { if (InputManager.Current.MostRecentInputDevice is KeyboardDevice) { upcomingKeyboard = true; ArmUpcomingPreview(); } };
            NextWeek.LostKeyboardFocus += (s, e) => { upcomingKeyboard = false; if (!UpcomingHovered) CloseUpcomingPreview(); };
            // A press turns the page: the bubble goes, and comes back with the next page's summary while the mouse stays
            // (after the flip has landed: see TryOpenUpcomingPreview).
            NextWeek.PreviewMouseLeftButtonDown += (s, e) => { CloseUpcomingPreview(); upcomingOpenTimer.Stop(); };
            NextWeek.Click += (s, e) => { CloseUpcomingPreview(); if (UpcomingHovered) ArmUpcomingPreview(); };
            // Any other press in the window, the window hidden / moved / switched away from, closed: the bubble goes.
            // (A press inside the bubble — "+N개 더" — reaches this window too: the popup is in its tree. It stays.)
            PreviewMouseDown += (s, e) =>
            {
                upcomingKeyboard = false;
                if (UpcomingPreviewOpen && !NextWeek.IsMouseOver && !IsWithinUpcomingBubble(e.OriginalSource as DependencyObject)) CloseUpcomingPreview();
            };
            IsVisibleChanged += (s, e) => { if (!(bool)e.NewValue) CloseUpcomingPreview(); };
            LocationChanged += (s, e) => CloseUpcomingPreview();
            Deactivated += (s, e) => { if (!UpcomingHovered) CloseUpcomingPreview(); };
            Closed += (s, e) => { upcomingOpenTimer.Stop(); upcomingCloseTimer.Stop(); if (UpcomingPopup != null) UpcomingPopup.IsOpen = false; };
            // The page changed some other way while it shows (a range pick, 오늘로 이동, midnight): the summary follows.
            var text = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            EventHandler pageChanged = (s, e) => { if (UpcomingPreviewOpen) { CloseUpcomingPreview(); if (UpcomingHovered) ArmUpcomingPreview(); } };
            text.AddValueChanged(WeekLabel, pageChanged);
            Closed += (s, e) => text.RemoveValueChanged(WeekLabel, pageChanged); // the descriptor holds its handlers strongly
        }

        private bool UpcomingHovered =>
            NextWeek != null && ((upcomingArrowHoverOverride ?? NextWeek.IsMouseOver) || upcomingKeyboard && NextWeek.IsKeyboardFocused) ||
            UpcomingPreviewOpen && upcomingBubble != null && (upcomingBubbleHoverOverride ?? (upcomingBubble.IsMouseOver || CursorOverUpcomingBubble()));

        // Checks only: stand in for the mouse being on the arrow / on the bubble (the checks never move the user's mouse).
        internal bool? upcomingArrowHoverOverride = null, upcomingBubbleHoverOverride = null;

        private void ArmUpcomingPreview()
        {
            if (upcomingOpenTimer == null || closed) return;
            upcomingOpenTimer.Interval = TimeSpan.FromMilliseconds(UpcomingOpenDelayMs);
            upcomingOpenTimer.Stop();
            upcomingOpenTimer.Start();
        }

        private void LeaveUpcomingPreview()
        {
            if (upcomingOpenTimer == null) return;
            if (!UpcomingPreviewOpen) { if (!UpcomingHovered) upcomingOpenTimer.Stop(); return; }
            upcomingCloseTimer.Stop();
            upcomingCloseTimer.Start();
        }

        /// <summary>Closes the preview (and drops a pending open). True when it was showing.</summary>
        internal bool CloseUpcomingPreview()
        {
            upcomingOpenTimer?.Stop();
            upcomingCloseTimer?.Stop();
            upcomingExpanded = false; // the next opening shows the short list again
            if (UpcomingPopup == null || !UpcomingPopup.IsOpen) return false;
            UpcomingPopup.IsOpen = false;
            return true;
        }

        private void TryOpenUpcomingPreview()
        {
            if (closed || companion || !IsVisible || !NextWeek.IsVisible || !NextWeek.IsEnabled) return;
            if (!UpcomingHovered) return;
            // Mid-flip, mid-rush, the arrow held down or the band being dragged: try again once that is over.
            if (WeekFlipRunning || Rushing || NextWeek.IsPressed || Mouse.Captured != null && Mouse.Captured != NextWeek || RecentlyDragged)
            { ArmUpcomingPreview(); return; }
            ShowUpcomingPreview();
        }

        /// <summary>Fills the bubble with the summary for the page on show and opens it at the arrow.</summary>
        internal void ShowUpcomingPreview()
        {
            if (UpcomingPopup == null || NextWeek == null) return;
            EnsureUpcomingBubble();
            UpcomingPreviewSummary = upcomingExpanded
                ? BuildUpcomingSummary(data.Schedules, weekStart.AddDays(DayCount), DateTime.Today, int.MaxValue, UpcomingExpandedDays)
                : BuildUpcomingSummary(data.Schedules, weekStart.AddDays(DayCount), DateTime.Today);
            FillUpcomingBubble(UpcomingPreviewSummary);
            // Expanded, the list scrolls inside the bubble instead of growing past the screen.
            upcomingScroll.MaxHeight = upcomingExpanded ? UpcomingExpandedMaxHeight : double.PositiveInfinity;
            upcomingScroll.VerticalScrollBarVisibility = upcomingExpanded ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            UpcomingPopup.IsOpen = false;
            UpcomingPopup.PlacementTarget = NextWeek;
            PlanUpcomingPlacement();
            RemeasurePopup(UpcomingPopup);
            UpcomingPopup.IsOpen = true;
        }

        // ---- The bubble ----
        private void EnsureUpcomingBubble()
        {
            if (upcomingBubble != null) return;
            upcomingBubble = new Grid { Width = 252, Focusable = false };
            AutomationProperties.SetName(upcomingBubble, "다음 일정 미리보기");
            upcomingFace = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 9, 0, 9) };
            upcomingFace.SetResourceReference(Border.BackgroundProperty, "AuxSurfaceBrush");
            upcomingFace.SetResourceReference(Border.BorderBrushProperty, "AuxHairlineBrush");
            upcomingList = new StackPanel();
            upcomingScroll = new ScrollViewer
            {
                Content = upcomingList, Focusable = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            upcomingFace.Child = upcomingScroll;
            Path Tail(string data, VerticalAlignment edge)
            {
                var tail = new Path
                {
                    Data = Geometry.Parse(data), Width = 18, Height = 10, StrokeThickness = 1, IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = edge
                };
                tail.SetResourceReference(Shape.FillProperty, "AuxSurfaceBrush");
                tail.SetResourceReference(Shape.StrokeProperty, "AuxHairlineBrush");
                return tail;
            }
            upcomingTailUp = Tail("M0,10 L9,1 L18,10", VerticalAlignment.Top);      // below the arrow, pointing up at it
            upcomingTailDown = Tail("M0,0 L9,9 L18,0", VerticalAlignment.Bottom);   // above it, pointing down
            upcomingBubble.Children.Add(upcomingFace);
            upcomingBubble.Children.Add(upcomingTailUp);
            upcomingBubble.Children.Add(upcomingTailDown);
            upcomingBubble.MouseEnter += (s, e) => upcomingCloseTimer?.Stop();
            upcomingBubble.MouseLeave += (s, e) => { if (UpcomingPreviewOpen) { upcomingCloseTimer.Stop(); upcomingCloseTimer.Start(); } };
            UpcomingPopup.Child = upcomingBubble;
        }

        private static TextBlock UpcomingText(string text, double size, string brush, FontWeight weight)
        {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis };
            block.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return block;
        }

        private void FillUpcomingBubble(UpcomingSummary summary)
        {
            upcomingList.Children.Clear();
            var title = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 2) };
            var since = UpcomingText(summary.From.ToString("M.d", CultureInfo.InvariantCulture) + "부터", 10.5, "AuxMutedBrush", FontWeights.Normal);
            DockPanel.SetDock(since, Dock.Right);
            since.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(since);
            title.Children.Add(UpcomingText("다음 일정", 12.5, "AuxInkBrush", FontWeights.SemiBold));
            upcomingList.Children.Add(title);
            if (summary.IsEmpty)
            {
                var empty = UpcomingText("앞으로 예정된 일정이 없습니다", 11.5, "AuxMutedBrush", FontWeights.Normal);
                empty.Margin = new Thickness(0, 8, 0, 2);
                upcomingList.Children.Add(empty);
                return;
            }
            foreach (var day in summary.Days)
            {
                var head = new DockPanel { Margin = new Thickness(0, 7, 0, 2) };
                var dday = UpcomingText(day.DDay, 10, "AuxMutedBrush", FontWeights.Bold);
                dday.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(dday, Dock.Right);
                head.Children.Add(dday);
                var date = UpcomingText(day.Header, 11.5, day.IsHoliday ? "MiniHolidayBrush" : "AuxInkBrush", FontWeights.SemiBold);
                if (day.Holiday != null)
                {
                    var name = new System.Windows.Documents.Run("  " + day.Holiday) { FontWeight = FontWeights.Normal, FontSize = 10.5 };
                    date.Inlines.Add(name);
                }
                head.Children.Add(date);
                AutomationProperties.SetName(head, day.Header + (day.Holiday != null ? " " + day.Holiday : "") + ", " + day.DDay);
                upcomingList.Children.Add(head);
                foreach (var line in day.Lines)
                {
                    var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(1, 6, 7, 0), VerticalAlignment = VerticalAlignment.Top };
                    try { dot.Fill = (Brush)new BrushConverter().ConvertFromString(line.Color); } catch (FormatException) { dot.SetResourceReference(Shape.FillProperty, "AuxMutedBrush"); }
                    row.Children.Add(dot);
                    if (line.Time.Length > 0)
                    {
                        var time = UpcomingText(line.Time, 10.5, "AuxMutedBrush", FontWeights.Normal);
                        time.Margin = new Thickness(0, 1, 6, 0);
                        time.VerticalAlignment = VerticalAlignment.Top;
                        Grid.SetColumn(time, 1);
                        row.Children.Add(time);
                    }
                    var text = new StackPanel();
                    text.Children.Add(UpcomingText(line.Title, 12, "AuxInkBrush", FontWeights.Normal));
                    if (line.Range != null) text.Children.Add(UpcomingText(line.Range, 10, "AuxMutedBrush", FontWeights.Normal));
                    Grid.SetColumn(text, 2);
                    row.Children.Add(text);
                    AutomationProperties.SetName(row, (line.Time.Length > 0 ? line.Time + " " : "") + line.Title + (line.Range != null ? ", " + line.Range : ""));
                    upcomingList.Children.Add(row);
                }
            }
            if (summary.More > 0)
            {
                // "+N개 더": a click shows them all (the list then scrolls inside the bubble).
                var more = UpcomingText("+" + summary.More + "개 더", 11, "AuxMutedBrush", FontWeights.SemiBold);
                more.Margin = new Thickness(0, 7, 0, 0);
                more.Cursor = Cursors.Hand;
                more.HorizontalAlignment = HorizontalAlignment.Left;
                more.ToolTip = "남은 일정 모두 보기";
                AutomationProperties.SetName(more, "남은 일정 " + summary.More + "개 더 보기");
                more.MouseEnter += (s, e) => more.TextDecorations = TextDecorations.Underline;
                more.MouseLeave += (s, e) => more.TextDecorations = null;
                more.MouseLeftButtonUp += (s, e) => { e.Handled = true; ExpandUpcomingPreview(); };
                upcomingList.Children.Add(more);
            }
        }

        /// <summary>"+N개 더" clicked: the bubble lists every future schedule (scrolling past a height) until it closes.</summary>
        internal void ExpandUpcomingPreview()
        {
            if (!UpcomingPreviewOpen || upcomingExpanded) return;
            upcomingExpanded = true;
            upcomingCloseTimer?.Stop();
            // In place: closing and reopening the popup (as ShowUpcomingPreview does) read as the mouse leaving the bubble,
            // and the preview went away right after the click.
            UpcomingPreviewSummary = BuildUpcomingSummary(data.Schedules, weekStart.AddDays(DayCount), DateTime.Today, int.MaxValue, UpcomingExpandedDays);
            FillUpcomingBubble(UpcomingPreviewSummary);
            upcomingScroll.MaxHeight = UpcomingExpandedMaxHeight;
            upcomingScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            PlanUpcomingPlacement();
            // A changed offset makes the open popup place itself again (now taller: maybe above the arrow instead).
            UpcomingPopup.HorizontalOffset = UpcomingPopup.HorizontalOffset == 0 ? 0.01 : 0;
        }

        private bool IsWithinUpcomingBubble(DependencyObject node)
        {
            for (; node != null; node = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if (node == upcomingBubble) return true;
            return false;
        }

        // Over the bubble by where the pointer really is: WPF's IsMouseOver lags when the bubble's content changes under
        // a still mouse (the click on "+N개 더" replaced the list) and read as having left it.
        private bool CursorOverUpcomingBubble()
        {
            if (upcomingBubble == null || !UpcomingPreviewOpen || PresentationSource.FromVisual(upcomingBubble) == null) return false;
            Point at = Mouse.GetPosition(upcomingBubble);
            return at.X >= 0 && at.Y >= 0 && at.X <= upcomingBubble.ActualWidth && at.Y <= upcomingBubble.ActualHeight;
        }

        // ---- Placement: below the arrow (above when the screen has no room below), kept on the arrow's screen, the tail
        // pointing at the arrow's middle. Worked out in device pixels before opening (the bubble's size is measured first),
        // so the tail can be put in place; the popup's own callback then only hands over the spot.
        private void PlanUpcomingPlacement()
        {
            upcomingOffset = new Point(0, NextWeek.ActualHeight);
            var source = PresentationSource.FromVisual(NextWeek);
            if (source == null || NextWeek.ActualWidth <= 0) return;
            upcomingBubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size size = upcomingBubble.DesiredSize;
            Point topLeft = NextWeek.PointToScreen(new Point(0, 0));
            Point bottomRight = NextWeek.PointToScreen(new Point(NextWeek.ActualWidth, NextWeek.ActualHeight));
            double scale = Math.Max(0.25, (bottomRight.X - topLeft.X) / NextWeek.ActualWidth);
            double width = size.Width * scale, height = size.Height * scale, center = (topLeft.X + bottomRight.X) / 2;
            Rect screen = upcomingWorkAreaOverride ?? UpcomingWorkArea(new Point(center, (topLeft.Y + bottomRight.Y) / 2));
            const double edge = 4;
            double top = bottomRight.Y - 2 * scale;      // the tail's tip just under the arrow
            bool above = top + height > screen.Bottom - edge && topLeft.Y - height + 2 * scale >= screen.Top + edge;
            if (above) top = topLeft.Y - height + 2 * scale;
            else top = Math.Max(screen.Top + edge, Math.Min(top, screen.Bottom - edge - height));
            double left = center - width + 22 * scale;   // the arrow sits at the band's right end: the bubble opens to the left
            left = Math.Max(screen.Left + edge, Math.Min(left, screen.Right - edge - width));
            double tailX = (center - left) / scale - 9;
            tailX = Math.Max(10, Math.Min(size.Width - 28, tailX));
            upcomingTailUp.Margin = upcomingTailDown.Margin = new Thickness(tailX, 0, 0, 0);
            upcomingTailUp.Visibility = above ? Visibility.Collapsed : Visibility.Visible;
            upcomingTailDown.Visibility = above ? Visibility.Visible : Visibility.Collapsed;
            upcomingOffset = new Point((left - topLeft.X) / scale, (top - topLeft.Y) / scale); // in the arrow's DIPs
        }

        internal Rect? upcomingWorkAreaOverride = null;         // checks / screenshots only: the screen's work area, in device pixels

        private static Rect UpcomingWorkArea(Point devicePoint)
        {
            var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)devicePoint.X, (int)devicePoint.Y)).WorkingArea;
            return new Rect(area.Left, area.Top, area.Width, area.Height);
        }

        // The callback's units are the target's size units (device pixels or DIPs, whichever WPF hands over): scale the spot alike.
        private CustomPopupPlacement[] PlaceUpcomingPopup(Size popupSize, Size targetSize, Point offset)
        {
            double k = NextWeek.ActualWidth > 0 && targetSize.Width > 0 ? targetSize.Width / NextWeek.ActualWidth : 1;
            return new[] { new CustomPopupPlacement(new Point(upcomingOffset.X * k, upcomingOffset.Y * k), PopupPrimaryAxis.None) };
        }
    }
}
