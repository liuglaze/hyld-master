/*
 * * * * * * * * * * * * * * * * 
 * Author:        龙之介
 * CreatTime:  2020/12/21 23:26:52 
 * Description: 控制游戏内的所有触发事件的功能物体
 * * * * * * * * * * * * * * * * 
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 所有的触发事件物体的枚举类型
/// </summary>
public enum Item
{

    Exp,
    /*
    GetKey,
  
    LoadValueScense,
    LoadNextScense,
    LoadUpScense,
    Transfer2DPoint,
    Transfer3DPoint,
    GetJumpButton,
    GetPuffButton,
    JustValueScense,
    Gold,
    Change3DCamera,
    */
    None,
    TipsUIText,
    Die,
    Blood,
    DownConner,UpConner,LeftConner,RightConner,
    Right,
    Up,
    Left,
    Down
}

/// <summary>
/// 所有触发事件对象的TAG
/// </summary>
public enum Tag
{
    None,
    Player,
    seed,
    firefly,
    NPC

}

public class ZYKTriggerTool : MonoBehaviour
{


    //public UseType ObjcetType = UseType.Trigger;

    //public Organ nowOrgan = Organ.Door;
    
    
    public Item nowItem = Item.TipsUIText;
    public Tag TriggerFor = Tag.Player;
    public string ItemValue ="1";//事件加多少值
    public int UseCnt = 1; //表示永远不消失

    public GameObject obj;
    private GameObject[] Targets;
    //private bool isCanDestroy = false;

    private void Start()
    {
        gameObject.transform.GetChild(0).gameObject.SetActive(false);
        //if collider change BoxCollider is Trigger
        /*if (ObjcetType == UseType.Collider)
        {
            gameObject.GetComponent<BoxCollider>().isTrigger = false;
            UseCnt = 0;//机关不会消失

            //Destroy(this.gameObject.GetComponent<Rigidbody>());
        }
        else if (ObjcetType == UseType.Trigger)
        {
            gameObject.GetComponent<BoxCollider>().isTrigger = true;
        }*/
        if(obj!=null)
        obj.SetActive(false);
        Targets=GameObject.FindGameObjectsWithTag(TriggerFor.ToString());
    }


    ItemManageEvent now_event = new ItemManageEvent();
    MethodInfo func;
/*
    private void OnCollisionEnter(Collision other)
    {
        print("OnCollisionEnter");
    }

*/
    /// <summary>
    /// 触发事件内容
    /// </summary>
    /// <param name="other"></param>
    /// 
    private void TapTapOnTriggerEnter()
    {
        foreach(GameObject target in Targets)
        {
            
            //if (other.tag ==  && UseCnt > 0)
            {
                //print("!!!");
                func = now_event.GetType().GetMethod((nowItem).ToString());
                object[] obj = new object[1];
                obj[0] = ItemValue;
                Logging.HYLDDebug.Log(func);
                Logging.HYLDDebug.Log(now_event);
                func.Invoke(now_event, obj);

                UseCnt--;

                if (UseCnt == 0)
                {
                    Invoke("MyOnDestroy", 1f);

                }
            }
        }

    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        //print("Im OnTriggerEnter"+other.tag);
        if (other.tag == TriggerFor.ToString())
        {
            //print("!!!");
            func = now_event.GetType().GetMethod((nowItem).ToString());
            object[] obj = new object[1];
            obj[0] = ItemValue;
            func.Invoke(now_event, obj);
            
            UseCnt--;
            
            if(UseCnt==0)
            {
                Invoke("MyOnDestroy",1f);
                
            }
        }
    }

    private void MyOnDestroy()
    {
        if (obj != null)
        {
            obj.SetActive(true);
        }
        Destroy(gameObject);
    }
}




public class ItemManageEvent : MonoBehaviour
{
    public GameObject g;
    public void GetJumpButton(string value)
    {
        //PlayerState.hasJumpButton = true;
        //MainCameraMove.isHavaLine = true;


    }
    public void GetPuffButton(string value)
    {
        //PlayerState.hasPuffButton = true;
        
    }


