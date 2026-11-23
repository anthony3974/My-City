using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

/// <summary>
/// In-game gameplay & playtest metrics (rolling 5-minute window + session totals):
/// - Passengers delivered per 5 minutes
/// - Money earned per minute
/// - Average delivery time
/// - Distance driven and top speed reached
/// - Total and longest drift score (plus a live drift score popup while drifting)
/// - Crashes and damage taken
/// - Nearby and ahead traffic density
///
/// Toggle the live overlay with M (or F3), or by clicking/tapping the STATS button under UPGRADES.
/// Creates itself at startup in any scene that has a CityStreamer.
/// </summary>
public class GameMetrics : MonoBehaviour
{
    const float WindowSeconds = 300f;   // 5-minute rolling window

    static Rect toggleButton;

    /// <summary>True if TouchControls should ignore a touch at p (GUI coordinates, y down).</summary>
    public static bool Blocks(Vector2 p) => toggleButton.width > 0f && toggleButton.Contains(p);

    struct TimedEvent { public float time, value; }
    struct DensitySample { public float time; public int near100, near160, ahead; }

    CityStreamer city;
    Transform car;
    CarController controller;
    CarDamage damage;
    TaxiGame taxi;
    Traffic traffic;

    bool showPanel;
    float sessionStart;
    Vector3 lastPos;
    bool hasLastPos;

    // Session & rolling-window data.
    readonly List<TimedEvent> deliveries = new();
    readonly List<TimedEvent> earnings = new();
    readonly List<DensitySample> densitySamples = new();

    int totalDeliveries, totalEarned, totalCrashes;
    float deliveryTimeSum, distanceMetres, topSpeedKmh, totalDamage;

    // Drift score tracking.
    float currentDriftScore, currentDriftTime, driftGrace;
    float totalDriftScore, longestDriftScore, longestDriftTime;
    float driftBankedAmount, driftBankedUntil;
    bool driftBroken;

    // Traffic density sampling.
    float nextSampleTime;
    int curNear100, curNear160, curAhead;
    float avgNear100, avgAhead;
    float dryAheadSeconds;   // how long straight ahead has had 0 traffic cars

