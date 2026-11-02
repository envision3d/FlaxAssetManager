using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FlaxEngine;
using FlaxAssetManager.Core.Models;
using FlaxAssetManager.Core.Services;
using FlaxAssetManager.Core.Utils;

namespace FlaxAssetManager.Core.Tests
{
    /// <summary>
    /// Live-editor end-to-end checks for the variant pipeline: fetch -> GetEffectiveFiles ->
    /// validation rules -> real install of two variants -> the window's variant selector.
    /// <para>
    /// Runtime gate only: it starts when the FLAX_AM_E2E=1 environment variable is set (see
    /// FlaxAssetManagerEditor.StartE2EIfRequested). A compile-time gate is not available for plugin
    /// modules on stock Flax 1.13 - the builder exposes no C# preprocessor-define path that reaches a
    /// plugin assembly - so this file ships inside the editor module and stays inert without the env
    /// var (evidence and discussion in docs/DECISIONS.md D4).
    /// </para>
    /// <para>
    /// It is deliberately <b>source-agnostic</b>: it drives whatever sources the user has added to the
    /// window, picks the first fetched pack that offers at least two variants, and derives every
    /// expectation from that manifest. Nothing here names a source, a pack id, a variant id, a file
    /// name, a URL host or a pack count — a manifest that changes upstream cannot make this test
    /// wrong, and adding a source never requires editing the test. If no fetched pack offers two
    /// variants the variant checks report as skipped, not failed.
    /// </para>
    /// </summary>
    public static class VariantE2ETest
    {
        static int passed, failed;
        static readonly BindingFlags NF = BindingFlags.NonPublic | BindingFlags.Instance;

        static void Check(bool cond, string name)
        {
            if (cond) { passed++; Debug.Log($"[E2E PASS] {name}"); }
            else { failed++; Debug.LogError($"[E2E FAIL] {name}"); }
        }

        static object GetField(object obj, string name) => obj.GetType().GetField(name, NF).GetValue(obj);
        static object Invoke(object obj, string name, params object[] args)
        {
            var types = Array.ConvertAll(args, a => (Type)(a == null ? typeof(object) : a.GetType()));
            return obj.GetType().GetMethod(name, NF, types).Invoke(obj, args);
        }
        static object GetProp(object obj, string name) => obj.GetType().GetProperty(name).GetValue(obj);
        static void SetProp(object obj, string name, object value) => obj.GetType().GetProperty(name).SetValue(obj, value);

        static string[] PathsOf(List<FileEntry> files) => files == null ? new string[0] : files.Select(f => f.Path).ToArray();
        static string[] NamesOf(List<string> paths) => paths.Select(p => System.IO.Path.GetFileName(p)).ToArray();
        static bool SameSet(string[] a, string[] b) => a.Length == b.Length && a.OrderBy(x => x).SequenceEqual(b.OrderBy(x => x));

