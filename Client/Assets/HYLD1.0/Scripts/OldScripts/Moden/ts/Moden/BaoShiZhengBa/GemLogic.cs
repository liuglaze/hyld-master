/*
 * * * * * * * * * * * * * * * * 
 * Author:        魏佳楠
 * CreatTime:  2020/6/20 16:45:00 
 * Description: 
 * * * * * * * * * * * * * * * * 
*/

using System;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;

public class GemLogic : MonoBehaviour
{
	public GameObject audioSourec; 
	private void OnCollisionEnter(Collision other)
	{
		//print("！@"+other.gameObject.layer);
		if (other.gameObject.tag == "Player")
		{
			int PlayerId=other.transform.parent.GetComponent<PlayerLogic>().playerID;

			HYLDStaticValue.Players[PlayerId].gemTotal += 1;
            //print("?"+HYLDStaticValue.SelfTeam);
            //print("!"+HYLDStaticValue.Players[PlayerId].playerTeam);

            //HYLDStaticValue.RoomRedTeamGemTotalValue += 1;


            HYLDStaticValue.ConfirmWinOrNot = true;
            Destroy(gameObject);
			if(PlayerId==0)
				Destroy(Instantiate(audioSourec, transform.position, Quaternion.identity),5f);
		}
	}

	void Start ()
	{
		if(HYLDStaticValue.ModenName!= "HYLDBaoShiZhengBa")
        {
            Destroy(gameObject);
        }
	}
	
	void FixedUpdate()
	{
		transform.Rotate(Vector3.up);
	}


}

