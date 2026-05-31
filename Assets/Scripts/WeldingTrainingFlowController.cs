using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Manages the full guided training sequence (P1 → P2 → … → PN) and, before
/// each individual session, shows an in-world step-by-step guide panel with a
/// pulsing beacon that highlights the relevant piece/clamp in the scene.
///
/// Call flow:
///   WeldingSelectionMenu.StartSession()
///     → BeginGuidedSequence(electrode, startExercise)
///       → ShowGuideForCurrentExercise()    ← new: pre-session in-world guide
///         → user presses "INICIAR SESIÓN"
///           → BeginActualSession()         ← evaluator.BeginSession()
///             → HandleSessionEnded()
///               → results shown, then next exercise or FinishSequence()
/// </summary>
[DisallowMultipleComponent]
public class WeldingTrainingFlowController : MonoBehaviour
{
    // ── Exercise catalogue — single-figure setup (T-joint only) ──────────────
    // The app now has one physical piece: Figura T.
    // All training is P2_T (double-fillet on T-joint).

    private static readonly WeldingEvaluator.ExerciseType[] ExerciseOrder =
    {
        WeldingEvaluator.ExerciseType.P2_T,
    };

    private static readonly string[] ExerciseLabels =
    {
        "P2 · Unión en T"
    };

    // ── Inspector — Sequence ──────────────────────────────────────────────────

    [Header("References")]
    [SerializeField] private WeldingEvaluator    evaluator;
    [SerializeField] private WeldingScoreUI      scoreUI;
    [SerializeField] private WeldingSelectionMenu selectionMenu;

    [Header("Guided Flow")]
    [SerializeField] private bool  guidedModeEnabled          = true;
    [SerializeField] private float resultsDisplaySeconds      = 4f;
    [SerializeField] private float finalResultsDisplaySeconds = 6f;
    [SerializeField] private bool  autoReturnToMenuAtSequenceEnd = true;
    [Tooltip("When true the sequence waits for the player to press REINTENTAR or MENÚ " +
             "before advancing. The auto-advance timer is disabled.")]
    [SerializeField] private bool  requireUserInputToContinue = true;

    [Header("Optional Remediation")]
    [SerializeField] private bool  repeatExerciseOnLowScore;
    [SerializeField] [Range(0f,100f)] private float passingScoreThreshold = 60f;

    [Header("Runtime")]
    [SerializeField] private bool  sequenceActive;
    [SerializeField] private bool  awaitingAdvance;
    [SerializeField] private int   startExerciseIndex;
    [SerializeField] private int   currentExerciseIndex;
    [SerializeField] private int   selectedElectrodeIndex;
    [SerializeField] private float transitionTimer;

    // ── Inspector — In-world Guide ────────────────────────────────────────────

    [Header("In-World Guide Panel")]
    [SerializeField] private GameObject      guidePanel;
    [SerializeField] private TextMeshProUGUI guideTitleText;
    [SerializeField] private TextMeshProUGUI guideBodyText;
    [SerializeField] private TextMeshProUGUI guideStepIndicator;
    [SerializeField] private Button          guideNextButton;
    [SerializeField] private TextMeshProUGUI guideNextButtonLabel;
    [SerializeField] private Button          guideSkipButton;

    [Header("Beacon indicator")]
    [SerializeField] private GameObject beaconGo;

    [Header("Guide placement")]
    [SerializeField] private Vector3 guidePanelOffset = new Vector3(-0.38f, -0.04f, 1.0f);
    [SerializeField] private float   guideAnchorDelay = 0.4f;

    [Header("Exercise → Welding Piece")]
    [Tooltip("Name of the single welding piece GameObject in the scene.")]
    [SerializeField] private string[] exercisePieceNames = {
        "Figura T"
    };
    [SerializeField] private string clampGoName = "Mig";

    // ── Sequence public API ───────────────────────────────────────────────────

    public bool GuidedModeEnabled => guidedModeEnabled;
    public bool SequenceActive    => sequenceActive;
    public bool AwaitingAdvance   => awaitingAdvance;

