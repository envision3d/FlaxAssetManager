# AGENTS.md — FlaxAssetManager

> **Read this first if you are an LLM/agent iterating on this project.** `docs/MANIFEST_SPEC.md` is the canonical manifest schema, `PRD.md` is product intent (goals, use-cases, non-goals), this file is the fast onboard + the hard constraints you must not break, and `docs/DECISIONS.md` is the why.
>
> **Doc status:** all four track the code. They are split on purpose - `PRD.md` used to restate the schema, the file map and the build loop, and those sections rotted; it no longer states any of them. **Implementation truth is this file plus `docs/MANIFEST_SPEC.md`.** If a PRD sentence disagrees with them, fix the PRD, do not implement from it.

## What This Is

FlaxEngine **plugin project** at `Plugins/FlaxAssetManager/` inside any Flax game project (host-agnostic). Adds an **editor window** (`Window → Flax Asset Manager`) that lets users browse/install:

- **Plugin projects** — full Flax plugin projects as git repos → plain `git clone` → `Plugins/<Name>/` + `.flaxproj` reference injection.
- **Raw asset packs** — explicit `files[]` lists of raw assets (Texture, Material, Model, etc.) behind plain `https://` URLs (including `raw.githubusercontent.com` for files inside repos) → parallel download → `Content/` or `Source/`.

No auth, no zips, no `Other` fallback, no auto-poll. Manual `Refresh` only. Update detection via `Content/FlaxAssetManager/.installed.json`.

Housing: this folder is the plugin project itself. When installed, it lives at `$(ProjectPath)/Plugins/FlaxAssetManager/` inside the host game project and references `$(EnginePath)/Flax.flaxproj`.

## File Map You Care About

```
FlaxAssetManager.flaxproj                          # GameTarget FlaxAssetManagerTarget, EditorTarget FlaxAssetManagerEditorTarget
Source/FlaxAssetManager/FlaxAssetManager.cs        # GamePlugin sets _description in ctor (Plugin.h:22) — do NOT add override Description
Source/FlaxAssetManager/FlaxAssetManager.Build.cs  # GameModule BuildNativeCode=false + using Flax.Build.NativeCpp
Source/FlaxAssetManagerEditor/FlaxAssetManagerEditor.cs  # EditorPlugin GamePluginType => FlaxAssetManager, builds services + window + Window menu
Source/FlaxAssetManagerEditor/FlaxAssetManagerEditor.Build.cs # GameEditorModule + PublicDependencies.Add("FlaxAssetManager")
Source/FlaxAssetManagerTarget.Build.cs             # GameProjectTarget
Source/FlaxAssetManagerEditorTarget.Build.cs       # GameProjectEditorTarget
Source/FlaxAssetManagerEditor/Core/Models/
  PackType.cs, FileAssetType.cs, FileEntry.cs, PackEntry.cs (PackVariant nested here), AssetSourceManifest.cs (pack + index kinds), IndexEntry.cs (one linked manifest of an index), AssetSourceConfig.cs (FlaxAssetManagerSettings, InstalledManifest/InstalledEntry)
Source/FlaxAssetManagerEditor/Core/Services/
  SettingsService.cs        # owns Content/FlaxAssetManager/{Settings,Sources,.installed}.json
  AssetSourceManager.cs     # HttpClient 30s, ValidateManifest (STRICT) + ValidateIndex (index manifests)
  AssetInstallerService.cs  # SemaphoreSlim(6), SHA256, git clone into staging, zip extract (entry-name rule), reference injection; VariantSubFolder is the one layout authority (files/<variant>/ for a variant install), RemoveReplacedInstall + PruneStaleVariantFolders drop the set a variant switch replaces, PruneEmptyFolders prunes to the pack root; uninstall deletes files[] + generatedAssets + imported sidecars, then prunes the pack folder
Source/FlaxAssetManagerEditor/Core/Services/MaterialCreatorService.cs # PBR role detection + surface-graph rewiring + model slot assignment (stock API, engine calls marshalled to the main thread); records the created M_<packId>.flax on the install entry (RecordGeneratedAsset)
Source/FlaxAssetManagerEditor/Core/Services/PluginUpdateService.cs # self-update: git pull --ff-only in this plugin's own folder, then ScriptsBuilder.MarkWorkspaceDirty + Compile; refuses a dirty/detached/no-upstream checkout before writing; the folder is DISCOVERED by its project file (file name, or the Name inside it) — assembly ancestors, the open project's References, .installed.json records, a bounded Plugins/ scan, the documented location — never by an assumed folder name, and always contained inside Globals.ProjectFolder
Source/FlaxAssetManagerEditor/Core/Utils/MaterialGraphBuilder.cs # creates the material asset (Editor.CreateAsset) and builds its PBR surface graph (root-box wiring, multipliers, the normal-map decode chain, public parameters); ValidateRootInputBoxes checks the hard-coded box ids against the engine's own box metadata before wiring
  MaterialRoleDetector.cs # PBR role from file name (ARM aware)
  GitSafety.cs            # engine-free guards: https-only URL, git ref/commit validation, path containment (Ordinal on Linux)
  FlaxProjectLocator.cs   # engine-free *.flaxproj reader/finder: a project's Name field, identity by file name OR Name, $(ProjectPath)/$(EnginePath)/$(ProjectName) macro expansion of References, bounded BFS scan that skips Binaries/Cache/Source/…, ancestors walk from an assembly dir; case-correct path comparison
  GitRunner.cs            # shared git spawn: ArgumentList per argument, exit code + both streams returned as data, kills an abandoned/timed-out git (with its tree)
  MainThreadDispatcher.cs  # the plugin's one main-thread hop (wraps Scripting.RunOnUpdate) for post-await engine/GUI work
  ContentDatabaseUtils.cs # content-folder refresh + lookups shared by install/import/material
  SystemFileOpener.cs     # open Sources.json in the OS editor (detached, non-blocking); refuses paths outside the project and shell-executable extensions
Source/FlaxAssetManagerEditor/Core/Tests/VariantE2ETest.cs # live-editor E2E (manifest→install→material); run with FLAX_AM_E2E=1, hooked in FlaxAssetManagerEditor.cs:53
Source/FlaxAssetManagerEditor/Core/Tests/MaterialReadyE2ETest.cs # live-editor material-readiness E2E (installed pack → saved graph reaches the loaded material); run with FLAX_AM_MATERIAL_E2E=1, D17
Source/FlaxAssetManagerEditor/UI/FlaxAssetManagerWindow.cs # EditorWindow hideOnClose:true (~2.8k lines, pixel-positioned layout, previews + variants inline; index manifests via RebuildIndexList/SelectIndexEntry/OnAddLinkedSourceClicked). Action buttons are one Install + one Remove, all state from the record via RefreshActionButtons; install builds the material when HasMaterialTextures. The asset list is a **fill, not a rebuild**: pooled rows, one layout per refill, debounced search (D18, and constraint 18)
Source/FlaxAssetManagerEditor/UI/AssetManagerListRow.cs, UI/ImageView.cs # pack row template; async image loading
EKEditorUIKit/                       # vendored sibling plugin project (the FlatButton/TextLabel/Dropdown/panel toolkit the window uses)
Content/GameSettings.json           # required for the .flaxproj to open (only file the plugin ships under Content/)
# Settings.json / Sources.json / .installed.json are NOT in this repo: SettingsService resolves them
# against Globals.ProjectFolder (SettingsService.cs:35) = the HOST project's Content/FlaxAssetManager,
# alongside installed packs and the .previews cache. .gitignore keeps local state out of the plugin.
example_manifest.json       # every FileAssetType + archivePack demonstrated (13 packs)
PRD.md                      # product intent only (goals / use-cases / non-goals); no schema, no file map
# NOT in this repo: the third-party manifest catalog (textures/PBR, models, audio, icons, ...) and its
# generator. That catalog is a separate repository outside this plugin and is not published with
# it; .gitignore keeps example_manifests/, manifests/ and tools/ out of the plugin on purpose.
docs/MANIFEST_SPEC.md       # exhaustive manifest permutations — read before touching schema
```

