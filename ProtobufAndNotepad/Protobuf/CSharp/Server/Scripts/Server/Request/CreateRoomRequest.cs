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
	public class CreateRoomRequest :BaseRequest
	{
        public RoomPanel RoomPanel;
        private MainPack mainPack;
        public override void Awake()
        {
            requestCode = RequestCode.Room;
            actionCode = ActionCode.CreateRoom;
            base.Awake();
        }
        private void Update()
        {
            if (mainPack != null)
            {
                RoomPanel.CreateRoomResponse(mainPack);
                mainPack = null;
            }
        }
        public override void OnResponse(MainPack pack)
        {
            mainPack = pack;
        }
        public void SendRequest()
        {
            MainPack pack = new MainPack();
            pack.Requestcode = requestCode;
            pack.Actioncode = actionCode;
            pack.Str = "r";//因为Proto的特殊机制，枚举类型为0会导致收到空包导致bug
            base.SendRequest(pack);
        }
    }
}