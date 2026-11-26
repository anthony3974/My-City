using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Start screen, pause menu and settings, drawn with OnGUI like the rest of the HUD.
///
/// The game opens on the title screen: the city is built and the traffic drives behind it while the camera
/// slowly circles the taxi, but the car takes no input. PLAY hands over control.
/// In the game, Esc, P, gamepad Start, Android back or the pause button (left of UPGRADES) pause it.
/// The pause menu can resume, open upgrades or stats, flip the car, change settings, go back to the
/// title screen or quit. Settings are saved in PlayerPrefs.
///
/// Other scripts ask <see cref="BlocksInput"/> (ignore driving input), <see cref="HideHud"/> (don't draw)
/// and <see cref="Frozen"/> (time is stopped). Creates itself at startup in any scene with a CityStreamer.
/// </summary>
public class GameMenu : MonoBehaviour
{
    enum Page { None, Title, Pause, Settings, ConfirmReset }

    static Page page = Page.None;
    static bool inGame = true;   // true when there is no menu system (scenes without a city)
    static Rect pauseButton;

    /// <summary>True once PLAY has been pressed (false on the title screen).</summary>
    public static bool InGame => inGame;
    /// <summary>Driving input, HUD buttons and the camera should ignore the player.</summary>
    public static bool BlocksInput => !inGame || page != Page.None || UpgradeMenu.Open;
    /// <summary>The HUD should not be drawn (title screen or a menu covers it).</summary>
    public static bool HideHud => !inGame || page != Page.None;
    /// <summary>Time is stopped (pause menu or upgrades). The title screen keeps the city moving.</summary>
    public static bool Frozen => (inGame && page != Page.None) || UpgradeMenu.Open;
    /// <summary>True if TouchControls should ignore a touch at p (GUI coordinates, y down).</summary>
    public static bool Blocks(Vector2 p) => page != Page.None || !inGame || pauseButton.Contains(p);

    /// <summary>Sets Time.timeScale from the open menus.</summary>
    public static void ApplyTimeScale() => Time.timeScale = Frozen ? 0f : 1f;

    // Settings (PlayerPrefs keys and choices).
    const string QualityKey = "Settings.RenderQuality", FpsKey = "Settings.FrameRate",
        InvertKey = "Settings.InvertY", TouchKey = "Settings.TouchSize";
    static readonly string[] QualityNames = { "LOW", "MEDIUM", "HIGH" };
    static readonly float[] QualityScale = { 0.65f, 0.82f, 1f };
    static readonly string[] FpsNames = { "30", "60", "MAX" };
    static readonly int[] FpsValues = { 30, 60, -1 };
    static readonly string[] TouchNames = { "SMALL", "MEDIUM", "LARGE" };
    static readonly float[] TouchScale = { 0.85f, 1f, 1.18f };

    int quality, fps, touchSize;
    bool invertY;
    UniversalRenderPipelineAsset urp;
    float baseRenderScale = 1f;

    CityStreamer city;
    CarController car;
    ThirdPersonCamera cam;
    TaxiGame taxi;

    Page settingsReturn = Page.Title;
    int selected;
    bool blockedLastFrame = true;
    Vector2 lastMouse;

