using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ZYKTool.Pool
{
    public interface ZYKPoolInterface
    {

        void OnInstantiateObject();

        void OnDesteryObject();

    }
    public interface IReusable
    {

        //取出时候调用
        void OnSpawn();

        //回收调用
        void OnUnSpawn();

    }
}