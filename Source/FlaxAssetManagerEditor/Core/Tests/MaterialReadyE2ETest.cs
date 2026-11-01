using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlaxAssetManager.Core.Models;
using FlaxAssetManager.Core.Services;
using FlaxAssetManager.Core.Utils;
using FlaxEngine;

namespace FlaxAssetManager.Core.Tests
{
    /// <summary>
    /// Live-editor check that a material this plugin builds renders as soon as the install finishes —
    /// the regression behind "a freshly installed material renders pure black until I reload it by hand".
    /// <para>
    /// Runtime gate: runs only with <c>FLAX_AM_MATERIAL_E2E=1</c> (see
    /// <c>FlaxAssetManagerEditor.StartE2EIfRequested</c>), kept separate from <c>FLAX_AM_E2E</c> so this
    /// check can be run without the variant harness reinstalling packs. No compile-time gate is available
    /// for plugin modules on stock Flax (see docs/DECISIONS.md D4).
    /// </para>
    /// <para>
    /// <b>What "renders" means here.</b> The engine's <c>Material.SaveSurface</c> writes the asset file; it
    /// does not rebuild the loaded material. The material generator produces the shader, the parameter table
    /// and the <c>MaterialInfo</c> usage flags from the surface graph during <i>asset load</i>, so a saved
    /// graph only turns into renderable state when the asset gets loaded — which is why the engine's own
    /// material editor saves and then reloads, and why a hand reload fixes a black material.
    /// </para>
    /// <para>
    /// A brand-new asset is loaded by the engine as part of being created, so the stale case is an asset that
    /// is already in memory when the plugin writes a new graph into it: rebuilding a material, reinstalling a
    /// pack over its own material, re-importing — where the report comes from. Step 5 pins that. It rebuilds
    /// the pack's material through the plugin's own build path with the public multiplier parameters switched
    /// off, so the graph on disk no longer declares "Color" or "NormalStrength", and requires the loaded
    /// material — the object the renderer binds, with no reload in between — to stop exposing them. Before the
    /// fix the loaded material keeps reporting the parameters of the graph it was loaded with; after it, the
    /// saved graph is what the loaded material shows.
    /// </para>
    /// <para>
    /// Source-agnostic like the rest of the harness: it takes the first <i>installed</i> asset pack of any
    /// configured source whose effective file set contains <see cref="FileAssetType.MaterialTextures"/>,
    /// rebuilds that pack's material, and derives every expectation from the pack's own files. Nothing here
    /// names a source, vendor, pack id, variant id or URL.
    /// </para>
    /// </summary>
    public static class MaterialReadyE2ETest
    {
        static int passed, failed;

        static void Check(bool cond, string name)
        {
            if (cond) { passed++; Debug.Log($"[E2E PASS] {name}"); }
            else { failed++; Debug.LogError($"[E2E FAIL] {name}"); }
        }

        static void Skip(string why)
        {
            Check(true, $"skipped: {why}");
            Report();
        }

        static void Report()
        {
            Debug.Log($"== material E2E done: {passed} passed, {failed} failed ==");
        }

