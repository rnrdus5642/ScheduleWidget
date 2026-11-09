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
    public partial class MiniWindow
    {
        private void Week_Click(object sender, RoutedEventArgs e)
        {
            CloseDayPopup();
            FinishWeekFlip(); // a flip still running jumps to its end first: the picture shows the settled page, the arrows its limits
            int direction = int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
            if (IsMonthViewOpen) { MoveMonth(direction); return; }
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
            if (IsMonthViewOpen) { displayedMonth = MonthStart(DateTime.Today); Refresh(); return; }
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
            !IsMonthViewOpen && // monthly navigation does not use the week-page animation
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

    }
}
