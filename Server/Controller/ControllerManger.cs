using System;
using System.Collections.Generic;
using SocketProto;

namespace Server.Controller
{
    /// <summary>
    /// RPC 处理函数签名。与既有控制器方法保持一致：三件套入参 + MainPack 返回。
    /// 用委托而不是反射字符串，签名由编译器校验（见 P1 说明）。
    /// </summary>
    internal delegate MainPack PmRpcHandler(Server server, Client client, MainPack pack);

    /// <summary>一条已注册的 RPC 处理项。</summary>
    internal sealed class PmRpcEntry
    {
        public RequestCode RequestCode;
        public ActionCode ActionCode;

        /// <summary>处理函数的可读名字（诊断用，例如 "UserController.Login"）。</summary>
        public string HandlerName;

        public PmRpcHandler Handler;
    }

    /// <summary>
    /// 请求路由。
    ///
    /// 原实现按 <c>ActionCode.ToString()</c> 反射查找控制器上的同名方法：
    ///   _controllerDic[Requestcode] → GetType().GetMethod(Actioncode.ToString()) → Invoke(...)
    /// 该方式没有签名校验，实际已经造成四种故障（计划 B1）：
    ///   1. ActionCode.AllClearSenceReady 命中 void AllClearSenceReady(Server, List&lt;int&gt;)，Invoke 抛异常 → 连接被服务端断开；
    ///   2. RequestCode.FriendRoom + ActionCode.ChangeHero 命中 void ChangeHero(Client, MainPack)，同类崩溃；
    ///   3. ActionCode.RejectInvateFriend 与服务端方法名 RejectInviteFriend 拼写不一致 → 请求被静默丢弃；
    ///   4. ActionCode.CancalInvateFriend 与服务端方法名/参数均不一致 → 请求被静默丢弃。
    ///
    /// 现在改为「启动时显式注册 (RequestCode, ActionCode) → 委托」，四个故障同时消除：
    /// 名字不再参与路由，签名在编译期校验，未注册组合会带上下文记日志而不是静默丢弃。
    /// </summary>
    class ControllerManger
    {
        private readonly Server _server;

        /// <summary>请求码 → 控制器实例。保留用途：断开连接时广播 CloseClient。</summary>
        private readonly Dictionary<RequestCode, BaseControllers> _controllerDic = new Dictionary<RequestCode, BaseControllers>();

        /// <summary>控制器类型名 → 实例。供控制器之间互相直调（如 UserController.ChangeHero → FriendRoomController）。</summary>
        private readonly Dictionary<string, BaseControllers> _controllerNameDic = new Dictionary<string, BaseControllers>();

        /// <summary>RequestCode → (ActionCode → 处理项)。这是路由的唯一事实源。</summary>
        private readonly Dictionary<RequestCode, Dictionary<ActionCode, PmRpcEntry>> _rpcTable =
            new Dictionary<RequestCode, Dictionary<ActionCode, PmRpcEntry>>();

        /// <summary>全部已注册处理项，按注册顺序，便于启动时打印与外部核对。</summary>
        private readonly List<PmRpcEntry> _entries = new List<PmRpcEntry>();

        public ControllerManger(Server server)
        {
            _server = server;

            UserController userController = new UserController();
            FriendController friendController = new FriendController();
            FriendRoomController friendRoomController = new FriendRoomController();
            PingPongController pingPongController = new PingPongController();
            MatchingController matchingController = new MatchingController();
            ClearSenceController clearSenceController = new ClearSenceController();

            _controllerDic.Add(friendRoomController.GetRequestCode, friendRoomController);
            _controllerDic.Add(userController.GetRequestCode, userController);
            _controllerDic.Add(friendController.GetRequestCode, friendController);
            _controllerDic.Add(pingPongController.GetRequestCode, pingPongController);
            _controllerDic.Add(matchingController.GetRequestCode, matchingController);
            _controllerDic.Add(clearSenceController.GetRequestCode, clearSenceController);

            _controllerNameDic.Add(nameof(UserController), userController);
            _controllerNameDic.Add(nameof(FriendController), friendController);
            _controllerNameDic.Add(nameof(FriendRoomController), friendRoomController);
            _controllerNameDic.Add(nameof(PingPongController), pingPongController);
            _controllerNameDic.Add(nameof(MatchingController), matchingController);
            _controllerNameDic.Add(nameof(ClearSenceController), clearSenceController);

            RegisterAll(userController, friendController, friendRoomController, pingPongController, matchingController, clearSenceController);
        }

