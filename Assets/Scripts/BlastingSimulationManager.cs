// =====================================================================
//  BlastingSimulationManager.cs - Scenario 2: Controlled Blasting & Misfire Protocol
//
//  Drop-in replacement. Put it on an empty GameObject in the BlastingMode scene
//  (Main Camera optional). Everything is built at runtime: no assets needed.
//
//  Drill flow (tap objects, drag to look):
//   1. Circuit continuity  - tap the galvanometer, answer the fault question
//   2. Siren & clearance   - tap the 3 sentry posts, then the siren
//   3. Detonation          - insert the firing key, confirm pre-fire checks, fire
//   4. Misfire protocol    - short-circuit the lead-in wires, wait out the
//                            cooling period, inspect the face, answer
//   5. Debrief certificate - 8-point score, PASS/FAIL, save and return
//
//  Notes:
//   - Training simulation only. Wording is DGMS-aligned practice, not legal text.
//   - The cooling period is accelerated (cooldownSeconds) for the drill.
//   - BackendSync is called by reflection, so the scene compiles with or
//     without that class. Result is also stored in PlayerPrefs.
// =====================================================================
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

public class BlastingSimulationManager : MonoBehaviour
{
    [Header("Session")]
    [SerializeField] private string traineeName = "B. GULAVANI";
    [SerializeField] private string traineeId = "EMP-1042";
    [SerializeField] private string mainMenuSceneName = "MainMenu";

    [Header("Blasting Parameters")]
    [SerializeField] private float circuitResistance = 4.2f;   // ohms shown by the galvanometer
    [SerializeField] private float okMinOhm = 2f;
    [SerializeField] private float okMaxOhm = 8f;
    [SerializeField] private float cooldownSeconds = 20f;      // real rule is far longer; accelerated for the drill
    [SerializeField, Range(0.5f, 1f)] private float passPercent = 0.75f;

    [Header("Controls")]
    [SerializeField] private float lookSensitivity = 0.12f;
    [SerializeField] private bool forcePortrait = true;

    // ---------------- types ----------------
    enum Phase { CircuitCheck, SirenClearance, Detonation, Misfire, Debrief }
    class Hot { public string hint; public System.Action tap; public Material mat; public bool guide, lit; public Color glow = new Color(1f, 0.72f, 0.1f); }
    class Entry { public string label; public int pts; public bool tried; }
    class Quiz { public string tag, text, why; public string[] opts; public int ok; }

    const int L_CIRCUIT = 0, L_FAULT = 1, L_CLEAR = 2, L_RADIUS = 3, L_PREFIRE = 4, L_SHORT = 5, L_MISFIRE = 6, L_DISC = 7;

    // ---------------- palette ----------------
    static readonly Color ColPanel = new Color(0.07f, 0.10f, 0.15f, 0.93f);
    static readonly Color ColPanel2 = new Color(0.11f, 0.16f, 0.23f, 1f);
    static readonly Color ColYellow = new Color(0.99f, 0.78f, 0.13f, 1f);
    static readonly Color ColBlue = new Color(0.30f, 0.72f, 1f, 1f);
    static readonly Color ColGreen = new Color(0.25f, 0.82f, 0.45f, 1f);
    static readonly Color ColRed = new Color(0.95f, 0.35f, 0.30f, 1f);
    static readonly Color ColText = new Color(0.90f, 0.93f, 0.96f, 1f);
    static readonly Color ColMuted = new Color(0.62f, 0.69f, 0.77f, 1f);

    // ---------------- state ----------------
    Phase phase = Phase.CircuitCheck;
    Camera cam;
    Transform worldRoot;
    Vector3 camGoal = new Vector3(0f, 1.65f, 3.5f);
    float baseYaw, baseYawGoal, lookYaw, lookPitch, shake, elapsed, lastBack = -10f, cooldownLeft;
    bool busy, cardOpen, certOpen, circuitOk, sirenDone, keyIn, fired, shorted, cooled, preFireOk, down, dragged, downBlocked, cooling;
    int postsCleared, violations;
    Vector2 downPos;

    readonly Dictionary<Collider, Hot> hots = new Dictionary<Collider, Hot>();
    readonly List<Hot> hotList = new List<Hot>();
    readonly List<Light> lamps = new List<Light>();
    readonly Entry[] ledger =
    {
        new Entry { label = "Circuit continuity tested" },
        new Entry { label = "Fault response decision" },
        new Entry { label = "Danger zone cleared (3 sentry posts)" },
        new Entry { label = "Danger radius and warning siren" },
        new Entry { label = "Pre-firing safety confirmation" },
        new Entry { label = "Wires short-circuited after misfire" },
        new Entry { label = "Misfire approach decision" },
        new Entry { label = "No procedure violations" }
    };

    // scene objects
    Hot hGalv, hSiren, hKey, hHandle, hWires, hFace;
    readonly Hot[] hPosts = new Hot[3];
    readonly Material[] postMats = new Material[3];
    Transform needle, keyObj, handleObj, wiresObj;
    GameObject misfireFx;
    Material mCoal, mFloor, mTimber, mSteel, mDark, mYellow, mLamp;
    Mesh cubeMesh, cylMesh, sphMesh;
    ParticleSystem dust;
    Texture2D softTex;

    // audio
    AudioSource sfx, loopSrc;
    AudioClip cClick, cOk, cBad, cSiren, cBoom, cTick, cBuzz;

    // ui
    Sprite sprRound;
    Texture2D stripeTex;
    RectTransform rootRT, safeRT, menuBtnRT, cardPanel, certPanel;
    CanvasGroup toastGroup, cooldownGroup;
    TextMeshProUGUI phaseT, timerT, scoreT, objT, boardT, hintT, toastT, bigT, cdText;
    Image flash, fade, cdFill;
    GameObject cardRoot, certRoot;
    TextMeshProUGUI cardTag, cardText, cardResult;
    Button[] optBtn = new Button[3];
    TextMeshProUGUI[] optTxt = new TextMeshProUGUI[3];
    Button contBtn;
    TextMeshProUGUI certStatus, certStats, certLedger, certSync;
    Coroutine toastCo;
    Rect lastSafe;
    int lastW, lastH;

    // =================================================================
    //  LIFECYCLE
    // =================================================================
    void Start()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Application.targetFrameRate = 60;
        Time.timeScale = 1f;
        if (forcePortrait) Screen.orientation = ScreenOrientation.Portrait;

        SetupCamera();
        EnsureEventSystem();
        cubeMesh = Prim(PrimitiveType.Cube);
        cylMesh = Prim(PrimitiveType.Cylinder);
        sphMesh = Prim(PrimitiveType.Sphere);
        sprRound = RoundSprite(64, 22, 22);
        stripeTex = StripeTex();
        softTex = SoftTex();
        BuildAudio();
        BuildWorld();
        BuildHud();
        BuildCard();
        BuildCert();
        StartCoroutine(Intro());
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (phase != Phase.Debrief) elapsed += dt;
        if (Screen.safeArea != lastSafe || Screen.width != lastW || Screen.height != lastH) ApplySafeArea();

