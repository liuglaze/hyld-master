// R3-B：客户端会话宿主（契约 Docs/plans/net-r3-control-contract.md §7.1 / §7.2 / §7.4）。
//
// 入口只有两个：<see cref="PMClientSessionHost.Enter"/>（收到 PMDS1 入局通知后）与
// <see cref="PMClientSessionHost.Stop"/>（退出/切服）。
//
// 关键纪律（逐条对应契约）：
//   - 先校验 offer 的协议摘要与碰撞摘要，**不一致直接失败，绝不回退旧链**（§7.1「解码失败报错不得退旧链」）；
//   - 保留 Lobby TCP（大厅长连接不动），但**显式关掉旧战斗 UDP socket**（§7.4「关闭旧 UDP 后 OpenClient」）；
//   - 建共享的固定测试场景（可见简单几何，**不宣称真实玩法**，§7.4）；
//   - `OpenClient(offer, bridge)` 后由本宿主 Pump；`endpoint.Pump` 内部已调 `bridge.Update`，
//     因此**不再二次 Update**（§7.2）；
//   - 本端副本经生命周期消息创建后，**仅本地 owner** 发一次上行探针（§7.4），随后观察 Echo 与属性收敛；
//   - 重复 Enter 同一局不重绑定；换局先 Stop 再 Enter；退出时清理世界/socket/场景根/static 事件。
//
// ---------------------------------------------------------------------------------------------
// R4-B（B3）新增：本机运动主链
// ---------------------------------------------------------------------------------------------
// 每个复制到的副本（**AP 与 SP 都算**，旧实现把 SP 直接跳过，那会让别人的角色在客户端根本不动）
// 建一个 PMR4MovementDriver + 一个 PMUnityMoverPresentation：
//   · AP（本地 owner）：每帧用真实整 ms 余数累加出 1..50 的子步（每 Update 最多 8 步），
//     每个子步“先 Sample（不消费边沿）→ Tick → 接受后才 Consume”（契约 §B3：不能先消费再因冻结/超限丢按键），
//     然后把预测状态喂给表现层；
//   · SP：只 Advance + SamplePresentation（只插值、不外推），不预测、不回滚。
// 每帧次序：Physics.SyncTransforms 一次 → endpoint.Pump（含 bridge.Update）→ 采样/预测或插值 →
// 后续网络 flush（上行输入由**下一次** endpoint.Pump 发出，本帧不抢数据报预算）。
// 失联/失败：必须 Freeze（不再 Tick/Advance/Apply），但墙钟仍可推进（R4-A 的断线墙钟只在冻结时生效），
// 输入与表现都不得越界。
// 与旧链并存约束：本类不启动任何旧战斗逻辑；Enter 时已显式关掉旧战斗 UDP socket，
// 因此不会与旧 BattleManger 同一局双驱动。
//
// ---------------------------------------------------------------------------------------------
// R4-C（C3）新增：显式会话模式（诊断内容 vs 正式内容）
// ---------------------------------------------------------------------------------------------
// offer 里的 `CollisionDigest` 是模式选择的**唯一**来源（它由 Lobby 分配、与票据/MAC 绑定）：
//   · `0`           → 非法（未选择内容），拒绝入局；
//   · `0x52334201`  → 诊断：保留 R3-B 的 PMR3TestScene + PMUnityMoverPresentation（保住已验烟测）；
//   · 其它非 0      → 正式：加载 C2 的 PMUnityBattleMap（manifest 内部强校验 + 与本 offer 摘要相等），
//                       表现改用 PMUnityBattlePresentation，查询 WorldVersion 来自同一份 manifest。
// 两种模式的 rig（驱动 + 表现）走**同一套** private 接口，因此 Apply/Dispose 只有一处实现。
// 正式模式下本地 owner 的测试相机由宿主**临时**关闭其它已启用相机来确保可见，
// 退局时按记录恢复（**不**改旧场景的永久设置）。
// 首批输入仍是键盘 WASD/Space（PMUnityMoverInput），攻击尚未接入 —— 不宣称完整玩法。

