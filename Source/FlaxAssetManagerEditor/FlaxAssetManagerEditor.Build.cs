using Flax.Build;
using Flax.Build.NativeCpp;

public class FlaxAssetManagerEditor : GameEditorModule
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
        options.PublicDependencies.Add("FlaxAssetManager");
        options.PublicDependencies.Add("EKEditorUIKitEditor");
        options.PublicDependencies.Add("EKEditorUIKit");
    }
}
