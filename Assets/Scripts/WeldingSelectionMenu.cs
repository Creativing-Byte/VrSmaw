using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Pre-session selection menu shown before training begins.
///
/// The student chooses:
///   1. Electrode  (E6013 3/32", 1/8", 5/32") — also via physical buttons on the clamp
///   2. Exercise   (P1-U, P2-T, P3-Cuña, P4-V, P5-Cilindro)
///
/// Physical electrode buttons are received through ArduinoBridgeReceiver.electrodeButtonIndex
/// (field 10 in the CSV protocol).  Unity detects the rising edge (0 → N) and highlights
/// the corresponding button automatically.
///
/// Call flow:
///   Show() at app start → student selects → StartSession() → evaluator.BeginSession()
///   After EndSession() WeldingScoreUI fires; clicking "NUEVA SESIÓN" calls Show() again.
/// </summary>
[DisallowMultipleComponent]
public class WeldingSelectionMenu : MonoBehaviour
{
    // ── Types ─────────────────────────────────────────────────────────────────

    // ── Single-figure setup: only the T-joint is available ───────────────────
    // All five exercise slots are kept so existing Inspector button arrays still
    // compile, but only the first entry is exposed to the student.  Buttons
    // beyond ExerciseOrder.Length are hidden at runtime in WireExerciseButtons().

    private static readonly WeldingEvaluator.ExerciseType[] ExerciseOrder =
    {
        WeldingEvaluator.ExerciseType.P2_T,
    };

    private static readonly string[] ExerciseLabels =
    {
        "P2 – Unión en T",
    };

    private static readonly string[] ExerciseDescriptions =
    {
        "<b>Unión en T · Filete doble</b>\n" +
        "Suelda los dos cordones de filete de la pieza T:\n" +
        "primero el <b>frente</b>, luego gira la pieza para el <b>reverso</b>.\n\n" +
        "<size=85%>Evalúa: Ángulo de trabajo 45° ±5° · Continuidad · " +
        "Cobertura del seam · Velocidad de avance</size>",
    };

    private static readonly string[] ElectrodeLabels =
    {
        "3/32\"\n2.4 mm",
        "1/8\"\n3.2 mm",
        "5/32\"\n4.0 mm"
    };

    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Systems")]
    [SerializeField] private WeldingEvaluator  evaluator;
    [SerializeField] private WeldingScoreUI    scoreUI;
    [SerializeField] private ArduinoBridgeReceiver bridge;
    [SerializeField] private WeldingTrainingFlowController flowController;

    [Header("Panel root")]
    [SerializeField] private GameObject menuPanel;

    [Header("Electrode row (3 buttons)")]
    [SerializeField] private Button[]          electrodeButtons  = new Button[3];
    [SerializeField] private Image[]           electrodeBGs      = new Image[3];
    [SerializeField] private TextMeshProUGUI[] electrodeTexts    = new TextMeshProUGUI[3];

    [Header("Exercise row (5 buttons)")]
    [SerializeField] private Button[]          exerciseButtons   = new Button[5];
    [SerializeField] private Image[]           exerciseBGs       = new Image[5];
    [SerializeField] private TextMeshProUGUI[] exerciseTexts     = new TextMeshProUGUI[5];

    [Header("Start button")]
    [SerializeField] private Button            startButton;

    [Header("Status text")]
    [SerializeField] private TextMeshProUGUI   statusText;

    [Header("Exercise description")]
    [Tooltip("Optional TMP label that shows a description of the selected exercise.")]
    [SerializeField] private TextMeshProUGUI exerciseDescriptionText;

    [Header("Sensor status")]
    [Tooltip("Optional TMP label driven by WeldingSensorStatusUI (auto-found).")]
    [SerializeField] private TextMeshProUGUI sensorStatusText;

    [Header("World Space Placement")]
    [SerializeField] private bool anchorMenuToCameraOnShow = true;
    [Tooltip("Keep rotating the menu so it always faces the camera. Disable to anchor once and stay fixed.")]
    [SerializeField] private bool keepMenuFacingCamera = false;
    [SerializeField] private Vector3 menuOffsetFromCamera = new Vector3(0f, -0.08f, 1.05f);
    [SerializeField] private Vector3 menuEulerOffset;
    [Tooltip("Seconds to wait after Show() before anchoring. Gives XR time to deliver a valid head pose.")]
    [SerializeField] private float anchorDelaySeconds = 0.5f;

    [Header("Colours")]
    [SerializeField] private Color colorSelected = new Color(0.15f, 0.50f, 0.90f);
    [SerializeField] private Color colorDefault  = new Color(0.18f, 0.18f, 0.22f);
    [SerializeField] private Color colorStart    = new Color(0.10f, 0.62f, 0.28f);
    [SerializeField] private Color colorPhysical = new Color(0.85f, 0.55f, 0.05f);

    // ── State ─────────────────────────────────────────────────────────────────

