# FlaxAssetManager

**Browse, install, and update third-party asset packs from inside the Flax editor.**

Flax has no package manager. Texture sets, HDRIs, models, audio, icons, scripts and whole plugin
projects — wherever they are published — get downloaded by hand, unzipped by hand, dragged into the Content
tree by hand, and re-downloaded by hand when they update. FlaxAssetManager gives you a single
editor window: register manifest URLs, browse what they offer, and install updates with one
click. The manifest format is a plain JSON file any host can serve — publishing a pack is
publishing a URL.

## Features

* **Source registry** — register manifest URLs; the list starts empty and you point it only at sources
  you trust (see *Authoring and hosting your own manifests*). Enable, disable, rename, remove, and
  bulk-edit the list in `Sources.json`.
* **Master manifests** — an *index* manifest (`manifestKind: "index"`) is a manifest that links other
  manifests, so one URL is a table of contents its owner keeps updating. Browse it in the window and add
  the entries you want with **Add linked source**; an index never installs anything itself.
* **Unified browser** — packs from every source in one list: search, type filter, variant
  dropdown (resolution/quality tiers), install-status badges
  (Installed / Update available), download counts, license and credit
  metadata per pack.
* **Previews** — the detail panel renders the pack's `previewUrl` (WebP is auto-resolved to a
  PNG fallback because the engine's image decoder has no WebP support), and audio files listed in
  a pack can be played in-window (downloaded to a temp cache in
  `Content/FlaxAssetManager/.previews/`). No thumbnail grid — one main image per pack.
* **Install / Update** — one action, for every pack type. `assetPack` manifests download each file
  with SHA-256 verification (6 parallel, 60 s timeout), strict path validation, per-file install
  tracking, and **automatic model import** (`.fbx/.obj/.blend` are handed to the editor's import
  pipeline, so you get real Flax assets, not loose files). The button reads **Install**,
  **Update** (a newer version is published) or **Reinstall**, and **Remove** appears only while
  something is installed.
* **Material, built in** — for packs with PBR texture sets (the `MaterialTextures` file type), the
  same **Install** action also builds a real `.flax` material: auto-detects PBR roles from file
  names (basecolor / normal / metallic / roughness / AO / emissive / height / opacity, including
  combined ARM maps), wires the surface graph, assigns it to the pack's model slots, and exposes
  **Color**, **EmissiveColor**, **NormalStrength**, **UV Scale** and **UV Tiling** as public
  material parameters. There is no separate "material" button: a texture pack without its material
  is a folder of images, so the pack decides, and the details panel tells you when it will.
* **Plugin packs** — `pluginProject` manifests (git repo + branch + pinned commit) clone
  into `Plugins/<name>` and register in your `.flaxproj`.
* **Uninstall** — deletes the recorded files *and* the assets the install produced (imported
  `.flax` models, the generated material, texture sidecars and their derived-data folders),
  prunes now-empty folders including the pack root, strips the `.flaxproj` reference of a plugin
  pack, and removes the install record.
* **Open website / edit source list** — the selected manifest's website opens in your browser, and
  the source list opens in your OS text editor (files inside the project only; see safety limits).

## Install

In the Flax editor: **Tools → Plugins → Clone Project**, and in the popup fill in

- **Git Path** — `https://github.com/envision3d/FlaxAssetManager.git`
- **Name** — `FlaxAssetManager` (blank takes the name from the URL; the folder must not exist yet)

Press **Clone**. The editor clones into `Plugins/FlaxAssetManager`, adds the plugin's `.flaxproj` to
your project's `References` itself, and reports *“FlaxAssetManager has been successfully cloned.
Restart editor for changes to take effect.”* Restart the editor, then open
**Window → Flax Asset Manager**.

