using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FlaxEngine;

namespace FlaxAssetManager.Core.Utils
{
    /// <summary>
    /// Size-bounded, retrying HTTP reads.
    /// <para>
    /// A manifest is remote input (D12), and so is everything it points at. Pack file downloads were
    /// already streamed to disk with a cap (<c>AssetInstallerService.MaxDownloadBytes</c>), but the
    /// small reads - manifest, index, preview image, preview audio - went through
    /// <see cref="HttpClient.GetStringAsync(string)"/> and <see cref="HttpClient.GetByteArrayAsync(string)"/>,
    /// which buffer the whole body in memory with no limit. A hostile or misconfigured host answering
    /// with a gigabyte is then an out-of-memory editor, and these reads run on every <b>Refresh</b>,
    /// not just on install.
    /// </para>
    /// <para>
    /// Every read here declares a ceiling up front and stops when the response exceeds it, so the
    /// caller gets a validation-style failure instead of a stalled editor. The ceiling is checked
    /// against <c>Content-Length</c> when the host sends one, and again while copying, because a
    /// chunked response can lie about its length.
    /// </para>
    /// <para>
    /// The same entry points also carry the network politeness (D11): a transient failure (429, 5xx,
    /// a dropped connection) is retried with backoff and honours <c>Retry-After</c>, and a manifest
    /// fetch can send <c>If-None-Match</c> so a <b>Refresh</b> of an unchanged source costs a 304
    /// instead of re-downloading the document. Both matter because Refresh hits every source at once;
    /// one flaky host should not turn into a red X, and a dozen identical manifests should not be
    /// re-fetched on every button press.
    /// </para>
    /// </summary>
    internal static class HttpStream
    {
        /// <summary>Manifest body, including an index (the kind is only known after parsing).</summary>
        public const long MaxManifestBodyBytes = 16L * 1024L * 1024L;

        /// <summary>
        /// Ceiling for a single asset source manifest. Kept below the index ceiling because an index
        /// lists other manifests and is legitimately the larger document.
        /// </summary>
        public const long MaxManifestTextChars = 8L * 1024L * 1024L;

        /// <summary>Preview image / preview audio: display input, never a pack.</summary>
        public const long MaxPreviewBytes = 64L * 1024L * 1024L;

        /// <summary>
        /// Attempts per request, counting the first. Small on purpose: Refresh fans out over all
        /// sources, and a source that is really down should fail fast enough that the window stays
        /// responsive.
        /// </summary>
        public const int MaxAttempts = 3;

        /// <summary>First backoff step; doubled per attempt, with jitter on top.</summary>
        public static readonly TimeSpan BaseRetryDelay = TimeSpan.FromMilliseconds(400);

        /// <summary>Upper bound on a server-supplied Retry-After that we are willing to sleep for.</summary>
        public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);

        /// <summary>Thrown when a response is larger than the caller's declared ceiling.</summary>
        public class OversizedResponseException : Exception
        {
            public OversizedResponseException(string url, long maxBytes)
            : base($"Response from '{url}' is larger than the {maxBytes / (1024L * 1024L)} MiB limit.")
            {
            }
        }

        /// <summary>
        /// A text fetch outcome. <see cref="NotModified"/> means the server answered the caller's
        /// <c>If-None-Match</c> validator, so <see cref="Text"/> is empty and the caller keeps its
        /// cached copy; <see cref="ETag"/> is the new validator to cache with it.
        /// </summary>
        public readonly struct TextFetchResult
        {
            public readonly string Text;
            public readonly string ETag;
            public readonly bool NotModified;

            public TextFetchResult(string text, string etag, bool notModified)
            {
                Text = text;
                ETag = etag;
                NotModified = notModified;
            }
        }

