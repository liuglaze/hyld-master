/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/28 19:50:6
    Description:     好友窗口
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;
using Server;
using SocketProto;

namespace MVC
{
	public class UIFriendPanel :UIbasePanel
	{
        public GameObject[] TalkingItemPrefabs;
        public Transform TalkingItemsParent;
        public InputField inputField;
        public RectTransform contentPanel;

        private Vector3 curPos;
        private int talkCnt=0;
        public override void Init()
        {
            base.Init();
            curPos = contentPanel.localPosition;
            Requests.Add(new BaseRequest(this, SocketProto.RequestCode.FriendRoom, SocketProto.ActionCode.Chat));
        }
        private void OnEnable()
        {
            contentPanel.localPosition = curPos ;
        }
        public override void Excute(float deltaTime)
        {
            base.Excute(deltaTime);

            if (Input.GetKeyDown(KeyCode.Return))
            {
                if (inputField.text != "")
                {
                    GameObject game = Instantiate(TalkingItemPrefabs[0], TalkingItemsParent);
                    game.GetComponent<TalkItem>().Init("您", inputField.text);
                    OnChat(inputField.text);
                    FixedPos();
                    //Invoke("CallBack", 1);
                    //contentPanel.anchoredPosition = (Vector2)scrollRect.transform.InverseTransformPoint(contentPanel.position)
                       // - (Vector2)scrollRect.transform.InverseTransformPoint(game.GetComponent<RectTransform>().position);
                    inputField.text = "";
                }
            }
        }
        public override void OnResponse(MainPack pack)
        {
            base.OnResponse(pack);
            if(pack.Chatpack.State==0)
            {
                Chat(pack.Chatpack.Playername,pack.Chatpack.Message);
            }
        }
        private void OnChat(string message)
        {
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[0].requestCode;
            pack.Actioncode = Requests[0].actionCode;
            ChatPack chatPack = new ChatPack();
            chatPack.Playername = HYLDStaticValue.PlayerName;
            chatPack.State = 0;
            chatPack.Message = message;
            pack.Chatpack = chatPack;
            Logging.HYLDDebug.Log("OnChat :" + pack);
            Requests[0].SendRequest(pack);
        }
        private void CallBack()
        {
            GameObject game = Instantiate(TalkingItemPrefabs[1], TalkingItemsParent);
            game.GetComponent<TalkItem>().Init("龙之介的小迷妹","我要做龙之介的gou!!!!!");

            FixedPos();
        }
        private void Chat(string name,string talking)
        {
            GameObject game = Instantiate(TalkingItemPrefabs[1], TalkingItemsParent);
            game.GetComponent<TalkItem>().Init(name,talking);
            FixedPos();
        }
        private void JoinRoom(string name)
        {
            GameObject game = Instantiate(TalkingItemPrefabs[1], TalkingItemsParent);
            game.GetComponent<TalkItem>().Init($"{name}加入了房间");
            FixedPos();
        }
        private void ExitRoom(string name)
        {
            GameObject game = Instantiate(TalkingItemPrefabs[1], TalkingItemsParent);
            game.GetComponent<TalkItem>().Init($"{name}退出了房间");
            FixedPos();
        }
        private void CreateRoom(string name)
        {
            GameObject game = Instantiate(TalkingItemPrefabs[1], TalkingItemsParent);
            game.GetComponent<TalkItem>().Init($"{name}创建了房间");
            FixedPos();
        }
        private void FixedPos()
        {
            talkCnt++;
            if (talkCnt > 5)
            {
                curPos += Vector3.up * 180;
                contentPanel.localPosition = curPos;
            }
        }
    }
}