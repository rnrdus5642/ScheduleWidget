using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScheduleWidget
{
    /// <summary>Only confirms installation. Downloading, verification and shutdown remain in MainWindow.</summary>
    public partial class UpdateConfirmationWindow : Window
    {
        public UpdateConfirmationWindow(UpdateCheckResult update)
        {
            if (update == null) throw new ArgumentNullException(nameof(update));
            InitializeComponent();
            ChromelessWindow.Apply(this, addCloseButton: false);
            CurrentVersion.Text = UpdateInfo.Format(update.Current);
            NewVersion.Text = UpdateInfo.Format(update.Version);
            RenderNotes(update.Manifest?.Notes);
        }

        internal void PrepareForOwner(Window owner)
        {
            var area = SystemParameters.WorkArea;
            var anchor = Rect.Empty;
            if (owner != null && owner.IsVisible)
            {
                Owner = owner;
                Topmost = owner.Topmost;
                var dpi = VisualTreeHelper.GetDpi(owner);
                if (NativeMethods.TryGetWorkArea(new WindowInteropHelper(owner).Handle, out NativeMethods.Rect screen))
                    area = new Rect(screen.Left / dpi.DpiScaleX, screen.Top / dpi.DpiScaleY,
                        (screen.Right - screen.Left) / dpi.DpiScaleX, (screen.Bottom - screen.Top) / dpi.DpiScaleY);
                if (owner.WindowState == WindowState.Normal && !double.IsNaN(owner.Left) && !double.IsNaN(owner.Top))
                    anchor = new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
            }
            FitWithin(area, anchor);
        }

        // Keep the actions on the owner's monitor, including a small work area after an RDP/display change.
        internal void FitWithin(Rect area, Rect anchor)
        {
            double width = Math.Min(560, Math.Max(1, area.Width - 32));
            double height = Math.Min(620, Math.Max(1, area.Height - 32));
            MinWidth = Math.Min(360, width);
            MinHeight = Math.Min(400, height);
            Width = width;
            Height = height;
            if (anchor.IsEmpty || !area.IntersectsWith(anchor)) anchor = area;
            Left = Math.Max(area.Left + 16, Math.Min(anchor.Left + (anchor.Width - width) / 2, area.Right - width - 16));
            Top = Math.Max(area.Top + 16, Math.Min(anchor.Top + (anchor.Height - height) / 2, area.Bottom - height - 16));
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        private void RenderNotes(string notes)
        {
            // Render release prose as native text, without a browser, external resources or a 500-character cut-off.
            // Headings and list markers affect layout; inline Markdown becomes plain, non-interactive text.
            var paragraph = new StringBuilder();
            bool bullet = false;
            Action flush = () =>
            {
                if (paragraph.Length == 0) return;
                AddParagraph(PlainText(paragraph.ToString()), bullet, false);
                paragraph.Clear();
                bullet = false;
            };
            foreach (string raw in (notes ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) { flush(); continue; }
                if (line.StartsWith("```", StringComparison.Ordinal) || Regex.IsMatch(line, @"^([-*_])(?:\s*\1){2,}$"))
                { flush(); continue; }
                var heading = Regex.Match(line, @"^#{1,6}\s+(.+?)\s*#*$");
                if (heading.Success)
                {
                    flush();
                    string title = PlainText(heading.Groups[1].Value);
                    // The window already names this first section.
                    if (NotesContent.Children.Count != 0 || (title.Replace(" ", "") != "변경사항" && title != "달라진 점"))
                        AddParagraph(title, false, true);
                    continue;
                }
                var item = Regex.Match(line, @"^(?:[-*+]\s+|\d+[.)]\s+)(.+)$");
                if (item.Success)
                {
                    flush();
                    bullet = true;
                    paragraph.Append(item.Groups[1].Value);
                }
                else
                {
                    if (paragraph.Length > 0) paragraph.Append(' ');
                    paragraph.Append(line);
                }
            }
            flush();
            if (NotesContent.Children.Count == 0) AddParagraph("이번 업데이트의 변경 사항은 따로 제공되지 않았습니다.", false, false);
        }

        private static string PlainText(string text)
        {
            text = Regex.Replace(text, @"!?\[([^\]]+)\]\([^)]*\)", "$1");
            text = Regex.Replace(text, @"`+([^`]+)`+", "$1");
            text = Regex.Replace(text, @"(\*\*|__)(.+?)\1", "$2");
            return Regex.Replace(text, @"(?<!\*)\*([^*]+)\*(?!\*)", "$1");
        }

        private void AddParagraph(string text, bool bullet, bool heading)
        {
            var body = new TextBlock
            {
                Text = text, FontSize = heading ? 14 : 13, LineHeight = 22,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, heading ? 12 : 0, 0, heading ? 10 : 12)
            };
            body.SetResourceReference(TextBlock.ForegroundProperty, "AuxInkBrush");
            if (!bullet) { NotesContent.Children.Add(body); return; }
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new Border { Width = 4, Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 9, 0, 0) };
            dot.SetResourceReference(Border.BackgroundProperty, "AuxMutedBrush");
            row.Children.Add(dot);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            NotesContent.Children.Add(row);
        }

        private void Later_Click(object sender, RoutedEventArgs e) => DialogResult = false;
        private void Install_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
