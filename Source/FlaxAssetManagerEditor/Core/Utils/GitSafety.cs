using System;

namespace FlaxAssetManager.Core.Utils
{
    /// <summary>
    /// Validation for manifest-controlled values before they reach a shell-out to <c>git</c>, plus
    /// the path-containment rule shared by install, archive extraction and uninstall. Deliberately
    /// engine-free pure helpers so they can be exercised in isolation.
    /// </summary>
    public static class GitSafety
    {
        /// <summary>
        /// True only for <c>https://</c> URLs. Cloning over <c>http://</c>, <c>ssh://</c>,
        /// <c>file://</c> or a bare path lets a manifest point the installer at a local transport
        /// (and, with <c>ext::</c>, at a local command), so only plain HTTPS is accepted.
        /// </summary>
        public static bool IsHttpsUrl(string url)
        {
            return !string.IsNullOrWhiteSpace(url) && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Git branch/ref names accepted from a manifest: a conservative subset — letters, digits
        /// and <c>. _ - /</c>, with no leading <c>-</c>, <c>.</c> or <c>/</c>, no trailing <c>/</c>,
        /// and no <c>..</c>, <c>//</c> or <c>@{</c> sequences.
        /// <para>
        /// This is defence in depth: the installer also passes every git argument through
        /// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> with a <c>--</c>
        /// end-of-options marker, so a value that starts with <c>-</c> can never be parsed as a git
        /// option (e.g. <c>--upload-pack=&lt;cmd&gt;</c>, which runs a local program).
        /// </para>
        /// </summary>
        public static bool IsValidRefName(string refName)
        {
            if (string.IsNullOrWhiteSpace(refName))
                return false;
            if (refName.StartsWith("-") || refName.StartsWith(".") || refName.StartsWith("/") || refName.EndsWith("/"))
                return false;
            if (refName.Contains("..") || refName.Contains("//") || refName.Contains("@{"))
                return false;
            for (int i = 0; i < refName.Length; i++)
            {
                char c = refName[i];
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == '/'))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Commit ids accepted from a manifest: 7-40 hex characters (abbreviated SHA-1 through
        /// SHA-256 length). Anything else — including <c>-b</c> style payloads and ref names — is rejected.
        /// </summary>
        public static bool IsValidCommit(string commit)
        {
            if (commit == null || commit.Length < 7 || commit.Length > 40)
                return false;
            for (int i = 0; i < commit.Length; i++)
            {
                char c = commit[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// True when <paramref name="fullPath"/> sits inside <paramref name="rootFull"/>. A trailing
        /// separator is added to the root when the caller omits it, so <c>/proj/ba</c> can never
        /// match <c>/proj/bar/…</c>. The comparison is case-insensitive only on Windows: on a
        /// case-sensitive filesystem an ignore-case compare lets sibling paths through the guards.
        /// </summary>
        public static bool IsUnderRoot(string fullPath, string rootFull)
        {
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(rootFull))
                return false;
            char sep = System.IO.Path.DirectorySeparatorChar;
            if (rootFull[rootFull.Length - 1] != sep && rootFull[rootFull.Length - 1] != '/')
                rootFull += sep;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return fullPath.StartsWith(rootFull, comparison);
        }

        /// <summary>
        /// True when a path escapes its base through a <c>..</c> **segment**. The obvious test,
        /// <c>path.Contains("..")</c>, is too broad in content authoring: <c>brick..png</c> and
        /// <c>rock_.._01.jpg</c> are names people ship, and rejecting them made valid packs fail.
        /// Traversal is a segment, so segments are what get compared.
        /// </summary>
        public static bool ContainsTraversal(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            var segments = path.Split('/', '\\');
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i] == "..")
                    return true;
            }

            return false;
        }
    }
}
