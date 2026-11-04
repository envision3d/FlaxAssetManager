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

        /// <summary>
        /// Seed URLs that have already been applied to this project's source list, from <b>either</b> seed
        /// channel: the project's <c>Settings.json</c> <c>seedSources</c>, or the <c>seed_sources.json</c> a
        /// plugin ships next to its own project file. Kept so a seed is a <b>one-time</b> starting point: a
        /// source the user removes (the window's <b>Remove</b>, an <b>Edit</b>, or an edit of this file)
        /// stays removed instead of reappearing on the next editor start. Written only by
        /// <see cref="Services.SettingsService.ApplySeedSources"/>, which states the rules.
        /// </summary>
        [JsonProperty("seededUrls")]
        public List<string> SeededUrls { get; set; } = new List<string>();
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

        /// <summary>
        /// The manifest URLs this project starts with, authored by the team in <c>Settings.json</c> and
        /// applied by <see cref="Services.SettingsService.ApplySeedSources"/> on load and on every
        /// **Refresh**. Each entry is appended to <c>Sources.json</c> **once** - never re-added after the
        /// user removes it - and from then on is an ordinary source: its enabled flag, display name, and
        /// the trust decision belong to the user (see <c>docs/DECISIONS.md</c> D12 and D19). This is the
        /// project's own seed channel; the plugin's is <see cref="PluginSeed"/> and the file it governs.
        /// </summary>
        [JsonProperty("seedSources")]
        public List<SeedSourceEntry> SeedSources { get; set; } = new List<SeedSourceEntry>();

        /// <summary>
        /// Opt-out for the seed file the <b>plugin</b> ships (<c>seed_sources.json</c> next to the
        /// plugin's <c>.flaxproj</c>). <c>false</c> in <c>Settings.json</c> and the plugin's seed list is
        /// not applied to this project at all - a project keeps the last word on who writes into its
        /// source list. A project's own <c>seedSources</c> are unaffected, and
        /// <c>$(PluginPath)</c> keeps resolving, so a project that names the plugin's files by hand is
        /// not opting out of that. See <c>docs/DECISIONS.md</c> D19.
        /// </summary>
        [JsonProperty("pluginSeed")]
        public bool PluginSeed { get; set; } = true;
    }

    /// <summary>
    /// One seed entry: a manifest URL, an optional display-name override, and an optional
    /// <c>enabled:false</c> to stage a source without fetching it. The shape is shared by both seed
    /// channels - the project's <c>Settings.json</c> <c>seedSources</c>, and the <c>seedSources</c> list of
    /// the <c>seed_sources.json</c> a plugin ships next to its own project file. The <c>url</c> accepts
    /// everything a <c>Sources.json</c> url accepts - <c>http(s)</c>, <c>file://</c>, an absolute path, a
    /// path relative to the project, and the <c>$(PluginPath)</c> (a plugin's own folder, see
    /// <see cref="Utils.PluginSeedFile"/>), <c>$(ProjectPath)</c> and <c>$(EnginePath)</c> macros (see
    /// <see cref="Services.AssetSourceManager.FetchOneAsync"/>) - so a seed can point at a manifest that
    /// ships inside the project, or inside a plugin, and needs no network.
    /// </summary>
    public class SeedSourceEntry
    {
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
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
        /// License, author and credit line as the manifest declared them at install time, plus the source's
        /// display name and canonical website. A record that only pointed at sourceId/packId reports the
        /// rights of today's catalog for files downloaded years ago, and a source the user later removes
        /// leaves the project with installed files and no record of what they may be used for. These
        /// fields are the snapshot the project ships by: they feed ATTRIBUTIONS.md and Copy credit line.
        /// </summary>
        [JsonProperty("license")] public string License { get; set; }
        [JsonProperty("licenseUrl")] public string LicenseUrl { get; set; }
        [JsonProperty("author")] public string Author { get; set; }
        [JsonProperty("credit")] public string Credit { get; set; }
        [JsonProperty("sourceName")] public string SourceName { get; set; }
        [JsonProperty("sourceWebsite")] public string SourceWebsite { get; set; }

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
