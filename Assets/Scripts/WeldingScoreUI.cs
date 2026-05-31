using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Controls two World Space UI panels for the welding training:
///   - HUD panel: live score, arc status and timer (visible while session is active)
///   - Results panel: per-criterion report (visible after EndSession)
///
/// Assign both panels in the Inspector, then call ShowHUD() / ShowResults() / Hide()
/// or let them be driven automatically via AutoUpdate (polls WeldingEvaluator each frame).
/// </summary>
[DisallowMultipleComponent]
public class WeldingScoreUI : MonoBehaviour
{
    // ── HUD colours ───────────────────────────────────────────────────────────

    private static readonly Color ColorGood    = new Color(0.20f, 0.85f, 0.35f);
    private static readonly Color ColorWarning = new Color(1.00f, 0.75f, 0.10f);
    private static readonly Color ColorBad     = new Color(0.90f, 0.20f, 0.20f);
    private static readonly Color ColorArcOn   = new Color(0.20f, 0.85f, 0.35f);
    private static readonly Color ColorArcOff  = new Color(0.45f, 0.45f, 0.45f);

    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Evaluator")]
    [SerializeField] private WeldingEvaluator evaluator;
    [SerializeField] private WeldingSelectionMenu selectionMenu;
    [SerializeField] private WeldingTrainingFlowController flowController;

    [Header("HUD Panel")]
    [SerializeField] private GameObject     hudPanel;
    [SerializeField] private TextMeshProUGUI hudScoreText;       // e.g. "87%"
    [SerializeField] private TextMeshProUGUI hudExerciseText;    // e.g. "P1-U | E6013 3/32\""
    [SerializeField] private TextMeshProUGUI hudTimerText;       // e.g. "0:42"
    [SerializeField] private Image           hudArcIndicator;    // coloured dot
    [SerializeField] private TextMeshProUGUI hudArcLabel;        // "ARCO ●" / "ARCO ○"

    [Header("Results Panel")]
    [SerializeField] private GameObject      resultsPanel;
    [SerializeField] private TextMeshProUGUI resultsTitleText;   // "RESULTADO FINAL"
    [SerializeField] private TextMeshProUGUI resultsScoreText;   // large score
    [SerializeField] private TextMeshProUGUI resultsCriteriaText;// scrollable criterion list
    [SerializeField] private Button          resultsCloseButton; // returns to idle

    [Header("Other UIs to hide while Results are shown")]
    [Tooltip("The floating live HUD — hidden when the results panel is visible.")]
    [SerializeField] private WeldingHUDController hudController;
    [Tooltip("The permanent corner status bar — hidden when results are visible.")]
    [SerializeField] private WeldingHudUI         hudStatusBar;

    [Header("Behaviour")]
    [Tooltip("When true the UI updates itself from WeldingEvaluator every frame.")]
    [SerializeField] private bool autoUpdate = true;
    [Tooltip("Distance in metres the results panel floats in front of the player's camera.")]
    [SerializeField] private float resultsPanelDistance = 1.1f;
    [Tooltip("Vertical offset from eye level (negative = slightly below).")]
    [SerializeField] private float resultsPanelVerticalOffset = -0.05f;

    // ── Private state ─────────────────────────────────────────────────────────

    private bool _wasSessionActive;
    private ArduinoBridgeReceiver _bridge;
    private string _resultsTitleOverride;
    private Button _retryButton;   // created programmatically in Awake

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    // World-space canvas scale used by this project for all UI panels.
    // Results Canvas is stored at scale 0 in the scene file; we correct it on Awake.
    private const float CanvasScale = 0.001f;

