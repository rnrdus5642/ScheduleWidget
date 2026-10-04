using System;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ScheduleWidget
{
    /// <summary>
    /// A DatePicker that UI Automation can't crash. With an automation client running (Narrator, Voice Access, other tools),
    /// WPF's DatePickerAutomationPeer.GetChildrenCore throws a NullReferenceException when the picker's drop-down is open
    /// but its template was never applied (e.g. the picker sits in a popup that has not been shown yet), and that takes the
    /// whole app down. This picker builds its template as soon as it is created, so the peer always has its parts.
    /// Use it in place of DatePicker everywhere (XAML: local:SafeDatePicker).
    /// </summary>
    public class SafeDatePicker : DatePicker
    {
        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            ApplyTemplate();
        }

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            // Made in code and not in any window yet: take its style (and so its template) now.
            if (!IsInitialized) { BeginInit(); EndInit(); }
            ApplyTemplate();
            return base.OnCreateAutomationPeer();
        }
    }
}
