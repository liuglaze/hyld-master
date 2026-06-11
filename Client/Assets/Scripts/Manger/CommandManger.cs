/****************************************************
    Author:            龙之介
    CreatTime:    2022/4/23 16:48:15
    Description:     命令模式
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;

interface Commad
{
    void Execute(int commandFrameId);
}

/// <summary>
/// 命令模式
/// </summary>
public class CommandManger
{
    private static CommandManger instance;
    public static CommandManger Instance
    {
        get
        {
            // 如果类的实例不存在则创建，否则直接返回
            if (instance == null)
            {
                instance = new CommandManger();
            }
            return instance;
        }
    }

    private CommandManger()
    {
    }

    private readonly List<Commad> allCommad = new List<Commad>();
    private float latestMoveX = 0f;
    private float latestMoveY = 0f;
    private bool _lastMoveWasZero = true;
    private int queuedAttackCommandCount = 0;

    public bool HasQueuedAttackCommands
    {
        get { return queuedAttackCommandCount > 0; }
    }

    public int QueuedAttackCommandCount
    {
        get { return queuedAttackCommandCount; }
    }

    /// <summary>
    /// 攻击命令：入队到 BattleData 的待确认攻击队列
    /// </summary>
    public class AttackCommad : Commad
    {
        private readonly float dx;
        private readonly float dy;

        public AttackCommad(float x, float y)
        {
            dx = x;
            dy = y;
        }

        public void Execute(int commandFrameId)
        {
            // 入队待确认攻击队列，分配唯一 AttackId
            Manger.BattleData.Instance.EnqueueAttack(dx, dy, commandFrameId);
        }
    }

    public void AddCommad_Attack(float dx, float dy)
    {
        allCommad.Add(new AttackCommad(dx, dy));
        queuedAttackCommandCount++;
    }

    /// <summary>
    /// 移动命令改为持续输入：记录最新值，按发送帧生效
    /// </summary>
    public void AddCommad_Move(float dx, float dy)
    {
        latestMoveX = dx;
        latestMoveY = dy;

        bool isZero = Mathf.Abs(dx) <= 1e-6f && Mathf.Abs(dy) <= 1e-6f;
        if (isZero != _lastMoveWasZero)
        {
            Logging.HYLDDebug.FrameTrace($"[MoveCommand] state={(isZero ? "ZERO" : "NON_ZERO")} move=({dx:F4},{dy:F4}) queuedAttackCount={allCommad.Count}");
            _lastMoveWasZero = isZero;
        }
    }

    public void AddCommad_Move(LZJ.Fixed dx, LZJ.Fixed dy)
    {
        latestMoveX = dx.ToFloat();
        latestMoveY = dy.ToFloat();
    }

    public void Execute(int commandFrameId)
    {
        // 每个发送帧都先应用最新移动输入（避免摇杆事件频率影响）
        Manger.BattleData.Instance.selfOperation.MoveX = latestMoveX;
        Manger.BattleData.Instance.selfOperation.MoveY = latestMoveY;

        // 再消费这一帧的离散指令（如攻击 → 入队 pendingAttacks）
        for (int i = 0; i < allCommad.Count; i++)
        {
            allCommad[i].Execute(commandFrameId);
        }

        // 离散指令只生效一帧
        allCommad.Clear();
        queuedAttackCommandCount = 0;

        // ★ 把所有待确认攻击（含本帧新增的 + 之前丢包未确认的）写入 selfOperation
        Manger.BattleData.Instance.FlushPendingAttacksToOperation();
    }
}