    // Buttons on the current page, rebuilt each frame.
    struct Item { public string label; public System.Action act; public Rect rect; public bool accent, danger; }
    readonly List<Item> items = new();
    Rect panel;
    float u;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        var streamer = FindFirstObjectByType<CityStreamer>();
        if (streamer == null || streamer.car == null) return;
        var m = new GameObject("Game Menu").AddComponent<GameMenu>();
        m.city = streamer;
        m.car = streamer.car.GetComponent<CarController>();
        inGame = false;
        page = Page.Title;
        ApplyTimeScale();
    }

    void Start()
    {
        if (Camera.main) cam = Camera.main.GetComponent<ThirdPersonCamera>();
        urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urp != null) baseRenderScale = urp.renderScale;

        quality = Mathf.Clamp(PlayerPrefs.GetInt(QualityKey, QualityNames.Length - 1), 0, QualityNames.Length - 1);
        fps = Mathf.Clamp(PlayerPrefs.GetInt(FpsKey, Application.isMobilePlatform ? 1 : 2), 0, FpsNames.Length - 1);
        touchSize = Mathf.Clamp(PlayerPrefs.GetInt(TouchKey, 1), 0, TouchNames.Length - 1);
        invertY = PlayerPrefs.GetInt(InvertKey, 0) == 1;
        ApplySettings();
    }

    void OnDestroy()
    {
        // The render pipeline asset is a project file: put it back so Play mode doesn't change it.
        if (urp != null) urp.renderScale = baseRenderScale;
        page = Page.None;
        inGame = true;
        pauseButton = default;
        Time.timeScale = 1f;
    }

    void ApplySettings()
    {
        if (urp != null) urp.renderScale = baseRenderScale * QualityScale[quality];
        Application.targetFrameRate = FpsValues[fps];
        QualitySettings.vSyncCount = 0;
        TouchControls.SizeScale = TouchScale[touchSize];
        if (cam != null) cam.invertY = invertY;
    }

    void SaveSettings()
    {
        PlayerPrefs.SetInt(QualityKey, quality);
        PlayerPrefs.SetInt(FpsKey, fps);
        PlayerPrefs.SetInt(TouchKey, touchSize);
        PlayerPrefs.SetInt(InvertKey, invertY ? 1 : 0);
        PlayerPrefs.Save();
        ApplySettings();
    }

    // ------------------------------------------------------------------ Page changes

    void Go(Page p)
    {
        page = p;
        selected = 0;
        ApplyTimeScale();
    }

    void Play()
    {
        inGame = true;
        Go(Page.None);
    }

    void ToTitle()
    {
        inGame = false;
        Go(Page.Title);
    }

    void OpenSettings()
    {
        settingsReturn = page;
        Go(Page.Settings);
    }

    static void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    void ResetProgress()
    {
        if (taxi != null) taxi.ResetProgress();
        var upgrades = FindFirstObjectByType<UpgradeMenu>();
        if (upgrades != null) upgrades.ResetLevels();
        Go(settingsReturn == Page.Pause ? Page.Pause : Page.Title);
    }

    void Back()
    {
        switch (page)
        {
            case Page.Pause: Go(Page.None); break;
            case Page.Settings: Go(settingsReturn); break;
            case Page.ConfirmReset: Go(Page.Settings); break;
            case Page.Title:
                // Android back on the title screen leaves the app, as phone users expect.
                if (Application.platform == RuntimePlatform.Android) Quit();
                break;
        }
    }

    // ------------------------------------------------------------------ Pages

    void BuildItems()
    {
        items.Clear();
        switch (page)
        {
            case Page.Title:
                bool started = taxi != null && (taxi.Money > 0 || taxi.Trips > 0);
                Add(started ? "CONTINUE" : "PLAY", Play, accent: true);
                Add("SETTINGS", OpenSettings);
                Add("QUIT", Quit);
                break;

            case Page.Pause:
                Add("RESUME", () => Go(Page.None), accent: true);
                Add("UPGRADES", () => UpgradeMenu.Show());
                Add(GameMetrics.PanelShown ? "HIDE STATS" : "SHOW STATS", () => GameMetrics.PanelShown = !GameMetrics.PanelShown);
                Add("FLIP CAR UPRIGHT", () => { if (car != null) car.ResetCar(); Go(Page.None); });
                Add("SETTINGS", OpenSettings);
                Add("MAIN MENU", ToTitle);
                if (!Application.isMobilePlatform) Add("QUIT GAME", Quit);
                break;

            case Page.Settings:
                Add($"RENDER QUALITY:  {QualityNames[quality]}", () => { quality = (quality + 1) % QualityNames.Length; SaveSettings(); });
                Add($"FRAME RATE:  {FpsNames[fps]}", () => { fps = (fps + 1) % FpsNames.Length; SaveSettings(); });
                Add($"INVERT CAMERA Y:  {(invertY ? "ON" : "OFF")}", () => { invertY = !invertY; SaveSettings(); });
                if (TouchControls.Active)
                    Add($"TOUCH BUTTONS:  {TouchNames[touchSize]}", () => { touchSize = (touchSize + 1) % TouchNames.Length; SaveSettings(); });
                Add("RESET PROGRESS", () => Go(Page.ConfirmReset), danger: true);
                Add("BACK", Back);
                break;

            case Page.ConfirmReset:
                Add("NO, KEEP IT", () => Go(Page.Settings), accent: true);
                Add("YES, RESET EVERYTHING", ResetProgress, danger: true);
                break;
        }
        selected = Mathf.Clamp(selected, 0, Mathf.Max(0, items.Count - 1));
    }

    void Add(string label, System.Action act, bool accent = false, bool danger = false) =>
        items.Add(new Item { label = label, act = act, accent = accent, danger = danger });

    void Layout()
    {
        var safe = Screen.safeArea;               // y up; GUI is y down
        float top = Screen.height - safe.yMax;
        float s = Mathf.Min(Screen.width, Screen.height);

        // Pause button: a square left of the UPGRADES button.
        var up = UpgradeMenu.OpenButtonRect;
        pauseButton = inGame && page == Page.None && !UpgradeMenu.Open && up.width > 0f
            ? new Rect(up.x - up.height * 1.25f, up.y, up.height, up.height)
            : default;

        // Title gets room above the buttons for the name.
        float header = page == Page.Title ? 3.2f : 1.6f;
        float rowH = 1.15f, gap = 0.28f;
        float ph = header + items.Count * (rowH + gap) + 0.5f;
        u = Mathf.Min(s * 0.075f, (safe.height - s * 0.06f) / ph, (safe.width - s * 0.06f) / 9f);
        float pw = u * 9f;
        panel = new Rect(safe.center.x - pw / 2f, top + safe.height / 2f - ph * u / 2f, pw, ph * u);
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            it.rect = new Rect(panel.x + u * 0.6f, panel.y + (header + i * (rowH + gap)) * u, pw - u * 1.2f, rowH * u);
            items[i] = it;
        }
    }

    // ------------------------------------------------------------------ Input

    void Update()
    {
        if (taxi == null) taxi = FindFirstObjectByType<TaxiGame>();
        if (cam == null && Camera.main) { cam = Camera.main.GetComponent<ThirdPersonCamera>(); ApplySettings(); }

        BuildItems();
        Layout();

        // Let the upgrade menu own its input; skip one more frame after it closes so its Esc/B
        // press doesn't also close the pause menu underneath.
        if (UpgradeMenu.Open || blockedLastFrame)
        {
            blockedLastFrame = UpgradeMenu.Open;
            return;
        }

        var kb = Keyboard.current;
        var pad = Gamepad.current;
        bool back = (kb != null && kb.escapeKey.wasPressedThisFrame) || (pad != null && pad.buttonEast.wasPressedThisFrame);
        bool pauseKey = (kb != null && (kb.escapeKey.wasPressedThisFrame || kb.pKey.wasPressedThisFrame))
            || (pad != null && pad.startButton.wasPressedThisFrame);
        bool tapped = UpgradeMenu.Tap(out Vector2 p);

        if (page == Page.None)
        {
            if (pauseKey || (tapped && pauseButton.Contains(p))) Go(Page.Pause);
            return;
        }

        if (back || (page == Page.Pause && pad != null && pad.startButton.wasPressedThisFrame)
                 || (page == Page.Pause && kb != null && kb.pKey.wasPressedThisFrame))
        {
            Back();
            return;
        }

        // Mouse hover picks a row (only when the mouse actually moves, so keys still work).
        var mouse = Mouse.current;
        if (mouse != null)
        {
            Vector2 mp = mouse.position.ReadValue();
            if ((mp - lastMouse).sqrMagnitude > 1f)
            {
                Vector2 g = new(mp.x, Screen.height - mp.y);
                for (int i = 0; i < items.Count; i++)
                    if (items[i].rect.Contains(g)) selected = i;
            }
            lastMouse = mp;
        }

        if (tapped)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].rect.Contains(p)) { selected = i; items[i].act(); return; }
            return;
        }

        int move = 0;
        bool activate = false;
        if (kb != null)
        {
            if (kb.upArrowKey.wasPressedThisFrame || kb.wKey.wasPressedThisFrame) move--;
            if (kb.downArrowKey.wasPressedThisFrame || kb.sKey.wasPressedThisFrame) move++;
            activate |= kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame;
        }
        if (pad != null)
        {
            if (pad.dpad.up.wasPressedThisFrame) move--;
            if (pad.dpad.down.wasPressedThisFrame) move++;
            activate |= pad.buttonSouth.wasPressedThisFrame;
            if (page == Page.Title && pad.startButton.wasPressedThisFrame) activate = true;
        }
        if (items.Count == 0) return;
        selected = (selected + move + items.Count) % items.Count;
        if (activate) items[selected].act();
    }

    // ------------------------------------------------------------------ Drawing

    void OnGUI()
    {
        GUI.depth = -20;   // above everything else
        if (Event.current.type != EventType.Repaint) return;

        if (page == Page.None)
        {
            if (pauseButton.width > 0f && !UpgradeMenu.Open)
            {
                HudDraw.Box(pauseButton, new Color(1f, 1f, 1f, 0.2f));
                float bw = pauseButton.width * 0.13f, bh = pauseButton.height * 0.46f;
                Vector2 c = pauseButton.center;
                HudDraw.Draw(new Rect(c.x - bw * 1.6f, c.y - bh / 2f, bw, bh), HudDraw.Bar, Color.white);
                HudDraw.Draw(new Rect(c.x + bw * 0.6f, c.y - bh / 2f, bw, bh), HudDraw.Bar, Color.white);
            }
            return;
        }
        if (UpgradeMenu.Open) return;   // upgrades opened from the pause menu draw themselves

        var full = new Rect(0f, 0f, Screen.width, Screen.height);
        HudDraw.Draw(full, Texture2D.whiteTexture, new Color(0f, 0f, 0f, page == Page.Title ? 0.25f : 0.5f));
        HudDraw.Box(panel, new Color(0.08f, 0.09f, 0.12f, 0.9f));

        float cx = panel.center.x;
        switch (page)
        {
            case Page.Title:
                HudDraw.Text(new Vector2(cx, panel.y + u * 1.05f), "MY CITY", Mathf.RoundToInt(u * 1.1f), Gold);
                string sub = taxi != null && (taxi.Money > 0 || taxi.Trips > 0)
                    ? $"${taxi.Money}   ·   {taxi.Trips} fares delivered"
                    : "Drive the taxi. Pick up fares. Get paid.";
                HudDraw.Text(new Vector2(cx, panel.y + u * 2.25f), sub, Mathf.RoundToInt(u * 0.36f), new Color(1f, 1f, 1f, 0.75f));
                break;
            case Page.Pause: Header("PAUSED"); break;
            case Page.Settings: Header("SETTINGS"); break;
            case Page.ConfirmReset: Header("RESET MONEY AND UPGRADES?"); break;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            bool sel = i == selected;
            Color fill = it.accent ? new Color(0.2f, 0.7f, 0.32f, sel ? 1f : 0.8f)
                : it.danger ? new Color(0.75f, 0.2f, 0.18f, sel ? 0.95f : 0.6f)
                : new Color(1f, 1f, 1f, sel ? 0.24f : 0.1f);
            HudDraw.Box(it.rect, fill);
            if (sel) HudDraw.Box(new Rect(it.rect.x, it.rect.y, u * 0.12f, it.rect.height), Gold);
            HudDraw.Text(it.rect.center, it.label, Mathf.RoundToInt(u * 0.42f), Color.white);
        }

        if (page == Page.Title)
        {
            string hint = TouchControls.Active ? "" : "Enter / A to choose    ·    Esc / Start pauses in game";
            if (hint.Length > 0)
                HudDraw.Text(new Vector2(cx, panel.yMax + u * 0.5f), hint, Mathf.RoundToInt(u * 0.28f), new Color(1f, 1f, 1f, 0.6f));
        }
    }

    void Header(string text) =>
        HudDraw.Text(new Vector2(panel.center.x, panel.y + u * 0.8f), text, Mathf.RoundToInt(u * 0.55f), Color.white);

    static readonly Color Gold = new(1f, 0.82f, 0.2f);
}
