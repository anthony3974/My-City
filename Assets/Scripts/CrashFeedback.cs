using System.Collections;
using UnityEngine;

/// <summary>
/// Crash feedback for the car: sparks at the point of impact, camera shake, and a
/// split-second slow-motion on hard hits. Scraping along a wall throws a steady stream
/// of sparks. Hits from below (landing, curbs) are ignored.
/// CarController adds this component on its own if it's missing.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CrashFeedback : MonoBehaviour
{
    [Header("Impact (change in speed, m/s)")]
    public float minImpact = 3f;            // softer bumps do nothing
    public float fullImpact = 15f;          // hits this hard get the full effect

    [Header("Sparks")]
    public int maxSparksPerHit = 40;
    public float scrapeMinSpeed = 4f;       // sliding speed along a wall before it sparks
    public float scrapeSparksPerMetre = 3f;

    [Header("Camera shake")]
    public float maxShake = 1f;
    public float scrapeShake = 0.12f;

    [Header("Slow motion (hard hits only)")]
    public float slowMoThreshold = 0.6f;    // share of fullImpact that triggers it
    public float slowMoScale = 0.25f;
    public float slowMoDuration = 0.12f;    // real seconds

    Rigidbody rb;
    ThirdPersonCamera cam;
    ParticleSystem sparks;
    float scrapeCarry;
    Coroutine slowMo;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        sparks = CreateSparks();
    }

    void Start()
    {
        if (Camera.main) cam = Camera.main.GetComponent<ThirdPersonCamera>();
    }

    void OnDisable()
    {
        if (slowMo != null)
        {
            StopCoroutine(slowMo);
            slowMo = null;
            if (!UpgradeMenu.Open) Time.timeScale = 1f;
        }
    }

    void OnDestroy()
    {
        if (sparks) Destroy(sparks.gameObject);
    }

    void OnCollisionEnter(Collision c)
    {
        if (!SideContact(c, out Vector3 point, out Vector3 normal)) return;

        float impact = c.impulse.magnitude / rb.mass;
        if (impact < minImpact) return;
        float strength = Mathf.InverseLerp(minImpact, fullImpact, impact);

        EmitSparks(point, normal, Mathf.Max(4, Mathf.RoundToInt(maxSparksPerHit * strength)), 2f + 8f * strength);
        if (cam) cam.Shake(maxShake * Mathf.Lerp(0.2f, 1f, strength));

        if (strength >= slowMoThreshold && slowMo == null)
            slowMo = StartCoroutine(SlowMo());
    }

    void OnCollisionStay(Collision c)
    {
        if (!SideContact(c, out Vector3 point, out Vector3 normal)) return;

        Vector3 slide = Vector3.ProjectOnPlane(rb.linearVelocity, normal);
        float speed = slide.magnitude;
        if (speed < scrapeMinSpeed) return;

        // Sparks per metre slid, so the stream gets denser the faster you scrape.
        scrapeCarry += speed * Time.fixedDeltaTime * scrapeSparksPerMetre;
        int count = (int)scrapeCarry;
        scrapeCarry -= count;
        if (count > 0) EmitSparks(point, normal, count, 3f, -slide * 0.4f);

        if (cam) cam.Shake(scrapeShake * Mathf.Clamp01(speed / fullImpact));
    }

    /// <summary>Gets the contact point and a normal facing the car. False for hits from below or above.</summary>
    bool SideContact(Collision c, out Vector3 point, out Vector3 normal)
    {
        point = normal = default;
        if (c.contactCount == 0) return false;

        var contact = c.GetContact(0);
        point = contact.point;
        normal = contact.normal;
        if (Vector3.Dot(normal, rb.worldCenterOfMass - point) < 0f) normal = -normal;
        return Mathf.Abs(normal.y) < 0.7f;
    }

    void EmitSparks(Vector3 point, Vector3 normal, int count, float speed, Vector3 extraVelocity = default)
    {
        if (!sparks) return;

        // Sparks fly off the wall and keep some of the car's sideways motion.
        Vector3 carSlide = Vector3.ProjectOnPlane(rb.linearVelocity, normal) * 0.5f;
        var p = new ParticleSystem.EmitParams { applyShapeToPosition = false };
        for (int i = 0; i < count; i++)
        {
            Vector3 dir = (normal + Random.insideUnitSphere * 0.8f + Vector3.up * 0.3f).normalized;
            p.position = point + Random.insideUnitSphere * 0.1f;
            p.velocity = dir * speed * Random.Range(0.5f, 1.2f) + carSlide + extraVelocity;
            p.startLifetime = Random.Range(0.25f, 0.6f);
            p.startSize = Random.Range(0.04f, 0.09f);
            p.startColor = Color.Lerp(new Color(1f, 0.95f, 0.6f), new Color(1f, 0.55f, 0.1f), Random.value);
            sparks.Emit(p, 1);
        }
    }

    IEnumerator SlowMo()
    {
        Time.timeScale = slowMoScale;
        yield return new WaitForSecondsRealtime(slowMoDuration);
        if (!UpgradeMenu.Open) Time.timeScale = 1f;   // the menu pauses the game; leave that alone
        slowMo = null;
    }

    static ParticleSystem CreateSparks()
    {
        // Resources/CrashSparks.mat (URP Particles/Unlit) is always in the build,
        // so its shader isn't stripped on Android.
        var mat = Resources.Load<Material>("CrashSparks");
        if (mat == null)
        {
            Debug.LogWarning("CrashFeedback: Resources/CrashSparks material is missing, no sparks.");
            return null;
        }

        var go = new GameObject("Crash Sparks");
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 1.2f;
        main.maxParticles = 300;

        var emission = ps.emission;
        emission.enabled = false;
        var shape = ps.shape;
        shape.enabled = false;

        // Shrink to nothing instead of fading, so the material can stay opaque.
        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));

        var drag = ps.limitVelocityOverLifetime;
        drag.enabled = true;
        drag.drag = 1.5f;

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = mat;
        r.renderMode = ParticleSystemRenderMode.Stretch;
        r.velocityScale = 0.04f;
        r.lengthScale = 1f;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;

        ps.Play();
        return ps;
    }
}
