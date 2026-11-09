using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private sealed class ConnectivityTestWatch : IDisposable
        {
            private readonly Action dispose;
            internal ConnectivityTestWatch(Action dispose) { this.dispose = dispose; }
            public void Dispose() => dispose();
        }

        private static void OfflineIndicatorTransitions()
        {
            var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false };
            int saves = 0, subscriptions = 0, disposals = 0;
            var mini = new MiniWindow(data, () => { saves++; return true; }, () => { });
            Action<bool?> report = null;
            SetField(mini, "connectivitySubscribe", (Func<Action<bool?>, IDisposable>)(callback =>
            {
                subscriptions++;
                report = callback;
                callback(true);
                return new ConnectivityTestWatch(() => disposals++);
            }));
            try
            {
                string before = JsonConvert.SerializeObject(data);
                var indicator = Control<ContentControl>(mini, "OfflineIndicator");
                Call(mini, "StartConnectivityWatch"); Call(mini, "StartConnectivityWatch");
                Require(subscriptions == 1 && indicator.Visibility == Visibility.Collapsed, "Online startup showed an offline icon or duplicated the subscription.");
                // Windows sends notifications from a background thread. The visual update must reach the WPF dispatcher.
                Task.Run(() => report(false)).GetAwaiter().GetResult();
                mini.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                Require(indicator.Visibility == Visibility.Visible, "The background offline notification was not displayed.");
                Require(AutomationProperties.GetName(indicator) == "오프라인" &&
                    (indicator.ToolTip as TextBlock)?.Text.Contains("PC에 저장") == true, "The status has no accessible name or local-save explanation.");
                report(true);
                Require(indicator.Visibility == Visibility.Collapsed, "Reconnecting left the offline icon visible.");
                report(false); report(null);
                Require(indicator.Visibility == Visibility.Collapsed, "An unknown OS status was presented as definitely offline.");

                var stale = report;
                Call(mini, "StopConnectivityWatch"); Call(mini, "StartConnectivityWatch");
                stale(false);
                Require(disposals == 1 && subscriptions == 2 && indicator.Visibility == Visibility.Collapsed,
                    "A stale callback changed the replacement watch's status.");
                Require(saves == 0 && JsonConvert.SerializeObject(data) == before, "Connectivity notifications changed or saved user data.");
                mini.Close();
                report(false);
                Require(disposals == 2 && indicator.Visibility == Visibility.Collapsed, "Closing the calendar leaked its watch or accepted a late callback.");
            }
            finally { if (!(bool)Field(mini, "closed")) mini.Close(); }

            var companion = new MiniWindow(new AppData(), () => true, () => { }, companion: true);
            SetField(companion, "connectivitySubscribe", (Func<Action<bool?>, IDisposable>)(_ =>
                throw new InvalidOperationException("A pets-only window subscribed to network status.")));
            try { Call(companion, "StartConnectivityWatch"); }
            finally { companion.Close(); }
        }

        private static void OfflineIndicatorLayout()
        {
            foreach (double width in new[] { 240.0, 360.0, 800.0 })
            {
                var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false,
                    MiniBoard = new MonitorStateData { Left = 100, Top = 100, Width = width, Height = 260 } };
                var mini = new MiniWindow(data, () => true, () => { });
                SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
                Action<bool?> report = null;
                SetField(mini, "connectivitySubscribe", (Func<Action<bool?>, IDisposable>)(callback =>
                {
                    report = callback; callback(false); return new ConnectivityTestWatch(() => { });
                }));
                try
                {
                    Call(mini, "StartConnectivityWatch");
                    foreach (bool month in new[] { false, true })
                    {
                        mini.SetMonthViewOpen(month); LayoutCalendarMode(mini);
                        var header = Control<FrameworkElement>(mini, "CalendarHeader");
                        var icon = Control<ContentControl>(mini, "OfflineIndicator");
                        var settings = Control<Button>(mini, "HeaderSettingsButton");
                        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(header).TransformBounds(new Rect(element.RenderSize));
                        var iconBounds = Bounds(icon); var settingsBounds = Bounds(settings);
                        Require(icon.ActualWidth == 20 && icon.ActualHeight == 24 && iconBounds.Left >= 0 && iconBounds.Right <= settingsBounds.Left + 1,
                            "The offline icon is clipped or does not sit beside settings.");
                        Require(Bounds(Control<Button>(mini, "NextWeek")).Right <= header.ActualWidth + 1,
                            "The offline indicator pushed navigation outside the header.");
                        if (width == 360)
                        {
                            SaveConnectivityHeader(header, month ? "offline-month.png" : "offline-compact.png");
                            mini.ApplyAppearance(AppearanceSettings.Presets["Dark"]); LayoutCalendarMode(mini);
                            SaveConnectivityHeader(header, month ? "offline-month-dark.png" : "offline-compact-dark.png");
                            mini.ApplyAppearance(AppearanceSettings.Presets["Light"]); LayoutCalendarMode(mini);
                        }
                        report(true); LayoutCalendarMode(mini);
                        Require(icon.Visibility == Visibility.Collapsed, "Online status did not remove the icon from header layout.");
                        report(false);
                    }
                    Require(!mini.IsVisible, "The offline layout check displayed a real window.");
                }
                finally { mini.Close(); }
            }
        }

        private static void SaveConnectivityHeader(FrameworkElement header, string name)
        {
            var size = new Size(header.ActualWidth, header.ActualHeight);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
                context.DrawRectangle(new VisualBrush(header) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size) }, null, new Rect(size));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(root, name))) encoder.Save(file);
        }
    }
}
