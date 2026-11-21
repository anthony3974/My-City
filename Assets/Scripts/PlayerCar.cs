using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The player's taxi, built in code by CityStreamer when the game starts: a yellow body with a black stripe,
/// glass cabin, bumpers, lights, a roof sign that is lit while the taxi is free, and four wheels that steer
/// and spin. Adds the Rigidbody, collider and CarController (which adds CrashFeedback).
/// Materials are tinted copies of one city material, so no extra shaders are needed in the build.
/// Traffic borrows some of them by part name ("Cabin", "FrontBumper", "Tire", "Headlight", "Taillight").
/// </summary>
public class PlayerCar : MonoBehaviour
{
    static readonly Color PaintColor = new(1f, 0.76f, 0.05f);
    static readonly Color SignFree = new(1f, 0.97f, 0.8f), SignTaken = new(0.3f, 0.3f, 0.3f);

    /// <summary>The yellow paint, also used as the base for the pedestrians' materials.</summary>
    public Material Paint { get; private set; }

    Material sign;
    readonly List<Material> owned = new();

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
        foreach (var m in owned) Destroy(m);
    }

    /// <summary>The roof sign is lit while the taxi is free.</summary>
    public void SetFree(bool free) => sign.SetColor("_BaseColor", free ? SignFree : SignTaken);

    void Build(Material lit)
    {
        Material M(Color c, float smoothness, string name)
        {
            var m = Shapes.Tinted(lit, c, smoothness, name);
            owned.Add(m);
            return m;
        }
        Paint = M(PaintColor, 0.6f, "Taxi Paint");
        sign = M(SignFree, 0.5f, "Taxi Sign");
        var stripe = M(new Color(0.03f, 0.03f, 0.03f), 0.3f, "Taxi Stripe");
        var glass = M(new Color(0.1f, 0.12f, 0.15f), 0.9f, "Car Glass");
        var trim = M(new Color(0.08f, 0.08f, 0.08f), 0.3f, "Car Trim");
        var tire = M(new Color(0.05f, 0.05f, 0.05f), 0.1f, "Car Tire");
        var rim = M(new Color(0.7f, 0.7f, 0.72f), 0.8f, "Car Rim");
        var headlight = M(new Color(1f, 0.95f, 0.75f), 0.9f, "Car Headlight");
        var taillight = M(new Color(0.9f, 0.05f, 0.05f), 0.9f, "Car Taillight");

        var body = new GameObject("Body").transform;
        body.SetParent(transform, false);
        Part(PrimitiveType.Cube, "Chassis", body, new Vector3(0f, 0.7f, 0f), new Vector3(1.9f, 0.55f, 4.2f), Paint);
        Part(PrimitiveType.Cube, "Cabin", body, new Vector3(0f, 1.225f, -0.2f), new Vector3(1.65f, 0.5f, 2f), glass);
        Part(PrimitiveType.Cube, "Roof", body, new Vector3(0f, 1.51f, -0.25f), new Vector3(1.72f, 0.08f, 1.8f), Paint);
        Part(PrimitiveType.Cube, "FrontBumper", body, new Vector3(0f, 0.5f, 2.12f), new Vector3(1.95f, 0.25f, 0.15f), trim);
        Part(PrimitiveType.Cube, "RearBumper", body, new Vector3(0f, 0.5f, -2.12f), new Vector3(1.95f, 0.25f, 0.15f), trim);
        for (int s = -1; s <= 1; s += 2)
        {
            Part(PrimitiveType.Cube, "Headlight", body, new Vector3(0.62f * s, 0.8f, 2.11f), new Vector3(0.4f, 0.15f, 0.04f), headlight);
            Part(PrimitiveType.Cube, "Taillight", body, new Vector3(0.65f * s, 0.8f, -2.11f), new Vector3(0.35f, 0.12f, 0.04f), taillight);
        }

        // Taxi markings. The chassis top is at y 0.975, the roof top at 1.55.
        Part(PrimitiveType.Cube, "Stripe", body, new Vector3(0f, 0.981f, 0f), new Vector3(0.4f, 0.012f, 4.21f), stripe);
        Part(PrimitiveType.Cube, "Roof Stripe", body, new Vector3(0f, 1.556f, -0.25f), new Vector3(0.4f, 0.012f, 1.81f), stripe);
        Part(PrimitiveType.Cube, "Sign Base", body, new Vector3(0f, 1.57f, -0.3f), new Vector3(0.8f, 0.04f, 0.36f), stripe);
        Part(PrimitiveType.Cube, "Taxi Sign", body, new Vector3(0f, 1.71f, -0.3f), new Vector3(0.72f, 0.24f, 0.28f), sign);

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
