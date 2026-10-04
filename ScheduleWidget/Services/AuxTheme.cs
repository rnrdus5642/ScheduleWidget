using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace ScheduleWidget
{
    // The extra windows (설정, 캐릭터 설정, 캐릭터 선택, 연락 · 알림, 음악) follow the app theme (Light / Dark / Blue / Pink / Modern)
    // like the mini window does: their Aux* brushes are overridden per window with the theme's colors.
    public static class AuxTheme
    {
        public sealed class Palette
        {
            // ActionText: the action color as text on Surface (outline buttons), dark / light enough to read (4.5:1).
            public string Canvas, Surface, Ink, Muted, Hairline, Action, ActionText;
        }

        // Same colors as the mini window's menus and bubbles for each theme.
        public static readonly Dictionary<string, Palette> Palettes = new Dictionary<string, Palette>(StringComparer.OrdinalIgnoreCase)
        {
            ["Light"] = new Palette { Canvas = "#F5F5F7", Surface = "#FFFFFF", Ink = "#1D1D1F", Muted = "#6E6E73", Hairline = "#E0E0E0", Action = "#1D1D1F", ActionText = "#1D1D1F" },
            ["Dark"] = new Palette { Canvas = "#1F2636", Surface = "#151D2C", Ink = "#F8FAFC", Muted = "#C3CDDC", Hairline = "#2A3A53", Action = "#5B6CE0", ActionText = "#8FA0FF" },
            ["Blue"] = new Palette { Canvas = "#EAF4FF", Surface = "#FFFFFF", Ink = "#102A4A", Muted = "#466B92", Hairline = "#A8C8ED", Action = "#2477E6", ActionText = "#1D64C4" },
            ["Pink"] = new Palette { Canvas = "#FFF4F8", Surface = "#FFFFFF", Ink = "#4A2035", Muted = "#A56A82", Hairline = "#F4CBDC", Action = "#D05A8A", ActionText = "#B03F6E" },
            ["Modern"] = new Palette { Canvas = "#F2F2F2", Surface = "#FFFFFF", Ink = "#111111", Muted = "#6B6B6B", Hairline = "#D9D9D9", Action = "#111111", ActionText = "#111111" }
        };

        public static string Current { get; private set; } = "Light";
        public static Palette Colors => Palettes.TryGetValue(Current, out Palette p) ? p : Palettes["Light"];

        /// <summary>Puts the current theme's Aux* brushes on this element (they win over its merged AuxiliaryStyles).</summary>
        public static void ApplyTo(FrameworkElement element)
        {
            if (element == null) return;
            var p = Colors;
            Set(element, "AuxCanvasBrush", p.Canvas);
            Set(element, "AuxSurfaceBrush", p.Surface);
            Set(element, "AuxInkBrush", p.Ink);
            Set(element, "AuxMutedBrush", p.Muted);
            Set(element, "AuxHairlineBrush", p.Hairline);
            Set(element, "AuxActionBrush", p.Action);
            Set(element, "AuxActionTextBrush", p.ActionText);
            Set(element, "AuxFocusBrush", p.Action);
            // Drop-downs (AuxComboBox on WidgetComboBox) read these; Light gives the same values they had fixed before.
            Set(element, "TextBrush", p.Ink);
            Set(element, "SubTextBrush", p.Muted);
            Set(element, "CardBrush", p.Surface);
            Set(element, "BorderBrush", p.Hairline);
            Set(element, "AccentBrush", p.Action);
            Set(element, "ControlHoverBrush", p.Canvas);
        }

        /// <summary>Switches the theme and recolors every open extra window (the mini window colors itself).</summary>
        public static void SetTheme(string preset)
        {
            Current = preset != null && Palettes.ContainsKey(preset) ? preset : "Light";
            if (Application.Current == null) return;
            // The mini window sets its own Aux* colors but not this one, so it reads the app-wide value.
            var actionText = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Colors.ActionText));
            actionText.Freeze();
            Application.Current.Resources["AuxActionTextBrush"] = actionText;
            foreach (Window window in Application.Current.Windows)
                if (IsExtraWindow(window)) ApplyTo(window);
        }

        /// <summary>Windows styled with AuxiliaryStyles and themed here (not the TODO window or the mini window).</summary>
        public static bool IsExtraWindow(Window window) => window != null && !(window is MainWindow) && !(window is MiniWindow);

        private static void Set(FrameworkElement element, string key, string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            element.Resources[key] = brush;
        }
    }
}
