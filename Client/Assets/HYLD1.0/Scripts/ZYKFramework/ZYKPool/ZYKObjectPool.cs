using System.Collections;
using System.Collections.Generic;
using UnityEngine;
/// <summary>
/// �����
/// ְ�ܣ���Ԥ����ȫ������
/// </summary>
public class ZYKObjectPool
{
    #region �ֶ�
    private Transform m_parent;
    private GameObject m_prefab;
    private Queue<GameObject> Pool = new Queue<GameObject>();
    #endregion


    #region ���� 
    public string Name
    {
        get
        {
            return m_prefab.name;
        }
    }
    public ZYKObjectPool(Transform parentsTrans, GameObject prefab)
    {
        m_parent = parentsTrans;
        m_prefab = prefab;
    }
    #endregion


    #region ����
    public GameObject PutOut()
    {
        GameObject go = null;
        if(Pool.Count==0)
        {
            go = GameObject.Instantiate<GameObject>(m_prefab);
            go.transform.parent = m_parent;
            Pool.Enqueue(go);
        }
        go = Pool.Dequeue();
        go.SendMessage("OnInstantiateObject", SendMessageOptions.DontRequireReceiver);
        go.SetActive(true);
        
        return go;
    }
    public void PutBack(GameObject go)
    {
        if (ContainInPool(go))
        {
            Pool.Enqueue(go);
            go.SendMessage("OnDesteryObject", SendMessageOptions.DontRequireReceiver);
            go.SetActive(false);
        }
    }
    public void PutBackALL()
    {
        foreach(var p in Pool)
        {
            PutBack(p);
        }
    }
    public bool ContainInPool(GameObject go)
    {
        return Pool.Contains(go);
    }
    #endregion
}
