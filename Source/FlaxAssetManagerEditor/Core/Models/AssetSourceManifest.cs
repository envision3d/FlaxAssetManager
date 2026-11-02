using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace FlaxAssetManager.Core.Models
{
    /// <summary>
    /// File format fetched from each external URL. One manifest per URL/source. Two kinds: a pack
    /// manifest (assets[] lists installable packs) and an index manifest (manifestKind "index", entries[]
    /// links other manifests, so one URL can be a table of contents the owner updates freely).
    /// </summary>
    public class AssetSourceManifest
    {
        [JsonProperty("sourceName", Required = Required.Always)]
        public string SourceName { get; set; }

        [JsonProperty("sourceId", Required = Required.Always)]
        public string SourceId { get; set; }

        [JsonProperty("sourceVersion", NullValueHandling = NullValueHandling.Ignore)]
        public string SourceVersion { get; set; }

        [JsonProperty("homepageUrl", NullValueHandling = NullValueHandling.Ignore)]
        public string HomepageUrl { get; set; }

        /// <summary>Canonical site of the original source of the assets this manifest catalogues - for
        /// attribution and reference. Not the manifest author's page (that is homepageUrl) and never used
        /// for fetching; see docs/MANIFEST_SPEC.md §3.</summary>
        [JsonProperty("website", NullValueHandling = NullValueHandling.Ignore)]
        public string Website { get; set; }

        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
        public string Description { get; set; }
        /// <summary>
        /// Source-wide defaults for pack-level <c>author</c>, <c>credit</c> and <c>licenseUrl</c>, used by
        /// the packs of this manifest that do not declare their own. A source whose every pack comes from
        /// one publisher (Kenney, Quaternius, Twemoji) states the credit obligation once here; a source
        /// whose license varies per pack (SoundBible, Game Icons) states it per pack and leaves these off.
        /// Written into <c>ATTRIBUTIONS.md</c> and the install record, never used for fetching.
        /// </summary>
        [JsonProperty("author", NullValueHandling = NullValueHandling.Ignore)]
        public string Author { get; set; }

        [JsonProperty("credit", NullValueHandling = NullValueHandling.Ignore)]
        public string Credit { get; set; }

        [JsonProperty("licenseUrl", NullValueHandling = NullValueHandling.Ignore)]
        public string LicenseUrl { get; set; }

        [JsonProperty("assets", NullValueHandling = NullValueHandling.Ignore)]
        public List<PackEntry> Assets { get; set; }

        [JsonProperty("manifestKind", NullValueHandling = NullValueHandling.Ignore)]
        public string ManifestKind { get; set; }

        [JsonProperty("entries", NullValueHandling = NullValueHandling.Ignore)]
        public List<IndexEntry> Entries { get; set; }

        /// <summary>
        /// True for an index manifest: it links other manifests and carries no assets of its own. The
        /// validator is what rejects a malformed one; this flag is what the UI branches on.
        /// </summary>
        [JsonIgnore]
        public bool IsIndex => string.Equals(ManifestKind, "index", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fetched result wrapper: keeps URL association.
    /// </summary>
    public class FetchedSource
    {
        public string Url { get; set; }
        public AssetSourceManifest Manifest { get; set; }
        /// <summary>Raw manifest text as fetched (before parsing/validation); shown by the View JSON button.</summary>
        public string RawJson { get; set; }
        public string Error { get; set; }
        public bool Success => Error == null && Manifest != null;
    }
}
