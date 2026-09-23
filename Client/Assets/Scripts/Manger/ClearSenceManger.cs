/****************************************************
    Author:            龙之介
    CreatTime:    2022/4/19 19:41:6
    Description:     Nothing
*****************************************************/

using System.Collections;
using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

namespace Manger
{ 

	public class ClearSenceManger :MonoBehaviour
	{
		// 仅保留场景序列化引用（HYLDAsyncScence.unity 中已挂载该面板）。
		// 旧「清场景」协议退役后本类不再与该面板交互；为避免改动 Unity 资产的
		// 序列化语义，此处不清空该字段（面板 class/GUID 保持不变，仅作本地进度 UI）。
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
			//Logging.HYLDDebug.LogError("AsyncLoadScne " + scene);
			async = SceneManager.LoadSceneAsync(scene);
			//yield return async;

			async.allowSceneActivation = false;

			// 纯本地异步加载：allowSceneActivation=false 时 Unity 最多加载到 0.9 就停住，
			// 因此 progress >= 0.9 即视为场景已就绪并直接放行。
			// 旧链在这里会等加载面板的「全员清场完成」远端 Ready 才放行；该协议与专属等待
			// 分支已随旧战斗链退役，避免没有对端响应时把加载流程挂死。
			while (async.progress < 0.9f)
			{
				progressValue = async.progress;
				slider.value = progressValue;
				progress.text = (int)(slider.value * 100) + " %";
				yield return null;
			}

			slider.value = 1.0f;
			progress.text = "100 %";
			async.allowSceneActivation = true;
		}
		void OnDestroy()
		{
			async = null;
			//		pictureAB.Unload (true);
			Resources.UnloadUnusedAssets();
		}
	}
}