    // ── Guide private state ───────────────────────────────────────────────────

    private readonly List<GuideStep> _guideSteps = new();
    private int   _guideStep       = -1;
    private bool  _guideAnchored;
    private bool  _guideWaitAnchor;
    private float _guideAnchorTimer;
    private bool  _prevArcActive;
    [Header("Arc auto-advance")]
    [SerializeField] private MigWelding _migWelding;

    private bool  _prevSessionActive;

    // Grab / tracking detection
    private XRGrabInteractable _pieceInteractable;
    private bool               _pieceWasGrabbed;
    private float              _grabConfirmTimer  = -1f;
    private const  float       GrabConfirmDelay   = 0.6f;
    private const  float       BeaconScale        = 0.025f;
    private const  float       PlacementRadius    = 0.20f;  // meters — how close is "placed"

    // Right controller tracking
    private readonly List<UnityEngine.XR.InputDevice> _xrDevices = new();
    private bool  _rightControllerWasTracked;
    private float _controllerConfirmTimer = -1f;
    private const float ControllerConfirmDelay = 0.5f;

    // Placement zone
    [Header("Placement zone (auto-created if null)")]
    [SerializeField] private GameObject _placementZoneGo;
    private Vector3 _targetPlacementPos;   // world pos where piece should land
    private bool    _pieceInHand;          // true while user is holding the piece

    // Beacon state
    private Color   _beaconDefaultColor = new Color(1f, 0.85f, 0.1f);
    private Color   _beaconConfirmColor = new Color(0.2f, 0.9f, 0.3f);
    private Vector3 _beaconBasePos;
    private bool    _beaconBasePosSet;

    // ── Types ─────────────────────────────────────────────────────────────────

    private class GuideStep
    {
        public string title;
        public string body;
        public string beaconTarget       = "";
        public bool   launchOnNext       = false;
    }

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        if (evaluator    == null) evaluator    = FindAnyObjectByType<WeldingEvaluator>();
        if (scoreUI      == null) scoreUI      = FindAnyObjectByType<WeldingScoreUI>();
        if (selectionMenu == null) selectionMenu = FindAnyObjectByType<WeldingSelectionMenu>();

        if (guideNextButton != null) guideNextButton.onClick.AddListener(OnGuideNext);
        if (guideSkipButton != null) guideSkipButton.onClick.AddListener(OnGuideSkip);

