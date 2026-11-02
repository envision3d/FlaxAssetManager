using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FlaxEngine;
using FlaxEngine.Tools;
using FlaxEditor;
using FlaxEditor.Content.Import;
using FlaxAssetManager.Core.Models;
using FlaxAssetManager.Core.Utils;

namespace FlaxAssetManager.Core.Services
{
    /// <summary>
    /// Downloads and installs packs. Strict per design: assetPack = explicit files, pluginProject = git clone.
    /// </summary>
    public class AssetInstallerService : IDisposable
    {
        private readonly SettingsService _settings;
        private readonly AssetSourceManager _sourceManager;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _concurrencyLimiter = new SemaphoreSlim(6);

        /// <summary>
        /// Subfolder (relative to the pack folder) that downloaded asset files are written into.
        /// Pack-generated assets (e.g. the created <c>.flax</c> material) stay at the pack root,
        /// so they remain distinguishable from raw downloads.
        /// </summary>
        public const string FilesSubFolder = "files";

        /// <summary>
        /// Subfolder of <see cref="FilesSubFolder"/> that a variant's downloads land in — <c>files/2k/</c>
        /// for a variant whose id is <c>2k</c> — so each file set a pack offers (1k / 2k / 4k / 8k texture
        /// resolutions and any other tier) keeps its files, and its imported <c>.flax</c> sidecars, on its
        /// own instead of sharing one flat folder whose identical names overwrite each other. Returns
        /// <see cref="string.Empty"/> for the default file set (a pack with no <c>variants[]</c>, or the base
        /// <c>files[]</c> selected), which keeps that layout exactly as it was. The id is manifest data, so it
        /// is run through <see cref="Sanitize"/> like every other folder name this service derives.
        /// </summary>
        public static string VariantSubFolder(string variantId) => string.IsNullOrEmpty(variantId) ? string.Empty : Sanitize(variantId);

        /// <summary>
        /// Extensions the editor's content pipeline turns into a .flax asset beside the source file
        /// ('Rock.tga' yields 'Rock.tga.flax' plus a 'Rock.tga/' derived-data folder). Uninstall derives
        /// those sidecars from the recorded downloads, so removing a pack also removes the assets it
        /// created in the Content tree.
        /// </summary>
        public static readonly HashSet<string> ImportedSourceExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".exr", ".bmp", ".dds", ".hdr", ".gif", ".ico", ".webp",
            ".fbx", ".obj", ".gltf", ".glb", ".blend", ".max",
            ".wav", ".ogg", ".mp3", ".flac", ".eot", ".otf", ".ttf", ".mp4", ".anm"
        };
        public event Action<string> Log;
        public event Action<float, string> Progress;

