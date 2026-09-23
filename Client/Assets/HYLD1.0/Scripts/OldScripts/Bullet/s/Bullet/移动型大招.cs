using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class 移动型大招 : MonoBehaviour
{
    // 旧链退役（契约 §B）：原取 `LocalPositionJumpTraceThreshold`(0.8f)。
    // 该值只服务本文件的日志门限，不能为取常量而保留旧 BattleData，故内联同值常量。
    private const float LocalPositionJumpTraceThreshold = 0.8f;
    public int playerid=-1;

    private float time = 0;
    public float 控制时间;
    public Vector3 子弹位置;
    public HeroName 当前英雄;
    public GameObject 格尔子弹;

    private void FixedUpdate()
{
        if (playerid==-1) return;
        if (当前英雄==HeroName.MaiKeSi) return;
        if(HYLDStaticValue.Players[playerid].被控制&&!HYLDStaticValue.Players[playerid].isNotDie)
        {
            if(当前英雄==HeroName.XueLi)

            {
                transform.Translate((transform.position - 子弹位置).normalized * Time.deltaTime * 1, Space.World);
                Vector3 before = HYLDStaticValue.Players[playerid].playerPositon;
                HYLDStaticValue.Players[playerid].playerPositon = transform.position;
                LogSelfPositionJump("MovementSuperXueLi", before, transform.position);
            }
            if (当前英雄 == HeroName.GeEr)
            {
                if (格尔子弹 == null)
                {
                    time = 0;
                    HYLDStaticValue.Players[playerid].被控制 = false;
                    HYLDStaticValue.Players[playerid].isNotDie = true;
                    playerid = -1;
                    return;
                }
                if (time > 0.2)
                {
                   // transform.position = transform.position;
                }
                else
                {
                    Vector3 temp = 格尔子弹.transform.position;
                    //temp -= new Vector3(0, 1, 0);
                    Vector3 before = HYLDStaticValue.Players[playerid].playerPositon;
                    transform.position = temp;
                    HYLDStaticValue.Players[playerid].playerPositon = transform.position;
                    LogSelfPositionJump("MovementSuperGeEr", before, transform.position);
                    //transform.Translate((transform.position - temp).normalized * Time.deltaTime * 1, Space.World);
                }

            }
            time += Time.fixedDeltaTime;
            if (time >= 控制时间)
            {
                time = 0;
                HYLDStaticValue.Players[playerid].被控制 = false;
                HYLDStaticValue.Players[playerid].isNotDie = true;
                playerid = -1;
            }
        }
    }

    private void LogSelfPositionJump(string source, Vector3 before, Vector3 after)
    {
        if (playerid != HYLDStaticValue.playerSelfIDInServer)
        {
            return;
        }

        float delta = Vector3.Distance(before, after);
        if (delta < LocalPositionJumpTraceThreshold)
        {
            return;
        }

        Logging.HYLDDebug.FrameTrace($"[LocalPosJump][{source}] playerID={playerid} hero={当前英雄} delta={delta:F3} before=({before.x:F2},{before.y:F2},{before.z:F2}) after=({after.x:F2},{after.y:F2},{after.z:F2})");
    }
    // P3'-3c：原本这里还有一个 OnTriggerEnter，在「自己人是麦克斯」时给队友调
    // `PlayerLogic.减速(-1.5f)`（即 +1.5 加速）。已删除。
    //
    // 理由：它改写的是 `Players[].移动速度`，而服务端的移速来自配置表
    // （`Battle.cs:GetMoveSpeedFor`）—— 也就是说这个「加速」只存在于客户端预测里，
    // 服务端完全不知道。结果是这名队友的位置预测会持续与服务端权威对不上，
    // 被 MoveAck 每帧回校正（表现为位置被「拉回」）。
    //
    // 它也是唯一一个**可能真的在联机下跑起来**的旧碰撞回调（麦克斯是唯一
    // `移动型大招=true` 的英雄，本实体由 `HYLDBulletManger.SpawnMovementSuperEntity` 生成），
    // 所以删除它不只是清理，而是消除一处真实的预测分歧源。
}
