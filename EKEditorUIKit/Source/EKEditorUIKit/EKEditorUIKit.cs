using System;
using FlaxEngine;

namespace EKEditorUIKit
{
    /// <summary>
    /// Plugin entry for the EK Editor UI Kit (reusable UI components for editor windows).
    /// </summary>
    public class EKEditorUIKit : GamePlugin
    {
        public EKEditorUIKit()
        {
            _description = new PluginDescription
            {
                Name = "EK Editor UI Kit",
                Category = "Tools",
                Author = "EK",
                AuthorUrl = "",
                HomepageUrl = "",
                RepositoryUrl = "",
                Description = "Reusable UI kit (theme + common controls) for editor windows.",
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
