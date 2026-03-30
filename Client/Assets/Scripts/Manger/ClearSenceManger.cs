/****************************************************
    Author:            龙之介
    CreatTime:    2022/4/19 19:41:6
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using UnityEngine.SceneManagement;
using SocketProto;

namespace Manger
{ 

	public class ClearSenceManger :MonoBehaviour
	{
		public MVC.UISliderPanel UISliderPanel;
		//显示进度的文本

		public Text progress;

		//进度条的数值

		private float progressValue;

		//进度条

		public Slider slider;

		//下一个场景
		private static int nextScene;
		//异步对象
		private AsyncOperation async;
        void Start()
        {
			//slider = FindObjectOfType<Slider>();
			//Logging.HYLDDebug.LogError("Clear!");
			StartCoroutine(ClearResouces());
        }
		IEnumerator ClearResouces()
		{
			yield return null;
#if UNITY_EDITOR

#else
			//		int _id = 10001;
			//		string abName = "playerbigpic" + _id.ToString ();
			//		string path = GlobalData.GetInstance ().GetABPath (abName);
			//		pictureAB = AssetBundle.LoadFromFile (path);
			//		yield return pictureAB;
			//
			//		string objName = "role_" + _id.ToString ();
			//
			//		var perfreb_player = pictureAB.LoadAsset<GameObject> (objName);
			//
			//		var obj_player = Instantiate (perfreb_player,_roleParent) as GameObject;
			//		obj_player.transform.localPosition = Vector3.zero;
			//		obj_player.transform.localEulerAngles = Vector3.zero; 
			//		obj_player.transform.localScale = Vector3.one;
			//
			//
			Resources.UnloadUnusedAssets();
			yield return new WaitForSeconds(0.1f);

			//		Material[] matAry = Resources.FindObjectsOfTypeAll<Material>();
			//
			//		int _num = 0;
			//		for (int i = 0; i < matAry.Length; ++i)
			//		{
			//			matAry [i] = null;
			//			_num++;
			//			if (_num % 5 == 0) yield return null;
			//		}
			//			
			//		Texture[] TexAry = Resources.FindObjectsOfTypeAll<Texture>();
			//
			//		for (int i = 0; i < TexAry.Length; ++i)
			//		{
			//			TexAry [i] = null;
			//			_num++;
			//			if (_num % 5 == 0) yield return null;
			//		}

			//卸载没有被引用的资源
			Resources.UnloadUnusedAssets();

			//立即进行垃圾回收
			GC.Collect();
			GC.WaitForPendingFinalizers();//挂起当前线程，直到处理终结器队列的线程清空该队列为止
			GC.Collect();


			yield return null;
#endif
			//Logging.HYLDDebug.LogError("Clear Over");
			StartCoroutine(AsyncLoadScene(nextScene));
		}
		/// <summary>
		/// 静态方法，直接切换到ClearScene，此脚本是挂在ClearScene场景下的，就会实例化，执行资源回收
		/// </summary>
		/// <param name="_nextSceneName"></param>
		public static void LoadScene(int _nextScene)
		{
			nextScene = _nextScene;
			SceneManager.LoadScene(SceneConfig.clearScene);

		}
		/// <summary>
		/// 异步加载下一个场景
		/// </summary>
		/// <param name="sceneName"></param>
		/// <returns></returns>
		IEnumerator AsyncLoadScene(int scene)
		{
			isAllPlayerClearOk = false;
			//Logging.HYLDDebug.LogError("AsyncLoadScne " + scene);
			async = SceneManager.LoadSceneAsync(scene);
			//yield return async;

			async.allowSceneActivation = false;

			while (!async.isDone)
			{
				if (async.progress < 0.9f)
					progressValue = async.progress;
				else
					progressValue = 1.0f;
				slider.value = progressValue;
				
				progress.text = (int)(slider.value * 100) + " %";
				if (progressValue >= 0.95)
				{
					if (scene != SceneConfig.battleScene)
					{
						break;
					}

					UISliderPanel.SendLoadOver();
					break;
				
				}
				yield return null;
			}


			//Server.UDPSocketManger.Instance.Send(pack);


			if (scene == SceneConfig.battleScene)
			{
				yield return new WaitUntil(() => {

					return UISliderPanel.IsCanEnterBattle; // 在这里等待所有玩家都异步场景加载完毕
				});
			}
			
			async.allowSceneActivation = true;
		}
		private bool isAllPlayerClearOk = false;
		void OnDestroy()
		{
			async = null;
			//		pictureAB.Unload (true);
			Resources.UnloadUnusedAssets();
		}
	}
}