using UnityEngine;

/// <summary>
/// Round speedometer: a ring of lights that fill up with speed (blue to green to yellow, red past the redline),
/// a needle, the speed in big digits and an R when reversing. Bottom-centre on phones (between the touch
/// buttons), bottom-right otherwise.
/// Creates itself at startup in any scene that has a CityStreamer.
/// </summary>
public class Speedometer : MonoBehaviour
{
    public float maxKmh = 140f;
    public float redlineKmh = 120f;
    public int lights = 40;

    const float Sweep = 270f;   // degrees from 0 to max, centred on straight up

    CarController car;
    float shown;   // smoothed km/h for the needle

    static readonly Color Cool = new(0.2f, 0.85f, 1f), Mid = new(0.35f, 1f, 0.5f), Hot = new(1f, 0.85f, 0.2f);
    static readonly Color Red = new(1f, 0.22f, 0.15f), NeedleColor = new(1f, 0.42f, 0.18f);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        var streamer = FindFirstObjectByType<CityStreamer>();
        if (streamer == null || streamer.car == null) return;
        var controller = streamer.car.GetComponent<CarController>();
        if (controller == null) return;
        var s = new GameObject("Speedometer").AddComponent<Speedometer>();
        s.car = controller;
    }

    void Update()
    {
        if (car == null) return;
        float kmh = Mathf.Abs(car.ForwardSpeed) * 3.6f;
        shown = Mathf.Lerp(shown, kmh, 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime));
    }

    Color LightColor(float kmh)
    {
        if (kmh >= redlineKmh) return Red;
        float t = kmh / redlineKmh;
        return t < 0.5f ? Color.Lerp(Cool, Mid, t * 2f) : Color.Lerp(Mid, Hot, (t - 0.5f) * 2f);
    }

    float Angle(float kmh) => -Sweep / 2f + Sweep * Mathf.Clamp01(kmh / maxKmh);

    void OnGUI()
    {
        if (car == null || GameMenu.HideHud || Event.current.type != EventType.Repaint) return;

        var safe = Screen.safeArea;   // y up; GUI is y down
        float shortSide = Mathf.Min(Screen.width, Screen.height);
        float size = shortSide * (TouchControls.Active ? 0.3f : 0.26f);
        float m = shortSide * 0.03f;
        float bottom = Screen.height - safe.yMin - m;
        float x = TouchControls.Active ? safe.center.x - size / 2f : safe.xMax - m - size;
        var r = new Rect(x, bottom - size, size, size);
        Vector2 c = r.center;
        float R = size / 2f;

        // Face.
        HudDraw.Draw(r, HudDraw.Disc, new Color(0.04f, 0.05f, 0.08f, 0.62f));
        HudDraw.Draw(r, HudDraw.Ring, new Color(1f, 1f, 1f, 0.22f));

        // Ring of lights. The ones past the redline stay faintly red when off.
        float jitter = shown > redlineKmh ? Mathf.Sin(Time.unscaledTime * 60f) * 0.6f : 0f;
        float bw = size * 0.034f, bh = size * 0.085f, lightR = R * 0.8f;
        for (int i = 0; i < lights; i++)
        {
            float kmh = (i + 0.5f) / lights * maxKmh;
            bool on = kmh <= shown;
            Color col = on ? LightColor(kmh)
                : kmh >= redlineKmh ? new Color(Red.r, Red.g, Red.b, 0.28f) : new Color(1f, 1f, 1f, 0.1f);
            var bar = new Rect(c.x - bw / 2f, c.y - lightR - bh / 2f, bw, bh);
            if (on)   // soft glow behind lit lights
                HudDraw.DrawRotated(new Rect(bar.x - bw * 0.6f, bar.y - bw * 0.6f, bw * 2.2f, bh + bw * 1.2f),
                    HudDraw.Bar, new Color(col.r, col.g, col.b, 0.25f), Angle(kmh), c);
            HudDraw.DrawRotated(bar, HudDraw.Bar, col, Angle(kmh), c);
        }

        // Numbers every 20 km/h.
        int fontSmall = Mathf.RoundToInt(size * 0.07f);
        for (int k = 0; k <= maxKmh + 0.1f; k += 20)
        {
            float a = Angle(k) * Mathf.Deg2Rad;
            var p = c + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * R * 0.58f;
            HudDraw.Text(p, k.ToString(), fontSmall, k >= redlineKmh ? Red : new Color(1f, 1f, 1f, 0.75f));
        }

        // Needle with a glow, then the hub.
        float na = Angle(shown) + jitter;
        float nl = R * 0.74f, nw = size * 0.05f;
        HudDraw.DrawRotated(new Rect(c.x - nw, c.y - nl - nw * 0.5f, nw * 2f, nl + nw * 0.5f), HudDraw.Needle,
            new Color(NeedleColor.r, NeedleColor.g, NeedleColor.b, 0.3f), na, c);
        HudDraw.DrawRotated(new Rect(c.x - nw / 2f, c.y - nl, nw, nl), HudDraw.Needle, NeedleColor, na, c);
        float hub = size * 0.11f;
        HudDraw.Draw(new Rect(c.x - hub / 2f, c.y - hub / 2f, hub, hub), HudDraw.Disc, new Color(0.08f, 0.09f, 0.12f, 1f));
        hub *= 0.5f;
        HudDraw.Draw(new Rect(c.x - hub / 2f, c.y - hub / 2f, hub, hub), HudDraw.Disc, NeedleColor);

        // Digital readout in the gap at the bottom of the dial.
        int kmhNow = Mathf.RoundToInt(Mathf.Abs(car.ForwardSpeed) * 3.6f);
        HudDraw.Text(c + new Vector2(0f, R * 0.4f), kmhNow.ToString(), Mathf.RoundToInt(size * 0.17f), Color.white);
        bool reversing = car.ForwardSpeed < -0.5f;
        HudDraw.Text(c + new Vector2(0f, R * 0.66f), reversing ? "R  km/h" : "km/h", fontSmall,
            reversing ? Red : new Color(1f, 1f, 1f, 0.6f));
    }
}
