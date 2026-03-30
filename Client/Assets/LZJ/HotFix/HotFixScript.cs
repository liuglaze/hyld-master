/****************************************************
    Author:            龙之介
    CreatTime:    2021/4/12 18:44:13
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using XLua;
using UnityEngine.Networking;
public class HotFixScript : MonoBehaviour
{
    private LuaEnv luaEnv;

    private void Awake()
    {
        luaEnv = new LuaEnv();
        luaEnv.AddLoader(MyLoader);
        luaEnv.DoString("require 'Invotion'");
    }
    private void Start()
    {

    }
    private void OnDisable()
    {
        luaEnv.DoString("require 'Invotiondispos'");
    }
    private void OnDestroy()
    {
        luaEnv.Dispose();
    }
    private byte[] MyLoader(ref string filePath)
    {
        //Invotion.lua
        string absPath = @"D:\XuanShuiLiuLi\Invotion\PlayerGamePackage\lua\" + filePath+".lua.txt";
        return System.Text.Encoding.UTF8.GetBytes(System.IO.File.ReadAllText(absPath));
    }


    public static Dictionary<string, GameObject> prefabDic=new Dictionary<string, GameObject>();
    [LuaCallCSharp]
    public void LoadResource(string resName,string filePath)
    {
        StartCoroutine(LoadResourceCorotine(resName,filePath));
       
    }
    [LuaCallCSharp]
    public  GameObject GetGameObject(string goName)
    {
        //Logging.HYLDDebug.Log("尝试获取游戏物体");
        if (prefabDic.ContainsKey(goName))
        {
         //   Logging.HYLDDebug.Log("存在 "+goName+"预制体");
            return prefabDic[goName];
        }
     //   Logging.HYLDDebug.Log("不存在 " + goName + "预制体");
        return null;
    }



    IEnumerator LoadResourceCorotine(string resName,string filePath)
    {
        string path = @"http://localhost/AssetBundles/" + filePath;


        UnityWebRequest request=UnityWebRequest.Get(path);
        yield return request.SendWebRequest();
        while(request.isHttpError)
        {
            Logging.HYLDDebug.LogError("ERROR: " + request.error);
            yield return null;
        }

        while(!request.isDone)
        {
            yield return null;
        }

        byte[] result = request.downloadHandler.data;


        //Logging.HYLDDebug.Log("导入预制体到字典");
        AssetBundle ab = AssetBundle.LoadFromMemory(result);//LoadFromFile(@"D:\XuanShuiLiuLi\Invotion\Client\AssetBundles\" + filePath);
       // Logging.HYLDDebug.Log(ab);
        GameObject gameObject = ab.LoadAsset<GameObject>(resName);
        //Logging.HYLDDebug.Log(gameObject);
        prefabDic.Add(resName, gameObject);
       // Logging.HYLDDebug.Log("导入预制体到字典成功");
    }
}
