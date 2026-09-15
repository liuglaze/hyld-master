using Google.Protobuf;
using SocketProto;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Server
{
	public class LZJUDP
	{
		private Socket server;
		// 多战斗回调字典：battleID -> handler
		private readonly Dictionary<int, Action<MainPack>> _handlers = new Dictionary<int, Action<MainPack>>();
		// BattleReady 建立的 UDP 远端端点路由：ip:port -> routeInfo
		private readonly Dictionary<string, EndpointRouteInfo> _endpointRouteMap = new Dictionary<string, EndpointRouteInfo>();
		private readonly object _handlersLock = new object();
		private readonly object _simRandomLock = new object();
		private readonly object _netSimConfigLock = new object();
		private readonly object _netSimStatsLock = new object();
		private readonly object _scheduledNetSimLock = new object();
		private readonly AutoResetEvent _scheduledNetSimSignal = new AutoResetEvent(false);
		private readonly List<ScheduledNetSimItem> _scheduledNetSimItems = new List<ScheduledNetSimItem>();
		private readonly Dictionary<int, BattleNetSimRuntimeConfig> _battleNetSimConfigs = new Dictionary<int, BattleNetSimRuntimeConfig>();
		private readonly Dictionary<int, BattleNetSimStats> _battleNetSimStats = new Dictionary<int, BattleNetSimStats>();

		private class EndpointRouteInfo
		{
			public int Uid;
			public int BattlePlayerId;
			public int BattleId;
		}

		private enum NetSimTrafficDirection
		{
			Uplink,
			Downlink,
		}

		private enum NetSimPacketStrategy
		{
			None,
			Data,
			Control,
			RouteSetup,
		}

		private sealed class ScheduledNetSimItem
		{
			public int BattleId;
			public NetSimTrafficDirection Direction;
			public long DueAtTick;
			public Action Execute;
		}

		private sealed class BattleNetSimRuntimeConfig
		{
			public float DropRate;
			public int DelayMinMs;
			public int DelayMaxMs;

			public bool IsActive => DropRate > 0f || DelayMinMs > 0 || DelayMaxMs > 0;
		}

		private sealed class BattleNetSimStats
		{
			public long WindowStartTick;
			public int UplinkScheduled;
			public int DownlinkScheduled;
			public int Dropped;
			public int Delayed;
			public int SentImmediate;
			public int SendErrors;
			public int SchedulerErrors;
			public int MaxQueueLength;
			public int MaxLagMs;
		}

		private static readonly Random _simRandom = new Random();

		public static void ApplyBattleNetSimConfig(int battleId, float dropRate, int delayMinMs, int delayMaxMs)
		{
			Instance.ApplyBattleNetSimConfigInternal(battleId, dropRate, delayMinMs, delayMaxMs);
		}

		public static void ClearBattleNetSimConfig(int battleId)
		{
			Instance.ClearBattleNetSimConfigInternal(battleId);
		}

		private static LZJUDP singleInstance;
		private static readonly object padlock = new object();
		public const int SIO_UDP_CONNRESET = -1744830452;

		/// <summary>
		/// 按 battleID 注册战斗回调
		/// </summary>
		public void RegisterBattle(int battleID, Action<MainPack> handler)
		{
			lock (_handlersLock)
			{
				_handlers[battleID] = handler;
			}
			Logging.Debug.Log($"[LZJUDP] RegisterBattle: battleID={battleID}");
		}

		/// <summary>
		/// 按 battleID 注销战斗回调
		/// </summary>
		public void UnregisterBattle(int battleID)
		{
			lock (_handlersLock)
			{
				_handlers.Remove(battleID);
				List<string> endpointKeys = new List<string>();
				foreach (var item in _endpointRouteMap)
				{
					if (item.Value.BattleId == battleID)
					{
						endpointKeys.Add(item.Key);
					}
				}
				foreach (string endpointKey in endpointKeys)
				{
					_endpointRouteMap.Remove(endpointKey);
				}
			}
			ClearBattleNetSimConfigInternal(battleID);
			Logging.Debug.Log($"[LZJUDP] UnregisterBattle: battleID={battleID}");
		}

		public static LZJUDP Instance
		{
			get
			{
				lock (padlock)
				{
					if (singleInstance == null)
					{
						singleInstance = new LZJUDP();
					}
					return singleInstance;
				}
			}
		}

		private LZJUDP()
		{
			server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(IPManager.GetIP(ADDRESSFAM.IPv4)), ServerConfig.UDPservePort);
			server.Bind(endPoint);
			Logging.Debug.Log("启动udp:" + endPoint);

			server.IOControl(
			(IOControlCode)SIO_UDP_CONNRESET,
			new byte[] { 0, 0, 0, 0 },
			null
			);
			// 启动接收线程
			(new Thread(RecvThread) { IsBackground = true }).Start();
			// 启动统一的 NetSim 延迟调度线程，避免每个包占用一个 ThreadPool + Sleep。
			(new Thread(NetSimSchedulerLoop) { IsBackground = true }).Start();
		}

		public void Init()
		{
		}

		public void Send(MainPack pack, string ip_port)
		{
			if (TryScheduleBattleNetSim(pack, ip_port, NetSimTrafficDirection.Downlink))
			{
				return;
			}

			SendImmediate(pack, ip_port);
		}

		private void SendImmediate(MainPack pack, string ip_port)
		{
			string[] ipport = ip_port.Split(":");
			EndPoint point = new IPEndPoint(IPAddress.Parse(ipport[0]), int.Parse(ipport[1]));
			byte[] sendbuff = pack.ToByteArray();
			server.SendTo(sendbuff, point);
		}

		/// <summary>
		/// 统一格式化 UDP 远端端点，作为后续消息路由键
		/// </summary>
		private string GetEndpointKey(EndPoint point)
		{
			IPEndPoint ipEndPoint = point as IPEndPoint;
			if (ipEndPoint != null)
			{
				return ipEndPoint.Address + ":" + ipEndPoint.Port;
			}
			return point?.ToString();
		}

		private EndPoint ClonePoint(EndPoint point)
		{
			IPEndPoint ipEndPoint = point as IPEndPoint;
			if (ipEndPoint == null)
			{
				return point;
			}

			return new IPEndPoint(ipEndPoint.Address, ipEndPoint.Port);
		}

		private NetSimPacketStrategy GetPacketStrategy(ActionCode actionCode)
		{
			switch (actionCode)
			{
				case ActionCode.BattlePushDowmPlayerOpeartions:
				case ActionCode.BattlePushDowmAllFrameOpeartions:
					return NetSimPacketStrategy.Data;

				case ActionCode.Ping:
				case ActionCode.Pong:
				case ActionCode.BattleStart:
				case ActionCode.ClientSendGameOver:
				case ActionCode.BattlePushDowmGameOver:
				case ActionCode.BattleSetNetSimConfig:
					return NetSimPacketStrategy.Control;

				case ActionCode.BattleReady:
					return NetSimPacketStrategy.RouteSetup;

				default:
					return NetSimPacketStrategy.None;
			}
		}

		private void ApplyBattleNetSimConfigInternal(int battleId, float dropRate, int delayMinMs, int delayMaxMs)
		{
			if (battleId <= 0)
			{
				return;
			}

			lock (_netSimConfigLock)
			{
				_battleNetSimConfigs[battleId] = new BattleNetSimRuntimeConfig
				{
					DropRate = dropRate,
					DelayMinMs = delayMinMs,
					DelayMaxMs = delayMaxMs,
				};
			}
		}

		private void ClearBattleNetSimConfigInternal(int battleId)
		{
			if (battleId <= 0)
			{
				return;
			}

			lock (_netSimConfigLock)
			{
				_battleNetSimConfigs.Remove(battleId);
			}
			lock (_netSimStatsLock)
			{
				_battleNetSimStats.Remove(battleId);
			}
		}

		private bool TryGetBattleNetSimConfig(int battleId, out BattleNetSimRuntimeConfig config)
		{
			config = null;
			if (battleId <= 0)
			{
				return false;
			}
			lock (_netSimConfigLock)
			{
				return _battleNetSimConfigs.TryGetValue(battleId, out config) && config != null && config.IsActive;
			}
		}

		private int NextDelayMs(BattleNetSimRuntimeConfig config)
		{
			if (config == null || config.DelayMaxMs <= 0)
			{
				return 0;
			}

			lock (_simRandomLock)
			{
				return _simRandom.Next(config.DelayMinMs, config.DelayMaxMs + 1);
			}
		}

		private bool ShouldDrop(NetSimPacketStrategy strategy, BattleNetSimRuntimeConfig config)
		{
			if (strategy != NetSimPacketStrategy.Data || config == null || config.DropRate <= 0f)
			{
				return false;
			}

			lock (_simRandomLock)
			{
				return _simRandom.NextDouble() < config.DropRate;
			}
		}

		private BattleNetSimStats GetNetSimStatsLocked(int battleId)
		{
			if (!_battleNetSimStats.TryGetValue(battleId, out BattleNetSimStats stats))
			{
				stats = new BattleNetSimStats
				{
					WindowStartTick = Environment.TickCount64,
				};
				_battleNetSimStats[battleId] = stats;
			}
			return stats;
		}

		private void RecordNetSimDecision(int battleId, NetSimTrafficDirection direction, string decision, int queueLength = 0, int lagMs = 0)
		{
			if (battleId <= 0)
			{
				return;
			}

			lock (_netSimStatsLock)
			{
				BattleNetSimStats stats = GetNetSimStatsLocked(battleId);
				if (direction == NetSimTrafficDirection.Uplink)
				{
					stats.UplinkScheduled++;
				}
				else
				{
					stats.DownlinkScheduled++;
				}

				switch (decision)
				{
					case "drop":
						stats.Dropped++;
						break;
					case "delay":
						stats.Delayed++;
						break;
					case "send_now":
						stats.SentImmediate++;
						break;
					case "send_error":
						stats.SendErrors++;
						break;
					case "scheduler_error":
						stats.SchedulerErrors++;
						break;
				}

				if (queueLength > stats.MaxQueueLength)
				{
					stats.MaxQueueLength = queueLength;
				}
				if (lagMs > stats.MaxLagMs)
				{
					stats.MaxLagMs = lagMs;
				}

				FlushNetSimStatsIfDueLocked(battleId, stats);
			}
		}

		private void RecordNetSimSchedulerLag(int battleId, int lagMs)
		{
			if (battleId <= 0 || lagMs <= 0)
			{
				return;
			}

			lock (_netSimStatsLock)
			{
				BattleNetSimStats stats = GetNetSimStatsLocked(battleId);
				if (lagMs > stats.MaxLagMs)
				{
					stats.MaxLagMs = lagMs;
				}
				FlushNetSimStatsIfDueLocked(battleId, stats);
			}
		}

		private void RecordNetSimSchedulerError(int battleId)
		{
			if (battleId <= 0)
			{
				return;
			}

			lock (_netSimStatsLock)
			{
				BattleNetSimStats stats = GetNetSimStatsLocked(battleId);
				stats.SchedulerErrors++;
				FlushNetSimStatsIfDueLocked(battleId, stats);
			}
		}

		private void RecordNetSimSendError(int battleId)
		{
			if (battleId <= 0)
			{
				return;
			}

			lock (_netSimStatsLock)
			{
				BattleNetSimStats stats = GetNetSimStatsLocked(battleId);
				stats.SendErrors++;
				FlushNetSimStatsIfDueLocked(battleId, stats);
			}
		}

		private void FlushNetSimStatsIfDueLocked(int battleId, BattleNetSimStats stats)
		{
			long nowTick = Environment.TickCount64;
			if (nowTick - stats.WindowStartTick < 1000)
			{
				return;
			}

			Logging.Debug.Log($"[BattleNetSim][Stats] battleId={battleId} up={stats.UplinkScheduled} down={stats.DownlinkScheduled} drop={stats.Dropped} delay={stats.Delayed} sendNow={stats.SentImmediate} sendErr={stats.SendErrors} schedulerErr={stats.SchedulerErrors} maxQueue={stats.MaxQueueLength} maxLagMs={stats.MaxLagMs}");
			stats.WindowStartTick = nowTick;
			stats.UplinkScheduled = 0;
			stats.DownlinkScheduled = 0;
			stats.Dropped = 0;
			stats.Delayed = 0;
			stats.SentImmediate = 0;
			stats.SendErrors = 0;
			stats.SchedulerErrors = 0;
			stats.MaxQueueLength = 0;
			stats.MaxLagMs = 0;
		}

		private void ScheduleNetSimAction(int battleId, NetSimTrafficDirection direction, int delayMs, Action action)
		{
			if (delayMs <= 0)
			{
				action?.Invoke();
				return;
			}

			long dueAtTick = Environment.TickCount64 + delayMs;
			int queueLength;
			lock (_scheduledNetSimLock)
			{
				_scheduledNetSimItems.Add(new ScheduledNetSimItem
				{
					BattleId = battleId,
					Direction = direction,
					DueAtTick = dueAtTick,
					Execute = action,
				});
				queueLength = _scheduledNetSimItems.Count;
			}
			RecordNetSimDecision(battleId, direction, "delay", queueLength);
			_scheduledNetSimSignal.Set();
		}

		private void NetSimSchedulerLoop()
		{
			while (true)
			{
				ScheduledNetSimItem dueItem = null;
				int waitMs = Timeout.Infinite;

				lock (_scheduledNetSimLock)
				{
					if (_scheduledNetSimItems.Count > 0)
					{
						_scheduledNetSimItems.Sort((a, b) => a.DueAtTick.CompareTo(b.DueAtTick));
						long nowTick = Environment.TickCount64;
						ScheduledNetSimItem first = _scheduledNetSimItems[0];
						if (first.DueAtTick <= nowTick)
						{
							dueItem = first;
							_scheduledNetSimItems.RemoveAt(0);
						}
						else
						{
							long delta = first.DueAtTick - nowTick;
							waitMs = delta > int.MaxValue ? int.MaxValue : (int)delta;
						}
					}
				}

				if (dueItem != null)
				{
					try
					{
						int lagMs = (int)Math.Max(0, Environment.TickCount64 - dueItem.DueAtTick);
						RecordNetSimSchedulerLag(dueItem.BattleId, lagMs);
						dueItem.Execute?.Invoke();
					}
					catch (Exception ex)
					{
						RecordNetSimSchedulerError(dueItem.BattleId);
						Logging.Debug.Log($"[BattleNetSim] scheduler_execute_error msg={ex.Message}");
					}
					continue;
				}

				_scheduledNetSimSignal.WaitOne(waitMs);
			}
		}

		private int TryGetBattleIdForNetSim(MainPack pack, EndPoint point)
		{
			try
			{
				switch (pack.Actioncode)
				{
					case ActionCode.BattleReady:
						if (pack.Battleplayerpack != null && pack.Battleplayerpack.Count > 0)
						{
							int uid = pack.Battleplayerpack[0].Id;
							if (uid > 0 && BattleManage.Instance.TryGetBattleIDByUID(uid, out int readyBattleId))
							{
								return readyBattleId;
							}
						}
						break;

					case ActionCode.BattlePushDowmPlayerOpeartions:
					case ActionCode.ClientSendGameOver:
					case ActionCode.Ping:
					case ActionCode.Pong:
					case ActionCode.BattleStart:
					case ActionCode.BattlePushDowmAllFrameOpeartions:
					case ActionCode.BattlePushDowmGameOver:
					case ActionCode.BattleSetNetSimConfig:
						string endpointKey = GetEndpointKey(point);
						if (!string.IsNullOrEmpty(endpointKey))
						{
							lock (_handlersLock)
							{
								if (_endpointRouteMap.TryGetValue(endpointKey, out EndpointRouteInfo routeInfo))
								{
									return routeInfo.BattleId;
								}
							}
						}
						break;
				}
			}
			catch
			{
			}

			return -1;
		}

		private bool TryScheduleBattleNetSim(MainPack pack, string endpoint, NetSimTrafficDirection direction)
		{
			NetSimPacketStrategy strategy = GetPacketStrategy(pack.Actioncode);
			if (direction != NetSimTrafficDirection.Downlink || strategy == NetSimPacketStrategy.None)
			{
				return false;
			}

			int battleId = TryGetBattleIdForNetSim(pack, new IPEndPoint(IPAddress.Parse(endpoint.Split(':')[0]), int.Parse(endpoint.Split(':')[1])));
			if (!TryGetBattleNetSimConfig(battleId, out BattleNetSimRuntimeConfig config))
			{
				return false;
			}

			if (ShouldDrop(strategy, config))
			{
				RecordNetSimDecision(battleId, direction, "drop");
				return true;
			}

			int delayMs = NextDelayMs(config);
			if (delayMs > 0)
			{
				MainPack delayedPack = pack;
				string delayedEndpoint = endpoint;
				int delayedBattleId = battleId;
				ScheduleNetSimAction(battleId, direction, delayMs, () =>
				{
					try
					{
						SendImmediate(delayedPack, delayedEndpoint);
					}
					catch (Exception ex)
					{
						RecordNetSimSendError(delayedBattleId);
						Logging.Debug.Log($"[BattleNetSim] dir={direction} action={delayedPack.Actioncode} endpoint={delayedEndpoint} decision=send_error msg={ex.Message}");
					}
				});
				return true;
			}

			RecordNetSimDecision(battleId, direction, "send_now");
			return false;
		}

		private bool TryScheduleBattleNetSim(MainPack pack, EndPoint point, NetSimTrafficDirection direction)
		{
			NetSimPacketStrategy strategy = GetPacketStrategy(pack.Actioncode);
			if (direction != NetSimTrafficDirection.Uplink || strategy == NetSimPacketStrategy.None)
			{
				return false;
			}

			string endpointKey = GetEndpointKey(point);
			int battleId = TryGetBattleIdForNetSim(pack, point);
			if (!TryGetBattleNetSimConfig(battleId, out BattleNetSimRuntimeConfig config))
			{
				return false;
			}

			if (ShouldDrop(strategy, config))
			{
				RecordNetSimDecision(battleId, direction, "drop");
				return true;
			}

			int delayMs = NextDelayMs(config);
			if (delayMs > 0)
			{
				MainPack delayedPack = pack;
				EndPoint delayedPoint = ClonePoint(point);
				ScheduleNetSimAction(battleId, direction, delayMs, () =>
				{
					ProcessInboundBattlePacket(delayedPack, delayedPoint);
				});
				return true;
			}

			RecordNetSimDecision(battleId, direction, "send_now");
			return false;
		}

		/// <summary>
		/// 按 ActionCode 从包内提取 battlePlayerId（战斗内玩家ID）
		/// </summary>
		private bool TryParseBattlePlayerId(MainPack pack, out int battlePlayerId)
		{
			battlePlayerId = -1;
			switch (pack.Actioncode)
			{
				case ActionCode.BattleReady:
					if (pack.Battleplayerpack == null || pack.Battleplayerpack.Count == 0)
					{
						return false;
					}
					battlePlayerId = pack.Battleplayerpack[0].Battleid;
					return battlePlayerId > 0;

				case ActionCode.BattlePushDowmPlayerOpeartions:
					if (pack.BattleInfo == null || pack.BattleInfo.ClientInput == null)
					{
						return false;
					}
					battlePlayerId = pack.BattleInfo.ClientInput.BattlePlayerId;
					return battlePlayerId > 0;

				case ActionCode.ClientSendGameOver:
					return int.TryParse(pack.Str, out battlePlayerId) && battlePlayerId > 0;

				case ActionCode.BattleSetNetSimConfig:
					if (pack.BattleNetSimConfig == null)
					{
						return false;
					}
					battlePlayerId = pack.BattleNetSimConfig.BattlePlayerId;
					return battlePlayerId > 0;

				default:
					return false;
			}
		}

		/// <summary>
		/// 按 ActionCode 路由：
		/// 1) BattleReady 用 uid 反查 battleId 并建立 endpoint -> (battleId,battlePlayerId) 映射
		/// 2) 其余战斗包通过 endpoint 映射并校验 battlePlayerId 一致性
		/// </summary>
		private bool TryResolveBattleID(MainPack pack, EndPoint point, out int battleID)
		{
			battleID = -1;
			string endpointKey = GetEndpointKey(point);
			try
			{
				if (!TryParseBattlePlayerId(pack, out int battlePlayerId))
				{
					return false;
				}

				switch (pack.Actioncode)
				{
					case ActionCode.BattleReady:
						if (pack.Battleplayerpack == null || pack.Battleplayerpack.Count == 0)
						{
							return false;
						}

						int uid = pack.Battleplayerpack[0].Id;
						if (uid <= 0 || !BattleManage.Instance.TryGetBattleIDByUID(uid, out battleID))
						{
							return false;
						}
						if (!BattleManage.Instance.TryGetBattlePlayerId(uid, out int expectedBattlePlayerId))
						{
							Logging.Debug.Log($"[LZJUDP] BattleReady 未找到 battlePlayerId, uid={uid}, battleID={battleID}");
							return false;
						}
						if (expectedBattlePlayerId != battlePlayerId)
						{
							Logging.Debug.Log($"[LZJUDP] BattleReady battlePlayerId 不匹配: uid={uid}, battleID={battleID}, expected={expectedBattlePlayerId}, actual={battlePlayerId}");
							return false;
						}

						if (!string.IsNullOrEmpty(endpointKey))
						{
							lock (_handlersLock)
							{
								_endpointRouteMap[endpointKey] = new EndpointRouteInfo
								{
									Uid = uid,
									BattlePlayerId = expectedBattlePlayerId,
									BattleId = battleID,
								};
							}
							// 使用服务端观测到的真实远端地址，避免依赖客户端自报地址
							pack.Str = endpointKey;
						}
						return true;

					case ActionCode.BattlePushDowmPlayerOpeartions:
					case ActionCode.ClientSendGameOver:
					case ActionCode.BattleSetNetSimConfig:
						if (string.IsNullOrEmpty(endpointKey))
						{
							return false;
						}
						lock (_handlersLock)
						{
							if (!_endpointRouteMap.TryGetValue(endpointKey, out EndpointRouteInfo routeInfo))
							{
								return false;
							}
							if (routeInfo.BattlePlayerId != battlePlayerId)
							{
								Logging.Debug.Log($"[LZJUDP] battlePlayerId 不匹配: endpoint={endpointKey}, expected={routeInfo.BattlePlayerId}, actual={battlePlayerId}");
								return false;
							}
							battleID = routeInfo.BattleId;
							return battleID > 0;
						}

					default:
						return false;
				}
			}
			catch (Exception ex)
			{
				Logging.Debug.Log($"[LZJUDP] TryResolveBattleID 异常: {ex.Message}");
				return false;
			}
		}

		private void ProcessInboundBattlePacket(MainPack pack, EndPoint point)
		{
			string endpointKey = GetEndpointKey(point);

			if (pack.Actioncode == ActionCode.Ping)
			{
				bool hasRoute;
				lock (_handlersLock)
				{
					hasRoute = !string.IsNullOrEmpty(endpointKey) && _endpointRouteMap.ContainsKey(endpointKey);
				}
				if (hasRoute)
				{
					MainPack pong = new MainPack();
					pong.Actioncode = ActionCode.Pong;
					pong.Timestamp = pack.Timestamp;
					Send(pong, endpointKey);
				}
				return;
			}

			if (!TryResolveBattleID(pack, point, out int battleID))
			{
				Logging.Debug.Log($"[LZJUDP] 无法路由 UDP 包: ActionCode={pack.Actioncode}, Endpoint={endpointKey}，已丢弃");
				return;
			}

			Action<MainPack> handler = null;
			lock (_handlersLock)
			{
				_handlers.TryGetValue(battleID, out handler);
			}

			if (handler != null)
			{
				handler.Invoke(pack);
			}
			else
			{
				Logging.Debug.Log($"[LZJUDP] battleID={battleID} 无已注册的 handler，丢弃包");
			}
		}

		private void RecvThread()
		{
			EndPoint point = new IPEndPoint(IPAddress.Any, 0);
			while (true)
			{
				try
				{
					byte[] bytes = new byte[1024];
					int length = server.ReceiveFrom(bytes, ref point);
					if (length == 0)
					{
						continue;
					}

					MainPack pack = (MainPack)MainPack.Descriptor.Parser.ParseFrom(bytes, 0, length);
					EndPoint currentPoint = ClonePoint(point);

					if (TryScheduleBattleNetSim(pack, currentPoint, NetSimTrafficDirection.Uplink))
					{
						continue;
					}

					ProcessInboundBattlePacket(pack, currentPoint);
				}
				catch (Exception ex)
				{
					Logging.Debug.Log(point + ":::udpClient接收数据异常:  " + ex.Message);
				}
			}
		}
	}
}
