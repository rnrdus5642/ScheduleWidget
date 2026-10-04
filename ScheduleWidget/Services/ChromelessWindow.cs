using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace ScheduleWidget
{
    // The app's extra windows (캐릭터 선택, 캐릭터 설정, 연락 · 알림, 음악, 미니 창의 설정) look alike: no title bar or icon,
    // no taskbar button, a single X in the top-right corner, drag an empty spot to move, edges still resize.
    public static class ChromelessWindow
    {
        /// <param name="rounded">Windows 11 rounded corners (and its thin border) for this window.</param>
        public static void Apply(Window window, bool addCloseButton = true, bool rounded = true)
        {
            AuxTheme.ApplyTo(window); // theme colors from the start (no light flash in Dark)
            if (rounded) window.SourceInitialized += (s, e) => RoundCorners(window);
            window.ShowInTaskbar = false;
            window.WindowStyle = WindowStyle.None;
            // Keeps the resize edges (and Aero snap) without drawing a title bar or the white frame of WindowStyle=None.
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = window.ResizeMode == ResizeMode.NoResize ? new Thickness(0) : new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
            // handledEventsToo: a window whose content scrolls (음악) marks every press handled, which blocked moving it.
            window.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) =>
            {
                // Only presses on empty spots move the window. A drag started on a drop-down (테마, 캐릭터 위치 …), slider,
                // text box, list or scroll bar would steal the mouse and close the drop-down the moment it opens.
                if (e.ButtonState != MouseButtonState.Pressed || OnControl(e.OriginalSource as DependencyObject)) return;
                try { window.DragMove(); } catch (System.InvalidOperationException) { }
            }), handledEventsToo: true);
            if (!addCloseButton || !(window.Content is UIElement content)) return;

            var close = new Button
            {
                // Clear of the vertical scroll bar when the whole window scrolls (음악).
                Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(0, 8, content is ScrollViewer ? 24 : 8, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand, ToolTip = "닫기" // focusable: keyboard users reach it with Tab and press Space / Enter
            };
            System.Windows.Automation.AutomationProperties.SetName(close, "닫기");
            if (window.TryFindResource("AuxUtilityButton") is Style style) close.Style = style;
            var cross = new Path
            {
                Data = Geometry.Parse("M4,4 L12,12 M12,4 L4,12"), Width = 12, Height = 12, Stretch = Stretch.Fill,
                StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
            cross.SetResourceReference(Shape.StrokeProperty, "AuxMutedBrush");
            close.Content = cross;
            close.Click += (s, e) => window.Close();
            Panel.SetZIndex(close, 100);

            var frame = new Border { BorderThickness = new Thickness(rounded ? 0 : 1) }; // rounded: Windows draws the border
            frame.SetResourceReference(Border.BorderBrushProperty, "AuxHairlineBrush");
            var layer = new Grid();
            window.Content = null;
            layer.Children.Add(content);
            layer.Children.Add(close);
            frame.Child = layer;
            window.Content = frame;
        }

        private static bool OnControl(DependencyObject node)
        {
            for (; node != null; node = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                if (node is System.Windows.Controls.Primitives.ButtonBase || node is ComboBox || node is System.Windows.Controls.Primitives.TextBoxBase ||
                    node is PasswordBox || node is Slider || node is System.Windows.Controls.Primitives.ScrollBar || node is System.Windows.Controls.Primitives.Thumb ||
                    node is ListBoxItem || node is System.Windows.Controls.Primitives.Selector || node is DatePicker || node is Calendar ||
                    node is System.Windows.Documents.Hyperlink || node is System.Windows.Interop.HwndHost)
                    return true;
                if (node is Window) return false;
            }
            return false;
        }

        // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2. Ignored on Windows 10 (square corners stay).
        private static void RoundCorners(Window window)
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            int round = 2;
            try { DwmSetWindowAttribute(handle, 33, ref round, sizeof(int)); }
            catch (System.DllNotFoundException) { }
            catch (System.EntryPointNotFoundException) { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(System.IntPtr hwnd, int attribute, ref int value, int size);
    }
}