using System;
using System.Collections.Generic;
using System.Globalization;
using Logging;
using PMNet;
using PMNet.Mover;
using PMNet.Prediction;
using PMNet.R3;
using PMNet.Session;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PMNet.Unity
{
    /// <summary>
    /// 客户端侧新链会话宿主（契约 §7.4）。
    ///
    /// 生命周期：<see cref="Enter"/> → 由内部驱动的 MonoBehaviour 每帧 <c>PumpActive</c> →
    /// <see cref="Stop"/>（或退出时自动收尾）。
    /// </summary>
    public static class PMClientSessionHost
    {
        /// <summary>心跳日志间隔（秒）。</summary>
        private const float HeartbeatLogIntervalSeconds = 5f;

        /// <summary>
        /// **诊断模式**下本局运动碰撞世界的版本号。**必须与 PMDsSessionHost.MovementWorldVersion 同值**：
        /// 快照携带的 Aux.CollisionWorldVersion 会被本端比对，不一致则拒绝并请求重同步。
        ///
        /// R4-C（C3）：正式内容模式使用 manifest 里的 `worldVersion`（见 Session.SelectedWorldVersion）。
        /// </summary>
        public const int MovementWorldVersion = 1;

        /// <summary>单个子步的最小/最大整毫秒（契约 §B1：1..50）。</summary>
        private const int MinStepMs = 1;
        private const int MaxStepMs = 50;

        /// <summary>每宿主 Update 最多推进的子步数（契约 §B1：最多 8 步不拉大 dt）。</summary>
        private const int MaxSubstepsPerUpdate = 8;

        /// <summary>累加器的上限（毫秒）。落后太多时宁可丢时间，也不一帧内追赶出巨量子步。</summary>
        private const double MaxStepAccumulatorMs = 200.0;

        /// <summary>
        /// rig 里表现的**统一写入面**：诊断用 PMUnityMoverPresentation（胶囊），
        /// 正式用 PMUnityBattlePresentation（C1 烘焙的正式角色）。
        ///
        /// 为什么用接口而不是改两组公开 API：两个表现类的 Apply/Dispose 语义一致、类型不同，
        /// 在宿主内部做一层薄适配即可让“清理 / Apply”只有一处调用点（C2 的公开面不动）。
        /// </summary>
        private interface IRigPresentation
        {
            void ApplyPredicted(PMMoverSyncState state);

            void ApplyInterpolated(PMMoverSyncState state);

            void Dispose();
        }

        private sealed class MoverRigPresentation : IRigPresentation
        {
            private readonly PMUnityMoverPresentation _inner;

            public MoverRigPresentation(PMUnityMoverPresentation inner)
            {
                _inner = inner;
            }

            public void ApplyPredicted(PMMoverSyncState state) { _inner.ApplyPredicted(state); }

            public void ApplyInterpolated(PMMoverSyncState state) { _inner.ApplyInterpolated(state); }

            public void Dispose() { _inner.Dispose(); }
        }

        private sealed class BattleRigPresentation : IRigPresentation
        {
            private readonly PMUnityBattlePresentation _inner;

            public BattleRigPresentation(PMUnityBattlePresentation inner)
            {
                _inner = inner;
            }

            public void ApplyPredicted(PMMoverSyncState state) { _inner.ApplyPredicted(state); }

            public void ApplyInterpolated(PMMoverSyncState state) { _inner.ApplyInterpolated(state); }

            public void Dispose() { _inner.Dispose(); }
        }

        /// <summary>
        /// 一个副本的本地运动链（驱动 + 表现）。
        /// AP 与 SP **都**需要它（区别在于每帧调 Tick 还是 Advance）。
        /// </summary>
        private sealed class MovementRig
        {
            public PMR3Player Player;
            public PMR4MovementDriver Driver;
            public IRigPresentation Presentation;
            public bool IsOwner;
        }

        /// <summary>被宿主临时关闭的相机（以及它原来的启用状态），退局时按此恢复。</summary>
        private sealed class SuppressedCamera
        {
            public Camera Camera;
            public bool WasEnabled;
        }

        private sealed class Session
        {
            public PMDsEntryOffer Offer;
            public PMSession NetSession;
            public PMNetWorld World;
            public PMNetSessionBridge Bridge;
            public PMUdpSessionEndpoint Endpoint;
            public readonly List<GameObject> SceneObjects = new List<GameObject>();
            public readonly List<PMR3Player> PendingProbes = new List<PMR3Player>();
            public readonly List<PMR3Player> KnownPlayers = new List<PMR3Player>();
            public long TickCount;
            public int ProbeNonce;
            public float NextHeartbeatLogTime;
            public bool Faulted;
            public string FaultReason;
            public Action<string> PreviousRuntimeWarn;

            // ---- R4-B（B3）：运动主链 ----

            /// <summary>碰撞查询（白名单 = 场景地板/墙，WorldVersion = 1，与 DS 同值）。</summary>
            public PMUnityMoverCollisionQuery Query;

            // ---- R4-C（C3）：显式会话模式 ----

            /// <summary>本局是否走正式内容（false = 诊断 PMR3TestScene）。</summary>
            public bool ContentFormal;

            /// <summary>正式内容地图（仅正式模式非空；它持有隔离物理场景与查询，释放走它自己的 Dispose）。</summary>
            public PMUnityBattleMap BattleMap;

            /// <summary>本局实际选定的碰撞摘要（诊断 = 保留值；正式 = manifest 值）。</summary>
            public uint SelectedCollisionDigest;

            /// <summary>本局运动碰撞世界版本（诊断 = 1；正式 = manifest.worldVersion）。</summary>
            public int SelectedWorldVersion;

            /// <summary>被宿主临时关闭的相机（正式模式重开相机可见性用；退局恢复）。</summary>
            public readonly List<SuppressedCamera> SuppressedCameras = new List<SuppressedCamera>();

            /// <summary>
            /// R4-B（B3）：地板/墙所在的**本地物理场景**（`LocalPhysicsMode.Physics3D`）。
            /// 这是碰撞隔离的载体：查询必须打在它上面，而不是 Physics.defaultPhysicsScene。
            /// 失效场景（default）表示本局没有隔离世界，ReleaseSession 里尝试释放。
            /// </summary>
            public Scene MovementScene;

            /// <summary>
            /// 本端唯一的 AP 副本（同局出现第二个 AP 直接失败：一份输入不能驱动两个 AP）。
            /// </summary>
            public PMR3Player OwnerPlayer;

            /// <summary>本机输入采样（AP 用；SP 不读输入）。</summary>
            public PMUnityMoverInput Input;

            /// <summary>全部副本的本地运动链（AP + SP）。</summary>
            public readonly List<MovementRig> Movements = new List<MovementRig>();

            /// <summary>真实整毫秒累积的剩余量（子步驱动）。</summary>
            public double StepAccumulatorMs;


            /// <summary>已因失联/失败冻结全部运动（不再 Tick/Advance/Apply）。</summary>
            public bool MovementFrozen;
        }

        private static Session _active;
        private static bool _stopping;

        /// <summary>是否已在局内（新链）。</summary>
        public static bool IsActive { get { return _active != null && !_active.Faulted; } }

        /// <summary>最近一次失败原因（无失败时为 null）。</summary>
        public static string LastError { get { return _active != null ? _active.FaultReason : null; } }

        /// <summary>当前局的 matchId（不在局内时为 null）。</summary>
        public static string CurrentMatchId { get { return _active != null ? _active.Offer.MatchId : null; } }

        /// <summary>当前局实际激活的连接（未激活时为 null）。</summary>
        public static PMTransportConnection Connection
        {
            get { return _active != null && _active.Endpoint != null ? _active.Endpoint.ClientConnection : null; }
        }

        /// <summary>已观察到的本端副本（诊断/门禁）。</summary>
        public static int KnownPlayerCount { get { return _active != null ? _active.KnownPlayers.Count : 0; } }

        /// <summary>已发出的探针数（诊断/门禁）。</summary>
        public static int ProbeSentCount { get { return _active != null ? _active.ProbeNonce : 0; } }

        /// <summary>已收到的 Echo 次数（诊断/门禁）。</summary>
        public static int EchoCount
        {
            get
            {
                if (_active == null) { return 0; }

                int total = 0;
                for (int i = 0; i < _active.KnownPlayers.Count; i++)
                {
                    PMR3Player player = _active.KnownPlayers[i];
                    if (player != null) { total += player.EchoCount; }
                }

                return total;
            }
        }

        // =================================================================================
        //  入口
        // =================================================================================

        /// <summary>
        /// 进入一局（新链）。**只在这里做一次性的摘要校验与全套接线**，失败即失败，不回旧链。
        ///
        /// 重复 Enter 同一局（MatchId + Epoch 相同）会被忽略：既有的世界/socket/副本保持不变。
        /// </summary>
        public static void Enter(PMDsEntryOffer offer)
        {
            if (offer == null) { throw new ArgumentNullException("offer"); }

            // 1) 生成表必须先注册：摘要校验的唯一来源（未注册时 ProtocolHash 为 0）。
            PMR3Runtime.Register();

            if (PMR3Runtime.ProtocolHash == 0u)
            {
                Fail(null, "PMR3 协议摘要为 0：生成表未注册，拒绝入局（不回旧链）");
                return;
            }

            if (offer.ProtocolHash != PMR3Runtime.ProtocolHash)
            {
                Fail(null, "协议摘要不一致（offer 0x" + offer.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture)
                           + " vs 本机 0x" + PMR3Runtime.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture)
                           + "），拒绝入局（不回旧链）");
                return;
            }

            // R4-C（C3）：摘要决定模式 —— 0 非法；保留值 0x52334201 = 诊断；其它非 0 = 正式内容。
            // **不按资源是否存在猜模式**：正式模式缺 manifest/地图就是失败（不回旧链、不回落诊断）。
            if (offer.CollisionDigest == 0u)
            {
                Fail(null, "入局通知 CollisionDigest 为 0（禁止）：新链必须显式选择正式内容或诊断保留摘要，"
                           + "拒绝入局（不回旧链）");
                return;
            }

            bool contentFormal = offer.CollisionDigest != PMR3Runtime.CollisionDigest;

            if (offer.Epoch == 0u || offer.Port <= 0 || string.IsNullOrEmpty(offer.Host))
            {
                Fail(null, "入局通知字段非法（epoch=" + offer.Epoch.ToString(CultureInfo.InvariantCulture)
                           + " port=" + offer.Port.ToString(CultureInfo.InvariantCulture)
                           + " host='" + (offer.Host ?? "<null>") + "'）");
                return;
            }

            // 2) 重复 Enter 同一局：不重绑定（避免把已有世界/socket 拆掉重建）。
            if (_active != null && SameMatch(_active.Offer, offer))
            {
                HYLDDebug.Log("[PMClientSessionHost] 已在同一局内（match=" + offer.MatchId
                              + " epoch=" + offer.Epoch.ToString(CultureInfo.InvariantCulture)
                              + "），忽略重复 Enter");
                return;
            }

            if (_active != null)
            {
                // 换局：先完整收尾旧局（含旧 socket 与 static 事件），再建新局。
                HYLDDebug.Log("[PMClientSessionHost] 检测到换局，先收尾旧局（match=" + _active.Offer.MatchId + "）");
                Stop();
            }

            Session session = new Session();
            session.Offer = offer;
            session.ContentFormal = contentFormal;
            session.SelectedCollisionDigest = offer.CollisionDigest;
            session.SelectedWorldVersion = contentFormal ? 0 : MovementWorldVersion;

            // 3) 世界 / 桥 / 运行时接线（客户端侧：world.IsServer 为假）。
            session.NetSession = new PMSession(offer.Epoch, false);
            session.World = new PMNetWorld(session.NetSession);
            session.World.Warn = delegate(string m) { HYLDDebug.LogWarning("[PMClientSessionHost] world: " + m); };
            session.Bridge = new PMNetSessionBridge(session.World);
            session.Bridge.Warn = delegate(string m) { HYLDDebug.LogWarning("[PMClientSessionHost] bridge: " + m); };

            PMR3Runtime.Attach(session.World, session.Bridge);
            session.PreviousRuntimeWarn = PMR3Runtime.Warn;
            PMR3Runtime.Warn = delegate(string m) { HYLDDebug.LogWarning("[PMClientSessionHost] runtime: " + m); };

            // 4) 运动碰撞世界（R4-C / C3 显式会话模式）：
            //    · 诊断（offer digest == 保留值）：R3-B 的固定测试场景（可见简单几何），保住已验烟测；
            //    · 正式（其它非 0）：C2 的正式地图（manifest 内部强校验 + 与本 offer 摘要相等），
            //      查询的 WorldVersion 来自同一份 manifest。
            //    两条路径都必须在**本地物理场景**里（LocalPhysicsMode.Physics3D），
            //    否则查询会打在默认物理世界（= 大厅/旧地图）上——layer 掩码与白名单都是
            //    “后置过滤”，不能当作隔离。
            if (session.ContentFormal)
            {
                PMUnityBattleMap battleMap;
                string mapError;
                if (!PMUnityBattleMap.TryLoad(out battleMap, out mapError))
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 正式内容地图加载失败：" + mapError
                                       + "，拒绝入局（不回旧链）");
                    return;
                }

                if (battleMap.Manifest.collisionDigest != session.SelectedCollisionDigest)
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 正式内容摘要不一致：offer 0x"
                                       + session.SelectedCollisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                                       + "，本机 manifest 0x"
                                       + battleMap.Manifest.collisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                                       + "，拒绝入局（不回旧链）");
                    return;
                }

                session.BattleMap = battleMap;
                session.Query = battleMap.Query;
                session.MovementScene = battleMap.Scene;
                session.SelectedWorldVersion = battleMap.Manifest.worldVersion;

                string consistencyError;
                if (!battleMap.ValidateManifestConsistency(out consistencyError))
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 正式地图与 manifest 不一致：" + consistencyError
                                       + "，拒绝入局（不回旧链）");
                    return;
                }

                string spawnCheckError;
                if (!battleMap.TryValidateAllSpawns(out spawnCheckError))
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 正式地图出生位自检失败：" + spawnCheckError
                                       + "，拒绝入局（不回旧链）");
                    return;
                }
            }
            else
            {
                string sceneError;
                Scene movementScene;
                PhysicsScene movementPhysicsScene;
                if (!PMR3TestScene.BuildIsolated("[PMClientScene]", true, session.SceneObjects,
                                                out movementScene, out movementPhysicsScene, out sceneError))
                {
                    // 与相邻失败分支逐字一致：必须走 ReleaseSession（还原静态 Warn + 释放桥），
                    // 否则静态出口永久停在本会话的 lambda 上，下一次 Enter 还会把它当“上一个”存下来。
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 测试场景不可用（本地物理场景隔离失败）："
                                       + sceneError + "，拒绝入局（不回旧链）");
                    return;
                }

                session.MovementScene = movementScene;

                // 4b) 诊断模式：运动碰撞查询 + 本机输入。
                //     白名单**必须**是刚建的地板/墙两个 Collider（契约 B2）：不能让旧地图/旧相机/
                //     表现胶囊参与查询。WorldVersion 与 DS 取同一个冻结值 1。
                if (!movementPhysicsScene.IsValid() || movementPhysicsScene.Equals(Physics.defaultPhysicsScene))
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 运动碰撞隔离失败：本地物理场景无效或等于默认物理世界，"
                                       + "拒绝入局（不回旧链）");
                    return;
                }

                Collider[] allowlist = new Collider[2];
                int allowCount;
                string allowError;
                if (!PMR3TestScene.TryCollectColliders(session.SceneObjects, allowlist, out allowCount, out allowError))
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 运动碰撞白名单不完整：" + allowError + "，拒绝入局（不回旧链）");
                    return;
                }

                int movementLayerMask = 0;
                for (int i = 0; i < allowCount; i++)
                {
                    if (allowlist[i] == null || allowlist[i].gameObject == null)
                    {
                        ReleaseSession(session);
                        HYLDDebug.LogError("[PMClientSessionHost] 运动碰撞白名单第 " + i + " 项无效，拒绝入局（不回旧链）");
                        return;
                    }

                    movementLayerMask |= 1 << allowlist[i].gameObject.layer;
                }

                try
                {
                    session.Query = new PMUnityMoverCollisionQuery(movementPhysicsScene, movementLayerMask,
                                                                   session.SelectedWorldVersion, allowlist);
                }
                catch (Exception ex)
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 运动碰撞查询构造失败：" + ex.GetType().Name + " " + ex.Message
                                       + "（不回旧链）");
                    return;
                }

                if (!session.Query.Scene.IsValid() || session.Query.Scene.Equals(Physics.defaultPhysicsScene))
                {
                    ReleaseSession(session);
                    HYLDDebug.LogError("[PMClientSessionHost] 运动碰撞查询未绑定到隔离物理场景（isDefaultWorld="
                                       + session.Query.Scene.Equals(Physics.defaultPhysicsScene) + "），拒绝入局（不回旧链）");
                    return;
                }
            }

            session.Input = new PMUnityMoverInput();

            // 5) 关掉旧战斗 UDP socket（§7.4）：旧 socket 属于硬编码 7777 的旧链，
            //    不关会让「切服/退局」泄漏一个仍在收旧包的 socket。幂等，未初始化时无副作用。
            //
            // 必须写 `global::Server`：本文件在 `PMNet.Unity` 里，直接写 `Server` 会解析到
            // `PMNet.Server`（另一个命名空间），而不是旧客户端的 `Server.UDPSocketManger`。
            global::Server.UDPSocketManger.CloseExisting();

            // 6) UDP 入局端点：验票相关的关联校验在端点内部完成（§7.2）。
            try
            {
                session.Endpoint = PMUdpSessionEndpoint.OpenClient(offer, session.Bridge);
            }
            catch (Exception ex)
            {
                ReleaseSession(session);
                HYLDDebug.LogError("[PMClientSessionHost] OpenClient 失败：" + ex.GetType().Name + " " + ex.Message
                                   + "（不回旧链）");
                return;
            }

            session.Endpoint.Failed += delegate(string reason) { OnEndpointFailed(reason); };

            _active = session;

            // static 事件是跨帧的：必须在这里订阅，并在 Stop 里退订（否则退出后再 Enter 会拿到旧宿主）。
            PMR3Runtime.PlayerReplicated += OnPlayerReplicated;

            PMClientSessionHostDriver.Ensure();

            HYLDDebug.Log("[PMClientSessionHost] ===== 新链客户端会话已建立 =====");
            HYLDDebug.Log("[PMClientSessionHost] match=" + offer.MatchId + " ds=" + offer.DsId
                          + " host=" + offer.Host + " port=" + offer.Port.ToString(CultureInfo.InvariantCulture)
                          + " epoch=" + offer.Epoch.ToString(CultureInfo.InvariantCulture)
                          + " hash=0x" + offer.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture)
                          + " mode=" + (session.ContentFormal ? "formal" : "diagnostic")
                          + " digest=0x" + session.SelectedCollisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                          + " worldVersion=" + session.SelectedWorldVersion.ToString(CultureInfo.InvariantCulture)
                          + " 本地uid=" + offer.Identity.Uid.ToString(CultureInfo.InvariantCulture));
            HYLDDebug.Log("[PMClientSessionHost] 保留 Lobby TCP 长连接；已关闭旧战斗 UDP socket；"
                          + "首批输入=键盘 WASD/Space（攻击尚未接入）；等待握手激活");
            HYLDDebug.Log("[PMClientSessionHost] ================================");
        }

        /// <summary>
        /// 退出当前局：退订 static 事件、释放端点、摘除运行时接线、销毁场景对象。幂等。
        /// </summary>
        public static void Stop()
        {
            if (_active == null) { return; }
            if (_stopping) { return; }

            _stopping = true;
            try
            {
                Session session = _active;
                _active = null;

                PMR3Runtime.PlayerReplicated -= OnPlayerReplicated;

                if (session.Endpoint != null)
                {
                    try { session.Endpoint.Dispose(); }
                    catch (Exception ex) { HYLDDebug.LogWarning("[PMClientSessionHost] 释放端点异常：" + ex.GetType().Name); }
                }

                ReleaseSession(session);

                PMClientSessionHostDriver.Release();

                HYLDDebug.Log("[PMClientSessionHost] 已退出新链会话（match=" + session.Offer.MatchId + "）");
            }
            finally
            {
                _stopping = false;
            }
        }

        // =================================================================================
        //  主线程驱动
        // =================================================================================

        /// <summary>Unity Update 的唯一驱动点（由内部 driver 调用）。</summary>
        internal static void PumpActive()
        {
            Session session = _active;
            if (session == null) { return; }

            long nowMs = (long)(Time.realtimeSinceStartup * 1000f);
            long nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            session.TickCount++;

            if (session.Faulted)
            {
                // 失联/失败：必须 Freeze（Fail 里已冻结）。墙钟仍可推进（R4-A 的断线墙钟只在冻结时生效，
                // 也是唯一能恢复的路径），但**不再**发输入、不再推进表现，避免越界外推。
                FrozenWallClockPump(session);
                return;
            }

            // 复审 D5：端点自身 _disposed 置位后 Pump 直接 return，既不报 ClientFailed 也不跑
            // CheckClientSessionAfterUpdate。若不在这里兜底，就会出现「端点不再 Pump、宿主一直预测」的
            // 无告警静默通道。IsDisposed 是端点暴露的只读快照，正是为这个判据准备的。
            if (session.Endpoint == null || session.Endpoint.IsDisposed)
            {
                Fail(session, "UDP 端点已释放但会话未 Stop：拒绝继续预测（不静默挂着）");
                FrozenWallClockPump(session);
                return;
            }

            // 契约 §B3：「主线程在 endpoint.Pump 前 Physics.SyncTransforms 一次」。
            // 适配器自己永不调它（契约 B2），因此这里是本端唯一调用点，且每帧只调一次；
            // 不能每条 resim/每次查询都扫一遍（那会把预测预算吃光）。
            Physics.SyncTransforms();

            session.Endpoint.Pump(nowMs, nowUnixSeconds);
            if (session.Faulted)
            {
                FrozenWallClockPump(session);
                return;
            }

            if (session.Endpoint.ClientFailed)
            {
                Fail(session, "入局握手失败（对端不在本局/票据已过期/关联校验不通过）");
                FrozenWallClockPump(session);
                return;
            }

            // 次序：网络入站/校正（上面） → 采样/预测/插值（这里） → 后续网络 flush（下一次 Pump）。
            PumpMovement(session);

            SendPendingProbes(session);

            if (Time.unscaledTime >= session.NextHeartbeatLogTime)
            {
                session.NextHeartbeatLogTime = Time.unscaledTime + HeartbeatLogIntervalSeconds;
                LogHeartbeat(session);
            }
        }

        // =================================================================================
        //  R4-B（B3）：运动主链
        // =================================================================================

        /// <summary>本帧真实经过的毫秒（非有限/负值归 0；不包含任何推测性平滑）。</summary>
        private static double CurrentFrameElapsedMs()
        {
            double elapsedMs = (double)Time.deltaTime * 1000.0;
            if (double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs) || elapsedMs < 0.0)
            {
                return 0.0;
            }

            return elapsedMs;
        }

        /// <summary>
        /// 冻结后的墙钟推进：**只**调用 Driver.Update（消费入站 + 断线墙钟）。
        /// 不 Tick、不 SendInputPayload、不 Advance、不 Apply——这就是"墙钟可推进但
        /// 不继续发输入/不让表现越界"的落地形式。
        /// </summary>
        private static void FrozenWallClockPump(Session session)
        {
            double elapsedMs = CurrentFrameElapsedMs();

            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (rig.Driver == null || rig.Driver.IsDisposed) { continue; }

                try { rig.Driver.Update(elapsedMs); }
                catch (Exception ex)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 冻结期墙钟推进异常：" + ex.GetType().Name);
                }
            }
        }

        /// <summary>每帧的运动驱动：全部副本 Update → AP 子步预测/发送 → SP 插值与表现。 </summary>
        private static void PumpMovement(Session session)
        {
            if (session.Movements.Count == 0) { return; }

            double elapsedMs = CurrentFrameElapsedMs();

            // 1) 消费入站（所有角色；Driver 的步进入口也会自动 ApplyPending）。
            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (rig.Driver == null || rig.Driver.IsDisposed) { continue; }

                try { rig.Driver.Update(elapsedMs); }
                catch (Exception ex)
                {
                    Fail(session, "运动驱动 Update 异常：" + ex.GetType().Name + " " + ex.Message);
                    return;
                }
            }

            // 输入轮询早于步数决策：不足 1ms 的渲染帧也必须缓存边沿。
            session.Input.PollHardware();

            // 2) AP：真实整毫秒余数累加 → 1..50 子步，每 Update 最多 8 步。
            session.StepAccumulatorMs += elapsedMs;
            if (session.StepAccumulatorMs > MaxStepAccumulatorMs)
            {
                session.StepAccumulatorMs = MaxStepAccumulatorMs;
            }

            int steps = 0;
            while (steps < MaxSubstepsPerUpdate && session.StepAccumulatorMs >= MinStepMs)
            {
                int stepMs = (int)Math.Floor(session.StepAccumulatorMs);
                if (stepMs < MinStepMs) { stepMs = MinStepMs; }
                if (stepMs > MaxStepMs) { stepMs = MaxStepMs; }

                if (!TickAutonomousProxies(session, stepMs))
                {
                    // 冻结/历史耗尽/未确认窗口满：剩余时间留在累加器里，本帧不再推进。
                    // 这是故意的：既不能拆分 dt，也不能因为服务器跟不上就凭空丢掉玩家的输入。
                    break;
                }

                session.StepAccumulatorMs -= stepMs;
                steps++;
            }

            // 3) SP：只插值（不外推）。SamplePresentation 超出最新权威值时会钳到最新，
            //    因此“不能越界”由驱动层保证，表现层只负责写 Transform。
            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (rig.IsOwner || rig.Driver == null || rig.Driver.IsDisposed || rig.Presentation == null)
                {
                    continue;
                }

                try
                {
                    rig.Driver.Advance(elapsedMs);
                    PMInterpolatedState<PMMoverSyncState, PMMoverAuxState> sample = rig.Driver.SamplePresentation();
                    if (sample.HasValue)
                    {
                        rig.Presentation.ApplyInterpolated(sample.Sync);
                    }
                }
                catch (Exception ex)
                {
                    Fail(session, "SP 插值异常：" + ex.GetType().Name + " " + ex.Message);
                    return;
                }
            }

            // 4) AP：表现喂**预测输出**（与 SP 的写入面完全相同，写入点只有这一处）。
            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (!rig.IsOwner || rig.Driver == null || rig.Driver.IsDisposed || rig.Presentation == null)
                {
                    continue;
                }

                try { rig.Presentation.ApplyPredicted(rig.Driver.GetPredictedSync()); }
                catch (Exception ex)
                {
                    Fail(session, "AP 表现写入异常：" + ex.GetType().Name + " " + ex.Message);
                    return;
                }
            }
        }

        /// <summary>
        /// 推进本端 AP 一个真实子步。返回 false 表示本帧不应再继续（冻结/历史耗尽/超限）。
        ///
        /// 关键契约（§B3 末段）：「Input 在 Tick 真正接受后 Consume 边沿，不能先消费再因冻结/超限失去按键」。
        /// 所以这里先 <see cref="PMUnityMoverInput.Sample"/>（**不**消费）→ Tick → 接受后才 Consume。
        /// </summary>
        private static bool TickAutonomousProxies(Session session, int stepMs)
        {

            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (!rig.IsOwner || rig.Driver == null || rig.Driver.IsDisposed) { continue; }

                PMMoverInput input = session.Input.Sample(stepMs);

                PMTickResult result;
                try
                {
                    // AP 不生成 DS 帧号；沿用最后权威元数据（尚未建立则为 None）。
                    // 用只读访问器 PendingServerFrame（不 clone Sync/Aux）而不是 GetSnapshot()：
                    // 后者每个子步都会深克隆带数组的 Sync/Aux，在非增量 GC 下是可见尖峰。
                    PMFrameId serverFrame = rig.Driver.Timeline.PendingServerFrame;
                    result = rig.Driver.Tick(stepMs, input, serverFrame);
                }
                catch (Exception ex)
                {
                    Fail(session, "AP Tick 异常：" + ex.GetType().Name + " " + ex.Message);
                    return false;
                }

                if (!result.Accepted)
                {
                    // 不消费边沿（键还在缓冲里，下一个真实步再试）。
                    return false;
                }

                session.Input.Consume(stepMs);

                try { rig.Driver.SendInputPayload(); }
                catch (Exception ex)
                {
                    Fail(session, "上行输入发送异常：" + ex.GetType().Name + " " + ex.Message);
                    return false;
                }
            }

            return true;
        }

        /// <summary>为副本建运动链（AP/SP 都需要）；已建过或无法建立时安全返回。</summary>
        private static MovementRig EnsureMovementRig(Session session, PMR3Player player, bool isOwner)
        {
            for (int i = 0; i < session.Movements.Count; i++)
            {
                if (ReferenceEquals(session.Movements[i].Player, player))
                {
                    return session.Movements[i];
                }
            }

            if (session.Query == null)
            {
                HYLDDebug.LogWarning("[PMClientSessionHost] 碰撞查询未建立，无法建立运动链（调用方会显式整局失败，不会以探针假成功掩盖）");
                return null;
            }

            string error;
            PMR4MovementDriver driver = PMR4MovementDriver.CreateFromPlayerInitialSnapshot(
                player, session.Query, session.Offer.Epoch, out error);

            if (driver == null)
            {
                HYLDDebug.LogWarning("[PMClientSessionHost] 运动驱动建立失败（netId="
                                     + player.NetId.Value + " role=" + player.Role + "）：" + error
                                     + "（调用方会显式整局失败）");
                return null;
            }

            string label = "uid" + player.Uid + "/net" + player.NetId.Value;

            IRigPresentation presentation;
            try
            {
                if (session.ContentFormal)
                {
                    // R4-C（C3）正式：C1 烘培的正式角色表现（C2 在构造时校验组件集并拒 DS/Authority）。
                    // 只为本地 owner 建测试相机（契约 B2：可选、不强制）；相机的销毁由表现层自己负责。
                    PMUnityBattlePresentation battle = new PMUnityBattlePresentation(
                        label, player.Role, session.BattleMap.Manifest, isOwner);
                    presentation = new BattleRigPresentation(battle);

                    if (isOwner)
                    {
                        // “确保可见”：临时关闭其它已启用相机（防旧主相机遮住新角色），退局时恢复。
                        SuppressOtherCameras(session, battle.TestCamera);
                    }
                }
                else
                {
                    // 诊断：R4-B 的胶囊表现（保留已验烟测；不改两组公开 API）。
                    presentation = new MoverRigPresentation(
                        new PMUnityMoverPresentation(label, player.Role, isOwner));
                }
            }
            catch (Exception ex)
            {
                driver.Dispose();
                HYLDDebug.LogWarning("[PMClientSessionHost] 表现层建立失败（" + label + "）："
                                     + ex.GetType().Name + " " + ex.Message + "（调用方会显式整局失败）");
                return null;
            }

            MovementRig rig = new MovementRig();
            rig.Player = player;
            rig.Driver = driver;
            rig.Presentation = presentation;
            rig.IsOwner = isOwner;
            session.Movements.Add(rig);

            HYLDDebug.Log("[PMClientSessionHost] 运动链已建立 " + label
                          + " role=" + player.Role
                          + " owner=" + (isOwner ? 1 : 0)
                          + " presentation=" + (session.ContentFormal ? "battle" : "capsule")
                          + " 驱动=" + driver.Describe());
            return rig;
        }

        /// <summary>
        /// 副本创建后的本地接线：**所有** AP/SP 副本都要 Driver + 表现；上行探针仍只给本地 owner。
        ///
        /// 旧实现把 SP 直接跳过（`if (player.Role != AutonomousProxy) return;`），
        /// 结果就是"看不到别人动"——这是必须改掉的过滤。但探针语义不变：它只证明本端
        /// owner 的上行能被 DS 执行，不该替别人的角色发。
        /// </summary>
        private static void OnPlayerReplicated(PMR3Player player)
        {
            Session session = _active;
            if (session == null || session.Faulted || player == null) { return; }

            bool isOwner = player.Role == PMNetRole.AutonomousProxy;

            if (isOwner && player.Uid != session.Offer.Identity.Uid)
            {
                // 角色说是我的、uid 却说不是：**显式整局失败**，不能降为 SP。
                // 降级断点：工厂的角色取自 player.Role（AutonomousProxy），而宿主下面会按 !IsOwner 调
                // Advance → 驱动 EnsureRole(SimulatedProxy) 抛异常并报成“SP 插值异常”（误导）；
                // 另一半组合（owner 副本被判为 SP）则**静默**失去控制。两种都不接。
                Fail(session, "副本角色与 uid 不一致（role=" + player.Role
                     + " uid=" + player.Uid.ToString(CultureInfo.InvariantCulture)
                     + " 期望 " + session.Offer.Identity.Uid.ToString(CultureInfo.InvariantCulture)
                     + "）：拒绝降级为观察副本");
                return;
            }

            if (isOwner)
            {
                // 契约 §B3：输入是**每会话一份**，一个客户端只应有一个 AP。
                // 否则同一按键会被两个 rig 各消费一次（近似双驱动同一输入）。
                if (session.OwnerPlayer != null && !ReferenceEquals(session.OwnerPlayer, player))
                {
                    Fail(session, "同一局出现第二个 AP 副本（已持有 uid="
                         + session.OwnerPlayer.Uid.ToString(CultureInfo.InvariantCulture)
                         + " netId=" + session.OwnerPlayer.NetId.Value.ToString(CultureInfo.InvariantCulture)
                         + "，又收到 uid=" + player.Uid.ToString(CultureInfo.InvariantCulture)
                         + " netId=" + player.NetId.Value.ToString(CultureInfo.InvariantCulture)
                         + "）：拒绝多 AP 共享同一份输入");
                    return;
                }

                session.OwnerPlayer = player;
            }

            MovementRig rig = EnsureMovementRig(session, player, isOwner);
            if (rig == null)
            {
                // 不能“只告警、照常发探针”：那会让探针 Echo 成功掩盖“运动链实际没接上”。
                Fail(session, "运动链建立失败（netId=" + player.NetId.Value.ToString(CultureInfo.InvariantCulture)
                     + " uid=" + player.Uid.ToString(CultureInfo.InvariantCulture)
                     + " role=" + player.Role + "）：拒绝以探针假成功掩盖");
                return;
            }

            if (!isOwner)
            {
                return;   // 非本地 owner：只需表现（已在上面建），不发探针。
            }

            if (player.ProbeSentCount > 0) { return; }

            for (int i = 0; i < session.PendingProbes.Count; i++)
            {
                if (ReferenceEquals(session.PendingProbes[i], player)) { return; }
            }

            session.PendingProbes.Add(player);
            session.KnownPlayers.Add(player);

            HYLDDebug.Log("[PMClientSessionHost] 本地副本已创建 netId="
                          + player.NetId.Value.ToString(CultureInfo.InvariantCulture)
                          + " uid=" + player.Uid.ToString(CultureInfo.InvariantCulture)
                          + "，排队发送一次上行探针");
        }

        /// <summary>
        /// 发送排队中的探针。
        ///
        /// **刻意放在复制回调之外**：`OnPlayerReplicated` 发生在入站 drain 之中（endpoint.Pump 内部），
        /// 那时直接发送会重入桥；改在本帧的 Pump 里发，既只发一次，也不重入。
        /// </summary>
        private static void SendPendingProbes(Session session)
        {
            if (session.PendingProbes.Count == 0) { return; }

            for (int i = 0; i < session.PendingProbes.Count; i++)
            {
                PMR3Player player = session.PendingProbes[i];
                if (player == null) { continue; }
                if (player.ProbeSentCount > 0) { continue; }

                if (player.State != PMNetObjectState.Active)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 本地副本已不 Active，跳过探针 netId="
                                         + player.NetId.Value.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                session.ProbeNonce++;
                int nonce = session.ProbeNonce;

                player.ProbeSentCount++;

                // 经**生成的调用桩**发（`PMNet_ServerProbe`），不手写业务状态包（§7.4）。
                player.PMNet_ServerProbe(nonce);

                HYLDDebug.Log("[PMClientSessionHost] 已发送 ServerProbe nonce=" + nonce.ToString(CultureInfo.InvariantCulture)
                              + " netId=" + player.NetId.Value.ToString(CultureInfo.InvariantCulture));
            }

            session.PendingProbes.Clear();
        }

        private static void OnEndpointFailed(string reason)
        {
            Session session = _active;
            if (session == null) { return; }

            // 端点会对「入局会话关闭」也报这条事件；这里按事实区分：已经激活过再关闭 = 会话断了。
            if (session.Endpoint != null && session.Endpoint.ClientConnection != null
                && !session.Endpoint.ClientConnection.IsReady)
            {
                Fail(session, "入局会话已断开：" + reason);
                return;
            }

            HYLDDebug.LogWarning("[PMClientSessionHost] 端点事件：" + reason);
        }

        // =================================================================================
        //  失败 / 清理
        // =================================================================================

        private static void Fail(Session session, string reason)
        {
            if (session != null)
            {
                if (session.Faulted) { return; }
                session.Faulted = true;
                session.FaultReason = reason;

                // 契约 §B3：「失联/失败必须 Freeze」。冻结在前：先让 AP 停止预测、
                // SP 停止推进，再由调用方进入"只推墙钟"的冻结帧。
                FreezeMovements(session);
            }

            // 新链失败**只报失败**：不切回旧链、不改走 BattleData/ClearSence（契约 §7.1 / §7.4）。
            HYLDDebug.LogError("[PMClientSessionHost] 新链失败：" + reason + "（不回落旧链；运动已冻结）");
        }

        /// <summary>冻结全部运动链（幂等）。不 Dispose：释放由 Stop/换局路径负责。</summary>
        private static void FreezeMovements(Session session)
        {
            if (session.MovementFrozen) { return; }
            session.MovementFrozen = true;

            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (rig.Driver == null || rig.Driver.IsDisposed) { continue; }

                try { rig.Driver.Freeze(); }
                catch (Exception ex)
                {
                    HYLDDebug.LogWarning("[PMClientSessionHost] 冻结运动驱动异常：" + ex.GetType().Name);
                }
            }
        }

        /// <summary>释放全部运动链：先停驱动，再销毁表现（避免 Apply 到已释放对象），最后 Dispose 驱动。</summary>
        private static void ReleaseMovements(Session session)
        {
            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];

                if (rig.Presentation != null)
                {
                    try { rig.Presentation.Dispose(); }
                    catch (Exception ex)
                    {
                        HYLDDebug.LogWarning("[PMClientSessionHost] 释放表现层异常：" + ex.GetType().Name);
                    }

                    rig.Presentation = null;
                }

                if (rig.Driver != null)
                {
                    try
                    {
                        rig.Driver.Freeze();
                        rig.Driver.Dispose();
                    }
                    catch (Exception ex)
                    {
                        HYLDDebug.LogWarning("[PMClientSessionHost] 释放运动驱动异常：" + ex.GetType().Name);
                    }

                    rig.Driver = null;
                }

                rig.Player = null;
            }

            session.Movements.Clear();
            session.StepAccumulatorMs = 0.0;
            session.Query = null;

            if (session.Input != null)
            {
                session.Input.Reset();
                session.Input = null;
            }
        }

        private static void ReleaseSession(Session session)
        {
            if (session == null) { return; }

            // R4-B（B3）：运动链先释放（表现 → 驱动），再拆世界/桥。
            ReleaseMovements(session);

            // R4-C（C3）：把正式模式临时关闭的旧相机恢复原状（不改旧场景的永久设置）。
            RestoreSuppressedCameras(session);

            if (session.World != null)
            {
                PMR3Runtime.Detach(session.World);
            }

            // 无条件还原：PreviousRuntimeWarn 为 null 也是合法状态（没有人装过出口），
            // 留着本会话的 lambda 会让已释放对象被后续调用（static 出口泄漏）。
            PMR3Runtime.Warn = session.PreviousRuntimeWarn;

            if (session.Bridge != null)
            {
                try { session.Bridge.Dispose(); }
                catch (Exception ex) { HYLDDebug.LogWarning("[PMClientSessionHost] 释放桥异常：" + ex.GetType().Name); }
            }

            for (int i = 0; i < session.SceneObjects.Count; i++)
            {
                if (session.SceneObjects[i] != null)
                {
                    UnityEngine.Object.Destroy(session.SceneObjects[i]);
                }
            }

            session.SceneObjects.Clear();

            // R4-B（B3）：释放承载地板/墙的**本地物理场景**（先销毁对象、后卸载场景）。
            // R4-C（C3）：正式地图持有自己的隔离物理场景与查询，释放必须走它自己的 Dispose；
            // 诊断模式仍按原路径卸载本地物理场景。
            if (session.BattleMap != null)
            {
                try { session.BattleMap.Dispose(); }
                catch (Exception ex) { HYLDDebug.LogWarning("[PMClientSessionHost] 释放正式地图异常：" + ex.GetType().Name); }

                session.BattleMap = null;
                session.MovementScene = default(Scene);
            }
            else
            {
                PMR3TestScene.ReleaseIsolatedScene(session.MovementScene);
                session.MovementScene = default(Scene);
            }

            session.OwnerPlayer = null;
            session.SelectedWorldVersion = 0;

            session.PendingProbes.Clear();
            session.KnownPlayers.Clear();
            session.Endpoint = null;
            session.Bridge = null;
            session.World = null;
            session.NetSession = null;
        }

        /// <summary>
        /// R4-C（C3）正式模式：**临时**关闭除自己之外的所有已启用相机，避免旧主相机把新角色遮住。
        ///
        /// 为什么必须记录+恢复：本类不能改旧场景的永久设置（Contract C3），退局/失败路径靠
        /// <see cref="RestoreSuppressedCameras"/> 逐个还原。
        /// 为什么只关“已启用”的：<c>Camera.allCameras</c> 本身只返回启用中的相机，
        /// 未启用的相机不参与渲染，也就无需（也不应）改动。
        /// </summary>
        private static void SuppressOtherCameras(Session session, Camera mine)
        {
            if (session == null || mine == null) { return; }

            Camera[] cameras = Camera.allCameras;
            if (cameras == null) { return; }

            for (int i = 0; i < cameras.Length; i++)
            {
                Camera other = cameras[i];
                if (other == null || ReferenceEquals(other, mine)) { continue; }
                if (!other.enabled) { continue; }

                SuppressedCamera entry = new SuppressedCamera();
                entry.Camera = other;
                entry.WasEnabled = true;
                session.SuppressedCameras.Add(entry);

                other.enabled = false;
            }

            if (session.SuppressedCameras.Count > 0)
            {
                HYLDDebug.Log("[PMClientSessionHost] 正式模式相机接管：临时关闭其它启用相机 "
                              + session.SuppressedCameras.Count.ToString(CultureInfo.InvariantCulture)
                              + " 个（退局恢复）");
            }
        }

        /// <summary>把 <see cref="SuppressOtherCameras"/> 关掉的相机恢复原状（幂等）。</summary>
        private static void RestoreSuppressedCameras(Session session)
        {
            if (session == null) { return; }

            for (int i = 0; i < session.SuppressedCameras.Count; i++)
            {
                SuppressedCamera entry = session.SuppressedCameras[i];
                if (entry == null || entry.Camera == null) { continue; }

                entry.Camera.enabled = entry.WasEnabled;
            }

            session.SuppressedCameras.Clear();
        }

        private static bool SameMatch(PMDsEntryOffer a, PMDsEntryOffer b)
        {
            if (a == null || b == null) { return false; }
            return string.Equals(a.MatchId, b.MatchId, StringComparison.Ordinal) && a.Epoch == b.Epoch;
        }

        private static void LogHeartbeat(Session session)
        {
            HYLDDebug.Log("[PMClientSessionHost] heartbeat tick=" + session.TickCount.ToString(CultureInfo.InvariantCulture)
                          + " activated=" + (session.Endpoint.ClientConnection != null ? 1 : 0)
                          + " objects=" + session.World.ObjectCount.ToString(CultureInfo.InvariantCulture)
                          + " probes=" + session.ProbeNonce.ToString(CultureInfo.InvariantCulture)
                          + " echoes=" + EchoCount.ToString(CultureInfo.InvariantCulture)
                          + " udpIn=" + session.Endpoint.DatagramsReceived.ToString(CultureInfo.InvariantCulture)
                          + " udpOut=" + session.Endpoint.DatagramsSent.ToString(CultureInfo.InvariantCulture)
                          + " handshakeRej=" + session.Endpoint.HandshakeRejections.ToString(CultureInfo.InvariantCulture)
                          + " | " + DescribeMovements(session));
        }

        /// <summary>运动链的单行诊断（心跳与门禁对账用）。</summary>
        private static string DescribeMovements(Session session)
        {
            if (session == null) { return "movement=<none>"; }

            int owners = 0;
            int proxies = 0;
            long snapshotsApplied = 0;
            long inputsSent = 0;
            long events = 0;

            for (int i = 0; i < session.Movements.Count; i++)
            {
                MovementRig rig = session.Movements[i];
                if (rig.IsOwner) { owners++; } else { proxies++; }

                if (rig.Driver == null) { continue; }

                snapshotsApplied += rig.Driver.SnapshotPayloadsApplied;
                inputsSent += rig.Driver.InputPayloadsSent;
                events += rig.Driver.EventBatchesAccepted;
            }

            return "movementRigs=" + session.Movements.Count.ToString(CultureInfo.InvariantCulture)
                   + "(ap=" + owners.ToString(CultureInfo.InvariantCulture)
                   + ",sp=" + proxies.ToString(CultureInfo.InvariantCulture) + ")"
                   + " snapshots=" + snapshotsApplied.ToString(CultureInfo.InvariantCulture)
                   + " inputsSent=" + inputsSent.ToString(CultureInfo.InvariantCulture)
                   + " eventBatches=" + events.ToString(CultureInfo.InvariantCulture)
                   + " accumulatorMs=" + session.StepAccumulatorMs.ToString("0.#", CultureInfo.InvariantCulture)
                   + " scene=" + (session.MovementScene.IsValid() ? session.MovementScene.name : "<none>")
                   + " isolated=" + (session.Query != null && session.Query.Scene.IsValid()
                                      && !session.Query.Scene.Equals(Physics.defaultPhysicsScene) ? 1 : 0)
                   + " frozen=" + (session.MovementFrozen ? 1 : 0);
        }

        /// <summary>单行诊断摘要（测试宿主/日志对账用）。</summary>
        public static string Describe()
        {
            Session session = _active;
            if (session == null) { return "client=<none>"; }

            return "match=" + session.Offer.MatchId
                   + " mode=" + (session.ContentFormal ? "formal" : "diagnostic")
                   + " digest=0x" + session.SelectedCollisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                   + " worldVersion=" + session.SelectedWorldVersion.ToString(CultureInfo.InvariantCulture)
                   + " tick=" + session.TickCount.ToString(CultureInfo.InvariantCulture)
                   + " probes=" + session.ProbeNonce.ToString(CultureInfo.InvariantCulture)
                   + " echoes=" + EchoCount.ToString(CultureInfo.InvariantCulture)
                   + " " + DescribeMovements(session)
                   + " fault=" + (session.Faulted ? session.FaultReason : "<none>");
        }
    }

    /// <summary>
    /// 客户端会话宿主的 Unity 驱动（每帧一次 <c>PumpActive</c> + 退出收尾）。
    ///
    /// 单独一个内部 MonoBehaviour 的原因：宿主本身是纯逻辑（可被门禁/工具复用），
    /// 而 Unity 的 Update 只能挂在组件上。这里不承载任何逻辑，只转发。
    /// </summary>
    internal sealed class PMClientSessionHostDriver : MonoBehaviour
    {
        private static PMClientSessionHostDriver _instance;

        internal static void Ensure()
        {
            if (_instance != null) { return; }

            GameObject go = new GameObject("[PMClientSessionHost]");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<PMClientSessionHostDriver>();
        }

        internal static void Release()
        {
            PMClientSessionHostDriver driver = _instance;
            if (driver == null) { return; }

            _instance = null;

            GameObject go = driver.gameObject;
            if (go != null)
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        private void Update()
        {
            PMClientSessionHost.PumpActive();
        }

        private void OnApplicationQuit()
        {
            PMClientSessionHost.Stop();
        }

        private void OnDestroy()
        {
            // 幂等：Stop 内部有 _stopping 闩锁，重复调用无副作用。
            PMClientSessionHost.Stop();
        }
    }
}
