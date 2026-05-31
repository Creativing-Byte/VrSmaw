using UnityEngine;

/// <summary>
/// Makes this GameObject follow an XR controller Transform at runtime.
///
/// Uses the controller's Unity Transform directly (via GameObject.Find / cached ref),
/// which already accounts for the XR Origin world-space transformation.
/// This avoids the coordinate-space mismatch of reading raw InputDevice tracking data.
///
/// Attach to ARco.  Tune positionOffset / rotationOffset in the Inspector on-device
/// until the virtual clamp visually matches the physical clamp on the controller.
/// </summary>
[DisallowMultipleComponent]
public class AttachToXRController : MonoBehaviour
{
    [Header("Controller GO name in scene")]
    [Tooltip("Name of the Right Controller GameObject inside the XR Rig hierarchy.")]
    [SerializeField] private string controllerGoName = "Right Controller";

    [Header("Mounting offset (controller-local space)")]
    [Tooltip("Position offset in controller-local space (metres). Tune on-device.")]
    [SerializeField] private Vector3 positionOffset = Vector3.zero;

    [Tooltip("Rotation offset applied after controller rotation (Euler degrees). Tune on-device.")]
    [SerializeField] private Vector3 rotationOffset = Vector3.zero;

    [Header("State (read-only)")]
    [SerializeField] private bool controllerFound;

    // ── Private ───────────────────────────────────────────────────────────────

    private Transform _controllerTransform;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Start()
    {
        FindController();
    }

    private void LateUpdate()
    {
        // Lazy-find in case XR rig wasn't ready at Start
        if (_controllerTransform == null)
            FindController();

        if (_controllerTransform == null) return;

        // Use the controller Transform directly — it's already in Unity world space
        transform.position = _controllerTransform.TransformPoint(positionOffset);
        transform.rotation = _controllerTransform.rotation * Quaternion.Euler(rotationOffset);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void FindController()
    {
        var go = GameObject.Find(controllerGoName);
        if (go != null)
        {
            _controllerTransform = go.transform;
            controllerFound      = true;
        }
        else
        {
            controllerFound = false;
        }
    }

    /// <summary>Adjust mount offset at runtime (e.g., calibration UI).</summary>
    public void SetOffset(Vector3 localPos, Vector3 eulerRot)
    {
        positionOffset = localPos;
        rotationOffset = eulerRot;
    }
}
