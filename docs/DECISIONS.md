# Open decisions — FlaxAssetManager

Decisions the code cannot make for itself. Each entry: the situation, the options, my
recommendation, and the blast radius. Numbered so they can be referenced from commits/issues.
Items already resolved without needing a judgement call are in `AGENTS.md` →
"Fixed" (and the changelog of the commit that lands them).

---

## D1 — Material creation lives in the plugin, not the engine · **resolved**

The plugin owns the whole feature: `Core/Utils/MaterialGraphBuilder.cs` creates the asset with
`Editor.CreateAsset("Material", path)` (the engine's own content-browser creator) and builds the PBR
surface graph through the public editor Visject API. A stock 1.13
`Binaries/Editor/Linux/Development/FlaxEngine.CSharp.dll` exposes `Editor.CreateAsset(string,string)`
and nothing that creates a material with its textures already connected, so the wiring lives this
side. Verified by compiling the editor module against that assembly from an official release.

**The guardrail:** every engine symbol this plugin calls must exist in the Flax 1.13 *release*
sources — the `compat/` CI build against the official editor assembly (`-warnaserror`) enforces it.
This document names no engine symbol that is absent from a release, on purpose: naming them has
twice led code back to them.

Implemented in plugin code (it is material *policy*):

- texture nodes → root boxes `Color 1 / Emissive 3 / Metalness 4 / Roughness 6 / AO 7 / Normal 8 /
  Opacity 9 / PositionOffset 11` (Height), plus `Normal Map (5,4)` for the normal map;
- public parameters `Color` (white), `EmissiveColor` (black = no emission; drives the Emissive input
  directly when the pack ships no emissive map) and `NormalStrength` (1);
- the created asset id, read back with `Content.GetAssetInfo` instead of a native out-param.

Consequences: material creation is enabled from the pack's `MaterialTextures` again (the
force-disable in `FlaxAssetManagerWindow` is gone — today it runs inside the one **Install** action
for such a pack), and every engine call in the material pipeline
is marshalled to the editor main thread — the D3 pattern, applied here first because a Visject
surface is editor GUI state, not a background-safe API.

Unchanged residual risk: the hard-coded Visject group/type ids and box indices are
engine-version-sensitive — they moved from the importer into `MaterialGraphBuilder.cs`.

---

## D2 — Licensing (plugin + vendored UI kit + catalog) · **resolved**

**Decided:** MIT, `Copyright (c) 2026 envision3d`, for everything this side of the assets.

