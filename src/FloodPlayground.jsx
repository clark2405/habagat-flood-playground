import React, { useRef, useEffect, useState, useCallback } from "react";
import ThreeCanvas from "./ThreeCanvas";
import { sound } from "./audio";
import { Icons } from "./components/Icons";

const W = 96;
const H = 64;
const FLOW_RATE = 0.15;
const MIN_WATER = 0.001;
const SEA_LEVEL = 0.0;

function makeTerrainPreset(type, seed = 1337) {
  let s = seed;
  const rnd = () => {
    s = (s * 1103515245 + 12345) & 0x7fffffff;
    return s / 0x7fffffff;
  };
  const gw = 13, gh = 9;
  const g = Array.from({ length: gh }, () => Array.from({ length: gw }, rnd));
  const lerp = (a, b, t) => a + (b - a) * (t * t * (3 - 2 * t));
  const elev = new Float32Array(W * H);

  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const gx = (x / (W - 1)) * (gw - 1), gy = (y / (H - 1)) * (gh - 1);
      const x0 = Math.floor(gx), y0 = Math.floor(gy);
      const fx = gx - x0, fy = gy - y0;
      const n = lerp(
        lerp(g[y0][x0], g[y0][Math.min(x0 + 1, gw - 1)], fx),
        lerp(g[Math.min(y0 + 1, gh - 1)][x0], g[Math.min(y0 + 1, gh - 1)][Math.min(x0 + 1, gw - 1)], fx),
        fy
      );

      const idx = y * W + x;

      if (type === "coastal") {
        const slope = 1 - y / (H - 1);
        let e = n * 2.2 + slope * 4.2 - 1.5;
        const riverX = W * 0.5 + Math.sin(y * 0.1) * 8;
        const rd = Math.abs(x - riverX);
        if (rd < 5.6) e -= 2.4 * Math.cos((rd / 5.6) * Math.PI * 0.5) ** 2;
        elev[idx] = e;
      } else if (type === "river") {
        // Highland Valley: Lush green river basin with deep winding river (NO SAND!)
        const distFromCenter = Math.abs(x - W / 2) / (W / 2);
        let e = n * 2.5 + distFromCenter * 3.2 + 0.3;
        const riverX = W * 0.5 + Math.sin(y * 0.15) * 14;
        const rd = Math.abs(x - riverX);
        if (rd < 9.6) e -= 4.2 * Math.cos((rd / 9.6) * Math.PI * 0.5) ** 2;
        elev[idx] = e;
      } else if (type === "island") {
        const cx = W / 2, cy = H / 2;
        const dist = Math.hypot(x - cx, y - cy) / (W * 0.42);
        let e = (1 - dist) * 4.5 + (n - 0.5) * 1.8 - 0.8;
        elev[idx] = e;
      } else if (type === "urban" || type === "basin") {
        // Metro Manila Urban Barangay: paved streets in a flat lowland basin.
        // The ground that pens the water in used to be a rim measured from the
        // map's edges, which drew a literal rectangle around the play area. It
        // is now a wobbled oval bowl, so the higher ground is a landform in its
        // own right and simply keeps going past the border.
        const cxn = (x - W / 2) / (W * 0.44);
        const cyn = (y - H / 2) / (H * 0.44);
        const bowl = Math.hypot(cxn, cyn) + (n - 0.5) * 0.55;
        const rim = Math.min(Math.max((bowl - 0.62) / 0.53, 0), 1);
        let e = 0.4 + (n - 0.5) * 0.6 + rim * rim * (3 - 2 * rim) * 2.6;
        const canalX = W * 0.48;
        const cd = Math.abs(x - canalX);
        if (cd < 4.4) e -= 1.15 * Math.cos((cd / 4.4) * Math.PI * 0.5) ** 2;
        elev[idx] = e;
      } else {
        elev[idx] = n * 2.5 - 0.5;
      }
    }
  }
  return elev;
}

