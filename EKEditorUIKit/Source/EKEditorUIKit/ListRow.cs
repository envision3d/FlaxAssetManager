using FlaxEngine;
using FlaxEngine.GUI;

namespace EKEditorUIKit
{
    /// <summary>
    /// Generic one-line list row: title + subtitle + trailing state chip, with selected
    /// state (3-px left accent + selection fill) and a 1-px bottom separator.
    /// </summary>
    public class ListRow : Button
    {
        private readonly Label _title;
        private readonly Label _subtitle;
        private readonly Label _chip;
        private bool _selected;

        public ListRow()
        {
            Height = Theme.RowHeight;
            Text = "";
            Font = Theme.BodyFont;
            HasBorder = false;
            BackgroundColor = Color.Transparent;
            BackgroundColorHighlighted = Theme.BgHover;
            BackgroundColorSelected = Theme.BgSelected;
            BorderColor = Color.Transparent;

            _title = new Label
            {
                Parent = this,
                Font = Theme.BodyFont,
                TextColor = Theme.Fg,
                HorizontalAlignment = TextAlignment.Near,
                VerticalAlignment = TextAlignment.Center,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(12, 90, 6, 22), // left,right,top,bottom — right=90 clears chip
            };

            _subtitle = new Label
            {
                Parent = this,
                Font = Theme.CaptionFont,
                TextColor = Theme.FgDim,
                HorizontalAlignment = TextAlignment.Near,
                VerticalAlignment = TextAlignment.Center,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(12, 90, 26, 15),
            };

            _chip = new Label
            {
                Parent = this,
                Font = Theme.CaptionFont,
                TextColor = Theme.FgDim,
                HorizontalAlignment = TextAlignment.Near,
                VerticalAlignment = TextAlignment.Center,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(300, 14, 80, 16), // left=12 left margin, anchored to the left edge
            };
        }

        public void SetContent(string title, string subtitle, string chip, Color? chipColor = null)
        {
            _title.Text = title;
            _subtitle.Text = subtitle;
            _chip.Text = chip ?? "";
            _chip.TextColor = chipColor ?? Theme.FgDim;
        }

        public bool Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                BackgroundColor = value ? Theme.BgSelected : Color.Transparent;
            }
        }

        public override void Draw()
        {
            base.Draw();
            if (_selected)
                Render2D.FillRectangle(new Rectangle(0, 0, 3, Height), Theme.Accent); // left accent
            Render2D.FillRectangle(new Rectangle(0, Height - 1, Width, 1), Theme.Border * 0.4f); // separator
        }
    }
}
