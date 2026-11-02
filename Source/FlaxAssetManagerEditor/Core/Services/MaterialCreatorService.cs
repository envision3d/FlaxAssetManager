using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlaxAssetManager.Core.Models;
using FlaxAssetManager.Core.Utils;
using FlaxEditor;
using FlaxEditor.Content.Import;
using FlaxEditor.Surface;
using FlaxEditor.Scripting;
using FlaxEngine;
using FlaxEngine.Tools;

namespace FlaxAssetManager.Core.Services
{
    /// <summary>
    /// Creates PBR materials from installed <see cref="FileAssetType.MaterialTextures"/> files.
    ///
    /// For each selected pack the role of every material texture is detected from its file name
    /// (see <see cref="MaterialRoleDetector"/>); the detected textures are then wired into a newly
    /// created material's PBR surface in one operation by <see cref="MaterialGraphBuilder"/>, which
    /// creates the asset and builds the graph with stock, public editor APIs (a bare default
    /// material has no exposed parameters to set, so a graph is the only way to assign textures).
    /// multiplier is exposed as a public "Color" material parameter instead of a hardcoded white
    /// constant. The emissive color multiplier is always exposed as a public "EmissiveColor"
    /// parameter (default black = no emission), so any material can gain emission — it modulates
    /// the emissive map when present, or drives the Emissive input directly when the pack ships
    /// no emissive texture.
    ///
    /// The material graph is then post-processed so that no texture node feeds its destination
    /// directly: every texture is routed through a <c>Sample Texture</c> node, and a combined ARM
    /// map (R = AmbientOcclusion, G = Roughness, B = Metallic) is sampled once and routed through a
    /// single <c>Unpack Float4</c> node (X -> Ambient Occlusion, Y -> Roughness, Z -> Metallic).
    /// A <c>Normal Map</c> node is never routed through a <c>Sample Texture</c> node: it holds its
    /// texture as an asset value and samples + decodes it internally (the HLSL the engine generates for
    /// it is <c>UnpackNormalMap(rg)</c>), so it has no texture input box to feed. A normal map therefore
    /// keeps the chain <see cref="MaterialGraphBuilder"/> built for it - <c>Normal Map -> Unpack Float4
    /// -> Pack Float2 -> x NormalStrength -> Derive Normal Z -> Normal input</c> - and this pass only
    /// feeds the <c>Normal Map</c> node's UVs input with the shared UV transform.
    ///
    /// Two public Float2 parameters, "UV Scale" (default (1,1)) and "UV Tiling" (default (0,0)),
    /// are exposed and fed to every sampler's UVs input (every <c>Sample Texture</c> node and every
    /// <c>Normal Map</c> node) as <c>UV * UV Scale + UV Tiling</c>, so all texture sampling can be
    /// scaled and offset at once.
    ///
    /// Every surface graph write is followed by an asset <c>Reload</c> + <c>WaitForLoaded</c>, performed by
    /// <c>MaterialGraphBuilder.MaterialSurfaceOwner.SurfaceData</c>: <c>Material.SaveSurface</c> only writes
    /// the asset file, so without that reload a material this service has just built keeps the shader of the
    /// graph that was on disk before the write — the empty default one — and renders pure black until the
    /// user reloads the asset by hand in the content browser.
    ///
    /// The service is defensive: it always creates the material asset, wires as many textures as
    /// it can match by name, and logs every step so a mismatch can be diagnosed in the editor.
    ///
    /// Every engine call in this service is marshalled to the editor's main thread (see
    /// <see cref="RunOnMain"/>): the content database, the import pipeline, asset load/save and the
    /// Visject material surface are all main-thread APIs, while the awaits below resume on a
    /// thread-pool thread.
    /// </summary>
    public class MaterialCreatorService
    {

        private readonly SettingsService _settings;

