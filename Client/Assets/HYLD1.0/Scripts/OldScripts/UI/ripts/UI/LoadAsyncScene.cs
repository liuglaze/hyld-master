using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

//这个脚本我挂在了我用于显示百分比的Text下

public class LoadAsyncScene : MonoBehaviour
{
    public static bool isBackStart = false;


    //显示进度的文本

    public  Text progress;

    //进度条的数值

    private float progressValue;

    //进度条

    private Slider slider;

    


    private AsyncOperation async = null;



    private void Start()
    {
        slider = FindObjectOfType<Slider>();
        Logging.HYLDDebug.LogError(Time.time);
        StartCoroutine("LoadScene");

    }



    IEnumerator LoadScene()
    {
        if(isBackStart)
        async = SceneManager.LoadSceneAsync("HuangYeLuanDouStart");
        else
        {
            async = SceneManager.LoadSceneAsync("HYLDGame");
            //async = SceneManager.LoadSceneAsync("HYLDGameNew");
        }
        async.allowSceneActivation = false;

        while (!async.isDone)
        {
            if (async.progress < 0.9f)
                progressValue = async.progress;
            else

                progressValue = 1.0f;
            slider.value = progressValue;
            progress.text = (int)(slider.value * 100) + " %";
            if (progressValue >= 0.95)
            {
                break;
                async.allowSceneActivation = true;
            }
            yield return null;
        }

      
    }
}
