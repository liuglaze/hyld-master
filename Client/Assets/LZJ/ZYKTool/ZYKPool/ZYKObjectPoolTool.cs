using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ZYKTool.Pool;

/// <summary>
/// 对象池管理者
/// 管理对象池的 物体 的回收/释放
/// 
/// </summary>
namespace ZYKTool
{


    public class ZYKObjectPoolTool : MonoBehaviour
    {
        public static ZYKObjectPoolTool Single;
        #region 字段
        public GameObject[] Resources;
        private Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>();
        private Dictionary<string, ZYKObjectPool> ZYKObjectPools = new Dictionary<string, ZYKObjectPool>();
        #endregion

        #region 方法 
        public void AddPrefab(string name,GameObject go)
        {
            Prefabs.Add(name,go);
        }

        //拿取对象池的对象
        public GameObject InstantiateObject(string name, Transform trans)
        {
            Logging.HYLDDebug.LogError(ZYKObjectPools.ContainsKey(name));
            
            if (!ZYKObjectPools.ContainsKey(name))
            {
                PoolAddToDictionary(name, trans);
            }
            ZYKObjectPool pool = ZYKObjectPools[name];
            return pool.PutOut();
        }
        //回收对象池对象
        public void DesteryObject(GameObject go)
        {
            
            ZYKObjectPool temppool = null;
            foreach (var p in ZYKObjectPools.Values)
            {
                if (p.ContainInPool(go))
                {
                    temppool = p;
                    break;
                }
            }
            Logging.HYLDDebug.LogError(go);
            temppool.PutBack(go);
        }
        public void DesteryObjectAll()
        {
            foreach (var p in ZYKObjectPools.Values)
            {
                p.PutBackALL();
            }
        }
        public void Clear()
        {
            DesteryObjectAll();
            ZYKObjectPools.Clear();
        }

        private void PoolAddToDictionary(string poolname, Transform pooltrans)
        {


            //print("Assets/HuangYeLuanDou/Prefabs/Player.prefab");
            //GameObject go =(GameObject)Instantiate(AssetDatabase.LoadAssetAtPath("Assets/HuangYeLuanDou/Prefabs/Player.prefab", typeof(GameObject)));
            //GameObject go = Resources.Load<GameObject>(path);
            GameObject go = Instantiate(Prefabs[poolname]);
            ZYKObjectPool pool = new ZYKObjectPool(pooltrans, go);
            ZYKObjectPools.Add(poolname, pool);
        }
        #endregion

        #region Unity回调
        private void Awake()
        {
            Single = this;
        }
        private void Start()
        {
            return;
            foreach (var Res in Resources)
            {
                //print(1);
                Logging.HYLDDebug.LogError(Res.name);
                Prefabs.Add(Res.name, Res);
                Single.InstantiateObject(Res.name,transform);
            }

        }
        #endregion

        #region 事件回调
        #endregion

        #region 帮助方法
        #endregion


    }
}