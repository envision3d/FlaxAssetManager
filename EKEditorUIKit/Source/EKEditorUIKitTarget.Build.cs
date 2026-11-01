using Flax.Build;

public class EKEditorUIKitTarget : GameProjectTarget
{
    public override void Init()
    {
        base.Init();
        Modules.Add("EKEditorUIKit");
    }
}
