/*
 * * * * * * * * * * * * * * * * 
 * Author:        魏佳楠
 * CreatTime:  2019/10/27 18:46:58 
 * Description: 
 * * * * * * * * * * * * * * * * 
*/

using System;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;
using UnityEditor;
/*

[CustomEditor(typeof(ItemTriggerManage))]
public class InspectorEditor : Editor 
{
	
	private SerializedObject obj;
	private ItemTriggerManage ITM;
	
	private SerializedProperty nowItem;
	private SerializedProperty TriggerFor;
	private SerializedProperty ItemValue;
	private SerializedProperty UseCnt;


	
	
	
	private List<SerializedProperty> Trigger;
	//private SerializedProperty[] Trigger;
	//private SerializedProperty[] watch2;

	private void OnEnable()
	{
		obj = new SerializedObject(target);
		nowItem = obj.FindProperty("nowItem");
		TriggerFor= obj.FindProperty("TriggerFor");
		ItemValue= obj.FindProperty("ItemValue");
		UseCnt= obj.FindProperty("UseCnt");


	}

	public override void OnInspectorGUI()
	{
		ITM = (ItemTriggerManage) target;
		ITM.ObjcetType = (UseType)EditorGUILayout.EnumPopup("NowType",ITM.ObjcetType);
		if (ITM.ObjcetType == UseType.Trigger)
		{
			
			EditorGUILayout.PropertyField(nowItem);
			EditorGUILayout.PropertyField(TriggerFor);
			
			EditorGUILayout.PropertyField(ItemValue);
			EditorGUILayout.PropertyField(UseCnt);
		}
		else if (ITM.ObjcetType == UseType.Collider)
		{
			
			EditorGUILayout.PropertyField(ItemValue);
		}
		


	}

	



}

*/