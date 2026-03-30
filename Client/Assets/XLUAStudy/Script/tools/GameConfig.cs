 
public class GameConfig
{
	public static bool fightTest = false ;
    private static bool useAssetsBundle = false;
    public static bool UseAssetsBundle { get { return useAssetsBundle; } }


    private static string version = "0.0.1.22231";
    public static string Version { get { return version; } }

    public static string Assetversion = "0"; 
#if UNITY_ANDROID && !UNITY_EDITOR
     private static string serverIP = "127.0.0.1";    //"192.168.0.110:80"; 
#else  
     private static string serverIP = "192.168.0.110";    //"192.168.0.110:80"; 
#endif 
    public static string ServerIP { get { return serverIP; } }
#if UNITY_ANDROID && !UNITY_EDITOR
    private static string assetIP = "http://1:6677/cdnj2"; 
#else  
  private static string assetIP = "http://1:1/cdnj2"; 
  //  private static string assetIP = "http://1:1/cdn"; 
#endif
    public static string AssetIP { get { return assetIP; } }
     
     
    public static void Init()
    {
        string txt = "";

        //不要删除下面这段注释
        //{txt}

        string[] a = txt.Split('\n');
        for (int i = 0; i < a.Length; i++)
        {
            if (string.IsNullOrEmpty(a[i]) == true)
                continue;
            string[] b = a[i].Split('=');
            SetData(b[0], b[1]);
        }
    }

    private static void SetData(string key, string value)
    {
        if (key == "version")
            version = value;
        else if (key == "serverIP")
            serverIP = value;
        else if (key == "assetIP")
            assetIP = value;
    }
}
