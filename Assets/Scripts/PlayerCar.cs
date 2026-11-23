using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The player's taxi, built in code by CityStreamer when the game starts: a yellow body with recessed wheel
/// wells, a black stripe, glass cabin, bumpers, lights, a roof light bar with placeholder text blocks (lit
/// while the taxi is free), and four wheels that steer and spin. Adds the Rigidbody, collider and
/// CarController (which adds CrashFeedback and CarDamage).
/// Materials are tinted copies of one city material, so no extra shaders are needed in the build.
/// Traffic borrows some of them by part name ("Cabin", "FrontBumper", "Tire", "Headlight", "Taillight").
/// </summary>
public class PlayerCar : MonoBehaviour
{
    static readonly Color PaintColor = new(1f, 0.76f, 0.05f);
    static readonly Color DamagedPaintColor = new(0.52f, 0.41f, 0.14f);
    static readonly Color SignFree = new(1f, 0.97f, 0.8f), SignTaken = new(0.3f, 0.3f, 0.3f);

    /// <summary>The yellow paint, also used as the base for the pedestrians' materials.</summary>
    public Material Paint { get; private set; }

    struct PartRest
    {
        public Transform t;
        public Vector3 pos;
        public Quaternion rot;
    }

    Material sign, trimMat, headlightMat, taillightMat;
    readonly List<Material> ownedMaterials = new();
    readonly List<Mesh> ownedMeshes = new();

    PartRest frontBumper, rearBumper, frontNose, rearTail, cabin, lightBar;
    Renderer[] headlights = new Renderer[2];
    Renderer[] taillights = new Renderer[2];

    public static PlayerCar Create(Material lit)
    {
        var go = new GameObject("Player Car");
        go.SetActive(false);   // CarController's Awake needs the wheels, so they go in first
        var car = go.AddComponent<PlayerCar>();
        car.Build(lit);
        go.SetActive(true);
        return car;
    }

    void OnDestroy()
    {
        foreach (var m in ownedMaterials) Destroy(m);
        foreach (var m in ownedMeshes) Destroy(m);
    }

    /// <summary>The roof light bar is lit while the taxi is free.</summary>
    public void SetFree(bool free) => sign.SetColor("_BaseColor", free ? SignFree : SignTaken);

    /// <summary>Updates visible body wear, bent bumpers, crooked light bar and broken lights.</summary>
    public void ApplyDamageVisuals(float healthRatio, float front, float rear, float left, float right)
    {
        float dmg = 1f - Mathf.Clamp01(healthRatio);
        if (Paint != null)
            Paint.SetColor("_BaseColor", Color.Lerp(PaintColor, DamagedPaintColor, dmg * 0.75f));

        float f = Mathf.Clamp01(front);
        float r = Mathf.Clamp01(rear);
        float sideTilt = Mathf.Clamp(right - left, -1f, 1f);

        if (frontBumper.t != null)
        {
            frontBumper.t.localPosition = frontBumper.pos + new Vector3(sideTilt * 0.04f * f, -0.14f * f, -0.14f * f);
            frontBumper.t.localRotation = frontBumper.rot * Quaternion.Euler(10f * f, sideTilt * 6f * f, (sideTilt >= 0f ? 1f : -1f) * 8f * f);
        }
        if (frontNose.t != null)
        {
            frontNose.t.localPosition = frontNose.pos + new Vector3(0f, -0.04f * f, -0.08f * f);
            frontNose.t.localRotation = frontNose.rot * Quaternion.Euler(5f * f, 0f, sideTilt * 3f * f);
        }
        if (rearBumper.t != null)
        {
            rearBumper.t.localPosition = rearBumper.pos + new Vector3(sideTilt * 0.04f * r, -0.14f * r, 0.14f * r);
            rearBumper.t.localRotation = rearBumper.rot * Quaternion.Euler(-10f * r, -sideTilt * 6f * r, -(sideTilt >= 0f ? 1f : -1f) * 8f * r);
        }
        if (rearTail.t != null)
        {
            rearTail.t.localPosition = rearTail.pos + new Vector3(0f, -0.03f * r, 0.06f * r);
            rearTail.t.localRotation = rearTail.rot * Quaternion.Euler(-4f * r, 0f, -sideTilt * 2.5f * r);
        }
        if (cabin.t != null)
        {
            float side = Mathf.Clamp01(Mathf.Max(left, right));
            cabin.t.localRotation = cabin.rot * Quaternion.Euler((f - r) * 1.8f, 0f, sideTilt * 3f * side);
        }
        if (lightBar.t != null)
        {
            float knock = Mathf.Clamp01((dmg - 0.35f) / 0.65f);
            lightBar.t.localPosition = lightBar.pos + new Vector3(sideTilt * 0.05f * knock, -0.02f * knock, 0f);
            lightBar.t.localRotation = lightBar.rot * Quaternion.Euler(4f * knock, 12f * knock, sideTilt * 9f * knock);
        }

        // Knock out headlights / taillights as front and rear take hits.
        if (headlights[0] != null) headlights[0].sharedMaterial = (f > 0.35f && left >= right * 0.6f) || f > 0.7f ? trimMat : headlightMat;
        if (headlights[1] != null) headlights[1].sharedMaterial = (f > 0.35f && right >= left * 0.6f) || f > 0.7f ? trimMat : headlightMat;
        if (taillights[0] != null) taillights[0].sharedMaterial = (r > 0.35f && left >= right * 0.6f) || r > 0.7f ? trimMat : taillightMat;
        if (taillights[1] != null) taillights[1].sharedMaterial = (r > 0.35f && right >= left * 0.6f) || r > 0.7f ? trimMat : taillightMat;
    }

