using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using FlaxEngine;
using Newtonsoft.Json;
using FlaxAssetManager.Core.Models;
using FlaxAssetManager.Core.Utils;

namespace FlaxAssetManager.Core.Services
{
    /// <summary>
    /// Fetches and validates manifests from user-configured URLs. Sources kept separate.
    /// </summary>
    public class AssetSourceManager : IDisposable
    {
        private readonly SettingsService _settings;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerSettings _jsonSettings;

        public List<FetchedSource> LastFetched { get; private set; } = new List<FetchedSource>();

        public event Action Fetched;
        /// <summary>
        /// Per-URL conditional-GET state (D11): the ETag a source last answered with, plus the body it
        /// belonged to. In memory only - a restart refetches, and a stale validator on a server that
        /// dropped the ETag just costs one full response. Bounded by the number of sources a user adds.
        /// </summary>
        private class CachedManifest
        {
            public string ETag;
            public string RawJson;
        }

        private readonly Dictionary<string, CachedManifest> _manifestCache = new Dictionary<string, CachedManifest>(StringComparer.Ordinal);
        private readonly object _cacheLock = new object();

        /// <summary>Drops all conditional-GET validators, e.g. after the source list is edited.</summary>
        public void ClearCache()
        {
            lock (_cacheLock)
                _manifestCache.Clear();
        }

        // Strict allowed extensions for assetPack files
        private static readonly HashSet<string> AllowedScriptExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };

