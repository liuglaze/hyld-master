using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class 移动型大招 : MonoBehaviour
{
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
        if (delta < Manger.BattleData.LocalPositionJumpTraceThreshold)
        {
            return;
        }

        Logging.HYLDDebug.FrameTrace($"[LocalPosJump][{source}] playerID={playerid} hero={当前英雄} delta={delta:F3} before=({before.x:F2},{before.y:F2},{before.z:F2}) after=({after.x:F2},{after.y:F2},{after.z:F2})");
    }
    private void OnTriggerEnter(Collider other)
    {
        if (other.tag == "Player"&& 当前英雄 == HeroName.MaiKeSi)
        {
            int targetPlayerId = other.transform.parent.GetComponent<PlayerLogic>().playerID;

            if (HYLDStaticValue.Players[targetPlayerId].teamID == HYLDStaticValue.Players[playerid].teamID)
            {
                other.transform.parent.GetComponent<PlayerLogic>().减速(-1.5f);
            }
        }
    }

}
