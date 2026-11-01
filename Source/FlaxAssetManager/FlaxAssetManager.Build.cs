using Flax.Build;
using Flax.Build.NativeCpp;

public class FlaxAssetManager : GameModule
{
    public override void Init()
    {
        base.Init();
        BuildNativeCode = false;
    }

    public override void Setup(BuildOptions options)
    {
        base.Setup(options);
        options.ScriptingAPI.IgnoreMissingDocumentationWarnings = true;
    }
}
