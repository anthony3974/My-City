using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Round minimap in the top-left corner, turned so the camera's view is up. Drawn every frame into a small
/// texture straight from the road grid (roads, sidewalks, buildings shaded by height, parks), so it needs no
/// second camera. Markers: traffic (grey), fares (green), the drop-off (yellow, pinned to the rim when off
/// the map), north, and the taxi in the middle.
/// Creates itself at startup in any scene that has a CityStreamer.
/// </summary>
public class Minimap : MonoBehaviour
{
    [Tooltip("Metres from the middle of the map to its rim.")]
    public float range = 170f;
    public int resolution = 160;

    static readonly Color32 Road = new(38, 42, 52, 255), Sidewalk = new(78, 84, 98, 255);
    static readonly Color32 LowRise = new(122, 128, 142, 255), Downtown = new(176, 182, 198, 255);
    static readonly Color32 Park = new(78, 140, 80, 255);
    static readonly Color FareColor = new(0.3f, 1f, 0.4f), DropColor = new(1f, 0.82f, 0.15f);

    const float Rim = 0.9f;   // markers off the map sit this far out (share of the radius)

    CityStreamer city;
    Transform car;
    TaxiGame taxi;
    Pedestrians people;
    Traffic traffic;
    Camera cam;

    Texture2D map;
    Color32[] pixels;
    Color32[] blocks;   // colour of each block near the car, for this frame
    readonly Dictionary<Vector2Int, Color32> blockCache = new();
    readonly List<Vector3> points = new();
    Vector3 fwd = Vector3.forward, right = Vector3.right;
    float camYaw;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        var streamer = FindFirstObjectByType<CityStreamer>();
        if (streamer == null || streamer.car == null) return;
        var m = new GameObject("Minimap").AddComponent<Minimap>();
        m.city = streamer;
        m.car = streamer.car;
    }

    void Start()
    {
        map = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.DontSave,
        };
        pixels = new Color32[resolution * resolution];
    }

    void OnDestroy() => Destroy(map);

    void LateUpdate()
    {
        if (car == null) return;
        if (cam == null) cam = Camera.main;
        // Other HUD pieces create themselves at the same time as this, so look them up lazily.
        if (taxi == null) taxi = FindFirstObjectByType<TaxiGame>();
        if (people == null) people = FindFirstObjectByType<Pedestrians>();
        if (traffic == null) traffic = FindFirstObjectByType<Traffic>();

        camYaw = cam ? cam.transform.eulerAngles.y : car.eulerAngles.y;
        float a = camYaw * Mathf.Deg2Rad;
        fwd = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
        right = new Vector3(Mathf.Cos(a), 0f, -Mathf.Sin(a));
        Redraw();
    }

    // ------------------------------------------------------------------ The map texture

    void Redraw()
    {
        var s = city.settings;
        float pitch = s.Pitch, rw = s.roadWidth, sw = s.sidewalkWidth;
        Vector3 c = car.position;

        // Colours of the blocks the map can show (the corners of the square reach range * sqrt 2).
        float reach = range * 1.42f;
        int bx0 = Mathf.FloorToInt((c.x - reach) / pitch), bz0 = Mathf.FloorToInt((c.z - reach) / pitch);
        int n = Mathf.CeilToInt(2f * reach / pitch) + 2;
        if (blocks == null || blocks.Length < n * n) blocks = new Color32[n * n];
        var origin = city.OriginCell * s.blocksPerTile;   // world block index -> the generator's block index
        if (blockCache.Count > 4000) blockCache.Clear();
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
                blocks[z * n + x] = BlockColor(s, new Vector2Int(bx0 + x + origin.x, bz0 + z + origin.y));

        int N = resolution;
        float inv = 2f / N, edge = N * 0.5f;
        for (int py = 0; py < N; py++)
        {
            float v = (py + 0.5f) * inv - 1f;
            for (int px = 0; px < N; px++)
            {
                float u = (px + 0.5f) * inv - 1f;
                float r2 = u * u + v * v;
                int i = py * N + px;
                if (r2 >= 1f) { pixels[i] = default; continue; }

                float wx = c.x + (right.x * u + fwd.x * v) * range;
                float wz = c.z + (right.z * u + fwd.z * v) * range;
                int bx = Mathf.FloorToInt(wx / pitch), bz = Mathf.FloorToInt(wz / pitch);
                float lx = wx - bx * pitch, lz = wz - bz * pitch;   // 0..rw is road, then the block

                Color32 col;
                if (lx < rw || lz < rw) col = Road;
                else if (lx < rw + sw || lz < rw + sw || lx > pitch - sw || lz > pitch - sw) col = Sidewalk;
                else
                {
                    int k = (bz - bz0) * n + (bx - bx0);
                    col = k >= 0 && k < blocks.Length ? blocks[k] : LowRise;
                }

                // Soft edge at the rim.
                col.a = (byte)(Mathf.Clamp01((1f - Mathf.Sqrt(r2)) * edge) * 235f);
                pixels[i] = col;
            }
        }
        map.SetPixels32(pixels);
        map.Apply(false);
    }

    Color32 BlockColor(CitySettings s, Vector2Int b)
    {
        if (blockCache.TryGetValue(b, out var col)) return col;
        col = CityGenerator.IsPark(s, b.x, b.y)
            ? Park
            : Color32.Lerp(LowRise, Downtown, CityGenerator.Downtown(s, b.x, b.y));
        blockCache[b] = col;
        return col;
    }

    // ------------------------------------------------------------------ Drawing

    /// <summary>Screen position of a world point on the map. Points past the rim are pinned to it.</summary>
    Vector2 ToMap(Vector3 w, Vector2 center, float radius, out bool onMap)
    {
        Vector3 rel = w - car.position;
        var p = new Vector2(Vector3.Dot(rel, right), -Vector3.Dot(rel, fwd)) / range;   // GUI y is down
        float m = p.magnitude;
        onMap = m <= Rim;
        if (!onMap) p *= Rim / m;
        return center + p * radius;
    }

    void Dot(Vector2 p, float size, Color c, bool outline = true)
    {
        if (outline)
        {
            float o = size * 1.45f;
            HudDraw.Draw(new Rect(p.x - o / 2f, p.y - o / 2f, o, o), HudDraw.Disc, new Color(0f, 0f, 0f, 0.7f * c.a));
        }
        HudDraw.Draw(new Rect(p.x - size / 2f, p.y - size / 2f, size, size), HudDraw.Disc, c);
    }

    void OnGUI()
    {
        if (car == null || map == null || GameMenu.HideHud || Event.current.type != EventType.Repaint) return;

        var safe = Screen.safeArea;
        float shortSide = Mathf.Min(Screen.width, Screen.height);
        float size = shortSide * (TouchControls.Active ? 0.32f : 0.28f);
        float m = shortSide * 0.03f;
        var r = new Rect(safe.xMin + m, Screen.height - safe.yMax + m, size, size);
        Vector2 c = r.center;
        float R = size / 2f;

        float pad = size * 0.03f;
        HudDraw.Draw(new Rect(r.x - pad, r.y - pad, size + pad * 2f, size + pad * 2f), HudDraw.Disc, new Color(0f, 0f, 0f, 0.4f));
        GUI.DrawTexture(r, map);
        HudDraw.Draw(r, HudDraw.Ring, new Color(1f, 1f, 1f, 0.55f));

        // Traffic.
        if (traffic != null)
        {
            points.Clear();
            traffic.GetCars(points);
            foreach (var p in points)
            {
                var sp = ToMap(p, c, R, out bool on);
                if (on) Dot(sp, size * 0.03f, new Color(0.92f, 0.92f, 0.95f, 0.85f), outline: false);
            }
        }

        // Fares, or the drop-off once someone is in the taxi.
        float pulse = 1f + 0.15f * Mathf.Sin(Time.unscaledTime * 6f);
        if (taxi != null && taxi.TryGetDropOff(out Vector3 drop))
        {
            Dot(ToMap(drop, c, R, out _), size * 0.075f * pulse, DropColor);
        }
        else if (people != null)
        {
            points.Clear();
            people.GetFares(points);
            foreach (var p in points)
                Dot(ToMap(p, c, R, out bool on), size * (on ? 0.055f : 0.04f), FareColor);
        }

        // North.
        var north = c + new Vector2(right.z, -fwd.z) * R * 0.84f;   // world +Z, on the rim
        HudDraw.Text(north, "N", Mathf.RoundToInt(size * 0.075f), new Color(1f, 1f, 1f, 0.85f));

        // The taxi, turned the way it faces on the map.
        float heading = car.eulerAngles.y - camYaw;
        float a = size * 0.12f, o = a * 1.35f;
        HudDraw.DrawRotated(new Rect(c.x - o / 2f, c.y - o / 2f, o, o), HudDraw.Arrow, new Color(0f, 0f, 0f, 0.7f), heading, c);
        HudDraw.DrawRotated(new Rect(c.x - a / 2f, c.y - a / 2f, a, a), HudDraw.Arrow, new Color(1f, 0.82f, 0.1f), heading, c);
    }
}
