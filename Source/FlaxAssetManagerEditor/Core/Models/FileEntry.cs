using Newtonsoft.Json;

namespace FlaxAssetManager.Core.Models
{
    /// <summary>
    /// Single file inside an assetPack.
    /// </summary>
    public class FileEntry
    {
        [JsonProperty("url", Required = Required.Always)]
        public string Url { get; set; }

        [JsonProperty("path", Required = Required.Always)]
        public string Path { get; set; }

        [JsonProperty("type", Required = Required.Always)]
        public FileAssetType Type { get; set; }

        [JsonProperty("sha256", NullValueHandling = NullValueHandling.Ignore)]
        public string Sha256 { get; set; }
    }
}
