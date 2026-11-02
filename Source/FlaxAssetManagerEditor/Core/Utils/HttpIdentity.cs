using System;
using System.Net.Http;

namespace FlaxAssetManager.Core.Utils
{
    /// <summary>
    /// The identity this plugin puts on every outbound request.
    /// <para>
    /// Every HTTP client in the plugin used to be a bare <see cref="HttpClient"/>, which sends
    /// <c>User-Agent: Mozilla/5.0 (Windows; …)</c> — .NET's default — and that says two untrue things
    /// about a request to a public asset host: it claims to be a browser, and it identifies no tool. A
    /// host that wants to be polite to automated traffic cannot tell FlaxAssetManager from a scraper,
    /// cannot publish a <c>robots.txt</c>/<c>Crawl-delay</c> rule addressed to it, and cannot contact
    /// anyone when a source starts hammering.
    /// </para>
    /// <para>
    /// Poly Haven's Public API terms are explicit about this: the API is free for commercial use, no key
    /// required, on condition that a consumer identifies itself with a unique User-Agent. That is the
    /// documented shape of the courtesy, so the plugin sends a self-identifying agent on <b>every</b>
    /// host, not just the one that asks — a manifest host, an asset CDN, a preview image, a GitHub
    /// raw file and a plugin's git host all get the same string.
    /// </para>
    /// <para>
    /// Create clients through <see cref="NewClient"/> rather than <c>new HttpClient()</c> so a new call
    /// site cannot silently regress to the browser-masquerading default.
    /// </para>
    /// </summary>
    internal static class HttpIdentity
    {
        /// <summary>Product token of the UA; keep in sync with the plugin's published version.</summary>
        public const string ProductName = "FlaxAssetManager";

        /// <summary>Version token of the UA; keep in sync with the plugin's published version.</summary>
        public const string ProductVersion = "1.0.0";

        /// <summary>Where a host operator can read what this tool does and find a contact route.</summary>
        public const string ProjectUrl = "https://github.com/envision3d/FlaxAssetManager";

        /// <summary>
        /// The full <c>User-Agent</c> header value: product/version, a comment with the project URL, and
        /// what the traffic is, so a webmaster reading an access log can classify it without guessing.
        /// </summary>
        public static readonly string UserAgent =
            $"{ProductName}/{ProductVersion} (+{ProjectUrl}; Flax Editor asset package manager)";

        /// <summary>
        /// A client that identifies itself. Every HTTP client in the plugin is built here; the
        /// <paramref name="timeout"/> stays per call site, because downloading a 300 MB archive and
        /// reading a 2 KB preview image are not the same request.
        /// </summary>
        public static HttpClient NewClient(TimeSpan timeout)
        {
            var client = new HttpClient { Timeout = timeout };
            // TryAddWithoutValidation, not UserAgent.ParseAdd: the comment form "(+url; text)" is legal
            // in a header value (RFC 7231 §3.2.6) and ParseAdd is strict about comment characters.
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            return client;
        }
    }
}
