/****************************************************
    Author:            龙之介
    CreatTime:    2022/4/25 18:31:6
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using Manger;
using System.Threading;

public class NetGlobal
{
	private static NetGlobal singleInstance;
	private List<Action> list_action = new List<Action>();
	private Mutex mutex_actionList = new Mutex();

	public string serverIP;
	public int udpSendPort;
	public int userUid;
	public static NetGlobal Instance
	{
		get {
			// 如果类的实例不存在则创建，否则直接返回
			if (singleInstance == null)
			{
				singleInstance = new NetGlobal();
			}
			return singleInstance;
		}

	}

	private NetGlobal()
	{
		GameObject obj = new GameObject("NetGlobal");
		obj.AddComponent<ActionManger>();
		GameObject.DontDestroyOnLoad(obj);
	}
	public void Init() { }

	public void Destory()
	{
		singleInstance = null;
	}

	public void AddAction(Action _action)
	{
		mutex_actionList.WaitOne();
		list_action.Add(_action);
		mutex_actionList.ReleaseMutex();
	}

	public void DoForAction()
	{
		mutex_actionList.WaitOne();
		for (int i = 0; i < list_action.Count; i++)
		{
			list_action[i]();
		}
		list_action.Clear();
		mutex_actionList.ReleaseMutex();
	}

}



namespace Manger
{
	public class ActionManger :MonoBehaviour
	{
        private void Update()
        {
            NetGlobal.Instance.DoForAction();
        }
    }
}