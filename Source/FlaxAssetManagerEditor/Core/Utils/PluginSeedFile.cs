using System;
using System.Collections.Generic;
using System.IO;
using FlaxEngine;
using Newtonsoft.Json;
using FlaxAssetManager.Core.Models;

namespace FlaxAssetManager.Core.Utils
{
    /// <summary>
    /// The plugin's own seed file: <c>seed_sources.json</c>, sitting next to this plugin's
    /// <c>.flaxproj</c>, i.e. versioned with the plugin rather than with the project that installs it.
    /// It is a plugin author's channel: whoever publishes this plugin decides what a project that
    /// installs it starts with, and every URL in it lands in the project's <c>Sources.json</c> through
    /// the same applied-once rules as a project's own <c>Settings.json</c> seeds
    /// (<see cref="Services.SettingsService.ApplySeedSources"/>, D19) - so a seeded source the user
    /// removes stays removed, and the plugin never re-pushes it.
    /// <para>
    /// Shape (one object, entries identical to <c>Settings.json</c> <c>seedSources</c>):
    /// <code>
    /// { "seedSources": [ { "url": "$(PluginPath)/docs/example_manifest.json", "name": "Example", "enabled": true } ] }
    /// </code>
    /// <c>$(PluginPath)</c> is this plugin's folder, so a seed file never hard-codes a folder name -
    /// that is the D16 rule (a folder name is not identity: it comes from a git URL, an archive, a fork,
    /// or the user) applied to a seed URL.
    /// </para>
    /// <para>
    /// The plugin folder is <b>discovered, not assumed</b>: the running assembly's ancestors, then the
    /// open project's own <c>References</c>, then the documented <c>Plugins/&lt;name&gt;</c> location,
    /// each matched by the project file (file name, or the <c>Name</c> inside it) through
    /// <see cref="FlaxProjectLocator"/>. The result is contained to the open project
    /// (<see cref="GitSafety.IsUnderRoot"/>, case-correct per platform) before anything is read: a
    /// discovered folder outside the project is reported, not read.
    /// </para>
    /// <para>
    /// Everything here is read-only, bounded (<see cref="MaxSeedFileBytes"/> through
    /// <see cref="HttpStream.ReadAllTextCapped"/>, rule 16) and non-fatal: no file, an unreadable file,
    /// an oversized file, or an unparseable one is a warning and an empty seed list, never a failed
    /// editor start. A seed file is data, not authority - it registers sources, and every fetch,
    /// validation and code-install gate applies to what it names.
    /// </para>
    /// </summary>
    public static class PluginSeedFile
    {
        /// <summary>The file name, read from the plugin's project folder.</summary>
        public const string FileName = "seed_sources.json";

        /// <summary>
        /// Ceiling for the seed file. It is a list of URLs a human writes by hand; a file of a megabyte
        /// is not a seed list, and the rule that bounds a manifest bounds this too.
        /// </summary>
        public const long MaxSeedFileBytes = 1024L * 1024L;

        /// <summary>How far up to walk from the running assembly to reach the plugin folder that built it.</summary>
        private const int AssemblyWalkDepth = 10;

        /// <summary>The JSON shape of the seed file; a bare array of entries is not accepted.</summary>
        private sealed class SeedFileShape
        {
            [JsonProperty("seedSources")]
            public List<SeedSourceEntry> SeedSources { get; set; }
        }

        /// <summary>What <see cref="Read"/> found, and how - so the caller can log the provenance.</summary>
        public sealed class Result
        {
            /// <summary>The plugin folder the file was read from; null when nothing was found.</summary>
            public string PluginFolder;

            /// <summary>The seed file path, when one was found (even if it failed to read).</summary>
            public string Path;

            /// <summary>How the plugin folder was identified (for the log).</summary>
            public string How;

            /// <summary>The entries; empty when there is no file, or it was refused.</summary>
            public List<SeedSourceEntry> Entries = new List<SeedSourceEntry>();

            /// <summary>Why the file was refused; null when it was read, or absent.</summary>
            public string Error;
        }

        /// <summary>
        /// Finds this plugin's folder and reads its <c>seed_sources.json</c>. Returns a
        /// <see cref="Result"/>; never throws, and never reads outside <paramref name="projectFolder"/>.
        /// </summary>
        public static Result Read(string projectFolder)
        {
            var result = new Result();
            if (string.IsNullOrWhiteSpace(projectFolder))
                return result;

            string rootFull;
            try
            {
                rootFull = Path.GetFullPath(projectFolder);
            }
            catch (Exception)
            {
                return result;
            }

            foreach (var candidate in EnumeratePluginFolders(projectFolder))
            {
                string folderFull;
                try
                {
                    folderFull = Path.GetFullPath(candidate.Folder);
                }
                catch (Exception)
                {
                    continue;
                }

                // Read-side containment, the same check the write side of folder discovery runs (D14):
                // a plugin folder outside the open project is reported, not read.
                if (!GitSafety.IsUnderRoot(folderFull, rootFull))
                {
                    result.PluginFolder = folderFull;
                    result.How = candidate.How;
                    result.Path = Path.Combine(folderFull, FileName);
                    result.Error = $"'{folderFull}' is outside the open project '{rootFull}'.";
                    return result;
                }

                var seedPath = Path.Combine(folderFull, FileName);
                if (!File.Exists(seedPath))
                    continue;

                result.PluginFolder = folderFull;
                result.How = candidate.How;
                result.Path = seedPath;
                return ReadFile(seedPath, result);
            }

            return result;
        }

