using System;
using System.Globalization;
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
                SetYearSelection(date.Year);
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

            YearCombo.SelectionChanged += (s, e) => UpdateDaysForCurrentSelection();
            YearCombo.LostFocus += (s, e) => NormalizeYearInput();
            MonthCombo.SelectionChanged += (s, e) => UpdateDaysForCurrentSelection();
        }

        private void UpdateDays(int year, int month)
        {
            int prevDay = DayCombo.SelectedItem is int d ? d : 1;
            DayCombo.Items.Clear();
            int days = DateTime.DaysInMonth(year, month);
            for (int i = 1; i <= days; i++) DayCombo.Items.Add(i);
            DayCombo.SelectedItem = Math.Min(prevDay, days);
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

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TitleInput.Text)) return;

            DateTime selectedDate;
            if (!TryGetSelectedDate(out selectedDate))
            {
                MessageBox.Show(
                    this,
                    "유효한 날짜를 입력해 주세요.",
                    "날짜 확인",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ResultTitle = TitleInput.Text.Trim();
            ResultPeriod = selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
