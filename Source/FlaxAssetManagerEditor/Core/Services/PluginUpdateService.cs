using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FlaxEngine;
using FlaxEditor;
using FlaxAssetManager.Core.Utils;

namespace FlaxAssetManager.Core.Services
{
    /// <summary>
    /// Updates <b>this plugin itself</b>: <c>git pull</c> in the plugin's own folder, then a scripts
    /// recompile request so the editor loads the new code.
    /// <para>
    /// The plugin ships as a git checkout under <c>Plugins/</c> (a <c>pluginProject</c> install is a plain
    /// clone, and a contributor's checkout is one too), so updating it is a git operation on that folder,
    /// not a download. Which folder that is gets <b>discovered, never guessed from a folder name</b> — see
    /// <see cref="TryResolvePluginFolder"/>. The plugin's assemblies are not in git - Flax generates
    /// operation on that folder, not a download. The plugin's assemblies are not in git — Flax generates
    /// <c>Binaries/</c> — so a changed source tree only takes effect once the editor rebuilds and reloads
    /// the scripts, which is why the pull is followed by <see cref="ScriptsBuilder.Compile"/>.
    /// </para>
    /// <para>
    /// Everything here runs off the main thread (git I/O) except the recompile request, which is an
    /// editor API and is marshalled through <see cref="MainThreadDispatcher"/>, per the plugin's
    /// threading rule (D3).
    /// </para>
    /// </summary>
    public sealed class PluginUpdateService
    {
        /// <summary>Marker file of this plugin project, used to locate its folder.</summary>
        public const string PluginProjectFileName = "FlaxAssetManager.flaxproj";

        /// <summary>
        /// What this plugin's project calls itself (the <c>Name</c> field of its <c>.flaxproj</c>). A folder
        /// name is not identity: it is whatever a git URL, an archive, a fork, or the user chose, so folder
        /// discovery matches a project file by <b>name and content</b> and never assumes a folder.
        /// </summary>
        public const string PluginProjectName = "FlaxAssetManager";

        /// <summary>How far up to walk from the running assembly to reach the project folder that built it.</summary>
        private const int AssemblyWalkDepth = 10;

        /// <summary>Budget for the last-resort <c>Plugins/</c> scan: depth, and directories read.</summary>
        private const int PluginsScanDepth = 4;
        private const int PluginsScanMaxDirectories = 2000;

        /// <summary>Preflight probes are local; a hung one means the checkout is broken, not the network.</summary>
        private const int PreflightTimeoutMs = 30 * 1000;

        /// <summary>A pull touches the network, and git has no built-in timeout of its own.</summary>
        private const int PullTimeoutMs = 300 * 1000;

        /// <summary>
        /// This plugin's settings service, for the install-record route of folder discovery: an installed
        /// plugin project's folder is recorded there under a name that came from the git URL, which is the
        /// common case of a folder name this plugin cannot guess. Null is allowed; that route is skipped.
        /// </summary>
        private readonly SettingsService _settings;

        /// <summary>Folder discovery without install records.</summary>
        public PluginUpdateService()
            : this(null)
        {
        }

