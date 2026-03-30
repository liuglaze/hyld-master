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
        damage = HYLDStaticValue.Players[Id].hero.bulletDamage;
        blood = HYLDStaticValue.Players[Id].hero.BloodValue;
        HYLDStaticValue.Players[Id].body.transform.Find("Capsule").transform.localScale += new Vector3(0.4f, 0.4f, 0.4f);
        HYLDStaticValue.Players[Id].hero.bulletDamage += (int)(HYLDStaticValue.Players[Id].hero.bulletDamage * 0.3);
        HYLDStaticValue.Players[Id].hero.BloodValue += (int)(HYLDStaticValue.Players[Id].hero.BloodValue * 0.3);
        HYLDStaticValue.Players[Id].移动速度 += 1;
        Invoke("Recover", 5);
        
    }
    
     void Recover()
    {
        
        //sLogging.HYLDDebug.LogError(2);
        HYLDStaticValue.Players[Id].body.transform.Find("Capsule").transform.localScale -= new Vector3(0.3f, 0.3f, 0.3f);
        HYLDStaticValue.Players[Id].hero.bulletDamage =damage;
        HYLDStaticValue.Players[Id].hero.BloodValue = blood;
        HYLDStaticValue.Players[Id].移动速度 -= 1;
    }
}
