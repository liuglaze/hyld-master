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
		private readonly object _scheduledNetSimLock = new object();
		private readonly AutoResetEvent _scheduledNetSimSignal = new AutoResetEvent(false);
		private readonly List<ScheduledNetSimItem> _scheduledNetSimItems = new List<ScheduledNetSimItem>();



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
			public long DueAtTick;
			public Action Execute;
		}

		// ---- NetSim 公共参数（由 BattleController 生命周期写入） ----
		// 战斗期间战斗相关 UDP 包共享同一模拟参数，确保 RTT 与权威帧/上行操作处于同一口径
		public static volatile float SimDropRate = 0f;
		public static volatile int SimDelayMinMs = 0;
		public static volatile int SimDelayMaxMs = 0;
		private static readonly Random _simRandom = new Random();

		public static void ApplyBattleNetSimConfig(float dropRate, int delayMinMs, int delayMaxMs)
		{
			SimDropRate = dropRate;
			SimDelayMinMs = delayMinMs;
			SimDelayMaxMs = delayMaxMs;
		}

		public static void ClearBattleNetSimConfig()
		{
			ApplyBattleNetSimConfig(0f, 0, 0);
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
					return NetSimPacketStrategy.Control;

				case ActionCode.BattleReady:
					return NetSimPacketStrategy.RouteSetup;

				default:
					return NetSimPacketStrategy.None;
			}
		}

		private bool IsBattleNetSimActive()
		{
			return SimDropRate > 0f || SimDelayMinMs > 0 || SimDelayMaxMs > 0;
		}

		private int NextDelayMs()
		{
			if (SimDelayMaxMs <= 0)
			{
				return 0;
			}

			lock (_simRandomLock)
			{
				return _simRandom.Next(SimDelayMinMs, SimDelayMaxMs + 1);
			}
		}

		private bool ShouldDrop(NetSimPacketStrategy strategy)
		{
			if (strategy != NetSimPacketStrategy.Data || SimDropRate <= 0f)
			{
				return false;
			}

			lock (_simRandomLock)
			{
				return _simRandom.NextDouble() < SimDropRate;
			}
		}

		private void LogNetSimDecision(NetSimTrafficDirection direction, ActionCode actionCode, int battleId, string endpoint, NetSimPacketStrategy strategy, string decision, int delayMs = 0)
		{
			Logging.Debug.Log($"[BattleNetSim] dir={direction} action={actionCode} battleId={battleId} endpoint={endpoint ?? "unknown"} strategy={strategy} decision={decision} delayMs={delayMs}");
		}

		private void ScheduleNetSimAction(int delayMs, Action action)
		{
			if (delayMs <= 0)
			{
				action?.Invoke();
				return;
			}

			long dueAtTick = Environment.TickCount64 + delayMs;
			lock (_scheduledNetSimLock)
			{
				_scheduledNetSimItems.Add(new ScheduledNetSimItem
				{
					DueAtTick = dueAtTick,
					Execute = action,
				});
			}
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
						dueItem.Execute?.Invoke();
					}
					catch (Exception ex)
					{
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
			if (direction != NetSimTrafficDirection.Downlink || strategy == NetSimPacketStrategy.None || !IsBattleNetSimActive())
			{
				return false;
			}

			int battleId = TryGetBattleIdForNetSim(pack, new IPEndPoint(IPAddress.Parse(endpoint.Split(':')[0]), int.Parse(endpoint.Split(':')[1])));
			if (ShouldDrop(strategy))
			{
				LogNetSimDecision(direction, pack.Actioncode, battleId, endpoint, strategy, "drop");
				return true;
			}

			int delayMs = NextDelayMs();
			if (delayMs > 0)
			{
				LogNetSimDecision(direction, pack.Actioncode, battleId, endpoint, strategy, "delay", delayMs);
				MainPack delayedPack = pack;
				string delayedEndpoint = endpoint;
				ScheduleNetSimAction(delayMs, () =>
				{
					try
					{
						SendImmediate(delayedPack, delayedEndpoint);
					}
					catch (Exception ex)
					{
						Logging.Debug.Log($"[BattleNetSim] dir={direction} action={delayedPack.Actioncode} endpoint={delayedEndpoint} decision=send_error msg={ex.Message}");
					}
				});
				return true;
			}

			return false;
		}

		private bool TryScheduleBattleNetSim(MainPack pack, EndPoint point, NetSimTrafficDirection direction)
		{
			NetSimPacketStrategy strategy = GetPacketStrategy(pack.Actioncode);
			if (direction != NetSimTrafficDirection.Uplink || strategy == NetSimPacketStrategy.None || !IsBattleNetSimActive())
			{
				return false;
			}

			string endpointKey = GetEndpointKey(point);
			int battleId = TryGetBattleIdForNetSim(pack, point);
			if (ShouldDrop(strategy))
			{
				LogNetSimDecision(direction, pack.Actioncode, battleId, endpointKey, strategy, "drop");
				return true;
			}

			int delayMs = NextDelayMs();
			if (delayMs > 0)
			{
				LogNetSimDecision(direction, pack.Actioncode, battleId, endpointKey, strategy, "delay", delayMs);
				MainPack delayedPack = pack;
				EndPoint delayedPoint = ClonePoint(point);
				ScheduleNetSimAction(delayMs, () =>
				{
					ProcessInboundBattlePacket(delayedPack, delayedPoint);
				});
				return true;
			}

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
					if (pack.BattleInfo == null || pack.BattleInfo.SelfOperation == null)
					{
						return false;
					}
					battlePlayerId = pack.BattleInfo.SelfOperation.Battleid;
					return battlePlayerId > 0;

				case ActionCode.ClientSendGameOver:
					return int.TryParse(pack.Str, out battlePlayerId) && battlePlayerId > 0;

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
					//按了一下射击，不太希望这个射击操作被比如丢包了，或者说是延迟到达了
					//状态帧，然后也会发一些比如受击事件，所有人物操作
					//vibe
					//
					//客户端统一放在一帧里去
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
