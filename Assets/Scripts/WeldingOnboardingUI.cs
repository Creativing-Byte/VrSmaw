using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Manages the welcome / onboarding overlay shown the first time the training
/// scene loads.  Shows step-by-step setup instructions and hides the main
/// selection menu until the user dismisses it.
///
/// Inspector wiring:
///   onboardingPanel   → root GameObject of this overlay (enabled/disabled)
///   titleText         → large title TMP label
///   bodyText          → main instruction TMP label (rich text)
///   stepIndicator     → "Paso 1 / 3" TMP label
///   nextButton        → advances through steps
///   prevButton        → goes back (hidden on step 1)
///   skipButton        → skips to the end immediately
///   selectionMenu     → WeldingSelectionMenu — shown after onboarding
/// </summary>
[DisallowMultipleComponent]
public class WeldingOnboardingUI : MonoBehaviour
{
    // ── Content ───────────────────────────────────────────────────────────────

    private static readonly string[] StepTitles =
    {
        "Bienvenido al Simulador SMAW",
        "Configura tu equipo",
        "Cómo soldar en el simulador",
    };

    private static readonly string[] StepBodies =
    {
        // Step 0 – overview
        "<size=115%><b>¿Qué es este simulador?</b></size>\n\n" +
        "Entrena soldadura con electrodo revestido (SMAW) en Realidad Mixta " +
        "sin riesgo ni material consumible.\n\n" +
        "La <b>pinza física</b> lleva un sensor que mide ángulos en tiempo real. " +
        "El <b>láser</b> detecta la distancia entre el electrodo y la pieza.\n\n" +
        "Al finalizar recibes una <b>puntuación detallada</b> por cada criterio de soldadura.",

        // Step 1 – setup
        "<size=115%><b>Antes de empezar</b></size>\n\n" +
        "<color=#FFCC00>①</color>  Asegúrate de que el indicador <b>Arduino</b> esté en <color=#33DD55>●</color> verde.\n" +
        "   Si no, conecta el cable USB al PC y abre el script puente.\n\n" +
        "<color=#FFCC00>②</color>  El <b>Controlador VR</b> debe estar en <color=#33DD55>●</color> verde.\n" +
        "   Está montado en la pinza — no lo toques mientras suelda.\n\n" +
        "<color=#FFCC00>③</color>  Puedes seleccionar el <b>electrodo físicamente</b>\n" +
        "   con los botones de la pinza, o tocando los botones del menú.",

        // Step 2 – how to weld
        "<size=115%><b>Durante la sesión</b></size>\n\n" +
        "<color=#33DD55>● Arco activo</color>  El electrodo está a la distancia correcta de la pieza.\n" +
        "   El controlador vibrará al entrar y salir del rango.\n\n" +
        "<color=#FF5555>○ Sin arco</color>    Demasiado lejos o demasiado cerca. Ajusta la distancia.\n\n" +
        "<b>Criterios evaluados según el ejercicio:</b>\n" +
        "  <size=90%>• Rectitud del cordón  (yaw ±5°)\n" +
        "  • Ángulo de trabajo  (pitch objetivo)\n" +
        "  • Uniformidad de altura  (láser ±mm)\n" +
        "  • Continuidad — sin pausas largas\n" +
        "  • Control del arco — estabilidad de distancia</size>",
    };

    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Panel")]
    [SerializeField] private GameObject onboardingPanel;

    [Header("Text elements")]
    [SerializeField] private TextMeshProUGUI titleText;
    [SerializeField] private TextMeshProUGUI bodyText;
    [SerializeField] private TextMeshProUGUI stepIndicatorText;

    [Header("Buttons")]
    [SerializeField] private Button nextButton;
    [SerializeField] private Button prevButton;
    [SerializeField] private Button skipButton;
    [SerializeField] private TextMeshProUGUI nextButtonText;

    [Header("References")]
    [SerializeField] private WeldingSelectionMenu selectionMenu;

    [Header("Settings")]
    [Tooltip("Show the onboarding every time the scene loads, not just the first time.")]
    [SerializeField] private bool alwaysShowOnLoad = false;

    [Header("World Space Placement")]
    [SerializeField] private bool anchorToCameraOnShow = true;
    [SerializeField] private bool keepFacingCamera     = true;
    [SerializeField] private Vector3 offsetFromCamera  = new Vector3(0f, -0.08f, 1.05f);
    [SerializeField] private Vector3 eulerOffset;

