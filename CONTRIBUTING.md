# Contributing

Thanks for taking an interest. This plugin is a single-editor-window asset browser written in C# on top
of Flax's public scripting API; it has no native code.

## Before you start

* Read [`README.md`](README.md) for behaviour, and [`docs/MANIFEST_SPEC.md`](docs/MANIFEST_SPEC.md) for
  the manifest format (pack manifests and index manifests).
* Read [`AGENTS.md`](AGENTS.md) — a file map, invariants and pitfalls. It is the shortest route to the
  right file; `docs/DECISIONS.md` records *why* the design looks the way it does.
* For anything that changes behaviour, open an issue first so we can agree on the shape before you write
  code. Small fixes (typos, a crash, a wrong log message) can go straight to a pull request.

## Environment

* Flax **1.13** (the version in `FlaxAssetManager.flaxproj` → `MinEngineVersion`), Linux or Windows.
* Work from a **host project** that contains this folder under `Plugins/`. Cloning the plugin alone does
  not produce a buildable project.

## Build and verify

No `.csproj`, `.sln`, `*.Gen.cs`, `launchSettings.json` or `Binaries/` are committed — Flax generates
them. Build through the engine's builder:

```bash
FLAX=/path/to/FlaxEngine
"$FLAX/Binaries/Tools/Flax.Build" -project=<HostProject>.flaxproj -build -platform=Linux -configuration=Development
```

That compiles the plugin's game and editor modules as dependencies of the host project's targets. The
builder's log is `<HostProject>/Cache/Intermediate/Log.txt`; grep it for `FlaxAssetManager` to confirm
the assemblies were compiled. `-configuration` takes `Development|Debug|Release` (there is no `Editor`
configuration). `-targets=<Target>` only selects targets for project generation, and
`-buildtargets=<PluginTarget>` reports *Missing target*.

Fast compile check against an official Flax release (no editor needed):

```bash
dotnet build compat/FlaxAssetManager.Compat.csproj -c Development -p:EngineDir=/path/to/flax
```

CI runs that same probe on every push and weekly (`compat/README.md`). Keep the tree warning-free.

For manifest work, serve the plugin's own examples over HTTP and add them as a source —
`AGENTS.md` (→ *Build & Verify*) has the commands for `example_manifest.json`, `example_index.json` and
the live-editor end-to-end harness (`FLAX_AM_E2E=1`).
### Behaviour probes outside the editor

Three behaviours are security-relevant, cheap to test and have no editor dependency, so they run as plain
`dotnet` probes against the plugin's sources (compile the file(s) in, stub `FlaxEngine.Debug` if the log
P/Invoke is absent). Keep them passing when you touch the code they cover:

| Probe | Covers |
| --- | --- |
| manifest validation | shipped `example_manifest.json` / `example_index.json` still pass `ValidateManifest`; duplicate `packId` rejected; `..` path forms (including the legitimate `brick..png`) |
| destination/path checks | `SettingsService.IsValidDestination` and `AssetSourceManager.SanitizeDestination` reject absolute, drive-rooted, traversal, `~`, UNC and `.`-segment paths |
| network | `HttpStream` refuses an oversize body by declared length and by counted bytes; retries 503/429 up to `MaxAttempts`; honours `Retry-After`; sends `If-None-Match` and reports 304; does **not** retry 404 |

Serve the examples over a local `HttpListener` for these — no public endpoint, and the retry/ETag cases
need a server that can be told to fail.
### Manual QA in an editor build

The compat build proves the plugin compiles; the E2E harness proves install + material run inside the
editor. Everything that makes this plugin safe to hand to someone is behaviour, so a release is checked
by hand in a real editor. Run the list, tick nothing you did not do, and paste the results in the PR.

Install / remove, per pack type (texture, model, script, audio, archive, plugin):

* [ ] Install — one click, one button; the status bar names the pack (and the material, for a texture
      pack). Label becomes **Reinstall**, and **Remove** appears.
* [ ] Remove — the Content browser keeps **nothing** from that pack: no `M_<packId>.flax`, no imported
      model `.flax`, no texture `.flax` sidecars, no `files/` folder, no pack root. Check by looking,
      not by trusting the log.
* [ ] Remove, then install the same pack again — clean install, clean record.
* [ ] A texture pack with gloss/cavity/ID files: the details panel names how many textures the
      material will not use.
* [ ] A plugin pack: install twice (replace path), and install one whose URL basename is
      `FlaxAssetManager` (refused). Point one at a nonexistent repo — the existing tree is intact.
* [ ] An archive containing `README.md` plus a `.cs`: the `.cs` fails it cleanly, the `README.md` is
      skipped, nothing is half-installed.

Window and threading:

* [ ] Close the window mid-install, mid-material, mid-remove, mid-audio-preview: the operation stops,
      no "not on main thread" assertion, no crash on reopening.
* [ ] Switch sources/packs/variants while a preview image and an audio preview are loading — no stale
      preview on the new pack, no leaked preview asset.
* [ ] Refresh with a source that is down: it fails with its own message, the rest render.

Remote-input refusals (the security behaviour, checked as behaviour):

