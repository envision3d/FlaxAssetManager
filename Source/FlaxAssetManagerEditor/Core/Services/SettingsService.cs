using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlaxEngine;
using Newtonsoft.Json;
using FlaxAssetManager.Core.Models;
using FlaxAssetManager.Core.Utils;

namespace FlaxAssetManager.Core.Services
{
    /// <summary>
    /// Persists plugin settings and source URL list.
    /// </summary>
    public class SettingsService
    {
        private readonly string _settingsPath;
        private readonly string _sourcesPath;
        private readonly string _installManifestPath;
        private readonly string _attributionsPath;

        public FlaxAssetManagerSettings Settings { get; private set; }
        public AssetSourceConfig SourceConfig { get; private set; }
        public InstalledManifest Installed { get; private set; }
        /// <summary>
        /// Destinations come from <c>Settings.json</c>, i.e. from data on disk like a manifest does, and
        /// they are the base of every installed path. A rooted value, a Windows drive hint (<c>C:foo</c>),
        /// a drive root, a UNC share, a bare <c>/</c> or a traversal makes
        /// <c>Path.Combine(ProjectFolder, destination)</c> resolve **outside** the project, so installs
        /// would write - and uninstalls delete - files anywhere the user can reach. Invalid values fall
        /// back to the shipped defaults; paths inside a pack are checked per file as before.
        /// </summary>
        public static bool IsValidDestination(string destination)
        {
            if (string.IsNullOrWhiteSpace(destination))
                return false;

            // Normalise Windows separators before checking, so Content\FlaxAssetManager stays valid on
            // every platform while a rooted, drive-hinted, UNC or traversing value does not.
            var normalized = destination.Trim().Replace('\\', '/');
            if (normalized.IndexOf(':') >= 0)
                return false;
            if (normalized.StartsWith("/") || normalized.StartsWith("~"))
                return false;
            if (Path.IsPathRooted(normalized))
                return false;
            if (GitSafety.ContainsTraversal(normalized))
                return false;

            var segments = normalized.Split('/');
            var any = false;
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].Length == 0 || segments[i] == ".")
                    return false;
                any = true;
            }

            return any;
        }

        public string ContentDestinationFullPath => Path.Combine(Globals.ProjectFolder, Settings.ContentDestination);
        public string ScriptDestinationFullPath => Path.Combine(Globals.ProjectFolder, Settings.ScriptDestination);
        public string PluginsFolder => Path.Combine(Globals.ProjectFolder, "Plugins");

        /// <summary>
        /// Absolute path of <c>Content/FlaxAssetManager/Sources.json</c>, the manifest source list.
        /// Exposed so the UI can hand the file to an external editor.
        /// </summary>
        public string SourcesJsonPath => _sourcesPath;

        /// <summary>Origin label of the plugin's seed channel, so the log says where a source arrived from.</summary>
        private const string PluginSeedOrigin = "seed_sources.json (plugin)";

        /// <summary>
        /// Entries of the plugin's own <c>seed_sources.json</c>, read once by
        /// <see cref="LoadPluginSeedFile"/>; empty when the plugin ships none, when the project opted out
        /// (<c>Settings.json</c> <c>pluginSeed:false</c>), or when the file was refused.
        /// </summary>
        private List<SeedSourceEntry> _pluginSeeds = new List<SeedSourceEntry>();

        /// <summary>
        /// This plugin's folder, resolved once at load by <see cref="PluginSeedFile"/> - discovered by its
        /// project file, never by an assumed folder name (D16) - and contained to the open project; it is
        /// what <c>$(PluginPath)</c> expands to in a source URL. Null when the folder was not found, or
        /// fell outside the project, in which case the macro expands to nothing and the source reports a
        /// fetch failure rather than reading outside the project.
        /// </summary>
        public string PluginFolderFullPath { get; private set; }

        /// <summary>
        /// Absolute path of the generated <c>Content/FlaxAssetManager/ATTRIBUTIONS.md</c>, the list of
        /// every pack this project installed and the license each one came with.
        /// </summary>
        public string AttributionsPath => _attributionsPath;

        public SettingsService()
        {
            var baseDir = Path.Combine(Globals.ProjectFolder, "Content", "FlaxAssetManager");
            _settingsPath = Path.Combine(baseDir, "Settings.json");
            _sourcesPath = Path.Combine(baseDir, "Sources.json");
            _installManifestPath = Path.Combine(baseDir, ".installed.json");
            _attributionsPath = Path.Combine(baseDir, AttributionsService.FileName);
            Load();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                    Settings = JsonConvert.DeserializeObject<FlaxAssetManagerSettings>(File.ReadAllText(_settingsPath)) ?? new FlaxAssetManagerSettings();
                else
                    Settings = new FlaxAssetManagerSettings();
            }
            catch
            {
                Settings = new FlaxAssetManagerSettings();
            }

        // A destination that escapes the project is a bug in the settings file, not a policy the
        // installer should honour: installs write and uninstalls delete under these bases.
        var defaults = new FlaxAssetManagerSettings();
        if (!IsValidDestination(Settings.ContentDestination))
        {
            FlaxEngine.Debug.LogWarning($"FlaxAssetManager: ignoring invalid contentDestination '{Settings.ContentDestination}' in Settings.json; using '{defaults.ContentDestination}'.");
            Settings.ContentDestination = defaults.ContentDestination;
        }
        if (!IsValidDestination(Settings.ScriptDestination))
        {
            FlaxEngine.Debug.LogWarning($"FlaxAssetManager: ignoring invalid scriptDestination '{Settings.ScriptDestination}' in Settings.json; using '{defaults.ScriptDestination}'.");
            Settings.ScriptDestination = defaults.ScriptDestination;
        }

            LoadSources();

            try
            {
                if (File.Exists(_installManifestPath))
                    Installed = JsonConvert.DeserializeObject<InstalledManifest>(File.ReadAllText(_installManifestPath)) ?? new InstalledManifest();
                else
                    Installed = new InstalledManifest();
            }
            catch
            {
                Installed = new InstalledManifest();
            }

            // Ensure directories exist for first run
            EnsureDirectories();

            // Resolve this plugin's folder and its seed_sources.json, then apply every seed this project
            // has not been seeded with yet - the project's own Settings.json list and the plugin's file.
            // With neither, nothing is written.
            LoadPluginSeedFile();
            ApplySeedSources();
        }

        private void LoadSources()
        {
            try
            {
                if (File.Exists(_sourcesPath))
                    SourceConfig = JsonConvert.DeserializeObject<AssetSourceConfig>(File.ReadAllText(_sourcesPath)) ?? new AssetSourceConfig();
                else
                    SourceConfig = new AssetSourceConfig();
            }
            catch
            {
                SourceConfig = new AssetSourceConfig();
            }
        }

        /// <summary>
        /// Re-reads Sources.json from disk so Refresh picks up externally edited
        /// source lists without restarting the editor, then re-applies the <c>Settings.json</c> seed
        /// list (<see cref="ApplySeedSources"/>), so a seed added or edited by hand takes effect on the
        /// same Refresh, by the same rule, and is fetched on that Refresh.
        /// </summary>
        public void ReloadSources()
        {
            LoadSources();
            ApplySeedSources();
        }

        /// <summary>
        /// Appends every seed URL this project has not been seeded with yet - from the project's own
        /// <c>Settings.json</c> <c>seedSources</c>, and from the <c>seed_sources.json</c> the plugin ships
        /// (<see cref="LoadPluginSeedFile"/>, D19) - so a team that commits a <c>Settings.json</c> gives
        /// every checkout of the project the same starting manifest sources, a plugin author can ship the
        /// same starting point with the plugin, and a fresh project has something to browse before anyone
        /// types a URL. Called from <see cref="Load"/> - so the first fetch of a session already sees the
        /// seeds - and from <see cref="ReloadSources"/>, the documented apply-on-<b>Refresh</b> path for
        /// external edits: editing a project's seed list takes effect on the next <b>Refresh</b>, exactly
        /// like editing <c>Sources.json</c> does.
        /// <para>
        /// Each seed URL is applied <b>once</b>, which is what <c>Sources.json</c> <c>seededUrls</c>
        /// records. That record is the point: a source the user removes - the window's <b>Remove</b>, an
        /// <b>Edit</b> that renames it, or deleting the line by hand - stays removed, because a seed that
        /// reappears on every editor start is a source that cannot be removed. A seed whose URL is already
        /// configured is recorded as applied and its existing entry is left alone (the user's display name
        /// and enabled flag win); the seed's <c>name</c> and <c>enabled</c> are honoured only when the seed
        /// creates the entry. URL comparisons are <c>OrdinalIgnoreCase</c>, the way <see cref="AddSource"/>
        /// and <see cref="RemoveSource"/> compare, so a scheme-casing difference cannot smuggle a duplicate
        /// back in or hide a removal.
        /// </para>
        /// <para>
        /// A seed names the channel it came from, and each channel stays in its lane: the project's list is
        /// project config, and the plugin's file names only what its own author publishes: this plugin seeds
        /// its author's hosted manifest index, and never anyone else's catalog - it advertises no third-party
        /// source and forces no URL on a project that declines the file (<c>docs/DECISIONS.md</c> D12), which
        /// <c>pluginSeed:false</c> does in full. Either way a seeded source goes through the same manifest
        /// validation, size caps and installs-code confirmation as a hand-added URL. A seed installs nothing
        /// by itself - it makes a source <b>browsable</b>; installs stay explicit.
        /// </para>
        /// </summary>
        /// <returns>How many sources were appended to the list. 0 writes nothing.</returns>
        public int ApplySeedSources()
        {
            // Two seed channels, one rule set: the project's own Settings.json list first (its author's
            // intent takes the order), then the file the plugin ships. Each entry carries the channel it
            // came from, so a source says in the log where it arrived from.
            var seeds = new List<(SeedSourceEntry Entry, string Origin)>();
            var projectSeeds = Settings?.SeedSources;
            if (projectSeeds != null)
                for (int i = 0; i < projectSeeds.Count; i++)
                    seeds.Add((projectSeeds[i], "Settings.json seedSources"));
            if (_pluginSeeds != null)
                for (int i = 0; i < _pluginSeeds.Count; i++)
                    seeds.Add((_pluginSeeds[i], PluginSeedOrigin));

            if (seeds.Count == 0)
                return 0;

            if (SourceConfig == null)
                SourceConfig = new AssetSourceConfig();
            if (SourceConfig.Sources == null)
                SourceConfig.Sources = new List<SourceUrlEntry>();
            if (SourceConfig.SeededUrls == null)
                SourceConfig.SeededUrls = new List<string>();

            var added = 0;
            var changed = false;
            for (int i = 0; i < seeds.Count; i++)
            {
                var seed = seeds[i].Entry;
                var url = seed?.Url?.Trim();
                if (string.IsNullOrWhiteSpace(url))
                {
                    // A seed list with a hole in it is a config bug; name the channel and the entry, and
                    // keep applying the rest rather than abandoning the whole seed over one typo.
                    Debug.LogWarning($"FlaxAssetManager: ignoring {seeds[i].Origin}[{i}]: no url.");
                    continue;
                }

                if (SourceConfig.SeededUrls.Exists(s => string.Equals(s, url, StringComparison.OrdinalIgnoreCase)))
                    continue;

                SourceConfig.SeededUrls.Add(url);
                changed = true;

                if (SourceConfig.Sources.Exists(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
                {
                    // Configured by hand, or added before this seed existed: record it as applied so the
                    // seed never resurrects it once the user removes it, and leave that entry's name alone.
                    continue;
                }

                var enabled = seed.Enabled;
                SourceConfig.Sources.Add(new SourceUrlEntry
                {
                    Url = url,
                    Enabled = enabled,
                    Name = string.IsNullOrWhiteSpace(seed.Name) ? null : seed.Name.Trim(),
                });
                added++;
                Debug.Log($"FlaxAssetManager: seeded source '{url}' from {seeds[i].Origin}{(enabled ? string.Empty : " (disabled)")}.");
            }

            // One write for the whole pass, and only when there is something to persist: rewriting
            // Sources.json on every editor start would dirty a tracked file for every developer.
            if (changed)
                SaveSources();

            return added;
        }

        /// <summary>
        /// Resolves this plugin's folder and reads the <c>seed_sources.json</c> it ships - the plugin
        /// author's seed channel (D19). Read <b>once per session</b>, unlike a project's own
        /// <c>Settings.json</c> seeds, which <see cref="ReloadSources"/> re-reads on every
        /// <b>Refresh</b>: a plugin's own files do not change under a running editor, so editing the seed
        /// file takes effect on the next start (which is also what <b>Update Plugin</b> asks for).
        /// <para>
        /// The folder is discovered by this plugin's project file and contained to the open project; a
        /// folder outside the project is reported and neither read nor exposed to <c>$(PluginPath)</c>.
        /// <c>pluginSeed:false</c> in <c>Settings.json</c> skips the file's entries while leaving
        /// <c>$(PluginPath)</c> usable, so a project keeps the last word on what registers in its source
        /// list without losing the ability to name the plugin's own files.
        /// </para>
        /// </summary>
        private void LoadPluginSeedFile()
        {
            var found = PluginSeedFile.Read(Globals.ProjectFolder);
            if (found.Error != null)
            {
                // A plugin folder outside the open project: reported, not read, and $(PluginPath) stays
                // unresolved so no source URL can reach outside the project through the macro.
                FlaxEngine.Debug.LogWarning($"FlaxAssetManager: plugin folder not used for seeding: {found.Error}");
            }
            else
            {
                PluginFolderFullPath = found.PluginFolder;
            }

            if (Settings != null && !Settings.PluginSeed)
            {
                Debug.Log("FlaxAssetManager: pluginSeed is false in Settings.json, so the plugin's seed_sources.json is not applied to this project.");
                return;
            }

            _pluginSeeds = found.Entries ?? new List<SeedSourceEntry>();
            if (_pluginSeeds.Count == 0)
                return;

            Debug.Log($"FlaxAssetManager: plugin seed file '{found.Path}' (found via {found.How}) lists {_pluginSeeds.Count} source(s).");
        }

        public void EnsureDirectories()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath));
                Directory.CreateDirectory(ContentDestinationFullPath);
                Directory.CreateDirectory(ScriptDestinationFullPath);
            }
            catch { }
        }

        public void SaveSettings()
        {
            try
            {
                EnsureDirectories();
                File.WriteAllText(_settingsPath, JsonConvert.SerializeObject(Settings, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogError($"FlaxAssetManager: Failed to save settings: {e.Message}");
            }
        }

        /// <summary>
        /// Writes Sources.json when it is missing so the list always exists before it is opened in
        /// an external editor (a fresh install has the file created only on the first add/remove).
        /// Returns false when it could not be written.
        /// </summary>
        public bool EnsureSourcesFile()
        {
            try
            {
                if (File.Exists(_sourcesPath))
                    return true;
                EnsureDirectories();
                File.WriteAllText(_sourcesPath, JsonConvert.SerializeObject(SourceConfig ?? new AssetSourceConfig(), Formatting.Indented));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"FlaxAssetManager: Failed to create sources file: {e.Message}");
                return false;
            }
        }

        public void SaveSources()
        {
            try
            {
                EnsureDirectories();
                File.WriteAllText(_sourcesPath, JsonConvert.SerializeObject(SourceConfig, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogError($"FlaxAssetManager: Failed to save sources: {e.Message}");
            }
        }

        public void SaveInstalled()
        {
            try
            {
                EnsureDirectories();
                File.WriteAllText(_installManifestPath, JsonConvert.SerializeObject(Installed, Formatting.Indented));
                // One choke point for the project's attribution list: every install, remove and
                // material-update writes its record through here, so ATTRIBUTIONS.md is never stale by
                // more than the write that just happened, and no new call site has to remember it.
                AttributionsService.TryWrite(Path.GetDirectoryName(_installManifestPath), Installed);
            }
            catch (Exception e)
            {
                Debug.LogError($"FlaxAssetManager: Failed to save install manifest: {e.Message}");
            }
        }

        public void AddSource(string url)
        {
            url = url?.Trim();
            if (string.IsNullOrWhiteSpace(url))
                return;
            if (SourceConfig.Sources.Exists(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
                return;
            SourceConfig.Sources.Add(new SourceUrlEntry { Url = url, Enabled = true });
            SaveSources();
        }

        public void RemoveSource(string url)
        {
            SourceConfig.Sources.RemoveAll(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase));
            SaveSources();
        }

        /// <summary>
        /// Updates the manifest URL of an existing source, located by its current URL.
        /// The display Name and Enabled flag are preserved; matching installed records have
        /// their SourceUrl rewritten so they do not keep pointing at a URL that is no longer
        /// configured. Returns false when the new URL is empty or already used by another source.
        /// </summary>
        public bool RenameSource(string oldUrl, string newUrl)
        {
            var old = oldUrl?.Trim();
            var neu = newUrl?.Trim();
            if (string.IsNullOrWhiteSpace(old) || string.IsNullOrWhiteSpace(neu))
                return false;
            var entry = SourceConfig.Sources.Find(s => string.Equals(s.Url, old, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return false;
            if (string.Equals(entry.Url, neu, StringComparison.OrdinalIgnoreCase))
                return false;
            if (SourceConfig.Sources.Any(s => s != entry && string.Equals(s.Url, neu, StringComparison.OrdinalIgnoreCase)))
                return false;
            entry.Url = neu;
            foreach (var installed in Installed.Installed)
            {
                if (string.Equals(installed.SourceUrl, old, StringComparison.OrdinalIgnoreCase))
                    installed.SourceUrl = neu;
            }
            SaveSources();
            SaveInstalled();
            return true;
        }

        public InstalledEntry FindInstalled(string sourceId, string packId)
        {
            return Installed.Installed.Find(x => x.SourceId == sourceId && x.PackId == packId);
        }
        /// <summary>
        /// The install records of one source, keyed by pack id, in a single pass over the records.
        /// <see cref="FindInstalled"/> is a linear scan, and the asset list wants the record of every row:
        /// asking per row made a list rebuild quadratic in how much is installed, and it is asked on every
        /// search keystroke. The first record for a pack id wins, as <see cref="FindInstalled"/> does.
        /// </summary>
        public Dictionary<string, InstalledEntry> BuildInstalledIndex(string sourceId)
        {
            var records = Installed?.Installed;
            var byPack = new Dictionary<string, InstalledEntry>(StringComparer.Ordinal);
            if (records == null)
                return byPack;
            for (int i = 0; i < records.Count; i++)
            {
                var e = records[i];
                if (e == null || e.SourceId != sourceId || string.IsNullOrEmpty(e.PackId))
                    continue;
                if (!byPack.ContainsKey(e.PackId))
                    byPack.Add(e.PackId, e);
            }
            return byPack;
        }

        public bool HasUpdate(PackEntry pack, string sourceId)
        {
            var e = FindInstalled(sourceId, pack.Id);
            if (e == null) return false;
            return !string.Equals(e.InstalledVersion, pack.Version, StringComparison.Ordinal);
        }

        public void RemoveInstalled(string sourceId, string packId)
        {
            var entry = FindInstalled(sourceId, packId);
            if (entry != null)
            {
                Installed.Installed.Remove(entry);
                SaveInstalled();
            }
        }
    }
}