* Plugin: `LICENSE` (MIT) added; `FlaxAssetManager.flaxproj` now carries `Company`, `Copyright`,
  `MinEngineVersion: 1.13.7012`; the plugin description fills `Author` / `AuthorUrl` /
  `HomepageUrl` / `RepositoryUrl` (the editor's Plugins window shows these).
* `EKEditorUIKit/` is the author's own code — the kit's sources carry **no** third-party copyright
  headers (verified file by file), so it is licensed under the same MIT license. `EKEditorUIKit/README.md`
  states the license, the vendoring mechanism, and the intent to possibly extract it into a standalone
  UI library later. No separate license file is needed inside the kit because nothing is being
  redistributed from someone else.
* The **catalog** (manifests for third-party sources, plus the scraper) lives in a separate
  repository that stays private and is **not referenced from this plugin repository** at all — no code,
  no README, no docs. The plugin ships only `example_manifest.json` and documents authoring and
  hosting your own manifests (README → "Authoring and hosting your own manifests"). The catalog is MIT
  with an explicit **asset carve-out**: manifests link to assets, they never ship them, so each source
  keeps its own license and the obligations land on the **end user** who ships them — CC-BY-3.0 /
  CC-BY-4.0 (attribution in the shipped project), Apache-2.0 (keep license/NOTICE + credit), MIT (keep
  the notice). The plugin README states those families **in this repository**, so users are not sent
  elsewhere to learn their duties.
* Identity: `Author` / `AuthorUrl` / `HomepageUrl` / `RepositoryUrl`, both `.flaxproj` `Company` /
  `Copyright` fields, and the README point at `envision3d` / `github.com/envision3d/FlaxAssetManager`.

**Remaining:** create the public GitHub repository for the plugin and commit. The catalog repository
stays **private** and keeps zero references from this plugin. Two follow-up tickets: a "copy credit
line" action in the detail panel (built from the manifest's `license` + `author`/`credit` fields),
which turns the CC-BY/Apache attribution duty into a one-click for users; and the `sourceId`
namespace question — ids built from a third party's own reverse-DNS domain are not ours to claim and
are permanent per the spec, so a catalog-owned prefix (`net.<owner>.catalog.*`) is cheaper to adopt now
than later (see D12).

---

## D3 — GUI mutation after `await` happened off the main thread · **resolved**

The engine installs **no `SynchronizationContext`** (verified: no `SynchronizationContext` anywhere
in `Source/Engine`), so every `await` continuation resumes on a thread-pool thread. Six
`async void` handlers in the window used to touch GUI controls, engine assets and settings files
after resuming:

`RefreshSourcesAsync:726`, `LoadPreviewImagesAsync:1430`, `OnInstallClicked:1641`,
`OnInstallMaterialClicked:1700`, `OnPreviewClicked:1801`, `OnRemoveClicked:2067`
(plus `ImageView`, fixed earlier by routing its texture decode through `Scripting.RunOnUpdate`).

Symptoms to expect: rare GUI glitches/deadlocks on install completion, preview leaks, and
engine "not on main thread" assertions — all inherently flaky, so they surface as "sometimes
breaks".

**Decided: option (2) — one dispatcher, and one hop per flow.**

* `Core/Utils/MainThreadDispatcher.cs` wraps `Scripting.RunOnUpdate`, the hop the engine's own
  scripting updates use. Convention: **I/O on the pool, then hop once and do all engine/GUI work
  inside one lambda.**
* Every handler above now ends in a single `await MainThreadDispatcher.RunOnMain(...)` tail —
  `FlaxAssetManagerWindow.cs:1543`, `1678`, `1743`, `2111`, and `1869` / `1933` / `1944` / `1965`
  for the preview. The preview hops in stages because the content refresh, the asset-registry lookup
  behind `Content.LoadAsync` and the preview scene graph are all main-thread APIs, while
  `AudioClip.WaitForLoaded` (blocking) deliberately stays off the main thread.
* Fire-and-forget reports cannot use that hop: there is nothing to `await`, and `Progress<T>`
  captures the (absent) context, so it resumes on the pool. The installer's `Progress`/`Log` events
  are queued into a `ConcurrentQueue<Action>` drained by the first line of `OnUpdate` (`758` →
  `DrainMainThreadCalls`, bounded at 64 per frame so a long install cannot starve the editor), and
  `WindowProgress:830` replaces every `Progress<T>` the window used to build.
* Queued updates are dropped once `IsDisposing` is set, and every marshalled tail re-checks it, so a
  late report cannot touch controls that are being disposed.
* `MaterialCreatorService` already marshalled through a private `RunOnMain`; it now delegates to the
  shared helper (`MaterialCreatorService.cs:201`), so the plugin has one convention.
* `RefreshSourcesAsync` keeps its pre-existing pattern (`Interlocked` flags → `OnFetchedMainThread`
  from `OnUpdate`) — the same idea, already main-thread safe.

**Remaining:** a QA pass on an editor build. GUI threading cannot be exercised headless, so the
behaviour to check by hand: install a pack, install + create material, preview audio while switching
packs/sources, remove a pack, and close the window mid-install. `FLAX_AM_E2E=1` (`VariantE2ETest`)
drives install and material creation inside the editor as the regression harness.

---

## D4 — E2E test ships inside the release assembly · **resolved (runtime gate accepted)**

`Core/Tests/VariantE2ETest.cs` is compiled into the editor module. Two changes made it safe to keep:

* It is now **source-agnostic**: it fetches whatever sources the user has configured, picks the first
  pack offering at least two variants, and derives every expectation from that manifest - variant ids,
  file sets, variant display names, the `SelectedVariants` key. No source id, pack id, variant id,
  filename, host or pack count appears in the file, so an upstream manifest change cannot make it wrong
  and adding a source never requires editing the test. If no fetched pack offers two variants, the
  variant checks report as skipped, not failed.
* The gate stays at **runtime** (`FLAX_AM_E2E=1`). A compile-time gate was attempted and is verified
  **unavailable for plugin modules on stock 1.13**: `Module.Setup` -> `options.ScriptingAPI.Defines`
  does not reach a plugin's C# assembly (a deliberate compile error inside `#if FLAXAM_E2E` was not
  reported by the builder), and `Target.GlobalDefinitions` only feeds native
  `CompileEnv.PreprocessorDefinitions`. Shipping a build symbol that no supported path can define
  would be worse than an honest runtime gate.

