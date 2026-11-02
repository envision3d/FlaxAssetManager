using System;
using FlaxAssetManager.Core.Models;

namespace FlaxAssetManager.Core.Utils
{
    /// <summary>
    /// Builds the one-line attribution a shipped project can paste into its credits.
    /// <para>
    /// A manifest carries three facts that only matter at shipping time: <c>license</c>, and - for the
    /// sources whose license requires credit - <c>author</c> and <c>credit</c>. Game Icons is the plain
    /// case: the assets are CC-BY-3.0, so every pack needs a per-author line in the shipped credits, and
    /// the license says the wording is up to the user as long as it names the creator. CC0 is the other
    /// case: no credit is owed, and a credit line is a courtesy, not an obligation.
    /// </para>
    /// <para>
    /// So the line is assembled from the manifest's own words, in this order: an explicit
    /// <c>credit</c> (pack, then source) is used verbatim, because a publisher who wrote the sentence the
    /// license asks for knows better than this plugin does. Otherwise the line is synthesised from
    /// pack name, author, license and the source's canonical website, which is enough to satisfy a
    /// CC-BY-style "give appropriate credit" clause.
    /// </para>
    /// <para>
    /// Nothing here parses license text, and nothing claims legal advice: it states what the manifest
    /// declared, at install time, from the install record - not a guess about today's catalog.
    /// </para>
    /// </summary>
    public static class CreditLine
    {
        /// <summary>
        /// Licenses that owe no credit for the asset itself, so a credit line is optional. Matched
        /// case-insensitively on the manifest's short id, with the spellings the shipped catalogs use.
        /// Anything else - including an empty license, which is worse than a restrictive one - is
        /// treated as owing credit, so the copy line and ATTRIBUTIONS.md always surface it.
        /// </summary>
        public static bool NoCreditRequired(string license)
        {
            if (string.IsNullOrWhiteSpace(license))
                return false;
            var l = license.Trim().Replace(" ", string.Empty).TrimEnd('.');
            return string.Equals(l, "CC0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(l, "CC0-1.0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(l, "PD", StringComparison.OrdinalIgnoreCase)
                || string.Equals(l, "PublicDomain", StringComparison.OrdinalIgnoreCase)
                || string.Equals(l, "PublicDomainMark", StringComparison.OrdinalIgnoreCase)
                || string.Equals(l, "Unlicense", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The credit line of one pack as currently offered by a source: <c>credit</c> verbatim when the
        /// manifest states one, otherwise name + author + license + the source's website.
        /// </summary>
        public static string Build(PackEntry pack, AssetSourceManifest manifest)
        {
            if (pack == null)
                return string.Empty;

            var explicitLine = FirstNonEmpty(pack.Credit, manifest?.Credit);
            if (!string.IsNullOrWhiteSpace(explicitLine))
                return explicitLine.Trim();

            var author = FirstNonEmpty(pack.Author, manifest?.Author);
            var license = FirstNonEmpty(pack.License);
            var licenseUrl = FirstNonEmpty(pack.LicenseUrl, manifest?.LicenseUrl);
            var sourceName = FirstNonEmpty(manifest?.SourceName);
            var sourceSite = FirstNonEmpty(manifest?.Website, manifest?.HomepageUrl);

            var sb = new System.Text.StringBuilder();
            sb.Append('"').Append(Safe(pack.Name)).Append('"');
            if (!string.IsNullOrWhiteSpace(author))
                sb.Append(" by ").Append(author.Trim());
            if (!string.IsNullOrWhiteSpace(license))
            {
                sb.Append(", license: ").Append(license.Trim());
                if (!string.IsNullOrWhiteSpace(licenseUrl))
                    sb.Append(" (").Append(licenseUrl.Trim()).Append(')');
            }
            else
            {
                sb.Append(", license: not declared by the source");
            }
            if (!string.IsNullOrWhiteSpace(sourceName) || !string.IsNullOrWhiteSpace(sourceSite))
            {
                sb.Append(" — source: ").Append(sourceName);
                if (!string.IsNullOrWhiteSpace(sourceSite))
                    sb.Append(sourceName == null ? string.Empty : " ").Append(sourceSite);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The credit line of an <b>installed</b> pack, built from the install record alone. This is what
        /// <c>ATTRIBUTIONS.md</c> is written from: after an uninstall of the source, or a catalog that
        /// changed the pack's license, the record still says what was installed and under what terms.
        /// </summary>
        public static string Build(InstalledEntry record)
        {
            if (record == null)
                return string.Empty;
            if (!string.IsNullOrWhiteSpace(record.Credit))
                return record.Credit.Trim();

            var sb = new System.Text.StringBuilder();
            sb.Append('"').Append(Safe(record.PackName ?? record.PackId)).Append('"');
            if (!string.IsNullOrWhiteSpace(record.Author))
                sb.Append(" by ").Append(record.Author.Trim());
            sb.Append(", license: ")
              .Append(string.IsNullOrWhiteSpace(record.License) ? "not declared by the source" : record.License.Trim());
            if (!string.IsNullOrWhiteSpace(record.LicenseUrl))
                sb.Append(" (").Append(record.LicenseUrl.Trim()).Append(')');
            if (!string.IsNullOrWhiteSpace(record.SourceName) || !string.IsNullOrWhiteSpace(record.SourceWebsite))
            {
                sb.Append(" — source: ").Append(record.SourceName);
                if (!string.IsNullOrWhiteSpace(record.SourceWebsite))
                    sb.Append(record.SourceName == null ? string.Empty : " ").Append(record.SourceWebsite);
            }
            else if (!string.IsNullOrWhiteSpace(record.SourceUrl))
            {
                sb.Append(" — source: ").Append(record.SourceUrl);
            }
            return sb.ToString();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            for (int i = 0; i < values.Length; i++)
                if (!string.IsNullOrWhiteSpace(values[i]))
                    return values[i];
            return null;
        }

        private static string Safe(string value) => (value ?? string.Empty).Trim();
    }
}