        /// <summary>
        /// Runs the check against the first installed texture pack of the configured sources.
        /// </summary>
        public static async Task RunAll(global::FlaxAssetManagerEditor.FlaxAssetManagerEditor plugin)
        {
            Debug.Log("== FlaxAssetManager material-readiness E2E ==");
            try
            {
                var settings = plugin.Settings;
                var sm = plugin.SourceManager;

                // ---- 1. The probe: an installed asset pack that produces a material ----
                await sm.FetchAllAsync();
                var fetched = sm.LastFetched.Where(s => s.Success && s.Manifest != null).ToList();
                if (fetched.Count == 0) { Skip("no source fetched - add a source URL in the window"); return; }

                var probe = (from source in fetched
                             from candidate in source.Manifest.Assets ?? new List<PackEntry>()
                             let variant = SelectedVariant(settings, source.Manifest.SourceId, candidate.Id)
                             where candidate.PackType == PackType.AssetPack
                                   && settings.FindInstalled(source.Manifest.SourceId, candidate.Id) != null
                                   && Textures(candidate.GetEffectiveFiles(variant)).Count > 0
                             select new { Source = source, Pack = candidate, Variant = variant })
                            .FirstOrDefault();
                if (probe == null) { Skip("no installed asset pack with MaterialTextures - install a texture pack first"); return; }

                var sourceId = probe.Source.Manifest.SourceId;
                var pack = probe.Pack;
                var variantId = probe.Variant;
                var textures = Textures(pack.GetEffectiveFiles(variantId));
                Debug.Log($"probing source '{sourceId}' pack '{pack.Id}' variant '{variantId ?? "<base>"}' with {textures.Count} material texture(s)");

                // ---- 2. Rebuild the material through the service, exactly as Install does ----
                var service = new MaterialCreatorService(settings);
                bool created = await service.CreateMaterial(sourceId, pack, null, CancellationToken.None, variantId);
                Check(created, "material: CreateMaterial completed for the installed pack");
                if (!created) { Report(); return; }

                // ---- 3. Read the loaded material back, with NO reload in between ----
                // Everything below is an engine call, so it runs on the editor's main thread. The path is the
                // documented destination of a generated material: the pack root, named "M_<packId>.flax".
                var materialFull = Path.Combine(
                    settings.ContentDestinationFullPath,
                    AssetInstallerService.Sanitize(sourceId),
                    AssetInstallerService.Sanitize(pack.Id),
                    "M_" + pack.Id + ".flax");

                Guid materialId = Guid.Empty;
                bool registered = false, isMaterial = false, isSurface = false;
                string[] parameterNames = null;
                MaterialUsageFlags usage = MaterialUsageFlags.None;

                await MainThreadDispatcher.RunOnMain(() =>
                {
                    registered = Content.GetAssetInfo(materialFull, out AssetInfo info);
                    if (!registered)
                        return;
                    materialId = info.ID;

                    // Content.Load hands back the loaded asset - the object the renderer binds. Asking for a
                    // reload here would hide the very defect this check exists for.
                    var material = Content.Load<Material>(materialId);
                    if (!material)
                        return;
                    isMaterial = true;
                    isSurface = material.IsSurface;
                    parameterNames = material.Parameters.Select(p => p.Name).ToArray();
                    usage = material.Info.UsageFlags;
                });

                Check(registered && materialId != Guid.Empty, $"material: '{Path.GetFileName(materialFull)}' is registered in the content database");
                Check(isMaterial, "material: the created asset loads as a Material");
                if (!isMaterial) { Report(); return; }

                Check(isSurface, "material: it is a surface material (usable on models)");

                // The public parameters of the built graph, visible on the loaded asset:
                // "EmissiveColor" is always exposed, and "UV Scale" / "UV Tiling" drive every
                // Sample Texture node, which a graph built from material textures always has.
                var names = parameterNames ?? Array.Empty<string>();
                Check(names.Length > 0, $"material: the loaded material exposes its parameters ({string.Join("|", names)})");
                Check(names.Contains("EmissiveColor"), "material: the loaded material exposes the EmissiveColor parameter");
                Check(names.Contains("UV Scale") && names.Contains("UV Tiling"), "material: the loaded material exposes the UV transform parameters");

                // The usage flags the material generator derives from the graph. The graph always feeds the
                // Emissive input (a texture times the multiplier, or the EmissiveColor parameter on its own).
                Check((usage & MaterialUsageFlags.UseEmissive) != 0, $"material: the loaded material's usage flags come from the saved graph ({usage})");

                var hasNormal = textures.Any(f => MaterialRoleDetector.Detect(f.Path) == MaterialRole.Normal);
                if (hasNormal)
                    Check((usage & MaterialUsageFlags.UseNormal) != 0, "material: the loaded material's normal map is part of the loaded shader");

                // ---- 4. The usage path: the pack's models render with this material ----
                // The service assigns the material to every slot of the pack's imported models. Reading the
                // slots back is how a user "uses" the material, so the check ends where the report starts.
                var packRoot = Path.Combine(
                    settings.ContentDestinationFullPath,
                    AssetInstallerService.Sanitize(sourceId),
                    AssetInstallerService.Sanitize(pack.Id));
                var modelFiles = (pack.GetEffectiveFiles(variantId) ?? new List<FileEntry>())
                    .Where(f => f.Type == FileAssetType.Model)
                    .Select(f => Path.Combine(packRoot, Path.GetFileNameWithoutExtension(f.Path) + ".flax"))
                    .Where(File.Exists)
                    .ToList();

                int slotsChecked = 0, slotsOnMaterial = 0;
                await MainThreadDispatcher.RunOnMain(() =>
                {
                    foreach (var modelFull in modelFiles)
                    {
                        var model = Content.Load<Model>(modelFull);
                        if (!model)
                            continue;
                        for (int i = 0; i < model.MaterialSlotsCount; i++)
                        {
                            var slot = model.MaterialSlots[i];
                            if (slot == null)
                                continue;
                            slotsChecked++;
                            if (slot.Material != null && slot.Material.ID == materialId)
                                slotsOnMaterial++;
                        }
                    }
                });

                if (modelFiles.Count == 0)
                    Check(true, "material: the pack ships no models to assign to (skipped)");
                else
                    Check(slotsChecked > 0 && slotsOnMaterial == slotsChecked,
                        $"material: all {slotsChecked} model slot(s) of {modelFiles.Count} model(s) render with it");

                // ---- 5. A saved graph reaches the loaded material (the black-material regression) ----
                // MaterialGraphBuilder.MaterialSurfaceOwner.SurfaceData writes the asset, but the engine rebuilds
                // the loaded material - its shader, its parameter table, its usage flags - during ASSET LOAD only.
                // Without the reload that setter performs, the loaded material (the object the renderer binds)
                // keeps the graph it was loaded with: the pure black material of the report, fixed by a hand
                // reload in the content browser.
                //
                // The probe rebuilds this pack's material through the plugin's own build path with the public
                // multiplier parameters switched OFF, so the graph on disk has no "Color" and no "NormalStrength"
                // parameter, and requires the LOADED material to stop exposing them. Pre-fix the loaded asset
                // keeps reporting them; post-fix they are gone the moment the graph is saved.
                await MainThreadDispatcher.RunOnMain(() =>
                {
                    var probeMaterial = Content.Load<Material>(materialId);
                    if (!probeMaterial)
                    {
                        Check(false, "material: the probe could not load the created material");
                        return;
                    }

                    var beforeNames = ParameterNames(probeMaterial);
                    Check(beforeNames.Contains("Color") && beforeNames.Contains("NormalStrength"),
                        $"material: the pack's material exposed its multiplier parameters before the probe ({string.Join("|", beforeNames)})");

                    var saved = MaterialGraphBuilder.TryCreate(
                        materialFull, ProbeSlots(packRoot, textures), packRoot, out Guid _,
                        exposeColorParameter: false, exposeEmissiveColorParameter: false, exposeNormalStrengthParameter: false);
                    Check(saved, "material: the probe graph was saved through the plugin's build path");

                    var probeNames = ParameterNames(probeMaterial);
                    Check(!probeNames.Contains("Color") && !probeNames.Contains("NormalStrength"),
                        $"material: parameters removed from the saved graph are gone from the loaded material ({string.Join("|", probeNames)})");
                });

                // Restoring the pack's real graph has to reach the loaded material the same way.
                var restoredOk = await service.CreateMaterial(sourceId, pack, null, CancellationToken.None, variantId);
                var restoredNames = Array.Empty<string>();
                await MainThreadDispatcher.RunOnMain(() => restoredNames = ParameterNames(Content.Load<Material>(materialId)));
                Check(restoredOk && restoredNames.Contains("Color") && restoredNames.Contains("NormalStrength"),
                    $"material: the restored pack graph is what the loaded material shows ({string.Join("|", restoredNames)})");
            }
            catch (Exception e)
            {
                Debug.LogError($"[E2E FAIL] material readiness: unhandled: {e}");
                failed++;
            }

            Report();
        }

