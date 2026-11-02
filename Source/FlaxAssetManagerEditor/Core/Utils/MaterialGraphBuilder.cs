using System;
using FlaxEditor;
using FlaxEditor.Scripting;
using FlaxEditor.Surface;
using FlaxEngine;

namespace FlaxAssetManager.Core.Utils
{
    /// <summary>
    /// The PBR texture slots of a material to build. <see cref="Guid.Empty"/> leaves that surface
    /// input unconnected. A combined ARM map is not a slot here: the material service wires it
    /// through a single <c>Sample Texture</c> + <c>Unpack Float4</c> chain in a second pass.
    /// </summary>
    public struct PbrTextureSlots
    {
        /// <summary>Base color / albedo texture (multiplied by the public "Color" parameter).</summary>
        public Guid BaseColor;

        /// <summary>Emissive / self-illumination texture (multiplied by "EmissiveColor").</summary>
        public Guid Emissive;

        /// <summary>Opacity / alpha texture.</summary>
        public Guid Opacity;

        /// <summary>Roughness texture (uses the sampled color value).</summary>
        public Guid Roughness;

        /// <summary>Metallic texture (uses the sampled color value).</summary>
        public Guid Metallic;

        /// <summary>Ambient occlusion texture (uses the sampled color value).</summary>
        public Guid AmbientOcclusion;

        /// <summary>Height / displacement texture, fed to the Position Offset input.</summary>
        public Guid Height;

        /// <summary>Normal map texture, sampled by a <c>Normal Map</c> node whose decoded X,Y are
        /// scaled by "NormalStrength" and rebuilt by <c>Derive Normal Z</c> into the Normal input.</summary>
        public Guid Normal;
    }

    /// <summary>
    /// Creates a PBR material asset and wires the detected textures into its surface graph, using
    /// only stock, public Flax editor APIs:
    /// <list type="bullet">
    ///   <item><description><see cref="Editor.CreateAsset(string, string)"/> with the <c>"Material"</c> tag — the same asset
    ///   creator the content browser's "New Material" entry uses — followed by <see cref="Content.GetAssetInfo(string, out AssetInfo)"/>
    ///   to get the created asset id.</description></item>
    ///   <item><description><see cref="FlaxEditor.Surface.MaterialSurface"/> over a headless
    ///   <see cref="IVisjectSurfaceOwner"/>, so the graph is built with the very node API the plugin
    ///   already uses to rewire it (<see cref="VisjectSurfaceContext.SpawnNode(ushort,ushort,Float2,object[],Action{SurfaceNode})"/>,
    ///   <see cref="FlaxEditor.Surface.Elements.Box.CreateConnection(FlaxEditor.Surface.Elements.Box)"/>, <see cref="VisjectSurface.Save()"/>).</description></item>
    /// </list>
    ///
    /// The graph shape mirrors a material built by the engine's own material import: a
    /// <c>Texture</c> node per PBR slot feeding the material root's input box (a bare default
    /// material has no exposed parameters to set, so a graph is the only way to assign textures),
    /// a multiply node for the colour multipliers, and the normal map driven by a <c>Normal Map</c>
    /// node (it samples and decodes its own texture, so it cannot take a sampled texture) whose
    /// decoded X,Y are scaled by "NormalStrength" and fed to a <c>Derive Normal Z</c> node that
    /// rebuilds Z before the Normal input. The public multipliers are exposed as material parameters
    /// ("Color" = white, "EmissiveColor" = black so any material can gain emission,
    /// "NormalStrength" = 1) instead of hardcoded constants.
    ///
    /// Keeping this in the plugin is deliberate: it is the plugin's material *policy* (which slots
    /// to wire, which parameters to expose, which nodes to spawn), and doing it with public API
    /// means the plugin compiles and runs against a stock engine build.
    ///
    /// The surface graph built here is the *input* to
    /// <c>MaterialCreatorService.InsertSamplingNodes</c>, which routes every <c>Texture</c> node
    /// through a <c>Sample Texture</c> node and adds the shared UV transform. A <c>Normal Map</c> node
    /// is left as it was built: it samples its own texture, so it takes no <c>Sample Texture</c> node
    /// and only its UVs input joins that transform.
    ///
    /// Both surface writes <b>apply</b> what they saved: <see cref="Material.SaveSurface"/> only writes
    /// the asset file, so <see cref="MaterialSurfaceOwner.SurfaceData"/> follows it with
    /// <see cref="Asset.Reload()"/> + <see cref="Asset.WaitForLoaded(double)"/> — the same pair the
    /// engine's material editor performs. Without that pair a freshly created material keeps the shader of
    /// the graph that was on disk before the write (the empty default one) and renders pure black until
    /// something reloads the asset.
    ///
    /// Must run on the editor's main thread: <see cref="SurfaceNode"/> instances are editor GUI
    /// controls and the asset/content APIs used here are not thread-safe.
    /// </summary>
    public static class MaterialGraphBuilder
    {
        /// <summary>The asset-creator tag of a plain Flax material asset.</summary>
        public const string MaterialAssetTag = "Material";