        // Forbid non-asset extensions
        private static readonly HashSet<string> ForbiddenExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cpp",".h",".hpp",".c",".cc",".inl",".md",".txt",".json",".bin",".dat",".gitignore",".gitattributes"
        };

        // Executables, native libraries and interpreter scripts. Only consulted for archive packs, whose
        // entries have no manifest type to validate (see CheckArchiveEntryName); the assetPack rules
        // above are unchanged so real packs that already pass keep passing.
        private static readonly HashSet<string> ArchiveBinaryExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dll",".exe",".so",".dylib",".ocx",".sys",".msi",".jar",".apk",".wasm",".ko",".elf",
            ".py",".rb",".pl",".sh",".bash",".ps1",".psm1",".bat",".cmd",".vbs",".lua",".ts",".js"
        };

        // Image extensions allowed for MaterialTextures files
        private static readonly HashSet<string> MaterialTextureExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png",".jpg",".jpeg",".tga",".bmp",".dds",".hdr",".exr",".psd",".tif",".tiff"
        };

        public AssetSourceManager(SettingsService settings)
        {
            _settings = settings;
            // Self-identifying UA: manifest hosts are publishers, not CDNs serving browsers, and the
            // polite contract with them (rate limits, contact route) starts with saying who is asking.
            _httpClient = HttpIdentity.NewClient(TimeSpan.FromSeconds(30));
            _jsonSettings = new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
            };
        }

        /// <summary>
        /// Fetches every enabled source. <c>force</c> drops the conditional-GET validators first, so a
        /// refresh after an edit - or a "something looks stale" refresh - reads every manifest in full
        /// even when a host advertises ETags.
        /// </summary>
        public async Task FetchAllAsync(bool force = false)
        {
            _settings.ReloadSources();
            if (force)
                ClearCache();
            var results = new List<FetchedSource>();
            foreach (var entry in _settings.SourceConfig.Sources)
            {
                if (!entry.Enabled)
                    continue;
                var fetched = await FetchOneAsync(entry.Url);
                results.Add(fetched);
            }
            LastFetched = results;
            Fetched?.Invoke();
        }

        public async Task<FetchedSource> FetchOneAsync(string url)
        {
            var result = new FetchedSource { Url = url };
            try
            {
                string json = null;
                string expanded = url?.Trim() ?? string.Empty;
                // Expand macros like $(ProjectPath) / $(EnginePath) / $(PluginPath) and file://
                try
                {
                    if (expanded.Contains("$(ProjectPath)", StringComparison.OrdinalIgnoreCase))
                        expanded = expanded.Replace("$(ProjectPath)", Globals.ProjectFolder, StringComparison.OrdinalIgnoreCase);
                    if (expanded.Contains("$(EnginePath)", StringComparison.OrdinalIgnoreCase))
                    {
                        var enginePath = Globals.StartupFolder ?? string.Empty;
                        // Try to resolve Flax.flaxproj location via Globals.EngineContentFolder parent
                        if (Directory.Exists(Path.Combine(Globals.EngineContentFolder ?? "", "..")) )
                            enginePath = Path.GetFullPath(Path.Combine(Globals.EngineContentFolder, ".."));
                        expanded = expanded.Replace("$(EnginePath)", enginePath, StringComparison.OrdinalIgnoreCase);
                    }
                    // $(PluginPath) = this plugin's folder, resolved by the plugin seed-file discovery
                    // (its own project file, contained to the open project), so a plugin that ships a
                    // manifest can be pointed at it without naming a folder - D16: a folder name is not
                    // identity. Left unresolved, the macro stays in the path and the source reports a
                    // fetch failure, rather than reading somewhere nobody agreed on.
                    if (expanded.Contains("$(PluginPath)", StringComparison.OrdinalIgnoreCase))
                    {
                        var pluginFolder = _settings.PluginFolderFullPath;
                        if (!string.IsNullOrEmpty(pluginFolder))
                            expanded = expanded.Replace("$(PluginPath)", pluginFolder, StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch { }

                bool isFile = false;
                string filePath = null;
                if (expanded.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    filePath = expanded.Substring(7);
                    // file:// + absolute may have leading /
                    if (filePath.StartsWith("//"))
                        filePath = filePath.Substring(2);
                    isFile = true;
                }
                else if (File.Exists(expanded))
                {
                    filePath = expanded;
                    isFile = true;
                }
                else if (!expanded.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !expanded.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    // Try relative to project folder
                    var rel = Path.Combine(Globals.ProjectFolder, expanded.TrimStart('/', '\\'));
                    if (File.Exists(rel))
                    {
                        filePath = rel;
                        isFile = true;
                    }
                }

                if (isFile)
                {
                    // Handle file:// with possible URI encoding
                    try { filePath = Uri.UnescapeDataString(filePath); } catch { }
                    json = HttpStream.ReadAllTextCapped(filePath, HttpStream.MaxManifestBodyBytes);
                }
                else
                {
                    // Capped read: GetStringAsync buffers the whole body, so a hostile host could make
                    // the editor allocate as much as it likes on every Refresh (HttpStream, D11/D12).
                    // The fetch is conditional: a source that has not changed answers 304, and Refresh
                    // (which hits every source, on every button press) costs a header round-trip
                    // instead of re-downloading and re-parsing the same document.
                    CachedManifest cached = null;
                    lock (_cacheLock)
                        _manifestCache.TryGetValue(expanded, out cached);

                    var fetched = await HttpStream.FetchTextAsync(_httpClient, expanded, HttpStream.MaxManifestBodyBytes, cached?.ETag);
                    json = fetched.NotModified && cached != null ? cached.RawJson : fetched.Text;

                    if (!string.IsNullOrEmpty(fetched.ETag) && json != null)
                    {
                        lock (_cacheLock)
                            _manifestCache[expanded] = new CachedManifest { ETag = fetched.ETag, RawJson = json };
                    }
                }

                // Keep the raw text for the "View JSON" button (valid or not).
                result.RawJson = json;
                var manifest = JsonConvert.DeserializeObject<AssetSourceManifest>(json, _jsonSettings);
                if (manifest == null)
                    throw new Exception("Manifest is null after deserialization.");
                // Normalize file paths to be relative to the pack root (strip a redundant
                // pack-folder prefix from manifests that embed it) so the installer, material
                // creator and role detection all agree on one on-disk layout.
                NormalizePackPaths(manifest);
                // The body was read under the index ceiling because the kind is only known here: an
                // asset source manifest gets the tighter budget of the two (HttpStream caps).
                if (!manifest.IsIndex && json.Length > HttpStream.MaxManifestTextChars)
                    throw new Exception($"Manifest is larger than the {HttpStream.MaxManifestTextChars / (1024L * 1024L)} MiB limit for a single source (index manifests may be up to {HttpStream.MaxManifestBodyBytes / (1024L * 1024L)} MiB).");
                Debug.Log($"FlaxAssetManager: Manifest from {url} is {(manifest.IsIndex ? $"an index linking {(manifest.Entries?.Count ?? 0)} manifests." : $"{(manifest.Assets?.Count ?? 0)} assets.")}");
                // Validate
                var validationError = ValidateManifest(manifest);
                if (validationError != null)
                    throw new Exception(validationError);
                result.Manifest = manifest;
            }
            catch (Exception e)
            {
                result.Error = e.Message;
                Debug.LogError($"FlaxAssetManager: Failed to fetch {url}: {e.Message}");
            }
            return result;
        }

        /// <summary>
        /// Strict validation per design: assetPack only raw assets, pluginProject via gitRepo.
        /// </summary>
        public static string ValidateManifest(AssetSourceManifest manifest)
        {
            // An index manifest is a different shape: links, not assets, so it validates separately.
            if (manifest.IsIndex)
                return ValidateIndex(manifest);

            if (string.IsNullOrWhiteSpace(manifest.SourceName))
                return "sourceName is required.";
            if (string.IsNullOrWhiteSpace(manifest.SourceId))
                return "sourceId is required.";
            if (manifest.Assets == null || manifest.Assets.Count == 0)
                return "assets array is required and must not be empty.";

            // packId keys the install folder and the install record, so two packs sharing one id would
            // merge their records, and one uninstall would delete files belonging to the other. This is the
            // rule the index validator already applies to sourceId; a manifest is remote input, so the
            // uniqueness is checked here rather than trusted. Duplicates are ordinal, like the keys are.
            var packIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pack in manifest.Assets)
            {
                if (string.IsNullOrWhiteSpace(pack.Id))
                    return $"Pack missing id (pack name: {pack.Name})";

                if (!packIds.Add(pack.Id))
                    return $"Duplicate pack id '{pack.Id}' in this manifest: packId keys the install folder and the install record, so two packs sharing one would merge their records (rename one).";
                if (string.IsNullOrWhiteSpace(pack.Name))
                    return $"Pack {pack.Id} missing name.";
                if (string.IsNullOrWhiteSpace(pack.Version))
                    return $"Pack {pack.Id} missing version.";

                if (pack.PackType == PackType.AssetPack)
                {
                    bool hasFiles = pack.Files != null && pack.Files.Count > 0;
                    bool hasVariants = pack.Variants != null && pack.Variants.Count > 0;
                    if (!hasFiles && !hasVariants)
                        return $"Pack {pack.Id}: packType assetPack requires non-empty files[] (or non-empty variants[]).";
                    if (pack.Distribution != null)
                        return $"Pack {pack.Id}: assetPack must not have distribution (use files[]).";
                    if (hasFiles)
                    {
                        var ferr = ValidateFiles(pack.Id, pack.Files);
                        if (ferr != null) return ferr;
                    }
                    if (pack.Variants != null)
                        foreach (var v in pack.Variants)
                        {
                            if (string.IsNullOrWhiteSpace(v.Id)) return $"Pack {pack.Id}: variant missing id.";
                            if (string.IsNullOrWhiteSpace(v.Name)) return $"Pack {pack.Id}: variant '{v.Id}' missing name.";
                            if (pack.Variants.FindIndex(x => string.Equals(x.Id, v.Id, StringComparison.Ordinal)) != pack.Variants.IndexOf(v))
                                return $"Pack {pack.Id}: duplicate variant id '{v.Id}'.";
                            if (v.Files == null || v.Files.Count == 0) return $"Pack {pack.Id}: variant '{v.Id}' requires non-empty files[].";
                            var verr = ValidateFiles(pack.Id, v.Files, $"variant '{v.Id}' ");
                            if (verr != null) return verr;
                        }
                }
                else if (pack.PackType == PackType.PluginProject)
                {
                    if (pack.Distribution == null)
                        return $"Pack {pack.Id}: packType pluginProject requires distribution.";
                    if (!string.Equals(pack.Distribution.Kind, "gitRepo", StringComparison.OrdinalIgnoreCase))
                        return $"Pack {pack.Id}: pluginProject distribution.kind must be 'gitRepo'.";
                    if (string.IsNullOrWhiteSpace(pack.Distribution.Url))
                        return $"Pack {pack.Id}: distribution.url required.";
                    if (pack.Files != null && pack.Files.Count > 0)
                        return $"Pack {pack.Id}: pluginProject must not have files[] (git clone provides them).";
                    if (pack.Variants != null && pack.Variants.Count > 0)
                        return $"Pack {pack.Id}: pluginProject must not have variants[].";
                    // Non-asset files allowed inside pluginProject clones
                }
                else if (pack.PackType == PackType.ArchivePack)
                {
                    if (pack.Distribution == null)
                        return $"Pack {pack.Id}: packType archivePack requires distribution.";
                    if (!string.Equals(pack.Distribution.Kind, "archive", StringComparison.OrdinalIgnoreCase))
                        return $"Pack {pack.Id}: archivePack distribution.kind must be 'archive'.";
                    if (string.IsNullOrWhiteSpace(pack.Distribution.Url))
                        return $"Pack {pack.Id}: distribution.url required.";
                    if (!pack.Distribution.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !pack.Distribution.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        return $"Pack {pack.Id}: distribution.url must be http(s).";
                    if (!pack.Distribution.Url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        return $"Pack {pack.Id}: archivePack distribution.url must point to a .zip file.";
                    if (pack.Files != null && pack.Files.Count > 0)
                        return $"Pack {pack.Id}: archivePack must not have files[] (the archive provides them).";
                    if (pack.Variants != null && pack.Variants.Count > 0)
                        return $"Pack {pack.Id}: archivePack must not have variants[].";
                }
            }
            return null;
        }

        /// <summary>
        /// Validation of an index manifest. It installs nothing - its entries are user-clickable pointers
        /// to other manifests - so each entry needs an id, a display name and an absolute http(s) URL, and
        /// ids/urls must be unique inside the index. A pack manifest's assets[] is rejected here so one
        /// file cannot be both kinds, and the linked manifest is validated on its own when the user adds it.
        /// </summary>
        public static string ValidateIndex(AssetSourceManifest manifest)
        {
            if (string.IsNullOrWhiteSpace(manifest.SourceName))
                return "sourceName is required (index manifest).";
            if (string.IsNullOrWhiteSpace(manifest.SourceId))
                return "sourceId is required (index manifest).";
            if (manifest.Assets != null && manifest.Assets.Count > 0)
                return "An index manifest must not have assets[] - use entries[] to link manifests; use a pack manifest to list assets.";
            if (manifest.Entries == null || manifest.Entries.Count == 0)
                return "entries array is required and must not be empty (index manifest).";

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in manifest.Entries)
            {
                if (e == null)
                    return "entries contains a null entry.";
                if (string.IsNullOrWhiteSpace(e.SourceId))
                    return "entry missing sourceId.";
                if (e.SourceId.IndexOf(' ') >= 0 || e.SourceId.Trim().Length != e.SourceId.Length)
                    return $"entry sourceId '{e.SourceId}' contains spaces or surrounding whitespace.";
                if (string.IsNullOrWhiteSpace(e.SourceName))
                    return $"Entry {e.SourceId} missing sourceName.";

                var url = e.Url?.Trim();
                if (string.IsNullOrWhiteSpace(url))
                    return $"Entry {e.SourceId} missing url.";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                    return $"Entry {e.SourceId}: url must be an absolute http(s) URL, got '{url}'.";

                if (!ids.Add(e.SourceId))
                    return $"Duplicate entry sourceId '{e.SourceId}' in this index.";
                if (!urls.Add(url))
                    return $"Duplicate entry url '{url}' in this index.";
            }
            return null;
        }
        /// <summary>
        /// Strict per-file checks (spec §5.1). Returns the first human-readable error, or null
        /// when every file passes. <paramref name="context"/> prefixes each message, so variant
        /// files can be reported as e.g. <c>variant '1k' file missing url.</c>
        /// </summary>

        private static string ValidateFiles(string packId, List<FileEntry> files, string context = "")
        {
            foreach (var f in files)
            {
                if (string.IsNullOrWhiteSpace(f.Url))
                    return $"Pack {packId}: {context}file missing url.";
                if (string.IsNullOrWhiteSpace(f.Path))
                    return $"Pack {packId}: {context}file missing path.";
                // Validate path: relative, no traversal
                // Relative, and no '..' **segment**. Contains("..") was too broad for real content names
                // (a pack shipping brick..png is not traversing anything), so segments are compared.
                if (Path.IsPathRooted(f.Path) || GitSafety.ContainsTraversal(f.Path))
                    return $"Pack {packId}: {context}file path must be relative and not contain '..': {f.Path}";
                var ext = System.IO.Path.GetExtension(f.Path);
                if (string.IsNullOrEmpty(ext))
                    return $"Pack {packId}: {context}file path must have extension: {f.Path}";
                if (ForbiddenExts.Contains(ext))
                    return $"Pack {packId}: {context}file '{f.Path}' extension '{ext}' not allowed in assetPack. Use pluginProject for docs/C++.";
                // For Script type only .cs allowed strictly
                if (f.Type == FileAssetType.Script)
                {
                    if (!AllowedScriptExts.Contains(ext))
                        return $"Pack {packId}: {context}file '{f.Path}' type Script requires .cs extension, got '{ext}'.";
                }
                else
                {
                    // Non-script raw assets: allow content exts plus .flax, but forbid script exts
                    if (AllowedScriptExts.Contains(ext))
                        return $"Pack {packId}: {context}file '{f.Path}' has script extension but type is {f.Type}.";
                }
                if (f.Type == FileAssetType.MaterialTextures && !MaterialTextureExts.Contains(ext))
                    return $"Pack {packId}: {context}file '{f.Path}' type MaterialTextures requires an image extension, got '{ext}'.";
                // URL must be http(s)
                if (!f.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !f.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return $"Pack {packId}: {context}file url must be http(s): {f.Url}";
            }
            return null;
        }

        /// <summary>What <see cref="CheckArchiveEntryName"/> decided about one zip entry name.</summary>
        public enum ArchiveEntryCheck
        {
            /// <summary>A raw asset: extract it.</summary>
            Extract,
            /// <summary>Not an asset (docs, JSON, VCS metadata) but not code either: drop the entry, keep installing.</summary>
            Skip,
            /// <summary>Code in any form (script, executable, native library): the whole archive is refused.</summary>
            Reject
        }

        /// <summary>
        /// The archivePack counterpart of <see cref="ValidateFiles"/>, applied to the <b>entry names of a
        /// zip</b> instead of to manifest file entries. An archive's entries carry no manifest <c>type</c>,
        /// so the per-file whitelist cannot be applied when the manifest is fetched;
        /// <see cref="AssetInstallerService.InstallArchivePackAsync"/> runs this over every entry name
        /// before extracting anything.
        /// <para>
        /// Code is refused, junk is dropped. An entry that would deliver a C# script, an executable, a
        /// native library or an interpreter script fails the whole install
        /// (<see cref="ArchiveEntryCheck.Reject"/>): an archive always extracts into the content
        /// destination, has no route to the script destination, and must not gain one — plugin code goes
        /// through <c>packType "pluginProject"</c>, which the window gates with an install-code
        /// confirmation. Docs, C++ sources, JSON and loose data are the families forbidden for assetPack
        /// files, but upstream archives carry them routinely (a zip of a repo has its own README.md) and
        /// they are not code, so they are skipped (<see cref="ArchiveEntryCheck.Skip"/>) rather than
        /// failing a pack the user is installing.
        /// </para>
        /// </summary>
        /// <param name="entryPath">An archive entry name (path inside the zip, '/' separated).</param>
        /// <param name="reason">Reason for <see cref="ArchiveEntryCheck.Skip"/> or <see cref="ArchiveEntryCheck.Reject"/>; null for <see cref="ArchiveEntryCheck.Extract"/>.</param>
        public static ArchiveEntryCheck CheckArchiveEntryName(string entryPath, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(entryPath))
            {
                reason = "archive entry has no name.";
                return ArchiveEntryCheck.Reject;
            }
            var ext = System.IO.Path.GetExtension(entryPath.Replace('\\', '/'));
            // Extension-less names (LICENSE, README, COPYING…) are content, not code, and are common in
            // real archives, so they are extracted as-is.
            if (string.IsNullOrEmpty(ext))
                return ArchiveEntryCheck.Extract;
            if (AllowedScriptExts.Contains(ext))
            {
                reason = $"entry '{entryPath}' has a script extension '{ext}'; an archive cannot install code, use packType \"pluginProject\".";
                return ArchiveEntryCheck.Reject;
            }
            // Executables, native libraries and interpreter scripts: dead weight at best, a payload at
            // worst. assetPack validation predates this list and is left untouched; for archives this is
            // the code route that remains, so it is closed here.
            if (ArchiveBinaryExts.Contains(ext))
            {
                reason = $"entry '{entryPath}' has an executable or script-payload extension '{ext}', which an archive cannot deliver; use packType \"pluginProject\" for code.";
                return ArchiveEntryCheck.Reject;
            }
            // A license file is the one "doc" an archive must not lose. Apache-2.0 §4 requires the
            // license text, and any NOTICE, to travel with distributed copies; MIT and Zlib require the
            // copyright notice; CC-BY asks that the license be shown - and the packs that carry those
            // obligations reach this project as archives, so the doc rule below would delete the very
            // file the license depends on. Name-matched and plain-text only, and code extensions are
            // already refused above, so this widens nothing an archive may install.
            if (IsLegalDocEntry(entryPath))
                return ArchiveEntryCheck.Extract;
            // Docs, C++ sources, JSON, loose data: not allowed as installed assets, and an archive did not
            // get them from a manifest the pack author controls, so drop the entry instead of failing the
            // install over files that were never going to be assets.
            if (ForbiddenExts.Contains(ext))
            {
                reason = $"entry '{entryPath}' extension '{ext}' is not an asset; skipped.";
                return ArchiveEntryCheck.Skip;
            }
            return ArchiveEntryCheck.Extract;
        }
        /// <summary>
        /// Archive entry names that carry a license, notice or attribution, in the spellings upstream
        /// publishers use. Matched on the file name (any folder inside the zip), case-insensitively.
        /// </summary>
        private static readonly HashSet<string> LegalDocNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LICENSE", "LICENSE.md", "LICENSE.txt", "LICENSE.md.txt", "LICENSE-MIT", "LICENSE-APACHE",
            "LICENCE", "LICENCE.md", "LICENCE.txt", "COPYING", "COPYING.md", "COPYING.txt", "COPYRIGHT",
            "COPYRIGHT.txt", "NOTICE", "NOTICE.md", "NOTICE.txt", "ATTRIBUTION", "ATTRIBUTIONS",
            "ATTRIBUTIONS.md", "ATTRIBUTION.md", "CREDITS", "CREDITS.md", "OFL", "OFL.txt", "OFL.md",
            "EULA", "EULA.txt", "APACHE-LICENSE", "APACHE-LICENSE-2.0", "MIT-LICENSE", "MIT-LICENSE.txt",
            "OPEN-SIL-LICENSE", "OPEN-SIL.txt", "CC0", "CC0-LICENSE", "CC0-LICENSE.txt",
        };

        // Plain-text extensions a legal doc is allowed to use. A "LICENSE.png" stays the asset it is.
        private static readonly HashSet<string> LegalDocExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "", ".md", ".txt", ".text", ".rst", ".html", ".pdf",
        };

        /// <summary>
        /// True when an archive entry is a license / notice / attribution file that has to survive the
        /// install (see <see cref="CheckArchiveEntryName"/>). Name and extension only; the content is
        /// never parsed, and the plugin does not verify that what a publisher named LICENSE says what
        /// the manifest's <c>license</c> field says - a manifest that mislabels its license is a catalog
        /// bug, and the file the plugin keeps is the publisher's own text, which is the better evidence.
        /// </summary>
        public static bool IsLegalDocEntry(string entryPath)
        {
            if (string.IsNullOrWhiteSpace(entryPath))
                return false;
            var name = System.IO.Path.GetFileName(entryPath.Replace('\\', '/'));
            if (string.IsNullOrEmpty(name))
                return false;
            var ext = System.IO.Path.GetExtension(name);
            if (!string.IsNullOrEmpty(ext) && !LegalDocExts.Contains(ext))
                return false;
            return LegalDocNames.Contains(name);
        }

        /// <summary>
        /// Rewrites every <see cref="FileEntry.Path"/> so it is relative to the pack root. Some
        /// manifests embed the pack folder as the leading path segment (e.g. <c>foo-asset/diff.png</c>)
        /// even though the installer already places files under <c>&lt;dest&gt;/&lt;source&gt;/&lt;pack&gt;/</c>;
        /// left alone that yields a doubled folder (<c>.../foo-asset/foo-asset/diff.png</c>). When the
        /// first segment — sanitized the same way the install folder is named — equals the pack folder,
        /// it is stripped so both manifest conventions resolve to the same layout.
        /// </summary>
        private static void NormalizePackPaths(AssetSourceManifest manifest)
        {
            if (manifest?.Assets == null) return;
            foreach (var pack in manifest.Assets)
            {
                if (pack == null) continue;
                var packFolder = AssetInstallerService.Sanitize(pack.Id);
                NormalizeFileListPaths(pack.Files, packFolder);
                if (pack.Variants != null)
                    foreach (var v in pack.Variants)
                        if (v != null)
                            NormalizeFileListPaths(v.Files, packFolder);
            }
        }

        /// <summary>
        /// Strips a leading pack-folder segment from every file path in the list (same Sanitize
        /// logic as the pack's base files), so variant files resolve to the same layout.
        /// </summary>
        private static void NormalizeFileListPaths(List<FileEntry> files, string packFolder)
        {
            if (files == null) return;
            foreach (var f in files)
            {
                if (string.IsNullOrWhiteSpace(f.Path)) continue;
                var p = f.Path.Replace('\\', '/').TrimStart('/');
                var slash = p.IndexOf('/');
                var first = slash < 0 ? p : p.Substring(0, slash);
                if (AssetInstallerService.Sanitize(first) == packFolder)
                {
                    p = slash < 0 ? string.Empty : p.Substring(slash + 1);
                    if (p.Length > 0)
                        f.Path = p;
                }
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
