using System;
using System.Linq;
using System.Threading.Tasks;
using FlaxAssetManager.Core.Tests;
using FlaxEditor;
using FlaxEditor.GUI.ContextMenu;
using FlaxAssetManager.Core.Services;
using FlaxAssetManager.UI;

namespace FlaxAssetManagerEditor
{
    /// <summary>
    /// Editor-side plugin. Registers the Flax Asset Manager window.
    /// </summary>
    public class FlaxAssetManagerEditor : EditorPlugin
    {
        public override Type GamePluginType => typeof(global::FlaxAssetManager.FlaxAssetManager);

        private FlaxAssetManagerWindow _window;
        private ContextMenuButton _menuButton;

        public static FlaxAssetManagerEditor Instance { get; private set; }

        public SettingsService Settings { get; private set; }
        public AssetSourceManager SourceManager { get; private set; }
        public AssetInstallerService Installer { get; private set; }

        public override void InitializeEditor()
        {
            base.InitializeEditor();
            Instance = this;

            Settings = new SettingsService();
            SourceManager = new AssetSourceManager(Settings);
            Installer = new AssetInstallerService(Settings, SourceManager);
            // Defer window creation until editor is fully initialized.
            if (Editor.IsInitialized)
                CreateWindow();
            else
                Editor.InitializationEnd += OnEditorInitEnd;

            StartE2EIfRequested();
        }

        private void StartE2EIfRequested()
        {
            // The E2E harness (Core/Tests/VariantE2ETest.cs) ships inside the editor module and runs
            // ONLY with FLAX_AM_E2E=1. It cannot be gated at compile time on stock 1.13: the builder
            // offers no C# define path for plugin modules (module ScriptingAPI.Defines does not reach
            // the plugin's assembly; Target.GlobalDefinitions is native-only) - verified by probe.
            // See docs/DECISIONS.md D4.
            if (System.Environment.GetEnvironmentVariable("FLAX_AM_E2E") == "1")
                Task.Run(() => VariantE2ETest.RunAll(this));
        }

        private void OnEditorInitEnd()
        {
            Editor.InitializationEnd -= OnEditorInitEnd;
            CreateWindow();
            StartE2EIfRequested();
        }

        private void CreateWindow()
        {
            _window = new FlaxAssetManagerWindow(Editor, Settings, SourceManager, Installer);
            // Hide initially; Show via Window menu uses FocusOrShow which checks Visible.
            // DockWindow starts Visible=true but IsHidden due to _dockedTo==null, so force Hide.
            _window.Hide();

            // Add to Window menu for reopening - ensure no duplicates.
            try
            {
                var menu = Editor.UI.MenuWindow.ContextMenu;
                // Remove any stale entry with same text (handles hot-reload duplicates).
                var existing = menu.Items.OfType<ContextMenuButton>().FirstOrDefault(b => b.Text == "Flax Asset Manager");
                if (existing != null)
                {
                    existing.Dispose();
                }
                if (_menuButton != null)
                {
                    _menuButton.Dispose();
                    _menuButton = null;
                }
                _menuButton = menu.AddButton("Flax Asset Manager", () =>
                {
                    if (_window == null) return;
                    // Use IsHidden (Visible && docked check) explicitly; FocusOrShow checks Visible only.
                    if (_window.IsHidden)
                        _window.Show();
                    else
                        _window.FocusOrShow();
                });
            }
            catch (Exception e)
            {
                FlaxEngine.Debug.LogWarning($"FlaxAssetManager: Failed to add Window menu entry: {e.Message}");
            }
        }

        public override void DeinitializeEditor()
        {
            // Remove menu entry.
            if (_menuButton != null)
            {
                try { _menuButton.Dispose(); } catch { }
                _menuButton = null;
            }
            // Fallback cleanup for any duplicated entries.
            try
            {
                var menu = Editor?.UI?.MenuWindow?.ContextMenu;
                if (menu != null)
                {
                    var dupes = menu.Items.OfType<ContextMenuButton>().Where(b => b.Text == "Flax Asset Manager").ToArray();
                    foreach (var b in dupes)
                    {
                        try { b.Dispose(); } catch { }
                    }
                }
            }
            catch { }

            Editor.InitializationEnd -= OnEditorInitEnd;

            if (_window != null)
            {
                try
                {
                    // HideOnClose=true so Close() just hides; Dispose fully removes from MasterPanel.
                    _window.Close();
                    _window.Dispose();
                }
                catch { }
                _window = null;
            }

            Installer?.Dispose();
            Installer = null;
            SourceManager?.Dispose();
            SourceManager = null;
            Instance = null;
            base.DeinitializeEditor();
        }

        /// <summary>
        /// Show the manager window on demand (e.g. from menu).
        /// </summary>
        public void ShowWindow()
        {
            _window?.FocusOrShow();
        }
    }
}
