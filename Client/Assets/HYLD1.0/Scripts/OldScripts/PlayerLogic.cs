
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

/// <summary>
/// 
/// </summary>
public class PlayerLogic : MonoBehaviour
{
	public int playerID = -1;
	[FormerlySerializedAs("selfTransform")] public Transform selfUITransform;
	[FormerlySerializedAs("target")] public Transform selfBodyTransform;
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

	public Animator bodyAnimator;

	private float timerisPoisoning = 0;
	private int PoisoningTime = 0;
	private float cureTime = 0;
	private float damageTime = 0;
	public GameObject 防护罩;
	private float 防护罩时间戳 = 0;
	void Start ()
	{
		playerBlood=HYLDStaticValue.Players[playerID].playerBloodValue;
		playerBloodMax = playerBlood;
		//
		playerNameText.text = HYLDStaticValue.Players[playerID].playerName;
		playerBloodValueText.text = playerBlood.ToString();
		changeColor(HYLDStaticValue.Players[playerID].playerType);
		tempBlood = playerBlood;

		原始速度 = HYLDStaticValue.Players[playerID].移动速度;
		if(HYLDStaticValue.ModenName== "HYLDTryGame")
		试玩模式复活点 = transform.position;
	}
	//脚底颜色
	void changeColor(PlayerType playerType)
	{
		if (playerType == PlayerType.Enemy)
		{
			selfUITransform.GetComponentsInChildren<Image>()[0].color=new Color(1,0,0,0.7f);
			selfUITransform.GetComponentsInChildren<Image>()[1].color=new Color(1,0,0,0.7f);
			playerNameText.color=new Color(1,0.3f,0,0.7f);
		}
		else if (playerType== PlayerType.Teammate)
		{
			selfUITransform.GetComponentsInChildren<Image>()[0].color=new Color(0,0.8f,1,0.7f);
			selfUITransform.GetComponentsInChildren<Image>()[1].color=new Color(0,0.8f,1,0.7f);
			playerNameText.color=new Color(0,0.8f,1,0.7f);
		}
		else if (playerType== PlayerType.Self)
		{
			selfUITransform.GetComponentsInChildren<Image>()[0].color=new Color(0,1f,0,0.7f);
			selfUITransform.GetComponentsInChildren<Image>()[1].color=new Color(0,1f,0,0.7f);
			playerNameText.color=new Color(0,0.8f,0.2f,0.7f);
		}
	}

	private void FixedUpdate()
	{
		
	}

	public void OnUpdateLogic()
	{
		// 联网战斗路径下不再在这里驱动 mana 自动回复。
		// 保留空入口，仅兼容 BattleManger -> playerManger.UpdateAllPlayerLogics() 的旧调用链。
	}

