// ============================================================================
//  WISPWOOD  —  a neon-forest endless runner   (single-file Unity port)
//  --------------------------------------------------------------------------
//  HOW TO USE
//   1. New 2D project (or any project).
//   2. Input auto-adapts to the active backend (new Input System on Unity 6, or the
//      legacy Input Manager). On Unity 6 the Input System package is present by default.
//   3. Create an empty GameObject, attach this script. Press Play.
//      Everything (camera, sprites, audio, UI) is generated at runtime — no assets.
//
//  CONTROLS   tap / Space / Up = jump (double-jump) · swipe down / Down = slide
//             M mute · R restart
//
//  Mechanics ported 1:1 from the HTML build: light-orbs, combo aura (x1..x6),
//  ground beasts (jump) + floaters (slide), 3 hearts, rare heart pickup,
//  ramping speed, screen shake, particle bursts, in-session best score.
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using TPhase = UnityEngine.InputSystem.TouchPhase;   // disambiguate from UnityEngine.TouchPhase
#endif

public class Wispwood : MonoBehaviour
{
    // ----------------------------------------------------------------- CONFIG
    const float WORLD_W = 8.5f;      // fixed horizontal field (world units)
    const float GROUND_H = 0.95f;     // ground band height
    const float PLAYER_R = 0.38f;

    const float GRAVITY = 34f;
    const float JUMP1 = 13.2f;
    const float JUMP2 = 12.0f;
    const float FASTFALL = 16f;

    const float SPD_BASE = 6.2f;
    const float SPD_MAX = 12.5f;
    const float SPD_ACCEL = 0.13f;

    const float SPAWN_BASE = 1.32f;
    const float SPAWN_MIN = 0.60f;
    const float SPAWN_ACCEL = 0.012f;
    const float SPAWN_VAR = 0.45f;

    const int ORB_VALUE = 10;
    const float COMBO_TIME = 2.6f;
    const float INVULN = 1.25f;
    const int MAX_HEALTH = 3;

    // ----------------------------------------------------------------- PALETTE
    Color C_ABYSS, C_MOSS, C_CYAN, C_SPIRIT, C_LEAF, C_GOLD, C_EMBER, C_PINK, C_SHADOW;
    Color[] AURA;

    // ----------------------------------------------------------------- SPRITES
    Sprite spGlow;    // soft radial glow (halos, particles, eyes, fireflies)
    Sprite spDisc;    // crisp disc (tree/monster bodies, cores)
    Sprite spBody;    // player body radial gradient
    Sprite spFace;    // baked cute face (eyes + smile + sparkles)
    Sprite spHeart;   // heart shape
    Sprite spGrad;    // vertical bg gradient
    Sprite spGround;  // ground gradient
    Sprite spPixel;   // 1px white (lines, trunks)

    // ----------------------------------------------------------------- CAMERA / LAYOUT
    Camera cam;
    float orthoSize, worldTop, worldBottom, halfW, groundY;
    float playerX;
    float minOrtho;   // smallest ortho size that still fits a full jump arc (set in Awake)
    int lastW = -1, lastH = -1;

    // ----------------------------------------------------------------- STATE
    enum St { Start, CharSelect, Playing, GameOver }
    St state = St.Start;

    float speed = SPD_BASE, elapsed, score, best, spawnTimer = 0.8f, shake, invuln;
    int health = MAX_HEALTH;
    bool lastObstacle;
    int combo, mult = 1, prevMult = 1, maxCombo;
    float comboTimer;
    bool muted;
    bool slideKey;
    float slideUntil;

    // pause
    bool paused;

    // ----------------------------------------------------------------- CHARACTERS
    // Each character is a small, opt-in modifier. The first one (Wisp) is the exact
    // baseline — choosing it leaves gameplay, physics and feel identical to the
    // original game. No perk ever changes jump height or gravity, so the camera
    // framing computed in Awake stays valid for every character.
    struct Char
    {
        public string name, perk;
        public Color body, ear;    // visual identity: body tint + ear colour
        public int hearts;       // starting / max hearts
        public float comboTime;    // seconds the combo aura survives between orbs
        public float orbReach;     // bonus orb pickup radius (gentle magnet)
    }
    Char[] chars;
    int selectedChar = 0;

    // per-run values copied from the chosen character in ApplyChar()
    int maxHealth = MAX_HEALTH;
    float comboTimeCur = COMBO_TIME;
    float orbBonus = 0f;

    // ----------------------------------------------------------------- ENTITIES
    class Player
    {
        public GameObject go; public Transform tr;
        public SpriteRenderer aura, body, face, earL, earR;
        public float y, vy, t, squash; public int jumps; public bool grounded; public bool slide;
    }
    Player P = new Player();

    class Orb { public GameObject go; public Transform tr; public SpriteRenderer halo, core; public float x, y, t; }
    class Mon
    {
        public GameObject go; public Transform tr; public SpriteRenderer body, b2, eyeL, eyeR;
        public int type; public float x, y, baseY, w, h, t, eyeT; public bool hit;
    }
    class Heart { public GameObject go; public Transform tr; public SpriteRenderer halo, core; public float x, y, t; }
    class Part
    {
        public GameObject go; public Transform tr; public SpriteRenderer sr;
        public float x, y, vx, vy, life, max, size; public Color col;
    }
    class Tree { public GameObject go; public Transform tr; public float x; public bool far; }
    class Mush { public GameObject go; public Transform tr; public float x; }
    class Fly
    {
        public GameObject go; public Transform tr; public SpriteRenderer sr;
        public float x, y, sp, drift, tw, tws, r; public Color col;
    }

    readonly List<Orb> orbs = new List<Orb>();
    readonly List<Mon> mons = new List<Mon>();
    readonly List<Heart> hearts = new List<Heart>();
    readonly List<Part> parts = new List<Part>();
    readonly List<Tree> trees = new List<Tree>();
    readonly List<Mush> mushes = new List<Mush>();
    readonly List<Fly> flies = new List<Fly>();

    readonly Queue<Orb> poolOrb = new Queue<Orb>();
    readonly Queue<Mon> poolMon = new Queue<Mon>();
    readonly Queue<Heart> poolHrt = new Queue<Heart>();
    readonly Queue<Part> poolPart = new Queue<Part>();

    Transform root;

    // floating combat text
    class Pop { public Vector3 world; public string text; public Color col; public float life, max; }
    readonly List<Pop> pops = new List<Pop>();

    // ----------------------------------------------------------------- AUDIO
    AudioSource au;
    AudioClip clJump1, clJump2, clOrb, clMile, clHeart, clHit, clOver;

    // sorting orders
    const int O_BG = -100, O_MOON = -90, O_TREE_FAR = -80, O_TREE_MID = -70,
              O_FLY = -60, O_GROUND = -50, O_EDGE = -48, O_MUSH = -40,
              O_ORB = -20, O_MON = -10, O_PLAYER = 0, O_PART = 10;

    const string BEST_KEY = "wispwood.best";   // PlayerPrefs key for the saved high score

    // ====================================================================== INIT
    void Awake()
    {
        Application.targetFrameRate = 60;
        QualitySettings.vSyncCount = 0;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Time.timeScale = 1f;   // reset in case a previous Editor session was stopped while paused

        best = PlayerPrefs.GetFloat(BEST_KEY, 0f);   // load saved high score on launch

        ColorUtility.TryParseHtmlString("#0b0a1e", out C_ABYSS);
        ColorUtility.TryParseHtmlString("#1a0d33", out C_MOSS);
        ColorUtility.TryParseHtmlString("#2ff3e0", out C_CYAN);
        ColorUtility.TryParseHtmlString("#e6fffb", out C_SPIRIT);
        ColorUtility.TryParseHtmlString("#6bffb0", out C_LEAF);
        ColorUtility.TryParseHtmlString("#ffd56b", out C_GOLD);
        ColorUtility.TryParseHtmlString("#ff9b54", out C_EMBER);
        ColorUtility.TryParseHtmlString("#ff5d9e", out C_PINK);
        ColorUtility.TryParseHtmlString("#06050f", out C_SHADOW);
        AURA = new[] { C_CYAN, C_CYAN, C_LEAF, Hex("#b6ff66"), C_GOLD, C_EMBER, C_PINK };

        // The four playable wisps. Wisp = untouched baseline; the rest add one small perk.
        chars = new[] {
            new Char { name = "Wisp",   perk = "Balanced. Plays like the original.",
                       body = Color.white,    ear = C_LEAF,
                       hearts = 3, comboTime = 2.6f, orbReach = 0f },
            new Char { name = "Ember",  perk = "Sturdy: starts with 4 hearts.",
                       body = Hex("#ffb38a"), ear = Hex("#ff7a4d"),
                       hearts = 4, comboTime = 2.6f, orbReach = 0f },
            new Char { name = "Lumen",  perk = "Radiant: combo aura lasts longer.",
                       body = Hex("#ffe7a0"), ear = C_GOLD,
                       hearts = 3, comboTime = 3.6f, orbReach = 0f },
            new Char { name = "Zephyr", perk = "Nimble: wider orb pickup reach.",
                       body = Hex("#a8ffd6"), ear = C_CYAN,
                       hearts = 3, comboTime = 2.6f, orbReach = 0.14f },
        };

        // Smallest vertical half-size that still fits the full jump arc + ground + margin,
        // so the wisp never needs to leave the top of the view on short (landscape) screens.
        float maxApex = (JUMP1 * JUMP1 + JUMP2 * JUMP2) / (2f * GRAVITY);
        minOrtho = (GROUND_H + 2f * PLAYER_R + maxApex + 0.9f) * 0.5f;

        BuildSprites();
        SetupCamera();

        root = new GameObject("Wispwood_World").transform;

        LayoutUpdate(true);   // compute world metrics before seeding the forest
        BuildBackground();
        BuildPlayer();
        BuildAudio();

        LayoutUpdate(true);
        SeatPlayerIdle();
        ApplyCharVisual();   // tint the idle wisp to the selected character (Wisp = unchanged)
    }

