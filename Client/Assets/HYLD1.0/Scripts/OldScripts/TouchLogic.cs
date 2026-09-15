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
public class TouchLogic : MonoBehaviour 
{
	public Slider 能量条;
	public GameObject 大招遥感;

	private bool TryGetSelfPlayer(out PlayerInformation selfPlayer)
	{
		int selfIndex = HYLDStaticValue.playerSelfIDInServer;
		if (selfIndex >= 0 && selfIndex < HYLDStaticValue.Players.Count)
		{
			selfPlayer = HYLDStaticValue.Players[selfIndex];
			return selfPlayer != null;
		}

		selfPlayer = null;
		return false;
	}

	private bool EnsureSelfFireLineRenderer(PlayerInformation selfPlayer)
	{
		if (selfPlayer == null || selfPlayer.body == null)
		{
			selfFireLineRenderer = null;
			return false;
		}

		if (selfFireLineRenderer != null)
		{
			return true;
		}

		selfFireLineRenderer = selfPlayer.body.GetComponentInChildren<LineRenderer>();
		return selfFireLineRenderer != null;
	}


	private void FixedUpdate()
{
		if (!TryGetSelfPlayer(out PlayerInformation selfPlayer))
		{
			if (能量条 != null) 能量条.gameObject.SetActive(true);
			if (大招遥感 != null) 大招遥感.SetActive(false);
			return;
		}

		if (selfPlayer.当前能量 >= selfPlayer.最大能量)
		{
			selfPlayer.可以按大招 = true;
		}
		能量条.gameObject.SetActive(!selfPlayer.可以按大招);
		大招遥感.SetActive(selfPlayer.可以按大招);
		if (能量条.gameObject.activeSelf)
		{
			能量条.value = selfPlayer.当前能量 / selfPlayer.最大能量;
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
			float prevMoveX = _lastLoggedMoveX;
			float prevMoveY = _lastLoggedMoveY;
			isMoveInputActive = false;
			HYLDStaticValue.PlayerMoveX  = 0f;
			HYLDStaticValue.PlayerMoveY  = 0f;
			CommandManger.Instance.AddCommad_Move(HYLDStaticValue.PlayerMoveX, HYLDStaticValue.PlayerMoveY);
			Logging.HYLDDebug.FrameTrace($"[StopInput][JoystickRelease] axis=(0.0000,0.0000) prevMove=({prevMoveX:F4},{prevMoveY:F4}) startDz={MoveStartDeadZone:F2} stopDz={MoveStopDeadZone:F2}");
			_lastLoggedMoveZero = true;
			_lastLoggedMoveX = 0f;
			_lastLoggedMoveY = 0f;
		}
		if (move.joystickName == "FireNormal"||move.joystickName=="FireSuper")
		{
			if (!TryGetSelfPlayer(out PlayerInformation selfPlayer))
			{
				Logging.HYLDDebug.FrameTrace($"[AttackInput] REJECTED joystick={move.joystickName} reason=self_player_not_found");
				return;
			}

			if (!EnsureSelfFireLineRenderer(selfPlayer))
			{
				Logging.HYLDDebug.FrameTrace($"[AttackInput] REJECTED joystick={move.joystickName} reason=line_renderer_not_ready");
				return;
			}

			selfFireLineRenderer.enabled = false;

			// ★ 去掉 fireState == none 的前置检查
			// 现在攻击统一走 CommandManger → EnqueueAttack 队列，
			// 不再用 fireState 做输入门控，fireState 只用于逻辑层驱动发射
			// 摇杆位移太小时忽略（两个轴都接近零 = 没有有效方向）
			Logging.HYLDDebug.FrameTrace($"[AttackInput] joystick={move.joystickName} FirePosX={FirePositionX:F4} FirePosY={FirePositionY:F4}");
			if (Mathf.Abs(FirePositionX) <= 0.02f && Mathf.Abs(FirePositionY) <= 0.02f)
			{
				Logging.HYLDDebug.FrameTrace("[AttackInput] REJECTED by dead zone");
				return;
			}
			if (move.joystickName == "FireSuper")
			{
				if (!selfPlayer.可以按大招 || selfPlayer.当前能量 < selfPlayer.最大能量)
				{
					Logging.HYLDDebug.FrameTrace($"[SuperInput] REJECTED energy={selfPlayer.当前能量:F1}/{selfPlayer.最大能量:F1}");
					return;
				}
				if (selfPlayer.hero == null
					|| selfPlayer.hero.superBullet == null
					|| selfPlayer.hero.大招实体 == null
					|| selfPlayer.hero.isSuperMovingType)
				{
					Logging.HYLDDebug.FrameTrace("[SuperInput] REJECTED reason=unsupported_super_type");
					return;
				}
				Logging.HYLDDebug.FrameTrace("[SuperInput] ACCEPTED -> AddCommad_SuperAttack");
				CommandManger.Instance.AddCommad_SuperAttack(FirePositionX, FirePositionY);
				return;
			}

			Logging.HYLDDebug.FrameTrace("[AttackInput] ACCEPTED -> AddCommad_Attack");
			CommandManger.Instance.AddCommad_Attack(FirePositionX, FirePositionY);
		}
	}

