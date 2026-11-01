using Flax.Build;

public class FlaxAssetManagerEditorTarget : GameProjectEditorTarget
{
    public override void Init()
    {
        base.Init();
        Modules.Add("FlaxAssetManager");
        Modules.Add("FlaxAssetManagerEditor");
    }
}
