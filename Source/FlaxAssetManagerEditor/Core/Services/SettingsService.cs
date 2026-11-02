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
        /// source lists without restarting the editor.
        /// </summary>
        public void ReloadSources()
        {
            LoadSources();
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
