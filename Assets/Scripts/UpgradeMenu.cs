using UnityEngine;
using UnityEngine.InputSystem;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

/// <summary>
/// Upgrade shop paid for with taxi money: speed, brakes and handling, each in five levels with rising prices.
/// Levels are saved in PlayerPrefs and applied to the CarController's tuning, starting from the values the car
/// was built with. The game pauses while the menu is open.
///
/// Open: the UPGRADES button (top-right), U, or Start on a gamepad.
/// In the menu: tap or click a price to buy; Up/Down (W/S, d-pad) pick a row, Enter/Space (A) buys;
/// Esc, U, Android back, B or Start close it, as does tapping outside the panel.
/// Added by TaxiGame.
/// </summary>
public class UpgradeMenu : MonoBehaviour
{
    public const int MaxLevel = 5;
    static readonly int[] Prices = { 50, 100, 150, 250, 400 };   // cost of level 1 to 5
    static readonly string[] Names = { "Speed", "Brakes", "Handling" };
    const int Speed = 0, Brakes = 1, Handling = 2;

    /// <summary>True while the menu is open (the game is paused).</summary>
    public static bool Open { get; private set; }
    static Rect openButton;

    /// <summary>Where the UPGRADES button is (GUI coordinates); GameMetrics places its STATS button under it.</summary>
    public static Rect OpenButtonRect => openButton;

    /// <summary>True if TouchControls should ignore a touch at p (GUI coordinates, y down).</summary>
    public static bool Blocks(Vector2 p) => Open || openButton.Contains(p);

    struct Tune { public float maxSpeed, motor, brake, frontGrip, rearGrip, steer, highSteer, steerRate; }

    // Fully upgraded car. Level 0 is whatever the car was built with.
    static readonly Tune Best = new()
    {
        maxSpeed = 38f, motor = 12000f,   // 137 km/h
        brake = 30000f,
        frontGrip = 0.92f, rearGrip = 0.88f, steer = 34f, highSteer = 6.5f, steerRate = 130f,
    };

    TaxiGame taxi;
    CarController car;
    CarDamage damage;
    Tune start;
    readonly int[] level = new int[3];

    int selected, flashRow = -1;
    bool flashOk;
    float flashUntil;

    float u;
    Rect panel, done, repair;
    readonly Rect[] rows = new Rect[3], buys = new Rect[3];

    public void Init(TaxiGame taxi, CarController car)
    {
        this.taxi = taxi;
        this.car = car;
        damage = car.GetComponent<CarDamage>();
        start = new Tune
        {
            maxSpeed = car.maxSpeed, motor = car.motorForce, brake = car.brakeForce,
            frontGrip = car.frontGrip, rearGrip = car.rearGrip,
            steer = car.maxSteerAngle, highSteer = car.highSpeedSteerAngle, steerRate = car.steerRate,
        };
        for (int i = 0; i < level.Length; i++)
            level[i] = Mathf.Clamp(PlayerPrefs.GetInt(Key(i), 0), 0, MaxLevel);
        Apply();
    }

    void OnDestroy()
    {
        if (Open) Time.timeScale = 1f;
        Open = false;
        openButton = default;
    }

    static string Key(int stat) => "Upgrade." + Names[stat];
    static float T(int lvl) => lvl / (float)MaxLevel;

    void Apply()
    {
        float s = T(level[Speed]), b = T(level[Brakes]), h = T(level[Handling]);
        car.maxSpeed = Mathf.Lerp(start.maxSpeed, Best.maxSpeed, s);
        car.motorForce = Mathf.Lerp(start.motor, Best.motor, s);
        car.brakeForce = Mathf.Lerp(start.brake, Best.brake, b);
        car.frontGrip = Mathf.Lerp(start.frontGrip, Best.frontGrip, h);
        car.rearGrip = Mathf.Lerp(start.rearGrip, Best.rearGrip, h);
        car.maxSteerAngle = Mathf.Lerp(start.steer, Best.steer, h);
        car.highSpeedSteerAngle = Mathf.Lerp(start.highSteer, Best.highSteer, h);
        car.steerRate = Mathf.Lerp(start.steerRate, Best.steerRate, h);
    }

    /// <summary>What a stat gives at a level, for the menu.</summary>
    string Value(int stat, int lvl)
    {
        float t = T(lvl);
        return stat switch
        {
            Speed => $"{Mathf.RoundToInt(Mathf.Lerp(start.maxSpeed, Best.maxSpeed, t) * 3.6f)} km/h",
            Brakes => $"{Mathf.RoundToInt(Mathf.Lerp(start.brake, Best.brake, t) / start.brake * 100f)}% power",
            _ => $"{Mathf.RoundToInt(Mathf.Lerp(start.frontGrip, Best.frontGrip, t) / start.frontGrip * 100f)}% grip",
        };
    }

