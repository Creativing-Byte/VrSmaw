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
    [Tooltip("Fraction remaining at which the warning chip appears (0.35 = 35% left).")]
    [SerializeField] [Range(0.05f, 0.5f)] private float showThreshold  = 0.35f;
    [Tooltip("Fraction at which the warning pulses fastest and turns red. " +
             "Should be just above the spentFraction in ElectrodeConsumer (default 0.22).")]
    [SerializeField] [Range(0.01f, 0.4f)] private float criticalThreshold = 0.22f;

    [Header("Canvas placement (enforced on both auto-built and Inspector-assigned canvases)")]
    [Tooltip("Local position relative to the camera. Bottom-right corner, slightly inset.")]
    [SerializeField] private Vector3 canvasLocalPosition = new Vector3(0.13f, -0.10f, 0.40f);
    [SerializeField] private Vector2 canvasSize = new Vector2(170f, 32f);
    [Tooltip("World-space scale. 0.00055 ≈ a 9.4cm × 1.8cm chip at 40cm — clearly readable but unobtrusive.")]
    [SerializeField] private float   canvasScale = 0.00055f;

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
            // Always enforce compact layout — fixes oversized Inspector-assigned canvases.
            EnforceCanvasLayout();

            _group = warningCanvas.GetComponent<CanvasGroup>();
            if (_group == null) _group = warningCanvas.gameObject.AddComponent<CanvasGroup>();
            _group.interactable   = false;
            _group.blocksRaycasts = false;
            _group.alpha = 0f;
            warningCanvas.gameObject.SetActive(true);
        }
    }

    /// <summary>
    /// Reparents the canvas to the main camera and forces the compact pill layout.
    /// Called after both the auto-built and Inspector-assigned canvas paths.
    /// </summary>
    private void EnforceCanvasLayout()
    {
        var cam = Camera.main;
        if (cam == null) return;

        var t = warningCanvas.transform;

        // Reparent to camera if not already (Inspector canvases are often scene-root)
        if (t.parent != cam.transform)
        {
            t.SetParent(cam.transform, false);
        }

        t.localPosition = canvasLocalPosition;
        t.localRotation = Quaternion.identity;
        t.localScale    = Vector3.one * canvasScale;

        // Force canvas mode
        warningCanvas.renderMode = RenderMode.WorldSpace;
        warningCanvas.worldCamera = cam;

        // Force compact size
        var rt = warningCanvas.GetComponent<RectTransform>();
        if (rt != null) rt.sizeDelta = canvasSize;
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

        // Update label and accent colour
        if (_label != null && show)
        {
            int pct = Mathf.CeilToInt(fraction * 100f);
            bool critical = fraction < criticalThreshold;
            _label.text = critical
                ? $"<b>⚡ ¡Electrodo crítico!</b>  {pct}%"
                : $"⚡ Electrodo bajo  {pct}%";
            UpdateAccentColour(fraction);
        }
    }

    // ── Canvas builder ────────────────────────────────────────────────────────

    private void BuildCanvas()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogWarning("[ElectrodeWarningUI] No main camera found."); return; }

        var canvasGO = new GameObject("ElectrodeWarningCanvas");
        canvasGO.transform.SetParent(cam.transform, false);

        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode  = RenderMode.WorldSpace;
        canvas.worldCamera = cam;

        canvasGO.AddComponent<CanvasScaler>();
        canvasGO.AddComponent<GraphicRaycaster>().enabled = false;

        warningCanvas = canvas;
        // EnforceCanvasLayout() is called right after BuildCanvas() in Start() and will
        // apply position/scale/size, so nothing more is needed here.

        // ── Dark glass background ─────────────────────────────────────────────
        var bgGO = new GameObject("BG");
        bgGO.transform.SetParent(canvasGO.transform, false);
        var bgRT = bgGO.AddComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero;
        bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = Vector2.zero;
        bgRT.offsetMax = Vector2.zero;
        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = new Color(0.06f, 0.04f, 0.02f, 0.88f);
        bgImg.raycastTarget = false;

        // ── Left accent bar (colour-coded warning) ────────────────────────────
        var accentGO = new GameObject("Accent");
        accentGO.transform.SetParent(canvasGO.transform, false);
        var accentRT = accentGO.AddComponent<RectTransform>();
        accentRT.anchorMin        = new Vector2(0f, 0f);
        accentRT.anchorMax        = new Vector2(0f, 1f);
        accentRT.pivot            = new Vector2(0f, 0.5f);
        accentRT.anchoredPosition = Vector2.zero;
        accentRT.sizeDelta        = new Vector2(5f, 0f);
        _icon = accentGO.AddComponent<Image>();
        _icon.color = new Color(1f, 0.55f, 0.05f, 1f);   // amber by default; turns red at critical
        _icon.raycastTarget = false;

        // ── Text label ────────────────────────────────────────────────────────
        var textGO = new GameObject("Label");
        textGO.transform.SetParent(canvasGO.transform, false);
        var textRT = textGO.AddComponent<RectTransform>();
        textRT.anchorMin = new Vector2(0f, 0f);
        textRT.anchorMax = new Vector2(1f, 1f);
        textRT.offsetMin = new Vector2(10f, 2f);
        textRT.offsetMax = new Vector2(-4f, -2f);

        _label = textGO.AddComponent<TextMeshProUGUI>();
        _label.text               = "⚡ Electrodo bajo";
        _label.fontSize           = 11f;
        _label.color              = Color.white;
        _label.alignment          = TextAlignmentOptions.MidlineLeft;
        _label.raycastTarget      = false;
        _label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        _label.overflowMode       = TextOverflowModes.Ellipsis;

        Debug.Log("[ElectrodeWarningUI] Chip canvas built.");
    }

    // ── Colour update during pulse ────────────────────────────────────────────

    private void UpdateAccentColour(float fraction)
    {
        if (_icon == null) return;
        _icon.color = fraction < criticalThreshold
            ? new Color(0.92f, 0.12f, 0.05f, 1f)   // red — critical
            : new Color(1.00f, 0.55f, 0.05f, 1f);   // amber — warning
    }
}