Notes on that flow (it is the engine's own [Plugins window](https://docs.flaxengine.com/manual/scripting/plugins/plugins-window.html)):

* `git` must be on your PATH — the editor runs it.
* If your project is itself a git repository, the plugin is added as a **git submodule** instead of a
  plain clone; that is engine behaviour, not something the plugin controls.
* The plugin needs nothing else: the `EKEditorUIKit/` widget kit it is built on is nested inside it
  and referenced plugin-relatively, so a clone is the whole install. The `.flaxproj` sits at the
  repository root, which is what **Clone Project** looks for.

Prefer doing it by hand? Copy this folder to `Plugins/FlaxAssetManager` and add to the `References`
of your root `.flaxproj`:

```json
{ "Name": "$(ProjectPath)/Plugins/FlaxAssetManager/FlaxAssetManager.flaxproj" }
```

Then restart the editor.

## Requirements

**Flax Engine 1.13** — pure C#, editor-only plugin, no native code. `MinEngineVersion` is **1.13.7012**,
the official release every module is compiled against in CI (`compat/README.md`); older 1.13 builds may
work, that is not tested.

Every feature runs on a stock 1.13 editor build — browse / install / update / uninstall, model
import, previews, plugin packs, and the material creation that runs as part of installing a
texture pack.

The material pipeline needs no engine patch: it creates the material asset with the engine's own
`Editor.CreateAsset("Material", path)` (the same creator the content browser uses) and builds the
surface graph through the public editor Visject API in
`Source/FlaxAssetManagerEditor/Core/Utils/MaterialGraphBuilder.cs` — the same node API the plugin
already uses to rewire the graph (texture / Sample Texture / Unpack Float4 / Derive Normal Z
chains, UV parameters).

## Usage

> **Only add manifest URLs you trust.** A manifest tells the plugin what to download, extract, clone, and
> — for `Script` files — what C# your project compiles. The plugin checks paths, file types, sizes and
> optional checksums; it cannot tell you whether a source is honest. Treat a manifest URL like a package
> registry you decide to trust.

**Add a source** — **Add URL**, paste a manifest URL, **Refresh**. The manifest's `sourceName`
becomes the source entry.

**Browse** — search by name, description, category, file path, or file type; filter by type;
pick a **variant** (e.g. `2K` / `8K` / `256px`) in the detail panel when a pack offers tiers — each
variant installs into its own `files/<variant>/` subfolder, and the panel shows the exact folder the
current selection writes to.

**Install a pack** — select it, press **Install**. Progress shows per file; the install record
drives the **Update available** badge on later refreshes, and the same record decides the button's
label (**Install** / **Update** / **Reinstall**) and whether **Remove** is offered — for texture,
model, script, audio, archive and plugin packs alike.

**Texture packs get their material too** — for packs whose files are typed `MaterialTextures`,
**Install** also imports the textures with role-appropriate import settings, creates the material,
rewires the surface graph (`Texture → Sample Texture`, ARM maps split via `Unpack Float4`, normal
maps decoded with `Derive Normal Z`), and assigns it to the pack's model slots. The button's tooltip
says when this will happen, and the details panel warns when a pack carries textures the material
will not use (gloss, specular, cavity, curvature and ID maps have no role — see
[`docs/DECISIONS.md`](docs/DECISIONS.md) D5).

**Remove** — deletes the recorded files and the assets the install produced: imported `.flax`
models, the generated `M_<packId>.flax`, the `.flax` sidecar and derived-data folder of every
imported texture, then prunes folders that become empty (including the pack root), strips the
`.flaxproj` reference for plugin packs, and drops the install record. A file the editor has locked
is reported, not silently skipped, and closing the window cancels the deletes.

**Open website** — opens the manifest's `website` (or `homepageUrl`) in your browser; only http and
https links only — the link is manifest data, and the OS is asked for *any* handler, so a `mailto:`
or `file://` link in a manifest gets refused rather than opening a mail composer or a file manager.

**Edit source list** — opens `Content/FlaxAssetManager/Sources.json` in your OS text editor, then press
**Refresh** to apply the edits. Only files inside the project folder are handed to the OS, and
extensions a shell would *run* are refused.

**Update the plugin itself** — **Update Plugin** (far right of the header bar) updates this plugin in
place: `git pull --ff-only` in this plugin's own folder, then a scripts recompile, because the plugin's
assemblies are built by Flax rather than shipped in git, so a pulled source tree only takes effect after a
rebuild. That folder is **found, not assumed** — the plugin's `.flaxproj` identifies it (by file name, or by
the `Name` inside it), and the open project's own `References` say where it lives — so an install in a folder
named after its git URL, a renamed folder, or a dev checkout all work. A confirm dialog states both steps
first — a scripts reload rebuilds the plugin's assemblies while the editor runs, and this window is disposed
by it. Nothing is pulled into a checkout with local changes, a detached HEAD, no upstream branch, or a branch
that has diverged from its remote: that is reported and the folder is left exactly as it was, and `--ff-only`
never merges, so a pull cannot rewrite your commits. It
needs `git` on PATH, and it updates sources only.
d05|
**Packs that install code** — a pack with `Script` files, or a `pluginProject` (clones a git repo into
`Plugins/` and adds it to your `.flaxproj`), shows **Installs code: …** in the details panel, which also
names the source host (`From`) and the license. Installing one asks you to confirm, naming the host and
the files; content-only packs install with no prompt.