        public MaterialCreatorService(SettingsService settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Creates (or overwrites) a single PBR material at
        /// <c>Content/{sourceId}/{packId}/M_{packId}.flax</c> and wires each detected material
        /// texture into the matching PBR surface input. The pack must already be installed on disk.
        /// </summary>
        /// <param name="sourceId">The source id the pack was fetched from.</param>
        /// <param name="pack">The installed pack.</param>
        /// <param name="progress">Optional progress reporter.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="variantId">Optional variant id; null uses the default file set.</param>
        public async Task<bool> CreateMaterial(string sourceId, PackEntry pack, IProgress<(float, string)> progress = null, CancellationToken ct = default, string variantId = null)
        {
            var files = pack.GetEffectiveFiles(variantId);

            // 0. Import the installed texture files into the editor's content database. Files
            //    written straight to disk are not resolvable assets until the import pipeline has
            //    processed them; without this step every ResolveTextureId call below fails and
            //    no texture ever gets wired to the material.
            await EnsureTexturesImportedAsync(sourceId, pack, files, progress, ct, variantId);

            // 1. Detect the PBR role of every installed material texture from its file name.
            var singleRoleTextures = new Dictionary<MaterialRole, Guid>();
            Guid combinedArm = Guid.Empty;
            int textureFiles = 0;
            var detected = new List<(MaterialRole Role, string AssetPath)>();
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (file.Type != FileAssetType.MaterialTextures) continue;
                textureFiles++;
                var role = MaterialRoleDetector.Detect(file.Path);
                if (role == MaterialRole.None) continue;
                detected.Add((role, MakeAssetFullPath(sourceId, pack.Id, file.Path, variantId)));
            }

            // Resolve each detected texture's asset id. Content.GetAssetInfo is an engine API, so
            // the whole lookup runs on the editor's main thread.
            await RunOnMain(() =>
            {
                foreach (var (role, assetPath) in detected)
                {
                    var id = ResolveTextureId(assetPath);
                    if (id == Guid.Empty) continue;
                    if (role == MaterialRole.Combined)
                        combinedArm = id;
                    else if (!singleRoleTextures.ContainsKey(role))
                        singleRoleTextures[role] = id;
                }
            });

            // A combined ARM map is NOT handed to MaterialGraphBuilder: it would spawn
            // a separate texture node per PBR input (AO / Roughness / Metallic), each with its own
            // sampling, which is exactly what we want to avoid. Instead it is wired in the surface
            // post-processing below as a single Texture -> Sample Texture -> Unpack Float4 chain
            // routing X = AmbientOcclusion, Y = Roughness, Z = Metallic.

            if (singleRoleTextures.Count == 0 && combinedArm == Guid.Empty)
            {
                var reason = textureFiles == 0
                    ? $"pack '{pack.Id}' contains no MaterialTextures"
                    : $"none of the {textureFiles} MaterialTexture(s) in pack '{pack.Id}' resolved to an asset (role undetected or import pending)";
                Debug.LogWarning($"FlaxAssetManager: {reason}. Material not created.");
                return false;
            }

            // 2. Create the material asset and wire the detected textures into its PBR surface
            //    graph. MaterialGraphBuilder does this entirely in managed code with public editor
            //    APIs (Editor.CreateAsset + a Visject surface), so the plugin needs no engine
            //    patch. It spawns texture nodes connected to the surface's root boxes - the only
            //    way to assign PBR textures - and exposes the colour multipliers as parameters.
            // Material asset name is prefixed with "M_" so imported materials are visually
            // distinguishable from models/prefabs in the content browser.
            var materialFileName = "M_" + pack.Id + ".flax";
            var materialPath = MakeContentPath(sourceId, pack.Id, materialFileName);
            var materialFull = Path.Combine(_settings.ContentDestinationFullPath, AssetInstallerService.Sanitize(sourceId), AssetInstallerService.Sanitize(pack.Id), materialFileName);
            var packRoot = Path.Combine(_settings.ContentDestinationFullPath, AssetInstallerService.Sanitize(sourceId), AssetInstallerService.Sanitize(pack.Id));

            Guid roleTexture(MaterialRole role) =>
                singleRoleTextures.TryGetValue(role, out var id) ? id : Guid.Empty;

            var slots = new PbrTextureSlots
            {
                BaseColor = roleTexture(MaterialRole.BaseColor),
                Normal = roleTexture(MaterialRole.Normal),
                Roughness = roleTexture(MaterialRole.Roughness),
                Metallic = roleTexture(MaterialRole.Metallic),
                Emissive = roleTexture(MaterialRole.Emissive),
                Opacity = roleTexture(MaterialRole.Opacity),
                AmbientOcclusion = roleTexture(MaterialRole.AmbientOcclusion),
                Height = roleTexture(MaterialRole.Height),
            };

            // The graph is built out of Visject nodes, which are editor GUI controls, so the whole
            // create-and-wire pass runs on the editor's main thread. The builder reuses the id of
            // an existing asset at that path, so no manual delete is needed for a rebuild.
            Guid materialId = Guid.Empty;
            bool created = false;
            await RunOnMain(() => created = MaterialGraphBuilder.TryCreate(materialFull, slots, packRoot, out materialId));
            if (!created)
            {
                Debug.LogError($"FlaxAssetManager: failed to create material asset at '{materialFull}'.");
                return false;
            }

            // 3. Post-process the material graph so no texture node feeds its destination
            //    directly: Texture -> Sample Texture -> destination, and combined ARM textures
            //    -> a single Unpack Float4 routing X/Y/Z to AO / Roughness / Metallic. Spawns
            //    Visject nodes, so it runs on the main thread.
            bool sampled = false;
            await RunOnMain(() => sampled = InsertSamplingNodes(materialId, combinedArm));
            if (!sampled)
                return false;

            // 4. Assign the material to every material slot of the pack's models so the models
            //    render with it: the engine's model import skips embedded materials (they can
            //    carry non-finite values that break shader compilation), leaving the slots empty.
            //    Loads and saves assets, so it runs on the main thread.
            await RunOnMain(() => AssignMaterialToModels(materialId, sourceId, pack, variantId));

            var textureCount = singleRoleTextures.Values.Distinct().Count() + (combinedArm != Guid.Empty ? 1 : 0);
            progress?.Report((1.0f, $"Wired {textureCount} texture(s)"));

            // Record the generated material on the install entry. Uninstall deletes downloaded files
            // from its own list and never sees this asset, so without this line a "removed" pack keeps
            // M_<packId>.flax in the Content tree.
            RecordGeneratedAsset(sourceId, pack.Id, materialPath);

            Debug.Log($"FlaxAssetManager: created material '{materialPath}' wired to {textureCount} texture(s) from pack '{pack.Id}'.");
            return true;
        }

