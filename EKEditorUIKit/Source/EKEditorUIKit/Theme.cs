using FlaxEngine;
using FlaxEngine.GUI;

namespace EKEditorUIKit
{
    /// <summary>
    /// Single source of truth for kit colors, fonts and metrics.
    /// Every value falls back to a fixed constant when Style.Current is null.
    /// </summary>
    public static class Theme
    {
        // Metrics
        public const float Padding = 8f;
        public const float Spacing = 4f;
        public const float ControlHeight = 22f;
        public const float RowHeight = 44f;
        public const float KeyColumnWidth = 96f;
        public const float PreviewHeight = 320f;

        // Fonts (FontReference(Font) is null-safe — verified Render2D/FontReference.cs:58)
        public static FontReference TitleFont => new FontReference(Style.Current?.FontLarge);
        public static FontReference BodyFont => new FontReference(Style.Current?.FontMedium);
        public static FontReference CaptionFont => new FontReference(Style.Current?.FontSmall);

        // Colors
        public static Color Bg => C(Style.Current?.Background, 0.15f, 0.15f, 0.16f);
        public static Color BgAlt => C(Style.Current?.LightBackground, 0.17f, 0.17f, 0.18f);
        public static Color Fg => C(Style.Current?.Foreground, 0.92f, 0.92f, 0.92f);
        public static Color FgDim => C(Style.Current?.ForegroundGrey, 0.62f, 0.62f, 0.62f);
        public static Color BgHover => C(Style.Current?.BackgroundHighlighted, 0.22f, 0.22f, 0.24f);
        public static Color BgSelected => C(Style.Current?.BackgroundSelected, 0.22f, 0.22f, 0.30f);
        public static Color Border => C(Style.Current?.BorderNormal, 0.30f, 0.30f, 0.32f);
        public static Color BorderSelected => C(Style.Current?.BorderSelected, 0.55f, 0.55f, 0.60f);
        public static Color Accent => C(Style.Current?.Selection, 0.25f, 0.55f, 1.00f);
        public static Color Progress => C(Style.Current?.ProgressNormal, 0.30f, 0.80f, 0.40f);

        private static Color C(Color? styleColor, float r, float g, float b) =>
            styleColor.HasValue ? styleColor.Value : new Color(r, g, b);
    }
}