**What an install will not do** — a manifest never deletes a folder it did not create. A `pluginProject`
clone goes into a staging folder inside `Plugins/`, is checked there (root `.flaxproj` present, requested
ref/commit reachable), and only then replaces the tree; replacing an existing `Plugins/<name>` requires
that a previous install of this manager owns it, so a folder you added by hand — or `Plugins/Flax` — is
refused, and a failed clone leaves the project exactly as it was. An **archive pack** is checked entry by
entry before anything is extracted: one entry that would deliver a script, an executable or a native
library fails the install, docs and config files it carries are skipped, and files written before the
failure are rolled back (see [docs/DECISIONS.md](docs/DECISIONS.md) → D14).

**Limits that bound a bad source** — every read is capped, not just the big ones: a single pack file
is streamed with a 2 GiB ceiling (a truncated download aborts that pack), a manifest is refused above
8 MiB, an index or linked manifest above 16 MiB, preview images and audio above 64 MiB. Pack ids must
be unique inside one manifest — `packId` keys the install folder and the install record, so two packs
sharing one would merge their records. **Uninstall** is cancellable while it runs.

## Destinations

Configurable in `Content/FlaxAssetManager/Settings.json`:

```json
{ "contentDestination": "Content/FlaxAssetManager", "scriptDestination": "Source/FlaxAssetManager", "overwriteExisting": false }
```

Files land in `<contentDestination>/<sourceId>/<packId>/files/<path>` (scripts in
`<scriptDestination>/…`); picking a **variant** puts them one level deeper, in `files/<variant>/<path>`,
so a pack's 1K / 2K / 4K / 8K sets each keep their own folder, and installing one removes the files — and
the imported model asset — of the variant installed before it. Imported `.flax` models and the generated
material land in the pack root.

> `overwriteExisting` (default `false`) keeps existing files: with it `false`, a file that is
> already on disk is not downloaded, not overwritten and not re-imported, and is reported as
> `skipped` in the log.

Sources list in `Content/FlaxAssetManager/Sources.json`. Install tracking in
`Content/FlaxAssetManager/.installed.json` drives **Update available** badges (manifest `version`
vs `installedVersion`).

### Where that state lives

All three files are created and maintained in **your project** (the paths above are relative to
your `.flaxproj`), not inside the plugin — the plugin ships no settings, no source list and no
install record, and recreates them on demand. Installed packs likewise land in your project's
`Content/` / `Source/`. Preview **images** are decoded straight from memory and never written; only
the audio files used by **Preview** are written, into
`Content/FlaxAssetManager/.previews/` (and deleted when playback stops).

Because they are per-project, per-developer state, add them to your **project's** `.gitignore`:

```gitignore
Content/FlaxAssetManager/.installed.json
Content/FlaxAssetManager/.previews/
```

Keep `Sources.json` tracked if you want a shared source list for your team (it is just URLs,
enabled flags and display names); treat `Settings.json` as team config if you standardise the
destinations.

## Authoring and hosting your own manifests

This plugin ships **no** third-party manifests — only `example_manifest.json`, which demonstrates
every file type and `archivePack` with placeholder URLs. A source is nothing but a URL to a manifest
JSON, so a catalog is simply whatever you publish:

1. Write the JSON — the format is specified in [docs/MANIFEST_SPEC.md](docs/MANIFEST_SPEC.md). The
   plugin validates it on load and rejects the **whole manifest** at the first violation (ids,
   relative paths, file URLs, file types/extensions); unrecognised fields are ignored.
2. Host it anywhere over HTTPS — GitHub Pages, a release asset, S3, an internal file server.
3. **Add URL** in the window. Nothing is registered anywhere; the plugin fetches, validates and lists
   the packs.

**A master manifest** — a manifest with `"manifestKind": "index"` and `"entries": [...]` links *other*
manifests instead of listing assets (`example_index.json` in the plugin root is a working shape). Add the
index's URL once and the window lists the linked manifests, each with **Add linked source**, so you publish
one URL that you keep updating and users pick what to take from it. An index installs nothing: entries are
pointers, and every linked manifest is fetched and validated on its own once the user adds it. Entry
`sourceId`s and URLs must be unique inside the index, and adding an entry whose `sourceId` is already
configured is refused — `sourceId` keys the install record. See `docs/MANIFEST_SPEC.md`.

That is the intended sharing workflow: a studio can host one manifest of its own art and point every
project at it; a community can host a curated list of free packs. Keep `sourceId` and `packId` stable
— they are the keys the install record and the *Update available* badges are built from, and a
manifest is remote input, so adding a URL means trusting whoever hosts it (see
`docs/DECISIONS.md` D12).

## Asset licenses you take on

