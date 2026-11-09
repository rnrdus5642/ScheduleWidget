using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using FormsScreen = System.Windows.Forms.Screen;

namespace ScheduleWidget
{
    public partial class MainWindow
    {
        private Guid? _inlineEditId;
        private Guid? _pendingRemovalId;
        private bool _inlineEditDateSelectorsInitialized;
        private bool _inlineEditLoading;

        private readonly EnterKeySubmit addScheduleEnter = new EnterKeySubmit();
        private readonly EnterKeySubmit editScheduleEnter = new EnterKeySubmit();

        private void AddScheduleInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            addScheduleEnter.Handle(e, () =>
            {
                if (!closingApp && !_dataUnavailable && appData != null)
                    AddButton_Click(sender, new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            });
        }

        private void EditScheduleInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Guid? edit = _inlineEditId;
            editScheduleEnter.Handle(e, () =>
            {
                if (!closingApp && !_dataUnavailable && appData != null && edit.HasValue && _inlineEditId == edit &&
                    InlineEditPanel.Visibility == Visibility.Visible)
                    InlineEditSaveButton_Click(sender, new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            });
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TitleInput.Text)) return;

            DateTime selectedDate;
            if (!TryGetSelectedDate(out selectedDate))
            {
                System.Windows.MessageBox.Show(
                    this,
                    "유효한 날짜를 입력해 주세요.",
                    "날짜 확인",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string normalizedTime = null;
            if (AddTimeToggle.IsChecked == true &&
                !FeatureRules.TryScheduleTime(AddTimeInput.Text, out normalizedTime))
            {
                System.Windows.MessageBox.Show(
                    this,
                    "유효한 시간을 입력해 주세요. (HH:mm)",
                    "시간 확인",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!TryRangeEnd(AddRangeToggle, AddEndPicker, selectedDate, out string endPeriod))
                return;

            string dateStr = selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var added = new ScheduleItem
            {
                Title = TitleInput.Text.Trim(),
                Period = dateStr,
                EndPeriod = endPeriod,
                Time = normalizedTime
            };
            if (!ScheduleChanges.Add(appData.Schedules, added, () => SaveDataSafely(false)))
            {
                AddSaveError.Text = "저장하지 못했습니다. 입력 내용은 유지되니 다시 시도해 주세요.";
                AddSaveError.Visibility = Visibility.Visible;
                return;
            }
            AddSaveError.Visibility = Visibility.Collapsed;
            RefreshScheduleList();

            TitleInput.Text = "";
            ResetDateToToday();
            AddTimeToggle.IsChecked = false;
            AddTimeInput.Text = "09:00";
            AddTimeInput.IsEnabled = false;
            AddRangeToggle.IsChecked = false;
            AddEndPicker.SelectedDate = null;

            TitleInput.Focus();
        }

        private void AddTimeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (AddTimeToggle == null || AddTimeInput == null) return;
            AddTimeInput.IsEnabled = AddTimeToggle.IsChecked == true;
        }

        // ---- 여러 날 (10/8 ~ 10/10): the add form's and the edit panel's 종료 날짜 ----

        private void AddRangeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (AddRangeToggle == null || AddEndRow == null) return;
            bool on = AddRangeToggle.IsChecked == true;
            AddEndRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            // Turned on: the day after the chosen start, to change from there.
            if (on && !AddEndPicker.SelectedDate.HasValue && TryGetSelectedDate(out DateTime start) && start < DateTime.MaxValue.Date)
            {
                AddEndPicker.SelectedDate = start.AddDays(1);
                AddEndPicker.DisplayDate = start.AddDays(1);
            }
            ShowEndText(AddEndText, AddEndPicker.SelectedDate);
        }

        private void AddEndPicker_SelectedDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
            ShowEndText(AddEndText, AddEndPicker.SelectedDate);

