
using System;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;
using UnityEngine.SocialPlatforms;
using Random = UnityEngine.Random;

public class HYLDPlayerController : MonoBehaviour
{
	public int AILazyDegree = 100;
	public bool isAI = false;
	public bool isSelf = false;
	public int playerID = 0;
	public Transform selfTransform;
	private Animator Ani;
	public float rotatespped = 100;
	public float movespeed = 5;

	/// <summary>本地玩家视觉平滑速度（units/sec）。值越大越跟手，越小越平滑。</summary>
	public float selfSmoothSpeed = 30f;

	/// <summary>远端玩家超过此距离直接传送（跳位），不做 Lerp 平滑。</summary>
	public float maxSmoothableOffset = 3.0f;

	/// <summary>Inspector 勾选后输出逐帧渲染位置日志，用于排查移动卡顿</summary>
	public bool debugRenderPos = false;

	private Vector3 _lastLogicPos;
	private int _doubleStepCount = 0;

	void Start()
	{
		if (HYLDStaticValue.Players[playerID].playerName == "A")
			isAI = true;
		Ani = GetComponent<PlayerLogic>().bodyAnimator;
	}

	// ★ 渲染位置追赶移到 Update，用 Time.deltaTime 完全解耦于逻辑帧率
	private void Update()
	{
		if (isAI || !HYLDStaticValue.Players[playerID].isNotDie)
		{
			return;
		}
		if (Toolbox.是否游戏结束) return;

		PlayerInformation player = HYLDStaticValue.Players[playerID];
		if (isSelf)
		{
			// ★ 本地玩家：MoveTowards 匀速追赶逻辑位置
			Vector3 logicPos = player.playerPositon;
			float maxStep = selfSmoothSpeed * Time.deltaTime;
			selfTransform.position = Vector3.MoveTowards(selfTransform.position, logicPos, maxStep);
			Vector3 moveDir = player.playerMoveDir;
			moveDir.y = 0f;
			if (moveDir.sqrMagnitude > 0.001f)
			{
				selfTransform.LookAt(selfTransform.position + moveDir.normalized);
			}
		}
		else
		{
			// ★ 远端玩家：也移到 Update，用 Time.deltaTime 插值
			Vector3 targetPos = player.playerPositon;

			if (Vector3.Distance(selfTransform.position, targetPos) > maxSmoothableOffset)
			{
				selfTransform.position = targetPos;
			}
			else if (Vector3.Distance(selfTransform.position, targetPos) >= 0.01f)
			{
				selfTransform.position = Vector3.Lerp(selfTransform.position, targetPos, Time.deltaTime * movespeed);
			}

			Vector3 remoteDir = player.playerMoveDir;
			remoteDir.y = 0f;
			if (remoteDir.sqrMagnitude > 0.001f)
			{
				Vector3 lookTarget = selfTransform.position + remoteDir.normalized;
				selfTransform.LookAt(Vector3.Lerp(selfTransform.position + selfTransform.forward, lookTarget, Time.deltaTime * rotatespped));
			}
		}
	}

	private int cnt = 0;
	private int needUpdatevalue = 0;
	float movex = 1;
	float movez = 1;
	// ★ FixedUpdate 只保留动画参数同步（动画状态机在物理步更新更稳定）
    void FixedUpdate()
    {
        if (isAI || !HYLDStaticValue.Players[playerID].isNotDie)
        {
            return;
        }
        if (Toolbox.是否游戏结束) return;

        PlayerInformation player = HYLDStaticValue.Players[playerID];
        Ani.SetFloat("Speed", player.playerMoveMagnitude);
    }

}

