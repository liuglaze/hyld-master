/****************************************************
    Author:            龙之介
    CreatTime:    2021/4/16 11:32:38
    Description:     Nothing
*****************************************************/


using UnityEditor.Build;
using UnityEditor.Build.Reporting;

public class MyCustomBuildProcessor : IPostBuildPlayerScriptDLLs
{
    public int callbackOrder {get{return 0;}}

    public void OnPostBuildPlayerScriptDLLs(BuildReport report)
    {
        for(int i=0;i<report.files.Length;i++)
        {
            Logging.HYLDDebug.Log(i + " --- " + report.files[i]);
        }
        Logging.HYLDDebug.Log("MyCustomBildProcessor.OnPostBuildPlayerScriptDLLs for target " + report.summary.platform + " at path " + report.summary.outputPath);
    }
}