    public void ResetDamageVisuals() => ApplyDamageVisuals(1f, 0f, 0f, 0f, 0f);

    void Build(Material lit)
    {
        Material M(Color c, float smoothness, string name)
        {
            var m = Shapes.Tinted(lit, c, smoothness, name);
            ownedMaterials.Add(m);
            return m;
        }
        Paint = M(PaintColor, 0.6f, "Taxi Paint");
        sign = M(SignFree, 0.5f, "Taxi Sign");
        var stripe = M(new Color(0.03f, 0.03f, 0.03f), 0.3f, "Taxi Stripe");
        var glass = M(new Color(0.1f, 0.12f, 0.15f), 0.9f, "Car Glass");
        trimMat = M(new Color(0.08f, 0.08f, 0.08f), 0.3f, "Car Trim");
        var tire = M(new Color(0.05f, 0.05f, 0.05f), 0.1f, "Car Tire");
        var rim = M(new Color(0.7f, 0.7f, 0.72f), 0.8f, "Car Rim");
        headlightMat = M(new Color(1f, 0.95f, 0.75f), 0.9f, "Car Headlight");
        taillightMat = M(new Color(0.9f, 0.05f, 0.05f), 0.9f, "Car Taillight");

        var body = new GameObject("Body").transform;
        body.SetParent(transform, false);

        // Chassis split around four recessed wheel wells (front z: 0.88..1.82, rear z: -1.82..-0.88, y: 0.425..0.805)
        // so the tires sit inside real arches and never clip the yellow body even when steering or compressed.
        // Underbody is inset on X/Y/Z so it never Z-fights with the outer yellow nose, tail, or wheel-well liners.
        Part(PrimitiveType.Cube, "Chassis", body, new Vector3(0f, 0.89f, 0f), new Vector3(1.9f, 0.17f, 4.2f), Paint);
        Part(PrimitiveType.Cube, "Underbody", body, new Vector3(0f, 0.61f, 0f), new Vector3(1.02f, 0.35f, 3.60f), trimMat);
        Part(PrimitiveType.Cube, "MidBody", body, new Vector3(0f, 0.615f, 0f), new Vector3(1.9f, 0.38f, 1.76f), Paint);
        frontNose = SaveRest(Part(PrimitiveType.Cube, "FrontNose", body, new Vector3(0f, 0.615f, 1.96f), new Vector3(1.9f, 0.38f, 0.28f), Paint).transform);
        rearTail = SaveRest(Part(PrimitiveType.Cube, "RearTail", body, new Vector3(0f, 0.615f, -1.96f), new Vector3(1.9f, 0.38f, 0.28f), Paint).transform);

        // Dark recessed wheel-well liners and subtle outer fender arch brows (combined into one mesh).
        AddCustomMesh("WheelWells", body, BuildWheelWellsMesh(), trimMat);

        cabin = SaveRest(Part(PrimitiveType.Cube, "Cabin", body, new Vector3(0f, 1.225f, -0.2f), new Vector3(1.65f, 0.5f, 2f), glass).transform);
        Part(PrimitiveType.Cube, "Roof", body, new Vector3(0f, 1.51f, -0.25f), new Vector3(1.72f, 0.08f, 1.8f), Paint);
        frontBumper = SaveRest(Part(PrimitiveType.Cube, "FrontBumper", body, new Vector3(0f, 0.5f, 2.12f), new Vector3(1.95f, 0.25f, 0.15f), trimMat).transform);
        rearBumper = SaveRest(Part(PrimitiveType.Cube, "RearBumper", body, new Vector3(0f, 0.5f, -2.12f), new Vector3(1.95f, 0.25f, 0.15f), trimMat).transform);

        for (int i = 0; i < 2; i++)
        {
            int s = i == 0 ? -1 : 1;
            headlights[i] = Part(PrimitiveType.Cube, "Headlight", body, new Vector3(0.62f * s, 0.84f, 2.11f), new Vector3(0.4f, 0.14f, 0.04f), headlightMat).GetComponent<Renderer>();
            taillights[i] = Part(PrimitiveType.Cube, "Taillight", body, new Vector3(0.65f * s, 0.84f, -2.11f), new Vector3(0.35f, 0.12f, 0.04f), taillightMat).GetComponent<Renderer>();
        }

        // Taxi markings. The chassis top is at y 0.975, the roof top at 1.55.
        Part(PrimitiveType.Cube, "Stripe", body, new Vector3(0f, 0.981f, 0f), new Vector3(0.4f, 0.012f, 4.21f), stripe);
        Part(PrimitiveType.Cube, "Roof Stripe", body, new Vector3(0f, 1.556f, -0.25f), new Vector3(0.4f, 0.012f, 1.81f), stripe);

        // Taxi roof light bar: mounting base, illuminated sign box, end caps and small placeholder "text" blocks
        // (matching the blocky shop-sign lettering style on buildings).
        var barRoot = new GameObject("LightBar").transform;
        barRoot.SetParent(body, false);
        barRoot.localPosition = new Vector3(0f, 1.55f, -0.28f);
        lightBar = SaveRest(barRoot);
        Part(PrimitiveType.Cube, "Sign Base", barRoot, new Vector3(0f, 0.02f, 0f), new Vector3(0.96f, 0.04f, 0.32f), stripe);
        Part(PrimitiveType.Cube, "Taxi Sign", barRoot, new Vector3(0f, 0.145f, 0f), new Vector3(0.8f, 0.21f, 0.24f), sign);
        AddCustomMesh("LightBarBlocks", barRoot, BuildLightBarDetailsMesh(), stripe);

        var col = gameObject.AddComponent<BoxCollider>();
        col.center = new Vector3(0f, 0.75f, 0f);
        col.size = new Vector3(1.9f, 0.65f, 4.2f);

        var rb = gameObject.AddComponent<Rigidbody>();
        rb.mass = 1200f;
        rb.angularDamping = 0.3f;

        var controller = gameObject.AddComponent<CarController>();
        var wheelsRoot = new GameObject("Wheels").transform;
        wheelsRoot.SetParent(transform, false);

        var anchors = new[]
        {
            (name: "FL", pos: new Vector3(-0.85f, 0.65f, 1.35f), front: true),
            (name: "FR", pos: new Vector3(0.85f, 0.65f, 1.35f), front: true),
            (name: "RL", pos: new Vector3(-0.85f, 0.65f, -1.35f), front: false),
            (name: "RR", pos: new Vector3(0.85f, 0.65f, -1.35f), front: false),
        };
        controller.wheels = new CarController.Wheel[anchors.Length];
        float diameter = controller.wheelRadius * 2f;
        for (int i = 0; i < anchors.Length; i++)
        {
            var pivot = new GameObject("Wheel_" + anchors[i].name).transform;
            pivot.SetParent(wheelsRoot, false);
            pivot.localPosition = anchors[i].pos - Vector3.up * controller.restLength;

            // Unity's cylinder is 1 wide and 2 tall along Y; it is turned on its side by the controller.
            var tireGo = Part(PrimitiveType.Cylinder, "Tire", pivot, Vector3.zero, new Vector3(diameter, 0.15f, diameter), tire);
            tireGo.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            // Two crossed spokes ("+") poking out of the tyre so you can see it spin.
            Part(PrimitiveType.Cube, "Rim", tireGo.transform, Vector3.zero, new Vector3(0.6f, 2.15f, 0.15f), rim);
            Part(PrimitiveType.Cube, "Rim", tireGo.transform, Vector3.zero, new Vector3(0.15f, 2.15f, 0.6f), rim);

            controller.wheels[i] = new CarController.Wheel
            {
                pivot = pivot,
                mesh = tireGo.transform,
                localAnchor = anchors[i].pos,
                front = anchors[i].front,
            };
        }
    }