	private float FirePositionY = 0f;
	private float FirePositionX = 0f;
	private LineRenderer selfFireLineRenderer;

	private const float MoveStartDeadZone = 0.18f;
	private const float MoveStopDeadZone = 0.12f;
	private bool isMoveInputActive = false;
	private bool _lastLoggedMoveZero = true;
	private float _lastLoggedMoveX = 0f;
	private float _lastLoggedMoveY = 0f;

	private float shootDistance;
	private float launchAngle;
	private void Start()
	{
		if (能量条 != null) 能量条.gameObject.SetActive(true);
		if (大招遥感 != null) 大招遥感.SetActive(false);
	}


	void OnJoystickMove(MovingJoystick move)
	{
		//Logging.HYLDDebug.LogError(Toolbox.是否游戏结束);
		if (Toolbox.是否游戏结束) return;
		if (move.joystickName == "FireNormal"|| move.joystickName == "FireSuper")
		{
			if (!TryGetSelfPlayer(out PlayerInformation selfPlayer))
			{
				Logging.HYLDDebug.FrameTrace($"[AttackAim] REJECTED joystick={move.joystickName} reason=self_player_not_found");
				return;
			}

			if (!EnsureSelfFireLineRenderer(selfPlayer))
			{
				Logging.HYLDDebug.FrameTrace($"[AttackAim] REJECTED joystick={move.joystickName} reason=line_renderer_not_ready");
				return;
			}
			if (selfPlayer.hero == null)
			{
				Logging.HYLDDebug.FrameTrace($"[AttackAim] REJECTED joystick={move.joystickName} reason=hero_not_ready");
				return;
			}

			FirePositionX = move.joystickAxis.x;
			FirePositionY = move.joystickAxis.y;
			
			selfFireLineRenderer.enabled = true;
			Vector3 temp = BattleFloatMath.ToWorldDirection(FirePositionX, FirePositionY, 1);
			shootDistance = selfPlayer.hero.shootDistance;
			
			//Logging.HYLDDebug.Log(shootDistance);
			
			launchAngle=selfPlayer.hero.LaunchAngle;
			float lineWidth=selfPlayer.hero.shootWidth;

			if (launchAngle == 0)
			{
				selfFireLineRenderer.startWidth = lineWidth;
				selfFireLineRenderer.endWidth = lineWidth;
				selfFireLineRenderer.startColor = new Color(1,1,1,0.5f);
				
				selfFireLineRenderer.endColor = new Color(1,1,1,0.5f);
				selfFireLineRenderer.SetPosition(0,selfPlayer.playerPositon);
				selfFireLineRenderer.SetPosition(1,selfPlayer.playerPositon+shootDistance*temp);

			}
			else
			{
				Vector3 center = selfPlayer.playerPositon;
				int pointAmmount = selfPlayer.hero.bulletCount;
				float eachAngle = launchAngle / pointAmmount;
				Vector3 forward = selfPlayer.body.transform.forward;
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
			bool wasMoveInputActive = isMoveInputActive;

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
				HYLDStaticValue.PlayerMoveX = 0f;
				HYLDStaticValue.PlayerMoveY = 0f;
				CommandManger.Instance.AddCommad_Move(HYLDStaticValue.PlayerMoveX, HYLDStaticValue.PlayerMoveY);
				if (!_lastLoggedMoveZero)
				{
					Logging.HYLDDebug.FrameTrace($"[StopInput][DeadZoneZero] axis=({axisX:F4},{axisY:F4}) magSqr={magnitudeSqr:F4} prevMove=({_lastLoggedMoveX:F4},{_lastLoggedMoveY:F4}) wasActive={wasMoveInputActive} startDz={MoveStartDeadZone:F2} stopDz={MoveStopDeadZone:F2}");
				}
				_lastLoggedMoveZero = true;
				_lastLoggedMoveX = 0f;
				_lastLoggedMoveY = 0f;
				return;
			}

			float mag = Mathf.Sqrt(axisX * axisX + axisY * axisY);

			if (mag > 0.001f)
			{
				float normX = axisX / mag;
				float normY = axisY / mag;
				HYLDStaticValue.PlayerMoveX = normX;
				HYLDStaticValue.PlayerMoveY = normY;
			}
			else
			{
				HYLDStaticValue.PlayerMoveX = 0f;
				HYLDStaticValue.PlayerMoveY = 0f;
			}

			CommandManger.Instance.AddCommad_Move(HYLDStaticValue.PlayerMoveX, HYLDStaticValue.PlayerMoveY);
			float currentMoveX = HYLDStaticValue.PlayerMoveX;
			float currentMoveY = HYLDStaticValue.PlayerMoveY;
			if (_lastLoggedMoveZero)
			{
				Logging.HYLDDebug.FrameTrace($"[StopInput][ResumeMove] axis=({axisX:F4},{axisY:F4}) normMove=({currentMoveX:F4},{currentMoveY:F4}) mag={mag:F4} startDz={MoveStartDeadZone:F2} stopDz={MoveStopDeadZone:F2}");
			}
			_lastLoggedMoveZero = false;
			_lastLoggedMoveX = currentMoveX;
			_lastLoggedMoveY = currentMoveY;
		}
		
	}

}

