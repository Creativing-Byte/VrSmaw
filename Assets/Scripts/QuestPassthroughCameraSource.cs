using System;
using UnityEngine;

[DisallowMultipleComponent]
public class QuestPassthroughCameraSource : MonoBehaviour
{
    [Serializable]
    public struct CameraFrame
    {
        public long timestampMs;
        public int width;
        public int height;
        public Texture texture;
        public Matrix4x4 cameraLocalToWorld;
    }

    [Header("Permissions")]
    [SerializeField]
    private QuestCameraPermissionGate permissionGate;

    [Header("Camera Selection")]
    [SerializeField]
    private bool autoStart = true;

    [SerializeField]
    private string preferredDeviceNameContains = "Meta";

    [SerializeField]
    private int requestedWidth = 1280;

    [SerializeField]
    private int requestedHeight = 960;

    [SerializeField]
    private int requestedFps = 30;

    [SerializeField]
    private bool verboseLogging;

    [Header("References")]
    [SerializeField]
    private Transform xrCameraTransform;

    [Header("Debug")]
    [SerializeField]
    private string selectedDeviceName;

    [SerializeField]
    private bool isStreaming;

    [SerializeField]
    private int frameWidth;

    [SerializeField]
    private int frameHeight;

    public Texture PreviewTexture => _webCamTexture;
    public bool IsStreaming => isStreaming;

    public event Action<CameraFrame> FrameUpdated;

    private WebCamTexture _webCamTexture;
    private WebCamDevice? _selectedDevice;

    private void Start()
    {
        EnsureDependencies();

        if (autoStart)
        {
            StartStream();
        }
    }

    private void Update()
    {
        if (_webCamTexture == null && autoStart)
        {
            EnsureDependencies();

            if (permissionGate == null)
            {
                StartStream();
            }
            else
            {
                permissionGate.RefreshPermissionState();
                if (permissionGate.HasPermission)
                {
                    StartStream();
                }
            }
        }

        if (_webCamTexture == null || !_webCamTexture.isPlaying || !_webCamTexture.didUpdateThisFrame)
        {
            return;
        }

        frameWidth = _webCamTexture.width;
        frameHeight = _webCamTexture.height;
        isStreaming = frameWidth > 16 && frameHeight > 16;

        var frame = new CameraFrame
        {
            timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            width = frameWidth,
            height = frameHeight,
            texture = _webCamTexture,
            cameraLocalToWorld = xrCameraTransform != null ? xrCameraTransform.localToWorldMatrix : Matrix4x4.identity
        };

        FrameUpdated?.Invoke(frame);
    }

    private void OnDisable()
    {
        StopStream();
    }

    private void OnDestroy()
    {
        StopStream();
    }

    public bool StartStream()
    {
        EnsureDependencies();

        if (permissionGate != null && !permissionGate.RequestPermissionIfNeeded())
        {
            return false;
        }

        if (_webCamTexture != null && _webCamTexture.isPlaying)
        {
            return true;
        }

        if (!TrySelectDevice(out var device))
        {
            if (verboseLogging)
            {
                Debug.LogWarning("QuestPassthroughCameraSource could not find a camera device.");
            }

            return false;
        }

        _selectedDevice = device;
        selectedDeviceName = device.name;
        _webCamTexture = new WebCamTexture(device.name, requestedWidth, requestedHeight, requestedFps);
        _webCamTexture.Play();

        if (verboseLogging)
        {
            Debug.Log($"QuestPassthroughCameraSource started '{device.name}' at requested {requestedWidth}x{requestedHeight}@{requestedFps}");
        }

        return true;
    }

    public void StopStream()
    {
        if (_webCamTexture == null)
        {
            return;
        }

        if (_webCamTexture.isPlaying)
        {
            _webCamTexture.Stop();
        }

        Destroy(_webCamTexture);
        _webCamTexture = null;
        isStreaming = false;
        frameWidth = 0;
        frameHeight = 0;
    }

    private void EnsureDependencies()
    {
        if (permissionGate == null)
        {
            permissionGate = FindAnyObjectByType<QuestCameraPermissionGate>();
        }

        if (xrCameraTransform == null && Camera.main != null)
        {
            xrCameraTransform = Camera.main.transform;
        }
    }

    private bool TrySelectDevice(out WebCamDevice device)
    {
        var devices = WebCamTexture.devices;
        if (devices == null || devices.Length == 0)
        {
            device = default;
            return false;
        }

        var preferred = preferredDeviceNameContains?.Trim();
        if (!string.IsNullOrEmpty(preferred))
        {
            foreach (var candidate in devices)
            {
                if (candidate.name.IndexOf(preferred, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    device = candidate;
                    return true;
                }
            }
        }

        device = devices[0];
        return true;
    }
}