	void Update()
	{
		if (HYLDStaticValue.isloading) return;

		
		//因为吃道具等因素使得玩家的最大血上限不等于英雄本来的血上限
		if (playerBloodMax != HYLDStaticValue.Players[playerID].hero.BloodValue)
		{
			//玩家的血量已经满血了，则让玩家增加到最新的血上限状态
			if (playerBlood == playerBloodMax)
			{
				playerBloodMax = HYLDStaticValue.Players[playerID].hero.BloodValue;
				playerBlood = playerBloodMax;
				ImageChangeLogic(playerBloodImage, playerBlood, playerBloodMax);
			}
			//否则只改变最大生命值
			else
			{
				playerBloodMax = HYLDStaticValue.Players[playerID].hero.BloodValue;
			}

		}


		#region 乌鸦毒标记
		timerisPoisoning += Time.deltaTime;
		if (HYLDStaticValue.Players[playerID].isPoisoning && timerisPoisoning >= 1)
		{
			if (PoisoningTime >= 5)
			{
				PoisoningTime = 0;
				HYLDStaticValue.Players[playerID].isPoisoning = false;
				HYLDStaticValue.Players[playerID].body.transform.Find("Canvas").Find("HeiYa").gameObject.SetActive(false);
				return;
			}
			HYLDStaticValue.Players[playerID].body.transform.Find("Canvas").Find("HeiYa").gameObject.SetActive(true);
			timerisPoisoning = 0;
			PoisoningTime++;
			playerBlood -= 85;
			HYLDStaticValue.Players[playerID].playerBloodValue -= 85;
			playerBloodValueText.text = HYLDStaticValue.Players[playerID].playerBloodValue.ToString();
			selfUITransform.position = selfBodyTransform.position;
			ImageChangeLogic(playerBloodImage, playerBlood, playerBloodMax);
			damageTime = 0;
			HYLDStaticValue.Players[playerID].isCanCure1 = false;
			playerHurt(85);
			if (playerBlood < 0)//玩家死亡
			{
				HYLDStaticValue.ConfirmWinOrNot = true;
				playerDieLogic();
			}
			return;
		}
		#endregion

		//血量，位置
		playerBlood = HYLDStaticValue.Players[playerID].playerBloodValue;
		playerBloodValueText.text = HYLDStaticValue.Players[playerID].playerBloodValue.ToString();
		selfUITransform.position = selfBodyTransform.position;

		//宝石
		playerGemTotal = HYLDStaticValue.Players[playerID].gemTotal;
		if (playerGemTotal == 0)
		{
			playerGem.SetActive(false);
		}
		else
		{
			playerGem.SetActive(true);
			playerGem.GetComponentInChildren<Text>().text = playerGemTotal.ToString();
		}
		ImageChangeLogic(playerBloodImage, playerBlood, playerBloodMax);
		ImageChangeLogic(playerManaImage, HYLDStaticValue.Players[playerID].playerManaValue, 90);

		//护盾
		if (HYLDStaticValue.Players[playerID].是否有防护罩)
		{
			防护罩.SetActive(true);
			防护罩时间戳 += Time.deltaTime;
			if (防护罩时间戳 > 3)
			{
				防护罩时间戳 = 0;
				防护罩.SetActive(false);
				HYLDStaticValue.Players[playerID].是否有防护罩 = false;
			}
			return;
		}
		//回血和扣血判断
		cureTime += Time.deltaTime;
		damageTime += Time.deltaTime;
		if (damageTime > 3f && !HYLDStaticValue.Players[playerID].isPoisoning) HYLDStaticValue.Players[playerID].isCanCure1 = true;//加血条件
		if (playerBlood < tempBlood)//扣血了，则不能回复生命
		{
			damageTime = 0;
			HYLDStaticValue.Players[playerID].isCanCure1 = false;
			playerHurt(tempBlood - playerBlood);
		}
		//如果可以回复生命并且血量到达回复生命的时间。
		//Logging.HYLDDebug.LogError($"{HYLDStaticValue.Players[playerID].isCanCure} + {HYLDStaticValue.Players[playerID].isCanCure1} + {cureTime > 1f}");
		if (HYLDStaticValue.Players[playerID].isCanCure1 && HYLDStaticValue.Players[playerID].isCanCure&&cureTime>1f)//加血了
		{
			cureTime = 0;
			playerCure();
		}
		tempBlood = playerBlood;
		
		if (playerBlood < 0)//玩家死亡
		{
            HYLDStaticValue.ConfirmWinOrNot = true;
			playerDieLogic();
		}
		
	}


	private void ImageChangeLogic(GameObject changeGameObject,int valueNow,int valueMax)
	{
		Vector3 temp=new Vector3(1,1,1);
		if (valueMax != 0)
		{
			temp.x = 1.0f*valueNow / valueMax;
		}
		//对物体产生形变
		changeGameObject.transform.localScale=temp;
	}
	
	void playerCure()
	{
		HYLDStaticValue.Players[playerID].playerBloodValue+=(int)(HYLDStaticValue.Players[playerID].hero.BloodValue*0.18);
		if(HYLDStaticValue.Players[playerID].playerBloodValue>= HYLDStaticValue.Players[playerID].hero.BloodValue)
		{
			HYLDStaticValue.Players[playerID].playerBloodValue = HYLDStaticValue.Players[playerID].hero.BloodValue;
		}
	}

