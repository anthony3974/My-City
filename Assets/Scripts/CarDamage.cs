using System;
using UnityEngine;
using UnityEngine.InputSystem;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

/// <summary>
/// Taxi health, visible damage (bent bumpers, dark lights, scuffed paint, hood smoke and flames), a HUD
/// damage bar under the minimap, and repair/replacement when the car is wrecked.
/// Added by CarController. CrashFeedback feeds side impacts and wall scrapes into it.
/// </summary>
public class CarDamage : MonoBehaviour
{
    public float maxHealth = 100f;
    public int fullRepairCost = 20;
    public float impactCooldown = 0.25f;

    /// <summary>Fired whenever the car takes damage: (damageAmount, isCrashImpact).</summary>
    public event Action<float, bool> Damaged;

    static Rect repairButton;

    /// <summary>True if TouchControls should ignore a touch at p (GUI coordinates, y down).</summary>
    public static bool Blocks(Vector2 p) => repairButton.width > 0f && repairButton.Contains(p);

    public float Health { get; private set; }
    public float HealthRatio => maxHealth > 0f ? Mathf.Clamp01(Health / maxHealth) : 1f;
    public bool Wrecked => Health <= 0.01f;

    /// <summary>Engine output multiplier: full until 30% health, sputters slightly when critical, 0 when wrecked.</summary>
    public float EngineScale => Wrecked ? 0f : HealthRatio < 0.3f ? Mathf.Lerp(0.72f, 1f, HealthRatio / 0.3f) : 1f;

    /// <summary>Cost to repair the current missing health (0 if already pristine).</summary>
    public int CurrentRepairCost
    {
        get
        {
            float missing = 1f - HealthRatio;
            if (missing < 0.02f) return 0;
            return Mathf.Max(5, Mathf.RoundToInt(fullRepairCost * missing));
        }
    }

    PlayerCar look;
    CarController controller;
    TaxiGame taxi;
    ParticleSystem smoke;
    float frontDmg, rearDmg, leftDmg, rightDmg;
    float lastImpactTime = -10f;
    float smokeCarry;
    float repairedUntil;
    string repairNotice;

    static readonly Color BarGood = new(0.28f, 0.85f, 0.38f);
    static readonly Color BarWarn = new(0.98f, 0.78f, 0.16f);
    static readonly Color BarBad = new(0.95f, 0.22f, 0.16f);

    void Awake()
    {
        Health = maxHealth;
        look = GetComponent<PlayerCar>();
        controller = GetComponent<CarController>();
        smoke = CreateSmoke();
    }

    void OnDestroy()
    {
        repairButton = default;
        if (smoke != null) Destroy(smoke.gameObject);
    }

    /// <summary>Called by CrashFeedback on side collisions above its minImpact threshold.</summary>
    public void TakeImpact(float impact, float strength, Vector3 worldPoint)
    {
        if (Wrecked) return;
        if (Time.time - lastImpactTime < impactCooldown) return;
        lastImpactTime = Time.time;

        float amount = Mathf.Lerp(5f, 36f, strength);
        ApplyDamage(amount, worldPoint, isCrash: true);
    }

    /// <summary>Called by CrashFeedback while scraping along a wall.</summary>
    public void TakeScrape(float slideSpeed, float dt, Vector3 worldPoint)
    {
        if (Wrecked) return;
        float amount = Mathf.Clamp01(slideSpeed / 20f) * 6f * dt;
        if (amount > 0.01f) ApplyDamage(amount, worldPoint, isCrash: false);
    }

    void ApplyDamage(float amount, Vector3 worldPoint, bool isCrash)
    {
        float prev = Health;
        Health = Mathf.Max(0f, Health - amount);
        float dealt = prev - Health;
        if (dealt <= 0f) return;

        // Track which side took the hit so visible damage deforms the right part of the taxi.
        Vector3 local = transform.InverseTransformPoint(worldPoint);
        float zoneAmt = dealt / (maxHealth * 0.45f);
        if (local.z > 0.5f) frontDmg = Mathf.Clamp01(frontDmg + zoneAmt);
        else if (local.z < -0.5f) rearDmg = Mathf.Clamp01(rearDmg + zoneAmt);
        if (local.x < -0.35f) leftDmg = Mathf.Clamp01(leftDmg + zoneAmt);
        else if (local.x > 0.35f) rightDmg = Mathf.Clamp01(rightDmg + zoneAmt);

        if (look != null)
            look.ApplyDamageVisuals(HealthRatio, frontDmg, rearDmg, leftDmg, rightDmg);

        Damaged?.Invoke(dealt, isCrash);
    }