    private void Awake()
    {
        if (evaluator == null)
            evaluator = FindAnyObjectByType<WeldingEvaluator>();
        if (selectionMenu == null)
            selectionMenu = FindAnyObjectByType<WeldingSelectionMenu>();
        if (flowController == null)
            flowController = FindAnyObjectByType<WeldingTrainingFlowController>();
        if (hudController == null)
            hudController = FindAnyObjectByType<WeldingHUDController>();
        if (hudStatusBar == null)
            hudStatusBar = FindAnyObjectByType<WeldingHudUI>();

        _bridge = ArduinoBridgeReceiver.Instance ?? FindAnyObjectByType<ArduinoBridgeReceiver>();

        // Ensure panels have the correct world-space scale.
        EnsureCanvasScale(hudPanel);
        EnsureCanvasScale(resultsPanel);

        // Wire close button (goes to menu / advances sequence)
        if (resultsCloseButton != null)
        {
            var label = resultsCloseButton.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = "MENÚ";
            resultsCloseButton.onClick.AddListener(HandleCloseResults);
        }

        // Create REINTENTAR button as a sibling of the close button
        BuildRetryButton();

        Hide();
    }

    private static void EnsureCanvasScale(GameObject panel)
    {
        if (panel == null) return;
        if (panel.transform.localScale == Vector3.zero)
            panel.transform.localScale = Vector3.one * CanvasScale;
    }

    private void BuildRetryButton()
    {
        if (resultsCloseButton == null) return;

        // Clone the close button to use its style
        var retryGO = Instantiate(resultsCloseButton.gameObject,
                                  resultsCloseButton.transform.parent);
        retryGO.name = "RetryButton";

        // Position to the LEFT of the close button
        var origRT  = resultsCloseButton.GetComponent<RectTransform>();
        var retryRT = retryGO.GetComponent<RectTransform>();
        retryRT.anchoredPosition = origRT.anchoredPosition
                                 + new Vector2(-(origRT.sizeDelta.x + 8f), 0f);

        // Label and accent colour
        var label = retryGO.GetComponentInChildren<TextMeshProUGUI>();
        if (label != null) label.text = "REINTENTAR";
        var img = retryGO.GetComponent<Image>();
        if (img != null) img.color = new Color(0.15f, 0.55f, 0.90f, 1f);

        _retryButton = retryGO.GetComponent<Button>();
        if (_retryButton != null)
        {
            _retryButton.onClick.RemoveAllListeners();
            _retryButton.onClick.AddListener(HandleRetry);
        }
    }

