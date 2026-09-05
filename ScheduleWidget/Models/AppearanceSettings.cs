using System.Collections.Generic;

namespace ScheduleWidget
{
    public class AppearanceSettings
    {
        public double Opacity { get; set; } = 1.0;
        public string ThemePreset { get; set; } = "Light";

        public string TopBarColor { get; set; } = "#FFE8EBFF";
        public string BackgroundColor { get; set; } = "#FFF7F8FC";
        public string CardColor { get; set; } = "#FFFFFFFF";
        public string CardBorderColor { get; set; } = "#FFE4E8F0";
        public string BottomBarColor { get; set; } = "#FFF0F3F8";
        public string TextColor { get; set; } = "#FF172033";
        public string SubTextColor { get; set; } = "#FF667085";
        public string BorderColor { get; set; } = "#FFD1D8E5";

        public double TitleFontSize { get; set; } = 14;
        public double DDayFontSize { get; set; } = 13;

        public static Dictionary<string, AppearanceSettings> Presets = new Dictionary<string, AppearanceSettings>
        {
            ["Light"] = new AppearanceSettings
            {
                ThemePreset = "Light",
                TopBarColor = "#FFE8EBFF",
                BackgroundColor = "#FFF7F8FC",
                CardColor = "#FFFFFFFF",
                CardBorderColor = "#FFE4E8F0",
                BottomBarColor = "#FFF0F3F8",
                TextColor = "#FF172033",
                SubTextColor = "#FF667085",
                BorderColor = "#FFD1D8E5"
            },
            ["Dark"] = new AppearanceSettings
            {
                ThemePreset = "Dark",
                TopBarColor = "#FF252A3A",
                BackgroundColor = "#FF151927",
                CardColor = "#FF202538",
                CardBorderColor = "#FF343B55",
                BottomBarColor = "#FF1D2232",
                TextColor = "#FFF7F8FF",
                SubTextColor = "#FFAAB2C5",
                BorderColor = "#FF3B4563"
            },
            ["Blue"] = new AppearanceSettings
            {
                ThemePreset = "Blue",
                TopBarColor = "#FFDCEBFF",
                BackgroundColor = "#FFF3F8FF",
                CardColor = "#FFFFFFFF",
                CardBorderColor = "#FFC6DBF5",
                BottomBarColor = "#FFE7F1FF",
                TextColor = "#FF172B4D",
                SubTextColor = "#FF5B7396",
                BorderColor = "#FFAEC9EA"
            },
            ["Pink"] = new AppearanceSettings
            {
                ThemePreset = "Pink",
                TopBarColor = "#FFFDE4EF",
                BackgroundColor = "#FFFFF7FA",
                CardColor = "#FFFFFFFF",
                CardBorderColor = "#FFF4CBDC",
                BottomBarColor = "#FFFFEDF4",
                TextColor = "#FF4A2035",
                SubTextColor = "#FFA56A82",
                BorderColor = "#FFE9B1C8"
            }
        };

        public void CopyColorsFrom(AppearanceSettings other)
        {
            TopBarColor = other.TopBarColor;
            BackgroundColor = other.BackgroundColor;
            CardColor = other.CardColor;
            CardBorderColor = other.CardBorderColor;
            BottomBarColor = other.BottomBarColor;
            TextColor = other.TextColor;
            SubTextColor = other.SubTextColor;
            BorderColor = other.BorderColor;
        }
    }
}
