/****************************************************
    Author:            龙之介
    CreatTime:    2021/9/21 20:46:53
    Description:     开始游戏面板
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
	public class UIStartPanel :UIbasePanel
	{
        public Button Login;

        protected override void RegisterUIEvent()
        {
            base.RegisterUIEvent();
            Login.onClick.AddListener(() => { CheckName();  });
        }
        public override void Init()
        {
            base.Init();
            Requests.Add(new BaseRequest(this, SocketProto.RequestCode.User, SocketProto.ActionCode.FindPlayerInfo));
        }
        private void CheckName()
        {
            /*
             2.1点击登陆钮
            发送查询玩家信息请求
             */
            MainPack pack = new MainPack();
            pack.Requestcode = Requests[0].requestCode;
            pack.Actioncode = Requests[0].actionCode;
            LoginPack loginPack = new LoginPack();
            loginPack.Username = HYLDStaticValue.UseName;
            pack.Loginpack = loginPack;
            pack.Str = PlayerPrefs.GetString(PlayerPrefabConstValue.HYLDPlayerHero, HeroName.XueLi.ToString());
            //pack.Str = "FindName";
            //Logging.HYLDDebug.Log("FindName : "+pack);
            Requests[0].SendRequest(pack);
            HYLDManger.Instance.ShowMessage(pack.Str);
        }
        public override void OnResponse(MainPack pack)
        {
            //2.5更新玩家信息
            HYLDManger.Instance.ShowMessage(pack.Actioncode.ToString());
            base.OnResponse(pack);
            HYLDStaticValue.PlayerUID = pack.UserInfopack.Id;
            switch (pack.Returncode)
            {
                case ReturnCode.Succeed:
                    HYLDStaticValue.PlayerName = pack.UserInfopack.Playername;
                    Logging.HYLDDebug.Trace($"{HYLDStaticValue.PlayerName} 开始游戏");
                    HYLDManger.Instance.ShowMessage($"{HYLDStaticValue.PlayerName}进入游戏");
                    break;
                case ReturnCode.Fail:
                    HYLDStaticValue.PlayerName = "";
                    HYLDManger.Instance.ShowMessage("还未创建过名字");
                    break;
                default:
                    print("Def");
                    break;
            }
            //2.6加载主菜单场景HYLDStart
            UnityEngine.SceneManagement.SceneManager.LoadScene("HYLDStart");
        }

    }
}