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
    public float motorForce = 10000f;
    public float maxSpeed = 32f;          // m/s (about 115 km/h)
    public float maxReverseSpeed = 10f;
    public float brakeForce = 16000f;
    public float rollingResistance = 0.12f;
    public float downforce = 3f;

    [Header("Steering")]
    public float maxSteerAngle = 35f;
    public float highSpeedSteerAngle = 10f;
    public float steerRate = 140f;        // degrees per second

    [Header("Grip (0-1, share of sideways slide removed each physics step)")]
    [Range(0f, 1f)] public float frontGrip = 0.9f;
    [Range(0f, 1f)] public float rearGrip = 0.85f;
    [Range(0f, 1f)] public float handbrakeRearGrip = 0.12f;

    [Header("Body")]
    public Vector3 centerOfMass = new Vector3(0f, 0.45f, 0f);
    public bool showSpeedometer = true;

    Rigidbody rb;
    float throttleInput, steerInput, currentSteer;
    bool handbrake;
    readonly RaycastHit[] hits = new RaycastHit[8];
    GUIStyle speedStyle;

    public float ForwardSpeed => rb ? Vector3.Dot(rb.linearVelocity, transform.forward) : 0f;
    public Rigidbody Body => rb;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.centerOfMass = centerOfMass;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        foreach (var w in wheels)
            w.suspensionLength = restLength;

        // Cars spawned before crash feedback existed don't have it yet.
        if (!GetComponent<CrashFeedback>())
            gameObject.AddComponent<CrashFeedback>();
    }

    void Update()
    {
        ReadInput();
        UpdateWheelVisuals();
    }

    void ReadInput()
    {
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

        if (reset)
            ResetCar();
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        float speed = ForwardSpeed;
        float speedFactor = Mathf.Clamp01(Mathf.Abs(speed) / maxSpeed);

        float targetSteer = steerInput * Mathf.Lerp(maxSteerAngle, highSpeedSteerAngle, speedFactor);
        currentSteer = Mathf.MoveTowards(currentSteer, targetSteer, steerRate * dt);

        float massPerWheel = rb.mass / wheels.Length;
        bool braking = (throttleInput > 0.05f && speed < -0.5f) || (throttleInput < -0.05f && speed > 0.5f);
        int groundedCount = 0;
        Vector3 up = transform.up;

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
            float grip = w.front ? frontGrip : (handbrake ? handbrakeRearGrip : rearGrip);
            rb.AddForceAtPosition(-right * sideVel * grip * massPerWheel / dt, forcePoint);

            float fwdVel = Vector3.Dot(fwd, wheelVel);
            float force;
            if (braking)
            {
                force = -Mathf.Sign(fwdVel) * brakeForce / wheels.Length * Mathf.Abs(throttleInput);
            }
            else if (Mathf.Abs(throttleInput) > 0.05f)
            {
                float limit = throttleInput > 0f ? maxSpeed : maxReverseSpeed;
                float power = 1f - Mathf.Clamp01(Mathf.Abs(speed) / limit);
                force = throttleInput * motorForce / wheels.Length * Mathf.Sqrt(power);
            }
            else
            {
                force = -fwdVel * rollingResistance * massPerWheel;
            }

            if (handbrake && !w.front)
                force = -fwdVel * massPerWheel * 0.8f;

            rb.AddForceAtPosition(fwd * force, forcePoint);

            w.spin += fwdVel / wheelRadius * Mathf.Rad2Deg * dt;
        }

        if (groundedCount > 0)
            rb.AddForce(-up * downforce * speed * speed);
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
            w.pivot.localPosition = w.localAnchor - Vector3.up * w.suspensionLength;
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

    void OnGUI()
    {
        if (!showSpeedometer || rb == null) return;
        speedStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            wordWrap = false,
            clipping = TextClipping.Overflow,
            normal = { textColor = Color.white }
        };
        speedStyle.fontSize = Mathf.Max(32, Screen.height / 22);

        var content = new GUIContent($"{Mathf.Abs(ForwardSpeed) * 3.6f:0} km/h");
        Vector2 size = speedStyle.CalcSize(content);
        var safe = Screen.safeArea;
        // Same margin as the touch buttons, so it scales with screen size.
        float m = Mathf.Min(Screen.width, Screen.height) * 0.05f;
        // Top-left on phones, where the touch buttons don't cover it; bottom-left otherwise.
        float y = TouchControls.Active ? Screen.height - safe.yMax + m : Screen.height - size.y - m;
        GUI.Label(new Rect(safe.xMin + m, y, size.x, size.y), content, speedStyle);
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
