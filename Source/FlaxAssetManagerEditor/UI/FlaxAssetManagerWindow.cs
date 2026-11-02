using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Net.Http;
using System.Linq;
using FlaxEditor;
using FlaxEditor.GUI;
using FlaxEditor.GUI.ContextMenu;
using FlaxEditor.Windows;
using FlaxEngine;
using FlaxEngine.GUI;
using EKEditorUIKit;
using FlaxAssetManager.Core.Models;
using FlaxAssetManager.Core.Services;
using StatusBar = EKEditorUIKit.StatusBar;
using FlaxAssetManager.Core.Utils;

namespace FlaxAssetManager.UI
{
    /// <summary>
    /// Editor window: browse sources (separate per URL) and install packs.
    /// </summary>
    public sealed class FlaxAssetManagerWindow : EditorWindow
    {
        private readonly SettingsService _settings;
        private readonly AssetSourceManager _sourceManager;
        private readonly AssetInstallerService _installer;
        private readonly MaterialCreatorService _materialService;
        private readonly PluginUpdateService _pluginUpdater;

        private Panel _root;
        private Panel _topBar;
        private SplitPanel _split;

        private Dropdown _sourceCombo;
        private Button _showJsonButton;
        private Button _refreshButton;
        private Button _addSourceButton;
        private Button _editSourceButton;
        private Button _removeSourceButton;
        private Button _editListFileButton;
        private Button _updatePluginButton;
        private TextBox _searchBox;
        private Dropdown _typeFilter;
        private LabeledCheckBox _installedOnly;
        // Left side
        private SectionHeader _assetCountHeader;
        // Column-header labels above the scrolling list: Asset / Category / Status (Status says whether
        // an item is installed here, and its cells come from ListRow.SetStatus). Children of Panel1, at
        // the rows' fixed column x plus ListRow.RowLeftInset; a Panel created here for the header drew
        // nothing at all, so the labels sit directly on the pane (see CreateUI).
        private Label _assetColHeader, _chipColHeader, _statusColHeader;
        private Panel _assetScroll;
        private VerticalPanel _assetListPanel;
        private TextLabel _emptyLabel;

        // Right side
        private Panel _detailsScroll;
        private VerticalPanel _detailsPanel;
        private TextLabel _detailsTitle;
        private VerticalPanel _metaRows;
        private KeyValueRow _metaRowSource, _metaRowId, _metaRowVersion, _metaRowType, _metaRowCategory, _metaRowLicense;
        private TextLabel _detailsInfo;
        private TextLabel _detailsStatus;
        private Button _installButton;
        private Button _removeButton;
        private HorizontalPanel _variantRow;
        private Dropdown _variantCombo;
        private bool _updatingVariantCombo;

        // Bottom
        private StatusBar _statusBar;

        private List<PackEntry> _currentPacks = new List<PackEntry>();
        private List<FetchedSource> _currentSources = new List<FetchedSource>();
        // Index sources ("manifestKind": "index"): the list shows linked manifests, so the selection is
        // an IndexEntry, not a PackEntry, and those rows live in their own list.
        private IndexEntry _selectedIndexEntry;
        private bool _listIsIndex;
        // A source added from an index entry is a status-line event, not a modal. The add is noted here
        // because the refresh that follows takes the status bar with its busy text right away, so the
        // note is replayed once by OnFetchedMainThread, after that source has been fetched.
        private string _pendingSourceNote;
        private readonly List<(IndexEntry entry, AssetManagerListRow row)> _indexRowItems = new List<(IndexEntry, AssetManagerListRow)>();
        private FlatButton _addLinkedSourceButton;
        // Clickable upstream-site link in the details panel (manifest `website`, falling back to
        // `homepageUrl`); _websiteUrl is what the click opens, null = hidden.
        private HorizontalPanel _websiteLinkRow;
        private FlatButton _websiteLinkButton;
        private string _websiteUrl;
        // Credit line of the selected pack: the manifest's `credit`, or name + author + license + the
        // source's website. Null when there is nothing to credit, which hides the row and its button.
        private TextLabel _detailsCredit;
        private FlatButton _copyCreditButton;
        private string _creditLine;
        private PackEntry _selectedPack;
        private FetchedSource _selectedSource;
        private ListRow _selectedRow;
        private readonly List<(PackEntry pack, ListRow row)> _rowItems = new List<(PackEntry, ListRow)>();
        // Row widgets live between rebuilds. A rebuild used to dispose every row and build a new one per
        // item: a row is a container (a ListRow is a Label, carrying four labels of its own), so a
        // 1,000-item list meant ~5,000 controls created, laid out and destroyed per keystroke, all of it
        // garbage. The pool keeps them; a rebuild re-labels the rows it needs and hides the rest. Pool
        // slots are the panel's children in order, which is what keeps the visible rows in list order.
        private readonly List<AssetManagerListRow> _rowPool = new List<AssetManagerListRow>();
        // Per-pack search haystack and file-type mask: facts of a fetched manifest, so each pack pays for
        // them once, not once per keystroke. Dropped when the pack list is replaced.
        private readonly Dictionary<PackEntry, string> _packHay = new Dictionary<PackEntry, string>();
        private readonly Dictionary<PackEntry, uint> _packTypes = new Dictionary<PackEntry, uint>();
        // Typing in the search box filters the list; rebuilding on every keystroke ran the whole rebuild -
        // filter, rows and layout - once per character. The rebuild runs this far behind the last keystroke.
        private const double SearchDebounceSeconds = 0.12;
        private DateTime _lastSearchTypedUtc;
        private bool _searchRebuildPending;
        private CancellationTokenSource _refreshCts;
        private CancellationTokenSource _installCts;
        private CancellationTokenSource _installMaterialCts;
        // Self-update (git pull + scripts recompile) keeps its own token, cancelled on close like every
        // other in-flight operation.
        private CancellationTokenSource _updateCts;
        // Fetch completes on a thread-pool thread; GUI updates are queued to the next main-thread OnUpdate.
        private int _fetchedPending;
        private int _refreshCleanupPending;

        // Install/update/remove progress and log reports arrive from worker threads. Progress<T> cannot
        // marshal them (the engine sets no SynchronizationContext), so they are queued here and applied
        // by the next main-thread OnUpdate — the same pattern as _fetchedPending.
        private readonly ConcurrentQueue<Action> _mainThreadCalls = new ConcurrentQueue<Action>();

        // Preview image cache
        private HttpClient _previewHttpClient;
        private ImageView _mainPreview;
        private Panel _previewPanel;
        private SectionHeader _previewHeader;
        private SectionHeader _texturesHeader;
        private TextLabel _fileListLabel;

        // Audio preview: live playback of the pack's first audio file, downloaded from its
        // source URL and played through a throwaway AudioSource in a preview scene (the same
        // pattern as the engine's AudioClip asset window; the engine has no HTTP audio
        // streaming API, so the clip is loaded from the downloaded file as an AudioClip).
        private FlatButton _previewButton;
        private AudioSource _previewSource;
        private EditorScene _previewScene;
        private string _previewTempFile;
        private string _previewingPackId;
        private CancellationTokenSource _previewCts;
        private HttpClient _audioHttpClient;
        private const float PreviewImageTop = 40f; // gap between the "Preview" header and the image

        public FlaxAssetManagerWindow(global::FlaxEditor.Editor editor, SettingsService settings, AssetSourceManager sourceManager, AssetInstallerService installer)
            : base(editor, true, ScrollBars.None)
        {
            Title = "Flax Asset Manager";
            _settings = settings;
            _sourceManager = sourceManager;
            _installer = installer;
            _materialService = new MaterialCreatorService(settings);
            _pluginUpdater = new PluginUpdateService(settings);
            _sourceManager.Fetched += OnFetched;
            // Both send the self-identifying UA (HttpIdentity): preview images and audio samples come from
            // the publisher's own site, fetched on every selection change, and a host should be able to
            // see that it is FlaxAssetManager asking and not a browser.
            _previewHttpClient = HttpIdentity.NewClient(TimeSpan.FromSeconds(15));
            _audioHttpClient = HttpIdentity.NewClient(TimeSpan.FromSeconds(120));
            // The installer raises these from its worker threads; both write the status bar, so they
            // go through the window's main-thread queue (Progress<T> could not marshal them).
            _installer.Log += s => QueueOnMain(() =>
            {
                if (_statusBar != null)
                    _statusBar.SetStatus(s);
            });
            _installer.Progress += (f, m) => QueueOnMain(() => SetProgress(f, m));

            CreateUI();
            _statusBar.SetStatus($"{_settings.SourceConfig.Sources.Count} source(s) configured. Click Refresh to fetch.");
            RefreshSourcesAsync();
        }

