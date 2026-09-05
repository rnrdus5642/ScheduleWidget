using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ScheduleWidget
{
    /// <summary>
    /// Makes a slider track behave like a direct-manipulation control: clicking
    /// jumps to that position and keeping the mouse button pressed continues to
    /// follow the pointer.
    /// </summary>
    public static class SliderInteraction
    {
        public static readonly DependencyProperty EnableMouseDragProperty =
            DependencyProperty.RegisterAttached(
                "EnableMouseDrag",
                typeof(bool),
                typeof(SliderInteraction),
                new PropertyMetadata(false, OnEnableMouseDragChanged));

        private static readonly DependencyProperty IsDraggingProperty =
            DependencyProperty.RegisterAttached(
                "IsDragging",
                typeof(bool),
                typeof(SliderInteraction),
                new PropertyMetadata(false));

        public static void SetEnableMouseDrag(DependencyObject element, bool value)
        {
            element.SetValue(EnableMouseDragProperty, value);
        }

        public static bool GetEnableMouseDrag(DependencyObject element)
        {
            return (bool)element.GetValue(EnableMouseDragProperty);
        }

        private static void OnEnableMouseDragChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs e)
        {
            Slider slider = dependencyObject as Slider;
            if (slider == null)
                return;

            if ((bool)e.NewValue)
            {
                slider.AddHandler(
                    UIElement.PreviewMouseLeftButtonDownEvent,
                    new MouseButtonEventHandler(Slider_PreviewMouseLeftButtonDown),
                    true);
                slider.AddHandler(
                    UIElement.PreviewMouseMoveEvent,
                    new MouseEventHandler(Slider_PreviewMouseMove),
                    true);
                slider.AddHandler(
                    UIElement.PreviewMouseLeftButtonUpEvent,
                    new MouseButtonEventHandler(Slider_PreviewMouseLeftButtonUp),
                    true);
            }
            else
            {
                slider.RemoveHandler(
                    UIElement.PreviewMouseLeftButtonDownEvent,
                    new MouseButtonEventHandler(Slider_PreviewMouseLeftButtonDown));
                slider.RemoveHandler(
                    UIElement.PreviewMouseMoveEvent,
                    new MouseEventHandler(Slider_PreviewMouseMove));
                slider.RemoveHandler(
                    UIElement.PreviewMouseLeftButtonUpEvent,
                    new MouseButtonEventHandler(Slider_PreviewMouseLeftButtonUp));
                StopDragging(slider);
            }
        }

        private static void Slider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Slider slider = sender as Slider;
            if (slider == null || IsInsideThumb(e.OriginalSource as DependencyObject, slider))
                return;

            UpdateValueFromMouse(slider, e);
            slider.Focus();
            slider.CaptureMouse();
            slider.SetValue(IsDraggingProperty, true);
            e.Handled = true;
        }

        private static void Slider_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            Slider slider = sender as Slider;
            if (slider == null || !IsDragging(slider))
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                StopDragging(slider);
                return;
            }

            UpdateValueFromMouse(slider, e);
            e.Handled = true;
        }

        private static void Slider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            Slider slider = sender as Slider;
            if (slider == null || !IsDragging(slider))
                return;

            UpdateValueFromMouse(slider, e);
            StopDragging(slider);
            e.Handled = true;
        }

        private static bool IsDragging(Slider slider)
        {
            return (bool)slider.GetValue(IsDraggingProperty);
        }

        private static void StopDragging(Slider slider)
        {
            slider.ClearValue(IsDraggingProperty);
            if (slider.IsMouseCaptured)
                slider.ReleaseMouseCapture();
        }

        private static bool IsInsideThumb(DependencyObject source, Slider slider)
        {
            while (source != null && source != slider)
            {
                if (source is Thumb)
                    return true;

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        private static void UpdateValueFromMouse(Slider slider, MouseEventArgs e)
        {
            Track track = FindVisualChild<Track>(slider);
            bool horizontal = slider.Orientation != Orientation.Vertical;
            Point position;
            double length;
            double thumbLength = 0;

            if (track != null)
            {
                position = e.GetPosition(track);
                length = horizontal ? track.ActualWidth : track.ActualHeight;
                if (track.Thumb != null)
                    thumbLength = horizontal ? track.Thumb.ActualWidth : track.Thumb.ActualHeight;
            }
            else
            {
                position = e.GetPosition(slider);
                length = horizontal ? slider.ActualWidth : slider.ActualHeight;
            }

            if (length <= 0 || slider.Maximum <= slider.Minimum)
                return;

            double coordinate = horizontal ? position.X : position.Y;
            double start = thumbLength > 0 ? thumbLength / 2.0 : 0;
            double end = thumbLength > 0 ? length - thumbLength / 2.0 : length;
            double ratio = end > start
                ? (coordinate - start) / (end - start)
                : coordinate / length;

            ratio = Math.Max(0, Math.Min(1, ratio));
            if (!horizontal)
                ratio = 1 - ratio;

            slider.Value = slider.Minimum + (slider.Maximum - slider.Minimum) * ratio;
        }

        private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
                return null;

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < childCount; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                T match = child as T;
                if (match != null)
                    return match;

                T nestedMatch = FindVisualChild<T>(child);
                if (nestedMatch != null)
                    return nestedMatch;
            }

            return null;
        }
    }
}
