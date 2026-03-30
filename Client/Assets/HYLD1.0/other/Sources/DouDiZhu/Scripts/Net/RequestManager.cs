using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;



public class RequestManager : MonoBehaviour
{
    // Start is called before the first frame update
    public static  void request(OldRequestCode requestCode,OldActionCode actionCode,string s)
    {
        
        
        if (requestCode != OldRequestCode.HYLDGame)
        {
            User u=new User();
            Type t = u.GetType();
            D.p("Rev="+requestCode.ToString()+actionCode.ToString()+s);
            MethodInfo mt = t.GetMethod(actionCode.ToString());//加载方法
            object[] objParams = new object[1] { s };
            mt.Invoke(u, objParams);
        }
        else//荒野乱斗游戏
        {
            HYLDActionMethon u=new HYLDActionMethon();
            Type t = u.GetType();
            D.p("Rev="+requestCode.ToString()+actionCode.ToString()+s);
            MethodInfo mt = t.GetMethod(actionCode.ToString());//加载方法
            object[] objParams = new object[1] { s };
            mt.Invoke(u, objParams);
        }
        
        
        
    }
    
}
