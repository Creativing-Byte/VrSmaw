using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneTransitionManager : MonoBehaviour
{
    public FadeScreen fadeScreen;
    public static SceneTransitionManager singleton;

    private void Awake()
    {
        if (singleton != null && singleton != this)
        {
            Destroy(gameObject);
            return;
        }

        singleton = this;
        DontDestroyOnLoad(gameObject);
    }

    public void GoToScene(int sceneIndex)
    {
        StartCoroutine(GoToSceneRoutine(sceneIndex));
    }

    IEnumerator GoToSceneRoutine(int sceneIndex)
    {
        fadeScreen.FadeOut();
        yield return new WaitForSeconds(fadeScreen.fadeDuration);

        //Launch the new scene
        SceneManager.LoadScene(sceneIndex);
    }

    public void GoToSceneAsync(int sceneIndex)
    {
        StartCoroutine(GoToSceneAsyncRoutine(sceneIndex));
    }

    public void GoToSceneAsync(string sceneName)
    {
        StartCoroutine(GoToSceneAsyncRoutine(sceneName));
    }

    IEnumerator GoToSceneAsyncRoutine(int sceneIndex)
    {
        float fadeDuration = 0f;
        if (fadeScreen != null)
        {
            fadeScreen.FadeOut();
            fadeDuration = fadeScreen.fadeDuration;
        }

        AsyncOperation operation = SceneManager.LoadSceneAsync(sceneIndex);
        if (operation == null)
        {
            Debug.LogError($"SceneTransitionManager: no se pudo iniciar carga de escena con índice {sceneIndex}. Verifica que esté en Build Settings.");
            yield break;
        }

        operation.allowSceneActivation = false;

        float timer = 0;
        while (timer <= fadeDuration && !operation.isDone)
        {
            timer += Time.deltaTime;
            yield return null;
        }

        operation.allowSceneActivation = true;
    }

    IEnumerator GoToSceneAsyncRoutine(string sceneName)
    {
        float fadeDuration = 0f;
        if (fadeScreen != null)
        {
            fadeScreen.FadeOut();
            fadeDuration = fadeScreen.fadeDuration;
        }

        AsyncOperation operation = SceneManager.LoadSceneAsync(sceneName);
        if (operation == null)
        {
            Debug.LogError($"SceneTransitionManager: no se pudo iniciar carga de escena '{sceneName}'. Verifica que esté en Build Settings.");
            yield break;
        }

        operation.allowSceneActivation = false;

        float timer = 0;
        while (timer <= fadeDuration && !operation.isDone)
        {
            timer += Time.deltaTime;
            yield return null;
        }

        operation.allowSceneActivation = true;
    }
}
