using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif
public class ResourceAttribute
{
    public string name;
    public string path;
    public string abname;
    public ResourceType rtype;
    public string nextPages;

}

public enum ResourceType
{
    Sprite = 1,
    Prefab = 2,
    // 可重复生成的预制体
    Module = 3,
    //功能模块
    GameObject = 4
    // 不属于 2和3的游戏体
}

public class ResourceManager
{
    

    public enum UIType
    {
        ROOT = 1,
        MAIN = 2,
        SECOND = 3,
        POP = 4
    }


#if UNITY_ANDROID && !UNITY_EDITOR
        static string pathRoot = LoadTools.assetBundlePath ;       // Application.dataPath + "!assets" ;
         static string     assetBundlePath= "";
#elif UNITY_IOS && !UNITY_EDITOR
	static string pathRoot = LoadTools.assetBundlePath ;       // Application.dataPath + "!assets" ;
	static string     assetBundlePath= ""; 

#else

    static string pathRoot = Application.dataPath;
    static string assetBundlePath = "/AssetBundles";
    #endif 


    public static Dictionary<string, Sprite> DicSprite = new Dictionary<string, Sprite>();

    public static Dictionary<string, ResourceAttribute> DicRecource = new Dictionary<string, ResourceAttribute>();

	public static Dictionary<string, Sprite> allSprite = new Dictionary<string, Sprite>(); 
    public static Dictionary<string, GameObject> allPrefab = new Dictionary<string, GameObject>();
    public static Dictionary<string, GameObject> allGameObject = new Dictionary<string, GameObject>();
    public static Dictionary<string, GameObject> allModule = new Dictionary<string, GameObject>();
    public static  Dictionary<string , AssetBundle> allAssetBundle = new Dictionary<string, AssetBundle>();
    public static  Dictionary<string , byte[]> allLuaByte = new Dictionary<string,  byte[]>();
    public static  Dictionary<string ,AudioClip> allAudioClip = new Dictionary<string,  AudioClip>();
	public static Dictionary<string, Font> allFont = new Dictionary<string, Font>();
	public static Dictionary<string, Texture> allTexture = new Dictionary<string, Texture>(); 


    public static void debugtest()
    {
        foreach (var t in allSprite)
        {
            Logging.HYLDDebug.Log(t.Key);
        }
    }



    static string Type = "one";

   public static string luaFileName ; // 更新下来的 lua ab文件的名字，每次更新后都不一样，后拼着md5等
     public static void InitFontResource(string name)
    {
    	  AssetBundle ab = Load(name); 
        // ab.LoadAsset<Font>(key);
        //  allFont.Add(temp,  System.Text.UTF8Encoding.Default.GetBytes (ab.LoadAsset<TextAsset>(abs[i]).text   )     );
    }
    public static void InitSpriteResource(string name)
    {
        // 公告放到base里，所以不需要专门解析base
        ////Logging.HYLDDebug.Log("InitSpriteResource ****** ");
      AssetBundle ab = Load("base");
        //string[] abs = ab.GetAllAssetNames();
        //for (int i = 0; i < abs.Length; i++)
        //{
        //    SetAsset(abs[i], ab);
        //}
        
    }


