using System.Collections;
using System.Collections.Generic;
using UnityEngine; 
using System.IO;
using System;
using System.Text;
using UnityEditor;

public class CreateAssetBundles : EditorWindow
{
 
     string  localPath = "AssetBundlesLocal"; 

    public UnityEngine.Object go = null;
    int buttonNum = 1; 
    bool isMain = true;
    bool isSecond = false;
    bool isPop = false;
    bool containOut = false;
    bool toggleEnabled; 


    private static Dictionary<string, string> dicArgs;
   //[MenuItem("Build/Build AssetBundle Android")]
   // public static void BuildAssetBundleAndroid()
   // {
   //     BuildAssetBundles(BuildTarget.Android);
   // }
    [MenuItem("Build/Build AssetBundle PC")]
    public static void BuildAssetBundlePC()
    {
        BuildAssetBundles(BuildTarget.StandaloneWindows64);
    }

    public static void BuildAssetBundles(BuildTarget buildTarget)
    {
        
        string p = Application.dataPath.Replace("Assets", "AssetBundles");
        //D:/Set a small target Tencent/Lua/Lua学习2022/Xlua/Xlua_study4/XLUA_study/AssetBundles
        if (Directory.Exists(p) == true)
            Directory.Delete(p, true);
        Directory.CreateDirectory(p);
        /*
        p = Application.dataPath + "/StreamingAssets/AssetBundle";
        //D:/Set a small target Tencent/Lua/Lua学习2022/Xlua/Xlua_study4/XLUA_study/Assets/StreamingAssets/AssetBundle
        if (Directory.Exists(p) == true)
            Directory.Delete(p, true);
        Directory.CreateDirectory(p);
        */
        ///创建Bundle依赖
        CreateBundle();

        BuildPipeline.BuildAssetBundles("AssetBundles", BuildAssetBundleOptions.ChunkBasedCompression, buildTarget);

        CreateAssetslist();

        Logging.HYLDDebug.Log("BuildAssetBundles End");
    }

 public static void CreateBundle()
    {
        ClearAll();

        string path = "Assets/AssetBundlesLocal";

        string[] allDir = Directory.GetDirectories(path); 
        for (int i = 0; i < allDir.Length; i++)
        {
            string[] allFiles = Directory.GetFiles(allDir[i]);
            string dirName = Path.GetFileName(allDir[i]);  
            for (int j = 0; j < allFiles.Length; j++)
            { 
                EditorUtility.DisplayProgressBar(dirName, allFiles[j], (1f+j)/allFiles.Length);
                if (Path.GetExtension(allFiles[j]) == ".meta")
                    continue;
                //通过静态方法GetAtPath获取指定路径（相对路径）下的资源的导入器
                AssetImporter importer = AssetImporter.GetAtPath(allFiles[j]);
                if(importer != null)
                    //设置AssetBundle Name / Property；
                    importer.assetBundleName = dirName.ToLower();
            } 
        }
        
        CreateLuaBundler();
        CreateSoundBundle();
        CreateFontBundle();
        CreateCustomBundle();
        CreateSpriteBundle();
        CreatePrefabBundle();
		CreateMoudleBundle();
        CreateTextureBundle();
        EditorUtility.ClearProgressBar();

        AssetDatabase.Refresh();
    }
 private static void CreateMoudleBundle(){
     string path = "Assets/AssetBundlesLocal/Module";

        string[] allDir = Directory.GetDirectories(path);
        for (int i = 0; i < allDir.Length; i++)
        {
            string[] allFiles = Directory.GetFiles(allDir[i]);
            string dirName = Path.GetFileName(allDir[i]); 
            
            for (int j = 0; j < allFiles.Length; j++)
            { 
                EditorUtility.DisplayProgressBar("Module "+dirName, allFiles[j], (1f+j)/allFiles.Length);
                if (Path.GetExtension(allFiles[j]) == ".meta")
                    continue;
                AssetImporter importer = AssetImporter.GetAtPath(allFiles[j]);
                if(importer != null)
                    importer.assetBundleName = dirName.ToLower();
            }
 
        } 
 }

