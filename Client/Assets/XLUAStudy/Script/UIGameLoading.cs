using System.Collections;
using System.Collections.Generic;
#if !UNITY_WEBPLAYER
using System.IO;
#endif
using UnityEngine;
using UnityEngine.UI;
 using System;

public class UIGameLoading : MonoBehaviour
{
 string luaABname = "lua";
    public Text txtVersion;
    public Text txtRes;
    public Text txtSize;
    public Text txtSize2;
    public Text txtSpeed;
    public Slider progressBar;
    public GameObject objMessage;
    public Text txtContent;
    public Button btnConfirm;
    public GameObject Tip;

#if !UNITY_WEBPLAYER

    List<AssetBundleInfo> listDown;

    Dictionary<string, AssetBundleInfo> dicServer;
    Dictionary<string, AssetBundleInfo> dicLocal;

    string timeServerString;
    string timeLocalString;

    uint timeServer { get { return uint.Parse(timeServerString); } }
    uint timeLocal { get { return uint.Parse(timeLocalString); } }

    float allSize;
    float downSize;

    public void Start()
    { 
        txtVersion.text = "当前版本:" + GameConfig.Version;
        txtRes.text = "";
        txtSize.text = "";
        txtSize2.text = ""; 
        progressBar.gameObject.SetActive(false); 
        dicLocal = new Dictionary<string, AssetBundleInfo>();
        Logging.HYLDDebug.LogError("LoadTools.assetBundlePath  --------- " + LoadTools.assetBundlePath);
        // 1.项目启动，进入热更新检查模块
        // 本地存放AB包的路径,如果为空,则创建
        // 2.首先判断PersistentData是否存在，不存在则创建这个目录，TODO:版本号为0
        // D:/Set a small target Tencent/Lua/Lua2022/Xlua/Xlua_study4/XLUA_study/PersistentData
        if (Directory.Exists(LoadTools.assetBundlePath) == false)
            Directory.CreateDirectory(LoadTools.assetBundlePath);

#if UNITY_EDITOR
        if (LoadTools.useAssetBundle == false){ 
            StartLogin(); 
        }
        else
            CopyDataFromStreaming();
#else
        CopyDataFromStreaming();
#endif
    }

    public static string GetTimeStamp(bool bflag)
        {
            TimeSpan ts = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, 0);
            string ret = string.Empty;
            if (bflag)
                ret = Convert.ToInt64(ts.TotalSeconds).ToString();
            else
                ret = Convert.ToInt64(ts.TotalMilliseconds).ToString();