## Hard Constraints — Do NOT Violate

1. **Plain clone only.** `AssetInstallerService` does `ProcessStartInfo FileName=git Arguments=clone…` — never `submodule add` (PluginsWindow.cs:397 branch is intentionally NOT replicated). Manager owns the tree; deletes + re-clones on update.
2. **Zips only via `archivePack`.** `assetPack` is `files[]` explicit only. Zip extraction lives exclusively in `InstallArchivePackAsync` with the zip-slip guard (`Path.GetFullPath` + `StartsWith(destRoot)`) and atomic rollback — do not add ad-hoc `ZipFile.ExtractToDirectory` elsewhere.
3. **`No $Other$ type.** `FileAssetType` is exhaustive 10 values: `Texture, Material, MaterialNode, Model, Prefab, Script, AnimGraph, Audio, Shader, MaterialTextures`. Unknown strings must fail. `MaterialTextures` also accepts the singular `MaterialTexture` (custom converter in `FileAssetType.cs`), so manifests that use the singular spelling and the shipped examples (plural) both load.
4. **`Script` is `.cs` only.** Forbidden exts for assetPack: `.cpp,.h,.hpp,.c,.cc,.inl,.md,.txt,.json,.bin,.dat,.gitignore`. If `type==Script` then ext must be `.cs`; if `type!=Script` then ext must NOT be `.cs`.
5. **`type` is file-level, not pack-level.** Pack has `packType` (AssetPack|PluginProject|ArchivePack); file has `type`.
6. **Per-source separation.** Each URL in `Sources.json` is fetched independently; UI `ComboBox` shows them separately — never merge.
7. **Manual refresh only.** `FetchAllAsync` on `OnInit` + `Refresh` button. No timers.
8. **`Plugin.h:22` _description in ctor.** Do not add `override Description` to GamePlugin — `Plugin.Description` is concrete (see Scripting.Bindings.Gen.cs:232).
9. **Destination guard.** `ResolveAssetPackPath` does `Path.GetFullPath` + `StartsWith(rootFull)` — paths with `..` or absolute are rejected before any download. The same `GitSafety.IsUnderRoot` guard also protects **both uninstall deletes**; keep it there, and never compare paths with `OrdinalIgnoreCase` on Linux.
10. **Main thread only for GUI + engine/asset APIs.** Flax installs **no `SynchronizationContext`**, so an `await` continuation resumes on a thread-pool thread. Any GUI mutation, texture creation (`Texture.FromFile`), asset load/save or `Editor.*` call after an `await` must be marshalled — use `await MainThreadDispatcher.RunOnMain(() => …)` (`Core/Utils/MainThreadDispatcher.cs`, a wrapper over `Scripting.RunOnUpdate`; `Core/Tests/VariantE2ETest.cs` shows the raw idiom). Convention: **I/O on the pool, then one hop, then all engine/GUI work in that lambda**; blocking asset waits (`AudioClip.WaitForLoaded`) stay off the main thread. Fire-and-forget reports from a service cannot use that hop (nothing to `await`) and `Progress<T>` resumes on the pool, so the window queues them into a `ConcurrentQueue<Action>` drained at the top of `OnUpdate` (`QueueOnMain` / `DrainMainThreadCalls` / `WindowProgress`). All six `async void` window handlers, `ImageView` and `MaterialCreatorService` are marshalled — this was **D3**, now resolved; keep it that way, and re-check `IsDisposing` inside every marshalled lambda.
11. **No shell string interpolation for processes.** git runs through `ProcessStartInfo.ArgumentList` with manifest values validated by `GitSafety` (`IsHttpsUrl`, `IsValidRefName`, `IsValidCommit`). Adding a git subcommand means adding it to that list, never to an `Arguments` string.
12. **Never hand arbitrary data to the OS.** `SystemFileOpener` opens only files inside `Globals.ProjectFolder` and refuses extensions a shell would *run*; `Platform.OpenUrl` is called only for `http(s)` (its `CanOpenUrl` gate asks the OS for *any* handler, so `file://`/`mailto:`/custom schemes from a manifest would otherwise open a file manager, a mail composer or an installed app). Same rule as the https-only install path: manifest data and `Settings.json` are input, not authority.
13. **Hard-coded Visject ids need a guard case.** Material box/node ids are engine-internal API (`MaterialNodeBoxes` is internal), so the compat build proves they compile, not that they mean the same thing. Any id added to `MaterialGraphBuilder` must be added to `RootInputBoxes`, which `ValidateRootInputBoxes` checks against the engine's live box metadata (presence, direction, value type) before wiring.
14. **Install/Remove state comes from the record, not the pack type.** One Install button and one Remove button, labelled and enabled by `RefreshActionButtons` from `FindInstalled`/`HasUpdate` — never a pack-type-specific button, never a second install action. Material creation is decided by `HasMaterialTextures` inside the install handler; if you add a pack type that produces assets, extend that predicate rather than adding a button, and keep the tooltip predicate (`InstallWouldCreateMaterial`) identical to the handler's so the UI cannot promise what the click will not do.
15. **An install that produces an asset must record it.** Anything written into the Content tree that is not a downloaded file (the generated material, an imported model `.flax`) goes into `InstalledEntry.GeneratedAssets` at the moment it is created, otherwise uninstall leaves it behind and "Remove" is a lie. Those deletes stay behind `GitSafety.IsUnderRoot`, and a locked file warns instead of aborting the removal.
16. **Manifest reads go through `HttpStream`.** Never `GetStringAsync`/`GetByteArrayAsync`/`File.ReadAllText` for remote or user-supplied content: every read declares a ceiling, and manifest fetches use `FetchTextAsync` so retry and `If-None-Match` stay in one place. A new remote read means choosing a ceiling and, for a cached document, an invalidation point (source-list edits call `FetchAllAsync(force: true)`).
17. **A list column: `ClipText`, fixed x, `Near` = top.** Flax `Label` defaults to `ClipText=false` (`Label.cs:181-187`, `[DefaultValue(false)]`: `DrawSelf` pushes a clip only when true), so cell text longer than its rect draws straight over the edge — a live list printed "Ground & Terrain" on top of "Not installed". Cells and column-header labels set `ClipText` and sit at a **fixed x measured from the row's left edge** (`Theme.NameColumnLeft/Width`, `ChipColumnLeft/Width`, `StatusColumnLeft/Width`; `TopLeft` anchor + `Bounds`): a right-anchored cell gets cut off, because the rows are laid out in the scroll panel's *content* area, which is wider than the visible pane by the 14px vertical scrollbar (`Panel._scrollBarsSize`), so the row's right edge is not the pane's. `TextAlignment` is `Near/Far/Center` — `Near` is the top, there is no `Top`. A header put in its own `Panel` between the section header and the list drew nothing at all in two live runs, background band included, so the header labels are children of `SplitView.Panel1`, at the rows' x plus `ListRow.RowLeftInset`.
18. **The asset list is filled, not rebuilt.** Rows are **pooled** (`AcquireRow`/`TrimRows`) and a pool slot is the panel's child at that index, which is what keeps visible rows in list order. A row is a *container* — `ListRow` is a `Label`, and a `Label` is a `ContainerControl` with four child labels — so creating one per item per refresh is ~5 controls of churn per row, every keystroke. A pooled row is bound **once** and reads the item it currently shows from `Control.Tag` (`OnRowActivate`): never add per-item `Clicked`/`Focused` handlers, they accumulate on a row that outlives its item and every row then fires the selection of every item it ever showed. Refill the list inside `BeginListLayoutBatch`/`EndListLayoutBatch`: Flax re-layouts a container on every child add, show, hide or resize (`ContainerControl.OnChildrenChanged`, `Control.Visible`, `ContainerControl.OnSizeChanged`), and one pass locks the subtree and re-bounds and lays out every child, so filling an **unlocked** panel is quadratic in the row count (modelled from the 1.13 engine: 1,046 rows = 2.76 M `UpdateBounds` calls per rebuild, 54 ms; locked = 0.4 ms). Keep per-pack facts cached (`PackHaystack`, `PackFileTypes`, dropped by `InvalidatePackCaches` where the pack list is swapped) and install records from one pass (`SettingsService.BuildInstalledIndex`): a per-row linear `FindInstalled` is quadratic in how much is installed, and the search box runs the filter per keystroke (D18).

