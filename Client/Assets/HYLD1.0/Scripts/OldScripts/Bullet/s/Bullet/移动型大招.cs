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
                HYLDStaticValue.Players[playerid].playerPositon = transform.position;
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
                    transform.position = temp;
                    HYLDStaticValue.Players[playerid].playerPositon = transform.position;
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
