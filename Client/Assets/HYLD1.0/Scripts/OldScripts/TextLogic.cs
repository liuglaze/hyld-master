
/*
 * * * * * * * * * * * * * * * * 
 * Author:        赵元恺
 * CreatTime:  2020/7/24
 * Description:	试玩模式的机器人
 * * * * * * * * * * * * * * * * 
*/

using System;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine.UI;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Serialization;
using Image = UnityEngine.UI.Image;

public class TextLogic : MonoBehaviour
{

 public Transform selfUITransform;
	 public Transform selfBodyTransform;
	public string playerName;
	public int playerBlood;
	private int playerBloodMax;
	private int tempBlood;
	private int playerGemTotal;
	public GameObject bloodHurtValueText;

	public GameObject Gem;

	public Text playerNameText;
	public Text playerBloodValueText;
	public GameObject playerGem;

	public GameObject playerBloodImage;
	public GameObject playerManaImage;

	public Transform BornPos;
	public bool ispanni = false;
	public  bool isPoisoning = false;
	private float timerisPoisoning = 0;
	private int PoisoningTime = 0;
	public GameObject HeiyaBiaoJi;
	public GameObject 减速标记;

	public bool 被控制 = false;
	private float 控制时间戳 = 0;
	public float 控制时间;
	public Vector3 子弹位置;
	public HeroName 当前英雄;
	public GameObject 格尔子弹;
	void Start()
	{

		playerBloodMax = 10000;
		playerBlood = 10000;
		//
		playerNameText.text = "忍者神龟";
		playerBloodValueText.text = playerBlood.ToString();
		tempBlood = playerBlood;
		ImageChangeLogic(playerBloodImage, playerBlood, playerBloodMax);
	}



	private int cnt = 0;
	private void OnTriggerEnter(Collider other)
	{
		//Logging.HYLDDebug.LogError(1);
		
		if(other.tag == "Obstacles"|| other.tag == "Wall")
		{
			if (被控制)
			{
				控制时间戳 = 1;
				控制时间 = 3;
			}
		}
				
	}

	void Update()
	{
		if(被控制)
		{
			if(当前英雄==HeroName.XueLi)
			{
				Vector3 temp = 子弹位置;
				temp -= new Vector3(0, 1, 0);
				transform.Translate((transform.position - temp).normalized * Time.deltaTime * 1, Space.World);
			}
			if(当前英雄==HeroName.GeEr)
			{
				if(格尔子弹==null)
				{
					控制时间戳 = 0;
					被控制 = false;
					return;
				}
				if(控制时间戳>0.2)
				{
					transform.position = transform.position;
				}
				else
				{
					Vector3 temp = 格尔子弹.transform.position;
					temp -= new Vector3(0, 1, 0);
					transform.position = temp;
					transform.Translate((transform.position - temp).normalized * Time.deltaTime * 1, Space.World);
				}

			}
			控制时间戳 += Time.deltaTime;
			if (控制时间戳 >= 控制时间)
			{
				控制时间戳 = 0;
				被控制 = false;
			}
		}





		timerisPoisoning += Time.deltaTime;
		if(isPoisoning) HeiyaBiaoJi.SetActive(true);
		if (isPoisoning && timerisPoisoning >= 1)
		{
			
			timerisPoisoning = 0;
			PoisoningTime++;
			playerBlood -= 85;
			playerHurt(85);
			if (PoisoningTime >= 5)
			{
				PoisoningTime = 0;
				isPoisoning = false;
				HeiyaBiaoJi.SetActive(false);
			}
		}

		playerBloodValueText.text = playerBlood.ToString();



		ImageChangeLogic(playerBloodImage, playerBlood, playerBloodMax);
		ImageChangeLogic(playerManaImage, 1, 90);
	
		tempBlood = playerBlood;

		if (playerBlood < 0)//玩家死亡
		{
			playerBlood = playerBloodMax;
			playerBloodValueText.text = playerBlood.ToString();
		}

	}


	void ImageChangeLogic(GameObject changeGameObject, int valueNow, int valueMax)
	{
		Vector3 temp = new Vector3(1, 1, 1);
		if (valueMax != 0)
		{
			temp.x = 1.0f * valueNow / valueMax;
		}
		//对物体产生形变
		changeGameObject.transform.localScale = temp;
	}




	public void playerHurt(int hurtValue)
	{
		bloodHurtValueText.GetComponent<Text>().text = hurtValue.ToString();
		bloodHurtValueText.GetComponent<Text>().color = Color.red;
		//Logging.HYLDDebug.LogError(bloodHurtValueText);
		Destroy(Instantiate(bloodHurtValueText, selfUITransform), 1f);
	}
	public void 减速()
	{
		减速标记.SetActive(true);
		CancelInvoke();
		Invoke("Recover", 3);
	}

	public void Recover()
	{
		减速标记.SetActive(false);
	}
}


