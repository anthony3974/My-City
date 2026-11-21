using UnityEngine;

/// <summary>
/// Turns the cube car into a taxi at runtime: yellow paint, a black stripe from front to back and a sign
/// box on the roof that is lit while the taxi is free. Added to the car by TaxiGame.
/// Works on the car built by Tools > Spawn Car (looks for its "Chassis" part).
/// </summary>
public class TaxiLook : MonoBehaviour
{
    public Color paint = new(1f, 0.76f, 0.05f);
    public Color stripe = new(0.03f, 0.03f, 0.03f);
    public Color signFree = new(1f, 0.97f, 0.8f);
    public Color signTaken = new(0.3f, 0.3f, 0.3f);

    /// <summary>The yellow paint material, or null if the car has no "Chassis" part.</summary>
    public Material Paint { get; private set; }

    Material stripeMat, signMat;

    void Awake()
    {
        Renderer chassis = null;
        foreach (var r in GetComponentsInChildren<Renderer>())
            if (r.name == "Chassis") { chassis = r; break; }
        if (chassis == null)
        {
            Debug.LogWarning("TaxiLook: no Chassis part on the car, leaving its look alone.");
            return;
        }

        // Copies of the car's own URP Lit material, so no extra shader variants are needed in the build.
        var source = chassis.sharedMaterial;
        Paint = Tinted(source, paint, 0.6f, "Taxi Paint");
        stripeMat = Tinted(source, stripe, 0.3f, "Taxi Stripe");
        signMat = Tinted(source, signFree, 0.5f, "Taxi Sign");

        foreach (var r in GetComponentsInChildren<Renderer>())
            if (r.sharedMaterial == source) r.sharedMaterial = Paint;   // chassis and roof

        // Sizes match the parts in CarSpawner: chassis top at y 0.975, roof top at y 1.55.
        var body = chassis.transform.parent;
        Box("Stripe", body, new Vector3(0f, 0.981f, 0f), new Vector3(0.4f, 0.012f, 4.21f), stripeMat);
        Box("Roof Stripe", body, new Vector3(0f, 1.556f, -0.25f), new Vector3(0.4f, 0.012f, 1.81f), stripeMat);
        Box("Sign Base", body, new Vector3(0f, 1.57f, -0.3f), new Vector3(0.8f, 0.04f, 0.36f), stripeMat);
        Box("Taxi Sign", body, new Vector3(0f, 1.71f, -0.3f), new Vector3(0.72f, 0.24f, 0.28f), signMat);
    }

    void OnDestroy()
    {
        Destroy(Paint);
        Destroy(stripeMat);
        Destroy(signMat);
    }

    public void SetFree(bool free)
    {
        if (signMat != null) signMat.SetColor("_BaseColor", free ? signFree : signTaken);
    }

    static Material Tinted(Material source, Color c, float smoothness, string name)
    {
        var m = new Material(source) { name = name };
        m.SetColor("_BaseColor", c);
        m.SetFloat("_Smoothness", smoothness);
        return m;
    }

    static void Box(string name, Transform parent, Vector3 pos, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        DestroyImmediate(go.GetComponent<Collider>());   // only the car's own box collider should exist
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = mat;
    }
}
