using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        private sealed class ModePicture
        {
            public BitmapSource Bitmap;
            public Rect Pixels;
        }
        private sealed class ModePetPicture
        {
            public int Slot;
            public Image Image;
            public Rect From, To;
        }

        private MonitorStateData modeTransitionFrom, modeTransitionTo;
        // Presentation geometry is exclusively in physical screen pixels, even across different monitor scales.
        private Rect modeOverlayPixels, modeBoardFromPixels, modeBoardToPixels;
        private ModePicture capturedModePicture;
        private double modeBoardOpacity = 1, modePetsOpacity = 1;
        private bool modeBoardHitTest, modeTransitionOpen, modeTransitionPrepared;
        private Window modeTransitionWindow;
        private TaskCompletionSource<bool> modeCoverFade;
        private Canvas modePetsLayer;
        private readonly List<ModePetPicture> modePetPictures = new List<ModePetPicture>();
        private int modeTransitionGeneration, modeTransitionFrames;
        private readonly System.Diagnostics.Stopwatch modeTransitionClock = new System.Diagnostics.Stopwatch();
        private const double ModeTransitionMilliseconds = 160;
        private static readonly DependencyProperty ModeTransitionProgressProperty = DependencyProperty.Register(
            "ModeTransitionProgress", typeof(double), typeof(MiniWindow), new PropertyMetadata(0.0,
                (sender, args) => ((MiniWindow)sender).PaintCalendarModeTransition((double)args.NewValue)));

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        private ImageSource CaptureModeSnapshot()
        {
            if (!IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation) return null;
            capturedModePicture = CaptureBoardPicture();
            return capturedModePicture?.Bitmap;
        }

        private ImageSource CaptureCalendarBoardImage() => CaptureBoardPicture()?.Bitmap;

        private static Rect WholePixelBounds(Rect rect) => new Rect(
            new Point(Math.Floor(rect.Left + .00001), Math.Floor(rect.Top + .00001)),
            new Point(Math.Ceiling(rect.Right - .00001), Math.Ceiling(rect.Bottom - .00001)));

        private Rect BoardPixels()
        {
            var dpi = VisualTreeHelper.GetDpi(CalendarBoardContent);
            var board = CaptureCalendarBoard();
            var origin = IsLoaded ? CalendarBoardContent.PointToScreen(new Point()) : new Point(board.Left * dpi.DpiScaleX, board.Top * dpi.DpiScaleY);
            return new Rect(origin, new Size(CalendarBoardContent.ActualWidth * dpi.DpiScaleX, CalendarBoardContent.ActualHeight * dpi.DpiScaleY));
        }

        private ModePicture CaptureBoardPicture()
        {
            if (CalendarBoardContent.ActualWidth < 1 || CalendarBoardContent.ActualHeight < 1) return null;
            try { return CaptureModeVisual(CalendarBoardContent, BoardPixels()); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is System.Runtime.InteropServices.COMException)
            {
                PetLog.Write("calendar-transition", "snapshot unavailable: " + ex.GetType().Name);
                return null;
            }
        }

        private ModePicture CaptureModeVisual(FrameworkElement element, Rect screenPixels)
        {
            var dpi = VisualTreeHelper.GetDpi(element);
            var pixels = WholePixelBounds(screenPixels);
            double reduction = Math.Min(1, 4000 / Math.Max(pixels.Width, pixels.Height));
            var offset = VisualTreeHelper.GetOffset(element);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                // Capture one intact visual rather than reconstructing its children. The fractional pixel origin is
                // included in the image, so its border and text land on exactly the same pixels at the handoff.
                drawing.DrawRectangle(new VisualBrush(element)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(offset.X, offset.Y, element.ActualWidth, element.ActualHeight)
                }, null, new Rect((screenPixels.Left - pixels.Left) / dpi.DpiScaleX, (screenPixels.Top - pixels.Top) / dpi.DpiScaleY,
                    element.ActualWidth, element.ActualHeight));
            }
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(pixels.Width * reduction)),
                Math.Max(1, (int)Math.Ceiling(pixels.Height * reduction)),
                96 * dpi.DpiScaleX * reduction, 96 * dpi.DpiScaleY * reduction, PixelFormats.Pbgra32);
            bitmap.Render(visual); bitmap.Freeze();
            return new ModePicture { Bitmap = bitmap, Pixels = pixels };
        }

        // Unshown checks use the same geometry, captures and completion path without creating a presentation window.
        private void StartCalendarModeTransition(MonitorStateData from, MonitorStateData to, ImageSource snapshot) =>
            BeginCalendarModeTransition(IsMonthViewOpen, from, to, snapshot);

        private void BeginCalendarModeTransition(bool open, MonitorStateData from, MonitorStateData to, ImageSource snapshot)
        {
            int generation = ++modeTransitionGeneration;
            CancelModeCoverFade();
            SetCurrentValue(ModeTransitionProgressProperty, 0.0);
            modeBoardOpacity = MiniRoot.Opacity; modePetsOpacity = CharacterHost.Opacity; modeBoardHitTest = MiniRoot.IsHitTestVisible;
            modeTransitionFrom = from; modeTransitionTo = to; modeTransitionOpen = open; modeTransitionPrepared = false;
            changingCalendarMode = true;
            var dpi = VisualTreeHelper.GetDpi(this);
            modeBoardFromPixels = ReferenceEquals(capturedModePicture?.Bitmap, snapshot) ? capturedModePicture.Pixels : WholePixelBounds(BoardPixels());
            modeBoardToPixels = WholePixelBounds(new Rect(to.Left * dpi.DpiScaleX, to.Top * dpi.DpiScaleY, to.Width * dpi.DpiScaleX, to.Height * dpi.DpiScaleY));
            modeOverlayPixels = Rect.Union(modeBoardFromPixels, modeBoardToPixels);
            if (PetsShown && IsLoaded)
            {
                foreach (var rect in PetRects())
                    if (rect.Width > 0 && rect.Height > 0) modeOverlayPixels.Union(PetScreenPixels(rect));
                foreach (var rect in PetBoardRects(to.Width, to.Height))
                    if (rect.Width > 0 && rect.Height > 0)
                        modeOverlayPixels.Union(new Rect((to.Left + rect.Left) * dpi.DpiScaleX, (to.Top + rect.Top) * dpi.DpiScaleY,
                            rect.Width * dpi.DpiScaleX, rect.Height * dpi.DpiScaleY));
            }
            modeOverlayPixels.Inflate(3, 3);
            modeOverlayPixels = WholePixelBounds(modeOverlayPixels);
            SetModeImage(CalendarModeSnapshot, snapshot, modeBoardFromPixels.Size);
            SetModeImage(CalendarModeNext, null, modeBoardToPixels.Size);
            CalendarModeOverlay.Visibility = Visibility.Visible;
            CalendarModeOverlay.Opacity = modeBoardOpacity;
            MiniRoot.IsHitTestVisible = false; // also closes date tooltips while their coordinates are changing
            PaintCalendarModeTransition(0);
            modeTransitionFrames = 0;
            if (!IsLoaded || !IsVisible)
            {
                PrepareCalendarModeDestination();
                modeTransitionClock.Restart();
                return;
            }
            try
            {
                ShowCalendarModeWindow();
                _ = RunCalendarModeTransitionAsync(generation);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is System.Runtime.InteropServices.COMException)
            {
                PetLog.Write("calendar-transition", "overlay unavailable: " + ex.GetType().Name);
                FinishCalendarModeTransition();
            }
        }

        private static void SetModeImage(Image image, ImageSource source, Size naturalPixels)
        {
            image.Source = source;
            image.Width = Math.Max(1, naturalPixels.Width); image.Height = Math.Max(1, naturalPixels.Height);
            Canvas.SetLeft(image, 0); Canvas.SetTop(image, 0);
            image.RenderTransform = new MatrixTransform();
        }

        private void ShowCalendarModeWindow()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (modeTransitionWindow == null)
            {
                OuterRoot.Children.Remove(CalendarModeOverlay);
                modePetsLayer = new Canvas { IsHitTestVisible = false };
                var content = new Grid { IsHitTestVisible = false };
                content.Children.Add(CalendarModeOverlay); content.Children.Add(modePetsLayer);
                modeTransitionWindow = new Window
                {
                    WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                    ShowInTaskbar = false, ShowActivated = false, Focusable = false, IsHitTestVisible = false,
                    ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.Manual,
                    Owner = this, Content = content, Left = modeOverlayPixels.Left / dpi.DpiScaleX, Top = modeOverlayPixels.Top / dpi.DpiScaleY,
                    Width = modeOverlayPixels.Width / dpi.DpiScaleX, Height = modeOverlayPixels.Height / dpi.DpiScaleY
                };
                modeTransitionWindow.SourceInitialized += (s, e) =>
                {
                    var handle = new WindowInteropHelper(modeTransitionWindow).Handle;
                    HwndSource.FromHwnd(handle)?.AddHook(AllowLargerThanScreen);
                    AskSizeLimits(handle);
                    NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE,
                        NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE) | WsExTransparent | WsExNoActivate | NativeMethods.WS_EX_TOOLWINDOW);
                };
            }
            var ownerHandle = new WindowInteropHelper(this).Handle;
            ((UIElement)modeTransitionWindow.Content).Opacity = 0;
            modeTransitionWindow.Topmost = (NativeMethods.GetWindowLong(ownerHandle, NativeMethods.GWL_EXSTYLE) & 8) != 0;
            var overlayHandle = new WindowInteropHelper(modeTransitionWindow).EnsureHandle();
            // A new Window's visual can still report 96 DPI before Show attaches its root to the HWND. Keep the
            // surface empty until it has its actual device transform, otherwise its first picture is the wrong size.
            CalendarModeOverlay.Visibility = modePetsLayer.Visibility = Visibility.Hidden;
            modeTransitionWindow.Show();
            for (int pass = 0; pass < 2; pass++)
            {
                if (NativeMethods.TryGetWindowRect(overlayHandle, out NativeMethods.Rect placed) &&
                    placed.Left == modeOverlayPixels.Left && placed.Top == modeOverlayPixels.Top &&
                    placed.Right - placed.Left == modeOverlayPixels.Width && placed.Bottom - placed.Top == modeOverlayPixels.Height) break;
                NativeMethods.SetWindowPos(overlayHandle, IntPtr.Zero,
                    (int)modeOverlayPixels.Left, (int)modeOverlayPixels.Top, (int)modeOverlayPixels.Width, (int)modeOverlayPixels.Height,
                    NativeMethods.SWP_NOACTIVATE | SwpNoZOrder | SwpNoOwnerZOrder);
            }
            modeTransitionWindow.UpdateLayout();
            var device = HwndSource.FromHwnd(overlayHandle)?.CompositionTarget;
            var transform = new MatrixTransform(device?.TransformFromDevice ?? Matrix.Identity); transform.Freeze();
            CalendarModeOverlay.RenderTransform = modePetsLayer.RenderTransform = transform;
            if (NativeMethods.TryGetWindowRect(overlayHandle, out NativeMethods.Rect actual))
                modeOverlayPixels = new Rect(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top);
            CaptureCalendarModePets();
            PaintCalendarModeTransition(0);
            CalendarModeOverlay.Visibility = modePetsLayer.Visibility = Visibility.Visible;
            modeTransitionWindow.UpdateLayout();
        }

        private Rect PetScreenPixels(Rect rect)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return WholePixelBounds(new Rect(OuterRoot.PointToScreen(rect.TopLeft), new Size(rect.Width * dpi.DpiScaleX, rect.Height * dpi.DpiScaleY)));
        }

        private void CaptureCalendarModePets()
        {
            modePetsLayer.Children.Clear(); modePetPictures.Clear();
            if (!PetsShown || CharacterView.ActualWidth < 1 || CharacterView.ActualHeight < 1) return;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(CharacterView);
                var frame = CaptureModeVisual(CharacterView, new Rect(CharacterView.PointToScreen(new Point()),
                    new Size(CharacterView.ActualWidth * dpi.DpiScaleX, CharacterView.ActualHeight * dpi.DpiScaleY)));
                var cells = PetRects();
                for (int slot = 0; slot < cells.Count; slot++)
                {
                    if (cells[slot].Width <= 0 || cells[slot].Height <= 0) continue;
                    var pixels = Rect.Intersect(PetScreenPixels(cells[slot]), frame.Pixels);
                    if (pixels.IsEmpty || pixels.Width < 1 || pixels.Height < 1) continue;
                    double sx = frame.Bitmap.PixelWidth / frame.Pixels.Width, sy = frame.Bitmap.PixelHeight / frame.Pixels.Height;
                    int x = (int)Math.Round((pixels.Left - frame.Pixels.Left) * sx), y = (int)Math.Round((pixels.Top - frame.Pixels.Top) * sy);
                    int width = Math.Min(frame.Bitmap.PixelWidth - x, Math.Max(1, (int)Math.Round(pixels.Width * sx)));
                    int height = Math.Min(frame.Bitmap.PixelHeight - y, Math.Max(1, (int)Math.Round(pixels.Height * sy)));
                    var cropped = new CroppedBitmap(frame.Bitmap, new Int32Rect(x, y, width, height)); cropped.Freeze();
                    var image = new Image { Stretch = Stretch.Fill, Opacity = modePetsOpacity };
                    SetModeImage(image, cropped, pixels.Size);
                    modePetPictures.Add(new ModePetPicture { Slot = slot, Image = image, From = pixels, To = pixels });
                    modePetsLayer.Children.Add(image);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is System.Runtime.InteropServices.COMException)
            { PetLog.Write("calendar-transition", "pet snapshot unavailable: " + ex.GetType().Name); }
        }

        private async Task ModePresentationAsync()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            DwmFlush();
        }

        private Task<bool> FadeModeCoverAsync(double from, double to, int milliseconds)
        {
            CancelModeCoverFade();
            if (!(modeTransitionWindow?.Content is UIElement surface)) return Task.FromResult(false);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            modeCoverFade = completion;
            // Store the final base value first: removing HoldEnd must not reset opacity for one frame.
            surface.Opacity = to;
            var fade = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) { FillBehavior = FillBehavior.HoldEnd };
            fade.Completed += (s, e) =>
            {
                if (modeCoverFade != completion) return;
                modeCoverFade = null;
                surface.BeginAnimation(OpacityProperty, null);
                completion.TrySetResult(true);
            };
            surface.BeginAnimation(OpacityProperty, fade);
            return completion.Task;
        }

        private void CancelModeCoverFade()
        {
            var pending = modeCoverFade; modeCoverFade = null;
            if (modeTransitionWindow?.Content is UIElement surface) surface.BeginAnimation(OpacityProperty, null);
            pending?.TrySetResult(false);
        }

        private async Task RunCalendarModeTransitionAsync(int generation)
        {
            try
            {
                if (generation != modeTransitionGeneration || modeTransitionTo == null || closed) return;
                // The screenshot and live text have slightly different rasterization. Blend identical geometry at
                // each end instead of swapping the two renderers on a single frame.
                if (!await FadeModeCoverAsync(0, 1, 20)) return;
                await ModePresentationAsync();
                if (generation != modeTransitionGeneration || modeTransitionTo == null || closed) return;
                MiniRoot.Opacity = 0; CharacterHost.Opacity = 0;
                await ModePresentationAsync();
                if (generation != modeTransitionGeneration || modeTransitionTo == null || closed) return;
                PrepareCalendarModeDestination();
                if (generation != modeTransitionGeneration || modeTransitionTo == null || closed) return;
                if (CalendarModeNext.Source == null) { FinishCalendarModeTransition(); return; }
                modeTransitionClock.Restart(); modeTransitionFrames = 0;
                var target = modeTransitionTo;
                var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ModeTransitionMilliseconds))
                { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.HoldEnd };
                animation.Completed += (s, e) => { if (ReferenceEquals(modeTransitionTo, target)) FinishCalendarModeTransition(); };
                BeginAnimation(ModeTransitionProgressProperty, animation);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                PetLog.Write("calendar-transition", ex.GetType().Name + ": " + ex.Message);
                if (generation == modeTransitionGeneration) FinishCalendarModeTransition();
            }
        }

        private void PrepareCalendarModeDestination()
        {
            if (modeTransitionPrepared || modeTransitionTo == null) return;
            modeTransitionPrepared = true;
            data.MiniMonthView = modeTransitionOpen;
            ApplyCalendarModeVisibility(modeTransitionOpen);
            MiniRoot.Opacity = 0;
            ResizeCalendarBoard(modeTransitionTo, updateLayout: false);
            Refresh(); UpdateLayout();
            if (!IsLoaded)
            {
                OuterRoot.Measure(new Size(Width, Height));
                OuterRoot.Arrange(new Rect(0, 0, Width, Height)); OuterRoot.UpdateLayout();
            }
            var next = CaptureBoardPicture();
            if (next != null)
            {
                modeBoardToPixels = next.Pixels;
                SetModeImage(CalendarModeNext, next.Bitmap, next.Pixels.Size);
            }
            if (IsLoaded)
            {
                var pets = PetRects();
                foreach (var pet in modePetPictures)
                    if (pet.Slot < pets.Count && pets[pet.Slot].Width > 0 && pets[pet.Slot].Height > 0) pet.To = PetScreenPixels(pets[pet.Slot]);
            }
            PaintCalendarModeTransition(0);
        }

        private static Rect InterpolateModeRect(Rect from, Rect to, double progress)
        {
            double Mix(double a, double b) => a + (b - a) * progress;
            return new Rect(Mix(from.X, to.X), Mix(from.Y, to.Y), Mix(from.Width, to.Width), Mix(from.Height, to.Height));
        }

        private void PlaceModeImage(Image image, Rect pixels)
        {
            if (!(image.RenderTransform is MatrixTransform transform)) return;
            // Transform cached images instead of invalidating their measure/arrange on every animation frame.
            transform.Matrix = new Matrix(pixels.Width / image.Width, 0, 0, pixels.Height / image.Height,
                pixels.X - modeOverlayPixels.X, pixels.Y - modeOverlayPixels.Y);
        }

        private void PaintCalendarModeTransition(double progress)
        {
            if (modeTransitionTo == null) return;
            modeTransitionFrames++;
            progress = Math.Max(0, Math.Min(1, progress));
            var pixels = InterpolateModeRect(modeBoardFromPixels, modeBoardToPixels, progress);
            PlaceModeImage(CalendarModeSnapshot, pixels);
            PlaceModeImage(CalendarModeNext, pixels);
            foreach (var pet in modePetPictures) PlaceModeImage(pet.Image, InterpolateModeRect(pet.From, pet.To, progress));
            CalendarModeSnapshot.Opacity = 1;
            CalendarModeNext.Opacity = progress;
        }

        private void AdvanceCalendarModeTransition(double progress)
        {
            if (progress >= 1) FinishCalendarModeTransition();
            else PaintCalendarModeTransition((1 - Math.Cos(Math.PI * Math.Max(0, progress))) / 2);
        }

        private void FinishCalendarModeTransition()
        {
            if (modeTransitionTo == null) return;
            ++modeTransitionGeneration;
            CancelModeCoverFade();
            // A HoldEnd animation otherwise falls back to its original zero value when detached.
            SetCurrentValue(ModeTransitionProgressProperty, 1.0);
            BeginAnimation(ModeTransitionProgressProperty, null);
            if (!closed) PrepareCalendarModeDestination();
            PaintCalendarModeTransition(1);
            modeTransitionClock.Stop();
            PetLog.Write("calendar-mode", "transition frames=" + modeTransitionFrames + ", ms=" + modeTransitionClock.ElapsedMilliseconds);
            modeTransitionTo = modeTransitionFrom = null;
            MiniRoot.Opacity = modeBoardOpacity; MiniRoot.IsHitTestVisible = modeBoardHitTest;
            CharacterHost.Opacity = modePetsOpacity;
            changingCalendarMode = false;
            if (!closed) { UpdateLayout(); SavePosition(); }
            if (modeTransitionWindow != null && IsVisible && !closed) _ = RetireCalendarModeWindowAsync(modeTransitionGeneration);
            else HideCalendarModeOverlay();
        }

        private async Task RetireCalendarModeWindowAsync(int generation)
        {
            try
            {
                await ModePresentationAsync();
                if (generation == modeTransitionGeneration) await FadeModeCoverAsync(1, 0, 30);
            }
            catch (TaskCanceledException) { }
            finally { if (generation == modeTransitionGeneration) HideCalendarModeOverlay(); }
        }

        private void HideCalendarModeOverlay()
        {
            ++modeTransitionGeneration;
            CancelModeCoverFade();
            modeTransitionWindow?.Hide();
            CalendarModeOverlay.Visibility = Visibility.Collapsed;
            CalendarModeSnapshot.Source = CalendarModeNext.Source = null;
            capturedModePicture = null;
            modePetPictures.Clear();
            modePetsLayer?.Children.Clear();
        }

        private void CloseCalendarModeOverlay()
        {
            HideCalendarModeOverlay();
            if (modeTransitionWindow == null) return;
            if (CalendarModeOverlay.Parent is Panel parent) parent.Children.Remove(CalendarModeOverlay);
            modeTransitionWindow.Content = null;
            modeTransitionWindow.Close(); modeTransitionWindow = null;
            modePetsLayer = null;
            if (!OuterRoot.Children.Contains(CalendarModeOverlay)) OuterRoot.Children.Insert(1, CalendarModeOverlay);
        }
    }
}
