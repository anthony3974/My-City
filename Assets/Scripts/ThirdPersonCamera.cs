using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Orbiting third-person follow camera.
/// Mouse: hold right button and drag to orbit (or always, if Hold Right Mouse is off), scroll to zoom.
/// Gamepad: right stick to orbit, bumpers to zoom.
/// Swings back behind the target on its own after you stop moving the camera.
/// Pulls in when a building gets between it and the target.
/// </summary>
public class ThirdPersonCamera : MonoBehaviour
{
    public Transform target;
    public Vector3 targetOffset = new Vector3(0f, 1.3f, 0f);

    [Header("Distance")]
    public float distance = 8f;
    public float minDistance = 3f;
    public float maxDistance = 25f;
    public float zoomStep = 0.12f;          // fraction of distance per scroll notch

    [Header("Orbit")]
    public bool holdRightMouseToOrbit = true;
    public float mouseSensitivity = 0.15f;  // degrees per pixel
    public float stickSensitivity = 140f;   // degrees per second
    public bool invertY = false;
    public float pitch = 15f;
    public float minPitch = -10f;
    public float maxPitch = 75f;

    [Header("Follow")]
    public float followSmoothTime = 0.06f;
    public float autoRecenterDelay = 1.5f;
    public float recenterSpeed = 2.5f;
    public float recenterPitch = 15f;

    [Header("Speed Feel")]
    public float baseFov = 60f;
    public float maxFov = 75f;
    public float fovAtSpeed = 35f;          // m/s where FOV reaches max

    [Header("Collision")]
    public float collisionRadius = 0.3f;
    public LayerMask collisionMask = ~0;

    float yaw;
    float currentDistance;
    float lastInputTime = -100f;
    Vector3 focus, focusVelocity;
    Rigidbody targetBody;
    Camera cam;
    readonly RaycastHit[] hits = new RaycastHit[16];

    void Start()
    {
        cam = GetComponent<Camera>();
        if (target != null)
        {
            yaw = target.eulerAngles.y;
            focus = target.position + targetOffset;
            targetBody = target.GetComponentInParent<Rigidbody>();
        }
        currentDistance = distance;
    }

    void LateUpdate()
    {
        if (target == null) return;
        float dt = Time.deltaTime;

        ReadInput(dt);

        // Swing back behind the car when the player isn't steering the camera.
        Vector3 velocity = targetBody ? targetBody.linearVelocity : Vector3.zero;
        float speed = velocity.magnitude;
        if (Time.time - lastInputTime > autoRecenterDelay && speed > 2f)
        {
            float t = 1f - Mathf.Exp(-recenterSpeed * dt);
            yaw = Mathf.LerpAngle(yaw, target.eulerAngles.y, t);
            pitch = Mathf.Lerp(pitch, recenterPitch, t);
        }

        Vector3 desiredFocus = target.position + targetOffset;
        focus = Vector3.SmoothDamp(focus, desiredFocus, ref focusVelocity, followSmoothTime);

        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 back = rot * Vector3.back;

        // Pull in if something is between the focus point and the camera.
        float allowed = distance;
        int count = Physics.SphereCastNonAlloc(focus, collisionRadius, back, hits, distance,
            collisionMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            if (hits[i].transform.IsChildOf(target.root)) continue;
            if (hits[i].distance > 0f)
                allowed = Mathf.Min(allowed, hits[i].distance);
        }

        // Snap in fast, ease back out slowly.
        currentDistance = allowed < currentDistance
            ? allowed
            : Mathf.Lerp(currentDistance, allowed, 1f - Mathf.Exp(-4f * dt));

        transform.SetPositionAndRotation(focus + back * currentDistance, rot);

        if (cam != null)
        {
            float fov = Mathf.Lerp(baseFov, maxFov, Mathf.Clamp01(speed / fovAtSpeed));
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, fov, 1f - Mathf.Exp(-3f * dt));
        }
    }

    /// <summary>Called when the world is moved back towards the origin, so the camera moves with it without smoothing.</summary>
    public void Shift(Vector3 delta)
    {
        focus += delta;
        transform.position += delta;
    }

    void ReadInput(float dt)
    {
        Vector2 look = Vector2.zero;
        float zoom = 0f;

        var mouse = Mouse.current;
        if (mouse != null)
        {
            if (!holdRightMouseToOrbit || mouse.rightButton.isPressed)
                look += mouse.delta.ReadValue() * mouseSensitivity;

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                zoom -= Mathf.Sign(scroll);
        }

        var pad = Gamepad.current;
        if (pad != null)
        {
            Vector2 stick = pad.rightStick.ReadValue();
            if (stick.sqrMagnitude > 0.02f)
                look += stick * stickSensitivity * dt;
            if (pad.rightShoulder.isPressed) zoom -= 3f * dt;
            if (pad.leftShoulder.isPressed) zoom += 3f * dt;
        }

        if (TouchControls.Active)
            look += TouchControls.LookDelta * 0.25f;

        if (look.sqrMagnitude > 0.0001f)
        {
            yaw += look.x;
            pitch += invertY ? look.y : -look.y;
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
            lastInputTime = Time.time;
        }

        if (zoom != 0f)
            distance = Mathf.Clamp(distance * (1f + zoom * zoomStep), minDistance, maxDistance);
    }
}
