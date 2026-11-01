using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace FlaxAssetManager.Core.Models
{
    /// <summary>
    /// Per-file asset type. Only raw Flax asset types are allowed; no catch-all.
    ///
    /// Deserialization accepts both the singular ("MaterialTexture") and plural
    /// ("MaterialTextures") spellings of the material-texture role via
    /// <see cref="FileAssetTypeConverter"/>, so manifests written with the singular spelling (as
    /// some community sources publish them) and the shipped examples (plural) both load. All
    /// other members use
    /// standard case-insensitive matching; unknown values still fail.
    /// </summary>
    [JsonConverter(typeof(FileAssetTypeConverter))]
    public enum FileAssetType
    {
        Texture = 0,
        Material = 1,
        MaterialNode = 2,
        Model = 3,
        Prefab = 4,
        Script = 5,
        AnimGraph = 6,
        Audio = 7,
        Shader = 8,
        /// <summary>
        /// Image(s) intended to be used to build a material (role detected from file name).
        /// </summary>
        MaterialTextures = 9,
    }

    /// <summary>
    /// JSON converter for <see cref="FileAssetType"/> that accepts both the singular and
    /// plural forms of the material-texture member. Everything else falls through to the
    /// standard case-insensitive enum matching. Unknown values raise a serialization error
    /// so malformed manifests are rejected rather than silently mis-parsed.
    /// </summary>
    public sealed class FileAssetTypeConverter : JsonConverter
    {
        private static readonly string[] MaterialTextureAliases =
        {
            "MaterialTexture",
            "MaterialTextures",
        };

        public override bool CanConvert(Type objectType) =>
            typeof(FileAssetType).IsAssignableFrom(objectType);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return Activator.CreateInstance(objectType);

            string text = reader.Value as string;
            if (string.IsNullOrEmpty(text))
                throw new JsonSerializationException($"Invalid FileAssetType: '{text}'.");

            // Enum.TryParse accepts numeric strings, so a manifest with "type": "5" (or a plain
            // number-as-string typo) would silently resolve to a real member. The spec requires
            // unknown values to fail loudly, so reject anything that is not a name first.
            char first = text[0];
            if ((first >= '0' && first <= '9') || first == '-' || first == '+')
                throw new JsonSerializationException($"Requested value '{text}' was not found.");

            // Fast path: exact (case-insensitive) match for every member.
            if (Enum.TryParse<FileAssetType>(text, true, out var parsed))
                return parsed;

            // Fallback: accept the singular/plural variant of the material-texture role.
            foreach (var alias in MaterialTextureAliases)
            {
                if (string.Equals(text, alias, StringComparison.OrdinalIgnoreCase))
                    return FileAssetType.MaterialTextures;
            }

            throw new JsonSerializationException($"Requested value '{text}' was not found.");
        }

        public override bool CanWrite => false;

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            => serializer.Serialize(writer, ((FileAssetType)value).ToString());
    }
}