const PRESETS = {
  coastal: {
    name: "Coastal Barangay",
    type: "coastal",
    seed: 1337,
    houses: [
      { id: 1, x: 18, y: 38, style: "nipa" },
      { id: 2, x: 24, y: 36, style: "nipa" },
      { id: 3, x: 30, y: 35, style: "store" },
      { id: 4, x: 36, y: 37, style: "house" },
      { id: 5, x: 42, y: 40, style: "hall" },
      { id: 6, x: 62, y: 38, style: "nipa" },
      { id: 7, x: 68, y: 36, style: "house" },
      { id: 8, x: 74, y: 35, style: "nipa" },
      { id: 9, x: 80, y: 39, style: "store" },
      { id: 10, x: 22, y: 28, style: "house" },
      { id: 11, x: 28, y: 26, style: "house" },
      { id: 12, x: 34, y: 25, style: "nipa" },
      { id: 13, x: 70, y: 27, style: "nipa" },
      { id: 14, x: 76, y: 28, style: "house" },
    ],
  },
  river: {
    name: "River Valley",
    type: "river",
    seed: 4040,
    houses: [
      { id: 1, x: 20, y: 15, style: "house" },
      { id: 2, x: 25, y: 20, style: "nipa" },
      { id: 3, x: 28, y: 28, style: "store" },
      { id: 4, x: 30, y: 36, style: "nipa" },
      { id: 5, x: 32, y: 44, style: "hall" },
      { id: 6, x: 65, y: 18, style: "house" },
      { id: 7, x: 68, y: 26, style: "nipa" },
      { id: 8, x: 70, y: 34, style: "house" },
      { id: 9, x: 72, y: 42, style: "nipa" },
      { id: 10, x: 75, y: 50, style: "store" },
      { id: 11, x: 15, y: 30, style: "nipa" },
      { id: 12, x: 18, y: 42, style: "house" },
      { id: 13, x: 80, y: 22, style: "nipa" },
      { id: 14, x: 82, y: 38, style: "house" },
    ],
  },
  urban: {
    name: "Urban Barangay",
    type: "urban",
    seed: 9999,
    houses: [
      { id: 1, x: 24, y: 20, style: "apartment" },
      { id: 2, x: 30, y: 20, style: "apartment" },
      { id: 3, x: 36, y: 20, style: "store" },
      { id: 4, x: 42, y: 20, style: "hall" },
      { id: 5, x: 54, y: 20, style: "apartment" },
      { id: 6, x: 60, y: 20, style: "apartment" },
      { id: 7, x: 66, y: 20, style: "store" },
      { id: 8, x: 24, y: 30, style: "apartment" },
      { id: 9, x: 30, y: 30, style: "apartment" },
      { id: 10, x: 36, y: 30, style: "house" },
      { id: 11, x: 54, y: 30, style: "apartment" },
      { id: 12, x: 60, y: 30, style: "apartment" },
      { id: 13, x: 66, y: 30, style: "house" },
      { id: 14, x: 24, y: 40, style: "store" },
      { id: 15, x: 30, y: 40, style: "house" },
      { id: 16, x: 36, y: 40, style: "house" },
      { id: 17, x: 54, y: 40, style: "house" },
      { id: 18, x: 60, y: 40, style: "store" },
      { id: 19, x: 66, y: 40, style: "house" },
    ],
  },
  island: {
    name: "Typhoon Island",
    type: "island",
    seed: 8888,
    houses: [
      { id: 1, x: 38, y: 25, style: "nipa" },
      { id: 2, x: 44, y: 22, style: "house" },
      { id: 3, x: 52, y: 22, style: "hall" },
      { id: 4, x: 58, y: 25, style: "nipa" },
      { id: 5, x: 32, y: 32, style: "store" },
      { id: 6, x: 64, y: 32, style: "nipa" },
      { id: 7, x: 34, y: 40, style: "nipa" },
      { id: 8, x: 40, y: 44, style: "house" },
      { id: 9, x: 48, y: 46, style: "house" },
      { id: 10, x: 56, y: 44, style: "nipa" },
      { id: 11, x: 62, y: 40, style: "store" },
      { id: 12, x: 48, y: 32, style: "hall" },
    ],
  },
};

