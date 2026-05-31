using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Shows a small face-locked warning when the electrode is almost spent.
///
/// Setup:
///   1. Attach to any persistent GameObject (e.g. ARco or the XR Rig).
///   2. Assign electrodeConsumer (auto-found if empty).
///   3. Assign warningCanvas — a WorldSpace Canvas that is a CHILD of the Main Camera.
///      The canvas automatically follows the player's gaze.
///      If left null, the canvas is created automatically at Start.
///
/// The warning fades in smoothly and pulses once visible.
/// It disappears the moment the electrode is replaced.
/// </summary>
[DisallowMultipleComponent]
public class ElectrodeWarningUI : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("References")]
    [SerializeField] private ElectrodeConsumer electrodeConsumer;
    [Tooltip("WorldSpace Canvas child of Main Camera. Auto-created if null.")]
    [SerializeField] private Canvas warningCanvas;

    [Header("Thresholds")]
    [Tooltip("Fraction remaining at which the warning appears (0.25 = 25% left).")]
    [SerializeField] [Range(0.05f, 0.5f)] private float showThreshold  = 0.25f;
    [Tooltip("Fraction at which the warning pulses fastest (critical zone).")]
    [SerializeField] [Range(0.01f, 0.2f)] private float criticalThreshold = 0.10f;

    [Header("Canvas placement (auto-created canvas only)")]
    [Tooltip("Local position relative to the camera. Bottom-right corner, slightly inset.")]
    [SerializeField] private Vector3 canvasLocalPosition = new Vector3(0.09f, -0.07f, 0.35f);
    [SerializeField] private Vector2 canvasSize = new Vector2(180f, 38f);
    [Tooltip("World-space scale (meters). 0.00065 keeps it small enough to not overlap HUD.")]
    [SerializeField] private float   canvasScale = 0.00065f;

    [Header("Pulse")]
    [SerializeField] private float pulseSpeedNormal   = 1.5f;
    [SerializeField] private float pulseSpeedCritical = 4.0f;
    [SerializeField] [Range(0.3f, 1f)] private float pulseMinAlpha = 0.45f;

    // ── Private ───────────────────────────────────────────────────────────────

    private CanvasGroup   _group;
    private TextMeshProUGUI _label;
    private Image         _icon;
    private float         _currentAlpha;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        if (electrodeConsumer == null)
            electrodeConsumer = FindAnyObjectByType<ElectrodeConsumer>();

        if (warningCanvas == null)
            BuildCanvas();

        if (warningCanvas != null)
        {
            _group = warningCanvas.GetComponent<CanvasGroup>();
            if (_group == null) _group = warningCanvas.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            warningCanvas.gameObject.SetActive(true);
        }
    }

    private void Update()
    {
        if (electrodeConsumer == null || _group == null) return;

        float fraction = electrodeConsumer.FractionRemaining;
        bool  show     = fraction < showThreshold;

        // Target alpha: 0 when hidden, pulsing when visible
        float targetAlpha;
        if (!show)
        {
            targetAlpha = 0f;
        }
        else
        {
            float speed = Mathf.Lerp(pulseSpeedNormal, pulseSpeedCritical,
                                     Mathf.InverseLerp(showThreshold, criticalThreshold, fraction));
            float pulse = (Mathf.Sin(Time.time * speed * Mathf.PI * 2f) + 1f) * 0.5f;
            targetAlpha = Mathf.Lerp(pulseMinAlpha, 1f, pulse);
        }

        // Smooth fade
        _currentAlpha = Mathf.MoveTowards(_currentAlpha, targetAlpha, Time.deltaTime * 4f);
        _group.alpha  = _currentAlpha;

        // Update label — compact single line
        if (_label != null && show)
        {
            int pct = Mathf.CeilToInt(fraction * 100f);
            _label.text = fraction < criticalThreshold
                ? $"Electrodo: ¡agotado! ({pct}%)"
                : $"Electrodo bajo ({pct}%)";
        }
    }

    // ── Canvas builder ────────────────────────────────────────────────────────

    private void BuildCanvas()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogWarning("[ElectrodeWarningUI] No main camera found."); return; }

        // Root canvas GO — child of camera so it follows the head
        var canvasGO = new GameObject("ElectrodeWarningCanvas");
        canvasGO.transform.SetParent(cam.transform, false);
        canvasGO.transform.localPosition = canvasLocalPosition;
        canvasGO.transform.localRotation = Quaternion.identity;
        canvasGO.transform.localScale    = Vector3.one * canvasScale;

        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = cam;

        var rt = canvasGO.GetComponent<RectTransform>();
        rt.sizeDelta = canvasSize;

        canvasGO.AddComponent<CanvasScaler>();
        canvasGO.AddComponent<GraphicRaycaster>().enabled = false;

        // Dark pill background
        var bgGO = new GameObject("BG");
        bgGO.transform.SetParent(canvasGO.transform, false);
        var bgRT = bgGO.AddComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero;
        bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = Vector2.zero;
        bgRT.offsetMax = Vector2.zero;
        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = new Color(0.04f, 0.04f, 0.04f, 0.78f);
        bgImg.raycastTarget = false;

        // Small colored dot indicator (left side)
        var iconGO = new GameObject("Dot");
        iconGO.transform.SetParent(canvasGO.transform, false);
        var iconRT = iconGO.AddComponent<RectTransform>();
        iconRT.anchorMin        = new Vector2(0f, 0.5f);
        iconRT.anchorMax        = new Vector2(0f, 0.5f);
        iconRT.pivot            = new Vector2(0f, 0.5f);
        iconRT.anchoredPosition = new Vector2(8f, 0f);
        iconRT.sizeDelta        = new Vector2(18f, 18f);
        _icon = iconGO.AddComponent<Image>();
        _icon.color = new Color(1f, 0.22f, 0.05f, 1f);
        _icon.raycastTarget = false;

        // Compact single-line text label
        var textGO = new GameObject("Label");
        textGO.transform.SetParent(canvasGO.transform, false);
        var textRT = textGO.AddComponent<RectTransform>();
        textRT.anchorMin = new Vector2(0f, 0f);
        textRT.anchorMax = new Vector2(1f, 1f);
        textRT.offsetMin = new Vector2(32f, 2f);
        textRT.offsetMax = new Vector2(-6f, -2f);

        _label = textGO.AddComponent<TextMeshProUGUI>();
        _label.text               = "Electrodo bajo";
        _label.fontSize           = 13f;
        _label.color              = Color.white;
        _label.alignment          = TextAlignmentOptions.MidlineLeft;
        _label.raycastTarget      = false;
        _label.enableWordWrapping = false;
        _label.overflowMode       = TextOverflowModes.Ellipsis;

        warningCanvas = canvas;

        Debug.Log("[ElectrodeWarningUI] Canvas created as child of Main Camera.");
    }
}