        /// <summary>
        /// Appends a produced asset (project-content-relative path) to the pack's install record.
        /// Silent when the pack has no record (a material built for a pack that was removed mid-run).
        /// </summary>
        private void RecordGeneratedAsset(string sourceId, string packId, string projectRelativePath)
        {
            var entry = _settings.FindInstalled(sourceId, packId);
            if (entry == null)
                return;
            if (entry.GeneratedAssets == null)
                entry.GeneratedAssets = new List<string>();
            if (!entry.GeneratedAssets.Contains(projectRelativePath, StringComparer.OrdinalIgnoreCase))
                entry.GeneratedAssets.Add(projectRelativePath);
            _settings.SaveInstalled();
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the editor's main thread and awaits its completion.
        ///
        /// Flax installs no <c>SynchronizationContext</c>, so an <c>await</c> in this service
        /// resumes on a thread-pool thread, while the APIs the pipeline calls - the content
        /// database, the import pipeline, asset load/save and the Visject material surface (its
        /// nodes are editor GUI controls) - are main-thread only. Routing them through here keeps
        /// the material pipeline off the pool thread it runs on.
        /// Kept as a local alias so the pipeline reads as one marshalling convention; the hop itself is
        /// <see cref="MainThreadDispatcher.RunOnMain"/>, shared with the window.
        /// </summary>
        private static Task RunOnMain(Action action) => MainThreadDispatcher.RunOnMain(action);

        /// <summary>
        /// Assigns the created material to every material slot of the pack's imported models and
        /// saves the changed models, so the models render with the pack's PBR material. The engine
        /// import of the model sources skips embedded materials (an OpenFBX quirk can leave their
        /// Emissive value non-finite, which makes the generated shader fail to compile), so the
        /// slots arrive empty and need this.
        /// </summary>
        private void AssignMaterialToModels(Guid materialId, string sourceId, PackEntry pack, string variantId)
        {
            var material = Content.Load<Material>(materialId);
            if (!material)
                return;
            var packRoot = Path.Combine(_settings.ContentDestinationFullPath, AssetInstallerService.Sanitize(sourceId), AssetInstallerService.Sanitize(pack.Id));
            foreach (var file in pack.GetEffectiveFiles(variantId))
            {
                if (file.Type != FileAssetType.Model) continue;
                // The import names the model asset after the source base name, at the pack root.
                var modelFull = Path.Combine(packRoot, Path.GetFileNameWithoutExtension(file.Path) + ".flax");
                if (!File.Exists(modelFull)) continue;
                var model = Content.Load<Model>(modelFull);
                if (!model)
                {
                    Debug.LogWarning($"FlaxAssetManager: failed to load model '{modelFull}' to assign the material.");
                    continue;
                }
                int assigned = 0;
                for (int i = 0; i < model.MaterialSlotsCount; i++)
                {
                    var slot = model.MaterialSlots[i];
                    if (slot == null) continue;
                    if (slot.Material == null || slot.Material.ID != materialId)
                    {
                        slot.Material = material;
                        assigned++;
                    }
                }
                if (assigned == 0) continue;
                if (model.Save(null))
                    Debug.LogWarning($"FlaxAssetManager: failed to save model '{modelFull}' after material assignment.");
                else
                    Debug.Log($"FlaxAssetManager: assigned material to {assigned} material slot(s) of model '{Path.GetFileName(modelFull)}'.");
            }
        }

        /// <summary>
        /// Visject surface node type ids (group &lt;&lt; 16 | type) used for the graph post-processing.
        /// </summary>
        private const uint MaterialRootNodeType = (1u << 16) | 1;
        private const uint TextureNodeType = (5u << 16) | 1;
        private const ushort TextureGroup = 5;
        private const ushort TextureType = 1;
        private const ushort SampleTextureGroup = 5;
        private const ushort SampleTextureType = 9;
        private const ushort UnpackFloat4Group = 4;
        private const ushort UnpackFloat4Type = 32;

        // Normal Map node (group 5, type 4): holds the normal texture as an asset value (Values[0])
        // and samples + decodes the encoded tangent-space vector itself, so a normal map needs no
        // Sample Texture node. Input box 0 = UVs (the shared UV transform feeds it); output box 1 =
        // Vector (the decoded Float3 normal), which the graph builder's decode chain consumes.
        private const uint NormalMapNodeType = (5u << 16) | 4;
        private const int NormalMapBoxUVs = 0;

        // Material root node input boxes a combined ARM texture may feed.
        private const int RootBoxMetalness = 4;
        private const int RootBoxRoughness = 6;
        private const int RootBoxAmbientOcclusion = 7;

        // Texture node output boxes: 1 = Color, 2 = R, 3 = G, 4 = B, 5 = A, 6 = object reference.
        private const int TextureBoxColor = 1;
        private const int TextureBoxA = 5;
        private const int TextureBoxObject = 6;

        // Sample Texture node boxes: 0 = Texture (object in), 4 = Color (float4 out).
        private const int SampleBoxTextureInput = 0;
        private const int SampleBoxColor = 4;

        // Sample Texture node default values: { Sampler, Level, Texture Group }. The sampler is
        // CommonSamplerType.LinearWrap (2) so tiled UVs wrap instead of clamping; Level -1 and
        // Texture Group 0 are the node defaults.
        private static readonly object[] SampleTextureValues = { 2, -1.0f, 0 };

        // Unpack Float4 node boxes: 0 = Value (in), 1 = X, 2 = Y, 3 = Z.
        private const int UnpackBoxValue = 0;
        private const int UnpackBoxX = 1;
        private const int UnpackBoxY = 2;
        private const int UnpackBoxZ = 3;

        // Texcoords node (group 5, type 2): base UV source, box 0 = UVs (Float2 out).
        private const ushort TexcoordsGroup = 5;
        private const ushort TexcoordsType = 2;
        private const int TexcoordsBoxUVs = 0;

        // Get Parameter node (group 6, type 1): box 0 = Value (matches the parameter type;
        // Float2 for the UV parameters). Boxes 1 and 2 expose the scalar X and Y components.
        private const ushort GetParamGroup = 6;
        private const ushort GetParamType = 1;
        private const int ParamBoxValue = 0;

        // Math group (3) Add (type 1) and Multiply (type 3) share the Op2 layout:
        // 0 = A (in), 1 = B (in), 2 = Result (out).
        private const ushort MathGroup = 3;
        private const ushort AddType = 1;
        private const ushort MultiplyType = 3;
        private const int MathBoxA = 0;
        private const int MathBoxB = 1;
        private const int MathBoxResult = 2;

        // Sample Texture node UVs input.
        private const int SampleBoxUVs = 1;


        /// <summary>
        /// Post-processes a freshly created material's surface graph so that no <c>Texture</c>
        /// node feeds its destination directly: every texture is routed through a <c>Sample
        /// Texture</c> node, and a combined ARM texture is routed through a single <c>Texture</c>
        /// + <c>Sample Texture</c> + <c>Unpack Float4</c> chain (X -> Ambient Occlusion,
        /// Y -> Roughness, Z -> Metallic). A <c>Normal Map</c> node is left exactly as the graph builder
        /// wired it: it holds its texture as an asset value and samples + decodes it internally, with no
        /// texture input box to feed, so it takes no <c>Sample Texture</c> node - the shared UV transform
        /// on its own UVs input is all this pass has to do for a normal map.
        /// </summary>
        /// <param name="materialId">The id of the material asset to rewire.</param>
        /// <param name="combinedArm">The id of the combined ARM texture to wire (if any), routed through
        /// one <c>Texture</c> + <c>Sample Texture</c> + <c>Unpack Float4</c> chain.</param>
        /// <returns>True if the surface graph was rewired and saved.</returns>
        private bool InsertSamplingNodes(Guid materialId, Guid combinedArm)
        {
            var material = Content.Load<Material>(materialId);
            if (!material)
            {
                Debug.LogError($"FlaxAssetManager: failed to load material asset {materialId} to rewire its texture sampling.");
                return false;
            }

            var surface = new MaterialSurface(new MaterialGraphBuilder.MaterialSurfaceOwner(material));
            try
            {
                // Load() returns true on failure.
                if (surface.Load())
                {
                    Debug.LogError($"FlaxAssetManager: failed to load the surface graph of material '{material}'.");
                    return false;
                }

                var context = surface.Context;

                // Locate the material root node.
                SurfaceNode root = null;
                foreach (var node in context.Nodes)
                {
                    if (node.Type == MaterialRootNodeType)
                    {
                        root = node;
                        break;
                    }
                }
                if (root == null)
                {
                    Debug.LogError($"FlaxAssetManager: material '{material}' has no material root node; cannot rewire its texture sampling.");
                    return false;
                }

                // Collect the texture nodes first: the rewiring below spawns new nodes, which
                // mutates context.Nodes.
                var textureNodes = new List<SurfaceNode>();
                foreach (var node in context.Nodes)
                {
                    if (node.Type == TextureNodeType)
                        textureNodes.Add(node);
                }

                // Collect the Normal Map nodes: a Normal Map node holds its texture and samples +
                // decodes it internally, so the normal chain the graph builder wired is left intact and
                // only the node's UVs input joins the shared UV transform below.
                var normalMapNodes = new List<SurfaceNode>();
                foreach (var node in context.Nodes)
                {
                    if (node.Type == NormalMapNodeType)
                        normalMapNodes.Add(node);
                }

                // Every Sample Texture node we create, so the UV transform below can be applied
                // to each one (single-role textures plus the combined-ARM sampler).
                var sampleNodes = new List<SurfaceNode>();

                // Route each texture's Color/channel outputs through a Sample Texture node.
                foreach (var texture in textureNodes)
                {
                    // Consumers of the texture's Color/channel outputs (boxes 1..5), deduped.
                    var consumers = new List<FlaxEditor.Surface.Elements.Box>();
                    for (int boxId = TextureBoxColor; boxId <= TextureBoxA; boxId++)
                    {
                        var box = texture.GetBox(boxId);
                        if (box == null)
                            continue;
                        foreach (var connection in box.Connections)
                        {
                            if (!consumers.Contains(connection))
                                consumers.Add(connection);
                        }
                    }
                    if (consumers.Count == 0)
                        continue; // Unconnected texture node; nothing to rewire.

                    // Spawn a Sample Texture node beside the texture and feed it the texture's
                    // object reference (the Texture node keeps its asset value).
                    var sample = context.SpawnNode(SampleTextureGroup, SampleTextureType, texture.Location + new Float2(240.0f, 0.0f), SampleTextureValues);
                    if (sample == null)
                    {
                        Debug.LogError($"FlaxAssetManager: failed to spawn a Sample Texture node in material '{material}'.");
                        return false;
                    }
                    texture.GetBox(TextureBoxObject).CreateConnection(sample.GetBox(SampleBoxTextureInput));

                    sampleNodes.Add(sample);

                    // Route every consumer from the sample's Color output; connecting to a
                    // single-connection consumer (e.g. a root input) drops the old link from the
                    // texture node.
                    foreach (var consumer in consumers)
                        sample.GetBox(SampleBoxColor).CreateConnection(consumer);

                    // Drop any leftover links from the texture's Color/channel outputs (box 6,
                    // the object reference, now feeds the Sample Texture node).
                    for (int boxId = TextureBoxColor; boxId <= TextureBoxA; boxId++)
                    {
                        var box = texture.GetBox(boxId);
                        if (box != null)
                            box.RemoveConnections();
                    }
                }

                // Wire the combined ARM texture as a single Texture -> Sample Texture ->
                // Unpack Float4 chain, routing X -> Ambient Occlusion, Y -> Roughness,
                // Z -> Metallic. Only root inputs that are not already fed by a distinct
                // texture are connected, so packs carrying an individual channel alongside
                // the ARM map are handled correctly.
                if (combinedArm != Guid.Empty)
                {
                    var location = new Float2(-400.0f, -400.0f);
                    var armTexture = context.SpawnNode(TextureGroup, TextureType, location, new object[] { combinedArm });
                    if (armTexture == null)
                    {
                        Debug.LogError($"FlaxAssetManager: failed to spawn the combined-ARM texture node in material '{material}'.");
                        return false;
                    }
                    var armSample = context.SpawnNode(SampleTextureGroup, SampleTextureType, location + new Float2(240.0f, 0.0f), SampleTextureValues);
                    if (armSample == null)
                    {
                        Debug.LogError($"FlaxAssetManager: failed to spawn the combined-ARM Sample Texture node in material '{material}'.");
                        return false;
                    }
                    var armUnpack = context.SpawnNode(UnpackFloat4Group, UnpackFloat4Type, location + new Float2(480.0f, 0.0f));
                    if (armUnpack == null)
                    {
                        Debug.LogError($"FlaxAssetManager: failed to spawn the combined-ARM Unpack Float4 node in material '{material}'.");
                        return false;
                    }
                    armTexture.GetBox(TextureBoxObject).CreateConnection(armSample.GetBox(SampleBoxTextureInput));
                    armSample.GetBox(SampleBoxColor).CreateConnection(armUnpack.GetBox(UnpackBoxValue));

                    sampleNodes.Add(armSample);

                    WireArmChannel(armUnpack, root, UnpackBoxX, RootBoxAmbientOcclusion);
                    WireArmChannel(armUnpack, root, UnpackBoxY, RootBoxRoughness);
                    WireArmChannel(armUnpack, root, UnpackBoxZ, RootBoxMetalness);
                }

                // Expose "UV Scale" and "UV Tiling" as public Float2 material parameters and feed every
                // sampler's UVs input with (UV * UV Scale + UV Tiling), so all texture sampling can be
                // scaled and offset at once. The Sample Texture nodes created above are the consumers for
                // the rewired texture nodes, and a Normal Map node - which samples its own texture, so it
                // has no Sample Texture node - is the consumer for a normal map, taking the transform on
                // its own UVs input.
                var uvConsumers = new List<(SurfaceNode Node, int UvBox)>();
                foreach (var sample in sampleNodes)
                    uvConsumers.Add((sample, SampleBoxUVs));
                foreach (var normalMap in normalMapNodes)
                    uvConsumers.Add((normalMap, NormalMapBoxUVs));

                if (!WireUvTransforms(context, uvConsumers))
                    return false;

                // Save() returns true on failure; the surface owner's setter throws when the asset write
                // fails, and reloads the asset after saving it so the in-memory material is the graph that
                // was just written (SaveSurface alone leaves the previous shader in place - see
                // MaterialGraphBuilder.MaterialSurfaceOwner.SurfaceData). All of it must be reported as a
                // clean failure here: the material asset already exists on disk, and an unhandled exception
                // would escape into the UI with the graph half-rewired.
                try
                {
                    if (surface.Save())
                    {
                        Debug.LogError($"FlaxAssetManager: failed to save the rewired surface graph of material '{material}'.");
                        return false;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"FlaxAssetManager: failed to save the rewired surface graph of material '{material}': {e.Message}");
                    return false;
                }

                return true;
            }
            finally
            {
                surface.Dispose();
            }
        }

        /// <summary>
        /// Connects a combined-ARM Unpack Float4 output to a material root input, but only when
        /// that input is not already fed by another node.
        /// </summary>
        private static void WireArmChannel(SurfaceNode unpack, SurfaceNode root, int unpackBoxId, int rootBoxId)
        {
            var rootBox = root.GetBox(rootBoxId);
            var unpackBox = unpack.GetBox(unpackBoxId);
            if (rootBox == null || unpackBox == null || rootBox.Connections.Count > 0)
                return;
            unpackBox.CreateConnection(rootBox);
        }

        /// <summary>
        /// Exposes public Float2 material parameters "UV Scale" and "UV Tiling" and feeds every sampler's
        /// UVs input - every <c>Sample Texture</c> node and every <c>Normal Map</c> node - with
        /// <c>UV * UV Scale + UV Tiling</c>. A shared <c>Texcoords</c> node supplies the base UV and
        /// two <c>Get Parameter</c> nodes supply the parameters; per-consumer <c>Multiply</c> +
        /// <c>Add</c> nodes produce the transformed UV.
        /// </summary>
        private bool WireUvTransforms(VisjectSurfaceContext context, List<(SurfaceNode Node, int UvBox)> uvConsumers)
        {
            if (uvConsumers.Count == 0)
                return true;

            // Shared base-UV source.
            var uvSource = context.SpawnNode(TexcoordsGroup, TexcoordsType, new Float2(-760.0f, 560.0f));
            if (uvSource == null)
            {
                Debug.LogError("FlaxAssetManager: failed to spawn the Texcoords (base UV) node in the material surface.");
                return false;
            }

            // Public Float2 parameters and the Get Parameter nodes that read them.
            var scaleParam = EnsureFloat2Parameter(context, "UV Scale", new Float2(1.0f, 1.0f));
            var tilingParam = EnsureFloat2Parameter(context, "UV Tiling", new Float2(0.0f, 0.0f));
            var getScale = context.SpawnNode(GetParamGroup, GetParamType, new Float2(-560.0f, 600.0f), new object[] { scaleParam.ID });
            if (getScale == null)
            {
                Debug.LogError("FlaxAssetManager: failed to spawn the UV Scale Get Parameter node in the material surface.");
                return false;
            }
            var getTiling = context.SpawnNode(GetParamGroup, GetParamType, new Float2(-560.0f, 680.0f), new object[] { tilingParam.ID });
            if (getTiling == null)
            {
                Debug.LogError("FlaxAssetManager: failed to spawn the UV Tiling Get Parameter node in the material surface.");
                return false;
            }

            // Per consumer: (UV * UV Scale + UV Tiling) -> the node's UVs input.
            foreach (var (node, uvBox) in uvConsumers)
            {
                var multiply = context.SpawnNode(MathGroup, MultiplyType, node.Location + new Float2(-40.0f, -340.0f));
                if (multiply == null)
                {
                    Debug.LogError("FlaxAssetManager: failed to spawn a UV Multiply node in the material surface.");
                    return false;
                }
                var add = context.SpawnNode(MathGroup, AddType, node.Location + new Float2(200.0f, -340.0f));
                if (add == null)
                {
                    Debug.LogError("FlaxAssetManager: failed to spawn a UV Add node in the material surface.");
                    return false;
                }

                uvSource.GetBox(TexcoordsBoxUVs).CreateConnection(multiply.GetBox(MathBoxA));
                getScale.GetBox(ParamBoxValue).CreateConnection(multiply.GetBox(MathBoxB));
                multiply.GetBox(MathBoxResult).CreateConnection(add.GetBox(MathBoxA));
                getTiling.GetBox(ParamBoxValue).CreateConnection(add.GetBox(MathBoxB));
                add.GetBox(MathBoxResult).CreateConnection(node.GetBox(uvBox));
            }

            return true;
        }

        /// <summary>
        /// Returns an existing Float2 material parameter by name, or creates and registers a new
        /// public one with the given default value.
        /// </summary>
        private SurfaceParameter EnsureFloat2Parameter(VisjectSurfaceContext context, string name, Float2 defaultValue)
        {
            var existing = context.GetParameter(name);
            if (existing != null)
                return existing;
            var param = new SurfaceParameter
            {
                Type = new ScriptType(typeof(Float2)),
                ID = Guid.NewGuid(),
                Name = name,
                IsPublic = true,
                Value = defaultValue,
            };
            context.Parameters.Add(param);
            return param;
        }

        // The headless IVisjectSurfaceOwner this file uses to edit a material's surface lives in
        // MaterialGraphBuilder.MaterialSurfaceOwner (shared with the graph builder).

        /// <summary>
        /// Imports the pack's installed <see cref="FileAssetType.MaterialTextures"/> files into the
        /// editor's content database and waits (cooperatively, without blocking the main thread)
        /// until each one is resolvable as an asset. Files are written straight to disk by the
        /// installer, so they only become loadable assets once the editor's import pipeline has
        /// processed them; running it here is what lets <see cref="ResolveTextureId"/> succeed.
        /// The set this install wrote lives under the selected variant's subfolder of <c>files/</c>,
        /// so <paramref name="variantId"/> decides which files are found and imported.
        /// </summary>
        private async Task EnsureTexturesImportedAsync(string sourceId, PackEntry pack, List<FileEntry> files, IProgress<(float, string)> progress, CancellationToken ct, string variantId)
        {
            // Group each material texture by the content folder its source file lives in. The
            // editor's import writes the resulting .flax asset into the target folder it is
            // handed, so files in a subfolder must be imported into that subfolder to keep the
            // registered asset path matching MakeAssetFullPath.
            var byFolder = new Dictionary<string, (List<string> FullPaths, List<string> AssetPaths, List<TextureImportSettings> Settings)>();
            foreach (var file in files)
            {
                if (file.Type != FileAssetType.MaterialTextures) continue;
                var full = Path.Combine(_settings.ContentDestinationFullPath, AssetInstallerService.Sanitize(sourceId), AssetInstallerService.Sanitize(pack.Id), AssetInstallerService.FilesSubFolder, AssetInstallerService.VariantSubFolder(variantId), file.Path);
                if (!File.Exists(full)) continue;
                var relFolder = MakeFilesContentPath(sourceId, pack.Id, Path.GetDirectoryName(file.Path), variantId);
                if (!byFolder.TryGetValue(relFolder, out var entry))
                {
                    entry = (new List<string>(), new List<string>(), new List<TextureImportSettings>());
                    byFolder[relFolder] = entry;
                }
                entry.FullPaths.Add(full);
                entry.AssetPaths.Add(MakeAssetFullPath(sourceId, pack.Id, file.Path, variantId));
                entry.Settings.Add(MakeTextureImportSettings(MaterialRoleDetector.Detect(file.Path), file.Path));
            }
            if (byFolder.Count == 0) return;

            progress?.Report((0.05f, "Importing textures…"));

            // Make the content tree aware of the freshly written files/folders so each target
            // folder exists as a ContentFolder we can import into. The content database is an engine
            // API, so this runs on the editor's main thread.
            bool refreshed = false;
            await RunOnMain(() => refreshed = ContentDatabaseUtils.RefreshContentFolder(_settings.ContentDestinationFullPath));
            if (!refreshed) return;

            // Hand each group to the editor's import pipeline (no settings dialog), file by file
            // so every texture can carry its own import settings: the texture "Type" is set from
            // its PBR role (normal maps -> Normal Map, displacement -> Grayscale, base color ->
            // RGB for JPG / RGBA for PNG). The actual import runs on the editor's import worker
            // thread and registers each texture with a persistent asset id.
            var assetPaths = new List<string>();
            await RunOnMain(() =>
            {
                foreach (var kv in byFolder)
                {
                    var folder = ContentDatabaseUtils.FindContentFolder(kv.Key);
                    if (folder == null)
                    {
                        Debug.LogWarning($"FlaxAssetManager: content folder '{kv.Key}' not found after refresh; textures in it may not import.");
                        continue;
                    }
                    for (int i = 0; i < kv.Value.FullPaths.Count; i++)
                    {
                        Editor.Instance.ContentImporting.Import(kv.Value.FullPaths[i], folder, skipSettingsDialog: true, settings: kv.Value.Settings[i]);
                    }
                    assetPaths.AddRange(kv.Value.AssetPaths);
                }
            });
            if (assetPaths.Count == 0) return;

            // Wait until every texture is registered so GetAssetInfo can return its id.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                bool allReady = true;
                // Content.GetAssetInfo is an engine API, so the readiness check runs on the main
                // thread; the wait itself stays on the pool thread.
                await RunOnMain(() =>
                {
                    foreach (var p in assetPaths)
                    {
                        if (!Content.GetAssetInfo(p, out _)) { allReady = false; break; }
                    }
                });
                if (allReady) return;
                await Task.Delay(100);
            }
            Debug.LogWarning($"FlaxAssetManager: timed out waiting for {assetPaths.Count} texture(s) of pack '{pack.Id}' to register.");
        }