        /// <summary>已注册的 RPC 处理函数数量（覆盖性核对用）。</summary>
        public int RegisteredHandlerCount
        {
            get { return _entries.Count; }
        }

        public BaseControllers GetControllerByName(string name)
        {
            BaseControllers controller;
            if (_controllerNameDic.TryGetValue(name, out controller))
            {
                return controller;
            }

            return null;
        }

        // ---------------- 注册 ----------------

        private void RegisterAll(
            UserController user,
            FriendController friend,
            FriendRoomController friendRoom,
            PingPongController pingPong,
            MatchingController matching,
            ClearSenceController clearSence)
        {
            // User
            Register(RequestCode.User, ActionCode.Logon, user.Logon);
            Register(RequestCode.User, ActionCode.Login, user.Login);
            Register(RequestCode.User, ActionCode.FindPlayerInfo, user.FindPlayerInfo);
            Register(RequestCode.User, ActionCode.FindFriendsInfo, user.FindFriendsInfo);
            Register(RequestCode.User, ActionCode.UpdateName, user.UpdateName);
            Register(RequestCode.User, ActionCode.ChangeHero, user.ChangeHero);

            // Friend
            Register(RequestCode.Friend, ActionCode.AplyAddFriend, friend.AplyAddFriend);
            Register(RequestCode.Friend, ActionCode.AcceptAddFriend, friend.AcceptAddFriend);
            Register(RequestCode.Friend, ActionCode.RejectAddFriend, friend.RejectAddFriend);

            // FriendRoom
            Register(RequestCode.FriendRoom, ActionCode.CreateRoom, friendRoom.CreateRoom);
            Register(RequestCode.FriendRoom, ActionCode.InviteFriend, friendRoom.InviteFriend);
            Register(RequestCode.FriendRoom, ActionCode.AcceptInvateFriend, friendRoom.AcceptInvateFriend);
            // 下面两条修掉「方法名与 ActionCode 不一致导致静默丢弃」：
            //   RejectInvateFriend(20) ↔ 方法 RejectInviteFriend
            //   CancalInvateFriend(21) ↔ 方法 CancelInviteFriend
            Register(RequestCode.FriendRoom, ActionCode.RejectInvateFriend, friendRoom.RejectInviteFriend);
            Register(RequestCode.FriendRoom, ActionCode.CancalInvateFriend, friendRoom.CancelInviteFriend);
            Register(RequestCode.FriendRoom, ActionCode.ExitRoom, friendRoom.ExitRoom);
            // Chat 原为 void（不回包），这里显式表达「处理但不回包」。
            RegisterVoid(RequestCode.FriendRoom, ActionCode.Chat, friendRoom.Chat);

            // Matching
            Register(RequestCode.Matching, ActionCode.AddMatchingPlayer, matching.AddMatchingPlayer);
            Register(RequestCode.Matching, ActionCode.RemoveMatchingPlayer, matching.RemoveMatchingPlayer);

            // PingPong
            Register(RequestCode.PingPong, ActionCode.Ping, pingPong.Ping);

            // ClearSence
            Register(RequestCode.ClearSence, ActionCode.ClientSendClearSenceReady, clearSence.ClientSendClearSenceReady);
        }

        private void Register(RequestCode requestCode, ActionCode actionCode, PmRpcHandler handler)
        {
            AddEntry(requestCode, actionCode, handler, handler != null ? handler.Method.DeclaringType.Name + "." + handler.Method.Name : "(null)");
        }

        /// <summary>注册一个不回包的处理器（显式表达 void 语义，避免用 lambda 掩盖意图）。</summary>
        private void RegisterVoid(RequestCode requestCode, ActionCode actionCode, Action<Server, Client, MainPack> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            AddEntry(requestCode, actionCode, delegate (Server s, Client c, MainPack p)
            {
                handler(s, c, p);
                return null;
            }, handler.Method.DeclaringType.Name + "." + handler.Method.Name);
        }

        private void AddEntry(RequestCode requestCode, ActionCode actionCode, PmRpcHandler handler, string handlerName)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            Dictionary<ActionCode, PmRpcEntry> byAction;
            if (!_rpcTable.TryGetValue(requestCode, out byAction))
            {
                byAction = new Dictionary<ActionCode, PmRpcEntry>();
                _rpcTable.Add(requestCode, byAction);
            }

