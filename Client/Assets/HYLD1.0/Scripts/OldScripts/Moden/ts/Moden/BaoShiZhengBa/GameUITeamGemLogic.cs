/*
 * * * * * * * * * * * * * * * * 
 * Author:        魏佳楠
 * CreatTime:  2020/6/20 17:13:29 
 * Description: 
 * * * * * * * * * * * * * * * * 
*/
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;

public class GameUITeamGemLogic : MonoBehaviour
{
	public int redTeamValue = 0;
	public int blueTeamValue = 0;

	public Text redTeamText;
	public Text blueTeamText;

	public GameObject redValue;
	public GameObject blueValue;

	public List<GameObject> redValues;
	public List<GameObject> blueValues;


	void Start ()
	{
		
	}

	List<GameObject> clearListGameObjects(List<GameObject> lists)
	{
		foreach (var pos in lists)
		{
			Destroy(pos);
		}

		lists.Clear();
		return lists;
	}

	List<GameObject> imageCreateLogic(GameObject copy, int cnt, List<GameObject> lists,bool isPositive)
	{
		
		for(int i=0;i<Mathf.Min(10,cnt);i++)
		{
			GameObject temp = Instantiate(copy, copy.transform.parent);
			temp.transform.position+=new Vector3((isPositive?i:-i)*35,0,0);
			lists.Add(temp);
		}
		return lists;
	}
	
	void Update () 
	{
		if (redTeamValue != HYLDStaticValue.RoomEnemyTeamGemTotalValue)
		{
			redTeamValue = HYLDStaticValue.RoomEnemyTeamGemTotalValue;
			redValues=clearListGameObjects(redValues);
			redTeamText.text = redTeamValue.ToString();
			redValues=imageCreateLogic(redValue,redTeamValue,redValues,false);
		}
		if (blueTeamValue != HYLDStaticValue.RoomSelfTeamGemTotalValue)
		{
			blueTeamValue = HYLDStaticValue.RoomSelfTeamGemTotalValue;
			blueValues=clearListGameObjects(blueValues);
			blueTeamText.text = blueTeamValue.ToString();
			blueValues=imageCreateLogic(blueValue,blueTeamValue,blueValues,true);
		}
	}


}