## Manifest Model (short; docs/MANIFEST_SPEC.md is canonical)

Top `AssetSourceManifest`, two kinds: a **pack** manifest (`sourceName, sourceId` + non-empty `assets[]`) and an
**index** manifest (`manifestKind:"index"` + non-empty `entries[]` of `IndexEntry`, and no `assets[]`). Kind-dependent
rules live in `ValidateManifest`/`ValidateIndex`, never in JSON `Required` (an index has no `assets`, so `assets` must
stay optional on the model). + `sourceVersion?, homepageUrl?, website?, description?` (`website` = canonical site of the
original source of the assets, for attribution/reference; never fetched, never validated).

Pack `PackEntry`: `id, name, packType, version` required + `description?, category?, tags?[], previewUrl?, license?` + **exactly one**:
- `packType:"assetPack"` → `files:FileEntry[]` (non-empty, no `distribution`)
- `packType:"pluginProject"` → `distribution:{kind:"gitRepo", url, branch?, commit?}` (no `files`)
- `packType:"archivePack"` → `distribution:{kind:"archive", url(…zip), sha256?}` (no `files`, no `variants`)

**Variants** (`variants[]`, assetPack only, `PackEntry.PackVariant`): with `variants[]` present, base `files[]` becomes optional; `PackEntry.GetEffectiveFiles(variantId)` resolves the install set (empty/unknown id → base `files[]`, else the **first** variant when base is empty). Selection persists per pack in `Settings.json` `selectedVariants` (key `sourceId/packId`) and per install in `.installed.json` `variant`. A variant install writes its files into its own subfolder of `files/` (see Destinations), so installing a variant removes the variant installed before it instead of overwriting it.