    public void GetSkill_Frozen(string value)
    {
        //PlayerState.Skill_Frozen = 1;

    }
    
    
    //public ItemManageEvent() { }
    public void None(string value)
    {
        //Logging.HYLDDebug.Log("物体未指定事件");
        
    }
    public void Exp(string value)
    {
        //Logging.HYLDDebug.Log("获取经验事件"+ value);
        //PlayerState.PlayerExp += int.Parse(value);

    }
    public void Blood(string value)
    {
        //Logging.HYLDDebug.Log("增加生命值事件" + value);
        //PlayerState.SeedBlood += int.Parse(value);
    }
    
    public void Gold(string value)
    {
        //Logging.HYLDDebug.Log("增加金币事件" + value);
        //PlayerState.Gold += int.Parse(value);
    }
    
    public void Change3DCamera(string value)
    {
        //Logging.HYLDDebug.Log("增加金币事件" + value);
        //PlayerState.Is3DGamePlay =!PlayerState.Is3DGamePlay;
    }
   
    public void TipsUIText(string value)
    {
        ///Tips显示内容的变更
        
       


        //StaticValue.GameTips_UIText=value;
    }
    
    
    
    public void GetKey(string value)
    {
        //PlayerState.Is3DGamePlay = false;
    }

    public void Die(string value)
    {
        //PlayerState.PlayerExp = 0;
        //PlayerState.SeedBlood = 3;

        //PlayerState.ScenseNextName = SceneManager.GetActiveScene().name;
        //Invoke("lazyLoadScene",3f);
        //PlayerState.startLoadScense = true;
    }

    
    
    public void LoadValueScense(string value)
    {
        //PlayerState.ScenseName = SceneManager.GetActiveScene().name;
        //PlayerState.ScenseNextName = value;

        //PlayerState.startLoadScense = true;
        //Invoke("lazyLoadScene",3f);

    }

    public void JustValueScense(string value)
    {
        //PlayerState.ScenseNextName = value;
        SceneManager.LoadScene(value);
    } 
    
    
    public void LoadNextScense(string value)
    {
        
        
        //SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex+1);

    }
    public void LoadUpScense(string value)
    {

        //SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex - 1);

    }

    public void Transfer2DPoint(string value)
    {
        string[] ans = value.Split(',');

        Vector3 point=new Vector3(float.Parse(ans[0]),float.Parse(ans[1]),0);
        //print(point);
        if(GameObject.FindWithTag("seed").GetComponent<NavMeshAgent>()!=null)
            
        GameObject.FindWithTag("seed").GetComponent<NavMeshAgent>().enabled = false;
        GameObject.FindWithTag("seed").transform.position=point;
        //GameObject.FindWithTag("firefly").transform.position=point+PlayerState.firefly2playerTemp;
        
        if(GameObject.FindWithTag("seed").GetComponent<NavMeshAgent>()!=null)
        GameObject.FindWithTag("seed").GetComponent<NavMeshAgent>().enabled = true;
    }
    public void Transfer3DPoint(string value)
    {
        string[] ans = value.Split(',');

        Vector3 point=new Vector3(float.Parse(ans[0]),float.Parse(ans[1]),float.Parse(ans[2]));
        Logging.HYLDDebug.Log(point);
        GameObject.FindWithTag("seed").transform.position=point;
        //GameObject.FindWithTag("firefly").transform.position=point+PlayerState.firefly2playerTemp;
    }

    public void DownConner(string value)
    {
      //  TapTapStaticValue.Player
       
    }
    public void UpConner(string value)
    {
        Logging.HYLDDebug.Log("gyugfgyugfuy");
     
    }
    public void LeftConner(string value)
    {
    
    }
    public void RightConner(string value)
    {
      
    }
    public void Right(string value)
    {
    
    }
    public void Up(string value)
    {
  
    }
    public void Left(string value)
    {
   
    }
    public void Down(string value)
    {
      
    }
}