        HandleInput();
        UpdateCooldown(dt);
        UpdateGuides();
        UpdateHint();
        UpdateHud();

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) RequestExit();
    }

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        Transform t = cam.transform;
        t.position = Vector3.Lerp(t.position, camGoal, 1f - Mathf.Exp(-3f * dt));
        baseYaw = Mathf.LerpAngle(baseYaw, baseYawGoal, 1f - Mathf.Exp(-3f * dt));
        shake = Mathf.MoveTowards(shake, 0f, dt * 0.6f);
        float n = Time.time * 30f;
        Vector3 sh = new Vector3(Mathf.PerlinNoise(n, 0f) - 0.5f, Mathf.PerlinNoise(0f, n) - 0.5f, 0f) * shake * 2f;
        t.rotation = Quaternion.Euler(lookPitch + sh.y * 6f, baseYaw + lookYaw + sh.x * 6f, 0f);
    }

    void SetupCamera()
    {
        cam = Camera.main;
        if (cam == null)
        {
            GameObject g = new GameObject("Main Camera");
            g.tag = "MainCamera";
            cam = g.AddComponent<Camera>();
        }
        if (cam.GetComponent<AudioListener>() == null) cam.gameObject.AddComponent<AudioListener>();
        cam.transform.position = camGoal;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.02f, 0.025f, 0.03f);
        cam.nearClipPlane = 0.05f;
        cam.fieldOfView = 68f;

        // remove leftover directional lights from the default scene
        foreach (GameObject go in SceneManager.GetActiveScene().GetRootGameObjects())
            foreach (Light l in go.GetComponentsInChildren<Light>(true))
                if (l.type == LightType.Directional) l.enabled = false;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.13f, 0.12f, 0.11f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = new Color(0.04f, 0.035f, 0.03f);
        RenderSettings.fogDensity = 0.028f;
    }

    void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        GameObject es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
        es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
    }

    // =================================================================
    //  WORLD (all procedural)
    // =================================================================
    Mesh Prim(PrimitiveType t)
    {
        GameObject g = GameObject.CreatePrimitive(t);
        Mesh m = g.GetComponent<MeshFilter>().sharedMesh;
        Destroy(g);
        return m;
    }

    Material Mat(Color c, Texture2D tex = null, float smooth = 0.2f, float metal = 0f, float emit = 0f)
    {
        Shader s = Shader.Find("Universal Render Pipeline/Lit");
        if (s == null) s = Shader.Find("Standard");
        Material m = new Material(s);
        m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (tex != null) { m.SetTexture("_BaseMap", tex); m.mainTexture = tex; }
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
        m.EnableKeyword("_EMISSION");
        m.SetColor("_EmissionColor", c * emit);
        return m;
    }

    GameObject Obj(string n, Transform p, Mesh mesh, Vector3 pos, Vector3 euler, Vector3 scale, Material mat)
    {
        GameObject g = new GameObject(n);
        g.transform.SetParent(p, false);
        g.transform.localPosition = pos;
        g.transform.localRotation = Quaternion.Euler(euler);
        g.transform.localScale = scale;
        g.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer r = g.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return g;
    }

    GameObject Box(string n, Transform p, Vector3 pos, Vector3 scale, Material mat) { return Obj(n, p, cubeMesh, pos, Vector3.zero, scale, mat); }
    GameObject Cyl(string n, Transform p, Vector3 pos, Vector3 euler, float radius, float len, Material mat) { return Obj(n, p, cylMesh, pos, euler, new Vector3(radius * 2f, len * 0.5f, radius * 2f), mat); }

    GameObject Rod(string n, Transform p, Vector3 a, Vector3 b, float th, Material mat)
    {
        Vector3 d = b - a;
        if (d.sqrMagnitude < 1e-6f) return null;
        Vector3 up = Mathf.Abs(Vector3.Dot(d.normalized, Vector3.up)) > 0.99f ? Vector3.right : Vector3.up;
        GameObject g = Obj(n, p, cubeMesh, (a + b) * 0.5f, Vector3.zero, new Vector3(th, th, d.magnitude), mat);
        g.transform.localRotation = Quaternion.LookRotation(d, up);
        return g;
    }

    Hot AddHot(GameObject g, string hint, System.Action tap, Material m, float pad = 0f)
    {
        Collider c = g.GetComponent<Collider>();
        if (c == null) c = g.AddComponent<BoxCollider>();
        Hot h = new Hot { hint = hint, tap = tap, mat = m };
        hots[c] = h;
        hotList.Add(h);
        if (pad > 0f)
        {
            // oversized invisible collider so small objects are easy to tap
            GameObject pg = new GameObject("TapPad");
            pg.transform.SetParent(worldRoot, false);
            pg.transform.position = g.transform.position;
            BoxCollider bc = pg.AddComponent<BoxCollider>();
            bc.size = Vector3.one * pad;
            hots[bc] = h;
        }
        return h;
    }

    void BuildWorld()
    {
        worldRoot = new GameObject("BlastingWorld").transform;
        Transform r = worldRoot;
        Texture2D coal = NoiseTex(128, 0.04f, 0.2f, 7);
        mCoal = Mat(Color.white, coal, 0.2f);
        mFloor = Mat(new Color(0.6f, 0.6f, 0.62f), coal, 0.45f);
        mTimber = Mat(new Color(0.36f, 0.22f, 0.1f), null, 0.1f);
        mSteel = Mat(new Color(0.5f, 0.52f, 0.55f), null, 0.55f, 0.8f);
        mDark = Mat(new Color(0.05f, 0.05f, 0.06f), null, 0.3f);
        mYellow = Mat(new Color(0.9f, 0.68f, 0.05f), null, 0.3f);
        mLamp = Mat(new Color(1f, 0.85f, 0.55f), null, 0.2f, 0f, 2.2f);

        // tunnel shell, z from -14 to 16
        Box("Floor", r, new Vector3(0f, -0.25f, 1f), new Vector3(5.6f, 0.5f, 34f), mFloor).AddComponent<BoxCollider>();
        Box("WallL", r, new Vector3(-2.8f, 1.8f, 1f), new Vector3(1.2f, 3.6f, 34f), mCoal);
        Box("WallR", r, new Vector3(2.8f, 1.8f, 1f), new Vector3(1.2f, 3.6f, 34f), mCoal);
        Box("Roof", r, new Vector3(0f, 3.4f, 1f), new Vector3(6.4f, 0.8f, 34f), mCoal);
        Box("BackWall", r, new Vector3(0f, 1.8f, -14.4f), new Vector3(6.4f, 3.6f, 1f), mCoal);
        Box("Face", r, new Vector3(0f, 1.6f, 15.9f), new Vector3(4.6f, 3.2f, 1f), mCoal);

        // irregular rock lumps on the ribs
        Random.InitState(42);
        for (int i = 0; i < 34; i++)
        {
            float side = i % 2 == 0 ? -1f : 1f;
            float s = Random.Range(0.45f, 1f);
            Obj("Lump", r, sphMesh, new Vector3(side * Random.Range(2.0f, 2.2f), Random.Range(0.3f, 3f), Random.Range(-13f, 15f)),
                new Vector3(Random.Range(0f, 90f), Random.Range(0f, 90f), 0f), new Vector3(s, s * Random.Range(0.6f, 1.2f), s), mCoal);
        }

        Random.InitState(System.Environment.TickCount);

        // timber sets, rails, ties, lamps
        for (float z = -12f; z <= 12f; z += 4f)
        {
            Box("Leg", r, new Vector3(-1.95f, 1.4f, z), new Vector3(0.25f, 2.8f, 0.25f), mTimber);
            Box("Leg", r, new Vector3(1.95f, 1.4f, z), new Vector3(0.25f, 2.8f, 0.25f), mTimber);
            Box("Cap", r, new Vector3(0f, 2.85f, z), new Vector3(4.4f, 0.25f, 0.3f), mTimber);
        }
        Box("RailL", r, new Vector3(-0.55f, 0.05f, 1f), new Vector3(0.08f, 0.1f, 30f), mSteel);
        Box("RailR", r, new Vector3(0.55f, 0.05f, 1f), new Vector3(0.08f, 0.1f, 30f), mSteel);
        for (float z = -13f; z <= 15f; z += 1f) Box("Tie", r, new Vector3(0f, 0.03f, z), new Vector3(1.5f, 0.06f, 0.18f), mTimber);

        float[] lz = { -10f, -2f, 6f, 13f };
        for (int i = 0; i < lz.Length; i++)
        {
            float x = i % 2 == 0 ? -1.2f : 1.2f;
            Box("Lamp", r, new Vector3(x, 2.6f, lz[i]), new Vector3(0.5f, 0.1f, 0.2f), mLamp);
            GameObject lg = new GameObject("LampLight");
            lg.transform.SetParent(r, false);
            lg.transform.localPosition = new Vector3(x, 2.35f, lz[i]);
            Light l = lg.AddComponent<Light>();
            l.type = LightType.Point; l.range = 12f; l.intensity = 3f; l.color = new Color(1f, 0.82f, 0.55f); l.shadows = LightShadows.None;
            lamps.Add(l);
        }

        BuildFace(r);
        BuildGalvanometer(r);
        BuildPostsAndSiren(r);
        BuildShelter(r);

        // dust for the blast
        GameObject dg = new GameObject("BlastDust");
        dg.transform.SetParent(r, false);
        dg.transform.position = new Vector3(0f, 1.6f, 14.5f);
        dust = dg.AddComponent<ParticleSystem>();
        dust.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystem.MainModule m = dust.main;
        m.playOnAwake = false; m.loop = false; m.duration = 1f;
        m.startLifetime = 7f; m.startSpeed = 2.4f; m.startSize = new ParticleSystem.MinMaxCurve(1.5f, 3.2f);
        m.startColor = new Color(0.55f, 0.5f, 0.44f, 0.5f); m.maxParticles = 300;
        ParticleSystem.EmissionModule em = dust.emission; em.enabled = false;
        ParticleSystem.ShapeModule sh = dust.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(3.5f, 2.5f, 0.5f);
        ParticleSystem.ColorOverLifetimeModule col = dust.colorOverLifetime; col.enabled = true;
        Gradient gr = new Gradient();
        gr.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                   new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.8f, 0.15f), new GradientAlphaKey(0f, 1f) });
        col.color = gr;
        ParticleSystemRenderer pr = dg.GetComponent<ParticleSystemRenderer>();
        Material pm = new Material(Shader.Find("Sprites/Default"));
        pm.mainTexture = softTex;
        pr.sharedMaterial = pm;
    }

    void BuildFace(Transform r)
    {
        Material cable = Mat(new Color(0.9f, 0.45f, 0.05f), null, 0.3f);
        Vector3 trunk = new Vector3(0f, 0.07f, 14.6f);
        for (int ix = 0; ix < 3; ix++)
            for (int iy = 0; iy < 3; iy++)
            {
                Vector3 p = new Vector3((ix - 1) * 1.2f, 0.8f + iy * 0.8f, 15.38f);
                Cyl("Hole", r, p, new Vector3(90f, 0f, 0f), 0.08f, 0.1f, mDark);
                Rod("Leg", r, p + new Vector3(0f, 0f, -0.04f), Vector3.Lerp(p, trunk, 0.55f) + new Vector3(0f, 0f, -0.3f), 0.015f, cable);
            }
        Rod("Trunk", r, trunk, new Vector3(0f, 0.07f, -10.8f), 0.05f, cable);

        // misfire marker on hole #3 (right, middle row) - hidden until the blast
        Vector3 hp = new Vector3(1.2f, 1.6f, 15.3f);
        misfireFx = new GameObject("MisfireMarker");
        misfireFx.transform.SetParent(r, false);
        misfireFx.transform.localPosition = hp;
        Material mm = Mat(new Color(1f, 0.1f, 0.05f), null, 0.3f, 0f, 0f);
        Obj("Glow", misfireFx.transform, sphMesh, new Vector3(0f, 0f, -0.1f), Vector3.zero, Vector3.one * 0.22f, mm);
        Rod("Wire", misfireFx.transform, Vector3.zero, new Vector3(0.1f, -0.5f, -0.8f), 0.03f, cable);
        BoxCollider bc = misfireFx.AddComponent<BoxCollider>();
        bc.size = new Vector3(0.9f, 0.9f, 0.6f);
        hFace = AddHot(misfireFx, "TAP: INSPECT THE MISFIRED HOLE", OnFaceTapped, mm);
        misfireFx.SetActive(false);
    }

    void BuildGalvanometer(Transform r)
    {
        Vector3 p = new Vector3(0.9f, 0.95f, 8f);
        Cyl("GalvStand", r, new Vector3(p.x, 0.45f, p.z), Vector3.zero, 0.05f, 0.9f, mSteel);
        Material body = Mat(new Color(0.15f, 0.35f, 0.18f), null, 0.4f, 0.3f);
        GameObject b = Box("Galvanometer", r, p, new Vector3(0.62f, 0.46f, 0.3f), body);
        Material dial = Mat(new Color(0.9f, 0.92f, 0.85f), null, 0.2f, 0f, 0.7f);
        Box("Dial", r, p + new Vector3(0f, 0.03f, -0.155f), new Vector3(0.5f, 0.3f, 0.01f), dial);
        GameObject pivot = new GameObject("NeedlePivot");
        pivot.transform.SetParent(r, false);
        pivot.transform.localPosition = p + new Vector3(0f, -0.1f, -0.17f);
        Box("Needle", pivot.transform, new Vector3(0f, 0.12f, 0f), new Vector3(0.012f, 0.24f, 0.01f), mDark);
        needle = pivot.transform;
        needle.localRotation = Quaternion.Euler(0f, 0f, 50f);
        hGalv = AddHot(b, "TAP: TEST CIRCUIT CONTINUITY", OnGalvTapped, body);
    }

    void BuildPostsAndSiren(Transform r)
    {
        Vector3[] pp = { new Vector3(-1.7f, 0f, -1f), new Vector3(1.7f, 0f, 1.5f), new Vector3(-1.7f, 0f, 4f) };
        string[] nm = { "A", "B", "C" };
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            Cyl("Pole" + nm[i], r, pp[i] + new Vector3(0f, 0.6f, 0f), Vector3.zero, 0.04f, 1.2f, mSteel);
            postMats[i] = Mat(ColRed, null, 0.3f, 0f, 0.35f);
            GameObject flag = Box("Flag" + nm[i], r, pp[i] + new Vector3(0.28f, 1.05f, 0f), new Vector3(0.55f, 0.36f, 0.04f), postMats[i]);
            hPosts[i] = AddHot(flag, "TAP: CONFIRM SENTRY POST " + nm[i] + " CLEAR", () => OnPostTapped(idx), postMats[i], 0.8f);
        }
        Material horn = Mat(new Color(0.85f, 0.12f, 0.1f), null, 0.4f, 0.2f);
        Box("SirenBracket", r, new Vector3(2.05f, 2.2f, 0.5f), new Vector3(0.2f, 0.2f, 0.2f), mSteel);
        GameObject h = Obj("SirenHorn", r, cylMesh, new Vector3(1.75f, 2.2f, 0.5f), new Vector3(0f, 0f, 90f), new Vector3(0.38f, 0.25f, 0.38f), horn);
        hSiren = AddHot(h, "TAP: SOUND THE WARNING SIREN", OnSirenTapped, horn, 0.8f);
    }

    void BuildShelter(Transform r)
    {
        Box("ShelterRoof", r, new Vector3(0f, 2.3f, -11.8f), new Vector3(4.2f, 0.15f, 4.2f), mSteel);
        foreach (float x in new[] { -2f, 2f })
            foreach (float z in new[] { -9.8f, -13.4f })
                Box("ShelterPost", r, new Vector3(x, 1.15f, z), new Vector3(0.14f, 2.3f, 0.14f), mYellow);
        Box("Bench", r, new Vector3(0f, 0.4f, -11.4f), new Vector3(1.4f, 0.8f, 0.7f), mTimber);
        Material exp = Mat(new Color(0.18f, 0.2f, 0.22f), null, 0.4f, 0.5f);
        Box("Exploder", r, new Vector3(0f, 0.97f, -11.4f), new Vector3(0.75f, 0.34f, 0.46f), exp);
        Box("ShelterSign", r, new Vector3(0f, 2.0f, -9.75f), new Vector3(1.3f, 0.35f, 0.04f), Mat(ColGreen, null, 0.3f, 0f, 0.5f));

        Material keyM = Mat(new Color(0.95f, 0.75f, 0.1f), null, 0.5f, 0.6f);
        GameObject sock = Cyl("KeySocket", r, new Vector3(-0.27f, 1.17f, -11.15f), new Vector3(90f, 0f, 0f), 0.05f, 0.06f, mDark);
        keyObj = Cyl("FiringKey", r, new Vector3(-0.27f, 1.17f, -10.55f), new Vector3(90f, 0f, 0f), 0.02f, 0.14f, keyM).transform;
        hKey = AddHot(sock, "TAP: INSERT THE FIRING KEY", OnKeyTapped, keyM, 0.28f);

        Material hm = Mat(new Color(0.8f, 0.1f, 0.08f), null, 0.4f, 0f, 0f);
        GameObject hp = new GameObject("HandlePivot");
        hp.transform.SetParent(r, false);
        hp.transform.localPosition = new Vector3(0.27f, 1.16f, -11.15f);
        handleObj = hp.transform;
        GameObject lev = Cyl("Handle", hp.transform, new Vector3(0f, 0f, 0.18f), new Vector3(90f, 0f, 0f), 0.035f, 0.36f, hm);
        hHandle = AddHot(lev, "TAP: OPERATE THE FIRING HANDLE", OnHandleTapped, hm, 0.28f);

        Material wm = Mat(new Color(0.9f, 0.45f, 0.05f), null, 0.3f, 0f, 0f);
        GameObject term = Box("Terminals", r, new Vector3(0f, 1.02f, -11.15f), new Vector3(0.2f, 0.1f, 0.1f), wm);
        wiresObj = term.transform;
        hWires = AddHot(term, "TAP: DISCONNECT AND SHORT-CIRCUIT THE WIRES", OnWiresTapped, wm, 0.22f);
    }

    // =================================================================
    //  FLOW
    // =================================================================
    IEnumerator Intro()
    {
        fade.color = Color.black;
        yield return Fade(fade, 1f, 0f, 1.1f);
        fade.raycastTarget = false;
        SetPhase(Phase.CircuitCheck, "PHASE 1  |  CIRCUIT CONTINUITY", "Walk to the galvanometer and TAP it to test the blasting circuit.");
        Guide(hGalv);
        Toast("Drag to look around. Tap glowing objects to interact.", ColYellow, 4f);
    }

    void SetPhase(Phase p, string title, string objective)
    {
        phase = p;
        phaseT.text = title;
        objT.text = objective;
        UpdateBoard();
    }

    void Violation(string msg)
    {
        violations++;
        Sfx(cBad, 0.8f);
        Toast("PROCEDURE ERROR: " + msg, ColRed, 3.2f);
        UpdateBoard();
    }

    void Grade(int i, bool ok)
    {
        ledger[i].tried = true;
        ledger[i].pts = ok ? 1 : 0;
    }

    int Score() { int s = 0; foreach (Entry e in ledger) s += e.pts; return s; }

    // ---------- phase 1 ----------
    void OnGalvTapped()
    {
        if (busy || cardOpen) return;
        if (ledger[L_CIRCUIT].tried) { Toast("Circuit already tested. Continue the procedure.", ColMuted, 2f); return; }
        StartCoroutine(CircuitTest());
    }

    IEnumerator CircuitTest()
    {
        busy = true;
        Guide();
        Toast("Testing lead-in wires... needle deflecting.", ColBlue, 2.5f);
        Sfx(cBuzz, 0.6f);
        float target = Mathf.Lerp(50f, -50f, Mathf.Clamp01(circuitResistance / 12f));
        for (float t = 0f; t < 1.8f; t += Time.deltaTime)
        {
            float wob = Mathf.Sin(t * 22f) * 6f * (1f - t / 1.8f);
            needle.localRotation = Quaternion.Euler(0f, 0f, Mathf.Lerp(50f, target, t / 1.8f) + wob);
            yield return null;
        }
        needle.localRotation = Quaternion.Euler(0f, 0f, target);
        circuitOk = circuitResistance >= okMinOhm && circuitResistance <= okMaxOhm;
        Grade(L_CIRCUIT, circuitOk);
        Sfx(circuitOk ? cOk : cBad, 0.8f);
        Toast(circuitOk ? "CONTINUITY PASSED: " + circuitResistance.ToString("0.0") + " ohm (normal)."
                        : "CIRCUIT FAULT: " + circuitResistance.ToString("0.0") + " ohm is abnormal.",
              circuitOk ? ColGreen : ColRed, 3f);
        UpdateBoard();
        yield return new WaitForSeconds(2.2f);
        busy = false;

        Quiz q = circuitOk
            ? new Quiz
            {
                tag = "TACTICAL DECISION  |  CIRCUIT OK",
                text = "The galvanometer reads " + circuitResistance.ToString("0.0") + " ohm, inside the expected range. What is the correct next step?",
                opts = new[] { "Move everyone to safety and clear the danger zone before connecting the exploder", "Connect the exploder now and fire a test shot", "Skip clearance because the circuit is good" },
                ok = 0,
                why = "A good circuit does not make the face safe. Clear and guard the danger zone first, then connect and fire from the shelter."
            }
            : new Quiz
            {
                tag = "TACTICAL DECISION  |  CIRCUIT FAULT",
                text = "The reading of " + circuitResistance.ToString("0.0") + " ohm is outside the expected range (break or earth leak). What do you do?",
                opts = new[] { "Do not fire. Keep the exploder disconnected and trace the fault", "Fire anyway: most holes will still go off", "Add more explosive to compensate" },
                ok = 0,
                why = "A faulty circuit can cause misfires. Never fire it; locate and repair the fault with the exploder disconnected."
            };
        Ask(q, ok =>
        {
            Grade(L_FAULT, ok);
            UpdateBoard();
            StartPhase2();
        });
    }

    // ---------- phase 2 ----------
    void StartPhase2()
    {
        camGoal = new Vector3(0f, 1.65f, -3f);
        baseYawGoal = 0f; lookYaw = 0f; lookPitch = 0f;
        SetPhase(Phase.SirenClearance, "PHASE 2  |  SIREN & CLEARANCE", "Confirm all 3 sentry posts are clear, then sound the siren.");
        Guide(hPosts);
    }

    void OnPostTapped(int i)
    {
        if (cardOpen || phase != Phase.SirenClearance) { if (phase == Phase.CircuitCheck) Violation("Finish the circuit test first."); return; }
        if (postMats[i].GetColor("_BaseColor") == ColGreen) return;
        postMats[i].SetColor("_BaseColor", ColGreen);
        postMats[i].SetColor("_EmissionColor", ColGreen * 0.5f);
        hPosts[i].glow = ColGreen;
        postsCleared++;
        Sfx(cOk, 0.6f);
        Toast("Sentry post " + (char)('A' + i) + ": area clear (" + postsCleared + "/3)", ColGreen, 2f);
        if (postsCleared == 3)
        {
            Grade(L_CLEAR, true);
            objT.text = "All posts clear. TAP the siren to give the warning signal.";
            Guide(hSiren);
        }
        UpdateBoard();
    }

    void OnSirenTapped()
    {
        if (busy || cardOpen) return;
        if (phase != Phase.SirenClearance) { if (!sirenDone) Violation("Clear the circuit check first."); return; }
        if (postsCleared < 3) { Violation("Sound the siren only after all sentry posts report clear."); return; }
        StartCoroutine(SirenRoutine());
    }

    IEnumerator SirenRoutine()
    {
        busy = true;
        Guide();
        loopSrc.clip = cSiren; loopSrc.volume = 0.7f; loopSrc.Play();
        Toast("WARNING SIREN: Clear all persons beyond the danger radius!", ColYellow, 3.5f);
        for (float t = 0f; t < 3.5f; t += Time.deltaTime)
        {
            flash.color = new Color(1f, 0.15f, 0.05f, 0.12f * (0.5f + 0.5f * Mathf.Sin(t * 8f)));
            yield return null;
        }
        flash.color = Color.clear;
        loopSrc.Stop();
        sirenDone = true;
        busy = false;
        UpdateBoard();
        Ask(new Quiz
        {
            tag = "TACTICAL DECISION  |  DANGER RADIUS",
            text = "The siren has sounded. In this drill the danger zone is cleared to 300 m. When may people re-enter?",
            opts = new[] { "Only after the blast, the all-clear and the post-blast inspection", "As soon as the smoke thins", "Once the siren stops" },
            ok = 0,
            why = "Nobody re-enters until the shotfirer gives the all-clear after inspecting the face and the roof."
        }, ok =>
        {
            Grade(L_RADIUS, ok);
            StartPhase3();
        });
    }

    // ---------- phase 3 ----------
    void StartPhase3()
    {
        camGoal = new Vector3(0f, 1.62f, -8.6f);
        baseYawGoal = 180f; lookYaw = 0f; lookPitch = 5f;
        SetPhase(Phase.Detonation, "PHASE 3  |  DETONATION", "Take shelter at the exploder. TAP the key socket to insert the firing key.");
        Guide(hKey);
    }

    void OnKeyTapped()
    {
        if (busy || cardOpen) return;
        if (phase != Phase.Detonation) { Violation("The firing key is only used from the shelter, after clearance."); return; }
        if (keyIn) return;
        StartCoroutine(KeyRoutine());
    }

    IEnumerator KeyRoutine()
    {
        busy = true;
        Guide();
        Vector3 a = keyObj.localPosition, b = new Vector3(a.x, a.y, -11.05f);
        for (float t = 0f; t < 0.5f; t += Time.deltaTime) { keyObj.localPosition = Vector3.Lerp(a, b, t / 0.5f); yield return null; }
        Sfx(cClick, 1f);
        keyIn = true;
        busy = false;
        UpdateBoard();
        Toast("Key inserted and turned to ON. Exploder ARMED.", ColYellow, 2.5f);
        yield return new WaitForSeconds(1.2f);
        Ask(new Quiz
        {
            tag = "TACTICAL DECISION  |  PRE-FIRING CHECK",
            text = "The exploder is armed. What must you confirm before operating the firing handle?",
            opts = new[] { "All persons are clear, sentries have reported and the warning was given", "Only that the key turns smoothly", "That the shift is nearly over" },
            ok = 0,
            why = "Firing is irreversible. Confirm people, sentries and warning first, then fire."
        }, ok =>
        {
            preFireOk = true;
            Grade(L_PREFIRE, ok);
            objT.text = "TAP the firing handle to detonate.";
            Guide(hHandle);
        });
    }

    void OnHandleTapped()
    {
        if (busy || cardOpen) return;
        if (phase != Phase.Detonation || !keyIn) { Violation("Insert the key and complete the checks before firing."); return; }
        if (!preFireOk || fired) return;
        StartCoroutine(Detonation());
    }

    IEnumerator Detonation()
    {
        busy = true; fired = true;
        Guide();
        UpdateBoard();
        for (int i = 3; i >= 1; i--)
        {
            bigT.text = i.ToString();
            Sfx(cTick, 1f);
            yield return new WaitForSeconds(0.9f);
        }
        bigT.text = "";
        for (float t = 0f; t < 0.25f; t += Time.deltaTime) { handleObj.localRotation = Quaternion.Euler(Mathf.Lerp(0f, 55f, t / 0.25f), 0f, 0f); yield return null; }

        Sfx(cBoom, 1f);
        shake = 0.5f;
        flash.color = new Color(1f, 0.95f, 0.8f, 0.9f);
        dust.Play();
        dust.Emit(160);
        StartCoroutine(Flicker());
        RenderSettings.fogDensity = 0.07f;
        for (float t = 0f; t < 1f; t += Time.deltaTime) { flash.color = new Color(1f, 0.95f, 0.8f, 0.9f * (1f - t)); yield return null; }
        flash.color = Color.clear;
        yield return new WaitForSeconds(2.5f);
        RenderSettings.fogDensity = 0.04f;

        Sfx(cBad, 1f);
        misfireFx.SetActive(true);
        Toast("BLAST COMPLETE. WARNING: Misfire detected on charge #3!", ColRed, 4f);
        yield return new WaitForSeconds(2.5f);
        busy = false;
        SetPhase(Phase.Misfire, "PHASE 4  |  MISFIRE PROTOCOL", "Disconnect the lead-in wires and short-circuit them at the exploder.");
        Guide(hWires);
    }

    IEnumerator Flicker()
    {
        for (int i = 0; i < 10; i++)
        {
            foreach (Light l in lamps) l.intensity = Random.Range(0.3f, 3f);
            yield return new WaitForSeconds(0.07f);
        }
        foreach (Light l in lamps) l.intensity = 3f;
    }

    // ---------- phase 4 ----------
    void OnWiresTapped()
    {
        if (busy || cardOpen) return;
        if (phase != Phase.Misfire) { Violation("Wires are only handled after a misfire is declared."); return; }
        if (shorted) return;
        shorted = true;
        Grade(L_SHORT, true);
        Guide();
        wiresObj.localScale = new Vector3(0.2f, 0.05f, 0.1f);
        Sfx(cOk, 0.8f);
        Toast("Wires disconnected and short-circuited. Key removed.", ColGreen, 3f);
        cooldownLeft = cooldownSeconds;
        cooling = true;
        cooldownGroup.alpha = 1f;
        objT.text = "Statutory waiting period in progress. Stay in the shelter. Do NOT approach the face.";
        UpdateBoard();
    }

    void UpdateCooldown(float dt)
    {
        if (!cooling) return;
        cooldownLeft -= dt;
        float f = Mathf.Clamp01(1f - cooldownLeft / cooldownSeconds);
        cdFill.fillAmount = f;
        int s = Mathf.CeilToInt(Mathf.Max(0f, cooldownLeft));
        cdText.text = "WAITING PERIOD  " + (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
        if (cooldownLeft > 0f) return;
        cooling = false; cooled = true;
        cooldownGroup.alpha = 0f;
        Sfx(cOk, 0.8f);
        Toast("Waiting period complete. You may now inspect the face.", ColGreen, 3f);
        camGoal = new Vector3(0f, 1.65f, 8.5f);
        baseYawGoal = 0f; lookYaw = 0f; lookPitch = 0f;
        objT.text = "Approach the face. TAP the misfired hole to inspect it.";
        Guide(hFace);
        UpdateBoard();
    }

    void OnFaceTapped()
    {
        if (busy || cardOpen) return;
        if (phase != Phase.Misfire || !shorted) { Violation("Short-circuit the wires before going near the face."); return; }
        if (!cooled) { Violation("Do not approach the face during the statutory waiting period."); return; }
        Guide();
        Ask(new Quiz
        {
            tag = "TACTICAL DECISION  |  MISFIRED HOLE",
            text = "Charge #3 has not fired. A colleague suggests drilling it out to save time. What is your response?",
            opts = new[] { "Refuse. Never drill into or pull out a misfired hole; follow the approved misfire procedure under supervision", "Drill carefully beside it at a slight angle", "Pull the leg wires and dig the stemming out by hand" },
            ok = 0,
            why = "A misfired charge is live. Never drill into it or pull its wires; deal with it only by the approved procedure."
        }, ok =>
        {
            Grade(L_MISFIRE, ok);
            StartCoroutine(Finish());
        });
    }

    IEnumerator Finish()
    {
        yield return new WaitForSeconds(0.4f);
        Grade(L_DISC, violations == 0);
        SetPhase(Phase.Debrief, "DEBRIEF  |  CERTIFICATE", "Drill complete.");
        ShowCert();
    }

    // =================================================================
    //  GUIDES / HINT / BOARD
    // =================================================================
    void Guide(params Hot[] hs)
    {
        foreach (Hot h in hotList) h.guide = false;
        foreach (Hot h in hs) if (h != null) h.guide = true;
    }

    void UpdateGuides()
    {
        float p = 0.5f + 0.5f * Mathf.Sin(Time.time * 5f);
        foreach (Hot h in hotList)
        {
            if (h.mat == null) continue;
            if (h.guide) { h.mat.SetColor("_EmissionColor", h.glow * (0.4f + 1.1f * p)); h.lit = true; }
            else if (h.lit) { h.mat.SetColor("_EmissionColor", h == hPosts[0] || h == hPosts[1] || h == hPosts[2] ? h.glow * 0.4f : Color.black); h.lit = false; }
        }
    }

    void UpdateHint()
    {
        string s = "";
        if (!cardOpen && !certOpen && Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, 40f) && hots.TryGetValue(hit.collider, out Hot h))
            s = h.hint;
        hintT.text = s;
    }

    void UpdateBoard()
    {
        string circ = !ledger[L_CIRCUIT].tried ? "<color=#9FB0C4>NOT TESTED" : (circuitOk ? "<color=#40D173>" + circuitResistance.ToString("0.0") + " ohm OK" : "<color=#F2594D>" + circuitResistance.ToString("0.0") + " ohm FAULT");
        string area = "<color=" + (postsCleared == 3 ? "#40D173" : "#FCC721") + ">posts " + postsCleared + "/3";
        string siren = sirenDone ? "<color=#40D173>SOUNDED" : "<color=#9FB0C4>OFF";
        string exp = fired ? "<color=#F2594D>FIRED" : (keyIn ? "<color=#FCC721>ARMED" : "<color=#9FB0C4>SAFE");
        string mis = !fired ? "<color=#9FB0C4>-" : (cooled ? "<color=#40D173>WAIT DONE" : (shorted ? "<color=#FCC721>COOLING" : "<color=#F2594D>HOLE #3"));
        string err = violations == 0 ? "<color=#40D173>0" : "<color=#F2594D>" + violations;
        boardT.text = "CIRCUIT<pos=42%>" + circ + "</color>\nAREA<pos=42%>" + area + "</color>\nSIREN<pos=42%>" + siren + "</color>\nEXPLODER<pos=42%>" + exp + "</color>\nMISFIRE<pos=42%>" + mis + "</color>\nERRORS<pos=42%>" + err + "</color>";
    }

    void UpdateHud()
    {
        int s = Mathf.FloorToInt(elapsed);
        timerT.text = (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
        scoreT.text = "SCORE " + Score() + " / " + ledger.Length;
    }

    // =================================================================
    //  INPUT
    // =================================================================
    void HandleInput()
    {
        Pointer p = Pointer.current;
        if (p == null) return;
        Vector2 pos = p.position.ReadValue();
        if (p.press.wasPressedThisFrame)
        {
            down = true; dragged = false; downPos = pos;
            downBlocked = cardOpen || certOpen || OverMenu(pos);
        }
        if (down && p.press.isPressed)
        {
            if ((pos - downPos).sqrMagnitude > 400f) dragged = true;
            if (dragged && !downBlocked)
            {
                Vector2 d = p.delta.ReadValue() * lookSensitivity * (1080f / Mathf.Max(1, Screen.width));
                lookYaw = Mathf.Clamp(lookYaw - d.x, -80f, 80f);
                lookPitch = Mathf.Clamp(lookPitch + d.y, -40f, 40f);
            }
        }
        if (down && p.press.wasReleasedThisFrame)
        {
            down = false;
            if (!dragged && !downBlocked) Tap(pos);
        }
    }

    bool OverMenu(Vector2 pos) { return menuBtnRT != null && RectTransformUtility.RectangleContainsScreenPoint(menuBtnRT, pos, null); }

    void Tap(Vector2 pos)
    {
        Ray ray = cam.ScreenPointToRay(pos);
        if (Physics.Raycast(ray, out RaycastHit hit, 40f) && hots.TryGetValue(hit.collider, out Hot h)) { Sfx(cClick, 0.5f); h.tap(); }
    }

    void RequestExit()
    {
        if (certOpen) return;
        if (Time.unscaledTime - lastBack < 2f) { LoadMenu(); return; }
        lastBack = Time.unscaledTime;
        Toast("Press BACK again to return to Dispatch", ColYellow, 2f);
    }

    void LoadMenu()
    {
        if (Application.CanStreamedLevelBeLoaded(mainMenuSceneName)) SceneManager.LoadScene(mainMenuSceneName);
        else Toast("Scene '" + mainMenuSceneName + "' is not in Build Profiles.", ColRed, 3f);
    }

    // =================================================================
    //  DECISION CARD
    // =================================================================
    System.Action<bool> cardDone;
    Quiz cardQuiz;
    int[] cardMap;
    bool cardOk;

    void Ask(Quiz q, System.Action<bool> done)
    {
        cardQuiz = q; cardDone = done; cardOpen = true;
        cardMap = new[] { 0, 1, 2 };
        for (int i = 2; i > 0; i--) { int j = Random.Range(0, i + 1); int t = cardMap[i]; cardMap[i] = cardMap[j]; cardMap[j] = t; }
        float w = Mathf.Min(980f, rootRT.rect.width - 48f);
        cardPanel.sizeDelta = new Vector2(w, 800f);
        cardTag.text = q.tag;
        cardText.text = q.text;
        cardResult.text = "";
        for (int i = 0; i < 3; i++)
        {
            optBtn[i].gameObject.SetActive(true);
            optTxt[i].text = (char)('A' + i) + ".  " + q.opts[cardMap[i]];
        }
        contBtn.gameObject.SetActive(false);
        cardRoot.SetActive(true);
    }

    void PickOption(int slot)
    {
        bool ok = cardMap[slot] == cardQuiz.ok;
        cardOk = ok;
        Sfx(ok ? cOk : cBad, 0.8f);
        foreach (Button b in optBtn) b.gameObject.SetActive(false);
        cardResult.text = (ok ? "<color=#40D173><b>CORRECT</b></color>\n" : "<color=#F2594D><b>INCORRECT</b></color>\n") + cardQuiz.why;
        contBtn.gameObject.SetActive(true);
    }

    void CloseCard()
    {
        bool ok = cardOk;
        cardRoot.SetActive(false);
        cardOpen = false;
        System.Action<bool> d = cardDone;
        cardDone = null;
        if (d != null) d(ok);
        UpdateBoard();
    }

    // =================================================================
    //  CERTIFICATE + SAVE
    // =================================================================
    void ShowCert()
    {
        int sc = Score();
        float pct = sc / (float)ledger.Length;
        bool pass = pct >= passPercent;
        certStatus.text = pass ? "<color=#40D173>PASS  -  COMPETENT</color>" : "<color=#F2594D>FAIL  -  RETRAIN REQUIRED</color>";
        int s = Mathf.FloorToInt(elapsed);
        certStats.text = "TRAINEE<pos=30%>" + traineeName + "  (" + traineeId + ")\nDRILL TIME<pos=30%>" + (s / 60).ToString("00") + ":" + (s % 60).ToString("00") +
                         "\nSCORE<pos=30%>" + sc + " / " + ledger.Length + "  (" + Mathf.RoundToInt(pct * 100f) + "%)\nERRORS<pos=30%>" + violations;
        string rows = "";
        foreach (Entry e in ledger)
            rows += e.label + "<pos=84%>" + (e.pts > 0 ? "<color=#40D173>[OK]" : "<color=#F2594D>[X]") + "</color>\n";
        certLedger.text = rows;
        certSync.text = SyncResult(sc, ledger.Length);
        certPanel.sizeDelta = new Vector2(Mathf.Min(990f, rootRT.rect.width - 24f), 1280f);
        certRoot.SetActive(true);
        certOpen = true;
        Sfx(pass ? cOk : cBad, 1f);
    }

    string SyncResult(int sc, int total)
    {
        PlayerPrefs.SetInt("CMVR_Blasting_Score", sc);
        PlayerPrefs.SetInt("CMVR_Blasting_Max", total);
        PlayerPrefs.Save();
        try
        {
            System.Type t = System.Type.GetType("BackendSync");
            if (t == null) return "Saved on this device. Backend not in this build.";
            const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            object inst = null;
            System.Reflection.PropertyInfo pi = t.GetProperty("Instance", bf);
            if (pi != null) inst = pi.GetValue(null);
            else { System.Reflection.FieldInfo fi = t.GetField("Instance", bf); if (fi != null) inst = fi.GetValue(null); }
            System.Reflection.MethodInfo mi = t.GetMethod("SaveAndSyncScore");
            if (inst == null || mi == null) return "Saved on this device. Backend not ready.";
            mi.Invoke(inst, new object[] { sc, total });
            return "Result sent to the training record.";
        }
        catch (System.Exception e) { return "Saved on this device. Sync failed: " + e.Message; }
    }

    // =================================================================
    //  HUD BUILD
    // =================================================================
    void BuildHud()
    {
        GameObject cg = new GameObject("BlastingHUD");
        Canvas canvas = cg.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        CanvasScaler sc = cg.AddComponent<CanvasScaler>();
        sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        sc.referenceResolution = new Vector2(1080f, 1920f);
        sc.matchWidthOrHeight = 0.5f;
        cg.AddComponent<GraphicRaycaster>();
        rootRT = (RectTransform)cg.transform;

        safeRT = NewRect("SafeArea", rootRT);
        ApplySafeArea();
        Transform s = safeRT;

        // top bar
        Image bar = Pnl("TopBar", s, ColPanel);
        Strip(bar.rectTransform, 24, 24, 16, 150);
        RawImage st = Stripe(bar.transform, 12);
        BottomStrip(st.rectTransform, 22, 22, 12, 12);
        phaseT = Txt(bar.transform, "PHASE 1  |  CIRCUIT CONTINUITY", 38, ColYellow, TextAlignmentOptions.MidlineLeft, FontStyles.Bold, true);
        Strip(phaseT.rectTransform, 30, 300, 52, 56);
        TextMeshProUGUI t1 = Txt(bar.transform, "SCENARIO 02  |  CONTROLLED BLASTING & MISFIRE", 22, ColMuted, TextAlignmentOptions.MidlineLeft, FontStyles.Bold, true);
        Strip(t1.rectTransform, 30, 300, 14, 34);
        timerT = Txt(bar.transform, "00:00", 46, Color.white, TextAlignmentOptions.MidlineRight, FontStyles.Bold, false);
        Fixed(timerT.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-26, -12), new Vector2(260, 60));
        scoreT = Txt(bar.transform, "SCORE 0 / 8", 24, ColBlue, TextAlignmentOptions.MidlineRight, FontStyles.Bold, false);
        Fixed(scoreT.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-26, -76), new Vector2(260, 34));

        // objective
        Image ob = Pnl("Objective", s, ColPanel);
        Strip(ob.rectTransform, 24, 24, 182, 104);
        Image chip = Pnl("Chip", ob.transform, new Color(0.99f, 0.78f, 0.13f, 0.2f));
        Fixed(chip.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(18, 0), new Vector2(170, 44));
        TextMeshProUGUI ct = Txt(chip.transform, "OBJECTIVE", 22, ColYellow, TextAlignmentOptions.Center, FontStyles.Bold, true);
        Fill(ct.rectTransform, 4, 2, 4, 2);
        objT = Txt(ob.transform, "", 27, Color.white, TextAlignmentOptions.MidlineLeft, FontStyles.Normal, true);
        Fill(objT.rectTransform, 206, 6, 20, 6);

        // blast console board
        Image bd = Pnl("Board", s, ColPanel);
        Fixed(bd.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(24, -302), new Vector2(430, 224));
        TextMeshProUGUI bt = Txt(bd.transform, "BLAST CONSOLE", 22, ColMuted, TextAlignmentOptions.MidlineLeft, FontStyles.Bold, false);
        Strip(bt.rectTransform, 20, 20, 10, 32);
        boardT = Txt(bd.transform, "", 25, ColText, TextAlignmentOptions.TopLeft, FontStyles.Normal, false);
        boardT.lineSpacing = 8f;
        Fill(boardT.rectTransform, 20, 8, 12, 48);

        // menu button
        Button mb = Btn("MenuBtn", s, "MENU", new Color(0.30f, 0.12f, 0.12f, 0.95f), 22, RequestExit);
        menuBtnRT = (RectTransform)mb.transform;
        Fixed(menuBtnRT, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-24, -302), new Vector2(130, 56));

        // crosshair + hint
        Image dot = NewImg("Cross", s, new Color(1f, 1f, 1f, 0.7f));
        Fixed(dot.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(10, 10));
        hintT = Txt(s, "", 30, ColYellow, TextAlignmentOptions.Center, FontStyles.Bold, true);
        Fixed(hintT.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -80), new Vector2(980, 54));

        // cooldown panel
        Image cd = Pnl("Cooldown", s, ColPanel);
        Fixed(cd.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 250), new Vector2(940, 100));
        cooldownGroup = cd.gameObject.AddComponent<CanvasGroup>();
        cooldownGroup.alpha = 0f; cooldownGroup.blocksRaycasts = false;
        cdText = Txt(cd.transform, "", 28, ColYellow, TextAlignmentOptions.MidlineLeft, FontStyles.Bold, true);
        Strip(cdText.rectTransform, 24, 24, 10, 42);
        Image track = NewImg("Track", cd.transform, new Color(1f, 1f, 1f, 0.12f));
        BottomStrip(track.rectTransform, 24, 24, 16, 20);
        cdFill = NewImg("Fill", track.transform, ColYellow);
        Fill(cdFill.rectTransform, 0, 0, 0, 0);
        cdFill.sprite = sprRound;                      // Filled images need a sprite
        cdFill.type = Image.Type.Filled;
        cdFill.fillMethod = Image.FillMethod.Horizontal;
        cdFill.fillAmount = 0f;

        // toast
        Image ts = Pnl("Toast", s, new Color(0.10f, 0.14f, 0.20f, 0.97f));
        Fixed(ts.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 130), new Vector2(940, 96));
        toastGroup = ts.gameObject.AddComponent<CanvasGroup>();
        toastGroup.alpha = 0f; toastGroup.blocksRaycasts = false;
        toastT = Txt(ts.transform, "", 27, Color.white, TextAlignmentOptions.Center, FontStyles.Bold, true);
        Fill(toastT.rectTransform, 20, 6, 20, 6);

        // big countdown, flash, fade (root level)
        bigT = Txt(rootRT, "", 220, ColYellow, TextAlignmentOptions.Center, FontStyles.Bold, false);
        Fixed(bigT.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(600, 300));
        flash = NewImg("Flash", rootRT, Color.clear);
        Fill(flash.rectTransform, 0, 0, 0, 0);
        fade = NewImg("Fade", rootRT, Color.black);
        Fill(fade.rectTransform, 0, 0, 0, 0);
        fade.raycastTarget = true;
        UpdateBoard();
    }

    void BuildCard()
    {
        cardRoot = new GameObject("DecisionCard", typeof(RectTransform));
        cardRoot.transform.SetParent(rootRT, false);
        Fill((RectTransform)cardRoot.transform, 0, 0, 0, 0);
        Image dim = NewImg("Dim", cardRoot.transform, new Color(0f, 0f, 0f, 0.75f));
        Fill(dim.rectTransform, 0, 0, 0, 0);
        dim.raycastTarget = true;
        Image panel = Pnl("Panel", cardRoot.transform, new Color(0.06f, 0.09f, 0.13f, 0.99f));
        panel.raycastTarget = true;
        cardPanel = panel.rectTransform;
        Fixed(cardPanel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(980, 800));
        RawImage st = Stripe(panel.transform, 14);
        Strip(st.rectTransform, 22, 22, 14, 14);

        cardTag = Txt(panel.transform, "", 24, ColYellow, TextAlignmentOptions.MidlineLeft, FontStyles.Bold, true);
        Strip(cardTag.rectTransform, 36, 36, 40, 40);
        cardText = Txt(panel.transform, "", 30, Color.white, TextAlignmentOptions.TopLeft, FontStyles.Normal, true);
        Strip(cardText.rectTransform, 36, 36, 96, 220);
        for (int i = 0; i < 3; i++)
        {
            int slot = i;
            optBtn[i] = Btn("Opt" + i, panel.transform, "", ColPanel2, 27, () => PickOption(slot));
            Strip((RectTransform)optBtn[i].transform, 36, 36, 340 + i * 142, 128);
            optTxt[i] = optBtn[i].GetComponentInChildren<TextMeshProUGUI>();
            optTxt[i].alignment = TextAlignmentOptions.MidlineLeft;
            Fill(optTxt[i].rectTransform, 22, 6, 22, 6);
        }
        cardResult = Txt(panel.transform, "", 29, ColText, TextAlignmentOptions.TopLeft, FontStyles.Normal, true);
        Strip(cardResult.rectTransform, 36, 36, 330, 300);
        contBtn = Btn("Continue", panel.transform, "CONTINUE", new Color(0.17f, 0.55f, 0.32f, 1f), 30, CloseCard);
        BottomStrip((RectTransform)contBtn.transform, 260, 260, 36, 92);
        cardRoot.SetActive(false);
    }

    void BuildCert()
    {
        certRoot = new GameObject("Certificate", typeof(RectTransform));
        certRoot.transform.SetParent(rootRT, false);
        Fill((RectTransform)certRoot.transform, 0, 0, 0, 0);
        Image dim = NewImg("Dim", certRoot.transform, new Color(0f, 0f, 0f, 0.82f));
        Fill(dim.rectTransform, 0, 0, 0, 0);
        dim.raycastTarget = true;
        Image panel = Pnl("Panel", certRoot.transform, new Color(0.06f, 0.09f, 0.13f, 0.99f));
        panel.raycastTarget = true;
        certPanel = panel.rectTransform;
        Fixed(certPanel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(990, 1280));
        Transform p = panel.transform;
        RawImage st = Stripe(p, 16);
        Strip(st.rectTransform, 22, 22, 16, 16);
        TextMeshProUGUI t = Txt(p, "BLASTING & MISFIRE DRILL", 46, ColYellow, TextAlignmentOptions.Center, FontStyles.Bold, true);
        Strip(t.rectTransform, 30, 30, 48, 64);
        TextMeshProUGUI t2 = Txt(p, "DEBRIEF CERTIFICATE (TRAINING SIMULATION)", 24, ColMuted, TextAlignmentOptions.Center, FontStyles.Bold, true);
        Strip(t2.rectTransform, 30, 30, 118, 34);
        certStatus = Txt(p, "", 48, Color.white, TextAlignmentOptions.Center, FontStyles.Bold, true);
        Strip(certStatus.rectTransform, 30, 30, 170, 70);
        certStats = Txt(p, "", 27, ColText, TextAlignmentOptions.TopLeft, FontStyles.Normal, false);
        certStats.lineSpacing = 12f;
        Strip(certStats.rectTransform, 50, 40, 270, 170);
        TextMeshProUGUI lh = Txt(p, "PROCEDURE CHECKLIST", 22, ColMuted, TextAlignmentOptions.MidlineLeft, FontStyles.Bold, false);
        Strip(lh.rectTransform, 50, 40, 462, 34);
        certLedger = Txt(p, "", 25, ColText, TextAlignmentOptions.TopLeft, FontStyles.Normal, false);
        certLedger.lineSpacing = 14f;
        Strip(certLedger.rectTransform, 50, 40, 504, 400);
        certSync = Txt(p, "", 22, ColBlue, TextAlignmentOptions.Center, FontStyles.Normal, true);
        Strip(certSync.rectTransform, 40, 40, 920, 40);
        TextMeshProUGUI note = Txt(p, "Wording follows DGMS-aligned practice for training. It is not an official certificate.", 20, ColMuted, TextAlignmentOptions.Center, FontStyles.Normal, true);
        Strip(note.rectTransform, 40, 40, 964, 34);
        Button save = Btn("Save", p, "SAVE & RETURN TO DISPATCH", new Color(0.17f, 0.55f, 0.32f, 1f), 30, LoadMenu);
        BottomStrip((RectTransform)save.transform, 60, 60, 112, 90);
        Button retry = Btn("Retry", p, "RETRY DRILL", new Color(0.20f, 0.45f, 0.70f, 1f), 28, () => SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex));
        BottomStrip((RectTransform)retry.transform, 60, 60, 12, 86);
        certRoot.SetActive(false);
    }

    // =================================================================
    //  UI TOOLKIT
    // =================================================================
    RectTransform NewRect(string n, Transform p)
    {
        GameObject g = new GameObject(n, typeof(RectTransform));
        g.transform.SetParent(p, false);
        return (RectTransform)g.transform;
    }

    static void Fill(RectTransform rt, float l, float b, float r, float t)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(l, b); rt.offsetMax = new Vector2(-r, -t);
    }

    static void Strip(RectTransform rt, float l, float r, float top, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
        rt.offsetMin = new Vector2(l, -top - h); rt.offsetMax = new Vector2(-r, -top);
    }

    static void BottomStrip(RectTransform rt, float l, float r, float bottom, float h)
    {
        rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0f);
        rt.offsetMin = new Vector2(l, bottom); rt.offsetMax = new Vector2(-r, bottom + h);
    }

    static void Fixed(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = anchor; rt.anchorMax = anchor; rt.pivot = pivot;
        rt.anchoredPosition = pos; rt.sizeDelta = size;
    }

    Image NewImg(string n, Transform p, Color c)
    {
        Image i = NewRect(n, p).gameObject.AddComponent<Image>();
        i.color = c; i.raycastTarget = false;
        return i;
    }

    Image Pnl(string n, Transform p, Color c)
    {
        Image i = NewImg(n, p, c);
        i.sprite = sprRound; i.type = Image.Type.Sliced;
        return i;
    }

    RawImage Stripe(Transform p, float h)
    {
        RawImage r = NewRect("Stripe", p).gameObject.AddComponent<RawImage>();
        r.texture = stripeTex; r.uvRect = new Rect(0f, 0f, 34f, h / 32f);
        r.color = new Color(1f, 1f, 1f, 0.9f); r.raycastTarget = false;
        return r;
    }

    TextMeshProUGUI Txt(Transform p, string s, float size, Color c, TextAlignmentOptions a, FontStyles st, bool fit)
    {
        TextMeshProUGUI t = NewRect("Text", p).gameObject.AddComponent<TextMeshProUGUI>();
        t.text = s; t.fontSize = size; t.color = c; t.alignment = a; t.fontStyle = st;
        t.raycastTarget = false; t.richText = true;
        t.overflowMode = TextOverflowModes.Ellipsis;
        if (fit) { t.enableAutoSizing = true; t.fontSizeMax = size; t.fontSizeMin = size * 0.5f; }
        return t;
    }

    Button Btn(string n, Transform p, string label, Color bg, float fs, UnityEngine.Events.UnityAction onClick)
    {
        Image img = Pnl(n, p, bg);
        img.raycastTarget = true;
        Button b = img.gameObject.AddComponent<Button>();
        b.targetGraphic = img;
        ColorBlock cb = b.colors;
        cb.normalColor = Color.white; cb.highlightedColor = Color.white; cb.selectedColor = Color.white;
        cb.pressedColor = new Color(0.72f, 0.72f, 0.72f, 1f);
        cb.fadeDuration = 0.08f;
        b.colors = cb;
        b.onClick.AddListener(onClick);
        TextMeshProUGUI t = Txt(img.transform, label, fs, Color.white, TextAlignmentOptions.Center, FontStyles.Bold, true);
        Fill(t.rectTransform, 12, 4, 12, 4);
        return b;
    }

    void ApplySafeArea()
    {
        lastSafe = Screen.safeArea; lastW = Screen.width; lastH = Screen.height;
        if (safeRT == null || lastW <= 0 || lastH <= 0) return;
        Vector2 min = lastSafe.position, max = lastSafe.position + lastSafe.size;
        safeRT.anchorMin = new Vector2(min.x / lastW, min.y / lastH);
        safeRT.anchorMax = new Vector2(max.x / lastW, max.y / lastH);
        safeRT.offsetMin = Vector2.zero; safeRT.offsetMax = Vector2.zero;
    }

    void Toast(string msg, Color c, float secs)
    {
        if (toastCo != null) StopCoroutine(toastCo);
        toastCo = StartCoroutine(ToastRoutine(msg, c, secs));
    }

    IEnumerator ToastRoutine(string msg, Color c, float secs)
    {
        toastT.text = msg; toastT.color = c;
        yield return Fade(toastGroup, toastGroup.alpha, 1f, 0.15f);
        yield return new WaitForSeconds(secs);
        yield return Fade(toastGroup, 1f, 0f, 0.3f);
    }

    IEnumerator Fade(CanvasGroup g, float from, float to, float dur)
    {
        for (float t = 0f; t < dur; t += Time.unscaledDeltaTime) { g.alpha = Mathf.Lerp(from, to, t / dur); yield return null; }
        g.alpha = to;
    }

    IEnumerator Fade(Image img, float from, float to, float dur)
    {
        Color c = img.color;
        for (float t = 0f; t < dur; t += Time.unscaledDeltaTime) { c.a = Mathf.Lerp(from, to, t / dur); img.color = c; yield return null; }
        c.a = to; img.color = c;
    }

    // =================================================================
    //  PROCEDURAL TEXTURES / SPRITES
    // =================================================================
    static Texture2D NoiseTex(int n, float lo, float hi, int seed)
    {
        Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
        Color32[] px = new Color32[n * n];
        System.Random rng = new System.Random(seed);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float v = Mathf.PerlinNoise(x * 0.06f + seed, y * 0.06f) * 0.6f + Mathf.PerlinNoise(x * 0.2f, y * 0.2f + seed) * 0.4f;
                float g = Mathf.Lerp(lo, hi, v);
                if (rng.NextDouble() < 0.004) g += 0.2f;
                px[y * n + x] = new Color(g, g, g * 1.04f, 1f);
            }
        t.SetPixels32(px);
        t.Apply(true, false);
        return t;
    }

    static Texture2D SoftTex()
    {
        Texture2D t = new Texture2D(64, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        Color32[] px = new Color32[64 * 64];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                float r = Mathf.Sqrt((x - 31.5f) * (x - 31.5f) + (y - 31.5f) * (y - 31.5f)) / 32f;
                float a = Mathf.Pow(Mathf.Clamp01(1f - r), 2f);
                px[y * 64 + x] = new Color(1f, 1f, 1f, a);
            }
        t.SetPixels32(px);
        t.Apply(false, false);
        return t;
    }

    static Texture2D StripeTex()
    {
        Texture2D t = new Texture2D(32, 32, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat };
        Color32 y = new Color32(252, 199, 33, 255), d = new Color32(22, 26, 32, 255);
        Color32[] px = new Color32[32 * 32];
        for (int j = 0; j < 32; j++)
            for (int i = 0; i < 32; i++) px[j * 32 + i] = (((i + j) / 8) % 2 == 0) ? y : d;
        t.SetPixels32(px);
        t.Apply(false, false);
        return t;
    }

    static Sprite RoundSprite(int size, int radius, int border)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        Color32[] px = new Color32[size * size];
        float half = size * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(Mathf.Abs(x + 0.5f - half) - (half - radius), 0f);
                float dy = Mathf.Max(Mathf.Abs(y + 0.5f - half) - (half - radius), 0f);
                float d = Mathf.Sqrt(dx * dx + dy * dy) - radius;
                px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(0.5f - d) * 255f));
            }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
    }

    // =================================================================
    //  PROCEDURAL AUDIO
    // =================================================================
    const int SR = 22050;

    AudioClip Tone(string n, float sec, System.Func<float, float> f)
    {
        float[] d = new float[Mathf.CeilToInt(SR * sec)];
        for (int i = 0; i < d.Length; i++) d[i] = Mathf.Clamp(f(i / (float)SR), -1f, 1f);
        AudioClip c = AudioClip.Create(n, d.Length, 1, SR, false);
        c.SetData(d, 0);
        return c;
    }

    void BuildAudio()
    {
        sfx = NewSrc("Sfx", false);
        loopSrc = NewSrc("Loop", true);
        const float tp = 6.2831853f;
        cClick = Tone("click", 0.07f, t => Mathf.Sin(tp * 1400f * t) * Mathf.Exp(-t * 50f) * 0.6f);
        cOk = Tone("ok", 0.4f, t => (Mathf.Sin(tp * 660f * t) + (t > 0.15f ? Mathf.Sin(tp * 990f * (t - 0.15f)) : 0f)) * Mathf.Exp(-t * 6f) * 0.4f);
        cBad = Tone("bad", 0.45f, t => Mathf.Sign(Mathf.Sin(tp * 150f * t)) * Mathf.Exp(-t * 5f) * 0.3f);
        cTick = Tone("tick", 0.12f, t => Mathf.Sin(tp * 900f * t) * Mathf.Exp(-t * 35f) * 0.6f);
        cBuzz = Tone("buzz", 1.8f, t => Mathf.Sin(tp * 120f * t) * 0.15f * (0.6f + 0.4f * Mathf.Sin(tp * 9f * t)));
        cSiren = Tone("siren", 2f, t => Mathf.Sin(tp * (800f * t + 350f / (tp * 0.5f) * (1f - Mathf.Cos(tp * 0.5f * t)))) * 0.45f);
        float lp = 0f;
        cBoom = Tone("boom", 3.5f, t =>
        {
            lp += (Random.value * 2f - 1f - lp) * 0.08f;
            float env = Mathf.Clamp01(t / 0.02f) * Mathf.Exp(-t * 1.1f);
            return (lp * 2.2f + Mathf.Sin(tp * (48f - 8f * t) * t) * 0.8f) * env;
        });
    }

    AudioSource NewSrc(string n, bool loop)
    {
        GameObject g = new GameObject(n);
        g.transform.SetParent(transform, false);
        AudioSource s = g.AddComponent<AudioSource>();
        s.playOnAwake = false; s.loop = loop; s.spatialBlend = 0f;
        return s;
    }

    void Sfx(AudioClip c, float v) { if (sfx != null && c != null) sfx.PlayOneShot(c, v); }
}