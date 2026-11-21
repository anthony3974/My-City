using UnityEngine;

/// <summary>
/// Soft-edged shapes for the HUD, made in code at first use (white, so GUI.color tints them), and helpers
/// for drawing them tinted and rotated with IMGUI.
/// </summary>
public static class HudDraw
{
    static Texture2D disc, ring, bar, needle, arrow;
    static GUIStyle label;

    public static Texture2D Disc => disc ? disc : disc = Make(128, 128, (x, y) => DiscAlpha(x, y, 64f, 0f));
    public static Texture2D Ring => ring ? ring : ring = Make(256, 256, (x, y) => DiscAlpha(x, y, 128f, 0.955f));

    /// <summary>Rounded bar, for the speedometer's lights.</summary>
    public static Texture2D Bar => bar ? bar : bar = Make(16, 48, (x, y) =>
    {
        // Distance outside a rounded rectangle, 1 pixel of soft edge.
        float r = 6f, dx = Mathf.Max(Mathf.Abs(x - 8f) - (8f - r), 0f), dy = Mathf.Max(Mathf.Abs(y - 24f) - (24f - r), 0f);
        return Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy));
    });

    /// <summary>Needle pointing up, pivot at the bottom middle: wide at the base, thin at the tip.</summary>
    public static Texture2D Needle => needle ? needle : needle = Make(32, 256, (x, y) =>
    {
        float half = Mathf.Lerp(6f, 1.2f, y / 256f);
        return Mathf.Clamp01(half - Mathf.Abs(x - 16f)) * Mathf.Clamp01((252f - y) / 6f);   // rounded-off tip
    });

    /// <summary>Chevron arrow pointing up.</summary>
    public static Texture2D Arrow => arrow ? arrow : arrow = Make(64, 64, (x, y) =>
        Coverage(x, y, new[] { new Vector2(32f, 60f), new Vector2(56f, 6f), new Vector2(32f, 20f), new Vector2(8f, 6f) }));

    public static GUIStyle Label
    {
        get
        {
            label ??= new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
                clipping = TextClipping.Overflow,
            };
            return label;
        }
    }

    public static void Draw(Rect r, Texture t, Color c)
    {
        var old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, t);
        GUI.color = old;
    }

    /// <summary>Draws a texture turned clockwise by angle degrees around pivot (screen pixels).</summary>
    public static void DrawRotated(Rect r, Texture t, Color c, float angle, Vector2 pivot)
    {
        var m = GUI.matrix;
        GUIUtility.RotateAroundPivot(angle, pivot);
        Draw(r, t, c);
        GUI.matrix = m;
    }

    /// <summary>Text centred on a point, with a soft drop shadow.</summary>
    public static void Text(Vector2 center, string text, int size, Color c)
    {
        var s = Label;
        s.fontSize = size;
        var r = new Rect(center.x - 200f, center.y - size, 400f, size * 2f);
        float o = Mathf.Max(1f, size / 14f);
        s.normal.textColor = new Color(0f, 0f, 0f, 0.6f * c.a);
        GUI.Label(new Rect(r.x + o, r.y + o, r.width, r.height), text, s);
        s.normal.textColor = c;
        GUI.Label(r, text, s);
    }

    // ------------------------------------------------------------------ Texture making

    static float DiscAlpha(float x, float y, float r, float inner)
    {
        float d = Mathf.Sqrt((x - r) * (x - r) + (y - r) * (y - r));
        float a = Mathf.Clamp01(r - 1f - d);
        if (inner > 0f) a *= Mathf.Clamp01(d - inner * r);
        return a;
    }

    /// <summary>Share of the pixel inside the polygon, from 4x4 samples.</summary>
    static float Coverage(float x, float y, Vector2[] poly)
    {
        int inside = 0;
        for (int sy = 0; sy < 4; sy++)
            for (int sx = 0; sx < 4; sx++)
                if (Inside(new Vector2(x - 0.5f + (sx + 0.5f) / 4f, y - 0.5f + (sy + 0.5f) / 4f), poly)) inside++;
        return inside / 16f;
    }

    static bool Inside(Vector2 p, Vector2[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                inside = !inside;
        return inside;
    }

    /// <summary>White texture whose alpha comes from f(x, y), sampled at pixel centres. Row 0 is the bottom.</summary>
    static Texture2D Make(int w, int h, System.Func<float, float, float> f)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.DontSave,
        };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = new Color32(255, 255, 255, (byte)(f(x + 0.5f, y + 0.5f) * 255f));
        t.SetPixels32(px);
        t.Apply(false, true);
        return t;
    }
}
