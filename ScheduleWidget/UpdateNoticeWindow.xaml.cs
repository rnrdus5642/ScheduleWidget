using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScheduleWidget
{
    /// <summary>
    /// 업데이트 알림: a small speech bubble over the first pet (or the calendar's top band) saying a newer version was found.
    /// A window of its own owned by the window it points at (so it stays above it, closes with it and never floats over
    /// other apps), never activated and not in Alt+Tab: it takes no focus and catches no clicks outside itself.
    /// MainWindow (MainWindow.UpdateNotice.cs) decides when it shows and where.
    /// </summary>
    public partial class UpdateNoticeWindow : Window
    {
        private const int WsExNoActivate = 0x08000000;
        private const double TailWidth = 18, TailEdge = 14;

        public event Action UpdateRequested, SnoozeRequested, CloseRequested;

        public UpdateNoticeWindow()
        {
            InitializeComponent();
            AuxTheme.ApplyTo(this);
            SourceInitialized += (s, e) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE,
                    NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE) | WsExNoActivate | NativeMethods.WS_EX_TOOLWINDOW);
            };
        }

        /// <summary>True while the tail points down at an anchor below the bubble (false: the bubble hangs under it).</summary>
        internal bool TailPointsDown => TailDown.Visibility == Visibility.Visible;
        /// <summary>Where the tail's tip is, in screen DIPs.</summary>
        internal Point TailTip => new Point(Left + TailDown.Margin.Left + TailWidth / 2, TailPointsDown ? Top + BubbleSize.Height : Top);

        public void SetVersion(string text) => NoticeVersion.Text = text ?? "";

        public void SetStatus(string text)
        {
            NoticeStatus.Text = text ?? "";
            NoticeStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        public void SetBusy(bool busy)
        {
            NoticeUpdateButton.IsEnabled = !busy;
            NoticeSnoozeButton.IsEnabled = !busy;
            NoticeCloseButton.IsEnabled = !busy; // a download started here shows its progress here until it ends
        }

        /// <summary>The colors of the window it points at (the mini window may wear a theme of its own), else the app theme.</summary>
        public void MatchTheme(Window anchor)
        {
            var app = AuxTheme.Colors;
            foreach (var key in new[] { "AuxCanvasBrush", "AuxSurfaceBrush", "AuxInkBrush", "AuxMutedBrush", "AuxHairlineBrush", "AuxActionBrush", "AuxFocusBrush", "AuxActionTextBrush" })
            {
                object brush = anchor is MiniWindow && anchor.Resources.Contains(key) ? anchor.Resources[key] : null;
                if (brush == null)
                {
                    string color = key == "AuxCanvasBrush" ? app.Canvas : key == "AuxSurfaceBrush" ? app.Surface : key == "AuxInkBrush" ? app.Ink
                        : key == "AuxMutedBrush" ? app.Muted : key == "AuxHairlineBrush" ? app.Hairline : key == "AuxActionTextBrush" ? app.ActionText : app.Action;
                    var made = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                    made.Freeze();
                    if (Resources[key] is SolidColorBrush now && now.Color == made.Color) continue;
                    brush = made;
                }
                if (!ReferenceEquals(Resources[key], brush)) Resources[key] = brush;
            }
        }

        private Size BubbleSize
        {
            get
            {
                Bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return Bubble.DesiredSize;
            }
        }

        /// <summary>
        /// Puts the bubble right above the anchor (screen DIPs), its tail at the anchor's middle; below it when the work area
        /// has no room above. Kept inside the work area only when the anchor is on it (a window parked off screen is not
        /// pulled onto the screen).
        /// </summary>
        public void PlaceOver(Rect anchor, Rect workArea, double tipX)
        {
            var size = BubbleSize;
            bool clamp = !workArea.IsEmpty && workArea.IntersectsWith(anchor);
            double left = tipX - size.Width / 2;
            if (clamp) left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - size.Width));
            double above = anchor.Top - size.Height, below = anchor.Bottom;
            bool down = !clamp || above >= workArea.Top || below + size.Height > workArea.Bottom;
            double top = down ? above : below;
            if (clamp && down) top = Math.Max(workArea.Top, top);
            BubbleFace.Margin = down ? new Thickness(0, 0, 0, 9) : new Thickness(0, 9, 0, 0);
            TailDown.Visibility = down ? Visibility.Visible : Visibility.Collapsed;
            TailUp.Visibility = down ? Visibility.Collapsed : Visibility.Visible;
            var tail = new Thickness(Math.Max(TailEdge, Math.Min(tipX - left - TailWidth / 2, size.Width - TailEdge - TailWidth)), 0, 0, 0);
            TailDown.Margin = tail;
            TailUp.Margin = tail;
            if (Math.Abs(Left - left) > 0.1) Left = left;
            if (Math.Abs(Top - top) > 0.1) Top = top;
        }

        private void Update_Click(object sender, RoutedEventArgs e) { e.Handled = true; UpdateRequested?.Invoke(); }
        private void Snooze_Click(object sender, RoutedEventArgs e) { e.Handled = true; SnoozeRequested?.Invoke(); }
        private void Close_Click(object sender, RoutedEventArgs e) { e.Handled = true; CloseRequested?.Invoke(); }
    }
}