    bool CanBuyAny()
    {
        for (int i = 0; i < level.Length; i++)
            if (level[i] < MaxLevel && taxi.Money >= Prices[level[i]]) return true;
        return false;
    }

    void Buy(int stat)
    {
        int lvl = level[stat];
        flashOk = lvl < MaxLevel && taxi.Spend(Prices[lvl]);
        if (flashOk)
        {
            level[stat] = lvl + 1;
            PlayerPrefs.SetInt(Key(stat), level[stat]);
            PlayerPrefs.Save();
            Apply();
        }
        flashRow = stat;
        flashUntil = Time.unscaledTime + 0.35f;
    }

    void SetOpen(bool open)
    {
        Open = open;
        Time.timeScale = open ? 0f : 1f;
    }

    // ------------------------------------------------------------------ Input

    void Update()
    {
        if (car == null) return;
        Layout();

        var kb = Keyboard.current;
        var pad = Gamepad.current;
        bool toggle = (kb != null && kb.uKey.wasPressedThisFrame) || (pad != null && pad.startButton.wasPressedThisFrame);
        bool tapped = Tap(out Vector2 p);

        if (!Open)
        {
            if (toggle || (tapped && openButton.Contains(p))) SetOpen(true);
            return;
        }

        bool back = (kb != null && kb.escapeKey.wasPressedThisFrame) || (pad != null && pad.buttonEast.wasPressedThisFrame);
        if (toggle || back)
        {
            SetOpen(false);
            return;
        }

        if (tapped)
        {
            if (done.Contains(p) || !panel.Contains(p))
            {
                SetOpen(false);
                return;
            }
            if (damage != null && damage.CurrentRepairCost > 0 && repair.Contains(p))
            {
                damage.TryRepair(allowFreeIfBroke: damage.Wrecked);
                return;
            }
            for (int i = 0; i < rows.Length; i++)
            {
                if (buys[i].Contains(p)) Buy(i);
                if (rows[i].Contains(p)) selected = i;
            }
        }

        int move = 0;
        if (kb != null)
        {
            if (kb.upArrowKey.wasPressedThisFrame || kb.wKey.wasPressedThisFrame) move--;
            if (kb.downArrowKey.wasPressedThisFrame || kb.sKey.wasPressedThisFrame) move++;
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame)
                Buy(selected);
        }
        if (pad != null)
        {
            if (pad.dpad.up.wasPressedThisFrame) move--;
            if (pad.dpad.down.wasPressedThisFrame) move++;
            if (pad.buttonSouth.wasPressedThisFrame) Buy(selected);
        }
        selected = (selected + move + rows.Length) % rows.Length;
    }

    /// <summary>A new touch or left click this frame, in GUI coordinates (y down).</summary>
    static bool Tap(out Vector2 p)
    {
        if (TouchControls.Active)
        {
            foreach (var t in Touch.activeTouches)
            {
                if (t.phase != TouchPhase.Began) continue;
                p = Flip(t.screenPosition);
                return true;
            }
        }
        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            p = Flip(mouse.position.ReadValue());
            return true;
        }
        p = default;
        return false;
    }

    static Vector2 Flip(Vector2 screen) => new(screen.x, Screen.height - screen.y);

    // ------------------------------------------------------------------ Drawing

    void Layout()
    {
        var safe = Screen.safeArea;               // y up; GUI is y down
        float top = Screen.height - safe.yMax;
        float s = Mathf.Min(Screen.width, Screen.height);

        if (TouchControls.Active)
        {
            // Under the RESET button.
            var r = TouchControls.ResetRect;
            openButton = new Rect(r.x, r.yMax + r.height * 0.3f, r.width, r.height);
        }
        else
        {
            float m = s * 0.05f, w = s * 0.32f, h = s * 0.08f;
            openButton = new Rect(safe.xMax - m - w, top + m, w, h);
        }

        float gutter = s * 0.04f;
        u = Mathf.Min(s * 0.1f, (safe.width - gutter * 2f) / 12f, (safe.height - gutter * 2f) / 8.4f);
        float pw = u * 12f, ph = u * 8.4f;
        panel = new Rect(safe.center.x - pw / 2f, top + safe.height / 2f - ph / 2f, pw, ph);

        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new Rect(panel.x + u * 0.4f, panel.y + u * 2.2f + i * u * 1.6f, pw - u * 0.8f, u * 1.4f);
            buys[i] = new Rect(rows[i].xMax - u * 3f, rows[i].center.y - u * 0.55f, u * 2.8f, u * 1.1f);
        }
        bool showRepair = damage != null && damage.CurrentRepairCost > 0;
        if (showRepair)
        {
            repair = new Rect(panel.center.x - u * 4.1f, panel.y + u * 7.05f, u * 4.2f, u * 1f);
            done = new Rect(panel.center.x + u * 0.4f, panel.y + u * 7.05f, u * 3.5f, u * 1f);
        }
        else
        {
            repair = default;
            done = new Rect(panel.center.x - u * 1.75f, panel.y + u * 7.05f, u * 3.5f, u * 1f);
        }
    }

    void OnGUI()
    {
        GUI.depth = -10;   // on top of the other HUD
        if (car == null || Event.current.type != EventType.Repaint) return;

        if (!Open)
        {
            HudDraw.Box(openButton, new Color(1f, 1f, 1f, 0.2f));
            string label = TouchControls.Active ? "UPGRADES" : "UPGRADES (U)";
            HudDraw.Text(openButton.center, label, Mathf.RoundToInt(openButton.height * 0.38f),
                CanBuyAny() ? Gold : Color.white);
            return;
        }

        HudDraw.Draw(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture, new Color(0f, 0f, 0f, 0.45f));
        HudDraw.Box(panel, new Color(0.08f, 0.09f, 0.12f, 0.94f));
        HudDraw.Text(new Vector2(panel.center.x, panel.y + u * 0.8f), "UPGRADES", Mathf.RoundToInt(u * 0.6f), Color.white);
        HudDraw.Text(new Vector2(panel.center.x, panel.y + u * 1.55f), $"${taxi.Money}", Mathf.RoundToInt(u * 0.42f), Gold);

        bool flashing = Time.unscaledTime < flashUntil;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (i == selected) HudDraw.Box(row, new Color(1f, 1f, 1f, 0.07f));

            int lvl = level[i];
            bool max = lvl >= MaxLevel;
            float textX = row.x + u * 2.2f;
            HudDraw.Text(new Vector2(textX, row.y + u * 0.5f), Names[i], Mathf.RoundToInt(u * 0.45f), Color.white);
            string value = max ? Value(i, lvl) : $"{Value(i, lvl)} → {Value(i, lvl + 1)}";
            HudDraw.Text(new Vector2(textX, row.y + u * 1.02f), value, Mathf.RoundToInt(u * 0.28f), new Color(1f, 1f, 1f, 0.7f));

            // One bar per level, lit up to the current level.
            float pw = u * 0.32f, gap = u * 0.22f, total = MaxLevel * pw + (MaxLevel - 1) * gap;
            float pipsCentre = (row.x + u * 4.5f + buys[i].x) / 2f;
            for (int k = 0; k < MaxLevel; k++)
            {
                var pip = new Rect(pipsCentre - total / 2f + k * (pw + gap), row.center.y - u * 0.4f, pw, u * 0.8f);
                HudDraw.Draw(pip, HudDraw.Bar, k < lvl ? Gold : new Color(1f, 1f, 1f, 0.15f));
            }

            bool afford = !max && taxi.Money >= Prices[lvl];
            Color fill = max ? new Color(1f, 1f, 1f, 0.06f)
                : afford ? new Color(0.2f, 0.7f, 0.32f, 0.95f)
                : new Color(1f, 1f, 1f, 0.12f);
            if (flashing && flashRow == i) fill = flashOk ? new Color(0.45f, 1f, 0.55f, 1f) : new Color(0.85f, 0.2f, 0.2f, 0.95f);
            HudDraw.Box(buys[i], fill);
            Color textColor = max ? new Color(1f, 1f, 1f, 0.45f) : afford ? Color.white : new Color(1f, 0.55f, 0.5f);
            HudDraw.Text(buys[i].center, max ? "MAX" : $"${Prices[lvl]}", Mathf.RoundToInt(u * 0.42f), textColor);
        }

        if (damage != null && damage.CurrentRepairCost > 0)
        {
            int rCost = damage.Wrecked ? damage.fullRepairCost : damage.CurrentRepairCost;
            bool canRepair = taxi.Money >= rCost || damage.Wrecked;
            Color rFill = canRepair ? new Color(0.22f, 0.56f, 0.85f, 0.95f) : new Color(1f, 1f, 1f, 0.12f);
            HudDraw.Box(repair, rFill);
            string rLabel = taxi.Money >= rCost ? $"REPAIR  ${rCost}" : (damage.Wrecked ? "FREE TOW" : $"REPAIR  ${rCost}");
            HudDraw.Text(repair.center, rLabel, Mathf.RoundToInt(u * 0.36f), canRepair ? Color.white : new Color(1f, 0.55f, 0.5f));
        }

        HudDraw.Box(done, new Color(1f, 1f, 1f, 0.18f));
        HudDraw.Text(done.center, "DONE", Mathf.RoundToInt(u * 0.42f), Color.white);
    }

    static readonly Color Gold = new(1f, 0.82f, 0.2f);
}
