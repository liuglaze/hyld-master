using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class rolateSelf : MonoBehaviour
{
    public Vector3 rolate=new Vector3(0,0,1f);
    public float speed = 2f;
    public bool isSnake = false;
    private float timer = 0;
    [HideInInspector]
    public float 蜜蜂大招转弯时间=-1;
    // Start is called before the first frame update
    void Start()
    {
    }
    // Update is called once per frame
    private void FixedUpdate()
    {

        //speed += 0.1f;
        if(蜜蜂大招转弯时间!=0)
        {
            if (timer > 蜜蜂大招转弯时间)
            {
                if (gameObject.GetComponent<Rigidbody>())
                {
                    gameObject.GetComponent<Rigidbody>().velocity = transform.forward * 8;
                }
                
                gameObject.transform.Rotate(rolate * speed);
            }
            timer += Time.fixedDeltaTime;
        }
        else
        {
            gameObject.transform.Rotate(rolate * speed);
            timer += Time.fixedDeltaTime;
            if (timer > 0.1f && isSnake)
            {
                speed *= -1;
                timer = 0;
            }
        }

      
    }
}
