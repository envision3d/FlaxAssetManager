using Flax.Build;

public class EKEditorUIKitEditorTarget : GameProjectEditorTarget
{
    public override void Init()
    {
        base.Init();
        Modules.Add("EKEditorUIKit");
        Modules.Add("EKEditorUIKitEditor");
    }
}
