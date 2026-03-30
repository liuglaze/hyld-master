/****************************************************
    Author:            龙之介
    CreatTime:    2021/4/16 14:39:54
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using UnityEngine.Networking;
using System.IO;
using System.Text;

namespace LongZhiJie
{
	public class LoadLuaTxt :MonoBehaviour
	{
        private void Start()
        {
            StartCoroutine(LoadResourCorotine());
    
        }

        IEnumerator LoadResourCorotine()
        {
            ///更新lua补丁
            UnityWebRequest request = UnityWebRequest.Get(@"http://localhost/Invotion.lua.txt");
            yield return request.SendWebRequest();

            while (request.isHttpError)
            {
                Logging.HYLDDebug.LogError("ERROR: " + request.error);
                yield return null;
            }

            while (!request.isDone)
            {
                yield return null;
            }

            byte[] data = request.downloadHandler.data;
            string str = Encoding.UTF8.GetString(data);
            File.WriteAllText(@"D:\XuanShuiLiuLi\Invotion\PlayerGamePackage\lua\Invotion.lua.txt", str,Encoding.UTF8);



            
            //更新lua释放补丁
            UnityWebRequest request1 = UnityWebRequest.Get(@"http://localhost/Invotiondispos.lua.txt");

            yield return request1.SendWebRequest();
   
            
            while (request1.isHttpError)
            {
                Logging.HYLDDebug.LogError("ERROR: " + request1.error);
                yield return null;
            }

            while (!request1.isDone)
            {
                yield return null;
            }

            byte[] data1 = request1.downloadHandler.data;
            string str1 = Encoding.UTF8.GetString(data1);
            File.WriteAllText(@"D:\XuanShuiLiuLi\Invotion\PlayerGamePackage\lua\Invotiondispos.lua.txt", str1, Encoding.UTF8);


            //Manger.ClearSceneData.LoadScene(1);
        }
	}
}