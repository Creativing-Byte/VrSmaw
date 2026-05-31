using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

[DisallowMultipleComponent]
public class QuestCameraPermissionGate : MonoBehaviour
{
    [SerializeField]
    private bool requestOnStart = true;

    [SerializeField]
    private bool verboseLogging = true;

    public bool HasPermission { get; private set; }

    public void Start()
    {
        if (requestOnStart)
        {
            RequestPermissionIfNeeded();
        }
        else
        {
            RefreshPermissionState();
        }
    }

    public bool RequestPermissionIfNeeded()
    {
        RefreshPermissionState();
        if (HasPermission)
        {
            Log("Quest camera permission already granted.");
            return true;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        Permission.RequestUserPermission(Permission.Camera);
        Log("Requested Android CAMERA permission for Quest passthrough camera access.");
#else
        HasPermission = true;
        Log("Quest camera permission is treated as granted in the editor.");
#endif

        return HasPermission;
    }

    public void RefreshPermissionState()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        HasPermission = Permission.HasUserAuthorizedPermission(Permission.Camera);
#else
        HasPermission = true;
#endif
    }

    private void Log(string message)
    {
        if (verboseLogging)
        {
            Debug.Log(message);
        }
    }
}