        // Material root node: group 1 (Material), type 1. Its input box ids are the engine's
        // MaterialGraphBoxes / the editor's MaterialNodeBoxes values, identical on both sides.
        private const uint MaterialRootNodeType = (1u << 16) | 1;
        private const int RootBoxColor = 1;
        private const int RootBoxEmissive = 3;
        private const int RootBoxMetalness = 4;
        private const int RootBoxRoughness = 6;
        private const int RootBoxAmbientOcclusion = 7;
        private const int RootBoxNormal = 8;
        private const int RootBoxOpacity = 9;
        private const int RootBoxPositionOffset = 11;

        // Node archetypes used for the graph (group, type). The ids are shared by the engine's
        // native importer and the editor's managed archetypes, which is what lets the same graph
        // be built from C#.
        private const ushort ConstantGroup = 2;
        private const ushort ConstantColorType = 7;   // "Color"
        private const ushort TextureGroup = 5;
        private const ushort TextureType = 1;
        private const ushort NormalMapType = 4;
        private const ushort MathGroup = 3;
        private const ushort MultiplyType = 3;
        private const ushort ParameterGroup = 6;
        private const ushort GetParameterType = 1;

        // The normal-map decode chain nodes. Their ids are the engine's own archetype type ids
        // (Source/Editor/Surface/Archetypes/Packing.cs and Math.cs): "Pack Float2" (4,20),
        // "Unpack Float4" (4,32) and "Derive Normal Z" (3,29).
        private const ushort PackingGroup = 4;
        private const ushort PackFloat2Type = 20;
        private const ushort UnpackFloat4Type = 32;
        private const ushort DeriveNormalZType = 29;

        // Texture node boxes: 0 = UVs, 1 = Color, 2..5 = R/G/B/A, 6 = texture object reference.
        private const int TextureBoxColor = 1;
        // Normal Map node: 0 = UVs, 1 = the decoded Vector normal.
        private const int NormalMapBoxVector = 1;
        // Constant nodes: box 0 is their single value output.
        private const int ConstantBoxValue = 0;
        // Multiply node: 0 = A, 1 = B, 2 = Result.
        private const int MultiplyBoxA = 0;
        private const int MultiplyBoxB = 1;
        private const int MultiplyBoxResult = 2;
        // Get Parameter node: box 0 outputs the parameter value (its type follows the parameter).
        private const int GetParamBoxValue = 0;

