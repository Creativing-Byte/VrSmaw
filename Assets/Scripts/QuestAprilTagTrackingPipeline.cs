using System;
using UnityEngine;
using AprilTag;

/// <summary>
/// Runs on-device AprilTag detection using the Quest 3 passthrough cameras.
/// Frames come from QuestPassthroughCameraSource (WebCamTexture).
/// Detected poses are submitted to AprilTagTrackingReceiver in CameraLocal frame.
/// AprilTagTrackedObject components then apply those poses to GameObjects.
/// </summary>
[DisallowMultipleComponent]
public class QuestAprilTagTrackingPipeline : MonoBehaviour
{
    // ──────────────────────────────────────────────────────────────────────────
    // Inspector fields
    // ──────────────────────────────────────────────────────────────────────────

    [Header("Sources")]
    [SerializeField]
    private QuestPassthroughCameraSource cameraSource;

    [SerializeField]
    private AprilTagTrackingReceiver trackingReceiver;

    [Header("Execution")]
    [SerializeField]
    private bool runOnDeviceOnly = true;

    [SerializeField]
    private bool pipelineEnabled = true;

    [SerializeField]
    [Tooltip("Process 1 out of every N frames. 2 = half rate. Saves CPU on Quest 3.")]
    private int processEveryNFrames = 2;

    [SerializeField]
    [Tooltip("Decimation: 1 = full resolution (slow), 2 = half (recommended), 3 = third (fast/rough).")]
    [Range(1, 4)]
    private int decimation = 2;

    [Header("Camera Intrinsics")]
    [SerializeField]
    [Tooltip(
        "Focal length in PIXELS for the passthrough camera.\n" +
        "Quest 3 color passthrough at 1280 px wide ≈ 640–880 px.\n" +
        "Formula: width / (2 * tan(hFOV_rad / 2)).\n" +
        "Tune this until virtual objects align with physical markers.")]
    private float focalLengthPixels = 800f;

    [Header("Marker")]
    [SerializeField]
    [Tooltip("Physical size (in meters) of the printed black square on the AprilTag. " +
             "Measure the outer black border edge to edge.")]
    private float tagSizeMeters = 0.08f;

    [Header("Debug / Simulation")]
    [SerializeField]
    [Tooltip("In Editor or when real detection is unavailable: inject a fake marker " +
             "driven by a scene Transform so you can test tracking without real tags.")]
    private bool simulateMarkerFromTransform;

    [SerializeField]
    private int simulatedMarkerId = 10;

    [SerializeField]
    private Transform simulatedMarkerTransform;

    [SerializeField]
    private Transform xrCameraTransform;

    [SerializeField]
    [Range(0f, 1f)]
    private float simulatedQuality = 0.95f;

    [Header("Diagnostics (read-only)")]
    [SerializeField]
    private int processedFrames;

    [SerializeField]
    private int detectedTagsLastFrame;

    [SerializeField]
    private long lastFrameTimestampMs;

    // ──────────────────────────────────────────────────────────────────────────
    // Private state
    // ──────────────────────────────────────────────────────────────────────────

    private TagDetector _detector;
    private int _detectorWidth;
    private int _detectorHeight;
    private int _frameCounter;

    // ──────────────────────────────────────────────────────────────────────────
    // Unity lifecycle
    // ──────────────────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        EnsureDependencies();

