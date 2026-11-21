using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

/// <summary>
/// On-screen buttons for phones. Creates itself at startup when a touchscreen exists.
/// Bottom-left: steer left / right. Bottom-right: gas, brake/reverse, handbrake. Top-right: reset.
/// Dragging a finger anywhere else orbits the camera.
/// CarController and ThirdPersonCamera read the static values below.
/// </summary>
public class TouchControls : MonoBehaviour
{
    public static float Throttle { get; private set; }
    public static float Steer { get; private set; }
    public static bool Handbrake { get; private set; }
    public static bool ResetPressed { get; private set; }
    public static Vector2 LookDelta { get; private set; }   // pixels this frame
    public static bool Active { get; private set; }
    /// <summary>Where the RESET button is (GUI coordinates); the upgrade menu's button goes under it.</summary>
    public static Rect ResetRect { get; private set; }

    Rect left, right, gas, brake, handbrakeRect, reset;
    int lookFingerId = -1;
    bool resetWasDown;
    GUIStyle style;
    Texture2D fill;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        if (Touchscreen.current == null && !Application.isMobilePlatform) return;
        var go = new GameObject("Touch Controls");
        DontDestroyOnLoad(go);
        go.AddComponent<TouchControls>();
    }

    void OnEnable()
    {
        EnhancedTouchSupport.Enable();
        Active = true;
    }

    void OnDisable()
    {
        Active = false;
        Throttle = Steer = 0f;
        Handbrake = ResetPressed = false;
        LookDelta = Vector2.zero;
    }

    void Layout()
    {
        // Sizes scale with the shorter screen side so buttons stay thumb-sized on any phone.
        float u = Mathf.Min(Screen.width, Screen.height) * 0.2f;
        float m = u * 0.25f;
        var safe = Screen.safeArea;
        float bottom = Screen.height - safe.yMin - m - u;   // GUI coordinates: y down, safe area: y up
        left = new Rect(safe.xMin + m, bottom, u, u);
        right = new Rect(left.xMax + m * 0.6f, bottom, u, u);
        gas = new Rect(safe.xMax - m - u, bottom - u * 0.3f, u, u * 1.3f);
        brake = new Rect(gas.xMin - m * 0.6f - u, bottom, u, u);
        handbrakeRect = new Rect(brake.xMin, gas.yMin - m * 0.6f - u * 0.6f, gas.xMax - brake.xMin, u * 0.6f);
        reset = new Rect(safe.xMax - m - u * 1.2f, Screen.height - safe.yMax + m, u * 1.2f, u * 0.5f);
        ResetRect = reset;
    }

    void Update()
    {
        Layout();
        float throttle = 0f, steer = 0f;
        bool hb = false, resetDown = false, lookFingerSeen = false;
        Vector2 look = Vector2.zero;

        foreach (var t in Touch.activeTouches)
        {
            // Touch positions are y-up; GUI rects are y-down.
            Vector2 p = t.screenPosition;
            p.y = Screen.height - p.y;
            int id = t.touchId;

            // The upgrade menu (or its button) takes this touch.
            if (UpgradeMenu.Blocks(p)) continue;

            if (id == lookFingerId)
            {
                lookFingerSeen = true;
                look += t.delta;
                continue;
            }

            if (left.Contains(p)) steer -= 1f;
            else if (right.Contains(p)) steer += 1f;
            else if (gas.Contains(p)) throttle += 1f;
            else if (brake.Contains(p)) throttle -= 1f;
            else if (handbrakeRect.Contains(p)) hb = true;
            else if (reset.Contains(p)) resetDown = true;
            else if (lookFingerId < 0 && t.phase == UnityEngine.InputSystem.TouchPhase.Began)
            {
                lookFingerId = id;
                lookFingerSeen = true;
            }
        }

        if (!lookFingerSeen) lookFingerId = -1;

        Throttle = Mathf.Clamp(throttle, -1f, 1f);
        Steer = Mathf.Clamp(steer, -1f, 1f);
        Handbrake = hb;
        ResetPressed = resetDown && !resetWasDown;
        resetWasDown = resetDown;
        LookDelta = look;
    }

    void OnGUI()
    {
        if (style == null)
        {
            fill = new Texture2D(1, 1);
            fill.SetPixel(0, 0, Color.white);
            fill.Apply();
            style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
        }
        style.fontSize = Mathf.RoundToInt(left.height * 0.22f);

        Button(left, "LEFT", Steer < 0f);
        Button(right, "RIGHT", Steer > 0f);
        Button(gas, "GAS", Throttle > 0f);
        Button(brake, "BRAKE", Throttle < 0f);
        Button(handbrakeRect, "DRIFT", Handbrake);
        Button(reset, "RESET", resetWasDown);
    }

    void Button(Rect r, string label, bool pressed)
    {
        var old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, pressed ? 0.45f : 0.2f);
        GUI.DrawTexture(r, fill);
        GUI.color = old;
        GUI.Label(r, label, style);
    }
}