        // Unpack Float4 node: 0 = Value (Float4 in), 1 = X, 2 = Y, 3 = Z, 4 = W.
        private const int UnpackFloat4BoxValue = 0;
        private const int UnpackFloat4BoxX = 1;
        private const int UnpackFloat4BoxY = 2;
        // Pack Float2 node: 0 = Value (Float2 out), 1 = X (in), 2 = Y (in).
        private const int PackFloat2BoxValue = 0;
        private const int PackFloat2BoxX = 1;
        private const int PackFloat2BoxY = 2;
        // Derive Normal Z node: 0 = XY (Float2 in), 1 = XYZ (Float3 out). It rebuilds Z from the
        // two tangent-space normal channels, so it is the node that turns a scaled XY back into a normal.
        private const int DeriveNormalZBoxXY = 0;
        private const int DeriveNormalZBoxXYZ = 1;
        /// <summary>
        /// The material-root input boxes this builder wires: the box id, the label the engine gives that
        /// box, and the value types it accepts. The ids are NOT public API - in Flax 1.13 they live in the
        /// internal <c>FlaxEditor.Surface.Archetypes.Material.SurfaceNodeMaterial.MaterialNodeBoxes</c>
        /// (read by reflection: Layer=0, Color=1, Mask=2, Emissive=3, Metalness=4, Specular=5, Roughness=6,
        /// AmbientOcclusion=7, Normal=8, Opacity=9, Refraction=10, PositionOffset=11, ...), which is why
        /// they are constants here, and why <see cref="ValidateRootInputBoxes"/> checks them at runtime.
        /// <para>
        /// <b>Accept</b> is a set, not a single type, because the engine's own material-root archetypes
        /// declare these inputs with native value types (<c>Float3</c>, <c>float</c>) while the graph feeds
        /// them managed ones (<see cref="Color"/>, <see cref="Vector3"/>). A box whose declared type is not
        /// in the set means this Flax build renamed, reordered or retyped a material input - which is exactly
        /// what used to be missed: against an engine that declares Color as Float4, wiring a Color texture
        /// into it produces a material that renders wrong and points at no one. The check refuses to wire,
        /// rather than pretending the mismatch cannot happen.
        /// </para>
        /// </summary>
        private static readonly (int Id, string EngineName, Type[] Accept)[] RootInputBoxes =
        {
            (RootBoxColor, "Color", new[] { typeof(Float3), typeof(Color) }),
            (RootBoxEmissive, "Emissive", new[] { typeof(Float3), typeof(Color) }),
            (RootBoxMetalness, "Metalness", new[] { typeof(float) }),
            (RootBoxRoughness, "Roughness", new[] { typeof(float) }),
            (RootBoxAmbientOcclusion, "AmbientOcclusion", new[] { typeof(float) }),
            (RootBoxNormal, "Normal", new[] { typeof(Float3), typeof(Vector3) }),
            (RootBoxOpacity, "Opacity", new[] { typeof(float) }),
        };

        // Names of the public material parameters this builder exposes.
        private const string ColorParameterName = "Color";
        private const string EmissiveColorParameterName = "EmissiveColor";
        private const string NormalStrengthParameterName = "NormalStrength";

        // Layout: column positions relative to the material root node.
        private const float NearColumn = -480.0f;
        private const float FarColumn = -760.0f;

        // The normal-map decode chain, laid out left to right: the Normal Map node (140 wide) feeds
        // an Unpack Float4, whose X,Y a Pack Float2 re-joins, a Multiply scales that XY by
        // "NormalStrength", and Derive Normal Z (the last column, 170 wide) rebuilds Z before the
        // Normal input. The strength parameter sits above its Multiply, on a row of its own.
        private static readonly Float2 NormalSourceLocation = new Float2(FarColumn, -460.0f);
        private static readonly Float2 NormalUnpackLocation = new Float2(-560.0f, -460.0f);
        private static readonly Float2 NormalPackLocation = new Float2(-380.0f, -460.0f);
        private static readonly Float2 NormalStrengthLocation = new Float2(-380.0f, -300.0f);
        private static readonly Float2 NormalScaleLocation = new Float2(-200.0f, -460.0f);
        private static readonly Float2 NormalDeriveLocation = new Float2(-200.0f, -300.0f);

