using System;
using System.Threading.Tasks;
using FlaxEngine;

namespace FlaxAssetManager.Core.Utils
{
    /// <summary>
    /// The plugin's single marshalling point onto the editor's main thread.
    /// <para>
    /// The engine installs <b>no</b> <see cref="System.Threading.SynchronizationContext"/>, so an <c>await</c>
    /// inside an editor plugin resumes on a thread-pool thread. Everything this plugin touches
    /// after an <c>await</c> — editor GUI controls, the status bar, the content database and
    /// import pipeline, scene-graph objects, the Visject material surface — is main-thread only,
    /// and <see cref="System.IProgress{T}"/>/Progress&lt;T&gt; cannot help because it, too, falls
    /// back to the thread pool without a context.
    /// </para>
    /// <para>
    /// Convention: do the I/O on the pool, then <c>await MainThreadDispatcher.RunOnMain(() =&gt;
    /// …)</c> once and do all engine/GUI work inside that lambda. See docs/DECISIONS.md (D3) and
    /// CONTRIBUTING.md.
    /// </para>
    /// </summary>
    public static class MainThreadDispatcher
    {
        /// <summary>
        /// Runs <paramref name="action"/> on the editor's main thread (at the next scripting
        /// update) and awaits its completion, so the continuation after the <c>await</c> is
        /// ordered after the work.
        /// </summary>
        /// <param name="action">The main-thread work. Must not throw past the editor update.</param>
        public static Task RunOnMain(Action action)
        {
            if (action == null)
                return Task.CompletedTask;

            // Scripting.RunOnUpdate queues onto the engine's scripting update — the hop the material
            // pipeline introduced, and the one every editor-module flow in this plugin now shares.
            return Scripting.RunOnUpdate(action);
        }
    }
}