    /// <summary>Repairs or replaces the taxi. Returns true if repaired.</summary>
    public bool TryRepair(bool allowFreeIfBroke)
    {
        if (taxi == null) taxi = FindFirstObjectByType<TaxiGame>();
        int cost = Wrecked ? fullRepairCost : CurrentRepairCost;
        if (cost <= 0) return false;

        bool paid = taxi != null && taxi.Spend(cost);
        if (!paid)
        {
            if (!allowFreeIfBroke) return false;
            repairNotice = "Replaced taxi (free tow)";
        }
        else
        {
            repairNotice = Wrecked ? $"Taxi repaired (-${cost})" : $"Repaired (-${cost})";
        }

        bool wasWrecked = Wrecked;
        Health = maxHealth;
        frontDmg = rearDmg = leftDmg = rightDmg = 0f;
        if (look != null) look.ResetDamageVisuals();
        if (wasWrecked && controller != null) controller.ResetCar();
        repairedUntil = Time.unscaledTime + 2.5f;
        return true;
    }

    void Update()
    {
        if (taxi == null) taxi = FindFirstObjectByType<TaxiGame>();

        UpdateSmoke();
        UpdateWreckedInput();
    }

    void UpdateSmoke()
    {
        if (smoke == null) return;
        float ratio = HealthRatio;
        if (ratio >= 0.55f) return;

        // Emit hood smoke below 55% health, getting darker and denser, plus flames when wrecked.
        float severity = 1f - ratio / 0.55f;
        float rate = Mathf.Lerp(5f, 26f, severity);
        smokeCarry += rate * Time.deltaTime;
        int count = (int)smokeCarry;
        smokeCarry -= count;
        if (count <= 0) return;

        Vector3 hood = transform.TransformPoint(new Vector3(0f, 1.02f, 1.45f));
        var p = new ParticleSystem.EmitParams { applyShapeToPosition = false };
        for (int i = 0; i < count; i++)
        {
            bool flame = Wrecked && UnityEngine.Random.value < 0.35f;
            p.position = hood + UnityEngine.Random.insideUnitSphere * 0.25f;
            p.velocity = Vector3.up * UnityEngine.Random.Range(1.4f, 3.2f) + UnityEngine.Random.insideUnitSphere * 0.6f;
            p.startLifetime = flame ? UnityEngine.Random.Range(0.22f, 0.45f) : UnityEngine.Random.Range(0.6f, 1.25f);
            p.startSize = flame ? UnityEngine.Random.Range(0.16f, 0.32f) : UnityEngine.Random.Range(0.22f, 0.48f);
            float grey = Mathf.Lerp(0.75f, 0.15f, severity);
            p.startColor = flame
                ? Color.Lerp(new Color(1f, 0.8f, 0.2f), new Color(1f, 0.25f, 0.05f), UnityEngine.Random.value)
                : new Color(grey, grey, grey, 1f);
            smoke.Emit(p, 1);
        }
    }

    void UpdateWreckedInput()
    {
        if (!Wrecked || GameMenu.BlocksInput)
        {
            repairButton = default;
            return;
        }

        float s = Mathf.Min(Screen.width, Screen.height);
        float bw = s * 0.48f, bh = s * 0.11f;
        repairButton = new Rect(Screen.width / 2f - bw / 2f, Screen.height * 0.54f, bw, bh);

        var kb = Keyboard.current;
        var pad = Gamepad.current;
        bool key = (kb != null && (kb.rKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame))
            || (pad != null && (pad.buttonSouth.wasPressedThisFrame || pad.buttonNorth.wasPressedThisFrame));
        bool clicked = false;
        if (TouchControls.Active)
        {
            foreach (var t in Touch.activeTouches)
            {
                if (t.phase != TouchPhase.Began) continue;
                Vector2 p = new(t.screenPosition.x, Screen.height - t.screenPosition.y);
                if (repairButton.Contains(p)) { clicked = true; break; }
            }
        }
        var mouse = Mouse.current;
        if (!clicked && mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            Vector2 p = new(mouse.position.ReadValue().x, Screen.height - mouse.position.ReadValue().y);
            if (repairButton.Contains(p)) clicked = true;
        }

        if (key || clicked)
            TryRepair(allowFreeIfBroke: true);
    }

