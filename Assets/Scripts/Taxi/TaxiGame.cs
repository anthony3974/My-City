using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Taxi job. People with a green light over their head want a ride: stop next to one and they get in. Then
/// drive to the yellow beacon a few blocks away and stop there to drop them off and get paid. An arrow above
/// the car points to the nearest fare, or to the drop-off once someone is in. Money is kept in PlayerPrefs.
/// Creates itself at startup in any scene that has a CityStreamer, and adds Pedestrians.
/// </summary>
public class TaxiGame : MonoBehaviour
{
    public int farePay = 25;
    [Tooltip("Drop-offs are this many blocks away along the streets (min, max).")]
    public Vector2Int tripBlocks = new(4, 8);
    public float pickupRadius = 10f;
    public float dropRadius = 8f;
    [Tooltip("The car has to be slower than this (m/s) to pick up or drop off.")]
    public float stopSpeed = 4f;
    public float arrowHeight = 2.6f;
    public Color dropColor = new(1f, 0.8f, 0.15f, 0.55f);
    public Color fareArrowColor = new(0.3f, 1f, 0.4f, 0.9f);

    const string MoneyKey = "Taxi.Money", TripsKey = "Taxi.Trips";
    const float DropInset = -2f;   // drop-off point is 2 m out into the road from the curb

    CityStreamer city;
    Transform car;
    Rigidbody body;
    PlayerCar look;   // null if the scene uses some other car
    Pedestrians people;

    bool passenger;
    Vector2Int dropBlock;
    int dropSide, money, trips;
    float paidUntil;