 private static void CreatePrefabBundle(){
     string path = "Assets/AssetBundlesLocal/Prefab";

        string[] allDir = Directory.GetDirectories(path);
        for (int i = 0; i < allDir.Length; i++)
        {
            string[] allFiles = Directory.GetFiles(allDir[i]);
            string dirName = Path.GetFileName(allDir[i]); 
            
            for (int j = 0; j < allFiles.Length; j++)
            { 
                EditorUtility.DisplayProgressBar("Prefab "+dirName, allFiles[j], (1f+j)/allFiles.Length);
                if (Path.GetExtension(allFiles[j]) == ".meta")
                    continue;
                AssetImporter importer = AssetImporter.GetAtPath(allFiles[j]);
                if(importer != null)
                    importer.assetBundleName = dirName.ToLower();
            }
 
        } 
 }
 private static void CreateSpriteBundle(){
     string path = "Assets/AssetBundlesLocal/Sprite";

        string[] allDir = Directory.GetDirectories(path);
        for (int i = 0; i < allDir.Length; i++)
        {
            string[] allFiles = Directory.GetFiles(allDir[i]);
            string dirName = Path.GetFileName(allDir[i]); 
            
            for (int j = 0; j < allFiles.Length; j++)
            { 
                EditorUtility.DisplayProgressBar("Sprite "+dirName, allFiles[j], (1f+j)/allFiles.Length);
                if (Path.GetExtension(allFiles[j]) == ".meta")
                    continue;
                AssetImporter importer = AssetImporter.GetAtPath(allFiles[j]);
                if(importer != null)
                    importer.assetBundleName = dirName.ToLower();
            }
 
        } 
 } 
    private static void CreateAssetslist()
    {
        string txtInfo = GetCurrentTimeUnix().ToString();
        string txtInfoAtStreaming = txtInfo;
        string path = Application.dataPath.Replace("Assets", "AssetBundles");
        string[] arrFiles = Directory.GetFiles(path);
        string[] bundleNameAtStreaming = new string[] {"AtlasPublic","AtlasItem" 
        ,"MainUI","Login" };

        string type = "";

        if (dicArgs != null && dicArgs.ContainsKey("BundleType") == true)
        {
            type = dicArgs["BundleType"];
        }


        List<string> arrFilesLevel1 = new List<string>() { "atlaspublic","atlasitem" 
        ,"mainui","login" };

        for (int i = 0; i < arrFiles.Length; i++)
        {
            EditorUtility.DisplayProgressBar("BuildAssetBundles", arrFiles[i], 1f * (1 + i) / arrFiles.Length);

            string fileName = Path.GetFileNameWithoutExtension(arrFiles[i]);
            string extension = Path.GetExtension(arrFiles[i]);
            int level = 0;
            if (extension == ".manifest")
            {
                File.Delete(arrFiles[i]);
                continue;
            }

            if(arrFilesLevel1.IndexOf(fileName) > -1)
            {
                level = 1;
            }

            FileStream file = new FileStream(arrFiles[i], FileMode.Open);
            string md5 = GetMD5HashFromFile(file);
            string info = "0," + fileName + "," + md5 + "," + file.Length + "," + level;
            file.Close();

            txtInfo += "\n" + info;

            if (type == "Normal" && Array.IndexOf(bundleNameAtStreaming, fileName) > -1 || type == "Full")
            {
                txtInfoAtStreaming += "\n" + info;
                File.Copy(arrFiles[i], Application.dataPath + "/StreamingAssets/AssetBundle/" + Path.GetFileName(arrFiles[i]));
            }

            File.Move(arrFiles[i], Path.Combine(path, fileName + "_" + md5 + extension));
        }

        EditorUtility.ClearProgressBar();

        File.WriteAllText(Application.dataPath.Replace("Assets", "AssetBundles") + "/assetslist.txt", txtInfo);
        File.WriteAllText(Application.dataPath + "/StreamingAssets/AssetBundle/assetslist.txt", "0");//txtInfoAtStreaming
    }