        private void CreateUI()
        {
            _root = new Panel
            {
                AnchorPreset = AnchorPresets.StretchAll,
                Offsets = Margin.Zero,
                BackgroundColor = Theme.Bg, // dark metallic blue backdrop for the whole window (panels are transparent by default)
                Parent = this
            };

            // === Top bar (60px) ===
            _topBar = new Panel(ScrollBars.None)
            {
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(0, 0, 0, 60),
                BackgroundColor = Theme.BgAlt,
                Parent = _root
            };

            // Row 1
            var sourceLabel = new TextLabel("Source:", TextLabelStyle.Body, dim: true)
            {
                Parent = _topBar,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(8, 7, 48, 22),
            };
            _sourceCombo = new Dropdown
            {
                Parent = _topBar,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(60, 7, 232, 22),
            };
            _sourceCombo.SelectedIndexChanged += (cb) => OnSourceChanged();

            _showJsonButton = new FlatButton("View JSON")
            {
                TooltipText = "View the raw manifest JSON for the selected source.",
                Parent = _topBar,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(298, 7, 72, 22),
            };
            _showJsonButton.Clicked += OnShowJsonClicked;

            _refreshButton = new FlatButton("Refresh")
            {
                TooltipText = "Re-fetch manifests from all enabled URLs. A source that has not changed answers 304; editing the source list forces a full read.",
                Parent = _topBar,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(376, 7, 72, 22),
            };
            _refreshButton.Clicked += () => RefreshSourcesAsync();

            _addSourceButton = new FlatButton("Add URL")
            {
                Parent = _topBar,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(454, 7, 72, 22),
            };
            _addSourceButton.Clicked += OnAddSourceClicked;
            _editSourceButton = new FlatButton("Edit")
            {
                TooltipText = "Edit the manifest URL of the selected source.",
                Parent = _topBar,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(534, 7, 60, 22),
            };
            _editSourceButton.Clicked += OnEditSourceClicked;

            _removeSourceButton = new FlatButton("Remove")
            {
                Parent = _topBar,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(602, 7, 66, 22),
            };
            _removeSourceButton.Clicked += OnRemoveSourceClicked;

            _editListFileButton = new FlatButton("Edit List")
            {
                TooltipText = "Open the source list file (Content/FlaxAssetManager/Sources.json) in the system's default text editor.\nClick Refresh afterwards to apply the edits.",
                Parent = _topBar,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(674, 7, 84, 22),
            };
            _editListFileButton.Clicked += OnEditListFileClicked;

            _updatePluginButton = new FlatButton("Update Plugin")
            {
                TooltipText = "Update this plugin in place: 'git pull --ff-only' in this plugin's own folder under Plugins/ (found by its .flaxproj, not by folder name), then a scripts recompile so the editor loads the new code. A confirm dialog states both steps, and a checkout with local changes is left untouched.",
                Parent = _topBar,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(764, 7, 108, 22),
            };
            _updatePluginButton.Clicked += OnUpdatePluginClicked;

            // Row 2
            _searchBox = new TextBox
            {
                WatermarkText = "Search assets...",
                Parent = _topBar,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(8, 35, 282, 22),
            };
            _searchBox.TextChanged += OnSearchTextChanged;

            var typeLabel = new TextLabel("Type:", TextLabelStyle.Body, dim: true)
            {
                Parent = _topBar,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(300, 35, 40, 22),
            };
            _typeFilter = new Dropdown
            {
                Parent = _topBar,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(344, 35, 140, 22),
            };
            _typeFilter.AddItem("All");
            foreach (var n in Enum.GetNames(typeof(FileAssetType)))
                _typeFilter.AddItem(n);
            _typeFilter.AddItem("PluginProject");
            _typeFilter.AddItem("ArchivePack");
            _typeFilter.SelectedIndex = 0;
            _typeFilter.SelectedIndexChanged += (cb) => RebuildAssetList();

            _installedOnly = new LabeledCheckBox("Installed only")
            {
                Parent = _topBar,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(500, 35, 108, 22),
                TooltipText = "Only show assets from the selected source that are already installed.",
            };
            _installedOnly.CheckedChanged += _ => RebuildAssetList();

            // === Split (assets | details) ===
            _split = new SplitPanel(Orientation.Horizontal, ScrollBars.None, ScrollBars.None)
            {
                AnchorPreset = AnchorPresets.StretchAll,
                Offsets = new Margin(0, 0, 60, 24),
                Parent = _root,
                SplitterValue = 0.5f,
            };

            // Left: asset list
            _assetCountHeader = new SectionHeader("Assets (0)")
            {
                Parent = _split.Panel1,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(8, 8, 6, 0),
                Height = 20,
            };

            // Column header, drawn under the section header while the list scrolls underneath it. The
            // labels are children of the panel behind the list - a Panel created here for the header
            // drew nothing at all, not even its background band, in two live runs - and they use the
            // rows' fixed column x plus the rows' inset, so each label sits above its column of cells
            // at any pane width. Built like a row cell: TopLeft + Bounds + ClipText.
            _assetColHeader = new Label
            {
                Parent = _split.Panel1,
                Font = Theme.CaptionFont,
                TextColor = Theme.FgDim,
                HorizontalAlignment = TextAlignment.Near,
                VerticalAlignment = TextAlignment.Near,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(ListRow.RowLeftInset + Theme.NameColumnLeft, 30, 150, 16),
                ClipText = true,
            };
            _chipColHeader = new Label
            {
                Parent = _split.Panel1,
                Font = Theme.CaptionFont,
                TextColor = Theme.FgDim,
                HorizontalAlignment = TextAlignment.Near,
                VerticalAlignment = TextAlignment.Near,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(ListRow.RowLeftInset + Theme.ChipColumnLeft, 30, Theme.ChipColumnWidth, 16),
                ClipText = true,
            };
            _statusColHeader = new Label
            {
                Parent = _split.Panel1,
                Font = Theme.CaptionFont,
                TextColor = Theme.FgDim,
                HorizontalAlignment = TextAlignment.Near,
                VerticalAlignment = TextAlignment.Near,
                AnchorPreset = AnchorPresets.TopLeft,
                Bounds = new Rectangle(ListRow.RowLeftInset + Theme.StatusColumnLeft, 30, Theme.StatusColumnWidth, 16),
                ClipText = true,
                TooltipText = "Whether this item is installed in this project, read from Content/FlaxAssetManager/.installed.json - the same record the Install / Update / Remove buttons use.",
            };

            _assetScroll = new Panel(ScrollBars.Vertical)
            {
                AnchorPreset = AnchorPresets.StretchAll,
                Offsets = new Margin(0, 0, 48, 0), // top clears the section header and the column-header labels
                Parent = _split.Panel1
            };

            _assetListPanel = new VerticalPanel
            {
                AnchorPreset = AnchorPresets.StretchAll,
                BackgroundColor = Color.Transparent,
                Pivot = Float2.Zero,
                IsScrollable = true,
                AutoSize = true,
                Parent = _assetScroll
            };
            _emptyLabel = new TextLabel("No assets. Add a source URL and click Refresh.", TextLabelStyle.Caption, dim: true)
            {
                Wrapping = TextWrapping.WrapWords,
                HorizontalAlignment = TextAlignment.Center,
                Parent = _assetScroll,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(8, 8, 8, 0),
                Height = 40,
                Visible = false,
            };

            // Right: details (scrollable)
            _detailsScroll = new Panel(ScrollBars.Vertical)
            {
                AnchorPreset = AnchorPresets.StretchAll,
                Offsets = Margin.Zero,
                Parent = _split.Panel2
            };
            _detailsPanel = new VerticalPanel
            {
                AnchorPreset = AnchorPresets.StretchAll,
                BackgroundColor = Color.Transparent,
                Pivot = Float2.Zero,
                IsScrollable = true,
                AutoSize = true,
                Spacing = 8,
                Margin = new Margin(8, 8, 16, 0),
                Parent = _detailsScroll
            };

            _detailsTitle = new TextLabel("Select a pack", TextLabelStyle.Title)
            {
                Parent = _detailsPanel,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = Margin.Zero,
            };

            _metaRows = new VerticalPanel
            {
                Parent = _detailsPanel,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = Margin.Zero,
                Spacing = 3,
                ControlChildSize = true,
            };
            _metaRowSource = new KeyValueRow { Parent = _metaRows };
            _metaRowId = new KeyValueRow { Parent = _metaRows };
            _metaRowVersion = new KeyValueRow { Parent = _metaRows };
            _metaRowType = new KeyValueRow { Parent = _metaRows };
            _metaRowCategory = new KeyValueRow { Parent = _metaRows };
            _metaRowLicense = new KeyValueRow { Parent = _metaRows };

            // Website link, directly under the metadata table: the selected manifest's `website`
            // (falling back to `homepageUrl`), opened in the user's own browser through Platform.OpenUrl.
            // Styled like the other buttons; the plugin never fetches this URL - the click is an explicit
            // user action (D12: trust stays per URL).
            _websiteLinkRow = new HorizontalPanel
            {
                Parent = _detailsPanel,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = Margin.Zero,
                Spacing = 8,
                Height = 26,
                Visible = false,
            };
            _websiteLinkButton = new FlatButton("Open website")
            {
                Parent = _websiteLinkRow,
                Width = 170,
                Height = 26,
                TooltipText = "Opens the source's website in your browser.",
            };
            _websiteLinkButton.Clicked += OnWebsiteLinkClicked;

            // Variant row: sits between the metadata rows and the status/actions, shown only
            // when the selected assetPack declares variants.
            _variantRow = new HorizontalPanel
            {
                Parent = _detailsPanel,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = Margin.Zero,
                Spacing = 8,
                Height = 26,
                Visible = false,
            };
            var variantLabel = new TextLabel("Variant:", TextLabelStyle.Body, dim: true)
            {
                Parent = _variantRow,
                Width = Theme.KeyColumnWidth, // aligns with KeyValueRow's key column
                Height = 26,
            };
            _variantCombo = new Dropdown
            {
                Parent = _variantRow,
                Width = 200,
                Height = 26,
            };
            _variantCombo.SelectedIndexChanged += (cb) => OnVariantChanged();

            _detailsStatus = new TextLabel("", TextLabelStyle.Caption)
            {
                Parent = _detailsPanel,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = Margin.Zero,
            };
            _detailsInfo = new TextLabel("Choose a source, then click an asset in the list to view details.", TextLabelStyle.Body)
            {
                Wrapping = TextWrapping.WrapWords,
                AutoHeight = true,
                Parent = _detailsPanel,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = Margin.Zero,
            };
            // The attribution the selected pack's license asks for. Wrapping and auto-height, because a
            // credit is a sentence while a KeyValueRow value is one clipped line.
            _detailsCredit = new TextLabel("", TextLabelStyle.Caption, dim: true)
            {
                Wrapping = TextWrapping.WrapWords,
                AutoHeight = true,
                Parent = _detailsPanel,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = Margin.Zero,
                Visible = false,
            };

            // Action buttons: created before the preview so they stay above it in the
            // scrollable details panel and are always visible when a pack is selected.
            var actionsPanel = new HorizontalPanel
            {
                Parent = _detailsPanel,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = Margin.Zero,
                Spacing = 8,
                Height = 26, // explicit: ControlChildSize=true forces children to this height; 0 collapses them
            };
            // The pack's single install action; RefreshActionButtons sets its label (Install / Update /
            // Reinstall) from the install record, for every pack type.
            _installButton = new FlatButton("Install")
            {
                Parent = actionsPanel,
                Width = 110,
                Height = 26,
                Enabled = false,
            };
            _installButton.Clicked += OnInstallClicked;


            _removeButton = new FlatButton("Remove")
            {
                Parent = actionsPanel,
                Width = 90,
                Height = 26,
                Enabled = false,
                Visible = false,
            };
            _removeButton.Clicked += OnRemoveClicked;
            _previewButton = new FlatButton("Preview")
            {
                Parent = actionsPanel,
                Width = 90,
                Height = 26,
                Enabled = false,
                Visible = false,
                TooltipText = "Play the pack's first audio file live from its source URL.",
            };
            _previewButton.Clicked += OnPreviewClicked;
            // Ships the credit line of the selected pack. A license that requires credit (CC-BY, an
            // attribution clause) is satisfied by a sentence in the shipped credits, and that sentence is
            // the manifest's data - not something the user should reconstruct from a license deed.
            _copyCreditButton = new FlatButton("Copy credit line")
            {
                Parent = actionsPanel,
                Width = 120,
                Height = 26,
                Visible = false,
                TooltipText = "Copies the attribution line for this pack to the clipboard.",
            };
            _copyCreditButton.Clicked += () =>
            {
                if (string.IsNullOrWhiteSpace(_creditLine))
                    return;
                try { Clipboard.Text = _creditLine; } catch { }
                if (_statusBar != null)
                    _statusBar.SetStatus("Credit line copied. Paste it into your project's credits; the license, not this line, decides what is required.");
            };

            // Shown only for an entry of an index manifest: it adds that manifest URL to the sources
            // list. Pack actions (Install/Remove/Preview) do not apply while an index is selected.
            _addLinkedSourceButton = new FlatButton("Add linked source")
            {
                Parent = actionsPanel,
                Width = 150,
                Height = 26,
                Visible = false,
                TooltipText = "Adds the linked manifest URL to your sources list.",
            };
            _addLinkedSourceButton.Clicked += OnAddLinkedSourceClicked;

            // Preview panel (header + main preview image + textures section)
            _previewPanel = new Panel(ScrollBars.None)
            {
                Parent = _detailsPanel,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = Margin.Zero,
                Height = 0,
                Visible = false,
            };
            _previewHeader = new SectionHeader("Preview")
            {
                Parent = _previewPanel,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(0, 0, 0, 0),
                Height = 20,
            };
            _texturesHeader = new SectionHeader("")
            {
                Parent = _previewPanel,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(0, 24 + Theme.PreviewHeight, 0, 0),
                Height = 20,
                Visible = false,
            };
            _fileListLabel = new TextLabel("", TextLabelStyle.Caption, dim: true)
            {
                Wrapping = TextWrapping.WrapWords,
                AutoHeight = true,
                Parent = _previewPanel,
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(0, 24 + Theme.PreviewHeight + 24, 0, 0),
            };

            // === Bottom bar ===
            _statusBar = new StatusBar
            {
                AnchorPreset = AnchorPresets.HorizontalStretchBottom,
                Offsets = Margin.Zero,
                Y = -24, // bottom-anchored: negative top offset = 24px above the root's bottom edge
                Parent = _root,
            };

            // Finalize layout
            _root.UnlockChildrenRecursive();
            _topBar.UnlockChildrenRecursive();
            _split.UnlockChildrenRecursive();
            _detailsPanel.UnlockChildrenRecursive();
            _assetScroll.UnlockChildrenRecursive();
            _statusBar.PerformLayout();
            PerformLayout();
        }


        private void OnAddSourceClicked()
        {
            var popup = new ContextMenuBase
            {
                Size = new Float2(380, 92),
                ClipChildren = false,
            };
            popup.Show(_addSourceButton, new Float2(0, _addSourceButton.Height));

            var tb = new TextBox
            {
                Parent = popup,
                WatermarkText = "https://example.com/manifest.json",
                Bounds = new Rectangle(10, 10, 360, 18),
            };
            var clip = Clipboard.Text ?? string.Empty;
            if (clip.StartsWith("http://") || clip.StartsWith("https://"))
                tb.Text = clip.Trim();

            var warning = new Label
            {
                Parent = popup,
                Text = "⚠ Only download from sources you trust.",
                TextColor = new Color(0.85f, 0.65f, 0.30f),
                Wrapping = TextWrapping.WrapWords,
                Bounds = new Rectangle(10, 31, 360, 18),
            };

            var addBtn = new Button { Parent = popup, Text = "Add", Bounds = new Rectangle(80, 58, 80, 22) };
            var cancelBtn = new Button { Parent = popup, Text = "Cancel", Bounds = new Rectangle(220, 58, 80, 22) };
            addBtn.Clicked += () =>
            {
                var url = tb.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    _settings.AddSource(url);
                    RefreshSourcesAsync(force: true);
                }
                popup.Hide();
            };
            cancelBtn.Clicked += () => popup.Hide();
            popup.UnlockChildrenRecursive();
        }

        private void OnEditSourceClicked()
        {
            if (_selectedSource == null)
            {
                MessageBox.Show("Select a source first.", "No source selected", MessageBoxButtons.OK);
                return;
            }
            var oldUrl = _selectedSource.Url;
            var popup = new ContextMenuBase
            {
                Size = new Float2(400, 88),
                ClipChildren = false,
            };
            popup.Show(_editSourceButton, new Float2(0, _editSourceButton.Height));

            var tb = new TextBox
            {
                Parent = popup,
                WatermarkText = "https://example.com/manifest.json",
                Bounds = new Rectangle(10, 10, 380, 18),
            };
            tb.Text = oldUrl;
            tb.Focus();

            void ApplySave()
            {
                var newUrl = tb.Text?.Trim();
                if (string.IsNullOrEmpty(newUrl))
                {
                    MessageBox.Show("The URL cannot be empty.", "Edit source", MessageBoxButtons.OK);
                    return;
                }

                if (string.Equals(newUrl, oldUrl, StringComparison.OrdinalIgnoreCase))
                {
                    popup.Hide();
                    return;
                }
                if (!_settings.RenameSource(oldUrl, newUrl))
                {
                    MessageBox.Show("That URL is already in use by another source.", "Edit source", MessageBoxButtons.OK);
                    return;
                }
                popup.Hide();
                RefreshSourcesAsync(force: true);
            }

            var saveBtn = new Button { Parent = popup, Text = "Save", Bounds = new Rectangle(110, 54, 80, 22) };
            var cancelBtn = new Button { Parent = popup, Text = "Cancel", Bounds = new Rectangle(230, 54, 80, 22) };
            saveBtn.Clicked += ApplySave;
            cancelBtn.Clicked += () => popup.Hide();
            tb.KeyDown += (key) =>
            {
                if (key == KeyboardKeys.Return)
                    ApplySave();
            };
            popup.UnlockChildrenRecursive();
        }