    Material glow;
    MaterialPropertyBlock props;
    Mesh beaconMesh, arrowMesh;
    Renderer beacon, arrow;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        var streamer = FindFirstObjectByType<CityStreamer>();
        if (streamer == null || streamer.car == null) return;
        var game = new GameObject("Taxi Game").AddComponent<TaxiGame>();
        game.city = streamer;
        game.car = streamer.car;
    }

    void Start()
    {
        money = PlayerPrefs.GetInt(MoneyKey, 0);
        trips = PlayerPrefs.GetInt(TripsKey, 0);
        body = car.GetComponent<Rigidbody>();
        city.OriginShifted += OnOriginShifted;

        look = car.GetComponent<PlayerCar>();

        // In Resources so its shader is always in the build (URP strips shaders no scene material uses).
        glow = Resources.Load<Material>("Glow");
        if (glow == null) Debug.LogWarning("TaxiGame: Resources/Glow material is missing, no beacons, lights or arrow.");
        beaconMesh = BeaconMesh();
        arrowMesh = ArrowMesh();
        props = new MaterialPropertyBlock();

        if (glow != null)
        {
            beacon = Visual("Drop-off Beacon", beaconMesh, new Vector3(4f, 120f, 4f));
            arrow = Visual("Taxi Arrow", arrowMesh, Vector3.one);
            beacon.enabled = false;
        }

        people = gameObject.AddComponent<Pedestrians>();
        people.Init(city, car, look != null ? look.Paint : city.materials[(int)CityMat.WhitePaint], glow, beaconMesh);

        var controller = car.GetComponent<CarController>();
        if (controller != null) gameObject.AddComponent<UpgradeMenu>().Init(this, controller);
    }

    void OnDestroy()
    {
        if (city != null) city.OriginShifted -= OnOriginShifted;
        Destroy(beaconMesh);
        Destroy(arrowMesh);
    }

    void OnOriginShifted(Vector3 delta)
    {
        float pitch = city.settings.Pitch;
        dropBlock += new Vector2Int(Mathf.RoundToInt(delta.x / pitch), Mathf.RoundToInt(delta.z / pitch));
    }

    // ------------------------------------------------------------------ Game logic

    float Speed => body ? body.linearVelocity.magnitude : 0f;

    public int Money => money;

    /// <summary>Takes cost from the money if there is enough (for the upgrade menu).</summary>
    public bool Spend(int cost)
    {
        if (money < cost) return false;
        money -= cost;
        PlayerPrefs.SetInt(MoneyKey, money);
        PlayerPrefs.Save();
        return true;
    }

    /// <summary>For the minimap: true and the drop-off point while someone is in the taxi.</summary>
    public bool TryGetDropOff(out Vector3 point)
    {
        point = passenger && people != null ? DropPoint() : default;
        return passenger && people != null;
    }

    Vector3 DropPoint()
    {
        float len = people.SideLength(DropInset);
        Vector3 p = people.SidewalkPoint(dropBlock, dropSide * len + len / 2f, DropInset, out _, out _);
        p.y = 0f;
        return p;
    }

    static float FlatDistance(Vector3 a, Vector3 b) => Shapes.Flat(a - b).magnitude;

    void Update()
    {
        if (people == null || car == null) return;
        bool stopped = Speed < stopSpeed;

        if (!passenger)
        {
            if (stopped && people.TryPickUp(car.position, pickupRadius))
            {
                passenger = true;
                PickDropOff();
                if (look != null) look.SetFree(false);
            }
        }
        else if (stopped && FlatDistance(car.position, DropPoint()) < dropRadius)
        {
            passenger = false;
            if (look != null) look.SetFree(true);
            // The passenger gets out on the sidewalk next to the drop-off and walks away.
            float len = people.SideLength(Pedestrians.WalkInset);
            people.DropOff(dropBlock, dropSide * len + len / 2f);

            money += farePay;
            trips++;
            PlayerPrefs.SetInt(MoneyKey, money);
            PlayerPrefs.SetInt(TripsKey, trips);
            PlayerPrefs.Save();
            paidUntil = Time.unscaledTime + 2.5f;
        }
    }

    void PickDropOff()
    {
        // Exactly n blocks away counting along the streets, in a random direction.
        Vector2Int from = people.BlockAt(car.position);
        int n = Random.Range(tripBlocks.x, tripBlocks.y + 1);
        int dx = Random.Range(-n, n + 1);
        int dz = (n - Mathf.Abs(dx)) * (Random.value < 0.5f ? -1 : 1);
        dropBlock = from + new Vector2Int(dx, dz);
        dropSide = Random.Range(0, 4);
    }

    // ------------------------------------------------------------------ Visuals

    void LateUpdate()
    {
        if (beacon == null || car == null) return;

        beacon.enabled = passenger;
        Vector3 target;
        Color c;
        if (passenger)
        {
            target = DropPoint();
            beacon.transform.position = target;
            Tint(beacon, dropColor);
            c = dropColor;
        }
        else if (people.NearestFare(car.position, out target))
        {
            c = fareArrowColor;
        }
        else
        {
            arrow.enabled = false;
            return;
        }

        // The arrow floats level above the car and points straight at the target.
        arrow.enabled = true;
        Vector3 pos = car.position + Vector3.up * arrowHeight;
        Vector3 dir = target - car.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.01f) arrow.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir));
        else arrow.transform.position = pos;
        c.a = 0.9f;
        Tint(arrow, c);
    }

    void Tint(Renderer r, Color c)
    {
        props.SetColor("_BaseColor", c);
        r.SetPropertyBlock(props);
    }

    Renderer Visual(string name, Mesh mesh, Vector3 scale)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.localScale = scale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = glow;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        return mr;
    }

    /// <summary>Open cylinder of radius 1 and height 1, solid at the bottom and fading out at the top (vertex alpha).</summary>
    public static Mesh BeaconMesh()
    {
        const int seg = 32;
        var verts = new Vector3[(seg + 1) * 2];
        var colors = new Color[verts.Length];
        var tris = new int[seg * 6];
        for (int i = 0; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            var ring = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            verts[i * 2] = ring;
            verts[i * 2 + 1] = ring + Vector3.up;
            colors[i * 2] = Color.white;
            colors[i * 2 + 1] = new Color(1f, 1f, 1f, 0f);
        }
        for (int i = 0; i < seg; i++)
        {
            int b = i * 2, t = i * 6;
            tris[t] = b; tris[t + 1] = b + 1; tris[t + 2] = b + 2;
            tris[t + 3] = b + 2; tris[t + 4] = b + 1; tris[t + 5] = b + 3;
        }
        return MakeMesh("Beacon", verts, colors, tris);
    }

    /// <summary>Flat arrow lying in the XZ plane, pointing along +Z.</summary>
    static Mesh ArrowMesh()
    {
        var verts = new[]
        {
            new Vector3(0f, 0f, 1.1f),                                          // tip
            new Vector3(-0.8f, 0f, 0.2f), new Vector3(0.8f, 0f, 0.2f),          // head corners
            new Vector3(-0.3f, 0f, 0.2f), new Vector3(0.3f, 0f, 0.2f),          // shaft top
            new Vector3(-0.3f, 0f, -0.9f), new Vector3(0.3f, 0f, -0.9f),        // shaft bottom
        };
        var colors = new Color[verts.Length];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
        var tris = new[] { 0, 2, 1, 3, 4, 5, 5, 4, 6 };
        return MakeMesh("Arrow", verts, colors, tris);
    }

    static Mesh MakeMesh(string name, Vector3[] verts, Color[] colors, int[] tris)
    {
        var m = new Mesh { name = name, vertices = verts, colors = colors, triangles = tris };
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    // ------------------------------------------------------------------ HUD

    void OnGUI()
    {
        if (people == null || car == null || Event.current.type != EventType.Repaint) return;

        string status;
        bool fast = Speed >= stopSpeed;
        if (passenger)
        {
            float d = FlatDistance(car.position, DropPoint());
            status = d < dropRadius && fast ? "Stop to drop off" : $"Drop off  {d:0} m";
        }
        else if (people.NearestFare(car.position, out Vector3 fare))
        {
            float d = FlatDistance(car.position, fare);
            status = d < pickupRadius && fast ? "Stop to pick up" : $"Find a fare (green light)  {d:0} m";
        }
        else status = "No fares nearby";

        var safe = Screen.safeArea;
        float m = Mathf.Min(Screen.width, Screen.height) * 0.05f;
        float y = Screen.height - safe.yMax + m;
        y = Line(y, $"${money}", Mathf.Max(36, Screen.height / 18));
        y = Line(y, status, Mathf.Max(26, Screen.height / 28));
        if (Time.unscaledTime < paidUntil) Line(y, $"+${farePay}", Mathf.Max(26, Screen.height / 28), new Color(0.4f, 1f, 0.45f));
    }

    /// <summary>One line of centred text below y; returns the y of the next line.</summary>
    static float Line(float y, string text, int size, Color? color = null)
    {
        float h = size * 1.3f;
        HudDraw.Text(new Vector2(Screen.width / 2f, y + h / 2f), text, size, color ?? Color.white);
        return y + h;
    }
}
