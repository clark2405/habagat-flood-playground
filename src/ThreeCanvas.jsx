import React, { useRef, useEffect } from "react";
import * as THREE from "three";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { mergeGeometries } from "three/addons/utils/BufferGeometryUtils.js";

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
const groundTint = (wx, wz) =>
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
    fogNear: 155,
    fogFar: 425,
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
    fogNear: 155,
    fogFar: 420,
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
    fogNear: 150,
    fogFar: 415,
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
    fogNear: 155,
    fogFar: 426,
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

    const camera = new THREE.PerspectiveCamera(40, width / height, 0.1, 1000);
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

    // 3. OrbitControls (Strict Camera Limits so user never dips below horizon)
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.dampingFactor = 0.05;
    controls.minDistance = 15;
    controls.maxDistance = 120;
    controls.minPolarAngle = Math.PI / 12;
    // Keep the eye a little higher than before: the world now continues past the
    // sandbox in every direction, and a near-ground camera would look *through*
    // that surrounding land instead of across it.
    controls.maxPolarAngle = Math.PI / 2.32;
    controls.target.set(0, 0, 0);

    if (cameraPreset === "isometric") {
      camera.position.set(42, 42, 42);
    } else if (cameraPreset === "top") {
      camera.position.set(0, 72, 0.1);
    } else if (cameraPreset === "close") {
      camera.position.set(-14, 18, 22);
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
      // Pre-hazed toward the fog colour so even the un-fogged parts read as far away.
      const hazed = new THREE.Color(cfg.color).lerp(new THREE.Color(env.fog), 0.62);
      // The backdrop never moves, so it is baked into a single geometry — 190
      // separate building meshes is 190 draw calls for scenery nobody touches.
      const parts = [];
      const stamp = (geo, x, y, z) => {
        geo.translate(x, y, z);
        parts.push(geo);
      };
      if (cfg.kind === "city") {
        const cityHaze = new THREE.Color(cfg.color).lerp(new THREE.Color(env.fog), 0.35);
        const cityMat = new THREE.MeshStandardMaterial({ color: cityHaze, roughness: 0.9, flatShading: true });
        backdropMats.push({ mat: cityMat, base: new THREE.Color(cfg.color), haze: 0.35 });
        for (let i = 0; i < cfg.count; i++) {
          const angle = rnd() * Math.PI * 2;
          const radius = cfg.radius + rnd() * cfg.spread;
          const bh = 20 + rnd() * 80;
          const bw = 10 + rnd() * 20;
          stamp(new THREE.BoxGeometry(bw, bh, bw), Math.cos(angle) * radius, bh / 2 - 6, Math.sin(angle) * radius);
        }
        group.add(new THREE.Mesh(mergeGeometries(parts, false), cityMat));
        return;
      }
      // mountains / forested peaks
      const mat = new THREE.MeshStandardMaterial({ color: hazed, roughness: 0.95, flatShading: true });
      backdropMats.push({ mat, base: new THREE.Color(cfg.color), haze: 0.62 });
      for (let m = 0; m < cfg.count; m++) {
        const angle = (m / cfg.count) * Math.PI * 2 + rnd() * 0.4;
        const radius = cfg.radius + rnd() * cfg.spread;
        const mtx = Math.cos(angle) * radius;
        const mtz = Math.sin(angle) * radius;
        if (cfg.behindOnly && mtz > -40) continue; // keep the bay open toward the camera
        const height = cfg.height[0] + rnd() * (cfg.height[1] - cfg.height[0]);
        const widthM = 40 + rnd() * 34;
        stamp(new THREE.ConeGeometry(widthM, height, 5), mtx, height / 2 - 8, mtz);
      }
      if (parts.length) group.add(new THREE.Mesh(mergeGeometries(parts, false), mat));
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

    // Reusable Materials
    const bambooMat = new THREE.MeshStandardMaterial({ color: 0x8b5e3c, roughness: 0.9 });
    const nipaMat = new THREE.MeshStandardMaterial({ color: 0xc49a45, roughness: 0.95 });
    const sawaliMat = new THREE.MeshStandardMaterial({ color: 0xe6cd9c, roughness: 0.8 });

    const houseRoofColors = [0xd9442b, 0x4a90e2, 0x5b8c3a, 0xe07a5f, 0x9b59b6];
    const houseWallColors = [0xf4f1de, 0xe0e1dd, 0xfceade, 0xe2ece9, 0xfff1e6];

    const drainBoxMat = new THREE.MeshStandardMaterial({ color: 0x5c6770 });
    const drainGrateMat = new THREE.MeshBasicMaterial({ color: 0x3ba99c, side: THREE.DoubleSide });
    const warningFlagMat = new THREE.MeshBasicMaterial({ color: 0xd9442b });

    const woodMat = new THREE.MeshStandardMaterial({ color: 0x5c4033 });
    const palmLeafMat = new THREE.MeshStandardMaterial({ color: 0x477a3d, roughness: 0.6 });

    // Barangay Building Creators
    const createNipaHut = () => {
      const g = new THREE.Group();
      for (let sx of [-0.25, 0.25]) {
        for (let sz of [-0.25, 0.25]) {
          const stilt = new THREE.Mesh(new THREE.CylinderGeometry(0.04, 0.04, 0.35, 4), bambooMat);
          stilt.position.set(sx, 0.175, sz);
          g.add(stilt);
        }
      }
      const body = new THREE.Mesh(new THREE.BoxGeometry(0.65, 0.45, 0.65), sawaliMat);
      body.position.y = 0.55;
      g.add(body);

      const roof = new THREE.Mesh(new THREE.ConeGeometry(0.62, 0.45, 4), nipaMat);
      roof.position.y = 0.95;
      roof.rotation.y = Math.PI / 4;
      g.add(roof);

      const flag = new THREE.Mesh(new THREE.ConeGeometry(0.12, 0.25, 3), warningFlagMat);
      flag.name = "warningFlag";
      flag.position.set(0, 1.25, 0);
      flag.rotation.x = Math.PI;
      flag.visible = false;
      g.add(flag);

      return g;
    };

    const createTownhouse = (id) => {
      const g = new THREE.Group();
      const wallColor = houseWallColors[id % houseWallColors.length];
      const roofColor = houseRoofColors[id % houseRoofColors.length];

      const wallMat = new THREE.MeshStandardMaterial({ color: wallColor, roughness: 0.7 });
      const roofMat = new THREE.MeshStandardMaterial({ color: roofColor, roughness: 0.6 });

      const body = new THREE.Mesh(new THREE.BoxGeometry(0.75, 0.55, 0.75), wallMat);
      body.position.y = 0.275;
      g.add(body);

      const roof = new THREE.Mesh(new THREE.ConeGeometry(0.6, 0.38, 4), roofMat);
      roof.position.y = 0.72;
      roof.rotation.y = Math.PI / 4;
      g.add(roof);

      const door = new THREE.Mesh(new THREE.BoxGeometry(0.18, 0.3, 0.05), woodMat);
      door.position.set(0, 0.15, 0.38);
      g.add(door);

      const flag = new THREE.Mesh(new THREE.ConeGeometry(0.12, 0.25, 3), warningFlagMat);
      flag.name = "warningFlag";
      flag.position.set(0, 1.0, 0);
      flag.rotation.x = Math.PI;
      flag.visible = false;
      g.add(flag);

      return g;
    };

    const createUrbanApartment = () => {
      const g = new THREE.Group();
      const wallMat = new THREE.MeshStandardMaterial({ color: 0x6e7882, roughness: 0.5 });
      const roofMat = new THREE.MeshStandardMaterial({ color: 0x2b3a4b, roughness: 0.4 });

      const body = new THREE.Mesh(new THREE.BoxGeometry(0.9, 0.9, 0.9), wallMat);
      body.position.y = 0.45;
      g.add(body);

      const roof = new THREE.Mesh(new THREE.BoxGeometry(0.95, 0.1, 0.95), roofMat);
      roof.position.y = 0.95;
      g.add(roof);

      const flag = new THREE.Mesh(new THREE.ConeGeometry(0.12, 0.25, 3), warningFlagMat);
      flag.name = "warningFlag";
      flag.position.set(0, 1.15, 0);
      flag.rotation.x = Math.PI;
      flag.visible = false;
      g.add(flag);

      return g;
    };

    const createSariSariStore = () => {
      const g = new THREE.Group();
      const wallMat = new THREE.MeshStandardMaterial({ color: 0xf4a261, roughness: 0.7 });
      const awningMat = new THREE.MeshStandardMaterial({ color: 0xe76f51, roughness: 0.5 });

      const body = new THREE.Mesh(new THREE.BoxGeometry(0.8, 0.5, 0.8), wallMat);
      body.position.y = 0.25;
      g.add(body);

      const awning = new THREE.Mesh(new THREE.BoxGeometry(0.85, 0.06, 0.3), awningMat);
      awning.position.set(0, 0.45, 0.45);
      awning.rotation.x = 0.2;
      g.add(awning);

      const flag = new THREE.Mesh(new THREE.ConeGeometry(0.12, 0.25, 3), warningFlagMat);
      flag.name = "warningFlag";
      flag.position.set(0, 0.95, 0);
      flag.rotation.x = Math.PI;
      flag.visible = false;
      g.add(flag);

      return g;
    };

    const createBarangayHall = () => {
      const g = new THREE.Group();
      const hallWallMat = new THREE.MeshStandardMaterial({ color: 0x4a90e2, roughness: 0.6 });
      const hallRoofMat = new THREE.MeshStandardMaterial({ color: 0x2b3a4b, roughness: 0.5 });

      const body = new THREE.Mesh(new THREE.BoxGeometry(1.2, 0.7, 0.95), hallWallMat);
      body.position.y = 0.35;
      g.add(body);

      const roof = new THREE.Mesh(new THREE.ConeGeometry(0.9, 0.48, 4), hallRoofMat);
      roof.position.y = 0.9;
      roof.rotation.y = Math.PI / 4;
      g.add(roof);

      const sandbagMat = new THREE.MeshStandardMaterial({ color: 0xd9c5a0 });
      for (let bx = -0.45; bx <= 0.45; bx += 0.25) {
        const bag = new THREE.Mesh(new THREE.BoxGeometry(0.22, 0.1, 0.12), sandbagMat);
        bag.position.set(bx, 0.05, 0.52);
        g.add(bag);
      }

      const flag = new THREE.Mesh(new THREE.ConeGeometry(0.14, 0.3, 3), warningFlagMat);
      flag.name = "warningFlag";
      flag.position.set(0, 1.25, 0);
      flag.rotation.x = Math.PI;
      flag.visible = false;
      g.add(flag);

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

    // Props
    const createCoconutPalm = (rnd) => {
      const g = new THREE.Group();
      const lean = (rnd() - 0.5) * 0.3;
      const trunk = new THREE.Mesh(new THREE.CylinderGeometry(0.055, 0.11, 1.25, 6), woodMat);
      trunk.position.y = 0.62;
      trunk.rotation.z = lean;
      g.add(trunk);

      // Crown of drooping fronds: two rings of tapered blades rather than one ring
      // of flat slabs, which from any distance just read as an asterisk.
      const crown = new THREE.Group();
      crown.position.set(Math.sin(lean) * -0.62, 1.24, 0);
      for (let ring = 0; ring < 2; ring++) {
        const n = ring === 0 ? 5 : 4;
        for (let a = 0; a < n; a++) {
          const angle = (a / n) * Math.PI * 2 + ring * 0.6 + rnd() * 0.2;
          const len = ring === 0 ? 0.62 : 0.44;
          const frond = new THREE.Mesh(new THREE.CylinderGeometry(0.09, 0.015, len, 3), palmLeafMat);
          frond.scale.set(1, 1, 0.32);
          frond.position.set(Math.cos(angle) * len * 0.42, ring === 0 ? -0.03 : 0.09, Math.sin(angle) * len * 0.42);
          frond.rotation.set(Math.PI / 2, -angle, ring === 0 ? 0.55 : 0.95);
          crown.add(frond);
        }
      }
      g.add(crown);
      g.rotation.y = rnd() * Math.PI * 2;
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
      const slab = new THREE.Mesh(new THREE.BoxGeometry(3.6, 0.16, 2.3), courtMat);
      slab.position.y = -0.02;
      slab.receiveShadow = true;
      g.add(slab);

      const halfway = new THREE.Mesh(new THREE.BoxGeometry(0.06, 0.02, 2.24), lineMat);
      halfway.position.y = 0.065;
      g.add(halfway);
      const circle = new THREE.Mesh(new THREE.TorusGeometry(0.45, 0.03, 4, 16), lineMat);
      circle.rotation.x = -Math.PI / 2;
      circle.position.y = 0.065;
      g.add(circle);

      for (const side of [-1.62, 1.62]) {
        const inward = side > 0 ? -1 : 1;
        const pole = new THREE.Mesh(new THREE.CylinderGeometry(0.055, 0.07, 1.15, 6), drainBoxMat);
        pole.position.set(side, 0.6, 0);
        pole.castShadow = true;
        g.add(pole);
        const arm = new THREE.Mesh(new THREE.BoxGeometry(0.22, 0.05, 0.05), drainBoxMat);
        arm.position.set(side + inward * 0.11, 1.1, 0);
        g.add(arm);
        const board = new THREE.Mesh(new THREE.BoxGeometry(0.05, 0.34, 0.46), sawaliMat);
        board.position.set(side + inward * 0.22, 1.08, 0);
        g.add(board);
        const rim = new THREE.Mesh(new THREE.TorusGeometry(0.11, 0.018, 4, 10), warningFlagMat);
        rim.rotation.x = -Math.PI / 2;
        rim.position.set(side + inward * 0.34, 0.96, 0);
        g.add(rim);
      }
      return g;
    };

    const createBangkaBoat = () => {
      const g = new THREE.Group();
      const hullMat = new THREE.MeshStandardMaterial({ color: 0x4a90e2, roughness: 0.6 });
      // Tapered hull + a bow, so it reads as a boat from above instead of a brick.
      const hull = new THREE.Mesh(new THREE.BoxGeometry(1.15, 0.16, 0.26), hullMat);
      hull.position.y = 0.06;
      g.add(hull);
      const bow = new THREE.Mesh(new THREE.ConeGeometry(0.15, 0.4, 4), hullMat);
      bow.rotation.z = -Math.PI / 2;
      bow.rotation.y = Math.PI / 4;
      bow.position.set(0.72, 0.06, 0);
      g.add(bow);
      const gunwale = new THREE.Mesh(new THREE.BoxGeometry(1.15, 0.05, 0.3), sawaliMat);
      gunwale.position.y = 0.15;
      g.add(gunwale);

      // Outriggers: floats running PARALLEL to the hull on cross-booms. The old
      // version put two long boxes across the hull, which looked like a hammer.
      for (const side of [-0.42, 0.42]) {
        const float = new THREE.Mesh(new THREE.CylinderGeometry(0.035, 0.035, 0.95, 5), woodMat);
        float.rotation.z = Math.PI / 2;
        float.position.set(0, 0.05, side);
        g.add(float);
        for (const bx of [-0.3, 0.3]) {
          const boom = new THREE.Mesh(new THREE.BoxGeometry(0.05, 0.035, Math.abs(side) + 0.05), woodMat);
          boom.position.set(bx, 0.16, side / 2);
          g.add(boom);
        }
      }
      return g;
    };

    const createMangroveTree = () => {
      const g = new THREE.Group();
      const trunk = new THREE.Mesh(new THREE.CylinderGeometry(0.08, 0.14, 0.5, 5), bambooMat);
      trunk.position.y = 0.25;
      g.add(trunk);

      const canopy = new THREE.Mesh(new THREE.DodecahedronGeometry(0.38, 1), palmLeafMat);
      canopy.position.y = 0.65;
      g.add(canopy);
      return g;
    };

    const createDrain = () => {
      const g = new THREE.Group();
      const box = new THREE.Mesh(new THREE.BoxGeometry(0.65, 0.2, 0.65), drainBoxMat);
      box.position.y = 0.1;
      g.add(box);

      const grate = new THREE.Mesh(new THREE.PlaneGeometry(0.45, 0.45), drainGrateMat);
      grate.rotation.x = -Math.PI / 2;
      grate.position.y = 0.21;
      g.add(grate);
      return g;
    };

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

        if (presetType !== "urban" && presetType !== "basin") {
          const proto = flattenProp(createCoconutPalm(prnd));
          for (let k = 0; k < 30; k++) {
            const px = Math.floor(10 + prnd() * (W - 20));
            const py = Math.floor(8 + prnd() * (H - 16));
            const i = py * W + px;
            if (elev[i] > 0.35 && elev[i] < 3.2) {
              const palm = new THREE.Mesh(proto.geometry, proto.material);
              palm.rotation.y = prnd() * Math.PI * 2;
              palm.scale.setScalar(0.85 + prnd() * 0.35);
              palm.position.set(px - W / 2, elev[i] - 0.05, py - H / 2);
              propsGroup.add(palm);
            }
          }
        }

        // Boats belong in water. They used to be dropped at fixed columns on one
        // fixed row, which on both water maps left them beached on dry grass.
        if (presetType !== "urban" && presetType !== "basin") {
          const bproto = flattenProp(createBangkaBoat());
          let placed = 0;
          for (let attempt = 0; attempt < 400 && placed < 5; attempt++) {
            const px = 6 + Math.floor(prnd() * (W - 12));
            const py = 6 + Math.floor(prnd() * (H - 12));
            const e = elev[py * W + px];
            if (e > -1.4 && e < -0.25) {
              const boat = new THREE.Mesh(bproto.geometry, bproto.material);
              boat.rotation.y = prnd() * Math.PI * 2;
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

        houses.forEach((h, idx) => {
          let house;
          if (h.style === "nipa") {
            house = createNipaHut();
          } else if (h.style === "apartment") {
            house = createUrbanApartment();
          } else if (h.style === "store") {
            house = createSariSariStore();
          } else if (h.style === "hall") {
            house = createBarangayHall();
          } else {
            house = createTownhouse(idx);
          }

          const gx = h.x - W / 2 + 0.5;
          const gz = h.y - H / 2 + 0.5;
          const gridX = Math.floor(h.x);
          const gridY = Math.floor(h.y);
          const e = elev[gridY * W + gridX] || 0;
          house.position.set(gx, e, gz);
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
      renderer.render(scene, camera);
    };

    animate();

    const handleResize = () => {
      if (!containerRef.current) return;
      const w = containerRef.current.clientWidth || window.innerWidth;
      const h = containerRef.current.clientHeight || window.innerHeight;
      camera.aspect = w / h;
      camera.updateProjectionMatrix();
      renderer.setSize(w, h);
    };
    window.addEventListener("resize", handleResize);

    return () => {
      cancelAnimationFrame(animId);
      window.removeEventListener("resize", handleResize);
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
