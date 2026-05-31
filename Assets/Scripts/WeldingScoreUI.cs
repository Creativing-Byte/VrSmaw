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

    [Header("Behaviour")]
    [Tooltip("When true the UI updates itself from WeldingEvaluator every frame.")]
    [SerializeField] private bool autoUpdate = true;

    // ── Private state ─────────────────────────────────────────────────────────

    private bool _wasSessionActive;
    private ArduinoBridgeReceiver _bridge;
    private string _resultsTitleOverride;

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

        _bridge = ArduinoBridgeReceiver.Instance ?? FindAnyObjectByType<ArduinoBridgeReceiver>();

        if (resultsCloseButton != null)
            resultsCloseButton.onClick.AddListener(HandleCloseResults);

        // Ensure panels have the correct world-space scale.
        // The scene stores some canvases at scale 0; we normalise here so
        // SetActive(true) makes them visible without any extra animation.
        EnsureCanvasScale(hudPanel);
        EnsureCanvasScale(resultsPanel);

        Hide();
    }

    private static void EnsureCanvasScale(GameObject panel)
    {
        if (panel == null) return;
        if (panel.transform.localScale == Vector3.zero)
            panel.transform.localScale = Vector3.one * CanvasScale;
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
    }

    public void ShowResults()
    {
        SetActive(hudPanel,     false);
        SetActive(resultsPanel, true);
        RefreshResults();
    }

    public void Hide()
    {
        SetActive(hudPanel,     false);
        SetActive(resultsPanel, false);
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

    // ── HUD refresh ──────────────────────────────────────────────────────────

    private void RefreshHUD()
    {
        if (evaluator == null) return;

        var score = evaluator.OverallScore;

        // Score text + colour
        if (hudScoreText != null)
        {
            hudScoreText.text  = $"{score:F0}<size=60%>%</size>";
            hudScoreText.color = ScoreColour(score);
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