        public static async Task RunAll(global::FlaxAssetManagerEditor.FlaxAssetManagerEditor plugin)
        {
            Debug.Log("== FlaxAssetManager variants E2E ==");
            try
            {
                var settings = plugin.Settings;
                var sm = plugin.SourceManager;
                var installer = plugin.Installer;

                // ---- 1. Fetch every configured source over the network ----
                await sm.FetchAllAsync();
                var fetched = sm.LastFetched.Where(s => s.Success && s.Manifest != null).ToList();
                Check(fetched.Count > 0, "fetch: at least one configured source fetched successfully");
                Check(sm.LastFetched.All(s => s.Success || !string.IsNullOrEmpty(s.Error)),
                    "fetch: every failed source reports why");
                if (fetched.Count == 0) { Skip("no source fetched - add a source URL in the window"); return; }

                // Probe = first pack offering >= 2 variants, from any source. Everything below is derived from it.
                var probe = fetched
                    .SelectMany(s => (s.Manifest.Assets ?? new List<PackEntry>()).Select(p => new { Source = s, Pack = p }))
                    .FirstOrDefault(x => x.Pack.Variants != null && x.Pack.Variants.Count >= 2);
                if (probe == null) { Skip("no fetched pack offers 2+ variants - add a source whose manifest has variants"); return; }

                var src = probe.Source;
                var man = src.Manifest;
                var pack = probe.Pack;
                var vA = pack.Variants[0];
                var vB = pack.Variants[1];
                Debug.Log($"probing source '{man.SourceId}' pack '{pack.Id}' variants '{vA.Id}' / '{vB.Id}'");

                Check(man.Assets.All(a => a.Variants == null || a.Variants.Count == a.Variants.Select(v => v.Id).Distinct().Count()),
                    "manifest: variant ids unique in every pack");
                Check(pack.Variants.All(v => v.Files != null && v.Files.Count > 0), "manifest: every probed variant has files");

                // ---- 2. GetEffectiveFiles semantics, derived from the manifest ----
                var baseFiles = pack.Files != null && pack.Files.Count > 0 ? pack.Files : vA.Files;
                Check(SameSet(PathsOf(pack.GetEffectiveFiles(null)), PathsOf(baseFiles)),
                    "null variant -> base files, falling back to the first variant");
                Check(SameSet(PathsOf(pack.GetEffectiveFiles(vA.Id)), PathsOf(vA.Files)), $"variant '{vA.Id}' -> its own files");
                Check(SameSet(PathsOf(pack.GetEffectiveFiles(vB.Id)), PathsOf(vB.Files)), $"variant '{vB.Id}' -> its own files");
                Check(SameSet(PathsOf(pack.GetEffectiveFiles("__no_such_variant__")), PathsOf(baseFiles)),
                    "unknown variant id -> same files as null");

                // Variants-only pack built in code: first variant is the default (no network involved)
                var only = new PackEntry
                {
                    Id = "t", Name = "T", Version = "1.0.0", PackType = PackType.AssetPack,
                    Variants = new List<PackVariant>
                    {
                        new PackVariant { Id = "a", Name = "A", Files = new List<FileEntry> { new FileEntry { Url = "https://example.com/a/x.jpg", Path = "x.jpg", Type = FileAssetType.MaterialTextures } } },
                        new PackVariant { Id = "b", Name = "B", Files = new List<FileEntry> { new FileEntry { Url = "https://example.com/b/y.jpg", Path = "y.jpg", Type = FileAssetType.MaterialTextures } } },
                    }
                };
                Check(only.GetEffectiveFiles(null)?.Count == 1 && only.GetEffectiveFiles(null)[0].Path == "x.jpg",
                    "variants-only: first variant is the default");
                Check(only.GetEffectiveFiles("b")[0].Path == "y.jpg", "variants-only: explicit variant wins");

                // ---- 3. Validation rules (synthetic manifests, no network) ----
                FileEntry F(string path) => new FileEntry { Url = $"https://example.com/{path}", Path = path, Type = FileAssetType.MaterialTextures };
                AssetSourceManifest EdgeManifest(Action<PackEntry> mutate)
                {
                    var p = new PackEntry
                    {
                        Id = "p", Name = "P", Version = "1.0.0", PackType = PackType.AssetPack,
                        Files = new List<FileEntry> { F("a.jpg") },
                        Variants = new List<PackVariant>
                        {
                            new PackVariant { Id = "1k", Name = "1K", Files = new List<FileEntry> { F("1k/a.jpg") } },
                            new PackVariant { Id = "4k", Name = "4K", Files = new List<FileEntry> { F("4k/a.jpg") } },
                        }
                    };
                    mutate(p);
                    return new AssetSourceManifest { SourceName = "t", SourceId = "t", Assets = new List<PackEntry> { p } };
                }
                Check(AssetSourceManager.ValidateManifest(EdgeManifest(p => p.Files = null)) == null,
                    "assetPack w/o base files but w/ variants is valid");
                Check(AssetSourceManager.ValidateManifest(EdgeManifest(p => { p.Files = null; p.Variants = null; }))
                    == "Pack p: packType assetPack requires non-empty files[] (or non-empty variants[]).",
                    "assetPack w/o files and w/o variants is rejected");
                Check(AssetSourceManager.ValidateManifest(EdgeManifest(p => p.Variants[1].Id = "1k"))
                    == "Pack p: duplicate variant id '1k'.", "duplicate variant id rejected");
                Check(AssetSourceManager.ValidateManifest(EdgeManifest(p => p.Variants[0].Files = new List<FileEntry>()))
                    == "Pack p: variant '1k' requires non-empty files[].", "empty variant files rejected");
                var mPluginVar = new AssetSourceManifest
                {
                    SourceName = "t", SourceId = "t",
                    Assets = new List<PackEntry>
                    {
                        new PackEntry
                        {
                            Id = "plug", Name = "Plug", Version = "1.0.0", PackType = PackType.PluginProject,
                            Distribution = new DistributionInfo { Kind = "gitRepo", Url = "https://example.com/x.git" },
                            Variants = new List<PackVariant> { new PackVariant { Id = "1k", Name = "1K", Files = new List<FileEntry> { F("a.jpg") } } }
                        }
                    }
                };
                Check(AssetSourceManager.ValidateManifest(mPluginVar) == "Pack plug: pluginProject must not have variants[].",
                    "pluginProject with variants rejected");

                // archivePack payload rules
                AssetSourceManifest ArcManifest(Action<PackEntry> mutate)
                {
                    var p = new PackEntry
                    {
                        Id = "arc", Name = "Arc", Version = "1.0.0", PackType = PackType.ArchivePack,
                        Distribution = new DistributionInfo { Kind = "archive", Url = "https://example.com/packs/arc.zip" }
                    };
                    mutate(p);
                    return new AssetSourceManifest { SourceName = "t", SourceId = "t", Assets = new List<PackEntry> { p } };
                }
                Check(AssetSourceManager.ValidateManifest(ArcManifest(_ => { })) == null, "archivePack minimal is valid");
                Check(AssetSourceManager.ValidateManifest(ArcManifest(p => p.Files = new List<FileEntry> { F("a.jpg") }))
                    ?.Contains("archivePack must not have files") == true, "archivePack with files rejected");
                Check(AssetSourceManager.ValidateManifest(ArcManifest(p => p.Distribution = null))
                    ?.Contains("archivePack requires distribution") == true, "archivePack w/o distribution rejected");
                Check(AssetSourceManager.ValidateManifest(ArcManifest(p => p.Distribution = new DistributionInfo { Kind = "gitRepo", Url = "https://example.com/x.git" }))
                    == "Pack arc: archivePack distribution.kind must be 'archive'.", "archivePack with gitRepo kind rejected");
                Check(AssetSourceManager.ValidateManifest(ArcManifest(p => p.Distribution = new DistributionInfo { Kind = "archive", Url = "https://example.com/packs/arc.tar.gz" }))
                    == "Pack arc: archivePack distribution.url must point to a .zip file.", "archivePack non-zip url rejected");
                Check(AssetSourceManager.ValidateManifest(ArcManifest(p => p.Variants = new List<PackVariant> { new PackVariant { Id = "1k", Name = "1K", Files = new List<FileEntry> { F("a.jpg") } } }))
                    == "Pack arc: archivePack must not have variants[].", "archivePack with variants rejected");

                // ---- 4. Real install: variant A, then variant B over it ----
                bool okA = await installer.InstallAsync(pack, man.SourceId, src.Url, null, CancellationToken.None, vA.Id, manifest: man);
                Check(okA, $"install variant '{vA.Id}' succeeds");
                var instA = settings.FindInstalled(man.SourceId, pack.Id);
                Check(instA != null && instA.Variant == vA.Id, $"installed record tracks variant '{vA.Id}'");
                Check(instA != null && instA.Files.All(f => System.IO.File.Exists(System.IO.Path.Combine(Globals.ProjectFolder, f))),
                    "installed files exist on disk");
                Check(instA != null && SameSet(NamesOf(instA.Files), NamesOf(PathsOf(vA.Files).ToList())),
                    $"installed file set matches variant '{vA.Id}' from the manifest");
                // Each variant's set is written into its own subfolder of files/, so the record's paths name
                // the tier - nothing overwrites another resolution's identical file names.
                var subA = AssetInstallerService.VariantSubFolder(vA.Id);
                var subB = AssetInstallerService.VariantSubFolder(vB.Id);
                Check(instA != null && instA.Files.All(f => f.Replace('\\', '/').Contains("/" + AssetInstallerService.FilesSubFolder + "/" + subA + "/")),
                    $"variant '{vA.Id}' installs under files/{subA}/");

                bool okB = await installer.InstallAsync(pack, man.SourceId, src.Url, null, CancellationToken.None, vB.Id, manifest: man);
                Check(okB, $"reinstall as variant '{vB.Id}' succeeds");
                var instB = settings.FindInstalled(man.SourceId, pack.Id);
                Check(instB != null && instB.Variant == vB.Id, $"installed record updated to variant '{vB.Id}'");
                Check(instB != null && SameSet(NamesOf(instB.Files), NamesOf(PathsOf(vB.Files).ToList())),
                    $"installed file set matches variant '{vB.Id}' from the manifest");
                Check(instB != null && instB.Files.All(f => System.IO.File.Exists(System.IO.Path.Combine(Globals.ProjectFolder, f))),
                    "re-installed files exist on disk");

                // A switch writes new paths under files/<variant>/ instead of overwriting the installed
                // ones, so the set it replaces has to be removed by the install: one pack keeps one variant
                // on disk, its own folder, and none of the previous variant's files or imported assets.
                Check(instA != null && instA.Files.All(f => !System.IO.File.Exists(System.IO.Path.Combine(Globals.ProjectFolder, f))),
                    $"installing '{vB.Id}' removed every file of '{vA.Id}'");
                Check(instA != null && instA.Files.All(f => !System.IO.File.Exists(System.IO.Path.Combine(Globals.ProjectFolder, f + ".flax"))),
                    $"installing '{vB.Id}' removed the imported .flax assets of '{vA.Id}'");
                var staleDir = System.IO.Path.Combine(System.IO.Path.GetFullPath(System.IO.Path.Combine(Globals.ProjectFolder, instB?.InstallPath ?? string.Empty)),
                                                      AssetInstallerService.FilesSubFolder, subA);
                Check(!System.IO.Directory.Exists(staleDir), $"the '{vA.Id}' variant folder under files/ is gone");
                Check(instB != null && instB.Files.All(f => f.Replace('\\', '/').Contains("/" + AssetInstallerService.FilesSubFolder + "/" + subB + "/")),
                    $"variant '{vB.Id}' installs under files/{subB}/");

                // ---- 4b. Rights snapshot: what the project ships, recorded at install time ----
                // A shipped game's credits are assembled from these fields, so an install that
                // records no license is an obligation quietly lost - and the values have to be the
                // manifest's, copied at install, not looked up later: a catalog can relabel a source,
                // a publisher can relicense, and the record is what the project can still prove.
                Check(!string.IsNullOrWhiteSpace(instB.License), "install record carries the pack's license");
                Check(instB.License == pack.License, "install record's license is the manifest's, verbatim");
                Check(!string.IsNullOrWhiteSpace(instB.LicenseUrl), "install record carries a license URL");
                Check(!string.IsNullOrWhiteSpace(instB.SourceName), "install record names the source it came from");
                Check(!string.IsNullOrWhiteSpace(CreditLine.Build(pack, man)), "a credit line can be built for the pack");
                Check(System.IO.File.Exists(settings.AttributionsPath), "installing wrote ATTRIBUTIONS.md into Content/FlaxAssetManager");
                var attr = System.IO.File.Exists(settings.AttributionsPath)
                    ? System.IO.File.ReadAllText(settings.AttributionsPath) : "";
                Check(attr.Contains(pack.Id), "ATTRIBUTIONS.md names the installed pack");
                Check(attr.Contains(man.SourceId), "ATTRIBUTIONS.md names the source the pack came from");
                if (!CreditLine.NoCreditRequired(pack.License))
                    Check(!string.IsNullOrWhiteSpace(instB.Credit), $"a {pack.License} pack records a credit line");

                // ---- 5. UI: the window's variant selector (all GUI work marshalled to the main thread) ----
                var window = GetField(plugin, "_window");
                Check(window != null, "window exists");
                if (window == null) { Report(); return; }

                plugin.ShowWindow();
                var deadline = DateTime.UtcNow.AddSeconds(30);
                List<FetchedSource> list = null;
                while (DateTime.UtcNow < deadline)
                {
                    list = (List<FetchedSource>)GetField(window, "_currentSources");
                    if (list != null && list.Count > 0) break;
                    await Task.Delay(250);
                }
                int srcIdx = -1;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                        if (ReferenceEquals(list[i].Manifest, man) || list[i].Manifest?.SourceId == man.SourceId) srcIdx = i;
                Check(srcIdx >= 0, $"source '{man.SourceId}' present in the source combo");
                if (srcIdx < 0) { Report(); return; }

                await Scripting.RunOnUpdate(() => SetProp(GetField(window, "_sourceCombo"), "SelectedIndex", srcIdx));
                await Task.Delay(400);

                // rowItem / noVarRow hold ListRow instances; kept as object so the harness carries no
                // UI-kit type dependency - reflection passes them straight into SelectPack.
                PackEntry rowPack = null, noVarPack = null;
                object rowItem = null, noVarRow = null;
                await Scripting.RunOnUpdate(() =>
                {
                    var rowItemsObj = GetField(window, "_rowItems");
                    var en = rowItemsObj.GetType().GetMethod("GetEnumerator").Invoke(rowItemsObj, null);
                    var moveNext = en.GetType().GetMethod("MoveNext");
                    var current = en.GetType().GetProperty("Current");
                    while ((bool)moveNext.Invoke(en, null))
                    {
                        var cur = current.GetValue(en);
                        var p = (PackEntry)cur.GetType().GetField("Item1").GetValue(cur);
                        var r = cur.GetType().GetField("Item2").GetValue(cur);
                        // Identity is by id, not reference: the window keeps its own fetch snapshot
                        // (_currentSources = LastFetched.ToList()), so a window PackEntry and the pack
                        // this harness probed are two objects from two fetch generations of one manifest.
                        if (p.Id == pack.Id) { rowPack = p; rowItem = r; }
                        if (p.Variants == null && noVarPack == null) { noVarPack = p; noVarRow = r; }
                    }
                });
                Check(rowPack != null, $"asset list shows the probed pack '{pack.Id}'");
                if (rowPack == null) { Report(); return; }

                await Scripting.RunOnUpdate(() => Invoke(window, "SelectPack", rowPack, rowItem));
                await Task.Delay(300);

                var vcombo = GetField(window, "_variantCombo");
                var vrow = GetField(window, "_variantRow");
                Check((bool)GetProp(vrow, "Visible"), "variant row visible for a pack with variants");

                var expectedLabels = pack.Variants.Select(v => v.Name).ToArray();
                var labels = new string[expectedLabels.Length];
                for (int i = 0; i < expectedLabels.Length; i++)
                {
                    int j = i;
                    await Scripting.RunOnUpdate(() => SetProp(vcombo, "SelectedIndex", j));
                    object item = null;
                    await Scripting.RunOnUpdate(() => item = GetProp(vcombo, "SelectedItem"));
                    labels[i] = item?.ToString();
                }
                Check(labels.SequenceEqual(expectedLabels),
                    $"combo items match the manifest variant names ({string.Join("|", expectedLabels)})");
                Check((int)GetProp(vcombo, "SelectedIndex") == expectedLabels.Length - 1,
                    "combo reflects the last selection");

                await Scripting.RunOnUpdate(() => SetProp(vcombo, "SelectedIndex", 1));
                await Task.Delay(300);
                var key = man.SourceId + "/" + pack.Id;
                var sv = settings.Settings.SelectedVariants;
                Check(sv.ContainsKey(key) && sv[key] == vB.Id, $"selection persisted for '{key}' -> '{vB.Id}'");

                var text = GetProp(GetField(window, "_detailsInfo"), "Text")?.ToString() ?? "";
                var nameB = System.IO.Path.GetFileName(vB.Files[0].Path);
                var nameA = System.IO.Path.GetFileName(vA.Files[0].Path);
                Check(text.Contains(nameB), "details file listing reflects the selected variant");
                if (nameA != nameB)
                    Check(!text.Contains(nameA), "details listing drops the unselected variant's files");

                if (noVarPack != null)
                {
                    await Scripting.RunOnUpdate(() => Invoke(window, "SelectPack", noVarPack, noVarRow));
                    await Task.Delay(300);
                    Check(!(bool)GetProp(vrow, "Visible"), "variant row hidden for a pack without variants");
                }
                else
                    Check(true, "no variant-less pack in the list (skipped)");
            }
            catch (Exception e)
            {
                Debug.LogError($"[E2E FAIL] unhandled: {e}");
                failed++;
            }
            Report();
        }

        static void Skip(string why)
        {
            Check(true, $"skipped: {why}");
            Report();
        }

        static void Report()
        {
            Debug.Log($"== E2E done: {passed} passed, {failed} failed ==");
        }
    }
}
