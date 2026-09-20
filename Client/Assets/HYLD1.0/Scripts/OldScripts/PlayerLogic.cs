
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
/// 玩家世界表现体（血条/名字/受击飘字/死亡动画）。
///
/// <para>
/// <b>P3'-3c 改动</b>：删掉了四个**只在客户端生效、且联机下无效或有害**的机制
/// （乌鸦毒 tick 扣血、回血、护盾计时、减速/加速）。它们共用同一个病灶：
/// 由客户端直接改写「权威字段」（<c>Players[].playerBloodValue</c> / <c>移动速度</c>），
/// 而服务端从不实现这些机制、每批权威帧还会把 HP 覆写回去。
/// 结果是这段代码在联机下要么完全无效（写了立刻被覆盖），
/// 要么主动制造预测分歧（改 <c>移动速度</c> 会让本地预测与服务端权威持续对不上）。
/// </para>
///
/// <para>
/// 删掉的直接证据（不只是「推测无效」）：
/// <list type="bullet">
/// <item>触发链不可达：`shell.cs:344/388` 与 `移动型大招.cs:89` 都在碰撞回调里，
/// 而联机的视觉子弹碰撞体被全部禁用（<c>shell.cs:129-139</c>）→ <c>OnTriggerEnter</c> 永不触发。</item>
/// <item>`isPoisoning` 的唯一写入点是 <c>shell.cs:344</c>（同上，不可达）。</item>
/// <item>`isCanCure` 全项目**没有任何地方置 true**（默认 false，只有 `HYLDBulletManger.cs:130` 置 false）
/// → 回血条件 `isCanCure1 &amp;&amp; isCanCure &amp;&amp; ...` 恒不成立。</item>
/// <item>护盾与减速的两段代码**一旦执行就会 NRE**：<c>防护罩</c> 字段在所有 prefab 里都没有绑定（运行时为 null），
/// 且全资产里不存在名为 `减速` 的 GameObject（`Find("减速")` 返回 null）—— 说明确实从来没有人走过它们。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>刻意保留</b>：HP 从权威值下降时的受击飘字/动画（<c>playerBlood &lt; tempBlood</c> 分支）——
/// 那是**纯表现**，读的是权威值，不改任何东西。死亡判定同理，
/// 见 <see cref="playerDieLogic"/> 上的说明。
/// </para>
///
/// <para>
/// <b>刻意不动</b>：<c>TextLogic.cs</c>（试玩模式的机器人）里有另一份毒/减速实现。
/// 它用的是自己硬编码的 <c>playerBlood = 10000</c>，**从不读写** <c>Players[].playerBloodValue</c>，
/// 因此不是「第二权威源」。删它只会破坏单机试玩，对权威收口没有贡献。
/// </para>
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

	void Start ()
	{
		playerBlood=HYLDStaticValue.Players[playerID].playerBloodValue;
		playerBloodMax = playerBlood;
		//
		playerNameText.text = HYLDStaticValue.Players[playerID].playerName;
		playerBloodValueText.text = playerBlood.ToString();
		changeColor(HYLDStaticValue.Players[playerID].playerType);
		tempBlood = playerBlood;

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

		
		//最大血上限来自**每玩家状态**（P3'-2）：初值是共享配置，之后可能被权威值覆盖或被道具改过。
		//历史实现读的是 hero.BloodValue（共享配置），使得「同英雄所有玩家」的上限被一起改。
		if (playerBloodMax != HYLDStaticValue.Players[playerID].playerBloodMax)
		{
			//玩家的血量已经满血了，则让玩家增加到最新的血上限状态
			if (playerBlood == playerBloodMax)
			{
				playerBloodMax = HYLDStaticValue.Players[playerID].playerBloodMax;
				playerBlood = playerBloodMax;
				ImageChangeLogic(playerBloodImage, playerBlood, playerBloodMax);
			}
			//否则只改变最大生命值
			else
			{
				playerBloodMax = HYLDStaticValue.Players[playerID].playerBloodMax;
			}

		}

		//血量，位置
		// P3'-3c：HP 只从权威值读进来显示，不再在本地做任何加减。
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

		//受击表现：HP 相对自己上一帧下降了才飘字/播受击动画。
		//
		//注意这里**只读**权威 HP（playerBlood 上面刚从 Players[].playerBloodValue 取），
		//不做任何扣血 —— 扣血是服务端的事。历史实现在这里连带做了「毒伤/回血」，
		//以及用 `damageTime`/`cureTime` 给回血计时，均已删除（见类注释）。
		if (playerBlood < tempBlood)
		{
			playerHurt(tempBlood - playerBlood);
		}
		tempBlood = playerBlood;

		//【兜底死亡判定 —— 不参与权威】
		//
		//权威死亡结论来自服务端：`BattleData.HitEvent.cs` 会按 `IsDead` 置 `isNotDie=false`
		//并播死亡动画。这里保留的是一条**表现层兜底**：如果服务端的死亡状态这一刻还没到，
		//但本地读到的权威 HP 已经小于 0，就先做「关 UI / 关碰撞体 / 播死亡动画」这些表现动作，
		//避免出现「血条空了人还站着」的观感。
		//
		//它**不决定胜负**（胜负由服务端 `GameOver` 下发，写 `HYLDStaticValue.玩家输了吗`），
		//也不改 HP。判定条件里的 `playerBlood` 是权威值的本地镜像，所以这里不会与权威打架。
		if (playerBlood < 0)
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
	
	Vector3 试玩模式复活点;
	void playerRevive()//复活 开UI 开人物 切换位置
	{
		Vector3 RevivePositon=new Vector3(-999,0,-999);;
		HYLDStaticValue.Players[playerID].isNotDie = true;
		Vector3 beforeRevive = HYLDStaticValue.Players[playerID].playerPositon;
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
		float reviveDelta = Vector3.Distance(beforeRevive, RevivePositon);
		if (playerID == HYLDStaticValue.playerSelfIDInServer && reviveDelta >= Manger.BattleData.LocalPositionJumpTraceThreshold)
		{
			Logging.HYLDDebug.FrameTrace($"[LocalPosJump][Revive] playerID={playerID} delta={reviveDelta:F3} before=({beforeRevive.x:F2},{beforeRevive.y:F2},{beforeRevive.z:F2}) revive=({RevivePositon.x:F2},{RevivePositon.y:F2},{RevivePositon.z:F2})");
		}
		selfBodyTransform.GetComponent<BoxCollider>().enabled = true;
		selfUITransform.gameObject.SetActive(true);
		bodyAnimator.SetBool("Die", false);
		// P3'-3c：原本这里还会给复活者一个防护罩（`Players[playerID].是否有防护罩 = true`）。
		// 已随护盾机制一并删除 —— 服务端不实现护盾，这个标记在联机下没有任何正确含义。
	}
	void playerDieLogic()//死亡：关UI关人物 放动画（联网模式不复活，等服务端 GameOver）
	{
		// P3'-3c：原这里还会清毒状态（PoisoningTime / isPoisoning / 隐藏 HeiYa 图标）。
		// 毒机制已删除；HeiYa 图标在 prefab 里的默认状态就是未激活（m_IsActive: 0），
		// 所以不再需要在这里兜底隐藏它。
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
	// P3'-3c：原 `#region 减速`（减速/Recover + 原始速度）已删除。
	//
	// 它改写 `Players[].移动速度` —— 那是**客户端预测的输入**。服务端的移速来自配置表
	// （`Battle.cs:GetMoveSpeedFor`），所以本地改速只会让预测与权威分叉，随后被 MoveAck
	// 持续回校正（表现为位置被「拉回」）。两个调用点（`shell.cs:388` 贝亚减速、
	// `移动型大招.cs:89` 麦克斯给队友加速）在联机下都不可达。
	//
	// 另外这段代码一旦执行就会 NRE：`Find("Canvas").Find("减速")` 找不到同名子物体
	// （全资产确认不存在），`防护罩` 也没有 prefab 绑定。这也佐证了它从未被真正走过。
}
