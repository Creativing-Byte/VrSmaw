using System;
using UnityEngine;

[DisallowMultipleComponent]
public class AprilTagBoardTracker : MonoBehaviour
{
    [Serializable]
    public class SourceMarker
    {
        public int sourceMarkerId;
        public Vector3 boardToMarkerPosition;
        public Vector3 boardToMarkerEuler;
        public float weight = 1f;
    }

    [Header("Board Output")]
    [SerializeField] private int virtualBoardMarkerId = 100;
    [SerializeField] private float boardMarkerSizeMeters = 0.08f;
    [SerializeField] private AprilTagTrackingReceiver.MarkerPoseFrame outputFrame = AprilTagTrackingReceiver.MarkerPoseFrame.CameraLocal;

    [Header("Sources")]
    [SerializeField] private AprilTagTrackingReceiver receiver;
    [SerializeField] private SourceMarker[] sourceMarkers =
    {
        new SourceMarker { sourceMarkerId = 10, boardToMarkerPosition = new Vector3(-0.05f, 0f, 0f), weight = 1f },
        new SourceMarker { sourceMarkerId = 11, boardToMarkerPosition = new Vector3( 0.05f, 0f, 0f), weight = 1f }
    };

    [Header("Filtering")]
    [SerializeField] private float trackingTimeoutSeconds = 0.2f;
    [SerializeField] private float minimumAcceptedQuality = 0.25f;
    [SerializeField] private bool emitFromSingleVisibleTag = true;

    [Header("Debug")]
    [SerializeField] private int visibleSourceCount;
    [SerializeField] private float outputQuality;
    [SerializeField] private Vector3 boardPosition;
    [SerializeField] private Vector3 boardEuler;

    private void Update()
    {
        EnsureDependencies();

        if (receiver == null || sourceMarkers == null || sourceMarkers.Length == 0)
        {
            return;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var hasReferenceRotation = false;
        Quaternion referenceRotation = Quaternion.identity;
        Vector4 accumulatedQuaternion = Vector4.zero;
        Vector3 accumulatedPosition = Vector3.zero;
        float accumulatedWeight = 0f;
        float accumulatedQuality = 0f;
        visibleSourceCount = 0;

        for (var i = 0; i < sourceMarkers.Length; i++)
        {
            var source = sourceMarkers[i];
            if (source == null || source.sourceMarkerId < 0)
            {
                continue;
            }

            if (!receiver.TryGetMarkerPose(source.sourceMarkerId, out var sample))
            {
                continue;
            }

            var ageSeconds = Mathf.Max(0f, (float)((nowMs - sample.timestampMs) / 1000d));
            if (ageSeconds > trackingTimeoutSeconds || sample.quality < minimumAcceptedQuality)
            {
                continue;
            }

            var markerLocalRotation = Quaternion.Euler(source.boardToMarkerEuler);
            var boardRotation = sample.rotation * Quaternion.Inverse(markerLocalRotation);
            var boardPositionEstimate = sample.position - (boardRotation * source.boardToMarkerPosition);

            var weight = Mathf.Max(0.0001f, source.weight) * Mathf.Max(0.0001f, sample.quality);

            if (!hasReferenceRotation)
            {
                referenceRotation = boardRotation;
                hasReferenceRotation = true;
            }

            var q = boardRotation;
            if (Quaternion.Dot(referenceRotation, q) < 0f)
            {
                q.x = -q.x;
                q.y = -q.y;
                q.z = -q.z;
                q.w = -q.w;
            }

            accumulatedQuaternion += new Vector4(q.x, q.y, q.z, q.w) * weight;
            accumulatedPosition += boardPositionEstimate * weight;
            accumulatedQuality += sample.quality * weight;
            accumulatedWeight += weight;
            outputFrame = sample.frame;
            visibleSourceCount++;
        }

        if (visibleSourceCount == 0 || (!emitFromSingleVisibleTag && visibleSourceCount < 2) || accumulatedWeight <= 0f)
        {
            return;
        }

        var averagedPosition = accumulatedPosition / accumulatedWeight;
        var averagedQuaternion = new Quaternion(
            accumulatedQuaternion.x / accumulatedWeight,
            accumulatedQuaternion.y / accumulatedWeight,
            accumulatedQuaternion.z / accumulatedWeight,
            accumulatedQuaternion.w / accumulatedWeight).normalized;

        boardPosition = averagedPosition;
        boardEuler = averagedQuaternion.eulerAngles;
        outputQuality = accumulatedQuality / accumulatedWeight;

        receiver.SubmitMarkerPose(new AprilTagTrackingReceiver.MarkerPoseSample
        {
            timestampMs = nowMs,
            markerId = virtualBoardMarkerId,
            position = averagedPosition,
            rotation = averagedQuaternion,
            quality = outputQuality,
            markerSizeMeters = boardMarkerSizeMeters,
            frame = outputFrame
        });
    }

    private void EnsureDependencies()
    {
        if (receiver == null)
        {
            receiver = AprilTagTrackingReceiver.Instance ?? FindAnyObjectByType<AprilTagTrackingReceiver>();
        }
    }
}