It remains the only automated end-to-end check: ~300 lines, one entry point, inert without the env
var. If Flax later adds a C# define path for plugin modules, wrapping the file in `#if` is a one-liner.

---

## D5 — Material role coverage · **warning implemented, coverage open**

`MaterialRoleDetector` maps BaseColor / Normal / Metallic / Roughness / AO / Emissive / Height /
Opacity (+ combined ARM). No patterns for **gloss/specular, cavity, curvature, ID/zone maps** —
those files get role `None` and are silently skipped by the material builder.

Decide: (a) leave as is and surface a UI warning "N textures not used by the material"; (b) add
gloss→Roughness (with the 1−x caveat, which needs a `1 - x` node in the graph); (c) add cavity→AO
multiply. Recommendation: (a) now (cheap, honest), (b)/(c) only if real packs need it.

**Implemented (a).** The details panel states it: `FlaxAssetManagerWindow.CountUnusedMaterialTextures`
counts the pack's `MaterialTextures` whose role detects as `None` at the selected variant, and
`SelectPack` appends "N texture(s) unused by the material (gloss/specular/cavity/curvature/ID maps
are not wired)" to the status line, in the same accent colour as the "installs code" affordance. The
number is recomputed per variant, because a variant can be the thing that carries the gloss map.

Why a warning and not a silent skip: the material looks complete. A user who installed a 9-file
Polyhaven set and sees baseColor/normal/roughness/metallic wired has no way to learn that the
gloss, AO-in-Cavity, curvature and ID maps went nowhere, and will conclude the importer is broken
when the render does not match the source's own viewer. Stating the limit is the cheap, honest fix.

(b)/(c) stay open. Both need a graph node with an argument (a `1 - x` invert for gloss, a multiply
for cavity), and a gloss→roughness conversion is only correct for materials authored as gloss —
getting that wrong is worse than not doing it, because the render changes subtly and the user has
no signal. The trigger for implementing them should be a real pack where it matters, not symmetry
of the enum.

---

## D6 — Uninstall completeness · **resolved (a)**

Uninstall deletes the recorded downloaded files, prunes folders that become empty (pack root kept
while non-empty), strips the `.flaxproj` reference for plugin packs, and removes the record. It
**does not** delete the generated material (`M_<packId>.flax`) nor the `.flax` assets produced by
importing model sources — so an "uninstall" leaves visible assets in the Content tree.

Decide: (a) record generated/imported asset ids at install time and delete them (+ their sidecar
folders) on uninstall; (b) keep as is and state it in the UI ("leaves imported assets") — README
currently states it. Recommendation: (a) — users will judge "uninstall" by what is left behind.

**Implemented (a).** An install record now carries the assets the install *produced*, and uninstall
deletes them:

* `InstalledEntry.GeneratedAssets` (JSON `generatedAssets`) lists project-relative `.flax` assets.
  `AssetInstallerService.ImportModelsAsync` returns exactly the `.flax` files the import pipeline
  wrote (diffed against a pre-import snapshot of the pack root, `NewFlaxAssets`), so a partial
  import records what actually landed. `MaterialCreatorService` appends the generated
  `M_<packId>.flax` through `RecordGeneratedAsset`.
* `AssetInstallerService.DeleteGeneratedAssetsAsync` runs at the end of uninstall: recorded assets,
  then the derived sidecars of every downloaded file the editor imports
  (`AssetInstallerService.ImportedSourceExts`: a recorded `Rock.tga` also deletes `Rock.tga.flax`
  and the `Rock.tga/` derived-data folder). Deriving sidecars matters for installs made before the
  field existed, so an upgrade does not leave the old packs un-cleanable.
* `TryDeleteEmptyPackFolder` removes the pack folder when nothing is left in it, so a removed pack
  does not leave an empty shell in the Content browser. A pack root that still holds files the
  record does not name is left alone.

