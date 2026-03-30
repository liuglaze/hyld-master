/****************************************************
    Author:            龙之介
    CreatTime:    2021/4/16 11:59:50
    Description:     Nothing
*****************************************************/


using System.Collections.Generic;
using System;
using System.Linq;
using XLua;
using System.Reflection;


public static class HotFixCfg
{
    [Hotfix]
    public static List<Type> by_property
    {
        get
        {
            return (from type in Assembly.Load("Assembly-CSharp").GetTypes()
                    where (type.Namespace == "Manger" || type.Namespace == "HOTFIX")
                    select type).ToList();
        }
    }


}