        if (cameraSource != null)
        {
            cameraSource.FrameUpdated += OnCameraFrameUpdated;
        }
    }

    private void OnDisable()
    {
        if (cameraSource != null)
        {
            cameraSource.FrameUpdated -= OnCameraFrameUpdated;
        }

        DisposeDetector();
    }

    private void OnDestroy()
    {
        DisposeDetector();
    }

    private void Update()
    {
        if (simulateMarkerFromTransform)
        {
            InjectSimulatedMarker();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Camera frame handling
    // ──────────────────────────────────────────────────────────────────────────

    private void OnCameraFrameUpdated(QuestPassthroughCameraSource.CameraFrame frame)
    {
        lastFrameTimestampMs = frame.timestampMs;

        if (!pipelineEnabled)
        {
            return;
        }

        // Optional: skip on desktop/editor
        if (runOnDeviceOnly && !Application.isMobilePlatform)
        {
            return;
        }

        // Throttle: process every N frames to save CPU
        _frameCounter++;
        if (processEveryNFrames > 1 && _frameCounter % processEveryNFrames != 0)
        {
            return;
        }

        processedFrames++;
        RunDetection(frame);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // AprilTag detection
    // ──────────────────────────────────────────────────────────────────────────

    private void RunDetection(QuestPassthroughCameraSource.CameraFrame frame)
    {
        // Only WebCamTexture is supported (what QuestPassthroughCameraSource uses)
        if (frame.texture is not WebCamTexture webcam)
        {
            return;
        }

        // WebCamTexture must have new data this frame
        if (!webcam.isPlaying || !webcam.didUpdateThisFrame)
        {
            return;
        }

        var w = webcam.width;
        var h = webcam.height;

        // Guard: skip tiny/invalid frames
        if (w < 16 || h < 16)
        {
            return;
        }

        // (Re)create detector if resolution changed
        if (_detector == null || _detectorWidth != w || _detectorHeight != h)
        {
            DisposeDetector();
            _detector = new TagDetector(w, h, decimation);
            _detectorWidth = w;
            _detectorHeight = h;

            Debug.Log($"[AprilTag] Detector created: {w}x{h} decimation={decimation}");
        }

        // Grab pixels from the passthrough camera (main thread — safe here)
        var pixels = webcam.GetPixels32();

        // Run detection: focalLengthPixels should match the actual camera
        _detector.ProcessImage(pixels, focalLengthPixels, tagSizeMeters);

        // Submit each detected tag to the receiver
        detectedTagsLastFrame = 0;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var tag in _detector.DetectedTags)
        {
            detectedTagsLastFrame++;

            // tag.Position and tag.Rotation are already in Unity camera-local space
            // (the keijiro library converts from OpenCV→Unity coordinate system internally)
            var sample = new AprilTagTrackingReceiver.MarkerPoseSample
            {
                timestampMs    = nowMs,
                markerId       = tag.ID,
                position       = tag.Position,
                rotation       = tag.Rotation,
                quality        = 1f,           // keijiro v1.0.x doesn't expose per-tag quality
                markerSizeMeters = tagSizeMeters,
                frame          = AprilTagTrackingReceiver.MarkerPoseFrame.CameraLocal
            };

            trackingReceiver?.SubmitMarkerPose(sample);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Editor / simulation mode
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inject a fake marker driven by a scene Transform.
    /// Useful for testing tracking pipelines in the Editor or on-device
    /// without printing physical AprilTags.
    /// </summary>
    private void InjectSimulatedMarker()
    {
        EnsureDependencies();

        if (trackingReceiver == null || simulatedMarkerTransform == null || xrCameraTransform == null)
        {
            return;
        }

        // Express the simulated marker in camera-local space
        var camPos = xrCameraTransform.InverseTransformPoint(simulatedMarkerTransform.position);
        var camRot = Quaternion.Inverse(xrCameraTransform.rotation) * simulatedMarkerTransform.rotation;

        var sample = new AprilTagTrackingReceiver.MarkerPoseSample
        {
            timestampMs      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            markerId         = simulatedMarkerId,
            position         = camPos,
            rotation         = camRot,
            quality          = simulatedQuality,
            markerSizeMeters = tagSizeMeters,
            frame            = AprilTagTrackingReceiver.MarkerPoseFrame.CameraLocal
        };

        trackingReceiver.SubmitMarkerPose(sample);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private void EnsureDependencies()
    {
        if (cameraSource == null)
        {
            cameraSource = FindAnyObjectByType<QuestPassthroughCameraSource>();
        }

        if (trackingReceiver == null)
        {
            trackingReceiver = AprilTagTrackingReceiver.Instance
                            ?? FindAnyObjectByType<AprilTagTrackingReceiver>();
        }

        if (xrCameraTransform == null && Camera.main != null)
        {
            xrCameraTransform = Camera.main.transform;
        }
    }

    private void DisposeDetector()
    {
        _detector?.Dispose();
        _detector = null;
    }
}
