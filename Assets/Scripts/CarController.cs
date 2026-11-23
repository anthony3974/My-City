using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Arcade raycast car. Each wheel is a spring that pushes the body up, plus
/// sideways grip and drive/brake forces. Works on any collider (roads, curbs, ramps).
///
/// Keyboard: W/S or Up/Down = gas/brake/reverse, A/D or Left/Right = steer,
///           Space = handbrake, R = flip upright.
/// Gamepad:  RT/LT = gas/brake, left stick = steer, A = handbrake, Y = reset.
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

    [Header("Steering")]
    // Starting car: slower speed-sensitive steering and loose grip, so better handling is earned.
    public float maxSteerAngle = 28f;
    public float highSpeedSteerAngle = 4.5f;
    public float steerRate = 85f;         // degrees per second at low speed; slows down at high speed
    [Tooltip("Speed (m/s) where steering reaches its high-speed limit.")]
    public float steerSpeedRef = 30f;

    [Header("Grip (0-1, share of sideways slide removed each physics step)")]
    [Range(0f, 1f)] public float frontGrip = 0.72f;
    [Range(0f, 1f)] public float rearGrip = 0.68f;
    [Range(0f, 1f)] public float handbrakeRearGrip = 0.25f;

    [Header("Drift")]
    [Tooltip("Front grip multiplier while handbrake-drifting so the whole car sweeps smoothly instead of pivoting.")]
    [Range(0.2f, 1f)] public float driftFrontGripScale = 0.62f;
    [Tooltip("Maximum steady drift slip angle (degrees) before self-stabilising assist reins in a spinout.")]
    public float maxDriftSlipAngle = 36f;

    [Header("Body")]
    public Vector3 centerOfMass = new Vector3(0f, 0.45f, 0f);

    Rigidbody rb;
    CarDamage damage;
    float throttleInput, steerInput, currentSteer;
    float driftBlend;   // smooth 0-1 blend into and out of handbrake drift
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
    }

    void Update()
    {
        ReadInput();
        UpdateWheelVisuals();
    }

    void ReadInput()
    {
        if (UpgradeMenu.Open)
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

        // Speed-sensitive steering: steer angle narrows with a smooth curve as speed builds,
        // and turning into a corner gets progressively slower at high speed (returning to centre stays responsive).
        float speedNorm = Mathf.Clamp01(absSpeed / Mathf.Max(1f, steerSpeedRef));
        float steerCurve = Mathf.Sqrt(speedNorm);
        float maxAngleNow = Mathf.Lerp(maxSteerAngle, highSpeedSteerAngle, steerCurve);
        // Smooth out drift steering so handbrake stretches a corner into a long sweeping drift instead of a snap U-turn.
        driftBlend = Mathf.MoveTowards(driftBlend, handbrake && absSpeed > 5f ? 1f : 0f, dt * (handbrake ? 5f : 2.8f));
        if (driftBlend > 0.01f)
            maxAngleNow *= Mathf.Lerp(1f, 0.78f, driftBlend * speedNorm);

        float targetSteer = wrecked ? 0f : steerInput * maxAngleNow;
        bool returning = Mathf.Abs(targetSteer) < Mathf.Abs(currentSteer) || Mathf.Sign(targetSteer) != Mathf.Sign(currentSteer);
        float speedRateScale = Mathf.Lerp(1f, 0.34f, speedNorm);
        float effectiveRate = steerRate * (returning ? Mathf.Lerp(1.1f, 0.75f, speedNorm) : speedRateScale);
        currentSteer = Mathf.MoveTowards(currentSteer, targetSteer, effectiveRate * dt);

        // Compute slip angle and drift state for both drift assist and metrics.
        Vector3 flatVel = Shapes.Flat(rb.linearVelocity);
        float flatSpeed = flatVel.magnitude;
        float slipSigned = flatSpeed > 4f ? Vector3.SignedAngle(Shapes.Flat(transform.forward), flatVel, Vector3.up) : 0f;
        DriftAngle = Mathf.Abs(slipSigned);

        float massPerWheel = rb.mass / wheels.Length;
        bool braking = !wrecked && ((throttleInput > 0.05f && speed < -0.5f) || (throttleInput < -0.05f && speed > 0.5f));
        int groundedCount = 0;
        Vector3 up = transform.up;

        // Grip values: during a drift, rear loosens moderately (not to zero) and front eases slightly
        // so the car glides around a wide arc instead of pivoting around the nose.
        float activeFrontGrip = Mathf.Lerp(frontGrip, frontGrip * driftFrontGripScale, driftBlend);
        float activeRearGrip = Mathf.Lerp(rearGrip, handbrakeRearGrip, driftBlend);

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
            rb.AddForceAtPosition(-right * sideVel * grip * massPerWheel / dt, forcePoint);

            float fwdVel = Vector3.Dot(fwd, wheelVel);
            float force;
            if (wrecked)
            {
                // Engine is dead: coast to a stop with extra rolling drag (or brake if pressed).
                force = -fwdVel * massPerWheel * (handbrake ? 1.8f : 0.65f);
            }
            else if (braking)
            {
                force = -Mathf.Sign(fwdVel) * brakeForce / wheels.Length * Mathf.Abs(throttleInput);
            }
            else if (Mathf.Abs(throttleInput) > 0.05f)
            {
                float limit = (throttleInput > 0f ? maxSpeed : maxReverseSpeed) * damageSpeedScale;
                float power = 1f - Mathf.Clamp01(absSpeed / Mathf.Max(1f, limit));
                force = throttleInput * (motorForce * damageSpeedScale) / wheels.Length * Mathf.Sqrt(power);
            }
            else
            {
                force = -fwdVel * rollingResistance * massPerWheel;
            }

            // Light rear drag during handbrake so the car keeps enough speed to carry a long sweeping drift.
            if (!wrecked && handbrake && !w.front)
                force = Mathf.Min(force, 0f) - fwdVel * massPerWheel * 0.24f;

            rb.AddForceAtPosition(fwd * force, forcePoint);

            w.spin += fwdVel / wheelRadius * Mathf.Rad2Deg * dt;
        }

        IsDrifting = groundedCount >= 2 && flatSpeed > 7.5f && DriftAngle > 13f && speed > 4f;

        if (groundedCount > 0)
        {
            rb.AddForce(-up * downforce * speed * speed);

            // Drift yaw damping and slip-angle limiter: prevents the handbrake from acting like a 180° U-turn button
            // and holds a smooth, modern sweeping drift angle through long corners.
            if (driftBlend > 0.05f || DriftAngle > 15f)
            {
                float yawRate = Vector3.Dot(rb.angularVelocity, up);
                float damp = Mathf.Lerp(0.4f, 2.1f, driftBlend);
                rb.AddTorque(-up * yawRate * rb.inertiaTensor.y * damp, ForceMode.Force);

                float excessSlip = DriftAngle - maxDriftSlipAngle;
                if (excessSlip > 0f && speed > 4f)
                {
                    // Gently turn the nose back toward the velocity vector to stop spinouts.
                    float restoreDir = Mathf.Sign(slipSigned);
                    float restoreTorque = restoreDir * Mathf.Clamp01(excessSlip / 25f) * rb.inertiaTensor.y * 5.5f;
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
