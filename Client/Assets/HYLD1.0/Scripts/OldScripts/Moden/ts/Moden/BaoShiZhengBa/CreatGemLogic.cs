/*
 * * * * * * * * * * * * * * * * 
 * Author:        魏佳楠
 * CreatTime:  2020/6/19 15:53:41 
 * Description: 生成宝石
 * * * * * * * * * * * * * * * * 
*/
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;
using Manger;
public class Gem
{
	public Vector3 gemPosition;
	public GameObject body;
	public int owner=-1;

	public Gem(Vector3 gemPosition, GameObject body, int owner)
	{
		this.gemPosition = gemPosition;
		this.body = body;
		this.owner = owner;
	}
}
public class CreatGemLogic : MonoBehaviour
{
	private GameObject Gem;
	//private Time startCreatTime;
	//public List<GameObject> Gems;
	public int timeCreate = 100;
	private Transform GemParent;
	public void InitData()
	{
		Gem = HYLDResourceManger.Load(HYLDResourceManger.Type.Gem);
		GemParent = (new GameObject("GemPool")).transform;
		Physics.IgnoreLayerCollision(8, 8);
        if(HYLDStaticValue.ModenName!= "HYLDBaoShiZhengBa")
        {
            Destroy(gameObject);
        }
		//Gems.Clear();
		//startCreatTime = new Time();
		
	}

	private int timeCnt = 0;
	public void OnLogicUpdate()
	{
		//Debug.LogError(timeCnt + "       " + timeCreate);
		

		if (timeCnt % timeCreate == 0)
		{
			NetGlobal.Instance.AddAction(() =>
			{
				GameObject temp = Instantiate(Gem, transform.position + Vector3.up * 0.2F, Quaternion.identity);
				temp.transform.SetParent(GemParent);
				//temp.transform.parent = gameObject.transform;

				//temp.GetComponent<Rigidbody>().AddForce(new Vector3(0, 10f, 0));
				//Gems.Add(temp);

			});
			
		}
		timeCnt += 1;

	}

	
}

