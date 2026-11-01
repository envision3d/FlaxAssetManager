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

        // Colors - a cohesive dark metallic (gunmetal) blue palette.
        // These are returned explicitly rather than read from Style.Current so the Asset Manager
        // window looks the same on every editor theme; Style's background is dark grey and would
        // defeat the intended look. The window's panels are transparent by default, so the root
        // panel is painted with Bg (see FlaxAssetManagerWindow.CreateUI) to give the window its
        // dark metallic blue backdrop.
        public static Color Bg => Metal(6f / 255f, 16f / 255f, 25f / 255f); // deep navy #061019 (main background)
        public static Color BgAlt => Metal(0.105f, 0.135f, 0.200f);     // top bar / status bar band
        public static Color Fg => Metal(0.882f, 0.910f, 0.957f);        // cool near-white text
        public static Color FgDim => Metal(0.588f, 0.639f, 0.722f);     // dimmed cool text
        public static Color BgHover => Metal(0.145f, 0.192f, 0.278f);   // row hover
        public static Color BgSelected => Metal(0.157f, 0.278f, 0.459f);// row selection (blue-tinted)
        public static Color Border => Metal(0.235f, 0.298f, 0.400f);
        public static Color BorderSelected => Metal(0.451f, 0.596f, 0.804f);
        public static Color Accent => Metal(0.298f, 0.580f, 0.949f);    // bright steel-blue accent
        public static Color Progress => C(Style.Current?.ProgressNormal, 0.30f, 0.80f, 0.40f);

        private static Color C(Color? styleColor, float r, float g, float b) =>
            styleColor.HasValue ? styleColor.Value : new Color(r, g, b);

        private static Color Metal(float r, float g, float b) => new Color(r, g, b);
    }
}
