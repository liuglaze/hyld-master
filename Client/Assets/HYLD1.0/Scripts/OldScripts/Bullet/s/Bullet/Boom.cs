/*
 ****************
 * Author:        邓龙浩
 * CreatTime:  
 * Description: 投掷类子弹投掷物爆炸逻辑
 ****************
*/
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Boom : MonoBehaviour
{
    // Start is called before the first frame update

    public int BoomDamage;
    public int BoomOnwerID = -1;
    public float BKTime = 1.0f;
    private bool[] BeHurted=new bool[10];
    private float timer = 0;
    public float TimeStamp = 1f;
    public bool isPoison = true;
    private List<GameObject> gos = new List<GameObject>();
    public bool 是炮台 = false;
    private void Start()
    {
        if (isPoison) timer = 0.8f;
        for (int i = 0; i < 10; i++)
        {
            BeHurted[i] = false;
        }
        if(!是炮台)
        Destroy(gameObject, BKTime);
        else
        {
            HYLDStaticValue.Players[BoomOnwerID].炮台数量++;
        }
    }

    private void OnTriggerEnter(Collider collision)
    {
        if (collision.tag == "Player"|| collision.tag == "Text")
        {
          //  Logging.HYLDDebug.LogError(collision.gameObject);
            gos.Add(collision.gameObject);
        }
       
    }
    private void OnTriggerExit(Collider collision)
    {
        if (collision.tag == "Player" || collision.tag == "Text")
        {
           // Logging.HYLDDebug.LogError(collision.gameObject);
            gos.Remove(collision.gameObject);
        }

    }
   
    
    private void FixedUpdate()
    {
        if (HYLDStaticValue.Players[BoomOnwerID].炮台数量>1)
        {
            HYLDStaticValue.Players[BoomOnwerID].炮台数量--;
            Destroy(gameObject);
        }
        timer += Time.fixedDeltaTime;
        if (timer <= TimeStamp) return;
        Logging.HYLDDebug.LogError(timer);
        timer = 0;
        foreach (GameObject others in gos)
        {
            if (others.tag == "Player")
            {
                int targetPlayerId = others.transform.parent.GetComponent<PlayerLogic>().playerID;
                if (HYLDStaticValue.Players[BoomOnwerID].hero.heroName == HeroName.PaMu && 是炮台)
                {
                    if (HYLDStaticValue.Players[targetPlayerId].teamID == HYLDStaticValue.Players[BoomOnwerID].teamID)
                    {
                        if (HYLDStaticValue.Players[targetPlayerId].是否有防护罩) return;
                        if (BeHurted[targetPlayerId] != true)
                        {
                            HYLDStaticValue.Players[targetPlayerId].playerBloodValue -= BoomDamage;
                            if (isPoison == false)
                                BeHurted[targetPlayerId] = true;
                        }
                    }
                }
                else
                {
                    if (HYLDStaticValue.Players[targetPlayerId].teamID != HYLDStaticValue.Players[BoomOnwerID].teamID)
                    {
                        if (HYLDStaticValue.Players[targetPlayerId].是否有防护罩) return;
                        if (BeHurted[targetPlayerId] != true)
                        {
                            HYLDStaticValue.Players[targetPlayerId].playerBloodValue -= BoomDamage;
                            if (isPoison == false)
                                BeHurted[targetPlayerId] = true;
                        }
                    }
                }
              //  Logging.HYLDDebug.LogError(targetPlayerId);
                
            }
            else if (others.tag == "Text")
            {
                others.gameObject.GetComponent<TextLogic>().playerBlood -= BoomDamage;
            }
            if (isPoison == false) Destroy(gameObject);
        }
    }
   
}