using System;
using FlaxEngine;
using FlaxEngine.GUI;

namespace EKEditorUIKit
{
    /// <summary>
    /// A checkbox with an integrated, clickable text label (the engine CheckBox has no label).
    /// </summary>
    public sealed class LabeledCheckBox : ContainerControl
    {
        private readonly CheckBox _box;
        private readonly Button _label;

        public LabeledCheckBox(string text)
        {
            Height = Theme.ControlHeight;
            BackgroundColor = Color.Transparent;

            _box = new CheckBox
            {
                Parent = this,
                BoxSize = 12,
                Bounds = new Rectangle(0, 7, 12, 12), // box centered on the control's midline
                BorderColor = Theme.Border,
                BorderColorHighlighted = Theme.BorderSelected,
                ImageColor = Theme.Fg,
            };

            _label = new Button
            {
                Parent = this,
                Text = text,
                Font = Theme.BodyFont,
                BackgroundColor = Color.Transparent,
                BorderColor = Color.Transparent,
                BorderColorHighlighted = Color.Transparent,
                BorderThickness = 0,
                HasBorder = false,
                TextColor = Theme.Fg,
                HorizontalAlignment = TextAlignment.Near,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(16, 0, 0, Theme.ControlHeight), // full height → button text centers on the box
            };

            _label.Clicked += () => _box.Toggle();
            _box.StateChanged += _ => CheckedChanged?.Invoke(Checked);
        }

        public bool Checked
        {
            get => _box.Checked;
            set => _box.Checked = value;
        }

        public event Action<bool> CheckedChanged;
    }
}
