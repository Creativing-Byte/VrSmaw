using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class GameStartMenu : MonoBehaviour
{
    private const string DefaultTrainingSceneName = "SampleScene";

    [Header("UI Pages")]
    public GameObject mainMenu;
    public GameObject options;
    public GameObject about;

    [Header("Main Menu Buttons")]
    public Button startButton;
    public Button optionButton;
    public Button aboutButton;
    public Button quitButton;

    public List<Button> returnButtons;

    [Header("Scene Flow")]
    [SerializeField]
    private string trainingSceneName = DefaultTrainingSceneName;

    [SerializeField]
    private int fallbackSceneIndex = 1;

    // Start is called before the first frame update
    void Start()
    {
        EnableMainMenu();

        //Hook events
        startButton.onClick.AddListener(StartGame);
        optionButton.onClick.AddListener(EnableOption);
        aboutButton.onClick.AddListener(EnableAbout);
        quitButton.onClick.AddListener(QuitGame);

        foreach (var item in returnButtons)
        {
            item.onClick.AddListener(EnableMainMenu);
        }
    }

    public void QuitGame()
    {
        Application.Quit();
    }

    public void StartGame()
    {
        HideAll();

        if (SceneTransitionManager.singleton == null)
        {
            Debug.LogError("SceneTransitionManager singleton is missing. Cargando escena directamente.");
            SceneManager.LoadScene(!string.IsNullOrWhiteSpace(trainingSceneName)
                ? trainingSceneName
                : fallbackSceneIndex.ToString());
            return;
        }

        if (!string.IsNullOrWhiteSpace(trainingSceneName))
        {
            SceneTransitionManager.singleton.GoToSceneAsync(trainingSceneName);
            return;
        }

        SceneTransitionManager.singleton.GoToSceneAsync(fallbackSceneIndex);
    }

    public void HideAll()
    {
        mainMenu.SetActive(false);
        options.SetActive(false);
        about.SetActive(false);
    }

    public void EnableMainMenu()
    {
        mainMenu.SetActive(true);
        options.SetActive(false);
        about.SetActive(false);
    }
    public void EnableOption()
    {
        mainMenu.SetActive(false);
        options.SetActive(true);
        about.SetActive(false);
    }
    public void EnableAbout()
    {
        mainMenu.SetActive(false);
        options.SetActive(false);
        about.SetActive(true);
    }
}
