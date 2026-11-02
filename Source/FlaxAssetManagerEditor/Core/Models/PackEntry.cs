using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace FlaxAssetManager.Core.Models
{
    /// <summary>
    /// One installable pack from a source manifest.
    /// </summary>
    public class PackEntry
    {
        [JsonProperty("id", Required = Required.Always)]
        public string Id { get; set; }

        [JsonProperty("name", Required = Required.Always)]
        public string Name { get; set; }

        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
        public string Description { get; set; }

        [JsonProperty("packType")]
        public PackType PackType { get; set; } = PackType.AssetPack;

        [JsonProperty("version", Required = Required.Always)]
        public string Version { get; set; }

        [JsonProperty("category", NullValueHandling = NullValueHandling.Ignore)]
        public string Category { get; set; }

        [JsonProperty("tags", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Tags { get; set; }

        [JsonProperty("previewUrl", NullValueHandling = NullValueHandling.Ignore)]
        public string PreviewUrl { get; set; }

        [JsonProperty("license", NullValueHandling = NullValueHandling.Ignore)]
        public string License { get; set; }

        [JsonProperty("licenseUrl", NullValueHandling = NullValueHandling.Ignore)]
        public string LicenseUrl { get; set; }

        /// <summary>
        /// Who made this pack, in the words the upstream publisher uses. Separate from
        /// <see cref="Credit"/> because a manifest author writes the name once and the credit line is
        /// derived from it together with <see cref="License"/>, not typed out per pack.
        /// </summary>
        [JsonProperty("author", NullValueHandling = NullValueHandling.Ignore)]
        public string Author { get; set; }

        /// <summary>
        /// The attribution sentence the license asks for, verbatim, for licenses that require credit
        /// (CC-BY, Apache-2.0 notices, a custom "commercial with attribution" license). It is copied
        /// into the install record, surfaced as <b>Copy credit line</b>, and written into the project's
        /// <c>ATTRIBUTIONS.md</c>, so shipping a project can satisfy the obligation without the user
        /// reconstructing the wording from license legalese. A CC0 pack leaves it empty.
        /// </summary>
        [JsonProperty("credit", NullValueHandling = NullValueHandling.Ignore)]
        public string Credit { get; set; }

        /// <summary>
        /// For assetPack: explicit list of raw asset files.
        /// For pluginProject/archivePack: null (use Distribution instead).
        /// </summary>
        [JsonProperty("files", NullValueHandling = NullValueHandling.Ignore)]
        public List<FileEntry> Files { get; set; }

        /// <summary>
        /// Optional alternative file sets (e.g. texture resolutions) for assetPack.
        /// When <see cref="Files"/> is present it is the default set; otherwise the first
        /// variant (declaration order) is the default. Absent = no variants.
        /// </summary>
        [JsonProperty("variants", NullValueHandling = NullValueHandling.Ignore)]
        public List<PackVariant> Variants { get; set; }

        /// <summary>
        /// For pluginProject: git repository distribution.
        /// For archivePack: .zip archive distribution (kind "archive").
        /// </summary>
        [JsonProperty("distribution", NullValueHandling = NullValueHandling.Ignore)]
        public DistributionInfo Distribution { get; set; }
        /// <summary>
        /// Files to install for a variant id; null id ⇒ default set (base files if present, else first variant).
        /// </summary>
        public List<FileEntry> GetEffectiveFiles(string variantId)
        {
            if (string.IsNullOrEmpty(variantId))
            {
                if (Files != null && Files.Count > 0) return Files;
                return Variants != null && Variants.Count > 0 ? Variants[0].Files : null;
            }
            for (int i = 0; i < (Variants?.Count ?? 0); i++)
                if (string.Equals(Variants[i].Id, variantId, StringComparison.Ordinal))
                    return Variants[i].Files;
            return GetEffectiveFiles(null);
        }
    }

    /// <summary>
    /// An alternative file set of an asset pack (e.g. a texture resolution: 1k/2k/4k).
    /// </summary>
    public class PackVariant
    {
        [JsonProperty("id", Required = Required.Always)]
        public string Id { get; set; }

        [JsonProperty("name", Required = Required.Always)]
        public string Name { get; set; }

        [JsonProperty("files", Required = Required.Always)]
        public List<FileEntry> Files { get; set; }
    }

    /// <summary>
    /// How a pack is distributed. pluginProject uses kind "gitRepo"; archivePack uses
    /// kind "archive" pointing at a .zip.
    /// </summary>
    public class DistributionInfo
    {
        [JsonProperty("kind", Required = Required.Always)]
        public string Kind { get; set; } // expect "gitRepo" or "archive"

        [JsonProperty("url", Required = Required.Always)]
        public string Url { get; set; }

        [JsonProperty("branch", NullValueHandling = NullValueHandling.Ignore)]
        public string Branch { get; set; }

        [JsonProperty("commit", NullValueHandling = NullValueHandling.Ignore)]
        public string Commit { get; set; }

        /// <summary>
        /// Optional SHA-256 (hex) of the archive file, verified before extraction.
        /// </summary>
        [JsonProperty("sha256", NullValueHandling = NullValueHandling.Ignore)]
        public string Sha256 { get; set; }
    }
}
