/*
 * * * * * * * * * * * * * * * * 
 * Author:        魏佳楠
 * CreatTime:  2020/6/21 22:52:57 
 * Description: HYLDActionMethon
 * * * * * * * * * * * * * * * * 
*/
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;
using System;

public class HYLDActionMethon : MonoBehaviour 
{
	public void PlayerLogicMove(string s)
	{
		print("!@!##########################");
		if (HYLDStaticValue.Players.Count == 0) return;
		string[] temp = s.Split('#');
		//1.PlayerID
		//2.move x
		//3.move z
		Logging.HYLDDebug.LogError(temp[0]);
		Logging.HYLDDebug.LogError(temp[1]);
		Logging.HYLDDebug.LogError(temp[2]);
		Logging.HYLDDebug.LogError(HYLDStaticValue.Players.Count);

		int playerid = int.Parse(temp[0]);
		Logging.HYLDDebug.LogError(playerid);
		if ( playerid!= HYLDStaticValue.playerSelfIDInServer)
		{//同步其他玩家的位置
			float x = float.Parse(temp[1]);
			float z = float.Parse(temp[2]);
			HYLDStaticValue.Players[playerid].playerPositon.x = x;
			HYLDStaticValue.Players[playerid].playerPositon.z = z;

		}
		
		
	}
	
	
	public void StartBSZBMacthing(string s)
	{
		//Logging.HYLDDebug.LogError(s);
		string[] temp = s.Split('#');
		int sl = s.Length;
		//Logging.HYLDDebug.LogError(sl);
		if (sl == 1)
		{
			if (HYLDStaticValue.playerSelfIDInServer == -1)
			{
				HYLDStaticValue.playerSelfIDInServer = int.Parse(s)-1; //我的玩家ID是谁
			}
			
			
			HYLDStaticValue.MatchingPlayerTotal = int.Parse(s);
			/////////////
			///
			/// 
			//HYLDStaticValue.playerSelfIDInServer = 5;
			////////////////////////////////
		}
		else if(sl>1)
		{
			TCPSocket.被选择的英雄.Clear();
			TCPSocket.玩家名.Clear();
			foreach (string a in temp)
			{
				if (a == "") return;
				string[] temp1 = a.Split('*');
			
				TCPSocket.玩家名.Add(temp1[0]);

				/*
				foreach (HeroName hero in Enum.GetValues(typeof(HeroName)))
				{
					if (temp1[1] == hero.ToString())
					{
						
						Logging.HYLDDebug.LogError(HYLDStaticValue.被选择的英雄[i]);
						break;
					}
				}
				*/
				
				TCPSocket.被选择的英雄.Add(temp1[1]);
			}
			
			//TOOD:
			/*
			HYLDStaticValue.Players.Add(new PlayerInformation(new Vector3(15,0,-5),"玩家1",HYLDStaticValue.Heros[HeroName.XueLi], PlayerTeam.team1));
			HYLDStaticValue.Players.Add(new PlayerInformation(new Vector3(-15,0,-6),"玩家2", HYLDStaticValue.Heros[HeroName.KeErTe], PlayerTeam.team2));
			HYLDStaticValue.Players.Add(new PlayerInformation(new Vector3(15,0,0),"玩家3",HYLDStaticValue.Heros[0], PlayerTeam.team1));
			HYLDStaticValue.Players.Add(new PlayerInformation(new Vector3(-15,0,0),"玩家4",HYLDStaticValue.Heros[0], PlayerTeam.team2));
			HYLDStaticValue.Players.Add(new PlayerInformation(new Vector3(15,0,5),"玩家5",HYLDStaticValue.Heros[0], PlayerTeam.team1));
			HYLDStaticValue.Players.Add(new PlayerInformation(new Vector3(-15,0,6),"玩家6", HYLDStaticValue.Heros[0], PlayerTeam.team2));

			HYLDStaticValue.SelfTeam=HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].playerTeam;

			int pl = HYLDStaticValue.Players.Count;
			print("pl="+pl);
			for (int i = 0; i < pl; i++)
			{
				if (i == HYLDStaticValue.playerSelfIDInServer)
				{
					HYLDStaticValue.Players[i].playerType = PlayerType.Self;
				}
				else if (i != HYLDStaticValue.playerSelfIDInServer &&
				    HYLDStaticValue.SelfTeam == HYLDStaticValue.Players[i].playerTeam)
				{
					HYLDStaticValue.Players[i].playerType = PlayerType.Teammate;
				}
				else if (i != HYLDStaticValue.playerSelfIDInServer &&
				         HYLDStaticValue.SelfTeam != HYLDStaticValue.Players[i].playerTeam)
				{
					HYLDStaticValue.Players[i].playerType = PlayerType.Enemy;
				}
			}
			
			
			
			for (int i = 0; i < sl-1; i++)
			{
				HYLDStaticValue.Players[i].playerName = temp[i];
			}
			*/
			
		}
	}

	
	
	
	

}