File `FileEntry`: `url, path, type` required + `sha256?`. `url` must be `http(s)`, `path` relative no `..` with extension, `type` in 9-value enum.

Matrix `docs/MANIFEST_SPEC.md` §4.1 lists every valid/invalid `packType × payload` combo and §5.1 lists per-file rules.

## Settings / Destinations / Install Manifest

`SettingsService` (`Globals.ProjectFolder` = `Globals.h:17`):

- `Settings.json` `{contentDestination:"Content/FlaxAssetManager", scriptDestination:"Source/FlaxAssetManager", overwriteExisting:true}` defaults — relative to project root. Changing requires restart.
- `Sources.json` `{sources:[{url,enabled,name?}]}` — `AddSource/RemoveSource` persist here; `EnsureSourcesFile` materializes it on demand so the UI's `Edit List` button can hand it to the system text editor (`Core/Utils/SystemFileOpener.cs`); `FetchAllAsync` re-reads it so external edits apply on `Refresh`.
- `.installed.json` `{installed:[{sourceId,sourceUrl,packId,packName,packType,installedVersion,installedAt,distributionUrl?,resolvedCommit?,installPath,files[]}]}` — key `sourceId+packId`, badge via `HasUpdate` (`installedVersion != pack.version`).

Destinations (downloaded files go under a `files/` subfolder; pack-*generated* assets — e.g. the created `.flax` material — stay at the pack root):

- `FileAssetType.Script` → `ScriptDestinationFullPath/Source/.../<sanitized(sourceId)>/<sanitized(packId)>/files/[<sanitized(variantId)>/]<file.path>`
- Others → `ContentDestinationFullPath/Content/.../<sanitized(sourceId)>/<sanitized(packId)>/files/[<sanitized(variantId)>/]<file.path>`
- Generated material (`.flax`) → `ContentDestinationFullPath/Content/.../<sanitized(sourceId)>/<sanitized(packId)>/M_<packId>.flax` (no `files/`)
- `pluginProject` → `Plugins/<Sanitized(lastUrlSegment)>` fixed.
- `archivePack` → `ContentDestinationFullPath/<sourceId>/<packId>/files/` — zip entries extracted with internal layout preserved.

**Variant subfolder** (`AssetInstallerService.VariantSubFolder`, the one place that knows the layout): installing a pack **with a variant selected** writes its files under `files/<sanitized(variantId)>/`, so a pack shipping 1k/2k/4k sets keeps each set — and the `.flax` assets imported from it — on its own instead of sharing one flat folder of identical names. The default file set (no `variants[]`, or base `files[]` selected) writes flat, exactly as before, and the material and imported models stay at the pack root in both cases. Because the sets share no path, the install that replaces one must delete it: `RemoveReplacedInstall` removes the replaced record's files plus their imported `.flax` sidecars and sidecar folders, the model asset the removed tier imported into the pack root (matched only against that record's own model sources — never the generated material, which the material pipeline rebuilds), and `PruneStaleVariantFolders` drops the folders of the pack's *other* declared variants (both destinations). That runs **after** the new files land, so a failed install never destroys what was already installed, and the replaced record's `generatedAssets` carry into the new record (`MergeGeneratedAssets`) because the pack root's imported models are not rewritten by a variant switch. The id is manifest data, so it is `Sanitize`d; two ids that sanitize to one folder name share that folder and the prune skips it. Pruning walks up to the pack root (`PruneEmptyFolders` + `PackRootOfRecordedFile`), never past it, and stops at the first non-empty folder.

`Sanitize` replaces invalid filename chars; `..` → `_`.

## UI Overview

`FlaxAssetManagerWindow : EditorWindow` (`EditorWindow.cs:16`, `DockWindow.cs:16`):

