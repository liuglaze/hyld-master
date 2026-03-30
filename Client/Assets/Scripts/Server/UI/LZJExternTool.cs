/****************************************************
    Author:            龙之介
    CreatTime:    2021/6/14 23:53:54
    Description:     龙之介工具类
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;




public static class LZJExternTool
{
    /// <summary>
    /// 判断a类是不是b类的子类
    /// </summary>
    /// <param name="type"></param>
    /// <param name="baseType"></param>
    /// <returns></returns>
    public static bool IsSubClassOf(Type type, Type baseType)
    {
        if (type == null) return false;
        var b = type.BaseType;
        while (b != null)
        {
            if (b.Equals(baseType))
            {
                return true;
            }
            b = b.BaseType;
        }
        return false;
    }

}
