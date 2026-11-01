using System;
using FlaxEditor;

namespace EKEditorUIKitEditor
{
    /// <summary>
    /// Editor-side plugin for the EK Editor UI Kit. Kit components live in the EKEditorUIKit module.
    /// </summary>
    public class EKEditorUIKitEditor : EditorPlugin
    {
        public override Type GamePluginType => typeof(global::EKEditorUIKit.EKEditorUIKit);

        public override void InitializeEditor()
        {
        }

        public override void DeinitializeEditor()
        {
        }
    }
}