    public static string[] GetNextPages(string name)
    {
      
        // ////Logging.HYLDDebug.Log("GetNextPages " + name);
        string temp = DicRecource[name].nextPages;
        

        return temp.Split('*');
    }
	static Texture GetTexture(string key ,string path){

		path = path.ToLower();

		if (allTexture.ContainsKey( key))
		{
			////Logging.HYLDDebug.Log("存在Sprite " + key); 
			return allTexture[ key];
		}
		else
		{   
			if(allAssetBundle.ContainsKey("Texture")){
				SetAssetOne("Texture",key,"Texture", allAssetBundle["Texture"]);
			}else{
				AssetBundle ab = Load("Texture"); 

				allAssetBundle["Texture"] = ab;
				SetAssetOne("Texture",key,"Texture", ab);
			}

		 
			return allTexture[ key];
		}
	}
	public static Texture GetTexturePath(string key, string path)
	{ 
		//Logging.HYLDDebug.Log("public GetModule 2 " + key  );
		if (!GameConfig.UseAssetsBundle)
		{
			#if UNITY_EDITOR 
                       
                        
			return   AssetDatabase.LoadAssetAtPath<Texture>("Assets/AssetBundlesLocal/texture/"  + key + ".png");
			#else 
			return GetTexture(key,path);
			#endif
		}
		else
		{

			return GetTexture(key,path);
		}
	}
      static Sprite GetSprite(string key ,string path )
    {  path = path.ToLower();
        
        if (allSprite.ContainsKey(path+"_"+key))
        {
            ////Logging.HYLDDebug.Log("存在Sprite " + key); 
            return allSprite[path+"_"+key];
        }
        else
        {  
            // if (Type.Equals("one"))
            // { 
                if(allAssetBundle.ContainsKey(path)){
                     SetAssetOne("Sprite",key,path, allAssetBundle[path]);
                }else{
                Logging.HYLDDebug.Log("不存在 Sprite  " + path);
                AssetBundle ab = Load(path); 
                     
                    allAssetBundle[path] = ab;
                    SetAssetOne("Sprite",key,path, ab);
                }
                  
                // }

            
            // }else
            // {
            //     AssetBundle ab = Load(DicRecource[key].abname);
            //     string[] abs = ab.GetAllAssetNames();
            //     for (int i = 0; i < abs.Length; i++)
            //     {
            //         SetAsset(abs[i], ab);
            //     }
            // } 
            return allSprite[path+"_"+key];
        }
    }
 public static  AudioClip LoadAudioClip(string path, string name)
    {
#if UNITY_EDITOR
      
            return AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/AssetBundlesLocal/" + path + "/" + name + ".mp3");
      
#endif

        return GetAudioClip(name , path );
    }
    

      static AudioClip GetAudioClip(string key ,string path )
    {  path = path.ToLower();
        
        if (allSprite.ContainsKey(path+"_"+key))
        {
            ////Logging.HYLDDebug.Log("存在Sprite " + key); 
            return allAudioClip[path+"_"+key];
        }
        else
        {  
            // if (Type.Equals("one"))
            // { 
                if(allAssetBundle.ContainsKey(path)){
                     SetAssetOne("AudioClip",key,path, allAssetBundle[path]);
                }else{
                     AssetBundle ab = Load(path); 
                     
                    allAssetBundle[path] = ab;
                    SetAssetOne("AudioClip",key,path, ab);
                }
                  
                // }

            
            // }else
            // {
            //     AssetBundle ab = Load(DicRecource[key].abname);
            //     string[] abs = ab.GetAllAssetNames();
            //     for (int i = 0; i < abs.Length; i++)
            //     {
            //         SetAsset(abs[i], ab);
            //     }
            // } 
            return allAudioClip[path+"_"+key];
        }
    }




    
    public static Sprite GetSpritePath(string key, string path)
    { 
        Logging.HYLDDebug.Log("*********************** public GetSpritePath  key  " + key + " path " + path);
        if (!GameConfig.UseAssetsBundle)
        {
            #if UNITY_EDITOR 
           
            return AssetDatabase.LoadAssetAtPath<Sprite>("Assets/AssetBundlesLocal/Sprite/" + path + "/" + key + ".png");
             #else 
               return GetSprite(key,path);
             #endif
        }
        else
        {

            return GetSprite(key,path);
        }
    } 
     public static void SetAssetOne( string type , string key ,string path , AssetBundle  ab )
    {
        Logging.HYLDDebug.Log("SetAssetOne key === " + key );
        Logging.HYLDDebug.Log("SetAssetOne type === " + type );
        Logging.HYLDDebug.Log("SetAssetOne path === " + path );

        if (type == "GameObject")
        {
            allGameObject[path+"_"+key] = ab.LoadAsset<GameObject>(key);
        }
        else if (type == "Sprite")
        {  
            allSprite[path+"_"+key] = ab.LoadAsset<Sprite>(key);
        }
        else if (type == "Prefab")
        {
            allPrefab[path+"_"+key] = ab.LoadAsset<GameObject>(key);
        }
        else if (type == "Module")
        {
            allModule[path+"_"+key] = ab.LoadAsset<GameObject>(key);
        }
         else if (type == "AudioClip")
        {
            allAudioClip[path+"_"+key] = ab.LoadAsset<AudioClip>(key);
		}
		else if (type == "Texture")
		{
			allTexture[ key] = ab.LoadAsset<Texture>(key);
		}
         //  allAssetBundle[ path ] = ab;
    } 