            return ret;
        }

    /// <summary>
    /// 3. 从StreamAssets目类复制文件，ab包复制到persistentDataPath目录--assetslist.txt一并复制过去
    /// </summary>
    private void CopyDataFromStreaming()
    { 
        
        //Logging.HYLDDebug.Log("CopyDataFromStreaming LoadTools.assetBundlePath  " +LoadTools.assetBundlePath);
        // if (File.Exists(Path.Combine(LoadTools.assetBundlePath, "assetslist.txt")) == false)
        // {
           
            StartCoroutine(CopyStreaming());
        //}
        // else
        // {
        //     StartDown();
        // }
    }

    private IEnumerator CopyStreaming()
    {
        Logging.HYLDDebug.LogError(PlayerPrefs.GetInt("IsFirstEnterGame", 0));
        //只有第一次进游戏才需要更新！
        if (PlayerPrefs.GetInt("IsFirstEnterGame", 0) == 1 )
        {
            Logging.HYLDDebug.LogError("首次进入游戏初始化资源");
            progressBar.gameObject.SetActive(true);
            progressBar.value = 0;
            txtRes.text = "首次进入游戏初始化资源(不消耗流量)";//
            yield return new WaitForEndOfFrame();
            string wwwStreamingAssetBundlePath = Path.Combine(LoadTools.wwwStreamingAssetsPath, "AssetBundle");
            // 项目 assets 下的StreamingAssets 目录
            Logging.HYLDDebug.LogError(Path.Combine(wwwStreamingAssetBundlePath, "assetslist.txt"));
            // 此www为了加载出项目内StreamingAssets里面的assetslist.txt
            WWW www = new WWW(Path.Combine(wwwStreamingAssetBundlePath, "assetslist.txt"));
            while (www.isDone == false) // 等待执行完毕
            {
                yield return new WaitForEndOfFrame();
            }
            // Logging.HYLDDebug.LogError("加载完毕");
            if (string.IsNullOrEmpty(www.error) == false) // 如果出错 ,重新执行这里可以累计一个次数 做限制.
            {
                Logging.HYLDDebug.LogError(www.error);
                StartCoroutine(CopyStreaming());
                yield break;
            }
            string txtAssetslist = www.text;
            // Logging.HYLDDebug.LogError(txtAssetslist);
            string[] arrAssetslist = www.text.Split('\n');
            // Logging.HYLDDebug.LogError(arrAssetslist.Length);
            for (int i = 1; i < arrAssetslist.Length; i++)
            {
                string[] arrData = arrAssetslist[i].Split(',');
                progressBar.value = (i + 1f) / arrAssetslist.Length;
                //Logging.HYLDDebug.LogError(arrData[0]+","+ arrData[1]+","+ arrData[2]);
                string fileName = arrData[1] + "_" + arrData[2];
                // Logging.HYLDDebug.Log("fileName --- " + fileName);
                string abname = arrData[1];
                // Logging.HYLDDebug.Log("abname --- " + abname);
                // 此www根据清单目录进行加载
                www = new WWW(Path.Combine(wwwStreamingAssetBundlePath, fileName));
                while (www.isDone == false)
                {
                    yield return new WaitForEndOfFrame();
                }
                if (string.IsNullOrEmpty(www.error) == false) //如果出错则本次重新循环 可加次数限制.
                {
                    i--;
                    continue;
                }
                // 下载的内容,写入 可读写目录
                File.WriteAllBytes(Path.Combine(LoadTools.assetBundlePath, abname), www.bytes);
                yield return new WaitForFixedUpdate();
            }
            //File.Copy(ResourcesTools.streamingAssetsPath + "/AssetBundle/assetslist.txt", Path.Combine(ResourcesTools.assetBundlePath, "assetslist.txt")); 
            //        Logging.HYLDDebug.LogError(www.text+" ==  "+arrAssetslist.Length);
            // 将清单写入可读写目录 
            //Logging.HYLDDebug.Log(Path.Combine(LoadTools.assetBundlePath, "assetslist.txt"));
            //D:/ Set a small target Tencent/ Lua / Lua2022 / Xlua / Xlua_study4 / XLUA_study / PersistentData\assetslist.txt
            File.WriteAllText(Path.Combine(LoadTools.assetBundlePath, "assetslist.txt"), txtAssetslist);
            txtRes.text = "";
            PlayerPrefs.SetInt("IsFirstEnterGame", 1);
        }
        StartDown();
    } 
    private void StartDown()
    { 
        listDown = new List<AssetBundleInfo>();

        if (string.IsNullOrEmpty(GameConfig.AssetIP) == false)
        {
           
            StartCoroutine(CheckResources());
        }
        else
        {
           
            StartLogin();
        } 
    } 
    /// <summary>
    /// 4 下载检测CheckResource
    /// </summary>
    /// <returns></returns>
    private IEnumerator CheckResources()
    {
        //一，从服务器资源下载assetslist.txt       
        string s = "检查更新";
        txtRes.text = s;
        // TODO: 后期改为用服务器由于没有服务器，后续会换成真实服务器的
        string url_ = "file:///D:/Set a small target Tencent/Lua/Lua2022/Xlua/Xlua_study4/XLUA_study/AssetBundles";
#if UNITY_EDITOR 
        WWW www = new WWW(url_ + "/assetslist.txt");// 用本地路径代替. 先下载 清单
#else
        WWW www = new WWW(url_ + "/assetslist.txt?v="+UnityEngine.Random.Range(10000,99999));
#endif
       // Logging.HYLDDebug.LogError("CheckResources");
        int index = 0;
        while (www.isDone == false)
        {
            txtRes.text = s + "...".Substring(index); // ... 小动画
            index = (index + 1) % 2;
            yield return new WaitForEndOfFrame();
        } 
        if (string.IsNullOrEmpty(www.error) == false) // 下载出错
        {
            Logging.HYLDDebug.Log(www.url + "\n" + www.error);
            yield return new WaitForSeconds(5f);
            StartCoroutine(CheckResources());
        }
        else
        {
           // Logging.HYLDDebug.Log("adad");
            string[] arr = www.text.Split('\n');
            timeServerString = arr[0]; // 0号就是time
            dicServer = CreateAssetDictionary(arr); // 创建一个 server上的AB信息字典

            //二，从本地的PersistentData读取本地的assetslist.txt  
            //D:/Set a small target Tencent/Lua/Lua2022/Xlua/Xlua_study4/XLUA_study/PersistentData/assetslist.txt
            Logging.HYLDDebug.Log(LoadTools.assetBundlePath + "/assetslist.txt");
            if (File.Exists(LoadTools.assetBundlePath + "/assetslist.txt") == true) //已经下载的目录存在
            {
                #if !UNITY_WEBPLAYER
                        arr = File.ReadAllText(LoadTools.assetBundlePath + "/assetslist.txt").Split('\n');
                
               // Logging.HYLDDebug.LogError((File.ReadAllText(LoadTools.assetBundlePath + "/assetslist.txt")));
#endif
                 Logging.HYLDDebug.LogError(arr.Length+"  "+arr[0]);
                timeLocalString = arr[0];
                dicLocal = CreateAssetDictionary(arr);// 创建本地字典
                GameConfig.Assetversion = (timeLocal % 100000).ToString(); // 这里用timeLocal做版本号. 算法可以自定义
                txtVersion.text = "当前版本:" + GameConfig.Version + ":" + GameConfig.Assetversion;
            }
            else// 本地不存在设置为0 
            {
                Logging.HYLDDebug.LogError("0");
                timeLocalString = "0";
                dicLocal = new Dictionary<string, AssetBundleInfo>();
            }
            yield return new WaitForEndOfFrame();
            Logging.HYLDDebug.Log(timeServer + "   " + timeLocal);
            //三，比较timeServer和timeLocal 如果服务端的时间大于本地说明需要更新，否则无需更新直接进入login节面
            if (timeServer > timeLocal) // 判断大版本号是否需要下载
            {
                 Logging.HYLDDebug.Log("大更新");

                //四，开始逐一server检查ab包
                foreach (var item in dicServer.Values)
                {
                    if (dicLocal.ContainsKey(item.name) == false) // 检测两个列表, 本地不包含则添加到下载列表.
                    {
                        allSize += item.size;
                        listDown.Add(item);
                    }
                    else if (dicLocal[item.name].md5 != item.md5) // 本地包含,但MD5信息不对
                    {
                        allSize += item.size;
                        listDown.Add(item);
                    }
                    //如果本地存在，MD5也一样不做处理
                }
                //五，确定完成了下载列表
                 StartCoroutine(DownAssets());  
            }
            else
            {
                Logging.HYLDDebug.Log("StartLogin 4");
                StartLogin();
            }
        }
    } 
    private string GetSize(float v)
    {
        if (v < 1024)
            return v + "K";
        if (v < 1024 * 1024)
            return (v / 1024f).ToString("0.00") + "KB";
        if (v < 1024 * 1024 * 1024)
            return (v / (1024f * 1024f)).ToString("0.00") + "MB";
        return "";
    } 
    /// <summary>
    /// 5，开启下载协程DownAssets
    /// </summary>
    /// <returns></returns>
    private IEnumerator DownAssets()
    {
        txtRes.text = "正在下载更新文件";
        txtSize2.text = "0%";
        txtSize.text = string.Format("{0}MB/{1}", (downSize).ToString("0.00"), GetSize(allSize));
        txtSpeed.text = "0KB/S";
        progressBar.value = 0;
        progressBar.gameObject.SetActive(true);
        for (int i = 0; i < listDown.Count; i++)
        {
            AssetBundleInfo ab = listDown[i];
            //TODO: 后期改为用服务器
            string url_ = "file:///D:/Set a small target Tencent/Lua/Lua2022/Xlua/Xlua_study4/XLUA_study/AssetBundles"; 
            //一，每个资源在服务器的下载地址是 IP+ab.name+ab.md5
            WWW www = new WWW(url_ + "/" + ab.name + "_" + ab.md5); // 用本地文件代替 
            //二，通过www下载
            while (www.isDone == false)
            {
                yield return new WaitForEndOfFrame();
                progressBar.value = (1f * (downSize + www.bytesDownloaded)) / allSize;
                txtSize.text = string.Format("{0}/{1}", GetSize(downSize + www.bytesDownloaded), GetSize(allSize));
                txtSpeed.text = string.Format("{0}/S", GetSize(www.bytesDownloaded / Time.deltaTime));
                txtSize2.text = (progressBar.value * 100).ToString("0.00") + "%";
            } 
            //yield return www;
            if (string.IsNullOrEmpty(www.error) == false)
            {
               // Logging.HYLDDebug.Log(ab.name + " " + www.error + " " + www.url);
                yield return new WaitForSeconds(5f);
                i--;
                continue;
            } 
            downSize += www.bytesDownloaded;


            //三，通过File.WriteAllBytes写入到persistentDataPath的对应ab包文件--把服务器上的ab包写入到客户端的硬盘上
#if !UNITY_WEBPLAYER
            File.WriteAllBytes(LoadTools.assetBundlePath + "/" + ab.name, www.bytes); // 写入可读写目录 
#endif
            dicLocal[ab.name] = ab;  // 更新本地字典
            if (i == listDown.Count - 1) // 全部下载完毕
            {
                timeLocalString = timeServerString;
                GameConfig.Assetversion = (timeLocal % 100000).ToString();
            }
            SaveLocal();//保存本地
        } 
        StartLogin(); 
    } 
    private void SaveLocal()
    {
        string txt = timeLocalString;


        foreach (var item in dicLocal.Values)
        {
            txt += "\n" + item.type + "," + item.name + "," + item.md5 + "," + item.size + ","+item.level;
        }
       // Logging.HYLDDebug.LogError("SaveLoacl   "+ LoadTools.assetBundlePath + "/assetslist.txt"+txt);
        //四，将最新的清单写入可读写目录
#if !UNITY_WEBPLAYER
       // Logging.HYLDDebug.LogError("???");
        File.WriteAllText(LoadTools.assetBundlePath + "/assetslist.txt", txt);
#endif
    } 
    private Dictionary<string, AssetBundleInfo> CreateAssetDictionary(string[] arrServerList)
    {
        Dictionary<string, AssetBundleInfo> result = new Dictionary<string, AssetBundleInfo>();
        for (int i = 1; i < arrServerList.Length; i++)
        {
            if (string.IsNullOrEmpty(arrServerList[i]) == true)
                continue;
            string[] arr = arrServerList[i].Split(',');
            AssetBundleInfo ab = new AssetBundleInfo(int.Parse(arr[0]), arr[1], arr[2], float.Parse(arr[3]), int.Parse(arr[4]));
            result.Add(ab.name, ab);
        }
        return result;
    } 
    private void StartLogin()
    {
        StartCoroutine(InitGame());
    } 
    private IEnumerator InitGame()
    {
        txtRes.text = "游戏初始化";
        progressBar.gameObject.SetActive(true);
        txtSpeed.gameObject.SetActive(false);
        txtSize.gameObject.SetActive(false);
        txtSize2.gameObject.SetActive(false);
        progressBar.value = 0f;
        yield return new WaitForEndOfFrame(); 
        LoadTools.Init(); 
        txtRes.text = "";
        progressBar.gameObject.SetActive(false);
        //  Logging.HYLDDebug.Log("over load");
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR

          luaABname = "";
            // Logging.HYLDDebug.Log("UNITY_ANDROID UNITY_ANDROID   luaABname = "+luaABname);
            if  (luaABname==""){ 
                foreach (string key in dicLocal.Keys)
                {

                  //  Logging.HYLDDebug.Log("key  "+key);
                    if (key.IndexOf("lua") != -1 )
                    {
                            luaABname = key ; 
                             // Logging.HYLDDebug.Log("luaABname == " + luaABname);
                            ResourceManager.luaFileName = luaABname ;
                    }else if (key.IndexOf("font") != -1 )
                    {
                           
                             // Logging.HYLDDebug.Log("font == " + key);
                             ResourceManager.InitFontResource(key);
                    }
                  
                }
            }
         //   Logging.HYLDDebug.Log("luaABname yy "+luaABname);
           ResourceManager.loadLuaToByte(luaABname);
#endif

        //6，加载资源
        if (GameConfig.UseAssetsBundle)
        {
            luaABname = ""; 
            if (luaABname == "")
            {
                //Logging.HYLDDebug.LogError(dicLocal.Count);
                foreach (string key in dicLocal.Keys)
                {
                    //Logging.HYLDDebug.LogError(key);
                    if (key.IndexOf("lua") != -1)
                    {
                      
                        luaABname = key;
                        //Logging.HYLDDebug.Log("luaABname == " + luaABname);
                        ResourceManager.luaFileName = luaABname;
                    } 
                }
            }
            //Logging.HYLDDebug.LogError(luaABname);
            if (luaABname!="")
            {
                ResourceManager.loadLuaToByte(luaABname);
            } 
        } 
        GameObject go = Resources.Load<GameObject>("Main");
        Instantiate(go);
        Destroy(this.gameObject);
        AppBoot.instance.Init();
    } 
#endif
}

public class AssetBundleInfo
{
    public string name;
    public string md5;
    public float size;
    public int type;
    public int level;

    public AssetBundleInfo(int type, string name, string md5, float size,int level)
    {
        this.type = type;
        this.name = name;
        this.size = size;
        this.md5 = md5;
        this.level = level;
    }

}
