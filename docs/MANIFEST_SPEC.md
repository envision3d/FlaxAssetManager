# FlaxAssetManager Manifest File Format — Exhaustive Spec

> Spec for the **external JSON manifest file** hosted behind each URL added as a *Source* in the Flax Asset Manager window. One JSON file per source, in one of two kinds: a **pack manifest** (`assets[]`, installable packs) and an **index manifest** (`manifestKind: "index"`, `entries[]`, links other manifests — see the last section). Editor fetches each `Sources.json` `url` independently; sources are never merged (`AssetSourceManager.FetchAllAsync`). The authoritative validators are `Core/Services/AssetSourceManager.ValidateManifest` and `ValidateIndex` — JSON attributes do not enforce kind-dependent rules.

---

## 1. At-a-Glance

```
URL (e.g. https://example.com/manifest.json)
  └─► GET 30s HttpClient.GetStringAsync
       └─► Newtonsoft.Json Deserialize → AssetSourceManifest
            └─► ValidateManifest (strict) → either Listed under that Source name or flagged "(error)"
                 └─► User picks Pack → InstallAsync by packType
```

No auth headers, no zip indirection, no merging. Failure of one source does not affect others.

---

## 2. MIME & Transport

* URL must be `http://` or `https://`. File-url `file://` works only if the desktop can fetch it via `HttpClient` — for local testing prefer `python -m http.server`.
* Response body must be UTF-8 JSON. No BOM required.
* `Content-Type` is not enforced — any response that parses as JSON is accepted.
* Timeout: 30 s per URL (`AssetSourceManager` `HttpClient.Timeout`), 60 s per file download during install.
* Size cap: a manifest body over **8 MiB**, and an index/linked manifest over **16 MiB**, is refused as
  invalid (`HttpStream.DownloadTextAsync`); preview images and audio are capped at 64 MiB, pack files at
  2 GiB streamed. Keep manifests small: the cap is a bound on hostile input, not a format budget.
* Caching: conditional GET. A manifest/index fetch sends `If-None-Match` with the `ETag` the host last
  returned (a **weak** validator, `W/"..."`, is sent back verbatim — that is what nginx and CDNs serve
  for static files, and weak matching ignores the indicator) and, on a 304, reuses the body cached in
  memory for that URL, so a `Refresh` of an unchanged source costs a header round-trip.
  The cache is in-memory only (a restart refetches) and is dropped
  whenever the source list is edited, so add/rename/remove always reads in full. Transient failures
  (429, 5xx, timeouts, dropped connections) are retried with backoff, honouring `Retry-After`;
  404/403/oversize are not (`HttpStream`). Pack file downloads do not retry.

---

## 3. Top-Level Object `AssetSourceManifest`

| Field | JSON key | Type | Required | Description |
|-------|----------|------|----------|-------------|
| Source display name | `sourceName` | string | **yes** | Human label shown in source picker (`ComboBox`). Fallback for picker: `SourceUrlEntry.name ?? Manifest.sourceName ?? url` (`FlaxAssetManagerWindow.OnFetched`). |
| Source stable id | `sourceId` | string | **yes** | Machine key used as part of install identity (`sourceId+packId` key into `.installed.json`). Should be reverse-DNS `com.example.community`. Changing it orphans prior installs. |
| Source version | `sourceVersion` | string | no | Informational source revision (e.g. `"1.0.0"`). Not used for update badges (badges use per-pack `version`). |
| Homepage | `homepageUrl` | string | no | Link shown in future detail pane. |
| Upstream source site | `website` | string | no | Canonical site of the **original source** of what this manifest catalogues — for attribution and reference (e.g. `"https://texturehaven.com"`). A mirror/scraper manifest puts the mirror in `homepageUrl` and the party that made the assets here. Not validated, never used for fetching, and not a trust boundary (trust is the source URL — see D12); copy it into your credits where the license requires attribution. Same top-level field for pack and index manifests. |
| Description | `description` | string | no | Short blurb shown in details. |
| Packs | `assets` | `PackEntry[]` | **yes** | Non-empty array. `assets` name is historical (means *packs*) — not files. |

**Validation:** Missing/empty `sourceName` or `sourceId`, or missing/empty `assets[]`, is `(error)` (`ValidateManifest` returns that string, logged via `Debug.LogError`, displayed as `"SourceName (error)"`).

---

## 4. Pack Object `PackEntry`

