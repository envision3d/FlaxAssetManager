# FlaxAssetManager — Product Requirements

> **Scope of this file (D9).** This document is *product intent*: what the plugin is for, who it is
> for, what it promises, and what it deliberately refuses to do.
>
> It is **not** the implementation spec. Two other documents are authoritative for their own
> territory, and they are the ones to read before changing code:
>
> | Question | Read |
> |---|---|
> | What may a manifest contain? Field names, types, validation rules, on-disk layout | [`docs/MANIFEST_SPEC.md`](docs/MANIFEST_SPEC.md) |
> | How is the code organised, what are the safety rules, how do I build and verify a change? | [`AGENTS.md`](AGENTS.md) |
> | Why is it built this way, and what is still open? | [`docs/DECISIONS.md`](docs/DECISIONS.md) |
> | What changed, and what is user-visible? | [`CHANGELOG.md`](CHANGELOG.md) |
>
> The first version of this document (Sep 2025) described the pre-split layout, listed enums that
> have since changed, and kept "future work" sections for features that are now implemented. It
> drifted because it duplicated the other two files, so it no longer does: a rule stated in three
> places is a rule that gets one update out of three. If you find a statement here that
> contradicts `docs/MANIFEST_SPEC.md` or the code, **those win** — fix this file.

---

## 1. What This Is

A Flax Engine **editor-only plugin** that installs community content into the project you are
working in. The user adds manifest URLs they trust; each URL is a *source*; the window lists the
packs that source publishes and installs them into the project's `Content/`/`Source/` folders.

Three kinds of pack, because upstream content comes in those three shapes:

- **Asset packs** — explicit lists of raw asset files behind plain HTTP URLs.
- **Plugin projects** — whole Flax plugin projects, cloned from a git repo.
- **Archive packs** — `.zip` downloads from sources that only ship bulk archives.

Plus a fourth thing the plugin does that is not an install format: for a PBR texture pack it
**creates a material** and wires the downloaded textures into it, because a folder of textures is
not something you can drop into a scene.

The plugin exists so that "use community assets" is not a manual ritual of finding git URLs,
copying files into the right folders, and hand-wiring a material graph.

## 2. Goals

| # | Goal |
|---|------|
| G1 | One-click install from an author-hosted manifest: files land, models import, and a texture pack arrives with a working material. |
| G2 | Per-source browsing. Each manifest URL is its own source with its own id; sources are never merged, so provenance stays visible. |
| G3 | The user supplies the URLs. Add, remove, enable, edit; persisted per project. The plugin ships no catalog and advertises no sources. |
| G4 | Destinations are configurable, with sane defaults (`Content/FlaxAssetManager`, `Source/FlaxAssetManager`). |
| G5 | Install state is recorded, so "installed", "update available", and "remove" are all answerable from the record. |
| G6 | Strict, fail-fast validation of remote input. No silent fallbacks, no half-installs: a pack that fails validation is not installed. |
| G7 | A removal is a removal: uninstall deletes what the install produced — downloaded files, imported model assets, the generated material — not just a bookkeeping entry. |
| G8 | Clean layering (Models / Services / UI) so a change stays in one place. |

## 3. Non-Goals (explicitly out of scope)

* Private/authenticated sources, payment, licensing enforcement, access control.
* Background polling, auto-update on launch, telemetry.
* A curated catalog, a hosted index, or a source registry (D2): the plugin ships a format, not a
  content policy.
* Loose C++ as an asset pack. Native code arrives as a plugin project, which the user must
  explicitly confirm (it compiles into their project).
* Package/cooker integration, runtime asset streaming, multiplayer.
* A general-purpose package manager with a lockfile, transitive dependencies, or semver ranges.

## 4. Users & Use-Cases

**Primary user:** a Flax developer who starts a project and wants community assets and plugins
without hand-assembling them from git URLs and forum threads.

* **UC1 — Discover.** Open the window, pick a source, filter/search, read the pack's id, version,
  type, category, license, and file list.
* **UC2 — Install.** One **Install** button. Files download, validate, land in the project, models
  import, and for a texture pack a material is created from them. **Remove** takes all of it out.
* **UC3 — Install a plugin.** `git clone` into `Plugins/`, reference injected into the project's
  `.flaxproj`, restart hint shown. A pack that delivers code says so on the details panel, and the
  install asks before it happens.
* **UC4 — Keep it current.** Refresh re-reads sources (an unchanged source costs an HTTP 304); the
  button becomes **Update** when the author bumps the version.
* **UC5 — Manage sources.** Add, rename, remove, enable/disable. Edit the list in the system editor
  for bulk changes.
* **UC6 — Author a source.** A content author writes a manifest, validates it against
  `docs/MANIFEST_SPEC.md`, hosts it anywhere over HTTPS, and points users at it.
* **UC7 — Update the plugin itself.** **Update Plugin** pulls the plugin's own git checkout
  (`git pull --ff-only` in `Plugins/FlaxAssetManager`) and asks the editor to recompile the scripts, so
  the new code is what the editor runs. Manual, confirmed, and refused before writing when the checkout
  is not a clean branch that tracks a remote (D16). It is not UC4: the plugin is not a pack, has no
  version badge, and nothing is polled.

## 5. Product rules the implementation is built around

These are the promises that outlive any particular refactor. Each links to the decision record
that argues for it.

1. **Remote data is input, never authority.** A manifest can name paths and URLs; it cannot make the
   plugin write outside the project, run a shell command, or open an arbitrary handler.
   Guards: path containment on every write and delete, https-only git URLs, validated ref/commit
   names, per-argument process spawn, bounded read sizes, archive-entry triage. ([D11](docs/DECISIONS.md), [D12](docs/DECISIONS.md))
2. **Installs are atomic.** A pack that fails validation leaves nothing behind; a plugin install
   swaps into place and renames back on failure. ([D4](docs/DECISIONS.md), [D12](docs/DECISIONS.md))
3. **One action per decision.** Install does the whole job for the pack type; Remove undoes it. The
   user is not asked to pick between "download" and "install", nor to notice that a material needs a
   second click. ([D15](docs/DECISIONS.md))
4. **The plugin never surprises the project.** Every write is under a configured destination inside
   the project; nothing is deleted that the install record does not name.
5. **Limits are stated, not implied.** A texture pack that has gloss/cavity/ID maps says how many
   of its textures the material will not use; a pack that installs code says so; a source over a
   size cap says so. ([D5](docs/DECISIONS.md))
6. **Trust is the user's, per URL.** The README opens with "only add manifest URLs you trust", the
   plugin ships no catalog, and the safety features are mechanical, not judgment. ([D12](docs/DECISIONS.md))

## 6. Success criteria

The plugin is done, from a product standpoint, when:

* a fresh project can install a real community texture pack and render it with the generated
  material, without editing the manifest by hand;
* installing the same pack twice, installing a broken manifest, removing a pack, and closing the
  window mid-install all leave the project in a state the user can explain;
* a content author can publish a source from the spec alone, and a user can add it by URL;
* the plugin compiles against a **stock** engine at its supported version, unmodified
  (`.github/workflows/compat.yml`);
* the manual QA list in [`CONTRIBUTING.md`](CONTRIBUTING.md) passes end to end in the editor.

## 7. Where this is expected to grow

Open decisions with recommendations live in [`docs/DECISIONS.md`](docs/DECISIONS.md) — that file, not
this one, is where "what next" is tracked. The standing product-level items there today: role
coverage for gloss/cavity maps (D5), `sha256` policy for published catalogs and which sources are
advertised (D11, D12), and the fate of the vendored UI kit (D10).
