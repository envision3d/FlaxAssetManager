# Changelog

All notable changes to **FlaxAssetManager** are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html). The version the editor shows is the
`Version` field of `FlaxAssetManager.flaxproj`; release tags are `v<major>.<minor>.<patch>`.

## [Unreleased]

### Added
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

### Fixed
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