	Vector3 试玩模式复活点;
	void playerRevive()//复活 开UI 开人物 切换位置  给防护盾
	{
		Vector3 RevivePositon=new Vector3(-999,0,-999);;
		HYLDStaticValue.Players[playerID].isNotDie = true;
		HYLDStaticValue.Players[playerID].playerPositon = RevivePositon;
		selfBodyTransform.position = RevivePositon;
		if (HYLDStaticValue.ModenName == "HYLDTryGame")
		{
			RevivePositon = 试玩模式复活点;
		}

		
		 
		//TODO: REMAKE 复活点逻辑重构！！！
		else if (HYLDStaticValue.Players[playerID].teamID== HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].teamID)
		{
			//012345
			//RevivePositon=new Vector3(15,1,playerID-3);
			RevivePositon = new Vector3(15, 1, 0);
		}
		else// if (HYLDStaticValue.Players[playerID].playerTeam==PlayerTeam.team2)
		{
			//RevivePositon=new Vector3(-15,1,playerID-2);
			RevivePositon = new Vector3(-15, 1, 0);
		}
		
		HYLDStaticValue.Players[playerID].playerPositon=RevivePositon;
		selfBodyTransform.position=RevivePositon;
		selfBodyTransform.GetComponent<BoxCollider>().enabled = true;
		selfUITransform.gameObject.SetActive(true);
		bodyAnimator.SetBool("Die", false);
		HYLDStaticValue.Players[playerID].是否有防护罩 = true;
	}
	void playerDieLogic()//死亡：关UI关人物 放动画（联网模式不复活，等服务端 GameOver）
	{
		#region  乌鸦
		PoisoningTime = 0;
		HYLDStaticValue.Players[playerID].isPoisoning = false;
		HYLDStaticValue.Players[playerID].body.transform.Find("Canvas").Find("HeiYa").gameObject.SetActive(false);
        #endregion
        selfBodyTransform.GetComponent<BoxCollider>().enabled = false;

		HYLDStaticValue.Players[playerID].isNotDie = false;

		StartCoroutine(掉宝石());

		selfUITransform.gameObject.SetActive(false);

		// D8: 不再自动复活，死亡即终局，等服务端下发 GameOver
		// 原: Invoke("playerRevive",3f);

		bodyAnimator.SetBool("Die",true);
        bodyAnimator.SetTrigger("DieTrigger");

		// D8: 不再加满血（服务端权威 HP）
		// 原: HYLDStaticValue.Players[playerID].playerBloodValue=playerBloodMax;
	}
	void playerHurt(int hurtValue)
	{
		
		bloodHurtValueText.GetComponent<Text>().text = hurtValue.ToString();

		bodyAnimator.SetTrigger("Hit");

		Destroy(Instantiate(bloodHurtValueText, selfUITransform), 1f);
		
		bloodHurtValueText.GetComponent<Text>().color=new Color(1,1,1);
	}
	IEnumerator 掉宝石()
	{

		for (int i = 0; i < HYLDStaticValue.Players[playerID].gemTotal; i++)
		{
			GameObject temp = Instantiate(Gem, selfBodyTransform.position, Quaternion.identity);
			float 力 = UnityEngine.Random.Range(2f, 2f);
			temp.GetComponent<Rigidbody>().AddForce(力, 0.01f, 0);
			yield return new WaitForSeconds(0.03f);

		}
		HYLDStaticValue.Players[playerID].gemTotal = 0;
	}
	#region 减速
	private float 原始速度;
	public void 减速(float value)
	{
		//Logging.HYLDDebug.LogError(playerID);
		//Logging.HYLDDebug.LogError(HYLDStaticValue.Players[playerID].hero.移动速度);
		HYLDStaticValue.Players[playerID].移动速度 = 原始速度-value;
		//Logging.HYLDDebug.LogError(HYLDStaticValue.Players[playerID].hero.移动速度);
		HYLDStaticValue.Players[playerID].body.transform.Find("Canvas").Find("减速").gameObject.SetActive(true);
		CancelInvoke();
		Invoke("Recover", 3);
	}

	public void Recover()
	{
		HYLDStaticValue.Players[playerID].移动速度 =原始速度;
		HYLDStaticValue.Players[playerID].body.transform.Find("Canvas").Find("减速").gameObject.SetActive(false);
	}
	#endregion
}