            if (byAction.ContainsKey(actionCode))
            {
                // 重复注册会让路由变得不确定，必须在启动时就暴露，而不是等到线上出现「有时不生效」。
                throw new InvalidOperationException(
                    $"[RPC][注册冲突] request={requestCode} action={actionCode} 被重复注册");
            }

            PmRpcEntry entry = new PmRpcEntry();
            entry.RequestCode = requestCode;
            entry.ActionCode = actionCode;
            entry.Handler = handler;
            entry.HandlerName = handlerName;

            byAction.Add(actionCode, entry);
            _entries.Add(entry);
        }

        /// <summary>打印注册清单。启动时调用一次，便于把「服务端实际支持哪些入口」固化到日志。</summary>
        public void LogRegisteredHandlers()
        {
            Logging.Debug.Log($"[RPC][注册清单] 共 {_entries.Count} 个处理函数");
            for (int i = 0; i < _entries.Count; i++)
            {
                PmRpcEntry e = _entries[i];
                Logging.Debug.Log($"[RPC][注册清单]   {e.RequestCode} + {e.ActionCode} -> {e.HandlerName}");
            }
        }

        // ---------------- 路由 ----------------

        public void CloseClient(Client client, int id)
        {
            foreach (BaseControllers controllers in _controllerDic.Values)
            {
                controllers.CloseClient(client, id);
            }
        }

        public void HandleRequest(MainPack pack, Client client)
        {
            if (pack == null)
            {
                return;
            }

            PmRpcEntry entry = FindEntry(pack.Requestcode, pack.Actioncode);
            if (entry == null)
            {
                // 明确报告「哪个组合没注册」，而不是原来那句无法定位的「没有找到指定事件处理」。
                // ActionCode 里存在只下行、客户端不该上行的取值；这类请求落到这里属于正常情况。
                Logging.Debug.Log($"[RPC][未注册] request={pack.Requestcode} action={pack.Actioncode} uid={(client != null ? client.UID : 0)} requestId={pack.RequestId} —— 该 (RequestCode, ActionCode) 组合没有注册处理函数");
                return;
            }

            MainPack response;
            try
            {
                response = entry.Handler(_server, client, pack);
            }
            catch (Exception ex)
            {
                // 异常隔离（原实现的第 7 个脆弱点）：此前处理函数抛出的异常会冒泡到 Client.ReceiveCallBack 的
                // catch，进而调用 Close() 主动断开该客户端连接，把一个业务异常放大成断线。
                // 现在只记录并回一个失败包，连接保持可用。
                Logging.Debug.Log($"[RPC][异常] handler={entry.HandlerName} request={pack.Requestcode} action={pack.Actioncode} uid={(client != null ? client.UID : 0)} ex={ex}");
                pack.Returncode = ReturnCode.Fail;
                pack.Actioncode = ActionCode.ActionNone;
                response = pack;
            }

            if (client == null)
            {
                return;
            }

            if (response == null)
            {
                // 处理函数没有回包（例如 Chat，或 JoinFriendRoom 已改为向房间广播）。
                // 但只要是带 request_id 的请求，客户端就在等一个回包以停止超时重试，
                // 因此这里补一个空的 ack。ActionNone 会被客户端路由层直接忽略，不会产生额外业务行为。
                if (pack.RequestId != 0)
                {
                    MainPack ack = new MainPack();
                    ack.Requestcode = pack.Requestcode;
                    ack.Actioncode = ActionCode.ActionNone;
                    ack.RequestId = pack.RequestId;
                    client.Send(ack);
                }

                return;
            }

            // request_id 回填的唯一收口点：保证「请求 → 响应」可配对（计划 B3）。
            // 放在这里而不是各处理函数里，是因为回包路径有多条（返回值、就地改写、广播后返回）。
            response.RequestId = pack.RequestId;
            client.Send(response);
        }

        private PmRpcEntry FindEntry(RequestCode requestCode, ActionCode actionCode)
        {
            Dictionary<ActionCode, PmRpcEntry> byAction;
            if (!_rpcTable.TryGetValue(requestCode, out byAction))
            {
                return null;
            }

            PmRpcEntry entry;
            if (byAction.TryGetValue(actionCode, out entry))
            {
                return entry;
            }

            return null;
        }
    }
}
