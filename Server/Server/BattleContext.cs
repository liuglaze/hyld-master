using Server.Controller;
using System.Collections.Generic;

namespace Server
{
	class BattleContext
	{
		public int BattleId { get; }
		public MatchingController.FightPattern FightPattern { get; }
		public List<MatchUserInfo> MatchUsers { get; }
		public List<int> PlayerUids { get; }
		public Dictionary<int, int> UidToBattlePlayerId { get; }
		public BattleController Controller { get; private set; }

		public BattleContext(int battleId, MatchingController.FightPattern fightPattern, List<MatchUserInfo> matchUsers)
		{
			BattleId = battleId;
			FightPattern = fightPattern;
			MatchUsers = new List<MatchUserInfo>(matchUsers);
			PlayerUids = new List<int>();
			UidToBattlePlayerId = new Dictionary<int, int>();
			int battlePlayerId = 0;
			foreach (MatchUserInfo matchUser in MatchUsers)
			{
				battlePlayerId++;
				PlayerUids.Add(matchUser.uid);
				UidToBattlePlayerId[matchUser.uid] = battlePlayerId;
			}
		}

		public void AttachController(BattleController controller)
		{
			Controller = controller;
		}
	}
}
