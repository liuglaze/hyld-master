/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/23 21:11:33
    Description:     单例模式模板
*****************************************************/

using UnityEngine;

public class Singleton<T> : MonoBehaviour where T : Singleton<T>
{
    public static T Instance { get; private set; }
    protected bool IsSingletonInstance { get; private set; }

    protected virtual void Awake()
    {
        if (Instance == null)
        {
            Instance = (T)this;
            IsSingletonInstance = true;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            IsSingletonInstance = false;
            Destroy(gameObject);
        }
    }
}