        HideGuide();
    }

    private void OnEnable()
    {
        if (evaluator == null) evaluator = FindAnyObjectByType<WeldingEvaluator>();
        if (evaluator != null)
        {
            evaluator.SessionStarted += HandleSessionStarted;
            evaluator.SessionEnded   += HandleSessionEnded;
        }
    }

    private void OnDisable()
    {
        if (evaluator != null)
        {
            evaluator.SessionStarted -= HandleSessionStarted;
            evaluator.SessionEnded   -= HandleSessionEnded;
        }
    }

    private void Update()
    {
        // ── Guide anchor delay ────────────────────────────────────────────────
        if (guidePanel != null && guidePanel.activeInHierarchy)
        {
            if (_guideWaitAnchor)
            {
                _guideAnchorTimer += Time.deltaTime;
                if (_guideAnchorTimer >= guideAnchorDelay)
                {
                    _guideWaitAnchor = false;
                    _guideAnchored   = AlignGuidePanel(true);
                }
            }
            else if (!_guideAnchored)
            {
                _guideAnchored = AlignGuidePanel(true);
            }

            // ── Beacon animation — small bob + subtle pulse ──────────────────
            if (beaconGo != null && beaconGo.activeInHierarchy)
            {
                if (!_beaconBasePosSet)
                {
                    _beaconBasePos    = beaconGo.transform.position;
                    _beaconBasePosSet = true;
                }
                float bob   = Mathf.Sin(Time.time * 3.5f) * 0.015f;
                float pulse = BeaconScale * (1f + 0.12f * Mathf.Sin(Time.time * 5f));
                beaconGo.transform.position   = _beaconBasePos + Vector3.up * bob;
                beaconGo.transform.localScale = Vector3.one * pulse;   // ← keeps scale at ~0.025
                beaconGo.transform.Rotate(Vector3.up, 45f * Time.deltaTime, Space.World);
            }

            // ── Step 0: right controller tracking (clamp is on the controller) ──
            if (_guideStep == 0)
            {
                UnityEngine.XR.InputDevices.GetDevicesAtXRNode(
                    UnityEngine.XR.XRNode.RightHand, _xrDevices);
                bool tracked = _xrDevices.Count > 0 && _xrDevices[0].isValid;

                if (tracked && !_rightControllerWasTracked)
                {
                    SetBeaconColor(_beaconConfirmColor);
                    _controllerConfirmTimer = ControllerConfirmDelay;
                }

                if (_controllerConfirmTimer > 0f)
                {
                    _controllerConfirmTimer -= Time.deltaTime;
                    if (_controllerConfirmTimer <= 0f)
                    {
                        _controllerConfirmTimer = -1f;
                        OnGuideNext();
                    }
                }

                if (!tracked && _rightControllerWasTracked)
                    SetBeaconColor(_beaconDefaultColor);

                _rightControllerWasTracked = tracked;
            }

            // ── Step 1: pick up piece → carry to zone → hold/place it there ──
            if (_guideStep == 1)
            {
                bool grabbed = _pieceInteractable != null && _pieceInteractable.isSelected;

                // Piece just picked up
                if (grabbed && !_pieceWasGrabbed)
                {
                    _pieceInHand = true;
                    _grabConfirmTimer = -1f;
                    if (beaconGo != null) beaconGo.SetActive(false);
                    ShowPlacementZone(_targetPlacementPos);
                    if (guideTitleText != null) guideTitleText.text = "Coloca la pieza en la zona";
                    if (guideBodyText  != null)
                        guideBodyText.text = "¡Bien! Lleva la pieza hacia\n" +
                            "el <b>círculo azul</b> y suéltala ahí.\n\n" +
                            "El círculo indica la posición de soldadura.";
                }

                // Check proximity CONTINUOUSLY — both while held and after release
                if (_pieceInHand && _grabConfirmTimer < 0f)
                {
                    var piecePos = _pieceInteractable != null
                        ? _pieceInteractable.transform.position
                        : _targetPlacementPos;

                    bool inZone = Vector3.Distance(piecePos, _targetPlacementPos) <= PlacementRadius;

                    if (inZone)
                    {
                        // Piece is in the zone — start confirm countdown
                        HidePlacementZone();
                        SetBeaconColor(_beaconConfirmColor);
                        if (guideTitleText != null) guideTitleText.text = "¡Posición correcta!";
                        if (guideBodyText  != null)
                            guideBodyText.text = "<color=#33DD55><b>✓</b></color>  Pieza en posición.\nAvanzando al siguiente paso…";
                        _grabConfirmTimer = GrabConfirmDelay;
                    }
                }

                // Piece released outside zone — reset and allow retry
                if (!grabbed && _pieceWasGrabbed && _pieceInHand && _grabConfirmTimer < 0f)
                {
                    _pieceInHand = false;
                    RefreshBeacon();
                    ShowPlacementZone(_targetPlacementPos);
                    if (guideTitleText != null) guideTitleText.text = "Agarra la pieza de trabajo";
                    if (guideBodyText  != null)
                        guideBodyText.text = "La pieza quedó fuera de la zona.\n" +
                            "Vuelve a agarrarla y colócala\nsobre el <b>círculo azul</b>.";
                }

                // Confirm countdown
                if (_grabConfirmTimer > 0f)
                {
                    _grabConfirmTimer -= Time.deltaTime;
                    if (_grabConfirmTimer <= 0f)
                    {
                        _grabConfirmTimer = -1f;
                        OnGuideNext();
                    }
                }

                _pieceWasGrabbed = grabbed;
            }

            // Auto-skip to session when arc fires during pre-session guide steps
            if (_migWelding == null) _migWelding = FindAnyObjectByType<MigWelding>();
            bool arcNow = _migWelding != null && _migWelding.ArcIsValid;
            if (arcNow && !_prevArcActive && _guideStep >= 1 && _guideStep <= 2)
            {
                if (evaluator != null && !evaluator.SessionActive)
                    BeginActualSession();
                HideGuide();
            }
            _prevArcActive = arcNow;


            // Auto-close when session ends (last step)
            if (_guideStep >= 0 && _guideStep < _guideSteps.Count)
            {
                bool sessionNow = evaluator != null && evaluator.SessionActive;
                if (_guideSteps[_guideStep].beaconTarget == "" // session-active step
                    && _prevSessionActive && !sessionNow)
                {
                    HideGuide();
                }
                _prevSessionActive = sessionNow;
            }
        }

        // ── Placement zone pulse animation ───────────────────────────────────
        if (_placementZoneGo != null && _placementZoneGo.activeInHierarchy)
        {
            float pulse = 1f + 0.10f * Mathf.Sin(Time.time * 4f);
            var s = _placementZoneGo.transform.localScale;
            _placementZoneGo.transform.localScale = new Vector3(0.22f * pulse, s.y, 0.22f * pulse);

            // Color shifts between cyan and white to draw attention
            var rend = _placementZoneGo.GetComponent<Renderer>();
            if (rend != null)
            {
                float t2 = (Mathf.Sin(Time.time * 3f) + 1f) * 0.5f;
                rend.material.color = Color.Lerp(
                    new Color(0.15f, 0.75f, 1.0f, 0.75f),
                    new Color(0.60f, 0.95f, 1.0f, 0.90f), t2);
            }
        }

        // ── Sequence transition timer ─────────────────────────────────────────
        if (!guidedModeEnabled || evaluator == null) return;

        if (sequenceActive && evaluator.SessionActive && !awaitingAdvance)
        {
            if (evaluator.TryGetGuidedAutoEnd(out var reason))
                evaluator.EndSession(reason);
        }

        if (!awaitingAdvance) return;

        // When requireUserInputToContinue the player must press REINTENTAR or MENÚ.
        // The auto-advance timer is suspended; only button presses drive transitions.
        if (!requireUserInputToContinue)
        {
            transitionTimer += Time.deltaTime;
            if (transitionTimer >= GetCurrentDisplaySeconds())
                CompleteTransition();
        }

        UpdateResultsTitle();
    }

    // ── Public sequence API ───────────────────────────────────────────────────

    public void BeginGuidedSequence(int electrodeIndex, WeldingEvaluator.ExerciseType startExercise)
    {
        if (!guidedModeEnabled || evaluator == null) return;

        selectedElectrodeIndex = electrodeIndex;
        startExerciseIndex     = GetExerciseIndex(startExercise);
        currentExerciseIndex   = startExerciseIndex;
        sequenceActive         = true;
        awaitingAdvance        = false;
        transitionTimer        = 0f;

        StartCurrentExercise();
    }

    public bool HandleResultsDismissedByUser()
    {
        if (!guidedModeEnabled || (!sequenceActive && !awaitingAdvance)) return false;
        if (awaitingAdvance) { CompleteTransition(); return true; }
        return sequenceActive;
    }

    /// <summary>
    /// Restart the current exercise from the beginning without returning to the menu.
    /// Returns true if the retry was handled; false if no active sequence exists.
    /// </summary>
    public bool RetryCurrentExercise()
    {
        if (evaluator == null) return false;
        awaitingAdvance = false;
        transitionTimer = 0f;
        scoreUI?.ClearResultsTitleOverride();
        StartCurrentExercise();
        return true;
    }

    // ── In-world guide ────────────────────────────────────────────────────────

    private void ShowGuideForCurrentExercise()
    {
        BuildGuideSteps(selectedElectrodeIndex, ExerciseOrder[currentExerciseIndex]);

        _guideStep        = 0;
        _guideAnchored    = false;
        _guideWaitAnchor  = true;
        _guideAnchorTimer = 0f;
        _prevSessionActive = false;
        _pieceWasGrabbed           = false;
        _pieceInHand               = false;
        _grabConfirmTimer          = -1f;
        _rightControllerWasTracked = false;
        _controllerConfirmTimer    = -1f;
        _beaconBasePosSet          = false;
        HidePlacementZone();

        // Find the piece interactable and record its resting position as placement target
        _pieceInteractable    = null;
        _targetPlacementPos   = Vector3.zero;
        string pieceName = ExercisePieceName(ExerciseOrder[currentExerciseIndex]);
        if (!string.IsNullOrEmpty(pieceName))
        {
            var pieceGo = GameObject.Find(pieceName);
            if (pieceGo != null)
            {
                _pieceInteractable  = pieceGo.GetComponent<XRGrabInteractable>()
                                   ?? pieceGo.GetComponentInChildren<XRGrabInteractable>();
                _targetPlacementPos = pieceGo.transform.position;  // where it rests = target
            }
        }

        if (guidePanel != null) guidePanel.SetActive(true);
        SetBeaconColor(_beaconDefaultColor);
        RefreshGuidePanel();
        RefreshBeacon();
    }

    private void SetBeaconColor(Color c)
    {
        if (beaconGo == null) return;
        var rend = beaconGo.GetComponent<Renderer>();
        if (rend != null) rend.material.color = c;
    }

    private void ShowPlacementZone(Vector3 worldPos)
    {
        if (_placementZoneGo == null) return;
        _placementZoneGo.transform.position = worldPos;
        _placementZoneGo.SetActive(true);
    }

    private void HidePlacementZone()
    {
        if (_placementZoneGo != null) _placementZoneGo.SetActive(false);
    }

    private void HideGuide()
    {
        if (guidePanel != null) guidePanel.SetActive(false);
        if (beaconGo   != null) beaconGo.SetActive(false);
        _guideStep = -1;
    }

    private void OnGuideNext()
    {
        if (_guideStep < 0 || _guideSteps.Count == 0) return;

        if (_guideSteps[_guideStep].launchOnNext)
        {
            BeginActualSession();
            _guideStep++;
            if (_guideStep >= _guideSteps.Count) { HideGuide(); return; }
            RefreshGuidePanel();
            RefreshBeacon();
            return;
        }

        _guideStep++;
        if (_guideStep >= _guideSteps.Count) { HideGuide(); return; }
        _guideAnchored = false;   // re-anchor panel on each step change
        RefreshGuidePanel();
        RefreshBeacon();
    }

    private void OnGuideSkip()
    {
        BeginActualSession();
        HideGuide();
    }

    private void RefreshGuidePanel()
    {
        if (_guideStep < 0 || _guideStep >= _guideSteps.Count) return;
        var s = _guideSteps[_guideStep];

        if (guideTitleText    != null) guideTitleText.text    = s.title;
        if (guideBodyText     != null) guideBodyText.text     = s.body;
        if (guideStepIndicator != null)
            guideStepIndicator.text = $"Paso {_guideStep + 1} / {_guideSteps.Count}";

        // Show Next button only on manual steps (not the last session-active step)
        bool showNext = _guideStep < _guideSteps.Count - 1;
        if (guideNextButton     != null) guideNextButton.gameObject.SetActive(showNext);
        if (guideNextButtonLabel != null)
            guideNextButtonLabel.text = s.launchOnNext ? "INICIAR SESIÓN  ›" : "SIGUIENTE  ›";
    }

    private void RefreshBeacon()
    {
        if (beaconGo == null) return;
        if (_guideStep < 0 || _guideStep >= _guideSteps.Count ||
            string.IsNullOrEmpty(_guideSteps[_guideStep].beaconTarget))
        {
            beaconGo.SetActive(false);
            return;
        }
        var target = GameObject.Find(_guideSteps[_guideStep].beaconTarget);
        if (target != null)
        {
            beaconGo.SetActive(true);
            beaconGo.transform.position = target.transform.position + Vector3.up * 0.12f;
            _beaconBasePosSet = false;   // recalculate bob base on next frame
        }
        else beaconGo.SetActive(false);
    }

    private bool AlignGuidePanel(bool forcePos)
    {
        if (guidePanel == null) return false;
        var cam = Camera.main != null ? Camera.main.transform : null;
        if (cam == null) return false;
        var t = guidePanel.transform;
        if (forcePos)
        {
            t.position = cam.position
                       + cam.right   * guidePanelOffset.x
                       + cam.up      * guidePanelOffset.y
                       + cam.forward * guidePanelOffset.z;
        }
        var look = t.position - cam.position;
        if (look.sqrMagnitude < 0.0001f) look = cam.forward;
        t.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
        return true;
    }

    // ── Step content ──────────────────────────────────────────────────────────

    private void BuildGuideSteps(int electrode, WeldingEvaluator.ExerciseType ex)
    {
        _guideSteps.Clear();

        string elLabel = electrode == 0 ? "3/32\"  (2.4 mm)"
                       : electrode == 1 ? "1/8\"   (3.2 mm)"
                       :                  "5/32\"  (4.0 mm)";
        string pieceGo = ExercisePieceName(ex);

        // Step 0 – pick up right controller
        _guideSteps.Add(new GuideStep
        {
            title        = "Toma el control derecho",
            body         = "<b>Ejercicio:</b>  Unión en T — filete doble\n" +
                           $"<b>Electrodo:</b>  E6013  {elLabel}\n\n" +
                           "La pinza de soldadura está montada en el\n" +
                           "<b>control derecho</b>.  Tómalo con tu mano dominante.\n\n" +
                           "<size=85%><color=#AAAAAA>Avanza automáticamente al detectar el control.</color></size>",
            beaconTarget = clampGoName,
            launchOnNext = false,
        });

        // Step 1 – grab the welding piece and position it
        _guideSteps.Add(new GuideStep
        {
            title        = "Coloca la pieza T en la mesa",
            body         = "El marcador amarillo señala la pieza T.\n\n" +
                           "<b>Agárrala con el control izquierdo</b> y apóyala\n" +
                           "sobre la superficie de la mesa.\n\n" +
                           "<b>Debes soldar DOS cordones de filete:</b>\n" +
                           "  1. <b>Frente</b>  — lado del chaflán que te mira\n" +
                           "  2. <b>Reverso</b> — gira la pieza 180° para este lado\n\n" +
                           "<b>Criterios:</b>\n" +
                           "• Ángulo 45° · Continuidad\n" +
                           "• Cobertura del seam · Velocidad de avance\n\n" +
                           "<size=85%><color=#AAAAAA>Avanza al agarrar la pieza.</color></size>",
            beaconTarget = pieceGo,
            launchOnNext = false,
        });

        // Step 2 – ready to start welding
        _guideSteps.Add(new GuideStep
        {
            title        = "¡Listo para soldar!",
            body         = "Acerca el electrodo al <b>frente</b> de la pieza\n" +
                           "hasta que el <color=#33DD55><b>arco se encienda</b></color>.\n\n" +
                           "<b>Orden sugerido:</b>\n" +
                           "  1. Suelda el <b>frente</b> de izq. a der.\n" +
                           "  2. Apaga el arco, agarra la pieza\n" +
                           "     y <b>gírala 180°</b> con el control izq.\n" +
                           "  3. Suelda el <b>reverso</b> de izq. a der.\n\n" +
                           "Cuando estés listo pulsa <b>INICIAR SESIÓN</b>.",
            beaconTarget = pieceGo,
            launchOnNext = true,
        });

        // Step 3 – session active
        _guideSteps.Add(new GuideStep
        {
            title        = "¡Evaluación en curso!",
            body         = "<color=#33DD55><b>●</b></color>  Arco activo  →  distancia correcta\n" +
                           "<color=#FF5555><b>○</b></color>  Sin arco    →  ajusta la distancia\n\n" +
                           "El HUD muestra  <b>F:X%  R:Y%</b> en tiempo real.\n" +
                           "Solo el avance a <b>1–25 mm/s</b> acumula progreso.\n\n" +
                           "<size=85%>Este panel se cierra al finalizar la sesión.</size>",
            beaconTarget = "",
            launchOnNext = false,
        });
    }

    private static string ExName(WeldingEvaluator.ExerciseType ex) => "P2 – Unión en T";

    private string ExercisePieceName(WeldingEvaluator.ExerciseType ex)
    {
        // Single-figure setup: all exercises use the same T-joint piece.
        return (exercisePieceNames != null && exercisePieceNames.Length > 0)
             ? exercisePieceNames[0] : "";
    }

    // ── Sequence internals ────────────────────────────────────────────────────

    private void StartCurrentExercise()
    {
        if (evaluator == null) return;

        scoreUI      ??= FindAnyObjectByType<WeldingScoreUI>();
        selectionMenu ??= FindAnyObjectByType<WeldingSelectionMenu>();

        scoreUI?.ClearResultsTitleOverride();
        selectionMenu?.Hide();

        // Show the in-world guide first; it will call BeginActualSession() when ready
        if (guidePanel != null)
            ShowGuideForCurrentExercise();
        else
            BeginActualSession();
    }

    private void BeginActualSession()
    {
        if (evaluator == null) return;
        evaluator.SelectElectrode(selectedElectrodeIndex);
        evaluator.SelectExercise(ExerciseOrder[currentExerciseIndex]);
        evaluator.BeginSession();
    }

    private void HandleSessionStarted()
    {
        if (!sequenceActive) return;
        awaitingAdvance = false;
        transitionTimer = 0f;
        scoreUI?.ClearResultsTitleOverride();
    }

    private void HandleSessionEnded()
    {
        if (!sequenceActive) return;
        awaitingAdvance = true;
        transitionTimer = 0f;
        UpdateResultsTitle();
    }

    private void CompleteTransition()
    {
        awaitingAdvance = false;
        transitionTimer = 0f;

        if (repeatExerciseOnLowScore && evaluator != null
            && evaluator.OverallScore < passingScoreThreshold)
        {
            StartCurrentExercise();
            return;
        }

        if (HasNextExercise())
        {
            currentExerciseIndex++;
            StartCurrentExercise();
            return;
        }

        FinishSequence();
    }

    private void FinishSequence()
    {
        sequenceActive  = false;
        awaitingAdvance = false;
        transitionTimer = 0f;
        scoreUI?.ClearResultsTitleOverride();
        if (autoReturnToMenuAtSequenceEnd) { scoreUI?.Hide(); selectionMenu?.Show(); }
    }

    private void UpdateResultsTitle()
    {
        if (scoreUI == null) return;

        if (requireUserInputToContinue)
        {
            // No countdown — player decides when to continue via buttons
            scoreUI.SetResultsTitleOverride(HasNextExercise()
                ? $"RESULTADO  {GetExerciseLabel(currentExerciseIndex)}"
                : "SECUENCIA COMPLETA");
            return;
        }

        var left = Mathf.CeilToInt(Mathf.Max(0f, GetCurrentDisplaySeconds() - transitionTimer));
        if (HasNextExercise())
        {
            scoreUI.SetResultsTitleOverride(
                $"RESULTADO {GetExerciseLabel(currentExerciseIndex)}\n" +
                $"Siguiente: {GetExerciseLabel(currentExerciseIndex + 1)} en {left}s");
            return;
        }
        scoreUI.SetResultsTitleOverride(autoReturnToMenuAtSequenceEnd
            ? $"SECUENCIA COMPLETA\nVolviendo al menú en {left}s"
            : "SECUENCIA COMPLETA");
    }

    private bool  HasNextExercise()       => currentExerciseIndex < ExerciseOrder.Length - 1;
    private float GetCurrentDisplaySeconds() => HasNextExercise() ? resultsDisplaySeconds : finalResultsDisplaySeconds;

    private static int GetExerciseIndex(WeldingEvaluator.ExerciseType exercise)
    {
        for (int i = 0; i < ExerciseOrder.Length; i++)
            if (ExerciseOrder[i] == exercise) return i;
        return 0;
    }

    private static string GetExerciseLabel(int index) =>
        (index >= 0 && index < ExerciseLabels.Length) ? ExerciseLabels[index] : "Práctica";
}
