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
        private ContextMenuSeparator _menuSeparator;

        // The Tools menu's own view cap, read before this plugin raised it (0 = untouched).
        private int _menuMaxItemsInView;

        /// <summary> The label used for the Tools menu entry this plugin adds. </summary>
        private const string MenuLabel = "Flax Asset Manager";

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

            // The material-readiness check (Core/Tests/MaterialReadyE2ETest.cs) is gated separately so it can
            // be run on its own, without the variant harness reinstalling packs over the network.
            if (System.Environment.GetEnvironmentVariable("FLAX_AM_MATERIAL_E2E") == "1")
                Task.Run(() => MaterialReadyE2ETest.RunAll(this));
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
            // Hide initially; Show via the Tools menu uses FocusOrShow which checks Visible.
            // DockWindow starts Visible=true but IsHidden due to _dockedTo==null, so force Hide.
            _window.Hide();

            // Add to the Tools menu for reopening - ensure no duplicates.
            try
            {
                var menu = Editor.UI.MenuTools.ContextMenu;
                // Drop the entries this plugin added before (a script hot-reload loses the
                // references - the button is found by text, the separator by adjacency).
                DisposeMenuEntries(menu);
                // Separator keeps the plugin entry split from the engine's Tools items.
                menu.AddSeparator();
                _menuSeparator = menu.Items.OfType<ContextMenuSeparator>().LastOrDefault();
                _menuButton = menu.AddButton(MenuLabel, () =>
                {
                    if (_window == null) return;
                    // Use IsHidden (Visible && docked check) explicitly; FocusOrShow checks Visible only.
                    if (_window.IsHidden)
                        _window.Show();
                    else
                        _window.FocusOrShow();
                });
                // A hot-reload leaves its own separator behind; collapse adjacent separators so
                // the Tools menu never shows a double rule.
                RemoveRedundantSeparators(menu);
                // A context menu shows at most MaximumItemsInViewCount items (the default, 20) and
                // scrolls the rest. The engine builds the Tools menu at exactly that cap, so the
                // separator + button added above would open it with a scroll bar. Raise the cap to
                // the menu's own item count: idempotent, since a hot-reload re-adds the same two
                // items and the cap is already that count.
                if (_menuMaxItemsInView == 0)
                    _menuMaxItemsInView = menu.MaximumItemsInViewCount;
                var itemCount = menu.Items.Count();
                if (itemCount > menu.MaximumItemsInViewCount)
                    menu.MaximumItemsInViewCount = itemCount;
            }
            catch (Exception e)
            {
                FlaxEngine.Debug.LogWarning($"FlaxAssetManager: Failed to add Tools menu entry: {e.Message}");
            }
        }

        /// <summary>
        /// Removes the menu entries this plugin added: its button (by text - a script hot-reload
        /// drops the reference, so text is the only handle) and the separator it owns.
        /// </summary>
        private void DisposeMenuEntries(ContextMenu menu)
        {
            foreach (var b in menu.Items.OfType<ContextMenuButton>().Where(b => b.Text == MenuLabel).ToArray())
            {
                try { b.Dispose(); } catch { }
            }
            if (_menuSeparator != null)
            {
                try { _menuSeparator.Dispose(); } catch { }
                _menuSeparator = null;
            }
            if (_menuButton != null)
            {
                try { _menuButton.Dispose(); } catch { }
                _menuButton = null;
            }
        }

        /// <summary>
        /// Disposes any separator sitting directly on top of another one, which is what a
        /// hot-reload leaves behind. Converges: one reload adds one, this removes one.
        /// Runs backwards so disposing an item never shifts an index that is still to visit.
        /// </summary>
        private static void RemoveRedundantSeparators(ContextMenu menu)
        {
            var items = menu.Items.ToList();
            for (int i = items.Count - 1; i > 0; i--)
            {
                if (items[i] is ContextMenuSeparator && items[i - 1] is ContextMenuSeparator)
                {
                    try { items[i].Dispose(); } catch { }
                }
            }
        }

        public override void DeinitializeEditor()
        {
            // DisposeMenuEntries finds the button by text, so it cleans up even when the
            // reference was lost to a script hot-reload.
            try
            {
                var menu = Editor?.UI?.MenuTools?.ContextMenu;
                if (menu != null)
                {
                    DisposeMenuEntries(menu);
                    RemoveRedundantSeparators(menu);
                    // Hand the menu back the view cap it had before this plugin widened it.
                    if (_menuMaxItemsInView > 0)
                    {
                        menu.MaximumItemsInViewCount = _menuMaxItemsInView;
                        _menuMaxItemsInView = 0;
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
