using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace FlaxAssetManager.Core.Utils
{
    /// <summary>
    /// Finds Flax project files (<c>*.flaxproj</c>) on disk by their <b>content</b> and location, never
    /// by assuming a folder name.
    /// <para>
    /// A plugin identifies itself with its project file, not with the folder it sits in: the file name
    /// (<c>FlaxAssetManager.flaxproj</c>) and the <c>Name</c> field inside it. A folder name is whatever
    /// the user, a git URL, or an archive chose — <c>Plugins/Flax Asset Manager</c>,
    /// <c>Plugins/flax-asset-manager</c>, a fork, a dev checkout with the plugin as the open project. Code
    /// that needs "where does this plugin live" has to read the project files, so this is that reader.
    /// </para>
    /// <para>
    /// Every entry point is defensive by construction: a directory it cannot read is skipped, not thrown;
    /// a scan is bounded by depth and by a directory budget; a <c>.flaxproj</c> is only parsed when it is
    /// small enough to be a project file. Deliberately engine-free so the rules can be exercised in
    /// isolation, like <see cref="GitSafety"/>.
    /// </para>
    /// </summary>
    public static class FlaxProjectLocator
    {
        /// <summary>Extension of a Flax project file.</summary>
        public const string ProjectFileExtension = ".flaxproj";

        /// <summary>A file over this size is not a project file; reading it is not worth the risk.</summary>
        private const long MaxProjectFileBytes = 256 * 1024;

        /// <summary>
        /// Directories a project scan must not descend into: build output, caches and generated trees,
        /// which are the bulk of any Flax tree and never contain a project file.
        /// </summary>
        private static readonly string[] SkipDirectoryNames =
        {
            "Binaries", "Cache", "Intermediate", "Saved", "Packages", "obj", "bin", "Content", "Source",
            ".git", ".vs", ".idea",
        };

        /// <summary>
        /// Path comparison that is correct for the running platform: an ignore-case compare on Windows,
        /// an ordinal one on a case-sensitive filesystem (see <see cref="GitSafety.IsUnderRoot"/>).
        /// </summary>
        public static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>Case-correct string comparer for paths, matching <see cref="PathComparison"/>.</summary>
        public static StringComparer PathComparer =>
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>
        /// The <c>Name</c> field of a <c>.flaxproj</c>, or null when the file is missing, unreadable,
        /// oversized, not JSON, or has no string <c>Name</c>. A project's name is what it calls itself;
        /// the folder name is not it.
        /// </summary>
        public static string ReadProjectName(string projectFilePath)
        {
            if (string.IsNullOrEmpty(projectFilePath))
                return null;

            try
            {
                var info = new FileInfo(projectFilePath);
                if (!info.Exists || info.Length > MaxProjectFileBytes)
                    return null;

                var json = File.ReadAllText(projectFilePath);
                var obj = JObject.Parse(json);
                var name = obj["Name"]?.Value<string>();
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            catch (Exception)
            {
                // A malformed project file is a fact about the tree, not an error to surface: the caller
                // is looking for a project and this one is not it.
                return null;
            }
        }

        /// <summary>
        /// True when <paramref name="projectFilePath"/> is the project file of the project named
        /// <paramref name="projectName"/>: its file name is <c>&lt;projectName&gt;.flaxproj</c>, or the
        /// <c>Name</c> inside it says so. The file-name test runs first because it needs no read, and the
        /// content test covers the case where the file itself was renamed with the folder.
        /// </summary>
        public static bool IsProjectNamed(string projectFilePath, string projectName)
        {
            if (string.IsNullOrEmpty(projectFilePath) || string.IsNullOrEmpty(projectName))
                return false;

            var fileName = Path.GetFileNameWithoutExtension(projectFilePath);
            if (string.Equals(fileName, projectName, PathComparison))
                return true;

            var name = ReadProjectName(projectFilePath);
            return name != null && string.Equals(name, projectName, PathComparison);
        }

        /// <summary>
        /// The project file of <paramref name="projectName"/> directly inside <paramref name="dir"/>,
        /// or null. <c>TopDirectoryOnly</c> on purpose: a project file marks the root of its own project,
        /// so anything deeper belongs to a nested project, not this directory.
        /// </summary>
        public static string FindProjectFileInDirectory(string dir, string projectName)
        {
            foreach (var file in EnumerateProjectFilesInDirectory(dir))
            {
                if (IsProjectNamed(file, projectName))
                    return file;
            }

            return null;
        }

        /// <summary>Any <c>.flaxproj</c> directly inside <paramref name="dir"/>, or null.</summary>
        public static string FindAnyProjectFileInDirectory(string dir)
        {
            foreach (var file in EnumerateProjectFilesInDirectory(dir))
                return file;

            return null;
        }

        /// <summary>
        /// <paramref name="startDir"/> and each of its ancestors, nearest first, at most
        /// <paramref name="maxDepth"/> steps up. The way to turn "the assembly I am running" into "the
        /// project folder that built it" without hard-coding the builder's
        /// <c>Binaries/&lt;target&gt;/&lt;platform&gt;/&lt;arch&gt;/&lt;config&gt;</c> depth.
        /// </summary>
        public static IEnumerable<string> EnumerateAncestorDirectories(string startDir, int maxDepth)
        {
            var dir = startDir;
            for (int depth = 0; depth < maxDepth && !string.IsNullOrEmpty(dir); depth++)
            {
                yield return dir;
                dir = SafeGetParentDirectory(dir);
            }
        }

        /// <summary>
        /// Every <c>.flaxproj</c> under <paramref name="rootDir"/>, breadth-first so the shallowest (the
        /// most likely intended) come first. Two budgets bound it: <paramref name="maxDepth"/> is how many
        /// directory levels below <paramref name="rootDir"/> are still read (0 = <paramref name="rootDir"/>
        /// alone), and <paramref name="maxDirectories"/> is how many directories are read in total,
        /// <paramref name="rootDir"/> counted. It never descends into <see cref="SkipDirectoryNames"/>, and
        /// an unreadable directory is skipped rather than thrown.
        /// </summary>
        public static List<string> FindProjectFilesRecursive(string rootDir, int maxDepth, int maxDirectories)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(rootDir) || maxDepth < 0)
                return found;

            var queue = new Queue<string>();
            queue.Enqueue(rootDir);
            var depths = new Queue<int>();
            depths.Enqueue(0);
            var directoriesRead = 0;

            while (queue.Count > 0)
            {
                var dir = queue.Dequeue();
                var depth = depths.Dequeue();

                if (++directoriesRead > maxDirectories)
                    break;

                foreach (var file in EnumerateProjectFilesInDirectory(dir))
                    found.Add(file);

                if (depth >= maxDepth)
                    continue;

                foreach (var child in EnumerateSubDirectories(dir))
                {
                    queue.Enqueue(child);
                    depths.Enqueue(depth + 1);
                }
            }

            return found;
        }

        /// <summary>
        /// The root project file of an open project (a <c>.flaxproj</c> at the top of
        /// <paramref name="projectFolder"/>), preferring the one named after the folder. Null when none.
        /// </summary>
        public static string FindRootProjectFile(string projectFolder)
        {
            if (string.IsNullOrEmpty(projectFolder))
                return null;

            var files = EnumerateProjectFilesInDirectory(projectFolder);
            if (files == null || files.Count == 0)
                return null;
            if (files.Count == 1)
                return files[0];

            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(projectFolder));
            foreach (var file in files)
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(file), folderName, PathComparison))
                    return file;
            }

            return files[0];
        }

        /// <summary>
        /// The project files this project references — the <c>References[].Name</c> entries of its
        /// <c>.flaxproj</c>, with Flax's <c>$(ProjectPath)</c> / <c>$(EnginePath)</c> / <c>$(ProjectName)</c>
        /// macros expanded and a bare relative path taken against the folder of
        /// <paramref name="projectFilePath"/>. Only files that exist are returned.
        /// <para>
        /// This is the tree's own statement of where its plugins live, so it is the one source of truth
        /// that survives a renamed plugin folder: the reference was written when the plugin was added.
        /// </para>
        /// </summary>
        public static List<string> ReadReferenceProjectPaths(string projectFilePath, string engineFolder)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(projectFilePath))
                return result;

            var ownFolder = SafeGetParentDirectory(projectFilePath);
            var seen = new HashSet<string>(PathComparer);

            try
            {
                var info = new FileInfo(projectFilePath);
                if (!info.Exists || info.Length > MaxProjectFileBytes)
                    return result;

                var obj = JObject.Parse(File.ReadAllText(projectFilePath));
                var references = obj["References"] as JArray;
                if (references == null)
                    return result;

                foreach (var reference in references)
                {
                    var raw = reference?["Name"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    var expanded = ExpandMacros(raw, ownFolder, engineFolder);
                    if (expanded == null)
                        continue;

                    string full;
                    try
                    {
                        // A reference with no macro is relative to the project that carries it, never to
                        // whatever the working directory happens to be.
                        full = Path.IsPathRooted(expanded)
                            ? Path.GetFullPath(expanded)
                            : Path.GetFullPath(Path.Combine(ownFolder ?? string.Empty, expanded));
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (!full.EndsWith(ProjectFileExtension, PathComparison))
                        continue;
                    if (!File.Exists(full))
                        continue;
                    if (!seen.Add(full))
                        continue;

                    result.Add(full);
                }
            }
            catch (Exception)
            {
                // Unreadable or malformed: this project tells us nothing, which is an answer.
            }

            return result;
        }

        /// <summary>
        /// Expands Flax's project macros. A reference that uses a macro this call cannot resolve (an
        /// unknown name, or <c>$(EnginePath)</c> with no engine folder) yields null so the caller skips
        /// it rather than resolving a path into the wrong place.
        /// </summary>
        private static string ExpandMacros(string referencePath, string projectFolder, string engineFolder)
        {
            var path = referencePath.Trim().Replace('\\', '/');

            for (int guard = 0; guard < 8; guard++)
            {
                int open = path.IndexOf("$(", StringComparison.Ordinal);
                if (open < 0)
                    break;

                int close = path.IndexOf(')', open);
                if (close < 0)
                    return null;

                var macro = path.Substring(open + 2, close - open - 2).Trim();
                string value;
                switch (macro)
                {
                case "ProjectPath":
                    value = projectFolder;
                    break;
                case "EnginePath":
                    value = engineFolder;
                    break;
                case "ProjectName":
                    value = string.IsNullOrEmpty(projectFolder)
                        ? null
                        : Path.GetFileName(Path.TrimEndingDirectorySeparator(projectFolder));
                    break;
                default:
                    return null;
                }

                if (string.IsNullOrEmpty(value))
                    return null;

                path = path.Substring(0, open) + value.TrimEnd('/', '\\') + path.Substring(close + 1);
            }

            return path.Replace('\\', '/');
        }

        /// <summary>
        /// The <c>*.flaxproj</c> files directly inside <paramref name="dir"/>, sorted for a deterministic
        /// order, or an empty list when the directory is missing, unreadable, or has none.
        /// </summary>
        private static List<string> EnumerateProjectFilesInDirectory(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return new List<string>();

                var files = new List<string>(Directory.GetFiles(dir, "*" + ProjectFileExtension, SearchOption.TopDirectoryOnly));
                files.Sort(StringComparer.Ordinal);
                return files;
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>The sub-directories of <paramref name="dir"/> that a project scan should descend into.</summary>
        private static List<string> EnumerateSubDirectories(string dir)
        {
            try
            {
                var dirs = new List<string>(Directory.GetDirectories(dir));
                dirs.Sort(StringComparer.Ordinal);

                var kept = new List<string>(dirs.Count);
                foreach (var full in dirs)
                {
                    var name = Path.GetFileName(full);
                    if (ShouldSkipDirectory(name))
                        continue;
                    kept.Add(full);
                }

                return kept;
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        private static bool ShouldSkipDirectory(string directoryName)
        {
            if (string.IsNullOrEmpty(directoryName))
                return true;

            for (int i = 0; i < SkipDirectoryNames.Length; i++)
            {
                if (string.Equals(directoryName, SkipDirectoryNames[i], PathComparison))
                    return true;
            }

            return false;
        }

        private static string SafeGetParentDirectory(string path)
        {
            try
            {
                return Path.GetDirectoryName(path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