Constraints kept from the existing safety rules: every delete goes through
`GitSafety.IsUnderRoot(…, Globals.ProjectFolder)` like the `files` loop, a locked or in-use asset
logs a warning and continues rather than aborting the uninstall (the record is removed after, so
the user is told what stayed behind instead of being shown a fake success), and closing the window
cancels the deletes via the shared install token (L7).

---

## D7 — Plugin packaging metadata · **mostly resolved**

`FlaxAssetManager.flaxproj` now sets `Company`, `Copyright` and `MinEngineVersion: 1.13.7012`, and
`FlaxAssetManager.cs:17-20` fills `Author` / `AuthorUrl` / `HomepageUrl` / `RepositoryUrl` — that is
what the editor's Plugins window renders (double-click a plugin's author label to open its URL).
`MinEngineVersion` on a **plugin** is advisory: `Flax.Build` never enforces it, and the editor's fatal
version check (`Editor.cpp:636`) applies to the project you open, not to plugin references.

**Icon: not shipped, by owner decision** (the Plugins window falls back to its default icon; the
convention, if that ever changes, is a square texture at `Content/FlaxAssetManager.Icon.flax`, 128–256
px, inside the plugin directory). The release version is **1.0.0** in both `.flaxproj` files (`PluginInfo`
has no version field), so what remains is tagging `v1.0.0` at the first public commit, so "which version
am I on?" has an answer.

---

## D8 — Publishing hygiene · **complete; publication state open**

No `SECURITY.md` — the owner decided a general caution is enough. The README's *Usage* opens with
**"Only add manifest URLs you trust"**, and D12 documents the trust model, the implemented guards
(https-only git URLs, ref validation, per-argument spawn, path containment, streamed size cap, optional
`sha256`) and the **"installs code"** confirmation gate in the window. Reporting is an ordinary GitHub
issue; *private vulnerability reporting* is deliberately **not** enabled.

**Now present:** `CHANGELOG.md`, `CONTRIBUTING.md`, and CI. `.github/workflows/compat.yml` builds **both**
targets exactly as `AGENTS.md` documents, from a **stock** engine at the supported version (D1 resolved)
— the check that the plugin has not drifted back onto patched engine API — with `compat/` as the
reference-only project it builds.

**Also now present:** `.gitattributes` (LF in the index, binaries never normalised or diffed) and
`.github/ISSUE_TEMPLATE/` bug + manifest/pack forms and a PR template that lists the real gates
(compat build, material E2E, window-close, docs in the same commit).

**Now present:** `.editorconfig` (LF, 4-space C#, 2-space data files, vendored kit and binaries
excluded — it records what the tree already is, so it is drift protection, not a reformat request),
`.github/CODEOWNERS` (marks the vendored kit and the install/material paths; the GitHub address is a
placeholder until the repo exists), and `.github/dependabot.yml` (monthly `github-actions` updates
only — the engine pin is deliberately **not** automated, because the compat build tests a specific
supported version, and the plugin declares no NuGet ranges of its own).

**Still missing:** nothing on the original list. What remains is publication state (no repo, no
commit, no tag), so these files have never been exercised by GitHub.

---

## D9 — PRD rewrite scope · **resolved (shrink)**

`PRD.md` (Sep 20) was materially stale: file map, enum lists, *future work* that is implemented,
no material/preview/test coverage, dev-loop commands pointing at the pre-split layout
(details in `AGENTS.md` → doc status).

Decide: full rewrite (~half a day) vs. shrinking it to product intent + "implementation truth =
`docs/MANIFEST_SPEC.md` + `AGENTS.md`". Recommendation: shrink — one source of truth for the
implementation, PRD keeps goals/use-cases/decisions.

**Implemented (shrink).** `PRD.md` is product intent only now: what the plugin is for, goals,
non-goals, use-cases, the product-level rules the implementation is built around (each pointing at
its decision record), and success criteria. The schema, file map, enum lists, install-flow sections,
build commands and the *future work* list are gone, and the header says plainly that
`docs/MANIFEST_SPEC.md` and `AGENTS.md` win on any conflict.

Why shrink rather than rewrite: the stale sections were not badly written, they were **duplicates**.
The file map rotted when the code moved, the enum list rotted when `MaterialTextures` arrived, the
future-work list rotted as previews and variants landed. A full rewrite would have produced a
fresher copy of the same facts — the same decay with a later start date. `AGENTS.md` (doc-status
note, file map, pointers) and the README links now describe that split instead of flagging
staleness, and a rule stated once cannot be contradicted by its own second copy.

