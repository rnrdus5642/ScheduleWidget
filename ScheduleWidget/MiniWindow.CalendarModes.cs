using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        private const double MonthDefaultHeight = 780, MonthMinimumHeight = 600;
        private bool changingCalendarMode, monthWasOpened;
        private double? calendarVerticalAnchor;
        private Func<Rect> calendarWorkAreaOverride = null; // isolated layout checks

        private void InitializeCalendarMode()
        {
            if (companion) return;
            monthWasOpened = data.MiniMonthView;
            ApplyCalendarModeVisibility(data.MiniMonthView);
            if (!data.MiniMonthView) return;
            var compact = SavedCompactBoard(data) ?? new MonitorStateData { Left = 80, Top = 80, Width = 792, Height = 312 };
            if (!ValidCalendarBoard(compact)) compact = new MonitorStateData { Left = 80, Top = 80, Width = 792, Height = 312 };
            if (!ValidCalendarBoard(data.MiniBoard)) data.MiniBoard = compact;
            var monthly = ValidCalendarBoard(data.MiniMonthBoard) ? data.MiniMonthBoard : NewMonthBoard(compact);
            // Older builds expanded horizontally. The compact board is the shared horizontal geometry on upgrade.
            var restored = new MonitorStateData { Left = compact.Left, Width = compact.Width, Top = monthly.Top, Height = monthly.Height };
            data.MiniMonthBoard = FitCalendarBoard(restored, CalendarWorkArea(restored), true);
        }

        private void ApplyCalendarModeVisibility(bool month)
        {
            Title = month ? "캐릭터와 월간 달력" : "캐릭터와 기본 달력";
            MonthCalendarPanel.Visibility = month ? Visibility.Visible : Visibility.Collapsed;
            WeekDays.Visibility = month ? Visibility.Hidden : Visibility.Visible;
            WeekRanges.Visibility = WeekDays.Visibility;
            RangeLabelButton.Visibility = month ? Visibility.Collapsed : Visibility.Visible;
            MonthLabel.Visibility = month ? Visibility.Visible : Visibility.Collapsed;
            Resources["CalendarHeaderHeight"] = new GridLength(month ? 38 : 30);
            if (month) MonthViewButton.SetResourceReference(BackgroundProperty, "MiniHoverBrush");
            else MonthViewButton.ClearValue(BackgroundProperty);
            string label = month ? "기본 모드로 전환" : "월간 모드로 전환";
            MonthViewButton.ToolTip = label;
            System.Windows.Automation.AutomationProperties.SetName(MonthViewButton, label);
        }

        private static bool ValidCalendarBoard(MonitorStateData board) => board != null &&
            IsFinite(board.Left) && IsFinite(board.Top) && IsFinite(board.Width) && IsFinite(board.Height) && board.Width > 0 && board.Height > 0;

        private MonitorStateData CaptureCalendarBoard()
        {
            var area = CalendarWorkArea();
            return new MonitorStateData
            {
                Left = IsFinite(Left) ? Left + Edge + petPads.Left : area.Left + 24,
                Top = IsFinite(Top) ? Top + Edge + petPads.Top : area.Top + 24,
                Width = BoardWidth, Height = BoardHeight
            };
        }

        private static MonitorStateData NewMonthBoard(MonitorStateData compact) => new MonitorStateData
        {
            Width = compact.Width, Height = Math.Max(MonthDefaultHeight, compact.Height),
            Left = compact.Left, Top = compact.Top
        };

        // Dimensions and position are in WPF DIPs, including negative coordinates on monitors left of the primary.
        private Rect CalendarWorkArea(MonitorStateData preferred = null)
        {
            if (calendarWorkAreaOverride != null) return calendarWorkAreaOverride();
            var dpi = VisualTreeHelper.GetDpi(this);
            if (ValidCalendarBoard(preferred))
            {
                var center = new System.Drawing.Point((int)Math.Max(-30000, Math.Min(30000, (preferred.Left + preferred.Width / 2) * dpi.DpiScaleX)),
                    (int)Math.Max(-30000, Math.Min(30000, (preferred.Top + preferred.Height / 2) * dpi.DpiScaleY)));
                if (NativeMethods.TryGetWorkAreaAt(center, out NativeMethods.Rect target, out double scale))
                {
                    if (scale <= 0) scale = dpi.DpiScaleX;
                    return new Rect(target.Left / scale, target.Top / scale, (target.Right - target.Left) / scale, (target.Bottom - target.Top) / scale);
                }
            }
            if (NativeMethods.TryGetWorkArea(new WindowInteropHelper(this).Handle, out NativeMethods.Rect area))
                return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY, (area.Right - area.Left) / dpi.DpiScaleX, (area.Bottom - area.Top) / dpi.DpiScaleY);
            return SystemParameters.WorkArea;
        }

        internal static MonitorStateData FitCalendarBoard(MonitorStateData desired, Rect area, bool month)
        {
            double width = Clamp(desired.Width, Math.Min(MinCalendarOnlyWidth, area.Width), area.Width);
            double height = Clamp(desired.Height, Math.Min(month ? MonthMinimumHeight : MinBoardHeight, area.Height), area.Height);
            return new MonitorStateData
            {
                Width = width, Height = height,
                Left = Math.Max(area.Left, Math.Min(desired.Left, area.Right - width)),
                Top = Math.Max(area.Top, Math.Min(desired.Top, area.Bottom - height))
            };
        }

        internal static MonitorStateData AnchorCalendarBoard(MonitorStateData current, MonitorStateData preferred,
            Rect area, bool month, double verticalAnchor)
        {
            // Mode switches change height only; saved widths must never move either horizontal edge.
            double currentWidth = Math.Min(current.Width, area.Width), currentHeight = Math.Min(current.Height, area.Height);
            double currentLeft = Clamp(current.Left, area.Left, area.Right - currentWidth);
            double currentTop = Clamp(current.Top, area.Top, area.Bottom - currentHeight);
            double height = Math.Min(area.Height, Math.Max(month ? Math.Max(currentHeight, MonthMinimumHeight) : MinBoardHeight, preferred.Height));
            if (month && height > currentHeight)
            {
                double extra = height - currentHeight;
                double above = currentTop - area.Top, below = area.Bottom - currentTop - currentHeight;
                // When one side is cramped and the other can fit the full growth, keep the existing edge fixed.
                // This also preserves a small gap above the taskbar instead of consuming it during expansion.
                if (above < extra / 2 && below >= extra) verticalAnchor = 0;
                else if (below < extra / 2 && above >= extra) verticalAnchor = 1;
            }
            return new MonitorStateData
            {
                Width = currentWidth, Height = height, Left = currentLeft,
                // If neither side alone can fit, distribute the growth within the work area.
                Top = Clamp(currentTop + (currentHeight - height) * Clamp(verticalAnchor, 0, 1), area.Top, area.Bottom - height)
            };
        }

        private double CollapseCalendarAnchor(MonitorStateData expanded, MonitorStateData compact, Rect area)
        {
            if (calendarVerticalAnchor.HasValue) return calendarVerticalAnchor.Value;
            // Recover the growth anchor after restarting in monthly mode, using only a compact rectangle contained
            // in this calendar. Old versions stored unrelated positions; those must not pull the calendar away.
            double extra = expanded.Height - (compact?.Height ?? expanded.Height);
            if (ValidCalendarBoard(compact) && extra > 0 &&
                Math.Abs(compact.Left + compact.Width / 2 - expanded.Left - expanded.Width / 2) <= 2 &&
                compact.Top >= expanded.Top - 2 && compact.Top + compact.Height <= expanded.Top + expanded.Height + 2)
                return Clamp((compact.Top - expanded.Top) / extra, 0, 1);
            if (Math.Abs(expanded.Top - area.Top) <= 2) return 0;
            if (Math.Abs(expanded.Top + expanded.Height - area.Bottom) <= 2) return 1;
            return .5;
        }

        private void ResizeCalendarBoard(MonitorStateData board, bool updateLayout = true)
        {
            // Calculate board + pets together. Clearing padding first briefly shrank the native window and its WebView.
            WindowStartupLocation = WindowStartupLocation.Manual;
            double basisHeight = ValidCalendarBoard(data.MiniBoard) && (IsMonthViewOpen || changingCalendarMode)
                ? data.MiniBoard.Height : board.Height;
            characterBaseHeight = Math.Max(80, basisHeight - CalendarTopMargin -
                (PlayerBar.Visibility == Visibility.Visible ? RestingBarRoom : CalendarBottomGapNoBar)) * 0.5;
            var first = PetSize(0);
            characterCellHeight = first.Height; characterCellWidth = first.Width;
            ApplyPetLayout(false, board);
            if (updateLayout) UpdateLayout();
        }

        private void SetCalendarWindowBounds(Rect bounds)
        {
            requestedSize = bounds.Size;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                // A layered window must move and resize together: four WPF property assignments expose its old
                // small surface at the new position while the remaining native messages are still being handled.
                if (NativeMethods.SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(bounds.Left * dpi.DpiScaleX),
                    (int)Math.Round(bounds.Top * dpi.DpiScaleY), (int)Math.Round(bounds.Width * dpi.DpiScaleX),
                    (int)Math.Round(bounds.Height * dpi.DpiScaleY), NativeMethods.SWP_NOACTIVATE | SwpNoZOrder | SwpNoOwnerZOrder)) return;
            }
            Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
        }
    }
}
