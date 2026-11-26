using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Arcade raycast car. Each wheel is a spring that pushes the body up, plus
/// sideways grip and drive/brake forces. Works on any collider (roads, curbs, ramps).
///
/// Keyboard: W/S or Up/Down = gas/brake/reverse, A/D or Left/Right = steer,
///           Space = handbrake/drift, R = flip upright.
/// Gamepad:  RT/LT = gas/brake, left stick = steer, A = handbrake/drift, Y = reset.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CarController : MonoBehaviour
{
    [System.Serializable]
    public class Wheel
    {
        public Transform pivot;       // moved by suspension, rotated by steering
        public Transform mesh;        // spins
        public Vector3 localAnchor;   // where the spring attaches to the body
        public bool front;

        [HideInInspector] public float suspensionLength;
        [HideInInspector] public float spin;
        [HideInInspector] public bool grounded;
    }

    public Wheel[] wheels;

    [Header("Suspension")]
    public float restLength = 0.4f;
    public float wheelRadius = 0.35f;
    public float springStrength = 30000f;
    public float springDamper = 3500f;

    [Header("Engine")]
    // Starting car: kept slow so upgrades have room to grow.
    public float motorForce = 7000f;
    public float maxSpeed = 24f;          // m/s (about 86 km/h)
    public float maxReverseSpeed = 10f;
    public float brakeForce = 16000f;
    public float rollingResistance = 0.12f;
    public float downforce = 3f;
    [Tooltip("Below this speed (m/s) with no throttle, the car brakes itself to a full stop.")]
    public float creepStopSpeed = 2f;

    [Header("Steering")]
    // Starting car: speed-sensitive steering and loose grip, so better handling is earned.
    public float maxSteerAngle = 28f;
    public float highSpeedSteerAngle = 5f;
    public float steerRate = 90f;         // degrees per second at low speed; slows down at high speed
    [Tooltip("Speed (m/s) where steering reaches its high-speed limit.")]
    public float steerSpeedRef = 30f;

    [Header("Grip (0-1, share of sideways slide removed each physics step)")]
    [Range(0f, 1f)] public float frontGrip = 0.72f;
    [Range(0f, 1f)] public float rearGrip = 0.68f;
    [Range(0f, 1f)] public float handbrakeRearGrip = 0.05f;

    [Header("Drift")]
    [Tooltip("Lateral force limit (in Gs) per front/rear axle while drifting, so drifts stretch across long sweeping corners.")]
    public float driftFrontMaxG = 0.68f;
    public float driftRearMaxG = 0.36f;
    [Tooltip("Maximum steady drift slip angle (degrees) before self-stabilising assist reins in a spinout.")]
    public float maxDriftSlipAngle = 36f;
    [Tooltip("Minimum steering angle available while handbrake-drifting so you can initiate and counter-steer at speed.")]
    public float driftMinSteerAngle = 15f;

    [Header("Body")]
    public Vector3 centerOfMass = new Vector3(0f, 0.45f, 0f);

    Rigidbody rb;
    CarDamage damage;
    ParticleSystem tireSmoke;
    float throttleInput, steerInput, currentSteer;
    float driftBlend;   // smooth 0-1 blend into and out of handbrake drift
    float smokeCarry;
    bool handbrake;
    readonly RaycastHit[] hits = new RaycastHit[8];

    public float ForwardSpeed => rb ? Vector3.Dot(rb.linearVelocity, transform.forward) : 0f;
    public float LateralSpeed => rb ? Vector3.Dot(rb.linearVelocity, transform.right) : 0f;
    public float DriftAngle { get; private set; }
    public bool IsDrifting { get; private set; }

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.centerOfMass = centerOfMass;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        foreach (var w in wheels)
            w.suspensionLength = restLength;

        // Cars spawned before crash feedback / damage existed don't have them yet.
        if (!GetComponent<CrashFeedback>())
            gameObject.AddComponent<CrashFeedback>();
        damage = GetComponent<CarDamage>();
        if (!damage)
            damage = gameObject.AddComponent<CarDamage>();
        tireSmoke = CreateTireSmoke();
    }

    void OnDestroy()
    {
        if (tireSmoke) Destroy(tireSmoke.gameObject);
    }

    void Update()
    {
        ReadInput();
        UpdateWheelVisuals();
        UpdateTireSmoke();
    }

    void ReadInput()
    {
        if (GameMenu.BlocksInput)
        {
            throttleInput = steerInput = 0f;
            handbrake = false;
            return;
        }

        float throttle = 0f, steer = 0f;
        bool brake = false, reset = false;

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) throttle += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) throttle -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) steer += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) steer -= 1f;
            brake |= kb.spaceKey.isPressed;
            reset |= kb.rKey.wasPressedThisFrame;
        }

        var pad = Gamepad.current;
        if (pad != null)
        {
            throttle += pad.rightTrigger.ReadValue() - pad.leftTrigger.ReadValue();
            steer += pad.leftStick.ReadValue().x;
            brake |= pad.buttonSouth.isPressed;
            reset |= pad.buttonNorth.wasPressedThisFrame;
        }

        if (TouchControls.Active)
        {
            throttle += TouchControls.Throttle;
            steer += TouchControls.Steer;
            brake |= TouchControls.Handbrake;
            reset |= TouchControls.ResetPressed;
        }

        throttleInput = Mathf.Clamp(throttle, -1f, 1f);
        steerInput = Mathf.Clamp(steer, -1f, 1f);
        handbrake = brake;

        if (reset && (damage == null || !damage.Wrecked))
            ResetCar();
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        float speed = ForwardSpeed;
        float absSpeed = Mathf.Abs(speed);
        bool wrecked = damage != null && damage.Wrecked;
        float damageSpeedScale = damage != null ? damage.EngineScale : 1f;

        // Compute slip angle from current velocity.
        Vector3 flatVel = Shapes.Flat(rb.linearVelocity);
        float flatSpeed = flatVel.magnitude;
        float slipSigned = flatSpeed > 3.5f ? Vector3.SignedAngle(Shapes.Flat(transform.forward), flatVel, Vector3.up) : 0f;
        DriftAngle = Mathf.Abs(slipSigned);

        // Speed-sensitive steering: narrows and slows down at high speed during normal driving.
        // Holding handbrake unlocks enough steering angle to kick out and counter-steer smoothly.
        float speedNorm = Mathf.Clamp01(absSpeed / Mathf.Max(1f, steerSpeedRef));
        float steerCurve = Mathf.Sqrt(speedNorm);
        float maxAngleNow = Mathf.Lerp(maxSteerAngle, highSpeedSteerAngle, steerCurve);

        bool wantDrift = !wrecked && handbrake && flatSpeed > 5f;
        // Enter drift promptly, but blend grip back gently over ~0.6 s when releasing Space so the tires
        // never snap-grip and whip the car sideways into the curb.
        driftBlend = Mathf.MoveTowards(driftBlend, wantDrift ? 1f : 0f, dt * (wantDrift ? 8f : 1.65f));
        if (driftBlend > 0.01f)
            maxAngleNow = Mathf.Lerp(maxAngleNow, Mathf.Max(maxAngleNow, driftMinSteerAngle), driftBlend);

        float targetSteer = wrecked ? 0f : steerInput * maxAngleNow;
        bool returning = Mathf.Abs(targetSteer) < Mathf.Abs(currentSteer) || Mathf.Sign(targetSteer) != Mathf.Sign(currentSteer);
        float speedRateScale = Mathf.Lerp(1f, 0.38f, speedNorm);
        float rateScale = driftBlend > 0.1f ? Mathf.Lerp(speedRateScale, 1.05f, driftBlend)
            : (returning ? Mathf.Lerp(1.1f, 0.78f, speedNorm) : speedRateScale);
        currentSteer = Mathf.MoveTowards(currentSteer, targetSteer, steerRate * rateScale * dt);

        float massPerWheel = rb.mass / wheels.Length;
        bool braking = !wrecked && ((throttleInput > 0.05f && speed < -0.5f) || (throttleInput < -0.05f && speed > 0.5f));
        // Straight-line handbrake slows the car decisively; steering/drifting with handbrake keeps momentum alive.
        bool straightHandbrake = !wrecked && handbrake && Mathf.Abs(steerInput) < 0.12f && DriftAngle < 10f;
        int groundedCount = 0;
        Vector3 up = transform.up;

        // Lateral grip and friction-circle saturation:
        // Low rear grip kicks the tail out; realistic G caps prevent snap-oversteer when releasing Space.
        // Smoothly lerp between normal tire saturation (~2.2 G) and drift saturation so recovery is gradual.
        float activeFrontGrip = Mathf.Lerp(frontGrip, Mathf.Min(frontGrip, 0.32f), driftBlend);
        float activeRearGrip = Mathf.Lerp(rearGrip, handbrakeRearGrip, driftBlend);
        float normalCap = 2.2f * rb.mass * 9.81f * 0.25f;            // per wheel when gripping normally
        float frontCap = driftFrontMaxG * rb.mass * 9.81f * 0.5f;   // per front wheel while drifting
        float rearCap = driftRearMaxG * rb.mass * 9.81f * 0.5f;     // per rear wheel while drifting
        float frontLimit = Mathf.Lerp(normalCap, frontCap, driftBlend);
        float rearLimit = Mathf.Lerp(normalCap, rearCap, driftBlend);

        foreach (var w in wheels)
        {
            Vector3 anchor = transform.TransformPoint(w.localAnchor);
            Quaternion steerRot = w.front ? Quaternion.AngleAxis(currentSteer, up) : Quaternion.identity;
            Vector3 fwd = steerRot * transform.forward;
            Vector3 right = steerRot * transform.right;

            w.grounded = CastWheel(anchor, -up, restLength + wheelRadius, out RaycastHit hit);
            if (!w.grounded)
            {
                w.suspensionLength = Mathf.MoveTowards(w.suspensionLength, restLength, dt * 2f);
                continue;
            }
            groundedCount++;

            // Spring + damper.
            w.suspensionLength = hit.distance - wheelRadius;
            float compression = restLength - w.suspensionLength;
            float upVel = Vector3.Dot(up, rb.GetPointVelocity(anchor));
            float springF = Mathf.Max(0f, compression * springStrength - upVel * springDamper);
            rb.AddForceAtPosition(up * springF, anchor);

            // Grip and drive forces are applied at center-of-mass height so the car doesn't roll over.
            Vector3 forcePoint = anchor + up * Vector3.Dot(rb.worldCenterOfMass - anchor, up);
            Vector3 wheelVel = rb.GetPointVelocity(hit.point);

            float sideVel = Vector3.Dot(right, wheelVel);
            float grip = w.front ? activeFrontGrip : activeRearGrip;
            float sideForce = -sideVel * grip * massPerWheel / dt;
            float cap = w.front ? frontLimit : rearLimit;
            sideForce = Mathf.Clamp(sideForce, -cap, cap);
            rb.AddForceAtPosition(right * sideForce, forcePoint);

            float fwdVel = Vector3.Dot(fwd, wheelVel);
            float force;
            if (wrecked)
            {
                force = -fwdVel * massPerWheel * (handbrake ? 1.8f : 0.65f);
            }
            else if (straightHandbrake)
            {
                // Pulling handbrake while going straight brakes cleanly instead of being overpowered by throttle.
                force = -Mathf.Sign(fwdVel) * (w.front ? brakeForce * 0.15f : brakeForce * 0.55f) / wheels.Length;
                if (Mathf.Abs(fwdVel) < 0.8f) force = -fwdVel * massPerWheel * 4f;
            }
            else if (braking)
            {
                force = -Mathf.Sign(fwdVel) * brakeForce / wheels.Length * Mathf.Abs(throttleInput);
            }
            else if (Mathf.Abs(throttleInput) > 0.05f)
            {
                float limit = (throttleInput > 0f ? maxSpeed : maxReverseSpeed) * damageSpeedScale;
                float power = 1f - Mathf.Clamp01(absSpeed / Mathf.Max(1f, limit));
                float driftDriveScale = driftBlend > 0.01f ? Mathf.Lerp(1f, 0.82f, driftBlend) : 1f;
                force = throttleInput * (motorForce * damageSpeedScale * driftDriveScale) / wheels.Length * Mathf.Sqrt(power);
            }
            else
            {
                // Coasting: rolling resistance alone only ever halves the speed, so the car would creep at
                // 1 km/h forever. Below creepStopSpeed the drag ramps up so it rolls to a real stop.
                float creep = Mathf.Clamp01(1f - Mathf.Abs(fwdVel) / creepStopSpeed);
                force = -fwdVel * Mathf.Lerp(rollingResistance, 2.5f, creep * creep) * massPerWheel;
            }

            if (!wrecked && handbrake && !straightHandbrake && !w.front)
                force = -fwdVel * massPerWheel * 0.22f;

            rb.AddForceAtPosition(fwd * force, forcePoint);

            w.spin += (handbrake && !w.front ? fwdVel * 0.2f : fwdVel) / wheelRadius * Mathf.Rad2Deg * dt;
        }

        // Fully parked: no throttle, almost still and on the ground, so stop for good instead of drifting by mm.
        if (groundedCount >= 3 && Mathf.Abs(throttleInput) < 0.05f && flatSpeed < 0.25f
            && Mathf.Abs(Vector3.Dot(rb.angularVelocity, up)) < 0.1f)
        {
            Vector3 v = rb.linearVelocity;
            rb.linearVelocity = new Vector3(0f, v.y, 0f);
        }

        IsDrifting = groundedCount >= 2 && flatSpeed > 6.5f && DriftAngle > 12f && speed > 3f;

        if (groundedCount > 0)
        {
            rb.AddForce(-up * downforce * speed * speed);

            // Drift control: gently kick the tail out when initiating Space + steer, cap over-rotation so
            // it holds a sweeping 25-36° angle around corners, and smoothly straighten the nose back toward
            // the travel direction on exit so releasing Space never whips the car sideways into the curb.
            if (driftBlend > 0.05f || DriftAngle > 4f)
            {
                float yawRate = Vector3.Dot(rb.angularVelocity, up);
                float iy = Mathf.Max(1500f, rb.inertiaTensor.y);

                // Smoothly kick the tail out into the turn when pressing Space + steer from straight driving.
                if (wantDrift && driftBlend > 0.2f && Mathf.Abs(steerInput) > 0.15f && DriftAngle < 26f && speed > 6f)
                {
                    float kick = steerInput * (1f - DriftAngle / 26f) * iy * 2.2f * driftBlend;
                    rb.AddTorque(up * kick, ForceMode.Force);
                }

                // When exiting a drift (Space released), gently align the car's nose back to its velocity vector
                // (critically damped) so the tail tucks cleanly behind you instead of snapping sideways.
                if (!wantDrift && DriftAngle > 3f && speed > 4f)
                {
                    float slipRad = slipSigned * Mathf.Deg2Rad;
                    float exitStrength = Mathf.Lerp(0.55f, 1f, Mathf.Clamp01(1f - Mathf.Abs(steerInput)));
                    float alignTorque = (slipRad * 11f - yawRate * 5.8f) * iy * exitStrength;
                    rb.AddTorque(up * alignTorque, ForceMode.Force);
                }

                // Cap maximum yaw rate during a drift so a 90° corner sweeps smoothly over ~1.8-2.5 s.
                const float maxDriftYaw = 1.25f;   // rad/s (~72 deg/s)
                if (driftBlend > 0.05f && Mathf.Abs(yawRate) > maxDriftYaw)
                {
                    float excess = yawRate - Mathf.Sign(yawRate) * maxDriftYaw;
                    rb.AddTorque(-up * excess * iy * 5f, ForceMode.Force);
                }

                // Self-stabilising assist past maxDriftSlipAngle so the car holds the drift instead of spinning out.
                float excessSlip = DriftAngle - maxDriftSlipAngle;
                if (excessSlip > 0f && speed > 3f)
                {
                    float restoreDir = Mathf.Sign(slipSigned);
                    float restoreTorque = restoreDir * Mathf.Clamp01(excessSlip / 20f) * iy * 7.5f;
                    rb.AddTorque(up * restoreTorque, ForceMode.Force);
                }
            }
        }
    }

    bool CastWheel(Vector3 origin, Vector3 dir, float length, out RaycastHit best)
    {
        best = default;
        int count = Physics.RaycastNonAlloc(origin, dir, hits, length, ~0, QueryTriggerInteraction.Ignore);
        float bestDist = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            if (hits[i].rigidbody == rb) continue;
            if (hits[i].distance < bestDist)
            {
                bestDist = hits[i].distance;
                best = hits[i];
            }
        }
        return bestDist < float.MaxValue;
    }

    void UpdateWheelVisuals()
    {
        foreach (var w in wheels)
        {
            if (w.pivot == null) continue;
            // Keep visual travel inside the wheel well ceiling even on hard curb landings.
            float visualLen = Mathf.Clamp(w.suspensionLength, 0.21f, restLength);
            w.pivot.localPosition = w.localAnchor - Vector3.up * visualLen;
            w.pivot.localRotation = w.front ? Quaternion.Euler(0f, currentSteer, 0f) : Quaternion.identity;
            if (w.mesh != null)
                w.mesh.localRotation = Quaternion.Euler(w.spin % 360f, 0f, 0f) * Quaternion.Euler(0f, 0f, 90f);
        }
    }

    void UpdateTireSmoke()
    {
        if (!tireSmoke || wheels == null) return;
        float flatSpeed = Shapes.Flat(rb.linearVelocity).magnitude;
        bool smoking = (IsDrifting || (handbrake && flatSpeed > 5f)) && (damage == null || !damage.Wrecked);
        if (!smoking) return;

        float intensity = IsDrifting ? Mathf.Clamp01(DriftAngle / 30f) : 0.65f;
        smokeCarry += Mathf.Lerp(14f, 34f, intensity) * Time.deltaTime;
        int count = (int)smokeCarry;
        smokeCarry -= count;
        if (count <= 0) return;

        var p = new ParticleSystem.EmitParams { applyShapeToPosition = false };
        foreach (var w in wheels)
        {
            if (w.front || !w.grounded || w.pivot == null) continue;
            Vector3 contact = w.pivot.position - transform.up * (wheelRadius * 0.85f);
            for (int i = 0; i < count; i++)
            {
                p.position = contact + Random.insideUnitSphere * 0.14f;
                p.velocity = Vector3.up * Random.Range(0.4f, 1.1f) + Random.insideUnitSphere * 0.45f;
                p.startLifetime = Random.Range(0.35f, 0.7f);
                p.startSize = Random.Range(0.24f, 0.45f);
                float g = Random.Range(0.76f, 0.9f);
                p.startColor = new Color(g, g, g, 1f);
                tireSmoke.Emit(p, 1);
            }
        }
    }

    static ParticleSystem CreateTireSmoke()
    {
        var mat = Resources.Load<Material>("CrashSparks");
        if (mat == null) return null;

        var go = new GameObject("Drift Smoke");
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = -0.06f;
        main.maxParticles = 180;

        var emission = ps.emission;
        emission.enabled = false;
        var shape = ps.shape;
        shape.enabled = false;

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.55f), new Keyframe(0.3f, 1.35f), new Keyframe(1f, 0f)));

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = mat;
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;

        ps.Play();
        return ps;
    }

    public void ResetCar()
    {
        float yaw = transform.eulerAngles.y;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.position += Vector3.up * 1.5f;
        rb.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    void OnDrawGizmosSelected()
    {
        if (wheels == null) return;
        Gizmos.color = Color.yellow;
        foreach (var w in wheels)
        {
            Vector3 a = transform.TransformPoint(w.localAnchor);
            Gizmos.DrawLine(a, a - transform.up * (restLength + wheelRadius));
        }
    }
}