| Field | Key | Type | Req | Notes |
|-------|-----|------|-----|-------|
|| Pack stable id | `id` | string | yes | Key in `.installed.json` alongside `sourceId`, and the pack folder name. **Must be unique within one manifest** — `ValidateManifest` rejects a duplicate, because two packs sharing one id would merge their install records (and one uninstall would delete the other's files). Keep it `^[a-zA-Z0-9._-]+$`; changing it breaks the update path. |
| Display name | `name` | string | yes | Shown in asset list row + detail title. |
| Description | `description` | string | no | Shown in details pane, searchable. |
| **Install semantics** | `packType` | enum string | no→`assetPack` | `"assetPack"`, `"pluginProject"`, or `"archivePack"` (case-insensitive via `StringEnumConverter`). Omit = `AssetPack` for compat. |
| Content version | `version` | string | yes | Semver-ish free form; compared via `string.Equals` ordinal (`SettingsService.HasUpdate`). Author must bump to make *Update available* appear. Recommend `x.y.z`. |
| Category | `category` | string | no | UI grouping hint (not validated). Displayed in details. |
| Tags | `tags` | `string[]` | no | For search only; no enum. Lowercased for search (`RebuildAssetList` hay). |
| Preview | `previewUrl` | string | no | HTTP(S) image for future thumbnail `Image` load (slot reserved). |
| License | `license` | string | no | SPDX short (e.g. `"MIT"`, `"CC0"`) shown in details. |
| **Asset pack payload** | `files` | `FileEntry[]` | conditional | `assetPack`: required non-empty **unless** `variants[]` is non-empty (then `variants[0]` is the default set). `pluginProject`/`archivePack`: forbidden. See §5. |
| **Variant file sets** | `variants` | `VariantEntry[]` | no | `assetPack` only. Optional alternative file sets (e.g. resolutions 1k/2k/4k). When `files[]` is present it is the default; otherwise `variants[0]` is. See §5.3. |
| **Distribution payload** | `distribution` | `DistributionInfo` | conditional | Required if `packType` is `pluginProject` or `archivePack`; forbidden if `assetPack`. See §6. |

### 4.1 `packType × payload` matrix

| `packType` | `files` | `distribution` | Valid? | Installer action |
|------------|---------|----------------|--------|------------------|
| `assetPack` (or omit) | non-empty | absent | ✅ | Parallel file downloads, auto-routing by file `type` |
| `assetPack` | empty/missing | absent, `variants` non-empty | ✅ | First variant is the default set |
| `assetPack` | empty/missing | absent, no `variants` | ❌ | `"assetPack requires non-empty files[] (or non-empty variants[])"` |
| `pluginProject` | any, `variants` present | any | ❌ | `"pluginProject must not have variants[]"` |
| `assetPack` | any | present | ❌ | `"assetPack must not have distribution"` |
| `pluginProject` | absent | present `kind:"gitRepo"` | ✅ | Plain `git clone` → `Plugins/<Name>` |
| `pluginProject` | present | any | ❌ | `"pluginProject must not have files[]"` |
| `pluginProject` | absent | missing/bad | ❌ | `"pluginProject requires distribution"` / `"distribution.kind must be 'gitRepo'"` |
| `archivePack` | absent | present `kind:"archive"`, `.zip` | ✅ | Download zip → extract to `<contentDest>/<sourceId>/<packId>/files/`, preserving archive layout |
| `archivePack` | present | any | ❌ | `"archivePack must not have files[] (the zip provides them)"` |
| `archivePack` | any | `variants` present | ❌ | `"archivePack must not have variants[]"` |
| `archivePack` | absent | missing/bad kind | ❌ | `"archivePack requires distribution"` / `"archivePack distribution.kind must be 'archive'"` |
| `archivePack` | absent | `kind:"archive"`, non-`.zip` | ❌ | `"archivePack distribution.url must point to a .zip file"` |

---

## 5. File Object `FileEntry` (assetPack only)

| Field | Key | Type | Req | Rules |
|-------|-----|------|-----|-------|
| Download URL | `url` | string | yes | Must start `http://`/`https://`. Can be `raw.githubusercontent.com` for a file inside a git repo, or a static host. One GET per file (cap 6 parallel `SemaphoreSlim(6)`). |
| Install path | `path` | string | yes | **Relative** POSIX-ish path, no `..`, no absolute (`Path.IsPathRooted` rejected). Extension determines routing but **explicit** `type` decides destination: `Script` → `ScriptDestinationFullPath/Source/...`, others → `ContentDestinationFullPath/Content/...`. The path is appended under `<dest>/<sanitized(sourceId)>/<sanitized(packId)>/files/<path>` preserving subfolders. A leading segment that equals the pack folder (sanitized, matching the install folder name) is **stripped** at fetch time, so manifests that embed the pack folder (e.g. `"path": "foo-asset/diff.png"` for pack id `foo-asset`) resolve to the same layout as pack-root-relative ones (`"diff.png"`) — never a doubled `foo-asset/foo-asset/`. |
| Asset type | `type` | enum string | yes | Exhaustive: `Texture`, `Material`, `MaterialNode`, `Model`, `Prefab`, `Script`, `AnimGraph`, `Audio`, `Shader`, `MaterialTextures`. `MaterialTextures` marks an image as a material-bound texture set: its PBR role (BaseColor/Normal/Metallic/Roughness/AO/Emissive/Height/Opacity) is auto-detected from the file name, and the *Install and create material* action uses these files to build a PBR material. Unknown value → deserialization/fetch error (`type` must be a *name*: numeric strings such as `"5"` are rejected, never mapped onto the enum member with that value). No `Other` — every raw file must be typed. |
| Checksum | `sha256` | string | no | Lowercase hex SHA-256 of the bytes; computed via `SHA256.Create()` and compared before write. On mismatch install fails for that pack (no partial entry). |

### 5.1 Strict per-file rules (`ValidateManifest` + `AssetInstallerService`)

* **Relative, no traversal:** `path` must not contain `..` or be rooted; validated before any download.
* **Pack-prefix tolerance:** `path` is expected to be relative to the pack root (no leading pack folder). If the first segment — sanitized the same way the install folder is named — equals the pack folder, it is stripped at fetch (`AssetSourceManager.NormalizePackPaths`) so both conventions install to `<dest>/<source>/<pack>/files/<path>` and every consumer (installer, material creator, role detection) agrees.
* **Extension non-empty:** `Path.GetExtension(path)` must be non-empty.
* **Forbidden extensions** (`AssetSourceManager.ForbiddenExts`): `{ .cpp, .h, .hpp, .c, .cc, .inl, .md, .txt, .json, .bin, .dat, .gitignore, .gitattributes }` — any of these in an `assetPack` file is `(error)` with hint `"Use pluginProject for docs/C++"`. This keeps asset packs to raw Flax assets only.
* **`Script` is `.cs` only** (`AllowedScriptExts: {".cs"}`): if `type=="Script"` and `ext` is not `.cs` → error `"Script requires .cs extension, got '.X'"`. Conversely, if `type != Script` but `ext == ".cs"` → error `"has script extension but type is X"`. Non-script raw assets accept any extension outside the forbidden set and `.cs`.
* **`MaterialTextures` is an image only** (`MaterialTextureExts`): if `type=="MaterialTextures"` and `ext` is not one of `{ .png,.jpg,.jpeg,.tga,.bmp,.dds,.hdr,.exr,.psd,.tif,.tiff }` → error `"MaterialTextures requires an image extension, got '.X'"`. The texture's PBR role is inferred from its file name when a material is created from it. Both `MaterialTextures` (plural) and `MaterialTexture` (singular) are accepted during deserialization (see `FileAssetType.cs`).
* **URL scheme:** must be `http(s)`. No `file://` or bare paths.
* **Destination guard:** installer does `Path.GetFullPath(combined)` and asserts `StartsWith(rootFull)`; traversal candidates never written.

### 5.2 Allowed type ↔ extension correlation

| `type` | Typically seen extensions | Destination root |
|--------|--------------------------|------------------|
| `Texture` | `.png,.jpg,.jpeg,.tga,.bmp,.dds,.hdr,.exr,.psd,.tif` | Content |
| `Material` | `.flax` (material asset), sometimes source `.png` textures + material | Content |
| `MaterialNode` | `.flax` / `.hlsl` includes | Content |
| `Model` | `.fbx,.obj,.gltf,.glb,.dae` + `.flax` prefab | Content |
| `Prefab` | `.flax` (prefab), `.json` not allowed here — wrap as `.flax` | Content |
| `Script` | **`.cs` only** | Scripts (`Source/…`) |
| `AnimGraph` | `.flax` | Content |
| `Audio` | `.wav,.ogg,.mp3,.flac` | Content |
| `Shader` | `.hlsl,.shader,.ush` | Content |
| `MaterialTextures` | image extensions only (`.png,.jpg,.jpeg,.tga,.bmp,.dds,.hdr,.exr,.psd,.tif,.tiff`) | Content (source images for auto-built PBR materials) |

Note: shader source lives as content, not under `Source/`, to respect `ContentFolder.CanHaveScripts`.

### 5.3 Variant Object `VariantEntry` (assetPack only)

| Field | Key | Type | Req | Rules |
|-------|-----|------|-----|-------|
| Variant id | `id` | string | yes | Required, unique within the pack (case-sensitive ordinal compare). Machine key used for selection persistence (`Settings.json` `selectedVariants`) and for the installed record (`.installed.json` `variant`). |
| Display label | `name` | string | yes | Shown in the detail panel's Variant dropdown. |
| Variant files | `files` | `FileEntry[]` | yes | Non-empty. Every rule in §5.1 applies verbatim (the same `ValidateFiles` check runs on them; errors are reported as `variant '<id>' <rule>`), including pack-prefix stripping. |

**Default set rule:** `files[]` is optional when `variants[]` is present. If base `files[]` is non-empty, it is the *default* file set (the dropdown's `Default` item); installing with no variant selected writes it. If `files[]` is absent, the **first variant** (declaration order) is the default — an install with no explicit selection writes the first variant's files. An `assetPack` needs non-empty `files[]` **or** non-empty `variants[]`; having neither ⇒ `(error)`. `pluginProject` packs must not have `variants[]`.

---

## 6. Distribution Object `DistributionInfo` (pluginProject / archivePack)

| Field | Key | Type | Req | Description |
|-------|-----|------|-----|-------------|
| Kind | `kind` | string | yes | `"gitRepo"` for pluginProject, `"archive"` for archivePack (case-insensitive). Any other value → error. |
| URL | `url` | string | yes | `gitRepo`: git clone URL, e.g. `"https://github.com/org/MyPlugin.git"`; must be **https**, and is passed to git as `clone [--branch=<branch>] -- "<url>" "<staging>"` — spawned with an argument list and a `--` end-of-options marker, so no manifest field can become a git option (`--upload-pack=<cmd>`). `archive`: HTTP(S) URL of a **`.zip`** archive, streamed to disk under a size cap. |
| Branch | `branch` | string | no | `gitRepo` only. If present, passed as `--branch=<branch>`; it must pass the ref-name check (`GitSafety.IsValidRefName`) or the install is refused. |
| Commit | `commit` | string | no | `gitRepo` only. Must pass the commit-id check (`GitSafety.IsValidCommit`); after the clone, `git -C "<staging>" checkout --detach "<commit>"`. Recorded as `resolvedCommit` after `rev-parse HEAD`. |
| Checksum | `sha256` | string | no | Lowercase hex SHA-256 of the downloaded **archive bytes** (archivePack). Compared before extraction; on mismatch the install fails. |

**Validation:** Missing/empty `url`, `kind` not matching the pack type, non-`.zip` `url` for archivePack, or `files` present alongside `distribution`, all fail.

**Clone target (gitRepo):** `Plugins/<folderName>` where `folderName = Sanitize(lastPathSegment(url) strip .git)` or `packId` fallback (`AssetInstallerService.DerivePluginFolderName`). The folder name is derived from a remote URL, so it is validated too: it must resolve to a **direct child of `Plugins/`** and must not contain the folder FlaxAssetManager's own assembly runs from. An existing folder is replaced **only** when a recorded `pluginProject` install of this manager owns it (`IsOwnedPluginInstall`, matched on the resolved path); any other existing folder — a plugin cloned by hand, the editor's own `Plugins/Flax` — makes the install refuse and change nothing. The clone runs into `Plugins/.flaxam-clone-<guid>` (staging, same filesystem as the destination so the swap is a rename), and root `Directory.GetFiles(staging, "*.flaxproj")` must be non-empty before it is moved into place. The swap is two renames: the owned tree goes to `Plugins/.flaxam-replaced-<guid>`, the clone moves in, and the replaced tree is deleted only after that succeeds — a failed move-in renames the old tree back. A failed clone leaves the staging folder removed by the `finally` and the project untouched. Reference injected into root `.flaxproj` via `JObject` edit: `$(ProjectPath)/Plugins/<folder>/<proj>.flaxproj`. Keep `.git` (not submodule) so `rev-parse HEAD` can be captured into `.installed.json:resolvedCommit`.

**Extract target (archive):** `<contentDestination>/<sanitized(sourceId)>/<sanitized(packId)>/files/`, preserving the archive's internal folder layout. Zip-slip guard: every entry is resolved with `Path.GetFullPath` and rejected if it escapes the destination (entries with `..` or absolute names). Entries are filtered to regular files (no directory entries). **Entry-name rule** (`AssetSourceManager.CheckArchiveEntryName`, applied to every entry *before* extraction, because archive entries carry no manifest `type`): a script, executable, native-library or interpreter-script extension (`.cs`, `.dll`, `.exe`, `.so`, `.dylib`, `.sh`, `.ps1`, `.py`, `.js`, `.lua`, …) **rejects** the whole archive — an archive extracts into the content destination and must not become a code route (plugin code is `pluginProject`, which the UI gates with a confirmation); docs, C++, JSON, loose data and VCS metadata are **skipped** (dropped, install continues) since upstream archives carry them routinely; everything else, including extension-less names, extracts. All files are extracted atomically: any failure rolls back every file already written, and no `InstalledEntry` is recorded.

---

## 7. Optional Pack Fields — Detail

* **`previewUrl`:** Optional per-pack HTTP(S) image URL; currently displayed as text in details; future window will load into `TextureBrush(Image)` like `PluginsWindow.PluginEntry.TryGetPluginIcon` caching pattern. Not validated.
* **`tags`:** Optional free-form `string[]`; concatenated into search haystack `name + id + description + category` (lowercased in `RebuildAssetList`). Recommend `["pbr","wall"]` style.
* **`category`:** Optional display string (e.g. `"Materials"`), shown in details, searchable. Not enum-enforced.
* **`homepageUrl` / `sourceHomepageUrl`:** Optional links.
* **`website`:** Optional top-level string (not a pack field): the canonical site of the original source of the assets, for attribution/reference. The details pane shows it as a clickable **`Open <host> ↗`** button in its own row under the metadata table (`homepageUrl` is the fallback). The plugin never fetches it, and the click opens only `http`/`https` links: `Platform.CanOpenUrl` asks the OS for any registered handler, so a `file://`, `mailto:` or custom-scheme value is refused rather than launched (D14). It is documentation for humans, not a link the installer follows.

---

## 8. Install Manifest Relationship

After successful install the editor writes (via `SettingsService.SaveInstalled`) to:

```
Content/FlaxAssetManager/.installed.json  → InstalledManifest
  { installed: [ InstalledEntry { sourceId, sourceUrl, packId, packName, packType, installedVersion, installedAt, distributionUrl?, resolvedCommit?, installPath, files[], generatedAssets[], variant? } ] }
```

Key is **`sourceId+packId`** (`FindInstalled`). Update badge: `HasUpdate(pack, sourceId)` is `installedVersion != pack.version` (ordinal). For plugin projects, `resolvedCommit` is also stored for future `commit` diff / manual `git ls-remote` check, but version bump is the source of truth.

`variant` (`string | null`, `null` = default set) records which variant's files were installed for asset packs. Existing `.installed.json` files without the field deserialize unchanged (`variant` = `null`).

`generatedAssets` (`string[]`, project-relative) lists the Content-tree assets the install **produced** rather
than downloaded: the `.flax` assets the model import created in the pack root, and the generated
`M_<packId>.flax`. They are not in `files[]` (which lists downloaded bytes), so an uninstall that ignored
them deletes the downloads and leaves the visible assets behind. `AssetInstallerService.DeleteGeneratedAssetsAsync`
deletes this list, plus the derived sidecars of every recorded file the editor imports (`.ImportedSourceExts`:
`Rock.tga` also removes `Rock.tga.flax` and the `Rock.tga/` derived-data folder — which also cleans installs
made before the field existed), through the same project-containment gate as `files[]`, and then removes the
pack folder if it is empty. Old records without the field deserialize to an empty list.

---

## 9. Versioning

* `pack.version` is an opaque dot-separated string; no semver parser. The author *must bump* `version` (e.g. `1.0.0 → 1.1.0`) for the window to show *Update available*. Same string is what gets recorded as `installedVersion`.
* `sourceVersion` is informational for the manifest author, not used for per-pack badges.

---

## 10. Error Handling & Reporting

* Fetch errors (HTTP, JSON parse, `ValidateManifest` failure) produce `FetchedSource{Url, Error}` and the source picker shows `"Label (error)"`. Other sources continue.
* `ValidateManifest` returns the first human string — included in `Debug.LogError` and in the status label `"N OK, M errors"`.
* Install errors per assetPack file are aggregated; if any file fails, the whole pack is considered failed and no `InstalledEntry` is written (no partial entry).
* Installer events `AssetInstallerService.Log` / `Progress(float,text)` drive `StatusLabel` / `ProgressBar.Value` in the window.

---

## 11. Full Permutations — Minimal → Maximal Examples

### 11.1 Minimal assetPack (one script)

```json
{
  "sourceName": "Tiny", "sourceId": "com.tiny", "assets": [
    { "id": "hello", "name": "Hello", "packType": "assetPack", "version": "1.0.0",
      "files": [{ "url": "https://example.com/Hello.cs", "path": "Hello.cs", "type": "Script" }] }
  ]
}
```

### 11.2 AssetPack exercising every `type`

```json
{
  "sourceName": "Kitchen Sink", "sourceId": "com.kitchen", "assets": [{
    "id": "all_types", "name": "All Types", "packType": "assetPack", "version": "1.0.0",
    "category": "Mixed", "tags": ["kitchen","sink"], "previewUrl": "https://example.com/preview.jpg", "license": "MIT",
    "files": [
      { "url": "https://example.com/diffuse.png",   "path": "Textures/diffuse.png",   "type": "Texture" },
      { "url": "https://example.com/mat.flax",      "path": "Materials/Mat.flax",     "type": "Material" },
      { "url": "https://example.com/node.hlsl",     "path": "Materials/Node.hlsl",    "type": "MaterialNode" },
      { "url": "https://example.com/model.fbx",     "path": "Models/Box.fbx",         "type": "Model" },
      { "url": "https://example.com/prefab.flax",   "path": "Prefabs/Cube.flax",      "type": "Prefab" },
      { "url": "https://example.com/logic.cs",      "path": "Scripts/Logic.cs",       "type": "Script" },
      { "url": "https://example.com/graph.flax",    "path": "Anim/Graph.flax",        "type": "AnimGraph" },
      { "url": "https://example.com/sound.wav",     "path": "Audio/Hit.wav",          "type": "Audio" },
      { "url": "https://example.com/shader.hlsl",   "path": "Shaders/MyShader.hlsl",  "type": "Shader" }
    ]}]
}
```

All 9 `type` values present. For `Script:Logic.cs`, `path` must be `.cs` otherwise rejected. Others go under `Content/...` (script goes under `Source/...`).

### 11.3 AssetPack with checksum & subfolder reuse

```json
{
  "sourceName": "Checksum Demo", "sourceId": "com.checksum", "assets": [{
    "id": "checked", "name": "Checked", "packType": "assetPack", "version": "1.0.0",
    "files": [
      { "url": "https://example.com/a.png",  "path": "A/a.png",  "type": "Texture", "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" },
      { "url": "https://example.com/b.png",  "path": "A/b.png",  "type": "Texture" }
    ]}]
}
```

`sha256` is lower-hex SHA-256 of the response bytes (`SHA256.Create()`). On mismatch, install fails before touching `files[]` entry.

### 11.4 PluginProject — minimal

```json
{
  "sourceName": "Plugins", "sourceId": "com.plugins", "assets": [{
    "id": "myplugin", "name": "My Plugin", "packType": "pluginProject", "version": "1.0.0",
    "distribution": { "kind": "gitRepo", "url": "https://github.com/org/MyPlugin.git" }
  }]
}
```

Clones `https://github.com/org/MyPlugin.git` → `Plugins/MyPlugin` (last segment strip `.git`), expects a `*.flaxproj` at repo root.

### 11.5 PluginProject — branch + pinned commit

```json
{
  "sourceName": "Plugins", "sourceId": "com.plugins", "assets": [{
    "id": "pinned", "name": "Pinned", "packType": "pluginProject", "version": "2.0.0",
    "distribution": { "kind": "gitRepo", "url": "https://github.com/org/Pinned.git", "branch": "release/1.x", "commit": "abc123def456..." }
  }]
}
```

Installer does `clone -b "release/1.x"` then `git -C dest checkout abc123...`. `resolvedCommit` recorded even if `commit` was absent.

### 11.6 Multi-source mixed (what Sources.json looks like + two manifests)

`Content/FlaxAssetManager/Sources.json` (user config):

```json
{ "sources": [
  { "url": "https://example.com/manifest.json", "enabled": true,  "name": "Example" },
  { "url": "https://team.internal/assets.json",  "enabled": true  },
  { "url": "https://old.example.com/old.json",   "enabled": false }
]}
```

Two manifests behind the first two URLs are shown as two entries in the source picker (`FlaxAssetManagerWindow._sourceCombo`). Disabled source is skipped during `FetchAllAsync`. Each source's `sourceId`+`pack.id` are independent — same `id` under different `sourceId` is distinct `InstalledEntry`.

### 11.7 Invalid cases (each yields `(error)`)

Missing top fields:
```json
{ "assets": [] }
→ "sourceName is required."
{ "sourceName":"X","sourceId":"com.x","assets":[] }
→ "assets array is required and must not be empty."
```

AssetPack wrong payload:
```json
{ "sourceName":"X","sourceId":"com.x","assets":[{ "id":"x","name":"X","packType":"assetPack","version":"1.0","distribution":{"kind":"gitRepo","url":"https://…"}}]}
→ "assetPack must not have distribution"
{ "sourceName":"X","sourceId":"com.x","assets":[{ "id":"x","name":"X","packType":"assetPack","version":"1.0"}]}
→ "assetPack requires non-empty files[] (or non-empty variants[])"
{ "sourceName":"X","sourceId":"com.x","assets":[{ "id":"x","name":"X","packType":"assetPack","version":"1.0","files":[{"url":"https://…","path":"a.png","type":"Texture"}],"variants":[{"id":"a","name":"A","files":[]}]}]}
→ "variant 'a' requires non-empty files[]"
{ "sourceName":"X","sourceId":"com.x","assets":[{ "id":"x","name":"X","packType":"assetPack","version":"1.0","files":[{"url":"https://…","path":"a.png","type":"Texture"}],"variants":[{"id":"a","name":"A","files":[{"url":"https://…","path":"a.png","type":"Texture"},{"url":"https://…","path":"b.png","type":"Texture"}]},{"id":"a","name":"B","files":[{"url":"https://…","path":"a.png","type":"Texture"}]}]}]}
→ "duplicate variant id 'a'"
{ "sourceName":"X","sourceId":"com.x","assets":[{ "id":"x","name":"X","packType":"pluginProject","version":"1.0","distribution":{"kind":"gitRepo","url":"https://…"},"variants":[{"id":"a","name":"A","files":[{"url":"https://…","path":"a.png","type":"Texture"}]}]}]}
→ "pluginProject must not have variants[]"
```

PluginProject wrong payload:
```json
{ "sourceName":"X","sourceId":"com.x","assets":[{ "id":"x","name":"X","packType":"pluginProject","version":"1.0","files":[{"url":"https://…","path":"a.png","type":"Texture"}]}]}
→ "pluginProject must not have files[]"
{ "sourceName":"X","sourceId":"com.x","assets":[{ "id":"x","name":"X","packType":"pluginProject","version":"1.0"}]}
→ "pluginProject requires distribution"
{ "sourceName":"X","sourceId":"com.x","assets":[{ "id":"x","name":"X","packType":"pluginProject","version":"1.0","distribution":{"kind":"archive","url":"https://…"}}]}
→ "distribution.kind must be 'gitRepo'"

ArchivePack wrong payload:
```json
{ "sourceName":"X","sourceId":"com.x","assets":[{ "id":"x","name":"X","packType":"archivePack","version":"1.0","distribution":{"kind":"archive","url":"https://…/pack.zip"},"files":[{"url":"https://…","path":"a.png","type":"Texture"}]}]}
→ "archivePack must not have files[] (the zip provides them)"
{ "sourceName":"X","sourceId":"com.x","assets":[{ "id":"x","name":"X","packType":"archivePack","version":"1.0"}]}
→ "archivePack requires distribution"
{ "sourceName":"X","sourceId":"com.x","assets":[{ "id":"x","name":"X","packType":"archivePack","version":"1.0","distribution":{"kind":"gitRepo","url":"https://…"}}]}
→ "archivePack distribution.kind must be 'archive'"
{ "sourceName":"X","sourceId":"com.x","assets":[{ "id":"x","name":"X","packType":"archivePack","version":"1.0","distribution":{"kind":"archive","url":"https://…/pack.tar.gz"}}]}
→ "archivePack distribution.url must point to a .zip file"
```

File rules:
```json
{ "url":"https://…","path":"../evil.png","type":"Texture" } → "must be relative and not contain '..'"
{ "url":"ftp://…","path":"a.png","type":"Texture" }          → "file url must be http(s)"
{ "url":"https://…","path":"note.md","type":"Texture" }      → "extension '.md' not allowed"
{ "url":"https://…","path":"bad.cpp","type":"Script" }       → "Script requires .cs extension"
{ "url":"https://…","path":"good.cs","type":"Material" }     → "has script extension but type is Material"
{ "url":"https://…","path":"a.png","type":"Other" }          → unknown type → JSON error
```

### 11.8 AssetPack with base files + variants (resolution sets)

```json
{
  "sourceName": "Textures", "sourceId": "com.textures", "assets": [{
    "id": "asphalt", "name": "Asphalt", "packType": "assetPack", "version": "1.0.0",
    "license": "CC0",
    "files": [
      { "url": "https://cdn.example.com/8k/asphalt_diff_8k.jpg", "path": "diff_8k.jpg", "type": "MaterialTexture" },
      { "url": "https://cdn.example.com/8k/asphalt_nor_dx_8k.jpg", "path": "nor_dx_8k.jpg", "type": "MaterialTexture" }
    ],
    "variants": [
      { "id": "1k", "name": "1K", "files": [
        { "url": "https://cdn.example.com/1k/asphalt_diff_1k.jpg", "path": "diff_1k.jpg", "type": "MaterialTexture" },
        { "url": "https://cdn.example.com/1k/asphalt_nor_dx_1k.jpg", "path": "nor_dx_1k.jpg", "type": "MaterialTexture" }
      ]},
      { "id": "4k", "name": "4K", "files": [
        { "url": "https://cdn.example.com/4k/asphalt_diff_4k.jpg", "path": "diff_4k.jpg", "type": "MaterialTexture" },
        { "url": "https://cdn.example.com/4k/asphalt_nor_dx_4k.jpg", "path": "nor_dx_4k.jpg", "type": "MaterialTexture" }
      ]}
    ]
  }]
}
```

Base `files[]` (8k) is the default set; the detail panel shows a Variant dropdown with `Default, 1K, 4K`. Selecting `1K` and installing writes the `1k` files and records `"variant": "1k"` in `.installed.json`.

### 11.9 Variants-only AssetPack (no base files)

```json
{
  "sourceName": "Textures", "sourceId": "com.textures", "assets": [{
    "id": "hd_wall", "name": "HD Wall", "packType": "assetPack", "version": "1.0.0",
    "variants": [
      { "id": "1k", "name": "1K", "files": [
        { "url": "https://cdn.example.com/1k/wall_diff_1k.jpg", "path": "diff_1k.jpg", "type": "MaterialTexture" }
      ]},
      { "id": "4k", "name": "4K", "files": [
        { "url": "https://cdn.example.com/4k/wall_diff_4k.jpg", "path": "diff_4k.jpg", "type": "MaterialTexture" }
      ]}
    ]
  }]
}
```

No `files[]` — allowed because `variants[]` is non-empty (the conditional-`files` rule, §5.3). The first variant `1k` is the default; the dropdown lists only `1K, 4K` (no `Default`), and a plain install writes the `1k` files with `"variant": "1k"` recorded.


### 11.10 ArchivePack — minimal

```json
{
  "sourceName": "Zips", "sourceId": "com.zips", "assets": [{
    "id": "box_pack", "name": "Box Pack", "packType": "archivePack", "version": "1.0.0",
    "license": "CC0",
    "distribution": { "kind": "archive", "url": "https://example.com/packs/box_pack.zip" }
  }]
}
```

Downloads `box_pack.zip`, extracts its contents into `Content/FlaxAssetManager/com.zips/box_pack/files/` preserving the zip's internal folder layout. No `files[]`/`variants[]` — the zip defines the content.

### 11.11 ArchivePack — pinned checksum

```json
{
  "sourceName": "Zips", "sourceId": "com.zips", "assets": [{
    "id": "stone_pack", "name": "Stone Pack", "packType": "archivePack", "version": "2.0.0",
    "distribution": { "kind": "archive", "url": "https://example.com/packs/stone_pack.zip", "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" }
  }]
}
```

`sha256` is the lower-hex SHA-256 of the zip bytes. On mismatch the install aborts before extraction.
---

## 12. Install Destinations (derived from `files[].type`)

| `type` | `Settings` key | Full path pattern (`Globals.ProjectFolder` prefix) |
|--------|----------------|----------------------------------------------------|
| `Script` | `scriptDestination` (`Source/FlaxAssetManager`) | `<project>/Source/FlaxAssetManager/<sanitized(sourceId)>/<sanitized(packId)>/files/<file.path>` |
| All others | `contentDestination` (`Content/FlaxAssetManager`) | `<project>/Content/FlaxAssetManager/<sanitized(sourceId)>/<sanitized(packId)>/files/<file.path>` |

Downloaded files live under a `files/` subfolder of the pack folder. Pack-*generated* assets are excluded: the `.flax` material created by *Install and create material* stays at `<contentDestination>/<sourceId>/<packId>/M_<packId>.flax` (no `files/`), and imported model sources register their `.flax` asset in the pack root too.

`Sanitize` replaces invalid filename chars and `/\..` with `_`. Traversal guard `Path.GetFullPath(combined).StartsWith(rootFull)` enforced in `AssetInstallerService.ResolveAssetPackPath`.

Plugin projects ignore `files[]` routing — straight to `Plugins/<Sanitized(folderName)>` with `Plugins/` fixed. Archive packs extract into `<contentDestination>/<sourceId>/<packId>/files/` (the zip defines the layout; see §6).

---

## 13. Update & Idempotence

* **Update badge:** `SettingsService.HasUpdate(pack, sourceId)` is `installed.installedVersion != pack.version` ordinal. Triggered by user **Refresh**.
* **Install manifest:** written after *all* files succeed (`File.WriteAllBytesAsync` per file), with `installedAt: DateTime.UtcNow`, `installPath`, `files[]` (relative `MakeRelative` paths), and `resolvedCommit` for git.
* **Re-install / Update** reinstalls all files in full (future: per-file sha256 skip). `Settings.overwriteExisting` (default `false`) is honoured: with it `false`, a file that already exists is left byte-for-byte untouched — no download, no checksum check, no model import — and is still recorded as part of the pack (logged as skipped).

---

## 14. Security Notes for Authors

* No auth — every `url` is publicly fetchable.
* **Trust boundary:** a manifest is remote input. For `pluginProject`, git runs argument-per-argument (`ProcessStartInfo.ArgumentList`) with a `--` end-of-options marker, and the URL must be `https://`, the branch must pass a conservative ref-name check, and the commit must be 7-40 hex characters (`Core/Utils/GitSafety.cs`) — so values that start with `-` (e.g. `--upload-pack=…`, which runs a local program) are rejected. Do not publish `pluginProject` packs to a shared catalog before that validation has been reviewed; the catalog is a convenience index, not a security boundary (see `docs/DECISIONS.md` D12).
* Downloads stream to disk, run 6 in parallel, time out after 60 s per file, and are capped at **2 GiB per file** — publish reasonable file sizes.
* Publish `sha256` for integrity if files are critical — for `archivePack` it covers the whole zip.
* `archivePack` zips are extracted with a zip-slip guard and directory entries are ignored; still avoid publishing archives containing absolute or `..` paths.
* Keep `sourceId` stable forever; changing it breaks updates.
* Keep `packId` stable; bump `version` for new content under the same pack naming.

---

## Index manifests (`manifestKind: "index"`)

An index is a manifest whose job is to point at **other manifests**. One URL becomes a table of contents
the owner can update whenever they like, and users choose which entries to add as sources.
`example_index.json` in the plugin root shows the shape.

```json
{
  "sourceName": "My index",
  "sourceId": "com.example.index",
  "manifestKind": "index",
  "entries": [
    {
      "sourceId": "com.example.textures",
      "sourceName": "Example textures",
      "url": "https://example.com/manifests/textures.json",
      "description": "PBR texture sets",
      "tags": ["textures", "PBR"]
    }
  ]
}
```

| Field | Required | Notes |
| --- | --- | --- |
| `sourceName`, `sourceId` | yes | identify the index source itself |
| `website` | no | Top-level optional field shared with pack manifests — the index owner's site, for attribution/reference |
| `manifestKind` | yes, `"index"` | absent ⇒ a pack manifest (the default) |
| `entries[]` | yes, non-empty | the linked manifests |
| `entries[].sourceId` | yes | the `sourceId` the linked manifest declares; unique in the index; no spaces |
| `entries[].sourceName` | yes | display name in the list |
| `entries[].url` | yes | absolute `http(s)` URL of the linked manifest |
| `entries[].website` | no | upstream site of the linked source; the index details panel shows it as a clickable browser link (the plugin never fetches it) |
| `entries[].description`, `entries[].tags` | no | shown in the list, matched by search |

Rules enforced by `AssetSourceManager.ValidateIndex` (the whole index is rejected at the first violation):

* An index **must not** carry `assets[]` — a manifest is one kind or the other.
* Entry ids and URLs are unique inside the index.
* An entry URL must be an absolute http(s) URL. The linked manifest is then fetched and validated under
  exactly the same rules as any other source, so an index grants nothing to its entries: **trust is per
  URL, never transitive**, and installing anything stays a user action, one entry at a time.
* Adding an entry whose `sourceId` is already a configured source is refused in the UI, because `sourceId`
  keys the install record and two sources sharing one would merge their records.
* An index pointing at itself is refused by the duplicate-URL check (its own URL is already a source).
  Indexes may nest — an entry may be an index — because the plugin never follows links on its own, so
  depth costs the user one deliberate click per level and cannot recurse.

An index installs nothing, caches nothing as content, and writes nothing to `.installed.json`.