    [SerializeField] private int _selectedElectrode;
    [SerializeField] private int _selectedExercise;

    private int  _prevPhysicalBtn;
    private int  _pendingPhysicalElectrode = -1;
    private bool _menuAnchoredToCamera;
    private bool _waitingForAnchor;
    private float _anchorTimer;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        if (evaluator == null) evaluator = FindAnyObjectByType<WeldingEvaluator>();
        if (scoreUI   == null) scoreUI   = FindAnyObjectByType<WeldingScoreUI>();
        if (bridge    == null) bridge    = ArduinoBridgeReceiver.Instance
                                        ?? FindAnyObjectByType<ArduinoBridgeReceiver>();
        if (flowController == null) flowController = FindAnyObjectByType<WeldingTrainingFlowController>();

        WireElectrodeButtons();
        WireExerciseButtons();

        if (startButton != null)
            startButton.onClick.AddListener(StartSession);

        if (bridge != null)
            bridge.TelemetryUpdated += OnTelemetry;

        // Hide immediately in Awake — before ANY Start() runs.
        // This prevents the menu from flashing whether the onboarding is shown
        // (WeldingOnboardingUI.ShowOnboarding keeps it hidden) or already done
        // (WeldingOnboardingUI.HideOnboarding calls Show() in its own Start()).
        if (menuPanel != null) menuPanel.SetActive(false);
    }

    private void OnDestroy()
    {
        if (bridge != null)
            bridge.TelemetryUpdated -= OnTelemetry;
    }

    private void Start()
    {
        // Visual refresh only — visibility is driven by WeldingOnboardingUI.
        RefreshElectrodeRow();
        RefreshExerciseRow();
        RefreshExerciseDescription();
    }

    private void Update()
    {
        // Resolve physical button selection on the main thread
        if (_pendingPhysicalElectrode >= 0)
        {
            ApplyElectrodeSelection(_pendingPhysicalElectrode, fromPhysical: true);
            _pendingPhysicalElectrode = -1;
        }

        if (menuPanel != null && menuPanel.activeInHierarchy)
        {
            // ── Startup delay: wait before placing so XR pose is valid ──────
            if (_waitingForAnchor)
            {
                _anchorTimer += Time.deltaTime;
                if (_anchorTimer >= anchorDelaySeconds)
                {
                    _waitingForAnchor     = false;
                    _menuAnchoredToCamera = AlignMenuToCamera(forcePosition: true);
                }
            }
            else if (anchorMenuToCameraOnShow && !_menuAnchoredToCamera)
            {
                _menuAnchoredToCamera = AlignMenuToCamera(forcePosition: true);
            }
            else if (keepMenuFacingCamera)
            {
                AlignMenuToCamera(forcePosition: false);
            }
        }

        // Lazily find bridge in case it was created after Awake
        if (bridge == null)
        {
            bridge = ArduinoBridgeReceiver.Instance ?? FindAnyObjectByType<ArduinoBridgeReceiver>();
            if (bridge != null)
                bridge.TelemetryUpdated += OnTelemetry;
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Show()
    {
        _menuAnchoredToCamera = false;
        _waitingForAnchor     = anchorMenuToCameraOnShow;   // start delay window
        _anchorTimer          = 0f;

        if (menuPanel != null) menuPanel.SetActive(true);
        if (scoreUI   != null) scoreUI.Hide();
        UpdateStatus();
    }

    public void Hide()
    {
        _menuAnchoredToCamera = false;
        if (menuPanel != null) menuPanel.SetActive(false);
    }

    // ── Public read-only state (used by WeldingHudUI) ─────────────────────────

    public int    SelectedElectrodeIndex => _selectedElectrode;
    public string SelectedElectrodeLabel => (_selectedElectrode >= 0 && _selectedElectrode < ElectrodeLabels.Length)
                                          ? ElectrodeLabels[_selectedElectrode].Replace("\n", " ")
                                          : "—";
    public string SelectedExerciseLabel  => (_selectedExercise >= 0 && _selectedExercise < ExerciseLabels.Length)
                                          ? ExerciseLabels[_selectedExercise]
                                          : "—";

    // ── Selection logic ───────────────────────────────────────────────────────

    private void SelectElectrode(int index)    => ApplyElectrodeSelection(index, false);
private void SelectExercise(int index)
    {
        _selectedExercise = Mathf.Clamp(index, 0, ExerciseOrder.Length - 1);
        RefreshExerciseRow();
        RefreshExerciseDescription();
        UpdateStatus();
    }

    private void ApplyElectrodeSelection(int index, bool fromPhysical)
    {
        _selectedElectrode = Mathf.Clamp(index, 0, electrodeButtons.Length - 1);
        RefreshElectrodeRow(fromPhysical ? _selectedElectrode : -1);
        UpdateStatus();
    }

    private void StartSession()
    {
        if (evaluator == null) return;

        if (flowController != null && flowController.enabled && flowController.GuidedModeEnabled)
        {
            flowController.BeginGuidedSequence(_selectedElectrode, ExerciseOrder[_selectedExercise]);
        }
        else
        {
            evaluator.SelectElectrode(_selectedElectrode);
            evaluator.SelectExercise(ExerciseOrder[_selectedExercise]);
            evaluator.BeginSession();
        }

        Hide();
    }

    // ── Physical button handler ───────────────────────────────────────────────

    // Called from ArduinoBridgeReceiver.Update() — already on main thread.
    private void OnTelemetry(ArduinoBridgeReceiver.WeldSensorTelemetry t)
    {
        var btn = t.electrodeButtonIndex;

        // Rising edge: only react when value changes from 0 → N
        if (btn > 0 && btn != _prevPhysicalBtn)
        {
            _pendingPhysicalElectrode = btn - 1; // 1-indexed → 0-indexed
        }

        _prevPhysicalBtn = btn;
    }

    // ── UI refresh ────────────────────────────────────────────────────────────

    private void RefreshExerciseDescription()
    {
        if (exerciseDescriptionText == null) return;
        if (_selectedExercise >= 0 && _selectedExercise < ExerciseDescriptions.Length)
            exerciseDescriptionText.text = ExerciseDescriptions[_selectedExercise];
    }

    
private void RefreshElectrodeRow(int physicalHighlight = -1)
    {
        for (int i = 0; i < electrodeButtons.Length; i++)
        {
            if (electrodeBGs == null || i >= electrodeBGs.Length || electrodeBGs[i] == null) continue;

            if (i == _selectedElectrode)
                electrodeBGs[i].color = (physicalHighlight == i) ? colorPhysical : colorSelected;
            else
                electrodeBGs[i].color = colorDefault;
        }
    }

    private void RefreshExerciseRow()
    {
        for (int i = 0; i < exerciseButtons.Length; i++)
        {
            if (exerciseBGs == null || i >= exerciseBGs.Length || exerciseBGs[i] == null) continue;
            exerciseBGs[i].color = i == _selectedExercise ? colorSelected : colorDefault;
        }
    }

    private void UpdateStatus()
    {
        if (statusText == null) return;

        var electrodeName = (electrodeTexts != null && _selectedElectrode < electrodeTexts.Length
                            && electrodeTexts[_selectedElectrode] != null)
                          ? electrodeTexts[_selectedElectrode].text.Replace("\n", " ")
                          : _selectedElectrode.ToString();

        // Single-figure setup: always T-joint, no multi-exercise sequence
        statusText.text = $"<b>Listo para soldar</b>  ·  E6013 {electrodeName}  ·  Unión en T";
    }

    private bool AlignMenuToCamera(bool forcePosition)
    {
        if (menuPanel == null) return false;

        var menuTransform = menuPanel.transform;
        var cameraTransform = Camera.main != null ? Camera.main.transform : null;
        if (menuTransform == null || cameraTransform == null) return false;

        if (forcePosition)
        {
            menuTransform.position =
                cameraTransform.position
                + cameraTransform.right * menuOffsetFromCamera.x
                + cameraTransform.up * menuOffsetFromCamera.y
                + cameraTransform.forward * menuOffsetFromCamera.z;
        }

        var lookDirection = menuTransform.position - cameraTransform.position;
        if (lookDirection.sqrMagnitude < 0.0001f)
        {
            lookDirection = cameraTransform.forward;
        }

        menuTransform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up)
                               * Quaternion.Euler(menuEulerOffset);

        return true;
    }

    // ── Button wiring ─────────────────────────────────────────────────────────

    private void WireElectrodeButtons()
    {
        for (int i = 0; i < electrodeButtons.Length; i++)
        {
            if (electrodeButtons[i] == null) continue;
            int idx = i;
            electrodeButtons[i].onClick.AddListener(() => SelectElectrode(idx));

            // Populate label if text array provided but text is empty
            if (electrodeTexts != null && idx < electrodeTexts.Length
                && electrodeTexts[idx] != null
                && string.IsNullOrWhiteSpace(electrodeTexts[idx].text))
            {
                electrodeTexts[idx].text = ElectrodeLabels[idx];
            }
        }
    }

    private void WireExerciseButtons()
    {
        for (int i = 0; i < exerciseButtons.Length; i++)
        {
            if (exerciseButtons[i] == null) continue;

            if (i < ExerciseOrder.Length)
            {
                // Active exercise button — always overwrite the label from code
                // so Inspector-saved stale text never shows.
                int idx = i;
                exerciseButtons[i].onClick.AddListener(() => SelectExercise(idx));

                if (exerciseTexts != null && idx < exerciseTexts.Length
                    && exerciseTexts[idx] != null)
                {
                    exerciseTexts[idx].text = ExerciseLabels[idx];
                }
            }
            else
            {
                // Hide buttons for exercises that no longer exist in the single-figure setup
                exerciseButtons[i].gameObject.SetActive(false);
            }
        }
    }
}
