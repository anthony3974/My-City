using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Spawn Car: builds a drivable car out of cubes and cylinders and hooks the
/// main camera up as a third-person follow camera.
/// </summary>
public static class CarSpawner
{
    const string CarName = "Player Car";

    [MenuItem("Tools/Spawn Car")]
    static void Spawn()
    {
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Spawn Car");

        var old = GameObject.Find(CarName);
        if (old != null) Undo.DestroyObjectImmediate(old);

        // Spawn at a "Car Spawn" marker if there is one, or under the Scene view's focus point.
        // (In play mode the City Streamer moves the car onto a road anyway.)
        Vector3 pos = Vector3.zero;
        Quaternion rot = Quaternion.identity;
        var spawn = GameObject.Find("Car Spawn");
        if (spawn != null)
        {
            pos = spawn.transform.position;
            rot = spawn.transform.rotation;
        }
        else if (SceneView.lastActiveSceneView != null)
        {
            Vector3 pivot = SceneView.lastActiveSceneView.pivot;
            pos = Physics.Raycast(pivot + Vector3.up * 1000f, Vector3.down, out var hit, 2000f)
                ? hit.point
                : new Vector3(pivot.x, 0f, pivot.z);
        }

        var car = BuildCar();
        car.transform.SetPositionAndRotation(pos + Vector3.up * 0.3f, rot);
        Undo.RegisterCreatedObjectUndo(car, "Spawn Car");

        SetupCamera(car.transform);

        Undo.CollapseUndoOperations(group);
        Selection.activeGameObject = car;
    }

    static GameObject BuildCar()
    {
        var bodyColor = GetMat("Car_Red", new Color(0.75f, 0.1f, 0.1f), 0.7f);
        var glass = GetMat("Car_Glass", new Color(0.1f, 0.12f, 0.15f), 0.9f);
        var trim = GetMat("Car_Trim", new Color(0.08f, 0.08f, 0.08f), 0.3f);
        var tire = GetMat("Car_Tire", new Color(0.05f, 0.05f, 0.05f), 0.1f);
        var rim = GetMat("Car_Rim", new Color(0.7f, 0.7f, 0.72f), 0.8f);
        var headlight = GetMat("Car_Headlight", new Color(1f, 0.95f, 0.75f), 0.9f);
        var taillight = GetMat("Car_Taillight", new Color(0.9f, 0.05f, 0.05f), 0.9f);

        var car = new GameObject(CarName);

        var body = new GameObject("Body").transform;
        body.SetParent(car.transform, false);
        Part(PrimitiveType.Cube, "Chassis", body, new Vector3(0f, 0.7f, 0f), new Vector3(1.9f, 0.55f, 4.2f), bodyColor);
        Part(PrimitiveType.Cube, "Cabin", body, new Vector3(0f, 1.225f, -0.2f), new Vector3(1.65f, 0.5f, 2.0f), glass);
        Part(PrimitiveType.Cube, "Roof", body, new Vector3(0f, 1.51f, -0.25f), new Vector3(1.72f, 0.08f, 1.8f), bodyColor);
        Part(PrimitiveType.Cube, "FrontBumper", body, new Vector3(0f, 0.5f, 2.12f), new Vector3(1.95f, 0.25f, 0.15f), trim);
        Part(PrimitiveType.Cube, "RearBumper", body, new Vector3(0f, 0.5f, -2.12f), new Vector3(1.95f, 0.25f, 0.15f), trim);
        for (int s = -1; s <= 1; s += 2)
        {
            Part(PrimitiveType.Cube, "Headlight", body, new Vector3(0.62f * s, 0.8f, 2.11f), new Vector3(0.4f, 0.15f, 0.04f), headlight);
            Part(PrimitiveType.Cube, "Taillight", body, new Vector3(0.65f * s, 0.8f, -2.11f), new Vector3(0.35f, 0.12f, 0.04f), taillight);
        }

        var col = car.AddComponent<BoxCollider>();
        col.center = new Vector3(0f, 0.75f, 0f);
        col.size = new Vector3(1.9f, 0.65f, 4.2f);

        var rb = car.AddComponent<Rigidbody>();
        rb.mass = 1200f;
        rb.angularDamping = 0.3f;

        var controller = car.AddComponent<CarController>();
        var wheelsRoot = new GameObject("Wheels").transform;
        wheelsRoot.SetParent(car.transform, false);

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
            var tireGo = Part(PrimitiveType.Cylinder, "Tire", pivot, Vector3.zero,
                new Vector3(diameter, 0.15f, diameter), tire);
            tireGo.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            var rimGo = Part(PrimitiveType.Cube, "Rim", tireGo.transform, Vector3.zero,
                new Vector3(0.6f, 2.15f, 0.15f), rim); // spoke poking out of the tire so you can see it spin

            controller.wheels[i] = new CarController.Wheel
            {
                pivot = pivot,
                mesh = tireGo.transform,
                localAnchor = anchors[i].pos,
                front = anchors[i].front,
            };
        }

        return car;
    }

    static void SetupCamera(Transform car)
    {
        var cam = Camera.main;
        if (cam == null)
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            Undo.RegisterCreatedObjectUndo(go, "Spawn Car");
            cam = go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
        }

        var follow = cam.GetComponent<ThirdPersonCamera>();
        if (follow == null)
            follow = Undo.AddComponent<ThirdPersonCamera>(cam.gameObject);

        Undo.RecordObject(follow, "Spawn Car");
        follow.target = car;

        Undo.RecordObject(cam.transform, "Spawn Car");
        cam.transform.position = car.position - car.forward * 8f + Vector3.up * 3.5f;
        cam.transform.LookAt(car.position + Vector3.up * 1.3f);

        Undo.RecordObject(cam, "Spawn Car");
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = Mathf.Max(cam.farClipPlane, 2000f);
    }

    static GameObject Part(PrimitiveType type, string name, Transform parent, Vector3 localPos,
        Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        Object.DestroyImmediate(go.GetComponent<Collider>()); // only the body's box collider is used
        return go;
    }

    static Material GetMat(string name, Color color, float smoothness)
    {
        const string folder = "Assets/City/Materials";
        if (!AssetDatabase.IsValidFolder("Assets/City")) AssetDatabase.CreateFolder("Assets", "City");
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/City", "Materials");
        string path = $"{folder}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;

        mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name, color = color };
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Smoothness", smoothness);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }
}
