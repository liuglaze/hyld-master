using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

using UnityEngine.SceneManagement;
public class logo : MonoBehaviour {

    public Slider loadingSlider;
    public Text loadingText;
    private float loadingSpeed = 1;
    private float targetValue;
    private AsyncOperation operation;
    public Image bg;
    // Use this for initialization	
    void Start ()	{
        Logging.HYLDDebug.Log("logo start *** " + Time.realtimeSinceStartup);
         loadingSlider.value = 0.0f;
                //启动协程		
            StartCoroutine(AsyncLoading());
         
    }
    IEnumerator AsyncLoading()	{
        operation = SceneManager.LoadSceneAsync("main");       //阻止当加载完成自动切换	
        operation.allowSceneActivation = false;
        yield return operation;
    }


    void Update()
    {
        targetValue = operation.progress ;
        if (operation.progress >= 0.9f)
        {           //operation.progress的值最大为0.9		
            targetValue = 1.0f;
        }
        if (targetValue != loadingSlider.value)		{           //插值运算		
            loadingSlider.value = Mathf.Lerp(loadingSlider.value, targetValue, Time.deltaTime * loadingSpeed);
            if (Mathf.Abs(loadingSlider.value - targetValue) < 0.01f)			{
                loadingSlider.value = targetValue;
            }
        }
       
        loadingText.text = ((int)(loadingSlider.value * 100)).ToString() + "%";
        if ((int)(loadingSlider.value * 100) == 100)		{           //允许异步加载完毕后自动切换场景		
            operation.allowSceneActivation = true;
        }
    }
           
}