        /// <summary>
        /// Builds the texture import settings for a material texture from its PBR role, so the
        /// imported texture's "Type" matches how the material samples it: normal maps import as a
        /// <c>Normal Map</c>, displacement/height as <c>Grayscale</c>, and base color as <c>RGB</c>
        /// (JPG) or <c>RGBA</c> (PNG, preserving the alpha channel). Returns null when the role
        /// needs no override, in which case the engine's own file-name based guess is used.
        /// </summary>
        private static TextureImportSettings MakeTextureImportSettings(MaterialRole role, string filePath)
        {
            TextureFormatType? type = null;
            switch (role)
            {
                case MaterialRole.Normal:
                    type = TextureFormatType.NormalMap;
                    break;
                case MaterialRole.Height:
                    type = TextureFormatType.GrayScale;
                    break;
                case MaterialRole.BaseColor:
                    var ext = Path.GetExtension(filePath).ToLowerInvariant();
                    if (ext == ".jpg" || ext == ".jpeg")
                        type = TextureFormatType.ColorRGB;
                    else if (ext == ".png")
                        type = TextureFormatType.ColorRGBA;
                    break;
            }
            if (type == null) return null;

            // Start from the engine defaults (compression, mip maps, size limits) and only
            // override the format type: the importer replaces the entry's whole options with the
            // override, so a zero-initialized struct would import uncompressed without mip maps.
            var options = TextureTool.Options.Default;
            options.Type = type.Value;
            if (role == MaterialRole.BaseColor)
                options.sRGB = true; // Color maps are authored in sRGB, like the engine's own guess
            return new TextureImportSettings { Settings = options };
        }


