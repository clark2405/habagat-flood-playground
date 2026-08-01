using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Habagat
{
    /// <summary>
    /// The interface, rebuilt in uGUI.
    ///
    /// This is the one piece that is a rewrite rather than a port — React's markup
    /// and CSS have no counterpart here, so only the layout and the behaviour carry
    /// across, not the code. Built from script for the same reason the scene is: a
    /// prefab is a YAML blob nobody can read in a diff, and "which object holds the
    /// rain slider" stops being archaeology when it is written down.
    ///
    /// Legacy <see cref="Text"/> rather than TextMeshPro deliberately: TMP needs its
    /// essential resources imported into the project before any of it renders, which
    /// a headless build cannot do for itself.
    /// </summary>
    [RequireComponent(typeof(HabagatWorld))]
    public class HabagatUI : MonoBehaviour
    {
        private static readonly Color Panel = new(1f, 1f, 1f, 0.93f);
        private static readonly Color Idle = new(0.96f, 0.96f, 0.97f, 1f);
        private static readonly Color Ink = new(0.13f, 0.15f, 0.18f);
        private static readonly Color Accent = new(0.851f, 0.267f, 0.169f);   // 0xd9442b
        private static readonly Color Selected = new(0.106f, 0.114f, 0.208f); // dark pill

        private HabagatWorld _world;
        private PaintController _paint;
        private OrbitCamera _orbit;
        private Font _font;

        private Text _stats;
        private Text _status;
        private Button _pauseBtn;
        private readonly List<(Button btn, PaintController.Brush brush)> _toolBtns = new();
        private readonly List<(Button btn, PresetType preset)> _presetBtns = new();

        /// <summary>The canvas, so the screenshot harness can retarget it.</summary>
        public Canvas Canvas { get; private set; }

        private void Start()
        {
            if (Canvas == null) Build();
        }

        /// <summary>
        /// Public so the harness can build the interface outside Play mode. A UI laid
        /// out blind is almost always wrong, and this is the only way to look at it
        /// without a running game.
        /// </summary>
        public void Build()
        {
            _world = GetComponent<HabagatWorld>();
            _paint = GetComponent<PaintController>();
            _orbit = _paint != null ? _paint.orbit : null;
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BuildLayout();
        }

        // ── Widget helpers ───────────────────────────────────────────────────
        private static RectTransform Rect(GameObject go) => go.GetComponent<RectTransform>();

        private static readonly Dictionary<int, Sprite> _rounded = new();

        /// <summary>
        /// A white rounded-rect sprite, generated rather than imported so the project
        /// keeps no binary UI assets and the radius stays a number in code.
        ///
        /// Sliced with a border equal to the radius, so one small texture stretches to
        /// any pill or panel without the corners deforming. The alpha ramp across the
        /// last pixel is what keeps the curve from looking like a staircase — uGUI
        /// does no antialiasing of its own.
        /// </summary>
        private static Sprite Rounded(int radius)
        {
            if (_rounded.TryGetValue(radius, out var cached)) return cached;

            int size = radius * 2 + 4; // 4px of stretchable middle
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // How far outside the inner (un-rounded) rectangle this pixel sits;
                    // zero anywhere in the straight edges and the middle.
                    float dx = Mathf.Max(radius - x, x - (size - 1 - radius), 0f);
                    float dy = Mathf.Max(radius - y, y - (size - 1 - radius), 0f);
                    float a = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply();

            var sprite = Sprite.Create(tex, new UnityEngine.Rect(0, 0, size, size),
                                       new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                                       new Vector4(radius, radius, radius, radius));
            _rounded[radius] = sprite;
            return sprite;
        }

        /// <summary>Selected and idle looks, both keeping hover and press feedback.</summary>
        private static ColorBlock Colors(Color normal)
        {
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = normal;
            cb.highlightedColor = normal * 0.94f;
            cb.pressedColor = normal * 0.86f;
            cb.selectedColor = normal;
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.08f;
            return cb;
        }

        private GameObject Node(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private GameObject Box(string name, Transform parent, Color color, int radius = 14)
        {
            var go = Node(name, parent);
            var img = go.AddComponent<Image>();
            img.color = color;
            if (radius > 0)
            {
                img.sprite = Rounded(radius);
                img.type = Image.Type.Sliced;
            }
            return go;
        }

        private Text Label(string name, Transform parent, string text, int size, Color color,
                           TextAnchor anchor = TextAnchor.MiddleCenter)
        {
            var go = Node(name, parent);
            var t = go.AddComponent<Text>();
            t.font = _font;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        private Button Pill(Transform parent, string text, System.Action onClick, float width = 150f)
        {
            // Colour lives in the Button's ColorBlock, not on the Image: the Button
            // drives targetGraphic.color on every state change, so anything written
            // straight onto the Image is overwritten the moment the pointer moves.
            var go = Box("Btn_" + text, parent, Color.white, 10);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.preferredHeight = 40f;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = go.GetComponent<Image>();
            btn.transition = Selectable.Transition.ColorTint;
            btn.colors = Colors(Idle);
            var label = Label("Text", go.transform, text, 15, Ink);
            var lr = Rect(label.gameObject);
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = Vector2.zero; lr.offsetMax = Vector2.zero;
            btn.onClick.AddListener(() => onClick());
            return btn;
        }

        private static void Tint(Button b, bool active)
        {
            var want = active ? Selected : Idle;
            if (b.colors.normalColor != want) b.colors = Colors(want);
            b.GetComponentInChildren<Text>().color = active ? Color.white : Ink;
        }

        /// <summary>
        /// A panel that lays its children out in a row and shrinks to fit them.
        ///
        /// <c>childControlWidth</c> is the load-bearing setting: with it off the group
        /// ignores every child's preferred width and hands them all an equal share,
        /// which is why the tool pills first came out uniformly narrow and the storm
        /// button's label ran outside its own box.
        /// </summary>
        private GameObject Bar(string name, Transform parent, float spacing = 8f)
        {
            var go = Box(name, parent, Panel);
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;
            h.padding = new RectOffset(14, 14, 10, 10);
            var f = go.AddComponent<ContentSizeFitter>();
            f.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            f.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return go;
        }

        // ── Layout ───────────────────────────────────────────────────────────
        private void BuildLayout()
        {
            var canvasGo = new GameObject("UI", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();
            Canvas = canvas;
            var root = canvasGo.transform;

            // Title, top left. Fixed size, so no layout group.
            var title = Box("Title", root, Panel);
            var tr = Rect(title);
            tr.anchorMin = tr.anchorMax = new Vector2(0f, 1f);
            tr.pivot = new Vector2(0f, 1f);
            tr.anchoredPosition = new Vector2(24, -24);
            tr.sizeDelta = new Vector2(360, 84);
            var h1 = Label("H1", title.transform, "HABAGAT 3D", 30, Ink, TextAnchor.UpperLeft);
            var h1r = Rect(h1.gameObject);
            h1r.anchorMin = Vector2.zero; h1r.anchorMax = Vector2.one;
            h1r.offsetMin = new Vector2(18, 8); h1r.offsetMax = new Vector2(-18, -12);
            _status = Label("Status", title.transform, "Safe & Dry", 18, Accent, TextAnchor.LowerLeft);
            var sr = Rect(_status.gameObject);
            sr.anchorMin = Vector2.zero; sr.anchorMax = Vector2.one;
            sr.offsetMin = new Vector2(18, 12); sr.offsetMax = new Vector2(-18, -8);

            // Presets and camera, top centre.
            var topBar = Bar("TopBar", root);
            var tb = Rect(topBar);
            tb.anchorMin = tb.anchorMax = new Vector2(0.5f, 1f);
            tb.pivot = new Vector2(0.5f, 1f);
            tb.anchoredPosition = new Vector2(0, -24);

            foreach (var (name, preset) in new (string, PresetType)[]
            {
                ("Coastal", PresetType.Coastal), ("River Valley", PresetType.River),
                ("Urban", PresetType.Urban), ("Typhoon Island", PresetType.Island),
            })
                _presetBtns.Add((Pill(topBar.transform, name, () => SwitchPreset(preset), 150), preset));

            Pill(topBar.transform, "Iso", () => SetView(135f, 48f, 92f), 80);
            Pill(topBar.transform, "Top", () => SetView(135f, 13f, 100f), 80);
            Pill(topBar.transform, "Cozy", () => SetView(120f, 62f, 46f), 90);

            // Everything else stacks at the bottom centre.
            var bottom = Node("Bottom", root);
            var br = Rect(bottom);
            br.anchorMin = br.anchorMax = new Vector2(0.5f, 0f);
            br.pivot = new Vector2(0.5f, 0f);
            br.anchoredPosition = new Vector2(0, 24);
            var vl = bottom.AddComponent<VerticalLayoutGroup>();
            vl.spacing = 10;
            vl.childAlignment = TextAnchor.LowerCenter;
            vl.childControlWidth = true; vl.childControlHeight = true;
            vl.childForceExpandWidth = false; vl.childForceExpandHeight = false;
            var bf = bottom.AddComponent<ContentSizeFitter>();
            bf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            bf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var statsBar = Bar("Stats", bottom.transform);
            _stats = Label("StatsText", statsBar.transform, "Tick: 0", 19, Ink);

            var toolBar = Bar("Tools", bottom.transform);
            foreach (var (name, brush) in new (string, PaintController.Brush)[]
            {
                ("Pan & Orbit", PaintController.Brush.None),
                ("Raise Land", PaintController.Brush.Raise),
                ("Dig Canal", PaintController.Brush.Lower),
                ("Flood Water", PaintController.Brush.Water),
                ("Mangroves", PaintController.Brush.Mangrove),
                ("Drain Pump", PaintController.Brush.DrainPump),
                ("Clear", PaintController.Brush.Clear),
            })
                _toolBtns.Add((Pill(toolBar.transform, name,
                    () => { if (_paint != null) _paint.brush = brush; }, 140), brush));

            var ctrlBar = Bar("Controls", bottom.transform, 14f);
            var stormBtn = Pill(ctrlBar.transform, "SUMMON HABAGAT STORM", ToggleStorm, 300);
            stormBtn.colors = Colors(Accent);
            stormBtn.GetComponentInChildren<Text>().color = Color.white;
            _pauseBtn = Pill(ctrlBar.transform, "Pause Sim", TogglePause, 130);

            var rainLabel = Label("RainLabel", ctrlBar.transform, "Rain", 18, Ink);
            var rle = rainLabel.gameObject.AddComponent<LayoutElement>();
            rle.preferredWidth = 56; rle.preferredHeight = 40;

            BuildSlider(ctrlBar.transform).onValueChanged.AddListener(v => _world.rain = v);
        }

        private Slider BuildSlider(Transform parent)
        {
            var go = Box("Rain", parent, new Color(0.88f, 0.89f, 0.9f), 7);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 200; le.preferredHeight = 14;
            var slider = go.AddComponent<Slider>();
            slider.minValue = 0f; slider.maxValue = 10f; slider.value = 0f;

            var fillArea = Node("FillArea", go.transform);
            var far = Rect(fillArea);
            far.anchorMin = Vector2.zero; far.anchorMax = Vector2.one;
            far.offsetMin = far.offsetMax = Vector2.zero;
            var fill = Box("Fill", fillArea.transform, Accent, 7);
            var fr = Rect(fill);
            fr.anchorMin = Vector2.zero; fr.anchorMax = Vector2.one;
            fr.offsetMin = fr.offsetMax = Vector2.zero;
            slider.fillRect = fr;
            slider.targetGraphic = fill.GetComponent<Image>();
            return slider;
        }

        // ── Behaviour ────────────────────────────────────────────────────────
        private void SwitchPreset(PresetType p)
        {
            _world.preset = p;
            _world.Rebuild();
        }

        private void SetView(float yaw, float pitch, float dist)
        {
            if (_orbit == null) return;
            _orbit.yaw = yaw; _orbit.pitch = pitch; _orbit.distance = dist;
        }

        private void ToggleStorm() => _world.storm = !_world.storm;

        private void TogglePause()
        {
            _world.running = !_world.running;
            _pauseBtn.GetComponentInChildren<Text>().text = _world.running ? "Pause Sim" : "Resume Sim";
        }

        private void Update() => Refresh();

        /// <summary>
        /// Push the current simulation state into the readout. Public so the
        /// screenshot harness can populate the UI without a running game.
        /// </summary>
        public void Refresh()
        {
            if (_world?.Sim == null || _stats == null) return;
            var s = _world.Stats;
            _stats.text = $"Homes Flooded: {s.FloodedHouses}    " +
                          $"Flooded Cells: {s.Flooded}    " +
                          $"Mangroves: {s.MangroveCount}    " +
                          $"Drains: {s.DrainCount}    " +
                          $"Tick: {s.Tick}";

            // The status line is the one piece of feedback that has to be legible at
            // a glance while the water is rising.
            bool wet = s.FloodedHouses > 0;
            _status.text = wet ? $"{s.FloodedHouses} homes flooded" : "Safe & Dry";
            _status.color = wet ? Accent : new Color(0.16f, 0.55f, 0.30f);

            foreach (var (btn, brush) in _toolBtns) Tint(btn, _paint != null && _paint.brush == brush);
            foreach (var (btn, preset) in _presetBtns) Tint(btn, _world.preset == preset);
        }
    }
}
