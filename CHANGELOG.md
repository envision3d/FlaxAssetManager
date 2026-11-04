# Changelog

All notable changes to **FlaxAssetManager** are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html). The version the editor shows is the
`Version` field of `FlaxAssetManager.flaxproj`; release tags are `v<major>.<minor>.<patch>`.

## [Unreleased]

### Added

* **Seeding the source list** — two channels, one rule set: a **plugin** ships `seed_sources.json` next to
  its `.flaxproj`, and a **project** declares `seedSources` in its own `Settings.json`. Either way a fresh
  checkout opens with the manifest URLs registered instead of an empty picker that only **Add URL** fills.
  `PluginSeedFile` finds the plugin's folder by its own project file (running-assembly ancestors → the open
  project's `References` → the documented `Plugins/<name>`, every route validated; D16: a folder name is not
  identity), reads that file under a 1 MiB cap, and refuses to read a plugin folder outside the open project.
  `SettingsService.ApplySeedSources` appends each URL this project has not been seeded with yet — project
  list first, plugin file second, and the log names the channel each source arrived from — from `Load` (so a
  session's first fetch already sees it) and from `ReloadSources` for the project list, while the plugin's own
  file is read once per session: a plugin's files do not change under a running editor, so editing that file
  takes effect on the next start, which is also what **Update Plugin** asks for. `Sources.json` records the
  URLs it has applied under `seededUrls`. That record is what keeps the feature honest: a seed is applied
  **once**, so a source you **Remove**, rename, or delete from the file stays removed on every later start; a
  URL that is already configured is recorded as applied and keeps its own name and enabled flag; URLs compare
  `OrdinalIgnoreCase`. A seed registers a source, it never installs one: it is fetched, manifest-validated,
  size-capped and installs-code-confirmed exactly like a typed URL. A seed URL takes everything a source URL
  takes (`http(s)`, `file://`, absolute, project-relative, `$(PluginPath)` = this plugin's folder,
  `$(ProjectPath)`, `$(EnginePath)`), which is why seeding a manifest that ships inside a project, or inside a
  plugin, needed no new fetch path. This plugin's own seed file names its author's hosted manifest index and
  no third-party source - nothing is bundled, and no URL is forced on a project that declines the file, so
  D12 stands — and a project that wants none of it
  puts `pluginSeed: false` in its `Settings.json`, which declines the plugin's file while leaving
  `$(PluginPath)` usable for URLs the project wrote itself. See `docs/DECISIONS.md` D19. **Tested:** compiles
  clean through `Flax.Build` on a host project and against the 1.13.7013 editor assembly with
  `-warnaserror`, 0 warnings. Live in the editor, across four starts: the plugin's seed file is found through
  the open project's `References`, logs `seeded source '$(PluginPath)/example_manifest.json' from
  seed_sources.json (plugin)`, its manifest is read **from disk** (14 assets, no network), the URL lands in
  `Sources.json` `seededUrls`, and the next start applies nothing; with `pluginSeed: false` the plugin's file
  is reported as not applied and nothing is registered. The project channel was verified the same way
  (`seeded source '<url>' from Settings.json seedSources`, recorded, silent on the next start). **Not verified
  in the editor GUI:** that **Remove** and **Edit** (rename) keep a seeded source out permanently — the record
  makes it so by construction, and a recorded seed adding nothing on a later start is the half that was run.
  Check it once by hand: seed a source, **Remove** it, restart the editor, and it should stay gone.
  Live again the same way once the shipped file was edited to name the author's hosted index: that start
  recorded the URL in `seededUrls` and, because the identical URL was already a configured source, changed
  nothing about the entry - the user's display name and enabled flag stayed.

* **An install-state column in the asset list** — the list showed a name, a type/version summary and
  a trailing chip, and the chip only carried install state for the one source that fills it
  (`InstallService.ChipText`); a category, tags, or nothing said whether the item was installed in
  *this* project, which you could only read from the buttons after selecting it. The list is now
  three columns under a fixed header — **Asset**, **Category** (the chip, now always the manifest
  category, was the install-state text) and **Status**: `Not installed`, `Installed` (tooltip: the
  installed version, variant and path) or `Update` when a newer version is published. Cells read
  `SettingsService.FindInstalled` / `HasUpdate`, the same install record the buttons use, so column,
  details panel and buttons cannot disagree. Index manifests install nothing, so they read
  `Index only` with a tooltip pointing at **Add linked source**, and their headers retitle to
  **Linked manifest** / **Tags** / **Status**. `EKEditorUIKit.ListRow` gained the cell (`SetStatus`)
  and `Theme` the column x/widths (`NameColumnLeft/Width`, `ChipColumnLeft/Width`,
  `StatusColumnLeft/Width`); `ListRow.RowLeftInset` is the one place the rows' inset, and so the
  header's matching x, is written down. The columns are **fixed x from the row's left edge**, every
  cell and header label sets `Label.ClipText`, and every column tops out at the same y
  (`TextAlignment.Near`, which is the top edge - Flax has no `Top`). Two live runs shaped this:
  right-anchored cells landed under the list's 14px vertical scrollbar and cut "Not installed" off,
  because the rows are laid out in the scroll panel's content area, which is wider than the visible
  pane; and text over-long for its column drew over the edge, "Ground & Terrain" on top of "Not
  installed", because a Flax Label defaults to `ClipText=false` (`Label.cs:181-187`,
  `[DefaultValue(false)]`: `DrawSelf` pushes a clip only when true). The chip column is 150px wide
  (live categories run to "Ground & Terrain/Sand/Beach") and carries its text as a tooltip. The header
  labels are children of `SplitView.Panel1`, at the rows' x plus `RowLeftInset`: a `Panel` created for
  the header drew nothing at all in both runs, its background band included, so nothing nests under it.
  **Tested:** `EKEditorUIKit` and `FlaxAssetManagerEditor` compile clean against the 1.11 runtime +
  editor assemblies, 0 warnings; the overlap, the cut-off status cell, the invisible header and
  `TextAlignment.Top` not existing are all from live runs (three 869-pack Texture Haven screenshots and
  the compiler). **This rework is not verified in the editor GUI** - check the header, the columns' x
  at a couple of pane widths, and the states after an install, remove, or an offline refresh.
* **One install action per pack, and install-state-driven buttons** — the details panel offered
  **Download** (files only) and **Install** (files **+** a generated PBR material) as two buttons for
  a texture pack. They are now one **Install**: for an asset pack with detectable PBR roles the click
  also builds the material, and every other pack type installs as before. Labels and availability come
  from the install record — **Install** / **Update** / **Reinstall**, and **Remove** visible only while
  installed — computed in one place (`RefreshActionButtons`), identically for asset, model, script,
  audio, archive and plugin packs, with both actions disabled while an operation runs (D15).
* **Uninstall removes what the install produced** — an install record carries `generatedAssets` (the
  imported `.flax` models, the generated `M_<packId>.flax`), and removing a pack deletes those plus the
  `.flax` sidecar and derived-data folder of every imported file, then prunes the pack folder when it is
  empty. Previously a "removed" pack left visible assets in the Content tree (D6).
* **A texture pack states what its material will not use** — gloss, specular, cavity, curvature and ID
  maps have no PBR role, so the details panel now says "N texture(s) unused by the material" instead of
  letting a complete-looking material imply that every downloaded file was wired (D5).
* **Manifest fetches retry politely and cost a 304 when unchanged** — transient failures (429, 5xx,
  timeouts, dropped connections) are retried with backoff honouring `Retry-After`, and a source that has
  not changed is re-read with `If-None-Match`; editing the source list forces a full read (D11).
* **Repo conventions** — `.editorconfig` (LF, 4-space C#, 2-space data files; vendored kit and binaries
  excluded), `.github/CODEOWNERS` (vendored kit and the install/material paths; the GitHub handle is a
  placeholder until the repo exists), and `.github/dependabot.yml` (monthly `github-actions` updates; the
  engine pin stays manual on purpose).
* **`website` — a top-level manifest field for attribution** — optional string naming the canonical site of
  the **original source** of a manifest's assets. It is separate from `homepageUrl` (the manifest author's
  page): a mirror/scraper manifest points `homepageUrl` at the mirror and `website` at whoever made the
  assets. Pure metadata — never fetched, never validated, not a trust boundary (trust stays the source URL).
  See `docs/MANIFEST_SPEC.md` §3 and the updated `example_manifest.json` / `example_index.json`.
* **Clickable website link in the details panel** — selecting a pack, or a manifest inside an index, shows
  an **`Open <host> ↗`** button in its own row directly under the From/ID/Version metadata table, styled
  like the other action buttons, that opens that manifest's `website` (falling back to `homepageUrl`) in
  your browser through `Platform.OpenUrl`. An index may publish
  `entries[].website`; an index cannot invent a website for an entry it has not published, and the link is
  offered only for `http(s)`. The plugin never fetches the URL — clicking is an explicit user action.
* **Update Plugin — the plugin updates itself** — a header button (far right, after **Edit List**) runs
  `git pull --ff-only` in `Plugins/FlaxAssetManager` and then asks the editor to recompile the project
  scripts, because the plugin's assemblies are built by Flax and are not shipped in git, so a pulled source
  tree only takes effect after a rebuild (`ScriptsBuilder.MarkWorkspaceDirty` + `Compile`, marshalled to the
  main thread). A confirm dialog names both steps up front: a scripts reload replaces the plugin's running
  assemblies and disposes this window. The pull is refused with **nothing written** when the folder is not a
  git checkout, has local changes, is on a detached HEAD, or has no upstream branch, and `--ff-only` means a
  diverged branch fails with git's own words rather than merging into the user's commits. The recompile is
  requested only when HEAD actually moved. The new shared `Core/Utils/GitRunner.cs` keeps git argument-per-
  argument (hard constraint 11) and kills an abandoned or timed-out git together with its process tree, so a
  hung `git pull` cannot land files after the operation has been reported over (D16).

### Changed
* **The window opens from `Tools`, not `Window`.** `Window → Flax Asset Manager` put a plugin entry among
  the engine's own window list, which is about the editor's layout (Content, Scene, Toolbox, layouts …);
  the manager is a tool that installs content, and `Tools` is where Flax puts the engine's own work
  windows (Game Cooker, Profiler, Plugins) beside its build actions. `FlaxAssetManagerEditor` now adds
  the button to `Editor.UI.MenuTools.ContextMenu`, behind a separator so it reads as the plugin's own
  section rather than as more engine items. The separator is a second item to own, so the hot-reload
  cleanup grew: the button is still found by text (a script reload drops the reference, so text is the
  only handle), the separator is tracked by reference, and a pass collapses any separator sitting
  directly on top of another, which is what a reload that added one leaves behind. That pass runs
  backwards over a snapshot of `menu.Items` — the engine exposes `Items` as `IEnumerable`, not an index —
  so disposing an item never shifts an index still to visit, and it converges: each reload adds one
  separator, the cleanup removes one. Deinitialize removes both entries. The window's `Title`, the
  docking behaviour, and `ShowWindow()` are unchanged; the docs, README install step, AGENTS file map and
  both issue templates follow the new location. Moving the entry is not free space: `ContextMenu`
  shows at most `MaximumItemsInViewCount` items (engine default 20) and scrolls the rest, and the
  engine builds its own Tools menu at **exactly** 20 — separator plus button landed as items 21 and
  22, so the first release of this change opened Tools with a scroll bar. The plugin now raises that
  cap to the menu's item count (so it never scrolls, and never needs to know how many items the
  engine ships), remembering the value it replaced and handing it back on unload; the raise is
  idempotent, since a hot-reload re-adds the same two items to a cap already at that count.
  **Tested:** compiles clean through `Flax.Build` on the
  host project (`FlaxAssetManagerEditor.CSharp.dll` compiled, 0 warnings) and against the stock 1.13
  editor assembly via `compat/` with `-warnaserror` — which is the real check here, that `MenuTools`,
  `ContextMenuSeparator` and `MaximumItemsInViewCount` are public stock API and not a local engine
  addition. The 20-item Tools menu is counted from the 1.13 sources, not guessed.
  **Not verified in the
  editor GUI:** that the entry appears under **Tools**, with a single separator above it and no scroll
  bar, and that it stays that way after a script hot-reload — the dedupe and the cap are by
  construction (text match + adjacent-separator collapse, cap = item count) and the engine
  itself drops a trailing separator on menu open; check it once by hand.

* **The shipped example manifest moved to `docs/example_manifest.json`.** It is a format demonstration to be
  read beside `docs/MANIFEST_SPEC.md`, not a pack a project installs, so it lives with the docs; `README.md`,
  `AGENTS.md`, `CONTRIBUTING.md` and the issue template follow, as does the offline seed form
  `$(PluginPath)/docs/example_manifest.json`. `example_index.json` stays in the plugin root, where the
  local-server verification recipe serves it.

* **Each variant of a pack keeps its own subfolder** — installing with a variant selected writes that
  set to `<contentDestination>/<sourceId>/<packId>/files/<variant>/<path>` (scripts under
  `scriptDestination`), so the 1K / 2K / 4K / 8K texture sets of a pack — and the `.flax` assets
  imported from them — stop sharing one flat folder of identical names, and the Content browser shows
  which tier each file belongs to. The default file set (no `variants[]`, or the base `files[]` =
  **Default**) is unchanged and still installs flat. A variant set lives at paths the previous set does
  not share, so nothing is overwritten by the switch: the install now **replaces** the set it installed
  before it, deleting that record's files with their `.flax` sidecars and derived-data folders and the model
  asset the removed tier imported into the pack root, dropping the folders of the pack's other declared
  variants (in both destinations), and carrying the replaced record's `generatedAssets` into the new record so
  **Remove** still finds every asset the pack produced. The pack's material is left for the material pipeline
  to rebuild, so a variant without PBR roles does not lose it.
  That cleanup runs only after the new files land, so a failed install never destroys what was already
  installed, and folders emptied by it are pruned up to — never past — the pack root (the old uninstall
  prune was fixed at two levels and left empty variant folders behind). The details panel names the exact
  folder the selected variant writes to and states that installing replaces the installed set. Layout
  policy lives in one place, `AssetInstallerService.VariantSubFolder`, which the material pipeline
  resolves installed textures through. A variant id is manifest data, so it is `Sanitize`d like any other
  derived folder name. No manifest schema change; see `docs/MANIFEST_SPEC.md` §5.3 and §12.
* **The Asset Manager's backdrop is a deep navy (`#061019`)** — the kit's `Theme` palette no longer reads
  `Style.Current` (whose grey background fallback made the window look like the editor theme, not like the
  plugin): every colour is now an explicit value, and `CreateUI` paints the window's root panel with `Bg`,
  because its panels are transparent by default. `Bg` is `Metal(6f / 255f, 16f / 255f, 25f / 255f)` — Flax
  `Color` stores floats, so hex has to go through them, and those divisions land on exactly 6/16/25 of 255.
  `BgAlt` keeps a lighter value so the top bar and status bar stay distinct from the panel, and `Progress`
  still follows `Style.Current` as the one colour that should track the editor's own accent.
* **Adding a source reports in the status bar, not in a modal** — **Add linked source** confirmed the add
  with a `MessageBox`. On Linux that is the engine's platform message box, and Flax's SDL path only
  forwards the icon argument (`SDLPlatform.Linux.cpp`): the 3-arg `MessageBox.Show` the plugin called
  passes `MessageBoxIcon::None`, so the dialog arrived with a caution triangle the plugin never asked
  for, in the desktop theme, unparented to the window, and blocking the refresh that was the actual proof
  the add worked. It is now a `Debug.Log` plus a one-shot status note (`_pendingSourceNote`, replayed by
  `OnFetchedMainThread` once that source's first fetch has finished) — the shape **Add URL** already
  used, so both adds report identically. The refusals (missing/non-http URL, duplicate URL, `sourceId`
  collision) and the destructive confirms keep their modals, where an alert icon is honest.
* **`PRD.md` is product intent, not a spec.** Its schema, file map, enum lists, install-flow sections,
  build commands and *future work* list are removed in favour of `docs/MANIFEST_SPEC.md` + `AGENTS.md`,
  which is what made those sections rot. The header states the precedence, and the doc-status notes in
  `AGENTS.md` / `README.md` describe the split instead of flagging staleness (D9).
* **Vendoring of `EKEditorUIKit` settled as "keep it vendored"** — MIT, same copyright holder as the
  plugin, referenced by `$(ProjectPath)` so a clone is self-contained; no separate package, no upstream
  PR, no API-stability promise, with the trigger to revisit written down (D10).
* **Docs no longer name engine APIs that a release does not provide.** The material path is described by
  what the plugin calls — `Editor.CreateAsset` plus the public Visject surface in `MaterialGraphBuilder` —
  and `docs/DECISIONS.md` D1 is retitled "Material creation lives in the plugin, not the engine", stating
  the guardrail in place of the old symbol names: every engine symbol the plugin calls must exist in the
  Flax 1.13 release sources, checked by the `compat/` build. Naming symbols absent from a release had
  pulled code back to them twice.
* **A pack material samples its normal map with a `Normal Map` node, and `NormalStrength` scales the
  tangent-space XY** — the Normal input is now fed `Normal Map (5,4) → Unpack Float4 (4,32) → Pack
  Float2 (4,20) → Multiply (3,3) by NormalStrength → Derive Normal Z (3,29) → Normal`. The graph used to
  sample the normal with a plain `Texture` + `Sample Texture` pair and rebuild the vector with a
  `Mad(rg, 2, -1)` node: the `Normal Map` node **is** the engine's normal-map sampler — the HLSL it
  generates is `UnpackNormalMap(rg)`, which converts R,G to [-1;1] and derives Z — so that `Mad` was a
  decode the sampler had already applied, and a `Texture` node sampled the map in a way that contradicted
  the texture's own import type (a normal map imports as `NormalMap`). Scaling the decoded `Float3`, as
  the builder first did, scaled Z too, so raising strength flattened the surface towards the shading
  normal instead of bending it; scaling the packed X,Y and letting `Derive Normal Z` rebuild Z is what
  strength means for a normal map. The sampling pass leaves that chain intact and feeds the `Normal Map`
  node's UVs input with the shared UV transform, so a normal map is tiled and offset like every texture.

### Fixed
* **The asset search box rebuilt the whole list on every keystroke, in a quadratic layout pass** — typing
  in *Search assets...* on a long source (1,046 packs / 6,564 files, 869 packs / 14,608 files) stalled the
  editor, and every other list change cost the same. `RebuildAssetList` disposed every row and built a new
  one per item, and a row is a *container* (`ListRow` is a `Label`, which is a `ContainerControl`, carrying
  four labels) — so one keystroke created, laid out and destroyed ~5,200 controls. Worse, the list panel
  starts layout-**locked** (the `ContainerControl` ctor, which is why the first build is quick) and the
  rebuild *unlocks* it, so from the second rebuild on every child added, shown, hidden or resized makes Flax
  re-layout the whole list: `ContainerControl.PerformLayout` locks the subtree, re-bounds every child, lays
  out every child, and the `VerticalPanel`'s `AutoSize` resize then notifies every child again. Once per row,
  for N rows, is quadratic. Now the refill runs with the list's subtree locked and the layout happens once at
  the end (`BeginListLayoutBatch`/`EndListLayoutBatch`); rows are **pooled** and re-labelled — a pooled row is
  bound once and reads its item from `Control.Tag`, so reuse re-points it at new data instead of stacking
  per-item handlers — and typing is debounced 120 ms through the window's `OnUpdate`, so a nine-character
  query filters once. The filter itself was the other half: `fileMatch` was computed even when the pack name
  had already matched, so every keystroke scanned every file of every pack and allocated two lower-cased
  strings per file, and the install record was a linear `FindInstalled` **twice per row**. The searchable
  text of a pack is now one cached haystack matched with `IndexOf(OrdinalIgnoreCase)`, its file types a
  cached bitmask, and its install record comes from one pass (`SettingsService.BuildInstalledIndex`).
  **Measured:** the layout cost is modelled from the engine's decompiled layout code (`ContainerControl`,
  `VerticalPanel`, `Panel` of the 1.13 assembly) — 1,046 rows: **54 ms → 0.4 ms** per rebuild, 2,500 rows:
  **334 ms → ~1 ms**, and the shape stops growing; the filter on the real manifests: 0.75-1.0 ms and
  1.7-2.6 MiB of garbage per keystroke → **0.03-0.5 ms and ~0 B**; 1,046 install records: 6.3 ms per rebuild
  → one pass. Compiles clean against the 1.13 editor assemblies, 0 warnings; the shape of the fix is
  confirmed in a live editor on a 1,046-pack source (D18).
* **A generated material renders as soon as it is created** — installing a texture pack built the material
  and assigned it to the pack's models, but it rendered **pure black** until the user reloaded it by hand in
  the Content browser. `Material.SaveSurface` writes the asset; what the renderer binds is the *loaded*
  material, and the material generator produces its shader, its public parameters and its
  `MaterialInfo.UsageFlags` only during asset load — so a material created in the running session kept the
  engine's default graph: no parameters, `UsageFlags = None`, nothing feeding BaseColor.
  The plugin's one surface writer (`MaterialGraphBuilder.MaterialSurfaceOwner.SurfaceData`) now saves and
  then reloads the asset — waiting for the load, and throwing if either step fails so the install reports it —
  the same shape as the stock material editor's window. Install, `Rebuild material` and a reinstall over an
  existing material all write surface data through it, so the cases where the asset is already in memory are
  fixed by the same rule. Measured in a live editor against an installed texture pack: with the reload taken
  out the loaded material reports no parameters and `UsageFlags = None`; with it, the checks are clean (D17).
* **`Core/Tests/MaterialReadyE2ETest.cs`** — a live-editor check that the graph the plugin saved is the graph
  the loaded material shows (runtime gate `FLAX_AM_MATERIAL_E2E=1`), so this regression cannot slip back in.
* **`Update Plugin` finds its own folder, and stops assuming its name** — the button resolved the plugin's
  folder by walking up from the running assembly to a file named `FlaxAssetManager.flaxproj`, falling back to
  `Plugins/FlaxAssetManager`. That is an assumed folder name, and the manager's own `pluginProject` install
  creates a folder named from the **git URL**, so an installed plugin failed with *"`FlaxAssetManager.flaxproj`
  not found next to the running assembly, and not at `Plugins/FlaxAssetManager`"* — as did a renamed folder,
  a checkout under a different name, and a fork. Discovery now reads the tree (`Core/Utils/FlaxProjectLocator.cs`):
  a plugin is identified by its project file — the file name, or the `Name` field inside it — through the
  project folder that built the running assembly, the `.flaxproj` the **open project references**, the folder
  named in this plugin's `.installed.json`, a bounded shallowest-first scan of `Plugins/`, the documented
  location, and last the project the assembly was built into, which is the only route that finds a fork that
  renamed the project file as well. Identity is checked on every candidate, the host project's own project
  file is never taken for the plugin's, and the containment rule of D14 is unchanged: the result must sit
  inside `Globals.ProjectFolder`, and the confirm dialog still names the exact folder before anything is
  written (D16).
* **The source picker counted an index's linked sources** — every `ComboBox` entry is the source's display
  name plus a ` (N)` count of what selecting it lists, and `OnFetchedMainThread` read `Manifest.Assets` for
  every source alike. An index manifest has no `assets[]` (D13), so a master index read **`Master index
  (local) (0)`** while the list under it read **`Linked sources (15)`** — the count said the source was
  empty in exactly the case where it is a table of contents. The count now branches on `Manifest.IsIndex`
  and reads `entries[]` for an index, the list `RebuildIndexList` fills — the same branch `AssetSourceManager`
  already uses when it logs a fetch — so picker and list header agree; a pack manifest still counts
  `assets[]`, and a failed source still reads `(error)`. **Tested:** compiles clean against the 1.13 editor
  assemblies, 0 warnings; the `(0)` over a 15-entry index is from a live run (the reported screenshot).

## [1.0.0]

First public release.

### Added

* **Asset browser in the editor** — `Window → Flax Asset Manager`: searchable list, category/type
  filters, details panel, preview images, variant picker, install status per pack.
* **Manifest sources over HTTPS** — add any URL that serves a manifest JSON; sources stay separate,
  each is fetched, validated and merged into one browsable list. Whole manifest is rejected at the
  first schema violation, so a malformed source never half-installs.
* **Index (master) manifests** — `"manifestKind": "index"` with `entries[]` linking other manifests
  (see `example_index.json` and `docs/MANIFEST_SPEC.md`). One URL becomes a table of contents the owner
  updates freely; the user picks entries with **Add linked source**. An index never installs anything,
  and trust is per URL, never transitive.
* **Installs** — files into `Content/`, `Plugins/`, `Source/`, `Data/`; git-cloned plugin projects with
  automatic `.flaxproj` reference injection; zip archives (`archivePack`) extracted with a zip-slip
  guard; models imported to `.flax` on install; parallel downloads with per-file SHA-256 verification
  when the manifest publishes checksums.
* **Generated PBR materials** — texture role detection (Albedo/Normal/Mask/Roughness/Ambient Occlusion/
  Height/Emissive), a material asset whose surface graph is built through the public editor API, UV
  scale/offset parameters, normal-map rewiring and material-slot assignment on installed models.
* **Update tracking and uninstall** — an install record per project (`.installed.json`), *Update
  available* badges from version comparison, and uninstall that removes installed files and the plugin
  folder for a `pluginProject`.
* **Trust gates** — a pack that lands compiled code (`Script` files, or a `pluginProject` clone) shows
  a warning in the details panel and requires an explicit confirmation naming the host, destination and
  files before anything is written.
* **Safety limits** — git invocations pass arguments as an argument list with a `--` end-of-options
  marker (no shell string), refs and commit ids are validated, git URLs are https-only, downloads stream
  to disk with a size cap, and every write path is checked for containment inside the project. A
  `pluginProject` clones into a staging folder and is validated there before it replaces anything, and it
  will not touch a `Plugins/` folder it did not install. Every `archivePack` entry name is checked before
  extraction: an entry that would deliver a script, executable or native library fails the install, and
  non-asset files the archive carries are skipped.
* **Docs for manifest authors** — `docs/MANIFEST_SPEC.md` (full format, including index manifests),
  `example_manifest.json`, `example_index.json`, `docs/DECISIONS.md` (design decisions and their
  trade-offs).
* **Compatibility CI** — `.github/workflows/compat.yml` compiles every module against an official Flax
  release's C# API (`compat/`), so drift toward a locally patched engine fails loudly.
* **Repo conventions** — `.gitattributes` (LF in the index, binaries never normalised or diffed) and
  `.github` issue forms for bugs and manifest/pack reports, plus a pull-request template that lists the
  real gates (compat build, material E2E, window-close check, docs in the same commit).

### Fixed

* **GUI updates after `await` ran on a worker thread.** The engine installs no `SynchronizationContext`,
  so install, install+material, remove, preview, preview-image and refresh handlers resumed off the main
  thread and wrote to editor GUI controls, engine assets and the preview scene graph there — the source of
  intermittent glitches, preview leaks and "not on main thread" engine assertions. All of them now hop
  back once through `MainThreadDispatcher.RunOnMain`, and installer progress/log reports are queued to the
  window's main-thread update instead of going through `Progress<T>` (which also resumes on the pool).
* **A `pluginProject` install could delete a folder it never created** — the destination derived from the
  manifest's URL basename was deleted *before* the URL, ref and commit were validated, so an unreachable
  repo, a bad ref, a repo without a root `.flaxproj`, a name colliding with a plugin you added by hand,
  `Plugins/Flax`, or the manager's own folder all cost you a tree. Installing now validates first, clones
  into staging, and only swaps a tree it owns.
* **`archivePack` bypassed the extension rules** (archive entries carry no manifest `type`) and was
  reported as *not installing code*, so an archive could land `Startup.cs` in the content tree without a
  confirmation. Entries are now name-checked up front (code refused, junk skipped).
* **Closing the window left the install+material flow running** — its cancellation token was never
  cancelled, so it kept touching engine APIs after the window was gone.
* **Unbounded reads and weak id uniqueness.** A manifest, an index, a linked manifest, a preview image
  and a preview audio file were downloaded with no size limit (only pack files were capped), and two
  packs sharing a `packId` were accepted, which merges their install records. Reads are now capped
  (8 MiB manifest, 16 MiB index, 64 MiB preview media) and duplicate `packId`s are rejected like
  duplicate index entries already were.
* **Uninstall ignored the window.** It accepted a cancellation token and the window passed none, so
  closing the window left a delete running against engine assets with nobody left to report to.
* **OS integration was the loose side of the path rules.** Install writes were contained to the project;
  the read side was not: opening the source list shell-executed whatever path `Settings.json` named, and
  the website button handed any manifest URL to the OS handler resolver. Files outside the project, and
  extensions a shell would run (`.exe`, `.lnk`, `.reg`, …), are now refused; website links are https/http.
* **Settings destinations were barely validated.** A hand-edited `contentDestination` of `C:foo`, a UNC
  share, a bare `/` or a drive root turned installed paths into absolute ones outside the project, and
  the `..` filter rejected content named `brick..png`. Destinations now refuse drive hints, UNC paths,
  roots and traversal; pack folder names are sanitised the way source ids already were.
* **Hard-coded material-graph ids are now checked against the engine at runtime.** The box ids this
  builder writes to are engine-internal API, so the compat build proved they compile, not that they still
  mean the same thing. Every material build verifies the root node's input boxes (presence, direction,
  value type) from the engine's own box metadata and refuses to wire a graph on mismatch.

### Known limitations

Tracked in `README.md` (→ *Known limitations*) and `docs/DECISIONS.md`: role detection for
gloss/specular, cavity, curvature and ID maps leaves textures unused; uninstall does not remove the
generated material or imported model assets; no retry/backoff or HTTP caching; `sourceId` stability is
the manifest author's responsibility.

[1.0.0]: https://github.com/envision3d/FlaxAssetManager/releases/tag/v1.0.0
