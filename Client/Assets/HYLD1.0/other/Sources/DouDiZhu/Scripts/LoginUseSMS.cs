using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
/*
 *职责1：负责send短信的按钮的逻辑
 *职责2：负责点击注册和登录信息时的按钮的逻辑
 *
 *2020/02/27
 *
 *职责新：负责整个LoginPanal的所有逻辑
 * 整个脚本有很大的缺点就是杂糅了过多的方法
 * 2020/03/01
 */
[System.Serializable]
public class PanalGameObject
{
    public string pannalName;
    public GameObject pannal;

}
public class LoginUseSMS : MonoBehaviour
{
    /*
     *职责1：实现一个方法传入这个pannal 使这个pannal显示出来，其他pannal隐藏
     *指着2：单例 且支持外部通信 
     */
    private static LoginUseSMS _instance;
    
    public static LoginUseSMS Instance { get {
        if (_instance == null)
        {
            _instance = GameObject.Find("UI/UserPanlae").GetComponent<LoginUseSMS>();
        }
        return _instance;
    } }
    
    public List<PanalGameObject> panals;
    public Text phoneNumberUIText;
    public Text loginNumberUIText;
    public Text loginNumberLastTimeUIText;
    public InputField vf_code;
    private bool isVfSend = false;

    public GameObject returnLoginPanal;

    public AnimationClip closePanalAnimation;
    //private TCPSocket _tcpSocketsocket;
    public void clickSendSMS(Text phoneNumber)
    {
        string number = phoneNumber.text;//selfPhoneNumber.text;
        Regex rx = new Regex(Server.NetConfigValue.RegexValue);
        if (number == "")
        {
            MessageController.sendStringMessage("手机号不能为空，请重新输入", MessageTypes.Login);
        }
        else if (rx.IsMatch(number))///////////号码正确，进行发送验证码
        {
            if (TCPSocket.Instance.Send(OldRequestCode.User, OldActionCode.SendSMS, number) == true)
            {
                StaticValue.selfPhoneNumber=number;
                MessageController.sendStringMessage("正在请求,请稍后...",MessageTypes.Wait);
                showPannal("vfPanal");
                phoneNumberUIText.text=number;
            }
        }
        else
        {
            MessageController.sendStringMessage("手机号非法，请重新输入",MessageTypes.Login);
        }
        
    }
    public void clickUserLogin()
    {
        if (StaticValue.isLogin==false)
        {
            if (TCPSocket.Instance.Send(OldRequestCode.User, OldActionCode.Logins, StaticValue.selfPhoneNumber) == true)
            {
                StaticValue.isLogin = true;
                closePannal();
            }
            else
            {
                MessageController.sendStringMessage("抱歉！网络异常,登录失败",MessageTypes.Error);
            }
        }
        else
        {
            closePannal();
        }
    }
    public void clickGameStart()
    {
        if(StaticValue.selfPhoneNumber!=""&&StaticValue.isLogin==true)
        {
            MessageController.sendStringMessage("进入成功",MessageTypes.Server);
            TCPSocket.Instance.Send(OldRequestCode.Game, OldActionCode.GameHall, StaticValue.selfPhoneNumber);
            SceneManager.LoadScene("game01");
        }
        else
        {
            MessageController.sendStringMessage("请先登录账号",MessageTypes.Server);
            //showPannal("loginPanal");
        }
        
    }
    private void Update()
    {
        if (vf_code != null)
        {
            string code = vf_code.text;
            if (code.Length > 6)
            {
                vf_code.text = vf_code.text.Substring(0, 6);
            }
            if (code.Length ==6&&isVfSend==false)
            {
                isVfSend = true;

                if (TCPSocket.Instance.Send(OldRequestCode.User, OldActionCode.VfCode, code) == true)
                {
                    
                    vf_code.text = "";
                    //sendStringMessage("验证码输入错误");
                    isVfSend = false;
                }
            }
        }

        if (User.isVfCodeShouldMainPrcessDo == true)
        {
            User.isVfCodeShouldMainPrcessDo = false;
            User.VfCodeShouldMainPrcessDo();

        }
        
    }

    
    public void showPannal(string panal)
    {
        foreach (PanalGameObject pos in panals)
        {
            if(pos.pannalName==panal)pos.pannal.SetActive(true);
            else pos.pannal.SetActive(false);
        }
    }

    public void sendStringMessage(string str)
    {
        MessageController.sendStringMessage(str, MessageTypes.Login);
    }
    public void closePannal()
    {
        foreach (PanalGameObject pos in panals)
        {
            if (pos.pannal.gameObject.activeInHierarchy == true)
            {
                GameObject closegGameObject=Instantiate(pos.pannal);
                closegGameObject.transform.parent = pos.pannal.transform.parent;
                pos.pannal.SetActive(false);
                Animator a=closegGameObject.GetComponent<Animator>();
                a.Play(closePanalAnimation.name);
                GameObject.Destroy(closegGameObject,1f);
            }
        }
        //GameObject.Find("exitButton").SetActive(false);
    }
    public void openLoginPanal()
    {
        
        if (PlayerPrefs.HasKey("LoginUserPhoneNumber") == false)
        {
            showPannal("smsPanal");
            returnLoginPanal.SetActive(false);
        }
        else if (StaticValue.isLogin==true)
        {
            showPannal("userPanal");
        }
        else
        {
            StaticValue.selfPhoneNumber = PlayerPrefs.GetString("LoginUserPhoneNumber");
            loginNumberUIText.text = StaticValue.selfPhoneNumber.Substring(0,3)+"****"+StaticValue.selfPhoneNumber.Substring(7,4);
            loginNumberLastTimeUIText.text=PlayerPrefs.GetString("LoginUserPhoneNumberLastTime");
            showPannal("loginPanal");
            returnLoginPanal.SetActive(true);
        }
        
    }
    
    void waitConn()
    {
        MessageController.sendStringMessage("", MessageTypes.Wait, 1f);
    }
    
    void Start()
    {
        showPannal("nullPanal");
        MessageController.sendStringMessage("",MessageTypes.Logo,3f);
        
        Invoke("waitConn",3f);
        Invoke("openLoginPanal",4f);

            
            
    }

}
