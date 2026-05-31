using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;

/// <summary>
/// Reads the pose of a VR controller mounted on the welding clamp and converts
/// it to pitch / roll / yaw angles for the WeldingEvaluator.
///
/// The controller is used ONLY as an orientation sensor.
/// No button inputs are processed.
///
/// Haptic feedback is driven externally (WeldingEvaluator calls the public
/// haptic methods) so this script stays decoupled from evaluation logic.
///
/// Setup:
///   1. Assign controllerTransform → the "Right Controller" (or Left) child of
///      "Camera Offset" inside XR Origin Hands (XR Rig).
///      That GO already has TrackedPoseDriver, so its rotation is world-tracked.
///   2. Assign hapticPlayer → the HapticImpulsePlayer on the same GO
///      (auto-found from controllerTransform in Awake if left empty).
///   3. Tune mountingEulerOffset until the displayed angles match the physical
///      electrode orientation (e.g. if the controller is tilted 45° on the clamp,
///      set X = -45 to compensate).
///   4. At session start the evaluator calls Calibrate() automatically.
/// </summary>
[DisallowMultipleComponent]
public class VRControllerWeldSensor : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Controller Transform")]
    [Tooltip("Assign the 'Right Controller' (or Left Controller) child of Camera Offset "
           + "inside XR Origin Hands (XR Rig). Its world rotation is already tracked "
           + "each frame by the TrackedPoseDriver component on that GO.")]
    [SerializeField] private Transform controllerTransform;

    [Header("Mounting Correction")]
    [Tooltip("Euler offset (degrees) to compensate for the physical angle at which the "
           + "controller is clamped onto the welding torch. Adjust until the displayed "
           + "angles match the actual electrode orientation.\n"
           + "Example: if the controller grip is tilted 30° forward on the clamp, "
           + "set X = -30 so the sensor reads 0° when the electrode is truly vertical.")]
    [SerializeField] private Vector3 mountingEulerOffset = Vector3.zero;

    [Header("Calibration")]
    [Tooltip("When true, WeldingEvaluator will call Calibrate() automatically at the "
           + "start of each session — stores the current orientation as the zero reference.")]
    [SerializeField] private bool calibrateOnSessionStart = true;

    [Header("Haptic Feedback")]
    [Tooltip("HapticImpulsePlayer on the controller GO. Auto-found from controllerTransform "
           + "if left empty.")]
    [SerializeField] private HapticImpulsePlayer hapticPlayer;

    [SerializeField] private bool hapticEnabled = true;

    [Tooltip("Amplitude (0-1) of the pulse when the arc enters valid range.")]
    [SerializeField] [Range(0f, 1f)] private float arcEntryAmplitude = 0.30f;
    [SerializeField] private float arcEntryDuration = 0.10f;

    [Tooltip("Amplitude (0-1) of the pulse when the arc exits valid range.")]
    [SerializeField] [Range(0f, 1f)] private float arcExitAmplitude  = 0.15f;
    [SerializeField] private float arcExitDuration  = 0.06f;

    [Tooltip("Soft periodic pulse while arc is stable and in range.")]
    [SerializeField] [Range(0f, 1f)] private float arcStableAmplitude   = 0.08f;
    [SerializeField] private float arcStablePulsePeriod = 1.5f;  // seconds

    [Header("Diagnostics (read-only)")]
    [SerializeField] private bool    isTracking;
    [SerializeField] private float   pitchDeg;
    [SerializeField] private float   rollDeg;
    [SerializeField] private float   yawDeg;
    [SerializeField] private Vector3 worldPosition;

    // ── Public accessors ─────────────────────────────────────────────────────

    public bool    IsTracking          => isTracking;
    public float   PitchDeg            => pitchDeg;
    public float   RollDeg             => rollDeg;
    public float   YawDeg              => yawDeg;
    public Vector3 WorldPosition       => worldPosition;
    public bool    CalibrateOnSessionStart => calibrateOnSessionStart;

    // ── Private state ─────────────────────────────────────────────────────────

    /// <summary>Inverse of the "zero" orientation — applied to remove the
    /// neutral pose from all subsequent readings.</summary>
    private Quaternion _calibrationOffset = Quaternion.identity;
    private float      _stablePulseTimer;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        // Auto-find haptic player from the controller transform if not assigned
        if (hapticPlayer == null && controllerTransform != null)
            hapticPlayer = controllerTransform.GetComponent<HapticImpulsePlayer>();
    }

    private void Update()
    {
        SamplePose();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores the current controller orientation as the zero (neutral) reference.
    /// After calling this, all angles are relative to this pose.
    /// Called automatically by WeldingEvaluator at session start if
    /// calibrateOnSessionStart is true.
    /// </summary>
    public void Calibrate()
    {
        if (controllerTransform == null)
        {
            Debug.LogWarning("[VRControllerWeldSensor] Cannot calibrate: controllerTransform not assigned.");
            return;
        }

        // _calibrationOffset is the rotation that, when inverted and applied to
        // the current world rotation, produces identity (zero angles).
        // Formula:  calibratedRot = Inverse(offset) * worldRot
        // At calibration moment worldRot == mounting-corrected pose we want as zero:
        //   desired zero = worldRot * Inverse(mountingEuler)
        _calibrationOffset = controllerTransform.rotation
                           * Quaternion.Inverse(Quaternion.Euler(mountingEulerOffset));

        Debug.Log($"[VRControllerWeldSensor] Calibrated. "
                + $"Ref rotation: {_calibrationOffset.eulerAngles}");
    }

    // ── Haptic helpers ────────────────────────────────────────────────────────

    /// <summary>Called by WeldingEvaluator when the arc enters the valid distance range.</summary>
    public void TriggerArcEntryHaptic()
        => SendHaptic(arcEntryAmplitude, arcEntryDuration);

    /// <summary>Called by WeldingEvaluator when the arc exits the valid distance range.</summary>
    public void TriggerArcExitHaptic()
        => SendHaptic(arcExitAmplitude, arcExitDuration);

    /// <summary>
    /// Periodic soft pulse while arc is stable.
    /// Call every frame while arc is active — self-throttles via arcStablePulsePeriod.
    /// </summary>
    public void TickStableArcHaptic()
    {
        if (!hapticEnabled || arcStablePulsePeriod <= 0f) return;
        _stablePulseTimer += Time.deltaTime;
        if (_stablePulseTimer >= arcStablePulsePeriod)
        {
            _stablePulseTimer = 0f;
            SendHaptic(arcStableAmplitude, 0.05f);
        }
    }

    /// <summary>Resets the stable-arc haptic timer (call when arc goes inactive).</summary>
    public void ResetStableArcTimer() => _stablePulseTimer = 0f;

    /// <summary>Sends a raw haptic impulse with custom parameters.</summary>
    public void SendHaptic(float amplitude, float durationSeconds)
    {
        if (!hapticEnabled || hapticPlayer == null) return;
        hapticPlayer.SendHapticImpulse(Mathf.Clamp01(amplitude), durationSeconds);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void SamplePose()
    {
        if (controllerTransform == null)
        {
            isTracking = false;
            return;
        }

        isTracking    = true;
        worldPosition = controllerTransform.position;

        // 1. Remove calibration offset → rotation relative to the "zero" reference
        var calibratedRot = Quaternion.Inverse(_calibrationOffset) * controllerTransform.rotation;

        // 2. Apply mounting correction → compensate for physical clamping angle
        var correctedRot  = calibratedRot * Quaternion.Euler(mountingEulerOffset);

        // 3. Extract Euler angles, normalised to -180 … +180
        var euler = correctedRot.eulerAngles;
        pitchDeg  = NormalizeAngle180(euler.x);   // forward / back tilt of electrode
        rollDeg   = NormalizeAngle180(euler.z);   // rotation around electrode axis
        yawDeg    = NormalizeAngle180(euler.y);   // horizontal rotation (direction of bead)
    }

    /// <summary>Maps 0…360 to -180…+180 for intuitive angle readings.</summary>
    private static float NormalizeAngle180(float angle)
    {
        angle %= 360f;
        if (angle >  180f) angle -= 360f;
        if (angle < -180f) angle += 360f;
        return angle;
    }
}
