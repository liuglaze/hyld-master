using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class CardUGUISprit : MonoBehaviour
{
    public Sprite[] sprites;
    private string cardName;
    public Weight weight;
    public Suits color;
    private CharacterType belongTo;
    private bool makedSprite;
    public string value;
    public GameObject CardBody;

    public bool isRemoveing = false;
    private void Start()
    {
        gameObject.GetComponentInChildren<Text>().text = value;
        foreach (Sprite pos in sprites)
        {
            if (pos.name ==color.ToString())
            {
                gameObject.GetComponentsInChildren<Image>()[1].sprite = pos;
                gameObject.GetComponentsInChildren<Image>()[2].sprite = pos;
                break;
            }
        }

        if (color == Suits.Heart || color == Suits.Diamond)
        {
            gameObject.GetComponentInChildren<Text>().color=new Color(0.83f,0,0);
        }
        else
        {
            gameObject.GetComponentInChildren<Text>().color=new Color(0f,0,0);
        }
        //temp=new CardUGUISprit(weight,color,value);
    }

    public bool isChoose = false;

    public bool isCanChoose = false;
    //private CardUGUISprit temp;
    public void clickCardUGUISprit()
    {
        if (isCanChoose)
        {
            if (isChoose == false)//up
            {
                gameObject.GetComponent<RectTransform>().anchoredPosition+=new Vector2(0,50);
                StaticValue.selfReadSend.Add(this);
            }
            else//down
            {
                gameObject.GetComponent<RectTransform>().anchoredPosition+=new Vector2(0,-50);
                StaticValue.selfReadSend.Remove(this);
            }

        
            isChoose = !isChoose;
        }
        
    }

    public CardUGUISprit( Weight weight, Suits color,string value)
    {
        this.value = value;
        this.weight = weight;
        this.color = color;
    }

    public CardUGUISprit(int weight, int color,string value)
    {
        
        makedSprite = false;
        this.weight =(Weight) weight;
        this.color =(Suits) color;
        this.value = value;
        cardName = this.weight.ToString() + this.color.ToString();
        this.belongTo = (CharacterType)1;
    }

    /// <summary>
    /// 返回牌名
    /// </summary>
    public string GetCardName
    {
        get { return cardName; }
    }

    /// <summary>
    /// 返回权值
    /// </summary>
    public Weight GetCardWeight
    {
        get { return weight; }
    }

    /// <summary>
    /// 返回花色
    /// </summary>
    public Suits GetCardSuit
    {
        get { return color; }
    }

    /// <summary>
    /// 是否精灵化
    /// </summary>
    public bool isSprite
    {
        set { makedSprite = value; }
        get { return makedSprite; }
    }

    /// <summary>
    /// 牌的归属
    /// </summary>
    public CharacterType Attribution
    {
        set { belongTo = value; }
        get { return belongTo; }
    }
 
}