        /// <summary>
        /// Downloads a text body with a hard ceiling, no caching. Source manifests use
        /// <see cref="FetchTextAsync"/> (same ceiling, plus retry and conditional GET); this is the
        /// plain form for a one-off read that has no cache to keep valid.
        /// </summary>
        public static async Task<string> DownloadTextAsync(HttpClient client, string url, long maxBytes, CancellationToken ct = default)
        {
            var bytes = await DownloadBytesAsync(client, url, maxBytes, ct);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// Downloads a manifest/index with a hard ceiling and conditional-get support. Pass the cached
        /// validator as <paramref name="ifNoneMatch"/>; a 304 comes back as
        /// <see cref="TextFetchResult.NotModified"/> with the body untouched.
        /// </summary>
        public static async Task<TextFetchResult> FetchTextAsync(HttpClient client, string url, long maxBytes, string ifNoneMatch = null, CancellationToken ct = default)
        {
            using (var response = await SendWithRetryAsync(client, url, ifNoneMatch, ct))
            {
                // A conditional GET answers 304 with an empty body. That is success for the caller, and
                // EnsureSuccessStatusCode would treat it as failure, so it is handled before that call.
                if (response.StatusCode == HttpStatusCode.NotModified)
                    return new TextFetchResult(null, response.Headers.ETag?.ToString() ?? ifNoneMatch, true);

                response.EnsureSuccessStatusCode();
                var text = await ReadCappedTextAsync(response, maxBytes, url, ct);
                return new TextFetchResult(text, response.Headers.ETag?.ToString(), false);
            }
        }

        /// <summary>
        /// Downloads a byte body (preview image, preview audio) with a hard ceiling.
        /// </summary>
        public static async Task<byte[]> DownloadBytesAsync(HttpClient client, string url, long maxBytes, CancellationToken ct = default)
        {
            using (var response = await SendWithRetryAsync(client, url, null, ct))
            {
                response.EnsureSuccessStatusCode();

                // Cheap refusal first, when the host tells us the size up front.
                var declared = response.Content.Headers.ContentLength;
                if (declared.HasValue && declared.Value > maxBytes)
                    throw new OversizedResponseException(url, maxBytes);

                using (var content = await response.Content.ReadAsStreamAsync())
                using (var buffer = new MemoryStream(declared.HasValue ? (int)Math.Min(declared.Value, maxBytes) : 81920))
                {
                    await CopyCappedAsync(content, buffer, maxBytes, url, ct);
                    return buffer.ToArray();
                }
            }
        }

        /// <summary>
        /// A local counterpart of the ceiling, for the <c>file://</c> manifest path: a source URL that
        /// resolves to a gigabyte file is refused for the same reason a remote one is.
        /// </summary>
        public static string ReadAllTextCapped(string fullPath, long maxBytes)
        {
            var length = new FileInfo(fullPath).Length;
            if (length > maxBytes)
                throw new OversizedResponseException(fullPath, maxBytes);
            return File.ReadAllText(fullPath);
        }

        /// <summary>
        /// Issues the GET, retrying transient failures with exponential backoff.
        /// <para>
        /// "Transient" is deliberately narrow: 408/425/429 and 5xx, plus transport-level failures
        /// (connection reset, DNS hiccup) and a timeout that the caller did not ask for. A 404, a 403
        /// or an oversize response is a fact about the source, not about the network, so it is returned
        /// to the caller immediately - retrying those multiplies a broken user experience into a slow
        /// one, and Refresh iterates over every source.
        /// </para>
        /// </summary>
        private static async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient client, string url, string ifNoneMatch, CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                HttpResponseMessage response = null;
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (!string.IsNullOrWhiteSpace(ifNoneMatch))
                        {
                            // Send back the validator the host gave us. `Headers.ETag.ToString()` renders a
                            // weak validator as `W/"..."` - the only kind nginx and CDNs answer with for
                            // static files - and the EntityTagHeaderValue **ctor** takes the quoted tag plus
                            // a separate isWeak flag, so handing it that rendered header throws
                            // FormatException ("The format of value 'W/"..."' is invalid.") and the whole
                            // source turns red on the second Refresh. TryParse understands the `W/` prefix;
                            // TryAddWithoutValidation covers any other form a host invents. Weak matching
                            // ignores the indicator (RFC 9110 §8.8.3.2), so a strong/weak ping-pong is fine.
                            if (EntityTagHeaderValue.TryParse(ifNoneMatch, out var etag))
                                request.Headers.IfNoneMatch.Add(etag);
                            else
                                request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
                        }

                        response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    }

