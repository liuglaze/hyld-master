/*
 * * * * * * * * * * * * * * * * 
 * Author:        魏佳楠
 * CreatTime:  2020/6/18 17:38:52 
 * Description: 自动生成前景的艺术字
 * * * * * * * * * * * * * * * * 
*/
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;

public class UITextEffect : MonoBehaviour
{
	public float v3y = 20;
	public int fontsize = 4;
	public float upDownTemp = 5;
	public float speed = 0.5f;
	private GameObject UItextClone;
	private Text UItextCloneText;
	private RectTransform UItextCloneRectTransform;
	private Text UItextSelf;

	void Start ()
	{
		UItextSelf = gameObject.GetComponent<Text>();
		//UItextClone = Instantiate(gameObject, gameObject.transform).GetComponent<Text>();
		UItextClone=new GameObject("TextEffectClone");
		UItextClone.AddComponent<Text>();
		UItextClone.transform.position = gameObject.transform.position;
		UItextClone.transform.parent = gameObject.transform;
		UItextCloneRectTransform = UItextClone.GetComponent<RectTransform>();
		UItextCloneRectTransform.localPosition+=new Vector3(0,v3y,0);
		UItextCloneRectTransform.sizeDelta = gameObject.GetComponent<RectTransform>().sizeDelta;
		UItextCloneRectTransform.localScale =new Vector3(1,1,1);

		
		UItextCloneText=UItextClone.GetComponent<Text>();
		UItextCloneText.fontStyle = UItextSelf.fontStyle;
		UItextCloneText.font = UItextSelf.font;
		UItextCloneText.alignment = UItextSelf.alignment;
		UItextCloneText.text = UItextSelf.text;
		UItextCloneText.color=new Color(1,1,1,1);
		UItextCloneText.fontSize=UItextSelf.fontSize-fontsize;

		UpPositionVector3 = UItextClone.GetComponent<RectTransform>().localPosition+new Vector3(0,upDownTemp,0);
		DownPositionVector3=UItextClone.GetComponent<RectTransform>().localPosition+new Vector3(0,-upDownTemp,0);

	}

	private Vector3 UpPositionVector3;
	private Vector3 DownPositionVector3;
	private MoveState moveState=MoveState.GoingDown;
	public enum MoveState
	{
		None,
		GoingUp,
		GoingDown,

	}
	void Update ()
	{
		UItextCloneText.text = UItextSelf.text;
		if (moveState == MoveState.GoingUp)
		{
			UItextCloneRectTransform.localPosition+=new Vector3(0,speed,0);
		}
		else if (moveState == MoveState.GoingDown)
		{
			UItextCloneRectTransform.localPosition-=new Vector3(0,speed,0);
		}
		
		
		if (UItextCloneRectTransform.localPosition.y > UpPositionVector3.y)
		{
			moveState =  MoveState.GoingDown;
		}
		
		if (UItextCloneRectTransform.localPosition.y < DownPositionVector3.y)
		{
			moveState =  MoveState.GoingUp;
		}
		
	}


}