        public AssetInstallerService(SettingsService settings, AssetSourceManager sourceManager)
        {
            _settings = settings;
            _sourceManager = sourceManager;
            // Self-identifying UA on every download: an asset host reading its access log sees
            // FlaxAssetManager, not a generic browser (HttpIdentity; Poly Haven's API terms ask for this).
            _httpClient = HttpIdentity.NewClient(TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// Installs one pack - asset files, an archive, or a plugin git clone, chosen by packType.
        /// <para>
        /// <paramref name="manifest"/> is the manifest <paramref name="pack"/> came from, used only to copy
        /// license, author, credit line and the source's website into the install record: the provenance a
        /// project ships by. It is never read for URLs, so fetching stays the per-file and per-archive job
        /// of the pack itself.
        /// </para>
        /// </summary>
        public async Task<bool> InstallAsync(PackEntry pack, string sourceId, string sourceUrl, IProgress<(float, string)> progress = null, CancellationToken ct = default, string variantId = null, AssetSourceManifest manifest = null)
        {
            try
            {
                Report(progress, 0f, $"Installing {pack.Name}...");
                if (pack.PackType == PackType.PluginProject)
                    return await InstallPluginProjectAsync(pack, sourceId, sourceUrl, progress, ct, manifest);
                else if (pack.PackType == PackType.ArchivePack)
                    return await InstallArchivePackAsync(pack, sourceId, sourceUrl, progress, ct, manifest);
                else
                    return await InstallAssetPackAsync(pack, sourceId, sourceUrl, progress, ct, variantId, manifest);
            }
            catch (Exception e)
            {
                Debug.LogError($"FlaxAssetManager: Install failed for {pack.Id}: {e.Message}\n{e.StackTrace}");
                Log?.Invoke($"Failed: {e.Message}");
                return false;
            }
        }

        private async Task<bool> InstallAssetPackAsync(PackEntry pack, string sourceId, string sourceUrl, IProgress<(float, string)> progress, CancellationToken ct, string variantId = null, AssetSourceManifest manifest = null)
        {
            var files = pack.GetEffectiveFiles(variantId);
            // A stale variant id (persisted in Settings.json, or a manifest that dropped a variant)
            // falls back to the default file set - install it, but say so loudly.
            if (!string.IsNullOrEmpty(variantId) &&
                (pack.Variants == null || !pack.Variants.Exists(v => string.Equals(v.Id, variantId, StringComparison.Ordinal))))
            {
                Debug.LogWarning($"FlaxAssetManager: variant '{variantId}' is not declared by pack '{pack.Id}'; installing the default file set.");
            }

            // The record this install replaces, read **before** anything is written: a variant switch
            // writes its set under files/<variant>/, so none of the previously installed paths are
            // overwritten and the old set would simply stay in the Content tree. Its file list is
            // exactly what has to go (see RemoveReplacedInstall).
            var replaced = _settings.FindInstalled(sourceId, pack.Id);

            int total = files.Count;
            int done = 0;
            int skipped = 0;
            var installedFiles = new List<string>();

            var tasks = new List<Task>();
            var errors = new List<string>();
            var lockObj = new object();

            foreach (var file in files)
            {
                await _concurrencyLimiter.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var destPath = ResolveAssetPackPath(file, pack, sourceId, variantId);
                        var dir = Path.GetDirectoryName(destPath);
                        Directory.CreateDirectory(dir);

                        // Settings.overwriteExisting = false keeps an existing file exactly as it is:
                        // no download, no checksum, no model import - it is still recorded as part of
                        // this pack so update/uninstall tracking stays complete.
                        if (!_settings.Settings.OverwriteExisting && File.Exists(destPath))
                        {
                            lock (lockObj)
                            {
                                skipped++;
                                installedFiles.Add(MakeRelative(destPath));
                            }
                            return;
                        }

                        // Streams to disk and verifies the optional sha256 before the file lands.
                        await DownloadToFileAsync(file.Url, destPath, file.Sha256, file.Path, ct);
                        lock (lockObj) installedFiles.Add(MakeRelative(destPath));
                    }
                    catch (Exception e)
                    {
                        lock (lockObj) errors.Add($"{file.Path}: {e.Message}");
                    }
                    finally
                    {
                        _concurrencyLimiter.Release();
                        var cur = Interlocked.Increment(ref done);
                        Report(progress, (float)cur / total, $"Downloaded {cur}/{total}");
                    }
                }, ct));
            }

            await Task.WhenAll(tasks);

            if (errors.Count > 0)
            {
                foreach (var e in errors)
                    Debug.LogError($"FlaxAssetManager: {e}");
                // Atomic install: roll back every file already written so a failed pack
                // leaves no orphan files on disk.
                RollbackInstalled(installedFiles);
                Log?.Invoke($"Failed: {errors.Count} file(s) errored; rolled back {installedFiles.Count} partial file(s).");
                return false;
            }

            // Every file of the new set is on disk, so it is now safe to drop the set this install
            // replaces: its recorded files, the imported .flax sidecars beside them, and the folders of
            // the pack's other variants. One pack keeps one variant on disk - a leftover 1k set beside a
            // 4k one is duplicate textures, and the graph can wire the tier that was just switched away from.
            RemoveReplacedInstall(replaced, sourceId, pack, variantId, installedFiles);

            // Trigger the editor's import pipeline for every model in the pack so the raw
            // .fbx/.obj/.blend sources become loadable Flax assets. The generated .flax assets
            // land in the pack root, one level above files/, next to the created material.
            var models = new List<string>();
            // Assets the install *produces* (imported models, and later the material), recorded so
            // uninstall can delete them; a downloaded-file list alone leaves them in the Content tree.
            var generatedAssets = new List<string>();
            foreach (var file in files)
            {
                if (file.Type != FileAssetType.Model) continue;
                var full = ResolveAssetPackPath(file, pack, sourceId, variantId);
                if (!File.Exists(full)) continue;
                models.Add(full);
            }
            try
            {
                generatedAssets = await ImportModelsAsync(sourceId, pack, models, progress, ct);
            }
            catch (OperationCanceledException)
            {
                // The files are already written to disk; a cancelled import just means the
                // models are not registered yet. Keep the pack installed so the record matches
                // what is on disk — the editor will import them on the next content refresh.
                Debug.LogWarning($"FlaxAssetManager: model import cancelled for pack '{pack.Id}'; files remain installed.");
            }

            // Record install. The record this install replaces is dropped, and the assets that install
            // *generated* carry over: the imported models and the created material live in the pack root,
            // shared by every variant and absent from the file list, and re-importing a model whose
            // .flax is already on disk produces nothing new - a fresh list would silently orphan them.
            if (replaced != null)
                _settings.Installed.Installed.Remove(replaced);
            _settings.Installed.Installed.Add(new InstalledEntry
            {
                SourceId = sourceId,
                SourceUrl = sourceUrl,
                PackId = pack.Id,
                PackName = pack.Name,
                PackType = pack.PackType,
                InstalledVersion = pack.Version,
                InstalledAt = DateTime.UtcNow,
                Variant = variantId,
                DistributionUrl = null,
                InstallPath = Path.Combine(_settings.Settings.ContentDestination, Sanitize(sourceId), Sanitize(pack.Id)),
                // License, author and credit line as the manifest declared them, copied into the record
                // beside the files they describe. The live pack's license string is the catalog's, so a
                // record that only named sourceId/packId reports today's terms for a pack installed years
                // ago, and a source the user deletes from Sources.json takes its manifest with it. This
                // snapshot is what ATTRIBUTIONS.md and Copy credit line read.
                License = pack.License,
                LicenseUrl = pack.LicenseUrl ?? manifest?.LicenseUrl,
                Author = pack.Author ?? manifest?.Author,
                Credit = pack.Credit ?? manifest?.Credit,
                SourceName = manifest?.SourceName,
                SourceWebsite = manifest?.Website ?? manifest?.HomepageUrl,
                Files = installedFiles,
                GeneratedAssets = MergeGeneratedAssets(replaced?.GeneratedAssets, generatedAssets),
            });
            _settings.SaveInstalled();
            if (skipped > 0)
                Debug.Log($"FlaxAssetManager: skipped {skipped} existing file(s) of pack '{pack.Id}' because Settings.overwriteExisting is false.");
            Report(progress, 1f, $"Installed {pack.Name} ({installedFiles.Count} files)");
            Log?.Invoke($"Installed {pack.Name} v{pack.Version}");
            return true;
        }

        private async Task<bool> InstallArchivePackAsync(PackEntry pack, string sourceId, string sourceUrl, IProgress<(float, string)> progress, CancellationToken ct, AssetSourceManifest manifest = null)
        {
            var dist = pack.Distribution;
            Report(progress, 0f, $"Downloading {pack.Name}...");

            var extractedFiles = new List<string>();
            try
            {
                // The archive goes straight to the temp zip below: archives are the largest payloads
                // the manager handles, so they must not be buffered in memory.

                // Extract under the pack's files/ folder (content destination), preserving
                // the archive's internal layout.
                var destRootFull = Path.GetFullPath(Path.Combine(_settings.ContentDestinationFullPath, Sanitize(sourceId), Sanitize(pack.Id), FilesSubFolder));
                Directory.CreateDirectory(destRootFull);

                var tempPath = Path.Combine(Path.GetTempPath(), $"flaxam_{Guid.NewGuid():N}.zip");
                try
                {
                    // Lands on disk, and the optional archive sha256 is verified from the file, before
                    // anything is extracted into the project.
                    await DownloadToFileAsync(dist.Url, tempPath, dist.Sha256, pack.Name, ct);
                    using (var archive = System.IO.Compression.ZipFile.OpenRead(tempPath))
                    {
                        var entries = archive.Entries
                            .Where(e => !e.FullName.EndsWith("/") && e.Length > 0)
                            .ToList();

                        // Archive entries carry no manifest type, so the manifest's per-file extension
                        // whitelist cannot be applied at fetch time. Apply it to every entry name here,
                        // *before* anything is written: an entry that delivers a C# script, an executable,
                        // a native library or an interpreter script fails the whole install, so an archive
                        // can never land code in the content tree the way assetPack files are forbidden to.
                        // Docs and config (README.md, .gitignore, JSON) are not code and upstream
                        // archives are full of them, so those entries are dropped, not treated as fatal.
                        // LICENSE / NOTICE / ATTRIBUTIONS are the exception, kept by name: they are the
                        // files some licenses require to travel with the assets they ship with.
                        var installable = new List<System.IO.Compression.ZipArchiveEntry>(entries.Count);
                        int skipped = 0;
                        foreach (var entry in entries)
                        {
                            var check = AssetSourceManager.CheckArchiveEntryName(entry.FullName, out var reason);
                            if (check == AssetSourceManager.ArchiveEntryCheck.Reject)
                                throw new Exception($"Archive of pack '{pack.Id}' is not installable: {reason}");
                            if (check == AssetSourceManager.ArchiveEntryCheck.Skip)
                            {
                                skipped++;
                                continue;
                            }
                            installable.Add(entry);
                        }
                        entries = installable;
                        if (skipped > 0)
                            Report(progress, 0.05f, $"Skipped {skipped} non-asset entr{(skipped == 1 ? "y" : "ies")} in the archive.");
                        int total = entries.Count;
                        int done = 0;
                        foreach (var entry in entries)
                        {
                            ct.ThrowIfCancellationRequested();
                            var name = entry.FullName.Replace('\\', '/');
                            var target = Path.GetFullPath(Path.Combine(destRootFull, name));
                            // Zip-slip guard: every entry must resolve inside the pack folder.
                            if (!GitSafety.IsUnderRoot(target, destRootFull + Path.DirectorySeparatorChar))
                                throw new Exception($"Archive entry escapes the pack folder: {entry.FullName}");
                            var dir = Path.GetDirectoryName(target);
                            Directory.CreateDirectory(dir);
                            entry.ExtractToFile(target, overwrite: true);
                            extractedFiles.Add(MakeRelative(target));
                            var cur = Interlocked.Increment(ref done);
                            Report(progress, 0.05f + 0.9f * (float)cur / total, $"Extracted {cur}/{total}");
                        }
                    }
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            }
            catch
            {
                // Atomic install: roll back every file already written so a failed pack
                // leaves no orphan files on disk.
                RollbackInstalled(extractedFiles);
                throw;
            }

            if (extractedFiles.Count == 0)
                throw new Exception($"Archive {dist.Url} contains no files.");

            // Trigger the editor's import pipeline for any model files the archive contained,
            // detected by extension since archive entries carry no explicit type. The generated
            // .flax assets all land in the pack root (one level above files/).
            var modelFiles = new List<string>();
            foreach (var rel in extractedFiles)
            {
                if (Array.IndexOf(ModelExtensions, Path.GetExtension(rel).ToLowerInvariant()) < 0) continue;
                modelFiles.Add(Path.Combine(Globals.ProjectFolder, rel));
            }
            try
            {
                await ImportModelsAsync(sourceId, pack, modelFiles, progress, ct);
            }
            catch (OperationCanceledException)
            {
                Debug.LogWarning($"FlaxAssetManager: model import cancelled for pack '{pack.Id}'; files remain installed.");
            }

            // Record install
            var existing = _settings.FindInstalled(sourceId, pack.Id);
            if (existing != null)
                _settings.Installed.Installed.Remove(existing);
            _settings.Installed.Installed.Add(new InstalledEntry
            {
                SourceId = sourceId,
                SourceUrl = sourceUrl,
                PackId = pack.Id,
                PackName = pack.Name,
                PackType = pack.PackType,
                InstalledVersion = pack.Version,
                InstalledAt = DateTime.UtcNow,
                Variant = null,
                DistributionUrl = dist.Url,
                InstallPath = Path.Combine(_settings.Settings.ContentDestination, Sanitize(sourceId), Sanitize(pack.Id)),
                // Provenance snapshot; see the asset-pack install for why these are copied, not looked up.
                License = pack.License,
                LicenseUrl = pack.LicenseUrl ?? manifest?.LicenseUrl,
                Author = pack.Author ?? manifest?.Author,
                Credit = pack.Credit ?? manifest?.Credit,
                SourceName = manifest?.SourceName,
                SourceWebsite = manifest?.Website ?? manifest?.HomepageUrl,
                Files = extractedFiles,
            });
            _settings.SaveInstalled();
            Report(progress, 1f, $"Installed {pack.Name} ({extractedFiles.Count} files)");
            Log?.Invoke($"Installed {pack.Name} v{pack.Version}");
            return true;
        }

        /// <summary>
        /// Installs a plugin project as a plain git clone of <c>distribution.url</c> into
        /// <c>Plugins/&lt;folderName&gt;</c> and injects the clone's <c>.flaxproj</c> into the host project.
        /// <para>
        /// Every value that reaches git comes from a manifest (remote input), so all of it — URL scheme,
        /// ref, commit, and the folder name derived from the URL — is validated <b>before</b> the
        /// filesystem is touched, git is spawned with an argument list and a <c>--</c> end-of-options
        /// marker (no manifest field can become a git option, e.g. <c>--upload-pack=&lt;cmd&gt;</c>), and
        /// the clone lands in a staging folder that is validated first. The existing tree is only replaced
        /// once a good clone exists in hand, and only when a prior install record of this plugin owns it:
        /// an unreachable URL, a rejected ref/commit, a repo with no root <c>.flaxproj</c>, or a name that
        /// collides with a folder the manager did not create all leave the project unchanged.
        /// </para>
        /// </summary>
        private async Task<bool> InstallPluginProjectAsync(PackEntry pack, string sourceId, string sourceUrl, IProgress<(float, string)> progress, CancellationToken ct, AssetSourceManifest manifest = null)
        {
            var gitUrl = pack.Distribution.Url;
            var branch = pack.Distribution.Branch;
            var commit = pack.Distribution.Commit;

            if (!GitSafety.IsHttpsUrl(gitUrl))
            {
                Debug.LogError($"FlaxAssetManager: refusing to clone non-https git URL '{gitUrl}'");
                return false;
            }
            if (!string.IsNullOrWhiteSpace(branch) && !GitSafety.IsValidRefName(branch))
            {
                Debug.LogError($"FlaxAssetManager: refusing to clone, invalid branch/ref '{branch}'");
                return false;
            }
            if (!string.IsNullOrWhiteSpace(commit) && !GitSafety.IsValidCommit(commit))
            {
                Debug.LogError($"FlaxAssetManager: refusing to check out, invalid commit id '{commit}'");
                return false;
            }

            // The folder name is derived from the URL, so it is remote-controlled too: it has to stay a
            // direct child of the project's Plugins folder, and must not be the folder this plugin itself
            // runs from (a URL basename is free to equal it, which would delete the code running us).
            var folderName = DerivePluginFolderName(gitUrl, pack.Id);
            var pluginsRoot = Path.GetFullPath(_settings.PluginsFolder);
            var destPath = Path.GetFullPath(Path.Combine(pluginsRoot, folderName));
            var sep = Path.DirectorySeparatorChar;
            if (string.Equals(destPath.TrimEnd(sep, '/'), pluginsRoot.TrimEnd(sep, '/'), PathComparison) ||
                !GitSafety.IsUnderRoot(destPath, pluginsRoot + sep))
            {
                Debug.LogError($"FlaxAssetManager: refusing to install, '{folderName}' does not resolve to a folder inside {pluginsRoot}");
                return false;
            }
            var selfDir = Path.GetDirectoryName(typeof(AssetInstallerService).Assembly.Location);
            if (!string.IsNullOrEmpty(selfDir) && GitSafety.IsUnderRoot(Path.GetFullPath(selfDir), destPath + sep))
            {
                Debug.LogError($"FlaxAssetManager: refusing to clone '{gitUrl}' over '{destPath}' - FlaxAssetManager runs from inside that folder");
                return false;
            }

            // Replacing an existing Plugins/<name> tree is only allowed for a tree a prior install record
            // of this manager owns. Anything else — a plugin the user cloned by hand, the editor's own
            // plugins — is refused, so no URL basename can destroy a folder the manager did not create.
            var replacesOwnedTree = Directory.Exists(destPath) && IsOwnedPluginInstall(destPath);
            if (Directory.Exists(destPath) && !replacesOwnedTree)
            {
                Debug.LogError($"FlaxAssetManager: refusing to install '{gitUrl}' - 'Plugins/{folderName}' already exists and was not installed by FlaxAssetManager");
                Log?.Invoke($"Failed: 'Plugins/{folderName}' already exists and is not managed by FlaxAssetManager, so nothing was changed. Remove that folder by hand, or use a pack id that does not collide.");
                return false;
            }

            // Plugins/ may not exist yet in a project that has never had a plugin.
            Directory.CreateDirectory(pluginsRoot);
            // Clone into a staging folder inside Plugins/ — the same filesystem as the destination, so the
            // swap is a rename — and validate it before the real folder is touched.
            Report(progress, 0.2f, $"Cloning {gitUrl}...");
            var stagingPath = Path.Combine(pluginsRoot, StagingFolderPrefix + Guid.NewGuid().ToString("N"));
            string projName;
            string resolvedCommit;
            string backupPath = null;
            try
            {
                var cloneArgs = new List<string> { "clone" };
                if (!string.IsNullOrWhiteSpace(branch))
                    cloneArgs.Add($"--branch={branch}");
                cloneArgs.Add("--");
                cloneArgs.Add(gitUrl);
                cloneArgs.Add(stagingPath);
                var cloneResult = await RunGitAsync(cloneArgs, null, ct);
                if (cloneResult != 0)
                {
                    Debug.LogError($"FlaxAssetManager: git clone failed with code {cloneResult}");
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(commit))
                {
                    var checkoutResult = await RunGitAsync(new[] { "-C", stagingPath, "checkout", "--detach", commit }, null, ct);
                    if (checkoutResult != 0)
                    {
                        Debug.LogError($"FlaxAssetManager: git checkout {commit} failed");
                        return false;
                    }
                }

                // Validate .flaxproj exists (in the clone, before it becomes the installed plugin).
                var projFiles = Directory.GetFiles(stagingPath, "*.flaxproj", SearchOption.TopDirectoryOnly);
                if (projFiles.Length == 0)
                {
                    Debug.LogError($"FlaxAssetManager: cloned repo {gitUrl} has no .flaxproj at its root; nothing was installed.");
                    Log?.Invoke($"Failed: {gitUrl} has no .flaxproj at its root; nothing was installed.");
                    return false;
                }
                projName = Path.GetFileNameWithoutExtension(projFiles[0]);

                // Resolve git commit for tracking
                resolvedCommit = await ResolveHeadCommitAsync(stagingPath, ct);

                // A good clone is in hand. The old tree is **renamed aside**, not deleted: the swap is
                // then two renames on one filesystem, and a failed move puts the user's tree back instead
                // of leaving them with a half-installed plugin and a deleted folder.
                if (replacesOwnedTree)
                {
                    Report(progress, 0.8f, $"Replacing {folderName}...");
                    backupPath = Path.Combine(pluginsRoot, ReplacedFolderPrefix + Guid.NewGuid().ToString("N"));
                    Directory.Move(destPath, backupPath);
                }
                try
                {
                    Directory.Move(stagingPath, destPath);
                }
                catch
                {
                    // The clone did not land: restore the tree that was moved aside, and let the caller
                    // report the failure. Nothing of the user's is lost.
                    if (backupPath != null && !Directory.Exists(destPath))
                    {
                        try { Directory.Move(backupPath, destPath); }
                        catch (Exception restore)
                        {
                            Debug.LogError($"FlaxAssetManager: the replaced folder could not be restored ({backupPath}): {restore.Message}");
                        }
                    }
                    throw;
                }
                // Installed: now it is safe to drop the replaced tree.
                if (backupPath != null)
                {
                    try
                    {
                        Directory.Delete(backupPath, true);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"FlaxAssetManager: installed {folderName}, but the replaced folder is still on disk ({backupPath}): {e.Message}");
                    }
                }
            }
            finally
            {
                // A failed clone leaves the staging folder behind, so it is removed here; a successful
                // swap already moved it to the destination. The tree that was moved aside is dropped on
                // success and restored in place when the swap failed (above).
            }

            // Add reference to root .flaxproj
            await AddReferenceToProjectAsync(folderName, projName);

            // Keep .git: it identifies the installed commit, and the clone is a plain clone, not a
            // submodule, so the user can run git operations in it by hand.
            var files = Directory.GetFiles(destPath, "*", SearchOption.AllDirectories).Select(MakeRelative).ToList();

            var existing = _settings.FindInstalled(sourceId, pack.Id);
            if (existing != null)
                _settings.Installed.Installed.Remove(existing);
            _settings.Installed.Installed.Add(new InstalledEntry
            {
                SourceId = sourceId,
                SourceUrl = sourceUrl,
                PackId = pack.Id,
                PackName = pack.Name,
                PackType = pack.PackType,
                InstalledVersion = pack.Version,
                InstalledAt = DateTime.UtcNow,
                Variant = null,
                DistributionUrl = gitUrl,
                ResolvedCommit = resolvedCommit,
                InstallPath = $"Plugins/{folderName}",
                // Provenance snapshot; see the asset-pack install for why these are copied, not looked up.
                License = pack.License,
                LicenseUrl = pack.LicenseUrl ?? manifest?.LicenseUrl,
                Author = pack.Author ?? manifest?.Author,
                Credit = pack.Credit ?? manifest?.Credit,
                SourceName = manifest?.SourceName,
                SourceWebsite = manifest?.Website ?? manifest?.HomepageUrl,
                Files = files
            });
            _settings.SaveInstalled();

            Report(progress, 1f, $"Cloned {folderName}. Restart editor to load plugin.");
            Log?.Invoke($"Installed plugin {pack.Name} to Plugins/{folderName}. Restart editor.");
            return true;
        }

        /// <summary>
        /// Prefix of the transient clone destination inside <c>Plugins/</c>. It sits next to the real
        /// destination so the final swap is a same-filesystem rename (a move to/from the OS temp folder
        /// is not, and can fail across mounts), and it is always removed — in the <c>finally</c> of the
        /// install — so a failed clone cannot leave tree behind.
        /// </summary>
        private const string StagingFolderPrefix = ".flaxam-clone-";

        /// <summary>
        /// Prefix of the folder a replaced plugin tree is renamed into during a swap. Renaming the old
        /// tree aside (rather than deleting it first) keeps the swap reversible: if moving the clone in
        /// fails, the user's tree is renamed back. Dropped on success.
        /// </summary>
        private const string ReplacedFolderPrefix = ".flaxam-replaced-";

        /// <summary>
        /// True when a recorded <see cref="PackType.PluginProject"/> install owns <paramref name="destPath"/>
        /// — the only case in which re-installing that pack may replace the folder. Records are matched on
        /// the resolved path (so a stored '\' separator or a nested path still matches) and compared with
        /// the OS-appropriate case rules.
        /// </summary>
        private bool IsOwnedPluginInstall(string destPath)
        {
            foreach (var entry in _settings.Installed.Installed)
            {
                if (entry.PackType != PackType.PluginProject || string.IsNullOrEmpty(entry.InstallPath))
                    continue;
                var recorded = Path.GetFullPath(Path.Combine(Globals.ProjectFolder, entry.InstallPath));
                if (string.Equals(recorded.TrimEnd(Path.DirectorySeparatorChar, '/'), destPath.TrimEnd(Path.DirectorySeparatorChar, '/'), PathComparison))
                    return true;
            }
            return false;
        }

        /// <summary>Path comparisons for install-folder guards: case-insensitive only on Windows.</summary>
        private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>The cloned repo's HEAD sha via <c>git rev-parse HEAD</c>, or null when it cannot be read.</summary>
        private async Task<string> ResolveHeadCommitAsync(string repoPath, CancellationToken ct)
        {
            try
            {
                var tmpOut = Path.GetTempFileName();
                await RunGitAsync(new[] { "-C", repoPath, "rev-parse", "HEAD" }, tmpOut, ct);
                if (File.Exists(tmpOut))
                {
                    var sha = (await File.ReadAllTextAsync(tmpOut, ct)).Trim();
                    File.Delete(tmpOut);
                    return string.IsNullOrEmpty(sha) ? null : sha;
                }
            }
            catch { }
            return null;
        }

        private string ResolveAssetPackPath(FileEntry file, PackEntry pack, string sourceId, string variantId)
        {
            // Script -> scriptDestination, others -> contentDestination
            bool isScript = file.Type == FileAssetType.Script;
            var root = isScript ? _settings.ScriptDestinationFullPath : _settings.ContentDestinationFullPath;
            var safeSource = Sanitize(sourceId);
            var safePack = Sanitize(pack.Id);
            // file.Path is relative like "Textures/Brick/brick.png" — preserve verbatim under
            // source/pack/files[/variant]. The variant segment is what keeps a pack's 1k/2k/4k sets apart
            // on disk; it is empty for the default file set, so a pack without variants is laid out as before.
            var combined = Path.Combine(root, safeSource, safePack, FilesSubFolder, VariantSubFolder(variantId), file.Path);
            // Normalize: remove any .. attempts already validated, but also ensure inside root
            var full = Path.GetFullPath(combined);
            var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            if (!GitSafety.IsUnderRoot(full, rootFull))
                throw new Exception($"Path traversal detected: {file.Path}");
            return full;
        }

        /// <summary>
        /// Deletes what the install record being replaced left in the project: every recorded file that the
        /// new install does not write itself, plus the <c>.flax</c> asset and the derived-data folder the
        /// editor created beside each imported source file, then prunes the folders that become empty.
        /// <para>
        /// A variant switch is where this matters: the new set lands under <c>files/&lt;variant&gt;/</c>, so it
        /// shares no path with the set installed before and nothing is overwritten. Without this pass the
        /// old set — and the assets imported from it — stay in the Content tree: one pack showing two
        /// resolutions at once, and the material rebuilt for the newly selected variant can wire textures
        /// of the tier the user just switched away from. The same pass cleans a pre-variant install (whose
        /// files landed flat in <c>files/</c>) that is being reinstalled as a variant, because its paths are
        /// recorded too.
        /// </para>
        /// <para>
        /// A model imports into the pack root and is named after its source base name, so the model of the
        /// removed tier ('set_1k.fbx' → 'set_1k.flax') is dropped as well, matched only against that record's
        /// own model sources. The pack's generated material is left alone: the material pipeline rebuilds it.
        /// </para>
        /// <para>
        /// Paths come from the record and pass the same project-containment gate as an uninstall delete; a
        /// path that fails it is logged and skipped, never deleted.
        /// </para>
        /// </summary>
        private void RemoveReplacedInstall(InstalledEntry replaced, string sourceId, PackEntry pack, string variantId, IList<string> keep)
        {
            PruneStaleVariantFolders(sourceId, pack, variantId);
            if (replaced?.Files == null || replaced.Files.Count == 0)
                return;

            var projectRootFull = Path.GetFullPath(Globals.ProjectFolder) + Path.DirectorySeparatorChar;
            var keepSet = new HashSet<string>((keep ?? new List<string>()).Select(NormalizeRel), StringComparer.Ordinal);
            int removed = 0;
            foreach (var rel in replaced.Files)
            {
                var normalized = NormalizeRel(rel);
                if (normalized.Length == 0 || keepSet.Contains(normalized))
                    continue;   // this install writes that exact path, so its file stays as it is
                var full = Path.GetFullPath(Path.Combine(Globals.ProjectFolder, normalized));
                if (!GitSafety.IsUnderRoot(full, projectRootFull))
                {
                    Debug.LogError($"FlaxAssetManager: refusing to delete '{rel}' - it resolves outside the project folder");
                    continue;
                }
                try
                {
                    if (File.Exists(full))
                    {
                        File.Delete(full);
                        removed++;
                    }
                    // The import pipeline writes the asset beside its source, plus a folder of derived
                    // data ('brick.png' → 'brick.png.flax' and 'brick.png/'), so dropping only the source
                    // leaves the previous variant half-installed and its asset still in the browser.
                    var sidecar = full + ".flax";
                    if (File.Exists(sidecar))
                    {
                        File.Delete(sidecar);
                        removed++;
                    }
                    var sidecarDir = full + Path.DirectorySeparatorChar;
                    if (Directory.Exists(sidecarDir))
                    {
                        Directory.Delete(sidecarDir, true);
                        removed++;
                    }
                    PruneEmptyFolders(Path.GetDirectoryName(full), PackRootOfRecordedFile(full));
                }
                catch (Exception e)
                {
                    // A file the editor has locked must not fail an install that already succeeded: name
                    // what stayed behind and carry on with the rest.
                    Debug.LogWarning($"FlaxAssetManager: could not remove '{rel}' left by the replaced install: {e.Message}");
                }
            }

            // A model imports into the pack root and the engine names the asset after the source base name,
            // so a tier whose source is gone leaves its model behind: 'set_1k.fbx' and 'set_4k.fbx' produce
            // 'set_1k.flax' and 'set_4k.flax' side by side in the Content browser. Drop the assets of exactly
            // the models this install removed - named from that record's own model sources - and leave every
            // other generated asset alone: the pack's material is rebuilt by the material pipeline, not here.
            var staleModelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in replaced.Files)
            {
                var normalized = NormalizeRel(rel);
                if (normalized.Length == 0 || keepSet.Contains(normalized))
                    continue;
                if (Array.IndexOf(ModelExtensions, Path.GetExtension(normalized).ToLowerInvariant()) < 0)
                    continue;
                staleModelNames.Add(Path.GetFileNameWithoutExtension(normalized));
            }
            foreach (var rel in replaced.GeneratedAssets ?? Enumerable.Empty<string>())
            {
                var normalized = NormalizeRel(rel);
                if (normalized.Length == 0 || !staleModelNames.Contains(Path.GetFileNameWithoutExtension(normalized)))
                    continue;
                var full = Path.GetFullPath(Path.Combine(Globals.ProjectFolder, normalized));
                if (!GitSafety.IsUnderRoot(full, projectRootFull))
                {
                    Debug.LogError($"FlaxAssetManager: refusing to delete '{rel}' - it resolves outside the project folder");
                    continue;
                }
                try
                {
                    if (File.Exists(full))
                    {
                        File.Delete(full);
                        removed++;
                    }
                    var assetDir = full + Path.DirectorySeparatorChar;
                    if (Directory.Exists(assetDir))
                    {
                        Directory.Delete(assetDir, true);
                        removed++;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"FlaxAssetManager: could not remove the model asset '{rel}' left by the replaced install: {e.Message}");
                }
            }
            if (removed > 0)
                Debug.Log($"FlaxAssetManager: removed {removed} file(s) left by the replaced install of pack '{pack.Id}' (variant '{replaced.Variant ?? "default"}').");
        }

        /// <summary>
        /// Removes the folders of the pack's <b>other</b> variants — for a default-set install, of every
        /// declared variant — under both the content and the script destination. Those folders exist only
        /// because this installer made them, and their names come from the pack's own declared variant ids,
        /// so nothing a user or a manifest put in <c>files/</c> is touched and the installed variant's own
        /// folder is never pruned.
        /// </summary>
        private void PruneStaleVariantFolders(string sourceId, PackEntry pack, string variantId)
        {
            if (pack?.Variants == null || pack.Variants.Count == 0)
                return;

            var keep = VariantSubFolder(variantId);
            var projectRootFull = Path.GetFullPath(Globals.ProjectFolder) + Path.DirectorySeparatorChar;
            foreach (var v in pack.Variants)
            {
                var sub = VariantSubFolder(v.Id);
                // Two variant ids can sanitize to one folder name; pruning it while installing the other
                // would delete the files this install is writing.
                if (sub.Length == 0 || string.Equals(sub, keep, PathComparison))
                    continue;
                foreach (var root in new[] { _settings.ContentDestinationFullPath, _settings.ScriptDestinationFullPath })
                {
                    var dir = Path.GetFullPath(Path.Combine(root, Sanitize(sourceId), Sanitize(pack.Id), FilesSubFolder, sub));
                    if (!GitSafety.IsUnderRoot(dir, projectRootFull) || !Directory.Exists(dir))
                        continue;
                    try
                    {
                        Directory.Delete(dir, true);
                        Debug.Log($"FlaxAssetManager: removed variant folder '{sub}' of pack '{pack.Id}' - it is not the installed variant.");
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"FlaxAssetManager: could not remove the variant folder '{dir}': {e.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Deletes <paramref name="startDir"/> and then each of its ancestors while it is empty, stopping at
        /// <paramref name="stopDir"/>, at the project boundary, and at the first folder that still holds
        /// anything. A variant set nested as <c>files/2k/Textures/</c> leaves its whole chain behind when its
        /// files are deleted, so pruning one fixed level (what uninstall used to do) left empty variant
        /// folders in the Content tree. The pack root is the stop point: it is shared with the other variant
        /// folders and with the assets an install generates, and <see cref="TryDeleteEmptyPackFolder"/> owns it.
        /// </summary>
        private static void PruneEmptyFolders(string startDir, string stopDir)
        {
            var sep = Path.DirectorySeparatorChar;
            var projectRoot = Path.GetFullPath(Globals.ProjectFolder);
            var stop = string.IsNullOrEmpty(stopDir) ? projectRoot : Path.GetFullPath(stopDir).TrimEnd(sep);
            if (string.IsNullOrEmpty(startDir))
                return;

            var current = Path.GetFullPath(startDir).TrimEnd(sep);
            while (!string.IsNullOrEmpty(current))
            {
                if (string.Equals(current, stop, PathComparison) || string.Equals(current, projectRoot, PathComparison))
                    break;
                if (!GitSafety.IsUnderRoot(current + sep, projectRoot + sep))
                    break;
                try
                {
                    if (!Directory.Exists(current) || Directory.EnumerateFiles(current).Any() || Directory.EnumerateDirectories(current).Any())
                        break;
                    Directory.Delete(current);
                }
                catch (Exception e)
                {
                    // A folder the editor still holds open is not an error: the files are gone, and the next
                    // refresh has nowhere left to prune. Stop walking up rather than fight it.
                    Debug.LogWarning($"FlaxAssetManager: kept the folder '{current}': {e.Message}");
                    break;
                }
                current = Path.GetDirectoryName(current);
            }
        }

        /// <summary>
        /// The pack folder that owns a recorded file: the parent of the <c>files/</c> segment in its path,
        /// which is where <see cref="PruneEmptyFolders"/> stops. Falls back to the project root — a no-op
        /// boundary — for a recorded path with no <c>files/</c> segment (a plugin clone, an older record).
        /// </summary>
        private static string PackRootOfRecordedFile(string fileFull)
        {
            var sep = Path.DirectorySeparatorChar;
            var projectRoot = Path.GetFullPath(Globals.ProjectFolder);
            var dir = Path.GetDirectoryName(fileFull);
            while (!string.IsNullOrEmpty(dir) && GitSafety.IsUnderRoot(dir + sep, projectRoot + sep))
            {
                if (string.Equals(Path.GetFileName(dir), FilesSubFolder, PathComparison))
                {
                    var packRoot = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrEmpty(packRoot))
                        return packRoot;
                    break;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return projectRoot;
        }

        /// <summary>
        /// The install record's generated-asset list: what the replaced install produced, plus what this one
        /// produced, deduplicated. A variant switch does not regenerate the pack root's imported models, so
        /// without carrying the old list over, uninstall would leave assets it no longer knows about (and a
        /// rebuilt material, recorded again by the material service, is the deduplicated case).
        /// </summary>
        private static List<string> MergeGeneratedAssets(IEnumerable<string> previous, IEnumerable<string> current)
        {
            var merged = new List<string>();
            foreach (var list in new[] { previous, current })
            {
                foreach (var asset in list ?? Enumerable.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(asset) && !merged.Contains(asset, StringComparer.OrdinalIgnoreCase))
                        merged.Add(asset);
            }
            return merged;
        }

        /// <summary>Recorded paths as stored in <c>.installed.json</c>: project-relative, forward slashes.</summary>
        private static string NormalizeRel(string p) => (p ?? string.Empty).Trim().Replace('\\', '/');

        /// <summary>
        /// Model source extensions recognized when scanning extracted archive packs.
        /// </summary>
        private static readonly string[] ModelExtensions = { ".fbx", ".obj", ".blend", ".glb", ".gltf" };

        /// <summary>
        /// Runs the editor's import pipeline over the given model source files so they become
        /// loadable Flax assets: refreshes the content tree, hands each model to
        /// <see cref="FlaxEditor.Editor.ContentImporting"/> (engine default model settings, no
        /// settings dialog) with the <b>pack root</b> as the target location, so every generated
        /// <c>.flax</c> asset lands one level above <see cref="FilesSubFolder"/> — next to the
        /// created material, keeping pack-generated assets distinguishable from raw downloads.
        /// Embedded materials are <b>not</b> imported (see the options override below), so the
        /// models keep empty material slots that
        /// <see cref="MaterialCreatorService"/> fills with the pack's created PBR material.
        /// Then waits, cooperatively without blocking the main thread, until each model's
        /// <c>.flax</c> asset has been written.
        /// </summary>
        private async Task<List<string>> ImportModelsAsync(string sourceId, PackEntry pack, IEnumerable<string> modelFiles, IProgress<(float, string)> progress, CancellationToken ct)
        {
            // Filled from the .flax files the import pipeline writes into the pack root (diffed against
            // the snapshot below), so the install record can name what this install produced.
            var produced = new List<string>();
            var models = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var full in modelFiles ?? Array.Empty<string>())
            {
                // All generated assets share the pack root, so two sources with the same base
                // name would collide; keep the first and skip the rest.
                var expected = Path.GetFileNameWithoutExtension(full) + ".flax";
                if (!seen.Add(expected))
                {
                    Debug.LogWarning($"FlaxAssetManager: skipping '{Path.GetFileName(full)}'; '{expected}' is already generated by another model of pack '{pack.Id}'.");
                    continue;
                }
                models.Add(full);
            }
            if (models.Count == 0) return produced;

            Report(progress, 0.95f, $"Importing {models.Count} model(s)...");

            // The single import target: the pack root, one level above files/.
            var packRoot = Path.Combine(_settings.ContentDestinationFullPath, Sanitize(sourceId), Sanitize(pack.Id));
            var relPackRoot = MakeRelative(packRoot);

            // Make the content tree aware of the freshly written files/folders so the pack
            // root exists as a ContentFolder we can import into.
            if (!ContentDatabaseUtils.RefreshContentFolder(_settings.ContentDestinationFullPath))
                return produced;

            // Snapshot the .flax files already present in the pack root (e.g. a previously
            // created material) so the new assets the import pipeline writes can be detected.
            var known = new HashSet<string>(Directory.Exists(packRoot) ? Directory.GetFiles(packRoot, "*.flax") : Array.Empty<string>());

            var folder = ContentDatabaseUtils.FindContentFolder(relPackRoot);
            if (folder == null)
            {
                return produced;
            }
            // Do not let the engine auto-import the models' embedded materials: OpenFBX leaves
            // the EmissiveFactor member uninitialized when the FBX omits it, so an
            // engine-created material asset can carry a non-finite Emissive value whose
            // generated shader fails to compile ('inf' : unknown variable). The pack's PBR
            // material is created separately by MaterialCreatorService and assigned to the
            // model's material slots, so no engine-side material is needed.
            var importOptions = ModelTool.Options.Default;
            importOptions.ImportMaterials = false;
            foreach (var full in models)
            {
                // The actual import runs on the editor's import worker thread and registers
                // each model with a persistent asset id in the target (pack root) folder.
                Editor.Instance.ContentImporting.Import(full, folder, skipSettingsDialog: true, settings: new ModelImportSettings { Settings = importOptions });
            }

            // Wait until every model's .flax asset exists in the pack root. The engine names
            // the asset from the source base name ('model.fbx' -> 'model.flax'); accept both
            // the stripped and the extension-preserving form.
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                bool allReady = true;
                foreach (var full in models)
                {
                    if (!ModelImported(full, packRoot, known)) { allReady = false; break; }
                }
                if (allReady)
                {
                    Log?.Invoke($"Imported {models.Count} model(s) for {pack.Name}");
                    produced.AddRange(NewFlaxAssets(packRoot, known));
                    return produced;
                }
                await Task.Delay(250);
            }
            Debug.LogWarning($"FlaxAssetManager: timed out waiting for {models.Count} model(s) of pack '{pack.Id}' to import.");
            produced.AddRange(NewFlaxAssets(packRoot, known));
            return produced;
        }

        /// <summary>
        /// The .flax files in packRoot that are not in the pre-import snapshot, as project-relative
        /// paths: exactly what the import pipeline produced for this install, so uninstall can delete
        /// those assets (plus their sidecar folders) instead of leaving them in the Content tree.
        /// </summary>
        private List<string> NewFlaxAssets(string packRoot, HashSet<string> snapshot)
        {
            var result = new List<string>();
            if (!Directory.Exists(packRoot))
                return result;
            foreach (var f in Directory.EnumerateFiles(packRoot, "*.flax"))
            {
                if (snapshot.Contains(f))
                    continue;
                var rel = MakeRelative(f);
                if (!string.IsNullOrEmpty(rel))
                    result.Add(rel);
            }
            return result;
        }

        private static bool ModelImported(string modelFull, string packRoot, HashSet<string> known)
        {
            if (!Directory.Exists(packRoot)) return false;
            var fileName = Path.GetFileName(modelFull);
            var candidates = new[] { Path.GetFileNameWithoutExtension(fileName) + ".flax", fileName + ".flax" };
            foreach (var f in Directory.EnumerateFiles(packRoot, "*.flax"))
            {
                if (known.Contains(f)) continue;
                var name = Path.GetFileName(f);
                foreach (var c in candidates)
                    if (string.Equals(name, c, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Largest single download the installer accepts, as a hard guard against a manifest that
        /// points at an oversized (or hostile) URL: streaming keeps memory flat, so the limit that
        /// actually protects the editor is the one on bytes written to disk.
        /// </summary>
        private const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;

        /// <summary>
        /// Downloads <paramref name="url"/> into <paramref name="destPath"/> by streaming and only
        /// publishes the file once the optional expected SHA-256 passes. The bytes never live in
        /// memory (8K PBR sets are routinely 100-300 MB), and a failed or cancelled download leaves
        /// no truncated file at the destination.
        /// </summary>
        /// <param name="url">The http(s) URL to fetch.</param>
        /// <param name="destPath">Absolute destination path (containment-checked by the caller).</param>
        /// <param name="expectedSha256">Optional hex SHA-256 to verify (case-insensitive).</param>
        /// <param name="displayName">Name used in log and error messages.</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task DownloadToFileAsync(string url, string destPath, string expectedSha256, string displayName, CancellationToken ct)
        {
            var tempPath = destPath + ".flaxam-download";
            try
            {
                using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    var declared = response.Content.Headers.ContentLength;
                    if (declared > MaxDownloadBytes)
                        throw new Exception($"File too large ({declared} bytes, limit {MaxDownloadBytes}): {displayName}");

                    using (var src = await response.Content.ReadAsStreamAsync(ct))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(tempPath)));
                        using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                        {
                            var buffer = new byte[81920];
                            long written = 0;
                            int read;
                            while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                            {
                                written += read;
                                if (written > MaxDownloadBytes)
                                    throw new Exception($"Download exceeded the {MaxDownloadBytes} byte limit after {written} bytes: {displayName}");
                                await dst.WriteAsync(buffer, 0, read, ct);
                            }
                        }
                    }
                }

                if (new FileInfo(tempPath).Length == 0)
                    throw new Exception($"Empty download for {displayName}");

                // Optional sha256 check, hashed from the file so hashing stays memory-flat.
                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    var hash = await ComputeFileSha256Async(tempPath, ct);
                    if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"Checksum mismatch for {displayName}: expected {expectedSha256} got {hash}");
                }

                File.Move(tempPath, destPath, true);
            }
            finally
            {
                // Publish-on-success: drop the partial temp file (already gone after a successful move).
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch (Exception e) { Debug.LogWarning($"FlaxAssetManager: could not remove temp download '{tempPath}': {e.Message}"); }
                }
            }
        }

        private async Task<int> RunGitAsync(System.Collections.Generic.IReadOnlyList<string> gitArgs, string outputPath, CancellationToken ct)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    // No 'Arguments' string: each argument is added to ArgumentList below, so values
                    // coming from a manifest are never re-parsed for options/quoting.
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var gitArg in gitArgs)
                    psi.ArgumentList.Add(gitArg);
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    // Read stdout and stderr concurrently to avoid pipe-buffer deadlock.
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    var stderrTask = proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync(ct);
                    await Task.WhenAll(stdoutTask, stderrTask);

                    if (!string.IsNullOrEmpty(outputPath))
                        await File.WriteAllTextAsync(outputPath, stdoutTask.Result, ct);

                    return proc.ExitCode;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"FlaxAssetManager: git {string.Join(" ", gitArgs)} failed: {e.Message}");
                return -1;
            }
        }

        private async Task AddReferenceToProjectAsync(string folderName, string projName)
        {
            try
            {
                var rootProjPath = ResolveRootProjectPath();
                if (string.IsNullOrEmpty(rootProjPath))
                    return;
                var json = await File.ReadAllTextAsync(rootProjPath);
                // Use Newtonsoft to manipulate References array without needing ProjectInfo type
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                var refs = obj["References"] as Newtonsoft.Json.Linq.JArray;
                if (refs == null)
                {
                    refs = new Newtonsoft.Json.Linq.JArray();
                    obj["References"] = refs;
                }
                var newPath = $"$(ProjectPath)/Plugins/{folderName}/{projName}.flaxproj";
                bool exists = refs.Any(x => string.Equals(x["Name"]?.ToString(), newPath, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    refs.Add(new Newtonsoft.Json.Linq.JObject { ["Name"] = newPath });
                    await File.WriteAllTextAsync(rootProjPath, obj.ToString(Newtonsoft.Json.Formatting.Indented));
                    Debug.Log($"FlaxAssetManager: Added reference {newPath} to {Path.GetFileName(rootProjPath)}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"FlaxAssetManager: Failed to add project reference: {e.Message}");
            }
        }

        /// <summary>
        /// Removes the root .flaxproj reference(s) for a deleted plugin project so the editor
        /// doesn't try to load a now-missing .flaxproj on next launch. Mirrors AddReferenceToProjectAsync,
        /// matching on the "$(ProjectPath)/Plugins/{folderName}/" prefix (projName is unknown at uninstall time).
        /// </summary>
        private async Task RemoveReferenceFromProjectAsync(string folderName)
        {
            try
            {
                var rootProjPath = ResolveRootProjectPath();
                if (string.IsNullOrEmpty(rootProjPath))
                    return;

                var json = await File.ReadAllTextAsync(rootProjPath);
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                var refs = obj["References"] as Newtonsoft.Json.Linq.JArray;
                if (refs == null) return;

                var prefix = $"$(ProjectPath)/Plugins/{folderName}/";
                var stale = refs.Where(x => x["Name"] != null &&
                                            x["Name"].ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                if (stale.Count == 0) return;

                foreach (var r in stale)
                    refs.Remove(r);

                await File.WriteAllTextAsync(rootProjPath, obj.ToString(Newtonsoft.Json.Formatting.Indented));
                Debug.Log($"FlaxAssetManager: Removed {stale.Count} reference(s) for {folderName} from {Path.GetFileName(rootProjPath)}");
            }
            catch (Exception e)
            {
                Debug.LogError($"FlaxAssetManager: Failed to remove project reference: {e.Message}");
            }
        }

        /// <summary>
        /// Replaces characters illegal in a file/dir name (path separators, '..', illegal chars).
        /// Shared so install and material-creation resolve to identical folder names.
        /// </summary>
        public static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "unknown";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s);
            foreach (var c in invalid) sb.Replace(c, '_');
            sb.Replace('/', '_').Replace('\\', '_');
            return sb.ToString().Replace("..", "_");
        }

        /// <summary>
        /// Resolves the host project's root <c>*.flaxproj</c> (top level only). Returns null when none.
        /// </summary>
        private static string ResolveRootProjectPath()
        {
            try
            {
                return Directory.GetFiles(Globals.ProjectFolder, "*.flaxproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Deletes the files a pack already wrote before a later file failed, so a failed install
        /// leaves no orphan files. Paths are project-relative (see <see cref="MakeRelative"/>).
        /// </summary>
        private static void RollbackInstalled(IEnumerable<string> relativeFiles)
        {
            foreach (var rel in relativeFiles)
            {
                try
                {
                    var full = Path.Combine(Globals.ProjectFolder, rel.Replace('\\', '/'));
                    if (File.Exists(full))
                        File.Delete(full);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"FlaxAssetManager: rollback failed for '{rel}': {e.Message}");
                }
            }
        }

        private static string DerivePluginFolderName(string gitUrl, string fallbackId)
        {
            try
            {
                var name = gitUrl.Split('/').LastOrDefault() ?? fallbackId;
                if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - ".git".Length);
                name = name.Trim();
                if (string.IsNullOrWhiteSpace(name)) name = fallbackId;
                // Sanitize to valid identifier
                name = Sanitize(name);
                if (string.IsNullOrWhiteSpace(name)) name = Sanitize(fallbackId);
                return name;
            }
            catch
            {
                return Sanitize(fallbackId);
            }
        }

        private static string MakeRelative(string fullPath)
        {
            try
            {
                var rel = Path.GetRelativePath(Globals.ProjectFolder, fullPath);
                return rel.Replace('\\', '/');
            }
            catch
            {
                return fullPath;
            }
        }

        /// <summary>
        /// SHA-256 of a file on disk, hashed in 128 KB chunks (never materialises the file).
        /// </summary>
        private static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var hash = await sha.ComputeHashAsync(fs, ct);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private void Report(IProgress<(float, string)> p, float f, string msg)
        {
            p?.Report((f, msg));
            Progress?.Invoke(f, msg);
        }

        public async Task<bool> UninstallAsync(InstalledEntry entry, IProgress<(float, string)> progress = null, CancellationToken ct = default)
        {
            try
            {
                var name = entry.PackName;
                // Containment boundary for every delete below: nothing outside the project folder
                // may be removed, whatever the install record says.
                var projectRootFull = Path.GetFullPath(Globals.ProjectFolder) + Path.DirectorySeparatorChar;
                Log?.Invoke($"Uninstalling {name}...");
                Report(progress, 0.1f, $"Uninstalling {name}...");

                if (entry.PackType == PackType.PluginProject)
                {
                    // Delete plugin folder
                    var pluginPath = Path.GetFullPath(Path.Combine(Globals.ProjectFolder, entry.InstallPath));
                    if (!GitSafety.IsUnderRoot(pluginPath, projectRootFull))
                    {
                        Debug.LogError($"FlaxAssetManager: refusing to delete '{pluginPath}' - it resolves outside the project folder");
                        return false;
                    }
                    if (Directory.Exists(pluginPath))
                    {
                        Report(progress, 0.3f, $"Removing plugin folder...");
                        await Task.Run(() => Directory.Delete(pluginPath, true), ct);
                        Debug.Log($"FlaxAssetManager: Deleted plugin folder {pluginPath}");
                    }
                    else
                    {
                        Debug.LogWarning($"FlaxAssetManager: Plugin folder not found: {pluginPath}");
                    }
                    // Strip the plugin's reference from the root .flaxproj so the editor doesn't
                    // try to load a now-deleted .flaxproj on next launch.
                    var pluginFolder = entry.InstallPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Last();
                    await RemoveReferenceFromProjectAsync(pluginFolder);
                }
                else if ((entry.PackType == PackType.AssetPack || entry.PackType == PackType.ArchivePack) && entry.Files != null)
                {
                    // Delete each installed file
                    int total = entry.Files.Count;
                    int done = 0;
                    foreach (var relPath in entry.Files)
                    {
                        ct.ThrowIfCancellationRequested();
                        var fullPath = Path.GetFullPath(Path.Combine(Globals.ProjectFolder, relPath));
                        if (!GitSafety.IsUnderRoot(fullPath, projectRootFull))
                        {
                            Debug.LogError($"FlaxAssetManager: refusing to delete '{relPath}' - it resolves outside the project folder");
                            continue;
                        }
                        if (File.Exists(fullPath))
                        {
                            await Task.Run(() => File.Delete(fullPath), ct);
                            // Prune every folder this delete emptied, walking up to the pack root: a
                            // variant set nested as files/2k/Textures/ leaves its whole chain behind, so the
                            // old fixed two-level prune left empty variant folders in the Content tree. The
                            // pack root itself is kept (it holds the generated material), and
                            // TryDeleteEmptyPackFolder below removes it when the pack is fully gone.
                            PruneEmptyFolders(Path.GetDirectoryName(fullPath), PackRootOfRecordedFile(fullPath));
                        }
                        var cur = Interlocked.Increment(ref done);
                        Report(progress, 0.2f + 0.5f * (float)cur / total, $"Removed {cur}/{total} files");
                    }
                }

                // Generated / imported assets. Deleting the downloaded files above leaves behind what
                // the install *produced*: the M_<packId>.flax material, the .flax assets the model
                // import created, and the .flax sidecar of every imported source file. Those are the
                // assets a user sees in the Content browser, so a "Remove" that skipped them has not
                // removed the pack. Paths come from the record and pass the same project containment
                // gate as Files.
                await DeleteGeneratedAssetsAsync(entry, projectRootFull, progress, ct);

                // Remove from installed manifest
                _settings.Installed.Installed.Remove(entry);
                _settings.SaveInstalled();

                Report(progress, 1f, $"Uninstalled {name}");
                Log?.Invoke($"Uninstalled {name}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"FlaxAssetManager: Uninstall failed for {entry.PackName}: {e.Message}");
                Log?.Invoke($"Failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deletes the assets an install produced (record.generatedAssets) plus the .flax sidecar file
        /// and sidecar folder of every imported downloaded file, then prunes the pack folder when it is
        /// empty. Every path is gated by the same project-containment rule as Files; a path that fails
        /// it is logged and skipped, never deleted.
        /// </summary>
        private async Task DeleteGeneratedAssetsAsync(InstalledEntry entry, string projectRootFull, IProgress<(float, string)> progress, CancellationToken ct)
        {
            var targets = new List<string>();
            if (entry.GeneratedAssets != null)
                targets.AddRange(entry.GeneratedAssets);

            // Imported source files leave a sidecar next to them: 'Rock.tga' becomes 'Rock.tga.flax'
            // (the asset) and a 'Rock.tga/' folder with derived data. Records list the source file, so
            // derive the sidecars; that also cleans installs made before generatedAssets was recorded.
            if (entry.Files != null)
            {
                foreach (var rel in entry.Files)
                {
                    var ext = Path.GetExtension(rel).ToLowerInvariant();
                    if (!ImportedSourceExts.Contains(ext))
                        continue;
                    targets.Add(rel + ".flax");
                    targets.Add(rel);
                }
            }

            if (targets.Count == 0)
                return;

            Report(progress, 0.7f, "Removing generated assets...");
            int removed = 0;
            foreach (var rel in targets)
            {
                ct.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(Path.Combine(Globals.ProjectFolder, rel));
                if (!GitSafety.IsUnderRoot(full, projectRootFull))
                {
                    Debug.LogError($"FlaxAssetManager: refusing to delete '{rel}' - it resolves outside the project folder");
                    continue;
                }

                try
                {
                    if (File.Exists(full))
                    {
                        await Task.Run(() => File.Delete(full), ct);
                        removed++;
                    }
                    var sidecar = full + ".flax";
                    if (File.Exists(sidecar))
                    {
                        await Task.Run(() => File.Delete(sidecar), ct);
                        removed++;
                    }
                    var sidecarDir = full + "/";
                    if (Directory.Exists(sidecarDir))
                    {
                        await Task.Run(() => Directory.Delete(sidecarDir, true), ct);
                        removed++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    // A locked or in-use asset must not abort the whole uninstall: the record is
                    // removed afterwards, so report what stayed behind instead of pretending success.
                    Debug.LogWarning($"FlaxAssetManager: could not remove '{rel}': {e.Message}");
                }
            }

            // Prune the pack folder if nothing is left in it (a pack root that still holds files the
            // record does not know about is left alone on purpose).
            TryDeleteEmptyPackFolder(entry);
            Report(progress, 0.85f, $"Removed {removed} generated asset(s)");
        }

        /// <summary>
        /// Deletes the pack's install folder when it holds no files and no subfolders, so an uninstalled
        /// pack does not leave an empty shell in the Content tree.
        /// </summary>
        private void TryDeleteEmptyPackFolder(InstalledEntry entry)
        {
            if (entry.PackType == PackType.PluginProject || string.IsNullOrEmpty(entry.InstallPath))
                return;
            var root = Path.GetFullPath(Path.Combine(Globals.ProjectFolder, entry.InstallPath));
            if (!GitSafety.IsUnderRoot(root, Path.GetFullPath(Globals.ProjectFolder) + Path.DirectorySeparatorChar))
                return;
            if (!Directory.Exists(root))
                return;
            if (Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any())
                return;
            try
            {
                Directory.Delete(root, true);
                Debug.Log($"FlaxAssetManager: removed empty pack folder {root}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"FlaxAssetManager: could not remove pack folder '{root}': {e.Message}");
            }
        }
        public void Dispose()
        {
            _httpClient?.Dispose();
            _concurrencyLimiter?.Dispose();
        }
    }
}
