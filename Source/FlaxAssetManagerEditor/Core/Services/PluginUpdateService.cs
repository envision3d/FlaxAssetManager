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
    /// The plugin ships as a git checkout under <c>Plugins/FlaxAssetManager/</c> (a <c>pluginProject</c>
    /// install is a plain clone, and a contributor's checkout is one too), so updating it is a git
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

        /// <summary>Preflight probes are local; a hung one means the checkout is broken, not the network.</summary>
        private const int PreflightTimeoutMs = 30 * 1000;

        /// <summary>A pull touches the network, and git has no built-in timeout of its own.</summary>
        private const int PullTimeoutMs = 300 * 1000;

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
        /// Finds the folder of the running plugin: the directory that holds
        /// <see cref="PluginProjectFileName"/>, found by walking up from this assembly (the editor module
        /// loads from <c>Binaries/&lt;EditorTarget&gt;/&lt;platform&gt;/&lt;arch&gt;/&lt;config&gt;/</c>,
        /// whose depth is the builder's business, not ours), falling back to the documented install
        /// location <c>&lt;project&gt;/Plugins/FlaxAssetManager</c>.
        /// <para>
        /// The result is required to sit inside <see cref="Globals.ProjectFolder"/>: this is the folder a
        /// <c>git pull</c> will write into, so the same containment rule that guards every other write
        /// applies (D14). A checkout that is the open project itself is reported as such, because
        /// updating it from inside the editor is the same operation on the project the user is working in.
        /// </para>
        /// </summary>
        public static bool TryResolvePluginFolder(out string folder, out string error)
        {
            folder = null;
            error = null;

            var projectFolder = Path.GetFullPath(Globals.ProjectFolder);
            var candidates = new List<string>();

            var dir = Path.GetDirectoryName(typeof(PluginUpdateService).Assembly.Location);
            for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(dir); depth++)
            {
                if (File.Exists(Path.Combine(dir, PluginProjectFileName)))
                {
                    candidates.Add(dir);
                    break;
                }
                dir = Path.GetDirectoryName(dir);
            }

            candidates.Add(Path.Combine(projectFolder, "Plugins", "FlaxAssetManager"));

            foreach (var candidate in candidates)
            {
                string full;
                try
                {
                    full = Path.GetFullPath(candidate);
                }
                catch (Exception e)
                {
                    error = $"Could not resolve the plugin folder path '{candidate}': {e.Message}";
                    continue;
                }

                if (!File.Exists(Path.Combine(full, PluginProjectFileName)))
                    continue;

                if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), projectFolder.TrimEnd(Path.DirectorySeparatorChar),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    error = "This plugin project is the open project, not a plugin inside it, so it cannot update itself from here. " +
                            "Update it with git in your own checkout, or open a host project that has it under Plugins/.";
                    continue;
                }

                if (!GitSafety.IsUnderRoot(full, projectFolder))
                {
                    error = $"The plugin folder '{full}' is outside the open project '{projectFolder}', so it will not be written to.";
                    continue;
                }

                folder = full;
                return true;
            }

            if (error == null)
                error = $"Could not find this plugin's folder ('{PluginProjectFileName}' not found next to the running assembly, " +
                        $"and not at '{Path.Combine("Plugins", "FlaxAssetManager")}' in the open project).";
            return false;
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