    public static void ClearAll()
    {
        /*
        string[] allFiles = AssetDatabase.GetAllAssetPaths();
        Logging.HYLDDebug.Log(" allFiles.Length " + allFiles.Length);
        for (int i = 0; i < allFiles.Length; i++)
        {
            EditorUtility.DisplayProgressBar("clear bundler", allFiles[i], (1f + i) / allFiles.Length);

            string fileExtension = Path.GetExtension(allFiles[i]);
            if (fileExtension == ".cs" || fileExtension == ".js")
                continue;
            Logging.HYLDDebug.Log(" fileExtension   " + fileExtension + "  " + allFiles[i]);
            AssetImporter importer = AssetImporter.GetAtPath(allFiles[i]);
            if (importer != null)

                importer.assetBundleName = "";
        }
        EditorUtility.ClearProgressBar();

        */
        DirectoryInfo direction = new DirectoryInfo("Assets/AssetBundlesLocal");
        FileInfo[] files = direction.GetFiles("*", SearchOption.AllDirectories);

        Logging.HYLDDebug.Log(files.Length);

        for (int i = 0; i < files.Length; i++)
        {
            if (files[i].Name.EndsWith(".meta"))
            {
                continue;
            }
           // Logging.HYLDDebug.Log(Application.dataPath + "||||" + files[i]+"  == "+ files[i].FullName.IndexOf(Application.dataPath.Replace("/", "\\")));
            string file = files[i].FullName.Substring(Application.dataPath.Length-6);
            file=file.Replace("\\", "/");




            EditorUtility.DisplayProgressBar("clear bundler", files[i].Name, (1f + i) / files.Length);

            string fileExtension = Path.GetExtension(file);
            if (fileExtension == ".cs" || fileExtension == ".js")
                continue;
            Logging.HYLDDebug.Log(" fileExtension   " + fileExtension + " || FullName: " + file);
            AssetImporter importer = AssetImporter.GetAtPath(file);
            if (importer != null)

            importer.assetBundleName = "";
            //Logging.HYLDDebug.Log(file);
            //Logging.HYLDDebug.Log("Name:" + files[i].Name + "   "+ files[i].FullName +"  file:"+file );
            //Logging.HYLDDebug.Log( "FullName:" + files[i].FullName );
            //Logging.HYLDDebug.Log( "DirectoryName:" + files[i].DirectoryName );
        }
        EditorUtility.ClearProgressBar();
    }
    /*
    public static void ClearAll()
    {

        string[] allFiles = AssetDatabase.GetAllAssetPaths();
        Logging.HYLDDebug.Log(" allFiles.Length "+ allFiles.Length );
        for (int i = 0; i < allFiles.Length; i++)
        {
            EditorUtility.DisplayProgressBar("clear bundler", allFiles[i], (1f + i) / allFiles.Length);

            string fileExtension = Path.GetExtension(allFiles[i]);
            if (fileExtension == ".cs"|| fileExtension == ".js")
                continue;
             Logging.HYLDDebug.Log(" fileExtension   "+ fileExtension);
            AssetImporter importer = AssetImporter.GetAtPath(allFiles[i]);
            if(importer != null)
                
                importer.assetBundleName = "";
        }
        EditorUtility.ClearProgressBar();

     
    }
    */

