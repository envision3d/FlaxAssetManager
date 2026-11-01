using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace FlaxAssetManager.Core.Models
{
    /// <summary>
    /// User-configured list of source URLs (persisted via SettingsService).
    /// </summary>
    public class AssetSourceConfig
    {
        [JsonProperty("sources")]
        public List<SourceUrlEntry> Sources { get; set; } = new List<SourceUrlEntry>();
    }

    public class SourceUrlEntry
    {
        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("name")]
        public string Name { get; set; } // display override; if null use manifest.sourceName

        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Url : Name;
    }

    /// <summary>
    /// Plugin settings (destinations, etc.).
    /// </summary>
    public class FlaxAssetManagerSettings
    {
        [JsonProperty("contentDestination")]
        public string ContentDestination { get; set; } = "Content/FlaxAssetManager";

        [JsonProperty("scriptDestination")]
        public string ScriptDestination { get; set; } = "Source/FlaxAssetManager";

        [JsonProperty("overwriteExisting")]
        public bool OverwriteExisting { get; set; } = false;

        /// <summary>
        /// Per-pack variant selection: key <c>sourceId + "/" + packId</c>, value a variant id.
        /// Absent key ⇒ default file set.
        /// </summary>
        [JsonProperty("selectedVariants")]
        public Dictionary<string, string> SelectedVariants { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Install tracking for updates.
    /// </summary>
    public class InstalledManifest
    {
        [JsonProperty("installed")]
        public List<InstalledEntry> Installed { get; set; } = new List<InstalledEntry>();
    }

    public class InstalledEntry
    {
        [JsonProperty("sourceId")] public string SourceId { get; set; }
        [JsonProperty("sourceUrl")] public string SourceUrl { get; set; }
        [JsonProperty("packId")] public string PackId { get; set; }
        [JsonProperty("packName")] public string PackName { get; set; }
        [JsonProperty("packType")] public PackType PackType { get; set; }
        [JsonProperty("installedVersion")] public string InstalledVersion { get; set; }
        [JsonProperty("installedAt")] public DateTime InstalledAt { get; set; }
        [JsonProperty("distributionUrl")] public string DistributionUrl { get; set; }
        [JsonProperty("variant")] public string Variant { get; set; }
        [JsonProperty("resolvedCommit")] public string ResolvedCommit { get; set; }
        [JsonProperty("installPath")] public string InstallPath { get; set; }
        [JsonProperty("files")] public List<string> Files { get; set; } = new List<string>();

        /// <summary>
        /// Content-tree assets this install PRODUCED rather than downloaded: the generated
        /// M_{packId}.flax material and the .flax assets created by importing model sources. They are
        /// not in Files (which lists downloaded bytes), so an uninstall that ignores them deletes the
        /// downloads and leaves the visible assets behind - a pack that is "removed" yet still shows in
        /// the Content browser. Stored as project-content-relative paths and deleted through the same
        /// containment check Files uses.
        /// </summary>
        [JsonProperty("generatedAssets")] public List<string> GeneratedAssets { get; set; } = new List<string>();
    }
}
