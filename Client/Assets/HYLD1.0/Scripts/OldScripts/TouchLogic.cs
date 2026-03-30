/*
 * * * * * * * * * * * * * * * * 
 * Author:        赵元恺
 * CreatTime:  2020/6/18 20:16:06 
 * Description: UI遥感交互逻辑
 * * * * * * * * * * * * * * * * 
*/

using System;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;
using LZJ;
public class TouchLogic : MonoBehaviour 
{
	public Slider 能量条;
	public GameObject 大招遥感;


	private void FixedUpdate()
{

		if (HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].当前能量 >= HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].最大能量)
		{
			HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].可以按大招 = true;
		}
		能量条.gameObject.SetActive(!HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].可以按大招);
		大招遥感.SetActive(HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].可以按大招);
		if (能量条.gameObject.activeSelf)
		{
			能量条.value = HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].当前能量 / HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].最大能量;
		}

	}
	void OnEnable()
	{
		EasyJoystick.On_JoystickMove += OnJoystickMove;
		EasyJoystick.On_JoystickMoveEnd +=JoystickMoveEnd;
	}


	
	void JoystickMoveEnd(MovingJoystick move)
	{
		if (Toolbox.是否游戏结束) return;
		if (move.joystickName == "PlayerMove")
		{
			isMoveInputActive = false;
			HYLDStaticValue.PlayerMoveX  = Fixed.Zero;
			HYLDStaticValue.PlayerMoveY  = Fixed.Zero;
			CommandManger.Instance.AddCommad_Move(HYLDStaticValue.PlayerMoveX, HYLDStaticValue.PlayerMoveY);
		}
		if (move.joystickName == "FireNormal"||move.joystickName=="FireSuper")
		{

			HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].body.GetComponentInChildren<LineRenderer>().enabled = false;

			// ★ 去掉 fireState == none 的前置检查
			// 现在攻击统一走 CommandManger → EnqueueAttack 队列，
			// 不再用 fireState 做输入门控，fireState 只用于逻辑层驱动发射
			// 摇杆位移太小时忽略（两个轴都接近零 = 没有有效方向）
			Logging.HYLDDebug.FrameTrace($"[AttackInput] joystick={move.joystickName} FirePosX={FirePositionX.ToFloat():F4} FirePosY={FirePositionY.ToFloat():F4}");
			if (MathFixed.Abs(FirePositionX) <= 0.02f && MathFixed.Abs(FirePositionY) <= 0.02f)
			{
				Logging.HYLDDebug.FrameTrace("[AttackInput] REJECTED by dead zone");
				return;
			}
			Logging.HYLDDebug.FrameTrace("[AttackInput] ACCEPTED -> AddCommad_Attack");
			CommandManger.Instance.AddCommad_Attack(FirePositionX.ToFloat(), FirePositionY.ToFloat());
		}
	}

	private Fixed FirePositionY=Fixed.Zero;
	private Fixed FirePositionX=Fixed.Zero;
	private LineRenderer selfFireLineRenderer;

	private const float MoveStartDeadZone = 0.18f;
	private const float MoveStopDeadZone = 0.12f;
	private bool isMoveInputActive = false;

	private float shootDistance;
	private float launchAngle;
	private void Start()
	{
		selfFireLineRenderer = HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].body.GetComponentInChildren<LineRenderer>();
		
		selfFireLineRenderer.enabled = false;

		for(int i=0;i<HYLDStaticValue.Players.Count;i++)
		{
			//Logging.HYLDDebug.LogError(HYLDStaticValue.Players.Count);
			HYLDStaticValue.Players[i].isNotDie = true;
		}

		
	}

	private void OnDestroy()
	{
		for(int i=0;i<HYLDStaticValue.Players.Count;i++)
		{
			HYLDStaticValue.Players[i].isNotDie = false;
		}
	}


	void OnJoystickMove(MovingJoystick move)
	{
		//Logging.HYLDDebug.LogError(Toolbox.是否游戏结束);
		if (Toolbox.是否游戏结束) return;
		if (move.joystickName == "FireNormal"|| move.joystickName == "FireSuper")
		{
			FirePositionY = new Fixed( move.joystickAxis.y);
			
			FirePositionX = new Fixed(move.joystickAxis.x);
			Fixed R = FirePositionX * FirePositionX + FirePositionY * FirePositionY;
			if(selfFireLineRenderer==null)
			{
				HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].body.transform.Find("Capsule").Find("Gun").gameObject.AddComponent<LineRenderer>();
				selfFireLineRenderer= HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].body.GetComponentInChildren<LineRenderer>();
			}
			selfFireLineRenderer.enabled = true;
			Vector3 temp =
				LZJ.MathFixed.Vector32UnitVector3((HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].playerPositon),
					(HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].playerPositon+new Vector3(FirePositionX.ToFloat(),1,FirePositionY.ToFloat())));
			temp.y = temp.z;
			temp.z = temp.x;
			temp.x = -temp.y;
			temp.y = 0;
			shootDistance = HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].hero.shootDistance;
			
			//Logging.HYLDDebug.Log(shootDistance);
			
			launchAngle=HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].hero.LaunchAngle;
			float lineWidth=HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].hero.shootWidth;

			if (launchAngle == 0)
			{
				selfFireLineRenderer.startWidth = lineWidth;
				selfFireLineRenderer.endWidth = lineWidth;
				selfFireLineRenderer.startColor = new Color(1,1,1,0.5f);
				
				selfFireLineRenderer.endColor = new Color(1,1,1,0.5f);
				selfFireLineRenderer.SetPosition(0,HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].playerPositon);
				selfFireLineRenderer.SetPosition(1,HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].playerPositon+shootDistance*temp);

			}
			else
			{
				Vector3 center = HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].playerPositon;
				int pointAmmount = HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].hero.bulletCount;
				float eachAngle = launchAngle / pointAmmount;
				Vector3 forward = HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].body.transform.forward;
				if (lineWidth == 0)
				{
					lineWidth = 0.1f;
				}
				
				selfFireLineRenderer.positionCount = (pointAmmount*2 + 2);
				selfFireLineRenderer.SetPosition(0, center);
				int i=1,cnt=1;
				for (; i <= pointAmmount; i++)
				{
					Vector3 pos = Quaternion.Euler(0, -launchAngle / 2 + eachAngle * (i - 1), 0) * temp * shootDistance+center;
					selfFireLineRenderer.SetPosition(cnt++,pos);
					selfFireLineRenderer.SetPosition(cnt++,center);

				}

				selfFireLineRenderer.SetPosition(cnt, center);
			}
			
			
			
		}
		
		if (move.joystickName == "PlayerMove")
		{
			float axisX = move.joystickAxis.y;
			float axisY = move.joystickAxis.x;
			float magnitudeSqr = axisX * axisX + axisY * axisY;
			float startDeadZoneSqr = MoveStartDeadZone * MoveStartDeadZone;
			float stopDeadZoneSqr = MoveStopDeadZone * MoveStopDeadZone;

			if (isMoveInputActive)
			{
				if (magnitudeSqr <= stopDeadZoneSqr)
				{
					isMoveInputActive = false;
				}
			}
			else if (magnitudeSqr >= startDeadZoneSqr)
			{
				isMoveInputActive = true;
			}

			if (!isMoveInputActive)
			{
				HYLDStaticValue.PlayerMoveX = Fixed.Zero;
				HYLDStaticValue.PlayerMoveY = Fixed.Zero;
				CommandManger.Instance.AddCommad_Move(HYLDStaticValue.PlayerMoveX, HYLDStaticValue.PlayerMoveY);
				return;
			}

			// 用 float 做归一化，避免 Fixed*Fixed 乘法 bug（缺少右移）
			float mag = Mathf.Sqrt(axisX * axisX + axisY * axisY);

			if (mag > 0.001f)
			{
				float normX = axisX / mag;
				float normY = axisY / mag;
				HYLDStaticValue.PlayerMoveX = new Fixed(normX);
				HYLDStaticValue.PlayerMoveY = new Fixed(normY);
			}
			else
			{
				HYLDStaticValue.PlayerMoveX = Fixed.Zero;
				HYLDStaticValue.PlayerMoveY = Fixed.Zero;
			}

			CommandManger.Instance.AddCommad_Move(HYLDStaticValue.PlayerMoveX, HYLDStaticValue.PlayerMoveY);
		}
		
	}

}