    static PartRest SaveRest(Transform t) => new() { t = t, pos = t.localPosition, rot = t.localRotation };

    Mesh BuildWheelWellsMesh()
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var tris = new List<int>();

        for (int sx = -1; sx <= 1; sx += 2)
        {
            for (int sz = -1; sz <= 1; sz += 2)
            {
                float cz = 1.35f * sz;
                // Dark recessed well ceiling, inner backing, and front/back well walls.
                Shapes.Box(verts, normals, tris, new Vector3(0.735f * sx, 0.80f, cz), new Vector3(0.43f, 0.02f, 0.94f));
                Shapes.Box(verts, normals, tris, new Vector3(0.528f * sx, 0.615f, cz), new Vector3(0.02f, 0.37f, 0.94f));
                Shapes.Box(verts, normals, tris, new Vector3(0.735f * sx, 0.615f, cz - 0.465f), new Vector3(0.43f, 0.37f, 0.02f));
                Shapes.Box(verts, normals, tris, new Vector3(0.735f * sx, 0.615f, cz + 0.465f), new Vector3(0.43f, 0.37f, 0.02f));
                // Subtle outer fender arch trim brow along the top and sides of the wheel opening.
                Shapes.Box(verts, normals, tris, new Vector3(0.955f * sx, 0.822f, cz), new Vector3(0.03f, 0.045f, 0.98f));
                Shapes.Box(verts, normals, tris, new Vector3(0.955f * sx, 0.625f, cz - 0.475f), new Vector3(0.03f, 0.38f, 0.035f));
                Shapes.Box(verts, normals, tris, new Vector3(0.955f * sx, 0.625f, cz + 0.475f), new Vector3(0.03f, 0.38f, 0.035f));
            }
        }
        return FinishMesh("Taxi Wheel Wells", verts, normals, tris);
    }

    Mesh BuildLightBarDetailsMesh()
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var tris = new List<int>();

        // End caps and top cap framing the illuminated light bar.
        Shapes.Box(verts, normals, tris, new Vector3(-0.42f, 0.145f, 0f), new Vector3(0.06f, 0.22f, 0.26f));
        Shapes.Box(verts, normals, tris, new Vector3(0.42f, 0.145f, 0f), new Vector3(0.06f, 0.22f, 0.26f));
        Shapes.Box(verts, normals, tris, new Vector3(0f, 0.258f, 0f), new Vector3(0.9f, 0.024f, 0.26f));

        // Four small placeholder "letter" blocks on both front and back faces, like the shop signs on buildings.
        float[] xs = { -0.24f, -0.08f, 0.08f, 0.24f };
        float[] ws = { 0.10f, 0.11f, 0.10f, 0.09f };
        for (int face = -1; face <= 1; face += 2)
        {
            float z = face * 0.126f;
            for (int i = 0; i < xs.Length; i++)
                Shapes.Box(verts, normals, tris, new Vector3(xs[i], 0.145f, z), new Vector3(ws[i], 0.105f, 0.018f));
        }
        return FinishMesh("Taxi Light Bar Details", verts, normals, tris);
    }

    Mesh FinishMesh(string name, List<Vector3> verts, List<Vector3> normals, List<int> tris)
    {
        var m = new Mesh { name = name };
        m.SetVertices(verts);
        m.SetNormals(normals);
        m.SetTriangles(tris, 0);
        m.RecalculateBounds();
        ownedMeshes.Add(m);
        return m;
    }

    static GameObject AddCustomMesh(string name, Transform parent, Mesh mesh, Material mat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        return go;
    }

    static GameObject Part(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        DestroyImmediate(go.GetComponent<Collider>());   // only the car's own box collider should exist
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }
}