        /// <summary>
        /// Returns the persistent asset id registered at the given (absolute) asset path, or
        /// <see cref="Guid.Empty"/> if the asset is not (yet) known to the content database.
        /// </summary>
        private Guid ResolveTextureId(string assetPath)
        {
            if (Content.GetAssetInfo(assetPath, out AssetInfo info))
                return info.ID;
            Debug.LogWarning($"FlaxAssetManager: texture '{assetPath}' is not registered as an asset; it will be skipped.");
            return Guid.Empty;
        }

        /// <summary>
        /// Builds the content-relative path (e.g. <c>Content/sourceId/packId/path.png</c>) for a
        /// file installed under a pack, mirroring <see cref="AssetInstallerService.Sanitize"/>.
        /// Used for content-database folder lookups.
        /// </summary>
        private string MakeContentPath(string sourceId, string packId, string filePath)
        {
            var full = Path.Combine(_settings.ContentDestinationFullPath, AssetInstallerService.Sanitize(sourceId), AssetInstallerService.Sanitize(packId), filePath);
            return Path.GetRelativePath(Globals.ProjectFolder, full).Replace('\\', '/');
        }

        /// <summary>
        /// Builds the content-relative path (e.g. <c>Content/sourceId/packId/files/2k/path.png</c>) for a
        /// downloaded file installed under a pack's <see cref="AssetInstallerService.FilesSubFolder"/>, inside
        /// the variant subfolder when the install selected a variant. Mirrors
        /// <see cref="AssetInstallerService.Sanitize"/> and <see cref="AssetInstallerService.VariantSubFolder"/>;
        /// used to locate the folder the raw texture source files (and therefore their imported <c>.flax</c>
        /// assets) live in.
        /// </summary>
        private string MakeFilesContentPath(string sourceId, string packId, string filePath, string variantId)
        {
            var full = Path.Combine(_settings.ContentDestinationFullPath, AssetInstallerService.Sanitize(sourceId), AssetInstallerService.Sanitize(packId), AssetInstallerService.FilesSubFolder, AssetInstallerService.VariantSubFolder(variantId), filePath);
            return Path.GetRelativePath(Globals.ProjectFolder, full).Replace('\\', '/');
        }

        /// <summary>
        /// Builds the absolute path of the <c>.flax</c> asset produced by importing a source
        /// image installed under a pack, inside the variant subfolder that install wrote to. The engine
        /// registers an imported texture beside its source (same folder, base name, <c>.flax</c> extension,
        /// preserving any subfolder) under an absolute path, so this exact path is what
        /// <see cref="Content.GetAssetInfo(string, out AssetInfo)"/> and material texture slots
        /// reference.
        /// </summary>
        private string MakeAssetFullPath(string sourceId, string packId, string filePath, string variantId)
        {
            var dir = Path.GetDirectoryName(filePath);
            var fileName = Path.GetFileNameWithoutExtension(filePath) + ".flax";
            var rel = string.IsNullOrEmpty(dir) ? fileName : Path.Combine(dir, fileName);
            return Path.Combine(_settings.ContentDestinationFullPath, AssetInstallerService.Sanitize(sourceId), AssetInstallerService.Sanitize(packId), AssetInstallerService.FilesSubFolder, AssetInstallerService.VariantSubFolder(variantId), rel);
        }
    }
}