    private const string PrefKey = "SmawOnboardingDone";

    [Header("Startup anchor delay")]
    [Tooltip("Seconds to wait after Show() before anchoring. Gives XR time to deliver the first head pose.")]
    [SerializeField] private float anchorDelaySeconds = 0.8f;

    // ── Private state ─────────────────────────────────────────────────────────

    private int   _currentStep;
    private bool  _anchored;
    private float _anchorTimer;       // counts up from 0; anchor fires once >= anchorDelaySeconds
    private bool  _waitingForAnchor;  // true while we're in the delay window

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        if (selectionMenu == null)
            selectionMenu = FindAnyObjectByType<WeldingSelectionMenu>();

        if (nextButton != null) nextButton.onClick.AddListener(OnNext);
        if (prevButton != null) prevButton.onClick.AddListener(OnPrev);
        if (skipButton != null) skipButton.onClick.AddListener(OnSkip);
    }

    private void Start()
    {
        bool shouldShow = alwaysShowOnLoad || !PlayerPrefs.HasKey(PrefKey);

        if (shouldShow)
            ShowOnboarding();
        else
            HideOnboarding();
    }

    private void Update()
    {
        if (onboardingPanel == null || !onboardingPanel.activeInHierarchy) return;

        // ── Startup delay: wait anchorDelaySeconds before placing the panel ──
        if (_waitingForAnchor)
        {
            _anchorTimer += Time.deltaTime;
            if (_anchorTimer >= anchorDelaySeconds)
            {
                _waitingForAnchor = false;
                _anchored = AlignToCamera(forcePosition: true);
            }
            return;   // don't face-track until we've anchored
        }

        if (anchorToCameraOnShow && !_anchored)
            _anchored = AlignToCamera(forcePosition: true);
        else if (keepFacingCamera)
            AlignToCamera(forcePosition: false);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void ShowOnboarding()
    {
        _anchored         = false;
        _waitingForAnchor = anchorToCameraOnShow;  // start delay window
        _anchorTimer      = 0f;
        _currentStep      = 0;

        if (onboardingPanel != null) onboardingPanel.SetActive(true);
        if (selectionMenu   != null) selectionMenu.Hide();
        RefreshStep();
    }

    public void HideOnboarding()
    {
        _anchored = false;
        if (onboardingPanel != null) onboardingPanel.SetActive(false);
        if (selectionMenu   != null) selectionMenu.Show();
        PlayerPrefs.SetInt(PrefKey, 1);
        PlayerPrefs.Save();
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void OnNext()
    {
        if (_currentStep < StepTitles.Length - 1)
        {
            _currentStep++;
            RefreshStep();
        }
        else
        {
            HideOnboarding();
        }
    }

    private void OnPrev()
    {
        if (_currentStep > 0)
        {
            _currentStep--;
            RefreshStep();
        }
    }

    private void OnSkip() => HideOnboarding();

    // ── Camera alignment ──────────────────────────────────────────────────────

    private bool AlignToCamera(bool forcePosition)
    {
        if (onboardingPanel == null) return false;
        var t   = onboardingPanel.transform;
        var cam = Camera.main != null ? Camera.main.transform : null;
        if (t == null || cam == null) return false;

        if (forcePosition)
        {
            t.position = cam.position
                       + cam.right    * offsetFromCamera.x
                       + cam.up       * offsetFromCamera.y
                       + cam.forward  * offsetFromCamera.z;
        }

        var look = t.position - cam.position;
        if (look.sqrMagnitude < 0.0001f) look = cam.forward;
        t.rotation = Quaternion.LookRotation(look.normalized, Vector3.up)
                   * Quaternion.Euler(eulerOffset);

        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RefreshStep()
    {
        bool isLast = _currentStep == StepTitles.Length - 1;

        if (titleText != null)
            titleText.text = StepTitles[_currentStep];

        if (bodyText != null)
            bodyText.text = StepBodies[_currentStep];

        if (stepIndicatorText != null)
            stepIndicatorText.text = $"Paso {_currentStep + 1} de {StepTitles.Length}";

        if (prevButton != null)
            prevButton.gameObject.SetActive(_currentStep > 0);

        if (nextButtonText != null)
            nextButtonText.text = isLast ? "COMENZAR" : "SIGUIENTE ›";
    }
}