export default function FloodPlayground() {
  const sim = useRef(null);
  const [tool, setTool] = useState("view"); // Default to Pan/View mode so users can immediately swipe around!
  const [rain, setRain] = useState(0);
  const [storm, setStorm] = useState(false);
  const [running, setRunning] = useState(true);
  const [muted, setMuted] = useState(false);
  const [cameraPreset, setCameraPreset] = useState("isometric");
  const [activePresetKey, setActivePresetKey] = useState("coastal");
  const [houses, setHouses] = useState(PRESETS.coastal.houses);
  const [showShareModal, setShowShareModal] = useState(false);
  const [shareUrl, setShareUrl] = useState("");
  const [copied, setCopied] = useState(false);

  const [stats, setStats] = useState({
    water: 0,
    flooded: 0,
    floodedHouses: 0,
    mangroveCount: 0,
    drainCount: 0,
    tick: 0,
  });

  const stormRef = useRef(false);
  const rainRef = useRef(0);
  const toolRef = useRef(tool);
  toolRef.current = tool;
  stormRef.current = storm;
  rainRef.current = rain;

  const loadPreset = useCallback((presetKey = "coastal") => {
    const config = PRESETS[presetKey] || PRESETS.coastal;
    setActivePresetKey(presetKey);
    setHouses(config.houses);

    sim.current = {
      elev: makeTerrainPreset(config.type, config.seed),
      water: new Float32Array(W * H),
      next: new Float32Array(W * H),
      drain: new Float32Array(W * H),
      absorb: new Float32Array(W * H),
      mang: new Uint8Array(W * H),
      drn: new Uint8Array(W * H),
      tick: 0,
      surgeTicks: 0,
    };

    const S = sim.current;
    if (config.type === "coastal") {
      for (let x = 10; x < 86; x += 3) {
        const i = 50 * W + x;
        if (S.elev[i] > SEA_LEVEL && S.elev[i] < 0.6) {
          S.mang[i] = 1;
          S.absorb[i] = 0.008;
        }
      }
    } else if (config.type === "urban") {
      [30 * W + 46, 32 * W + 46, 40 * W + 46].forEach((i) => {
        if (S.elev[i] > SEA_LEVEL) {
          S.drn[i] = 1;
          S.drain[i] = 0.03;
        }
      });
    }
  }, []);

  if (!sim.current) loadPreset("coastal");

  // ── Realistic CA Simulation: Soil Infiltration & Downstream Flow ──
  const step = useCallback(() => {
    const S = sim.current;
    if (!S) return;
    const { elev, water, next, drain, absorb } = S;
    const rainRate = rainRef.current * 0.0004 + (stormRef.current ? 0.002 : 0);

    if (rainRate > 0) {
      for (let i = 0; i < water.length; i++) {
        if (elev[i] > SEA_LEVEL) {
          water[i] = Math.min(water[i] + rainRate, 3.0);
        }
      }
    }

    if (S.surgeTicks > 0) {
      S.surgeTicks--;
      const surgeDepth = 0.6;
      for (let y = H - 6; y < H; y++) {
        for (let x = 0; x < W; x++) {
          const i = y * W + x;
          water[i] = Math.min(Math.max(water[i], surgeDepth + (elev[i] < SEA_LEVEL ? SEA_LEVEL - elev[i] : 0)), 3.0);
        }
      }
    }

    for (let i = 0; i < water.length; i++) {
      if (elev[i] <= SEA_LEVEL) {
        water[i] = Math.min(Math.max(water[i], SEA_LEVEL - elev[i]), 3.0);
      }
    }

    next.set(water);
    for (let y = 0; y < H; y++) {
      for (let x = 0; x < W; x++) {
        const i = y * W + x;
        if (water[i] <= MIN_WATER) continue;
        const hi = elev[i] + water[i];

        if (x > 0) flow(i, i - 1, hi);
        if (x < W - 1) flow(i, i + 1, hi);
        if (y > 0) flow(i, i - W, hi);
        if (y < H - 1) flow(i, i + W, hi);
      }
    }

    function flow(i, n, hi) {
      const diff = hi - (elev[n] + water[n]);
      if (diff > 0) {
        const maxOutflow = next[i] * 0.2;
        const moved = Math.min(diff * FLOW_RATE, maxOutflow);
        next[i] -= moved;
        next[n] += moved;
      }
    }

    const tmp = S.water;
    S.water = S.next;
    S.next = tmp;

    const w2 = S.water;
    let totalWater = 0, floodedCells = 0, mangCount = 0, drnCount = 0;

    for (let i = 0; i < w2.length; i++) {
      if (S.mang[i]) mangCount++;
      if (S.drn[i]) drnCount++;

      if (elev[i] > SEA_LEVEL && w2[i] > 0) {
        const naturalSeepage = 0.0004;
        w2[i] = Math.max(0, w2[i] - naturalSeepage - drain[i] - absorb[i]);
        if (w2[i] < MIN_WATER) w2[i] = 0;
      }

      if (elev[i] > SEA_LEVEL) {
        totalWater += w2[i];
        if (w2[i] > 0.03) floodedCells++;
      }
    }

    let floodedH = 0;
    houses.forEach((h) => {
      const gx = Math.floor(h.x);
      const gy = Math.floor(h.y);
      const i = gy * W + gx;
      if (S.water[i] > 0.08) floodedH++;
    });

    S.tick++;
    setStats({
      water: totalWater,
      flooded: floodedCells,
      floodedHouses: floodedH,
      mangroveCount: mangCount,
      drainCount: drnCount,
      tick: S.tick,
    });
  }, [houses]);

  // Game Loop
  useEffect(() => {
    let raf;
    const loop = () => {
      if (running) {
        step();
        step();
      }
      raf = requestAnimationFrame(loop);
    };
    raf = requestAnimationFrame(loop);
    return () => cancelAnimationFrame(raf);
  }, [running, step]);

  // 3D Paint Raycast handler
  const handlePaint = useCallback(
    (x, y) => {
      const S = sim.current;
      if (!S) return;
      const R = 3;
      const t = toolRef.current;

      if (t === "view" || t === "pan") return; // No paint in View / Orbit mode!

      for (let dy = -R; dy <= R; dy++) {
        for (let dx = -R; dx <= R; dx++) {
          const px = x + dx;
          const py = y + dy;
          if (px < 0 || px >= W || py < 0 || py >= H) continue;
          const d2 = dx * dx + dy * dy;
          if (d2 > R * R) continue;
          const i = py * W + px;
          const fall = 1 - Math.sqrt(d2) / R;

          if (t === "raise") {
            S.elev[i] += 0.15 * fall;
            sound.playTerraform(true);
          } else if (t === "lower") {
            S.elev[i] -= 0.15 * fall;
            sound.playTerraform(false);
          } else if (t === "water") {
            S.water[i] += 0.25 * fall;
            sound.playWaterSplash();
          } else if (t === "mangrove" && S.elev[i] > SEA_LEVEL) {
            S.mang[i] = 1;
            S.absorb[i] = 0.008;
            sound.playPlant();
          } else if (t === "drain" && S.elev[i] > SEA_LEVEL) {
            S.drn[i] = 1;
            S.drain[i] = 0.025;
            sound.playBuild();
          } else if (t === "house" && S.elev[i] > SEA_LEVEL && dx === 0 && dy === 0) {
            if (!houses.some((h) => Math.hypot(h.x - px, h.y - py) < 2)) {
              const styles = ["nipa", "house", "store", "apartment"];
              const randomStyle = styles[Math.floor(Math.random() * styles.length)];
              setHouses((prev) => [...prev, { id: Date.now(), x: px, y: py, style: randomStyle }]);
              sound.playBuild();
            }
          } else if (t === "clear") {
            S.mang[i] = 0;
            S.drn[i] = 0;
            S.absorb[i] = 0;
            S.drain[i] = 0;
            S.water[i] = 0;
          }
        }
      }
    },
    [houses]
  );

  const toggleStorm = () => {
    const nextStorm = !storm;
    setStorm(nextStorm);
    sound.setStorm(nextStorm);
    if (nextStorm) {
      sim.current.surgeTicks = 200;
    }
  };

  const toggleMute = () => {
    const isMuted = sound.toggleMute();
    setMuted(isMuted);
  };

  const generateShareLink = () => {
    const exportData = { houses, rain, storm, preset: activePresetKey };
    const hash = btoa(JSON.stringify(exportData));
    const url = `${window.location.origin}${window.location.pathname}#map=${hash}`;
    setShareUrl(url);
    setShowShareModal(true);
    setCopied(false);
  };

  const copyToClipboard = () => {
    navigator.clipboard.writeText(shareUrl);
    setCopied(true);
  };

  // Tools Palette with Pan / View Mode First!
  const TOOLS = [
    { id: "view", label: "Pan & Orbit", Icon: Icons.Move, desc: "Swipe & drag to orbit 3D camera" },
    { id: "raise", label: "Raise Land", Icon: Icons.Mountain, desc: "Build dikes & high terrain" },
    { id: "lower", label: "Dig Canal", Icon: Icons.Shovel, desc: "Carve rivers & channels" },
    { id: "water", label: "Flood Water", Icon: Icons.Droplet, desc: "Pour water volume" },
    { id: "mangrove", label: "Mangroves", Icon: Icons.Tree, desc: "Plant coastal buffer trees" },
    { id: "drain", label: "Drain Pump", Icon: Icons.Drain, desc: "Install active pump station" },
    { id: "house", label: "Barangay Home", Icon: Icons.Home, desc: "Place homes to protect" },
    { id: "clear", label: "Clear", Icon: Icons.Trash, desc: "Remove structures and water" },
  ];

  const threatLabel =
    stats.floodedHouses > 3 || stats.flooded > 400
      ? "Critical Flood"
      : stats.floodedHouses > 0 || stats.flooded > 100
      ? "Moderate Surge"
      : "Safe & Dry";

  const threatColor =
    stats.floodedHouses > 3 || stats.flooded > 400
      ? "#D9442B"
      : stats.floodedHouses > 0 || stats.flooded > 100
      ? "#F4B942"
      : "#5B8C3A";

  return (
    <div className={`game-viewport ${storm ? "hud-card-storm" : ""}`}>
      {/* 3D WebGL Canvas Layer */}
      <ThreeCanvas
        simRef={sim}
        tool={tool}
        storm={storm}
        rain={rain}
        cameraPreset={cameraPreset}
        onPaint={handlePaint}
        houses={houses}
        presetType={activePresetKey}
      />

      {/* Floating Modern HUD Overlay */}
      <div className="hud-overlay">
        {/* Top Header Bar */}
        <div className="top-hud-bar hud-interactive">
          <div className="hud-card header-brand">
            <Icons.Waves style={{ color: "#D9442B" }} />
            <div>
              <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                <h1 className="heading-title">
                  HABAGAT <span style={{ color: "#D9442B" }}>3D</span>
                </h1>
                <span
                  className="preset-btn"
                  style={{
                    background: `${threatColor}15`,
                    color: threatColor,
                    borderColor: `${threatColor}40`,
                    pointerEvents: "none",
                  }}
                >
                  <Icons.AlertTriangle style={{ width: 13, height: 13 }} />
                  {threatLabel}
                </span>
              </div>
              <p style={{ fontSize: 12, color: "var(--text-muted)", marginTop: 2 }}>
                Cozy Paralives 3D Flood Simulation & Barangay Defense
              </p>
            </div>
          </div>

          <div className="hud-card" style={{ padding: "10px 16px", display: "flex", alignItems: "center", gap: 10 }}>
            <span style={{ fontSize: 12, fontWeight: 700, color: "var(--text-muted)" }}>Presets:</span>
            {Object.entries(PRESETS).map(([key, p]) => (
              <button
                key={key}
                className={`preset-btn ${activePresetKey === key ? "active" : ""}`}
                onClick={() => {
                  loadPreset(key);
                  sound.playPop(400);
                }}
              >
                {p.name}
              </button>
            ))}
            <button
              className="preset-btn"
              onClick={() => {
                const keys = Object.keys(PRESETS);
                const randomKey = keys[Math.floor(Math.random() * keys.length)];
                loadPreset(randomKey);
                sound.playPop(500);
              }}
            >
              <Icons.Refresh style={{ width: 13, height: 13 }} />
              Random
            </button>

            <div style={{ width: 1, height: 18, background: "rgba(0,0,0,0.1)" }} />

            <div style={{ display: "flex", alignItems: "center", gap: 4 }}>
              <button
                className={`preset-btn ${cameraPreset === "isometric" ? "active" : ""}`}
                onClick={() => setCameraPreset("isometric")}
                title="Isometric View"
              >
                <Icons.Camera style={{ width: 13, height: 13 }} /> Iso
              </button>
              <button
                className={`preset-btn ${cameraPreset === "top" ? "active" : ""}`}
                onClick={() => setCameraPreset("top")}
                title="Top-Down View"
              >
                <Icons.Eye style={{ width: 13, height: 13 }} /> Top
              </button>
              <button
                className={`preset-btn ${cameraPreset === "close" ? "active" : ""}`}
                onClick={() => setCameraPreset("close")}
                title="Cozy View"
              >
                <Icons.Home style={{ width: 13, height: 13 }} /> Cozy
              </button>
            </div>

            <div style={{ width: 1, height: 18, background: "rgba(0,0,0,0.1)" }} />

            <button className="preset-btn" onClick={toggleMute} title="Toggle Sound">
              {muted ? <Icons.VolumeX style={{ width: 13, height: 13 }} /> : <Icons.Volume2 style={{ width: 13, height: 13 }} />}
            </button>

            <button className="preset-btn" onClick={generateShareLink} title="Share Link">
              <Icons.Share2 style={{ width: 13, height: 13 }} />
            </button>
          </div>
        </div>

        {/* Bottom Control Bar */}
        <div className="bottom-control-panel hud-interactive">
          <div className="hud-card" style={{ padding: "8px 18px", display: "flex", alignItems: "center", gap: 16 }}>
            <div className="stat-item">
              <Icons.Home style={{ width: 14, height: 14, color: stats.floodedHouses > 0 ? "#D9442B" : "#5B8C3A" }} />
              Homes Flooded:{" "}
              <strong style={{ color: stats.floodedHouses > 0 ? "#D9442B" : "#5B8C3A" }}>
                {stats.floodedHouses} / {houses.length}
              </strong>
            </div>
            <div className="stat-item">
              <Icons.Droplet style={{ width: 14, height: 14, color: "#3BA99C" }} />
              Water Volume: <strong>{stats.water.toFixed(1)}m³</strong>
            </div>
            <div className="stat-item">
              <Icons.Tree style={{ width: 14, height: 14, color: "#5B8C3A" }} />
              Mangroves: <strong>{stats.mangroveCount}</strong>
            </div>
            <div className="stat-item">
              <Icons.Drain style={{ width: 14, height: 14, color: "#4A90E2" }} />
              Drains: <strong>{stats.drainCount}</strong>
            </div>
            <div className="stat-item" style={{ color: "var(--text-muted)" }}>
              Tick: {stats.tick}
            </div>
          </div>

          <div className="hud-card" style={{ padding: "10px 16px", display: "flex", flexWrap: "wrap", justifyContent: "center", gap: 8 }}>
            {TOOLS.map(({ id, label, Icon, desc }) => (
              <button
                key={id}
                className={`tool-btn ${tool === id ? "active" : ""}`}
                onClick={() => {
                  setTool(id);
                  sound.playPop(480);
                }}
                title={desc}
              >
                <Icon style={{ width: 15, height: 15 }} />
                {label}
              </button>
            ))}
          </div>

          <div className="hud-card" style={{ padding: "12px 24px", display: "flex", alignItems: "center", gap: 20 }}>
            <button
              className={`storm-btn ${storm ? "active-storm" : ""}`}
              onClick={toggleStorm}
            >
              {storm ? (
                <>
                  <Icons.Sun style={{ width: 18, height: 18 }} /> Calm Sunshine
                </>
              ) : (
                <>
                  <Icons.CloudRain style={{ width: 18, height: 18 }} /> SUMMON HABAGAT STORM
                </>
              )}
            </button>

            <button
              className="tool-btn"
              onClick={() => {
                setRunning(!running);
                sound.playPop(350);
              }}
            >
              {running ? <Icons.Pause style={{ width: 14, height: 14 }} /> : <Icons.Play style={{ width: 14, height: 14 }} />}
              {running ? "Pause Sim" : "Resume Sim"}
            </button>

            <div style={{ display: "flex", alignItems: "center", gap: 10, fontSize: 13, fontWeight: 700, color: "var(--text-muted)" }}>
              <span>Rain Intensity:</span>
              <input
                type="range"
                min="0"
                max="10"
                value={rain}
                onChange={(e) => setRain(+e.target.value)}
                className="cozy-slider"
                style={{ width: 110 }}
              />
              <span style={{ width: 20, textAlign: "center", color: "var(--text-dark)" }}>{rain}</span>
            </div>
          </div>
        </div>
      </div>

      {/* Share Link Modal */}
      {showShareModal && (
        <div className="modal-backdrop" onClick={() => setShowShareModal(false)}>
          <div className="hud-card hud-interactive" style={{ padding: 28, maxWidth: 440, width: "100%" }} onClick={(e) => e.stopPropagation()}>
            <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginBottom: 16 }}>
              <h2 className="heading-title" style={{ display: "flex", alignItems: "center", gap: 8 }}>
                <Icons.Share2 style={{ color: "#D9442B" }} /> Share Map Online
              </h2>
              <button className="preset-btn" onClick={() => setShowShareModal(false)}>✕</button>
            </div>

            <p style={{ fontSize: 13, color: "var(--text-muted)", marginBottom: 16, lineHeight: 1.5 }}>
              Share this link with anyone online to load your custom 3D barangay terrain layout and flood defense setup!
            </p>

            <div style={{ display: "flex", gap: 8, marginBottom: 20 }}>
              <input
                type="text"
                readOnly
                value={shareUrl}
                style={{ flex: 1, padding: "8px 12px", fontSize: 12, borderRadius: 8, border: "1px solid rgba(0,0,0,0.15)", background: "rgba(255,255,255,0.7)" }}
              />
              <button className="tool-btn active" onClick={copyToClipboard}>
                {copied ? "Copied!" : "Copy"}
              </button>
            </div>

            <div style={{ textAlign: "right" }}>
              <button className="tool-btn" onClick={() => setShowShareModal(false)}>Close</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