- Top: `ComboBox` source picker (`SourceUrlEntry.name ?? Manifest.sourceName ?? url + " (error)"`, plus a ` (N)` count that is `assets[]` for a pack manifest and `entries[]` for an index — the number the list below shows, so a non-empty index never reads `(0)`), `Refresh`, `Add URL` (ContextMenuBase `PluginsWindow.cs:233` + clipboard prefill), `Edit` (prefilled URL popup, calls `SettingsService.RenameSource`), `Remove`, `Edit List` (opens `Sources.json` in the OS editor via `SystemFileOpener`; detached launch so a modal editor cannot block the window), `Update Plugin` (far right; confirms, then `PluginUpdateService`: `git pull --ff-only` in the plugin folder + a scripts recompile — the reload disposes this window, so the handler's marshalled tail expects `IsDisposing`).
- Row 2: `TextBox` search + `ComboBox` type filter `[All, FileAssetType names, PluginProject, ArchivePack]`.
- Split: left `SectionHeader` + the column-header labels (`_assetColHeader` / `_chipColHeader` / `_statusColHeader`: **Asset** / **Category** / **Status**, children of `Panel1` at the rows' x plus `ListRow.RowLeftInset`, raw `Label`s with `ClipText`, hidden when the list is empty, retexted for an index list) + `Panel(ScrollBars.Vertical)+VerticalPanel` rows (`ListRow.SetContent` title/subtitle/category chip, `ListRow.SetStatus` install-state cell — `SetRowStatus` writes **Not installed** / **Installed** / **Update** from `FindInstalled`+`HasUpdate`, the same record `RefreshActionButtons` reads; an index manifest shows **Index only**). All three columns sit at fixed x (`Theme`), top-align (`TextAlignment.Near`), clip (`ClipText`), and the chip carries its text as a tooltip — rule 17 is why they are not right-anchored and not nested in a header panel. Right: `Label` title/details/status + `Install/Update/Reinstall` + `ProgressBar` (`HorizontalStretchBottom`).
- Bottom: `ProgressBar` + `Label` progress/status + `Label` dest hint.
- Wiring: `AssetSourceManager.Fetched` → `OnFetched` → `OnSourceChanged` → `RebuildAssetList` (search filters name/id/desc/category + `path/type`); `SelectPack` fills details; `OnInstallClicked` → `Installer.InstallAsync` with `Progress<(float,string)>`.

Add new UI elements in `CreateUI`; keep window thin — extract `Controls/` if growing.

## Material pipeline = stock API only — read before touching it

The asset comes from `Editor.CreateAsset("Material", path)` (the engine's own content-browser
creator) and the surface graph is built in `Core/Utils/MaterialGraphBuilder.cs` with the public
editor Visject API: texture nodes feeding the material root's boxes `Color 1 / Emissive 3 /
Metalness 4 / Roughness 6 / AO 7 / Normal 8 / Opacity 9 / PositionOffset 11` (Height), a
`Normal Map` node `(5,4)` for the normal map (it samples + decodes its own texture, so it cannot
take a sampled one) feeding the decode chain `Unpack Float4 (4,32) → Pack Float2 (4,20) → Multiply
(3,3)` by `NormalStrength` → `Derive Normal Z (3,29)` → the Normal input, a `Color` constant `(2,7)`,
`Multiply (3,3)`, `Get Parameter (6,1)`, and the public parameters `Color` / `EmissiveColor` /
`NormalStrength` added via `context.Parameters`.

These ids are engine-internal (`MaterialNodeBoxes` is not public), so `ValidateRootInputBoxes` checks
each root box (present, is an input, expected value type) against the engine's live box metadata before
any node is wired — the compat build proves the ids compile, not that they still mean the same thing.

Do **not** move material creation into the engine: stock Flax exposes no public C# API that creates
a material asset with its textures already connected, and every engine symbol this plugin calls must
exist in the Flax 1.13 **release** sources — the `compat/` CI build against the official editor
assembly (`-warnaserror`) is that gate. Building the graph in plugin code is stock-clean **and**
keeps the material policy (where textures go, which parameters are public) in the plugin.

Node group/type ids and box ids are engine constants: `MaterialGenerator.h` `MaterialGraphBoxes`
and `Source/Editor/Surface/Archetypes/Material.cs` `MaterialNodeBoxes` agree (1,3,4,6,7,8,9,11) —
re-check both if you change them. Material creation is decided purely from the pack's
`MaterialTextures` (`FlaxAssetManagerWindow.HasMaterialTextures`, re-checked by
`InstallWouldCreateMaterial` for the tooltip): installing such a pack builds the material, no engine-feature
gate remains, and there is no separate material button.

Stock APIs the plugin relies on: `Editor.CreateAsset`, `Asset.Save`, `ModelBase.MaterialSlots`
(live proxies — writing `slot.Material` writes through), `Material.LoadSurface/SaveSurface`,
`ContentImporting.Import`, `VisjectSurface`/`SpawnNode`, `Content.GetAssetInfo`,
`Scripting.RunOnUpdate`.

## Fixed (security & correctness pass)

Kept as a record of what the guards below are defending, so they are not "simplified" away later.

- **`Settings.overwriteExisting` is honoured.** `InstallAssetPackAsync` reads
  `_settings.Settings.OverwriteExisting`; when `false`, an existing file is left untouched (no
  download, no checksum, no model import) and is still recorded, and the count is logged.
- **git is invoked argument-per-argument.** `RunGitAsync` takes `IReadOnlyList<string>` and fills
  `ProcessStartInfo.ArgumentList`; clone uses `--branch=<v> -- <url> <dest>`, checkout uses
  `checkout --detach <commit>`. URL/branch/commit are validated by `Core/Utils/GitSafety.cs`
  (https-only, conservative ref names, 7-40 hex commit) before any spawn. A manifest value
  starting with `-` can no longer reach git's option parser (`--upload-pack=…` was the RCE risk).
- **A plugin clone replaces only what it owns.** The clone lands in `Plugins/.flaxam-clone-<guid>` (staging,
  same filesystem as the destination → the swap is a rename), is validated there (root `.flaxproj`, requested
  ref/commit), and only then does the swap run — the owned tree is renamed to `Plugins/.flaxam-replaced-<guid>`,
  the clone is renamed into `Plugins/<folderName>`, and the replaced tree is deleted afterwards (a failed
  move-in renames the old tree back). The folder name comes from a
  remote URL, so it is checked to be a direct child of `Plugins/` and not the folder this plugin's own
  assembly runs from; an existing folder is replaced only when a recorded `pluginProject` install owns it
  (`IsOwnedPluginInstall`), so a hand-added plugin, `Plugins/Flax`, or an unreachable repo costs nothing.
  (The old flow deleted the destination *before* validating the URL — the destructive path this closed.)
- **Archives cannot deliver code.** Archive entries carry no manifest `type`, so the per-file whitelist
  never applied to them. `AssetSourceManager.CheckArchiveEntryName` now runs over every entry name before
  extraction: script / executable / native-library / interpreter-script extensions **reject** the archive,
  docs/JSON/VCS-metadata are **skipped** (upstream archives ship them; failing an install over a `README.md`
  is user-hostile), everything else extracts. The `assetPack` rules in `ValidateFiles` are unchanged.
- **Nothing on the GUI/asset path runs off the main thread.** See rule 10: `MainThreadDispatcher.RunOnMain`
  is the one hop, the window's six `async void` handlers end in a single marshalled tail, installer reports
  are queued to `OnUpdate`, and closing the window cancels all four CTSs (the material flow's was never
  cancelled). This closed **D3**.
- **Downloads stream to disk with a size cap.** `DownloadToFileAsync` streams with a 2 GiB cap
  (`MaxDownloadBytes`), hashes the temp file for the optional `sha256`, and publishes with
  `FileMove` — no in-memory buffering, no truncated file at the destination on failure.
- **Path containment everywhere, case-correctly.** `GitSafety.IsUnderRoot` guards install paths,
  zip extraction, and both uninstall deletes (record paths and the plugin folder). Comparison is
  `Ordinal` on Linux/macOS, `OrdinalIgnoreCase` only on Windows.
- **Manifest `type` is name-only.** `FileAssetTypeConverter` rejects numeric strings first, so
  `"type": "5"` no longer silently resolves to `Script` (the spec's "unknown must fail").
- **A normal map is never rewired.** `MaterialGraphBuilder` owns its chain (`Normal Map → Unpack
  Float4 → Pack Float2 → × NormalStrength → Derive Normal Z → Normal`): the `Normal Map` node samples +
  decodes its own texture, so `InsertSamplingNodes` leaves that graph alone and only feeds the node's
  UVs input with the shared UV transform, and a failing surface save is reported (not thrown into UI).
- **Every read is bounded, not just pack files.** `HttpStream.DownloadTextAsync` caps a manifest at
  8 MiB and an index/linked manifest at 16 MiB; `DownloadPreviewAsync` caps preview images and audio at
  64 MiB. A manifest over the cap is refused as invalid, so a hostile source cannot make the editor
  buffer a huge body (pack files were already streamed with the 2 GiB cap).
- **OS handoff is contained.** `SystemFileOpener.TryOpenInEditor` requires the file to be inside
  `Globals.ProjectFolder` (via `GitSafety.IsUnderRoot`) and refuses shell-executable extensions; the
  website button calls `Platform.OpenUrl` only for `http(s)`, because `CanOpenUrl` asks the OS for any
  handler and a manifest's `file://`/`mailto:` link is data, not a request to open a local path.
- **Settings destinations are validated, not guessed.** `SettingsService.IsValidDestination` refuses
  drive hints (`C:foo`), `C:\` roots, UNC shares and traversal; `InstallRootFolderFor` sanitises the
  pack folder name. `ContainsTraversal` replaced the old `Contains("..")` filter, which also rejected
  legitimate content named `brick..png`.
- **Uninstall is cancellable, and manifest pack ids are unique.** `UninstallAsync` is handed the window's
  `_installCts`; `ValidateManifest` rejects duplicate pack ids the way the index validator rejects
  duplicate `sourceId`s, because both keys address install records.
- **Preview decode runs on the main thread.** `ImageView.LoadFromBytes` queues `Texture.FromFile`
  through `Scripting.RunOnUpdate` (it used to run in `Task.Run` → GPU texture creation off-thread).
- **Material creation lives in the plugin, not the engine.** `Core/Utils/MaterialGraphBuilder.cs`
  creates the asset with `Editor.CreateAsset("Material", …)` and builds the PBR graph with the
  public Visject surface API, so the editor module compiles against a **stock** 1.13 engine
  assembly — which is what the `compat/` CI build compiles against.
- **Every material-pipeline engine call runs on the main thread.** `MaterialCreatorService.RunOnMain`
  (→ `MainThreadDispatcher.RunOnMain` → `Scripting.RunOnUpdate`) wraps the content-database lookups, the
  import hand-off, the graph build/rewire and the model-slot save; the awaits stay off the main thread.
- Verified helpers: `GitSafety` (31 assertions), the `FileAssetType` converter (9 cases) and
  `FlaxProjectLocator` (42 checks: project-file discovery by name and by the `Name` inside, `.flaxproj`
  macro expansion, scan depth/budget limits, ancestor walks) were exercised in a standalone harness with
  the real sources linked in. That locator harness is what proves `Update Plugin` finds its own folder when
  the folder is named from a git URL (D16).
  exercised in a standalone harness with the real sources linked in.

## Open items — need a product/publishing decision

See **`docs/DECISIONS.md`** (numbered D1-D16; **D1 is resolved** — the material pipeline is stock
API only, and material creation runs as part of installing a texture pack again; **D3** off-main-thread GUI work and
**D14** write-from-remote-input safety and **D16** the plugin's own `git pull` + recompile update are resolved too). Headlines: **D2** licensing incl. vendored
`EKEditorUIKit`, **D4** E2E test shipping in the release assembly, **D5** material role coverage
(the UI now states what the material will not use), **D6** uninstall deletes generated/imported
assets, **D7/D8** plugin metadata and publishing hygiene,
**D9** PRD rewrite scope, **D10** UI ownership, **D11** network behaviour (reads are now size-capped and
pack ids unique; retry/backoff, ETag caching and the `sha256` policy are still open), **D12** manifest
trust.

## Build & Verify

```bash
# Contribute/develop from a host project that has this folder under Plugins/.
# No .csproj is committed (Flax generates them), so build with the engine's builder, not dotnet:
FLAX=/path/to/FlaxEngine   # engine checkout with Binaries/Tools/Flax.Build
# Build through the HOST project: the plugin targets are dependencies of the host project targets, so
# one plain -build compiles the plugin C# (game module + editor module + vendored UI kit).
"$FLAX/Binaries/Tools/Flax.Build" -project=<HostProject>.flaxproj -build -platform=Linux -configuration=Development
# Verify the claim, not the exit code: the builder logs every assembly it compiles.
#   grep FlaxAssetManager Cache/Intermediate/Log.txt   # project root; add -verbose for more detail
# Output: Plugins/FlaxAssetManager/Binaries/FlaxAssetManager*Target/Linux/x64/Development/*.CSharp.dll
# Gotchas: -configuration takes TargetConfiguration (Development|Debug|Release); Editor is NOT a value.
#   -targets=<Target> selects targets for -genproject ONLY and compiles nothing; -buildtargets=<PluginTarget>
#   prints Missing target FlaxAssetManagerEditorTarget + No targets to build and still exits 0. A plugin
#   target is not a build target. Never trust exit 0 here - grep the log.
# Fast API check against an OFFICIAL Flax release (no editor, seconds; CI runs this):
dotnet build compat/FlaxAssetManager.Compat.csproj -c Development -p:EngineDir=/path/to/flax

# Local manifest test (the spec examples ship with the plugin: example_manifest.json, example_index.json)
python3 -m http.server --directory Plugins/FlaxAssetManager 8899
# Window: Add URL → http://localhost:8899/example_manifest.json → Refresh → Install → check Content/… + Source/… + .installed.json
# Index manifests: Add URL → http://localhost:8899/example_index.json → the list shows the linked manifests,
# pick one → Add linked source → Refresh → the linked manifest appears as its own source (nothing installs
# from the index itself; entry URLs are placeholders, so expect a fetch failure for example.com).
# Variants E2E (Core/Tests/VariantE2ETest.cs; runtime gate FLAX_AM_E2E=1) needs any manifest served
# over HTTP whose packs offer 2+ variants - it asserts manifest-derived invariants only, nothing is
# hardcoded:
#   python3 -m http.server --directory <dir-with-a-variants-manifest> 8899
#   → Add URL http://localhost:8899/<that-manifest>.json, then run the editor with FLAX_AM_E2E=1
# Material-readiness E2E (Core/Tests/MaterialReadyE2ETest.cs; runtime gate FLAX_AM_MATERIAL_E2E=1) needs an
# editor session with at least one source configured and one installed asset pack that ships material
# textures (install one through the window first). It rebuilds that pack's material, reads the LOADED asset
# back with no reload in between, and asserts the saved graph is what the loaded material shows - the
# black-material regression of D17. It rewrites that pack's material twice (probe, restore):
#   <editor> -project=<project> -headless -SkipCompile   (headless is enough; the check is engine state)
#   env FLAX_AM_MATERIAL_E2E=1 → look for "material E2E done: N passed, M failed" in the editor log
```

No native code (`BuildNativeCode=false` both modules); pure C#; Newtonsoft.Json via `FlaxEngine.CSharp.dll` (Editor module pulls `Editor`).

## Extending

- **New FileAssetType:** Add to `FileAssetType.cs`, update `AllowedScriptExts/ForbiddenExts` in `AssetSourceManager.cs:27` if routing changes, update `MANIFEST_SPEC.md` §5.2 (type ↔ extension) and `example_manifest.json`, rebuild. Keep `StringEnumConverter` case-insensitive compat.
- **New PackType (e.g. Template):** Add to `PackType.cs`, add branch in `AssetInstallerService.InstallAsync`, update `ValidateManifest` matrix, extend `AssetSourceConfig` if needed; omit → AssetPack compat.
- **Services:** Keep I/O out of models, UI out of services. Add helpers under `Core/Utils/`. Use `SettingsService` for all `Content/FlaxAssetManager/*` I/O.
- **Git:** Keep plain clone. To add update check without re-clone, implement `git ls-remote` ahead of `clone` — do not touch submodules.
  Every git spawn goes through `Core/Utils/GitRunner.cs` (argument-per-argument, bounded, kills an
  abandoned git). The plugin's own update is the one pull allowed, in its own folder, `--ff-only` only,
  after the preflight refusals (D16) — it is not a pattern for installing packs.
- **Index (master) manifests:** `manifestKind:index` + `entries[]` (`Core/Models/IndexEntry.cs`), rules in `AssetSourceManager.ValidateIndex`, UI in `RebuildIndexList`/`SelectIndexEntry`/`OnAddLinkedSourceClicked`. An index must never install, never auto-follow a link, and never write install records (D13): trust is per URL, never transitive. Entry `sourceId` is the join key — keep the uniqueness check in the validator and the collision check in the UI.

## Common Pitfalls

- Using `ScriptItem.FilterScriptName` — it's `internal` (`ScriptItem.cs:33`); use local `Sanitize` instead.
- Namespace collision: `FlaxAssetManagerEditor` is both project folder and type — our editor plugin is `namespace FlaxAssetManagerEditor` to avoid `FlaxAssetManager.Editor` vs `FlaxEditor.Editor` ambiguity (`global::FlaxEditor.Editor` qualifier in window ctor).
- Forgetting `PublicDependencies.Add("FlaxAssetManager")` in `FlaxAssetManagerEditor.Build.cs` → `GamePluginType` unresolved.
- Adding `Other` or allowing `.cpp` in assetPack → violates PRD strict rule — will fail validation and disappoint users.
- Forgetting to update `example_manifest.json` + `MANIFEST_SPEC.md` together when schema changes.
- Re-adding `Required.Always` to `AssetSourceManifest.assets`: an index manifest has no `assets`. Kind-dependent rules belong in `ValidateManifest`/`ValidateIndex`, the single authority (D13).
- Reading a remote body without a cap (`GetStringAsync`/`GetByteArrayAsync` have no limit) — use
  `HttpStream.DownloadTextAsync` / `DownloadPreviewAsync`, which enforce the manifest/preview caps.
- Handing a path or URL from `Settings.json`/a manifest to the OS without a check — see rule 12; the
  install side is contained, and the read side has to stay contained too.
- Adding a Visject box/node id to `MaterialGraphBuilder` without a `RootInputBoxes` case — CI proves
  it compiles, not that it is the same box (rule 13).
- Naming a real asset source, vendor, pack, variant or URL in code, tests, examples, fixtures or docs. The plugin is source-agnostic by policy; its examples stay `example.com` placeholders.
- Expecting a build symbol to gate plugin C#: on stock 1.13 there is **no** C# preprocessor-define path for plugin modules (`ScriptingAPI.Defines` in `Module.Setup` does not reach the plugin assembly; `Target.GlobalDefinitions` is native-only). Gate dev-only code at runtime (see `FLAX_AM_E2E` and D4).
- Naming a specific asset source, manifest, pack id, variant id or URL in code, tests or docs — the plugin and its E2E harness are source-agnostic by policy.
- Dropping the **"installs code" gate** (`PackInstallsCode` / `ConfirmCodeInstall` in the window): any pack that lands *compiled* code — `Script` files, a `pluginProject` clone — must warn and confirm first. A new pack type, destination or file type that ends up compiled needs the same gate, otherwise the trust story has a hole (see `docs/DECISIONS.md` D12).
- Writing a material's surface data **without reloading the asset**. `Material.SaveSurface` writes the file and the asset's chunks; the loaded material's shader, public parameter table and `MaterialInfo.UsageFlags` are produced by the material generator during **asset load**. A saved graph is therefore invisible to the renderer until the asset is reloaded, and a material created in the running session keeps the engine's default graph — a pure black surface with no parameters (the bug report). `MaterialGraphBuilder.MaterialSurfaceOwner.SurfaceData` is the plugin's one writer and does save → `Reload` → `WaitForLoaded`, throwing on failure so the install reports it; route every new surface write through it (`docs/DECISIONS.md` D17), guarded by `Core/Tests/MaterialReadyE2ETest.cs` (`FLAX_AM_MATERIAL_E2E=1`). `Asset.WaitForLoaded()` returns **true on failure**.
- Naming the plugin's own folder in a path. The manager's `pluginProject` install names a cloned folder from
  the git URL, users rename folders, and forks rename projects, so `Plugins/FlaxAssetManager` is documentation,
  not an identity: resolve this plugin by its project file — file name, or the `Name` inside it — through
  `Core/Utils/FlaxProjectLocator.cs`, and keep the D14 containment check on the result (D16).

## Pointers

- Product intent: `PRD.md:1` (goals, use-cases, non-goals — deliberately not a spec)

- Runnable manifest with every type: `example_manifest.json:1` (13 packs, Counter shows all 9 types)
- Exhaustive manifest permutations, error catalog, kitchen-sink examples: `docs/MANIFEST_SPEC.md:1`
- Open decisions (material ownership, licensing, thread-affinity refactor, publishing, …): `docs/DECISIONS.md:1`
- Engine refs: `Flax.Build/ProjectInfo.cs:214` (macros), `ContentFolder.cs:27` (Content vs Source), `UIModule.cs:775` (Window menu), `PluginsWindow.cs:388` (clone pattern).

Iterate here — keep the three layers clean and the validator strict.
