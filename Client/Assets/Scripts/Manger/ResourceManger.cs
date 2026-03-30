/****************************************************
    Author:            龙之介
    CreatTime:    2022/4/22 21:4:46
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;



namespace Manger
{
	public class HYLDResourceManger
	{
        public enum Type
        {
            Player,
            Bullet,
            Gem
        }
        private static Dictionary<Type, string> dic_typeMapToPath;
        private static void Init()
        {
            dic_typeMapToPath = new Dictionary<Type, string>();
            string reamke = "Remake/";
            int k = 0;
            foreach (var type in Enum.GetNames(typeof(Type)))
            {
                dic_typeMapToPath.Add((Type)k++, reamke + type);
            }
        }
        public static GameObject Load(Type type)
        {
            if (dic_typeMapToPath == null) Init();
            GameObject res = Resources.Load<GameObject>(dic_typeMapToPath[type]);
            return res;
        }
	}
}