---

## D10 — UI ownership · **vendoring settled (keep vendored)**

The window is ~2,340 lines of pixel-positioned layout on top of the vendored `EKEditorUIKit` (564 lines; ~7,830 lines of C# in the plugin).
Decide whether to keep the vendored kit (upstream it as its own plugin? contribute to Flax's own
UI?), and whether to restructure the window into `Controls/` with anchored layout. Recommendation:
defer the layout refactor, but settle the vendoring/licensing question now (part of D2) because it
affects redistribution.
**Vendoring settled: keep it vendored, MIT, same author, no upstreaming planned.** The licensing
side is already closed by D2 — `EKEditorUIKit/README.md` states MIT, Copyright (c) 2026 envision3d,
same holder as the plugin, so redistribution inside the plugin folder is unremarkable: one license
covers the tree, and the kit is referenced with `$(ProjectPath)` so a clone is self-contained.

The decision recorded here is what *not* to do: not a separate plugin package (a second repo, second
version, and `MinEngineVersion` to keep in lockstep for one consumer), not a PR to Flax's own editor
UI (Flax's editor GUI is a low-level surface with its own release cadence; the kit is a small set of
app-shaped widgets — `FlatButton`, `ListRow`, `KeyValueRow`, `StatusBar`, `Theme` — tuned to this
window). Marked in `.github/CODEOWNERS` so the vendored folder is visibly not ordinary plugin code.

Trigger to revisit: a second consumer appears, or someone else wants the widgets. Until then the kit
has no API-stability promise, which is what the vendoring buys and `EKEditorUIKit/README.md` already
says. The **layout refactor** (window into `Controls/`, anchored instead of pixel-positioned) stays
deferred — it is a large, purely internal diff with no behaviour change to verify against, and the
manual QA list is what protects this plugin, not the compiler.

---

## D11 — Network behaviour · **manifest retry/ETag implemented; pack-download retry open**

Originally: no retry/backoff, no ETag/`If-None-Match` caching, `sha256` optional (so most community packs
install unverified), and the download size cap is a compile-time constant
(`AssetInstallerService.MaxDownloadBytes = 2 GiB`).

Size is now bounded on every read path, not just pack files: `MaxDownloadBytes = 2 GiB` per pack file
(streamed), 8 MiB per manifest, 16 MiB per index/linked manifest, 64 MiB per preview image or audio file
(`HttpStream`). What remains open is the *policy* questions below, not the unbounded-read risk.

Decide: (a) add retry-with-backoff + ETag for manifest fetches (cheap, high value for
`Refresh`); (b) require `sha256` for `assetPack` in the catalog repo (author-side policy, plugin
stays tolerant); (c) expose the size cap in `Settings.json` (per-project policy).
Recommendation: (a) + (b) yes, (c) only if anyone hits the limit.

**Implemented (a) for manifest reads.** `HttpStream` owns the polite path now: a GET goes through
`SendWithRetryAsync`, which retries **408 / 425 / 429 / 5xx**, transport failures, and a timeout the
caller did not request, up to `MaxAttempts = 3`, with exponential backoff from `BaseRetryDelay`
(400 ms) plus jitter, honouring a server `Retry-After` capped at `MaxRetryDelay` (10 s — sleeping
minutes on one source is worse than one extra request). A 404, a 403, or an oversize response is
**not** retried: those are facts about the source, and Refresh fans out over every source, so
retrying them turns a broken experience into a slow one.

`FetchTextAsync` adds conditional GET. `AssetSourceManager` keeps a per-URL `CachedManifest` (the
validator and the body it came with, in memory, behind a lock); a 304 reuses the cached text, and
that status is handled *before* `EnsureSuccessStatusCode`, which would treat it as a failure. The
cache is dropped when the source list changes — add, rename, remove, add-linked-source all call
`FetchAllAsync(force: true)` — so an edit is always a full read, and the Refresh tooltip states the
304 behaviour.

**Weak validators are the normal case, not the edge case.** nginx, Apache and the CDNs answer a
static file with `W/"..."`; that is what `Headers.ETag.ToString()` renders and therefore what gets
cached, and the `EntityTagHeaderValue` **ctor** takes the quoted tag plus a separate `isWeak` flag,
so handing it that rendered header throws `FormatException` ("The format of value 'W/"…"' is
invalid."). Every source then fetched cleanly once per editor session and turned red on every
**Refresh** after that — the error surfaced as a per-source `FetchedSource.Error`, not as a cache
miss. `SendWithRetryAsync` parses with `EntityTagHeaderValue.TryParse` (it understands the `W/`
prefix) and falls back to `TryAddWithoutValidation`, so the validator goes back to the host verbatim.
Weak matching ignores the indicator (RFC 9110 §8.8.3.2) and nginx answers a 304 with the strong form
of the same tag, so a strong/weak ping-pong costs nothing.

**Not done, on purpose: pack file downloads do not retry.** An install writes 6 files in parallel
into the project with rollback; a retry loop there needs partial-failure semantics the E2E and the
manual QA list cannot exercise today, and re-downloading a 2 GiB file behind a rate limiter is not
obviously kinder than failing cleanly. That is the rest of (a), and it should land with a test for
the resume/rollback path.

(b) stays a catalog-side policy: the plugin verifies `sha256` when present and installs unverified
when absent — a published catalog should require it. (c) stays undone: the caps are compile-time
(2 GiB per pack file, 16 MiB index, 8 MiB single manifest, 64 MiB preview) and nobody has reported
hitting one.

---

## D12 — Trust model of a manifest source · **plugin side implemented; catalog policy answered**

A manifest is remote input: it is fetched over HTTPS, and its entries become files on the user's disk
(`files[]` downloads, `archivePack` zip extraction, `pluginProject` **git clones of third-party
repos**). The plugin's guards are mechanical — https-only git URLs, validated branch/commit names, per-
argument git invocation, path containment on every write and delete, streamed size cap, optional
`sha256` — none of them judge *intent*. So the policy question is about **which sources you point
users at**, not about parsing:

* Which sources are advertised, and who reviewed them before being added?
* Are `pluginProject` entries (arbitrary git clones) allowed in catalogs you distribute at all?
* Does `sha256` become mandatory for any source you publish, and is it published by the source or
  transcribed by the catalog author?
* What the README tells users to expect when they add a third-party URL.

Recommendation: an allowlist with a review step per source (and per `pluginProject`), `sha256`
required for catalogs you publish yourself, and an explicit statement that a catalog is a
convenience index — not an endorsement and not a security boundary. The plugin README already says a
manifest URL means trusting its host; keep the rest of that policy wherever catalogs live (today:
outside this repository, see D2).

**Implemented in the plugin (the visible part of the policy):** the details panel shows the selected pack's
source **host** (`From`) and license; a pack with `Script` files, or a `pluginProject`, shows
**Installs code: …** in the status line and requires an explicit confirmation naming host, destination and
files, before anything is written (`PackInstallsCode` / `ConfirmCodeInstall` in the window). The rest stays
an author decision: which sources get advertised, whether `pluginProject` entries appear in a catalog, and
whether `sha256` is mandatory there.

**The four policy questions, answered at the level this repository can answer.** The catalog itself
lives outside this repo (D2), so the answers are stated as the plugin's published defaults — the
behaviour a user or a catalog author can rely on, and what the plugin refuses to imply:

* *Which sources are advertised?* **None, from here.** The plugin ships no catalog, no index, no
  default URL, and the README's first usage line is "only add manifest URLs you trust". The
  `.github/ISSUE_TEMPLATE/manifest_and_pack.yml` form is where a source being *proposed* for a
  catalog gets reviewed by hand, field by field (`sourceId`, host, license, `sha256` presence).
* *Are `pluginProject` entries allowed in a distributed catalog?* **Allowed, and gated.** The plugin
  cannot bless them, so it makes the decision visible instead: the details panel says
  **Installs code: …** with the host, and the install asks before cloning. A catalog that lists one
  has taken on the review of that repo; the PR template lists the code-install path as a gate.
* *Is `sha256` mandatory?* **Recommended and default for published catalogs** — required for
  `assetPack` entries a catalog author publishes, verified when present, installed unverified when
  absent. The plugin stays tolerant (community manifests exist without hashes) and says so in
  `docs/MANIFEST_SPEC.md`; a catalog's own README should state which of its entries are hashed.
* *What users are told to expect when adding a third-party URL:* the README's trust section — the
  host is trusted entirely (its files, its paths, its text, its links), installs are contained to
  the project, code-installing packs are confirmed, and `sha256` is the only integrity signal a
  manifest can carry.

---

## D13 — Index manifests (a master manifest that links other manifests) · **implemented**

Goal: publish **one URL** whose contents the owner controls, so users add a single source and pick from a
list the owner keeps updating — without the plugin shipping a catalog (D2) or advertising sources (D12).

Shape: `manifestKind: "index"` + `entries[]` (`sourceId`, `sourceName`, `url`, `description?`, `tags?`).
Rejected alternatives: a `linkedSources[]` field on a *pack* manifest (one file then means two things, and
"reject the whole manifest at the first violation" gets muddy when the rules differ per section); and
`includes[]` **merge** semantics (a merged view would surface content from URLs the user never reviewed and
would blur the per-source install-record key).

Decisions baked in:

* **An index links, it never installs.** No assets, no records, no automatic following. Each entry becomes a
  source through one explicit click, and the linked manifest is validated as an ordinary manifest. Trust
  stays a per-URL decision (D12) instead of transitive.
* **`sourceId` is the join key.** Entry ids must be unique inside an index and must not collide with an
  already-configured source: `ValidateIndex` enforces the first, `OnAddLinkedSourceClicked` the second,
  because `sourceId` keys `.installed.json` and a collision would silently merge two sources' records.
* **`assets` is no longer `Required.Always` in the model** (an index has none). Enforcement moved into
  `ValidateManifest`, which still rejects a pack manifest without `assets`. Validation is the single
  authority — do not reintroduce JSON-level `Required` for fields whose rules depend on the manifest kind.
* UI: index rows in the asset list (`RebuildIndexList`), an index-specific details panel
  (`SelectIndexEntry`) and **Add linked source** (`OnAddLinkedSourceClicked`); pack actions hide while an
  index is selected, and arrow-key navigation follows whichever list is showing (`_listIsIndex`).

Deliberately not done: searching across linked manifests, "add all entries", and any weighting of entries
hosted on the index's own domain. Each buys convenience at the cost of trust-parsing the index does not need.


---

## D14 — Writing to disk from remote input: clone-to-staging, and archives cannot deliver code · **resolved**

D12 settled *which* sources to trust; the plugin's own job is that a manifest never destroys something it
did not create. Two flows write outside their own record, and both were fixed the same way — **validate
first, write to a staging location, publish on success**.

**`pluginProject` install (`AssetInstallerService.InstallPluginProjectAsync:334`).** The old flow derived a
folder name from the manifest's git URL, deleted `Plugins/<that name>` **before** validating the URL, ref
and commit, then cloned into it. Consequences: an unreachable URL, a rejected ref/commit or a repo with no
root `.flaxproj` left the project with a folder deleted; a URL basename could name the manager's own
folder, deleting the code that was running the install; and `Plugins/Flax` (the editor's own plugin tree)
was reachable by a manifest whose basename was `Flax`.

Now, in order:

* URL scheme, ref and commit are validated **before** the filesystem is touched (as is every git
  invocation: argument list + `--` end-of-options, so no manifest field becomes a git option).
* The folder derived from the URL must resolve to a **direct child of `Plugins/`** and must not contain
  the folder this plugin's own assembly runs from.
* An existing `Plugins/<name>` is replaced **only** when a recorded `pluginProject` install of this
  manager owns it (matched on the resolved path). Anything else — a plugin the user cloned by hand, the
  editor's own plugins — is refused with a message naming the folder, and nothing is changed.
  validated there (root `.flaxproj` present, HEAD resolved). Only then does the swap run, as **two
  renames**: the owned tree is moved aside to `Plugins/.flaxam-replaced-<guid>` and the clone is moved in,
  and the replaced tree is deleted once the install succeeded. If the move-in fails, the old tree is renamed
  back — a failed swap costs nothing. The `finally` removes any leftover staging folder.

**`archivePack` extraction (`AssetInstallerService.InstallArchivePackAsync`,
`AssetSourceManager.CheckArchiveEntryName:380`).** Archive entries carry no manifest `type`, so the
per-file extension whitelist never applied to them, and `PackInstallsCode` reports an archive as
not-installing-code — so an archive could drop `Startup.cs` into the content tree with no confirmation
and no record of its intent. Every entry name is now checked **before anything is extracted**:

* **Reject** (whole install fails, files rolled back): a C# script, an executable, a native library or an
  interpreter script (`.cs`, `.dll`, `.exe`, `.so`, `.dylib`, `.sh`, `.ps1`, `.py`, `.js`, `.lua`, …). An
  archive always extracts into the content destination, so this is the only code route left, and it is
  closed — plugin code goes through `pluginProject`, which the window gates with a confirmation.
* **Skip** (entry dropped, install continues): docs, C++ sources, JSON, loose data, VCS metadata. They are
  forbidden as *installed assets*, and upstream archives carry them routinely (a zip of a repo has its own
  `README.md`), so failing a pack over them would be user-hostile.
* **Extract**: everything else, including extension-less names (`LICENSE`, `README`).

The `assetPack` rules (`ValidateFiles`) are deliberately **unchanged**: a pack author controls those
paths, and real packs that pass today keep passing.
**The read side of the same rule.** The write paths are contained, so the OS-integration paths may not
be the loose ones — their input is also data on disk:

* `SystemFileOpener.TryOpenInEditor` opens only files inside `Globals.ProjectFolder` (`GitSafety.IsUnderRoot`)
  and refuses extensions a shell would *run* (`.exe`, `.lnk`, `.reg`, `.url`, `.ps1`, …). Windows opens by
  shell execute, so "open in the editor" for an attacker-chosen path is code execution; the caller today is
  the **Edit source list** button, whose path comes from `Settings.json`.
* The website button calls `Platform.OpenUrl` only for `http(s)`. `Platform.CanOpenUrl` asks the OS whether
  *any* handler exists, so a manifest `file://`, `mailto:` or custom-scheme link would otherwise open a file
  manager, a mail composer or an installed app.
* `SettingsService.IsValidDestination` refuses drive hints, drive roots, UNC shares and traversal, so a
  hand-edited `contentDestination` cannot turn an installed path into an absolute one elsewhere.

**Material graph ids.** The box ids `MaterialGraphBuilder` wires into (`Color 1`, `Normal 8`, …) are
engine-internal (`MaterialNodeBoxes` is not public), so CI proves they compile, not that they still name
the same socket. `ValidateRootInputBoxes` checks each one against the engine's live box metadata (present,
is an input, expected value type) before wiring and refuses to build the graph on a mismatch — a silent
mis-wired material becomes a loud, actionable log line.

**Remaining:** manual QA of both flows in an editor build — install a plugin pack twice (replace path),
install one whose URL basename is `FlaxAssetManager` (refused), point one at a nonexistent repo (existing
tree intact), and install an archive containing `README.md` plus a `.cs` (the `.cs` fails it, cleanly).

---

## D15 — One install action, not two · **resolved**

The details panel used to offer **Download** (files, model import) and **Install** (files **+** a
generated PBR material) as two buttons for a texture pack, plus **Remove**. Users asked what the
difference was, which is the sign the split was the author's, not the user's.

**Resolved: one action.** `_installButton` is labelled **Install** / **Update** / **Reinstall** from
the install record and, for an asset pack whose textures have detectable PBR roles, the same click
also builds the material; every other pack type installs as before. `_installMaterialButton` is
gone, along with the second enabled-state rule it needed. **Remove** is visible only while the
record says something is installed. `RefreshActionButtons` is the single place that computes label,
enabled state and tooltip — from the **record**, not from the pack type — so model, script, audio,
archive and plugin packs get identical behaviour, and both buttons go disabled while an operation is
in flight (one install per window; the second click cannot start a concurrent one).

Why collapsing rather than renaming: a texture pack without its material is a folder of images, and
"download" promised something a user would then have to finish by hand. The decision is derivable
from the pack (`HasMaterialTextures`), so the pack makes it; the tooltip and the D5 unused-texture
line tell the user what is about to happen. Keeping two buttons would have kept a state where
"installed" meant two different things for one pack — which is what made **Remove** ambiguous.

`withMaterial` is computed in `OnInstallClicked` and drives both the flow and its own token
(`_installMaterialCts`, cancelled on close like every other operation), so the tooltip's promise and
the click's behaviour are computed from the same predicate.
