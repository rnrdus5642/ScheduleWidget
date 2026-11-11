using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScheduleWidget
{
    /// <summary>Closes an editor after focus moves outside its controls and owned dialogs.</summary>
    internal sealed class AuxiliaryWindowDismissal
    {
        private readonly Window window;
        private readonly Action requestClose;
        private readonly DispatcherTimer timer;
        private bool closed;

        // Checks can simulate foreground changes without activating any real windows.
        internal Func<bool> CanDismissOverride { get; set; }

        internal AuxiliaryWindowDismissal(Window window, Action requestClose)
        {
            this.window = window;
            this.requestClose = requestClose;
            timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += RecheckDismissal;
            window.Activated += Window_Activated;
            window.Deactivated += Window_Deactivated;
            window.Closed += Window_Closed;
        }

        private void Window_Activated(object sender, EventArgs e) => timer.Stop();

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (!closed) timer.Start();
        }

        private void RecheckDismissal(object sender, EventArgs e)
        {
            if (closed || !timer.IsEnabled) return;
            // Deactivation may precede the new foreground window. Recheck while a related or modal dialog is open.
            bool dismiss = CanDismissOverride?.Invoke() ?? (!window.IsActive && window.IsVisible && ShouldDismissForForeground(
                new WindowInteropHelper(window).Handle, NativeMethods.GetForegroundWindow()));
            if (!dismiss) return;
            timer.Stop();
            requestClose();
        }

        internal static bool ShouldDismissForForeground(IntPtr editor, IntPtr foreground)
        {
            // Do not close a disabled modal owner, or during transient focus loss on lock/remote display changes.
            return editor != IntPtr.Zero && NativeMethods.IsWindow(editor) && NativeMethods.IsWindowEnabled(editor) &&
                foreground != IntPtr.Zero && NativeMethods.IsWindow(foreground) && !NativeMethods.IsWindowOrOwnedBy(foreground, editor);
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            closed = true;
            timer.Stop();
            timer.Tick -= RecheckDismissal;
            window.Activated -= Window_Activated;
            window.Deactivated -= Window_Deactivated;
            window.Closed -= Window_Closed;
        }
    }
}
