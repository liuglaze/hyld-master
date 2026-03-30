using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.Threading;
using UnityEngine.Serialization;

/*
 *职责1：负责游戏内所有的信息提醒和关闭消息
 *职责2：负责游戏内所有的错误报告提示
 *职责3：需要获得信息数据，具有一个消息提醒的队列，所有消息一件一件提醒
 *
 * 采用命令模式+中介者模式
 * 具有不同的消息类型：游戏登陆界面，游戏内获取道具界面，等等
 *2020/02/27
 */
public enum MessageTypes
{
    Login,
    Server,
    Error,
    Warning,
    Game,
    Wait,
    Logo,
    Left,
    Right,
    Game02,
}

[System.Serializable]
public class MessageGameObject
{
    [FormerlySerializedAs("messageType")] public MessageTypes messageTypes;
    public GameObject messageGameObject;

}
public class Message
{
    public Message(string value,MessageTypes type,float destoryTime=2f)
    {
        this.Value = value;
        this.Type = type;
        this.DestoryTime = destoryTime;
        IsRead = false;
    }

    public float DestoryTime{ set; get; }
    public string Value { set; get; }
    public  MessageTypes Type {set;get;}
    public  bool IsRead {set;get;}

}
public class MessageController : MonoBehaviour
{

    public static List<Message> GameMessages= new List<Message>(1);
    public MessageGameObject[] MessageGameObjects;
    private int _gameMessagesLen;
    public static void sendStringMessage(string str,MessageTypes type,float destroyTime=2f)
    {
        Message success = new Message(str,type,destroyTime);
        GameMessages.Add(success);
    }
    void closeMessage(GameObject message)
    {
        message.SetActive(false);
    }
    void MessageConsume(Message message)
    {
        foreach (MessageGameObject pos in MessageGameObjects)
        {
            if (message.Type == pos.messageTypes)
            {
                GameObject temp=Instantiate(pos.messageGameObject);
                //print("!!!!!!!!!");
                if(message.Value!="")
                    ToAudio.Instance.SpackStr( message.Value);
                temp.SetActive(true);
                
                temp.GetComponentInChildren<Text>().text = message.Value;
                temp.transform.SetParent(gameObject.transform);
                temp.GetComponent<RectTransform>().anchoredPosition=new Vector2(0,0);
                temp.GetComponent<RectTransform>().localScale=new Vector3(1,1,1);
                Destroy(temp,message.DestoryTime);
                break;
            }
        }
    }
    
    // Update is called once per frame
    void Update()
    {
        
        if (GameMessages != null)
        {
            
            _gameMessagesLen = GameMessages.Count;
        
            if ( _gameMessagesLen!= 0)
            {
                //进行消费消息队列
                for (int i = 0; i < _gameMessagesLen; i++)
                {
                    if (GameMessages[i].IsRead == false)
                    {
                        GameMessages[i].IsRead = true;
                        MessageConsume(GameMessages[i]);
                    }
                }
                GameMessages.Clear();
            }
        }
        
        
        
    }
}