        /// <summary>
        /// Creates (or overwrites) a PBR material asset at <paramref name="assetFullPath"/> and
        /// wires <paramref name="slots"/> into its surface graph in one pass. Overwriting reuses
        /// the existing asset id, so no manual delete is needed.
        /// </summary>
        /// <param name="assetFullPath">Absolute path of the <c>.flax</c> material to create.</param>
        /// <param name="slots">The PBR textures to wire (empty guid = leave that input unconnected).</param>
        /// <param name="packRootFullPath">
        /// Absolute path of the content folder the material lives in. Only used as a fallback: if
        /// the freshly created asset is not registered in the content database yet, that folder is
        /// refreshed and the lookup retried. Pass null to skip the retry.
        /// </param>
        /// <param name="assetId">Receives the created (or reused) asset id.</param>
        /// <param name="exposeColorParameter">
        /// When true and a base color texture is given, its multiplier is a public "Color" parameter
        /// (default white); when false a white constant node is used, matching the engine importer.
        /// </param>
        /// <param name="exposeEmissiveColorParameter">
        /// When true the emissive multiplier is a public "EmissiveColor" parameter (default black =
        /// no emission): it modulates the emissive map when one is wired, and drives the Emissive
        /// input directly when the pack ships no emissive texture, so any material can gain emission.
        /// </param>
        /// <param name="exposeNormalStrengthParameter">
        /// When true and a normal texture is given, the decoded normal's tangent-space XY is scaled by a
        /// public "NormalStrength" parameter (default 1) before <c>Derive Normal Z</c> rebuilds Z.
        /// </param>
        /// <returns>True when the material was created, wired and saved.</returns>
        public static bool TryCreate(string assetFullPath,
                                    PbrTextureSlots slots,
                                    string packRootFullPath,
                                    out Guid assetId,
                                    bool exposeColorParameter = true,
                                    bool exposeEmissiveColorParameter = true,
                                    bool exposeNormalStrengthParameter = true)
        {
            assetId = Guid.Empty;

            // 1. Create the material asset. Editor.CreateAsset returns true on FAILURE. The engine's
            //    "Material" creator writes a Lit / Opaque / Surface material with no surface chunk;
            //    the engine generates the default graph (root node only) when the surface is loaded.
            if (Editor.CreateAsset(MaterialAssetTag, assetFullPath))
            {
                Debug.LogError($"FlaxAssetManager: failed to create material asset at '{assetFullPath}'.");
                return false;
            }

            // 2. Resolve the asset id. The creator registers the asset itself; the content-database
            //    refresh is only a fallback for a pack folder the database has not seen yet.
            if (!Content.GetAssetInfo(assetFullPath, out var info))
            {
                if (packRootFullPath == null
                    || !ContentDatabaseUtils.RefreshContentFolder(packRootFullPath)
                    || !Content.GetAssetInfo(assetFullPath, out info))
                {
                    Debug.LogError($"FlaxAssetManager: created material '{assetFullPath}' but it is not registered as an asset.");
                    return false;
                }
            }
            assetId = info.ID;

            var material = Content.Load<Material>(assetId);
            if (!material)
            {
                Debug.LogError($"FlaxAssetManager: failed to load the created material asset '{assetFullPath}'.");
                return false;
            }

            // 3. Build the surface graph.
            var surface = new MaterialSurface(new MaterialSurfaceOwner(material));
            try
            {
                // Load() returns true on failure.
                if (surface.Load())
                {
                    Debug.LogError($"FlaxAssetManager: failed to load the surface graph of material '{material}'.");
                    return false;
                }

                var context = surface.Context;

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
                    Debug.LogError($"FlaxAssetManager: material '{material}' has no material root node; cannot wire its PBR inputs.");
                    return false;
                }

                // Sanity-check the hard-coded box ids against the engine's own box metadata before
                // wiring anything (see ValidateRootInputBoxes).
                if (!ValidateRootInputBoxes(root, material))
                    return false;

                if (!BuildGraph(context, root, material, slots, exposeColorParameter, exposeEmissiveColorParameter, exposeNormalStrengthParameter))
                    return false;

                context.MarkAsModified();

                // Save() returns true on failure, and the surface owner's setter throws when the
                // asset write itself fails; both are reported as a clean failure.
                try
                {
                    if (surface.Save())
                    {
                        Debug.LogError($"FlaxAssetManager: failed to save the surface graph of material '{material}'.");
                        return false;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"FlaxAssetManager: failed to save the surface graph of material '{material}': {e.Message}");
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
        /// Checks the hard-coded material-root box ids against the box metadata the engine loaded for
        /// this asset, before any node is wired. Those ids are internal engine API, not public API, so
        /// the compat build proves they compile, not that they still mean what they meant: a Flax
        /// release that removes a box, flips one into an output, or retypes one would otherwise wire a
        /// PBR input into the wrong socket silently, and a material that renders slightly off is a
        /// nasty bug to trace back to a plugin update. Presence, direction and value type are verified;
        /// the display label is not compared, because the internal enum names are not the UI labels.
        /// <para>
        /// The type check accepts any of a box's declared types (<see cref="RootInputBoxes"/>): the engine
        /// declares material inputs with native value types while the graph feeds managed ones, so an exact
        /// single-type comparison is wrong. It was: against a Flax build whose Color input is declared as
        /// Float3 the old check rejected a valid material and produced no material at all, while the real
        /// mismatch it was meant to catch stayed invisible behind that noise.
        /// </para>
        /// </summary>
        private static bool ValidateRootInputBoxes(SurfaceNode root, Material material)
        {
            foreach (var expected in RootInputBoxes)
            {
                if (!root.TryGetBox(expected.Id, out var box) || box == null)
                {
                    Debug.LogError($"FlaxAssetManager: the material root of '{material}' has no input box {expected.Id} (engine box '{expected.EngineName}'): this Flax build's box ids no longer match MaterialGraphBuilder, so the graph is not wired. Update the builder to this engine's ids.");
                    return false;
                }

                if (box.IsOutput)
                {
                    Debug.LogError($"FlaxAssetManager: material root box {expected.Id} ('{expected.EngineName}') of '{material}' is an output box in this Flax build; refusing to wire a PBR input into it.");
                    return false;
                }

                if (!AcceptsBoxType(box, expected.Accept))
                {
                    Debug.LogError($"FlaxAssetManager: material root box {expected.Id} ('{expected.EngineName}') of '{material}' takes {box.DefaultType.TypeName}, not one of [{string.Join(", ", System.Array.ConvertAll(expected.Accept, t => t.Name))}]: this Flax build's material inputs no longer match MaterialGraphBuilder, so the graph is not wired.");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// True when the box's declared connection type is one of the types this builder is willing to
        /// feed it. Compared through the engine's own <c>ScriptType</c> equality, which matches a managed
        /// <see cref="System.Type"/> against the archetype entry the engine built that box from.
        /// </summary>
        private static bool AcceptsBoxType(FlaxEditor.Surface.Elements.Box box, Type[] accept)
        {
            for (int i = 0; i < accept.Length; i++)
            {
                if (box.DefaultType == accept[i])
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Spawns and connects the nodes that wire <paramref name="slots"/> into the material root.
        /// A single failure aborts the build (the caller reports it); the asset is left on disk with
        /// the wiring done so far, exactly as the importer would leave it on a failed import.
        /// </summary>
        private static bool BuildGraph(VisjectSurfaceContext context,
                                       SurfaceNode root,
                                       Material material,
                                       PbrTextureSlots slots,
                                       bool exposeColorParameter,
                                       bool exposeEmissiveColorParameter,
                                       bool exposeNormalStrengthParameter)
        {
            var origin = root.Location;

            // Base color: Texture * multiplier -> Color input.
            if (slots.BaseColor != Guid.Empty)
            {
                var multiplier = exposeColorParameter
                    ? SpawnGetParameter(context, EnsureParameter(context, ColorParameterName, new ScriptType(typeof(Color)), Color.White), origin + new Float2(FarColumn + 150.0f, -180.0f))
                    : SpawnConstantColor(context, Color.White, origin + new Float2(FarColumn + 150.0f, -180.0f));
                if (!WireTexturedInput(context, root, slots.BaseColor, RootBoxColor, origin + new Float2(FarColumn, -320.0f), multiplier, "base color", material))
                    return false;
            }

            // Emissive: Texture * multiplier -> Emissive input, or the multiplier alone when the
            // pack ships no emissive texture (black default = no emission, raised at runtime).
            if (slots.Emissive != Guid.Empty)
            {
                var multiplier = exposeEmissiveColorParameter
                    ? SpawnGetParameter(context, EnsureParameter(context, EmissiveColorParameterName, new ScriptType(typeof(Color)), Color.Black), origin + new Float2(FarColumn + 150.0f, 130.0f))
                    : SpawnConstantColor(context, Color.Black, origin + new Float2(FarColumn + 150.0f, 130.0f));
                if (!WireTexturedInput(context, root, slots.Emissive, RootBoxEmissive, origin + new Float2(FarColumn, 0.0f), multiplier, "emissive", material))
                    return false;
            }
            else if (exposeEmissiveColorParameter)
            {
                var getEmissive = SpawnGetParameter(context, EnsureParameter(context, EmissiveColorParameterName, new ScriptType(typeof(Color)), Color.Black), origin + new Float2(NearColumn, 40.0f));
                if (getEmissive == null)
                {
                    Debug.LogError($"FlaxAssetManager: failed to spawn the {EmissiveColorParameterName} Get Parameter node in material '{material}'.");
                    return false;
                }
                getEmissive.GetBox(GetParamBoxValue).CreateConnection(root.GetBox(RootBoxEmissive));
            }

            // Roughness / Metallic / Ambient Occlusion / Opacity / Height: the sampled texture
            // value drives the input directly (Height feeds Position Offset).
            if (slots.Roughness != Guid.Empty && !WireTextureInput(context, root, slots.Roughness, RootBoxRoughness, origin + new Float2(NearColumn, 180.0f), "roughness", material))
                return false;
            if (slots.Metallic != Guid.Empty && !WireTextureInput(context, root, slots.Metallic, RootBoxMetalness, origin + new Float2(NearColumn, 300.0f), "metallic", material))
                return false;
            if (slots.AmbientOcclusion != Guid.Empty && !WireTextureInput(context, root, slots.AmbientOcclusion, RootBoxAmbientOcclusion, origin + new Float2(NearColumn, 420.0f), "ambient occlusion", material))
                return false;
            if (slots.Opacity != Guid.Empty && !WireTextureInput(context, root, slots.Opacity, RootBoxOpacity, origin + new Float2(NearColumn, 540.0f), "opacity", material))
                return false;
            if (slots.Height != Guid.Empty && !WireTextureInput(context, root, slots.Height, RootBoxPositionOffset, origin + new Float2(NearColumn, 660.0f), "height", material))
                return false;

            // Normal: sampled and decoded by a Normal Map node (it holds the texture as an asset
            // value and decodes the tangent-space vector itself, so it takes no texture input box and
            // no Sample Texture node), scaled by "NormalStrength" and rebuilt by Derive Normal Z.
            if (slots.Normal != Guid.Empty && !WireNormalInput(context, root, slots.Normal, exposeNormalStrengthParameter, origin, material))
                return false;

            return true;
        }

        /// <summary>
        /// Wires a normal map into the material root's Normal input as
        /// <c>Normal Map -> Unpack Float4 -> Pack Float2 -> (x "NormalStrength") -> Derive Normal Z -> Normal</c>.
        /// The <c>Normal Map</c> node is the sampler: it holds the texture as its asset value, samples
        /// it and decodes the encoded tangent-space vector itself (the HLSL the engine generates for it
        /// is <c>UnpackNormalMap(rg)</c> = <c>rg * 2 - 1</c> with Z rebuilt), so its Vector output is
        /// already a normal in [-1;1] - the chain needs no <c>Texture</c> + <c>Sample Texture</c> nodes
        /// and no [0;1] -> [-1;1] remap, and a normal map is sampled by a <c>Normal Map</c> node, which
        /// is what the texture's import settings (it imports as a <c>Normal Map</c> texture) describe.
        /// Only its X,Y are kept (<c>Unpack Float4</c> + <c>Pack Float2</c>) because normal strength is a
        /// tangent-space XY scale: scaling the decoded <c>Float3</c> would scale Z too, so strength 2
        /// would flatten the surface towards the shading normal instead of bending it. <c>Derive Normal
        /// Z</c> rebuilds Z from the scaled channels, so the Normal input is fed a normal at any
        /// strength; the parameter's default of 1 leaves the decoded normal untouched.
        /// </summary>
        private static bool WireNormalInput(VisjectSurfaceContext context,
                                            SurfaceNode root,
                                            Guid normalTextureId,
                                            bool exposeNormalStrengthParameter,
                                            Float2 origin,
                                            Material material)
        {
            // The sampler: holds the texture and decodes it, exposing the normal on its Vector box.
            var normalMap = context.SpawnNode(TextureGroup, NormalMapType, origin + NormalSourceLocation, new object[] { normalTextureId });
            if (normalMap == null)
            {
                Debug.LogError($"FlaxAssetManager: failed to spawn the Normal Map node in material '{material}'.");
                return false;
            }

            // Keep the decoded X,Y: unpack the vector, then pack the two channels back into a Float2.
            var unpack = context.SpawnNode(PackingGroup, UnpackFloat4Type, origin + NormalUnpackLocation);
            if (unpack == null)
            {
                Debug.LogError($"FlaxAssetManager: failed to spawn the normal-map Unpack Float4 node in material '{material}'.");
                return false;
            }

            var pack = context.SpawnNode(PackingGroup, PackFloat2Type, origin + NormalPackLocation);
            if (pack == null)
            {
                Debug.LogError($"FlaxAssetManager: failed to spawn the normal-map Pack Float2 node in material '{material}'.");
                return false;
            }

            normalMap.GetBox(NormalMapBoxVector).CreateConnection(unpack.GetBox(UnpackFloat4BoxValue));
            unpack.GetBox(UnpackFloat4BoxX).CreateConnection(pack.GetBox(PackFloat2BoxX));
            unpack.GetBox(UnpackFloat4BoxY).CreateConnection(pack.GetBox(PackFloat2BoxY));

            // Scale the tangent-space XY by the public "NormalStrength" parameter (default 1).
            var normalXY = pack.GetBox(PackFloat2BoxValue);
            if (exposeNormalStrengthParameter)
            {
                var strength = SpawnGetParameter(context, EnsureParameter(context, NormalStrengthParameterName, new ScriptType(typeof(float)), 1.0f), origin + NormalStrengthLocation);
                var scale = SpawnMultiply(context, origin + NormalScaleLocation);
                if (strength == null || scale == null)
                {
                    Debug.LogError($"FlaxAssetManager: failed to spawn the {NormalStrengthParameterName} scale nodes in material '{material}'.");
                    return false;
                }

                normalXY.CreateConnection(scale.GetBox(MultiplyBoxA));
                strength.GetBox(GetParamBoxValue).CreateConnection(scale.GetBox(MultiplyBoxB));
                normalXY = scale.GetBox(MultiplyBoxResult);
            }

            // Rebuild Z from the scaled channels and feed the material root's Normal input.
            var derive = context.SpawnNode(MathGroup, DeriveNormalZType, origin + NormalDeriveLocation);
            if (derive == null)
            {
                Debug.LogError($"FlaxAssetManager: failed to spawn the normal-map Derive Normal Z node in material '{material}'.");
                return false;
            }

            normalXY.CreateConnection(derive.GetBox(DeriveNormalZBoxXY));
            derive.GetBox(DeriveNormalZBoxXYZ).CreateConnection(root.GetBox(RootBoxNormal));
            return true;
        }

        /// <summary>
        /// Adds a <c>Texture</c> node whose Color output drives a material root input directly.
        /// </summary>
        private static bool WireTextureInput(VisjectSurfaceContext context, SurfaceNode root, Guid textureId, int rootBoxId, Float2 location, string label, Material material)
        {
            var texture = SpawnTexture(context, textureId, location);
            if (texture == null)
            {
                Debug.LogError($"FlaxAssetManager: failed to spawn the {label} texture node in material '{material}'.");
                return false;
            }
            texture.GetBox(TextureBoxColor).CreateConnection(root.GetBox(rootBoxId));
            return true;
        }

        /// <summary>
        /// Adds a <c>Texture</c> node whose Color output is multiplied by <paramref name="multiplier"/>
        /// (a constant or Get Parameter node) before driving a material root input.
        /// </summary>
        private static bool WireTexturedInput(VisjectSurfaceContext context, SurfaceNode root, Guid textureId, int rootBoxId, Float2 location, SurfaceNode multiplier, string label, Material material)
        {
            var texture = SpawnTexture(context, textureId, location);
            if (texture == null || multiplier == null)
            {
                Debug.LogError($"FlaxAssetManager: failed to spawn the {label} texture wiring nodes in material '{material}'.");
                return false;
            }

            var multiply = SpawnMultiply(context, location + new Float2(240.0f, 80.0f));
            if (multiply == null)
            {
                Debug.LogError($"FlaxAssetManager: failed to spawn the {label} multiply node in material '{material}'.");
                return false;
            }

            texture.GetBox(TextureBoxColor).CreateConnection(multiply.GetBox(MultiplyBoxA));
            multiplier.GetBox(ConstantBoxValue).CreateConnection(multiply.GetBox(MultiplyBoxB));
            multiply.GetBox(MultiplyBoxResult).CreateConnection(root.GetBox(rootBoxId));
            return true;
        }

        private static SurfaceNode SpawnTexture(VisjectSurfaceContext context, Guid textureId, Float2 location)
        {
            return context.SpawnNode(TextureGroup, TextureType, location, new object[] { textureId });
        }

        private static SurfaceNode SpawnMultiply(VisjectSurfaceContext context, Float2 location)
        {
            return context.SpawnNode(MathGroup, MultiplyType, location);
        }

        private static SurfaceNode SpawnConstantColor(VisjectSurfaceContext context, Color value, Float2 location)
        {
            return context.SpawnNode(ConstantGroup, ConstantColorType, location, new object[] { value });
        }

        private static SurfaceNode SpawnGetParameter(VisjectSurfaceContext context, SurfaceParameter parameter, Float2 location)
        {
            if (parameter == null)
                return null;
            return context.SpawnNode(ParameterGroup, GetParameterType, location, new object[] { parameter.ID });
        }

        /// <summary>
        /// Returns an existing material parameter by name, or creates and registers a new public one
        /// with the given default value. Reusing an existing parameter keeps repeated material
        /// rebuilds idempotent (the parameter id, and so every Get Parameter node, stays stable).
        /// </summary>
        private static SurfaceParameter EnsureParameter(VisjectSurfaceContext context, string name, ScriptType type, object defaultValue)
        {
            var existing = context.GetParameter(name);
            if (existing != null)
                return existing;
            var param = new SurfaceParameter
            {
                Type = type,
                ID = Guid.NewGuid(),
                Name = name,
                IsPublic = true,
                Value = defaultValue,
            };
            context.Parameters.Add(param);
            return param;
        }

        /// <summary>
        /// Minimal <see cref="IVisjectSurfaceOwner"/> that lets a material asset's surface be loaded,
        /// edited and saved without the material editor window (headless graph editing on the
        /// editor's main thread).
        /// <para>
        /// Writing <see cref="SurfaceData"/> also <b>applies</b> the graph: saving the surface file is
        /// not enough to make the saved material render, see the setter.
        /// </para>
        /// </summary>
        public sealed class MaterialSurfaceOwner : IVisjectSurfaceOwner
        {
            private readonly Material _material;

            public MaterialSurfaceOwner(Material material)
            {
                _material = material;
            }

            public Undo Undo => null;

            public Asset SurfaceAsset => _material;

            public string SurfaceName => _material.ToString();

            public VisjectSurfaceContext ParentContext => null;

            public byte[] SurfaceData
            {
                get => _material.LoadSurface(createDefaultIfMissing: true);
                set
                {
                    // SaveSurface returns true on failure.
                    if (_material.SaveSurface(value, _material.Info))
                        throw new InvalidOperationException($"Failed to save the surface data of material '{_material}'.");

                    // Saving the surface file does NOT apply the graph, and this is the step that makes a
                    // freshly built material render. The engine's Material::SaveSurface releases the
                    // asset's chunks, overwrites the on-disk MaterialInfo, drops the shader cache and
                    // returns; the loaded asset keeps the MaterialShader it was built with, which the
                    // material generator produced from the graph that was on disk BEFORE this write. The
                    // in-memory material is therefore a new graph and a new MaterialInfo bound to a shader
                    // that knows none of the new graph's parameters (Color, EmissiveColor, NormalStrength,
                    // UV Scale, UV Tiling), so the parameters it exposes are never bound and the surface
                    // renders pure black. The engine's own material editor closes that gap in this same
                    // setter - FlaxEditor/Windows/Assets/MaterialWindow.cs, SurfaceData setter: SaveSurface,
                    // then Reload, then WaitForLoaded - and the content browser's asset context-menu
                    // "Reload" entry is the same Asset.Reload, which is exactly why a hand-reloaded
                    // material of this plugin starts to render.
                    _material.Reload();

                    // Reload queues an asynchronous re-load: the shader for the new graph exists only once
                    // it finishes, and callers read the asset straight after (the service reloads it by id
                    // to assign it to the pack's models). WaitForLoaded returns true when the load FAILED.
                    if (_material.WaitForLoaded())
                        throw new InvalidOperationException($"Failed to reload material '{_material}' after saving its surface data.");
                }
            }

            public void OnContextCreated(VisjectSurfaceContext context)
            {
            }

            public void OnSurfaceEditedChanged()
            {
            }

            public void OnSurfaceGraphEdited()
            {
            }

            public void OnSurfaceClose()
            {
            }
        }
    }
}
