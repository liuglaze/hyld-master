/*
 * Tencent is pleased to support the open source community by making xLua available.
 * Copyright (C) 2016 THL A29 Limited, a Tencent company. All rights reserved.
 * Licensed under the MIT License (the "License"); you may not use this file except in compliance with the License. You may obtain a copy of the License at
 * http://opensource.org/licenses/MIT
 * Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations under the License.
*/

using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using XLua;
using System;
using System.IO;
using System.Text;

[System.Serializable]
public class Injection
{
    public string name;
    public GameObject value;
}

[LuaCallCSharp]
public class LuaBehaviour : MonoBehaviour {
    public TextAsset luaScript;
    public string  luaScript_ls;
    public Injection[] injections;
 
    internal static LuaEnv luaEnv = new LuaEnv(); //all lua behaviour shared one luaenv only!
    internal static float lastGCTime = 0;
    internal const float GCInterval = 1;//1 second 

    private static Action luaStart;
    private static  Action luaUpdate;
    private static Action luaOnDestroy;

    public static  LuaTable scriptEnv;
  //  float times = 2;

    //private TextAsset text_;
    string main;
    string main2;

    [CSharpCallLua]
    public delegate void LuaAction(string v);
    //[CSharpCallLua]
    //public delegate void test22();
 

    [CSharpCallLua]
    public delegate string ConfigNameList_GetRandomName();

    public static ConfigNameList_GetRandomName configNameList_GetRandomName;


    [CSharpCallLua]
    public delegate string ConfigYuanFen_GetData(string name,string value,string name2);// 拿到缘分数据

    public static ConfigYuanFen_GetData configYuanFen_GetData;
 


    //[CSharpCallLua]
    //public delegate void luaStart();
    //[CSharpCallLua]
    //public delegate void luaUpdate();
    //[CSharpCallLua]
    //public delegate void luaOnDestroy();
    //[CSharpCallLua]
    //public delegate void luaAwake();




	public  static LuaAction ray;
	public  static LuaAction rayF;


   public  static LuaAction sockerSendMsg;

    private byte[] CustomLoaderMethod(ref string fileName)
    {
        //	Logging.HYLDDebug.Log("CustomLoaderMethod  fileName == "+fileName);
#if UNITY_ANDROID && !UNITY_EDITOR
	//	Logging.HYLDDebug.Log("UNITY_ANDROID  fileName == "+fileName);
     //Logging.HYLDDebug.Log("ResourceManager.luaFileName "+ ResourceManager.luaFileName);
   //  Logging.HYLDDebug.Log("fileName  "+fileName);
        //  AssetBundle ab = ResourceManager.Load( ResourceManager.luaFileName ); //"lua_945ca74524bdea802078acbf98db6f0b"
        //  string  str = ab.LoadAsset<TextAsset>(fileName).text;
        //           Logging.HYLDDebug.Log("str  "+str);
        //  byte[] byteArray = System.Text.UTF8Encoding.Default.GetBytes ( str );         
        //     return File.ReadAllBytes(fileName);

        return  ResourceManager.allLuaByte[ fileName.ToLower() ];



#elif UNITY_IOS && !UNITY_EDITOR
        //  AssetBundle ab = ResourceManager.Load( ResourceManager.luaFileName ); //"lua_945ca74524bdea802078acbf98db6f0b"
        //  string  str = ab.LoadAsset<TextAsset>(fileName).text;
                 
        //  byte[] byteArray = System.Text.UTF8Encoding.Default.GetBytes ( str );         
        // return File.ReadAllBytes(fileName);
	//	Logging.HYLDDebug.Log("ResourceManager.allLuaByte  fileName == "+fileName.ToLower());
	//	Logging.HYLDDebug.Log("ResourceManager.allLuaByte    == "+ResourceManager.allLuaByte[fileName.ToLower()]);
		return  ResourceManager.allLuaByte[fileName.ToLower()];

#else
      //  Logging.HYLDDebug.Log($"{GameConfig.UseAssetsBundle} CustomLoaderMethod  {fileName}");
        if (GameConfig.UseAssetsBundle)
        {
            //TODO:问题出在这里
          //  Logging.HYLDDebug.Log(":????Ad");
         //   Logging.HYLDDebug.LogError(ResourceManager.allLuaByte[fileName.ToLower()].Length);
            //Logging.HYLDDebug.LogError(Encoding.Unicode.GetString(ResourceManager.allLuaByte[file''Name.ToLower()]));
            return ResourceManager.allLuaByte[fileName.ToLower()];
        }
        else
        {
            //	Logging.HYLDDebug.Log("UNITY_IOS  fileName == "+fileName);
            //找到指定文件  
            fileName = main2 + fileName.Replace('.', '/') + ".lua.txt";
            
            //   Logging.HYLDDebug.Log("fileName  "  + fileName); 
            if (File.Exists(fileName))
            {
                //  Logging.HYLDDebug.Log("File.Exists  " );
               // Logging.HYLDDebug.LogError(Encoding.Unicode.GetString(File.ReadAllBytes(fileName.ToLower())));
                return File.ReadAllBytes(fileName.ToLower());
            }
            else
            {
                // Logging.HYLDDebug.Log("! File.Exists  " );
                return null;
            }
        }


	

          
 
    #endif
      
 

    }
      