        private void OnShowJsonClicked()
        {
            if (_selectedSource == null)
            {
                MessageBox.Show("Select a source first.", "No source selected", MessageBoxButtons.OK);
                return;
            }
            var json = _selectedSource.RawJson;
            if (string.IsNullOrEmpty(json))
            {
                var msg = _selectedSource.Success
                    ? "No raw manifest text is available for this source."
                    : "No raw manifest text is available.\n\n" + _selectedSource.Error;
                MessageBox.Show(msg, "View JSON", MessageBoxButtons.OK);
                return;
            }

            // Show the raw manifest in a lightweight read-only label. A TextBox performs a full
            // per-character layout (caret/selection/hit-test) that freezes the window on large
            // text, so we cap the displayed size here; Copy still gives the full text.
            const int maxDisplayChars = 200000;
            string displayJson = json;
            if (json.Length > maxDisplayChars)
                displayJson = json.Substring(0, maxDisplayChars) +
                    $"\n\n... truncated {json.Length - maxDisplayChars:N0} chars - use Copy for the full text.";

            var titleText = _selectedSource.Manifest?.SourceName ?? _selectedSource.Url ?? _selectedSource.Manifest?.SourceId ?? _selectedSource.Url;
            var popup = new ContextMenuBase
            {
                Size = new Float2(760, 560),
                ClipChildren = false,
            };

            new TextLabel(titleText, TextLabelStyle.Body)
            {
                Parent = popup,
                Bounds = new Rectangle(12, 10, popup.Width - 24, 22),
                Wrapping = TextWrapping.WrapWords,
            };

            var jsonScroll = new Panel(ScrollBars.Vertical)
            {
                Parent = popup,
                Bounds = new Rectangle(12, 40, popup.Width - 24, popup.Height - 40 - 58),
            };
            new TextLabel(displayJson, TextLabelStyle.Body)
            {
                Parent = jsonScroll,
                Wrapping = TextWrapping.WrapWords,
                AutoHeight = true,
                Bounds = new Rectangle(2, 2, jsonScroll.Width - 4, 1),
            };

            var copyBtn = new Button { Parent = popup, Text = "Copy", Bounds = new Rectangle(12, popup.Height - 48, 80, 30) };
            var closeBtn = new Button { Parent = popup, Text = "Close", Bounds = new Rectangle(popup.Width - 92, popup.Height - 48, 80, 30) };
            copyBtn.Clicked += () =>
            {
                try { Clipboard.Text = json; } catch { }
            };
            closeBtn.Clicked += () => popup.Hide();

            var loc = new Float2(
                Mathf.Max(0f, (this.Bounds.Width - popup.Width) * 0.5f),
                Mathf.Max(0f, (this.Bounds.Height - popup.Height) * 0.5f));
            popup.Show(this, loc);
            popup.UnlockChildrenRecursive();
            // Pin the viewer to the top. A fresh panel centers tall content on first enable and the
            // ViewOffset setter is a no-op while the panel is locked, so zero the offset, force a
            // full re-layout to recompute the range from un-scrolled bounds, then write the target
            // (applies immediately, no smoothing).
            jsonScroll.ViewOffset = Float2.Zero;
            jsonScroll.PerformLayout(true);
            var vBar = jsonScroll.VScrollBar;
            if (vBar != null)
                vBar.TargetValue = vBar.Minimum;
        }

        private void OnRemoveSourceClicked()
        {
            if (_selectedSource == null)
            {
                MessageBox.Show("Select a source first.", "No source selected", MessageBoxButtons.OK);
                return;
            }
            var url = _selectedSource.Url;
            if (MessageBox.Show($"Remove source?\n{url}", "Remove source", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                _settings.RemoveSource(url);
                _selectedSource = null;
                RefreshSourcesAsync(force: true);
            }
        }

        /// <summary>
        /// Opens the persisted source list (Sources.json) in the OS-associated text editor. The
        /// file is created first if needed, because a source is only persisted on add/remove, so a
        /// fresh install has no file to open. FetchAllAsync reloads the file from disk, so a later
        /// Refresh picks up external edits without an editor restart.
        /// </summary>
        private void OnEditListFileClicked()
        {
            if (!_settings.EnsureSourcesFile())
            {
                MessageBox.Show("The source list file could not be created. Check that the Content folder is writable.", "Edit source list", MessageBoxButtons.OK);
                return;
            }

            var path = _settings.SourcesJsonPath;
            if (!SystemFileOpener.TryOpenInEditor(path, out var error))
            {
                MessageBox.Show($"Could not open the source list in an external editor.\n\n{error}", "Edit source list", MessageBoxButtons.OK);
                return;
            }

            Debug.Log($"FlaxAssetManager: opened source list '{path}' in the system editor.");
            if (_statusBar != null)
                _statusBar.SetStatus($"Opened {Path.GetFileName(path)} in the system editor - click Refresh to apply changes.");
        }

        /// <summary>
        /// Updates the plugin itself: <c>git pull</c> in its own folder, then a scripts recompile so the
        /// editor loads the new code (the plugin's assemblies are built by Flax, not shipped in git, so a
        /// pulled source tree only takes effect after a rebuild). The confirm names both steps, because the
        /// second one reloads the plugin's assemblies out from under the running editor — the scripts reload
        /// disposes this window, which is why the tail below expects <c>IsDisposing</c>.
        /// <para>
        /// Every refusal (not a git checkout, dirty tree, detached HEAD, no upstream, diverged branch) comes
        /// from the service before anything is written, and keeps a modal: each one names something the user
        /// has to fix in git, which a status-bar line would hide (see the status-bar/modal split in the
        /// changelog). Success reports in the status bar.
        /// </para>
        /// </summary>
        private async void OnUpdatePluginClicked()
        {
            // Resolve the folder before asking, so the confirm can name exactly what gets written to.
            if (!_pluginUpdater.TryResolvePluginFolder(out var folder, out var resolveError))
            {
                MessageBox.Show(resolveError, "Update plugin", MessageBoxButtons.OK);
                return;
            }

            var display = PluginUpdateService.DisplayFolder(folder);
            var confirm =
                "Update Flax Asset Manager?\n\n" +
                $"1. 'git pull --ff-only' runs in '{display}'.\n" +
                "2. If that changed any files, the editor is asked to recompile the project scripts, which rebuilds and reloads this plugin's code.\n\n" +
                "Save your work first - a scripts reload rebuilds the editor's plugin assemblies.\n\n" +
                "Continue?";
            if (MessageBox.Show(confirm, "Update Flax Asset Manager", MessageBoxButtons.YesNo) != DialogResult.Yes)
                return;

            // One update at a time, and closing the window aborts the pull (OnDestroy cancels this token).
            _updateCts?.Cancel();
            var cts = _updateCts = new CancellationTokenSource();
            var token = cts.Token;
            if (_updatePluginButton != null)
                _updatePluginButton.Enabled = false;
            if (_statusBar != null)
            {
                _statusBar.SetBusy($"Updating {display} (git pull)...");
                _statusBar.SetProgress(0.3f);
            }

            PluginUpdateService.Result result = null;
            string error = null;
            try
            {
                result = await _pluginUpdater.UpdateAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Closed mid-flight; the window is going away, so there is nothing to report.
                return;
            }
            catch (Exception e)
            {
                error = e.Message;
                Debug.LogError($"FlaxAssetManager: self-update failed: {e.Message}");
            }

            // UpdateAsync resumes on a thread-pool thread (the engine sets no SynchronizationContext), and
            // everything below is GUI state — one hop back, the shape every other operation here uses. After
            // a recompile request the scripts reload disposes this window, so the IsDisposing exit is the
            // normal ending of a successful update, not a failure.
            await MainThreadDispatcher.RunOnMain(() =>
            {
                if (IsDisposing)
                    return;
                if (_updatePluginButton != null)
                    _updatePluginButton.Enabled = true;
                if (_statusBar != null)
                {
                    _statusBar.SetIdle();
                    if (error != null)
                        _statusBar.SetStatus($"Error: {error}");
                    else if (result != null)
                        _statusBar.SetStatus(FirstLine(result.Message));
                }

                if (result == null)
                    return;

                // git's own words go to the log, capped: the status bar has one line and a modal is the
                // user's channel, so a long pull transcript does not become a dialog wall.
                if (!string.IsNullOrEmpty(result.Detail))
                    Debug.Log($"FlaxAssetManager: self-update git output:\n{Truncate(result.Detail, 4000)}");

                if (!result.Okay)
                    MessageBox.Show(result.Message, "Update Flax Asset Manager", MessageBoxButtons.OK);
            });
        }

        /// <summary>First line of a multi-line message, for the one-line status bar.</summary>
        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            var idx = text.IndexOf('\n');
            return idx < 0 ? text : text.Substring(0, idx);
        }

        /// <summary>Caps a block of log text, naming how much was dropped.</summary>
        private static string Truncate(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
                return text;
            return text.Substring(0, maxChars) + $"\n... truncated {text.Length - maxChars:N0} chars (see your git tooling for the full output).";
        }

