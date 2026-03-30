/*
 ****************
 * Author:        邓龙浩
 * CreatTime:  
 * Description: 投掷类子弹投掷物逻辑
 ****************
*/
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BoomCreater : MonoBehaviour
{
    public int BoomDamage;
    public int BoomOnwerID = -1;
    public float BoomRange = 2.0f;
    public float BKTime = 1.0f;
    public GameObject TheBoom;
    public GameObject 跟随物;
    private void Start()
    {
        Invoke("StartBoom", BKTime);
    }
    private void FixedUpdate()
    {
        if(跟随物!=null)
        transform.position = 跟随物.transform.position;
        
    }
    private void StartBoom()
    {
        GameObject Boom = GameObject.Instantiate(TheBoom, this.transform.position-new Vector3(0,1,0), Quaternion.Euler(0,0,1));
        Boom.GetComponent<Boom>().BoomDamage = BoomDamage;
        Boom.GetComponent<Boom>().BoomOnwerID = BoomOnwerID;
        Boom.transform.localScale =new Vector3(BoomRange, BoomRange, BoomRange);
        Destroy(gameObject, 0);
    }

}
