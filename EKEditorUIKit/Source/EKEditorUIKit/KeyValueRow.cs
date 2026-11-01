using FlaxEngine;
using FlaxEngine.GUI;

namespace EKEditorUIKit
{
    /// <summary>
    /// Fixed-width key column (dim caption) + stretching value column (caption).
    /// </summary>
    public sealed class KeyValueRow : ContainerControl
    {
        private readonly Label _key;
        private readonly Label _value;

        public KeyValueRow()
        {
            Height = 15;
            BackgroundColor = Color.Transparent;

            _key = new Label
            {
                Parent = this,
                Font = Theme.CaptionFont,
                TextColor = Theme.FgDim,
                HorizontalAlignment = TextAlignment.Near,
                VerticalAlignment = TextAlignment.Center,
                Bounds = new Rectangle(0, 0, Theme.KeyColumnWidth, 15),
            };

            _value = new Label
            {
                Parent = this,
                Font = Theme.CaptionFont,
                TextColor = Theme.Fg,
                HorizontalAlignment = TextAlignment.Near,
                VerticalAlignment = TextAlignment.Center,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(Theme.KeyColumnWidth, 0, 0, 15), // full row height → value centers on the key
            };
        }

        public void Set(string key, string value)
        {
            _key.Text = key;
            _value.Text = value;
            _value.TextColor = Theme.Fg;
        }

        public void SetColored(string key, string value, Color valueColor)
        {
            _key.Text = key;
            _value.Text = value;
            _value.TextColor = valueColor;
        }
    }
}