* [ ] `Settings.json` `contentDestination` hand-edited to `C:foo`, `/abs/path`, `../escape`, `~/home`:
      a warning in the log and the shipped default is used, not the value.
* [ ] The source-list path in settings pointed outside the project, and at a `.lnk`/`.exe`: both refused.
* [ ] A manifest `website` of `file:///etc` and one of `mailto:someone@example.com`: the website button
      refuses with a message; nothing launches.
* [ ] Serve an oversize manifest (>8 MiB) and an oversize preview (>64 MiB): refused with the size message,
      editor stays responsive.
* [ ] Serve a manifest that answers 429 once: it retries and succeeds (check the log line).

Material (needs an editor build; the only part with no automated coverage beyond E2E):

* [ ] Open the generated material: boxes wired, texture visible, parameters **Color**,
      **EmissiveColor**, **NormalStrength**, **UV Scale**, **UV Tiling** present and editable.
* [ ] Install a pack whose textures have no detectable roles: no material, one clear log line, files
      installed.

## Code conventions

* Keep the layout: models in `Core/Models` (no I/O), services in `Core/Services`, engine-utility helpers
  in `Core/Utils`, UI in `UI/`. The window file is large on purpose — it is one editor window with
  pixel-positioned layout; add to it rather than splitting it, and keep new UI in the row templates.
* GUI touches happen on the main thread. The engine sets **no** `SynchronizationContext`, so an `await`
  resumes on a thread-pool thread: do the I/O there, then hop **once** with
  `await MainThreadDispatcher.RunOnMain(() => …)` (a thin wrapper over `Scripting.RunOnUpdate`) and do
  all engine/GUI work inside that lambda. Fire-and-forget reports from a service (progress, log) go
  through the window's main-thread queue, not `Progress<T>` — that falls back to the pool too. Never
  create GPU textures on a worker thread.
* A manifest is remote input: any new field that becomes a path, URL, ref, extension, size or type must
  be validated in `AssetSourceManager` before it reaches the filesystem, git, or the asset pipeline —
  and rejected as a whole manifest at the first violation.
* Anything a manifest can turn into a filesystem write goes into a staging location and is published only
  on success, and may only replace a path this manager recorded itself — a failed fetch must leave the
  project as it was (`docs/DECISIONS.md` → D14). Archives are checked entry-name-first: code is refused,
  non-asset junk is skipped (`AssetSourceManager.CheckArchiveEntryName`).
* Reading remote bodies goes through `HttpStream`: `FetchTextAsync` for a manifest (cap + retry +
  `If-None-Match`), `DownloadBytesAsync` for preview media, `ReadAllTextCapped` for a `file://` source, and
  `DownloadToFileAsync` for pack files. Each declares a cap; raw
  `HttpClient.GetStringAsync`/`GetByteArrayAsync` do not, and an unbounded read turns a hostile manifest into
  a memory problem. Retry/backoff and ETag handling live in `SendWithRetryAsync` — do not add a second
  retry loop at a call site.
* Data reaching the OS is checked like data reaching the disk: `SystemFileOpener` opens only files inside the
  project and refuses shell-executable extensions, and `Platform.OpenUrl` is called only for `http(s)`
  (`CanOpenUrl` accepts any registered scheme handler). See D14.
* A new hard-coded Visject id needs a `MaterialGraphBuilder.RootInputBoxes` case. Those ids are
  engine-internal, so CI proves they compile, not that they name the same box; `ValidateRootInputBoxes` is
  what keeps a future engine release from silently wiring the wrong socket.
* Kind-dependent manifest rules live in the validator, never in JSON `Required` attributes.
* Do not add a dependency on engine APIs that are not in an official release; the compat probe will fail.
* Keep the plugin source-agnostic: no specific asset-source, vendor, pack, variant or URL names in code,
  tests, fixtures, examples or docs. The plugin ships no catalog and no third-party manifests; runtime
  state (`Settings.json`, `Sources.json`, `.installed.json`) belongs to the host project, not to here.
* Do not commit machine-specific absolute paths, generated project files, engine binaries, or content
  under `Content/FlaxAssetManager/`.

## Manifests, examples and licensing

`example_manifest.json` and `example_index.json` are format demonstrations with `example.com`
placeholders. Keep them placeholder-only: real third-party manifests carry asset licences
(CC-BY, Apache-2.0, and so on) that must not be redistributed from this repository. End users who
install assets take on the licence obligations of whoever published them.

This project is under the MIT licence (`LICENSE`). By contributing you agree that your contributions are
licensed the same way.

Line endings are fixed by `.gitattributes` (LF in the index and in the checkout) — there is nothing to
configure per machine, and no CRLF conversion diffs. `.github/ISSUE_TEMPLATE/` has a bug form and a
manifest/pack form; the pull-request template lists the checks a PR is expected to satisfy.

## Reporting

Ordinary issues, including anything that looks like a security problem — please title it `security` so it
gets read first. This repository does not enable private vulnerability reporting and does not run a
disclosure programme; the trust model is documented in `docs/DECISIONS.md` (D12): a manifest is remote
input, and the plugin's job is to bound the damage, not to vouch for a source.
