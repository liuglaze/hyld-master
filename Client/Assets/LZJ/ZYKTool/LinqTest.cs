/****************************************************
    ScriptName:        LinqTest.cs
    Author:            龙之介
    Emall:        505258140@qq.com
    CreatTime:    2020/12/18 21:8:11
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



namespace ZYKTest
{
	public class LinqTest :MonoBehaviour
	{
        //在List<T>中，存在三种方法:Contains, Exists, Any。都可以实现查找元素。下面来做个测试，看下他们之间的性能比较如何。
        private void TestListLinqAny()
        {
            List<int> list=new List<int>();
            for (int i = 0; i < 10; i++)
                list.Add(i);

            list.Any(u => u == 1);
            list.Exists(u => u == 1);
            list.Contains(1);
        }
	}
}