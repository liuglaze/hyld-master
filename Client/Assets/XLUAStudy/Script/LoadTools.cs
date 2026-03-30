#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine; 
using System.Collections.Generic;
using System.IO; 
 
public static class LoadTools
{ 

    static private AssetBundleManifest _manifest;

    static private Dictionary<string, AssetBundle> _dicAssetBundle;
    static private Dictionary<string, int> _dicAssetBundleUsed;

    static public bool useAssetBundle = true;

    static public void Init()
    {

#if UNITY_EDITOR
        if (useAssetBundle == false)
            return;
#endif
        if (_manifest == null)
        {
            _dicAssetBundle = new Dictionary<string, AssetBundle>();
            _dicAssetBundleUsed = new Dictionary<string, int>();
        }
            
    }

   
    static public AssetBundleCreateRequest LoadAssetBundleAsync(string name)
    {
        return AssetBundle.LoadFromFileAsync(assetBundlePath + "/" + name);
    }

   
    static public void AddAssetBundle(AssetBundle v)
    {
        _dicAssetBundle[v.name] =v;
        _dicAssetBundleUsed[v.name] = 0;
    }

    static private AssetBundle Load(string name)
    {
        Logging.HYLDDebug.Log("name " + name);
        return AssetBundle.LoadFromFile(assetBundlePath + "/" + name);
    }

    static public void AssetBundleUnload()
    {
        Logging.HYLDDebug.Log("~~~~~~~~~~~~~~~~~~~~~~~~~~~ AssetBundleUnload");
        if (_dicAssetBundle != null && _dicAssetBundle.Count > 40)
        {
            List<string> tempList = new List<string>(_dicAssetBundle.Keys);
            tempList.Sort((string a, string b) =>
            {
                return _dicAssetBundleUsed[b] - _dicAssetBundleUsed[a];
            });
            for (int i = tempList.Count - 1; i > 19; i--)
            {
                AssetBundle tempAB = _dicAssetBundle[tempList[i]];
                _dicAssetBundle.Remove(tempList[i]);
                tempAB.Unload(false);
            }
        }
    }

    static public AssetBundle LoadAssetBundle(string name)
    {
      

        name = name.ToLower();

        if (_dicAssetBundle.ContainsKey(name) == false)
        {
            _dicAssetBundle[name] = Load(name);
            _dicAssetBundleUsed[name] = 0;
        }


        string[] arrNames = _manifest.GetAllDependencies(name);
        
        for (int i = 0; i < arrNames.Length; i++)
        {
            if (_dicAssetBundle.ContainsKey(arrNames[i]) == true)
                continue;
            else
                LoadAssetBundle(arrNames[i]);
        }

        _dicAssetBundleUsed[name]++;
        return _dicAssetBundle[name];
    }

    static private T LoadAsset<T>(string path, string name) where T : Object
    {
        return LoadAssetBundle(path).LoadAsset<T>(name);
    }

	static public Material LoadMaterial(string path,string name)
	{
		#if UNITY_EDITOR
		if (useAssetBundle == false)
		{
			return AssetDatabase.LoadAssetAtPath<Material>("Assets/BundleResources/" + path + "/" + name + ".mat");
		}
		#endif
		return LoadAssetBundle(path).LoadAsset<Material>(name);
	}

    static public GameObject LoadPrefab(string moudule, string name)
    {
        string path = moudule + "/" + name;
        //if (path == _lastPrefabPath)
        //    return _lastLoadPrefab;

#if UNITY_EDITOR
        if (useAssetBundle == false)
        {
            return AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BundleResources/" + path + ".prefab");
        }
#endif
        if (moudule == "ShipSpine")
            moudule = name.ToLower();

        return LoadAssetBundle(moudule).LoadAsset<GameObject>(name);
    }

    

    static public GameObject LoadGameObject(string moudule, string name, GameObject parent = null)
    {
        GameObject source = LoadPrefab(moudule,name);
        if(source == null)
        {
            Logging.HYLDDebug.LogError("Error not find " + moudule + " " + name);
            return null;
        }

        GameObject obj = GameObject.Instantiate(source) as GameObject;
        obj.name = source.name;
        if (parent != null)
        {
            obj.transform.SetParent(parent.transform);
            obj.transform.localPosition = Vector3.zero;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;
            obj.layer = parent.layer;
        }
        return obj;
    }

    

     

    static public Sprite LoadSprite(string path, string name)
    {
#if UNITY_EDITOR
        if (useAssetBundle == false)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>("Assets/BundleResources/" + path + "/" + name + ".png");
        }
#endif
        return LoadAssetBundle(path).LoadAsset<Sprite>(name);
    }
 
    
    static public TextAsset LoadTextAsset(string path,string name)
    {
#if UNITY_EDITOR
        if (useAssetBundle == false)
        {
            return AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/BundleResources/" + path + "/" + name + ".json");
        }
#endif

        return LoadAssetBundle(path).LoadAsset<TextAsset>(name);
    }

   
    static public T Load<T>(string path,string name) where T : Object
    {
#if UNITY_EDITOR
        if (useAssetBundle == false)
        {
            return AssetDatabase.LoadAssetAtPath<T>("Assets/BundleResources/" + path + "/" + name);
        }
#endif

        return LoadAssetBundle(path).LoadAsset<T>(name);
    }

    public static string assetBundlePath
    {
        get
        { 
             return persistentDataPath;
        }
    }

    public static string streamingAssetsPath
    {
        get
        {
            return Application.streamingAssetsPath;
        }
    }

    public static string wwwStreamingAssetsPath
    {
        get
        {

#if UNITY_ANDROID && !UNITY_EDITOR
            return streamingAssetsPath;
#else
            return "file://" + streamingAssetsPath;
#endif

        }
    }


    public static string persistentDataPath
    {
        get
        {
#if UNITY_EDITOR
            return Application.dataPath.Replace("Assets", "PersistentData");
#elif UNITY_ANDROID
            return Application.persistentDataPath;
#else
            return Application.persistentDataPath;
#endif
        }
    }


}