    void Awake()
	{
		//Logging.HYLDDebug.Log ("Awake");
        initWWWPath();

	//	Logging.HYLDDebug.Log ("Awake2");
         LuaEnv.CustomLoader method = CustomLoaderMethod;
		/// Logging.HYLDDebug.Log ( Application.dataPath );
		/// 
		/// 
	//	Logging.HYLDDebug.Log ("luaEnv1 "+luaEnv);
		luaEnv = new LuaEnv();
	//	Logging.HYLDDebug.Log ("luaEnv2  "+luaEnv);
		//Logging.HYLDDebug.Log ("Awake3");
          luaEnv.AddLoader(method);

	//	Logging.HYLDDebug.Log ("Awake4");
		scriptEnv = luaEnv.NewTable();
		//Logging.HYLDDebug.Log ("Awake5");
        // scriptEnv = luaEnv.Global;
        // 为每个脚本设置一个独立的环境，可一定程度上防止脚本间全局变量、函数冲突
		LuaTable meta = luaEnv.NewTable();
		//Logging.HYLDDebug.Log ("Awake6");
		meta.Set("__index", luaEnv.Global);
	//	Logging.HYLDDebug.Log ("Awake7");
		scriptEnv.SetMetaTable(meta);
	//	Logging.HYLDDebug.Log ("Awake8");
         meta.Dispose();

	//	Logging.HYLDDebug.Log ("Awake9");
        scriptEnv.Set("self", this); 
        //foreach (var injection in injections)
        //{
        //    scriptEnv.Set(injection.name, injection.value);
        //}
        //luaScript.name.Replace("lua", "") +
        StartCoroutine(LoadLua());


	//	Logging.HYLDDebug.Log ("Awake10");
//        LoadLua() ;

//		Logging.HYLDDebug.Log ("Awake11");

    }
   void initWWWPath(){  //main。lua的下载地址 
            string streamingPath =  LoadTools.assetBundlePath;// Application.persistentDataPath; 
          
            #if UNITY_ANDROID && !UNITY_EDITOR  
                    main = "file://"+streamingPath + "/assets/lua/"    ; 
                    main2 =  streamingPath + "/assets/lua/"    ; 
            #else 
                    main = "file://" + Application.dataPath + "/" + "Lua/" ;
                    main2 = Application.dataPath + "/" + "Lua/" ;
#endif
        Logging.HYLDDebug.Log($"main:  {main}  {main2}");
   }


    IEnumerator LoadLua() // IEnumerator
    {
        yield return new WaitForFixedUpdate();
        // Logging.HYLDDebug.Log("Application.persistentDataPath  " + Application.persistentDataPath);
        //   Logging.HYLDDebug.Log("LoadLua  **** " +  main + "main.lua.txt");
        //   WWW www = new WWW(main + "main.lua.txt");
        TextAsset mainText = Resources.Load<TextAsset>("main.lua");
        Logging.HYLDDebug.LogError(mainText);
       // 等待WWW代码执行完毕之后后面的代码才会执行。
    //   yield return www;
    //   if (www.error == null && www.isDone)
    //   { 
         //  luaScript_ls =  www.text;
           luaScript_ls = mainText.text;
        Logging.HYLDDebug.Log($"LoadLua  {mainText}   {mainText.text}");
        // Logging.HYLDDebug.Log(luaScript_ls);
        Init2();
    //   }else{
    //       Logging.HYLDDebug.Log("www.error "  + www.error);
   //    }


    }
void Init2(){

       
       // TextAsset luaScript = Resources.Load("enter.lua") as TextAsset;
        luaEnv.DoString(luaScript_ls, "LuaBehaviour", scriptEnv);  //, "LuaBehaviour", null

        Action luaAwake = scriptEnv.Get<Action>( "Awake");
                                       scriptEnv.Get( "Start", out luaStart);
                                         scriptEnv.Get( "update", out luaUpdate);
                                      scriptEnv.Get(  "ondestroy", out luaOnDestroy);
		scriptEnv.Get("sockerSendMsg", out sockerSendMsg);
		scriptEnv.Get("ray", out ray);
		scriptEnv.Get("rayF", out rayF);

        ////Test("1");

        ///// Logging.HYLDDebug.Log("PostReceive = " + Test);
        if (luaAwake != null)
        {
           Logging.HYLDDebug.Log("luaAwake 2 " + luaAwake);
            luaAwake();
        }
        Start1();

 }
	// Use this for initialization
	    void Start1 ()
    {
        if (luaStart != null)
        {
            luaStart();
        }
        //if(sockerSendMsg !=null){
        //    sockerSendMsg("socekt test");
        //}

     
    }
	
	// Update is called once per frame
	void Update ()
    {
        if (luaUpdate != null)
        {
            luaUpdate();
        }
        if (Time.time - LuaBehaviour.lastGCTime > GCInterval)
        {
            luaEnv.Tick();
            LuaBehaviour.lastGCTime = Time.time;
        }
        测试时间 -= Time.deltaTime;
        if (测试时间 < 0)
        {
            测试时间 = 5;
            ResourceManager.debugtest();
        }
       
    }
    float 测试时间 = 5;
    void OnDestroy()
    {
        if (luaOnDestroy != null)
        {
            luaOnDestroy();
        }
        luaOnDestroy = null;
        luaUpdate = null;
        luaStart = null;
        scriptEnv.Dispose();
        injections = null;
        configNameList_GetRandomName = null;
    }
 
}
