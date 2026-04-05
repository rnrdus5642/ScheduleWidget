using System.Collections.Generic;

namespace ScheduleWidget
{
    public class AppearanceSettings
    {
        public double Opacity { get; set; } = 1.0;
        public string ThemePreset { get; set; } = "Light";

        public string TopBarColor { get; set; } = "#FFD9D9D9";
        public string BackgroundColor { get; set; } = "#FFFFFF";
        public string CardColor { get; set; } = "#FFFFFF";
        public string CardBorderColor { get; set; } = "#DDDDDD";
        public string BottomBarColor { get; set; } = "#FFE0E0E0";
        public string TextColor { get; set; } = "#000000";
        public string SubTextColor { get; set; } = "#808080";
        public string BorderColor { get; set; } = "#808080";

        public double TitleFontSize { get; set; } = 14;
        public double DDayFontSize { get; set; } = 13;

        public static Dictionary<string, AppearanceSettings> Presets = new Dictionary<string, AppearanceSettings>
        {
            ["Light"] = new AppearanceSettings
            {
                ThemePreset = "Light",
                TopBarColor = "#FFD9D9D9",
                BackgroundColor = "#FFFFFF",
                CardColor = "#FFFFFF",
                CardBorderColor = "#DDDDDD",
                BottomBarColor = "#FFE0E0E0",
                TextColor = "#000000",
                SubTextColor = "#808080",
                BorderColor = "#808080"
            },
            ["Dark"] = new AppearanceSettings
            {
                ThemePreset = "Dark",
                TopBarColor = "#FF2D2D30",
                BackgroundColor = "#FF1E1E1E",
                CardColor = "#FF2D2D30",
                CardBorderColor = "#FF3F3F46",
                BottomBarColor = "#FF2D2D30",
                TextColor = "#FFFFFF",
                SubTextColor = "#FF9E9E9E",
                BorderColor = "#FF3F3F46"
            },
            ["Blue"] = new AppearanceSettings
            {
                ThemePreset = "Blue",
                TopBarColor = "#FF4A90D9",
                BackgroundColor = "#FFF0F6FF",
                CardColor = "#FFFFFF",
                CardBorderColor = "#FFB0CBE8",
                BottomBarColor = "#FFD6E6F7",
                TextColor = "#FF1A1A2E",
                SubTextColor = "#FF5A7A9E",
                BorderColor = "#FF4A90D9"
            },
            ["Pink"] = new AppearanceSettings
            {
                ThemePreset = "Pink",
                TopBarColor = "#FFE891B2",
                BackgroundColor = "#FFFFF0F5",
                CardColor = "#FFFFFF",
                CardBorderColor = "#FFF0C0D0",
                BottomBarColor = "#FFFCE4EC",
                TextColor = "#FF4A1A2E",
                SubTextColor = "#FFA0607A",
                BorderColor = "#FFE891B2"
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