    void OnGUI()
    {
        if (GameMenu.HideHud || Event.current.type != EventType.Repaint) return;

        var safe = Screen.safeArea;
        float shortSide = Mathf.Min(Screen.width, Screen.height);
        float mapSize = shortSide * (TouchControls.Active ? 0.32f : 0.28f);
        float m = shortSide * 0.03f;
        float top = Screen.height - safe.yMax;

        // Damage bar neatly under the minimap (top-left).
        float barH = Mathf.Max(22f, shortSide * 0.036f);
        var bg = new Rect(safe.xMin + m, top + m + mapSize + shortSide * 0.02f, mapSize, barH);
        HudDraw.Box(bg, new Color(0.05f, 0.06f, 0.09f, 0.72f));

        float ratio = HealthRatio;
        float pad = 3f;
        float fillW = Mathf.Max(0f, (bg.width - pad * 2f) * ratio);
        Color col = ratio > 0.6f ? Color.Lerp(BarWarn, BarGood, (ratio - 0.6f) / 0.4f)
            : ratio > 0.3f ? Color.Lerp(BarBad, BarWarn, (ratio - 0.3f) / 0.3f)
            : BarBad;
        if (ratio < 0.3f && !Wrecked)
            col.a = 0.7f + 0.3f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 8f));
        if (fillW > 2f)
            HudDraw.Box(new Rect(bg.x + pad, bg.y + pad, fillW, bg.height - pad * 2f), col);

        string label = Wrecked ? "WRECKED" : $"CAR  {Mathf.CeilToInt(ratio * 100f)}%";
        HudDraw.Text(bg.center, label, Mathf.RoundToInt(barH * 0.54f), Color.white);

        if (Time.unscaledTime < repairedUntil && !string.IsNullOrEmpty(repairNotice))
        {
            HudDraw.Text(new Vector2(bg.center.x, bg.yMax + barH * 0.75f), repairNotice,
                Mathf.RoundToInt(barH * 0.56f), BarGood);
        }

        // Wrecked modal prompt with clickable/tappable repair or free replacement button.
        if (Wrecked && !GameMenu.BlocksInput && repairButton.width > 0f)
        {
            float pw = repairButton.width * 1.18f, ph = repairButton.height * 2.5f;
            var box = new Rect(Screen.width / 2f - pw / 2f, repairButton.y - repairButton.height * 1.15f, pw, ph);
            HudDraw.Box(box, new Color(0.08f, 0.07f, 0.09f, 0.92f));
            HudDraw.Text(new Vector2(box.center.x, box.y + repairButton.height * 0.55f),
                "TAXI WRECKED", Mathf.RoundToInt(repairButton.height * 0.52f), BarBad);

            bool canPay = taxi != null && taxi.Money >= fullRepairCost;
            string btnText = canPay
                ? (TouchControls.Active ? $"REPAIR  ${fullRepairCost}" : $"REPAIR  ${fullRepairCost}  (R)")
                : (TouchControls.Active ? "REPLACE TAXI (FREE)" : "REPLACE TAXI (R)");
            HudDraw.Box(repairButton, new Color(0.2f, 0.72f, 0.34f, 0.95f));
            HudDraw.Text(repairButton.center, btnText, Mathf.RoundToInt(repairButton.height * 0.4f), Color.white);
        }
    }

    static ParticleSystem CreateSmoke()
    {
        var mat = Resources.Load<Material>("CrashSparks");
        if (mat == null) return null;

        var go = new GameObject("Engine Smoke");
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = -0.12f;
        main.maxParticles = 120;

        var emission = ps.emission;
        emission.enabled = false;
        var shape = ps.shape;
        shape.enabled = false;

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.5f), new Keyframe(0.35f, 1.3f), new Keyframe(1f, 0f)));

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = mat;
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;

        ps.Play();
        return ps;
    }
}
