/*
 ****************
 * Author:        赵元恺
 * CreatTime:  2020/7/28
 * Description: 额因为貌似生成的子弹方向不可调所以我用代码强制调了
 ****************
*/
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RotationSlef : MonoBehaviour
{
    // Start is called before the first frame update
    public Vector3 rotation;
    public bool isPeiPei = false;
    public bool 是奶妈 = false;
    void Start()
    {
        if (是奶妈) transform.position += new Vector3(0, 1, 0);
        this.gameObject.transform.Rotate (rotation);
        
    }

    // Update is called once per frame
    void Update()
    {
        if (gameObject.transform.localScale.magnitude < 0.0001) Destroy(gameObject);
        if(isPeiPei)
        {
            gameObject.transform.localScale -= new Vector3(0.005f, 0.005f, 0.005f);
        }
    }
}
