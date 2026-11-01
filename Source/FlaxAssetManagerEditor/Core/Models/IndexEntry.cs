using System.Collections.Generic;
using Newtonsoft.Json;

namespace FlaxAssetManager.Core.Models
{
    /// <summary>
    /// One entry of an index manifest: a pointer to another manifest URL. Entries install nothing - the
    /// user adds the URL as a source and the linked manifest is then fetched and validated on its own,
    /// so an entry is a lead, not a promise about what that manifest contains.
    /// </summary>
    public class IndexEntry
    {
        /// <summary>The sourceId the linked manifest declares. Keys the install record, so it must be
        /// unique inside the index and not collide with an already-configured source.</summary>
        [JsonProperty("sourceId", Required = Required.Always)]
        public string SourceId { get; set; }

        [JsonProperty("sourceName", Required = Required.Always)]
        public string SourceName { get; set; }

        /// <summary>Absolute http(s) URL of the linked manifest.</summary>
        [JsonProperty("url", Required = Required.Always)]
        public string Url { get; set; }

        /// <summary>Canonical site of the original source of the linked manifest's assets. Optional; the
        /// index details panel offers it as a clickable browser link. The plugin never fetches it, and an
        /// index cannot invent one for an entry - fall back to the linked manifest's own `website`.</summary>
        [JsonProperty("website", NullValueHandling = NullValueHandling.Ignore)]
        public string Website { get; set; }

        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
        public string Description { get; set; }

        [JsonProperty("tags", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Tags { get; set; }
    }
}
