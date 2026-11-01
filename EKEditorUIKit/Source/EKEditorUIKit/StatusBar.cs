using FlaxEngine;
using FlaxEngine.GUI;

namespace EKEditorUIKit
{
    /// <summary>
    /// One bar for status + progress: status text left; message + progress bar right,
    /// shown only while busy.
    /// </summary>
    public sealed class StatusBar : Panel
    {
        private readonly Label _status;
        private readonly ProgressBar _progress;
        private readonly Label _progressText;
        private bool _busy;

        public StatusBar() : base(ScrollBars.None)
        {
            Height = 24;
            BackgroundColor = Theme.BgAlt;

            _status = new Label
            {
                Parent = this,
                Font = Theme.CaptionFont,
                TextColor = Theme.FgDim,
                HorizontalAlignment = TextAlignment.Near,
                VerticalAlignment = TextAlignment.Center,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(8, 8, 0, 24), // full height → text centers vertically
            };

            _progressText = new Label
            {
                Parent = this,
                Font = Theme.CaptionFont,
                TextColor = Theme.FgDim,
                HorizontalAlignment = TextAlignment.Far,
                VerticalAlignment = TextAlignment.Center,
                AnchorPreset = AnchorPresets.TopRight,
                Offsets = new Margin(0, 8, 0, 0),
                Bounds = new Rectangle(-116, 0, 108, 24), // negative left offset = right margin (TopRight anchor convention)
                Visible = false,
            };

            _progress = new ProgressBar
            {
                Parent = this,
                AnchorPreset = AnchorPresets.TopRight,
                Offsets = new Margin(0, 118, 8, 12),
                Bounds = new Rectangle(-116, 8, 100, 8),
                Value = 0,
                Visible = false,
            };
        }

        public void SetStatus(string text) => _status.Text = text;

        public void SetBusy(string message)
        {
            if (_busy)
            {
                _progressText.Text = message ?? "";
                return;
            }
            _busy = true;
            _progress.Visible = true;
            _progressText.Visible = true;
            _progressText.Text = message ?? "";
            // Make room for the right zone (108 text + 100 bar + gaps):
            _status.Offsets = new Margin(8, 230, 0, 24);
            PerformLayout();
        }

        public void SetProgress(float fraction) => _progress.Value = fraction * 100f;

        public void SetIdle()
        {
            _busy = false;
            _progress.Visible = false;
            _progressText.Visible = false;
            _progress.Value = 0;
            _status.Offsets = new Margin(8, 8, 0, 24);
            PerformLayout();
        }

        public override void Draw()
        {
            base.Draw();
            Render2D.FillRectangle(new Rectangle(0, 0, Width, 1), Theme.Border);
        }
    }
}