    public static void CreateLuaBundler()
    {
        string path = "Assets/Lua"; //  string path = "Assets/AssetBundlesLocal/Lua";
        string[] allFiles = Directory.GetFiles(path);
        EditorUtility.DisplayProgressBar("CreateLuaBundler", "wait", 0);
        // for (int i = 0; i < allFiles.Length; i++)
        // {
        //     EditorUtility.DisplayProgressBar("CreateLuaBundler", allFiles[i], (1f+i)/allFiles.Length);
        //     if (Path.GetExtension(allFiles[i]) != ".bytes")
        //     {
        //         continue;
        //     }
        //     AssetImporter importer = AssetImporter.GetAtPath(allFiles[i]);
        //     importer.assetBundleName = "lua";
        // }
        AssetImporter importer = AssetImporter.GetAtPath(path);
        Logging.HYLDDebug.Log("importer " + importer);
        importer.assetBundleName = "lua";

    }
	public static void CreateTextureBundle()
	{
		string path = "Assets/AssetBundlesLocal/texture";
		string[] allFiles = Directory.GetFiles(path);
		for (int i = 0; i < allFiles.Length; i++)
		{
			string fileExtension = Path.GetExtension(allFiles[i]);
			if (fileExtension != ".png")
				continue;
			AssetImporter importer = AssetImporter.GetAtPath(allFiles[i]);
			importer.assetBundleName = "texture";
		}
	}
    public static void CreateSoundBundle()
    {
        string path = "Assets/AssetBundlesLocal/Sound";
        string[] allFiles = Directory.GetFiles(path);
        for (int i = 0; i < allFiles.Length; i++)
        {
            string fileExtension = Path.GetExtension(allFiles[i]);
            if (fileExtension != ".mp3")
                continue;
            AssetImporter importer = AssetImporter.GetAtPath(allFiles[i]);
            importer.assetBundleName = "sound";
        }
    }

    private static void CreateFontBundle()
    {
        string path = "";
        path = "Assets/AssetBundlesLocal/Font";
        string[] allFiles = Directory.GetFiles(path);
        for (int i = 0; i < allFiles.Length; i++)
        {
            string fileExtension = Path.GetExtension(allFiles[i]);
            if (fileExtension == ".meta")
                continue;
            AssetImporter importer = AssetImporter.GetAtPath(allFiles[i]);
            importer.assetBundleName = "font";
        }
    }

    private static void CreateCustomBundle()
    {
        string path = "Assets/AssetBundlesLocal/custom";
        string[] allFiles = Directory.GetFiles(path);
        for (int i = 0; i < allFiles.Length; i++)
        {
            string fileExtension = Path.GetExtension(allFiles[i]);
            if (fileExtension == ".meta")
                continue;
            AssetImporter importer = AssetImporter.GetAtPath(allFiles[i]);
            importer.assetBundleName = "custom";
        }
    }



    private static long GetCurrentTimeUnix()
    {
        TimeSpan cha = (DateTime.Now - TimeZone.CurrentTimeZone.ToLocalTime(new System.DateTime(1970, 1, 1)));
        long t = (long)cha.TotalSeconds;
        return t;
    }

    private static string GetMD5HashFromFile(FileStream file)
    {
        try
        {
            System.Security.Cryptography.MD5 md5 = new System.Security.Cryptography.MD5CryptoServiceProvider();
            byte[] retVal = md5.ComputeHash(file);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < retVal.Length; i++)
            {
                sb.Append(retVal[i].ToString("x2"));
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            throw new Exception("GetMD5HashFromFile() fail, error:" + ex.Message);
        }
    }

  public static void CopyLua()
    {
        EditorUtility.DisplayProgressBar("CopyLua", "wait", 0);

        List<string> list = new List<string>();
        GetAllFiles("Assets/Lua", list);

        string path = "Assets/AssetBundlesLocal/Lua";

        if (Directory.Exists(path) == true)
            Directory.Delete(path,true);

        Directory.CreateDirectory(path);
        Logging.HYLDDebug.Log("list.Count " +list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            if (Path.GetExtension(list[i]) != ".txt") // .lua
                continue;
            string fileName = Path.GetFileNameWithoutExtension(list[i]);
            EditorUtility.DisplayProgressBar("CopyLua", fileName, (i+1f)/list.Count);
            byte[] fileData = File.ReadAllBytes(list[i]);
            //for (int j = 0; j < fileData.Length; j++)
            //{
            //    fileData[j] += (byte)(7 - fileData.Length % 7);
            //}
            File.WriteAllBytes(path + "/" + fileName + ".bytes",fileData);
        }
        EditorUtility.ClearProgressBar();

        AssetDatabase.Refresh();
    }
  public static void GetAllFiles(string path,List<string> listName)
    {
        string[] files = Directory.GetFiles(path);
        listName.AddRange(files);

        string[] dirs = Directory.GetDirectories(path);
        for (int i = 0; i < dirs.Length; i++)
        {
            GetAllFiles(dirs[i], listName);
        }
    }
}

