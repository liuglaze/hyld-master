using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// ����ع�����
/// �������ص� ���� �Ļ���/�ͷ�
/// 
/// </summary>
public class ZYKPoolManger : LZJSingleModen<ZYKPoolManger>
{
    #region �ֶ�
    public GameObject[] Resourcessss;
    private Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>();
    private Dictionary<string, ZYKObjectPool> ZYKObjectPools = new Dictionary<string, ZYKObjectPool>();
    #endregion

    #region ���� 
    //��ȡ����صĶ���
    public GameObject InstantiateObject(string name,Transform trans)
    {
        if(!ZYKObjectPools.ContainsKey(name))
        {
            PoolAddToDictionary(name, trans);
        }
        ZYKObjectPool pool= ZYKObjectPools[name];
        return pool.PutOut();
    }
    //���ն���ض���
    public void DesteryObject(GameObject go)
    {
        ZYKObjectPool temppool= null;
        foreach(var p in ZYKObjectPools.Values)
        {
            if(p.ContainInPool(go))
            {
                temppool = p;
                break;
            }
        }
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

    private void PoolAddToDictionary(string poolname,Transform pooltrans)
    {
        
        
        //print("Assets/HuangYeLuanDou/Prefabs/Player.prefab");
        //GameObject go =(GameObject)Instantiate(AssetDatabase.LoadAssetAtPath("Assets/HuangYeLuanDou/Prefabs/Player.prefab", typeof(GameObject)));
        //GameObject go = Resources.Load<GameObject>(path);
        GameObject go = Instantiate(Prefabs[poolname]);
        ZYKObjectPool pool = new ZYKObjectPool(pooltrans, go);
        ZYKObjectPools.Add(poolname,pool);
    }
    #endregion

    #region Unity�ص�
    private void Start()
    {
        foreach(var Res in Resourcessss)
        {
            //print(1);
            Prefabs.Add(Res.name,Res);
        }
        
    }
    #endregion

    #region �¼��ص�
    #endregion

    #region ��������
    #endregion


}
