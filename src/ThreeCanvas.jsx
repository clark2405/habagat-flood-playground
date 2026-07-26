import React, { useRef, useEffect } from "react";
import * as THREE from "three";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { mergeGeometries } from "three/addons/utils/BufferGeometryUtils.js";
import { EffectComposer } from "three/addons/postprocessing/EffectComposer.js";
import { RenderPass } from "three/addons/postprocessing/RenderPass.js";
import { GTAOPass } from "three/addons/postprocessing/GTAOPass.js";
import { OutputPass } from "three/addons/postprocessing/OutputPass.js";

const W = 96;
const H = 64;
const SEA_LEVEL = 0.0;
const SKY_R = 450;   // sky dome radius — anything past it is hidden behind the sky
const DX = W / (W - 1); // world-space spacing between heightmap columns…
const DZ = H / (H - 1); // …and rows, used for analytic terrain normals

// ── Per-Preset World / Backdrop ──────────────────────────────────────────────
// Each preset gets a distinct surrounding "world". The horizon band of the sky
// dome is painted with the EXACT fog colour, so land that fades into fog meets
// a sky of the same colour — the sandbox has no visible end. Storm mode is a
// shared dark override layered on top of these clear-weather palettes.
const STORM_ENV = {
  zenith: 0x18202b,
  mid: 0x27333f,
  fog: 0x3d4a58,
  fogNear: 30,
  fogFar: 300,
};

