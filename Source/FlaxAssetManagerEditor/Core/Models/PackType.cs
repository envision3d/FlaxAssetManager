using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace FlaxAssetManager.Core.Models
{
    /// <summary>
    /// How an asset pack should be installed.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum PackType
    {
        /// <summary>
        /// Mixed content+scripts as explicit file list. Files are copied to Content/ and Source/ destinations.
        /// </summary>
        AssetPack = 0,

        /// <summary>
        /// A full Flax plugin project distributed as a git repository (distribution.kind "gitRepo").
        /// Cloned to Plugins/&lt;name&gt; and a .flaxproj reference injected into the host project.
        /// </summary>
        PluginProject = 1,

        /// <summary>
        /// Content distributed as a single .zip archive. Downloaded and extracted to
        /// Content/&lt;source&gt;/&lt;pack&gt;/files/, preserving the archive's internal layout.
        /// </summary>
        ArchivePack = 2,
    }
}