        private void InlineEditRangeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (InlineEditRangeToggle == null || InlineEditEndBox == null) return;
            bool on = InlineEditRangeToggle.IsChecked == true;
            InlineEditEndBox.IsEnabled = on;
            if (on && !_inlineEditLoading && !InlineEditEndPicker.SelectedDate.HasValue && TryGetInlineEditDate(out DateTime start) && start < DateTime.MaxValue.Date)
            {
                InlineEditEndPicker.SelectedDate = start.AddDays(1);
                InlineEditEndPicker.DisplayDate = start.AddDays(1);
            }
            ShowEndText(InlineEditEndText, on ? InlineEditEndPicker.SelectedDate : null);
        }

        private void InlineEditEndPicker_SelectedDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
            ShowEndText(InlineEditEndText, InlineEditRangeToggle.IsChecked == true ? InlineEditEndPicker.SelectedDate : null);

        private static void ShowEndText(System.Windows.Controls.TextBlock text, DateTime? end)
        {
            if (text == null) return;
            text.Text = end.HasValue ? end.Value.ToString("yyyy. M. d. (ddd)", CultureInfo.GetCultureInfo("ko-KR")) + "까지" : "종료 날짜";
        }

        /// <summary>
        /// The EndPeriod a form saves: null with 여러 날 off (or the same day picked), the range's last day otherwise; false
        /// (after saying why) when the 종료 날짜 is missing, before the start or too far.
        /// </summary>
        private bool TryRangeEnd(System.Windows.Controls.CheckBox toggle, System.Windows.Controls.DatePicker picker, DateTime start, out string endPeriod)
        {
            endPeriod = null;
            if (toggle.IsChecked != true) return true;
            string error;
            if (!picker.SelectedDate.HasValue) error = "종료 날짜를 선택해 주세요.";
            else if (ScheduleItem.TryRange(start, picker.SelectedDate.Value, out endPeriod, out error)) return true;
            if (rangeMessageOverride != null) rangeMessageOverride(error);
            else System.Windows.MessageBox.Show(this, error, "날짜 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // Checks only: receives the 종료 날짜 message instead of a message box.
        internal static Action<string> rangeMessageOverride = null;

        private bool _syncingAddDate; // the combos and the calendar are updating each other

        private void CalendarPicker_SelectedDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_syncingAddDate || !CalendarPicker.SelectedDate.HasValue) return;
            DateTime selected = CalendarPicker.SelectedDate.Value;
            _syncingAddDate = true;
            try
            {
                SetYearSelection(selected.Year);

                MonthCombo.SelectedItem = selected.Month;

                UpdateDays(selected.Year, selected.Month);
                DayCombo.SelectedItem = selected.Day;
            }
            finally { _syncingAddDate = false; }
        }

        // A date picked in the combos: the calendar button opens on that date too.
        private void SyncCalendarPickerToCombos()
        {
            if (_syncingAddDate || !TryGetSelectedDate(out DateTime date) || CalendarPicker.SelectedDate == date) return;
            _syncingAddDate = true;
            try
            {
                CalendarPicker.SelectedDate = date;
                CalendarPicker.DisplayDate = date;
            }
            finally { _syncingAddDate = false; }
        }

        private void ResetDateToToday()
        {
            DateTime today = DateTime.Today;

            SetYearSelection(today.Year);
            MonthCombo.SelectedItem = today.Month;

            UpdateDays(today.Year, today.Month);
            DayCombo.SelectedItem = today.Day;

            CalendarPicker.SelectedDate = today;
            _addFormDate = today;
        }

        private void EditSchedule_Click(object sender, RoutedEventArgs e)
        {
            ScheduleItem item = GetScheduleItemFromContextMenu(sender);
            if (item != null)
            {
                int index = appData.Schedules.FindIndex(s => s.Id == item.Id);
                if (index < 0) return;

                OpenInlineEdit(item);
            }
        }

        private void OpenInlineEdit(ScheduleItem item)
        {
            if (item == null)
                return;
            InlineEditSaveError.Visibility = Visibility.Collapsed;

            // Settings open in this window give way; settings in their own window (from the mini window) stay open.
            if (InlineSettingsPanel.Visibility == Visibility.Visible && settingsHost == null)
                CloseInlineSettings(false);

            if (RemoveConfirmPanel.Visibility == Visibility.Visible)
                CloseRemoveConfirmation();

            if (!_inlineEditDateSelectorsInitialized)
                InitInlineEditDateSelectors();

            DateTime date;
            if (!DateTime.TryParseExact(
                    item.Period,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out date))
            {
                date = DateTime.Today;
            }

            _inlineEditId = item.Id;
            _inlineEditLoading = true;
            InlineEditTitleInput.Text = item.Title ?? string.Empty;
            SetInlineEditYear(date.Year);
            InlineEditMonthCombo.SelectedItem = date.Month;
            UpdateInlineEditDays(date.Year, date.Month);
            InlineEditDayCombo.SelectedItem = date.Day;
            InlineEditTimeToggle.IsChecked = item.Time != null;
            InlineEditTimeInput.Text = item.Time ?? "09:00";
            InlineEditTimeInput.IsEnabled = item.Time != null;
            // 여러 날: its last day; a one-day schedule starts with the toggle off (and the day after as the picker's default).
            InlineEditEndPicker.SelectedDate = item.EndDate;
            InlineEditEndPicker.DisplayDate = item.EndDate ?? (date < DateTime.MaxValue.Date ? date.AddDays(1) : date);
            InlineEditRangeToggle.IsChecked = item.IsMultiDay;
            InlineEditEndBox.IsEnabled = item.IsMultiDay;
            ShowEndText(InlineEditEndText, item.EndDate);
            _inlineEditLoading = false;

            InlineEditPanel.Visibility = Visibility.Visible;
            InlineEditTitleInput.Focus();
            InlineEditTitleInput.SelectAll();
        }

        private void InlineEditTimeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (InlineEditTimeToggle == null || InlineEditTimeInput == null) return;
            InlineEditTimeInput.IsEnabled = InlineEditTimeToggle.IsChecked == true;
        }

        private void InitInlineEditDateSelectors()
        {
            DateTime today = DateTime.Today;

            for (int year = today.Year - 5; year <= today.Year + 5; year++)
                InlineEditYearCombo.Items.Add(year);
            for (int month = 1; month <= 12; month++)
                InlineEditMonthCombo.Items.Add(month);

            _inlineEditLoading = true;
            SetInlineEditYear(today.Year);
            InlineEditMonthCombo.SelectedItem = today.Month;
            UpdateInlineEditDays(today.Year, today.Month);
            InlineEditDayCombo.SelectedItem = today.Day;
            _inlineEditLoading = false;

            InlineEditYearCombo.SelectionChanged += (s, e) => UpdateInlineEditDaysForCurrentSelection();
            InlineEditYearCombo.LostFocus += (s, e) => NormalizeInlineEditYear();
            InlineEditMonthCombo.SelectionChanged += (s, e) => UpdateInlineEditDaysForCurrentSelection();

            _inlineEditDateSelectorsInitialized = true;
        }

        private void UpdateInlineEditDays(int year, int month)
        {
            int previousDay = InlineEditDayCombo.SelectedItem is int selectedDay ? selectedDay : 1;
            int daysInMonth;
            try
            {
                daysInMonth = DateTime.DaysInMonth(year, month);
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }

            InlineEditDayCombo.Items.Clear();
            for (int dayIndex = 1; dayIndex <= daysInMonth; dayIndex++)
                InlineEditDayCombo.Items.Add(dayIndex);

            InlineEditDayCombo.SelectedItem = Math.Min(previousDay, daysInMonth);
        }

        private void SetInlineEditYear(int year)
        {
            if (!InlineEditYearCombo.Items.Contains(year))
                InlineEditYearCombo.Items.Add(year);

            InlineEditYearCombo.SelectedItem = year;
            InlineEditYearCombo.Text = year.ToString(CultureInfo.InvariantCulture);
        }

        private void NormalizeInlineEditYear()
        {
            int year;
            if (TryGetInlineEditYear(out year))
                SetInlineEditYear(year);

            UpdateInlineEditDaysForCurrentSelection();
        }

        private void UpdateInlineEditDaysForCurrentSelection()
        {
            if (_inlineEditLoading)
                return;

            int year;
            if (TryGetInlineEditYear(out year) && InlineEditMonthCombo.SelectedItem is int month)
                UpdateInlineEditDays(year, month);
        }

        private bool TryGetInlineEditYear(out int year)
        {
            string yearText = InlineEditYearCombo.Text;
            if (string.IsNullOrWhiteSpace(yearText) && InlineEditYearCombo.SelectedItem != null)
                yearText = InlineEditYearCombo.SelectedItem.ToString();

            return int.TryParse(
                       yearText,
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out year)
                   && year >= DateTime.MinValue.Year
                   && year <= DateTime.MaxValue.Year;
        }

        private bool TryGetInlineEditDate(out DateTime selectedDate)
        {
            selectedDate = default(DateTime);

            int year;
            if (!TryGetInlineEditYear(out year) ||
                !(InlineEditMonthCombo.SelectedItem is int month) ||
                !(InlineEditDayCombo.SelectedItem is int day))
                return false;

            try
            {
                selectedDate = new DateTime(year, month, day);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        private void InlineEditSaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_inlineEditId.HasValue)
                return;

            string title = InlineEditTitleInput.Text == null
                ? string.Empty
                : InlineEditTitleInput.Text.Trim();
            if (title.Length == 0)
                return;

            DateTime selectedDate;
            if (!TryGetInlineEditDate(out selectedDate))
            {
                System.Windows.MessageBox.Show(
                    this,
                    "유효한 날짜를 입력해 주세요.",
                    "날짜 확인",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string normalizedTime = null;
            if (InlineEditTimeToggle.IsChecked == true &&
                !FeatureRules.TryScheduleTime(InlineEditTimeInput.Text, out normalizedTime))
            {
                System.Windows.MessageBox.Show(
                    this,
                    "유효한 시간을 입력해 주세요. (HH:mm)",
                    "시간 확인",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!TryRangeEnd(InlineEditRangeToggle, InlineEditEndPicker, selectedDate, out string endPeriod))
                return;

            ScheduleItem item = appData.Schedules.Find(s => s.Id == _inlineEditId.Value);
            if (item == null)
            {
                CloseInlineEdit();
                return;
            }

            if (!ScheduleChanges.Update(item, () =>
            {
                item.Title = title;
                item.Period = selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                item.EndPeriod = endPeriod;
                item.Time = normalizedTime;
            }, () => SaveDataSafely(false)))
            {
                InlineEditSaveError.Visibility = Visibility.Visible;
                return;
            }
            InlineEditSaveError.Visibility = Visibility.Collapsed;
            RefreshScheduleList();
            CloseInlineEdit();
        }

        private void InlineEditCancelButton_Click(object sender, RoutedEventArgs e)
        {
            CloseInlineEdit();
        }

        private void InlineEditCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseInlineEdit();
        }

        private void CloseInlineEdit()
        {
            InlineEditPanel.Visibility = Visibility.Collapsed;
            _inlineEditId = null;
        }

        private void RemoveSchedule_Click(object sender, RoutedEventArgs e)
        {
            ScheduleItem item = GetScheduleItemFromContextMenu(sender);
            if (item != null)
            {
                int index = appData.Schedules.FindIndex(s => s.Id == item.Id);
                if (index < 0) return;

                _pendingRemovalId = item.Id;
                RemoveConfirmMessage.Text = RemoveConfirmText(item);
                RemoveConfirmPanel.Visibility = Visibility.Visible;
            }
        }

        // A card linked to Google Calendar says what deleting it does there, for that event: one this app made (no guests)
        // goes from Google too; one made in Google (or with other people invited) stays there. Same words as the mini window.
        private string RemoveConfirmText(ScheduleItem item)
        {
            string text = $"‘{item.Title}’ 일정을 삭제할까요?";
            string google = MiniWindow.GoogleDeleteNote(appData?.GoogleCalendar, item);
            if (google != null) text += Environment.NewLine + google;
            return text;
        }

        private void RemoveConfirmApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_pendingRemovalId.HasValue)
            {
                CloseRemoveConfirmation();
                return;
            }

            int index = appData.Schedules.FindIndex(s => s.Id == _pendingRemovalId.Value);
            if (index >= 0)
            {
                if (!ScheduleChanges.Remove(appData.Schedules, appData.Schedules[index], () => SaveDataSafely(false)))
                {
                    RemoveConfirmMessage.Text = "저장하지 못해 일정을 삭제하지 않았습니다. 다시 시도해 주세요.";
                    return;
                }
                RefreshScheduleList();
            }

            CloseRemoveConfirmation();
        }

        private void RemoveConfirmCancelButton_Click(object sender, RoutedEventArgs e)
        {
            CloseRemoveConfirmation();
        }

        private void CloseRemoveConfirmation()
        {
            RemoveConfirmPanel.Visibility = Visibility.Collapsed;
            _pendingRemovalId = null;
        }

        private static ScheduleItem GetScheduleItemFromContextMenu(object sender)
        {
            var menuItem = sender as System.Windows.Controls.MenuItem;
            var contextMenu = menuItem?.Parent as System.Windows.Controls.ContextMenu;
            var placementTarget = contextMenu?.PlacementTarget as FrameworkElement;

            if (placementTarget?.DataContext is ScheduleItem item)
                return item;

            // 명시적인 ContextMenu 바인딩이 적용되지 않는 상황에서도 기존 동작을 유지합니다.
            return menuItem?.DataContext as ScheduleItem;
        }

        private void InitDateSelectors()
        {
            DateTime today = DateTime.Today;
            for (int y = today.Year - 5; y <= today.Year + 5; y++) YearCombo.Items.Add(y);
            for (int m = 1; m <= 12; m++) MonthCombo.Items.Add(m);

            YearCombo.SelectedItem = today.Year;
            MonthCombo.SelectedItem = today.Month;
            UpdateDays(today.Year, today.Month);
            DayCombo.SelectedItem = today.Day;
            CalendarPicker.SelectedDate = today;
            _addFormDate = today;

            YearCombo.SelectionChanged += (s, e) => UpdateDaysForCurrentSelection();
            YearCombo.LostFocus += (s, e) => NormalizeYearInput();
            MonthCombo.SelectionChanged += (s, e) => UpdateDaysForCurrentSelection();
            DayCombo.SelectionChanged += (s, e) => SyncCalendarPickerToCombos();
        }

        // Another month (or year) keeps the chosen day, as far as the month has it (31 → 30 in April), like the edit form.
        private void UpdateDays(int year, int month)
        {
            int previousDay = DayCombo.SelectedItem is int selectedDay ? selectedDay : 1;
            DayCombo.Items.Clear();
            int days = DateTime.DaysInMonth(year, month);
            for (int d = 1; d <= days; d++) DayCombo.Items.Add(d);
            DayCombo.SelectedItem = Math.Min(previousDay, days);
        }

        private void SetYearSelection(int year)
        {
            if (!YearCombo.Items.Contains(year))
                YearCombo.Items.Add(year);

            YearCombo.SelectedItem = year;
            YearCombo.Text = year.ToString(CultureInfo.InvariantCulture);
        }

        private void NormalizeYearInput()
        {
            int year;
            if (TryGetSelectedYear(out year))
                SetYearSelection(year);

            UpdateDaysForCurrentSelection();
        }

        private void UpdateDaysForCurrentSelection()
        {
            int year;
            if (TryGetSelectedYear(out year) && MonthCombo.SelectedItem is int month)
                UpdateDays(year, month);
        }

        private bool TryGetSelectedYear(out int year)
        {
            string yearText = YearCombo.Text;
            if (string.IsNullOrWhiteSpace(yearText) && YearCombo.SelectedItem != null)
                yearText = YearCombo.SelectedItem.ToString();

            return int.TryParse(
                       yearText,
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out year)
                   && year >= DateTime.MinValue.Year
                   && year <= DateTime.MaxValue.Year;
        }

        private bool TryGetSelectedDate(out DateTime selectedDate)
        {
            selectedDate = default(DateTime);

            int year;
            if (!TryGetSelectedYear(out year) ||
                !(MonthCombo.SelectedItem is int month) ||
                !(DayCombo.SelectedItem is int day))
                return false;

            try
            {
                selectedDate = new DateTime(year, month, day);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

    }
}