    static readonly Color Gold = new(1f, 0.82f, 0.2f);
    static readonly Color Good = new(0.38f, 0.96f, 0.48f);
    static readonly Color Warn = new(1f, 0.65f, 0.2f);
    static readonly Color Dim = new(0.78f, 0.82f, 0.88f, 0.9f);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        var streamer = FindFirstObjectByType<CityStreamer>();
        if (streamer == null || streamer.car == null) return;
        var m = new GameObject("Game Metrics").AddComponent<GameMetrics>();
        m.city = streamer;
        m.car = streamer.car;
    }

    void Start()
    {
        sessionStart = Time.time;
        controller = car.GetComponent<CarController>();
        damage = car.GetComponent<CarDamage>();
        if (city != null) city.OriginShifted += OnOriginShifted;
        if (damage != null) damage.Damaged += OnDamaged;
        lastPos = car.position;
        hasLastPos = true;
    }

    void OnDestroy()
    {
        toggleButton = default;
        if (city != null) city.OriginShifted -= OnOriginShifted;
        if (damage != null) damage.Damaged -= OnDamaged;
        if (taxi != null) taxi.Delivered -= OnDelivered;
    }

    void OnOriginShifted(Vector3 delta)
    {
        lastPos += delta;
    }

    void OnDelivered(int pay, float duration)
    {
        float now = Time.time;
        totalDeliveries++;
        totalEarned += pay;
        deliveryTimeSum += duration;
        deliveries.Add(new TimedEvent { time = now, value = duration });
        earnings.Add(new TimedEvent { time = now, value = pay });
    }

    void OnDamaged(float amount, bool isCrash)
    {
        totalDamage += amount;
        if (isCrash)
        {
            totalCrashes++;
            // A hard crash during a drift breaks the active drift combo.
            if (currentDriftScore > 25f)
            {
                currentDriftScore = 0f;
                currentDriftTime = 0f;
                driftBroken = true;
                driftBankedUntil = Time.unscaledTime + 1.4f;
            }
        }
    }

    void Update()
    {
        if (car == null) return;

        // Lazily attach to components created in parallel during AfterSceneLoad.
        if (taxi == null)
        {
            taxi = FindFirstObjectByType<TaxiGame>();
            if (taxi != null) taxi.Delivered += OnDelivered;
        }
        if (traffic == null) traffic = FindFirstObjectByType<Traffic>();

        LayoutButton();
        ReadToggleInput();

        if (UpgradeMenu.Open) return;

        float dt = Time.deltaTime;
        float now = Time.time;

        // Distance driven and top speed.
        if (hasLastPos)
        {
            float step = Shapes.Flat(car.position - lastPos).magnitude;
            if (step < 50f) distanceMetres += step;   // ignore reset/teleport jumps
        }
        lastPos = car.position;
        hasLastPos = true;

        if (controller != null)
        {
            float kmh = Mathf.Abs(controller.ForwardSpeed) * 3.6f;
            if (kmh > topSpeedKmh) topSpeedKmh = kmh;
            UpdateDrift(dt);
        }

        // Sample traffic density every 0.5s.
        if (traffic != null && now >= nextSampleTime)
        {
            nextSampleTime = now + 0.5f;
            curNear100 = traffic.CountWithin(100f);
            curNear160 = traffic.CountWithin(160f);
            curAhead = traffic.CountAhead(160f, 35f);
            densitySamples.Add(new DensitySample { time = now, near100 = curNear100, near160 = curNear160, ahead = curAhead });

            if (controller != null && controller.ForwardSpeed > 8f && curAhead == 0) dryAheadSeconds += 0.5f;
            else dryAheadSeconds = 0f;

            PruneWindow(now);
            RecomputeDensityAverages();
        }
    }

    void UpdateDrift(float dt)
    {
        if (controller.IsDrifting && (damage == null || !damage.Wrecked))
        {
            driftGrace = 0.45f;
            driftBroken = false;
            float speed = Mathf.Abs(controller.ForwardSpeed);
            float angleFactor = Mathf.Clamp(controller.DriftAngle / 25f, 0.5f, 2.2f);
            currentDriftScore += speed * angleFactor * 8f * dt;
            currentDriftTime += dt;
        }
        else if (currentDriftScore > 0f)
        {
            driftGrace -= dt;
            if (driftGrace <= 0f)
            {
                if (currentDriftScore >= 20f)
                {
                    totalDriftScore += currentDriftScore;
                    if (currentDriftScore > longestDriftScore)
                    {
                        longestDriftScore = currentDriftScore;
                        longestDriftTime = currentDriftTime;
                    }
                    driftBankedAmount = currentDriftScore;
                    driftBroken = false;
                    driftBankedUntil = Time.unscaledTime + 1.8f;
                }
                currentDriftScore = 0f;
                currentDriftTime = 0f;
            }
        }
    }

    void PruneWindow(float now)
    {
        float cutoff = now - WindowSeconds;
        while (deliveries.Count > 0 && deliveries[0].time < cutoff) deliveries.RemoveAt(0);
        while (earnings.Count > 0 && earnings[0].time < cutoff) earnings.RemoveAt(0);
        while (densitySamples.Count > 0 && densitySamples[0].time < cutoff) densitySamples.RemoveAt(0);
    }

    void RecomputeDensityAverages()
    {
        if (densitySamples.Count == 0) { avgNear100 = avgAhead = 0f; return; }
        float s100 = 0f, sAhead = 0f;
        for (int i = 0; i < densitySamples.Count; i++)
        {
            s100 += densitySamples[i].near100;
            sAhead += densitySamples[i].ahead;
        }
        avgNear100 = s100 / densitySamples.Count;
        avgAhead = sAhead / densitySamples.Count;
    }

    void LayoutButton()
    {
        var up = UpgradeMenu.OpenButtonRect;
        if (up.width > 0f)
        {
            float gap = up.height * 0.22f;
            toggleButton = new Rect(up.x, up.yMax + gap, up.width, up.height * 0.78f);
        }
        else
        {
            var safe = Screen.safeArea;
            float s = Mathf.Min(Screen.width, Screen.height);
            float m = s * 0.05f, w = s * 0.32f, h = s * 0.062f;
            toggleButton = new Rect(safe.xMax - m - w, Screen.height - safe.yMax + m + s * 0.1f, w, h);
        }
    }

    void ReadToggleInput()
    {
        var kb = Keyboard.current;
        if (kb != null && (kb.mKey.wasPressedThisFrame || kb.f3Key.wasPressedThisFrame))
        {
            showPanel = !showPanel;
            return;
        }

        if (UpgradeMenu.Open) return;

        if (TouchControls.Active)
        {
            foreach (var t in Touch.activeTouches)
            {
                if (t.phase != TouchPhase.Began) continue;
                Vector2 p = new(t.screenPosition.x, Screen.height - t.screenPosition.y);
                if (toggleButton.Contains(p)) { showPanel = !showPanel; return; }
            }
        }
        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            Vector2 p = new(mouse.position.ReadValue().x, Screen.height - mouse.position.ReadValue().y);
            if (toggleButton.Contains(p)) showPanel = !showPanel;
        }
    }

    void OnGUI()
    {
        if (car == null || Event.current.type != EventType.Repaint) return;

        // Live drift score popup (bottom-centre above touch/speedo area).
        if (!UpgradeMenu.Open)
            DrawDriftBanner();

        if (UpgradeMenu.Open) return;

        // Compact STATS toggle button tucked under UPGRADES.
        if (toggleButton.width > 0f)
        {
            Color bg = showPanel ? new Color(0.22f, 0.55f, 0.85f, 0.55f) : new Color(1f, 1f, 1f, 0.16f);
            HudDraw.Box(toggleButton, bg);
            string label = TouchControls.Active ? "STATS" : "STATS (M)";
            HudDraw.Text(toggleButton.center, label, Mathf.RoundToInt(toggleButton.height * 0.44f), Color.white);
        }

        if (!showPanel) return;

        DrawMetricsPanel();
    }

    void DrawDriftBanner()
    {
        float shortSide = Mathf.Min(Screen.width, Screen.height);
        float y = Screen.height * (TouchControls.Active ? 0.58f : 0.78f);
        int size = Mathf.RoundToInt(Mathf.Max(22f, shortSide * 0.042f));

        if (currentDriftScore >= 15f)
        {
            HudDraw.Text(new Vector2(Screen.width / 2f, y),
                $"DRIFT  +{Mathf.RoundToInt(currentDriftScore)}", size, Gold);
        }
        else if (Time.unscaledTime < driftBankedUntil)
        {
            string msg = driftBroken ? "DRIFT LOST" : $"DRIFT  +{Mathf.RoundToInt(driftBankedAmount)}";
            Color col = driftBroken ? Warn : Good;
            HudDraw.Text(new Vector2(Screen.width / 2f, y), msg, Mathf.RoundToInt(size * 0.9f), col);
        }
    }

    void DrawMetricsPanel()
    {
        float now = Time.time;
        PruneWindow(now);
        float elapsed = Mathf.Max(1f, now - sessionStart);
        float window = Mathf.Min(elapsed, WindowSeconds);

        float faresPer5m = deliveries.Count * (300f / window);
        float moneyWin = 0f;
        for (int i = 0; i < earnings.Count; i++) moneyWin += earnings[i].value;
        float moneyPerMin = moneyWin / (window / 60f);
        string avgDelivery = totalDeliveries > 0 ? $"{deliveryTimeSum / totalDeliveries:0.0} s" : "--";
        string distText = distanceMetres >= 1000f ? $"{distanceMetres / 1000f:0.00} km" : $"{distanceMetres:0} m";

        var safe = Screen.safeArea;
        float s = Mathf.Min(Screen.width, Screen.height);
        float panelW = Mathf.Min(safe.width * 0.44f, Mathf.Max(260f, s * 0.56f));
        float rowH = Mathf.Max(20f, s * 0.032f);
        int font = Mathf.RoundToInt(rowH * 0.58f);
        float pad = rowH * 0.55f;
        const int rowCount = 9;
        float panelH = pad * 2f + rowH * (rowCount + 1.2f);

        float x = toggleButton.width > 0f ? toggleButton.xMax - panelW : safe.xMax - s * 0.03f - panelW;
        float y = toggleButton.width > 0f ? toggleButton.yMax + s * 0.015f : Screen.height - safe.yMax + s * 0.16f;
        // Clamp so the panel never overlaps the bottom-right speedometer.
        float maxBottom = Screen.height - safe.yMin - s * 0.31f;
        if (y + panelH > maxBottom) y = Mathf.Max(Screen.height - safe.yMax + s * 0.02f, maxBottom - panelH);

        var rect = new Rect(x, y, panelW, panelH);
        HudDraw.Box(rect, new Color(0.06f, 0.08f, 0.11f, 0.84f));

        float cy = rect.y + pad;
        HudDraw.TextInRect(new Rect(rect.x + pad, cy, panelW - pad * 2f, rowH * 1.1f),
            $"METRICS  ({Mathf.FloorToInt(window / 60f)}m {Mathf.FloorToInt(window % 60f):00}s)",
            Mathf.RoundToInt(font * 1.08f), Gold, TextAnchor.MiddleLeft);
        cy += rowH * 1.2f;

        void Row(string label, string val, Color? valColor = null)
        {
            var r = new Rect(rect.x + pad, cy, panelW - pad * 2f, rowH);
            HudDraw.TextInRect(r, label, font, Dim, TextAnchor.MiddleLeft);
            HudDraw.TextInRect(r, val, font, valColor ?? Color.white, TextAnchor.MiddleRight);
            cy += rowH;
        }

        Row("Fares / 5 min", $"{faresPer5m:0.0}  (total {totalDeliveries})", Good);
        Row("Money / min", $"${moneyPerMin:0.0}  (total ${totalEarned})", Gold);
        Row("Avg delivery", avgDelivery);
        Row("Distance", distText);
        Row("Top speed", $"{topSpeedKmh:0} km/h");
        Row("Drift total", $"{Mathf.RoundToInt(totalDriftScore):N0}");
        Row("Longest drift", longestDriftScore > 0f ? $"{Mathf.RoundToInt(longestDriftScore):N0}  ({longestDriftTime:0.0}s)" : "--");
        Row("Crashes / dmg", $"{totalCrashes}  /  {Mathf.RoundToInt(totalDamage)} hp", totalCrashes > 0 ? Warn : Color.white);

        // Nearby traffic density & ahead corridor check (ties to item 20).
        Color trafficCol = curAhead == 0 && dryAheadSeconds >= 2f ? Warn : Good;
        Row("Traffic near / ahead", $"{curNear100} (avg {avgNear100:0.0})  /  {curAhead} ahead", trafficCol);
    }
}