        /// <summary>Folder discovery with this plugin's install records included.</summary>
        public PluginUpdateService(SettingsService settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// The outcome of an update. <see cref="Okay"/> says the flow ran to a conclusion the user can
        /// act on (a refusal is Okay=false with a <see cref="Message"/> explaining what to fix);
        /// <see cref="Updated"/> says the pull moved HEAD; <see cref="Recompiled"/> says a scripts
        /// compile was requested.
        /// </summary>
        public sealed class Result
        {
            /// <summary>True when the update ran and did something (a refusal or a failure is false).</summary>
            public bool Okay;

            /// <summary>True when HEAD moved, i.e. new commits arrived.</summary>
            public bool Updated;

            /// <summary>True when a scripts recompile was requested (only after files actually changed).</summary>
            public bool Recompiled;

            /// <summary>One-line result for the status bar.</summary>
            public string Message = string.Empty;

            /// <summary>git's own words, for the log (truncated by the caller when echoed to the UI).</summary>
            public string Detail = string.Empty;
        }

        /// <summary>
        /// Finds the folder of the running plugin <b>by content, never by an assumed folder name</b>, trying
        /// these routes in order (the first one that passes the checks below wins):
        /// <list type="number">
        /// <item>the project folder that built the running assembly — the nearest ancestor of
        /// <c>Assembly.Location</c> holding this plugin's <c>.flaxproj</c> (the
        /// <c>Binaries</c> / target / platform / arch / config depth is the builder's
        /// business, not ours);</item>
        /// <item>the plugin the <b>open project references</b> — the <c>.flaxproj</c> a <c>References</c>
        /// entry of the host project file points at. That entry was written when the plugin was added, so
        /// it is the tree's own record of where the plugin lives, and it is the route that survives a
        /// folder the user (or a git URL, or an archive) named differently from this plugin;</item>
        /// <item>the folder this plugin's own <c>.installed.json</c> records for a <c>pluginProject</c>
        /// install — that folder name comes from the git URL, never from this plugin's name;</item>
        /// <item>a bounded, shallowest-first scan of <c>Plugins/</c> for a project file that is this
        /// plugin's project;</item>
        /// <item>the documented install location <c>&lt;project&gt;/Plugins/FlaxAssetManager</c>;</item>
        /// <item>as a last resort, the project folder this assembly was built into, when that is not the
        /// open project — the only route that finds a fork that renamed the project file too.</item>
        /// </list>
        /// Every candidate has to <i>be</i> this plugin's project folder: a file named
        /// <see cref="PluginProjectFileName"/>, or a <c>.flaxproj</c> whose <c>Name</c> field is
        /// <see cref="PluginProjectName"/>. A folder that merely looks like it is never written to.
        /// <para>
        /// The result is required to sit inside <see cref="Globals.ProjectFolder"/>: this is the folder a
        /// <c>git pull</c> will write into, so the same containment rule that guards every other write
        /// applies (D14). A candidate that is the open project's own folder is the host project — the
        /// project target builds into it, so its project file says nothing about this plugin, and it is
        /// skipped — unless it is this plugin's project, in which case the plugin <i>is</i> the open project
        /// (a contributor's checkout) and that is reported instead.
        /// </para>
        /// </summary>
        public bool TryResolvePluginFolder(out string folder, out string error)
        {
            folder = null;
            error = null;

            var projectFolder = Path.GetFullPath(Globals.ProjectFolder);
            var candidates = EnumerateCandidateFolders(projectFolder, _settings);

            if (candidates.Count == 0)
            {
                error = $"Could not find this plugin's folder: no Flax project named '{PluginProjectName}' " +
                        $"(a '{PluginProjectFileName}', or a .flaxproj whose \"Name\" is '{PluginProjectName}') was found " +
                        $"next to the running assembly, in the references of the open project, in an install record, " +
                        $"or under '{DisplayFolder(Path.Combine(projectFolder, "Plugins"))}'.";
                return false;
            }

            string rejection = null;
            foreach (var candidate in candidates)
            {
                string full;
                try
                {
                    full = Path.GetFullPath(candidate.Folder);
                }
                catch (Exception e)
                {
                    rejection = $"Could not resolve the plugin folder path '{candidate.Folder}': {e.Message}";
                    continue;
                }

                if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), projectFolder.TrimEnd(Path.DirectorySeparatorChar),
                        FlaxProjectLocator.PathComparison))
                {
                    if (candidate.IsThisPluginsProject)
                    {
                        rejection = "This plugin project is the open project, not a plugin inside it, so it cannot update itself from here. " +
                                    "Update it with git in your own checkout, or open a host project that has it under Plugins/.";
                    }

                    continue;
                }

                if (!GitSafety.IsUnderRoot(full, projectFolder))
                {
                    rejection = $"The plugin folder '{full}' is outside the open project '{projectFolder}', so it will not be written to.";
                    continue;
                }

                folder = full;
                Debug.Log($"FlaxAssetManager: self-update plugin folder resolved to '{DisplayFolder(full)}' via {candidate.How}.");
                return true;
            }

            if (rejection != null)
            {
                error = rejection;
                return false;
            }

