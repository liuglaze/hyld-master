using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ScaleSelf : MonoBehaviour
{
    
    void Update()
    {
        if(this.gameObject.transform.localScale.x>=1.65f)this.gameObject.transform.localScale = new Vector3(0, 0, 0);
        this.gameObject.transform.localScale += new Vector3(0.01f, 0.01f, 0);
        this.gameObject.GetComponent<Renderer>().material.color = Color.yellow;
    }
}
