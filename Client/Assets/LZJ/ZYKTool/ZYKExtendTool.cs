/****************************************************
    ScriptName:        ExtendUtil.cs
    Author:            龙之介
    Emall:        505258140@qq.com
    CreatTime:    2020/12/1 15:4:11
    Description:    扩展工具类
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using ZYKTool;


namespace ZYKTool
{
    public static class ZYKExtendTool
    {

        public static void AddBtnListener(this RectTransform rect, Action action)
        {
            var button = rect.GetComponent<Button>();
            if (button == null)
            {
                rect.gameObject.AddComponent<Button>();
            }
            button.onClick.AddListener(() => action());
        }

        public static RectTransform RectTransform(this Transform transform)
        {
            var rect = transform.GetComponent<RectTransform>();
            if (rect != null)
            {
                return rect;
            }
            else
            {
                Logging.HYLDDebug.LogError("can not find RectTransform");
                return null;
            }
        }

        public static Image Image(this Transform transform)
        {
            var image = transform.GetComponent<Image>();
            if (image != null)
            {
                return image;
            }
            else
            {
                Logging.HYLDDebug.LogError("can not find Image");
                return null;
            }
        }
        public static Button Button(this Transform transform)
        {
            var button = transform.GetComponent<Button>();
            if (button != null)
            {
                return button;
            }
            else
            {
                Logging.HYLDDebug.LogError("can not find Image");
                return null;
            }
        }


        public static T GetOrAddComponent<T>(this Transform transform) where T : Component
        {
            var component = transform.GetComponent<T>();
            if (component == null)
            {
                return transform.gameObject.AddComponent<T>();
            }
            return component;
        }
        public static T GetOrAddComponent<T>(this GameObject gameObject) where T : Component
        {
            Transform transform = gameObject.transform;
            var component = transform.GetComponent<T>();
            if (component == null)
            {
                return transform.gameObject.AddComponent<T>();
            }
            return component;
        }

        public static Transform GetByName(this Transform transform, string name)
        {
            var temp = transform.Find(name);
            if (temp == null)
            {
                Logging.HYLDDebug.LogError("can not find name+" + name + "under" + transform);
                return temp;

            }
            else
            {
                return temp;
            }
        }


        /*
public static Transform GetButtonParent(this Transform transform)
{
    var parent = transform.Find(ConstValue.BUTTON_PATH);
    if(parent==null)
    {
        Logging.HYLDDebug.LogError("not find ButtonPath");

    }
    return parent;
}


public static void AddButtonListener(this Transform transform,string buttonName,Action callBack)
{
    var buttontran= transform.Find(ConstValue.BUTTON_PATH+"/"+buttonName);
    if(buttontran!=null)
    {
        buttontran.RectTransform().AddBtnListener(callBack);
    }
    else
    {
        Logging.HYLDDebug.LogError("not find button" + transform.parent + "/" + transform + "/" + buttonName);
    }
}
*/


    }
}