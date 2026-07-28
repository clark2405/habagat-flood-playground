using UnityEngine;
using UnityEngine.Rendering;

namespace Habagat.Render
{
    /// <summary>
    /// Builds the entire scene — terrain, the surrounding world, water, props,
    /// lighting and atmosphere — under a single root object.
    ///
    /// This exists so there is exactly ONE construction path. The screenshot
    /// harness used to assemble the scene itself, which meant every image it
    /// produced was evidence about the harness rather than about anything you could
    /// press Play on. Both the runtime component and the harness now call this, so a
    /// screenshot is a statement about the real scene.
    ///
    /// Environment values mirror ENV.coastal in ThreeCanvas.jsx. The web build has a
    /// palette per preset; carrying the rest across is still on the list.
    /// </summary>
    public class WorldBuilder
    {
        /// <summary>The horizon band of the sky must equal the fog colour exactly —
        /// that identity is what makes land dissolve into sky with no seam.</summary>
        public static readonly Color FogColor = new Color(0.874f, 0.933f, 0.957f); // 0xdfeef4

        public GameObject Root { get; private set; }
        public WaterMeshBuilder Water { get; private set; }
        public OuterlandBuilder Outer { get; private set; }
        public Light Sun { get; private set; }

        private Material _propMat;

        public bool Build(FloodSim sim, PresetType type, bool withProps, Transform parent = null)
        {
            var shader = Shader.Find("Habagat/VertexColorLit");
            var waterShader = Shader.Find("Habagat/WaterVertexColor");
            if (shader == null || waterShader == null)
            {
                Debug.LogError("[WorldBuilder] Habagat shaders not found — did they fail to compile?");
                return false;
            }

            var palette = TerrainPalette.For(type);
            Root = new GameObject("HabagatWorld");
            if (parent != null) Root.transform.SetParent(parent, false);

            GameObject Child(string name, Mesh mesh, Material mat, bool cast, bool receive)
            {
                var go = new GameObject(name);
                go.transform.SetParent(Root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = cast ? ShadowCastingMode.On : ShadowCastingMode.Off;
                r.receiveShadows = receive;
                return go;
            }

            // ── Terrain ──────────────────────────────────────────────────────
            var terrainMesh = new TerrainMeshBuilder().Build(sim.Elev, palette);
            Child("Terrain", terrainMesh, new Material(shader), true, true);

            // ── The world outside the sandbox ────────────────────────────────
            // Deliberately NOT receiving shadows: the shadow frustum only covers the
            // play area, and sampling it out here paints a hard straight frustum edge
            // across the landscape — exactly the artificial line this mesh exists to
            // remove.
            Outer = new OuterlandBuilder();
            Outer.Build(sim, palette, OuterConfig.For(type));
            var outerMat = new Material(shader);
            outerMat.SetFloat("_Cull", 0f); // double-sided, like the web build
            Child("Outerland", Outer.Land, outerMat, false, false);

            // ── Props ────────────────────────────────────────────────────────
            // Built against the DRY terrain: props do not react to the flood, so
            // building them from an already-flooded heightmap would sink the barangay.
            if (withProps)
            {
                _propMat = new Material(shader);
                // Props are the only thing packing emissive into vertex alpha — the
                // glass. Terrain and outerland leave this at 0.
                _propMat.SetFloat("_EmissiveFromAlpha", 1f);

                var batches = PropScatter.Build(sim, type);
                batches.AddRange(PropScatter.BuildSimProps(sim));
                batches.AddRange(WorldDress.Build(Outer, type));
                foreach (var b in batches)
                    Child("Props_" + b.Mesh.name, b.Bake(b.Mesh.name + "_baked"), _propMat, b.CastShadow, false);
            }

            // ── Water ────────────────────────────────────────────────────────
            Water = new WaterMeshBuilder();
            var waterMat = new Material(waterShader);
            Child("Water", Water.Build(sim, palette), waterMat, false, false);
            Child("OuterWater", Outer.Water, new Material(waterShader), false, false);

            // ── Atmosphere ───────────────────────────────────────────────────
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = FogColor;
            RenderSettings.fogStartDistance = 100f;
            RenderSettings.fogEndDistance = 430f;
            RenderSettings.ambientMode = AmbientMode.Flat;
            // Well below the web build's nominal 0.85: three.js folds a 1/PI into its
            // diffuse BRDF that URP's Lambert does not, so carrying the number across
            // literally doubles the fill light and flattens everything into pastel.
            RenderSettings.ambientLight = new Color(0.874f, 0.910f, 0.980f) * 0.42f;

            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(Root.transform, false);
            Sun = sunGo.AddComponent<Light>();
            Sun.type = LightType.Directional;
            Sun.color = new Color(1f, 0.941f, 0.678f); // 0xfff0ad
            Sun.intensity = 1.35f;
            Sun.shadows = LightShadows.Soft;
            // Z negated against the web build's (40,65,40) for the same reason the
            // mesh negates it: the scene was mirrored into left-handed space, so
            // anything positioned in it has to be mirrored too or the sun comes from
            // the wrong quarter.
            sunGo.transform.position = new Vector3(40, 65, -40);
            sunGo.transform.LookAt(Vector3.zero);

            // ACES tone mapping is not a nicety: the web build renders through it, so
            // every colour in the ramp was chosen against its response curve.
            // Rendering the same vertex colours linearly gives a washed-out pastel map.
            var volumeGo = new GameObject("GlobalVolume");
            volumeGo.transform.SetParent(Root.transform, false);
            var volume = volumeGo.AddComponent<Volume>();
            volume.isGlobal = true;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var tonemap = profile.Add<UnityEngine.Rendering.Universal.Tonemapping>();
            tonemap.mode.overrideState = true;
            tonemap.mode.value = UnityEngine.Rendering.Universal.TonemappingMode.ACES;
            volume.sharedProfile = profile;

            return true;
        }

        /// <summary>
        /// Re-shape the water surface for the current simulation state. Only the
        /// water changes per tick — terrain, props and the surrounding world are
        /// static, which is what keeps this affordable.
        /// </summary>
        public void RefreshWater(FloodSim sim, PresetType type, double time, double swellAmp = 0.035) =>
            Water.UpdateGeometry(sim, TerrainPalette.For(type), time, swellAmp);

        public void Destroy()
        {
            if (Root == null) return;
            if (Application.isPlaying) Object.Destroy(Root); else Object.DestroyImmediate(Root);
            Root = null;
        }
    }
}
