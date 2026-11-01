using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ProcessAlias = System.Diagnostics.Process;
using ProcessStartInfoAlias = System.Diagnostics.ProcessStartInfo;

namespace FlaxAssetManager.Core.Utils
{
    /// <summary>
    /// Shared <c>git</c> invocation for the editor module: every argument goes through
    /// <c>ProcessStartInfo.ArgumentList</c>, never a shell string, and the exit code plus
    /// both streams come back to the caller as data.
    /// <para>
    /// This is the rule <c>AGENTS.md</c> states for the installer's own clone/checkout path, kept in one
    /// place so a new git subcommand cannot reach an option parser: a value starting with <c>-</c> is
    /// passed as a positional argument, never parsed as an option (<c>--upload-pack=&lt;cmd&gt;</c> was
    /// the RCE risk). Callers pass <c>-C &lt;path&gt;</c> themselves to choose the repository.
    /// </para>
    /// <para>
    /// Engine-free (no <c>Debug</c>, no GUI) so it can be exercised outside the editor, like
    /// <see cref="GitSafety"/>. It does not log: a non-zero exit is a normal answer for probes such as
    /// <c>symbolic-ref -q HEAD</c>, so the caller decides what a given exit code means.
    /// </para>
    /// </summary>
    public static class GitRunner
    {
        /// <summary>
        /// One git run's outcome. <see cref="SpawnFailed"/> distinguishes "git is not runnable" (no git on
        /// PATH, blocked by policy) from "git ran and said no", which the probes rely on.
        /// </summary>
        public sealed class Output
        {
            /// <summary>git's exit code, or -1 when the process never ran.</summary>
            public int ExitCode = -1;

            /// <summary>Captured standard output (empty when the process never ran).</summary>
            public string StdOut = string.Empty;

            /// <summary>Captured standard error (empty when the process never ran).</summary>
            public string StdErr = string.Empty;

            /// <summary>True when the process could not be started at all.</summary>
            public bool SpawnFailed;

            /// <summary>Why the process could not be started, when <see cref="SpawnFailed"/> is true.</summary>
            public string SpawnError;

            /// <summary>True when git ran and exited 0.</summary>
            public bool Ok => !SpawnFailed && ExitCode == 0;

            /// <summary>Trimmed combined output, for a message that shows the user what git said.</summary>
            public string Text
            {
                get
                {
                    var outPart = StdOut == null ? string.Empty : StdOut.Trim();
                    var errPart = StdErr == null ? string.Empty : StdErr.Trim();
                    if (outPart.Length == 0)
                        return errPart;
                    if (errPart.Length == 0)
                        return outPart;
                    return outPart + "\n" + errPart;
                }
            }
        }

        /// <summary>
        /// Runs <c>git</c> with <paramref name="gitArgs"/> (argument per argument) and returns the
        /// captured result. stdout and stderr are read concurrently so a chatty command cannot fill a
        /// pipe buffer and deadlock before it exits.
        /// </summary>
        /// <param name="gitArgs">Arguments for git; the first is the subcommand (or <c>-C</c>).</param>
        /// <param name="timeoutMs">Kill the wait after this many milliseconds (0 = no timeout).</param>
        /// <param name="ct">Cancellation; rethrown, so an in-flight operation aborts on window close.</param>
        public static async Task<Output> RunAsync(IReadOnlyList<string> gitArgs, int timeoutMs, CancellationToken ct)
        {
            var result = new Output();
            if (gitArgs == null)
            {
                result.SpawnFailed = true;
                result.SpawnError = "No git arguments were given.";
                return result;
            }

            var psi = new ProcessStartInfoAlias
            {
                FileName = "git",
                // No 'Arguments' string: every entry is added to ArgumentList below, so a path from
                // disk is never re-parsed for quoting or options.
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            for (int i = 0; i < gitArgs.Count; i++)
                psi.ArgumentList.Add(gitArgs[i]);

            using (var cts = timeoutMs > 0 ? CancellationTokenSource.CreateLinkedTokenSource(ct) : new CancellationTokenSource())
            {
                if (timeoutMs > 0)
                    cts.CancelAfter(timeoutMs);

                ProcessAlias proc = null;
                try
                {
                    proc = ProcessAlias.Start(psi);
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    var stderrTask = proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync(cts.Token);
                    await Task.WhenAll(stdoutTask, stderrTask);
                    result.ExitCode = proc.ExitCode;
                    result.StdOut = stdoutTask.Result ?? string.Empty;
                    result.StdErr = stderrTask.Result ?? string.Empty;
                }
                catch (OperationCanceledException)
                {
                    // A window close, or the timeout on a network-bound command (git has no built-in pull
                    // timeout). Kill the abandoned git: a hung pull that outlives this call would land
                    // files in the plugin folder after the caller has moved on - and a git waiting on a
                    // credential prompt hangs forever, never exiting on its own.
                    KillTree(proc);
                    if (ct.IsCancellationRequested)
                        throw; // the caller's own token wins, so a close is not reported as a git failure
                    result.SpawnFailed = true;
                    result.SpawnError = $"{Describe(gitArgs)} timed out after {timeoutMs / 1000}s";
                    return result;
                }
                catch (Exception e)
                {
                    KillTree(proc);
                    result.SpawnFailed = true;
                    result.SpawnError = e.Message;
                    result.ExitCode = -1;
                }
                finally
                {
                    if (proc != null)
                        proc.Dispose();
                }
            }

            return result;
        }

        /// <summary>
        /// Names a git call for a message: <c>-C &lt;path&gt; pull</c> reads as <c>git pull</c>, not
        /// <c>git -C</c>, so a timeout report names the operation the user is waiting for.
        /// </summary>
        private static string Describe(IReadOnlyList<string> gitArgs)
        {
            for (int i = 0; i < gitArgs.Count; i++)
            {
                var arg = gitArgs[i];
                if (arg == "-C")
                {
                    i++; // skip the path that follows -C
                    continue;
                }
                if (arg.StartsWith("-", StringComparison.Ordinal))
                    continue;
                return "git " + arg;
            }
            return "git";
        }

        /// <summary>
        /// Kills a git run the caller is abandoning (cancelled, timed out, or failed mid-wait), together
        /// with its process tree. A <c>git pull</c> waiting on a credential prompt never exits by itself,
        /// and a git that outlives its call lands files in the folder after the caller has already
        /// reported the operation as over. Never throws: the caller is already handling a failure.
        /// </summary>
        private static void KillTree(ProcessAlias proc)
        {
            if (proc == null)
                return;
            try
            {
                if (!proc.HasExited)
                    proc.Kill(true);
            }
            catch
            {
                // Already exited, already reaped, or not ours to kill.
            }
        }
    }
}
