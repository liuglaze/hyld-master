/*
 * * * * * * * * * * * * * * * * 
 * Author:        魏佳楠
 * CreatTime:  #CREATIONDATE# 
 * Description: 控制旋转
 * * * * * * * * * * * * * * * * 
*/
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;

public class StarRotate : MonoBehaviour 
{


	void Start ()
	{
	  
	}
	
	void FixedUpdate()
	{
		gameObject.GetComponent<Transform>().Rotate(new Vector3(0,0,1),1f);
	}


}

