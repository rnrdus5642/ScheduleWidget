using System.Collections.Generic;

namespace ScheduleWidget
{
    public class AppearanceSettings
    {
        public double Opacity { get; set; } = 1.0;
        public string ThemePreset { get; set; } = "Light";

        public string TopBarColor { get; set; } = "#FFF7F6FF";
        public string BackgroundColor { get; set; } = "#FFFCFBFF";
        public string CardColor { get; set; } = "#FFFFFFFF";
        public string CardBorderColor { get; set; } = "#FFE8E5F2";
        public string BottomBarColor { get; set; } = "#FFF7F6FC";
        public string TextColor { get; set; } = "#FF24243A";
        public string SubTextColor { get; set; } = "#FF6E6D84";
        public string BorderColor { get; set; } = "#FFD9D8E8";
        public string AccentColor { get; set; } = "#FF5B61D6";
        public string ControlHoverColor { get; set; } = "#FFEFEEFF";

        public double TitleFontSize { get; set; } = 14;
        public double DDayFontSize { get; set; } = 13;

        public static Dictionary<string, AppearanceSettings> Presets = new Dictionary<string, AppearanceSettings>
        {
            ["Light"] = new AppearanceSettings
            {
                ThemePreset = "Light",
                TopBarColor = "#FFF7F6FF",
                BackgroundColor = "#FFFCFBFF",
                CardColor = "#FFFFFFFF",
                CardBorderColor = "#FFE8E5F2",
                BottomBarColor = "#FFF7F6FC",
                TextColor = "#FF24243A",
                SubTextColor = "#FF6E6D84",
                BorderColor = "#FFD9D8E8",
                AccentColor = "#FF5B61D6",
                ControlHoverColor = "#FFEFEEFF"
            },
            ["Dark"] = new AppearanceSettings
            {
                ThemePreset = "Dark",
                TopBarColor = "#FF111827",
                BackgroundColor = "#FF080C14",
                CardColor = "#FF151D2C",
                CardBorderColor = "#FF2A3A53",
                BottomBarColor = "#FF0E1522",
                TextColor = "#FFF8FAFC",
                SubTextColor = "#FFC3CDDC",
                BorderColor = "#FF354862",
                AccentColor = "#FF8295FF",
                ControlHoverColor = "#FF263752"
            },
            ["Blue"] = new AppearanceSettings
            {
                ThemePreset = "Blue",
                TopBarColor = "#FFDCEBFF",
                BackgroundColor = "#FFEAF4FF",
                CardColor = "#FFF8FCFF",
                CardBorderColor = "#FFA8C8ED",
                BottomBarColor = "#FFDCEEFF",
                TextColor = "#FF102A4A",
                SubTextColor = "#FF466B92",
                BorderColor = "#FF8FB8E7",
                AccentColor = "#FF2477E6",
                ControlHoverColor = "#FFD2E7FF"
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
                BorderColor = "#FFE9B1C8",
                AccentColor = "#FFD05A8A",
                ControlHoverColor = "#FFFCE0EB"
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
            AccentColor = other.AccentColor;
            ControlHoverColor = other.ControlHoverColor;
        }
    }
}
