/****************************************************
    Author:            龙之介
    CreatTime:    2022/4/22 20:57:54
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using SocketProto;

namespace Manger
{
    public class HYLDPlayerManger : MonoBehaviour
    {
        public enum PlayerType
        {
            none,
            Self,
            Teammate,
            Enemy,
        }

        public bool initFinish { get; private set; }
        private GameObject PlayerClone;
        private Dictionary<int, int> dic_battleID_map_Playeridx;
        private List<PlayerLogic> list_playerlogics;

        public void InitData()
        {
           

            initFinish = false;
            PlayerClone = HYLDResourceManger.Load(HYLDResourceManger.Type.Player);
            List<BattlePlayerPack> list_battleUser = BattleData.Instance.list_battleUsers;
            list_playerlogics = new List<PlayerLogic>();
            dic_battleID_map_Playeridx = new Dictionary<int, int>();
            int teamid = BattleData.Instance.teamID;
            int battleid = BattleData.Instance.battleID;

            /*******************生成两个队伍的出生点(自己永远在紫色方系列)*********************/
            Queue<Vector3> myteam = new Queue<Vector3>();
            myteam.Enqueue(new Vector3(15, 1, -5));
            myteam.Enqueue(new Vector3(15, 1, 0));
            myteam.Enqueue(new Vector3(15, 1, 5));
            Queue<Vector3> otherTeam = new Queue<Vector3>();
            //敌人位置镜像生成
            otherTeam.Enqueue(new Vector3(-15, 1, 5));
            otherTeam.Enqueue(new Vector3(-15, 1, 0));
            otherTeam.Enqueue(new Vector3(-15, 1, -5));


            /*********************************初始化荒野乱斗玩家信息********************************************/
            int k = 0;
            foreach (var player in list_battleUser)
            {
                if (player.Battleid == battleid)
                {
                    //是player
                    HYLDStaticValue.Players.Add(new PlayerInformation
                        (myteam.Dequeue(), player.Playername, 
                        HYLDStaticValue.Heros[(HeroName)((int)player.Hero)], player.Teamid, global::PlayerType.Self));
                }
                else if (player.Teamid == teamid)
                {
                    //和player一个队伍的
                    HYLDStaticValue.Players.Add(new PlayerInformation(myteam.Dequeue(), 
                        player.Playername, HYLDStaticValue.Heros[(HeroName)((int)player.Hero)], 
                        player.Teamid, global::PlayerType.Teammate));
                }
                else
                {
                    HYLDStaticValue.Players.Add(new PlayerInformation(otherTeam.Dequeue(), 
                        player.Playername, HYLDStaticValue.Heros[(HeroName)((int)player.Hero)], 
                        player.Teamid, global::PlayerType.Enemy));
                }
                Logging.HYLDDebug.LogError(player);
                dic_battleID_map_Playeridx.Add(player.Battleid, k++);

            }


            /**************生成player预制体实例***************/
            StartCoroutine(CreatePlayers());
        }

        private IEnumerator CreatePlayers()
        {
            int k = 0;
            foreach (var player in HYLDStaticValue.Players)
            {
                yield return new WaitForEndOfFrame();
                GameObject temp = Instantiate(PlayerClone, Vector3.zero, Quaternion.identity);
                temp.transform.GetChild(0).position = player.playerPositon;
                //Logging.HYLDDebug.LogError(player.playerPositon);
                if (player.playerType == global::PlayerType.Self)
                {
                    temp.GetComponent<HYLDPlayerController>().isSelf = true;
                    HYLDStaticValue.playerSelfIDInServer = k;
                }
                else
                {
                    temp.GetComponent<HYLDPlayerController>().isSelf = false;
                }
                temp.GetComponent<HYLDPlayerController>().playerID = k;
                list_playerlogics.Add(temp.GetComponent<PlayerLogic>());
                list_playerlogics[list_playerlogics.Count - 1].playerID = k;
                HYLDStaticValue.Players[k].body = temp;
                k++;
                //FactoryManager.CharacterFactory.CreateCharacter(WeaponType.Gun, new Vector3(0, 0, 0), HeroName.RuiKe);
            }

            for (int i = 0; i < k; i++)
            {
                Collider c1 = HYLDStaticValue.Players[i].body.transform.GetChild(0).GetComponent<BoxCollider>();
                for (int j = i + 1; j < k; j++)
                {
                    Collider c2 = HYLDStaticValue.Players[j].body.transform.GetChild(0).GetComponent<BoxCollider>();
                    Physics.IgnoreCollision(c1, c2, true);
                }
            }
            initFinish = true;
        }

        /// <summary>
        /// 统一驱动所有玩家的逻辑/UI 组件刷新。
        /// 这里只触发 PlayerLogic.OnUpdateLogic，不负责 Transform 追帧或 Animator 参数更新。
        /// </summary>
        public void UpdateAllPlayerLogics()
        {
            for (int i = 0; i < list_playerlogics.Count; i++)
            {
                list_playerlogics[i].OnUpdateLogic();
            }
        }


        /// <summary>
        /// 将一条玩家操作落到本地运行时玩家状态。
        /// 包含：移动输入写入、逻辑位置推进、攻击朝向更新。
        /// </summary>
        public void ApplyPlayerOperation(PlayerOperation opt)
        {
            int playerIndex = dic_battleID_map_Playeridx[opt.Battleid];
            PlayerInformation player = HYLDStaticValue.Players[playerIndex];
            int sign = GetTeamRelativeSign(playerIndex);

            LZJ.Fixed3 movementDir = ApplyMovementInput(player, opt, sign);
            AdvancePlayerPosition(player, movementDir);
            ApplyAttackFacing(player, opt, sign);
        }

        private int GetTeamRelativeSign(int playerIndex)
        {
            return HYLDStaticValue.Players[playerIndex].teamID != HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].teamID
                ? -1
                : 1;
        }

        private LZJ.Fixed3 ApplyMovementInput(PlayerInformation player, PlayerOperation opt, int sign)
        {
            player.playerMoveX = sign * opt.PlayerMoveX;
            player.playerMoveY = sign * opt.PlayerMoveY;

            LZJ.Fixed3 movementDir = new LZJ.Fixed3(-player.playerMoveX, 0f, player.playerMoveY);
            LZJ.Fixed movementMagnitude = movementDir.magnitude;

            // ★ 停步时显式清零 moveDir，防止渲染层 LookAt 继续朝旧方向、Animator 继续跑步
            if (movementMagnitude.ToFloat() < 0.001f)
            {
                player.playerMoveDir = Vector3.zero;
                player.playerMoveMagnitude = 0f;
            }
            else
            {
                player.playerMoveDir = movementDir.ToVector3();
                player.playerMoveMagnitude = movementMagnitude.ToFloat();
            }

            return movementDir;
        }

        private void AdvancePlayerPosition(PlayerInformation player, LZJ.Fixed3 movementDir)
        {
            // 移动公式：dir * 移动速度(units/sec) * frameTime(sec)
            LZJ.Fixed3 move = movementDir * player.移动速度 * Server.NetConfigValue.frameTime;
            player.playerPositon = (new LZJ.Fixed3(player.playerPositon) + move).ToVector3();
        }

        private void ApplyAttackFacing(PlayerInformation player, PlayerOperation opt, int sign)
        {
            // ★ 新协议：AttackOperations 是 repeated 列表，取最后一个攻击的方向作为开火朝向
            if (opt.AttackOperations == null || opt.AttackOperations.Count <= 0)
                return;

            AttackOperation lastAttack = opt.AttackOperations[opt.AttackOperations.Count - 1];
            player.fireState = FireState.PstolNormal;

            Vector3 temp = LZJ.MathFixed.xAndY2UnitVector3(lastAttack.Towardy, lastAttack.Towardx);
            temp.x *= -1 * sign;
            temp.z *= sign;

            player.fireTowards = temp;
        }
       
    }
}