    private void Update()
    {
        if (!autoUpdate || evaluator == null) return;

        var sessionNow = evaluator.SessionActive;

        if (sessionNow)
        {
            if (!_wasSessionActive) ShowHUD();
            RefreshHUD();
        }
        else if (_wasSessionActive)
        {
            // Session just ended
            ShowResults();
        }

        _wasSessionActive = sessionNow;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void ShowHUD()
    {
        SetActive(hudPanel,     true);
        SetActive(resultsPanel, false);
        RestoreOtherUIs();
    }

    public void ShowResults()
    {
        // Anchor the results panel directly in front of the player
        AnchorResultsToCamera();

        SetActive(hudPanel,     false);
        SetActive(resultsPanel, true);

        // Hide everything else so only the results panel is visible
        HideOtherUIs();

        RefreshResults();
    }

    public void Hide()
    {
        SetActive(hudPanel,     false);
        SetActive(resultsPanel, false);
        RestoreOtherUIs();
    }

    // ── Camera anchoring ──────────────────────────────────────────────────────

    private void AnchorResultsToCamera()
    {
        if (resultsPanel == null) return;
        var cam = Camera.main?.transform;
        if (cam == null) return;

        var t = resultsPanel.transform;
        t.position = cam.position
                   + cam.forward * resultsPanelDistance
                   + cam.up      * resultsPanelVerticalOffset;

        // Face the panel toward the camera
        var look = t.position - cam.position;
        if (look.sqrMagnitude > 0.0001f)
            t.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
    }

    // ── Other-UI visibility ───────────────────────────────────────────────────

    private void HideOtherUIs()
    {
        if (hudController != null) hudController.SetForceHidden(true);
        if (hudStatusBar  != null) hudStatusBar.gameObject.SetActive(false);
    }

    private void RestoreOtherUIs()
    {
        if (hudController != null) hudController.SetForceHidden(false);
        if (hudStatusBar  != null) hudStatusBar.gameObject.SetActive(true);
    }

    public void SetResultsTitleOverride(string title)
    {
        _resultsTitleOverride = title;

        if (resultsPanel != null && resultsPanel.activeSelf)
            RefreshResults();
    }

    public void ClearResultsTitleOverride()
    {
        _resultsTitleOverride = null;

        if (resultsPanel != null && resultsPanel.activeSelf)
            RefreshResults();
    }

    private void HandleCloseResults()
    {
        if (flowController == null)
            flowController = FindAnyObjectByType<WeldingTrainingFlowController>();

        if (flowController != null && flowController.HandleResultsDismissedByUser())
            return;

        Hide();

        if (selectionMenu == null)
            selectionMenu = FindAnyObjectByType<WeldingSelectionMenu>();

        if (selectionMenu != null)
            selectionMenu.Show();
    }

    private void HandleRetry()
    {
        if (flowController == null)
            flowController = FindAnyObjectByType<WeldingTrainingFlowController>();

        Hide();

        if (flowController != null && flowController.RetryCurrentExercise())
            return;

        // Fallback: restart the same session directly
        if (evaluator != null)
            evaluator.BeginSession();
    }

    // ── HUD refresh ──────────────────────────────────────────────────────────

    private void RefreshHUD()
    {
        if (evaluator == null) return;

        // ── Score / coverage text ────────────────────────────────────────────
        // P2-T: show per-seam dwell-coverage instead of overall score.
        // During a live session all criteria default to 100 % (they are only
        // finalised at EndSession), so displaying OverallScore would always read
        // "100 %" and give a completely wrong impression of progress.
        if (evaluator.Exercise == WeldingEvaluator.ExerciseType.P2_T)
        {
            float[] perSeam = evaluator.GetPerSeamCoverages();
            if (hudScoreText != null)
            {
                if (perSeam != null && perSeam.Length >= 2)
                {
                    int pct0 = Mathf.RoundToInt(perSeam[0] * 100f);
                    int pct1 = Mathf.RoundToInt(perSeam[1] * 100f);
                    float avg = (perSeam[0] + perSeam[1]) * 50f;   // 0-100
                    hudScoreText.text  = $"<size=75%>F:</size>{pct0}%  <size=75%>R:</size>{pct1}%";
                    hudScoreText.color = ScoreColour(avg);
                }
                else
                {
                    // No coverage data yet — prompt the student to start welding
                    hudScoreText.text  = "Soldar →";
                    hudScoreText.color = ColorWarning;
                }
            }
        }
        else
        {
            var score = evaluator.OverallScore;
            if (hudScoreText != null)
            {
                hudScoreText.text  = $"{score:F0}<size=60%>%</size>";
                hudScoreText.color = ScoreColour(score);
            }
        }

        // Exercise / electrode label
        if (hudExerciseText != null)
        {
            var e = evaluator.ActiveElectrode;
            hudExerciseText.text = $"{evaluator.Exercise}  |  {e?.code ?? "—"}";
        }

        // Timer
        if (hudTimerText != null)
        {
            var elapsed = evaluator.SessionElapsedSeconds;
            var m = Mathf.FloorToInt(elapsed / 60f);
            var s = Mathf.FloorToInt(elapsed % 60f);
            hudTimerText.text = $"{m}:{s:D2}";
        }

        // Arc indicator
        var arcOn = IsArcActive();
        if (hudArcIndicator != null) hudArcIndicator.color = arcOn ? ColorArcOn : ColorArcOff;
        if (hudArcLabel != null)     hudArcLabel.text       = arcOn ? "ARCO  ●" : "ARCO  ○";
    }

    // ── Results refresh ───────────────────────────────────────────────────────

    private void RefreshResults()
    {
        if (evaluator == null) return;

        var score = evaluator.OverallScore;

        if (resultsTitleText != null)
            resultsTitleText.text = string.IsNullOrWhiteSpace(_resultsTitleOverride)
                ? "RESULTADO FINAL"
                : _resultsTitleOverride;

        if (resultsScoreText != null)
        {
            string grade    = LetterGrade(score);
            string gradeCol = GradeColourHex(score);
            // Large letter grade + numeric score on one line
            resultsScoreText.text  = $"<color={gradeCol}><b>{grade}</b></color>   {score:F0}%";
            resultsScoreText.color = Color.white;
        }

        if (resultsCriteriaText != null)
            resultsCriteriaText.text = BuildCriteriaList();
    }

    private string BuildCriteriaList()
    {
        var sb = new StringBuilder();
        bool any = false;

        for (int i = 1; i <= evaluator.CriteriaCount; i++)
        {
            var c = evaluator.GetCriterion(i);
            if (c == null || !c.applicable) continue;
            any = true;

            string icon     = CriterionIcon(c.id);
            string label    = CriterionLabel(c.id);
            string bar      = ScoreBar(c.score, 8);
            string scoreCol = GradeColourHex(c.score);
            string mark     = c.score >= 60f
                ? "<color=#44DD66>✓</color>"
                : "<color=#FF4444>✗</color>";

            // Row: mark  icon Label ░░░░░░░░ 87%
            sb.AppendLine(
                $"{mark} {icon} <b>{label,-14}</b>  {bar}  <color={scoreCol}>{c.score:F0}%</color>");

            // Detail line — smaller, dimmer
            if (!string.IsNullOrEmpty(c.details))
                sb.AppendLine($"   <size=72%><color=#888888>{c.details}</color></size>");

            sb.AppendLine();
        }

        if (!any)
            sb.AppendLine("<color=#777777>Sin criterios para este ejercicio.</color>");

        return sb.ToString();
    }

    // ── Formatting helpers ────────────────────────────────────────────────────

    private static string ScoreBar(float score, int length)
    {
        int filled = Mathf.Clamp(Mathf.RoundToInt(score / 100f * length), 0, length);
        string col  = GradeColourHex(score);
        return $"<color={col}>{new string('█', filled)}</color>" +
               $"<color=#2A2A2A>{new string('█', length - filled)}</color>";
    }

    private static string LetterGrade(float score)
    {
        if (score >= 90f) return "A";
        if (score >= 80f) return "B";
        if (score >= 70f) return "C";
        if (score >= 60f) return "D";
        return "F";
    }

    private static string GradeColourHex(float score)
    {
        if (score >= 80f) return "#44EE66";
        if (score >= 60f) return "#FFCC22";
        return "#FF4444";
    }

    // Short human-readable label per criterion (no technical numbers)
    private static string CriterionLabel(int id) => id switch
    {
        1  => "Rectitud",
        2  => "Uniformidad",
        3  => "Angulo 45°",
        4  => "Angulo 13°",
        5  => "Unif. cuña",
        6  => "Continuidad",
        7  => "Sin cortes",
        8  => "Cierre 360°",
        9  => "Angulo curva",
        10 => "Cierre laser",
        11 => "Estabilidad",
        12 => "Posicion",
        13 => "Cobertura",
        14 => "Velocidad",
        _  => $"Criterio {id}"
    };

    private static string CriterionIcon(int id) => id switch
    {
        1  => "─",   // straightness
        2  => "↕",   // height uniformity
        3  => "∠",   // angle
        4  => "∠",
        5  => "↕",
        6  => "▐",   // pause / continuity
        7  => "▐",
        8  => "↺",   // 360 closure
        9  => "~",   // curve
        10 => "⊙",   // laser closure
        11 => "◈",   // arc stability
        12 => "●",   // bead position
        13 => "↔",   // coverage
        14 => "→",   // travel speed
        _  => "·"
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool IsArcActive()
    {
        if (_bridge == null)
            _bridge = ArduinoBridgeReceiver.Instance ?? FindAnyObjectByType<ArduinoBridgeReceiver>();

        if (_bridge == null || !_bridge.TryGetLatest(out var t)) return false;

        var e = evaluator?.ActiveElectrode;
        if (e == null) return false;

        return t.laserDistanceMm >= e.arcMinMm && t.laserDistanceMm <= e.arcMaxMm;
    }

    private static Color ScoreColour(float score) =>
        score >= 80f ? ColorGood : score >= 60f ? ColorWarning : ColorBad;

    private static void SetActive(GameObject go, bool value)
    {
        if (go != null) go.SetActive(value);
    }
}
