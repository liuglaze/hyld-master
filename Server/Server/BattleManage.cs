using Server.Controller;
using SocketProto;
using System;
using System.Collections.Generic;

namespace Server
{
	/// <summary>
	/// 用于开启战场的单例管理
	/// </summary>
	class BattleManage
	{
		private readonly object _manageLock = new object();
		private int _nextBattleId;
		private readonly Dictionary<int, BattleContext> _battleContexts;
		private readonly Dictionary<int, int> _uidToBattleIds;
		private static BattleManage instance = null;
		private Server server;
		public static BattleManage Instance
		{
			get
			{
				if (instance == null)
				{
					instance = new BattleManage();
				}
				return instance;
			}
		}

		private BattleManage()
		{
			_nextBattleId = 0;
			_battleContexts = new Dictionary<int, BattleContext>();
			_uidToBattleIds = new Dictionary<int, int>();
		}

		public bool TryGetBattleIDByUID(int uid, out int foundBattleID)
		{
			lock (_manageLock)
			{
				return _uidToBattleIds.TryGetValue(uid, out foundBattleID);
			}
		}

		public bool TryGetBattleContext(int battleId, out BattleContext battleContext)
		{
			lock (_manageLock)
			{
				return _battleContexts.TryGetValue(battleId, out battleContext);
			}
		}

		public bool TryGetBattleContextByUid(int uid, out BattleContext battleContext)
		{
			lock (_manageLock)
			{
				battleContext = null;
				if (!_uidToBattleIds.TryGetValue(uid, out int battleId))
				{
					return false;
				}
				return _battleContexts.TryGetValue(battleId, out battleContext);
			}
		}

		public bool IsUserInBattle(int uid)
		{
			lock (_manageLock)
			{
				return _uidToBattleIds.ContainsKey(uid);
			}
		}

		public bool TryGetController(int battleId, out BattleController battleController)
		{
			lock (_manageLock)
			{
				battleController = null;
				if (!_battleContexts.TryGetValue(battleId, out BattleContext battleContext) || battleContext.Controller == null)
				{
					return false;
				}
				battleController = battleContext.Controller;
				return true;
			}
		}

		public bool TryGetBattlePlayerId(int uid, out int battlePlayerId)
		{
			lock (_manageLock)
			{
				battlePlayerId = 0;
				if (!_uidToBattleIds.TryGetValue(uid, out int battleId))
				{
					return false;
				}
				if (!_battleContexts.TryGetValue(battleId, out BattleContext battleContext))
				{
					return false;
				}
				return battleContext.UidToBattlePlayerId.TryGetValue(uid, out battlePlayerId);
			}
		}

		public bool TryBeginBattle(Server server, List<MatchUserInfo> battleUsers, MatchingController.FightPattern fightPattern, out int battleId)
		{
			battleId = 0;
			if (battleUsers == null || battleUsers.Count == 0)
			{
				return false;
			}
			if (this.server == null) this.server = server;
			BattleContext battleContext;
			lock (_manageLock)
			{
				foreach (MatchUserInfo battleUser in battleUsers)
				{
					if (_uidToBattleIds.ContainsKey(battleUser.uid))
					{
						Logging.Debug.Log($"TryBeginBattle 用户已在战斗中，uid={battleUser.uid}");
						return false;
					}
				}

				_nextBattleId++;
				battleContext = new BattleContext(_nextBattleId, fightPattern, battleUsers);
				_battleContexts.Add(battleContext.BattleId, battleContext);
				foreach (int uid in battleContext.PlayerUids)
				{
					_uidToBattleIds[uid] = battleContext.BattleId;
				}
				battleId = battleContext.BattleId;
			}

			try
			{
				BattleController battleController = new BattleController(server, battleContext);
				lock (_manageLock)
				{
					if (_battleContexts.TryGetValue(battleContext.BattleId, out BattleContext activeContext))
					{
						activeContext.AttachController(battleController);
					}
				}
				Logging.Debug.Log("开始战斗。。。。。BattleID：" + battleContext.BattleId);
				return true;
			}
			catch (Exception ex)
			{
				Logging.Debug.Log($"TryBeginBattle 创建 BattleController 失败，BattleID={battleContext.BattleId}, ex={ex}");
				lock (_manageLock)
				{
					_battleContexts.Remove(battleContext.BattleId);
					foreach (int uid in battleContext.PlayerUids)
					{
						if (_uidToBattleIds.TryGetValue(uid, out int activeBattleId) && activeBattleId == battleContext.BattleId)
						{
							_uidToBattleIds.Remove(uid);
						}
					}
				}
				battleId = 0;
				return false;
			}
		}

		public void HandleClientDisconnect(Server server, int uid)
		{
			if (uid <= 0)
			{
				return;
			}

			BattleContext battleContext;
			lock (_manageLock)
			{
				if (!_uidToBattleIds.TryGetValue(uid, out int battleId) || !_battleContexts.TryGetValue(battleId, out battleContext))
				{
					return;
				}
			}

			Logging.Debug.Log($"HandleClientDisconnect 检测到战斗中玩家断线，uid={uid}, battleId={battleContext.BattleId}");
			if (TryGetController(battleContext.BattleId, out BattleController battleController))
			{
				battleController.HandlePlayerDisconnect(uid);
			}
		}

		public void FinishBattle(int battleId, Dictionary<int, AllPlayerOperation> frameHistory)
		{
			BattleContext battleContext;
			lock (_manageLock)
			{
				if (!_battleContexts.TryGetValue(battleId, out battleContext))
				{
					Logging.Debug.Log($"FinishBattle 未找到完整战斗数据，BattleID={battleId}");
					return;
				}
				_battleContexts.Remove(battleId);
				foreach (int uid in battleContext.PlayerUids)
				{
					_uidToBattleIds.Remove(uid);
				}
			}

			MainPack mainPack = new MainPack();
			mainPack.Actioncode = ActionCode.BattleReview;
			BattleInfo battleInfo = new BattleInfo();
			foreach (MatchUserInfo matchUser in battleContext.MatchUsers)
			{
				BattlePlayerPack battleUser = new BattlePlayerPack();
				battleUser.Id = matchUser.uid;
				battleUser.Battleid = battleContext.UidToBattlePlayerId[matchUser.uid];
				battleUser.Playername = matchUser.userName;
				battleUser.Hero = matchUser.hero;
				battleUser.Teamid = matchUser.teamid;
				battleInfo.BattleUserInfo.Add(battleUser);
			}

			foreach (AllPlayerOperation allPlayerOperation in frameHistory.Values)
			{
				battleInfo.AllPlayerOperation.Add(allPlayerOperation);
			}

			mainPack.Str = ((int)battleContext.FightPattern).ToString();
			mainPack.BattleInfo = battleInfo;
			Console.WriteLine(mainPack);
			foreach (int uid in battleContext.PlayerUids)
			{
				Client activeClient = server.GetClientByID(uid);
				if (activeClient == null)
				{
					continue;
				}
				activeClient.Send(mainPack);
			}

			Logging.Debug.Log("战斗结束。。。。。BattleID：" + battleId);
		}
	}
}
