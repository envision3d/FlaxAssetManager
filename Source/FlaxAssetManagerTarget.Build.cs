using Flax.Build;

public class FlaxAssetManagerTarget : GameProjectTarget
{
    public override void Init()
    {
        base.Init();
        Modules.Add("FlaxAssetManager");
    }
}
