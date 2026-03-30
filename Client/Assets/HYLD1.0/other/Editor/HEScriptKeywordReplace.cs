/*
 * * * * * * * * * * * * * * * * 
 * Author:        魏佳楠
 * CreatTime:  #CREATIONDATE# 
 * Description: 
 * * * * * * * * * * * * * * * * 
*/
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;
using UnityEditor;


public class HEScriptKeywordReplace : UnityEditor.AssetModificationProcessor {
	public static void OnWillCreateAsset ( string path ) {
		path = path.Replace(".meta", "");
		int index = path.LastIndexOf(".");
		string file = path.Substring(index);
		if (file != ".cs" && file != ".js" && file != ".boo") return;
		string fileExtension = file;

		index = Application.dataPath.LastIndexOf("Assets");
		path = Application.dataPath.Substring(0, index) + path;
		file = System.IO.File.ReadAllText(path);

		file = file.Replace("#CREATIONDATE#", System.DateTime.Now.ToString());
		file = file.Replace("#PROJECTNAME#", PlayerSettings.productName);
		file = file.Replace("#SMARTDEVELOPERS#", PlayerSettings.companyName);
		file = file.Replace("#FILEEXTENSION#", fileExtension);

		System.IO.File.WriteAllText(path, file);
		AssetDatabase.Refresh();
	}
}