/****************************************************
    ScriptName:        SingetonBase.cs
    Author:            龙之介
    Emall:        505258140@qq.com
    CreatTime:    2020/12/6 12:0:56
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using ZYKTool;
using DG.Tweening;
using System.Linq;


namespace ZYKTool
{
	public class ZYKSingleModen<T>:MonoBehaviour where T : new()
	{
        public static T Single { get; protected set; } = new T();
	}
}