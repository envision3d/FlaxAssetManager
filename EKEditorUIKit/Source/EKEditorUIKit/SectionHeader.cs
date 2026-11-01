using FlaxEngine;
using FlaxEngine.GUI;

namespace EKEditorUIKit
{
    /// <summary>
    /// Uppercase caption with a 1-px bottom rule.
    /// </summary>
    public sealed class SectionHeader : Label
    {
        public SectionHeader(string text)
        {
            Text = text;
            Font = Theme.CaptionFont;
            CaseOption = TextCaseOptions.Uppercase;
            TextColor = Theme.FgDim;
            HorizontalAlignment = TextAlignment.Near;
            VerticalAlignment = TextAlignment.Center;
            Height = 20;
            Margin = new Margin(0, 0, 0, 3); // text margin (left,right,top,bottom) — clears the rule
        }

        public override void Draw()
        {
            base.Draw();
            Render2D.FillRectangle(new Rectangle(0, Height - 1, Width, 1), Theme.Border);
        }
    }
}
