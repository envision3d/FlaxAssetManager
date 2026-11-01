using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FlaxEngine;

namespace FlaxAssetManager.Core.Utils
{
    /// <summary>
    /// Hands a file over to the operating system so it opens in the user's own associated
    /// application (the default text editor for the plugin's JSON files).
    /// <see cref="Platform.OpenUrl"/> is browser-only, so this shells out per-OS: the Windows
    /// shell, a desktop opener on Linux, and <c>open</c> on macOS. The launched process is
    /// detached - the editor never waits for it, so a modal GUI editor cannot block the window.
    /// <para>
    /// Handing a path to the OS is not as neutral as it looks: shell execute on Windows RUNS
    /// executables and shell files, and the path a desktop opener receives comes from settings on
    /// disk. The plugin's own outputs (Sources.json) all live inside the project, so both are
    /// required here - the file must be inside the project folder, and its extension must not be
    /// one the shell would execute. A source list edited by hand to point somewhere else gets a
    /// clear refusal instead of invoking the user's handlers on an arbitrary path.
    /// </para>
    /// </summary>
    internal static class SystemFileOpener
    {
        // Linux desktop openers, best first. Terminal editors are deliberately absent: a
        // TUI started from a detached process has no terminal to attach to.
        private static readonly string[] LinuxOpenerCandidates =
        {
            "xdg-open",
            "gio",
            "kde-open5",
            "kfmclient",
            "kate",
            "kwrite",
            "gedit",
        };

        // Extensions the Windows shell would run rather than open. Shell execute has no sandbox, so
        // anything that reaches it must not be one of these; the plugin only ever hands over text/JSON
        // files it wrote itself. The list is checked on every platform (a desktop opener treats them as
        // plain text, and the refusal keeps the rule uniform), and it is deliberately a deny-list of
        // executable handler types, not a general file filter.
        private static readonly HashSet<string> ShellExecutableExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".bat", ".cmd", ".com", ".msi", ".msu", ".cpl", ".msc", ".inf",
            ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ps1", ".psm1", ".hta", ".shb",
            ".lnk", ".scf", ".url", ".appref-ms", ".jar", ".reg", ".search-ms", ".settingcontent-ms",
        };

        /// <summary>
        /// Opens the given file in the OS-associated editor. The file must exist - callers pass
        /// paths they have just created - and must live inside the project folder, and its extension
        /// must not be one the shell would execute. Returns false and fills <paramref name="error"/>
        /// with a human-readable reason (including the path) when nothing could be launched.
        /// </summary>
        public static bool TryOpenInEditor(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "No file path was given.";
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception e)
            {
                error = $"Invalid file path '{path}': {e.Message}";
                return false;
            }

            if (!File.Exists(fullPath))
            {
                error = $"File not found:\n{fullPath}";
                return false;
            }

            // Containment: this is the OS-integration counterpart of the installer's zip-slip and
            // install-record rules, which keep writes inside the project. The read side must not be
            // the loose one, because the path is data on disk the user (or a bad settings sync) controls.
            var projectFolder = Globals.ProjectFolder;
            if (string.IsNullOrEmpty(projectFolder) || !GitSafety.IsUnderRoot(fullPath, projectFolder))
            {
                error = $"Refusing to open a file outside the project folder:\n{fullPath}\n\nMove the file under the project's Content folder, or open it yourself.";
                FlaxEngine.Debug.LogWarning($"FlaxAssetManager: not opening '{fullPath}': outside the project folder '{projectFolder}'.");
                return false;
            }

            var extension = Path.GetExtension(fullPath);
            if (!string.IsNullOrEmpty(extension) && ShellExecutableExts.Contains(extension))
            {
                error = $"Refusing to open a file Windows would run rather than edit:\n{fullPath}\n\nThis plugin only opens its own text files (Sources.json).";
                FlaxEngine.Debug.LogWarning($"FlaxAssetManager: not opening '{fullPath}': '{extension}' is a shell-executable extension.");
                return false;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Shell execute lets Windows pick the registered handler for the extension.
                return TryStart(new ProcessStartInfo
                {
                    FileName = fullPath,
                    UseShellExecute = true,
                }, "the system shell", fullPath, out error);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // -t asks Launch Services for the default *text* editor, not a QuickLook viewer.
                return TryStart(new ProcessStartInfo
                {
                    FileName = "open",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "-t", fullPath },
                }, "open", fullPath, out error);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var failures = new List<string>();
                foreach (var opener in LinuxOpenerCandidates)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = opener,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    // gio needs an "open" verb and kfmclient wants "openURL"; the rest take a bare path.
                    if (opener == "gio")
                    {
                        psi.ArgumentList.Add("open");
                        psi.ArgumentList.Add(fullPath);
                    }
                    else if (opener == "kfmclient")
                    {
                        psi.ArgumentList.Add("openURL");
                        psi.ArgumentList.Add(fullPath);
                    }
                    else
                    {
                        psi.ArgumentList.Add(fullPath);
                    }

                    if (TryStart(psi, opener, fullPath, out _))
                    {
                        error = null;
                        return true;
                    }
                    failures.Add(opener);
                }
                error = "No desktop opener is available on this system (tried: " +
                        string.Join(", ", failures) + $").\nOpen it manually:\n{fullPath}";
                return false;
            }

            error = $"Opening files externally is not supported on this platform.\n{fullPath}";
            return false;
        }

        /// <summary>
        /// Launches the process and detaches from it (disposing the <see cref="Process"/> handle
        /// does not kill it). Failures - a missing binary is the common one - are reported through
        /// <paramref name="error"/> without throwing, so the caller can try the next candidate.
        /// </summary>
        private static bool TryStart(ProcessStartInfo psi, string what, string path, out string error)
        {
            error = null;
            try
            {
                using (Process.Start(psi))
                {
                }
                return true;
            }
            catch (Exception e)
            {
                error = $"Could not launch {what} for\n{path}\n{e.Message}";
                // Qualified: System.Diagnostics.Debug (from the using above) collides with FlaxEngine.Debug.
                FlaxEngine.Debug.LogWarning($"FlaxAssetManager: {what} failed to open '{path}': {e.Message}");
                return false;
            }
        }
    }
}