   static GameObject GetPrefab(string key ,string path )
    {  path = path.ToLower();
 
            Logging.HYLDDebug.LogError(path + "   GetPrefab    key  " +key);
       
        if (allPrefab.ContainsKey(path+"_"+key))
        {
            ////Logging.HYLDDebug.Log("存在Sprite " + key); 
            return allPrefab[path+"_"+key];
        }
        else
        {      if(allAssetBundle.ContainsKey(path)){
                     SetAssetOne("Prefab",key,path, allAssetBundle[path]);
                }else{
                    AssetBundle ab = Load(path);
                    allAssetBundle[path] = ab;
                    SetAssetOne("Prefab",key,path, ab);
                }
                
            return allPrefab[path+"_"+key];
        }
    }


   static GameObject GetGameObject(string key ,string path )
    {    path = path.ToLower();
              Logging.HYLDDebug.LogError(path + "   GetGameObject    key  " +key);
       
        if (allGameObject.ContainsKey(path+"_"+key))
        {
            ////Logging.HYLDDebug.Log("存在Sprite " + key); 
            return allGameObject[path+"_"+key];
        }
        else
        {      if(allAssetBundle.ContainsKey(path)){
                     SetAssetOne("GameObject",key,path, allAssetBundle[path]);
                }else{
                    AssetBundle ab = Load(path);
                    
                    allAssetBundle[path] = ab;
                   // allAssetBundle[DicRecource[key].abname] = ab;
                    SetAssetOne("GameObject",key,path, ab);
                }
            return allGameObject[path+"_"+key];
        }
    }
   static GameObject GetModule(string key ,string path )
    {       Logging.HYLDDebug.Log( "   GetModule  path = "+path +"  key = " +key);

        path = path.ToLower();
        if (allModule.ContainsKey(path+"_"+key))
        {
            Logging.HYLDDebug.Log(" have Module " + path+"_"+key); 
            return allModule[path+"_"+key];
        }
        else
        
        {      if(allAssetBundle.ContainsKey(path)){

                     Logging.HYLDDebug.Log("ContainsKey GetModule  " + path);   
                     SetAssetOne("Module",key,path, allAssetBundle[path]);
                }else{
                    
                     Logging.HYLDDebug.Log("!ContainsKey GetModule  " + path);   
                    AssetBundle ab = Load(path);
                    
                    allAssetBundle[path] = ab;
                   // allAssetBundle[DicRecource[key].abname] = ab;
                    SetAssetOne("Module",key,path, ab);
                }
            return allModule[path+"_"+key];
        }
    }












    public static GameObject GetPrefabPath(string key, string path)
    { 
        if (!GameConfig.UseAssetsBundle)
        {
            #if UNITY_EDITOR 
           
            return AssetDatabase.LoadAssetAtPath<GameObject>("Assets/AssetBundlesLocal/Prefab/" + path + "/" + key + ".prefab");
             #else 
               return GetPrefab(key,path);
             #endif
        }
        else
        {

            return GetPrefab(key,path);
        }
    }



