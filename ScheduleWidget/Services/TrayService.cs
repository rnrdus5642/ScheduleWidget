using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScheduleWidget
{
    public class TrayService : IDisposable
    {
        private NotifyIcon trayIcon;

        public void Initialize(MainWindow window)
        {
            trayIcon = new NotifyIcon
            {
                Icon = CreateTrayIcon(),
                Visible = true,
                Text = "일정 위젯"
            };

            var menu = new ContextMenuStrip
            {
                Renderer = new WidgetMenuRenderer(),
                ShowImageMargin = false,
                ShowCheckMargin = false,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(23, 32, 51),
                // OS 메뉴 글꼴을 사용해 DPI 배율에서도 글자가 흐려지거나 잘리지 않게 합니다.
                Font = SystemFonts.MenuFont,
                Padding = new Padding(8),
                DropShadowEnabled = true
            };

            // 메뉴 창 자체도 렌더러와 같은 반경으로 잘라 모서리에서 흰색 사각형이
            // 삐져나오지 않도록 합니다.
            menu.SizeChanged += (s, e) => ApplyMenuRegion(menu);

            menu.Items.Add(CreateMenuItem("열기", (s, e) => window.Show()));

            menu.Items.Add(CreateMenuItem("위치 초기화", (s, e) =>
            {
                window.ResetPositionToPrimaryMonitorCenter();
            }));

            menu.Items.Add(new ToolStripSeparator
            {
                AutoSize = false,
                Height = 1,
                Margin = new Padding(8, 7, 8, 7)
            });

            ToolStripMenuItem exitItem = CreateMenuItem("종료", (s, e) => {
                Dispose();
                System.Windows.Application.Current.Shutdown();
            });
            exitItem.ForeColor = Color.FromArgb(210, 65, 82);
            menu.Items.Add(exitItem);

            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += (s, e) => window.Show();
        }

        private static ToolStripMenuItem CreateMenuItem(string text, EventHandler handler)
        {
            var item = new ToolStripMenuItem(text)
            {
                AutoSize = false,
                Size = new Size(180, 40),
                Padding = Padding.Empty,
                Margin = new Padding(0),
                TextAlign = ContentAlignment.MiddleLeft
            };
            item.Click += handler;
            return item;
        }

        private static void ApplyMenuRegion(ContextMenuStrip menu)
        {
            if (menu.Width <= 0 || menu.Height <= 0)
                return;

            using (GraphicsPath path = CreateRoundedRectangle(
                new RectangleF(0.5f, 0.5f, menu.Width - 1f, menu.Height - 1f), 12f))
            {
                menu.Region = new Region(path);
            }
        }

        private static Icon CreateTrayIcon()
        {
            const int size = 32;

            try
            {
                using (var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.Clear(Color.Transparent);

                    using (var background = new SolidBrush(Color.FromArgb(86, 100, 233)))
                    using (GraphicsPath tile = CreateRoundedRectangle(new RectangleF(2, 2, 28, 28), 8))
                    {
                        graphics.FillPath(background, tile);
                    }

                    using (var white = new Pen(Color.White, 1.8f))
                    {
                        white.StartCap = LineCap.Round;
                        white.EndCap = LineCap.Round;
                        white.LineJoin = LineJoin.Round;

                        graphics.DrawRectangle(white, 8, 9, 16, 14);
                        graphics.DrawLine(white, 8, 13, 24, 13);
                        graphics.DrawLine(white, 11, 7, 11, 11);
                        graphics.DrawLine(white, 21, 7, 21, 11);

                        using (var dateBrush = new SolidBrush(Color.White))
                        {
                            graphics.FillEllipse(dateBrush, 11, 16, 2, 2);
                            graphics.FillEllipse(dateBrush, 15, 16, 2, 2);
                            graphics.FillEllipse(dateBrush, 19, 16, 2, 2);
                            graphics.FillEllipse(dateBrush, 11, 20, 2, 2);
                            graphics.FillEllipse(dateBrush, 15, 20, 2, 2);
                            graphics.FillEllipse(dateBrush, 19, 20, 2, 2);
                        }
                    }

                    IntPtr iconHandle = bitmap.GetHicon();
                    try
                    {
                        using (Icon source = Icon.FromHandle(iconHandle))
                        {
                            return (Icon)source.Clone();
                        }
                    }
                    finally
                    {
                        DestroyIcon(iconHandle);
                    }
                }
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
        {
            float diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class WidgetMenuRenderer : ToolStripRenderer
        {
            private static readonly Color Background = Color.White;
            private static readonly Color Border = Color.FromArgb(218, 224, 234);
            private static readonly Color Hover = Color.FromArgb(241, 243, 247);

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                using (GraphicsPath path = CreateRoundedRectangle(
                    new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1f, e.ToolStrip.Height - 1f), 12f))
                using (var brush = new SolidBrush(Background))
                {
                    e.Graphics.FillPath(brush, path);
                }
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                using (GraphicsPath path = CreateRoundedRectangle(
                    new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1f, e.ToolStrip.Height - 1f), 12f))
                using (var pen = new Pen(Border, 1f))
                {
                    e.Graphics.DrawPath(pen, path);
                }
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected && !e.Item.Pressed)
                    return;

                // OnRenderMenuItemBackground의 Graphics 원점은 해당 항목 내부입니다.
                Rectangle bounds = new Rectangle(Point.Empty, e.Item.Size);
                bounds.Inflate(-2, -1);

                using (GraphicsPath path = CreateRoundedRectangle(bounds, 8f))
                using (var brush = new SolidBrush(Hover))
                {
                    e.Graphics.FillPath(brush, path);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                // 기본 renderer의 텍스트 사각형 대신 항목 전체를 기준으로 계산합니다.
                // 이렇게 해야 메뉴 높이가 바뀌거나 DPI가 달라도 세로 중앙이 유지됩니다.
                // 항목의 Bounds에는 메뉴 안에서의 위치가 포함되므로 사용하지 않습니다.
                // 렌더러가 넘겨준 Graphics는 항목 좌표계에서 시작합니다.
                Rectangle bounds = new Rectangle(Point.Empty, e.Item.Size);
                bounds.X += 16;
                bounds.Width = Math.Max(0, bounds.Width - 32);

                TextFormatFlags flags = TextFormatFlags.Left |
                                        TextFormatFlags.VerticalCenter |
                                        TextFormatFlags.SingleLine |
                                        TextFormatFlags.NoPrefix |
                                        TextFormatFlags.NoPadding |
                                        TextFormatFlags.EndEllipsis;
                TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, bounds, e.TextColor, flags);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                Rectangle bounds = new Rectangle(Point.Empty, e.Item.Size);
                int y = bounds.Top + (bounds.Height / 2);

                using (var pen = new Pen(Border, 1f))
                {
                    e.Graphics.DrawLine(pen, bounds.Left + 8, y, bounds.Right - 8, y);
                }
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public void Dispose()
        {
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
        }
    }
}
