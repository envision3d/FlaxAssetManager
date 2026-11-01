using System;
using FlaxEngine;

namespace FlaxAssetManager
{
    /// <summary>
    /// Runtime plugin stub. Editor functionality lives in FlaxAssetManagerEditor.
    /// </summary>
    public class FlaxAssetManager : GamePlugin
    {
        public FlaxAssetManager()
        {
            _description = new PluginDescription
            {
                Name = "Flax Asset Manager",
                Category = "Tools",
                Author = "envision3d",
                AuthorUrl = "https://github.com/envision3d",
                HomepageUrl = "https://github.com/envision3d/FlaxAssetManager",
                RepositoryUrl = "https://github.com/envision3d/FlaxAssetManager",
                Description = "Browse and import assets and plugin projects from external manifests. Managed via Flax Asset Manager window.",
                Version = new Version(1, 0, 0),
                IsBeta = false,
                IsAlpha = false,
            };
        }

        public override void Initialize()
        {
            base.Initialize();
        }

        public override void Deinitialize()
        {
            base.Deinitialize();
        }
    }
}
