using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(ZYKPoolManger))]
public class ZYKGame : LZJSingleModen<ZYKGame>
{
    ZYKPoolManger poolManger;
    /// <summary>
    ///     调用这个方法就能实现生成Prefabs的物体
    ///     poolManger.InstantiateObject();
    //      poolManger.DesteryObject();
    /// </summary>

    void Start()
    {
        poolManger = ZYKPoolManger.Instance;
    }

    // Update is called once per frame
    void Update()
    {
        //poolManger.InstantiateObject("Bullet", gameObject.transform);
    }
}