// Deterministic RNG — the surrounding world must NOT reshuffle every time the
// scene is rebuilt (placing a single house re-runs the whole effect).
function seededRng(seed) {
  let s = seed >>> 0;
  return () => {
    s = (s + 0x6d2b79f5) >>> 0;
    let t = Math.imul(s ^ (s >>> 15), 1 | s);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
function strSeed(str) {
  let h = 2166136261;
  for (let i = 0; i < str.length; i++) { h ^= str.charCodeAt(i); h = Math.imul(h, 16777619); }
  return h >>> 0;
}

// ── Tiny value-noise for organic surrounding terrain ─────────────────────────
function _hash2(ix, iz) {
  let h = (ix * 374761393 + iz * 668265263) | 0;
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  return ((h ^ (h >>> 16)) >>> 0) / 4294967295;
}
function _vnoise(x, z) {
  const x0 = Math.floor(x), z0 = Math.floor(z);
  const fx = x - x0, fz = z - z0;
  const sx = fx * fx * (3 - 2 * fx), sz = fz * fz * (3 - 2 * fz);
  const n00 = _hash2(x0, z0), n10 = _hash2(x0 + 1, z0);
  const n01 = _hash2(x0, z0 + 1), n11 = _hash2(x0 + 1, z0 + 1);
  const a = n00 + (n10 - n00) * sx;
  const b = n01 + (n11 - n01) * sx;
  return a + (b - a) * sz;
}
function _fbm(x, z) {
  return _vnoise(x, z) * 0.6 + _vnoise(x * 2.13 + 11.3, z * 2.13 + 5.7) * 0.3 + _vnoise(x * 4.31 + 27.1, z * 4.31 + 17.9) * 0.1;
}
const _smooth = (a, b, x) => { const t = Math.min(Math.max((x - a) / (b - a), 0), 1); return t * t * (3 - 2 * t); };

// Shared world swell. The sandbox's water sheet and the open water outside it are
// two separate meshes, so they must be displaced by the SAME function of world
// position — anything else steps them apart at the border. Amplitude rises with
// the weather, which is what makes the whole seascape react to a storm at once.
const swellAt = (wx, wz, t) =>
  Math.sin(wx * 0.085 + t * 1.25) * 0.55 +
  Math.sin(wz * 0.061 - t * 0.92) * 0.36 +
  Math.sin((wx + wz) * 0.155 + t * 2.05) * 0.2;

const _lin = (a, b, x) => Math.min(Math.max((x - a) / (b - a), 0), 1);
const _mix3 = (a, b, k) => [a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k, a[2] + (b[2] - a[2]) * k];

// Fine ground variegation, sampled in WORLD space so the sandbox and the land
// around it share one continuous pattern. It doubles as dither: the colour ramp
// changes so gradually across a slope that 8-bit output quantises it into
// contour bands, which the grid's diagonal split serrates into scallops.
// The low-frequency term is the important one: without a broad patchiness the
// ground is a single flat colour over enormous stretches, which is most of what
// made the landscape read as dry and bland. Big soft patches of lighter and
// darker growth give the eye something to follow across an empty field.
const groundTint = (wx, wz) =>
  (_fbm(wx * 0.055 + 13.7, wz * 0.055 + 31.1) - 0.5) * 0.105 +
  (_fbm(wx * 0.34 + 71.3, wz * 0.34 + 19.7) - 0.5) * 0.075 +
  (_fbm(wx * 1.13 + 3.9, wz * 1.13 + 47.1) - 0.5) * 0.035;

// Terrain color ramp, shared by the sandbox AND the surrounding land so the two
// are literally the same material response. Underwater ground darkens with
// depth (a bright sand shelf that stops at the map border is the single most
// obvious "this is a square tile" tell), and a narrow wet-sand beach band sits
// just above the waterline so shorelines read as shorelines.
function colorFor(e, c) {
  if (e <= SEA_LEVEL) return _mix3(c.sea, c.deep, _lin(0, -2.6, e));
  // Every band must span at least ~1 cell of slope. A narrow band is effectively a
  // step in the vertex colours, and a step interpolated across a diagonally-split
  // grid scallops into crescents along any steep bank — the ramp, not the mesh, was
  // what made riverbanks and shorelines look corrugated.
  if (e < c.beach) return _mix3(c.sea, c.low, _lin(0, c.beach, e));
  // Linear inside each band on purpose: the mesh interpolates vertex colours
  // linearly, so a piecewise-linear ramp reproduces exactly and leaves no
  // interpolation error to alternate with the triangulation.
  const t = Math.min(Math.max((e - c.beach) / (3.4 - c.beach), 0), 1);
  if (t < 0.5) return _mix3(c.low, c.mid, t / 0.5);
  return _mix3(c.mid, c.high, (t - 0.5) / 0.5);
}

const ENV = {
  // Tropical beach village → land continues into an irregular coast + open sea
  coastal: {
    sky: { zenith: 0x2f86c9, mid: 0x8bcde9 },
    fog: 0xdfeef4,
    fogNear: 100,
    fogFar: 430,
    ambient: { color: 0xdfe8fa, intensity: 0.85 },
    dir: { color: 0xfff0ad, intensity: 1.55 },
    sun: true,
    colors: {
      sea: [0.86, 0.75, 0.56], deep: [0.07, 0.25, 0.33], beach: 0.85,
      low: [0.48, 0.62, 0.34], mid: [0.38, 0.55, 0.28], high: [0.56, 0.54, 0.38],
    },
    water: { shallow: [0.42, 0.80, 0.76], deep: [0.05, 0.24, 0.33] },
    outer: { sea: true, reach: 430, rings: 64, step0: 1.0, amp: 2.4, rise: 3.4, seabed: -6.0, freq: 0.019 },
    backdrop: { kind: "mountains", color: 0x5b7b7a, count: 34, behindOnly: true, radius: 300, height: [20, 50], spread: 95 },
  },
  // Lush highland basin → the valley opens out into endless rolling green hills
  river: {
    sky: { zenith: 0x5aa3c4, mid: 0xa8cfd6 },
    fog: 0xdce8dd,
    fogNear: 100,
    fogFar: 425,
    ambient: { color: 0xe4eee0, intensity: 0.82 },
    dir: { color: 0xfff1c2, intensity: 1.5 },
    sun: true,
    colors: {
      sea: [0.33, 0.31, 0.22], deep: [0.13, 0.17, 0.14], beach: 1.50,
      low: [0.34, 0.52, 0.24], mid: [0.28, 0.45, 0.20], high: [0.45, 0.44, 0.36],
    },
    water: { shallow: [0.36, 0.64, 0.62], deep: [0.09, 0.26, 0.30] },
    outer: { sea: false, reach: 430, rings: 64, step0: 1.0, amp: 3.2, rise: 3.0, freq: 0.016 },
    backdrop: { kind: "mountains", color: 0x40624a, count: 42, behindOnly: false, radius: 292, height: [26, 66], spread: 96 },
  },
  // Metro Manila barangay → paved ground continues into a hazy city skyline
  urban: {
    sky: { zenith: 0x8fa6b8, mid: 0xb6c4cf },
    fog: 0xd2dae0,
    fogNear: 72,
    fogFar: 360,
    ambient: { color: 0xdbe3ea, intensity: 0.72 },
    dir: { color: 0xf2efe0, intensity: 1.15 },
    sun: false,
    colors: {
      sea: [0.26, 0.30, 0.32], deep: [0.09, 0.13, 0.15], beach: 0.60,
      low: [0.44, 0.45, 0.45], mid: [0.50, 0.51, 0.49], high: [0.46, 0.51, 0.40],
    },
    water: { shallow: [0.40, 0.58, 0.60], deep: [0.11, 0.22, 0.26] },
    outer: { sea: false, reach: 430, rings: 64, step0: 1.0, amp: 0.6, rise: 0.8, freq: 0.02 },
    backdrop: { kind: "city", color: 0x3f4852, count: 190, radius: 200, spread: 150 },
  },
  // Storm-lashed island → land breaks up into an irregular coast on every side
  island: {
    sky: { zenith: 0x2a8fd0, mid: 0x83cbe9 },
    fog: 0xdcf0f6,
    fogNear: 100,
    fogFar: 430,
    ambient: { color: 0xe1ecfd, intensity: 0.85 },
    dir: { color: 0xffeeb8, intensity: 1.55 },
    sun: true,
    colors: {
      sea: [0.86, 0.76, 0.56], deep: [0.06, 0.26, 0.34], beach: 0.90,
      low: [0.34, 0.56, 0.28], mid: [0.26, 0.48, 0.22], high: [0.52, 0.52, 0.40],
    },
    water: { shallow: [0.42, 0.80, 0.78], deep: [0.05, 0.25, 0.34] },
    outer: { sea: true, reach: 430, rings: 64, step0: 1.0, amp: 2.0, rise: 1.2, seabed: -6.0, freq: 0.024 },
    backdrop: { kind: "mountains", color: 0x4f7a86, count: 22, behindOnly: false, radius: 300, height: [12, 30], spread: 95 },
  },
};
ENV.basin = ENV.urban;

export default function ThreeCanvas({
  simRef,
  tool,
  storm,
  rain = 0,
  cameraPreset,
  onPaint,
  onHoverCell,
  houses = [],
  presetType = "coastal",
}) {
  const containerRef = useRef(null);
  const raycasterRef = useRef(new THREE.Raycaster());
  const mouseRef = useRef(new THREE.Vector2(-999, -999));
  const isMouseDownRef = useRef(false);
  const stormRef = useRef(storm);
  const rainRef = useRef(rain);
  const toolRef = useRef(tool);
  stormRef.current = storm;
  rainRef.current = rain;
  toolRef.current = tool;

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    let width = container.clientWidth || window.innerWidth;
    let height = container.clientHeight || window.innerHeight;

    // 1. Scene & Fog Setup (driven by the selected preset's world palette)
    //    Linear fog with an explicit near/far: the sandbox itself stays crisp,
    //    while everything past ~350u is 100% fog colour — and the sky's horizon
    //    band is painted that same colour, so land dissolves into sky with no
    //    seam and no discernible end to the world.
    const scene = new THREE.Scene();
    const env = ENV[presetType] || ENV.coastal;
    const isInitialStorm = stormRef.current;

    scene.fog = new THREE.Fog(
      isInitialStorm ? STORM_ENV.fog : env.fog,
      isInitialStorm ? STORM_ENV.fogNear : env.fogNear,
      isInitialStorm ? STORM_ENV.fogFar : env.fogFar
    );

    // Near plane at 0.5 rather than 0.1. Nothing can get closer than that — the
    // orbit controls clamp the eye to 15 units from the target — and it cuts the
    // far:near ratio from 10000:1 to 2000:1. The ambient occlusion pass below
    // reconstructs view positions from the depth buffer, so it is the one thing
    // in the scene that cares about depth precision; this is cheap insurance for
    // it rather than a fix for an observed artefact.
    const camera = new THREE.PerspectiveCamera(40, width / height, 0.5, 1000);
    camera.position.set(0, 48, 55);

    // 2. WebGL Renderer
    const renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: "high-performance" });
    renderer.setSize(width, height);
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure = 1.0;

    container.innerHTML = "";
    container.appendChild(renderer.domElement);

    // 2b. Post-processing — ambient occlusion
    //     One directional light and a flat ambient term leave every prop sitting
    //     on the ground with no contact shadow, which is what made the scene read
    //     as objects floating on a painted surface rather than resting on it.
    //     Ground-truth AO darkens the creases the light model cannot: under the
    //     eaves, inside the stilts, where a bush meets the slope, along kerbs.
    //     It is the single biggest remaining step toward the soft hand-painted
    //     look, and unlike the shadow map it costs nothing per light.
    const composer = new EffectComposer(renderer);
    composer.setSize(width, height);
    composer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    composer.addPass(new RenderPass(scene, camera));

    const gtao = new GTAOPass(scene, camera, width, height);
    gtao.updateGtaoMaterial({
      // Radius and thickness are both in WORLD units — a terrain cell is 1u and a
      // house is ~2.5u — and `thickness` is the parameter that actually matters
      // here. It tells the pass how solid an occluder is assumed to be, and left
      // at its default of 1.0 against props that are 2-4 units deep the horizon
      // search passes clean through every hut and tree: the AO buffer comes back
      // essentially blank no matter how large the radius or how high the blend.
      // Matching thickness to the depth of the geometry is what makes the effect
      // exist at all.
      radius: 5.0,
      thickness: 5.0,
      // Below 1.0 the samples bunch toward the shaded point, which favours tight
      // contact shading over a broad grey wash across open ground.
      distanceExponent: 0.7,
      scale: 1.3,
      samples: 16,
      screenSpaceRadius: false,
    });
    // Restrained on purpose. Pushed harder this reads as dirt rather than shade,
    // and the reference art keeps its shadows soft and low-contrast.
    gtao.blendIntensity = 0.9;
    composer.addPass(gtao);

    // OutputPass applies tone mapping and the sRGB conversion at the very end of
    // the chain — with a composer in play the renderer no longer does it itself,
    // and without this the whole image comes out flat and washed.
    composer.addPass(new OutputPass());

    // 3. OrbitControls (Strict Camera Limits so user never dips below horizon)
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.dampingFactor = 0.05;
    controls.minDistance = 15;
    // Far enough out to take in the surrounding world, which is now worth
    // looking at rather than being an empty green apron.
    controls.maxDistance = 185;
    controls.minPolarAngle = Math.PI / 12;
    // Keep the eye a little higher than before: the world now continues past the
    // sandbox in every direction, and a near-ground camera would look *through*
    // that surrounding land instead of across it.
    controls.maxPolarAngle = Math.PI / 2.32;
    controls.target.set(0, 0, 0);

    // Framing had to open up along with the props. Everything on the map is now
    // built at roughly three times its old size, so the old iso position sat the
    // eye inside the village instead of looking across it — and none of the new
    // surrounding world was ever in shot.
    if (cameraPreset === "isometric") {
      camera.position.set(56, 52, 56);
    } else if (cameraPreset === "top") {
      camera.position.set(0, 92, 0.1);
    } else if (cameraPreset === "close") {
      camera.position.set(-20, 15, 30);
    }
    controls.update();

    // 4. Gradient Sky Dome — vertex-coloured so it goes through the exact same
    //    tone-mapping path as the fog colour. The bottom band IS the fog colour,
    //    which is what makes the terrain→sky transition disappear.
    const skyGeo = new THREE.SphereGeometry(SKY_R, 32, 24);
    const skyCount = skyGeo.attributes.position.count;
    const skyColArr = new Float32Array(skyCount * 3);
    skyGeo.setAttribute("color", new THREE.BufferAttribute(skyColArr, 3));
    const skyMat = new THREE.MeshBasicMaterial({
      side: THREE.BackSide,
      vertexColors: true,
      fog: false,
    });
    const skyMesh = new THREE.Mesh(skyGeo, skyMat);
    scene.add(skyMesh);

    const skyY = skyGeo.attributes.position.array;
    const _skyTmp = new THREE.Color();
    const paintSky = (horizon, midC, zenith) => {
      for (let i = 0; i < skyCount; i++) {
        const yn = skyY[i * 3 + 1] / SKY_R; // -1 (nadir) .. 1 (zenith)
        _skyTmp.copy(horizon).lerp(midC, _smooth(0.10, 0.58, yn));
        _skyTmp.lerp(zenith, _smooth(0.42, 0.96, yn));
        skyColArr[i * 3] = _skyTmp.r;
        skyColArr[i * 3 + 1] = _skyTmp.g;
        skyColArr[i * 3 + 2] = _skyTmp.b;
      }
      skyGeo.attributes.color.needsUpdate = true;
    };

    // Live sky colours, lerped toward the current weather each frame.
    const skyHorizon = new THREE.Color(isInitialStorm ? STORM_ENV.fog : env.fog);
    const skyMid = new THREE.Color(isInitialStorm ? STORM_ENV.mid : env.sky.mid);
    const skyZenith = new THREE.Color(isInitialStorm ? STORM_ENV.zenith : env.sky.zenith);
    paintSky(skyHorizon, skyMid, skyZenith);

    // Glowing Sun Disc (hidden on presets whose sky is overcast, e.g. smoggy city)
    const sunGeo = new THREE.SphereGeometry(9, 16, 16);
    const sunMat = new THREE.MeshBasicMaterial({ color: 0xffd166, fog: false });
    const sunMesh = new THREE.Mesh(sunGeo, sunMat);
    sunMesh.position.set(150, 200, -170);
    sunMesh.visible = env.sun && !isInitialStorm;
    scene.add(sunMesh);

    // 5. Lighting Setup (tinted per preset for a matching mood)
    const ambientLight = new THREE.AmbientLight(
      isInitialStorm ? 0x3d4b5c : env.ambient.color,
      isInitialStorm ? 0.95 : env.ambient.intensity
    );
    scene.add(ambientLight);

    const dirLight = new THREE.DirectionalLight(
      isInitialStorm ? 0x889bb0 : env.dir.color,
      isInitialStorm ? 1.05 : env.dir.intensity
    );
    dirLight.position.set(40, 65, 40);
    dirLight.castShadow = true;
    dirLight.shadow.mapSize.width = 2048;
    dirLight.shadow.mapSize.height = 2048;
    dirLight.shadow.bias = -0.0005;
    dirLight.shadow.normalBias = 0.02;
    dirLight.shadow.camera.left = -72;
    dirLight.shadow.camera.right = 72;
    dirLight.shadow.camera.top = 56;
    dirLight.shadow.camera.bottom = -56;
    scene.add(dirLight);

    // 6. (The water surface outside the sandbox is built further down, on the
    //    same ring mesh as the surrounding land — see 9c — so that its depth
    //    shading is generated by exactly the same rule as the play area's.)

    // 7. Distant Backdrop Silhouettes — mountains, forested peaks, or a city
    //    skyline depending on the preset. They sit far enough out (240u+) to be
    //    ~80% dissolved by fog, which is what sells them as distance rather than
    //    as props parked around the edge of a tile.
    const backdropGroup = new THREE.Group();
    const backdropMats = [];
    const buildBackdrop = (group, cfg) => {
      const rnd = seededRng(strSeed("backdrop:" + presetType));
      // The backdrop never moves, so each layer is baked into a single geometry —
      // 190 separate building meshes is 190 draw calls for scenery nobody touches.
      // Every layer is pre-hazed toward the fog colour by its own amount, so even
      // the un-fogged parts read as being at their own distance.
      // Depth comes from LAYERS, not from one ring. A single band of silhouettes
      // at one distance and one haze level reads as a painted wall standing
      // around the map; three bands at different distances, each hazed harder
      // than the one in front, is what actually makes a horizon recede.
      const layers = [
        { dr: 0.0, haze: 0.34, hs: 1.0 },
        { dr: 0.42, haze: 0.58, hs: 1.35 },
        { dr: 0.95, haze: 0.78, hs: 1.75 },
      ];

      if (cfg.kind === "city") {
        for (let L = 0; L < layers.length; L++) {
          const lay = layers[L];
          const bucket = [];
          const push = (geo, x, y, z) => { geo.translate(x, y, z); bucket.push(geo); };
          const tone = new THREE.Color(cfg.color).lerp(new THREE.Color(env.fog), lay.haze);
          const mat = new THREE.MeshStandardMaterial({ color: tone, roughness: 0.9, flatShading: true });
          backdropMats.push({ mat, base: new THREE.Color(cfg.color), haze: lay.haze });
          const n = Math.round(cfg.count * (L === 0 ? 0.5 : L === 1 ? 0.32 : 0.28));
          for (let i = 0; i < n; i++) {
            const angle = rnd() * Math.PI * 2;
            const radius = cfg.radius * (1 + lay.dr) + rnd() * cfg.spread;
            // Front layer is low-rise sprawl, back layers are towers. Reading a
            // skyline depends on that gradient of heights, not on random boxes.
            const bh = (L === 0 ? 8 + rnd() * 22 : 24 + rnd() * 78) * lay.hs;
            const bw = (L === 0 ? 9 + rnd() * 14 : 12 + rnd() * 22);
            const cx = Math.cos(angle) * radius, cz = Math.sin(angle) * radius;
            push(new THREE.BoxGeometry(bw, bh, bw), cx, bh / 2 - 6, cz);
            // A setback block on the taller towers so the skyline is not a bar chart.
            if (L > 0 && rnd() > 0.55) {
              const th = bh * (0.2 + rnd() * 0.3);
              push(new THREE.BoxGeometry(bw * 0.55, th, bw * 0.55), cx, bh - 6 + th / 2, cz);
            }
          }
          if (bucket.length) group.add(new THREE.Mesh(mergeGeometries(bucket, false), mat));
        }
        return;
      }

      // mountains / forested peaks, in the same three receding bands
      for (let L = 0; L < layers.length; L++) {
        const lay = layers[L];
        const bucket = [];
        const push = (geo, x, y, z) => { geo.translate(x, y, z); bucket.push(geo); };
        const tone = new THREE.Color(cfg.color).lerp(new THREE.Color(env.fog), lay.haze);
        const mat = new THREE.MeshStandardMaterial({ color: tone, roughness: 0.95, flatShading: true });
        backdropMats.push({ mat, base: new THREE.Color(cfg.color), haze: lay.haze });
        const n = Math.round(cfg.count * (L === 0 ? 0.45 : L === 1 ? 0.32 : 0.3));
        for (let m = 0; m < n; m++) {
          const angle = (m / n) * Math.PI * 2 + rnd() * 0.55;
          const radius = cfg.radius * (1 + lay.dr * 0.55) + rnd() * cfg.spread;
          const mtx = Math.cos(angle) * radius;
          const mtz = Math.sin(angle) * radius;
          if (cfg.behindOnly && L === 0 && mtz > -40) continue; // keep the bay open
          const height = (cfg.height[0] + rnd() * (cfg.height[1] - cfg.height[0])) * lay.hs;
          const widthM = 40 + rnd() * 34;
          push(new THREE.ConeGeometry(widthM, height, 5), mtx, height / 2 - 8, mtz);
          // A subsidiary shoulder off each peak. A ridge of lone cones looks like
          // a row of traffic bollards; overlapping masses look like mountains.
          if (rnd() > 0.35) {
            const sh = height * (0.45 + rnd() * 0.3);
            const off = widthM * (0.55 + rnd() * 0.4) * (rnd() > 0.5 ? 1 : -1);
            push(
              new THREE.ConeGeometry(widthM * 0.7, sh, 5),
              mtx + Math.cos(angle + Math.PI / 2) * off, sh / 2 - 8,
              mtz + Math.sin(angle + Math.PI / 2) * off
            );
          }
        }
        if (bucket.length) group.add(new THREE.Mesh(mergeGeometries(bucket, false), mat));
      }
    };
    buildBackdrop(backdropGroup, env.backdrop);
    scene.add(backdropGroup);

    // 8. (No bedrock skirt any more — the terrain no longer *ends* at the border,
    //    so a box under the sandbox would only re-introduce the straight edges
    //    this whole world-continuation exists to get rid of.)

    // 9. Main Heightmap Terrain Mesh
    const terrainGeo = new THREE.PlaneGeometry(W, H, W - 1, H - 1);
    terrainGeo.rotateX(-Math.PI / 2);

    const terrainTint = new Float32Array(W * H);
    for (let y = 0; y < H; y++) {
      for (let x = 0; x < W; x++) {
        terrainTint[y * W + x] = groundTint(-W / 2 + x * DX, -H / 2 + y * DZ);
      }
    }

    const terrainColors = new Float32Array(W * H * 3);
    terrainGeo.setAttribute("color", new THREE.BufferAttribute(terrainColors, 3));

    // Smooth-shaded, NOT faceted. Flat shading a heightmap makes each cell's two
    // triangles catch the light differently, which corrugates every slope into a
    // herringbone of ribs — very obvious on riverbanks and coasts. The low-poly
    // character lives in the props and the coarse heightmap, not in the normals.
    const terrainMat = new THREE.MeshStandardMaterial({
      vertexColors: true,
      roughness: 0.85,
      metalness: 0.05,
    });
    const terrainMesh = new THREE.Mesh(terrainGeo, terrainMat);
    terrainMesh.receiveShadow = true;
    terrainMesh.castShadow = true;
    scene.add(terrainMesh);

    // 9b. Continuous Outerland — the world does not end at the sandbox; the
    //     border is offset OUTWARD as a rounded rectangle in 40 rings that grow
    //     geometrically (first ring exactly one cell wide, so the tessellation
    //     matches the sandbox's, then coarser as it recedes past the fog).
    //
    //     Two things matter for the seam being invisible:
    //       • ring 0 sits on the sandbox's own border vertices at their exact
    //         heights, and every displacement term is multiplied by a ramp that
    //         is 0 at t=0 — so the join is smooth, not a welded crease;
    //       • it uses the identical material + colour ramp as the sandbox.
    //
    //     Offsetting along the border's *outward normal* (rather than radially
    //     from the centre, as before) is what removes the big straight-edged
    //     diamond silhouette: rings stay parallel to the edge they came from,
    //     corners round off, and a low-frequency warp makes the outline organic.
    const oc = env.outer;
    const OR = oc.rings;
    const CORNER_FAN = 7;
    const vx = (col) => -W / 2 + col * (W / (W - 1));
    const vz = (row) => -H / 2 + row * (H / (H - 1));

    const border = [];
    const addPt = (col, row, nx, nz) =>
      border.push({ x: vx(col), z: vz(row), nx, nz, i: row * W + col });
    const addCorner = (col, row, ax, az, bx, bz) => {
      for (let k = 1; k <= CORNER_FAN; k++) {
        const a = k / (CORNER_FAN + 1);
        const nx = ax + (bx - ax) * a, nz = az + (bz - az) * a;
        const l = Math.hypot(nx, nz) || 1;
        addPt(col, row, nx / l, nz / l);
      }
    };
    for (let col = 0; col < W; col++) addPt(col, 0, 0, -1);            // north edge
    addCorner(W - 1, 0, 0, -1, 1, 0);
    for (let row = 1; row < H; row++) addPt(W - 1, row, 1, 0);         // east edge
    addCorner(W - 1, H - 1, 1, 0, 0, 1);
    for (let col = W - 2; col >= 0; col--) addPt(col, H - 1, 0, 1);    // south edge
    addCorner(0, H - 1, 0, 1, -1, 0);
    for (let row = H - 2; row >= 1; row--) addPt(0, row, -1, 0);       // west edge
    addCorner(0, 0, -1, 0, 0, -1);
    const P = border.length;

    // Geometric ring spacing: step0 units at the border, reaching `reach` total.
    const ringD = new Float32Array(OR + 1);
    {
      let lo = 1.0001, hi = 2.0;
      for (let it = 0; it < 80; it++) {
        const r = (lo + hi) / 2;
        const s = (oc.step0 * (Math.pow(r, OR) - 1)) / (r - 1);
        if (s < oc.reach) lo = r; else hi = r;
      }
      const r = (lo + hi) / 2;
      let d = 0;
      for (let j = 0; j <= OR; j++) { ringD[j] = d; d += oc.step0 * Math.pow(r, j); }
    }
    // How strongly each ring follows a live edit of the sandbox border.
    const ringFollow = new Float32Array(OR + 1);
    for (let j = 0; j <= OR; j++) ringFollow[j] = 1 - _smooth(0, 10, ringD[j]);

    const outPos = new Float32Array((OR + 1) * P * 3);
    const outCol = new Float32Array((OR + 1) * P * 3);
    const outBaseY = new Float32Array((OR + 1) * P); // pristine heights (pre-edit)
    const outBaseCol = new Float32Array((OR + 1) * P * 3); // dry colours, pre-wetness
    const outerBorderSurf = new Float32Array(P); // sim water level along the border
    const baseE0 = new Float32Array(P);

    const buildOuter = () => {
      const elev0 = simRef.current ? simRef.current.elev : null;
      for (let p = 0; p < P; p++) {
        const b = border[p];
        const e0 = elev0 ? elev0[b.i] : 0;
        baseE0[p] = e0;
        // Is this stretch of border dry land or already water? Decides whether the
        // world continues as coast+ocean or as inland country.
        const landiness = _smooth(-0.15, 1.5, e0);
        // Low-frequency warp of the offset distance → organic outline, no rectangle.
        const warp =
          1 +
          (_fbm(b.x * 0.007 + 3.3, b.z * 0.007 + 7.7) - 0.5) * 0.6 +
          (_fbm(b.x * 0.021 + 19.1, b.z * 0.021 + 4.3) - 0.5) * 0.24;

        for (let j = 0; j <= OR; j++) {
          const t = j / OR;
          // Warp fades out far away so distant rings can never fold over each other.
          const dEff = ringD[j] * (1 + (warp - 1) * (1 - _smooth(0.3, 0.95, t)));
          const wx = b.x + b.nx * dEff;
          const wz = b.z + b.nz * dEff;

          let far;
          if (oc.sea) {
            const landFar = Math.max(e0 * 0.55, 0.7) + oc.rise * 0.45;
            far = oc.seabed + (landFar - oc.seabed) * landiness;
          } else {
            // Inland maps open DOWNWARD into lower country, so the surrounding
            // land can never rise up and occlude the play area — and so a river
            // leaving the map keeps its channel instead of being filled in.
            far = e0 * 0.45;
          }

          let h = e0 + (far - e0) * _smooth(0, 1, t);
          const nr = _smooth(0, 0.16, t); // every wrinkle starts at zero on the seam
          h += (_fbm(wx * oc.freq, wz * oc.freq) - 0.5) * oc.amp * nr;
          h += (_fbm(wx * oc.freq * 3.1 + 31.7, wz * oc.freq * 3.1 + 13.3) - 0.5) * oc.amp * 0.3 * nr;
          h += (_fbm(wx * oc.freq * 0.33 + 5.1, wz * oc.freq * 0.33 + 9.3) - 0.5) * oc.rise * _smooth(0.04, 0.6, t);
          // Extra relief concentrated in the band where the land crosses sea
          // level: without it the coast tracks the border's own elevation and so
          // runs in long straight lines parallel to the map edge. This is what
          // turns it into bays, spits and offshore sandbars.
          h += (_fbm(wx * oc.freq * 1.7 + 61.3, wz * oc.freq * 1.7 + 45.9) - 0.5) *
            oc.amp * 1.5 * _smooth(0.02, 0.14, t) * (1 - _smooth(0.34, 0.8, t));

          const vi = (j * P + p) * 3;
          outPos[vi] = wx; outPos[vi + 1] = h; outPos[vi + 2] = wz;
          outBaseY[j * P + p] = h;
          const cc = colorFor(h, env.colors);
          const tn = groundTint(wx, wz);
          outBaseCol[vi] = cc[0] + tn;
          outBaseCol[vi + 1] = cc[1] + tn * 0.92;
          outBaseCol[vi + 2] = cc[2] + tn * 0.78;
          outCol[vi] = outBaseCol[vi]; outCol[vi + 1] = outBaseCol[vi + 1]; outCol[vi + 2] = outBaseCol[vi + 2];
        }
      }
    };
    buildOuter();

    const outIdx = [];
    for (let j = 0; j < OR; j++) {
      for (let p = 0; p < P; p++) {
        const pn = (p + 1) % P;
        const a = j * P + p, b = j * P + pn, c = (j + 1) * P + p, d = (j + 1) * P + pn;
        outIdx.push(a, c, b, b, c, d);
      }
    }
    const outGeo = new THREE.BufferGeometry();
    outGeo.setAttribute("position", new THREE.BufferAttribute(outPos, 3));
    outGeo.setAttribute("color", new THREE.BufferAttribute(outCol, 3));
    outGeo.setIndex(outIdx);
    outGeo.computeVertexNormals();
    // Deliberately identical to the sandbox terrain material below — same
    // roughness, same faceting — so no lighting break betrays the boundary.
    const outMat = new THREE.MeshStandardMaterial({
      vertexColors: true,
      roughness: 0.85,
      metalness: 0.05,
      side: THREE.DoubleSide,
    });
    const outerMesh = new THREE.Mesh(outGeo, outMat);
    // Deliberately NOT receiveShadow: the shadow frustum only covers the play
    // area, and sampling it out here paints a hard straight frustum edge across
    // the landscape — the exact kind of artificial line we're removing.
    outerMesh.receiveShadow = false;
    scene.add(outerMesh);

    // 9c. Open-Water Sheet — the water OUTSIDE the sandbox, laid on the very
    //     same ring mesh as the outerland and shaded by depth with the identical
    //     shallow→deep rule the play area's water uses. That is what makes the
    //     shoreline continue past the border: pale shallows over the beach,
    //     darkening as the seabed drops away, all the way into the fog.
    const owCol = new Float32Array((OR + 1) * P * 4);
    const owPos = new Float32Array((OR + 1) * P * 3);
    for (let k = 0; k < (OR + 1) * P; k++) {
      owPos[k * 3] = outPos[k * 3];
      owPos[k * 3 + 1] = SEA_LEVEL;
      owPos[k * 3 + 2] = outPos[k * 3 + 2];
      const depth = SEA_LEVEL - outBaseY[k];
      const kk = _smooth(0.02, 1.7, depth);
      const wS = env.water.shallow, wD = env.water.deep;
      owCol[k * 4] = wS[0] + (wD[0] - wS[0]) * kk;
      owCol[k * 4 + 1] = wS[1] + (wD[1] - wS[1]) * kk;
      owCol[k * 4 + 2] = wS[2] + (wD[2] - wS[2]) * kk;
      owCol[k * 4 + 3] = depth <= 0 ? 0 : 0.25 + 0.7 * kk;
    }
    const owGeo = new THREE.BufferGeometry();
    owGeo.setAttribute("position", new THREE.BufferAttribute(owPos, 3));
    owGeo.setAttribute("color", new THREE.BufferAttribute(owCol, 4));
    owGeo.setIndex(outIdx);
    owGeo.computeVertexNormals();
    const owMat = new THREE.MeshStandardMaterial({
      vertexColors: true,
      transparent: true,
      roughness: 0.12,
      metalness: 0.2,
      depthWrite: false,
      side: THREE.DoubleSide,
    });
    const outerWater = new THREE.Mesh(owGeo, owMat);
    outerWater.renderOrder = 1;
    scene.add(outerWater);

    // The swell is a sum of three plane waves, so each term splits into
    // sin(kx)cos(wt) +/- cos(kx)sin(wt). Baking the per-vertex sin/cos of the spatial
    // part once means the per-frame cost is six multiply-adds instead of three
    // sin() calls across ~20k vertices.
    // A smoothed copy of the surrounding ground, used ONLY as the base for rain
    // lying on it. Draping the flood straight over the real heightfield makes the
    // water surface as bumpy as the land, and the ring mesh coarsens with distance,
    // so that showed up as concentric terraces. Blur is faded in from ring 0 so the
    // border still matches the play area's own water level exactly.
    const outSmoothY = new Float32Array((OR + 1) * P);
    const smoothOuter = () => {
      outSmoothY.set(outBaseY);
      const tmp = new Float32Array((OR + 1) * P);
      for (let pass = 0; pass < 5; pass++) {
        tmp.set(outSmoothY);
        for (let j = 0; j <= OR; j++) {
          const w = _smooth(0, 6, j); // ring 0 stays untouched
          if (w <= 0) continue;
          for (let p = 0; p < P; p++) {
            const k = j * P + p;
            const avg =
              (tmp[k] +
                tmp[j * P + ((p + 1) % P)] +
                tmp[j * P + ((p - 1 + P) % P)] +
                tmp[Math.max(j - 1, 0) * P + p] +
                tmp[Math.min(j + 1, OR) * P + p]) / 5;
            outSmoothY[k] = tmp[k] + (avg - tmp[k]) * w;
          }
        }
      }
    };
    smoothOuter();

    const owTint = new Float32Array((OR + 1) * P);
    for (let k = 0; k < (OR + 1) * P; k++) owTint[k] = groundTint(owPos[k * 3], owPos[k * 3 + 2]);

    const owBasis = new Float32Array((OR + 1) * P * 6);
    // Past ~120u the water is deep in fog; displacing it there costs frames and
    // shows nothing, so the swell stops at that ring.
    let owSwellRings = OR;
    for (let j = 0; j <= OR; j++) if (ringD[j] <= 120) owSwellRings = j;
    for (let k = 0; k < (OR + 1) * P; k++) {
      const wx = owPos[k * 3], wz = owPos[k * 3 + 2];
      owBasis[k * 6] = Math.sin(wx * 0.085);
      owBasis[k * 6 + 1] = Math.cos(wx * 0.085);
      owBasis[k * 6 + 2] = Math.sin(wz * 0.061);
      owBasis[k * 6 + 3] = Math.cos(wz * 0.061);
      owBasis[k * 6 + 4] = Math.sin((wx + wz) * 0.155);
      owBasis[k * 6 + 5] = Math.cos((wx + wz) * 0.155);
    }

    // ── Weather state shared by the sandbox and the world around it ──────────
    // `wetness` drives how dark/slick every ground surface looks; `swellAmp`
    // drives the one water displacement both sheets use. The surrounding world is
    // purely presentational — it is never raycast, so it stays non-interactive.
    let wetness = 0;
    let lastWetness = -1;
    let swellAmp = 0;
    let owWasAnimating = false;
    let owColDirty = true;
    let lastActive = 0;
    // Scratch for the water-sheet dilation, and how deep the play area's rainwater
    // is on average — the figure the surrounding world floods to.
    const wSurf = new Float32Array(W * H);
    const wSurfTmp = new Float32Array(W * H);
    let bgDepth = 0;
    const boatAnchors = [];
    // How quickly the open water relaxes from the sandbox's own water level (ring 0)
    // out to the global sea level — this is what carries a storm surge offshore.
    const waterRelax = new Float32Array(OR + 1);
    for (let j = 0; j <= OR; j++) waterRelax[j] = _smooth(0, 0.22, j / OR);

    // Terraforming a border cell must drag the surrounding world with it, or the
    // seam tears open. Cheap: shift the first ~10 units of rings by the delta.
    let lastPerimSum = NaN;
    const reweldOuter = (elev) => {
      for (let p = 0; p < P; p++) {
        const de = elev[border[p].i] - baseE0[p];
        for (let j = 0; j <= OR; j++) {
          const f = ringFollow[j];
          if (f <= 0) break;
          const k = j * P + p;
          const h = outBaseY[k] + de * f;
          outPos[k * 3 + 1] = h;
          const cc = colorFor(h, env.colors);
          const tn = groundTint(outPos[k * 3], outPos[k * 3 + 2]);
          outBaseCol[k * 3] = cc[0] + tn;
          outBaseCol[k * 3 + 1] = cc[1] + tn * 0.92;
          outBaseCol[k * 3 + 2] = cc[2] + tn * 0.78;
          outCol[k * 3] = outBaseCol[k * 3];
          outCol[k * 3 + 1] = outBaseCol[k * 3 + 1];
          outCol[k * 3 + 2] = outBaseCol[k * 3 + 2];
        }
      }
      outGeo.attributes.position.needsUpdate = true;
      outGeo.attributes.color.needsUpdate = true;
      outGeo.computeVertexNormals();
      lastWetness = -1; // colours were just reset to dry — re-apply the wet tint
      owColDirty = true; // the seabed moved, so open-water depths did too
      smoothOuter();
    };

    // 10. Water Surface Mesh Setup — a 4-component colour attribute gives every
    //     vertex its own alpha, so puddles stay glassy while deep water turns
    //     opaque and matches the open-water sheet exactly at the border.
    const waterGeo = new THREE.PlaneGeometry(W, H, W - 1, H - 1);
    waterGeo.rotateX(-Math.PI / 2);

    const waterColors = new Float32Array(W * H * 4);
    waterGeo.setAttribute("color", new THREE.BufferAttribute(waterColors, 4));

    const waterMat = new THREE.MeshStandardMaterial({
      vertexColors: true,
      transparent: true,
      roughness: 0.12,
      metalness: 0.2,
      depthWrite: false,
    });
    {
      const wn = waterGeo.attributes.normal.array;
      for (let k = 0; k < wn.length; k += 3) { wn[k] = 0; wn[k + 1] = 1; wn[k + 2] = 0; }
    }
    const waterMesh = new THREE.Mesh(waterGeo, waterMat);
    waterMesh.renderOrder = 2;
    scene.add(waterMesh);

    // 11. Dynamic 3D Rain Particles System
    // Rain has to fall on the WHOLE world. Confining it to a box around the play
    // area meant that during a downpour the rain itself outlined the sandbox.
    const RAIN_SPAN = 300;
    const RAIN_TOP = 70;
    const rainCount = 9000;
    const rainGeo = new THREE.BufferGeometry();
    const rainPositions = new Float32Array(rainCount * 6);
    for (let i = 0; i < rainCount; i++) {
      const rx = (Math.random() - 0.5) * RAIN_SPAN;
      const ry = Math.random() * RAIN_TOP;
      const rz = (Math.random() - 0.5) * RAIN_SPAN;

      rainPositions[i * 6] = rx;
      rainPositions[i * 6 + 1] = ry;
      rainPositions[i * 6 + 2] = rz;
      rainPositions[i * 6 + 3] = rx - 0.4;
      rainPositions[i * 6 + 4] = ry - 1.6;
      rainPositions[i * 6 + 5] = rz;
    }
    rainGeo.setAttribute("position", new THREE.BufferAttribute(rainPositions, 3));
    const rainLinesMat = new THREE.LineBasicMaterial({
      color: 0xa8d0f5,
      transparent: true,
      opacity: 0.65,
    });
    const rainLines = new THREE.LineSegments(rainGeo, rainLinesMat);
    rainLines.renderOrder = 3;
    scene.add(rainLines);

    // 13. Interactive Brush Cursor Ring
    const ringGeo = new THREE.RingGeometry(1.2, 1.6, 24);
    ringGeo.rotateX(-Math.PI / 2);
    const ringMat = new THREE.MeshBasicMaterial({
      color: 0xd9442b,
      side: THREE.DoubleSide,
      transparent: true,
      opacity: 0.85,
    });
    const brushRing = new THREE.Mesh(ringGeo, ringMat);
    brushRing.visible = false;
    scene.add(brushRing);

    // 14. Low-Poly Props Groups
    const mangGroup = new THREE.Group();
    const drnGroup = new THREE.Group();
    const hseGroup = new THREE.Group();
    const propsGroup = new THREE.Group();

    scene.add(mangGroup);
    scene.add(drnGroup);
    scene.add(hseGroup);
    scene.add(propsGroup);

    // ── Reusable Materials ───────────────────────────────────────────────────
    // Every surface the barangay is built from gets its own material rather than
    // being folded into a generic "wood" or "wall": thatch, sawali matting,
    // painted hollow-block and corrugated GI sheet are what make a Filipino
    // village legible as one, and at this scale the eye reads material before
    // it reads shape.
    const bambooMat = new THREE.MeshStandardMaterial({ color: 0x9a6b45, roughness: 0.9 });
    const nipaMat = new THREE.MeshStandardMaterial({ color: 0xc49a45, roughness: 0.95 });
    const nipaDarkMat = new THREE.MeshStandardMaterial({ color: 0x9d7530, roughness: 0.95 });
    const sawaliMat = new THREE.MeshStandardMaterial({ color: 0xe6cd9c, roughness: 0.8 });
    // Glass is dark and a touch emissive so windows still read as windows once
    // fog and distance have flattened everything else out.
    const glassMat = new THREE.MeshStandardMaterial({
      color: 0x2c3f4d, roughness: 0.25, metalness: 0.1, emissive: 0x16242e, emissiveIntensity: 0.6,
    });
    const trimMat = new THREE.MeshStandardMaterial({ color: 0xf7f3e8, roughness: 0.75 });
    const concreteMat = new THREE.MeshStandardMaterial({ color: 0xbfb8ab, roughness: 0.95 });
    const rustMat = new THREE.MeshStandardMaterial({ color: 0x9c5b3c, roughness: 0.95 });
    const tarpMat = new THREE.MeshStandardMaterial({ color: 0x2f7fb5, roughness: 0.7 });

    const houseRoofColors = [0xd9442b, 0x4a90e2, 0x5b8c3a, 0xe07a5f, 0x9b59b6, 0xe8a33d, 0x3f8f86];
    const houseWallColors = [0xf4f1de, 0xe0e1dd, 0xfceade, 0xe2ece9, 0xfff1e6, 0xf6e7c9, 0xe8eef3];

    const drainBoxMat = new THREE.MeshStandardMaterial({ color: 0x5c6770 });
    const drainGrateMat = new THREE.MeshBasicMaterial({ color: 0x3ba99c, side: THREE.DoubleSide });
    const warningFlagMat = new THREE.MeshBasicMaterial({ color: 0xd9442b });

    const woodMat = new THREE.MeshStandardMaterial({ color: 0x5c4033 });
    const palmLeafMat = new THREE.MeshStandardMaterial({ color: 0x477a3d, roughness: 0.6 });
    const leafMidMat = new THREE.MeshStandardMaterial({ color: 0x3d6b34, roughness: 0.75 });
    const leafLightMat = new THREE.MeshStandardMaterial({ color: 0x6a9b4a, roughness: 0.8 });
    const bananaMat = new THREE.MeshStandardMaterial({ color: 0x5c9440, roughness: 0.7 });
    const rockMat = new THREE.MeshStandardMaterial({ color: 0x8d8b82, roughness: 1.0, flatShading: true });

    // ── World scale ──────────────────────────────────────────────────────────
    // One terrain cell is one world unit, and the map is 96×64 cells. The props
    // used to be built at ~0.75u — SMALLER than the cell they stand on — so from
    // the isometric camera (~72u out) a house covered about a dozen pixels and
    // the whole barangay read as confetti sprinkled on a bedsheet. Treating a
    // cell as roughly 4 m puts a 10 m house at ~2.5u, which is the proportion
    // everything below is now built to.

    // ── Barangay Building Creators ───────────────────────────────────────────
    const createNipaHut = (variant = 0) => {
      const g = new THREE.Group();
      const thatch = variant % 2 ? nipaDarkMat : nipaMat;

      // Stilts, with the cross-bracing that actually keeps a bahay kubo standing.
      // Flood water running *under* a house is the whole point of the building
      // type, so the gap beneath the floor has to be visible.
      for (const sx of [-0.72, 0.72]) {
        for (const sz of [-0.72, 0.72]) {
          const stilt = new THREE.Mesh(new THREE.CylinderGeometry(0.1, 0.13, 1.0, 6), bambooMat);
          stilt.position.set(sx, 0.5, sz);
          stilt.castShadow = true;
          g.add(stilt);
        }
      }
      for (const sz of [-0.72, 0.72]) {
        const brace = new THREE.Mesh(new THREE.BoxGeometry(1.5, 0.07, 0.07), bambooMat);
        brace.position.set(0, 0.72, sz);
        g.add(brace);
      }

      // Floor platform, deliberately wider than the walls so it reads as a deck.
      const floor = new THREE.Mesh(new THREE.BoxGeometry(1.95, 0.12, 1.95), bambooMat);
      floor.position.y = 1.03;
      floor.castShadow = true;
      g.add(floor);

      // Sawali-walled body with corner posts.
      const body = new THREE.Mesh(new THREE.BoxGeometry(1.65, 1.15, 1.65), sawaliMat);
      body.position.y = 1.68;
      body.castShadow = true;
      g.add(body);
      for (const sx of [-0.8, 0.8]) {
        for (const sz of [-0.8, 0.8]) {
          const post = new THREE.Mesh(new THREE.BoxGeometry(0.11, 1.2, 0.11), bambooMat);
          post.position.set(sx, 1.68, sz);
          g.add(post);
        }
      }

      // Shuttered window on two sides, propped open the way a real kubo's is.
      for (const [wx, wz, ry] of [[0, 0.84, 0], [0.84, 0, Math.PI / 2]]) {
        const win = new THREE.Mesh(new THREE.BoxGeometry(0.6, 0.5, 0.06), glassMat);
        win.position.set(wx, 1.78, wz);
        win.rotation.y = ry;
        g.add(win);
        const shutter = new THREE.Mesh(new THREE.BoxGeometry(0.66, 0.05, 0.42), bambooMat);
        shutter.position.set(wx * 1.22, 2.06, wz * 1.22);
        shutter.rotation.y = ry;
        shutter.rotation.x = ry ? 0 : -0.5;
        shutter.rotation.z = ry ? -0.5 : 0;
        g.add(shutter);
      }

      // Two stacked cones: a steep thatch cap over a shallower flared eave. One
      // cone alone reads as a party hat; the break in the slope is what makes it
      // look like layered nipa shingles.
      const eave = new THREE.Mesh(new THREE.ConeGeometry(1.62, 0.5, 4), thatch);
      eave.position.y = 2.44;
      eave.rotation.y = Math.PI / 4;
      eave.castShadow = true;
      g.add(eave);
      const cap = new THREE.Mesh(new THREE.ConeGeometry(1.2, 1.0, 4), thatch);
      cap.position.y = 2.95;
      cap.rotation.y = Math.PI / 4;
      cap.castShadow = true;
      g.add(cap);
      const ridge = new THREE.Mesh(new THREE.BoxGeometry(0.16, 0.16, 0.16), bambooMat);
      ridge.position.y = 3.45;
      g.add(ridge);

      // Ladder up to the deck — the detail that sells the height of the stilts.
      const ladderX = 0;
      for (const lx of [-0.22, 0.22]) {
        const rail = new THREE.Mesh(new THREE.CylinderGeometry(0.05, 0.05, 1.3, 5), bambooMat);
        rail.position.set(ladderX + lx, 0.55, 1.28);
        rail.rotation.x = 0.28;
        g.add(rail);
      }
      for (let r = 0; r < 3; r++) {
        const rung = new THREE.Mesh(new THREE.BoxGeometry(0.44, 0.05, 0.05), bambooMat);
        rung.position.set(ladderX, 0.25 + r * 0.34, 1.4 - r * 0.1);
        g.add(rung);
      }

      return g;
    };

    const createTownhouse = (id) => {
      const g = new THREE.Group();
      const wallColor = houseWallColors[id % houseWallColors.length];
      const roofColor = houseRoofColors[id % houseRoofColors.length];

      const wallMat = new THREE.MeshStandardMaterial({ color: wallColor, roughness: 0.7 });
      const roofMat = new THREE.MeshStandardMaterial({ color: roofColor, roughness: 0.6 });

      // Low plinth: concrete houses here sit on a raised slab, and it gives the
      // silhouette a base instead of a box floating on the grass.
      const plinth = new THREE.Mesh(new THREE.BoxGeometry(2.35, 0.22, 2.35), concreteMat);
      plinth.position.y = 0.11;
      plinth.receiveShadow = true;
      g.add(plinth);

      const body = new THREE.Mesh(new THREE.BoxGeometry(2.1, 1.5, 2.1), wallMat);
      body.position.y = 0.97;
      body.castShadow = true;
      g.add(body);

      // Roof with a real overhang, plus a ridge cap. The overhang is what makes
      // a hipped roof read as a roof rather than as a cone stuck on a cube.
      const roof = new THREE.Mesh(new THREE.ConeGeometry(1.85, 0.95, 4), roofMat);
      roof.position.y = 2.18;
      roof.rotation.y = Math.PI / 4;
      roof.castShadow = true;
      g.add(roof);
      const fascia = new THREE.Mesh(new THREE.BoxGeometry(2.3, 0.1, 2.3), roofMat);
      fascia.position.y = 1.75;
      g.add(fascia);

      // Door with frame and a step down to the ground.
      const door = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.9, 0.08), woodMat);
      door.position.set(0, 0.67, 1.06);
      g.add(door);
      const jamb = new THREE.Mesh(new THREE.BoxGeometry(0.64, 1.02, 0.05), trimMat);
      jamb.position.set(0, 0.7, 1.03);
      g.add(jamb);
      const step = new THREE.Mesh(new THREE.BoxGeometry(0.7, 0.1, 0.3), concreteMat);
      step.position.set(0, 0.16, 1.28);
      g.add(step);

      // Windows on three faces, each with a sill — three lit rectangles is the
      // cheapest thing that turns a blank cube into an inhabited house.
      const winSpots = [
        [-0.62, 1.06, 0], [0.62, 1.06, 0],
        [1.06, 0, Math.PI / 2], [-1.06, 0, Math.PI / 2],
      ];
      for (const [a, b, ry] of winSpots) {
        const x = ry ? a : a, z = ry ? b : b;
        const win = new THREE.Mesh(new THREE.BoxGeometry(0.52, 0.5, 0.06), glassMat);
        win.position.set(x, 1.15, z);
        win.rotation.y = ry;
        g.add(win);
        const sill = new THREE.Mesh(new THREE.BoxGeometry(0.62, 0.07, 0.12), trimMat);
        sill.position.set(x, 0.87, z);
        sill.rotation.y = ry;
        g.add(sill);
      }

      // Rooftop water drum — near-universal on Philippine houses and a great
      // little silhouette-breaker against the sky.
      const tank = new THREE.Mesh(new THREE.CylinderGeometry(0.22, 0.22, 0.36, 8), tarpMat);
      tank.position.set(0.62, 2.0, -0.55);
      g.add(tank);

      return g;
    };

    const createUrbanApartment = (variant = 0) => {
      const g = new THREE.Group();
      const wallTones = [0x8d97a1, 0xa8a294, 0x9fb0ad, 0xb0a6a0];
      const wallMat = new THREE.MeshStandardMaterial({
        color: wallTones[variant % wallTones.length], roughness: 0.75,
      });
      const shopMat = new THREE.MeshStandardMaterial({ color: 0xd9663a, roughness: 0.7 });

      // Three storeys instead of one cube. The old apartment was a 0.9u box with
      // a near-black roof slab, which at map scale rendered as a black speck —
      // height and banded floors are what make it read as a building at all.
      const storeys = 3 + (variant % 2);
      const sh = 1.05;

      // Ground floor is a shopfront: the mixed-use ground level is what a Metro
      // Manila street actually looks like, and the warm colour lifts the whole
      // grey palette off the grey ground.
      const shop = new THREE.Mesh(new THREE.BoxGeometry(2.4, sh, 2.4), shopMat);
      shop.position.y = sh / 2;
      shop.castShadow = true;
      g.add(shop);
      const shopGlass = new THREE.Mesh(new THREE.BoxGeometry(1.7, 0.6, 0.06), glassMat);
      shopGlass.position.set(0, 0.55, 1.22);
      g.add(shopGlass);
      const shopAwn = new THREE.Mesh(new THREE.BoxGeometry(2.5, 0.08, 0.55), tarpMat);
      shopAwn.position.set(0, 1.0, 1.35);
      shopAwn.rotation.x = 0.22;
      g.add(shopAwn);

      for (let s = 1; s < storeys; s++) {
        const y = sh * s + sh / 2;
        const floor = new THREE.Mesh(new THREE.BoxGeometry(2.4, sh, 2.4), wallMat);
        floor.position.y = y;
        floor.castShadow = true;
        g.add(floor);
        // Slab edge between floors — a horizontal line per storey is the single
        // clearest cue for "this is a multi-storey building".
        const band = new THREE.Mesh(new THREE.BoxGeometry(2.56, 0.1, 2.56), concreteMat);
        band.position.y = sh * s;
        g.add(band);
        // Two windows per visible face.
        for (const [ox, oz, ry] of [[0, 1.22, 0], [1.22, 0, Math.PI / 2], [-1.22, 0, Math.PI / 2], [0, -1.22, 0]]) {
          for (const off of [-0.52, 0.52]) {
            const win = new THREE.Mesh(new THREE.BoxGeometry(0.62, 0.55, 0.06), glassMat);
            win.position.set(ox || off, y + 0.06, oz || off);
            win.rotation.y = ry;
            g.add(win);
          }
        }
        // Balcony rail on the street face.
        const rail = new THREE.Mesh(new THREE.BoxGeometry(2.4, 0.32, 0.08), concreteMat);
        rail.position.set(0, y - 0.32, 1.26);
        g.add(rail);
      }

      // Roof: parapet, water tanks and an aerial. Rooftop clutter is what stops
      // a stack of boxes from ending in a dead flat plane.
      const roofTop = sh * storeys;
      const parapet = new THREE.Mesh(new THREE.BoxGeometry(2.56, 0.26, 2.56), concreteMat);
      parapet.position.y = roofTop + 0.13;
      parapet.castShadow = true;
      g.add(parapet);
      for (const tx of [-0.6, 0.1]) {
        const tank = new THREE.Mesh(new THREE.CylinderGeometry(0.26, 0.26, 0.5, 8), tarpMat);
        tank.position.set(tx, roofTop + 0.5, -0.5);
        g.add(tank);
      }
      const mast = new THREE.Mesh(new THREE.CylinderGeometry(0.03, 0.03, 1.1, 4), drainBoxMat);
      mast.position.set(0.8, roofTop + 0.8, 0.7);
      g.add(mast);

      return g;
    };

    const createSariSariStore = () => {
      const g = new THREE.Group();
      const wallMat = new THREE.MeshStandardMaterial({ color: 0xf4a261, roughness: 0.7 });
      const awningMat = new THREE.MeshStandardMaterial({ color: 0xe76f51, roughness: 0.5 });

      const plinth = new THREE.Mesh(new THREE.BoxGeometry(2.3, 0.18, 2.3), concreteMat);
      plinth.position.y = 0.09;
      g.add(plinth);

      const body = new THREE.Mesh(new THREE.BoxGeometry(2.05, 1.5, 2.05), wallMat);
      body.position.y = 0.93;
      body.castShadow = true;
      g.add(body);

      // Corrugated GI roof, slightly pitched — the flat-topped version read as an
      // unfinished box.
      const roof = new THREE.Mesh(new THREE.BoxGeometry(2.4, 0.12, 2.4), rustMat);
      roof.position.y = 1.72;
      roof.rotation.z = 0.09;
      roof.castShadow = true;
      g.add(roof);

      // The counter grille: a sari-sari store IS its serving window, so it gets
      // the biggest single detail on the building.
      const counterHole = new THREE.Mesh(new THREE.BoxGeometry(1.3, 0.72, 0.08), glassMat);
      counterHole.position.set(0, 1.1, 1.04);
      g.add(counterHole);
      const counter = new THREE.Mesh(new THREE.BoxGeometry(1.5, 0.14, 0.36), woodMat);
      counter.position.set(0, 0.72, 1.14);
      g.add(counter);
      for (let b = 0; b < 5; b++) {
        const bar = new THREE.Mesh(new THREE.BoxGeometry(0.05, 0.72, 0.05), drainBoxMat);
        bar.position.set(-0.52 + b * 0.26, 1.1, 1.09);
        g.add(bar);
      }

      // Deep awning on posts + hanging sachet strips, the signature of the shop.
      const awning = new THREE.Mesh(new THREE.BoxGeometry(2.5, 0.09, 1.15), awningMat);
      awning.position.set(0, 1.66, 1.62);
      awning.rotation.x = 0.2;
      awning.castShadow = true;
      g.add(awning);
      for (const px of [-1.1, 1.1]) {
        const post = new THREE.Mesh(new THREE.CylinderGeometry(0.06, 0.06, 1.5, 5), bambooMat);
        post.position.set(px, 0.75, 2.08);
        g.add(post);
      }
      for (let s = 0; s < 6; s++) {
        const strip = new THREE.Mesh(new THREE.BoxGeometry(0.14, 0.5, 0.03), trimMat);
        strip.position.set(-0.85 + s * 0.34, 1.32, 1.12);
        g.add(strip);
      }

      // Signboard across the top.
      const sign = new THREE.Mesh(new THREE.BoxGeometry(1.9, 0.4, 0.07), trimMat);
      sign.position.set(0, 1.58, 1.06);
      g.add(sign);

      // Crates and a cooler out front.
      for (let c = 0; c < 3; c++) {
        const crate = new THREE.Mesh(new THREE.BoxGeometry(0.34, 0.3, 0.34), woodMat);
        crate.position.set(-0.9 + c * 0.36, 0.24 + (c === 1 ? 0.3 : 0), 1.75);
        g.add(crate);
      }
      const cooler = new THREE.Mesh(new THREE.BoxGeometry(0.44, 0.62, 0.4), tarpMat);
      cooler.position.set(0.85, 0.4, 1.72);
      g.add(cooler);

      return g;
    };

    const createBarangayHall = () => {
      const g = new THREE.Group();
      const hallWallMat = new THREE.MeshStandardMaterial({ color: 0x4a90e2, roughness: 0.6 });
      const hallRoofMat = new THREE.MeshStandardMaterial({ color: 0x2b3a4b, roughness: 0.5 });

      // The civic building should be the landmark of the barangay: widest
      // footprint, a portico, and a flag. If everything is the same size there is
      // nothing for the eye to anchor on.
      const plinth = new THREE.Mesh(new THREE.BoxGeometry(4.0, 0.3, 3.0), concreteMat);
      plinth.position.y = 0.15;
      plinth.receiveShadow = true;
      g.add(plinth);

      const body = new THREE.Mesh(new THREE.BoxGeometry(3.5, 1.7, 2.6), hallWallMat);
      body.position.y = 1.15;
      body.castShadow = true;
      g.add(body);

      const roof = new THREE.Mesh(new THREE.ConeGeometry(2.75, 1.05, 4), hallRoofMat);
      roof.position.y = 2.5;
      roof.rotation.y = Math.PI / 4;
      roof.castShadow = true;
      g.add(roof);
      const fascia = new THREE.Mesh(new THREE.BoxGeometry(3.9, 0.12, 3.0), hallRoofMat);
      fascia.position.y = 2.02;
      g.add(fascia);

      // Portico: four columns and a canopy over the entrance.
      const canopy = new THREE.Mesh(new THREE.BoxGeometry(2.4, 0.14, 1.1), trimMat);
      canopy.position.set(0, 1.75, 1.75);
      g.add(canopy);
      for (const cx of [-1.0, -0.34, 0.34, 1.0]) {
        const col = new THREE.Mesh(new THREE.CylinderGeometry(0.1, 0.12, 1.6, 8), trimMat);
        col.position.set(cx, 0.95, 2.15);
        col.castShadow = true;
        g.add(col);
      }
      const doors = new THREE.Mesh(new THREE.BoxGeometry(1.0, 1.15, 0.08), glassMat);
      doors.position.set(0, 0.9, 1.32);
      g.add(doors);
      for (let s = 0; s < 2; s++) {
        const step = new THREE.Mesh(new THREE.BoxGeometry(2.2, 0.12, 0.3 + s * 0.2), concreteMat);
        step.position.set(0, 0.06 + s * 0.0, 2.5 + s * 0.28);
        step.position.y = 0.24 - s * 0.12;
        g.add(step);
      }

      // Windows down both long faces.
      for (const wx of [-1.2, -0.4, 0.4, 1.2]) {
        const win = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.72, 0.06), glassMat);
        win.position.set(wx, 1.3, 1.32);
        g.add(win);
      }
      for (const wz of [-0.7, 0.4]) {
        for (const sx of [-1.77, 1.77]) {
          const win = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.72, 0.06), glassMat);
          win.position.set(sx, 1.3, wz);
          win.rotation.y = Math.PI / 2;
          g.add(win);
        }
      }

      // Flagpole with a flag — the one vertical accent in the whole village.
      const pole = new THREE.Mesh(new THREE.CylinderGeometry(0.045, 0.055, 3.4, 6), trimMat);
      pole.position.set(-2.35, 1.7, 1.9);
      g.add(pole);
      const flagCloth = new THREE.Mesh(new THREE.BoxGeometry(0.75, 0.45, 0.03), warningFlagMat);
      flagCloth.position.set(-1.95, 3.15, 1.9);
      g.add(flagCloth);

      // Sandbag line across the front — the flood-defence read, kept from before
      // but now at a size where the individual bags are visible.
      const sandbagMat = new THREE.MeshStandardMaterial({ color: 0xd9c5a0, roughness: 0.95 });
      for (let row = 0; row < 2; row++) {
        for (let bx = -1.6; bx <= 1.6; bx += 0.42) {
          const bag = new THREE.Mesh(new THREE.BoxGeometry(0.38, 0.17, 0.22), sandbagMat);
          bag.position.set(bx + (row ? 0.2 : 0), 0.09 + row * 0.17, 2.95);
          g.add(bag);
        }
      }

      return g;
    };

    // The flood warning flag is a shared prop rather than a per-house cone: one
    // pole plus a cloth reads far better than a floating triangle, and building
    // it once keeps the per-house mesh count down.
    const createWarningFlag = () => {
      const g = new THREE.Group();
      const pole = new THREE.Mesh(new THREE.CylinderGeometry(0.035, 0.035, 1.5, 5), trimMat);
      pole.position.y = 0.75;
      g.add(pole);
      const cloth = new THREE.Mesh(new THREE.BoxGeometry(0.62, 0.38, 0.03), warningFlagMat);
      cloth.position.set(0.31, 1.3, 0);
      g.add(cloth);
      return g;
    };

    // Collapse a multi-part prop into ONE mesh with a material group per material.
    // A palm built from ten little meshes costs ten draw calls, and thirty of them
    // was enough to push the frame budget over on the busier presets.
    const flattenProp = (group) => {
      group.updateMatrixWorld(true);
      const byMat = new Map();
      group.traverse((o) => {
        if (!o.isMesh) return;
        // Normalise to non-indexed: the primitives here are a mix of indexed
        // (Box/Cylinder/Torus) and non-indexed (Dodecahedron) geometry, and
        // mergeGeometries refuses a list that mixes the two.
        const g = o.geometry.index ? o.geometry.toNonIndexed() : o.geometry.clone();
        for (const name of Object.keys(g.attributes)) {
          if (name !== "position" && name !== "normal" && name !== "uv") g.deleteAttribute(name);
        }
        g.applyMatrix4(o.matrixWorld);
        if (!byMat.has(o.material)) byMat.set(o.material, []);
        byMat.get(o.material).push(g);
      });
      const mats = [...byMat.keys()];
      const perMat = mats.map((m) => mergeGeometries(byMat.get(m), false));
      return { geometry: mergeGeometries(perMat, true), material: mats };
    };

    // Scatter a flattened prop across many placements as ONE InstancedMesh.
    // Filling the map with the hundreds of small objects it was missing is only
    // affordable if they cost a draw call per *kind*, not per object — a few
    // hundred bushes as individual meshes would have cost more than the entire
    // rest of the scene.
    const _sm = new THREE.Matrix4();
    const _sq = new THREE.Quaternion();
    const _sp = new THREE.Vector3();
    const _ss = new THREE.Vector3();
    const _se = new THREE.Euler();
    const scatterInstanced = (proto, placements, { shadow = true } = {}) => {
      if (!placements.length) return null;
      const inst = new THREE.InstancedMesh(proto.geometry, proto.material, placements.length);
      placements.forEach((p, i) => {
        _sp.set(p.x, p.y, p.z);
        _se.set(0, p.ry || 0, 0);
        _sq.setFromEuler(_se);
        const s = p.s ?? 1;
        _ss.set(s, p.sy ?? s, s);
        _sm.compose(_sp, _sq, _ss);
        inst.setMatrixAt(i, _sm);
      });
      inst.instanceMatrix.needsUpdate = true;
      inst.castShadow = shadow;
      inst.receiveShadow = false;
      // The scatter is decorative only; skipping the frustum test avoids
      // recomputing a bounding sphere for it every frame.
      inst.frustumCulled = false;
      return inst;
    };

    // Flattened prototypes are cached by key: a preset switch rebuilds the whole
    // scatter, and re-merging identical geometry each time was pure waste.
    const protoCache = new Map();
    const getProto = (key, build) => {
      if (!protoCache.has(key)) protoCache.set(key, flattenProp(build()));
      return protoCache.get(key);
    };

    // ── Props ────────────────────────────────────────────────────────────────
    const createCoconutPalm = (rnd) => {
      const g = new THREE.Group();
      const lean = (rnd() - 0.5) * 0.34;
      const TH = 4.4; // a coconut palm towers over a one-storey hut — it should here too
      // Segmented trunk: a single tapered cylinder is a stick, whereas a stack of
      // slightly offset segments curves the way a palm actually leans.
      const segs = 5;
      for (let s = 0; s < segs; s++) {
        const t0 = s / segs, t1 = (s + 1) / segs;
        const h = TH / segs;
        const seg = new THREE.Mesh(
          new THREE.CylinderGeometry(0.15 - t1 * 0.07, 0.15 - t0 * 0.07, h * 1.04, 6), woodMat
        );
        seg.position.set(Math.sin(lean * t0) * TH * t0 * 0.55, h * (s + 0.5), 0);
        seg.rotation.z = lean * (0.4 + t0);
        seg.castShadow = true;
        g.add(seg);
      }

      const crownX = Math.sin(lean) * TH * 0.5;
      // Crown of drooping fronds: three rings of tapered blades rather than one
      // ring of flat slabs, which from any distance just read as an asterisk.
      const crown = new THREE.Group();
      crown.position.set(crownX, TH, 0);
      for (let ring = 0; ring < 3; ring++) {
        const n = ring === 0 ? 6 : ring === 1 ? 5 : 4;
        for (let a = 0; a < n; a++) {
          const angle = (a / n) * Math.PI * 2 + ring * 0.55 + rnd() * 0.25;
          const len = ring === 0 ? 2.0 : ring === 1 ? 1.55 : 1.05;
          const mat = ring === 2 ? leafLightMat : palmLeafMat;
          const frond = new THREE.Mesh(new THREE.CylinderGeometry(0.26, 0.03, len, 3), mat);
          frond.scale.set(1, 1, 0.3);
          frond.position.set(
            Math.cos(angle) * len * 0.42,
            ring === 0 ? -0.16 : ring === 1 ? 0.12 : 0.34,
            Math.sin(angle) * len * 0.42
          );
          frond.rotation.set(Math.PI / 2, -angle, ring === 0 ? 0.62 : ring === 1 ? 0.95 : 1.25);
          frond.castShadow = true;
          crown.add(frond);
        }
      }
      // Coconuts clustered at the crown base.
      for (let c = 0; c < 4; c++) {
        const a = (c / 4) * Math.PI * 2;
        const nut = new THREE.Mesh(new THREE.DodecahedronGeometry(0.12, 0), bananaMat);
        nut.position.set(Math.cos(a) * 0.2, -0.2, Math.sin(a) * 0.2);
        crown.add(nut);
      }
      g.add(crown);
      g.rotation.y = rnd() * Math.PI * 2;
      return g;
    };

    // ── Set-dressing library ─────────────────────────────────────────────────
    // The map was 6144 cells carrying 30 palms and a dozen huts, so most of the
    // frame was unbroken flat colour. These are the small, cheap, repeatable
    // things that fill the space between the landform and the buildings — the
    // layer that was missing entirely.
    const createBush = (rnd) => {
      const g = new THREE.Group();
      const n = 2 + Math.floor(rnd() * 2);
      for (let i = 0; i < n; i++) {
        const r = 0.32 + rnd() * 0.24;
        const blob = new THREE.Mesh(
          new THREE.DodecahedronGeometry(r, 0),
          rnd() > 0.5 ? leafMidMat : leafLightMat
        );
        blob.position.set((rnd() - 0.5) * 0.5, r * 0.75, (rnd() - 0.5) * 0.5);
        blob.scale.y = 0.78;
        blob.castShadow = true;
        g.add(blob);
      }
      return g;
    };

    const createBananaPlant = (rnd) => {
      const g = new THREE.Group();
      const trunk = new THREE.Mesh(new THREE.CylinderGeometry(0.09, 0.14, 1.0, 5), bananaMat);
      trunk.position.y = 0.5;
      g.add(trunk);
      for (let l = 0; l < 6; l++) {
        const a = (l / 6) * Math.PI * 2 + rnd() * 0.4;
        const leaf = new THREE.Mesh(new THREE.BoxGeometry(1.25, 0.05, 0.42), l % 2 ? bananaMat : leafMidMat);
        leaf.position.set(Math.cos(a) * 0.55, 1.05 + rnd() * 0.2, Math.sin(a) * 0.55);
        leaf.rotation.set(0, -a, -0.45 - rnd() * 0.25);
        leaf.castShadow = true;
        g.add(leaf);
      }
      return g;
    };

    const createRock = (rnd) => {
      const g = new THREE.Group();
      const r = 0.28 + rnd() * 0.3;
      const rock = new THREE.Mesh(new THREE.DodecahedronGeometry(r, 0), rockMat);
      rock.position.y = r * 0.55;
      rock.rotation.set(rnd() * 3, rnd() * 3, rnd() * 3);
      rock.scale.set(1, 0.7, 0.85);
      rock.castShadow = true;
      g.add(rock);
      if (rnd() > 0.5) {
        const chip = new THREE.Mesh(new THREE.DodecahedronGeometry(r * 0.45, 0), rockMat);
        chip.position.set(r * 1.1, r * 0.25, r * 0.5);
        g.add(chip);
      }
      return g;
    };

    // Reed/grass clump. Kept as a handful of thin tapered blades: they catch the
    // light at grazing angles and break up the big empty greens without costing
    // anything, which is exactly what "barren" was asking for.
    const createGrassTuft = (rnd) => {
      const g = new THREE.Group();
      const n = 5 + Math.floor(rnd() * 4);
      for (let i = 0; i < n; i++) {
        const h = 0.35 + rnd() * 0.4;
        const blade = new THREE.Mesh(
          new THREE.CylinderGeometry(0.012, 0.05, h, 3),
          rnd() > 0.4 ? leafLightMat : leafMidMat
        );
        blade.position.set((rnd() - 0.5) * 0.38, h * 0.5, (rnd() - 0.5) * 0.38);
        blade.rotation.set((rnd() - 0.5) * 0.5, rnd() * 3, (rnd() - 0.5) * 0.5);
        g.add(blade);
      }
      return g;
    };

    // A short run of bamboo fence — placed in lines, these are what turn loose
    // scattered houses into something that reads as plots and yards.
    const createFence = () => {
      const g = new THREE.Group();
      for (let p = 0; p < 4; p++) {
        const post = new THREE.Mesh(new THREE.CylinderGeometry(0.045, 0.045, 0.75, 4), bambooMat);
        post.position.set(-0.75 + p * 0.5, 0.37, 0);
        g.add(post);
      }
      for (const y of [0.28, 0.58]) {
        const rail = new THREE.Mesh(new THREE.BoxGeometry(2.05, 0.05, 0.05), bambooMat);
        rail.position.set(-0.05, y, 0);
        g.add(rail);
      }
      return g;
    };

    // Laundry strung between two poles — pure cozy-village texture, and the
    // brightest small colour accents on the map.
    const createLaundryLine = (rnd) => {
      const g = new THREE.Group();
      const clothCols = [0xe8556d, 0x49a6e0, 0xf2c14e, 0xf7f3e8, 0x6cc070];
      for (const px of [-1.1, 1.1]) {
        const pole = new THREE.Mesh(new THREE.CylinderGeometry(0.05, 0.06, 1.7, 5), bambooMat);
        pole.position.set(px, 0.85, 0);
        g.add(pole);
      }
      const line = new THREE.Mesh(new THREE.BoxGeometry(2.2, 0.02, 0.02), trimMat);
      line.position.y = 1.6;
      g.add(line);
      for (let c = 0; c < 5; c++) {
        const mat = new THREE.MeshStandardMaterial({
          color: clothCols[Math.floor(rnd() * clothCols.length)], roughness: 0.85,
          side: THREE.DoubleSide,
        });
        const cloth = new THREE.Mesh(new THREE.BoxGeometry(0.3, 0.45, 0.03), mat);
        cloth.position.set(-0.85 + c * 0.42, 1.35, 0);
        g.add(cloth);
      }
      return g;
    };

    // Street lamp + power pole. Verticals are what a flat urban map is missing —
    // without them the city preset is a grey plane with specks on it.
    const createStreetLamp = () => {
      const g = new THREE.Group();
      const pole = new THREE.Mesh(new THREE.CylinderGeometry(0.07, 0.1, 3.2, 6), drainBoxMat);
      pole.position.y = 1.6;
      pole.castShadow = true;
      g.add(pole);
      const arm = new THREE.Mesh(new THREE.BoxGeometry(0.7, 0.07, 0.07), drainBoxMat);
      arm.position.set(0.33, 3.15, 0);
      g.add(arm);
      const head = new THREE.Mesh(new THREE.BoxGeometry(0.34, 0.12, 0.2), trimMat);
      head.position.set(0.66, 3.06, 0);
      g.add(head);
      // Crossarm + insulators: the tangle of overhead wiring is a Manila signature.
      const cross = new THREE.Mesh(new THREE.BoxGeometry(1.15, 0.06, 0.06), woodMat);
      cross.position.set(0, 2.6, 0);
      g.add(cross);
      for (const ix of [-0.45, 0, 0.45]) {
        const ins = new THREE.Mesh(new THREE.CylinderGeometry(0.05, 0.05, 0.14, 5), tarpMat);
        ins.position.set(ix, 2.72, 0);
        g.add(ins);
      }
      return g;
    };

    // Roadside market stall under a tarp.
    const createMarketStall = (rnd) => {
      const g = new THREE.Group();
      const tarpCols = [0x3f8f86, 0xd9663a, 0x4a90e2, 0xe8a33d];
      const tMat = new THREE.MeshStandardMaterial({
        color: tarpCols[Math.floor(rnd() * tarpCols.length)], roughness: 0.8,
      });
      for (const px of [-0.7, 0.7]) {
        for (const pz of [-0.55, 0.55]) {
          const post = new THREE.Mesh(new THREE.CylinderGeometry(0.045, 0.045, 1.5, 4), bambooMat);
          post.position.set(px, 0.75, pz);
          g.add(post);
        }
      }
      const canopy = new THREE.Mesh(new THREE.BoxGeometry(1.8, 0.08, 1.5), tMat);
      canopy.position.y = 1.5;
      canopy.rotation.x = 0.1;
      canopy.castShadow = true;
      g.add(canopy);
      const table = new THREE.Mesh(new THREE.BoxGeometry(1.5, 0.1, 0.7), woodMat);
      table.position.set(0, 0.72, 0.2);
      g.add(table);
      for (let c = 0; c < 3; c++) {
        const crate = new THREE.Mesh(new THREE.BoxGeometry(0.28, 0.22, 0.28), rnd() > 0.5 ? bananaMat : rustMat);
        crate.position.set(-0.45 + c * 0.45, 0.88, 0.2);
        g.add(crate);
      }
      return g;
    };

    // Parked tricycle — the single most recognisable object on a barangay street.
    const createTricycle = (rnd) => {
      const g = new THREE.Group();
      const bodyCols = [0xd9442b, 0x3f8f86, 0x4a90e2, 0xe8a33d];
      const bMat = new THREE.MeshStandardMaterial({
        color: bodyCols[Math.floor(rnd() * bodyCols.length)], roughness: 0.55, metalness: 0.2,
      });
      const cab = new THREE.Mesh(new THREE.BoxGeometry(0.75, 0.55, 0.62), bMat);
      cab.position.set(0.1, 0.42, 0.3);
      cab.castShadow = true;
      g.add(cab);
      const roof = new THREE.Mesh(new THREE.BoxGeometry(0.85, 0.07, 0.72), rustMat);
      roof.position.set(0.1, 0.74, 0.3);
      g.add(roof);
      const bike = new THREE.Mesh(new THREE.BoxGeometry(0.85, 0.22, 0.2), drainBoxMat);
      bike.position.set(-0.05, 0.33, -0.22);
      g.add(bike);
      for (const [wx, wz] of [[-0.42, -0.22], [0.42, -0.22], [0.3, 0.55]]) {
        const wheel = new THREE.Mesh(new THREE.CylinderGeometry(0.19, 0.19, 0.09, 8), woodMat);
        wheel.rotation.z = Math.PI / 2;
        wheel.position.set(wx, 0.19, wz);
        g.add(wheel);
      }
      return g;
    };

    // Beached / moored outrigger and driftwood for the shorelines.
    const createDriftwood = (rnd) => {
      const g = new THREE.Group();
      const log = new THREE.Mesh(new THREE.CylinderGeometry(0.11, 0.14, 1.1 + rnd() * 0.6, 5), woodMat);
      log.rotation.set(0, rnd() * 3, Math.PI / 2 + (rnd() - 0.5) * 0.3);
      log.position.y = 0.12;
      g.add(log);
      return g;
    };

    // A slab, not a plane: a flat PlaneGeometry laid on a heightmap gets sliced by
    // any slope, which is why the court used to read as a red rag half-buried in
    // the ground. The slab has skirts, and the caller sits it on the HIGHEST point
    // of its footprint so nothing can poke through it.
    const createBasketballCourt = () => {
      const g = new THREE.Group();
      const courtMat = new THREE.MeshStandardMaterial({ color: 0xc23d27, roughness: 0.85 });
      const lineMat = new THREE.MeshStandardMaterial({ color: 0xf0e6d2, roughness: 0.8 });
      // A real court is 28×15 m; at ~4 m per cell that is about 7×3.8 units. The
      // covered barangay court is the social centre of the village, so it is also
      // the largest flat man-made thing on the map — it needs the footprint.
      const CW = 7.6, CD = 4.6;
      const slab = new THREE.Mesh(new THREE.BoxGeometry(CW, 0.22, CD), courtMat);
      slab.position.y = -0.03;
      slab.receiveShadow = true;
      g.add(slab);
      // Painted border + halfway line + centre circle.
      for (const [w, d, z] of [[CW - 0.5, 0.09, -CD / 2 + 0.35], [CW - 0.5, 0.09, CD / 2 - 0.35]]) {
        const ln = new THREE.Mesh(new THREE.BoxGeometry(w, 0.03, d), lineMat);
        ln.position.set(0, 0.09, z);
        g.add(ln);
      }
      const halfway = new THREE.Mesh(new THREE.BoxGeometry(0.1, 0.03, CD - 0.7), lineMat);
      halfway.position.y = 0.09;
      g.add(halfway);
      const circle = new THREE.Mesh(new THREE.TorusGeometry(0.95, 0.05, 4, 20), lineMat);
      circle.rotation.x = -Math.PI / 2;
      circle.position.y = 0.09;
      g.add(circle);
      for (const kx of [-CW / 2 + 1.15, CW / 2 - 1.15]) {
        const key = new THREE.Mesh(new THREE.TorusGeometry(0.7, 0.045, 4, 16), lineMat);
        key.rotation.x = -Math.PI / 2;
        key.position.set(kx, 0.09, 0);
        g.add(key);
      }

      for (const side of [-CW / 2 - 0.15, CW / 2 + 0.15]) {
        const inward = side > 0 ? -1 : 1;
        const pole = new THREE.Mesh(new THREE.CylinderGeometry(0.09, 0.13, 2.9, 6), drainBoxMat);
        pole.position.set(side, 1.45, 0);
        pole.castShadow = true;
        g.add(pole);
        const arm = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.09, 0.09), drainBoxMat);
        arm.position.set(side + inward * 0.25, 2.55, 0);
        g.add(arm);
        const board = new THREE.Mesh(new THREE.BoxGeometry(0.09, 0.72, 1.05), trimMat);
        board.position.set(side + inward * 0.5, 2.5, 0);
        board.castShadow = true;
        g.add(board);
        const rim = new THREE.Mesh(new THREE.TorusGeometry(0.24, 0.035, 4, 12), warningFlagMat);
        rim.rotation.x = -Math.PI / 2;
        rim.position.set(side + inward * 0.78, 2.2, 0);
        g.add(rim);
      }

      // Perimeter benches: cheap, and they stop the slab from reading as a bare
      // red rectangle dropped on the grass.
      for (const bz of [-CD / 2 - 0.5, CD / 2 + 0.5]) {
        for (const bx of [-1.8, 1.8]) {
          const seat = new THREE.Mesh(new THREE.BoxGeometry(1.5, 0.1, 0.32), woodMat);
          seat.position.set(bx, 0.34, bz);
          g.add(seat);
          for (const lx of [-0.6, 0.6]) {
            const leg = new THREE.Mesh(new THREE.BoxGeometry(0.09, 0.34, 0.09), drainBoxMat);
            leg.position.set(bx + lx, 0.17, bz);
            g.add(leg);
          }
        }
      }
      return g;
    };

    const createBangkaBoat = (rnd = Math.random) => {
      const g = new THREE.Group();
      const hullCols = [0x4a90e2, 0xd9442b, 0x3f8f86, 0xe8a33d];
      const hullMat = new THREE.MeshStandardMaterial({
        color: hullCols[Math.floor(rnd() * hullCols.length)], roughness: 0.6,
      });
      // A bangka is ~8-10 m long: about 2.5 units here. Tapered hull + a real bow
      // so it reads as a boat from above instead of a brick.
      const hull = new THREE.Mesh(new THREE.BoxGeometry(2.5, 0.34, 0.56), hullMat);
      hull.position.y = 0.13;
      hull.castShadow = true;
      g.add(hull);
      const bow = new THREE.Mesh(new THREE.ConeGeometry(0.33, 0.9, 4), hullMat);
      bow.rotation.z = -Math.PI / 2;
      bow.rotation.y = Math.PI / 4;
      bow.position.set(1.6, 0.13, 0);
      g.add(bow);
      const stern = new THREE.Mesh(new THREE.ConeGeometry(0.28, 0.5, 4), hullMat);
      stern.rotation.z = Math.PI / 2;
      stern.rotation.y = Math.PI / 4;
      stern.position.set(-1.4, 0.13, 0);
      g.add(stern);
      const gunwale = new THREE.Mesh(new THREE.BoxGeometry(2.5, 0.1, 0.66), sawaliMat);
      gunwale.position.y = 0.33;
      g.add(gunwale);
      // Interior thwarts + a little canopy over the middle.
      for (const tx of [-0.6, 0.3]) {
        const thwart = new THREE.Mesh(new THREE.BoxGeometry(0.14, 0.07, 0.56), woodMat);
        thwart.position.set(tx, 0.4, 0);
        g.add(thwart);
      }
      const canopy = new THREE.Mesh(new THREE.BoxGeometry(0.95, 0.06, 0.7), tarpMat);
      canopy.position.set(-0.15, 0.95, 0);
      g.add(canopy);
      for (const px of [-0.55, 0.25]) {
        const post = new THREE.Mesh(new THREE.CylinderGeometry(0.03, 0.03, 0.6, 4), bambooMat);
        post.position.set(px, 0.63, 0.28);
        g.add(post);
        const post2 = post.clone();
        post2.position.z = -0.28;
        g.add(post2);
      }

      // Outriggers: floats running PARALLEL to the hull on cross-booms. The old
      // version put two long boxes across the hull, which looked like a hammer.
      for (const side of [-1.0, 1.0]) {
        const float = new THREE.Mesh(new THREE.CylinderGeometry(0.08, 0.08, 2.1, 5), bambooMat);
        float.rotation.z = Math.PI / 2;
        float.position.set(0, 0.11, side);
        g.add(float);
        for (const bx of [-0.7, 0.7]) {
          const boom = new THREE.Mesh(new THREE.BoxGeometry(0.09, 0.07, Math.abs(side) + 0.1), bambooMat);
          boom.position.set(bx, 0.36, side / 2);
          g.add(boom);
        }
      }
      return g;
    };

    const createMangroveTree = () => {
      const g = new THREE.Group();
      // Mangroves are defined by their stilt roots standing clear of the water —
      // at the old 0.5u height that detail was invisible, so they just looked
      // like green dots.
      for (let r = 0; r < 5; r++) {
        const a = (r / 5) * Math.PI * 2;
        const root = new THREE.Mesh(new THREE.CylinderGeometry(0.045, 0.075, 0.75, 4), bambooMat);
        root.position.set(Math.cos(a) * 0.22, 0.34, Math.sin(a) * 0.22);
        root.rotation.set(Math.cos(a) * 0.42, 0, -Math.sin(a) * 0.42);
        g.add(root);
      }
      const trunk = new THREE.Mesh(new THREE.CylinderGeometry(0.11, 0.17, 0.85, 5), bambooMat);
      trunk.position.y = 0.9;
      trunk.castShadow = true;
      g.add(trunk);

      const canopy = new THREE.Mesh(new THREE.DodecahedronGeometry(0.72, 0), palmLeafMat);
      canopy.position.y = 1.6;
      canopy.scale.y = 0.8;
      canopy.castShadow = true;
      g.add(canopy);
      const canopy2 = new THREE.Mesh(new THREE.DodecahedronGeometry(0.46, 0), leafMidMat);
      canopy2.position.set(0.4, 1.3, 0.28);
      canopy2.scale.y = 0.8;
      g.add(canopy2);
      const canopy3 = new THREE.Mesh(new THREE.DodecahedronGeometry(0.4, 0), leafLightMat);
      canopy3.position.set(-0.35, 1.42, -0.3);
      canopy3.scale.y = 0.8;
      g.add(canopy3);
      return g;
    };

    const createDrain = () => {
      const g = new THREE.Group();
      // Kerb inlet + pump housing. Scaled to match the new buildings, and given a
      // visible outfall pipe so it reads as drainage infrastructure.
      const box = new THREE.Mesh(new THREE.BoxGeometry(1.35, 0.42, 1.35), drainBoxMat);
      box.position.y = 0.21;
      box.castShadow = true;
      g.add(box);
      const kerb = new THREE.Mesh(new THREE.BoxGeometry(1.55, 0.14, 1.55), concreteMat);
      kerb.position.y = 0.07;
      g.add(kerb);

      const grate = new THREE.Mesh(new THREE.PlaneGeometry(0.95, 0.95), drainGrateMat);
      grate.rotation.x = -Math.PI / 2;
      grate.position.y = 0.43;
      g.add(grate);
      for (let b = 0; b < 4; b++) {
        const bar = new THREE.Mesh(new THREE.BoxGeometry(0.95, 0.05, 0.07), drainBoxMat);
        bar.position.set(0, 0.45, -0.34 + b * 0.23);
        g.add(bar);
      }
      // Pump housing + discharge pipe.
      const housing = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.55, 0.5), rustMat);
      housing.position.set(0.75, 0.5, -0.5);
      housing.castShadow = true;
      g.add(housing);
      const pipe = new THREE.Mesh(new THREE.CylinderGeometry(0.1, 0.1, 1.0, 6), drainBoxMat);
      pipe.rotation.z = Math.PI / 2;
      pipe.position.set(1.2, 0.62, -0.5);
      g.add(pipe);
      return g;
    };

    // ── 14b. Dressing the world OUTSIDE the sandbox ──────────────────────────
    // The surrounding land was geometrically continuous with the play area but
    // completely empty — a smooth wash of one colour running to the fog. That is
    // what made the background read as "dry and bland" and, worse, as a separate
    // thing from the sandbox: inside the border there were trees and houses,
    // outside there was nothing at all. The fix is to keep scattering the SAME
    // kinds of objects past the border, thinning with distance and letting the
    // scene fog do the blending, so the eye finds no line where detail stops.
    const worldDressGroup = new THREE.Group();
    scene.add(worldDressGroup);
    {
      const wrnd = seededRng(strSeed("worlddress:" + presetType));
      const isCity = presetType === "urban" || presetType === "basin";

      // Cheap two-blob tree — this is canopy texture seen from hundreds of units
      // away, so it only has to hold a silhouette.
      const createDistantTree = (r) => {
        const g = new THREE.Group();
        const trunk = new THREE.Mesh(new THREE.CylinderGeometry(0.1, 0.16, 1.1, 4), woodMat);
        trunk.position.y = 0.55;
        g.add(trunk);
        const c1 = new THREE.Mesh(new THREE.DodecahedronGeometry(0.85, 0), leafMidMat);
        c1.position.y = 1.6; c1.scale.y = 0.85;
        g.add(c1);
        const c2 = new THREE.Mesh(new THREE.DodecahedronGeometry(0.6, 0), palmLeafMat);
        c2.position.set(r() * 0.5 - 0.25, 2.25, r() * 0.5 - 0.25);
        g.add(c2);
        return g;
      };
      // A far-off house: a coloured roof on a pale box is all that survives haze,
      // and it is enough to say "the barangay keeps going out there".
      const createFarHouse = (r) => {
        const g = new THREE.Group();
        const wall = new THREE.MeshStandardMaterial({
          color: houseWallColors[Math.floor(r() * houseWallColors.length)], roughness: 0.85,
        });
        const roof = new THREE.MeshStandardMaterial({
          color: houseRoofColors[Math.floor(r() * houseRoofColors.length)], roughness: 0.8,
        });
        const body = new THREE.Mesh(new THREE.BoxGeometry(2.0, 1.3, 2.0), wall);
        body.position.y = 0.65;
        g.add(body);
        const top = new THREE.Mesh(new THREE.ConeGeometry(1.7, 0.85, 4), roof);
        top.position.y = 1.72; top.rotation.y = Math.PI / 4;
        g.add(top);
        return g;
      };
      const createFarBlock = (r) => {
        const g = new THREE.Group();
        const tones = [0x8d97a1, 0xa8a294, 0x9fb0ad, 0xb0a6a0, 0xc0b6a8, 0xb9a48f];
        const mat = new THREE.MeshStandardMaterial({
          color: tones[Math.floor(r() * tones.length)], roughness: 0.9,
        });
        const st = 2 + Math.floor(r() * 3);
        const body = new THREE.Mesh(new THREE.BoxGeometry(2.1, st * 1.0, 2.1), mat);
        body.position.y = (st * 1.0) / 2;
        g.add(body);
        // Roofs carry the colour. From an overhead camera the roof IS most of
        // what you see of a distant building, so a grey cap on every one of them
        // turned the sprawl into a field of headstones. Real Manila roofs are
        // rust red, faded teal and blue GI sheet — that variety is the whole
        // difference between a drab backdrop and a living city.
        const roofs = [0xa4523a, 0x8a6f4e, 0x3f7f7a, 0x4a6b93, 0x9c5b3c, 0x6b6a66, 0x7c8a6a];
        const roofMat = new THREE.MeshStandardMaterial({
          color: roofs[Math.floor(r() * roofs.length)], roughness: 0.9,
        });
        if (r() > 0.45) {
          // Low-rise with a pitched roof.
          const cap = new THREE.Mesh(new THREE.ConeGeometry(1.75, 0.8, 4), roofMat);
          cap.position.y = st * 1.0 + 0.4;
          cap.rotation.y = Math.PI / 4;
          g.add(cap);
        } else {
          const cap = new THREE.Mesh(new THREE.BoxGeometry(2.3, 0.2, 2.3), roofMat);
          cap.position.y = st * 1.0;
          g.add(cap);
          // Roof clutter, just enough to break the flat plane.
          const tank = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.4, 0.5), mat);
          tank.position.set(r() - 0.5, st * 1.0 + 0.3, r() - 0.5);
          g.add(tank);
        }
        return g;
      };

      const treeSpots = [], houseSpots = [], blockSpots = [], scrubSpots = [];
      // Reject anything at or below the waterline, and anything on a slope steep
      // enough that a vertical prop would visibly float or sink.
      const sampleOuter = () => {
        const j = 2 + Math.floor(wrnd() * (OR - 3));
        const p = Math.floor(wrnd() * P);
        const d = ringD[j];
        // Hold the dressing back from the border. Packed right up against the
        // sandbox it built a hedge around the play area — the outside world
        // should start just past the edge of attention, not on top of it.
        if (d < 7.0 || d > 215) return null;
        if (d < 18 && wrnd() > (d - 7) / 11) return null;
        const k = j * P + p;
        const h = outBaseY[k];
        if (h < 0.6) return null;
        return { k, d, h, x: outPos[k * 3], z: outPos[k * 3 + 2] };
      };

      for (let a = 0; a < 9000; a++) {
        const s = sampleOuter();
        if (!s) continue;
        // Props grow with distance so a far-off stand of trees still covers a few
        // pixels; without this the outer world silts up into featureless mush
        // exactly the way it did before. Capped, though — ungoverned it made the
        // near background bigger than the village it is supposed to sit behind.
        const grow = 1 + Math.min(s.d, 200) * 0.011;
        const base = { x: s.x, y: s.h - 0.1, z: s.z, ry: wrnd() * Math.PI * 2 };

        if (isCity) {
          // The city has to keep being a city right up to the skyline, or the
          // barangay ends abruptly in open scrubland with towers behind it.
          if (s.d < 130 && wrnd() < 0.62) {
            if (blockSpots.length < 420) blockSpots.push({ ...base, s: grow * (0.9 + wrnd() * 0.5) });
          } else if (wrnd() < 0.5) {
            if (treeSpots.length < 300) treeSpots.push({ ...base, s: grow * (0.7 + wrnd() * 0.5) });
          } else if (scrubSpots.length < 300) {
            scrubSpots.push({ ...base, s: grow * (0.9 + wrnd() * 0.8) });
          }
        } else {
          // Village: forest, thinning to scrub, with occasional hamlets. Groves
          // rather than an even sprinkle — clumping is what makes scatter read as
          // landscape instead of as wallpaper.
          const rr = wrnd();
          if (rr < 0.52) {
            if (treeSpots.length < 620) {
              treeSpots.push({ ...base, s: grow * (0.75 + wrnd() * 0.6) });
              // Clump-mates
              const mates = Math.floor(wrnd() * 3);
              for (let m = 0; m < mates && treeSpots.length < 620; m++) {
                const rad = (1.5 + wrnd() * 3.5) * grow;
                const ang = wrnd() * Math.PI * 2;
                treeSpots.push({
                  x: s.x + Math.cos(ang) * rad, y: s.h - 0.1, z: s.z + Math.sin(ang) * rad,
                  ry: wrnd() * Math.PI * 2, s: grow * (0.7 + wrnd() * 0.55),
                });
              }
            }
          } else if (rr < 0.62 && s.d > 12 && s.d < 150) {
            if (houseSpots.length < 150) {
              const cluster = 2 + Math.floor(wrnd() * 4);
              for (let m = 0; m < cluster && houseSpots.length < 150; m++) {
                const rad = (m === 0 ? 0 : 2.5 + wrnd() * 5) * grow;
                const ang = wrnd() * Math.PI * 2;
                houseSpots.push({
                  x: s.x + Math.cos(ang) * rad, y: s.h - 0.1, z: s.z + Math.sin(ang) * rad,
                  ry: wrnd() * Math.PI * 2, s: grow * (0.85 + wrnd() * 0.3),
                });
              }
            }
          } else if (scrubSpots.length < 520) {
            scrubSpots.push({ ...base, s: grow * (1.0 + wrnd() * 1.1) });
          }
        }
      }

      const dress = (key, build, spots) => {
        const inst = scatterInstanced(getProto(key, build), spots, { shadow: false });
        // Purely background: it must never receive the play area's shadow map,
        // whose frustum edge would otherwise draw a hard line across the world.
        if (inst) worldDressGroup.add(inst);
      };
      dress("wtree", () => createDistantTree(wrnd), treeSpots);
      dress("wscrub", () => createBush(wrnd), scrubSpots);
      if (houseSpots.length) dress("whouse", () => createFarHouse(wrnd), houseSpots);
      if (blockSpots.length) dress("wblock", () => createFarBlock(wrnd), blockSpots);
    }

    let mangProto = null;
    let drainProto = null;
    let lastMangCount = -1;
    let lastDrnCount = -1;
    let lastHouseKey = "";
    let lastPresetType = "";

    // 15. Main Animation Render Loop
    let animId;
    let clock = new THREE.Clock();
    const _tgtFog = new THREE.Color();
    const _tgtMid = new THREE.Color();
    const _tgtZen = new THREE.Color();

    const animate = () => {
      animId = requestAnimationFrame(animate);
      const time = clock.getElapsedTime();
      const sim = simRef.current;

      if (!sim) return;

      const isStorm = stormRef.current;
      const currentRainVal = rainRef.current;
      const hasRain = currentRainVal > 0 || isStorm;

      // ── Weather, applied to the whole world rather than just the play area ───
      // The background is scenery, not simulation, so it can't compute its own
      // flooding — but it can share every visual consequence of the weather:
      // wet ground, rougher darker water, and a swell that grows with the storm.
      const rainNorm = Math.min(1, currentRainVal / 10);
      const targetWet = isStorm ? 1 : rainNorm * 0.85;
      wetness += (targetWet - wetness) * 0.025;
      swellAmp = 0.035 + wetness * 0.5;

      // Wet ground reads as darker and slicker — one shared value so the sandbox
      // and the surrounding land always soak through together.
      const rough = 0.85 - 0.3 * wetness;
      terrainMat.roughness = rough;
      outMat.roughness = rough;
      const waterRough = 0.12 + 0.45 * wetness;
      waterMat.roughness = waterRough;
      owMat.roughness = waterRough;

      // Dynamic Sky Gradient & Atmosphere Shifting (blend toward this preset's world).
      // The horizon band tracks the fog colour exactly, so however the weather
      // moves, land and sky always meet in the same colour.
      _tgtFog.setHex(isStorm ? STORM_ENV.fog : env.fog);
      _tgtMid.setHex(isStorm ? STORM_ENV.mid : env.sky.mid);
      _tgtZen.setHex(isStorm ? STORM_ENV.zenith : env.sky.zenith);
      skyHorizon.lerp(_tgtFog, 0.05);
      skyMid.lerp(_tgtMid, 0.05);
      skyZenith.lerp(_tgtZen, 0.05);
      scene.fog.color.copy(skyHorizon);
      scene.fog.near = THREE.MathUtils.lerp(scene.fog.near, isStorm ? STORM_ENV.fogNear : env.fogNear, 0.05);
      scene.fog.far = THREE.MathUtils.lerp(scene.fog.far, isStorm ? STORM_ENV.fogFar : env.fogFar, 0.05);
      paintSky(skyHorizon, skyMid, skyZenith);

      // Keep the distant silhouettes hazed toward whatever the sky is doing.
      for (const bd of backdropMats) bd.mat.color.copy(bd.base).lerp(skyHorizon, bd.haze);

      // Random Sheet Lightning Flashes in Storm Mode
      if (isStorm && Math.random() < 0.018) {
        dirLight.intensity = 3.5;
        skyZenith.setHex(0x507090);
        skyMid.setHex(0x6f8ba6);
      } else {
        dirLight.intensity = THREE.MathUtils.lerp(dirLight.intensity, isStorm ? 1.05 : env.dir.intensity, 0.1);
      }

      sunMesh.visible = env.sun && !isStorm;

      // Dynamic 3D Rain Particles Animation
      rainLines.visible = hasRain;
      if (hasRain) {
        const fallSpeed = isStorm ? 1.9 : 0.9 + (currentRainVal / 10) * 1.1;
        const streak = isStorm ? 2.6 : 1.2 + (currentRainVal / 10) * 1.0;
        const slant = isStorm ? 1.1 : 0.3;
        const pos = rainGeo.attributes.position.array;
        // Only as many drops as the intensity calls for; the rest park below ground.
        const active = Math.ceil(rainCount * (isStorm ? 1 : 0.15 + 0.85 * (currentRainVal / 10)));
        for (let i = 0; i < rainCount; i++) {
          if (i >= active) { pos[i * 6 + 1] = -999; pos[i * 6 + 4] = -999; continue; }
          pos[i * 6 + 1] -= fallSpeed;
          pos[i * 6 + 4] -= fallSpeed;
          if (pos[i * 6 + 1] < -8 || pos[i * 6 + 1] < -900) {
            const rx = (Math.random() - 0.5) * RAIN_SPAN;
            const ry = RAIN_TOP * (0.55 + Math.random() * 0.45);
            const rz = (Math.random() - 0.5) * RAIN_SPAN;
            pos[i * 6] = rx;
            pos[i * 6 + 1] = ry;
            pos[i * 6 + 2] = rz;
            pos[i * 6 + 3] = rx - slant;
            pos[i * 6 + 4] = ry - streak;
            pos[i * 6 + 5] = rz;
          }
        }
        rainLinesMat.opacity = isStorm ? 0.7 : 0.35 + 0.3 * (currentRainVal / 10);
        // Upload only the drops in play rather than the whole 9k buffer every frame.
        const rp = rainGeo.attributes.position;
        rp.clearUpdateRanges();
        rp.addUpdateRange(0, Math.max(active, lastActive) * 6);
        lastActive = active;
        rp.needsUpdate = true;
      }

      // Terrain Colors & Water Height
      const { elev, water } = sim;
      const tPos = terrainGeo.attributes.position.array;
      const tCol = terrainGeo.attributes.color.array;
      const tNrm = terrainGeo.attributes.normal.array;

      const wPos = waterGeo.attributes.position.array;
      const wCol = waterGeo.attributes.color.array;

      const terrCols = env.colors; // shared with the surrounding outerland
      const wetMul = 1 - 0.15 * wetness;   // rain-darkened ground
      const wetCool = 1 + 0.07 * wetness;  // and very slightly cooler
      // Water-surface height of a cell, or -Infinity if it holds no water.
      const surfaceAt = (n) => {
        const en = elev[n], wn = water[n];
        // A submerged cell normally sits exactly at SEA_LEVEL, but it must be
        // allowed to RISE when flood water or a surge is piled on it — pinning it
        // meant rain standing on the shore had nowhere to spill, so the flooded
        // land ended in a cell-high wall of water against a flat sea.
        if (en <= SEA_LEVEL) return Math.max(SEA_LEVEL, en + wn);
        // Cap only as a guard against a runaway column; the sim already clamps rain
        // to 3.0. The old 2.2 cap sat BELOW that clamp, so deep flood water was
        // pulled down toward the ground and the surface traced the terrain in
        // terraces instead of lying level the way the flow solver intends.
        return wn > 0.015 ? Math.min(en + wn, en + 3.2) : -Infinity;
      };
      const wcS = env.water.shallow;
      const wcD = env.water.deep;

      let currentMangCount = 0;
      let currentDrnCount = 0;
      let perimSum = 0;
      let landCells = 0;
      let landWaterSum = 0;

      // Pass 1: the true water surface of every cell (-Infinity where there is none).
      for (let i = 0; i < W * H; i++) wSurf[i] = surfaceAt(i);
      // Passes 2-3: push that surface a couple of cells INTO the surrounding dry
      // land. The sheet then reaches under the bank and the opaque terrain decides
      // where the waterline falls, instead of the water mesh's own cell boundary —
      // which is what made flood edges look like stair-stepped blocks.
      for (let pass = 0; pass < 2; pass++) {
        wSurfTmp.set(wSurf);
        for (let y = 0; y < H; y++) {
          for (let x = 0; x < W; x++) {
            const i = y * W + x;
            if (wSurfTmp[i] > -1e30) continue;
            let m = -Infinity;
            if (x > 0) m = Math.max(m, wSurfTmp[i - 1]);
            if (x < W - 1) m = Math.max(m, wSurfTmp[i + 1]);
            if (y > 0) m = Math.max(m, wSurfTmp[i - W]);
            if (y < H - 1) m = Math.max(m, wSurfTmp[i + W]);
            wSurf[i] = m;
          }
        }
      }
      // Passes 4-6: relax the LAND flood surface toward level. The sim happily
      // holds rain on a hillside, so e+w traces the terrain and renders as a
      // staircase of blocky terraces. Averaging with wet land neighbours settles
      // it into something that reads as standing water. Sea cells are excluded so
      // the open sea stays pinned at exactly SEA_LEVEL.
      // ...then relax it toward level with an EDGE-PRESERVING average: neighbours
      // only count if their surface is within 0.7 of this one. Adjacent cells of
      // one pool settle into a single sheet (the sim leaves a cell-wide staircase
      // that renders as blocky terraces), while a genuine step — water held behind
      // a dike, say — is left standing, because that is gameplay, not noise.
      // The threshold scales with how flooded things are: in a deep flood the sim
      // saturates every cell at its rain cap, so the surface drapes over the terrain
      // in plateaus and needs levelling hard; a shallow puddle keeps its detail.
      const relaxThresh = 0.5 + 1.7 * Math.min(1, bgDepth);
      for (let pass = 0; pass < 8; pass++) {
        wSurfTmp.set(wSurf);
        for (let y = 0; y < H; y++) {
          for (let x = 0; x < W; x++) {
            const i = y * W + x;
            const c = wSurfTmp[i];
            if (c <= -1e30) continue;
            let sum = c, n = 1;
            const take = (m) => {
              const v = wSurfTmp[m];
              if (v > -1e30 && Math.abs(v - c) < relaxThresh) { sum += v; n++; }
            };
            if (x > 0) take(i - 1);
            if (x < W - 1) take(i + 1);
            if (y > 0) take(i - W);
            if (y < H - 1) take(i + W);
            // Open water may be lifted by a flood spilling into it, never dragged below.
            wSurf[i] = Math.max(sum / n, elev[i] <= SEA_LEVEL ? SEA_LEVEL : -Infinity);
          }
        }
      }

      for (let y = 0; y < H; y++) {
        for (let x = 0; x < W; x++) {
          const i = y * W + x;
          const idx = i * 3;
          const idx4 = i * 4;
          const e = elev[i];
          const w = water[i];

          if (sim.mang[i]) currentMangCount++;
          if (sim.drn[i]) currentDrnCount++;
          if (x === 0 || y === 0 || x === W - 1 || y === H - 1) perimSum += e;

          tPos[idx + 1] = e;

          const [r, g, b] = colorFor(e, terrCols);
          const tn = terrainTint[i];
          tCol[idx] = (r + tn) * wetMul;
          tCol[idx + 1] = (g + tn * 0.92) * wetMul;
          tCol[idx + 2] = (b + tn * 0.78) * wetMul * wetCool;

          if (e > SEA_LEVEL) { landCells++; landWaterSum += w; }

          // Water Mesh. Open sea rests exactly at SEA_LEVEL so it is flush with the
          // surrounding water sheet; only rain/flood water sitting ON LAND gets the
          // anti-water-column depth cap.
          const surf = wSurf[i];
          if (surf <= -1e30) {
            // Well away from any water: lay the sheet ON the ground at zero alpha.
            // Dropping it half a unit under instead left the quads bridging to the
            // dilated cells tilted and faintly visible — pale wedges poking out of
            // slopes wherever a flood edge ran.
            wPos[idx + 1] = e - 0.04;
            wCol[idx4 + 3] = 0;
          } else {
            const depth = surf - e;
            // Shared swell, scaled down in shallow water so a puddle doesn't slosh
            // through the ground. No border taper is needed: the open-water sheet
            // outside uses this identical expression, so the two agree exactly.
            const wx = -W / 2 + x * DX, wz = -H / 2 + y * DZ;
            const shal = Math.min(1, Math.max(depth, 0) / 0.8);
            wPos[idx + 1] = surf + swellAt(wx, wz, time) * swellAmp * shal;

            // Shallow = pale and see-through, deep = the open ocean's own colour.
            // Same constants as the open-water sheet — that identity is what lets
            // the two surfaces meet at the border without a step.
            // Dither the depth ramp with the same world-space noise the ground uses.
            // Over a gently shelving bed this gradient covers a lot of screen, and
            // 8-bit output quantises it into contour bands that the grid's diagonal
            // split serrates into terraces — it reads as a staircase in the flood
            // even though the surface is dead level.
            const k = Math.min(1, Math.max(0, _smooth(0.02, 1.7, depth) + terrainTint[i] * 1.1));
            wCol[idx4] = wcS[0] + (wcD[0] - wcS[0]) * k;
            wCol[idx4 + 1] = wcS[1] + (wcD[1] - wcS[1]) * k;
            wCol[idx4 + 2] = wcS[2] + (wcD[2] - wcS[2]) * k;
            wCol[idx4 + 3] = 0.25 + 0.7 * k;
          }
        }
      }

      // Normals straight from the heightmap's central differences. computeVertexNormals()
      // averages the six triangles around a vertex, and because a PlaneGeometry splits
      // every cell along the same diagonal, that average is biased in an alternating
      // pattern — which corrugates smooth slopes into light/dark ribs (very visible on
      // riverbanks). An analytic gradient has no such bias, and it's cheaper.
      for (let y = 0; y < H; y++) {
        for (let x = 0; x < W; x++) {
          const idx = (y * W + x) * 3;
          const xm = x > 0 ? x - 1 : x, xp = x < W - 1 ? x + 1 : x;
          const ym = y > 0 ? y - 1 : y, yp = y < H - 1 ? y + 1 : y;
          const gx = -(elev[y * W + xp] - elev[y * W + xm]) / ((xp - xm) * DX);
          const gz = -(elev[yp * W + x] - elev[ym * W + x]) / ((yp - ym) * DZ);
          const inv = 1 / Math.hypot(gx, 1, gz);
          tNrm[idx] = gx * inv;
          tNrm[idx + 1] = inv;
          tNrm[idx + 2] = gz * inv;
        }
      }

      terrainGeo.attributes.position.needsUpdate = true;
      terrainGeo.attributes.color.needsUpdate = true;
      terrainGeo.attributes.normal.needsUpdate = true;

      // Border terraformed? Drag the surrounding world along so the seam holds.
      if (perimSum !== lastPerimSum) {
        if (!Number.isNaN(lastPerimSum)) reweldOuter(elev);
        lastPerimSum = perimSum;
      }

      waterGeo.attributes.position.needsUpdate = true;
      waterGeo.attributes.color.needsUpdate = true;

      // ── The world outside the sandbox, reacting to the same weather ──────────
      // Open water welds to the sandbox's OWN water level at ring 0 and relaxes
      // outward to sea level, so a storm surge or a flood at the shore rolls on
      // past the border instead of stopping at it. Only low-lying shoreline cells
      // may push the level up — otherwise rain pooling on a hillside would drag
      // the horizon's water up with it.
      // How deep the rain is lying on the play area's land. The world outside has no
      // simulation of its own, so it floods to this same depth — otherwise a
      // downpour turns the sandbox into a dark lake inside a bone-dry landscape,
      // which is the most glaring possible way to advertise where it ends.
      const targetDepth = landCells ? landWaterSum / landCells : 0;
      const prevDepth = bgDepth;
      bgDepth += (targetDepth - bgDepth) * 0.08;

      let levelsChanged = Math.abs(bgDepth - prevDepth) > 0.0015;
      for (let p = 0; p < P; p++) {
        const bi = border[p].i;
        const bs = wSurf[bi]; // the relaxed level, so the two sheets agree exactly
        const lvl =
          bs > SEA_LEVEL && bs > -1e30 && elev[bi] <= SEA_LEVEL + 0.8
            ? Math.min(bs, SEA_LEVEL + 2.0)
            : SEA_LEVEL;
        if (Math.abs(lvl - outerBorderSurf[p]) > 0.002) levelsChanged = true;
        outerBorderSurf[p] = lvl;
      }
      // Displacement is only worth uploading while the sea is actually moving.
      const owAnimating = swellAmp > 0.09;
      if (levelsChanged || owAnimating || owWasAnimating) {
        owWasAnimating = owAnimating;
        const ct1 = Math.cos(time * 1.25), st1 = Math.sin(time * 1.25);
        const ct2 = Math.cos(time * 0.92), st2 = Math.sin(time * 0.92);
        const ct3 = Math.cos(time * 2.05), st3 = Math.sin(time * 2.05);
        const jMax = owAnimating ? owSwellRings : 0;
        for (let p = 0; p < P; p++) {
          const sw = outerBorderSurf[p];
          for (let j = 0; j <= OR; j++) {
            const k = j * P + p;
            const h = outBaseY[k];
            const sm = outSmoothY[k];
            const sea = sw + (SEA_LEVEL - sw) * waterRelax[j];
            // Rain lies at a level set by the smoothed ground, and fades out as that
            // ground drops to the waterline so flooded land meets the open sea flush
            // instead of standing above it. Depth is measured against the REAL
            // ground, so high ground still emerges as dry islands.
            const level = Math.max(sea, sea + (sm + bgDepth - sea) * _smooth(-0.2, 0.9, sm));
            const depth = level - h;
            if (depth <= 0) { owPos[k * 3 + 1] = level; continue; }
            if (j > jMax) { owPos[k * 3 + 1] = level; continue; }
            const b6 = k * 6;
            const sw3 =
              (owBasis[b6] * ct1 + owBasis[b6 + 1] * st1) * 0.55 +
              (owBasis[b6 + 2] * ct2 - owBasis[b6 + 3] * st2) * 0.36 +
              (owBasis[b6 + 4] * ct3 + owBasis[b6 + 5] * st3) * 0.2;
            owPos[k * 3 + 1] = level + sw3 * swellAmp * Math.min(1, depth / 0.8);
          }
        }
        owGeo.attributes.position.needsUpdate = true;
      }
      // Colours only depend on depth, so they only need rewriting when a level moved.
      if (levelsChanged || owColDirty) {
        owColDirty = false;
        for (let p = 0; p < P; p++) {
          const sw = outerBorderSurf[p];
          for (let j = 0; j <= OR; j++) {
            const k = j * P + p;
            const sm = outSmoothY[k];
            const sea = sw + (SEA_LEVEL - sw) * waterRelax[j];
            const depth =
              Math.max(sea, sea + (sm + bgDepth - sea) * _smooth(-0.2, 0.9, sm)) - outBaseY[k];
            if (depth <= 0) { owCol[k * 4 + 3] = 0; continue; }
            const kk = Math.min(1, Math.max(0, _smooth(0.02, 1.7, depth) + owTint[k] * 1.1));
            owCol[k * 4] = wcS[0] + (wcD[0] - wcS[0]) * kk;
            owCol[k * 4 + 1] = wcS[1] + (wcD[1] - wcS[1]) * kk;
            owCol[k * 4 + 2] = wcS[2] + (wcD[2] - wcS[2]) * kk;
            owCol[k * 4 + 3] = 0.25 + 0.7 * kk;
          }
        }
        owGeo.attributes.color.needsUpdate = true;
      }

      // The surrounding land soaks through with the play area. Its colours are
      // static geometry, so only repaint when the wetness has actually moved.
      if (Math.abs(wetness - lastWetness) > 0.004) {
        lastWetness = wetness;
        for (let k = 0; k < (OR + 1) * P; k++) {
          const base = outBaseCol[k * 3], baseG = outBaseCol[k * 3 + 1], baseB = outBaseCol[k * 3 + 2];
          outCol[k * 3] = base * wetMul;
          outCol[k * 3 + 1] = baseG * wetMul;
          outCol[k * 3 + 2] = baseB * wetMul * wetCool;
        }
        outGeo.attributes.color.needsUpdate = true;
      }

      // Boats ride the surface they're floating on, swell and all.
      for (const b of boatAnchors) {
        b.obj.position.y = SEA_LEVEL + swellAt(b.x, b.z, time) * swellAmp;
        b.obj.rotation.z = swellAt(b.x + 1.2, b.z, time) * swellAmp * 0.35;
      }

      // Sync Mangroves & Drains
      if (currentMangCount !== lastMangCount || currentDrnCount !== lastDrnCount) {
        lastMangCount = currentMangCount;
        lastDrnCount = currentDrnCount;

        mangGroup.clear();
        drnGroup.clear();
        if (!mangProto) mangProto = flattenProp(createMangroveTree());
        if (!drainProto) drainProto = flattenProp(createDrain());

        for (let y = 0; y < H; y++) {
          for (let x = 0; x < W; x++) {
            const i = y * W + x;
            const gx = x - W / 2 + 0.5;
            const gz = y - H / 2 + 0.5;
            const e = elev[i];

            if (sim.mang[i]) {
              const tree = new THREE.Mesh(mangProto.geometry, mangProto.material);
              tree.position.set(gx, e, gz);
              mangGroup.add(tree);
            }
            if (sim.drn[i]) {
              const drain = new THREE.Mesh(drainProto.geometry, drainProto.material);
              drain.position.set(gx, e, gz);
              drnGroup.add(drain);
            }
          }
        }
      }

      // Populate Props
      if (lastPresetType !== presetType) {
        lastPresetType = presetType;
        propsGroup.clear();
        boatAnchors.length = 0;

        // Seeded: props must land in the same spots every rebuild (adding one
        // house re-runs this effect) or the whole world visibly reshuffles.
        const prnd = seededRng(strSeed("props:" + presetType));

        // The court is a rigid 4x3-cell slab, so it needs genuinely flat, dry
        // ground. It used to be hard-coded to x = 0.48W — which on the urban map
        // is exactly where the canal runs, so it spawned in the water.
        {
          let best = null;
          for (let gy = 6; gy < H - 6; gy++) {
            for (let gx = 6; gx < W - 6; gx++) {
              let lo = Infinity, hi = -Infinity;
              for (let dy = -2; dy <= 2; dy++) {
                for (let dx = -2; dx <= 2; dx++) {
                  const e = elev[(gy + dy) * W + gx + dx];
                  if (e < lo) lo = e;
                  if (e > hi) hi = e;
                }
              }
              if (lo < 0.45) continue; // must be dry, with freeboard
              // Prefer flat, and prefer sitting near the middle of the barangay.
              const score = (hi - lo) + Math.hypot(gx - W * 0.5, gy - H * 0.5) * 0.012;
              if (!best || score < best.score) best = { gx, gy, hi, score };
            }
          }
          if (best) {
            const cproto = flattenProp(createBasketballCourt());
            const bball = new THREE.Mesh(cproto.geometry, cproto.material);
            bball.receiveShadow = true;
            bball.position.set(best.gx - W / 2, best.hi + 0.08, best.gy - H / 2);
            propsGroup.add(bball);
          }
        }

        // Bilinear height lookup in world space — roads and paths have to follow
        // the ground exactly or they slice through every rise they cross.
        const elevAt = (wx, wz) => {
          const fx = Math.min(Math.max(wx + W / 2, 0), W - 1.001);
          const fz = Math.min(Math.max(wz + H / 2, 0), H - 1.001);
          const x0 = Math.floor(fx), z0 = Math.floor(fz);
          const tx = fx - x0, tz = fz - z0;
          const e00 = elev[z0 * W + x0], e10 = elev[z0 * W + x0 + 1];
          const e01 = elev[(z0 + 1) * W + x0], e11 = elev[(z0 + 1) * W + x0 + 1];
          return (e00 * (1 - tx) + e10 * tx) * (1 - tz) + (e01 * (1 - tx) + e11 * tx) * tz;
        };

        // Keep the scatter out of people's yards: anything within this many cells
        // of a house is reserved, so bushes never grow through a wall.
        const occupied = new Set();
        const reserve = (cx, cy, r) => {
          for (let dy = -r; dy <= r; dy++) {
            for (let dx = -r; dx <= r; dx++) occupied.add((cy + dy) * W + (cx + dx));
          }
        };
        houses.forEach((h) => reserve(Math.floor(h.x), Math.floor(h.y), 2));

        // Generic scatter: sample random cells, keep the ones that pass `ok`, and
        // return placements ready for one InstancedMesh.
        const gather = (want, ok, opts = {}) => {
          const out = [];
          const tries = want * 24;
          const { minS = 0.85, maxS = 1.2, jitter = 0.42, reserveR = 0, yOff = 0 } = opts;
          for (let a = 0; a < tries && out.length < want; a++) {
            const px = 3 + Math.floor(prnd() * (W - 6));
            const py = 3 + Math.floor(prnd() * (H - 6));
            const i = py * W + px;
            if (occupied.has(i)) continue;
            if (!ok(elev[i], px, py)) continue;
            if (reserveR) reserve(px, py, reserveR);
            out.push({
              x: px - W / 2 + 0.5 + (prnd() - 0.5) * jitter,
              y: elev[i] + yOff,
              z: py - H / 2 + 0.5 + (prnd() - 0.5) * jitter,
              ry: prnd() * Math.PI * 2,
              s: minS + prnd() * (maxS - minS),
            });
          }
          return out;
        };

        const addScatter = (key, build, placements, opts) => {
          const inst = scatterInstanced(getProto(key, build), placements, opts);
          if (inst) propsGroup.add(inst);
        };

        const isVillage = presetType !== "urban" && presetType !== "basin";
        // Dry, walkable ground: the band everything vegetal and man-made lives in.
        const dryLand = (e) => e > 0.35 && e < 3.4;

        if (isVillage) {
          // Palms are the signature silhouette of the coast, so there are enough
          // of them to form groves rather than a dozen lonely sticks — but each
          // one now reserves its own cell, because a canopy dense enough to hide
          // the village is no better than an empty field.
          addScatter("palm", () => createCoconutPalm(prnd),
            gather(40, (e) => e > 0.4 && e < 3.0, { minS: 0.8, maxS: 1.25, reserveR: 2 }));

          addScatter("banana", () => createBananaPlant(prnd),
            gather(34, (e) => e > 0.5 && e < 2.6, { minS: 0.85, maxS: 1.3, reserveR: 1 }));

          // The bulk of the fill. Bushes and tufts are what stop the big greens
          // from rendering as unbroken sheets of one colour, and being low they
          // add texture without ever occluding a building.
          addScatter("bush", () => createBush(prnd),
            gather(140, dryLand, { minS: 0.8, maxS: 1.4 }), { shadow: false });
          addScatter("tuft", () => createGrassTuft(prnd),
            gather(260, (e) => e > 0.2 && e < 3.6, { minS: 0.8, maxS: 1.6, jitter: 0.7 }),
            { shadow: false });

          // Rocks cluster on the steeper ground and along the waterline.
          addScatter("rock", () => createRock(prnd),
            gather(70, (e) => e > 0.1 && e < 3.8, { minS: 0.8, maxS: 1.7 }), { shadow: false });

          // Driftwood and stranded gear right at the tideline.
          addScatter("drift", () => createDriftwood(prnd),
            gather(34, (e) => e > -0.35 && e < 0.55, { minS: 0.9, maxS: 1.5 }), { shadow: false });

          // Yards: a fence and a laundry line beside some of the homes. Placed
          // relative to the houses rather than at random, because the point of
          // them is to make the houses look lived in.
          const fenceSpots = [];
          const laundrySpots = [];
          houses.forEach((h, hi) => {
            const e = elev[Math.floor(h.y) * W + Math.floor(h.x)];
            if (e < 0.45) return;
            const bx = h.x - W / 2 + 0.5, bz = h.y - H / 2 + 0.5;
            if (hi % 2 === 0) {
              const a = prnd() * Math.PI * 2;
              fenceSpots.push({ x: bx + Math.cos(a) * 2.3, y: e, z: bz + Math.sin(a) * 2.3, ry: a + Math.PI / 2, s: 1 });
            }
            if (hi % 3 === 1) {
              const a = prnd() * Math.PI * 2;
              laundrySpots.push({ x: bx + Math.cos(a) * 2.6, y: e, z: bz + Math.sin(a) * 2.6, ry: prnd() * Math.PI, s: 1 });
            }
          });
          addScatter("fence", () => createFence(), fenceSpots, { shadow: false });
          addScatter("laundry", () => createLaundryLine(prnd), laundrySpots, { shadow: false });
        } else {
          // ── Urban: streets, poles and street furniture ──────────────────────
          // The city preset was the worst offender — a flat grey plane with dark
          // specks on it. What a barangay street actually has is asphalt, a
          // forest of utility poles, and clutter along the kerb.
          const roadMat = new THREE.MeshStandardMaterial({ color: 0x4a4a4d, roughness: 0.95 });
          const markMat = new THREE.MeshStandardMaterial({ color: 0xe8e2cf, roughness: 0.9 });
          const canalX = W * 0.48;

          // Ribbon geometry that samples the heightmap, so the road drapes over
          // the ground instead of guillotining it.
          const buildRoad = (pts, halfW, y0) => {
            const pos = [], idx = [];
            for (let s = 0; s < pts.length; s++) {
              const p = pts[s];
              const q = pts[Math.min(s + 1, pts.length - 1)];
              const r = pts[Math.max(s - 1, 0)];
              let dx = q.x - r.x, dz = q.z - r.z;
              const l = Math.hypot(dx, dz) || 1;
              dx /= l; dz /= l;
              const nx = -dz, nz = dx;
              for (const sgn of [-1, 1]) {
                const wx = p.x + nx * halfW * sgn;
                const wz = p.z + nz * halfW * sgn;
                pos.push(wx, elevAt(wx, wz) + y0, wz);
              }
            }
            for (let s = 0; s < pts.length - 1; s++) {
              const a = s * 2, b = s * 2 + 1, c = s * 2 + 2, d = s * 2 + 3;
              idx.push(a, c, b, b, c, d);
            }
            const geo = new THREE.BufferGeometry();
            geo.setAttribute("position", new THREE.Float32BufferAttribute(pos, 3));
            geo.setIndex(idx);
            geo.computeVertexNormals();
            return geo;
          };

          const roadRows = [15, 25, 35, 45, 55];
          const roadParts = [], markParts = [];
          for (const gy of roadRows) {
            // Two carriageways so the canal is spanned by a gap, not paved over.
            for (const [x0, x1] of [[6, canalX - 3.2], [canalX + 3.2, W - 6]]) {
              const pts = [];
              for (let x = x0; x <= x1; x += 2) pts.push({ x: x - W / 2, z: gy - H / 2 });
              if (pts.length < 2) continue;
              roadParts.push(buildRoad(pts, 1.5, 0.07));
              // Dashed centre line.
              for (let x = x0 + 1; x < x1 - 1; x += 5) {
                const seg = [{ x: x - W / 2, z: gy - H / 2 }, { x: x + 2 - W / 2, z: gy - H / 2 }];
                markParts.push(buildRoad(seg, 0.09, 0.1));
              }
            }
          }
          // One cross street on the dry side of the canal.
          {
            const pts = [];
            for (let y = 8; y <= H - 8; y += 2) pts.push({ x: 20 - W / 2, z: y - H / 2 });
            roadParts.push(buildRoad(pts, 1.4, 0.07));
            const pts2 = [];
            for (let y = 8; y <= H - 8; y += 2) pts2.push({ x: 72 - W / 2, z: y - H / 2 });
            roadParts.push(buildRoad(pts2, 1.4, 0.07));
          }
          if (roadParts.length) {
            const rm = new THREE.Mesh(mergeGeometries(roadParts, false), roadMat);
            rm.receiveShadow = true;
            rm.renderOrder = 0;
            propsGroup.add(rm);
          }
          if (markParts.length) {
            propsGroup.add(new THREE.Mesh(mergeGeometries(markParts, false), markMat));
          }

          // Footbridges over the canal on every road line.
          {
            const bridgeParts = [];
            for (const gy of roadRows) {
              const bx = canalX - W / 2;
              const bz = gy - H / 2;
              const deck = new THREE.BoxGeometry(8.0, 0.22, 3.0);
              deck.translate(bx, Math.max(elevAt(bx, bz), 0) + 0.75, bz);
              bridgeParts.push(deck);
              for (const rz of [-1.4, 1.4]) {
                const rail = new THREE.BoxGeometry(8.0, 0.5, 0.14);
                rail.translate(bx, Math.max(elevAt(bx, bz), 0) + 1.1, bz + rz);
                bridgeParts.push(rail);
              }
            }
            const bm = new THREE.Mesh(mergeGeometries(bridgeParts, false), concreteMat);
            bm.castShadow = true;
            bm.receiveShadow = true;
            propsGroup.add(bm);
          }

          // Utility poles marching down both sides of every street. These are the
          // verticals the flat city was completely missing.
          const lampSpots = [];
          for (const gy of roadRows) {
            for (let x = 8; x < W - 8; x += 7) {
              if (Math.abs(x - canalX) < 5) continue;
              const side = (x / 7) % 2 < 1 ? -1 : 1;
              const wx = x - W / 2, wz = gy - H / 2 + side * 2.4;
              lampSpots.push({ x: wx, y: elevAt(wx, wz), z: wz, ry: side > 0 ? Math.PI : 0, s: 0.95 + prnd() * 0.2 });
            }
          }
          addScatter("lamp", () => createStreetLamp(), lampSpots);

          // Kerbside life: stalls, parked tricycles, planters.
          const stallSpots = [], trikeSpots = [];
          for (const gy of roadRows) {
            for (let x = 12; x < W - 12; x += 9) {
              if (Math.abs(x - canalX) < 6) continue;
              if (prnd() > 0.55) {
                const wx = x - W / 2 + prnd() * 2, wz = gy - H / 2 + (prnd() > 0.5 ? 2.7 : -2.7);
                stallSpots.push({ x: wx, y: elevAt(wx, wz), z: wz, ry: prnd() * Math.PI * 2, s: 1 });
              }
              if (prnd() > 0.45) {
                const wx = x - W / 2 + prnd() * 3, wz = gy - H / 2 + (prnd() > 0.5 ? 1.9 : -1.9);
                trikeSpots.push({ x: wx, y: elevAt(wx, wz), z: wz, ry: prnd() * 0.5 + (prnd() > 0.5 ? 0 : Math.PI), s: 1 });
              }
            }
          }
          addScatter("stall", () => createMarketStall(prnd), stallSpots);
          addScatter("trike", () => createTricycle(prnd), trikeSpots);

          // Even a paved barangay has weeds, potted plants and rubble.
          addScatter("bush", () => createBush(prnd),
            gather(110, dryLand, { minS: 0.7, maxS: 1.2 }), { shadow: false });
          addScatter("tuft", () => createGrassTuft(prnd),
            gather(160, (e) => e > 0.25 && e < 3.6, { minS: 0.7, maxS: 1.3, jitter: 0.7 }),
            { shadow: false });
          addScatter("rock", () => createRock(prnd),
            gather(50, (e) => e > 0.2 && e < 3.8, { minS: 0.7, maxS: 1.3 }), { shadow: false });
          // A few palms survive along the canal, as they do in real Metro Manila.
          addScatter("palm", () => createCoconutPalm(prnd),
            gather(16, (e, px) => e > 0.45 && e < 2.4 && Math.abs(px - canalX) < 14,
              { minS: 0.8, maxS: 1.1, reserveR: 1 }));
        }

        // Boats belong in water. They used to be dropped at fixed columns on one
        // fixed row, which on both water maps left them beached on dry grass.
        // Kept as individual meshes rather than instanced: each one rides the
        // swell independently, so each needs its own transform every frame.
        {
          let placed = 0;
          const want = isVillage ? 8 : 3;
          const taken = [];
          for (let attempt = 0; attempt < 900 && placed < want; attempt++) {
            const px = 6 + Math.floor(prnd() * (W - 12));
            const py = 6 + Math.floor(prnd() * (H - 12));
            const e = elev[py * W + px];
            // The hull is 0.34 deep and the outriggers sit lower still, so a
            // boat needs real draught under it — the old -0.3 cut-off left them
            // sitting on the beach shelf looking stranded. Spread them out too:
            // eight boats in one huddle is not a fishing village.
            const clear = taken.every((t) => Math.hypot(t.x - px, t.y - py) > 7);
            if (e > -2.6 && e < -0.85 && clear) {
              taken.push({ x: px, y: py });
              const bproto = getProto("boat" + (placed % 4), () => createBangkaBoat(prnd));
              const boat = new THREE.Mesh(bproto.geometry, bproto.material);
              boat.rotation.y = prnd() * Math.PI * 2;
              boat.castShadow = true;
              boat.position.set(px - W / 2, SEA_LEVEL, py - H / 2);
              propsGroup.add(boat);
              boatAnchors.push({ obj: boat, x: px - W / 2, z: py - H / 2 });
              placed++;
            }
          }
        }
      }

      // Sync 3D Barangay Houses
      const houseKey = houses.map((h) => `${h.x},${h.y},${h.style || ""}`).join("|");
      if (houseKey !== lastHouseKey) {
        lastHouseKey = houseKey;
        hseGroup.clear();

        // Each building is now 25-60 primitives instead of four, so it is merged
        // down to one multi-material mesh and the merge is cached per style
        // variant. Without that, nineteen detailed apartments would have cost a
        // thousand draw calls.
        const houseSpec = (style, idx) => {
          if (style === "nipa") return { key: `nipa${idx % 2}`, build: () => createNipaHut(idx), flagY: 3.6 };
          if (style === "apartment") return { key: `apt${idx % 4}`, build: () => createUrbanApartment(idx), flagY: 1.05 * (3 + (idx % 2)) + 1.5 };
          if (style === "store") return { key: "store", build: () => createSariSariStore(), flagY: 2.6 };
          if (style === "hall") return { key: "hall", build: () => createBarangayHall(), flagY: 3.3 };
          return { key: `town${idx % houseWallColors.length}`, build: () => createTownhouse(idx), flagY: 2.9 };
        };

        houses.forEach((h, idx) => {
          const spec = houseSpec(h.style, idx);
          const proto = getProto("house:" + spec.key, spec.build);

          const house = new THREE.Group();
          const shell = new THREE.Mesh(proto.geometry, proto.material);
          shell.castShadow = true;
          shell.receiveShadow = true;
          house.add(shell);

          // The flood-warning flag rides above the roof as its own object so it
          // can be toggled without rebuilding the merged shell.
          const fproto = getProto("flag", createWarningFlag);
          const flag = new THREE.Mesh(fproto.geometry, fproto.material);
          flag.name = "warningFlag";
          flag.position.y = spec.flagY;
          flag.visible = false;
          house.add(flag);

          const gx = h.x - W / 2 + 0.5;
          const gz = h.y - H / 2 + 0.5;
          const gridX = Math.floor(h.x);
          const gridY = Math.floor(h.y);
          const e = elev[gridY * W + gridX] || 0;
          house.position.set(gx, e, gz);
          // A small deterministic yaw stops a row of identical houses from
          // reading as a spreadsheet.
          house.rotation.y = ((strSeed(`${h.x},${h.y}`) % 1000) / 1000 - 0.5) * 0.5;
          hseGroup.add(house);
        });
      }

      // Update warning flags
      hseGroup.children.forEach((house, idx) => {
        const h = houses[idx];
        if (!h) return;
        const gridX = Math.floor(h.x);
        const gridY = Math.floor(h.y);
        const w = water[gridY * W + gridX] || 0;
        const flag = house.getObjectByName("warningFlag");
        if (flag) flag.visible = w > 0.08;
      });

      // Raycaster 3D Brush Tracking (DISABLED in View/Pan Mode)
      const isPanTool = toolRef.current === "view" || toolRef.current === "pan";

      if (!isPanTool) {
        raycasterRef.current.setFromCamera(mouseRef.current, camera);
        const intersects = raycasterRef.current.intersectObject(terrainMesh);

        if (intersects.length > 0) {
          const pt = intersects[0].point;
          brushRing.visible = true;
          brushRing.position.set(pt.x, pt.y + 0.08, pt.z);

          const gridX = Math.floor(pt.x + W / 2);
          const gridY = Math.floor(pt.z + H / 2);

          if (gridX >= 0 && gridX < W && gridY >= 0 && gridY < H) {
            if (onHoverCell) onHoverCell(gridX, gridY);
            if (isMouseDownRef.current && onPaint) {
              onPaint(gridX, gridY);
            }
          }
        } else {
          brushRing.visible = false;
        }
      } else {
        brushRing.visible = false;
      }

      controls.update();
      composer.render();
    };

    animate();

    const handleResize = () => {
      if (!containerRef.current) return;
      const w = containerRef.current.clientWidth || window.innerWidth;
      const h = containerRef.current.clientHeight || window.innerHeight;
      camera.aspect = w / h;
      camera.updateProjectionMatrix();
      renderer.setSize(w, h);
      // The composer owns its own render targets, so it has to be resized too —
      // otherwise the AO buffer keeps the old dimensions and the effect drifts
      // out of alignment with the image.
      composer.setSize(w, h);
      gtao.setSize(w, h);
    };
    window.addEventListener("resize", handleResize);

    return () => {
      cancelAnimationFrame(animId);
      window.removeEventListener("resize", handleResize);
      composer.dispose();
      renderer.dispose();
      container.innerHTML = "";
    };
  }, [simRef, storm, rain, cameraPreset, houses, presetType, onPaint, onHoverCell]);

  const handlePointerMove = (e) => {
    if (!containerRef.current) return;
    const rect = containerRef.current.getBoundingClientRect();
    mouseRef.current.x = ((e.clientX - rect.left) / rect.width) * 2 - 1;
    mouseRef.current.y = -((e.clientY - rect.top) / rect.height) * 2 + 1;
  };

  const handlePointerDown = (e) => {
    if (e.button !== 0) return;
    isMouseDownRef.current = true;
    handlePointerMove(e);
  };

  const handlePointerUp = () => {
    isMouseDownRef.current = false;
  };

  const isPanTool = tool === "view" || tool === "pan";

  return (
    <div
      ref={containerRef}
      style={{
        position: "absolute",
        inset: 0,
        width: "100%",
        height: "100%",
        overflow: "hidden",
        cursor: isPanTool ? "grab" : "crosshair",
      }}
      onPointerMove={handlePointerMove}
      onPointerDown={handlePointerDown}
      onPointerUp={handlePointerUp}
      onPointerLeave={handlePointerUp}
    />
  );
}