Every pack records its own license in the manifest's `license` field. The plugin shows it and passes
it through; it does not verify it, and the obligation lands on **whoever ships the assets**:

| License recorded | What shipping it obliges |
| --- | --- |
| `CC0`, public domain | nothing |
| `CC-BY-3.0`, `CC-BY-4.0` | attribution in your shipped project (what, from whom, under what license) |
| `Apache-2.0` | keep the license and NOTICE, and give credit |
| `MIT` | keep the copyright and license notice |
| anything else | read it — the manifest field is a claim, not a warranty |

## Manifest format

Short version of a **pack** manifest (one per URL):

```json
{
  "sourceName": "My Assets", "sourceId": "com.example", "sourceVersion": "1.0",
  "assets": [
    { "id": "pbr_pack", "name": "PBR Pack", "packType": "assetPack", "version": "1.0.0",
      "files": [
        { "url": "https://example.com/brick.png", "path": "brick.png", "type": "MaterialTextures" },
        { "url": "https://example.com/brick.fbx", "path": "brick.fbx", "type": "Model", "sha256": "…" }
      ],
      "variants": [ { "id": "8k", "name": "8K", "files": [ … ] } ] },
    { "id": "my_plugin", "name": "My Plugin", "packType": "pluginProject", "version": "1.2.0",
      "distribution": { "kind": "gitRepo", "url": "https://github.com/org/MyPlugin.git", "branch": "main" } }
  ]
}
```

The second kind is an **index manifest**: `manifestKind: "index"` with `entries[]` linking *other*
manifests, so one URL is a table of contents you keep updating (see `example_index.json`). An index
installs nothing; the user adds an entry as a source with **Add linked source**.

Strict rules: raw asset files only (`.cs` is the only allowed `Script` extension;
`.cpp/.h/.md/.txt/.json/.bin/…` are rejected with a hint to use `pluginProject`); no archives as
`assetPack` files; traversal-free relative `path`s; `pluginProject` requires
`distribution.kind:"gitRepo"` and a staged `*.flaxproj`.

* **Manifest format:** [`docs/MANIFEST_SPEC.md`](docs/MANIFEST_SPEC.md)
* **Product intent:** [`PRD.md`](PRD.md) — goals, use-cases, non-goals (deliberately not the implementation spec)
* **Contributor guide:** [`AGENTS.md`](AGENTS.md)
* **Open decisions:** [`docs/DECISIONS.md`](docs/DECISIONS.md) — engine requirement, licensing,
  known architectural debt

## Known limitations

* Installs are **tracked, not sandboxed**: uninstall deletes the recorded files, the assets the
  install produced (the generated material, imported models, texture sidecars) and now-empty
  folders, and never touches untracked files — see `docs/DECISIONS.md` D6. A file the editor has
  locked at that moment is reported and stays behind.
* 6 parallel downloads, 60 s per-file timeout. Manifest fetches retry transient failures with
  backoff and send `If-None-Match`, so an unchanged source costs a 304 (D11); **pack file
  downloads do not retry** — a source that rate-limits you fails that install, and the pack rolls
  back rather than half-installing.
* Downloads stream to disk and are capped at 2 GiB per file; a pack with a single larger file
  fails that file (see D11 for making the cap configurable).
* `sha256` is optional in manifests; packs without it are installed unverified.
* git runs with per-argument quoting and validates the URL (https-only), branch and commit —
  the trust model of a public catalog is still an open decision (D12).
* The UI is a work in progress: pixel-positioned layout, dev-stage polish, not theme-integrated.
  Every post-download update is marshalled to the editor's main thread (D3), but that marshalling has
  had no manual QA pass on a real editor build yet — report anything flaky around install/preview.
* Source URLs are fetched over plain HTTPS with no authentication support.
* Installing a texture pack builds the material by driving the editor's Visject surface headlessly
  (it spawns real material nodes, on the editor's main thread). Open the generated
  `M_<packId>.flax` in the Material Editor to inspect what it built.

## License

MIT — see [LICENSE](LICENSE). Release history is in [CHANGELOG.md](CHANGELOG.md); how to build, test and
contribute is in [CONTRIBUTING.md](CONTRIBUTING.md).

The plugin bundles **EKEditorUIKit** (in `EKEditorUIKit/`) — the editor widget toolkit this window is
built on — under the same MIT license. It is a sibling Flax plugin project referenced from
`FlaxAssetManager.flaxproj` (`$(ProjectPath)` resolves relative to the referencing plugin's folder,
so a clone of this repository is self-contained). The kit is a candidate for extraction into a
standalone UI library later; nothing depends on that happening.