    Color Hex(string h) { Color c; ColorUtility.TryParseHtmlString(h, out c); return c; }

    // ----------------------------------------------------------------- TEXTURES
    Texture2D Tex(int w, int h)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
        t.filterMode = FilterMode.Bilinear;
        t.wrapMode = TextureWrapMode.Clamp;
        return t;
    }
    Sprite ToSprite(Texture2D t)
    {
        // 1 sprite = 1 world unit at scale 1  (ppu == texture size)
        return Sprite.Create(t, new Rect(0, 0, t.width, t.height),
                             new Vector2(0.5f, 0.5f), t.width);
    }
    void Plot(Color[] px, int size, int x, int y, Color c)
    {
        if (x < 0 || y < 0 || x >= size || y >= size) return;
        px[y * size + x] = c;
    }
    void Disc(Color[] px, int size, float cx, float cy, float r, Color c)
    {
        int x0 = Mathf.Max(0, (int)(cx - r) - 1), x1 = Mathf.Min(size - 1, (int)(cx + r) + 1);
        int y0 = Mathf.Max(0, (int)(cy - r) - 1), y1 = Mathf.Min(size - 1, (int)(cy + r) + 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                float a = Mathf.Clamp01(r - d);                  // 1px AA edge
                if (a <= 0) continue;
                Color e = px[y * size + x];
                px[y * size + x] = Color.Lerp(e, c, a * c.a);
            }
    }

    void BuildSprites()
    {
        // soft glow
        {
            int s = 128; var t = Tex(s, s); var px = new Color[s * s];
            float c = (s - 1) / 2f, rr = s / 2f;
            for (int y = 0; y < s; y++) for (int x = 0; x < s; x++)
                {
                    float dx = (x - c) / rr, dy = (y - c) / rr;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Pow(Mathf.Clamp01(1 - d), 2.4f);
                    px[y * s + x] = new Color(1, 1, 1, a);
                }
            t.SetPixels(px); t.Apply(); spGlow = ToSprite(t);
        }
        // crisp disc (soft 8% edge)
        {
            int s = 128; var t = Tex(s, s); var px = new Color[s * s];
            float c = (s - 1) / 2f, rr = s / 2f;
            for (int y = 0; y < s; y++) for (int x = 0; x < s; x++)
                {
                    float dx = (x - c) / rr, dy = (y - c) / rr;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = d < 0.86f ? 1f : Mathf.Clamp01(1 - (d - 0.86f) / 0.14f);
                    px[y * s + x] = new Color(1, 1, 1, a);
                }
            t.SetPixels(px); t.Apply(); spDisc = ToSprite(t);
        }
        // player body  (white core -> spirit -> cyan)
        {
            int s = 128; var t = Tex(s, s); var px = new Color[s * s];
            float c = (s - 1) / 2f, rr = s / 2f;
            for (int y = 0; y < s; y++) for (int x = 0; x < s; x++)
                {
                    float dx = (x - c) / rr, dy = (y - c) / rr;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    Color col = d < 0.45f ? Color.Lerp(Color.white, C_SPIRIT, d / 0.45f)
                                           : Color.Lerp(C_SPIRIT, C_CYAN, (d - 0.45f) / 0.55f);
                    float a = d < 0.9f ? 1f : Mathf.Clamp01(1 - (d - 0.9f) / 0.1f);
                    col.a = a; px[y * s + x] = col;
                }
            t.SetPixels(px); t.Apply(); spBody = ToSprite(t);
        }
        // face  (dark eyes + sparkles + smile)
        {
            int s = 128; var t = Tex(s, s); var px = new Color[s * s]; // transparent
            Color dark = Hex("#16243a");
            float eo = s * 0.20f, ey = s * 0.56f, er = s * 0.085f;
            Disc(px, s, s * 0.5f - eo, ey, er, dark);
            Disc(px, s, s * 0.5f + eo, ey, er, dark);
            Disc(px, s, s * 0.5f - eo + er * 0.4f, ey + er * 0.4f, er * 0.34f, Color.white);
            Disc(px, s, s * 0.5f + eo + er * 0.4f, ey + er * 0.4f, er * 0.34f, Color.white);
            // smile arc
            float scx = s * 0.5f, scy = s * 0.50f, sr = s * 0.20f;
            for (float a = 0.15f * Mathf.PI; a <= 0.85f * Mathf.PI; a += 0.012f)
            {
                float xx = scx + Mathf.Cos(Mathf.PI + a) * sr;       // lower arc
                float yy = scy + Mathf.Sin(Mathf.PI + a) * sr;
                Disc(px, s, xx, yy, s * 0.022f, dark);
            }
            t.SetPixels(px); t.Apply(); spFace = ToSprite(t);
        }
        // heart
        {
            int s = 128; var t = Tex(s, s); var px = new Color[s * s];
            for (int y = 0; y < s; y++) for (int x = 0; x < s; x++)
                {
                    float nx = (x / (float)s - 0.5f) * 2.8f;
                    float ny = (y / (float)s - 0.45f) * 2.8f;          // y up: lobes at top
                    float f = Mathf.Pow(nx * nx + ny * ny - 1f, 3f) - nx * nx * ny * ny * ny;
                    px[y * s + x] = f <= 0 ? Color.white : new Color(0, 0, 0, 0);
                }
            t.SetPixels(px); t.Apply(); spHeart = ToSprite(t);
        }
        // bg gradient
        {
            int s = 256; var t = new Texture2D(4, s, TextureFormat.RGBA32, false);
            t.filterMode = FilterMode.Bilinear; t.wrapMode = TextureWrapMode.Clamp;
            var px = new Color[4 * s];
            for (int y = 0; y < s; y++)
            {
                float tt = y / (float)(s - 1);                      // 0 bottom .. 1 top
                Color col = tt < 0.45f ? Color.Lerp(C_MOSS, Hex("#120b2c"), tt / 0.45f)
                                        : Color.Lerp(Hex("#120b2c"), C_ABYSS, (tt - 0.45f) / 0.55f);
                for (int x = 0; x < 4; x++) px[y * 4 + x] = col;
            }
            t.SetPixels(px); t.Apply();
            spGrad = Sprite.Create(t, new Rect(0, 0, 4, s), new Vector2(0.5f, 0.5f), s);
        }
        // ground gradient
        {
            int s = 64; var t = new Texture2D(4, s, TextureFormat.RGBA32, false);
            t.filterMode = FilterMode.Bilinear; t.wrapMode = TextureWrapMode.Clamp;
            var px = new Color[4 * s];
            for (int y = 0; y < s; y++)
            {
                float tt = y / (float)(s - 1);
                Color col = Color.Lerp(Hex("#0a0620"), Hex("#1c1140"), tt);
                for (int x = 0; x < 4; x++) px[y * 4 + x] = col;
            }
            t.SetPixels(px); t.Apply();
            spGround = Sprite.Create(t, new Rect(0, 0, 4, s), new Vector2(0.5f, 0.5f), s);
        }
        // white pixel
        {
            var t = Tex(4, 4); var px = new Color[16];
            for (int i = 0; i < 16; i++) px[i] = Color.white;
            t.SetPixels(px); t.Apply(); spPixel = ToSprite(t);
        }
    }

    // ----------------------------------------------------------------- CAMERA
    void SetupCamera()
    {
        cam = Camera.main;
        if (cam == null)
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            cam = go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
        }
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = C_ABYSS;
        cam.allowHDR = false;          // flat 2D sprites — no HDR needed (mobile GPU saving)
        cam.allowMSAA = false;
        cam.transform.position = new Vector3(0, 0, -10);
    }

    // ----------------------------------------------------------------- BUILD HELPERS
    SpriteRenderer NewSR(string name, Sprite sp, int order, Transform parent)
    {
        var go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp; sr.sortingOrder = order;
        return sr;
    }

    void BuildBackground()
    {
        NewSR("BG", spGrad, O_BG, root);

        // moons
        Color[] mc = { Hex("#3a2f6e"), Hex("#2a4a66"), Hex("#4a2f5e") };
        for (int i = 0; i < 3; i++)
        {
            var sr = NewSR("Moon", spGlow, O_MOON, root);
            sr.color = new Color(mc[i].r, mc[i].g, mc[i].b, Random.Range(0.10f, 0.18f));
            float r = Random.Range(2.2f, 4.2f);
            sr.transform.localScale = new Vector3(r, r, 1);
            sr.transform.localPosition = new Vector3(Random.Range(-halfW, halfW), Random.Range(1f, 4f), 0);
        }

        NewSR("Ground", spGround, O_GROUND, root);
        NewSR("Edge", spPixel, O_EDGE, root).color = new Color(C_LEAF.r, C_LEAF.g, C_LEAF.b, 0.85f);

        // trees
        float x = -halfW - 1f;
        while (x < halfW + 1.5f)
        {
            trees.Add(MakeTree(x, Random.value < 0.5f));
            x += Random.Range(0.55f, 1.15f);
        }
        // mushrooms
        x = -halfW;
        while (x < halfW + 0.5f)
        {
            mushes.Add(MakeMush(x));
            x += Random.Range(0.9f, 2.0f);
        }
        // fireflies
        for (int i = 0; i < 42; i++) flies.Add(MakeFly());
    }

    Tree MakeTree(float x, bool far)
    {
        var t = new Tree();
        t.go = new GameObject("Tree");
        t.go.transform.SetParent(root, false);
        t.tr = t.go.transform;
        t.far = far; t.x = x;
        float sc = far ? Random.Range(0.7f, 1.0f) : Random.Range(1.05f, 1.6f);
        int order = far ? O_TREE_FAR : O_TREE_MID;
        Color body = far ? Hex("#0a0a20") : Hex("#070612");

        // rim glow behind canopy
        var rim = NewSR("rim", spGlow, order - 1, t.tr);
        rim.color = far ? new Color(C_LEAF.r, C_LEAF.g, C_LEAF.b, 0.10f)
                         : new Color(C_LEAF.r, C_LEAF.g, C_LEAF.b, 0.16f);
        rim.transform.localScale = Vector3.one * (2.0f * sc);
        rim.transform.localPosition = new Vector3(0, 1.55f * sc, 0);

        // trunk
        var trunk = NewSR("trunk", spPixel, order, t.tr);
        trunk.color = C_SHADOW;
        trunk.transform.localScale = new Vector3(0.10f * sc, 1.1f * sc, 1);
        trunk.transform.localPosition = new Vector3(0, 0.55f * sc, 0);

        // canopy blobs
        Vector3[] blob = {
            new Vector3(0f, 1.55f, 0.95f), new Vector3(-0.45f, 1.20f, 0.78f),
            new Vector3(0.45f, 1.25f, 0.72f), new Vector3(0f, 0.95f, 0.82f)
        };
        foreach (var b in blob)
        {
            var c = NewSR("canopy", spDisc, order, t.tr);
            c.color = body;
            c.transform.localScale = Vector3.one * (b.z * sc);
            c.transform.localPosition = new Vector3(b.x * sc, b.y * sc, 0);
        }
        return t;
    }

    Mush MakeMush(float x)
    {
        var m = new Mush();
        m.go = new GameObject("Mush");
        m.go.transform.SetParent(root, false);
        m.tr = m.go.transform; m.x = x;
        Color col = new[] { C_CYAN, C_PINK, C_LEAF, C_GOLD }[Random.Range(0, 4)];
        float h = Random.Range(0.18f, 0.38f);

        var stem = NewSR("stem", spPixel, O_MUSH, m.tr);
        stem.color = new Color(C_SPIRIT.r, C_SPIRIT.g, C_SPIRIT.b, 0.25f);
        stem.transform.localScale = new Vector3(0.04f, h, 1);
        stem.transform.localPosition = new Vector3(0, h * 0.5f, 0);

        var glow = NewSR("capGlow", spGlow, O_MUSH, m.tr);
        glow.color = new Color(col.r, col.g, col.b, 0.85f);
        glow.transform.localScale = new Vector3(0.42f, 0.30f, 1);
        glow.transform.localPosition = new Vector3(0, h, 0);

        var cap = NewSR("cap", spDisc, O_MUSH + 1, m.tr);
        cap.color = col;
        cap.transform.localScale = new Vector3(0.22f, 0.14f, 1);
        cap.transform.localPosition = new Vector3(0, h, 0);
        return m;
    }

    Fly MakeFly()
    {
        var f = new Fly();
        f.go = new GameObject("Fly");
        f.go.transform.SetParent(root, false);
        f.tr = f.go.transform;
        f.sr = f.go.AddComponent<SpriteRenderer>();
        f.sr.sprite = spGlow; f.sr.sortingOrder = O_FLY;
        f.col = new[] { C_LEAF, C_CYAN, C_GOLD }[Random.Range(0, 3)];
        f.r = Random.Range(0.03f, 0.10f);
        f.sp = Random.Range(0.1f, 0.4f);
        f.drift = Random.Range(-0.15f, 0.15f);
        f.tw = Random.Range(0, 6.28f); f.tws = Random.Range(1.2f, 3f);
        f.x = Random.Range(-halfW, halfW);
        f.y = Random.Range(0f, 4f);
        f.tr.localScale = Vector3.one * (f.r * 4f);
        return f;
    }

    void BuildPlayer()
    {
        P.go = new GameObject("Player");
        P.go.transform.SetParent(root, false);
        P.tr = P.go.transform;

        P.aura = NewSR("aura", spGlow, O_PLAYER - 2, P.tr);
        P.earL = NewSR("earL", spDisc, O_PLAYER - 1, P.tr);
        P.earR = NewSR("earR", spDisc, O_PLAYER - 1, P.tr);
        P.body = NewSR("body", spBody, O_PLAYER, P.tr);
        P.face = NewSR("face", spFace, O_PLAYER + 1, P.tr);

        float d = PLAYER_R * 2f;
        P.body.transform.localScale = Vector3.one * d;
        P.face.transform.localScale = Vector3.one * d;

        P.earL.color = C_LEAF; P.earR.color = C_LEAF;
        P.earL.transform.localScale = new Vector3(0.14f, 0.24f, 1);
        P.earR.transform.localScale = new Vector3(0.14f, 0.24f, 1);
        P.earL.transform.localPosition = new Vector3(-0.16f, 0.30f, 0);
        P.earR.transform.localPosition = new Vector3(0.16f, 0.30f, 0);
        P.earL.transform.localRotation = Quaternion.Euler(0, 0, 22);
        P.earR.transform.localRotation = Quaternion.Euler(0, 0, -22);
    }

    // ====================================================================== AUDIO
    void BuildAudio()
    {
        au = gameObject.AddComponent<AudioSource>();
        au.playOnAwake = false;
        clJump1 = Synth(0.16f, (b, sr) => AddTone(b, sr, 0f, 0.16f, 360, 560, 0.22f, 1));
        clJump2 = Synth(0.16f, (b, sr) => AddTone(b, sr, 0f, 0.16f, 520, 760, 0.22f, 1));
        clOrb = Synth(0.13f, (b, sr) => AddTone(b, sr, 0f, 0.13f, 560, 760, 0.18f, 0));
        clMile = Synth(0.40f, (b, sr) => {
            AddTone(b, sr, 0f, 0.22f, 523, 523, 0.14f, 0);
            AddTone(b, sr, 0.06f, 0.24f, 659, 659, 0.12f, 0);
            AddTone(b, sr, 0.12f, 0.26f, 784, 784, 0.11f, 0);
        });
        clHeart = Synth(0.20f, (b, sr) => {
            AddTone(b, sr, 0f, 0.14f, 660, 990, 0.18f, 0);
            AddTone(b, sr, 0.05f, 0.16f, 880, 880, 0.13f, 0);
        });
        clHit = Synth(0.32f, (b, sr) => {
            AddTone(b, sr, 0f, 0.30f, 150, 70, 0.22f, 2);
            AddNoise(b, sr, 0f, 0.22f, 0.16f);
        });
        clOver = Synth(0.70f, (b, sr) => {
            AddTone(b, sr, 0f, 0.30f, 330, 160, 0.18f, 0);
            AddTone(b, sr, 0.18f, 0.45f, 220, 90, 0.16f, 0);
        });
    }

    delegate void Filler(float[] buf, int sr);
    AudioClip Synth(float dur, Filler fill)
    {
        int sr = 44100; int n = Mathf.Max(1, (int)(dur * sr));
        var buf = new float[n];
        fill(buf, sr);
        for (int i = 0; i < n; i++) buf[i] = Mathf.Clamp(buf[i], -1f, 1f);
        var c = AudioClip.Create("sfx", n, 1, sr, false);
        c.SetData(buf, 0);
        return c;
    }
    void AddTone(float[] buf, int sr, float start, float dur, float f0, float f1, float vol, int wave)
    {
        int s0 = (int)(start * sr), n = (int)(dur * sr);
        for (int i = 0; i < n; i++)
        {
            int idx = s0 + i; if (idx >= buf.Length) break;
            float prog = i / (float)n;
            float t = i / (float)sr;
            float f = Mathf.Lerp(f0, f1, prog);
            float ph = 2 * Mathf.PI * f * t;
            float s = wave == 0 ? Mathf.Sin(ph)
                    : wave == 1 ? Mathf.PingPong(ph / Mathf.PI, 1f) * 2f - 1f      // ~triangle
                                : Mathf.Sign(Mathf.Sin(ph)) * 0.7f;                // ~saw-ish
            float env = Mathf.Exp(-5f * prog);
            buf[idx] += s * env * vol;
        }
    }
    void AddNoise(float[] buf, int sr, float start, float dur, float vol)
    {
        int s0 = (int)(start * sr), n = (int)(dur * sr);
        for (int i = 0; i < n; i++)
        {
            int idx = s0 + i; if (idx >= buf.Length) break;
            float env = 1f - i / (float)n;
            buf[idx] += (Random.value * 2 - 1) * env * vol;
        }
    }
    void Play(AudioClip c, float pitch = 1f, float vol = 1f)
    {
        if (muted || c == null) return;
        au.pitch = pitch;
        au.PlayOneShot(c, vol);
    }

    // ====================================================================== LAYOUT
    void LayoutUpdate(bool force)
    {
        if (!force && Screen.width == lastW && Screen.height == lastH) return;
        lastW = Screen.width; lastH = Screen.height;

        float aspect = Mathf.Max(0.1f, (float)Screen.width / Screen.height);
        // Vertical field never drops below minOrtho, so a full jump always fits on screen.
        // On portrait this is identical to before; on short/landscape views it zooms to fit.
        orthoSize = Mathf.Max((WORLD_W * 0.5f) / aspect, minOrtho);
        cam.orthographicSize = orthoSize;
        worldTop = orthoSize; worldBottom = -orthoSize;
        groundY = worldBottom + GROUND_H;

        halfW = orthoSize * aspect;            // half of the ACTUAL visible width
        float visW = halfW * 2f;
        // player sits ~18% in from the left edge, but always fully inside the view
        playerX = Mathf.Clamp(-halfW + 0.18f * visW, -halfW + PLAYER_R, halfW - PLAYER_R);

        // bg cover sized to the real visible width (so no edge shows during shake/rotation)
        foreach (Transform ch in root)
        {
            if (ch.name == "BG") { ch.localScale = new Vector3(visW * 1.12f, orthoSize * 2.2f, 1); ch.localPosition = Vector3.zero; }
            else if (ch.name == "Ground") { float gh = GROUND_H + 0.4f; ch.localScale = new Vector3(visW * 1.12f, gh, 1); ch.localPosition = new Vector3(0, groundY - gh * 0.5f, 0); }
            else if (ch.name == "Edge") { ch.localScale = new Vector3(visW * 1.12f, 0.045f, 1); ch.localPosition = new Vector3(0, groundY, 0); }
        }
        foreach (var t in trees) t.tr.localPosition = new Vector3(t.x, groundY, 0);
        foreach (var m in mushes) m.tr.localPosition = new Vector3(m.x, groundY, 0);
        foreach (var f in flies) { if (f.y < groundY + 0.4f) f.y = groundY + 0.4f; if (f.y > worldTop - 0.2f) f.y = worldTop - 0.2f; f.tr.localPosition = new Vector3(f.x, f.y, 0); }
    }

    void SeatPlayerIdle()
    {
        P.y = groundY + PLAYER_R;
        P.tr.localPosition = new Vector3(playerX, P.y, 0);
        SetAura();
    }

    // ====================================================================== CONTROL
    void StartGame()
    {
        ApplyChar();   // copy chosen character's hearts / combo time / orb reach + tint

        foreach (var o in orbs) Despawn(o); orbs.Clear();
        foreach (var m in mons) Despawn(m); mons.Clear();
        foreach (var h in hearts) Despawn(h); hearts.Clear();
        foreach (var p in parts) Despawn(p); parts.Clear();
        pops.Clear();

        speed = SPD_BASE; elapsed = 0; score = 0; health = maxHealth; invuln = 0;
        spawnTimer = 0.8f; shake = 0; lastObstacle = false;
        combo = 0; comboTimer = 0; mult = 1; prevMult = 1; maxCombo = 0;
        slideKey = false; slideUntil = 0;

        P.y = groundY + PLAYER_R; P.vy = 0; P.jumps = 0; P.grounded = true; P.t = 0; P.slide = false; P.squash = 0;
        SetPlayerVisible(true);

        paused = false; Time.timeScale = 1f;
        state = St.Playing;
    }

    void GameOver()
    {
        state = St.GameOver;
        Play(clOver);
        if (score > best)
        {
            best = score;
            PlayerPrefs.SetFloat(BEST_KEY, best);   // persist the new high score
            PlayerPrefs.Save();
        }
    }

    // ---- characters ----
    void ApplyChar()
    {
        Char c = chars[Mathf.Clamp(selectedChar, 0, chars.Length - 1)];
        maxHealth = c.hearts;
        comboTimeCur = c.comboTime;
        orbBonus = c.orbReach;
        ApplyCharVisual();
    }
    void ApplyCharVisual()
    {
        if (chars == null || P.body == null) return;
        Char c = chars[Mathf.Clamp(selectedChar, 0, chars.Length - 1)];
        P.body.color = c.body;
        P.earL.color = c.ear; P.earR.color = c.ear;
    }
    void SelectChar(int i)
    {
        selectedChar = Mathf.Clamp(i, 0, chars.Length - 1);
        ApplyCharVisual();      // update the idle wisp on the menu immediately
        state = St.Start;
    }

    // ---- pause ----
    void TogglePause()
    {
        if (state != St.Playing) return;
        paused = !paused;
        Time.timeScale = paused ? 0f : 1f;
    }
    void RestartFromPause()
    {
        paused = false; Time.timeScale = 1f;
        StartGame();
    }
    void GoHome()
    {
        paused = false; Time.timeScale = 1f;
        foreach (var o in orbs) Despawn(o); orbs.Clear();
        foreach (var m in mons) Despawn(m); mons.Clear();
        foreach (var h in hearts) Despawn(h); hearts.Clear();
        foreach (var p in parts) Despawn(p); parts.Clear();
        pops.Clear();
        combo = 0; mult = 1; prevMult = 1; comboTimer = 0;
        SetPlayerVisible(true);
        state = St.Start;
    }

    void Jump()
    {
        if (state != St.Playing) return;
        if (P.jumps < 2)
        {
            P.vy = (P.jumps == 0) ? JUMP1 : JUMP2;
            P.jumps++; P.grounded = false; P.slide = false;
            Play(P.jumps == 2 ? clJump2 : clJump1);
            Puff(playerX, P.y - PLAYER_R, P.jumps == 2 ? C_LEAF : C_CYAN, 8);
        }
    }
    void StartSlide()
    {
        if (state != St.Playing) return;
        if (!P.grounded) P.vy -= FASTFALL;
        slideUntil = Time.time + 0.6f;
    }

    int MultFor(int c)
    {
        if (c >= 40) return 6; if (c >= 25) return 5; if (c >= 15) return 4;
        if (c >= 8) return 3; if (c >= 4) return 2; return 1;
    }
    Color Aura(int m) { return AURA[Mathf.Clamp(m, 0, AURA.Length - 1)]; }

    void ToggleMute() { muted = !muted; }

    // ====================================================================== INPUT
    Vector2 touchStart; float touchT0; bool touchSwiped;
    float swipePx { get { return Mathf.Max(40f, Screen.height * 0.05f); } }

    // Input auto-adapts: uses the new Input System when active (Unity 6 default),
    // and falls back to the legacy Input Manager otherwise. Works in either setting.
    void HandleInput()
    {
#if ENABLE_INPUT_SYSTEM
        HandleInputNew();
#elif ENABLE_LEGACY_INPUT_MANAGER
        HandleInputLegacy();
#endif
    }

