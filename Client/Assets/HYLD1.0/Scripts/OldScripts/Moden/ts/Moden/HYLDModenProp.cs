using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HYLDModenProp : MonoBehaviour
{
    // Start is called before the first frame update
    public float timeStamp;
    public GameObject Prop;
    public bool isBottleCreate = false;
    public bool isBottle = false;
    // Update is called once per frame
    public  int Id=0;
    public  int damage;
    public  int blood;
    public bool 是搞服务器的 = false;
    public GameObject 服务器;
    private void Start()
    {
       if(是搞服务器的&&TCPSocket.Instance==null)
        {
            Instantiate(服务器);
        }
        if (isBottleCreate)
        Destroy(this.gameObject,timeStamp);
    }
    private void OnDestroy()
    {
        if(isBottleCreate)
        Instantiate(Prop, gameObject.transform.position,Quaternion.identity);
    }
    private void OnCollisionEnter(Collision collision)
    {
        if (isBottle == false) return;

        if(collision.gameObject.tag == "Player")
        {
            Id = collision.transform.parent.GetComponent<PlayerLogic>().playerID;
            wd();
            gameObject.transform.position = new Vector3(10000, 10000, 10000);
            Destroy(gameObject, 6);
        }
      
    }
    void wd()
    {
       // Logging.HYLDDebug.LogError(Id);
        // 狂暴瓶：+30% 伤害与血量上限、+1 移速，5 秒后还原。
        //
        // P3'-2 起这些修正写在**每玩家有效值**上（Players[Id].bulletDamage / playerBloodMax / 移动速度），
        // 不再写 hero.* —— 后者是共享配置，写它会让「同英雄的所有玩家」一起被加强。
        damage = HYLDStaticValue.Players[Id].bulletDamage;
        blood = HYLDStaticValue.Players[Id].playerBloodMax;
        HYLDStaticValue.Players[Id].body.transform.Find("Capsule").transform.localScale += new Vector3(0.4f, 0.4f, 0.4f);
        HYLDStaticValue.Players[Id].bulletDamage += (int)(HYLDStaticValue.Players[Id].bulletDamage * 0.3);
        HYLDStaticValue.Players[Id].playerBloodMax += (int)(HYLDStaticValue.Players[Id].playerBloodMax * 0.3);
        HYLDStaticValue.Players[Id].移动速度 += 1;
        Invoke("Recover", 5);
        
    }
    
     void Recover()
    {
        
        //sLogging.HYLDDebug.LogError(2);
        HYLDStaticValue.Players[Id].body.transform.Find("Capsule").transform.localScale -= new Vector3(0.3f, 0.3f, 0.3f);
        HYLDStaticValue.Players[Id].bulletDamage = damage;
        HYLDStaticValue.Players[Id].playerBloodMax = blood;
        HYLDStaticValue.Players[Id].移动速度 -= 1;
    }
}
