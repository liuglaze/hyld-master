/****************************************************
    Author:            龙之介
    CreatTime:    #CreateTime#
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using SocketProto;
using System.Net;
using System.Net.Sockets;

namespace LongZhiJie
{
	public class RoomPanel :BasePanel
	{

        private void OnStartGameClick()
        {
            
        }
        private void OnCreateRoomClick()
        { 

        }
        private void OnExitRoomClick()
        {
            
        }
        public void CreateRoomResponse(MainPack pack)
        {
            switch (pack.Returncode)
            {
                case ReturnCode.Succeed:
                    UIManger.ShowMessage("创建成功");
                    UIManger.PushPanel(UIPanelType.TalkingROOM);
                    break;
                case ReturnCode.Fail:
                    UIManger.ShowMessage("创建失败");
                    //Debug.Log("注册失败");
                    break;
                default:
                    Debug.Log("Def");
                    break;
            }
            Debug.LogError(pack.Returncode.ToString());
        }
        public override void OnEnter()
        {
            base.OnEnter();
            gameObject.SetActive(true);
        }
        public override void OnExit()
        {
            base.OnExit();
            gameObject.SetActive(false);
        }
        public override void OnRecovery()
        {
            base.OnRecovery();
            gameObject.SetActive(true);
        }
        public override void OnPause()
        {
            base.OnPause();
            gameObject.SetActive(false);
        }
    }
}