using FlaxEngine;
using FlaxEngine.GUI;

namespace EKEditorUIKit
{
    /// <summary>
    /// Generic one-line list row: title + subtitle + trailing state chip + a status column
    /// (SetStatus), with selected state (3-px left accent + selection fill) and a 1-px bottom
    /// separator.
    /// </summary>
    public class ListRow : Button
    {
        private readonly Label _title;
        private readonly Label _subtitle;
        private readonly Label _chip;
        private readonly Label _status;
        private bool _selected;

        // The row's left inset inside the list panel (its Offsets.Left, set by the window). The window's
        // column-header labels are children of the panel behind the list, so they add this to the same
        // Theme column x - the one place that relationship is written down.
        public const float RowLeftInset = 4f;

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
                VerticalAlignment = TextAlignment.Near, // Near = the top edge (Flax TextAlignment has no Top); every column tops out at the same y
                AnchorPreset = AnchorPresets.TopLeft, // not stretch/right-anchor: fixed columns (see Theme)
                Bounds = new Rectangle(Theme.NameColumnLeft, 6, Theme.NameColumnWidth, 18),
                ClipText = true, // without this the title/subtitle text spills into the columns
            };

            _subtitle = new Label
            {
                Parent = this,
                Font = Theme.CaptionFont,
                TextColor = Theme.FgDim,
                HorizontalAlignment = TextAlignment.Near,
                VerticalAlignment = TextAlignment.Near,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(Theme.NameColumnLeft, 24, Theme.NameColumnWidth, 14), // second line of the name column
                ClipText = true,
            };

            _chip = new Label
            {
                Parent = this,
                Font = Theme.CaptionFont,
                TextColor = Theme.FgDim,
                HorizontalAlignment = TextAlignment.Near,
                VerticalAlignment = TextAlignment.Near,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(Theme.ChipColumnLeft, 6, Theme.ChipColumnWidth, 16),
                ClipText = true, // a category longer than the column stops at its edge, tooltip gives the rest
            };

            // Status column: the install-state cell (Installed / Update / Not installed). Fixed x, and
            // text starts at the cell top so the columns read as a table down the list.
            _status = new Label
            {
                Parent = this,
                Font = Theme.CaptionFont,
                TextColor = Theme.FgDim,
                HorizontalAlignment = TextAlignment.Near,
                VerticalAlignment = TextAlignment.Near,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(Theme.StatusColumnLeft, 6, Theme.StatusColumnWidth, 16),
                ClipText = true, // Flax Labels do not clip by default (Label.cs:181-187)
            };
        }

        /// <summary>
        /// Fills the name column (title + subtitle) and the category chip. The chip's tooltip carries
        /// the full text, so a category clipped by the column width is still readable.
        /// </summary>
        public void SetContent(string title, string subtitle, string chip, Color? chipColor = null)
        {
            _title.Text = title;
            _subtitle.Text = subtitle;
            _chip.Text = chip ?? "";
            _chip.TextColor = chipColor ?? Theme.FgDim;
            _chip.TooltipText = chip ?? "";
        }

        /// <summary>
        /// Writes the status column: the install-state cell of this row. The caller derives the text
        /// from the install record; the kit only paints it, so a list cannot invent a state here.
        /// </summary>
        public void SetStatus(string text, Color? color = null, string tooltip = null)
        {
            _status.Text = text ?? string.Empty;
            _status.TextColor = color ?? Theme.FgDim;
            _status.TooltipText = tooltip ?? string.Empty;
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
