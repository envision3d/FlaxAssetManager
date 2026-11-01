using FlaxEngine;
using FlaxEngine.GUI;

namespace EKEditorUIKit
{
    public enum TextLabelStyle { Title, Body, Caption }

    public sealed class TextLabel : Label
    {
        public TextLabel(string text, TextLabelStyle style = TextLabelStyle.Body, bool dim = false)
        {
            Text = text;
            switch (style)
            {
                case TextLabelStyle.Title:
                    Font = Theme.TitleFont; Height = 26; break;
                case TextLabelStyle.Body:
                    Font = Theme.BodyFont; Height = 18; break;
                default:
                    Font = Theme.CaptionFont; Height = 14; break;
            }
            HorizontalAlignment = TextAlignment.Near;
            VerticalAlignment = TextAlignment.Center;
            TextColor = dim ? Theme.FgDim : Theme.Fg;
        }

        /// <summary>Switch dim state without rebuilding.</summary>
        public void Dim(bool dim) => TextColor = dim ? Theme.FgDim : Theme.Fg;
    }
}
