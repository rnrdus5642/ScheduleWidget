using System;
using System.Windows;

namespace ScheduleWidget
{
    public partial class EditScheduleWindow : Window
    {
        public string ResultTitle { get; private set; }
        public string ResultPeriod { get; private set; }

        public EditScheduleWindow(ScheduleItem item)
        {
            InitializeComponent();

            InitDateSelectors();
            LoadFromItem(item);
        }

        private void LoadFromItem(ScheduleItem item)
        {
            TitleInput.Text = item.Title;

            if (DateTime.TryParse(item.Period, out DateTime date))
            {
                if (YearCombo.Items.Contains(date.Year))
                    YearCombo.SelectedItem = date.Year;
                MonthCombo.SelectedItem = date.Month;
                UpdateDays(date.Year, date.Month);
                DayCombo.SelectedItem = date.Day;
            }

            TitleInput.Focus();
            TitleInput.SelectAll();
        }

        private void InitDateSelectors()
        {
            DateTime today = DateTime.Now;
            for (int y = today.Year - 5; y <= today.Year + 5; y++) YearCombo.Items.Add(y);
            for (int m = 1; m <= 12; m++) MonthCombo.Items.Add(m);

            YearCombo.SelectedItem = today.Year;
            MonthCombo.SelectedItem = today.Month;
            UpdateDays(today.Year, today.Month);
            DayCombo.SelectedItem = today.Day;

            YearCombo.SelectionChanged += (s, e) => UpdateDays((int)YearCombo.SelectedItem, (int)MonthCombo.SelectedItem);
            MonthCombo.SelectionChanged += (s, e) => UpdateDays((int)YearCombo.SelectedItem, (int)MonthCombo.SelectedItem);
        }

        private void UpdateDays(int year, int month)
        {
            int prevDay = DayCombo.SelectedItem is int d ? d : 1;
            DayCombo.Items.Clear();
            int days = DateTime.DaysInMonth(year, month);
            for (int i = 1; i <= days; i++) DayCombo.Items.Add(i);
            DayCombo.SelectedItem = Math.Min(prevDay, days);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TitleInput.Text)) return;

            ResultTitle = TitleInput.Text.Trim();
            ResultPeriod = $"{(int)YearCombo.SelectedItem}-{(int)MonthCombo.SelectedItem:D2}-{(int)DayCombo.SelectedItem:D2}";
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