        static List<FileEntry> Textures(List<FileEntry> files)
        {
            return files == null
                ? new List<FileEntry>()
                : files.Where(f => f.Type == FileAssetType.MaterialTextures).ToList();
        }

        /// <summary>
        /// The variant the window would install this pack as: the persisted selection, falling back to the
        /// pack's default (null), which is what the install record stores for an unselected pack.
        /// </summary>
        static string SelectedVariant(SettingsService settings, string sourceId, string packId)
        {
            var key = sourceId + "/" + packId;
            var selected = settings.Settings.SelectedVariants;
            if (selected != null && selected.TryGetValue(key, out var variant) && !string.IsNullOrEmpty(variant))
                return variant;
            return null;
        }

        /// <summary>
        /// The public parameter names of a loaded material (empty when it is not a loaded material). They come
        /// from the loaded asset, so a material saved without a reload reports the parameters of the graph it
        /// was LOADED with rather than the ones written to disk - which is what the probe above reads.
        /// </summary>
        static string[] ParameterNames(Material material)
        {
            if (material == null)
                return Array.Empty<string>();
            var parameters = material.Parameters;
            if (parameters == null)
                return Array.Empty<string>();
            return parameters.Select(p => p.Name).ToArray();
        }

        /// <summary>
        /// Resolves the pack's installed material textures into the PBR inputs of a graph, the way
        /// MaterialCreatorService does, so the probe saves the same texture wiring with the multiplier
        /// parameters switched off. Loads assets, so it runs on the editor's main thread.
        /// </summary>
        static PbrTextureSlots ProbeSlots(string packRoot, List<FileEntry> textureFiles)
        {
            var slots = new PbrTextureSlots();
            foreach (var file in textureFiles)
            {
                var assetPath = Path.Combine(packRoot, Path.GetFileNameWithoutExtension(file.Path) + ".flax");
                if (!File.Exists(assetPath))
                    continue;
                var texture = Content.Load<Texture>(assetPath);
                if (!texture)
                    continue;

                switch (MaterialRoleDetector.Detect(file.Path))
                {
                case MaterialRole.BaseColor: slots.BaseColor = texture.ID; break;
                case MaterialRole.Normal: slots.Normal = texture.ID; break;
                case MaterialRole.Roughness: slots.Roughness = texture.ID; break;
                case MaterialRole.Metallic: slots.Metallic = texture.ID; break;
                case MaterialRole.Emissive: slots.Emissive = texture.ID; break;
                case MaterialRole.Opacity: slots.Opacity = texture.ID; break;
                case MaterialRole.AmbientOcclusion: slots.AmbientOcclusion = texture.ID; break;
                case MaterialRole.Height: slots.Height = texture.ID; break;
                }
            }
            return slots;
        }
    }
}
