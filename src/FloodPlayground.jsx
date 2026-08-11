import React, { useRef, useEffect, useState, useCallback } from "react";
import ThreeCanvas from "./ThreeCanvas";
import { sound } from "./audio";
import { Icons } from "./components/Icons";
// The simulation lives in its own module so it can be run without React —
// scripts/fingerprint.mjs does exactly that, and that fingerprint is what the
// C# port is checked against. Keeping a second copy here would mean the
// verified sim and the played sim could quietly drift apart.
import { W, H, SEA_LEVEL, PRESETS, createSim, step as simStep, paint as simPaint } from "./sim";

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

    sim.current = createSim(presetKey);
  }, []);

  if (!sim.current) loadPreset("coastal");

  // ── Realistic CA Simulation: Soil Infiltration & Downstream Flow ──
  const step = useCallback(() => {
    // The CA itself is in ./sim, shared with the fingerprint harness. What stays
    // here is only the React part: reading the live rain/storm values and pushing
    // the result into state.
    const next = simStep(sim.current, rainRef.current, stormRef.current, houses);
    if (next) setStats(next);
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

      // Homes are placed, not painted: one per cell, refused next to an existing
      // one, and the style is genuinely random rather than seeded. That is why it
      // is not part of sim.paint.
      if (t === "house") {
        if (S.elev[y * W + x] > SEA_LEVEL &&
            !houses.some((h) => Math.hypot(h.x - x, h.y - y) < 2)) {
          const styles = ["nipa", "house", "store", "apartment"];
          const randomStyle = styles[Math.floor(Math.random() * styles.length)];
          setHouses((prev) => [...prev, { id: Date.now(), x, y, style: randomStyle }]);
          sound.playBuild();
        }
        return;
      }

      // One cue per affected cell, which is what this always did — the callback
      // exists so the loop could move into ./sim without changing that.
      simPaint(S, t, x, y, R, (tool) => {
        if (tool === "raise") sound.playTerraform(true);
        else if (tool === "lower") sound.playTerraform(false);
        else if (tool === "water") sound.playWaterSplash();
        else if (tool === "mangrove") sound.playPlant();
        else if (tool === "drain") sound.playBuild();
      });
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
