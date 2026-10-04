using System;
using System.Windows;
using System.Windows.Controls;

namespace ScheduleWidget
{
    // Existing named controls retain their event handlers while the unified settings window hosts them.
    internal sealed class SettingsContentLease : IDisposable
    {
        private readonly Panel originalParent;
        private readonly int originalIndex;
        private readonly FrameworkElement element;
        private readonly Thickness margin;
        private bool restored;

        public SettingsContentLease(FrameworkElement element)
        {
            this.element = element ?? throw new ArgumentNullException(nameof(element));
            originalParent = element.Parent as Panel ?? throw new InvalidOperationException("Settings content needs a panel parent.");
            originalIndex = originalParent.Children.IndexOf(element);
            margin = element.Margin;
            originalParent.Children.Remove(element);
        }

        public void Dispose()
        {
            if (restored) return;
            if (element.Parent is Panel parent) parent.Children.Remove(element);
            else if (element.Parent is Decorator decorator) decorator.Child = null;
            else if (element.Parent is ContentControl control) control.Content = null;
            element.Margin = margin;
            originalParent.Children.Insert(Math.Min(originalIndex, originalParent.Children.Count), element);
            restored = true;
        }
    }
}
