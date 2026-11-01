using FlaxEngine;
using FlaxEngine.GUI;

namespace EKEditorUIKit
{
    public sealed class FlatButton : Button
    {
        public FlatButton(string text = "")
        {
            Text = text;
            Font = Theme.BodyFont;
            Height = Theme.ControlHeight;
            HorizontalAlignment = TextAlignment.Center;
        }
    }
}
