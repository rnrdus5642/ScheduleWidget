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
        // ---- Right-click menu on a single schedule block (수정 / 완료 / 삭제) ----
        private ScheduleItem blockItem;

        private void Block_RightClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (RecentlyDragged) return;
            var block = sender as FrameworkElement;
            var context = block?.DataContext;
            var item = context?.GetType().GetProperty("Item")?.GetValue(context) as ScheduleItem;
            if (item == null || !data.Schedules.Contains(item)) return;
            if (CloseRightClickMenus()) return;
            CloseDayPopup();
            RangePopup.IsOpen = false;
            blockItem = item;
            BlockPopupTitle.Text = (string.IsNullOrWhiteSpace(item.Time) ? "" : item.Time + " · ") + item.Title;
            BlockPopupTitle.ToolTip = item.Title;
            BlockDone.Content = item.IsCompleted ? "미완료" : "완료";
            ResetBlockDelete();
            BlockPopup.IsOpen = false;
            BlockPopup.PlacementTarget = block;
            RemeasurePopup(BlockPopup);
            BlockPopup.IsOpen = true;
        }

        // Centered above the block, the tail pointing down at it.
        private System.Windows.Controls.Primitives.CustomPopupPlacement[] PlaceBlockPopup(Size popupSize, Size targetSize, Point offset) =>
            new[] { new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, -popupSize.Height),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal) };

        private void ResetBlockDelete()
        {
            BlockDelete.Tag = null;
            BlockDelete.Content = "삭제";
            BlockDelete.ClearValue(BackgroundProperty);
            BlockDelete.ClearValue(BorderBrushProperty);
            ShowDeleteNote(BlockDeleteNote, null);
        }

        /// <summary>
        /// What deleting a schedule linked to Google Calendar does there (null when it is not linked): an event this app made
        /// (no guests) goes from Google too; any other stays there (see GoogleCalendarSync.DeletesOnGoogle). With 구글 캘린더
        /// 연동 off nothing happens there now, but the deletion is still carried out once it is on again: that is said for an
        /// event it will delete; one that stays gets no note, like an unlinked schedule (nothing will happen on Google).
        /// Shared with the TODO window's 삭제 confirmation.
        /// </summary>
        internal static string GoogleDeleteNote(GoogleCalendarSettings settings, ScheduleItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.GoogleEventId) || settings == null) return null;
            bool deletes = GoogleCalendarSync.DeletesOnGoogle(settings, item);
            if (settings.Enabled) return deletes ? "구글 캘린더에서도 삭제됩니다." : "구글 캘린더에는 남습니다.";
            return deletes ? "구글 캘린더 연동을 다시 켜면 구글에서도 삭제됩니다." : null;
        }

        // Under a 삭제 button waiting for its second press: the Google note for that schedule (hidden when there is none).
        private void ShowDeleteNote(TextBlock note, ScheduleItem item)
        {
            if (note == null) return;
            string text = GoogleDeleteNote(data.GoogleCalendar, item);
            note.Text = text ?? string.Empty;
            note.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // Saves through `changed`, which in the app also refreshes this window (via the TODO list): refreshed here only when
        // it did not (the day bubble comes along with Refresh).
        private bool SaveAndRefresh()
        {
            int before = refreshCount;
            bool saved = changed == null || changed();
            if (refreshCount == before) Refresh();
            return saved;
        }

        private const string ScheduleSaveFailure = "저장하지 못해 변경 전 상태로 복원했습니다. 다시 시도해 주세요.";
        internal bool PersistingScheduleChange { get; private set; }
        internal event Action ScheduleChangeCompleted;

        private bool CommitScheduleChange(Func<Func<bool>, bool> change)
        {
            bool saved = false, previousKeep = keepDayPopupState, previousPersisting = PersistingScheduleChange;
            int before = refreshCount;
            keepDayPopupState = true;
            PersistingScheduleChange = true;
            try { saved = change(changed); }
            finally
            {
                try
                {
                    int beforeNotification = refreshCount;
                    ScheduleChangeCompleted?.Invoke(); // All observers see the committed or restored state, never a rejected mutation.
                    if ((!saved && refreshCount == beforeNotification) || refreshCount == before) Refresh();
                }
                finally { keepDayPopupState = previousKeep; PersistingScheduleChange = previousPersisting; }
            }
            SetScheduleSaveWarning(saved ? null : ScheduleSaveFailure);
            return saved;
        }

        private bool SaveBlockChange(Func<Func<bool>, bool> change)
        {
            bool saved = CommitScheduleChange(change);
            if (!saved)
            {
                BlockDeleteNote.Text = ScheduleSaveFailure;
                BlockDeleteNote.Visibility = Visibility.Visible;
                BlockPopup.IsOpen = true;
            }
            return saved;
        }

        private void BlockDone_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var item = blockItem;
            BlockPopup.IsOpen = false;
            if (item == null || !data.Schedules.Contains(item)) return;
            SaveBlockChange(save => ScheduleChanges.Update(item, () => item.IsCompleted = !item.IsCompleted, save));
        }

        private void BlockColor_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            BlockPopup.IsOpen = false;
            if (blockItem != null && data.Schedules.Contains(blockItem)) ScheduleColorRequested?.Invoke(blockItem);
        }

        private void BlockDefaultColor_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            BlockPopup.IsOpen = false;
            var item = blockItem;
            if (item == null || !data.Schedules.Contains(item)) return;
            SaveBlockChange(save => ScheduleChanges.Update(item, () => item.Color = null, save));
        }

        private void BlockMessage_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            BlockPopup.IsOpen = false;
            if (blockItem != null && data.Schedules.Contains(blockItem)) ScheduleMessageRequested?.Invoke(blockItem);
        }

        // First press arms ("정말 삭제", darker red), second press deletes — same as the day bubble.
        private void BlockDelete_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var item = blockItem;
            if (item == null || !data.Schedules.Contains(item)) { BlockPopup.IsOpen = false; return; }
            if (!Equals(BlockDelete.Tag, "armed"))
            {
                BlockDelete.Tag = "armed";
                BlockDelete.Content = "정말 삭제";
                BlockDelete.Background = BlockDelete.BorderBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x1C, 0x28));
                ShowDeleteNote(BlockDeleteNote, item);
                return;
            }
            BlockPopup.IsOpen = false;
            if (!SaveBlockChange(save => ScheduleChanges.Remove(data.Schedules, item, save))) return;
            if (miniEditId == item.Id)
            {
                miniEditId = null;
                MiniEditorPanel.Visibility = Visibility.Collapsed;
            }
            blockItem = null;
        }

        // Opens that day's bubble with the editor for this schedule.
        private void BlockEdit_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var item = blockItem;
            BlockPopup.IsOpen = false;
            if (item == null || !data.Schedules.Contains(item)) return;
            if (!DateTime.TryParseExact(item.Period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day)) return;
            if ((BlockPopup.PlacementTarget as FrameworkElement)?.DataContext is MonthScheduleBlock monthBlock) day = monthBlock.Date;
            if ((BlockPopup.PlacementTarget as FrameworkElement)?.DataContext is CalendarRangeBar rangeBar) day = rangeBar.Date;
            var dayButton = FindDayButton(day.Date);
            // 여러 날 begun before this page: the first day of it the page shows.
            for (int i = 0; dayButton == null && item.IsMultiDay && i < dayCells.Count; i++)
                if (item.Covers(dayCells[i].Date)) dayButton = FindDayButton(dayCells[i].Date);
            if (dayButton == null) return;
            Day_Click(dayButton, new RoutedEventArgs(Button.ClickEvent));
            EditMiniSchedule_Click(new Button { DataContext = item }, new RoutedEventArgs(Button.ClickEvent));
        }

        private void BlockPopup_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            BlockPopup.IsOpen = false;
        }

        private Button FindDayButton(DateTime day)
        {
            Button Search(DependencyObject node)
            {
                for (int i = 0; node != null && i < VisualTreeHelper.GetChildrenCount(node); i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    if (child is Button b && b.Tag is DateTime d && d.Date == day) return b;
                    var found = Search(child);
                    if (found != null) return found;
                }
                return null;
            }
            return Search(IsMonthViewOpen ? MonthDays : WeekDays);
        }

        private void CompleteMiniSchedule_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as FrameworkElement)?.DataContext as ScheduleItem;
            if (item == null || !data.Schedules.Contains(item)) return;
            string message = !item.IsCompleted ? "완료로 표시했습니다." : "완료 표시를 취소했습니다.";
            CommitDayChange(save => ScheduleChanges.Update(item, () => item.IsCompleted = !item.IsCompleted, save), message);
            e.Handled = true;
        }

        // First click arms the button ("정말 삭제"), second click deletes. The list refresh resets unconfirmed buttons.
        private void DeleteMiniSchedule_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button?.DataContext as ScheduleItem;
            if (item == null || !data.Schedules.Contains(item)) return;
            e.Handled = true;
            if (!Equals(button.Tag, "armed"))
            {
                button.Tag = "armed";
                button.Content = "정말 삭제";
                // Darker red while waiting for the confirming second click.
                button.Background = button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x1C, 0x28));
                ShowDeleteNote(DayDeleteNote, item);
                return;
            }
            if (!CommitDayChange(save => ScheduleChanges.Remove(data.Schedules, item, save), "‘" + item.Title + "’ 일정을 삭제했습니다.")) return;
            if (miniEditId == item.Id)
            {
                miniEditId = null;
                MiniEditorPanel.Visibility = Visibility.Collapsed;
            }
        }

        private bool CommitDayChange(Func<Func<bool>, bool> change, string message)
        {
            bool saved = CommitScheduleChange(change);
            bool reopened = !DayPopup.IsOpen && selectedDay.HasValue;
            if (reopened) DayPopup.IsOpen = true;
            // The save's own refresh (through the TODO list) covers the calendar and the open bubble's list; refresh here
            // when it did not run, and the list when the bubble was closed meanwhile.
            if (reopened) RefreshDaySchedules();
            MiniEditorMessage.Text = saved ? message : ScheduleSaveFailure;
            return saved;
        }

        private void RefreshDaySchedules()
        {
            if (!selectedDay.HasValue || DaySchedules == null) return;
            DateTime day = selectedDay.Value.Date;
            string period = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            // The day's own schedules and the 여러 날 ones covering it (their rows show the range: "10.08 (목) ~ 10.10 (토)").
            var tasks = DayTasks(data.Schedules.Where(s => s.Period == period && !s.IsMultiDay), data.Schedules.Where(s => s.IsMultiDay), day);
            string holidayName = KoreanHolidays.NameOf(day);
            DayPopupTitle.Text = day.ToString("M월 d일 (ddd)", CultureInfo.GetCultureInfo("ko-KR")) + (holidayName != null ? " · " + holidayName : "") + " · " + tasks.Count + "개 일정";
            DaySchedules.ItemsSource = tasks;
            MiniScheduleEmpty.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowDeleteNote(DayDeleteNote, null); // the rebuilt rows start unarmed
        }

        private void ResetMiniEditor(DateTime date)
        {
            miniEditId = null;
            miniDateInvalid = false;
            MiniTitleInput.Text = string.Empty;
            MiniDateInput.SelectedDate = date.Date;
            MiniTimeToggle.IsChecked = false;
            MiniTimeInput.Text = "09:00";
            MiniTimeInput.IsEnabled = false;
            SetMiniRange(null);
            MiniEditorMessage.Text = string.Empty;
        }

        // ---- 여러 날 (10/8 ~ 10/10) in the bubble's editor ----
        private bool miniEndInvalid;

        // Shows a range's last day (여러 날 on, 종료 날짜 open), or none (one day).
        private void SetMiniRange(DateTime? end)
        {
            miniEndInvalid = false;
            MiniEndDateInput.SelectedDate = end;
            MiniRangeToggle.IsChecked = end.HasValue;
            MiniEndPanel.Visibility = end.HasValue ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MiniRangeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (MiniEndPanel == null || MiniEndDateInput == null) return;
            bool on = MiniRangeToggle.IsChecked == true;
            MiniEndPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            // Turned on: the day after the start, to change from there.
            if (on && !MiniEndDateInput.SelectedDate.HasValue && MiniDateInput.SelectedDate.HasValue && MiniDateInput.SelectedDate.Value < DateTime.MaxValue.Date)
                MiniEndDateInput.SelectedDate = MiniDateInput.SelectedDate.Value.Date.AddDays(1);
            if (!on) { miniEndInvalid = false; MiniEndDateInput.IsDropDownOpen = false; }
        }

        private void MiniEndDateInput_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MiniEndDateInput.SelectedDate.HasValue) miniEndInvalid = false;
        }

        private void MiniEndDateInput_EditStarted(object sender, TextCompositionEventArgs e)
        {
            if (miniEndInvalid) MiniEditorMessage.Text = string.Empty;
            miniEndInvalid = false;
        }

        private void MiniEndDateInput_DateValidationError(object sender, DatePickerDateValidationErrorEventArgs e)
        {
            e.ThrowException = false;
            miniEndInvalid = true;
            MiniEditorMessage.Text = "유효한 종료 날짜를 입력해 주세요.";
        }

        /// <summary>
        /// The EndPeriod the bubble's editor saves for <paramref name="start"/>: null with 여러 날 off; false with the message
        /// (shown in the editor) when the 종료 날짜 is missing, not a date, before the start or too far.
        /// </summary>
        private bool TryGetMiniEnd(DateTime start, out string endPeriod, out string error)
        {
            endPeriod = null;
            error = null;
            if (MiniRangeToggle.IsChecked != true) return true;
            var editor = MiniEndDateInput.Template?.FindName("PART_TextBox", MiniEndDateInput) as TextBox;
            string text = editor?.Text ?? MiniEndDateInput.Text;
            if (!miniEndInvalid && !string.IsNullOrWhiteSpace(text)) MiniEndDateInput.Text = text; // commit typed text (DatePicker's own parsing)
            if (miniEndInvalid || string.IsNullOrWhiteSpace(text) || !MiniEndDateInput.SelectedDate.HasValue)
            {
                error = string.IsNullOrWhiteSpace(text) ? "종료 날짜를 입력해 주세요." : "유효한 종료 날짜를 입력해 주세요.";
                return false;
            }
            return ScheduleItem.TryRange(start, MiniEndDateInput.SelectedDate.Value, out endPeriod, out error);
        }

        private void CloseDayPopup()
        {
            reopenDayAfterLowering = false;
            if (MiniDateInput != null) MiniDateInput.IsDropDownOpen = false;
            if (MiniEndDateInput != null) MiniEndDateInput.IsDropDownOpen = false;
            if (DayPopup != null)
            {
                DayPopup.StaysOpen = false;
                DayPopup.IsOpen = false;
            }
            miniEditId = null;
            selectedDay = null;
            miniDateInvalid = false;
            if (MiniEditorPanel != null) MiniEditorPanel.Visibility = Visibility.Collapsed;
        }

        private bool reopenDayAfterLowering;

        internal void DismissPopupsForLowering()
        {
            // Closing the popup normally clears the editor; preserve it across a shortcut toggle.
            reopenDayAfterLowering = DayPopup.IsOpen;
            keepDayPopupState = true;
            try { DayPopup.IsOpen = false; }
            finally { keepDayPopupState = false; }
            MiniDateInput.IsDropDownOpen = MiniEndDateInput.IsDropDownOpen = false;
            PlaylistPopup.IsOpen = VolumePopup.IsOpen = RangePopup.IsOpen = QueuePopup.IsOpen = BlockPopup.IsOpen = false;
            CloseUpcomingPreview();
        }

        internal void RestorePopupAfterRaising()
        {
            if (!reopenDayAfterLowering || !selectedDay.HasValue) return;
            reopenDayAfterLowering = false;
            RefreshDaySchedules();
            RemeasurePopup(DayPopup);
            DayPopup.IsOpen = true;
        }

        private bool TryGetMiniDate(out DateTime date)
        {
            date = default(DateTime);
            if (miniDateInvalid) return false;
            var editor = MiniDateInput.Template?.FindName("PART_TextBox", MiniDateInput) as TextBox;
            string text = editor?.Text ?? MiniDateInput.Text;
            if (string.IsNullOrWhiteSpace(text)) return false;
            // Commit pending keyboard input using DatePicker's own culture and validation.
            MiniDateInput.Text = text;
            if (miniDateInvalid || !MiniDateInput.SelectedDate.HasValue) return false;
            date = MiniDateInput.SelectedDate.Value.Date;
            return true;
        }

        private void MiniDateInput_EditStarted(object sender, TextCompositionEventArgs e) => ClearMiniDateError();
        private void MiniDateInput_Pasting(object sender, DataObjectPastingEventArgs e) => ClearMiniDateError();
        private void MiniDateInput_EditKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Back || e.Key == Key.Delete || e.Key == Key.X && Keyboard.Modifiers == ModifierKeys.Control)
                ClearMiniDateError();
        }
        private void ClearMiniDateError()
        {
            if (miniDateInvalid) MiniEditorMessage.Text = string.Empty;
            miniDateInvalid = false;
        }

        private void Day_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || !(button.Tag is DateTime) || RecentlyDragged) return;
            e.Handled = true;
            DateTime day = ((DateTime)button.Tag).Date;
            // Clicking the same day again while its bubble shows (or the press that just closed it) only closes it.
            bool sameDay = dayPopupDay == day;
            if (sameDay && DayPopup.IsOpen) { CloseDayPopup(); return; }
            if (sameDay && popupClosedAt.TryGetValue(DayPopup, out DateTime closedAt) && (DateTime.Now - closedAt).TotalMilliseconds < 300) return;
            PrepareDayDetails(day);
            OpenDayPopupAt(button);
            e.Handled = true;
        }

        private void PrepareDayDetails(DateTime day)
        {
            dayPopupDay = selectedDay = day.Date;
            ResetMiniEditor(day.Date);
            MiniEditorPanel.Visibility = Visibility.Collapsed;
            RefreshDaySchedules();
        }

        private void OpenDayPopupAt(FrameworkElement anchor)
        {
            DayPopup.PlacementTarget = WeekCalendar;
            double center = anchor.TranslatePoint(new Point(anchor.ActualWidth / 2, 0), WeekCalendar).X;
            DayPopup.HorizontalOffset = Math.Max(0, Math.Min(center - DayBubble.Width / 2, WeekCalendar.ActualWidth - DayBubble.Width));
            DayBubbleTail.Margin = new Thickness(Math.Max(16, Math.Min(center - DayPopup.HorizontalOffset - 12, DayBubble.Width - 40)), 0, 0, 0);
            DayPopup.StaysOpen = false;
            RemeasurePopup(DayPopup);
            DayPopup.IsOpen = true;
            CloseDayBubble.Focus();
        }

        private DateTime? dayPopupDay; // day of the bubble last opened (kept after it closes, for the toggle above)

        private void CloseDayPopup_Click(object sender, RoutedEventArgs e) { CloseDayPopup(); e.Handled = true; }

        private void DayPopup_Opened(object sender, EventArgs e)
        {
            bool below = DayBubble.PointToScreen(new Point()).Y >= WeekCalendar.PointToScreen(new Point()).Y;
            DayBubbleFace.Margin = below ? new Thickness(0, 10, 0, 0) : new Thickness(0, 0, 0, 10);
            DayBubbleTail.VerticalAlignment = below ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            DayBubbleTail.Data = Geometry.Parse(below ? "M0,11 L12,0 L24,11" : "M0,0 L12,11 L24,0");
        }

        private void DayBubble_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (DayPopup.IsOpen) Dispatcher.BeginInvoke(new Action(() =>
            {
                if (DayPopup.IsOpen) DayPopup_Opened(null, EventArgs.Empty);
            }));
        }

        private void EditMiniSchedule_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as FrameworkElement)?.DataContext as ScheduleItem;
            if (item == null) return;
            MiniEditorPanel.Visibility = Visibility.Visible;
            miniEditId = item.Id;
            // The schedule's own date; the bubble's day (or today) when it has none that reads as a date.
            DateTime date = selectedDay ?? DateTime.Today;
            if (DateTime.TryParseExact(item.Period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exact)) date = exact;
            else if (DateTime.TryParse(item.Period, out DateTime parsed)) date = parsed;
            MiniTitleInput.Text = item.Title ?? string.Empty;
            MiniDateInput.SelectedDate = date.Date;
            if (!string.IsNullOrWhiteSpace(item.Time) && FeatureRules.TryScheduleTime(item.Time, out string normalizedTime))
            {
                MiniTimeToggle.IsChecked = true;
                MiniTimeInput.Text = normalizedTime;
                MiniTimeInput.IsEnabled = true;
            }
            else
            {
                MiniTimeToggle.IsChecked = false;
                MiniTimeInput.Text = "09:00";
                MiniTimeInput.IsEnabled = false;
            }
            SetMiniRange(item.EndDate); // 여러 날: its last day
            miniDateInvalid = false;
            MiniEditorMessage.Text = string.Empty;
            MiniTitleInput.Focus();
            MiniTitleInput.SelectAll();
            e.Handled = true;
        }

        private void NewMiniSchedule_Click(object sender, RoutedEventArgs e)
        {
            ResetMiniEditor(selectedDay ?? DateTime.Today);
            MiniEditorPanel.Visibility = Visibility.Visible;
            MiniTitleInput.Focus();
            e.Handled = true;
        }

        // Cancel discards the input and folds the editor away; the day's bubble stays open.
        private void CancelMiniSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (MiniDateInput.IsDropDownOpen) MiniDateInput.IsDropDownOpen = false;
            if (MiniEndDateInput.IsDropDownOpen) MiniEndDateInput.IsDropDownOpen = false;
            ResetMiniEditor(selectedDay ?? DateTime.Today);
            MiniEditorPanel.Visibility = Visibility.Collapsed;
            RefreshDaySchedules();
            CloseDayBubble.Focus();
            e.Handled = true;
        }

        // The bubble is a Popup: its own HWND, while Win32 focus stays on the mini window. The IME then has no
        // composition context for the text box, so Korean arrives as separate jamo (ㅎㅏㄴ) or raw letters.
        // Moving Win32 focus to the popup's HWND lets the IME compose syllables normally.
        private void DayBubble_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!(e.NewFocus is TextBox box)) return;
            var source = PresentationSource.FromVisual(DayBubble) as HwndSource;
            if (source == null || source.Handle == IntPtr.Zero || NativeMethods.GetFocus() == source.Handle) return;
            // Run after WPF finishes this focus change; doing it inside the event leaves the IME detached.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (source.IsDisposed || !DayPopup.IsOpen || NativeMethods.GetFocus() == source.Handle) return;
                NativeMethods.SetFocus(source.Handle);
                // Re-enter the box so WPF binds the IME to the popup's HWND (a no-op Focus() would skip that).
                int caret = box.CaretIndex;
                Keyboard.ClearFocus();
                box.Focus();
                box.CaretIndex = caret;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private readonly EnterKeySubmit miniScheduleEnter = new EnterKeySubmit();

        private void MiniScheduleInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Guid? edit = miniEditId;
            DateTime? day = selectedDay;
            miniScheduleEnter.Handle(e, () =>
            {
                if (closed || MiniEditorPanel.Visibility != Visibility.Visible || miniEditId != edit || selectedDay != day ||
                    string.IsNullOrWhiteSpace(MiniTitleInput.Text)) return;
                SaveMiniSchedule_Click(sender, new RoutedEventArgs(Button.ClickEvent));
            });
        }

        private void SaveMiniSchedule_Click(object sender, RoutedEventArgs e)
        {
            string title = MiniTitleInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                MiniEditorMessage.Text = "제목을 입력해 주세요.";
                MiniTitleInput.Focus();
                return;
            }
            if (!TryGetMiniDate(out DateTime date))
            {
                MiniEditorMessage.Text = "유효한 날짜를 입력해 주세요.";
                MiniDateInput.Focus();
                return;
            }
            string time = null;
            if (MiniTimeToggle.IsChecked == true && !FeatureRules.TryScheduleTime(MiniTimeInput.Text, out time))
            {
                MiniEditorMessage.Text = "시간을 00:00~23:59 형식으로 입력해 주세요.";
                MiniTimeInput.Focus();
                return;
            }
            if (!TryGetMiniEnd(date, out string endPeriod, out string endError))
            {
                MiniEditorMessage.Text = endError;
                MiniEndDateInput.Focus();
                return;
            }

            ScheduleItem item = miniEditId.HasValue ? data.Schedules.FirstOrDefault(s => s.Id == miniEditId.Value) : null;
            bool adding = item == null;
            Guid? previousEditId = miniEditId;
            if (item == null)
            {
                if (miniEditId.HasValue)
                {
                    MiniEditorMessage.Text = "수정할 일정을 찾을 수 없습니다.";
                    return;
                }
                item = new ScheduleItem { Id = Guid.NewGuid() };
            }
            Action apply = () =>
            {
                item.Title = title;
                item.Period = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                item.EndPeriod = endPeriod;
                item.Time = time;
            };
            bool saved = CommitScheduleChange(save =>
            {
                bool committed = false;
                try
                {
                    if (adding)
                    {
                        apply(); miniEditId = item.Id;
                        committed = ScheduleChanges.Add(data.Schedules, item, save);
                    }
                    else committed = ScheduleChanges.Update(item, apply, save);
                    return committed;
                }
                finally { if (!committed) miniEditId = previousEditId; }
            });
            if (!saved)
            {
                MiniEditorMessage.Text = "저장하지 못했습니다. 변경 내용을 유지했으니 다시 저장해 주세요.";
                RemeasurePopup(DayPopup);
                DayPopup.IsOpen = true;
                return;
            }

            DateTime listDay = selectedDay ?? date;
            ResetMiniEditor(listDay);
            MiniEditorMessage.Text = date.ToString("M월 d일", CultureInfo.GetCultureInfo("ko-KR")) +
                (item.EndDate.HasValue ? "~" + item.EndDate.Value.ToString("M월 d일", CultureInfo.GetCultureInfo("ko-KR")) : "") + "에 저장했습니다.";
            RefreshDaySchedules();
            MiniEditorPanel.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        private void MiniTimeToggle_Changed(object sender, RoutedEventArgs e)
        {
            MiniTimeInput.IsEnabled = MiniTimeToggle.IsChecked == true;
            if (MiniTimeInput.IsEnabled && string.IsNullOrWhiteSpace(MiniTimeInput.Text)) MiniTimeInput.Text = "09:00";
        }

        private void DayPopup_Closed(object sender, EventArgs e)
        {
            if (keepDayPopupState)
            {
                DayPopup.StaysOpen = false;
                return;
            }
            miniEditId = null;
            selectedDay = null;
            miniDateInvalid = false;
            MiniEditorPanel.Visibility = Visibility.Collapsed;
            DayPopup.StaysOpen = false;
        }

        private void MiniDateInput_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MiniDateInput.SelectedDate.HasValue)
                miniDateInvalid = false;
        }

        private void MiniDateInput_DateValidationError(object sender, DatePickerDateValidationErrorEventArgs e)
        {
            e.ThrowException = false;
            miniDateInvalid = true;
            MiniEditorMessage.Text = "유효한 날짜를 입력해 주세요.";
        }

        private void DayPopup_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (MiniDateInput.IsDropDownOpen) MiniDateInput.IsDropDownOpen = false;
            else if (MiniEndDateInput.IsDropDownOpen) MiniEndDateInput.IsDropDownOpen = false;
            else DayPopup.IsOpen = false;
        }

        private void MiniDateInput_CalendarOpened(object sender, RoutedEventArgs e)
        {
            ClearMiniDateError();
            DayPopup.StaysOpen = true;
        }

        private void MiniDateInput_CalendarClosed(object sender, RoutedEventArgs e)
        {
            DayPopup.StaysOpen = false;
        }

    }
}