                    if (!IsTransientStatus(response.StatusCode) || attempt >= MaxAttempts)
                        return response;

                    var status = (int)response.StatusCode;
                    var wait = RetryDelay(response, attempt);
                    response.Dispose();
                    Debug.Log($"FlaxAssetManager: '{url}' returned {status}; retrying in {wait.TotalMilliseconds:F0} ms (attempt {attempt + 1}/{MaxAttempts}).");
                    await Task.Delay(wait, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // HttpClient's own timeout surfaces as a cancellation the caller did not request.
                    response?.Dispose();
                    if (attempt >= MaxAttempts)
                        throw;
                    var wait = RetryDelay(null, attempt);
                    Debug.LogWarning($"FlaxAssetManager: request to '{url}' timed out; retrying in {wait.TotalMilliseconds:F0} ms (attempt {attempt + 1}/{MaxAttempts}).");
                    await Task.Delay(wait, ct);
                }
                catch (HttpRequestException)
                {
                    response?.Dispose();
                    if (attempt >= MaxAttempts)
                        throw;
                    var wait = RetryDelay(null, attempt);
                    Debug.LogWarning($"FlaxAssetManager: request to '{url}' failed; retrying in {wait.TotalMilliseconds:F0} ms (attempt {attempt + 1}/{MaxAttempts}).");
                    await Task.Delay(wait, ct);
                }
            }
        }

        private static bool IsTransientStatus(HttpStatusCode code)
        {
            var n = (int)code;
            if (n >= 500)
                return true;
            return n == 408 /* Request Timeout */
                || n == 425 /* Too Early */
                || n == 429; /* Too Many Requests */
        }

        /// <summary>
        /// Backoff for the next attempt: exponential from <see cref="BaseRetryDelay"/>, plus a small
        /// jitter so several sources behind the same host do not retry in lockstep, capped at
        /// <see cref="MaxRetryDelay"/>. A server <c>Retry-After</c> wins when it is shorter than the
        /// cap; a longer one is ignored, because blocking the refresh loop for minutes on one source
        /// is worse than one extra request.
        /// </summary>
        private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
        {
            var exponential = TimeSpan.FromMilliseconds(BaseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
            var jitter = TimeSpan.FromMilliseconds(new Random(System.Environment.TickCount ^ attempt).Next(0, 250));
            var delay = exponential + jitter;

            var retryAfter = response?.Headers.RetryAfter;
            if (retryAfter != null)
            {
                if (retryAfter.Delta.HasValue)
                    delay = retryAfter.Delta.Value;
                else if (retryAfter.Date.HasValue)
                    delay = retryAfter.Date.Value - DateTime.UtcNow;
            }

            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;
            if (delay > MaxRetryDelay)
                delay = MaxRetryDelay;
            return delay;
        }

        private static async Task<string> ReadCappedTextAsync(HttpResponseMessage response, long maxBytes, string url, CancellationToken ct)
        {
            var declared = response.Content.Headers.ContentLength;
            if (declared.HasValue && declared.Value > maxBytes)
                throw new OversizedResponseException(url, maxBytes);

            using (var content = await response.Content.ReadAsStreamAsync())
            using (var buffer = new MemoryStream(declared.HasValue ? (int)Math.Min(declared.Value, maxBytes) : 81920))
            {
                await CopyCappedAsync(content, buffer, maxBytes, url, ct);
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        private static async Task CopyCappedAsync(Stream source, Stream destination, long maxBytes, string url, CancellationToken ct)
        {
            var chunk = new byte[81920];
            long total = 0L;
            while (true)
            {
                int read = await source.ReadAsync(chunk, 0, chunk.Length, ct);
                if (read <= 0)
                    break;

                total += read;
                if (total > maxBytes)
                    throw new OversizedResponseException(url, maxBytes);

                await destination.WriteAsync(chunk, 0, read, ct);
            }
        }
    }
}