            var found = new List<string>(candidates.Count);
            foreach (var candidate in candidates)
                found.Add(DisplayFolder(candidate.Folder));
            error = $"Found this plugin's project at {string.Join(", ", found)}, but none of those folders is a plugin inside the open project '{DisplayFolder(projectFolder)}'.";
            return false;
        }

        /// <summary>
        /// One folder a discovery route pointed at, with the route's name (for the log and the failure
        /// message) and whether the folder was identified as this plugin's project by its project file
        /// (<c>true</c>) or only as the project that built the running assembly (<c>false</c>).
        /// </summary>
        private sealed class Candidate
        {
            public string Folder;
            public bool IsThisPluginsProject;
            public string How;
        }

        /// <summary>
        /// Runs the discovery routes in priority order and de-duplicates them, so a plugin found by its
        /// reference and by a scan is one candidate, listed once, in the order the routes ran.
        /// </summary>
        private static List<Candidate> EnumerateCandidateFolders(string projectFolder, SettingsService settings)
        {
            var candidates = new List<Candidate>();
            var seen = new HashSet<string>(FlaxProjectLocator.PathComparer);

            // 1. The project folder that built the running assembly. Walking up is the only way that
            // needs no knowledge of the builder's Binaries layout. The nearest ancestor holding *any*
            // project file is remembered separately: for an installed plugin that is the host project
            // (whose project file is not this plugin's), and for a fork it is this plugin's folder under
            // a name nothing else knows.
            string builtIntoProject = null;
            foreach (var dir in FlaxProjectLocator.EnumerateAncestorDirectories(SafeAssemblyDirectory(), AssemblyWalkDepth))
            {
                if (FlaxProjectLocator.FindProjectFileInDirectory(dir, PluginProjectName) != null)
                {
                    Add(candidates, seen, dir, true, "the project folder that built the running assembly");
                    break;
                }

                if (builtIntoProject == null && FlaxProjectLocator.FindAnyProjectFileInDirectory(dir) != null)
                    builtIntoProject = dir;
            }

            // 2. The open project's references: the tree's own record of where its plugins are.
            var rootProjectFile = FlaxProjectLocator.FindRootProjectFile(projectFolder);
            if (rootProjectFile != null)
            {
                foreach (var reference in FlaxProjectLocator.ReadReferenceProjectPaths(rootProjectFile, EngineFolderForMacros()))
                {
                    if (!FlaxProjectLocator.IsProjectNamed(reference, PluginProjectName))
                        continue;

                    Add(candidates, seen, Path.GetDirectoryName(reference), true,
                        $"the reference '{DisplayFolder(reference)}' of '{Path.GetFileName(rootProjectFile)}'");
                }
            }

            // 3. This plugin's install records: a pluginProject clone is installed into a folder named
            // from the git URL, which is not this plugin's name.
            var records = settings?.Installed?.Installed;
            if (records != null)
            {
                foreach (var entry in records)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.InstallPath))
                        continue;

                    var installedFolder = Path.Combine(projectFolder, entry.InstallPath);
                    if (FlaxProjectLocator.FindProjectFileInDirectory(installedFolder, PluginProjectName) == null)
                        continue;

                    Add(candidates, seen, installedFolder, true, $"the install record of '{entry.PackId}'");
                }
            }

            // 4+5. The Plugins tree, then the documented location.
            var pluginsFolder = Path.Combine(projectFolder, "Plugins");
            foreach (var projectFile in FlaxProjectLocator.FindProjectFilesRecursive(pluginsFolder, PluginsScanDepth, PluginsScanMaxDirectories))
            {
                if (!FlaxProjectLocator.IsProjectNamed(projectFile, PluginProjectName))
                    continue;

                Add(candidates, seen, Path.GetDirectoryName(projectFile), true,
                    $"the scan of '{DisplayFolder(pluginsFolder)}'");
            }

            AddIfPluginsProject(candidates, seen, Path.Combine(pluginsFolder, PluginProjectName),
                $"the documented location '{DisplayFolder(Path.Combine(pluginsFolder, PluginProjectName))}'");

            // 6. Last resort: the project folder this assembly was built into, when that is not the open
            // project (the containment and open-project checks in the caller filter the host case out).
            Add(candidates, seen, builtIntoProject, false, "the project folder the running assembly was built into");

            return candidates;
        }

        /// <summary>Adds a candidate that has to be this plugin's project folder, i.e. hold its project file.</summary>
        private static void AddIfPluginsProject(List<Candidate> candidates, HashSet<string> seen, string folder, string how)
        {
            if (string.IsNullOrWhiteSpace(folder) ||
                FlaxProjectLocator.FindProjectFileInDirectory(folder, PluginProjectName) == null)
                return;

            Add(candidates, seen, folder, true, how);
        }

        private static void Add(List<Candidate> candidates, HashSet<string> seen, string folder, bool isThisPluginsProject, string how)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return;

            try
            {
                if (!seen.Add(Path.GetFullPath(folder)))
                    return;
            }
            catch (Exception)
            {
                return;
            }

            candidates.Add(new Candidate { Folder = folder, IsThisPluginsProject = isThisPluginsProject, How = how });
        }

        /// <summary>The directory the running assembly was loaded from, or null when the runtime cannot say.</summary>
        private static string SafeAssemblyDirectory()
        {
            try
            {
                return Path.GetDirectoryName(typeof(PluginUpdateService).Assembly.Location);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The folder <c>$(EnginePath)</c> expands to, for reference paths that use it: the engine content
        /// folder sits at <c>&lt;engine&gt;/Content</c>, so its parent is the engine. Null when the engine
        /// cannot be asked — such a reference is then skipped rather than resolved into the wrong place.
        /// </summary>
        private static string EngineFolderForMacros()
        {
            try
            {
                return Path.GetDirectoryName(Globals.EngineContentFolder);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Displays a resolved plugin folder as a project-relative path (<c>Plugins/FlaxAssetManager</c>),
        /// which is what a user recognises in a dialog.
        /// </summary>
        public static string DisplayFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder))
                return "the plugin folder";
            var projectFolder = Path.GetFullPath(Globals.ProjectFolder);
            var full = Path.GetFullPath(folder);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (full.StartsWith(projectFolder + Path.DirectorySeparatorChar, comparison))
                return full.Substring(projectFolder.Length + 1).Replace('\\', '/');
            return full;
        }

        /// <summary>
        /// Runs the whole update: probes the checkout, pulls, and asks the editor to rebuild the scripts
        /// when the pull changed files. Every refusal happens <b>before</b> anything is written, so a
        /// checkout the plugin is not sure about is left exactly as it was.
        /// </summary>
        public async Task<Result> UpdateAsync(CancellationToken ct)
        {
            if (!TryResolvePluginFolder(out var folder, out var resolveError))
                return Refuse(resolveError);

            var display = DisplayFolder(folder);

            if (!IsGitCheckout(folder))
                return Refuse($"'{display}' is not a git checkout, so it cannot update itself. Update it by hand, or install the plugin from a git source.");

            var version = await GitRunner.RunAsync(new[] { "--version" }, PreflightTimeoutMs, ct);
            if (version.SpawnFailed)
                return Refuse($"Could not run 'git' ({version.SpawnError}). Install git, or update '{display}' by hand.");

            // A pull must not have to merge, overwrite, or drop local work. A plugin folder is the user's
            // installed code, not a scratch tree: a dirty checkout, a detached HEAD, or a branch with no
            // upstream is refused, and the user fixes it with git in their own terms.
            var status = await GitRunner.RunAsync(new[] { "-C", folder, "status", "--porcelain" }, PreflightTimeoutMs, ct);
            if (!status.Ok)
                return Refuse($"'git status' in '{display}' failed (exit {status.ExitCode}):\n{status.Text}", status.Text);
            var dirty = SplitLines(status.StdOut);
            if (dirty.Count > 0)
            {
                var shown = dirty.Count <= 5 ? string.Join("\n", dirty) : string.Join("\n", dirty.GetRange(0, 5)) + $"\n... and {dirty.Count - 5} more";
                return Refuse($"'{display}' has {dirty.Count} local change(s), so nothing was pulled:\n{shown}\n\nCommit or revert them first.", status.Text);
            }

            var symbolic = await GitRunner.RunAsync(new[] { "-C", folder, "symbolic-ref", "-q", "HEAD" }, PreflightTimeoutMs, ct);
            if (!symbolic.Ok)
                return Refuse($"'{display}' is not on a branch (detached HEAD), so there is nothing to pull into. Check out the branch you want to track first.", symbolic.Text);

            var upstream = await GitRunner.RunAsync(new[] { "-C", folder, "rev-parse", "--abbrev-ref", "@{u}" }, PreflightTimeoutMs, ct);
            if (!upstream.Ok)
                return Refuse($"The current branch in '{display}' has no upstream branch to pull from. Set one with git, then try again.", upstream.Text);

            var before = await GitRunner.RunAsync(new[] { "-C", folder, "rev-parse", "--short", "HEAD" }, PreflightTimeoutMs, ct);
            var headBefore = SplitFirstLine(before.StdOut);

            // --ff-only: the point of the button is "take what upstream published", not "reconcile my
            // branch with it". A diverged checkout fails loudly here, with git's own words in Detail.
            var pull = await GitRunner.RunAsync(new[] { "-C", folder, "pull", "--ff-only" }, PullTimeoutMs, ct);
            if (pull.SpawnFailed)
                return Refuse($"'git pull' in '{display}' could not be completed: {pull.SpawnError}", pull.Text);
            if (!pull.Ok)
                return Refuse($"'git pull --ff-only' in '{display}' failed (exit {pull.ExitCode}); nothing was recompiled.\n{pull.Text}", pull.Text);

            var after = await GitRunner.RunAsync(new[] { "-C", folder, "rev-parse", "--short", "HEAD" }, PreflightTimeoutMs, ct);
            var headAfter = SplitFirstLine(after.StdOut);
            var pullText = pull.Text;

            if (headBefore.Length > 0 && headBefore == headAfter)
            {
                Debug.Log($"FlaxAssetManager: self-update found no new commits in '{display}' ({headBefore}).");
                return new Result
                {
                    Okay = true,
                    Updated = false,
                    Recompiled = false,
                    Message = $"Flax Asset Manager is already up to date ({headBefore}) - no recompile needed.",
                    Detail = pullText,
                };
            }

            // Only a pull that moved HEAD is worth a rebuild: a scripts compile reloads the plugin's
            // assemblies, so it is not free, and it is pointless when no source changed.
            int changedFiles = 0;
            if (headBefore.Length > 0 && headAfter.Length > 0)
            {
                var diff = await GitRunner.RunAsync(new[] { "-C", folder, "diff", "--name-only", $"{headBefore}..{headAfter}" }, PreflightTimeoutMs, ct);
                if (diff.Ok)
                    changedFiles = SplitLines(diff.StdOut).Count;
            }

            var recompile = await RequestScriptsRecompileAsync();

            var moved = string.IsNullOrEmpty(headBefore) ? headAfter : $"{headBefore} -> {headAfter}";
            var suffix = recompile.Requested
                ? "Recompiling scripts - the editor reloads the plugin when the build finishes."
                : recompile.Reason;
            var message = recompile.Requested
                ? $"Updated Flax Asset Manager {moved} ({changedFiles} file(s)); scripts are recompiling."
                : $"Updated Flax Asset Manager {moved} ({changedFiles} file(s)), but no scripts recompile was started: {suffix}";
            Debug.Log($"FlaxAssetManager: self-update pulled '{display}' {moved}; recompile requested: {recompile.Requested} ({suffix})");

            return new Result
            {
                Okay = true,
                Updated = true,
                Recompiled = recompile.Requested,
                Message = message,
                Detail = pullText,
            };
        }

        /// <summary>
        /// The engine-side half of the update: mark the script workspace dirty and request a compile.
        /// Both are editor APIs, so they run on the main thread (D3). A compile is a request, not a
        /// handover: the editor rebuilds and reloads the scripts assemblies on its own schedule, which
        /// disposes and recreates plugin windows — the caller must not expect this window to outlive it.
        /// </summary>
        private static async Task<(bool Requested, string Reason)> RequestScriptsRecompileAsync()
        {
            bool requested = false;
            string reason = null;

            await MainThreadDispatcher.RunOnMain(() =>
            {
                if (string.IsNullOrEmpty(ScriptsBuilder.BuildToolPath))
                {
                    reason = "the engine reports no Flax.Build tool to compile with, so rebuild the project yourself.";
                    return;
                }
                if (ScriptsBuilder.IsCompiling)
                {
                    reason = "a scripts compile is already running, which will pick up the new sources.";
                    return;
                }

                // MarkWorkspaceDirty keeps the editor's own compile-on-focus logic consistent with the
                // state on disk, so the new sources are not treated as already built.
                ScriptsBuilder.MarkWorkspaceDirty();
                ScriptsBuilder.Compile();
                requested = true;
            });

            return (requested, reason ?? string.Empty);
        }

        /// <summary>True when the folder is a git work tree: <c>.git</c> as a directory, or as a file
        /// (a linked work tree / submodule points at its gitdir).</summary>
        private static bool IsGitCheckout(string folder)
        {
            var gitPath = Path.Combine(folder, ".git");
            return Directory.Exists(gitPath) || File.Exists(gitPath);
        }

        private static Result Refuse(string message, string detail = null)
        {
            Debug.LogWarning($"FlaxAssetManager: self-update refused: {message}");
            return new Result
            {
                Okay = false,
                Updated = false,
                Recompiled = false,
                Message = message,
                Detail = detail ?? string.Empty,
            };
        }

        private static List<string> SplitLines(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text))
                return result;
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r').Trim();
                if (line.Length > 0)
                    result.Add(line);
            }
            return result;
        }

        private static string SplitFirstLine(string text)
        {
            var lines = SplitLines(text);
            return lines.Count > 0 ? lines[0] : string.Empty;
        }
    }
}