#if ENABLE_INPUT_SYSTEM
    void HandleInputNew()
    {
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.escapeKey.wasPressedThisFrame || kb.pKey.wasPressedThisFrame)
            {
                if (state == St.Playing) TogglePause();
                else if (state == St.CharSelect) state = St.Start;
            }

            if (paused)
            {
                if (kb.rKey.wasPressedThisFrame) RestartFromPause();
                if (kb.mKey.wasPressedThisFrame) ToggleMute();
                slideKey = false;
            }
            else
            {
                if (kb.spaceKey.wasPressedThisFrame || kb.upArrowKey.wasPressedThisFrame || kb.wKey.wasPressedThisFrame)
                { if (state == St.Playing) Jump(); else if (state == St.Start || state == St.GameOver) StartGame(); }

                slideKey = kb.downArrowKey.isPressed || kb.sKey.isPressed;
                if ((kb.downArrowKey.wasPressedThisFrame || kb.sKey.wasPressedThisFrame) && state == St.Playing && !P.grounded) P.vy -= FASTFALL;
                if (kb.rKey.wasPressedThisFrame && state == St.GameOver) StartGame();
                if (kb.mKey.wasPressedThisFrame) ToggleMute();
                if ((kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) && (state == St.Start || state == St.GameOver)) StartGame();
            }
        }
        else slideKey = false;

        var ts = Touchscreen.current;

        // mouse — only when no touchscreen is present (avoids touch->mouse double input)
        var ms = Mouse.current;
        if (ms != null && ts == null && ms.leftButton.wasPressedThisFrame)
        {
            Vector2 mp = ms.position.ReadValue();
            if (!HandleUIPress(mp))
            { if (state == St.Playing) Jump(); else if (state == St.Start || state == St.GameOver) StartGame(); }
        }

        // touch (primary finger)
        if (ts != null)
        {
            var pt = ts.primaryTouch;
            TPhase phase = pt.phase.ReadValue();
            Vector2 pos = pt.position.ReadValue();
            if (phase == TPhase.Began)
            {
                touchStart = pos; touchT0 = Time.time; touchSwiped = false;
                if (HandleUIPress(pos)) touchSwiped = true;                       // tap landed on UI
                else if (state == St.Start || state == St.GameOver) { StartGame(); touchSwiped = true; }
            }
            else if (phase == TPhase.Moved)
            {
                if (!paused && !touchSwiped && state == St.Playing && (touchStart.y - pos.y) > swipePx) { touchSwiped = true; StartSlide(); }
            }
            else if (phase == TPhase.Ended || phase == TPhase.Canceled)
            {
                if (!paused && state == St.Playing && !touchSwiped &&
                    (Time.time - touchT0) < 0.28f &&
                    Vector2.Distance(touchStart, pos) < swipePx)
                    Jump();
            }
        }
    }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
    void HandleInputLegacy()
    {
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.P))
        {
            if (state == St.Playing) TogglePause();
            else if (state == St.CharSelect) state = St.Start;
        }

        if (paused)
        {
            if (Input.GetKeyDown(KeyCode.R)) RestartFromPause();
            if (Input.GetKeyDown(KeyCode.M)) ToggleMute();
            slideKey = false;
        }
        else
        {
            if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W))
            { if (state == St.Playing) Jump(); else if (state == St.Start || state == St.GameOver) StartGame(); }

            slideKey = Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S);
            if ((Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) && state == St.Playing && !P.grounded) P.vy -= FASTFALL;
            if (Input.GetKeyDown(KeyCode.R) && state == St.GameOver) StartGame();
            if (Input.GetKeyDown(KeyCode.M)) ToggleMute();
            if (Input.GetKeyDown(KeyCode.Return) && (state == St.Start || state == St.GameOver)) StartGame();
        }

        if (Input.touchCount == 0 && Input.GetMouseButtonDown(0))
        {
            Vector2 mp = Input.mousePosition;
            if (!HandleUIPress(mp))
            { if (state == St.Playing) Jump(); else if (state == St.Start || state == St.GameOver) StartGame(); }
        }

        if (Input.touchCount > 0)
        {
            Touch t = Input.GetTouch(0);
            if (t.phase == TouchPhase.Began)
            {
                touchStart = t.position; touchT0 = Time.time; touchSwiped = false;
                if (HandleUIPress(t.position)) touchSwiped = true;
                else if (state == St.Start || state == St.GameOver) { StartGame(); touchSwiped = true; }
            }
            else if (t.phase == TouchPhase.Moved)
            {
                if (!paused && !touchSwiped && state == St.Playing && (touchStart.y - t.position.y) > swipePx)
                { touchSwiped = true; StartSlide(); }
            }
            else if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
            {
                if (!paused && state == St.Playing && !touchSwiped &&
                    (Time.time - touchT0) < 0.28f &&
                    Vector2.Distance(touchStart, t.position) < swipePx)
                    Jump();
            }
        }
    }
