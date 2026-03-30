
/*
 * * * * * * * * * * * * * * * * 
 * Author:        赵元恺
 * CreatTime:  2020/7/28
 * Description: 一些特殊子弹的爆炸效果
 * * * * * * * * * * * * * * * * 
*/
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class shellZhaLie : MonoBehaviour
{
    // Start is called before the first frame update
    public GameObject[] ZhaliePrefab;
    public string ZhaLieName = "NULL";
    public float speed;
    public int bulletOnwerID;
    void Start()
    {
        if (ZhaLieName == "PanNi")
        {
            float j = -3 / 2;
           float  sumtamp =  3;
            for (int i = 0; i < sumtamp; i++, j ++)
            {
                //Logging.HYLDDebug.LogError(transform.rotation);
                GameObject go = GameObject.Instantiate(ZhaliePrefab[0], transform.position,this.transform.rotation) as GameObject;
                
                go.transform.LookAt(go.transform.position + go.transform.right );
                go.transform.Rotate(new Vector3(0, j * 10));

                go.GetComponent<Rigidbody>().velocity = go.transform.forward * speed;
                go.GetComponent<shell>().bulletOnwerID = bulletOnwerID;
                go.GetComponent<shell>().bulletDamage = 300;
                go.GetComponent<shell>().isZhaLie = true;
                Destroy(go, 0.5f);
            }

        }
        if (ZhaLieName == "XianRenZhang")
        {
            
            float sumtamp = 6;
            int j = 0;
            for (int i = 0; i < sumtamp; i++,j++)
            {
               // Logging.HYLDDebug.LogError(transform.rotation);
                GameObject go = GameObject.Instantiate(ZhaliePrefab[0], transform.position, this.transform.rotation) as GameObject;

                go.transform.LookAt(go.transform.position + go.transform.right);
                go.transform.Rotate(new Vector3(0,   j*60));
                go.transform.position += go.transform.forward*0.3f;
                go.GetComponent<Rigidbody>().velocity = go.transform.forward * speed;
                go.GetComponent<shell>().bulletOnwerID = bulletOnwerID;
                go.GetComponent<shell>().bulletDamage = 400;
                go.GetComponent<shell>().isZhaLie = true;
                Destroy(go, 0.7f);
            }

        }
    }
    // Update is called once per frame
    void Update()
    {
        
    }
}
