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
        private Audio.SoundEngine _sound;
        private Font _font;

        private Text _stats;
        private Text _status;
        private Button _pauseBtn;
        private Button _muteBtn;

        /// <summary>
        /// How much room the interface may spend. Comfortable is the desktop layout
        /// this was designed at; Compact is what fits in a hand.
        /// </summary>
        private enum Density { Comfortable, Compact }

        private Density _density = Density.Comfortable;
        private bool Compact => _density == Density.Compact;

        /// <summary>Diagnostics for the self-test: which layout, and how many rebuilds.</summary>
        public string DensityName => _density.ToString();
        public int LayoutBuilds { get; private set; }

        /// <summary>
        /// Chosen from the screen's width in *reference* pixels, not device pixels.
        ///
        /// Device pixels cannot answer this question. A phone reporting a 2x pixel
        /// ratio has a 1688-pixel-wide screen and would look roomier than a 1280-wide
        /// laptop, which is backwards — it is a third of the physical size. Dividing
        /// by the reported DPI recovers something proportional to how big the glass
        /// actually is, which is what decides whether a label fits or a thumb lands.
        ///
        /// Screen.dpi is 0 where the platform will not say; the raw width is the right
        /// fallback there, since every platform that does not know its DPI is a desktop.
        /// </summary>
        /// <summary>
        /// Width in reference pixels to lay out for, overriding the screen. Zero means
        /// ask the screen, which is right everywhere except the editor harness: in
        /// batch mode Screen reports the editor's own surface rather than the
        /// RenderTexture being drawn into, so without this a 1600x900 screenshot
        /// silently shows the phone layout and stops being evidence about anything.
        /// </summary>
        [System.NonSerialized] public float layoutWidthOverride;

        private Density DensityFor()
        {
            if (layoutWidthOverride > 0f)
                return layoutWidthOverride < 1100f ? Density.Compact : Density.Comfortable;

            float dpi = Screen.dpi;
            float refWidth = dpi > 1f ? Screen.width * 96f / dpi : Screen.width;
            // 1100 sits between a small laptop window and a large tablet. The labelled
            // bars need ~1400 units of canvas and stop fitting well before this.
            return refWidth < 1100f ? Density.Compact : Density.Comfortable;
        }
        private readonly List<(Button btn, PaintController.Brush brush, string name)> _toolBtns = new();
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
            // Absent in the screenshot harness, so every use of it is guarded.
            _sound = GetComponent<Audio.SoundEngine>();
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _density = DensityFor();
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

        /// <summary>Which glyph sits to the left of a button's label.</summary>
        public enum Glyph { None, Move, Peak, Channel, Wave, Tree, Grate, House, Cross, Sound, Mute }

        private static readonly Dictionary<Glyph, Sprite> _glyphs = new();

        /// <summary>
        /// Icons drawn in code, like the rounded corners and every mesh in this
        /// project — no binary UI assets, and a glyph stays a few lines of geometry
        /// rather than a file somebody has to find again later.
        ///
        /// Each shape is a predicate over normalised [-1,1] space, sampled 3x3 per
        /// pixel. That supersampling is doing the antialiasing: at 19 px a hard
        /// in/out test gives diagonals made of visible steps, which on a house roof
        /// or an X reads as a rendering fault rather than as a style.
        /// </summary>
        private static Sprite GlyphSprite(Glyph g)
        {
            if (_glyphs.TryGetValue(g, out var cached)) return cached;
            const int N = 48, SS = 3;

            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < SS; sy++)
                        for (int sx = 0; sx < SS; sx++)
                        {
                            var p = new Vector2(
                                ((x + (sx + 0.5f) / SS) / N) * 2f - 1f,
                                ((y + (sy + 0.5f) / SS) / N) * 2f - 1f);
                            if (GlyphHit(g, p)) hits++;
                        }
                    px[y * N + x] = new Color32(255, 255, 255, (byte)(255f * hits / (SS * SS)));
                }
            tex.SetPixels32(px);
            tex.Apply();

            var sprite = Sprite.Create(tex, new UnityEngine.Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            _glyphs[g] = sprite;
            return sprite;
        }

        private static bool GlyphBox(Vector2 p, float cx, float cy, float hw, float hh) =>
            Mathf.Abs(p.x - cx) <= hw && Mathf.Abs(p.y - cy) <= hh;

        private static bool GlyphTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            static float Side(Vector2 u, Vector2 v, Vector2 w) =>
                (u.x - w.x) * (v.y - w.y) - (v.x - w.x) * (u.y - w.y);
            float d1 = Side(p, a, b), d2 = Side(p, b, c), d3 = Side(p, c, a);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);
        }

        private static bool GlyphSeg(Vector2 p, Vector2 a, Vector2 b, float w)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            return (p - (a + ab * t)).magnitude <= w;
        }

        private static bool GlyphHit(Glyph g, Vector2 p)
        {
            switch (g)
            {
                // A crosshair, for the mode where the drag belongs to the camera.
                case Glyph.Move:
                    return GlyphBox(p, 0f, 0f, 0.12f, 0.85f) || GlyphBox(p, 0f, 0f, 0.85f, 0.12f);
                // Land pushed up, and a channel cut down into it.
                case Glyph.Peak:
                    return GlyphTri(p, new Vector2(0f, 0.8f), new Vector2(-0.85f, -0.7f), new Vector2(0.85f, -0.7f));
                case Glyph.Channel:
                    return GlyphTri(p, new Vector2(0f, -0.8f), new Vector2(-0.85f, 0.7f), new Vector2(0.85f, 0.7f));
                // Two crests: one alone reads as a divider rather than as water.
                case Glyph.Wave:
                    return Mathf.Abs(p.y - 0.32f - 0.26f * Mathf.Sin(p.x * 3.4f)) < 0.15f
                        || Mathf.Abs(p.y + 0.42f - 0.26f * Mathf.Sin(p.x * 3.4f)) < 0.15f;
                case Glyph.Tree:
                    return GlyphBox(p, 0f, -0.62f, 0.11f, 0.33f)
                        || (p - new Vector2(0f, 0.22f)).magnitude < 0.62f;
                // Outline plus bars, so it reads as a grate and not a filled block.
                case Glyph.Grate:
                    return (GlyphBox(p, 0f, 0f, 0.82f, 0.82f) && !GlyphBox(p, 0f, 0f, 0.64f, 0.64f))
                        || GlyphBox(p, 0f, 0.32f, 0.64f, 0.08f)
                        || GlyphBox(p, 0f, 0f, 0.64f, 0.08f)
                        || GlyphBox(p, 0f, -0.32f, 0.64f, 0.08f);
                case Glyph.House:
                    return GlyphTri(p, new Vector2(0f, 0.85f), new Vector2(-0.9f, 0.05f), new Vector2(0.9f, 0.05f))
                        || GlyphBox(p, 0f, -0.42f, 0.6f, 0.45f);
                case Glyph.Cross:
                    return GlyphSeg(p, new Vector2(-0.7f, -0.7f), new Vector2(0.7f, 0.7f), 0.15f)
                        || GlyphSeg(p, new Vector2(-0.7f, 0.7f), new Vector2(0.7f, -0.7f), 0.15f);
                // A speaker: body, flaring cone, and two arcs for the sound leaving it.
                // Muted keeps the same speaker and strikes it through, rather than
                // drawing a different shape — the pair has to read as one control in
                // two states, not as two unrelated buttons.
                case Glyph.Sound:
                case Glyph.Mute:
                {
                    bool body = GlyphBox(p, -0.62f, 0f, 0.2f, 0.28f)
                             || GlyphTri(p, new Vector2(-0.42f, 0f),
                                            new Vector2(-0.02f, 0.72f),
                                            new Vector2(-0.02f, -0.72f));
                    if (g == Glyph.Mute)
                        return body || GlyphSeg(p, new Vector2(-0.8f, -0.8f), new Vector2(0.8f, 0.8f), 0.13f);
                    float r = (p - new Vector2(-0.02f, 0f)).magnitude;
                    return body
                        || (p.x > 0.16f && r > 0.42f && r < 0.58f)
                        || (p.x > 0.32f && r > 0.74f && r < 0.9f);
                }
                default:
                    return false;
            }
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

        /// <summary>
        /// <paramref name="clickHz"/> is the pitch of the blip this button makes. The
        /// reference gives each group its own — presets 400, camera 500, tools 480,
        /// controls 350 — so the interface has a little tonal map of itself, and a
        /// misrouted click is audible before it is visible.
        /// </summary>
        /// <summary>
        /// <paramref name="id"/> names the GameObject and never changes; <paramref
        /// name="text"/> is what the button shows, which density does change. Keeping
        /// them apart is what lets anything find a button by what it IS — the
        /// self-test was looking for "Pause Sim" and finding nothing once compact had
        /// shortened it to "Pause".
        /// </summary>
        private Button Pill(Transform parent, string id, string text, System.Action onClick,
                            float width = 150f, Glyph glyph = Glyph.None, float clickHz = 0f,
                            bool showLabel = true)
        {
            // Colour lives in the Button's ColorBlock, not on the Image: the Button
            // drives targetGraphic.color on every state change, so anything written
            // straight onto the Image is overwritten the moment the pointer moves.
            var go = Box("Btn_" + id, parent, Color.white, 10);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.preferredHeight = 40f;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = go.GetComponent<Image>();
            btn.transition = Selectable.Transition.ColorTint;
            btn.colors = Colors(Idle);
            // The GameObject keeps the full name even when the label is hidden, so a
            // button stays findable by what it is rather than by what it shows.
            RectTransform lr = null;
            if (showLabel)
            {
                var label = Label("Text", go.transform, text, 15, Ink);
                label.raycastTarget = false;
                lr = Rect(label.gameObject);
                lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
                lr.offsetMin = Vector2.zero; lr.offsetMax = Vector2.zero;
            }

            if (glyph != Glyph.None)
            {
                var ico = Node("Icon", go.transform);
                var img = ico.AddComponent<Image>();
                img.sprite = GlyphSprite(glyph);
                img.color = Ink;
                img.preserveAspect = true;
                // Not a raycast target: an icon that eats the pointer leaves a dead
                // spot in the middle of its own button.
                img.raycastTarget = false;
                var ir = Rect(ico);
                if (showLabel)
                {
                    ir.anchorMin = ir.anchorMax = new Vector2(0f, 0.5f);
                    ir.pivot = new Vector2(0f, 0.5f);
                    ir.anchoredPosition = new Vector2(11f, 0f);
                    ir.sizeDelta = new Vector2(19f, 19f);
                    // The label centres in what is left over, so it stays optically
                    // centred instead of colliding with the glyph.
                    lr.offsetMin = new Vector2(30f, 0f);
                }
                else
                {
                    // Alone, the glyph takes the middle and grows — it is now the whole
                    // meaning of the button, not an ornament beside the word.
                    ir.anchorMin = ir.anchorMax = new Vector2(0.5f, 0.5f);
                    ir.pivot = new Vector2(0.5f, 0.5f);
                    ir.anchoredPosition = Vector2.zero;
                    ir.sizeDelta = new Vector2(24f, 24f);
                }
            }

            btn.onClick.AddListener(() =>
            {
                if (clickHz > 0f) _sound?.PlayPop(clickHz);
                onClick();
            });
            return btn;
        }

        /// <summary>Swap a button's icon, for the controls that have two states.</summary>
        private static void SetGlyph(Button b, Glyph g)
        {
            var ico = b.transform.Find("Icon");
            if (ico != null) ico.GetComponent<Image>().sprite = GlyphSprite(g);
        }

        private static void Tint(Button b, bool active)
        {
            var want = active ? Selected : Idle;
            if (b.colors.normalColor != want) b.colors = Colors(want);
            var ink = active ? Color.white : Ink;
            // No Text at all on an icon-only pill.
            var txt = b.GetComponentInChildren<Text>();
            if (txt != null) txt.color = ink;
            // The glyph follows the label, or a selected button ends up with white
            // text beside a near-black icon.
            var ico = b.transform.Find("Icon");
            if (ico != null) ico.GetComponent<Image>().color = ink;
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
            h.padding = Compact ? new RectOffset(8, 8, 6, 6) : new RectOffset(14, 14, 10, 10);
            var f = go.AddComponent<ContentSizeFitter>();
            f.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            f.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return go;
        }

        // ── Layout ───────────────────────────────────────────────────────────
        private void BuildLayout()
        {
            // Rebuilt wholesale on a density change rather than resized in place:
            // half these widths are baked into LayoutElements and the tool pills
            // change shape entirely, so there is less to go wrong in throwing it away.
            if (Canvas != null) DestroyImmediate(Canvas.gameObject);
            _toolBtns.Clear();
            _presetBtns.Clear();
            LayoutBuilds++;

            var canvasGo = new GameObject("UI", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            // A much smaller reference on a handheld, which is what actually enlarges
            // everything: at 760x420 a phone lands near 2x, taking the 40-unit pills
            // from about 16 reference pixels to about 40. Apple and Google both put
            // the floor for a touch target around 44, so this is close to it rather
            // than comfortably past it — it is bounded by the bars still having to fit.
            scaler.referenceResolution = Compact ? new Vector2(760, 420) : new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();
            Canvas = canvas;
            var root = canvasGo.transform;

            // Title, top left. Fixed size, so no layout group.
            //
            // Dropped entirely when compact. It is not a space saving so much as a
            // collision: the top bar is centred and nearly fills a phone's width, so
            // the two overlap and "HABAGAT 3D" is sliced off mid-word. Nothing is lost
            // with it — the status line said "1 homes flooded" and the stats row
            // already says "Homes 1" — so the alarm colour moves there instead.
            _status = null;
            if (!Compact) BuildTitle(root);

            // Presets and camera, top centre.
            var topBar = Bar("TopBar", root);
            var tb = Rect(topBar);
            tb.anchorMin = tb.anchorMax = new Vector2(0.5f, 1f);
            tb.pivot = new Vector2(0.5f, 1f);
            tb.anchoredPosition = new Vector2(0, Compact ? -12 : -24);

            // The preset names shorten rather than the buttons shrinking: "Coastal"
            // at half the point size is unreadable at arm's length, "River" is not.
            foreach (var (name, shortName, preset) in new (string, string, PresetType)[]
            {
                ("Coastal", "Coast", PresetType.Coastal),
                ("River Valley", "River", PresetType.River),
                ("Urban", "Urban", PresetType.Urban),
                ("Typhoon Island", "Island", PresetType.Island),
            })
                _presetBtns.Add((Pill(topBar.transform, name, Compact ? shortName : name,
                                      () => SwitchPreset(preset), Compact ? 96 : 150,
                                      Glyph.None, 400f), preset));

            float viewW = Compact ? 62 : 80;
            Pill(topBar.transform, "Iso", "Iso", () => SetView(135f, 48f, 92f), viewW, Glyph.None, 500f);
            Pill(topBar.transform, "Top", "Top", () => SetView(135f, 13f, 100f), viewW, Glyph.None, 500f);
            Pill(topBar.transform, "Cozy", "Cozy", () => SetView(120f, 62f, 46f), Compact ? 62 : 90, Glyph.None, 500f);

            // Everything else stacks at the bottom centre.
            var bottom = Node("Bottom", root);
            var br = Rect(bottom);
            br.anchorMin = br.anchorMax = new Vector2(0.5f, 0f);
            br.pivot = new Vector2(0.5f, 0f);
            br.anchoredPosition = new Vector2(0, Compact ? 10 : 24);
            var vl = bottom.AddComponent<VerticalLayoutGroup>();
            vl.spacing = Compact ? 6 : 10;
            vl.childAlignment = TextAnchor.LowerCenter;
            vl.childControlWidth = true; vl.childControlHeight = true;
            vl.childForceExpandWidth = false; vl.childForceExpandHeight = false;
            var bf = bottom.AddComponent<ContentSizeFitter>();
            bf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            bf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var statsBar = Bar("Stats", bottom.transform);
            _stats = Label("StatsText", statsBar.transform, "Tick: 0", Compact ? 14 : 19, Ink);

            var toolBar = Bar("Tools", bottom.transform, Compact ? 5f : 8f);
            foreach (var (name, brush, glyph) in new (string, PaintController.Brush, Glyph)[]
            {
                ("Pan & Orbit", PaintController.Brush.None, Glyph.Move),
                ("Raise Land", PaintController.Brush.Raise, Glyph.Peak),
                ("Dig Canal", PaintController.Brush.Lower, Glyph.Channel),
                ("Flood Water", PaintController.Brush.Water, Glyph.Wave),
                ("Mangroves", PaintController.Brush.Mangrove, Glyph.Tree),
                ("Drain Pump", PaintController.Brush.DrainPump, Glyph.Grate),
                ("Barangay Home", PaintController.Brush.House, Glyph.House),
                ("Clear", PaintController.Brush.Clear, Glyph.Cross),
            })
                // Icon only when compact. Eight labelled pills need about 1350 units
                // and there are only ~830 on a phone, so something had to give — and
                // the icons were drawn for exactly this. The button stays 58 wide
                // rather than shrinking to the glyph, because the tappable area is the
                // point, not the drawing.
                _toolBtns.Add((Pill(toolBar.transform, name, name,
                    () => { if (_paint != null) _paint.brush = brush; },
                    Compact ? 58 : 158, glyph, 480f, showLabel: !Compact), brush, name));

            var ctrlBar = Bar("Controls", bottom.transform, Compact ? 8f : 14f);
            var stormBtn = Pill(ctrlBar.transform, "Storm",
                                Compact ? "STORM" : "SUMMON HABAGAT STORM",
                                ToggleStorm, Compact ? 130 : 300, Glyph.None, 350f);
            stormBtn.colors = Colors(Accent);
            stormBtn.GetComponentInChildren<Text>().color = Color.white;
            _pauseBtn = Pill(ctrlBar.transform, "Pause", Compact ? "Pause" : "Pause Sim", TogglePause,
                             Compact ? 92 : 130, Glyph.None, 350f);
            // No click pitch of its own: the blip it would make is the very thing it
            // is being pressed to stop, and on the way back it is drowned by the pop
            // the un-muting already plays.
            _muteBtn = Pill(ctrlBar.transform, "Sound", "Sound On", ToggleMute, Compact ? 58 : 150,
                            Glyph.Sound, showLabel: !Compact);

            // The word "Rain" goes when compact; the slider is the only thing in the
            // interface shaped like a slider, so it does not need naming.
            if (!Compact)
            {
                var rainLabel = Label("RainLabel", ctrlBar.transform, "Rain", 18, Ink);
                var rle = rainLabel.gameObject.AddComponent<LayoutElement>();
                rle.preferredWidth = 56; rle.preferredHeight = 40;
            }

            BuildSlider(ctrlBar.transform).onValueChanged.AddListener(v => _world.rain = v);
        }

        private void BuildTitle(Transform root)
        {
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
        }

        private Slider BuildSlider(Transform parent)
        {
            var go = Box("Rain", parent, new Color(0.88f, 0.89f, 0.9f), 7);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = Compact ? 140 : 200;
            // Thicker when compact: 14 units is a comfortable mouse target and an
            // impossible thumb one.
            le.preferredHeight = Compact ? 22 : 14;
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

        private void ToggleStorm()
        {
            _world.storm = !_world.storm;
            _sound?.SetStorm(_world.storm);
        }

        private void ToggleMute()
        {
            if (_sound == null) return;
            bool muted = _sound.ToggleMute();
            // Compact drops the word, so the glyph is the whole of the feedback.
            var txt = _muteBtn.GetComponentInChildren<Text>();
            if (txt != null) txt.text = muted ? "Sound Off" : "Sound On";
            SetGlyph(_muteBtn, muted ? Glyph.Mute : Glyph.Sound);
            // Confirms it came back — the only feedback that proves audio is alive.
            if (!muted) _sound.PlayPop(350f);
        }

        private void TogglePause()
        {
            _world.running = !_world.running;
            _pauseBtn.GetComponentInChildren<Text>().text = _world.running ? "Pause Sim" : "Resume Sim";
        }

        /// <summary>Display name of the selected brush, for the compact readout.</summary>
        private string ActiveToolName()
        {
            if (_paint == null) return "";
            foreach (var (_, brush, name) in _toolBtns)
                if (brush == _paint.brush) return name;
            return "";
        }

        private void Update()
        {
            // A browser window dragged narrow, or a phone turned over, crosses the
            // threshold without anything else happening — so it is checked here rather
            // than only at startup.
            var want = DensityFor();
            if (want != _density)
            {
                _density = want;
                BuildLayout();
            }
            Refresh();
        }

        /// <summary>
        /// Push the current simulation state into the readout. Public so the
        /// screenshot harness can populate the UI without a running game.
        /// </summary>
        public void Refresh()
        {
            if (_world?.Sim == null || _stats == null) return;
            var s = _world.Stats;
            // The full readout is about 600 units wide, which is most of a phone's
            // canvas. Compact keeps every number and drops the prose around them.
            // Abbreviated words rather than symbols: the font is LegacyRuntime.ttf,
            // which has no dependable coverage past Latin, and a missing glyph renders
            // as an empty box — worse than the word it replaced.
            // Compact leads with the tool's name, because compact is also the layout
            // that took the names off the buttons. Eight glyphs with nothing spelling
            // any of them out is a guessing game — a grate reads as a drain pump only
            // once you already know that is what it is.
            _stats.text = Compact
                ? $"{ActiveToolName()}   Homes {s.FloodedHouses}   Cells {s.Flooded}   " +
                  $"Mang {s.MangroveCount}   Drain {s.DrainCount}   t{s.Tick}"
                : $"Homes Flooded: {s.FloodedHouses}    " +
                  $"Flooded Cells: {s.Flooded}    " +
                  $"Mangroves: {s.MangroveCount}    " +
                  $"Drains: {s.DrainCount}    " +
                  $"Tick: {s.Tick}";

            // The one piece of feedback that has to be legible at a glance while the
            // water is rising. Compact has no title panel to put it in, so the whole
            // stats row carries the alarm colour instead — the number is already in it.
            bool wet = s.FloodedHouses > 0;
            if (_status != null)
            {
                _status.text = wet ? $"{s.FloodedHouses} homes flooded" : "Safe & Dry";
                _status.color = wet ? Accent : new Color(0.16f, 0.55f, 0.30f);
            }
            else
            {
                _stats.color = wet ? Accent : Ink;
            }

            foreach (var (btn, brush, _) in _toolBtns) Tint(btn, _paint != null && _paint.brush == brush);
            foreach (var (btn, preset) in _presetBtns) Tint(btn, _world.preset == preset);
        }
    }
}