        /// <summary>
        /// Fetches every source. <c>force</c> drops the conditional-GET validators, so the caller can
        /// demand full reads: after editing the source list, after opening a manifest in the system
        /// editor, and on Shift+click, which is the "this looks stale" gesture.
        /// </summary>
        private async void RefreshSourcesAsync(bool force = false)
        {
            _refreshCts?.Cancel();
            _refreshCts = new CancellationTokenSource();
            if (_statusBar != null)
            {
                _statusBar.SetBusy("Fetching manifests...");
                _statusBar.SetProgress(0.08f);
            }
            if (_refreshButton != null) _refreshButton.Enabled = false;
            try
            {
                await _sourceManager.FetchAllAsync(force);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"FlaxAssetManager: Fetch failed: {e.Message}");
            }
            finally
            {
                // Runs on a thread-pool thread; defer GUI cleanup to the main thread.
                System.Threading.Interlocked.Exchange(ref _refreshCleanupPending, 1);
            }
        }

        private void OnFetched()
        {
            // Fires on a thread-pool thread; queue the UI update to the main thread.
            if (!IsDisposing)
                System.Threading.Interlocked.Exchange(ref _fetchedPending, 1);
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            // Apply reports queued by installer worker threads on the main thread.
            DrainMainThreadCalls();
            // Run a deferred search rebuild once the typing has settled (see OnSearchTextChanged).
            if (_searchRebuildPending && (DateTime.UtcNow - _lastSearchTypedUtc).TotalSeconds >= SearchDebounceSeconds)
            {
                _searchRebuildPending = false;
                RebuildAssetList();
            }
            if (System.Threading.Interlocked.Exchange(ref _fetchedPending, 0) == 1)
                OnFetchedMainThread();
            if (System.Threading.Interlocked.Exchange(ref _refreshCleanupPending, 0) == 1)
            {
                if (_refreshButton != null) _refreshButton.Enabled = true;
                if (_statusBar != null) _statusBar.SetIdle();
            }

            // Tick the audio preview scene so the source detects the end of the clip and
            // fires ClipFinished (the preview scene does not tick on its own).
            if (_previewScene != null)
            {
                _previewScene.Update();
                if (_previewButton != null)
                    _previewButton.Text = _previewSource != null && _previewSource.State == AudioSource.States.Playing ? "Stop" : "Preview";
            }
        }

        /// <summary>
        /// Queues a GUI/engine update to run on the editor's main thread (the next <see cref="OnUpdate"/>).
        /// For fire-and-forget reports — installer progress and log lines — that arrive from worker
        /// threads; a handler that has to order work after an <c>await</c> uses
        /// <see cref="MainThreadDispatcher.RunOnMain"/> instead. Updates are dropped once the window is
        /// going away, so a late report cannot touch controls that are being disposed.
        /// </summary>
        private void QueueOnMain(Action action)
        {
            if (action == null || IsDisposing)
                return;
            _mainThreadCalls.Enqueue(action);
        }

        /// <summary>
        /// Applies the queued main-thread updates. Bounded per frame, so a long install reporting every
        /// file cannot starve the editor; leftovers run on the next update.
        /// </summary>
        private void DrainMainThreadCalls()
        {
            for (int i = 0; i < 64 && _mainThreadCalls.TryDequeue(out var action); i++)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"FlaxAssetManager: queued main-thread update failed: {e.Message}");
                }
            }
        }

        /// <summary>Writes a progress report to the status bar. Main thread only (via <see cref="QueueOnMain"/>).</summary>
        private void SetProgress(float value, string message)
        {
            if (_statusBar == null)
                return;
            if (!string.IsNullOrEmpty(message))
                _statusBar.SetBusy(message);
            _statusBar.SetProgress(value);
        }

        /// <summary>
        /// <see cref="IProgress{T}"/> handed to the install/update flows. <see cref="Progress{T}"/> would
        /// resume its callback on a thread-pool thread — it falls back to the thread pool because the
        /// engine installs no <see cref="SynchronizationContext"/> — and the callback writes to the
        /// status bar, so reports are routed through <see cref="QueueOnMain"/>.
        /// </summary>
        private sealed class WindowProgress : IProgress<(float, string)>
        {
            private readonly FlaxAssetManagerWindow _window;

            public WindowProgress(FlaxAssetManagerWindow window)
            {
                _window = window;
            }

            public void Report((float, string) value) => _window.QueueOnMain(() => _window.SetProgress(value.Item1, value.Item2));
        }

        private void OnFetchedMainThread()
        {
            _currentSources = _sourceManager.LastFetched.ToList();
            // Replay a pending "added source" note once, here, now that its first fetch has finished.
            var addedNote = string.IsNullOrEmpty(_pendingSourceNote) ? string.Empty : $"{_pendingSourceNote}: ";
            _pendingSourceNote = null;
            _sourceCombo.ClearItems();
            foreach (var s in _currentSources)
            {
                var cfg = _settings.SourceConfig.Sources.Find(x => x.Url == s.Url);
                var label = cfg?.Name ?? s.Manifest?.SourceName ?? s.Url;
                if (!s.Success)
                {
                    label += " (error)";
                }
                else
                {
                    // An index manifest has no assets of its own; what it lists are the manifests it
                    // links, so that is the number the picker shows, matching "Linked sources (N)".
                    var m = s.Manifest;
                    var count = m.IsIndex ? (m.Entries?.Count ?? 0) : (m.Assets?.Count ?? 0);
                    label += $" ({count})";
                }
                _sourceCombo.AddItem(label);
            }
            if (_currentSources.Count == 0)
            {
                _sourceCombo.AddItem("(no sources) Add a URL");
                _sourceCombo.SelectedIndex = 0;
                _selectedSource = null;
                _currentPacks = new List<PackEntry>();
                InvalidatePackCaches();
                _assetCountHeader.Text = "Assets (0)";
                RebuildAssetList();
                if (_statusBar != null)
                    _statusBar.SetStatus($"{addedNote}No sources configured. Click Add URL.");
                return;
            }

            _sourceCombo.SelectedIndex = 0;
            _selectedSource = _currentSources[0];
            // Show fetch errors inline
            var errors = _currentSources.Where(s => !s.Success).Select(s => $"{s.Url}: {s.Error}").ToArray();
            if (errors.Length > 0)
                Debug.LogWarning($"FlaxAssetManager: {errors.Length} source(s) failed:\n{string.Join("\n", errors)}");

            UpdatePacksForSelectedSource();
            if (_statusBar != null)
            {
                var ok = _currentSources.Count(s => s.Success);
                var fail = _currentSources.Count - ok;
                var fetched = fail == 0 ? $"Fetched {ok} source(s). Select an asset to install." : $"Fetched {ok} OK, {fail} failed. See log.";
                _statusBar.SetStatus($"{addedNote}{fetched}");
            }
        }

        private void OnSourceChanged()
        {
            var idx = _sourceCombo.SelectedIndex;
            if (idx < 0 || idx >= _currentSources.Count) return;
            _selectedSource = _currentSources[idx];
            // Clear selection
            _selectedPack = null;
            if (_selectedRow != null)
                _selectedRow.Selected = false;
            _selectedRow = null;
            _selectedIndexEntry = null;
            _detailsTitle.Text = "Select a pack";
            _installButton.Visible = true;
            _addLinkedSourceButton.Visible = false;
            SetWebsiteLinkVisible(false);
            _websiteUrl = null;
            _metaRowId.Set("ID", "");
            _metaRowVersion.Set("Version", "");
            _metaRowType.Set("Type", "");
            _metaRowCategory.Set("Category", "");
            _metaRowLicense.Set("License", "");
            ShowCredit(null);
            _metaRowSource.Set("From", "");
            _detailsInfo.Text = "Choose an asset in the list to view details.";
            _detailsStatus.Text = "";
            _detailsStatus.TextColor = Theme.FgDim;
            _variantRow.Visible = false;
            HidePreview();
            StopPreview();
            _installButton.Enabled = false;
            _installButton.Text = "Install";
            _removeButton.Visible = false;
            _removeButton.Enabled = false;
            UpdatePacksForSelectedSource();
        }

        private void UpdatePacksForSelectedSource()
        {
            // The search caches key on the pack objects of a fetched manifest, so the new pack list
            // makes the old entries unreachable; drop them here, where the list is swapped.
            InvalidatePackCaches();
            if (_selectedSource?.Manifest?.Assets != null)
                // An index manifest has no assets; keep the pack-side code paths on an empty list.
                _currentPacks = _selectedSource.Manifest.Assets ?? new List<PackEntry>();
            else
                _currentPacks = new List<PackEntry>();
            RebuildAssetList();
        }
        /// <summary>
        /// The search box filters the list, but not on the keystroke: the rebuild is deferred until typing
        /// stops and <see cref="OnUpdate"/> runs it. A rebuild is bounded by the rows it shows, so nine of
        /// them for a nine-character query is nine times the work of one, all of it on the keystroke.
        /// </summary>
        private void OnSearchTextChanged()
        {
            _lastSearchTypedUtc = DateTime.UtcNow;
            _searchRebuildPending = true;
        }

        /// <summary>
        /// Renders an index manifest: one row per linked manifest (name, host, description, tags). An
        /// index has no assets of its own, so installs, materials, previews and removal do not apply -
        /// the only action is adding a linked manifest to the sources list.
        /// </summary>
        private void RebuildIndexList()
        {
            _listIsIndex = true;
            var entries = _selectedSource?.Manifest?.Entries ?? new List<IndexEntry>();
            var search = _searchBox.Text?.Trim() ?? string.Empty;

            var filtered = new List<IndexEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (search.Length != 0)
                {
                    var hay = string.Concat(e.SourceName ?? string.Empty, " ", e.SourceId ?? string.Empty, " ", e.Description ?? string.Empty, " ", string.Join(" ", e.Tags ?? new List<string>()));
                    if (hay.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }
                filtered.Add(e);
            }

            BeginListLayoutBatch();
            try
            {
                _assetCountHeader.Text = $"Linked sources ({filtered.Count})";
                _assetColHeader.Text = "Linked manifest";
                _chipColHeader.Text = "Tags";
                _statusColHeader.Text = "Index";   // these rows can only be an index, never installed
                _assetColHeader.Visible = _chipColHeader.Visible = _statusColHeader.Visible = filtered.Count > 0;
                _emptyLabel.Visible = filtered.Count == 0;
                if (filtered.Count == 0)
                    _emptyLabel.Text = entries.Count == 0 ? "This index links no manifests." : "No linked source matches the search.";

                for (int i = 0; i < filtered.Count; i++)
                {
                    var entry = filtered[i];
                    try
                    {
                        var row = AcquireRow(i);
                        row.Tag = entry;
                        row.Selected = false;

                        var host = SafeHost(entry.Url);
                        string subtitle = $"manifest  •  {host}";
                        if (!string.IsNullOrWhiteSpace(entry.Description))
                            subtitle += $"  •  {entry.Description}";
                        string chip = entry.Tags != null && entry.Tags.Count > 0 ? string.Join(", ", entry.Tags) : "";
                        row.SetContent(string.IsNullOrWhiteSpace(entry.SourceName) ? entry.SourceId : entry.SourceName, subtitle, chip, Theme.FgDim);
                        // An index links manifests; nothing is installed from it, so its rows have no
                        // install state to report - the column says so instead of staying blank.
                        row.SetStatus("Index only", Theme.FgDim, "An index manifest links other manifests. Select one and use Add linked source to browse and install its packs.");
                        _indexRowItems.Add((entry, row));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"FlaxAssetManager: Failed to create row for linked source {entry.SourceId}: {ex.Message}");
                        HideRow(i);
                    }
                }

                TrimRows(filtered.Count);
            }
            finally
            {
                EndListLayoutBatch();
            }
        }

        /// <summary>
        /// Details panel for a linked manifest entry; the index-source counterpart of SelectPack. Pack
        /// actions are hidden, "Add linked source" is shown, and the status line states plainly that
        /// browsing an index installs nothing.
        /// </summary>
        private void SelectIndexEntry(IndexEntry entry, AssetManagerListRow row)
        {
            _selectedIndexEntry = entry;
            _selectedPack = null;
            if (_selectedRow != null)
                _selectedRow.Selected = false;
            _selectedRow = row;
            if (_selectedRow != null)
                _selectedRow.Selected = true;

            _detailsTitle.Text = string.IsNullOrWhiteSpace(entry.SourceName) ? entry.SourceId : entry.SourceName;
            _metaRowId.Set("ID", entry.SourceId);
            _metaRowVersion.Set("Version", "—");
            _metaRowType.Set("Type", "Linked manifest");
            _metaRowCategory.Set("Category", entry.Tags != null && entry.Tags.Count > 0 ? string.Join(", ", entry.Tags) : "—");
            _metaRowLicense.Set("License", "—");
            ShowCredit(null);
            _metaRowSource.Set("From", SafeHost(entry.Url));
            _detailsInfo.Text = (string.IsNullOrWhiteSpace(entry.Description) ? "" : entry.Description + "\n\n") +
                                $"sourceId: {entry.SourceId}\nurl: {entry.Url}" +
                                (string.IsNullOrWhiteSpace(entry.Website) ? "" : $"\nwebsite: {entry.Website}");
            _detailsStatus.Text = "Not installed. An index only links other manifests - add this one as a source to browse it.";
            _detailsStatus.TextColor = Theme.FgDim;

            _installButton.Visible = false;
            _removeButton.Visible = false;
            _previewButton.Visible = false;
            _addLinkedSourceButton.Visible = true;
            _addLinkedSourceButton.Enabled = true;
            ShowWebsiteLink(WebsiteForIndexEntry(entry));
        }

        /// <summary>
        /// Shows the details-panel link to the selected manifest's upstream site. Only http(s) is offered,
        /// and the plugin never fetches it - the click is the user's own action, so a manifest cannot make
        /// the editor load anything (D12: trust stays per URL).
        /// </summary>
        private void ShowWebsiteLink(string url)
        {
            _websiteUrl = null;
            var trimmed = url?.Trim();
            if (string.IsNullOrEmpty(trimmed) ||
                !(trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                SetWebsiteLinkVisible(false);
                return;
            }

            _websiteUrl = trimmed;
            var host = SafeHost(trimmed);
            _websiteLinkButton.Text = host == "the manifest source" ? "Open website ↗" : $"Open {host} ↗";
            _websiteLinkButton.TooltipText = trimmed;
            SetWebsiteLinkVisible(true);
        }

        /// <summary>
        /// Shows/hides the website link row. Row and button move together: the row is what occupies layout
        /// space under the metadata table, so hiding only the button would leave an empty gap.
        /// </summary>
        private void SetWebsiteLinkVisible(bool visible)
        {
            _websiteLinkRow.Visible = visible;
            _websiteLinkButton.Visible = visible;
        }
        /// <summary>
        /// Shows the credit line of the selected pack and enables <b>Copy credit line</b> with it. Hidden
        /// when there is nothing to credit, which is the common case: the CC0 sources of the shipped
        /// catalog owe no attribution, and an empty row with a copy button is a shrug, not a credit.
        /// <para>
        /// The line is the manifest's own words - its <c>credit</c>, else pack name + author + license +
        /// the source's website. The same function builds the line copied into the install record and the
        /// one <c>ATTRIBUTIONS.md</c> is rendered from, so the button, the record and the file cannot
        /// disagree about what a pack owes.
        /// </para>
        /// </summary>
        private void ShowCredit(string line)
        {
            _creditLine = string.IsNullOrWhiteSpace(line) ? null : line.Trim();
            var visible = _creditLine != null;
            _detailsCredit.Visible = visible;
            _copyCreditButton.Visible = visible;
            if (!visible)
                return;

            _detailsCredit.Text = _creditLine;
            _copyCreditButton.TooltipText = "Copies this pack's attribution line to the clipboard, for your project's credits.\n" + _creditLine;
        }

        /// <summary>
        /// Website for an index entry: the entry's own `website` when the index publishes one, otherwise the
        /// `website` (falling back to `homepageUrl`) of that manifest when it is already among the fetched
        /// sources, matched by URL. Null keeps the link hidden - an index cannot invent a website.
        /// </summary>
        private string WebsiteForIndexEntry(IndexEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.Website))
                return entry.Website;

            var url = entry.Url?.Trim();
            if (string.IsNullOrEmpty(url))
                return null;

            var src = _currentSources.FirstOrDefault(s => s.Success && s.Manifest != null && !s.Manifest.IsIndex
                                                         && string.Equals(s.Url?.Trim(), url, StringComparison.OrdinalIgnoreCase));
            if (src == null)
                return null;
            return string.IsNullOrWhiteSpace(src.Manifest.Website) ? src.Manifest.HomepageUrl : src.Manifest.Website;
        }

        private void OnWebsiteLinkClicked()
        {
            var url = _websiteUrl;
            if (string.IsNullOrEmpty(url))
                return;

            // The URL is remote manifest data (website / homepageUrl). Platform.CanOpenUrl asks the OS
            // whether any handler exists for the string, so a manifest carrying file://, mailto: or a
            // custom scheme would open a file manager, a mail composer, or an installed app. Only web
            // links are handed over; the install side is https-only for the same reason (D12).
            if (!GitSafety.IsHttpsUrl(url) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show($"Only http and https links can be opened:\n{url}", "Website", MessageBoxButtons.OK);
                return;
            }
            if (!Platform.CanOpenUrl(url))
            {
                MessageBox.Show($"No browser available to open:\n{url}", "Website", MessageBoxButtons.OK);
                return;
            }
            Platform.OpenUrl(url);
        }

        /// <summary>
        /// Adds the selected index entry's URL as a source. The scheme and duplicate-URL checks mirror
        /// the Add URL popup; a sourceId collision is refused, because sourceId keys the install record
        /// and two sources sharing one would silently merge their records. Success is a status-line note
        /// plus the refresh it triggers, not a modal - a modal is reserved for the refusals above.
        /// </summary>
        private void OnAddLinkedSourceClicked()
        {
            var entry = _selectedIndexEntry;
            if (entry == null)
            {
                MessageBox.Show("Select a linked manifest first.", "No selection", MessageBoxButtons.OK);
                return;
            }
            var url = entry.Url?.Trim();
            if (string.IsNullOrEmpty(url) || !(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("The linked manifest URL is missing, or is not http(s).", "Add linked source", MessageBoxButtons.OK);
                return;
            }
            if (_settings.SourceConfig.Sources.Exists(s => string.Equals(s.Url?.Trim(), url, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("That URL is already one of your sources.", "Add linked source", MessageBoxButtons.OK);
                return;
            }
            var clash = _currentSources.FirstOrDefault(s => s.Success && s.Manifest != null && !s.Manifest.IsIndex
                                                            && !ReferenceEquals(s, _selectedSource)
                                                            && string.Equals(s.Manifest.SourceId, entry.SourceId, StringComparison.Ordinal));
            if (clash != null)
            {
                MessageBox.Show($"A source with sourceId '{entry.SourceId}' is already configured:\n{clash.Url}\n\nRefusing to add a second one - sourceId keys the install record.",
                    "Add linked source", MessageBoxButtons.OK);
                return;
            }
            _settings.AddSource(url);
            // No modal: the refresh below is the proof the add worked. The note is replayed on the
            // status bar by OnFetchedMainThread, after the busy text of this refresh has cleared.
            Debug.Log($"FlaxAssetManager: added linked source '{entry.SourceId}' ({url})");
            _pendingSourceNote = $"Added '{entry.SourceId}' as a source";
            RefreshSourcesAsync(force: true);
        }

        private void RebuildAssetList()
        {
            if (IsDisposing || _assetListPanel == null)
                return;
            // A rebuild now covers whatever the search box says, so a pending deferred one is spent.
            _searchRebuildPending = false;

            _selectedRow = null;
            _rowItems.Clear();
            _indexRowItems.Clear();
            _listIsIndex = false;
            _selectedIndexEntry = null;

            // An index manifest carries no assets: render the manifests it links instead.
            if (_selectedSource?.Manifest?.IsIndex == true)
            {
                RebuildIndexList();
                return;
            }

            var search = _searchBox.Text?.Trim() ?? string.Empty;
            var installedOnly = _installedOnly?.Checked == true;
            var sourceId = _selectedSource?.Manifest?.SourceId ?? "unknown";
            var filter = _typeFilter.SelectedItem?.ToString() ?? "All";
            bool allTypes = filter == "All";
            bool pluginFilter = filter == "PluginProject";
            bool archiveFilter = filter == "ArchivePack";
            bool fileTypesFilter = Enum.TryParse(filter, out FileAssetType wantType) && !allTypes && !pluginFilter && !archiveFilter;

            // The install record of every row, from one pass over the records. FindInstalled is a linear
            // scan, so asking it per row - twice - made a rebuild quadratic in how much is installed.
            var installedByPack = _settings.BuildInstalledIndex(sourceId);

            var filtered = new List<PackEntry>();
            for (int i = 0; i < _currentPacks.Count; i++)
            {
                var p = _currentPacks[i];

                // Installed only: keep just the packs already installed from this source
                if (installedOnly && !installedByPack.ContainsKey(p.Id))
                    continue;

                // Search: match name/id/desc/category/tags OR any file path/type. Per pack that is one
                // case-insensitive IndexOf over a haystack built once for that pack (PackHaystack);
                // typing used to concatenate and lower-case a string per pack, plus two per file, and
                // scan every file of every pack even when the name had already matched.
                if (search.Length != 0 && PackHaystack(p).IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (allTypes)
                {
                    // every pack passes the type filter
                }
                else if (pluginFilter)
                {
                    if (p.PackType != PackType.PluginProject)
                        continue;
                }
                else if (archiveFilter)
                {
                    if (p.PackType != PackType.ArchivePack)
                        continue;
                }
                else if (fileTypesFilter && (p.PackType != PackType.AssetPack || (PackFileTypes(p) & (1u << (int)wantType)) == 0))
                {
                    // The types a pack offers are a fact of its manifest, so the filter is a bit test
                    // against a per-pack mask, not a scan of its files and variants.
                    continue;
                }

                filtered.Add(p);
            }

            BeginListLayoutBatch();
            try
            {
                _assetCountHeader.Text = $"Assets ({filtered.Count})";
                _assetColHeader.Text = "Asset";
                _chipColHeader.Text = "Category";
                _statusColHeader.Text = "Status";
                _assetColHeader.Visible = _chipColHeader.Visible = _statusColHeader.Visible = filtered.Count > 0; // no column header over an empty list
                _emptyLabel.Visible = filtered.Count == 0;
                if (filtered.Count == 0)
                {
                    if (_currentPacks.Count == 0)
                        _emptyLabel.Text = _selectedSource == null ? "No source selected." : _selectedSource.Success ? "This source has no assets." : $"Failed to fetch:\n{_selectedSource.Error}";
                    else
                        _emptyLabel.Text = installedOnly ? "No installed assets match filter/search." : "No assets match filter/search.";
                }

                for (int i = 0; i < filtered.Count; i++)
                {
                    var pack = filtered[i];
                    try
                    {
                        var row = AcquireRow(i);
                        row.Tag = pack;
                        row.Selected = false;

                        installedByPack.TryGetValue(pack.Id, out var installed);
                        // The rule of SettingsService.HasUpdate, read off the record already in hand.
                        bool hasUpdate = installed != null && !string.Equals(installed.InstalledVersion, pack.Version, StringComparison.Ordinal);

                        string subtitle = $"{pack.PackType}  •  v{pack.Version}";
                        if (pack.PackType == PackType.AssetPack && pack.Files != null)
                            subtitle += $"  •  {pack.Files.Count} file(s)";
                        if (pack.PackType == PackType.AssetPack && pack.Variants != null && pack.Variants.Count > 0)
                            subtitle += $"  •  {pack.Variants.Count} variant(s)";
                        if (pack.PackType == PackType.ArchivePack)
                            subtitle += "  •  zip archive";

                        // The chip column shows the manifest's category; the install state lives in the
                        // Status cell below, so the two never show the same thing twice.
                        row.SetContent(pack.Name, subtitle, pack.Category ?? string.Empty, Theme.FgDim);
                        SetRowStatus(row, pack, installed, hasUpdate);
                        _rowItems.Add((pack, row));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"FlaxAssetManager: Failed to create row for {pack.Id}: {ex.Message}\n{ex.StackTrace}");
                        // The slot has no content of its own; drop it so it cannot show the next item's
                        // data shifted up one row.
                        HideRow(i);
                    }
                }

                // Rows the filtered list does not need stay in the pool, out of sight.
                TrimRows(filtered.Count);
            }
            finally
            {
                EndListLayoutBatch();
            }

            // Reapply selection highlight on the rebuilt rows
            if (_selectedPack != null)
            {
                var item = _rowItems.FirstOrDefault(x => ReferenceEquals(x.pack, _selectedPack));
                if (item.row != null)
                {
                    item.row.Selected = true;
                    _selectedRow = item.row;
                }
            }

            // The new rows change the scroll range; pin the list back to the top.
            ResetAssetListScroll();

            // Clear details if selection not in filtered
            if (_selectedPack != null && !filtered.Contains(_selectedPack))
            {
                _selectedPack = null;
                _selectedRow = null;
                _selectedIndexEntry = null;
                _detailsTitle.Text = "Select a pack";
                _installButton.Visible = true;
                _addLinkedSourceButton.Visible = false;
                SetWebsiteLinkVisible(false);
                _websiteUrl = null;
                _metaRowId.Set("ID", "");
                _metaRowVersion.Set("Version", "");
                _metaRowType.Set("Type", "");
                _metaRowCategory.Set("Category", "");
                _metaRowLicense.Set("License", "");
                ShowCredit(null);
                _metaRowSource.Set("From", "");
                _detailsInfo.Text = "Choose an asset in the list to view details.";
                _detailsStatus.Text = "";
                _detailsStatus.TextColor = Theme.FgDim;
                _variantRow.Visible = false;
                _installButton.Enabled = false;
                _installButton.Text = "Install";
                _removeButton.Visible = false;
                _removeButton.Enabled = false;
                HidePreview();
                StopPreview();
            }
        }

        /// <summary>
        /// The row showing list slot <paramref name="slot"/>: taken from the pool, created on first use.
        /// Pool slots are the panel's children in order, so the visible rows stay in list order no matter
        /// which item lands in a slot; a reused row reads its item from <see cref="Control.Tag"/>.
        /// </summary>
        private AssetManagerListRow AcquireRow(int slot)
        {
            if (slot >= _rowPool.Count)
            {
                var created = new AssetManagerListRow
                {
                    Parent = _assetListPanel,
                    AnchorPreset = AnchorPresets.HorizontalStretchTop,
                    Offsets = new Margin(4, 0, 4, 2),
                };
                created.Height = Theme.RowHeight; // Offsets setter squashed height (top-anchored); re-assert
                // Bound once, for the life of the row: the handlers read the row's current Tag, so reuse
                // re-points them at new data. Per-item handlers would pile up on a row that outlives the
                // item it was built for, and every row would fire the selection of every item it ever showed.
                created.Clicked += () => OnRowActivate(created);
                created.Focused += () => OnRowActivate(created);
                created.OnArrow = OnRowArrow;
                _rowPool.Add(created);
            }

            var row = _rowPool[slot];
            if (!row.Visible)
                row.Visible = true;
            return row;
        }

        /// <summary>
        /// Hides the pooled rows past the end of the filtered list; they keep their widgets for the next
        /// rebuild instead of being disposed and rebuilt with it.
        /// </summary>
        private void TrimRows(int used)
        {
            for (int i = used; i < _rowPool.Count; i++)
                HideRow(i);
        }

        /// <summary>Takes a pool slot out of the list (content failed to build, or the filter excluded it).</summary>
        private void HideRow(int slot)
        {
            if (slot >= _rowPool.Count)
                return;
            var row = _rowPool[slot];
            row.Tag = null;
            row.Selected = false;
            if (row.Visible)
                row.Visible = false;
        }

        /// <summary>
        /// Clicked/focused on a row: selects what that row is showing right now. A row is reused across
        /// rebuilds, so the item comes from its Tag - a pack row and a linked-manifest row share the pool.
        /// </summary>
        private void OnRowActivate(Control row)
        {
            switch (row?.Tag)
            {
            case PackEntry pack:
                SelectPack(pack, (AssetManagerListRow)row);
                break;
            case IndexEntry entry:
                SelectIndexEntry(entry, (AssetManagerListRow)row);
                break;
            }
        }

        /// <summary>
        /// Suppresses layout for the asset list while it is being refilled.
        ///
        /// Flax lays a container out whenever a child is added, removed, shown, hidden or resized -
        /// ContainerControl.OnChildrenChanged, Control.Visible and Control.OnSizeChanged all call
        /// PerformLayout - and one pass of a ContainerControl locks the subtree, re-bounds every child,
        /// lays out every child, and lets the panel's own AutoSize resize it, which notifies every child
        /// again. Doing that once per row, for N rows, is quadratic in N; a 1,000-row list is a few tens
        /// of milliseconds per keystroke and a 2,500-row list is hundreds. Locking the subtree turns all
        /// of those calls into no-ops, so the list is laid out once, by EndListLayoutBatch.
        /// </summary>
        private void BeginListLayoutBatch()
        {
            _assetScroll.LockChildrenRecursive();
        }

        /// <summary>Releases the batch and lays the list out once (see <see cref="BeginListLayoutBatch"/>).</summary>
        private void EndListLayoutBatch()
        {
            _assetScroll.UnlockChildrenRecursive();
            _assetScroll.PerformLayout();
        }

        /// <summary>
        /// Everything a pack is searchable by - name, id, description, category, tags, and every file
        /// path and type including the variants' - as one string, built once per pack and dropped when
        /// the manifest is replaced (InvalidatePackCaches). Search matching is case-insensitive over
        /// this, so typing allocates nothing.
        /// </summary>
        private string PackHaystack(PackEntry pack)
        {
            if (_packHay.TryGetValue(pack, out var hay))
                return hay;
            hay = BuildHaystack(pack);
            _packHay[pack] = hay;
            return hay;
        }

        private static string BuildHaystack(PackEntry pack)
        {
            var sb = new StringBuilder();
            sb.Append(pack.Name).Append(' ');
            sb.Append(pack.Id).Append(' ');
            sb.Append(pack.Description ?? string.Empty).Append(' ');
            sb.Append(pack.Category ?? string.Empty).Append(' ');
            if (pack.Tags != null)
            {
                for (int i = 0; i < pack.Tags.Count; i++)
                    sb.Append(pack.Tags[i]).Append(' ');
            }
            AppendFiles(sb, pack.Files);
            if (pack.Variants != null)
            {
                for (int i = 0; i < pack.Variants.Count; i++)
                    AppendFiles(sb, pack.Variants[i].Files);
            }
            return sb.ToString();
        }

        private static void AppendFiles(StringBuilder sb, List<FileEntry> files)
        {
            if (files == null)
                return;
            for (int i = 0; i < files.Count; i++)
                sb.Append(files[i].Path).Append(' ').Append(files[i].Type).Append(' ');
        }

        /// <summary>
        /// The file types a pack offers, as a bitmask (FileAssetType tops out at 9), built once per pack
        /// like the search haystack. Keeps the Type filter a bit test per row instead of a scan of the
        /// pack's files and variants.
        /// </summary>
        private uint PackFileTypes(PackEntry pack)
        {
            if (_packTypes.TryGetValue(pack, out var mask))
                return mask;
            mask = BuildTypeMask(pack);
            _packTypes[pack] = mask;
            return mask;
        }

        private static uint BuildTypeMask(PackEntry pack)
        {
            uint mask = 0u;
            AddFileTypes(ref mask, pack.Files);
            if (pack.Variants != null)
            {
                for (int i = 0; i < pack.Variants.Count; i++)
                    AddFileTypes(ref mask, pack.Variants[i].Files);
            }
            return mask;
        }

        private static void AddFileTypes(ref uint mask, List<FileEntry> files)
        {
            if (files == null)
                return;
            for (int i = 0; i < files.Count; i++)
                mask |= 1u << (int)files[i].Type;
        }

        /// <summary>
        /// Drops the per-pack search caches. They key on the pack objects of a fetched manifest, so a
        /// refetch - which parses new ones - has to drop the old entries; the pack list is what changed.
        /// </summary>
        private void InvalidatePackCaches()
        {
            _packHay.Clear();
            _packTypes.Clear();
        }

        /// <summary>
        /// Writes a pack row's Status cell from its install record - the same record RefreshActionButtons
        /// reads - so the column, the details panel and the Install/Remove buttons cannot disagree. The
        /// cell text stays inside the fixed column width; version, variant and install path go in the
        /// tooltip.
        /// </summary>
        private static void SetRowStatus(ListRow row, PackEntry pack, InstalledEntry installed, bool hasUpdate)
        {
            if (installed == null)
            {
                row.SetStatus("Not installed", Theme.FgDim, pack.Name + " is not installed in this project.");
                return;
            }

            var variant = string.IsNullOrEmpty(installed.Variant) ? string.Empty : "  variant: " + installed.Variant;
            if (hasUpdate)
            {
                row.SetStatus(
                    "Update",
                    Theme.Progress,
                    "Installed v" + installed.InstalledVersion + "; v" + pack.Version + " is published - Install updates it." + variant + "\n" + installed.InstallPath);
                return;
            }

            row.SetStatus(
                "Installed",
                Theme.Fg,
                "Installed v" + installed.InstalledVersion + "." + variant + "\n" + installed.InstallPath);
        }

        /// <summary>
        /// Pins the asset list scroll to the top of the new scroll range.
        ///
        /// The engine does not clear the panel's view offset when content is disposed while
        /// scrolled: Panel.UpdateScrollBars keeps the bar "enabled" while the controls bounds
        /// top is negative, so its reset-on-enable flip never fires. The ViewOffset setter's
        /// internal PerformLayout is also a no-op (the setter locks the panel first), which
        /// leaves the children placed at the old offset and the bar range derived from the
        /// stale negative bounds - value 0 lands mid-track, exactly the "starts in the middle"
        /// state. Zeroing the offset and then forcing a full re-layout re-arranges the children
        /// at the top and recomputes the scroll range from un-scrolled bounds; the final
        /// TargetValue write applies immediately (no smoothing) and cancels any in-flight
        /// scroll animation.
        /// </summary>
        private void ResetAssetListScroll()
        {
            if (_assetScroll == null)
                return;

            _assetScroll.ViewOffset = Float2.Zero;
            _assetScroll.PerformLayout(true);

            var bar = _assetScroll.VScrollBar;
            if (bar != null)
                bar.TargetValue = bar.Minimum;
        }

        /// <summary>
        /// Single source of truth for the two pack actions, used by selection, by the end of an install
        /// and by the end of a remove. Labels follow the install record: Install (never installed),
        /// Update (a newer version is published), Reinstall (installed, current). Remove exists only
        /// while there is something installed. This is the same for asset, model, script, archive and
        /// plugin packs - the record, not the pack type, decides.
        ///
        /// `busy` disables both actions during an in-flight operation so a second click cannot start a
        /// concurrent install/uninstall against the same pack.
        /// </summary>
        private void RefreshActionButtons(PackEntry pack, InstalledEntry installed, bool hasUpdate, bool busy)
        {
            _installButton.Visible = pack != null;
            _installButton.Enabled = pack != null && !busy;
            _installButton.Text = pack == null ? "Install" : hasUpdate ? "Update" : installed == null ? "Install" : "Reinstall";
            _installButton.TooltipText = pack != null && InstallWouldCreateMaterial(pack)
                ? "Downloads the pack's files, imports them, and creates a PBR material from its textures."
                : "Downloads the pack's files and imports them into your project.";

            _removeButton.Visible = installed != null;
            _removeButton.Enabled = installed != null && !busy;
        }

        /// <summary>
        /// Refresh for the currently selected pack, reading its install record from settings. Used where
        /// the caller has no record at hand (the tail of an install, a variant change).
        /// </summary>
        private void RefreshActionButtons(bool busy = false)
        {
            var pack = _selectedPack;
            if (pack == null)
            {
                RefreshActionButtons(null, null, false, busy);
                return;
            }

            var sourceId = _selectedSource?.Manifest?.SourceId ?? "unknown";
            var installed = _settings.FindInstalled(sourceId, pack.Id);
            RefreshActionButtons(pack, installed, _settings.HasUpdate(pack, sourceId), busy);
        }

        /// <summary>
        /// True when installing this pack (at the selected variant) also creates a PBR material, i.e. an
        /// asset pack carrying textures with detectable roles. Drives the button tooltip only; the install
        /// handler recomputes the same condition from the pack, so the two can never disagree.
        /// </summary>
        private bool InstallWouldCreateMaterial(PackEntry pack)
        {
            return pack.PackType == PackType.AssetPack && HasMaterialTextures(pack.GetEffectiveFiles(GetSelectedVariantId()));
        }

        private void SelectPack(PackEntry pack, ListRow row)
        {
            _selectedPack = pack;
            _selectedIndexEntry = null;
            _installButton.Visible = true;
            _addLinkedSourceButton.Visible = false;
            ShowWebsiteLink(string.IsNullOrWhiteSpace(_selectedSource?.Manifest?.Website) ? _selectedSource?.Manifest?.HomepageUrl : _selectedSource?.Manifest?.Website);

            // Highlight
            if (_selectedRow != null)
                _selectedRow.Selected = false;
            _selectedRow = row;
            if (_selectedRow != null)
                _selectedRow.Selected = true;

            var sourceId = _selectedSource?.Manifest?.SourceId ?? "unknown";
            var installed = _settings.FindInstalled(sourceId, pack.Id);
            var hasUpdate = _settings.HasUpdate(pack, sourceId);

            _detailsTitle.Text = pack.Name;
            _metaRowId.Set("ID", pack.Id);
            _metaRowVersion.Set("Version", pack.Version);
            _metaRowType.Set("Type", pack.PackType.ToString());
            _metaRowCategory.Set("Category", string.IsNullOrWhiteSpace(pack.Category) ? "—" : pack.Category);
            _metaRowLicense.Set("License", string.IsNullOrWhiteSpace(pack.License) ? "—" : pack.License);
            ShowCredit(CreditLine.Build(pack, _selectedSource?.Manifest));
            _metaRowSource.Set("From", SafeHost(_selectedSource?.Url));

            // Variant dropdown: rebuild items, restore the last selected variant (persisted per
            // source/pack), falling back to the installed variant, then to the default set.
            bool hasBase = pack.Files != null && pack.Files.Count > 0;
            _updatingVariantCombo = true;
            _variantCombo.ClearItems();
            if (hasBase)
                _variantCombo.AddItem("Default");
            if (pack.Variants != null)
                foreach (var v in pack.Variants)
                    _variantCombo.AddItem(v.Name);
            int selIdx = -1;
            int FindVariantIndex(string vid)
            {
                if (pack.Variants == null || vid == null) return -1;
                for (int i = 0; i < pack.Variants.Count; i++)
                    if (string.Equals(pack.Variants[i].Id, vid, StringComparison.Ordinal))
                        return hasBase ? i + 1 : i;
                return -1;
            }
            if (_settings.Settings.SelectedVariants.TryGetValue(sourceId + "/" + pack.Id, out var storedVariant))
                selIdx = FindVariantIndex(storedVariant);
            if (selIdx < 0 && installed != null && !string.IsNullOrEmpty(installed.Variant))
                selIdx = FindVariantIndex(installed.Variant);
            if (selIdx < 0)
                selIdx = 0;
            _variantCombo.SelectedIndex = selIdx;
            _updatingVariantCombo = false;
            _variantRow.Visible = pack.PackType == PackType.AssetPack && pack.Variants != null && pack.Variants.Count > 0;

            UpdateDetailsFileListing();

            if (installed != null)
            {
                string variantSuffix = null;
                if (!string.IsNullOrEmpty(installed.Variant) && pack.Variants != null)
                {
                    var v = pack.Variants.Find(x => string.Equals(x.Id, installed.Variant, StringComparison.Ordinal));
                    variantSuffix = "  (variant: " + (v != null ? v.Name : installed.Variant) + ")";
                }
                _detailsStatus.Text = (hasUpdate ? $"Installed v{installed.InstalledVersion}  →  Update to v{pack.Version} available." : $"Installed v{installed.InstalledVersion}  at  {installed.InstallPath}") + (variantSuffix ?? "");
                _detailsStatus.TextColor = hasUpdate ? Theme.Progress : Theme.Fg;

                // Switching variant on an installed pack is not a no-op: the selected set is written into
                // its own subfolder of files/, and installing removes the set installed before it. Say which
                // is which, because the line above describes the *installed* variant, not the selection.
                var selectedVariant = GetSelectedVariantId();
                if (!string.Equals(selectedVariant ?? string.Empty, installed.Variant ?? string.Empty, StringComparison.Ordinal))
                {
                    string Label(string vid) => string.IsNullOrEmpty(vid)
                        ? "default"
                        : pack.Variants?.Find(x => string.Equals(x.Id, vid, StringComparison.Ordinal))?.Name ?? vid;
                    _detailsStatus.Text += $"  Selected variant: {Label(selectedVariant)} (installed: {Label(installed.Variant)}) — installing replaces the installed set.";
                    _detailsStatus.TextColor = Theme.Progress;
                }
            }
            else
            {
                _detailsStatus.Text = "Not installed.";
                _detailsStatus.TextColor = Theme.FgDim;
            }

            // Trust affordance: a pack that delivers code says so in the status line, so the install
            // button is never the first place a user learns that C# gets compiled into their project.
            if (PackInstallsCode(pack, GetSelectedVariantId(), out var codeHeadline, out _))
            {
                _detailsStatus.Text = _detailsStatus.Text + "  Installs code: " + codeHeadline;
                _detailsStatus.TextColor = Theme.Progress;
            }

            // Role-coverage affordance (D5): MaterialRoleDetector knows BaseColor/Normal/Metallic/
            // Roughness/AO/Emissive/Height/Opacity (+ combined ARM). Gloss, specular, cavity, curvature
            // and ID maps parse to role None and are skipped by the graph builder. That is a deliberate
            // limit (gloss -> roughness needs a 1-x node; cavity -> AO needs a multiply), so the UI says
            // plainly how many of the pack's textures the material will ignore, rather than letting a
            // complete-looking material imply that every downloaded file is in it.
            var unusedCount = CountUnusedMaterialTextures(pack, GetSelectedVariantId());
            if (unusedCount > 0)
            {
                _detailsStatus.Text = _detailsStatus.Text + $"  {unusedCount} texture(s) unused by the material (gloss/specular/cavity/curvature/ID maps are not wired).";
                _detailsStatus.TextColor = Theme.Progress;
            }

            // One install action, one remove action, for every pack type. Their labels and enabled state
            // come from the install record, not from the pack type, so a model pack, a script pack, an
            // archive pack and a plugin project all behave the same way here.
            RefreshActionButtons(pack, installed, hasUpdate, false);

            // Audio preview: show the button when the effective file set has an audio file,
            // and stop any preview that belongs to a different pack.
            var effectiveFiles = pack.PackType == PackType.AssetPack ? pack.GetEffectiveFiles(GetSelectedVariantId()) : null;
            bool hasAudio = effectiveFiles != null && effectiveFiles.Any(f => f.Type == FileAssetType.Audio);
            _previewButton.Visible = hasAudio;
            _previewButton.Enabled = hasAudio;
            if (_previewingPackId != pack.Id)
                StopPreview();
            else if (_previewSource != null)
                _previewButton.Text = _previewSource.State == AudioSource.States.Playing ? "Stop" : "Preview";

            LoadPreviewImagesAsync(pack);
            _detailsPanel.PerformLayout();
            _detailsScroll.PerformLayout();
        }

        // Overload for legacy callers
        private void SelectPack(PackEntry pack) => SelectPack(pack, null);

        /// <summary>
        /// The variant id selected in the variant dropdown, or null for the default file set
        /// (base files when present, else the first variant).
        /// </summary>
        private string GetSelectedVariantId()
        {
            if (_variantRow.Visible == false || _selectedPack?.Variants == null || _selectedPack.Variants.Count == 0) return null;
            bool hasBase = _selectedPack.Files != null && _selectedPack.Files.Count > 0;
            int idx = _variantCombo.SelectedIndex;
            if (hasBase)
            {
                if (idx <= 0) return null;   // "Default" item
                idx--;
            }
            return idx < _selectedPack.Variants.Count ? _selectedPack.Variants[idx].Id : null;
        }

        private void OnVariantChanged()
        {
            if (_updatingVariantCombo || _selectedPack == null) return;
            var key = (_selectedSource?.Manifest?.SourceId ?? "unknown") + "/" + _selectedPack.Id;
            var vid = GetSelectedVariantId();
            if (vid == null) _settings.Settings.SelectedVariants.Remove(key);
            else _settings.Settings.SelectedVariants[key] = vid;
            _settings.SaveSettings();
            // Refresh the file listing and the install button (the tooltip follows the new effective files,
            // and a variant with no PBR roles installs files only).
            UpdateDetailsFileListing();
            RefreshActionButtons();
        }

        /// <summary>
        /// Rebuilds the detail info text: description, the effective file listing for the
        /// selected variant (or the git info for pluginProject).
        /// </summary>
        private void UpdateDetailsFileListing()
        {
            if (_selectedPack == null) return;
            var pack = _selectedPack;
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(pack.Description))
            {
                sb.AppendLine(pack.Description);
                sb.AppendLine();
            }
            var files = pack.PackType == PackType.AssetPack ? pack.GetEffectiveFiles(GetSelectedVariantId()) : null;
            if (pack.PackType == PackType.AssetPack && files != null)
            {
                sb.AppendLine($"Files ({files.Count}):");
                foreach (var f in files.Take(18))
                    sb.AppendLine($"  [{f.Type}]  {f.Path}");
                if (files.Count > 18)
                    sb.AppendLine($"  … and {files.Count - 18} more");
                sb.AppendLine();
                // The install target names the folder the selected variant's files land in, so the path the
                // panel shows is the path the installer writes, and switching variant visibly moves it.
                var variantSub = AssetInstallerService.VariantSubFolder(GetSelectedVariantId());
                var filesFolder = AssetInstallerService.FilesSubFolder + (variantSub.Length > 0 ? "/" + variantSub : "/");
                sb.AppendLine($"Installs to:  Source/… or Content/… under");
                sb.AppendLine($"  {_settings.Settings.ScriptDestination}/<source>/<pack>/{filesFolder}  (Scripts)");
                sb.AppendLine($"  {_settings.Settings.ContentDestination}/<source>/<pack>/{filesFolder}  (assets)");
                if (variantSub.Length > 0)
                    sb.AppendLine($"  Variant sets keep their own subfolder of {AssetInstallerService.FilesSubFolder}/: installing this one removes the files of the variant installed before it.");
            }
            else if (pack.PackType == PackType.PluginProject && pack.Distribution != null)
            {
                sb.AppendLine($"Git: {pack.Distribution.Url}");
                if (!string.IsNullOrWhiteSpace(pack.Distribution.Branch)) sb.AppendLine($"Branch: {pack.Distribution.Branch}");
                if (!string.IsNullOrWhiteSpace(pack.Distribution.Commit)) sb.AppendLine($"Commit: {pack.Distribution.Commit}");
                sb.AppendLine();
                sb.AppendLine("Installs to: Plugins/<Name>/  (restart editor to load)");
            }
            else if (pack.PackType == PackType.ArchivePack && pack.Distribution != null)
            {
                sb.AppendLine($"Zip: {pack.Distribution.Url}");
                sb.AppendLine();
                sb.AppendLine($"Extracts to:  {_settings.Settings.ContentDestination}/<source>/<pack>/files/  (archive layout preserved)");
            }
            _detailsInfo.Text = sb.ToString();
            _detailsInfo.AutoHeight = true;
        }

        private async void LoadPreviewImagesAsync(PackEntry pack)
        {
            if (_previewHttpClient == null || pack.PackType == PackType.PluginProject)
            {
                HidePreview();
                return;
            }

            // Build the texture inventory: list each texture by name and the PBR role it is
            // identified as (MaterialRoleDetector). Independent of previewUrl so the list stays
            // visible even when a pack has no preview image.
            var textureFiles = pack.Files?.Where(f => f.Type == FileAssetType.Texture).ToList();
            var hasTextures = textureFiles != null && textureFiles.Count > 0;
            var list = new System.Text.StringBuilder();
            if (hasTextures)
            {
                foreach (var f in textureFiles)
                {
                    var roleLabel = FormatMaterialRole(MaterialRoleDetector.Detect(f.Path));
                    list.Append("  ");
                    list.Append(Path.GetFileName(f.Path));
                    if (roleLabel.Length > 0)
                        list.Append(" - " + roleLabel);
                    list.AppendLine();
                }
            }
            _fileListLabel.Text = hasTextures ? list.ToString() : string.Empty;
            _texturesHeader.Text = hasTextures ? $"Textures ({textureFiles.Count})" : "";
            // AutoHeight is not implemented in the engine Label — size it explicitly (14px per caption line).
            _fileListLabel.Height = hasTextures ? textureFiles.Count * 14f : 0;

            // Main image: the previewUrl only (no texture fallback, no thumbnails).
            var hasPreview = !string.IsNullOrEmpty(pack.PreviewUrl);
            if (hasPreview)
            {
                // Replace any previous preview control: release its texture and detach the old
                // control so repeatedly selecting packs does not leak ImageView instances.
                if (_mainPreview != null)
                {
                    _mainPreview.DisposeTexture();
                    _mainPreview.Dispose();
                }
                _mainPreview = new ImageView
                {
                    // Centered fixed-size box: TopCenter anchor → offsets.Left is the shift from
                    // center, offsets.Right/Bottom are the width/height (Control.Bounds.cs:482-502).
                    AnchorPreset = AnchorPresets.TopCenter,
                    Offsets = new Margin(-Theme.PreviewHeight * 0.5f, Theme.PreviewHeight, PreviewImageTop, Theme.PreviewHeight),
                    Parent = _previewPanel,
                    ScaleMode = ImageScaleMode.AspectFit,
                };
                _mainPreview.Visible = true;
            }
            else if (_mainPreview != null)
            {
                _mainPreview.Texture = null;
                _mainPreview.DisposeTexture();
                _mainPreview.Visible = false;
            }

            // Nothing to show: collapse the panel.
            if (!hasPreview && !hasTextures)
            {
                HidePreview();
                return;
            }

            // Seat the sections: header (0-20), image (PreviewImageTop..PreviewImageTop+PreviewHeight), textures header, list.
            float imageBottom = PreviewImageTop + Theme.PreviewHeight;
            if (hasPreview)
            {
                _previewHeader.Visible = true;
                _texturesHeader.Offsets = new Margin(0, imageBottom, 0, 0);
                _texturesHeader.Visible = hasTextures;
                _fileListLabel.Offsets = new Margin(0, imageBottom + 24, 0, 0);
                _previewPanel.Height = hasTextures ? imageBottom + 24 + _fileListLabel.Height + 8 : imageBottom + 8;
            }
            else // textures only
            {
                _previewHeader.Visible = false;
                _texturesHeader.Offsets = new Margin(0, 0, 0, 0);
                _texturesHeader.Visible = true;
                _fileListLabel.Offsets = new Margin(0, 20, 0, 0);
                _previewPanel.Height = 20 + _fileListLabel.Height;
            }

            _previewPanel.Visible = true;
            _previewPanel.UnlockChildrenRecursive();
            _previewPanel.PerformLayout();
            _detailsPanel.PerformLayout();
            _detailsScroll.PerformLayout();

            // Download and show the preview image.
            if (hasPreview)
            {
                try
                {
                    // Some asset CDNs serve optimised WEBP for preview URLs, inconsistently per
                    // asset, and the engine's stb_image has no WEBP decoder - so those previews
                    // fail to load. Appending the CDN's format=png transformation asks for a
                    // decodable image; CDNs that do not know the parameter ignore it.
                    var data = await HttpStream.DownloadBytesAsync(_previewHttpClient, ResolvePreviewUrl(pack.PreviewUrl), HttpStream.MaxPreviewBytes);
                    // Last resort: if the CDN still returns WEBP, try the parameter-less URL.
                    if (IsWebp(data) && pack.PreviewUrl.IndexOf('?') >= 0)
                    {
                        var baseUri = pack.PreviewUrl.Substring(0, pack.PreviewUrl.IndexOf('?'));
                        data = await HttpStream.DownloadBytesAsync(_previewHttpClient, baseUri, HttpStream.MaxPreviewBytes);
                    }

                    // The awaits resume on a thread-pool thread and _mainPreview is a GUI control, so
                    // the bytes are handed to the image on the main thread (a selection made while the
                    // download ran owns the panel by then).
                    var image = data;
                    await MainThreadDispatcher.RunOnMain(() =>
                    {
                        if (IsDisposing || _mainPreview == null || !_mainPreview.Visible)
                            return;
                        if (image != null && image.Length > 0)
                            _mainPreview.LoadFromBytes(image);
                    });
                }
                catch { /* ignore load failures */ }
            }
        }
        /// <summary>
        /// Appends the CDN image-optimisation parameter that forces a decodable PNG. Some CDNs serve
        /// optimised WEBP (undecodable by the engine's stb_image) for preview URLs; the parameter is
        /// ignored harmlessly by CDNs that do not recognise it.
        /// </summary>
        private static string ResolvePreviewUrl(string previewUrl)
        {
            if (string.IsNullOrEmpty(previewUrl)) return previewUrl;
            var sep = previewUrl.IndexOf('?') >= 0 ? '&' : '?';
            return previewUrl + sep + "format=png";
        }

        /// <summary>
        /// True when the bytes begin with the WEBP "RIFF…WEBP" container signature. The engine
        /// decodes images with stb_image, which has no WEBP decoder, so previews served as WEBP
        /// fail to load and show "Failed to load image.".
        /// </summary>
        private static bool IsWebp(byte[] data)
        {
            return data != null && data.Length >= 12 &&
                data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
                data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P';
        }

        /// <summary>
        /// Detects packs whose install puts executable code into the project: a pluginProject clones a
        /// third-party repo into Plugins/ and adds it to the .flaxproj, and Script-typed files land in
        /// the script destination, where the engine compiles them. Both run with your own permissions,
        /// and a manifest is remote input that the plugin cannot judge - so the UI states it plainly and
        /// asks before installing. headline is a one-liner for the details panel, detail the full
        /// explanation for the confirmation dialog.
        /// </summary>
        private bool PackInstallsCode(PackEntry pack, string variantId, out string headline, out string detail)
        {
            headline = null;
            detail = null;
            if (pack.PackType == PackType.PluginProject)
            {
                var dist = pack.Distribution;
                string what;
                if (!string.IsNullOrEmpty(dist?.Commit))
                    what = "commit " + dist.Commit.Substring(0, Math.Min(10, dist.Commit.Length));
                else if (!string.IsNullOrEmpty(dist?.Branch))
                    what = dist.Branch;
                else
                    what = "default branch";
                headline = "clones a third-party git repo into Plugins/ and adds it to your project";
                detail = $"It clones {SafeHost(dist?.Url)} ({what}) into your project's Plugins/ folder, adds it to your .flaxproj, and the engine compiles that repository's C# code into your project. The clone is verified in a staging folder before anything is installed, and a Plugins/ folder this manager did not install is never replaced.";
                return true;
            }
            // archivePack is deliberately not a code route: its entries carry no manifest type, so every
            // entry name is checked before extraction and a script/executable/native-library entry fails
            // the install (AssetSourceManager.CheckArchiveEntryName). Nothing left here to confirm.
            if (pack.PackType != PackType.AssetPack)
            if (pack.PackType != PackType.AssetPack)
                return false;
            var scripts = pack.GetEffectiveFiles(variantId)?.Where(f => f.Type == FileAssetType.Script).ToList();
            if (scripts == null || scripts.Count == 0)
                return false;
            headline = $"writes {scripts.Count} C# script file(s) into {_settings.Settings.ScriptDestination}";
            var listing = scripts.Take(5).Select(f => "  " + f.Path).ToList();
            if (scripts.Count > listing.Count)
                listing.Add($"  ... {scripts.Count - listing.Count} more");
            detail = $"It writes {scripts.Count} C# script file(s) from {SafeHost(scripts[0].Url)} into {_settings.Settings.ScriptDestination}, and the engine compiles them into your project:\n{string.Join("\n", listing)}";
            return true;
        }

        /// <summary>
        /// Host of a manifest-supplied URL, for display only. The full URL is not shown: manifests and
        /// clones can carry tokens in the path or query string, and the host is what identifies the source.
        /// </summary>
        private static string SafeHost(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
                return u.Host;
            return "the manifest source";
        }

        /// <summary>
        /// Blocks installing a code-delivering pack until the user says yes, naming the host and what
        /// lands where. Content-only packs install with no prompt.
        /// </summary>
        private bool ConfirmCodeInstall(PackEntry pack, string variantId)
        {
            if (!PackInstallsCode(pack, variantId, out _, out var detail))
                return true;
            return MessageBox.Show(
                $"{pack.Name} v{pack.Version} installs code, not just content.\n\n{detail}\n\n" +
                "Only continue if you trust this source. Install anyway?",
                "This pack installs code", MessageBoxButtons.YesNo) == DialogResult.Yes;
        }
        private async void OnInstallClicked()
        {
            if (_selectedPack == null || _selectedSource == null)
            {
                MessageBox.Show("Select a pack first.", "No selection", MessageBoxButtons.OK);
                return;
            }
            // A pack that delivers code needs an explicit yes - see ConfirmCodeInstall.
            if (!ConfirmCodeInstall(_selectedPack, GetSelectedVariantId()))
                return;
            var pack = _selectedPack;
            var row = _selectedRow;
            var source = _selectedSource;
            var variantId = GetSelectedVariantId();

            // Whether a material is created is the pack's business, not the user's: an asset pack whose
            // textures have detectable PBR roles is installed *with* its material, because a texture pack
            // without a material is just a folder of images. Every other pack type installs as before.
            bool withMaterial = pack.PackType == PackType.AssetPack && HasMaterialTextures(pack.GetEffectiveFiles(variantId));

            // One install at a time. The material flow keeps its own token (both are cancelled on close),
            // so closing the window never leaves a material build touching engine APIs.
            _installCts?.Cancel();
            var cts = withMaterial ? (_installMaterialCts = new CancellationTokenSource()) : (_installCts = new CancellationTokenSource());
            var ct = cts.Token;
            RefreshActionButtons(busy: true);   // both actions off while this runs
            if (_statusBar != null)
            {
                _statusBar.SetBusy(withMaterial ? $"Installing {pack.Name} + creating material..." : $"Installing {pack.Name}...");
                _statusBar.SetProgress(0);
            }

            var progress = new WindowProgress(this);
            bool okInstall = false, okMaterial = false;
            string error = null;
            try
            {
                okInstall = await _installer.InstallAsync(pack, source.Manifest.SourceId, source.Url, progress, ct, variantId, source.Manifest);
                // Only the texture-pack shape calls the material service: CreateMaterial imports the
                // textures and logs a warning when nothing resolved, so running it for a model, script
                // or archive pack would be wasted work plus a misleading log line.
                if (okInstall && withMaterial)
                    okMaterial = await _materialService.CreateMaterial(source.Manifest.SourceId, pack, progress, ct, variantId);
            }
            catch (Exception e)
            {
                error = e.Message;
                Debug.LogError($"FlaxAssetManager: Install error: {e.Message}");
            }

            // InstallAsync resumes on a thread-pool thread (the engine sets no SynchronizationContext).
            // Everything below is editor GUI state, so hop back to the main thread and do it in one go.
            await MainThreadDispatcher.RunOnMain(() =>
            {
                if (IsDisposing)
                    return;
                RefreshActionButtons();
                if (_statusBar != null)
                {
                    _statusBar.SetIdle();
                    if (error != null)
                        _statusBar.SetStatus($"Error: {error}");
                    else if (okInstall && okMaterial)
                        _statusBar.SetStatus($"Installed {pack.Name} v{pack.Version} and created material.");
                    else if (okInstall && withMaterial)
                        _statusBar.SetStatus($"Installed {pack.Name} v{pack.Version}, but no material was created (no PBR role resolved).");
                    else if (okInstall)
                        _statusBar.SetStatus($"Installed {pack.Name} v{pack.Version}  ·  license: {pack.License ?? "not declared by the source"}");
                    else
                        _statusBar.SetStatus($"Install failed for {pack.Name}");
                }
                if (okInstall)
                {
                    SelectPack(pack, row);
                    RebuildAssetList();
                    // The obligations of an install outlive this window: ATTRIBUTIONS.md is written from
                    // the install record, not from the session, so say once where that list is.
                    Debug.Log($"FlaxAssetManager: '{pack.Name}' installed under license {pack.License ?? "not declared by the source"}; the project's credits list is {_settings.AttributionsPath}");
                    // Selection (and the "Installed" chip) is reapplied by RebuildAssetList.
                }
            });
        }

        /// <summary>
        /// True if the file set contains MaterialTextures files whose role can be detected from
        /// the file name (so a material can be created from them).
        /// </summary>
        private static bool HasMaterialTextures(List<FileEntry> files)
        {
            if (files == null) return false;
            foreach (var f in files)
            {
                if (f.Type == FileAssetType.MaterialTextures && MaterialRoleDetector.Detect(f.Path) != MaterialRole.None)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// How many of the pack's MaterialTextures the generated material will not use, i.e. role None
        /// from the file name (gloss/specular/cavity/curvature/ID maps). Shown in the details status so
        /// the material's completeness is stated, not implied. Returns 0 for non-asset packs.
        /// </summary>
        private static int CountUnusedMaterialTextures(PackEntry pack, string variantId)
        {
            if (pack == null || pack.PackType != PackType.AssetPack)
                return 0;

            var files = pack.GetEffectiveFiles(variantId);
            if (files == null)
                return 0;

            int unused = 0;
            foreach (var f in files)
            {
                if (f.Type != FileAssetType.MaterialTextures)
                    continue;
                if (MaterialRoleDetector.Detect(f.Path) == MaterialRole.None)
                    unused++;
            }
            return unused;
        }
        /// <summary>
        /// Collapses the preview image panel (main preview + file list).
        /// </summary>
        private void HidePreview()
        {
            _previewPanel.Visible = false;
            _previewPanel.Height = 0;
        }

        /// <summary>
        /// Toggles live playback of the selected pack's first audio file. The file is
        /// downloaded from its source URL into the project content destination (a file
        /// outside the content root would be invisible to the asset registry and thus
        /// unresolvable by Content.LoadAsync) and played through a throwaway AudioSource
        /// in a preview scene (the engine has no HTTP audio streaming API, so the clip
        /// is loaded from the downloaded file as an AudioClip asset).
        /// </summary>
        private async void OnPreviewClicked()
        {
            var pack = _selectedPack;
            if (pack == null)
            {
                Debug.Log("FlaxAssetManager: Preview clicked, but no pack is selected.");
                return;
            }

            // Clicking the button again stops the in-flight preview of the same pack.
            if (_previewingPackId == pack.Id && _previewSource != null)
            {
                StopPreview();
                return;
            }

            var files = pack.GetEffectiveFiles(GetSelectedVariantId());
            var file = files?.FirstOrDefault(f => f.Type == FileAssetType.Audio);
            if (file == null)
            {
                Debug.Log($"FlaxAssetManager: Preview: no audio file in pack '{pack.Id}' ({(files == null ? "no files" : files.Count + " files")}).");
                return;
            }
            Debug.Log($"FlaxAssetManager: Preview: downloading audio '{file.Path}' from {file.Url}");

            StopPreview();
            _previewCts?.Cancel();
            _previewCts = new CancellationTokenSource();
            var ct = _previewCts.Token;
            _previewButton.Enabled = false;
            if (_statusBar != null)
                _statusBar.SetBusy($"Downloading preview: {Path.GetFileName(file.Path)}...");

            // The engine's audio importer picks the decoder by file extension, so keep the
            // original one.
            var ext = Path.GetExtension(file.Path);
            if (string.IsNullOrEmpty(ext))
                ext = Path.GetExtension(file.Url);
            if (string.IsNullOrEmpty(ext))
                ext = ".wav";
            // Download inside the project content destination (not /tmp): Content.LoadAsync
            // resolves a path through the asset registry (AssetsCache), which only knows files
            // discovered under the content root — a /tmp file is invisible to it and yields null.
            var previewsDir = Path.Combine(_settings.ContentDestinationFullPath, ".previews");
            Directory.CreateDirectory(previewsDir);
            var tempPath = Path.Combine(previewsDir, $"flaxam_preview_{Guid.NewGuid():N}{ext}");

            bool cancelled = false;
            string downloadError = null;
            try
            {
                var data = await HttpStream.DownloadBytesAsync(_audioHttpClient, file.Url, HttpStream.MaxPreviewBytes, ct);
                await File.WriteAllBytesAsync(tempPath, data, ct);
            }
            catch (OperationCanceledException)
            {
                // The preview was stopped (pack changed / source switched / window closed).
                cancelled = true;
            }
            catch (Exception e)
            {
                downloadError = e.Message;
            }

            // The download is the only part that belongs on a worker thread: the content refresh, the
            // asset-registry lookup behind Content.LoadAsync and the UI updates are main-thread state.
            AudioClip clip = null;
            bool aborted = false;
            await MainThreadDispatcher.RunOnMain(() =>
            {
                if (IsDisposing)
                {
                    DeletePreviewTempFile(tempPath);
                    aborted = true;
                    return;
                }
                if (cancelled)
                {
                    DeletePreviewTempFile(tempPath);
                    if (_previewButton != null)
                        _previewButton.Text = "Preview";
                    if (_statusBar != null)
                        _statusBar.SetIdle();
                    aborted = true;
                    return;
                }
                if (downloadError != null)
                {
                    DeletePreviewTempFile(tempPath);
                    if (_previewButton != null)
                    {
                        _previewButton.Enabled = true;
                        _previewButton.Text = "Preview";
                    }
                    if (_statusBar != null)
                    {
                        _statusBar.SetIdle();
                        _statusBar.SetStatus($"Preview failed: {downloadError}");
                    }
                    aborted = true;
                    return;
                }

                _previewTempFile = tempPath;
                // Make the content database (and the asset registry behind Content.LoadAsync)
                // aware of the freshly written file, like the install flow does for new packs.
                ContentDatabaseUtils.RefreshContentFolder(previewsDir);
                if (ct.IsCancellationRequested)
                {
                    DeletePreviewTempFile(tempPath);
                    if (_previewButton != null)
                        _previewButton.Enabled = true;
                    aborted = true;
                    return;
                }

                // Load the downloaded file as an AudioClip (the editor imports it on demand).
                clip = FlaxEngine.Content.LoadAsync<AudioClip>(tempPath);
            });
            if (aborted || IsDisposing)
                return;

            bool loaded = false;
            try
            {
                // The blocking asset wait stays off the main thread on purpose (a 60 s import would
                // freeze the editor); StopPreview / OnDestroy cancel the token.
                loaded = clip != null && await Task.Run(() => clip.WaitForLoaded(60000.0), ct);
            }
            catch (OperationCanceledException)
            {
                // Preview was stopped while the clip was loading; StopPreview already reset UI.
                await MainThreadDispatcher.RunOnMain(() =>
                {
                    if (!IsDisposing)
                        DeletePreviewTempFile(tempPath);
                });
                return;
            }
            Debug.Log($"FlaxAssetManager: Preview: clip loaded: {loaded}, clip is null: {clip == null}, IsLoaded: {clip?.IsLoaded ?? false}");

            if (!loaded)
            {
                await MainThreadDispatcher.RunOnMain(() =>
                {
                    if (IsDisposing)
                        return;
                    DeletePreviewTempFile(tempPath);
                    if (_previewButton != null)
                    {
                        _previewButton.Enabled = true;
                        _previewButton.Text = "Preview";
                    }
                    if (_statusBar != null)
                    {
                        _statusBar.SetIdle();
                        _statusBar.SetStatus($"Preview failed: could not load {Path.GetFileName(file.Path)} as audio.");
                    }
                });
                return;
            }

            // The preview scene graph is engine state (OnUpdate ticks _previewScene on the main
            // thread), so the scene, the source and Play() all run there.
            await MainThreadDispatcher.RunOnMain(() =>
            {
                if (IsDisposing)
                    return;
                _previewTempFile = tempPath;
                _previewingPackId = pack.Id;
                _previewScene = new EditorScene();
                _previewSource = new AudioSource
                {
                    Parent = _previewScene,
                    AllowSpatialization = false,
                    Clip = clip,
                };
                _previewSource.ClipFinished += OnPreviewClipFinished;
                _previewSource.Play();
                Debug.Log($"FlaxAssetManager: Preview: Play() called. Source state: {_previewSource.State}, clip: {clip}");
                _previewButton.Enabled = true;
                _previewButton.Text = "Stop";
                if (_statusBar != null)
                {
                    _statusBar.SetIdle();
                    _statusBar.SetStatus($"Previewing {Path.GetFileName(file.Path)}...");
                }
            });
        }

        private void OnPreviewClipFinished()
        {
            StopPreview();
            if (_statusBar != null)
                _statusBar.SetStatus("Preview finished.");
        }

        /// <summary>
        /// Stops the live audio preview, disposes the preview source/scene and deletes the
        /// downloaded temp file.
        /// </summary>
        private void StopPreview()
        {
            if (_previewSource != null)
            {
                _previewSource.ClipFinished -= OnPreviewClipFinished;
                _previewSource.Stop();
                _previewSource.Clip = null;
                FlaxEngine.Object.Destroy(_previewSource);
                _previewSource = null;
            }
            if (_previewScene != null)
            {
                FlaxEngine.Object.Destroy(_previewScene);
                _previewScene = null;
            }
            DeletePreviewTempFile(_previewTempFile);
            _previewTempFile = null;
            _previewingPackId = null;
            _previewCts?.Cancel();
            _previewCts = null;
            if (_previewButton != null)
            {
                _previewButton.Text = "Preview";
                if (_selectedPack != null)
                    _previewButton.Enabled = _previewButton.Visible;
            }
        }

        private void DeletePreviewTempFile(string path)
        {
            if (path != null)
            {
                var dir = Path.GetDirectoryName(path);
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch { /* best-effort cleanup */ }
                // Drop the stale registry entry so a future load of the same path resolves cleanly.
                if (dir != null && dir.StartsWith(_settings.ContentDestinationFullPath, StringComparison.Ordinal))
                    ContentDatabaseUtils.RefreshContentFolder(dir);
            }
        }

        /// <summary>
        /// Human-readable PBR role name for the preview file list.
        /// </summary>
        private static string FormatMaterialRole(MaterialRole role)
        {
            switch (role)
            {
                case MaterialRole.BaseColor: return "Color / Diffuse";
                case MaterialRole.Normal: return "Normal";
                case MaterialRole.Metallic: return "Metallic";
                case MaterialRole.Roughness: return "Roughness";
                case MaterialRole.AmbientOcclusion: return "Ambient Occlusion";
                case MaterialRole.Emissive: return "Emissive";
                case MaterialRole.Height: return "Height / Displacement";
                case MaterialRole.Opacity: return "Opacity";
                case MaterialRole.Combined: return "AO / Roughness / Metallic";
                default: return string.Empty;
            }
        }

        private async void OnRemoveClicked()
        {
            if (_selectedPack == null || _selectedSource == null)
            {
                MessageBox.Show("Select a pack first.", "No selection", MessageBoxButtons.OK);
                return;
            }
            var sourceId = _selectedSource.Manifest.SourceId;
            var installed = _settings.FindInstalled(sourceId, _selectedPack.Id);
            if (installed == null)
            {
                Debug.LogWarning($"FlaxAssetManager: No installed entry for {_selectedPack.Id}");
                return;
            }

            var confirm = MessageBox.Show($"Remove \"{installed.PackName}\"?\nThis will delete all installed files and the plugin folder.\n\nPath: {installed.InstallPath}",
                "Remove pack", MessageBoxButtons.YesNo);
            if (confirm != DialogResult.Yes)
                return;

            RefreshActionButtons(busy: true);   // both actions off while this runs
            if (_statusBar != null)
            {
                _statusBar.SetBusy($"Removing {installed.PackName}...");
                _statusBar.SetProgress(0);
            }

            // Same single-operation pattern as install: closing the window cancels this token, so a
            // remove never keeps deleting files and touching engine assets after its window is gone.
            _installCts?.Cancel();
            _installCts = new CancellationTokenSource();
            var progress = new WindowProgress(this);

            bool ok = false;
            string error = null;
            try
            {
                ok = await _installer.UninstallAsync(installed, progress, _installCts.Token);
            }
            catch (Exception e)
            {
                error = e.Message;
                Debug.LogError($"FlaxAssetManager: Remove error: {e.Message}");
            }

            // UninstallAsync resumes on a thread-pool thread; the buttons, status bar and asset list
            // are editor GUI state, so the tail runs on the main thread.
            await MainThreadDispatcher.RunOnMain(() =>
            {
                if (IsDisposing)
                    return;
                // Re-read the record: on success the pack is gone, so Remove hides itself and the
                // install button goes back to "Install"; on failure both stay as they were.
                RefreshActionButtons();
                if (_statusBar != null)
                {
                    _statusBar.SetIdle();
                    if (error != null)
                        _statusBar.SetStatus($"Error: {error}");
                    else
                        _statusBar.SetStatus(ok ? $"Removed {installed.PackName}" : $"Remove failed for {installed.PackName}");
                }
                if (ok)
                {
                    SelectPack(_selectedPack, _selectedRow);
                    RebuildAssetList();
                }
            });
        }

        public override void OnDestroy()
        {
            // Cancel every in-flight flow — the material token was never cancelled, and none of the
            // five were released. Pending continuations check IsDisposing and skip their GUI updates.
            _refreshCts?.Cancel();
            _installCts?.Cancel();
            _installMaterialCts?.Cancel();
            _previewCts?.Cancel();
            _updateCts?.Cancel();
            StopPreview();
            _refreshCts?.Dispose();
            _installCts?.Dispose();
            _installMaterialCts?.Dispose();
            _previewCts?.Dispose();
            _updateCts?.Dispose();
            _audioHttpClient?.Dispose();
            _sourceManager.Fetched -= OnFetched;
            _previewHttpClient?.Dispose();
            _mainPreview?.DisposeTexture();
        }

        /// <summary>
        /// Arrow navigation for the asset list: moves focus to the neighbor row (which the
        /// row's Focused handler selects) exactly like Tab/Shift+Tab. No-op at the list ends
        /// or before anything is selected.
        /// </summary>
        private void OnRowArrow(bool down)
        {
            if (_selectedRow == null)
                return;

            // Position of the selected row within the currently displayed (filtered) list.
            // Arrow keys move focus within whichever list is showing (asset rows, or an index's linked
            // manifests); each row's Focused handler performs the selection.
            var rows = _listIsIndex
                ? _indexRowItems.Select(x => (ListRow)x.row).ToList()
                : _rowItems.Select(x => x.row).ToList();

            int index = -1;
            for (int i = 0; i < rows.Count; i++)
            {
                if (ReferenceEquals(rows[i], _selectedRow))
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
                return;

            int target = index + (down ? 1 : -1);
            if (target < 0 || target >= rows.Count)
                return;

            var next = rows[target];
            next.Focus();
            _assetScroll.ScrollViewTo(next);
        }
    }
}