    public static GameObject GetModulePath(string key, string path)
    {
        //Logging.HYLDDebug.Log("public GetModule 2 " + key  );
        if (!GameConfig.UseAssetsBundle)
        {
            #if UNITY_EDITOR 
           
            return AssetDatabase.LoadAssetAtPath<GameObject>("Assets/AssetBundlesLocal/Module/" + path + "/" + key + ".prefab");
             #else 
               return GetModule(key,path);
             #endif
        }
        else
        {

            return GetModule(key,path);
        }
    } 
    public static GameObject GetGameObjectPath(string key, string path)
    {
        if (!GameConfig.UseAssetsBundle)
        {    
           #if UNITY_EDITOR 
                  return AssetDatabase.LoadAssetAtPath<GameObject>("Assets/AssetBundlesLocal/Prefab/" + path + "/" + key + ".prefab");
            #else  
                 return GetGameObject(key,path);
            #endif
        }
        else
        {

            return GetGameObject(key,path);
        }
    } 

    public static Sprite LoadSprite(string Path)
    {
//        //Logging.HYLDDebug.Log("ResourceManager的LoadSprite的Path=" + Path);
        if (Resources.Load<GameObject>("Sprites/" + Path) != null)
        {
            return Resources.Load<GameObject>("Sprites/" + Path).GetComponent<Image>().sprite;
        }
        else
        {
            return null;
        }
       
    }


    static public AssetBundle Load(string name)
    {
        Logging.HYLDDebug.Log(" Load  **************** name = " + name);  //+ pathRoot + assetBundlePath + "/" 
        if (GameConfig.UseAssetsBundle)
        {
             pathRoot = LoadTools.assetBundlePath;
            assetBundlePath = "";
        }
        // D:/ Set a small target Tencent/ Lua / Lua2022 / Xlua / Xlua_study4 / XLUA_study / PersistentData / lua
        Logging.HYLDDebug.LogError("uuuuuuuuuuuuu  "  + pathRoot + assetBundlePath + "/" + name);
        return AssetBundle.LoadFromFile(pathRoot + assetBundlePath + "/" +name);
        
    }


	//储存格式： 玩家账号|音乐|音效|是否跟随|是否显示聊天|同屏玩家 
    public static string GetPlayerData(){  
        string baseDate = "";
        if (PlayerPrefs.HasKey("baseDate")) {
            baseDate = PlayerPrefs.GetString("baseDate"); 
        }else{
            baseDate = " |1|1|0|1|1|ip";
        }
        return baseDate;
     }
     public static void SetPlayerData(string str){
       PlayerPrefs.SetString("baseDate",str); 
     }



     //设置引导位置
     public static void SetGuidePos(GameObject GuiObj,string posID,string posX,string posW){
        //入参posID  1 = Left  2 = Right  3 = top  4 = bottom
        float X = float.Parse(posX);
        float W = float.Parse(posW); 
        if (posID == "1"){
             GuiObj.GetComponent<RectTransform>().SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, X, W);   
        }else if (posID == "2"){
             GuiObj.GetComponent<RectTransform>().SetInsetAndSizeFromParentEdge(RectTransform.Edge.Right, X, W);     
        }else if (posID == "3"){
             GuiObj.GetComponent<RectTransform>().SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, X, W);  
        }else if (posID == "4"){
             GuiObj.GetComponent<RectTransform>().SetInsetAndSizeFromParentEdge(RectTransform.Edge.Bottom, X, W);    
        }
     }
  

 public static void loadLuaToByte( string name   ){ 
     
                
                AssetBundle ab = Load(name);  
                string[] abs = ab.GetAllAssetNames(); 

                for (int i = 0; i < abs.Length; i++)
        {
            //Logging.HYLDDebug.LogError("lua ------------------------------- begin");
            //Logging.HYLDDebug.LogError(abs[i]);
                   string  temp = abs[i].Substring(11);
            temp = temp.Substring(0 , temp.Length - 8);

            //Logging.HYLDDebug.LogError(temp);
            //Logging.HYLDDebug.LogError("lua ------------------------------- end");
            if ( allLuaByte.ContainsKey( temp)){ 
                     return ;
                   } 
                   allLuaByte.Add(temp,  System.Text.UTF8Encoding.Default.GetBytes (ab.LoadAsset<TextAsset>(abs[i]).text   )     );
                 //if(abs[i].IndexOf("login") !=-1){
                 //   Logging.HYLDDebug.Log("**********loadLuaToByte  ** name  ************  "+abs[i]);
                 //} 
                } 
    }

}
