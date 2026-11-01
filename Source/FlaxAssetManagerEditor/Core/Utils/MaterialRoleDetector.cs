using System.IO;
using System.Text.RegularExpressions;

namespace FlaxAssetManager.Core.Utils
{
    /// <summary>
    /// PBR material texture role, inferred from a texture file's name.
    /// </summary>
    public enum MaterialRole
    {
        /// <summary>No recognizable role token in the file name.</summary>
        None,
        /// <summary>Base color / albedo / diffuse / color.</summary>
        BaseColor,
        /// <summary>Normal map.</summary>
        Normal,
        /// <summary>Metallic / metalness.</summary>
        Metallic,
        /// <summary>Roughness.</summary>
        Roughness,
        /// <summary>Ambient occlusion.</summary>
        AmbientOcclusion,
        /// <summary>Emissive / self-illumination.</summary>
        Emissive,
        /// <summary>Height / displacement.</summary>
        Height,
        /// <summary>Opacity / alpha / mask.</summary>
        Opacity,
        /// <summary>
        /// Combined ARM map (R = AmbientOcclusion, G = Roughness, B = Metallic).
        /// Wired to the AmbientOcclusion, Roughness and Metallic inputs at once, each from
        /// its own channel.
        /// </summary>
        Combined,
    }

    /// <summary>
    /// Detects the PBR role of a material texture from its file name.
    ///
    /// Matches common naming conventions: short tokens (diff / nor / arm / sso / disp) plus
    /// generic names (albedo / normal / metallic / roughness / ao / emissive / height /
    /// opacity). A token matches when it is delimited by anything
    /// other than a letter or digit, so "diff_8k" matches "diff" but "albedo" does not
    /// match "ao" and "sso" is not a substring of a longer word.
    /// </summary>
    public static class MaterialRoleDetector
    {
        // Role groups are checked in order; the first group whose token appears wins.
        // Combined (arm) is checked first because a combined map carries three roles.
        private static readonly (MaterialRole Role, Regex Pattern)[] RolePatterns =
        {
            (MaterialRole.Combined,          Pattern("arm")),
            (MaterialRole.BaseColor,         Pattern(@"base[_ -]?(?:color|colour)", "albedo", "diffuse", "diff", "color", "col")),
            (MaterialRole.Normal,            Pattern("normal", "nor", "nrm")),
            (MaterialRole.Metallic,          Pattern("metallic", "metalness", "metal", "met")),
            (MaterialRole.Roughness,         Pattern("roughness", "rough", "rgh")),
            (MaterialRole.AmbientOcclusion,  Pattern("ambientocclusion", @"ambient[_ -]?occlusion", "occlusion", "sso", "ao")),
            (MaterialRole.Emissive,          Pattern("emissive", "emission", "emis", "glow")),
            (MaterialRole.Height,            Pattern("displacement", "disp", "height", "hgt")),
            (MaterialRole.Opacity,           Pattern("opacity", "opa", "alpha", "mask")),
        };

        private static Regex Pattern(params string[] tokens)
        {
            string joined = string.Join("|", tokens);
            return new Regex($@"(?<![A-Za-z0-9])(?:(?:{joined})(?:[_ -]?map)?)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        /// <summary>
        /// Detects the PBR role from a texture file name, or a path whose final segment is
        /// the file name. Returns <see cref="MaterialRole.None"/> when no role token matches.
        /// </summary>
        public static MaterialRole Detect(string fileNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(fileNameOrPath))
                return MaterialRole.None;

            string fileName = Path.GetFileName(fileNameOrPath);
            string name = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrEmpty(name))
                name = fileName;

            foreach (var (role, pattern) in RolePatterns)
            {
                if (pattern.IsMatch(name))
                    return role;
            }
            return MaterialRole.None;
        }

    }
}
