using System;
using UnityEngine;

[DisallowMultipleComponent]
public class AprilTagTrackedObject : MonoBehaviour
{
    [Header("Marker")]
    [SerializeField]
    private int markerId;

    [SerializeField]
    private AprilTagTrackingReceiver receiver;

    [SerializeField]
    private AprilTagTrackingReceiver.MarkerPoseFrame expectedFrame = AprilTagTrackingReceiver.MarkerPoseFrame.CameraLocal;

    [SerializeField]
    private bool autoBootstrapReceiver = true;

    [SerializeField]
    private int autoReceiverPort = 9200;

    [Header("Pose References")]
    [SerializeField]
    private Transform xrCameraTransform;

    [SerializeField]
    private Transform xrOriginTransform;

    [SerializeField]
    private Transform targetTransform;

    [SerializeField]
    private Rigidbody targetRigidbody;

    [SerializeField]
    private bool driveRigidbodyKinematic = true;

    [Header("Marker To Object Offset")]
    [SerializeField]
    private Vector3 localPositionOffset;

    [SerializeField]
    private Vector3 localEulerOffset;

    [Header("Filtering")]
    [SerializeField]
    private float positionLerpSpeed = 20f;

    [SerializeField]
    private float rotationLerpSpeed = 20f;

    [SerializeField]
    private float trackingTimeoutSeconds = 0.25f;

    [SerializeField]
    private float minimumAcceptedQuality = 0.1f;

    [Header("Debug")]
    [SerializeField]
    private bool isTracked;

    [SerializeField]
    private float lastQuality;

    [SerializeField]
    private float lastSeenAgeSeconds;

    [SerializeField]
    private Vector3 desiredWorldPosition;

    [SerializeField]
    private Vector3 desiredWorldEuler;

    public bool IsTracked => isTracked;

    private bool _originalKinematic;

    private void Awake()
    {
        if (targetTransform == null)
        {
            targetTransform = transform;
        }

        if (targetRigidbody == null)
        {
            targetRigidbody = GetComponent<Rigidbody>();
        }

        if (targetRigidbody != null)
        {
            _originalKinematic = targetRigidbody.isKinematic;
            if (driveRigidbodyKinematic)
                targetRigidbody.isKinematic = true;
        }
    }

    private void OnDestroy()
    {
        if (targetRigidbody != null)
            targetRigidbody.isKinematic = _originalKinematic;
    }

    private void Update()
    {
        EnsureDependencies();

        if (receiver == null || !receiver.TryGetMarkerPose(markerId, out var sample))
        {
            UpdateTrackingState(false, 0f);
            return;
        }

        lastQuality = sample.quality;
        lastSeenAgeSeconds = Mathf.Max(0f, (float)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sample.timestampMs) / 1000d));

        var sampleFresh = lastSeenAgeSeconds <= trackingTimeoutSeconds;
        var sampleGood = sample.quality >= minimumAcceptedQuality;

        if (!sampleFresh || !sampleGood)
        {
            UpdateTrackingState(false, sample.quality);
            return;
        }

        var worldPose = ResolveWorldPose(sample);
        ApplyOffset(ref worldPose.position, ref worldPose.rotation);

        desiredWorldPosition = worldPose.position;
        desiredWorldEuler = worldPose.rotation.eulerAngles;
        ApplyPose(worldPose.position, worldPose.rotation);
        UpdateTrackingState(true, sample.quality);
    }

    private void EnsureDependencies()
    {
        if (receiver == null)
        {
            receiver = AprilTagTrackingReceiver.Instance ?? FindAnyObjectByType<AprilTagTrackingReceiver>();

            if (receiver == null && autoBootstrapReceiver)
            {
                var receiverObject = new GameObject("AprilTag Tracking Receiver");
                receiver = receiverObject.AddComponent<AprilTagTrackingReceiver>();
                receiver.Configure(autoReceiverPort);
            }
        }

        if (xrCameraTransform == null && Camera.main != null)
        {
            xrCameraTransform = Camera.main.transform;
        }
    }

    private (Vector3 position, Quaternion rotation) ResolveWorldPose(AprilTagTrackingReceiver.MarkerPoseSample sample)
    {
        var frame = sample.frame;
        if (frame != expectedFrame)
        {
            frame = expectedFrame;
        }

        switch (frame)
        {
            case AprilTagTrackingReceiver.MarkerPoseFrame.World:
                return (sample.position, sample.rotation);

            case AprilTagTrackingReceiver.MarkerPoseFrame.XROriginLocal:
                if (xrOriginTransform != null)
                {
                    return (
                        xrOriginTransform.TransformPoint(sample.position),
                        xrOriginTransform.rotation * sample.rotation);
                }
                break;

            case AprilTagTrackingReceiver.MarkerPoseFrame.CameraLocal:
            default:
                if (xrCameraTransform != null)
                {
                    return (
                        xrCameraTransform.TransformPoint(sample.position),
                        xrCameraTransform.rotation * sample.rotation);
                }
                break;
        }

        return (sample.position, sample.rotation);
    }

    private void ApplyOffset(ref Vector3 worldPosition, ref Quaternion worldRotation)
    {
        var offsetRotation = Quaternion.Euler(localEulerOffset);
        worldRotation = worldRotation * offsetRotation;
        worldPosition += worldRotation * localPositionOffset;
    }

    private void ApplyPose(Vector3 position, Quaternion rotation)
    {
        var currentPosition = targetTransform.position;
        var currentRotation = targetTransform.rotation;
        var positionT = 1f - Mathf.Exp(-positionLerpSpeed * Time.deltaTime);
        var rotationT = 1f - Mathf.Exp(-rotationLerpSpeed * Time.deltaTime);

        var smoothedPosition = Vector3.Lerp(currentPosition, position, positionT);
        var smoothedRotation = Quaternion.Slerp(currentRotation, rotation, rotationT);

        if (driveRigidbodyKinematic && targetRigidbody != null)
        {
            targetRigidbody.MovePosition(smoothedPosition);
            targetRigidbody.MoveRotation(smoothedRotation);
        }
        else
        {
            targetTransform.SetPositionAndRotation(smoothedPosition, smoothedRotation);
        }
    }

    private void UpdateTrackingState(bool tracked, float quality)
    {
        isTracked = tracked;
        lastQuality = quality;
    }
}