#endif

    // ====================================================================== UPDATE
    void Update()
    {
        LayoutUpdate(false);
        HandleInput();

        if (state != St.Playing) { cam.transform.position = new Vector3(0, 0, -10); SeatPlayerIdle(); return; }
        if (paused) return;   // frozen — pause panel handled in OnGUI

        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        Step(dt);
    }

    void Step(float dt)
    {
        elapsed += dt;
        speed = Mathf.Min(SPD_MAX, SPD_BASE + elapsed * SPD_ACCEL);
        score += speed * dt * 0.6f;
        if (shake > 0) shake = Mathf.Max(0, shake - dt * 0.9f);
        if (invuln > 0) invuln -= dt;

        P.slide = P.grounded && (slideKey || Time.time < slideUntil);

        // physics
        P.t += dt;
        P.vy -= GRAVITY * dt;
        P.y += P.vy * dt;
        float floor = groundY + PLAYER_R;
        if (P.y <= floor)
        {
            if (!P.grounded && P.vy < -5f) Puff(playerX, groundY, C_LEAF, 6);
            P.y = floor; P.vy = 0; P.grounded = true; P.jumps = 0;
        }
        else P.grounded = false;

        // ceiling — wisp can never leave the top of the view. With minOrtho headroom the
        // full jump arc fits, so this only ever bites on extreme viewports (pure safety net).
        float ceil = worldTop - PLAYER_R - 0.45f;
        if (P.y > ceil) { P.y = ceil; if (P.vy > 0) P.vy = 0f; }

        P.squash += ((P.slide ? 1f : 0f) - P.squash) * Mathf.Min(1f, dt * 18f);

        // trail
        if (Random.value < 0.9f)
            Spark(playerX - 0.1f, P.y + Random.Range(-0.12f, 0.12f),
                  -speed * 0.15f + Random.Range(-0.3f, 0.3f), Random.Range(-0.2f, 0.2f),
                  Random.Range(0.25f, 0.5f), Aura(mult), Random.Range(0.04f, 0.08f));

        // combo decay
        if (combo > 0) { comboTimer -= dt; if (comboTimer <= 0) { combo = 0; } }
        mult = MultFor(combo);
        if (mult > prevMult)
        {
            AddPop(playerX, P.y + 0.7f, "x" + mult + "!", Aura(mult));
            Burst(playerX, P.y, Aura(mult), 14, 1.9f);
            Play(clMile);
            shake = Mathf.Min(0.13f, shake + 0.06f);
        }
        prevMult = mult;

        // spawn
        spawnTimer -= dt;
        if (spawnTimer <= 0)
        {
            Spawn();
            float gap = Mathf.Max(SPAWN_MIN, SPAWN_BASE - elapsed * SPAWN_ACCEL);
            spawnTimer = gap + Random.Range(0, SPAWN_VAR);
        }

        float dx = speed * dt;

        // orbs
        for (int i = orbs.Count - 1; i >= 0; i--)
        {
            var o = orbs[i];
            o.x -= dx; o.t += dt * 4f;
            float pulse = 1f + Mathf.Sin(o.t) * 0.12f;
            o.core.transform.localScale = Vector3.one * (0.34f * pulse);
            o.halo.transform.localScale = Vector3.one * (0.7f * pulse);
            o.tr.localPosition = new Vector3(o.x, o.y, 0);

            float ddx = o.x - playerX, ddy = o.y - P.y;
            float pr = PLAYER_R + 0.02f + orbBonus;
            if (ddx * ddx + ddy * ddy < (0.17f + pr) * (0.17f + pr))
            {
                combo++; comboTimer = comboTimeCur;
                if (combo > maxCombo) maxCombo = combo;
                int gain = ORB_VALUE * MultFor(combo);
                score += gain;
                Burst(o.x, o.y, Aura(MultFor(combo)), 10, 1.7f);
                AddPop(o.x, o.y + 0.15f, "+" + gain, C_GOLD);
                Play(clOrb, 1f + Mathf.Min(combo, 30) * 0.03f);
                Despawn(o); orbs.RemoveAt(i);
                continue;
            }
            if (o.x < -halfW - 0.6f) { Despawn(o); orbs.RemoveAt(i); }
        }

        // hearts
        for (int i = hearts.Count - 1; i >= 0; i--)
        {
            var h = hearts[i];
            h.x -= dx; h.t += dt * 3f;
            h.tr.localPosition = new Vector3(h.x, h.y + Mathf.Sin(h.t) * 0.06f, 0);
            float ddx = h.x - playerX, ddy = h.y - P.y;
            if (ddx * ddx + ddy * ddy < (0.28f + PLAYER_R) * (0.28f + PLAYER_R))
            {
                if (health < maxHealth) health++;
                Burst(h.x, h.y, C_PINK, 16, 1.8f);
                AddPop(h.x, h.y + 0.2f, "+heart", C_PINK);
                Play(clHeart);
                Despawn(h); hearts.RemoveAt(i); continue;
            }
            if (h.x < -halfW - 0.6f) { Despawn(h); hearts.RemoveAt(i); }
        }

        // monsters
        for (int i = mons.Count - 1; i >= 0; i--)
        {
            var m = mons[i];
            m.x -= dx; m.t += dt; m.eyeT += dt;
            float my = m.y;
            if (m.type == 0) { } else { my = m.baseY + Mathf.Sin(m.t * 2.4f) * 0.1f; }
            m.tr.localPosition = new Vector3(m.x, my, 0);
            float eg = 0.6f + 0.4f * Mathf.Sin(m.eyeT * 5f);
            float es = 0.14f + eg * 0.04f;
            m.eyeL.transform.localScale = Vector3.one * es;
            m.eyeR.transform.localScale = Vector3.one * es;

            if (!m.hit && invuln <= 0)
            {
                float ph = P.slide ? PLAYER_R * 0.9f : PLAYER_R * 2f;
                float pTop = P.slide ? (P.y - PLAYER_R + ph) : (P.y + PLAYER_R);
                float pBot = P.y - PLAYER_R;
                float pL = playerX - PLAYER_R * 0.8f, pR = playerX + PLAYER_R * 0.8f;
                float mL = m.x - m.w * 0.5f, mR = m.x + m.w * 0.5f;
                float mB = my - m.h * 0.5f, mT = my + m.h * 0.5f;
                if (pL < mR && pR > mL && pBot < mT && pTop > mB)
                {
                    m.hit = true; health--;
                    invuln = INVULN; combo = 0; mult = 1; prevMult = 1; shake = 0.13f;
                    Burst(playerX, P.y, C_PINK, 22, 2.4f);
                    Play(clHit);
                    if (health <= 0) { GameOver(); UpdatePlayerVisual(dt); return; }
                }
            }
            if (m.x < -halfW - 1f) { Despawn(m); mons.RemoveAt(i); }
        }

        // particles
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            var p = parts[i];
            p.life -= dt;
            p.x += p.vx * dt; p.y += p.vy * dt; p.vy -= 3f * dt; p.vx *= 0.98f;
            if (p.life <= 0) { Despawn(p); parts.RemoveAt(i); continue; }
            float a = Mathf.Max(0, p.life / p.max);
            p.sr.color = new Color(p.col.r, p.col.g, p.col.b, a);
            p.tr.localPosition = new Vector3(p.x, p.y, 0);
            p.tr.localScale = Vector3.one * (p.size * (0.4f + a));
        }

        // pops
        for (int i = pops.Count - 1; i >= 0; i--)
        {
            pops[i].life -= dt;
            pops[i].world.y += 0.6f * dt;
            if (pops[i].life <= 0) pops.RemoveAt(i);
        }

        // parallax + fireflies
        foreach (var t in trees)
        {
            t.x -= speed * (t.far ? 0.12f : 0.32f) * dt;
            if (t.x < -halfW - 1.6f) t.x = halfW + Random.Range(0.4f, 1.6f);
            t.tr.localPosition = new Vector3(t.x, groundY, 0);
        }
        foreach (var m in mushes)
        {
            m.x -= dx;
            if (m.x < -halfW - 0.5f) m.x = halfW + Random.Range(0.9f, 2.2f);
            m.tr.localPosition = new Vector3(m.x, groundY, 0);
        }
        foreach (var f in flies)
        {
            f.x -= f.sp * dt + speed * 0.05f * dt;
            f.y += f.drift * dt;
            f.tw += f.tws * dt;
            if (f.x < -halfW - 0.2f) { f.x = halfW + Random.Range(0, 0.4f); f.y = Random.Range(groundY + 0.4f, worldTop - 0.2f); }
            if (f.y < groundY + 0.3f) f.y = groundY + 0.3f;
            if (f.y > worldTop - 0.1f) f.y = worldTop - 0.1f;
            float a = 0.35f + 0.45f * Mathf.Abs(Mathf.Sin(f.tw));
            f.sr.color = new Color(f.col.r, f.col.g, f.col.b, a);
            f.tr.localPosition = new Vector3(f.x, f.y, 0);
        }

        float _ox = 0, _oy = 0;
        if (shake > 0) { _ox = Random.Range(-shake, shake); _oy = Random.Range(-shake, shake); }
        cam.transform.position = new Vector3(_ox, _oy, -10);

        UpdatePlayerVisual(dt);
    }

    void UpdatePlayerVisual(float dt)
    {
        float bob = P.grounded ? Mathf.Sin(P.t * 9f) * 0.04f : 0f;
        P.tr.localPosition = new Vector3(playerX, P.y + bob, 0);

        float sq = P.squash;
        P.tr.localScale = new Vector3(1f + sq * 0.45f, 1f - sq * 0.5f, 1f);

        SetAura();

        // invuln blink
        bool vis = !(invuln > 0 && Mathf.FloorToInt(invuln * 16f) % 2 == 0);
        SetPlayerVisible(vis);
    }

    void SetAura()
    {
        Color c = Aura(mult);
        P.aura.color = new Color(c.r, c.g, c.b, 0.45f);
        float ad = PLAYER_R * 2f * (1.4f + mult * 0.18f);
        P.aura.transform.localScale = Vector3.one * ad;
    }
    void SetPlayerVisible(bool v)
    {
        P.aura.enabled = v; P.body.enabled = v; P.face.enabled = v; P.earL.enabled = v; P.earR.enabled = v;
    }

    // ====================================================================== SPAWN
    void Spawn()
    {
        float r = Random.value;
        string kind;
        if (lastObstacle && r < 0.62f)
            kind = new[] { "orbArc", "orbLine", "orbCluster", "orbArc" }[Random.Range(0, 4)];
        else
        {
            if (r < 0.26f) kind = "ground";
            else if (r < 0.48f) kind = "air";
            else if (r < 0.60f) kind = "groundOrbs";
            else if (r < 0.74f) kind = "orbArc";
            else if (r < 0.86f) kind = "orbLine";
            else if (r < 0.965f) kind = "orbCluster";
            else kind = "heart";
        }
        float X = halfW + 0.8f;
        lastObstacle = false;

        if (kind == "ground" || kind == "groundOrbs")
        {
            SpawnMon(X, 0);
            lastObstacle = true;
            if (kind == "groundOrbs")
                for (int i = 0; i < 5; i++)
                { float tt = i / 4f; SpawnOrb(X - 0.4f + tt * 1.9f, groundY + 1.0f + Mathf.Sin(tt * Mathf.PI) * 1.6f); }
        }
        else if (kind == "air")
        {
            SpawnMon(X, 1);
            lastObstacle = true;
            if (Random.value < 0.7f)
                for (int i = 0; i < 4; i++) SpawnOrb(X - 0.3f + i * 0.55f, groundY + 0.3f);
        }
        else if (kind == "orbArc")
        {
            float baseY = groundY + Random.Range(1.0f, 1.9f);
            for (int i = 0; i < 6; i++)
            { float tt = i / 5f; SpawnOrb(X + tt * 2.2f, baseY + Mathf.Sin(tt * Mathf.PI) * 1.3f); }
        }
        else if (kind == "orbLine")
        {
            float y = groundY + Random.Range(0.6f, 2.2f);
            for (int i = 0; i < 5; i++) SpawnOrb(X + i * 0.58f, y);
        }
        else if (kind == "orbCluster")
        {
            float cx = X + 0.4f, cy = groundY + Random.Range(0.9f, 2.2f);
            for (int i = 0; i < 5; i++)
            { float a = i / 5f * 6.28f; SpawnOrb(cx + Mathf.Cos(a) * 0.4f, cy + Mathf.Sin(a) * 0.4f); }
        }
        else if (kind == "heart")
            SpawnHeart(X, groundY + Random.Range(0.9f, 1.9f));
    }

    // ====================================================================== POOLS
    void SpawnOrb(float x, float y)
    {
        Orb o = poolOrb.Count > 0 ? poolOrb.Dequeue() : null;
        if (o == null)
        {
            o = new Orb();
            o.go = new GameObject("Orb"); o.go.transform.SetParent(root, false); o.tr = o.go.transform;
            o.halo = NewSR("halo", spGlow, O_ORB, o.tr); o.halo.color = new Color(C_GOLD.r, C_GOLD.g, C_GOLD.b, 0.5f);
            o.core = NewSR("core", spDisc, O_ORB + 1, o.tr); o.core.color = C_GOLD;
        }
        o.x = x; o.y = y; o.t = Random.Range(0, 6.28f);
        o.go.SetActive(true); o.tr.localPosition = new Vector3(x, y, 0);
        orbs.Add(o);
    }
    void Despawn(Orb o) { o.go.SetActive(false); poolOrb.Enqueue(o); }

    void SpawnMon(float x, int type)
    {
        Mon m = poolMon.Count > 0 ? poolMon.Dequeue() : null;
        if (m == null)
        {
            m = new Mon();
            m.go = new GameObject("Mon"); m.go.transform.SetParent(root, false); m.tr = m.go.transform;
            m.body = NewSR("body", spDisc, O_MON, m.tr); m.body.color = C_SHADOW;
            m.b2 = NewSR("b2", spDisc, O_MON, m.tr); m.b2.color = C_SHADOW;
            m.eyeL = NewSR("eyeL", spGlow, O_MON + 2, m.tr); m.eyeL.color = C_PINK;
            m.eyeR = NewSR("eyeR", spGlow, O_MON + 2, m.tr); m.eyeR.color = C_PINK;
        }
        m.type = type; m.x = x; m.t = Random.Range(0, 6.28f); m.eyeT = Random.Range(0, 6.28f); m.hit = false;

        if (type == 0)   // ground beast — jump
        {
            m.w = 0.9f; m.h = 0.9f; m.y = groundY + m.h * 0.5f;
            m.body.transform.localScale = new Vector3(0.95f, 0.85f, 1);
            m.body.transform.localPosition = new Vector3(0, 0, 0);
            m.b2.transform.localScale = new Vector3(0.6f, 0.55f, 1);
            m.b2.transform.localPosition = new Vector3(0, 0.28f, 0);
        }
        else             // floater — slide
        {
            m.w = 0.95f; m.h = 0.8f; m.baseY = groundY + 0.6f + m.h * 0.5f; m.y = m.baseY;
            m.body.transform.localScale = new Vector3(0.85f, 0.95f, 1);
            m.body.transform.localPosition = new Vector3(0, 0.05f, 0);
            m.b2.transform.localScale = new Vector3(0.5f, 0.4f, 1);
            m.b2.transform.localPosition = new Vector3(0, -0.32f, 0);
        }
        m.eyeL.transform.localPosition = new Vector3(-0.17f, m.type == 0 ? 0.05f : 0.1f, 0);
        m.eyeR.transform.localPosition = new Vector3(0.17f, m.type == 0 ? 0.05f : 0.1f, 0);

        m.go.SetActive(true); m.tr.localPosition = new Vector3(m.x, m.y, 0);
        mons.Add(m);
    }
    void Despawn(Mon m) { m.go.SetActive(false); poolMon.Enqueue(m); }

    void SpawnHeart(float x, float y)
    {
        Heart h = poolHrt.Count > 0 ? poolHrt.Dequeue() : null;
        if (h == null)
        {
            h = new Heart();
            h.go = new GameObject("Heart"); h.go.transform.SetParent(root, false); h.tr = h.go.transform;
            h.halo = NewSR("halo", spGlow, O_ORB, h.tr); h.halo.color = new Color(C_PINK.r, C_PINK.g, C_PINK.b, 0.5f);
            h.halo.transform.localScale = Vector3.one * 0.9f;
            h.core = NewSR("core", spHeart, O_ORB + 1, h.tr); h.core.color = C_PINK;
            h.core.transform.localScale = Vector3.one * 0.55f;
        }
        h.x = x; h.y = y; h.t = Random.Range(0, 6.28f);
        h.go.SetActive(true); h.tr.localPosition = new Vector3(x, y, 0);
        hearts.Add(h);
    }
    void Despawn(Heart h) { h.go.SetActive(false); poolHrt.Enqueue(h); }

    // particles
    void Spark(float x, float y, float vx, float vy, float life, Color col, float size)
    {
        Part p = poolPart.Count > 0 ? poolPart.Dequeue() : null;
        if (p == null)
        {
            p = new Part();
            p.go = new GameObject("Part"); p.go.transform.SetParent(root, false); p.tr = p.go.transform;
            p.sr = p.go.AddComponent<SpriteRenderer>(); p.sr.sprite = spGlow; p.sr.sortingOrder = O_PART;
        }
        p.x = x; p.y = y; p.vx = vx; p.vy = vy; p.life = life; p.max = life; p.size = size; p.col = col;
        p.sr.color = col;
        p.go.SetActive(true); p.tr.localPosition = new Vector3(x, y, 0); p.tr.localScale = Vector3.one * size;
        parts.Add(p);
    }
    void Despawn(Part p) { p.go.SetActive(false); poolPart.Enqueue(p); }

    void Burst(float x, float y, Color col, int n, float spd)
    {
        for (int i = 0; i < n; i++)
        {
            float a = Random.value * 6.28f, s = Random.Range(spd * 0.3f, spd);
            Spark(x, y, Mathf.Cos(a) * s, Mathf.Sin(a) * s, Random.Range(0.3f, 0.7f), col, Random.Range(0.04f, 0.09f));
        }
    }
    void Puff(float x, float y, Color col, int n)
    {
        for (int i = 0; i < n; i++)
        {
            float a = Mathf.PI * 0.5f + Random.Range(-1.1f, 1.1f), s = Random.Range(0.6f, 1.8f);
            Spark(x, y, Mathf.Cos(a) * s, Mathf.Sin(a) * s + 0.6f, Random.Range(0.25f, 0.5f), col, Random.Range(0.04f, 0.08f));
        }
    }
    void AddPop(float x, float y, string text, Color col)
    {
        pops.Add(new Pop { world = new Vector3(x, y, 0), text = text, col = col, life = 0.9f, max = 0.9f });
    }

    // ====================================================================== UI (OnGUI)
    Texture2D _panelTex, _btnTex, _barTex, _btn2Tex;
    GUIStyle _title, _h2, _sub, _body, _btn, _btn2, _label, _num, _small;
    GUIStyle _mute, _comboMult, _comboStreak, _pop, _bigNum, _goldNum, _keys;
    GUIStyle _cardName, _cardPerk;
    float _builtScale = -1f;   // GUI styles are rebuilt only when this changes (mobile GC saving)
    float _uiScale;

    void EnsureGUI()
    {
        if (_panelTex == null)
        {
            _panelTex = SolidTex(new Color(0.05f, 0.04f, 0.10f, 0.80f));
            _btnTex = SolidTex(new Color(C_CYAN.r, C_CYAN.g, C_CYAN.b, 0.90f));
            _barTex = SolidTex(Color.white);
            _btn2Tex = SolidTex(new Color(0.15f, 0.18f, 0.30f, 0.95f));
        }
        float scale = Mathf.Clamp(Mathf.Min(Screen.width, Screen.height) / 720f, 0.85f, 3f);
        if (scale == _builtScale) return;   // styles already built for this size — no per-frame allocations
        _builtScale = scale; _uiScale = scale;
        int F(int s) { return Mathf.RoundToInt(s * scale); }

        _title = St2(F(60), C_CYAN, TextAnchor.MiddleCenter, FontStyle.Bold);
        _h2 = St2(F(34), C_PINK, TextAnchor.MiddleCenter, FontStyle.Bold);
        _sub = St2(F(16), new Color(0.81f, 1f, 0.98f, 0.85f), TextAnchor.MiddleCenter, FontStyle.Normal);
        _body = St2(F(15), new Color(0.81f, 0.91f, 1f, 0.78f), TextAnchor.MiddleCenter, FontStyle.Normal);
        _btn = St2(F(20), new Color(0.02f, 0.07f, 0.10f), TextAnchor.MiddleCenter, FontStyle.Bold);
        _btn.normal.background = _btnTex; _btn.hover.background = _btnTex; _btn.active.background = _btnTex;
        _btn2 = St2(F(18), new Color(0.85f, 0.93f, 1f, 0.96f), TextAnchor.MiddleCenter, FontStyle.Bold);
        _btn2.normal.background = _btn2Tex; _btn2.hover.background = _btn2Tex; _btn2.active.background = _btn2Tex;
        _cardName = St2(F(20), Color.white, TextAnchor.MiddleLeft, FontStyle.Bold);
        _cardPerk = St2(F(13), new Color(0.82f, 0.91f, 1f, 0.72f), TextAnchor.MiddleLeft, FontStyle.Normal);
        _label = St2(F(11), new Color(0.81f, 0.91f, 1f, 0.55f), TextAnchor.UpperLeft, FontStyle.Bold);
        _num = St2(F(36), C_SPIRIT, TextAnchor.UpperLeft, FontStyle.Bold);
        _small = St2(F(13), C_LEAF, TextAnchor.UpperLeft, FontStyle.Normal);

        _mute = St2(F(18), new Color(0.81f, 0.91f, 1f, 0.9f), TextAnchor.MiddleCenter, FontStyle.Bold);
        _mute.normal.background = _panelTex; _mute.hover.background = _panelTex; _mute.active.background = _panelTex;
        _comboMult = St2(F(48), C_CYAN, TextAnchor.LowerLeft, FontStyle.Bold);
        _comboStreak = St2(F(12), new Color(0.81f, 0.91f, 1f, 0.7f), TextAnchor.UpperLeft, FontStyle.Normal);
        _pop = St2(F(22), Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
        _bigNum = St2(F(40), C_SPIRIT, TextAnchor.MiddleCenter, FontStyle.Bold);
        _goldNum = St2(F(40), C_GOLD, TextAnchor.MiddleCenter, FontStyle.Bold);
        _keys = St2(F(13), new Color(0.81f, 0.91f, 1f, 0.7f), TextAnchor.MiddleCenter, FontStyle.Normal);
    }
    GUIStyle St2(int size, Color col, TextAnchor anchor, FontStyle fs)
    {
        var s = new GUIStyle(); s.fontSize = size; s.normal.textColor = col;
        s.alignment = anchor; s.fontStyle = fs; s.wordWrap = true; return s;
    }
    Texture2D SolidTex(Color c)
    {
        var t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); return t;
    }

    Rect SafeGui()
    {
        Rect s = Screen.safeArea;
        return new Rect(s.x, Screen.height - s.yMax, s.width, s.height);
    }
    Rect MuteRect()
    {
        Rect sg = SafeGui();
        float sz = 42 * _uiScale;
        return new Rect(sg.xMax - sz - 10, sg.y + 10, sz, sz);
    }
    bool OverMute(Vector2 screenPos)
    {
        if (_panelTex == null) return false;          // GUI not laid out yet
        Vector2 g = new Vector2(screenPos.x, Screen.height - screenPos.y);
        return MuteRect().Contains(g);
    }
    Rect PauseRect()
    {
        Rect m = MuteRect();
        float sz = m.height;
        return new Rect(m.x - sz - 8f * _uiScale, m.y, sz, sz);   // sits just left of the mute button
    }

    // UI rects captured during OnGUI so taps can be hit-tested in HandleUIPress.
    Rect _resumeRect, _restartRect, _homeRect, _charBtnRect, _backRect;
    Rect[] _cardRects;

    // Single tap/click handler for all UI. Mirrors how Start/Restart taps already work:
    // IMGUI buttons are unreliable for touch under the new Input System, so the pause
    // button, pause panel and character cards are drawn as visuals and activated here
    // (this path fires for both mouse and touch). Returns true if the press was
    // consumed by UI, so the caller must not turn it into a jump / game-start.
    bool HandleUIPress(Vector2 screenPos)
    {
        if (_panelTex == null) return false;          // GUI not laid out yet
        Vector2 g = new Vector2(screenPos.x, Screen.height - screenPos.y);

        if (paused)
        {
            if (_resumeRect.Contains(g)) TogglePause();
            else if (_restartRect.Contains(g)) RestartFromPause();
            else if (_homeRect.Contains(g)) GoHome();
            return true;                              // while paused, swallow every press
        }
        if (state == St.CharSelect)
        {
            if (_cardRects != null)
                for (int i = 0; i < _cardRects.Length; i++)
                    if (_cardRects[i].Contains(g)) { SelectChar(i); return true; }
            if (_backRect.Contains(g)) { state = St.Start; return true; }
            return true;                              // swallow other presses on this screen
        }
        if (MuteRect().Contains(g)) return true;      // mute has its own button; just block the jump
        if (state == St.Playing && PauseRect().Contains(g)) { TogglePause(); return true; }
        if (state == St.Start && _charBtnRect.Contains(g)) { state = St.CharSelect; return true; }
        return false;
    }

    void OnGUI()
    {
        EnsureGUI();
        Rect sg = SafeGui();
        float pad = 14 * _uiScale;

        // mute button (always)
        GUI.color = Color.white;
        if (GUI.Button(MuteRect(), muted ? "off" : "?", _mute)) { ToggleMute(); }

        if (state == St.Playing)
        {
            // score
            GUI.Label(new Rect(sg.x + pad, sg.y + pad, 300 * _uiScale, 20 * _uiScale), "LIGHT", _label);
            GUI.Label(new Rect(sg.x + pad, sg.y + pad + 16 * _uiScale, 300 * _uiScale, 50 * _uiScale), Mathf.FloorToInt(score).ToString(), _num);
            GUI.Label(new Rect(sg.x + pad, sg.y + pad + 56 * _uiScale, 300 * _uiScale, 22 * _uiScale),
                      "best " + Mathf.FloorToInt(Mathf.Max(best, score)), _small);

            // pause button (top-right, just left of mute) — hidden while the panel is up
            if (!paused)
            {
                Rect pbr = PauseRect();
                GUI.color = Color.white; GUI.DrawTexture(pbr, _panelTex);
                float bw2 = pbr.width * 0.14f, bh2 = pbr.height * 0.42f, gp2 = pbr.width * 0.11f;
                float by2 = pbr.y + (pbr.height - bh2) * 0.5f, cxp = pbr.x + pbr.width * 0.5f;
                GUI.color = new Color(0.82f, 0.91f, 1f, 0.92f);
                GUI.DrawTexture(new Rect(cxp - gp2 - bw2, by2, bw2, bh2), _barTex);
                GUI.DrawTexture(new Rect(cxp + gp2, by2, bw2, bh2), _barTex);
                GUI.color = Color.white;
            }

            // hearts (just left of the pause button)
            float hs = 26 * _uiScale;
            float heartsRight = PauseRect().x;
            for (int i = 0; i < maxHealth; i++)
            {
                GUI.color = i < health ? C_PINK : new Color(C_PINK.r, C_PINK.g, C_PINK.b, 0.18f);
                GUI.DrawTexture(new Rect(heartsRight - (maxHealth - i) * (hs + 6 * _uiScale) - 8 * _uiScale, sg.y + 18 * _uiScale, hs, hs), spHeart.texture);
            }
            GUI.color = Color.white;

            // combo
            if (combo >= 2 || mult > 1)
            {
                Color cc = Aura(mult);
                _comboMult.normal.textColor = cc;
                GUI.Label(new Rect(sg.x + pad, sg.yMax - 120 * _uiScale, 260 * _uiScale, 60 * _uiScale), "x" + mult, _comboMult);
                GUI.Label(new Rect(sg.x + pad, sg.yMax - 62 * _uiScale, 260 * _uiScale, 22 * _uiScale),
                          combo + (combo == 1 ? " ORB" : " ORBS"), _comboStreak);
                // combo bar
                float bw = 130 * _uiScale, bh = 6 * _uiScale, by = sg.yMax - 40 * _uiScale;
                GUI.color = new Color(1, 1, 1, 0.12f); GUI.DrawTexture(new Rect(sg.x + pad, by, bw, bh), _barTex);
                GUI.color = cc; GUI.DrawTexture(new Rect(sg.x + pad, by, bw * Mathf.Clamp01(comboTimer / comboTimeCur), bh), _barTex);
                GUI.color = Color.white;
            }
        }

        // floating pops
        foreach (var p in pops)
        {
            Vector3 sp = cam.WorldToScreenPoint(p.world);
            if (sp.z < 0) continue;
            float a = Mathf.Clamp01(p.life / p.max);
            _pop.normal.textColor = new Color(p.col.r, p.col.g, p.col.b, a);
            Rect pr = new Rect(sp.x - 80, Screen.height - sp.y - 16, 160, 32);
            GUI.Label(pr, p.text, _pop);
        }

        if (state == St.Start) DrawStart();
        if (state == St.CharSelect) DrawCharSelect();
        if (state == St.GameOver) DrawOver();
        if (paused) DrawPause();
    }

    void DrawStart()
    {
        Rect p = PanelRect(540, 500);
        GUI.color = new Color(1, 1, 1, 1); GUI.DrawTexture(p, _panelTex);
        float cx = p.x, w = p.width; float y = p.y + 24 * _uiScale;
        GUI.Label(new Rect(cx, y, w, 30 * _uiScale), "G L O W - R U N N E R", _sub); y += 30 * _uiScale;
        GUI.Label(new Rect(cx, y, w, 90 * _uiScale), "WISPWOOD", _title); y += 90 * _uiScale;
        GUI.Label(new Rect(cx, y, w, 28 * _uiScale), "Run the shadow forest. Gather the light.", _sub); y += 38 * _uiScale;
        GUI.Label(new Rect(cx + 30 * _uiScale, y, w - 60 * _uiScale, 80 * _uiScale),
                  "Jump the shadow-beasts, slide beneath the floaters, and chain light-orbs to grow your combo aura.", _body); y += 84 * _uiScale;
        GUI.Label(new Rect(cx, y, w, 24 * _uiScale), "tap / Space  jump      swipe ? / Down  slide", _keys); y += 34 * _uiScale;

        // character chooser — opens the Character Select screen (handled in HandleUIPress)
        string cn = chars[Mathf.Clamp(selectedChar, 0, chars.Length - 1)].name;
        _charBtnRect = new Rect(cx + w * 0.5f - 150 * _uiScale, y, 300 * _uiScale, 44 * _uiScale);
        GUI.color = Color.white; GUI.Label(_charBtnRect, "Character:  " + cn + "    > change", _btn2);
        y += 44 * _uiScale + 14 * _uiScale;

        if (GUI.Button(new Rect(cx + w * 0.5f - 110 * _uiScale, y, 220 * _uiScale, 54 * _uiScale), "Enter the wood", _btn))
            StartGame();
    }

    void DrawOver()
    {
        Rect p = PanelRect(520, 420);
        GUI.color = Color.white; GUI.DrawTexture(p, _panelTex);
        float cx = p.x, w = p.width; float y = p.y + 28 * _uiScale;
        GUI.Label(new Rect(cx, y, w, 26 * _uiScale), "the light went out", _sub); y += 30 * _uiScale;
        GUI.Label(new Rect(cx, y, w, 50 * _uiScale), "The shadows caught you", _h2); y += 60 * _uiScale;
        GUI.Label(new Rect(cx, y, w, 24 * _uiScale), "but the wood remembers your glow.", _sub); y += 46 * _uiScale;

        float colW = w / 3f;
        GUI.Label(new Rect(cx, y, colW, 22 * _uiScale), "LIGHT", _sub);
        GUI.Label(new Rect(cx + colW, y, colW, 22 * _uiScale), "BEST COMBO", _sub);
        GUI.Label(new Rect(cx + 2 * colW, y, colW, 22 * _uiScale), "RECORD", _sub);
        y += 26 * _uiScale;
        GUI.Label(new Rect(cx, y, colW, 50 * _uiScale), Mathf.FloorToInt(score).ToString(), _bigNum);
        GUI.Label(new Rect(cx + colW, y, colW, 50 * _uiScale), "x" + MultFor(maxCombo), _bigNum);
        GUI.Label(new Rect(cx + 2 * colW, y, colW, 50 * _uiScale), Mathf.FloorToInt(best).ToString(), _goldNum);
        y += 70 * _uiScale;

        if (GUI.Button(new Rect(cx + w * 0.5f - 100 * _uiScale, y, 200 * _uiScale, 54 * _uiScale), "Run again", _btn))
            StartGame();
    }

    void DrawCharSelect()
    {
        Rect p = PanelRect(560, 560);
        GUI.color = Color.white; GUI.DrawTexture(p, _panelTex);
        float cx = p.x, w = p.width; float y = p.y + 24 * _uiScale;
        GUI.Label(new Rect(cx, y, w, 28 * _uiScale), "choose your wisp", _sub); y += 30 * _uiScale;
        GUI.Label(new Rect(cx, y, w, 46 * _uiScale), "Characters", _h2); y += 58 * _uiScale;

        if (_cardRects == null || _cardRects.Length != chars.Length) _cardRects = new Rect[chars.Length];
        float mx = 24 * _uiScale, cardH = 70 * _uiScale, gap = 12 * _uiScale, cardW = w - mx * 2f;
        for (int i = 0; i < chars.Length; i++)
        {
            Rect r = new Rect(cx + mx, y, cardW, cardH);
            _cardRects[i] = r;
            bool sel = (i == selectedChar);
            Char c = chars[i];

            GUI.color = Color.white;
            GUI.DrawTexture(r, sel ? _btnTex : _btn2Tex);

            // colour dot (the character's ear colour)
            float dot = cardH * 0.42f;
            Rect dotR = new Rect(r.x + 14 * _uiScale, r.y + (cardH - dot) * 0.5f, dot, dot);
            GUI.color = c.ear; GUI.DrawTexture(dotR, spDisc.texture);
            GUI.color = Color.white;

            _cardName.normal.textColor = sel ? new Color(0.02f, 0.07f, 0.10f) : new Color(0.90f, 0.95f, 1f, 0.97f);
            _cardPerk.normal.textColor = sel ? new Color(0.02f, 0.07f, 0.10f, 0.85f) : new Color(0.80f, 0.90f, 1f, 0.72f);
            float tx = dotR.xMax + 14 * _uiScale, tw = r.xMax - tx - 12 * _uiScale;
            GUI.Label(new Rect(tx, r.y + 9 * _uiScale, tw, 26 * _uiScale), c.name + (sel ? "   (selected)" : ""), _cardName);
            GUI.Label(new Rect(tx, r.y + 35 * _uiScale, tw, 28 * _uiScale), c.perk, _cardPerk);

            y += cardH + gap;
        }

        _backRect = new Rect(cx + w * 0.5f - 110 * _uiScale, y + 4 * _uiScale, 220 * _uiScale, 50 * _uiScale);
        GUI.color = Color.white; GUI.Label(_backRect, "Back", _btn);
    }

    void DrawPause()
    {
        // dim the whole screen behind the panel
        GUI.color = new Color(0.02f, 0.02f, 0.06f, 0.72f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _barTex);
        GUI.color = Color.white;

        Rect p = PanelRect(440, 372);
        GUI.DrawTexture(p, _panelTex);
        float cx = p.x, w = p.width; float y = p.y + 30 * _uiScale;
        GUI.Label(new Rect(cx, y, w, 28 * _uiScale), "paused", _sub); y += 30 * _uiScale;
        GUI.Label(new Rect(cx, y, w, 52 * _uiScale), "Take a breath", _h2); y += 66 * _uiScale;

        float bw = Mathf.Min(260 * _uiScale, w - 60 * _uiScale);
        float bx = cx + (w - bw) * 0.5f, bh = 52 * _uiScale, gap = 14 * _uiScale;
        _resumeRect = new Rect(bx, y, bw, bh); y += bh + gap;
        _restartRect = new Rect(bx, y, bw, bh); y += bh + gap;
        _homeRect = new Rect(bx, y, bw, bh);

        DrawPanelBtn(_resumeRect, "Resume", true);
        DrawPanelBtn(_restartRect, "Restart run", false);
        DrawPanelBtn(_homeRect, "Main menu", false);
    }

    void DrawPanelBtn(Rect r, string label, bool primary)
    {
        GUI.color = Color.white;
        GUI.Label(r, label, primary ? _btn : _btn2);
    }

    Rect PanelRect(float wRef, float hRef)
    {
        float w = Mathf.Min(wRef * _uiScale, Screen.width * 0.92f);
        float h = hRef * _uiScale;
        return new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
    }
}
