using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        public bool IsAllSchedulesOpen => AllSchedulesPanel.Visibility == Visibility.Visible;
        private string allSchedulesSignature;
        private List<AllScheduleRow> allScheduleRows = new List<AllScheduleRow>();

        public void SetAllSchedulesOpen(bool open)
        {
            if (closed || companion) return;
            if (open == IsAllSchedulesOpen) { if (open) RefreshAllSchedules(); return; }
            FinishWeekFlip();
            CloseUpcomingPreview();
            CloseDayPopup();
            BlockPopup.IsOpen = RangePopup.IsOpen = false;
            AllSchedulesPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open) TodoButton.SetResourceReference(BackgroundProperty, "MiniHoverBrush");
            else TodoButton.ClearValue(BackgroundProperty);
            string label = open ? "전체 일정 접기" : "전체 일정 펼치기";
            TodoButton.ToolTip = label;
            System.Windows.Automation.AutomationProperties.SetName(TodoButton, label);
            UpdateAllSchedulesLayout();
            if (open) RefreshAllSchedules();
        }

        private void ToggleAllSchedules_Click(object sender, RoutedEventArgs e)
        {
            if (RecentlyDragged) return;
            SetAllSchedulesOpen(!IsAllSchedulesOpen);
            e.Handled = true;
        }

        private void AllSchedulesClose_Click(object sender, RoutedEventArgs e)
        {
            SetAllSchedulesOpen(false);
            TodoButton.Focus();
            e.Handled = true;
        }

        private void CalendarSheet_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAllSchedulesLayout();

        private void UpdateAllSchedulesLayout()
        {
            if (!IsAllSchedulesOpen) { WeekDays.Visibility = Visibility.Visible; return; }
            double width = CalendarSheet.ActualWidth;
            if (width <= 0) return;
            bool beside = width >= 620;
            WeekDays.Visibility = beside ? Visibility.Visible : Visibility.Hidden;
            Grid.SetColumn(AllSchedulesPanel, beside ? 1 : 0);
            AllSchedulesPanel.Width = beside ? Math.Min(320, width * 0.37) : Math.Max(0, width - 16);
            AllSchedulesPanel.Margin = new Thickness(beside ? 0 : 8, 4, 8, 10);
        }

        private void RefreshAllSchedules()
        {
            if (!IsAllSchedulesOpen) return;
            var sorted = data.Schedules.OrderBy(s => s.RemainingDays).ThenBy(s => s.StartDate ?? DateTime.MaxValue)
                .ThenBy(s => s.Time ?? "99:99", StringComparer.Ordinal).ThenBy(s => s.Title ?? "", StringComparer.CurrentCulture).ToList();
            string signature = ThemeName + "|" + DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" +
                string.Join("\n", sorted.Select(s => s.Id + "|" + s.Title + "|" + s.Period + "|" + s.EndPeriod + "|" + s.Time + "|" + s.IsCompleted + "|" + s.Color));
            if (signature == allSchedulesSignature && allScheduleRows.Select(row => row.Item).SequenceEqual(sorted)) return;
            var scroll = AllSchedulesList.Template?.FindName("AllSchedulesScroll", AllSchedulesList) as ScrollViewer;
            double offset = scroll?.VerticalOffset ?? 0;
            allScheduleRows = sorted.Select(s =>
            {
                string detail = s.StartDate?.ToString("yyyy.MM.dd (ddd)", CultureInfo.GetCultureInfo("ko-KR")) ?? "날짜 확인";
                if (s.EndDate.HasValue) detail += " ~ " + s.EndDate.Value.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(s.Time)) detail += " · " + s.Time;
                bool customColor = FeatureRules.IsColor(s.Color);
                return new AllScheduleRow { Item = s, Detail = detail,
                    Background = customColor ? s.Color : AgendaColor(scheduleAppearance.CardColor, theme.Day),
                    Ink = customColor ? FeatureRules.ReadableText(s.Color) : AgendaColor(scheduleAppearance.TextColor, theme.Ink),
                    DDayInk = AgendaStatusInk(s) };
            }).ToList();
            AllSchedulesList.ItemsSource = allScheduleRows;
            if (offset > 0) scroll?.ScrollToVerticalOffset(offset);
            AllSchedulesCount.Text = sorted.Count + "개";
            AllSchedulesEmpty.Visibility = sorted.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            allSchedulesSignature = signature;
        }

        private sealed class AllScheduleRow
        {
            public ScheduleItem Item { get; set; }
            public string Title => Item.Title;
            public string DDay => Item.DDay;
            public bool IsCompleted => Item.IsCompleted;
            public string Detail { get; set; }
            public string Background { get; set; }
            public string Ink { get; set; }
            public string DDayInk { get; set; }
            public string Hint => Title + "\n" + Detail + " · " + DDay + "\n클릭: 수정 · 우클릭: 완료 / 색상 / 메시지 작성 / 삭제";
        }

        private void AllScheduleEdit_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AllScheduleRow row) OpenAllScheduleEditor(row.Item);
            e.Handled = true;
        }

        private void AllSchedulesAdd_Click(object sender, RoutedEventArgs e)
        {
            OpenAllScheduleEditor(null);
            e.Handled = true;
        }

        private void OpenAllScheduleEditor(ScheduleItem item)
        {
            if (RecentlyDragged || !PrepareAllScheduleEditor(item)) return;
            OpenDayPopupAt(AllSchedulesPanel);
            MiniTitleInput.Focus();
            MiniTitleInput.SelectAll();
        }

        // Shared validation/save logic stays in the existing editor. Preparing it does not show a window or popup.
        private bool PrepareAllScheduleEditor(ScheduleItem item)
        {
            if (item != null && !data.Schedules.Contains(item)) return false;
            CloseDayPopup();
            BlockPopup.IsOpen = false;
            selectedDay = item?.StartDate ?? DateTime.Today;
            dayPopupDay = selectedDay;
            ResetMiniEditor(selectedDay.Value);
            RefreshDaySchedules();
            MiniEditorPanel.Visibility = Visibility.Visible;
            if (item != null) EditMiniSchedule_Click(new Button { DataContext = item }, new RoutedEventArgs(Button.ClickEvent));
            return true;
        }
    }
}