        /// <summary>
        /// Parses the seed file under the ceiling. A missing list, a hole in it, or a JSON error is
        /// reported through <see cref="Result.Error"/> and leaves the entry list empty.
        /// </summary>
        private static Result ReadFile(string seedPath, Result result)
        {
            string json;
            try
            {
                json = HttpStream.ReadAllTextCapped(seedPath, MaxSeedFileBytes);
            }
            catch (HttpStream.OversizedResponseException)
            {
                result.Error = $"'{seedPath}' is larger than {MaxSeedFileBytes / 1024} KiB.";
                return result;
            }
            catch (Exception e)
            {
                result.Error = $"'{seedPath}' could not be read: {e.Message}";
                return result;
            }

            try
            {
                var shape = JsonConvert.DeserializeObject<SeedFileShape>(json);
                if (shape == null)
                {
                    result.Error = $"'{seedPath}' is empty.";
                    return result;
                }

                if (shape.SeedSources == null)
                {
                    result.Error = $"'{seedPath}' has no seedSources array.";
                    return result;
                }

                result.Entries = shape.SeedSources;
                return result;
            }
            catch (Exception e)
            {
                result.Error = $"'{seedPath}' is not valid JSON: {e.Message}";
                return result;
            }
        }

        /// <summary>
        /// The plugin folder, by the same matching rules folder discovery uses everywhere (D16): the
        /// project file, by file name or by the <c>Name</c> inside it. Read-only routes, cheapest first;
        /// no <c>Plugins/</c> tree scan, because a read that finds nothing costs a directory walk and
        /// every route that matters is a lookup.
        /// </summary>
        private static IEnumerable<(string Folder, string How)> EnumeratePluginFolders(string projectFolder)
        {
            var name = Services.PluginUpdateService.PluginProjectName;

            // 1. The plugin folder the running assembly was built into: walk up from the assembly and
            // stop at the first ancestor holding this plugin's project file.
            var assemblyDir = SafeAssemblyDirectory();
            if (!string.IsNullOrWhiteSpace(assemblyDir))
            {
                foreach (var dir in FlaxProjectLocator.EnumerateAncestorDirectories(assemblyDir, AssemblyWalkDepth))
                {
                    if (FlaxProjectLocator.FindProjectFileInDirectory(dir, name) == null)
                        continue;

                    yield return (dir, "the project folder that built the running assembly");
                    break;
                }
            }

            // 2. The open project's References: the project tree's own record of where its plugins are.
            var rootProjectFile = FlaxProjectLocator.FindRootProjectFile(projectFolder);
            if (rootProjectFile != null)
            {
                foreach (var reference in FlaxProjectLocator.ReadReferenceProjectPaths(rootProjectFile, EngineFolderForMacros()))
                {
                    if (!FlaxProjectLocator.IsProjectNamed(reference, name))
                        continue;

                    var dir = Path.GetDirectoryName(reference);
                    if (!string.IsNullOrWhiteSpace(dir))
                        yield return (dir, $"the reference '{reference}' of '{Path.GetFileName(rootProjectFile)}'");
                }
            }

            // 3. The documented location, validated by its project file (a folder that merely looks
            // like this plugin is not this plugin).
            var documented = Path.Combine(projectFolder, "Plugins", name);
            if (FlaxProjectLocator.FindProjectFileInDirectory(documented, name) != null)
                yield return (documented, $"the documented location 'Plugins/{name}'");
        }

        private static string SafeAssemblyDirectory()
        {
            try
            {
                return Path.GetDirectoryName(typeof(PluginSeedFile).Assembly.Location);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string EngineFolderForMacros()
        {
            try
            {
                var contentFolder = Globals.EngineContentFolder;
                if (string.IsNullOrWhiteSpace(contentFolder))
                    return Globals.StartupFolder ?? string.Empty;

                var parent = Path.GetFullPath(Path.Combine(contentFolder, ".."));
                return Directory.Exists(parent) ? parent : (Globals.StartupFolder ?? string.Empty);
            }
            catch (Exception)
            {
                return Globals.StartupFolder ?? string.Empty;
            }
        }
    }
}
