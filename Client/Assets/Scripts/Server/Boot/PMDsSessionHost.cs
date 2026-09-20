// R3-B：DS 侧会话宿主（契约 Docs/plans/net-r3-control-contract.md §7.4）。
//
// 装配次序（契约 §7.4 冻结）：读引导文件 → 校验局/摘要/端口 → Register → 世界/桥/Attach →
// **运行时程序化建立固定测试碰撞场景并校验 Collider** → OpenServer（§7.2）→ 订阅 Connected → SpawnPlayer →
// 控制通道 Ready/Heartbeat/Result/ResultAck。
//
// 为什么**不**加载旧 HYLDGame 场景：那片场景的 Awake 链会拉起 HYLDManger / BattleManger / UDPSocketManger
// （绑硬编码 UDP 7777），而 R3-B 只需要一个确定的、可比对的碰撞环境。
// 契约因此明确「不加载随机旧地图」：本文件用固定数值现场建 BoxCollider，并用
// PMR3Runtime.CollisionDigest 标识这套布局（不声称它是任何旧地图的摘要）。
//
// 驱动口径：本类**不是** MonoBehaviour，由 PMDsHost.Update 唯一驱动（`Pump()` 里调
// `endpoint.Pump(...)`，而 endpoint.Pump 内部已经调用过 `bridge.Update(...)`）
// —— 宿主**不得**再二次调用 bridge.Update（契约 §7.2 明确）。
//
// R4-C（C3）显式会话模式（契约 Docs/plans/net-r4c-content-contract.md 的 C3 段）：
//   · 引导文件里的 `CollisionDigest` 是**模式选择的唯一来源**（它由 Lobby 分配、与票据/MAC 绑定）：
//       - `0`            → 非法（未选择内容），拒绝启动；
//       - `0x52334201`   → 诊断内容：保留 R3-B 的 PMR3TestScene + 默认运动参数（保住已验烟测）；
//       - 其它非 0 值     → 正式内容：加载 C2 的正式地图 + manifest，且 digest 必须与本机 manifest 相等。
//   · 正式模式在**上报 Ready 之前**必须完成：地图加载/组件校验 → 全部 spawn 校验 →
//     名册槽位 + 英雄移速；Ready 回报的 digest 是本局**实际选定**的那个值。
//   · 两条路径都**不猜资源是否存在**：正式缺资源就是失败，诊断也不会偷偷用正式资源。
//
// 语言面：Unity 侧（需要 GameObject/BoxCollider 建场景），但**不使用任何渲染/输入/UI API**。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Logging;
using PMNet;
using PMNet.Control;
using PMNet.Mover;
using PMNet.Prediction;
using PMNet.R3;
using PMNet.Session;
using PMNet.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PMNet.Unity
{
    /// <summary>
    /// R3-B 的固定测试场景布局（DS 与客户端**共用同一份数值**，因此摘要可比）。
    ///
    /// 布局（契约 §7.4 冻结）：平面地板中心 (0,-0.5,0) 尺寸 (40,1,40)；墙中心 (0,1,0) 尺寸 (1,2,8)。
    /// 两侧都用 BoxCollider，因此世界空间里的碰撞盒完全一致。
    ///
    /// 两种建法都产出同一个世界空间盒子，这是有意的：
    ///   - DS（无头、不渲染）：`new GameObject` + 显式 `BoxCollider.center/size`；
    ///   - 客户端：`GameObject.CreatePrimitive(Cube)` + `transform.position/localScale`
    ///     （可见的简单几何，作用是「看得见测试场景确实建起来了」，**不宣称真实玩法表现**）。
    /// </summary>
    public static class PMR3TestScene
    {
        /// <summary>地板中心（世界坐标）。</summary>
        public static readonly Vector3 FloorCenter = new Vector3(0f, -0.5f, 0f);

        /// <summary>地板尺寸。</summary>
        public static readonly Vector3 FloorSize = new Vector3(40f, 1f, 40f);

        /// <summary>墙中心（世界坐标）。</summary>
        public static readonly Vector3 WallCenter = new Vector3(0f, 1f, 0f);

        /// <summary>墙尺寸。</summary>
        public static readonly Vector3 WallSize = new Vector3(1f, 2f, 8f);

        /// <summary>
        /// 建出这套固定布局。成功时 <paramref name="created"/> 追加新建的 GameObject（供宿主销毁）。
        ///
        /// 返回 false 且 <paramref name="error"/> 非空表示**碰撞环境不可用**：
        /// 调用方必须据此拒绝上报 SceneReady（契约 §7.4：「确认 enabled/active 才 SceneReady」）。
        /// </summary>
        public static bool Build(string rootName, bool visiblePrimitives, List<GameObject> created, out string error)
        {
            error = null;

            if (created == null) { throw new ArgumentNullException("created"); }

            GameObject floor;
            GameObject wall;
            Vector3 floorCenter = FloorCenter;
            Vector3 floorSize = FloorSize;
            Vector3 wallCenter = WallCenter;
            Vector3 wallSize = WallSize;

            if (!CreateBox(rootName + "Floor", visiblePrimitives, floorCenter, floorSize, out floor, out error))
            {
                return false;
            }

            created.Add(floor);

            if (!CreateBox(rootName + "Wall", visiblePrimitives, wallCenter, wallSize, out wall, out error))
            {
                return false;
            }

            created.Add(wall);

            if (!ValidateCollider(floor, "地板", out error)) { return false; }
            if (!ValidateCollider(wall, "墙", out error)) { return false; }

            return true;
        }

        private static bool CreateBox(string name, bool visible, Vector3 center, Vector3 size,
                                      out GameObject go, out string error)
        {
            error = null;
            go = null;

            try
            {
                if (visible)
                {
                    // 可见路径：Unity 原语自带 MeshRenderer + BoxCollider；缩放/位置直接决定世界空间碰撞盒。
                    go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.name = name;
                    go.transform.position = center;
                    go.transform.localScale = size;
                    go.SetActive(true);
                }
                else
                {
                    // 无头路径：不建网格，只建碰撞体，避免任何渲染依赖。
                    go = new GameObject(name);
                    BoxCollider box = go.AddComponent<BoxCollider>();
                    if (box != null)
                    {
                        box.center = center;
                        box.size = size;
                        box.enabled = true;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = name + " 创建失败：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }
        }

        private static bool ValidateCollider(GameObject go, string what, out string error)
        {
            error = null;

            if (go == null)
            {
                error = what + " GameObject 为 null";
                return false;
            }

            if (!go.activeInHierarchy || !go.activeSelf)
            {
                error = what + " GameObject 未处于激活态";
                return false;
            }

            BoxCollider box = go.GetComponent<BoxCollider>();
            if (box == null)
            {
                error = what + " 缺少 BoxCollider";
                return false;
            }

            if (!box.enabled)
            {
                error = what + " 的 BoxCollider 未启用（enabled=false）";
                return false;
            }

            if (box.gameObject == null)
            {
                error = what + " 的 BoxCollider 未挂在 GameObject 上";
                return false;
            }

            return true;
        }

        /// <summary>本地物理场景名的自增序号（同一进程内多次建场景必须名字唯一：CreateScene 禁止重名）。</summary>
        private static int _isolatedSceneSequence;

        /// <summary>
        /// R4-B（B3）**隔离**建法：在**运行时创建的本地物理场景**（`LocalPhysicsMode.Physics3D`）里
        /// 建出同一套地板/墙，并把该场景与其 <see cref="PhysicsScene"/> 交给调用方。
        ///
        /// 为什么必须隔离：适配器的 layerMask 取自白名单自身 layer（地板/墙在 Default(0)），
        /// 若查询打在 <see cref="Physics.defaultPhysicsScene"/>，大厅/旧地图里同层 Collider 会参与命中
        /// 甚至顶到查询缓冲饱和（契约 B2：「不能让旧地图、表现胶囊或其它局参与查询」）。
        /// 隔离的**唯一**可靠判据是「物理场景 ≠ Physics.defaultPhysicsScene」，因此返回前显式校验；
        /// 不成立就失败（**绝不**退回默认物理世界假隔离，也不靠改 layer 或放松饱和门绕过）。
        ///
        /// 编辑模式：`SceneManager.CreateScene` 是**运行时** API（2019.4 文档原文 "at runtime"）。
        /// 本方法不假设它在编辑模式可用——照常调用并**用真实 API 结果判定**：抛异常、或
        /// `GetPhysicsScene()` 回退到默认物理世界，都直接失败，由调用方提示「仅 Play 模式可执行」，
        /// 而不是把默认世界当隔离世界。
        ///
        /// 对象落点：`new GameObject` / `CreatePrimitive` 都落在**活动场景**，因此这里临时把活动场景
        /// 切到新建的本地物理场景（Collider 一创建就注册在隔离世界里，不会先落到默认世界），
        /// 并在 finally 里**还原**原活动场景。
        /// </summary>
        public static bool BuildIsolated(string rootName, bool visiblePrimitives, List<GameObject> created,
                                         out Scene scene, out PhysicsScene physicsScene, out string error)
        {
            error = null;
            scene = default(Scene);
            physicsScene = default(PhysicsScene);

            if (created == null) { throw new ArgumentNullException("created"); }

            int sequence = System.Threading.Interlocked.Increment(ref _isolatedSceneSequence);
            string sceneName = rootName + "Physics" + sequence.ToString(CultureInfo.InvariantCulture);

            try
            {
                scene = SceneManager.CreateScene(sceneName, new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            }
            catch (Exception ex)
            {
                scene = default(Scene);
                error = "创建本地物理场景失败：" + ex.GetType().Name + " " + ex.Message
                        + "（SceneManager.CreateScene 是**运行时** API；编辑模式请在 Play 模式下运行）";
                return false;
            }

            if (!scene.IsValid() || !scene.isLoaded)
            {
                error = "本地物理场景无效（IsValid=" + scene.IsValid() + " isLoaded=" + scene.isLoaded + "）";
                scene = default(Scene);
                return false;
            }

            physicsScene = scene.GetPhysicsScene();
            if (!physicsScene.IsValid())
            {
                error = "本地物理场景没有可用的 PhysicsScene（IsValid=false）";
                ReleaseIsolatedScene(scene);
                scene = default(Scene);
                physicsScene = default(PhysicsScene);
                return false;
            }

            if (physicsScene.Equals(Physics.defaultPhysicsScene))
            {
                error = "本地物理场景回退到了默认物理世界（GetPhysicsScene() == Physics.defaultPhysicsScene）："
                        + "本平台/模式下不支持隔离物理世界，拒绝把默认世界当隔离世界（编辑模式请在 Play 模式下运行）";
                ReleaseIsolatedScene(scene);
                scene = default(Scene);
                physicsScene = default(PhysicsScene);
                return false;
            }

            Scene previousActive = SceneManager.GetActiveScene();
            bool switched = false;
            int createdStart = created.Count;
            bool built;

            try
            {
                switched = SceneManager.SetActiveScene(scene);
                built = Build(rootName, visiblePrimitives, created, out error);

                if (built)
                {
                    // 活动场景切换可能不生效（例如场景未就绪）：显式搬迁 + 校验对象确实在隔离场景里。
                    built = EnsureCreatedInScene(created, createdStart, scene, out error);
                }
            }
            catch (Exception ex)
            {
                built = false;
                error = rootName + " 场景对象建立异常：" + ex.GetType().Name + " " + ex.Message;
            }
            finally
            {
                if (switched)
                {
                    // 契约：活动场景语义属于宿主/用户，测试场景不得留在活动位。
                    SceneManager.SetActiveScene(previousActive);
                }
            }

            if (!built)
            {
                // 失败由本函数释放场景（对象由调用方按 created 列表释放，避免双重销毁的语义歧义）。
                ReleaseIsolatedScene(scene);
                scene = default(Scene);
                physicsScene = default(PhysicsScene);
                return false;
            }

            physicsScene = scene.GetPhysicsScene();
            return true;
        }

        /// <summary>
        /// 把 `[createdStart, created.Count)` 段的新建对象**显式**搬进隔离场景，并校验它们确实在那里。
        ///
        /// 为什么需要：`new GameObject` / `CreatePrimitive` 都落在**活动场景**。正常路径靠临时
        /// `SetActiveScene` 让它们直接出生在隔离场景；但活动场景切换可能不生效（例如场景未就绪），
        /// 那时必须显式搬迁 + 校验，否则就是“以为隔离了、其实建到了宿主/用户场景”。
        /// </summary>
        private static bool EnsureCreatedInScene(List<GameObject> created, int createdStart, Scene scene, out string error)
        {
            error = null;

            for (int i = createdStart; i < created.Count; i++)
            {
                GameObject go = created[i];
                if (go == null) { continue; }

                if (!go.scene.IsValid() || go.scene.handle != scene.handle)
                {
                    SceneManager.MoveGameObjectToScene(go, scene);
                }
            }

            for (int i = createdStart; i < created.Count; i++)
            {
                GameObject go = created[i];
                if (go == null) { continue; }

                if (!go.scene.IsValid() || go.scene.handle != scene.handle)
                {
                    error = "测试对象没有进入隔离物理场景（scene mismatch，对象=" + go.name + "）";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 释放 <see cref="BuildIsolated"/> 建出的本地物理场景（幂等、不抛）。
        ///
        /// 只用**运行时** <see cref="SceneManager.UnloadSceneAsync(Scene)"/>：本类同时被 DS 构建与
        /// 客户端编译，**不允许**引用 UnityEditor。编辑模式下的关闭由 Editor 验证入口自己处理。
        /// </summary>
        public static void ReleaseIsolatedScene(Scene scene)
        {
            try
            {
                if (scene.IsValid() && scene.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(scene);
                }
            }
            catch (Exception)
            {
                // 清理路径不容许再抛：释放失败也不能把宿主带崩（后续故障由宿主日志体现）。
            }
        }

        /// <summary>
        /// 从 <see cref="Build"/> 建出来的对象里收集 Collider，作为运动碰撞查询的**白名单**
        /// （契约 B2：「宿主把新建测试地板/墙传入隔离旧场景；不能让旧地图、表现胶囊
        /// 或其它局参与查询」）。
        ///
        /// 按 <see cref="Build"/> 的固定顺序写回：先地板、后墙。缺失/未启用时返回 false 并给出原因
        /// —— 白名单不完整时查询会漏掉墙，必须显式失败而不是静默降级。
        /// </summary>
        public static bool TryCollectColliders(List<GameObject> created, Collider[] into,
                                               out int count, out string error)
        {
            error = null;
            count = 0;

            if (into == null) { throw new ArgumentNullException("into"); }
            if (created == null || created.Count < 2)
            {
                error = "场景对象不足两个（地板 + 墙）";
                return false;
            }

            for (int i = 0; i < 2; i++)
            {
                GameObject go = created[i];
                if (go == null)
                {
                    error = (i == 0 ? "地板" : "墙") + " GameObject 为 null";
                    return false;
                }

                Collider collider = go.GetComponent<Collider>();
                if (collider == null)
                {
                    error = (i == 0 ? "地板" : "墙") + " 缺少 Collider";
                    return false;
                }

                if (!collider.enabled)
                {
                    error = (i == 0 ? "地板" : "墙") + " 的 Collider 未启用";
                    return false;
                }

                into[i] = collider;
                count++;
            }

            return true;
        }
    }

    /// <summary>
    /// DS 侧一局的会话宿主（契约 §7.4）。
    ///
    /// 由 <see cref="PMDsHost"/> 在 `-bootstrap` 非空时创建并驱动；旧诊断路径（PMUdpRouter）
    /// **绝不会**与本类同时启动（带 bootstrap 时旧路径被显式跳过）。
    /// </summary>
    public sealed class PMDsSessionHost : IDisposable
    {
        /// <summary>smoke 验收的探针完成期限（毫秒）。显式 smoke 模式下超期即失败，不静默挂着。</summary>
        public const int SmokeDeadlineMs = 120000;

        /// <summary>心跳日志间隔（秒）。</summary>
        private const float HeartbeatLogIntervalSeconds = 5f;

        /// <summary>
        /// R4-B（B3）：**诊断模式**下本局运动碰撞世界的版本号。
        ///
        /// 两侧（DS 与客户端）必须用**同一个值**，因为快照携带的 Aux.CollisionWorldVersion
        /// 会被接收侧比对，不一致就直接拒绝并请求重同步（契约 §B1）。这里显式写 1，
        /// 而不是从 PMMoverTestWorld 的 FrozenCollisionWorldVersion（0x52334201）取：
        /// 后者是 net8 测试替身的几何版本号，与 Unity 侧适配器是两套东西。
        ///
        /// R4-C（C3）：**正式内容模式不使用本常量**，而用 manifest 里的 `worldVersion`
        /// （见 <see cref="_selectedWorldVersion"/>）——那才是同一份地图两端同值的口径。
        /// </summary>
        public const int MovementWorldVersion = 1;

        /// <summary>名册内相邻两行在 Z 轴上的间距（米）。只用于把同队玩家错开，避免重叠。</summary>
        private const float RosterRowSpacingMeters = 1.5f;

        /// <summary>出生点距墙中心的水平偏移（米）。墙盒 x∈[-0.5,0.5]，±3 保证在墙外。</summary>
        private const float SpawnLateralOffsetMeters = 3f;

        private PMNetLaunchOptions _options;
        private PMDsBootstrappedMatch _boot;
        private PMSession _session;
        private PMNetWorld _world;
        private PMNetSessionBridge _bridge;
        private PMUdpSessionEndpoint _endpoint;
        private PMDsLobbyAgent _lobby;

        private readonly List<GameObject> _sceneObjects = new List<GameObject>();
        private readonly Dictionary<int, PMR3Player> _playersByUid = new Dictionary<int, PMR3Player>();
        private readonly HashSet<int> _expectedUids = new HashSet<int>();

        // ---- R4-B（B3）：每玩家运动权威驱动 ----

        /// <summary>碰撞查询（与客户端同一套几何 + 同一 WorldVersion；白名单 = 地板 + 墙）。</summary>
        private PMUnityMoverCollisionQuery _movementQuery;

        /// <summary>
        /// R4-B（B3）：地板/墙所在的**本地物理场景**（`LocalPhysicsMode.Physics3D`）。
        /// 它才是碰撞隔离的载体：查询必须打在这个场景，而不是 <see cref="Physics.defaultPhysicsScene"/>。
        /// 失效场景（default）表示本局没有隔离世界，必须在 Dispose 里尝试释放。
        /// </summary>
        private Scene _movementScene;

        /// <summary>与 <see cref="_movementScene"/> 对应的物理场景（构造查询时显式校验并传入）。</summary>
        private PhysicsScene _movementPhysicsScene;

        // ---- R4-C（C3）：显式会话模式（诊断 vs 正式内容） ----

        /// <summary>
        /// 正式内容地图（仅正式模式非空）。它持有隔离物理场景、白名单与查询，
        /// 因此释放必须走 <see cref="PMUnityBattleMap.Dispose"/>。
        /// </summary>
        private PMUnityBattleMap _battleMap;

        /// <summary>本局是否走正式内容（false = 诊断 PMR3TestScene）。</summary>
        private bool _contentFormal;

        /// <summary>本局**实际选定**并上报的碰撞摘要（诊断 = 保留值；正式 = manifest 值）。</summary>
        private uint _selectedCollisionDigest;

        /// <summary>本局运动碰撞世界版本（诊断 = <see cref="MovementWorldVersion"/>；正式 = manifest.worldVersion）。</summary>
        private int _selectedWorldVersion;

        /// <summary>正式模式：uid → 已校验的出生状态（含该英雄 <c>MaxSpeed</c>）。</summary>
        private readonly Dictionary<int, PMMoverSyncState> _formalSpawnByUid = new Dictionary<int, PMMoverSyncState>();

        /// <summary>正式模式：uid → 规范槽位（由 TeamId/PlayerId/Uid 稳定排序决定，0..2）。</summary>
        private readonly Dictionary<int, int> _formalSlotByUid = new Dictionary<int, int>();

        /// <summary>正式模式：uid → teamIndex（TeamId 2 → 0(+X) / 1 → 1(-X)）。</summary>
        private readonly Dictionary<int, int> _formalTeamIndexByUid = new Dictionary<int, int>();

        /// <summary>正式模式：uid → 该英雄的合法移速（来自 Shared/BattleNumericConfig，**不信客户端自报**）。</summary>
        private readonly Dictionary<int, float> _formalMaxSpeedByUid = new Dictionary<int, float>();

        /// <summary>uid → 运动 Driver（只含已创建副本的玩家）。</summary>
        private readonly Dictionary<int, PMR4MovementDriver> _driversByUid = new Dictionary<int, PMR4MovementDriver>();

        /// <summary>uid → 名册下标（决定出生的 Z 行）。</summary>
        private readonly Dictionary<int, int> _rosterRowByUid = new Dictionary<int, int>();

        /// <summary>uid → 名册队伍号（决定出生的 X 侧）。</summary>
        private readonly Dictionary<int, int> _rosterTeamByUid = new Dictionary<int, int>();

        /// <summary>名册人数（决定 Z 行居中量）。</summary>
        private int _rosterCount;

        /// <summary>DS 自己决定的权威服务帧（独立于客户端预测帧，不与 AP 帧做算术）。</summary>
        private PMFrameId _movementServerFrame;

        /// <summary>宿主 tick 序号（显式传给 Driver，同一 tick 不重复充值信用）。</summary>
        private long _movementHostTickId;

        /// <summary>上一次运动 Pump 的墙钟（毫秒），用于算实际 elapsed。</summary>
        private long _lastMovementPumpMs;

        private bool _sceneReady;
        private bool _faulted;
        private string _faultReason;
        private bool _disposed;
        private bool _smokeSubmitted;
        private long _smokeDeadlineMs;
        private bool _playerDropReported;
        private long _tickCount;
        private float _nextHeartbeatLogTime;
        private Action<string> _previousRuntimeWarn;

        /// <summary>退出请求（转发给 UnityEngine.Application.Quit 的唯一出口）。</summary>
        public Action<int> ExitRequested;

        /// <summary>日志（诊断摘要）出口。</summary>
        public Action<string> Log;

        /// <summary>本局实际绑定并上报的 UDP 端口。</summary>
        public int BoundPort { get; private set; }

        /// <summary>权威世界（诊断/门禁）。</summary>
        public PMNetWorld World { get { return _world; } }

        /// <summary>会话桥（诊断/门禁）。</summary>
        public PMNetSessionBridge Bridge { get { return _bridge; } }

        /// <summary>UDP 入局端点（诊断/门禁）。</summary>
        public PMUdpSessionEndpoint Endpoint { get { return _endpoint; } }

        /// <summary>控制通道代理（诊断/门禁）。</summary>
        public PMDsLobbyAgent Lobby { get { return _lobby; } }

        /// <summary>碰撞场景是否已建立并通过校验。</summary>
        public bool SceneReady { get { return _sceneReady; } }

        /// <summary>是否已进入明确失败态。</summary>
        public bool IsFaulted { get { return _faulted; } }

        /// <summary>失败原因。</summary>
        public string FaultReason { get { return _faultReason; } }

        /// <summary>已创建的玩家副本数。</summary>
        public int PlayerCount { get { return _playersByUid.Count; } }

        // =================================================================================
        //  启动
        // =================================================================================

        /// <summary>
        /// 启动新链 DS 宿主。失败返回 null 并写 <paramref name="error"/>（不抛）。
        /// 调用方（<see cref="PMDsHost"/>）在失败时应以非 0 退出码结束进程。
        /// </summary>
        public static PMDsSessionHost Start(PMNetLaunchOptions options, out string error)
        {
            error = null;

            if (options == null) { error = "启动参数为 null"; return null; }

            PMDsSessionHost host = new PMDsSessionHost();
            if (!host.Initialize(options, out error))
            {
                host.Dispose();
                return null;
            }

            return host;
        }

        private bool Initialize(PMNetLaunchOptions options, out string error)
        {
            error = null;
            _options = options;

            // 无头进程要求后台运行（与 PMDsHost 的旧路径同一取向）。
            Application.runInBackground = true;

            // 1) 引导文件：只传路径，密钥从不经过命令行（契约 §2）。
            if (string.IsNullOrEmpty(options.BootstrapPath))
            {
                error = "缺少 -bootstrap 引导文件路径";
                return false;
            }

            byte[] document;
            try
            {
                document = File.ReadAllBytes(options.BootstrapPath);
            }
            catch (Exception ex)
            {
                error = "读取引导文件失败：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            PMDsBootstrappedMatch boot;
            string decodeError;
            if (!PMDsBootstrapDocument.TryDecode(document, out boot, out decodeError))
            {
                error = "引导文件解码失败：" + decodeError;
                return false;
            }

            _boot = boot;

            // 2) 局标识与摘要校验（引导文件是权威，命令行只是搬运）。
            if (!string.Equals(boot.MatchId, options.MatchId, StringComparison.Ordinal))
            {
                error = "引导文件 MatchId 与 -matchid 不一致（'" + boot.MatchId + "' vs '"
                        + (options.MatchId ?? "<none>") + "'）";
                return false;
            }

            if (!string.Equals(boot.DsId, options.DsId, StringComparison.Ordinal))
            {
                error = "引导文件 DsId 与 -dsid 不一致（'" + boot.DsId + "' vs '"
                        + (options.DsId ?? "<none>") + "'）";
                return false;
            }

            if (boot.Epoch == 0u)
            {
                error = "引导文件 Epoch 为 0（非法世代）";
                return false;
            }

            if (boot.Bootstrap == null || boot.Bootstrap.AsBootstrap == null)
            {
                error = "引导文件缺少 Bootstrap 业务体";
                return false;
            }

            // 3) 注册生成表 → 校验协议摘要。顺序不可换：摘要来自生成表的 Seal。
            PMR3Runtime.Register();
            _previousRuntimeWarn = PMR3Runtime.Warn;
            PMR3Runtime.Warn = OnRuntimeWarn;

            if (PMR3Runtime.ProtocolHash == 0u)
            {
                error = "PMR3 协议摘要为 0：生成表未注册（PMR3Runtime.Register 失败）";
                return false;
            }

            if (boot.ProtocolHash != PMR3Runtime.ProtocolHash)
            {
                error = "协议摘要不一致：引导文件 0x" + boot.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture)
                        + "，本机 0x" + PMR3Runtime.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture)
                        + "（Lobby 必须用 PMR3Runtime.ProtocolHash 分配局）";
                return false;
            }

            // 4) 端口：引导 body 不带端口，因此端口来自 -port，并由 Ready.BoundPort 回报给 Lobby 对账。
            int port = options.ListenPort;
            if (port <= 0 || port > 65535)
            {
                error = "缺少有效 -port（实际 " + port.ToString(CultureInfo.InvariantCulture)
                        + "）：新链由 Lobby 独占分配端口";
                return false;
            }

            // 4) 显式会话模式（R4-C / C3）：引导文件里的 CollisionDigest 是唯一选择来源。
            //    0 禁止；保留值 0x52334201 = 诊断场景；其它非 0 = 正式内容。
            //    **不按资源是否存在猜模式**：正式内容缺 manifest/地图就是失败。
            if (boot.CollisionDigest == 0u)
            {
                error = "引导文件 CollisionDigest 为 0（禁止）：新链必须显式选择正式内容或诊断保留摘要";
                return false;
            }

            if (boot.CollisionDigest == PMR3Runtime.CollisionDigest)
            {
                // 诊断：保留 R3-B 已验烟测（PMR3TestScene + 默认运动参数），不碰正式资源。
                _contentFormal = false;
                _selectedCollisionDigest = PMR3Runtime.CollisionDigest;
                _selectedWorldVersion = MovementWorldVersion;
            }
            else
            {
                PMBattleContentManifest manifest;
                string manifestError;
                if (!PMUnityBattleMap.TryLoadManifest(out manifest, out manifestError))
                {
                    error = "正式内容 manifest 不可用（" + manifestError
                            + "）：拒绝以诊断内容冒充正式（不回退旧链）";
                    return false;
                }

                if (manifest.collisionDigest == 0u
                    || manifest.collisionDigest == PMR3Runtime.CollisionDigest)
                {
                    error = "正式内容 manifest 的 collisionDigest=0x"
                            + manifest.collisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                            + " 与 0/保留诊断值 0x"
                            + PMR3Runtime.CollisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                            + " 冲突：正式内容不得撞保留值";
                    return false;
                }

                if (manifest.collisionDigest != boot.CollisionDigest)
                {
                    error = "正式内容摘要不一致：引导文件 0x"
                            + boot.CollisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                            + "，本机 manifest 0x"
                            + manifest.collisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                            + "（Lobby 必须用同一份 manifest 分配局）";
                    return false;
                }

                _contentFormal = true;
                _selectedCollisionDigest = manifest.collisionDigest;
                _selectedWorldVersion = manifest.worldVersion;
            }

            // 5) 名册：期望的 uid 集合（Ready 之前不依赖玩家是否已连上）。
            PMDsBootstrapPlayer[] roster = boot.Bootstrap.AsBootstrap.Players;
            if (roster == null || roster.Length == 0)
            {
                error = "引导文件名册为空";
                return false;
            }

            for (int i = 0; i < roster.Length; i++)
            {
                if (roster[i].Identity.Uid > 0)
                {
                    _expectedUids.Add(roster[i].Identity.Uid);
                }

                // R4-B（B3）：名册行/队是**出生点**的唯一依据（不从客户端包自报位置采纳）。
                // 名册在引导文件里是可信输入，因此这是稳定的：同一个 uid 每局都落在同一行。
                if (!_rosterRowByUid.ContainsKey(roster[i].Identity.Uid))
                {
                    _rosterRowByUid[roster[i].Identity.Uid] = i;
                    _rosterTeamByUid[roster[i].Identity.Uid] = roster[i].Identity.TeamId;
                }
            }

            _rosterCount = roster.Length;

            if (_expectedUids.Count == 0)
            {
                error = "引导文件名册没有合法 uid";
                return false;
            }

            // R4-C（C3）正式模式：名册规范化（队伍/槽位/英雄），出生几何在建图后核对。
            if (_contentFormal)
            {
                string rosterError;
                if (!BuildFormalRoster(roster, out rosterError))
                {
                    error = rosterError;
                    return false;
                }
            }

            // 6) 世界 / 桥 / 运行时接线。
            _session = new PMSession(boot.Epoch, true);
            _world = new PMNetWorld(_session);
            _world.Warn = OnWorldWarn;
            _bridge = new PMNetSessionBridge(_world);
            _bridge.Warn = OnWorldWarn;

            PMR3Runtime.Attach(_world, _bridge);
            PMR3Runtime.PlayerSpawned += OnPlayerSpawned;

            // 7) 运动碰撞世界：**先校验再谈就绪**。
            //     R4-C（C3）：诊断模式 = R3-B 的固定测试场景（保留已验烟测）；
            //     正式模式 = C2 的正式地图。两条路径都必须在**本地物理场景**里
            //     （LocalPhysicsMode.Physics3D）——这是碰撞隔离的唯一可靠形式
            //     （不能靠 layer/白名单“后置过滤”）。
            //     正式模式另外要求：**全部 spawn 校验通过**之后才能把 _sceneReady 置位。
            if (_contentFormal)
            {
                PMUnityBattleMap battleMap;
                string mapError;
                if (!PMUnityBattleMap.TryLoad(out battleMap, out mapError))
                {
                    error = "正式内容地图加载失败（拒绝以诊断内容冒充正式）：" + mapError;
                    return false;
                }

                _battleMap = battleMap;
                _movementScene = battleMap.Scene;
                _movementPhysicsScene = battleMap.PhysicsScene;
                _movementQuery = battleMap.Query;

                string consistencyError;
                if (!battleMap.ValidateManifestConsistency(out consistencyError))
                {
                    error = "正式地图与 manifest 不一致：" + consistencyError;
                    return false;
                }

                string spawnCheckError;
                if (!battleMap.TryValidateAllSpawns(out spawnCheckError))
                {
                    error = "正式地图出生位自检失败（拒绝在几何不可用的地图上开局）：" + spawnCheckError;
                    return false;
                }

                string assignmentError;
                if (!BuildFormalSpawns(out assignmentError))
                {
                    error = assignmentError;
                    return false;
                }
            }
            else
            {
                string sceneError;
                Scene movementScene;
                PhysicsScene movementPhysicsScene;
                if (!PMR3TestScene.BuildIsolated("[PMDsAuthority]", false, _sceneObjects,
                                                out movementScene, out movementPhysicsScene, out sceneError))
                {
                    error = "固定测试碰撞场景不可用（本地物理场景隔离失败）：" + sceneError;
                    return false;
                }

                _movementScene = movementScene;
                _movementPhysicsScene = movementPhysicsScene;

                // 诊断白名单**必须**是刚建的地板/墙两个 Collider（契约 B2）：
                // 不能让旧地图、表现胶囊或其它局参与查询。
                Collider[] allowlist = new Collider[2];
                int allowCount;
                string allowError;
                if (!PMR3TestScene.TryCollectColliders(_sceneObjects, allowlist, out allowCount, out allowError))
                {
                    error = "运动碰撞白名单不完整：" + allowError;
                    return false;
                }

                if (allowCount != 2)
                {
                    error = "运动碰撞白名单应恰好为地板 + 墙两个 Collider，实际 "
                            + allowCount.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                int movementLayerMask = 0;
                for (int i = 0; i < allowCount; i++)
                {
                    if (allowlist[i] == null || allowlist[i].gameObject == null)
                    {
                        error = "运动碰撞白名单第 " + i.ToString(CultureInfo.InvariantCulture) + " 项无效";
                        return false;
                    }

                    movementLayerMask |= 1 << allowlist[i].gameObject.layer;
                }

                try
                {
                    _movementQuery = new PMUnityMoverCollisionQuery(_movementPhysicsScene, movementLayerMask,
                                                                   _selectedWorldVersion, allowlist);
                }
                catch (Exception ex)
                {
                    error = "运动碰撞查询构造失败：" + ex.GetType().Name + " " + ex.Message;
                    return false;
                }
            }

            // 7b) 运动碰撞查询：**必须**绑定到隔离物理场景，且 WorldVersion 与本局选定口径一致
            //     （诊断 = 1；正式 = manifest.worldVersion）。用默认物理场景构造（旧写法）
            //     等于把整张旧地图当作碰撞世界。
            if (!_movementPhysicsScene.IsValid() || _movementPhysicsScene.Equals(Physics.defaultPhysicsScene))
            {
                error = "运动碰撞隔离失败：本地物理场景无效或等于 Physics.defaultPhysicsScene";
                return false;
            }

            if (_movementQuery == null)
            {
                error = "运动碰撞查询未建立";
                return false;
            }

            if (!_movementQuery.Scene.IsValid() || _movementQuery.Scene.Equals(Physics.defaultPhysicsScene))
            {
                error = "运动碰撞查询未绑定到隔离物理场景（sceneValid=" + _movementQuery.Scene.IsValid()
                        + " isDefaultWorld=" + _movementQuery.Scene.Equals(Physics.defaultPhysicsScene) + "）";
                return false;
            }

            if (_movementQuery.WorldVersion != _selectedWorldVersion || !_movementQuery.HasAllowlist)
            {
                error = "运动碰撞查询未按预期建立（worldVersion=" + _movementQuery.WorldVersion
                        + " 期望=" + _selectedWorldVersion.ToString(CultureInfo.InvariantCulture)
                        + " allowlist=" + _movementQuery.AllowlistCount.ToString(CultureInfo.InvariantCulture) + "）";
                return false;
            }

            // 到这里地图/碰撞/出生位全部校验通过，场景才算就绪（**才**允许上报 Ready）。
            _sceneReady = true;

            // 8) UDP 入局端点（§7.2）：所有收发包由 Pump 驱动，不开后台线程。
            try
            {
                _endpoint = PMUdpSessionEndpoint.OpenServer(boot, _bridge, options.ListenAddress, port);
            }
            catch (Exception ex)
            {
                error = "OpenServer 失败：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            BoundPort = _endpoint.BoundPort;
            if (BoundPort != port)
            {
                // 「端口与分配一致」是 Lobby 发布地址的前置条件；不一致就不该上报就绪。
                error = "实际绑定端口 " + BoundPort.ToString(CultureInfo.InvariantCulture)
                        + " 与分配端口 " + port.ToString(CultureInfo.InvariantCulture) + " 不一致";
                return false;
            }

            _endpoint.Connected += OnConnected;
            _endpoint.Failed += OnEndpointFailed;

            // 9) 控制通道（Lobby 侧 listener 只 loopback）。
            if (string.IsNullOrEmpty(options.ControlHost))
            {
                error = "缺少 -control 主机：新链必须与 Lobby 控制通道通信";
                return false;
            }

            if (options.ControlPort <= 0 || options.ControlPort > 65535)
            {
                error = "缺少有效 -control 端口（实际 " + options.ControlPort.ToString(CultureInfo.InvariantCulture) + "）";
                return false;
            }

            try
            {
                // R4-C（C3）：Ready 回报的是本局**实际选定**的摘要（诊断=保留值；正式=manifest 值）。
                _lobby = new PMDsLobbyAgent(boot, options.ControlHost, options.ControlPort,
                                           BoundPort, _selectedCollisionDigest);
            }
            catch (Exception ex)
            {
                error = "控制通道代理构造失败：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            _lobby.SceneReady = _sceneReady;
            _lobby.Log = OnLobbyLog;
            _lobby.Warn = OnLobbyWarn;
            _lobby.ExitRequested = OnLobbyExitRequested;
            _lobby.PlayerCountProvider = delegate { return _playersByUid.Count; };

            string startError;
            if (!_lobby.Start(out startError))
            {
                error = "控制通道连接失败：" + startError;
                return false;
            }

            Info("===== R3-B 新链 DS 就绪 =====");
            Info("matchid=" + boot.MatchId + " dsid=" + boot.DsId
                 + " epoch=" + boot.Epoch.ToString(CultureInfo.InvariantCulture)
                 + " hash=0x" + PMR3Runtime.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture)
                 + " digest=0x" + _selectedCollisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                 + " WorldVersion=" + _selectedWorldVersion.ToString(CultureInfo.InvariantCulture));
            Info("UDP boundPort=" + BoundPort.ToString(CultureInfo.InvariantCulture)
                 + " 名册人数=" + _expectedUids.Count.ToString(CultureInfo.InvariantCulture)
                 + " 控制通道=" + _lobby.ControlEndpoint
                 + (_options.ServerSmoke ? " serverSmoke=on" : " serverSmoke=off（默认不自动胜利）"));
            if (_contentFormal)
            {
                Info("会话模式=正式内容（manifest）白名单="
                     + (_movementQuery != null ? _movementQuery.AllowlistCount.ToString(CultureInfo.InvariantCulture) : "0")
                     + "（Collider 白名单，不含 trigger）box="
                     + (_battleMap != null ? _battleMap.BoxColliderCount.ToString(CultureInfo.InvariantCulture) : "0")
                     + " nonBox=" + (_battleMap != null ? _battleMap.NonBoxColliderCount.ToString(CultureInfo.InvariantCulture) : "0")
                     + " 隔离=本地物理场景(" + (_movementScene.IsValid() ? _movementScene.name : "<none>")
                     + " isDefaultWorld=" + _movementPhysicsScene.Equals(Physics.defaultPhysicsScene) + ")");
                Info("正式出生名册：队伍≤2、每队≤3、队内按 PlayerId/Uid 定槽；出生位来自 PMUnityBattleMap.TryGetSpawn；"
                     + "移速来自 Shared/BattleNumericConfig（不信客户端自报）");
                Info("正式出生位对账：" + (_battleMap != null ? _battleMap.DescribeSpawns() : "<no map>"));
            }
            else
            {
                Info("会话模式=诊断（PMR3TestScene，保留摘要 0x"
                     + PMR3Runtime.CollisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                     + "）：地板/墙 BoxCollider enabled+active");
                Info("R4-B 运动接线：碰撞白名单="
                     + (_movementQuery != null ? _movementQuery.AllowlistCount.ToString(CultureInfo.InvariantCulture) : "0")
                     + "（地板+墙） WorldVersion=" + _selectedWorldVersion.ToString(CultureInfo.InvariantCulture)
                     + " 隔离=本地物理场景(" + (_movementScene.IsValid() ? _movementScene.name : "<none>")
                     + " isDefaultWorld=" + _movementPhysicsScene.Equals(Physics.defaultPhysicsScene)
                     + ") 每帧 Physics.SyncTransforms 一次 + 逐副本 authority Pump（预算 8步/100ms，实际墙钟）");
                Info("R4-B 出生规则：x=±" + SpawnLateralOffsetMeters.ToString("0.###", CultureInfo.InvariantCulture)
                     + "（墙外） z=名册行居中×" + RosterRowSpacingMeters.ToString("0.###", CultureInfo.InvariantCulture)
                     + " y=" + PMMoverDefaults.CapsuleHalfHeightMeters.ToString("0.###", CultureInfo.InvariantCulture)
                     + "（禁止默认原点：原点在测试墙盒内部）");
            }

            Info("============================");
            return true;
        }

        // =================================================================================
        //  主线程驱动
        // =================================================================================

        /// <summary>
        /// 主线程帧步（由 <see cref="PMDsHost.Update"/> 唯一调用）。
        ///
        /// 次序：UDP 端点 Pump（内部已含 bridge.Update，**不要再调一次**）→ smoke 判定 → 控制通道 Pump。
        /// </summary>
        public void Pump()
        {
            if (_disposed || _faulted) { return; }

            long nowMs = (long)(Time.realtimeSinceStartup * 1000f);
            long nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            _tickCount++;

            if (_endpoint == null) { Fail("UDP 端点未建立"); return; }

            // R4-B（B3）契约：「主线程在 endpoint.Pump 前 Physics.SyncTransforms 一次」。
            // 为什么必须在 Pump **之前**：本帧入站的生命周期/RPC 会触发 Spawn / 入场，
            // 而运动碰撞查询（CapsuleCast 等）读的是 PhysX 的场景级加速结构。
            // 新建的 Collider 必须在本帧任何查询之前就位（否则会出现"墙建了但本帧不挡人"）。
            // 适配器自己**永不**调它（契约 B2），因此这里是唯一调用点，且每帧只调一次。
            Physics.SyncTransforms();

            _endpoint.Pump(nowMs, nowUnixSeconds);
            if (_faulted) { return; }

            PumpMovement(nowMs);
            if (_faulted) { return; }

            CheckPlayerDrops();
            CheckSmoke(nowMs);

            if (_lobby != null)
            {
                _lobby.Pump(nowMs, nowUnixSeconds);
            }

            if (Time.unscaledTime >= _nextHeartbeatLogTime)
            {
                _nextHeartbeatLogTime = Time.unscaledTime + HeartbeatLogIntervalSeconds;
                LogHeartbeat();
            }
        }

        // =================================================================================
        //  R4-B（B3）：每玩家权威运动驱动
        // =================================================================================

        /// <summary>
        /// 每宿主帧驱动全部玩家副本的权威运动。
        ///
        /// 次序（契约 §B3）：Pump 已完成、桥已 Update（不需要也不允许再次调 bridge.Update，
        /// 否则会抢数据报预算）→ 逐个副本 Update（消费入站）→ Pump（按**实际墙钟**推进信用）。
        ///
        /// **服务器无输入、无表现**：这里不做任何输入采样，也不创建任何表现对象。
        /// 客户端输入只能通过 <c>ServerMovementInputV1</c>（不可靠）上线，而它先入队、
        /// 由 Driver 在自己的步进入口消费——宿主不直接调业务实现冒充输入。
        /// </summary>
        private void PumpMovement(long nowMs)
        {
            if (_movementQuery == null) { return; }
            if (_driversByUid.Count == 0)
            {
                _lastMovementPumpMs = nowMs;
                return;
            }

            double elapsedMs = 0.0;
            if (_lastMovementPumpMs != 0L && nowMs > _lastMovementPumpMs)
            {
                elapsedMs = (double)(nowMs - _lastMovementPumpMs);
            }

            _lastMovementPumpMs = nowMs;

            // 一个宿主 tick = 一个明确的序号 + 一个 DS 自己的权威服务帧。
            // 两者都与客户端预测帧无关：契约禁止用客户端自报帧号推进服务器进度。
            _movementHostTickId++;
            _movementServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, _movementServerFrame.Value + 1L);

            List<int> uids = new List<int>(_driversByUid.Keys);
            for (int i = 0; i < uids.Count; i++)
            {
                PMR4MovementDriver driver;
                if (!_driversByUid.TryGetValue(uids[i], out driver) || driver == null) { continue; }
                if (driver.IsDisposed) { continue; }

                try
                {
                    driver.Update(elapsedMs);
                    driver.Pump(elapsedMs, _movementServerFrame, _movementHostTickId);
                }
                catch (Exception ex)
                {
                    Fail("运动驱动异常（uid=" + uids[i].ToString(CultureInfo.InvariantCulture) + "）："
                         + ex.GetType().Name + " " + ex.Message);
                    return;
                }
            }
        }

        /// <summary>
        /// 断线收尾：连接不再就绪的副本一律**先 Freeze 再 Dispose**（契约 §B3）。
        ///
        /// Freeze 在前不是形式：Freeze 让 AP 侧进入"只等显式重新同步"、不再推进预测；
        /// 直接 Dispose 会让一个正在被引用（可能还有本帧待处理入站）的 Driver 突然失效。
        /// </summary>
        private void PruneDisconnectedDrivers()
        {
            if (_driversByUid.Count == 0) { return; }

            List<int> uids = new List<int>(_driversByUid.Keys);
            for (int i = 0; i < uids.Count; i++)
            {
                int uid = uids[i];

                PMR3Player player;
                if (!_playersByUid.TryGetValue(uid, out player) || player == null)
                {
                    continue;
                }

                if (player.OwnerConnection != null && player.OwnerConnection.IsReady)
                {
                    continue;
                }

                PMR4MovementDriver driver;
                if (!_driversByUid.TryGetValue(uid, out driver) || driver == null)
                {
                    _driversByUid.Remove(uid);
                    continue;
                }

                try
                {
                    driver.Freeze();
                    driver.Dispose();
                }
                catch (Exception ex)
                {
                    Warn("断线驱动收尾异常（uid=" + uid.ToString(CultureInfo.InvariantCulture) + "）："
                         + ex.GetType().Name);
                }

                _driversByUid.Remove(uid);
                Warn("玩家断线：运动驱动已 Freeze + Dispose（uid=" + uid.ToString(CultureInfo.InvariantCulture) + "）");
            }
        }

        /// <summary>
        /// 按名册给 uid 算一个**稳定的、墙外**的出生状态（契约 §B3）。
        ///
        /// 规则：x = ±3（按队伍取侧；队伍未知时按名册行奇偶取侧），z = 名册行居中后 × 间距，
        /// y = 胶囊半高（正好站在地板上）。
        ///
        /// **为什么不能直接用 <see cref="PMMoverSyncState.CreateDefault"/>**：它的位置是 (0,1,0)，
        /// 而测试墙盒恰好是 x∈[-0.5,0.5]、y∈[0,2]、z∈[-4,4]——原点在**墙内**，
        /// 一出生就被墙包裹（扫掠会立即被阻挡，"输入驱动位移"根本测不出来）。
        /// 这个坑在 R4-B / B1 报告里已登记，这里显式避开。
        /// </summary>
        private PMMoverSyncState BuildSpawnSync(int uid)
        {
            // R4-C（C3）正式模式：出生几何 + 英雄移速都来自**已校验的正式分配**。
            PMMoverSyncState formal;
            if (_contentFormal && _formalSpawnByUid.TryGetValue(uid, out formal))
            {
                return formal;
            }

            PMMoverSyncState state = PMMoverSyncState.CreateDefault();

            int row;
            if (!_rosterRowByUid.TryGetValue(uid, out row))
            {
                row = 0;
            }

            int team;
            if (!_rosterTeamByUid.TryGetValue(uid, out team))
            {
                team = 0;
            }

            float sign;
            if (team == 1) { sign = -1f; }
            else if (team == 2) { sign = 1f; }
            else { sign = (row % 2 == 0) ? -1f : 1f; }

            int count = _rosterCount > 0 ? _rosterCount : 1;
            float centeredRow = row - (count - 1) * 0.5f;

            state.Position = new PMVector3(
                sign * SpawnLateralOffsetMeters,
                PMMoverDefaults.CapsuleHalfHeightMeters,
                centeredRow * RosterRowSpacingMeters);
            state.Velocity = PMVector3.Zero;
            state.PreAdditiveVelocity = PMVector3.Zero;
            state.YawDegrees = 0f;
            state.Mode = PMMoverMode.Walking;
            state.Grounded = true;
            state.GroundNormal = PMVector3.Up;
            state.ActiveLayers = null;
            return state;
        }

        // =================================================================================
        //  R4-C（C3）：正式内容名册 / 出生位
        // =================================================================================

        /// <summary>
        /// 正式模式的名册规范化（纯数据）：按 TeamId 分组（≤ 2 队、每队 ≤ 3 人）、
        /// 队内按 <c>PlayerId</c> 再 <c>Uid</c> 稳定排序决定槽位；英雄编号必须合法。
        ///
        /// 这里**不**算出生几何：几何要用 <see cref="PMUnityBattleMap.TryGetSpawn"/> 在真实物理场景上
        /// 核（见 <see cref="BuildFormalSpawns"/>），因此建图后才做。
        ///
        /// 为何稳定排序：同一个 uid 每局都必须落在同一个槽位，否则客户端与服务端对同一名册
        /// 会得出不同出生点（而 Lobby 的名册顺序不保证稳定）。
        /// </summary>
        private bool BuildFormalRoster(PMDsBootstrapPlayer[] roster, out string error)
        {
            error = null;

            _formalTeamIndexByUid.Clear();
            _formalSlotByUid.Clear();
            _formalMaxSpeedByUid.Clear();
            _formalSpawnByUid.Clear();

            List<PMDsBootstrapPlayer> ordered = new List<PMDsBootstrapPlayer>(roster.Length);
            HashSet<int> seenUids = new HashSet<int>();
            HashSet<int> teams = new HashSet<int>();

            for (int i = 0; i < roster.Length; i++)
            {
                PMDsBootstrapPlayer entry = roster[i];
                if (entry == null)
                {
                    error = "正式名册第 " + i.ToString(CultureInfo.InvariantCulture) + " 项为 null";
                    return false;
                }

                int uid = entry.Identity.Uid;
                if (uid <= 0)
                {
                    error = "正式名册第 " + i.ToString(CultureInfo.InvariantCulture)
                            + " 项 uid 非法（" + uid.ToString(CultureInfo.InvariantCulture) + "）";
                    return false;
                }

                if (!seenUids.Add(uid))
                {
                    error = "正式名册出现重复 uid=" + uid.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                int teamIndex;
                if (!PMUnityBattleMap.TryResolveSpawnTeamIndex(entry.Identity.TeamId, out teamIndex))
                {
                    error = "正式名册含不受支持的 TeamId=" + entry.Identity.TeamId.ToString(CultureInfo.InvariantCulture)
                            + "（uid=" + uid.ToString(CultureInfo.InvariantCulture)
                            + "）：只支持 TeamId 2 → +X 侧、TeamId 1 → -X 侧（不做行号奇偶兜底）";
                    return false;
                }

                if (!IsValidHeroId(entry.Identity.HeroId))
                {
                    error = "正式名册含非法 HeroId=" + entry.Identity.HeroId.ToString(CultureInfo.InvariantCulture)
                            + "（uid=" + uid.ToString(CultureInfo.InvariantCulture) + "，合法范围 0.."
                            + (PMHeroId.Count - 1).ToString(CultureInfo.InvariantCulture) + "）";
                    return false;
                }

                // 移速只来自英雄表（共享单一事实源）；客户端自报速度不会被采纳。
                float maxSpeed = BattleNumericConfig.Get(entry.Identity.HeroId).MoveSpeed;
                if (float.IsNaN(maxSpeed) || float.IsInfinity(maxSpeed) || maxSpeed <= 0f)
                {
                    error = "英雄 HeroId=" + entry.Identity.HeroId.ToString(CultureInfo.InvariantCulture)
                            + " 的移速非法（" + maxSpeed.ToString("R", CultureInfo.InvariantCulture) + "）";
                    return false;
                }

                _formalTeamIndexByUid[uid] = teamIndex;
                _formalMaxSpeedByUid[uid] = maxSpeed;
                teams.Add(entry.Identity.TeamId);
                ordered.Add(entry);
            }

            if (teams.Count > PMUnityBattleMap.TeamCount)
            {
                error = "正式名册队伍数=" + teams.Count.ToString(CultureInfo.InvariantCulture)
                        + " 超过 " + PMUnityBattleMap.TeamCount.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            ordered.Sort(CompareRosterForSlot);

            Dictionary<int, int> usedPerTeam = new Dictionary<int, int>();
            for (int i = 0; i < ordered.Count; i++)
            {
                int teamId = ordered[i].Identity.TeamId;
                int used;
                usedPerTeam.TryGetValue(teamId, out used);

                if (used >= PMUnityBattleMap.SpawnSlotCount)
                {
                    error = "正式名册队伍 " + teamId.ToString(CultureInfo.InvariantCulture)
                            + " 人数超过 " + PMUnityBattleMap.SpawnSlotCount.ToString(CultureInfo.InvariantCulture)
                            + "（契约每队 ≤3 人）";
                    return false;
                }

                _formalSlotByUid[ordered[i].Identity.Uid] = used;
                usedPerTeam[teamId] = used + 1;
            }

            return true;
        }

        /// <summary>
        /// 正式模式的出生位解算：对每个名册 uid 调 <see cref="PMUnityBattleMap.TryGetSpawn"/>，
        /// 并把英雄移速写进 SyncState（因此副本建立时驱动拿到的是**真值**，而不是默认同速）。
        ///
        /// 任何一个人解不出来就整个失败：不存在"部分人用假出生位"的降级。
        /// </summary>
        private bool BuildFormalSpawns(out string error)
        {
            error = null;
            _formalSpawnByUid.Clear();

            if (_battleMap == null)
            {
                error = "正式地图未加载，无法解算出生位";
                return false;
            }

            foreach (KeyValuePair<int, int> pair in _formalTeamIndexByUid)
            {
                int uid = pair.Key;

                int slot;
                if (!_formalSlotByUid.TryGetValue(uid, out slot))
                {
                    error = "正式名册 uid=" + uid.ToString(CultureInfo.InvariantCulture) + " 缺少槽位分配";
                    return false;
                }

                float maxSpeed;
                if (!_formalMaxSpeedByUid.TryGetValue(uid, out maxSpeed))
                {
                    error = "正式名册 uid=" + uid.ToString(CultureInfo.InvariantCulture) + " 缺少英雄移速";
                    return false;
                }

                PMMoverSyncState state;
                string spawnError;
                if (!_battleMap.TryGetSpawn(pair.Value, slot, out state, out spawnError))
                {
                    error = "正式出生位解算失败（uid=" + uid.ToString(CultureInfo.InvariantCulture)
                            + " teamIndex=" + pair.Value.ToString(CultureInfo.InvariantCulture)
                            + " slot=" + slot.ToString(CultureInfo.InvariantCulture) + "）：" + spawnError;
                    return false;
                }

                state.MaxSpeed = maxSpeed;
                _formalSpawnByUid[uid] = state;
            }

            return true;
        }

        /// <summary>队内槽位排序：TeamId → PlayerId → Uid（全部升序，稳定且可复现）。</summary>
        private static int CompareRosterForSlot(PMDsBootstrapPlayer left, PMDsBootstrapPlayer right)
        {
            if (left.Identity.TeamId != right.Identity.TeamId)
            {
                return left.Identity.TeamId < right.Identity.TeamId ? -1 : 1;
            }

            if (left.Identity.PlayerId != right.Identity.PlayerId)
            {
                return left.Identity.PlayerId < right.Identity.PlayerId ? -1 : 1;
            }

            if (left.Identity.Uid != right.Identity.Uid)
            {
                return left.Identity.Uid < right.Identity.Uid ? -1 : 1;
            }

            return 0;
        }

        private static bool IsValidHeroId(int heroId)
        {
            return heroId >= 0 && heroId < PMHeroId.Count;
        }

        private void OnConnected(PMTransportConnection connection)
        {
            if (connection == null) { return; }

            int uid = connection.Identity.Uid;
            if (_playersByUid.ContainsKey(uid))
            {
                Warn("同一 uid 重复入局（uid=" + uid.ToString(CultureInfo.InvariantCulture) + "），本局已存在该副本");
                return;
            }

            PMR3Player player = PMR3Runtime.SpawnPlayer(_world, _bridge, connection);
            if (player == null)
            {
                Fail("SpawnPlayer 失败（uid=" + uid.ToString(CultureInfo.InvariantCulture) + "）");
                return;
            }

            _playersByUid.Add(uid, player);

            // R4-B（B3）：建权威运动驱动 + **在首次生命周期 flush 之前**写好初值快照。
            //
            // 时序要点：本回调由 `_endpoint.Pump` 的接收段（握手接纳）在**同一次 Pump** 里调用，
            // 而桥的 Update/生命周期派发发生在那之后。所以这里写下的初值会随 Create 记录
            // **一同到达客户端**（客户端因此能在建 Driver 时拿到出生状态，而不是先收到 0 值）。
            // 若把 PublishInitialSnapshot 挪到 Pump 之后，客户端会先收到无初值快照的 Create，
            // 那就只能靠重同步补——那是缺陷不是设计。
            string movementError;
            if (!AttachMovementDriver(uid, player, out movementError))
            {
                Fail("运动驱动建立失败（uid=" + uid.ToString(CultureInfo.InvariantCulture) + "）：" + movementError);
                return;
            }

            Info("玩家副本已创建 uid=" + uid.ToString(CultureInfo.InvariantCulture)
                 + " playerId=" + connection.Identity.PlayerId.ToString(CultureInfo.InvariantCulture)
                 + " netId=" + player.NetId.Value.ToString(CultureInfo.InvariantCulture)
                 + " 名册进度=" + _playersByUid.Count.ToString(CultureInfo.InvariantCulture)
                 + "/" + _expectedUids.Count.ToString(CultureInfo.InvariantCulture)
                 + " 出生=" + DescribeSpawn(uid));
        }

        /// <summary>为已创建的权威副本建运动驱动，并在首次 flush 前发布初始快照。失败返回 false。</summary>
        private bool AttachMovementDriver(int uid, PMR3Player player, out string error)
        {
            error = null;

            if (_movementQuery == null)
            {
                error = "运动碰撞查询未建立";
                return false;
            }

            // R4-C（C3）正式模式：名册外的 uid **不**允许用兵底出生位（那会让集合与名册不一致）。
            if (_contentFormal && !_formalSpawnByUid.ContainsKey(uid))
            {
                error = "uid=" + uid.ToString(CultureInfo.InvariantCulture)
                        + " 不在正式名册内（正式模式拒绝用兵底出生位）";
                return false;
            }

            PMMoverSyncState initialSync = BuildSpawnSync(uid);
            PMMoverAuxState initialAux = PMMoverAuxState.CreateDefault();

            PMR4MovementDriver driver;
            try
            {
                driver = new PMR4MovementDriver(
                    player, _movementQuery, PMNetRole.Authority, _boot.Epoch, initialSync, initialAux);
            }
            catch (Exception ex)
            {
                error = "构造异常 " + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            // 契约 §B1：必须在首次生命周期 Flush 之前发布，否则 Create 不带初值。
            if (!driver.PublishInitialSnapshot())
            {
                driver.Dispose();
                error = "PublishInitialSnapshot 返回 false（快照编码失败？）";
                return false;
            }

            _driversByUid[uid] = driver;
            return true;
        }

        private string DescribeSpawn(int uid)
        {
            PMMoverSyncState state = BuildSpawnSync(uid);
            return "(" + state.Position.X.ToString("0.###", CultureInfo.InvariantCulture)
                   + "," + state.Position.Y.ToString("0.###", CultureInfo.InvariantCulture)
                   + "," + state.Position.Z.ToString("0.###", CultureInfo.InvariantCulture) + ")";
        }

        private void OnEndpointFailed(string reason)
        {
            // 注意：这条事件**也**用于「入局会话关闭」，因此这里只告警；
            // 玩家掉线对测试局的影响由 CheckPlayerDrops 按连接数事实判定。
            Warn("UDP 端点事件：" + reason);
        }

        private void CheckPlayerDrops()
        {
            // R4-B（B3）：断线必须冻结/释放该玩家的运动驱动（先于"测试局"级别的判定）。
            PruneDisconnectedDrivers();

            if (_endpoint == null || _playersByUid.Count == 0) { return; }
            if (_endpoint.ConnectionCount >= _playersByUid.Count) { return; }
            if (_playerDropReported) { return; }

            _playerDropReported = true;
            Warn("检测到玩家连接减少（存活 " + _endpoint.ConnectionCount.ToString(CultureInfo.InvariantCulture)
                 + " / 已创建 " + _playersByUid.Count.ToString(CultureInfo.InvariantCulture) + "）");

            if (_options != null && _options.ServerSmoke)
            {
                // 契约 §7.3：玩家掉线首版中止该测试局，不伪造正常胜利。
                Fail("smoke 验收期玩家掉线，测试局中止");
            }
        }

        /// <summary>
        /// `-server-smoke` 显式验收：**所有名册玩家都完成过一次探针**才提交 smoke 结果。
        ///
        /// 身份与计数都是真实的（uid 来自已认证连接，ProbeCount 由 RPC 执行时自增并复制），
        /// 不用定时器伪造完成。默认关闭时本方法什么都不做（正常路径由未来权威玩法调 SubmitResult）。
        /// </summary>
        private void CheckSmoke(long nowMs)
        {
            if (_smokeSubmitted || _lobby == null || !_lobby.ReadySent) { return; }
            if (_options == null || !_options.ServerSmoke) { return; }

            if (_smokeDeadlineMs == 0L)
            {
                _smokeDeadlineMs = nowMs + SmokeDeadlineMs;
            }

            bool allDone = _playersByUid.Count == _expectedUids.Count && _expectedUids.Count > 0;
            if (allDone)
            {
                foreach (KeyValuePair<int, PMR3Player> kv in _playersByUid)
                {
                    if (kv.Value == null || kv.Value.ProbeCount <= 0)
                    {
                        allDone = false;
                        break;
                    }
                }
            }

            if (!allDone)
            {
                if (nowMs >= _smokeDeadlineMs)
                {
                    Fail("smoke 验收超时（" + SmokeDeadlineMs + "ms）：名册 " + _expectedUids.Count.ToString(CultureInfo.InvariantCulture)
                         + " 人，已完成探针 " + CountProbedPlayers().ToString(CultureInfo.InvariantCulture) + " 人");
                }

                return;
            }

            byte[] summary = BuildSmokeSummary();
            string error;
            if (!_lobby.SubmitResult(0, summary, out error))
            {
                Fail("提交 smoke 结果失败：" + error);
                return;
            }

            _smokeSubmitted = true;
            Info("smoke 验收：名册 " + _expectedUids.Count.ToString(CultureInfo.InvariantCulture)
                 + " 人全部完成探针，已提交 smoke 结果（summary=" + summary.Length.ToString(CultureInfo.InvariantCulture) + "B）");
        }

        private int CountProbedPlayers()
        {
            int count = 0;
            foreach (KeyValuePair<int, PMR3Player> kv in _playersByUid)
            {
                if (kv.Value != null && kv.Value.ProbeCount > 0) { count++; }
            }

            return count;
        }

        /// <summary>
        /// smoke 结果摘要：自描述（"smoke" 字面量）+ 真实身份/计数。
        /// 上限 512 字节（契约 §3 的 MaxSummaryBytes），因此 6 人 × (uid,count) 远在界内。
        /// </summary>
        private byte[] BuildSmokeSummary()
        {
            PMNetWriter writer = new PMNetWriter(128);
            writer.WriteInt32(1);                 // 格式版本
            writer.WriteStringValue("smoke");     // 明确标注这是 smoke 验收摘要，不是正常胜负

            List<int> uids = new List<int>(_playersByUid.Keys);
            uids.Sort();

            writer.WriteInt32(uids.Count);
            for (int i = 0; i < uids.Count; i++)
            {
                PMR3Player player = _playersByUid[uids[i]];
                writer.WriteInt32(uids[i]);
                writer.WriteInt32(player != null ? player.ProbeCount : 0);
            }

            return writer.ToArray();
        }

        // =================================================================================
        //  失败 / 退出 / 释放
        // =================================================================================

        private void OnLobbyExitRequested(int code)
        {
            Action<int> handler = ExitRequested;
            if (handler != null)
            {
                handler(code);
            }
        }

        private void Fail(string reason)
        {
            if (_faulted) { return; }

            _faulted = true;
            _faultReason = reason;
            Error("R3-B 新链 DS 失败：" + reason);

            Action<int> handler = ExitRequested;
            if (handler != null)
            {
                handler(1);
            }
        }

        /// <summary>幂等释放：控制通道 → UDP 端点 → 运行时接线 → 场景对象。</summary>
        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;

            if (_lobby != null)
            {
                try { _lobby.Dispose(); }
                catch (Exception ex) { Error("释放控制通道代理异常：" + ex.GetType().Name); }

                _lobby = null;
            }

            if (_endpoint != null)
            {
                try
                {
                    _endpoint.Connected -= OnConnected;
                    _endpoint.Failed -= OnEndpointFailed;
                    _endpoint.Dispose();
                }
                catch (Exception ex) { Error("释放 UDP 端点异常：" + ex.GetType().Name); }

                _endpoint = null;
            }

            PMR3Runtime.PlayerSpawned -= OnPlayerSpawned;
            if (_world != null)
            {
                PMR3Runtime.Detach(_world);
            }

            // R4-B（B3）：运动驱动必须先 Freeze 再 Dispose（脱离 player 引用），
            // 然后才能放开查询/世界。顺序反了会让还在飞的入站回调拿到已释放对象。
            List<int> driverUids = new List<int>(_driversByUid.Keys);
            for (int i = 0; i < driverUids.Count; i++)
            {
                PMR4MovementDriver driver;
                if (!_driversByUid.TryGetValue(driverUids[i], out driver) || driver == null) { continue; }

                try
                {
                    driver.Freeze();
                    driver.Dispose();
                }
                catch (Exception ex) { Error("释放运动驱动异常：" + ex.GetType().Name); }
            }

            _driversByUid.Clear();
            _movementQuery = null;

            // 静态日志出口要还原，避免退出后再进来的人拿到已释放对象的回调。
            if (PMR3Runtime.Warn == OnRuntimeWarn)
            {
                PMR3Runtime.Warn = _previousRuntimeWarn;
            }

            if (_bridge != null)
            {
                try { _bridge.Dispose(); }
                catch (Exception ex) { Error("释放会话桥异常：" + ex.GetType().Name); }

                _bridge = null;
            }

            for (int i = 0; i < _sceneObjects.Count; i++)
            {
                if (_sceneObjects[i] != null)
                {
                    UnityEngine.Object.Destroy(_sceneObjects[i]);
                }
            }

            _sceneObjects.Clear();

            // R4-C（C3）：正式地图持有自己的隔离物理场景与查询，释放必须走它自己的 Dispose；
            // 诊断模式则先销毁测试对象、再卸载本地物理场景（顺序反了引用会变 fake-null）。
            if (_battleMap != null)
            {
                try { _battleMap.Dispose(); }
                catch (Exception ex) { Error("释放正式地图异常：" + ex.GetType().Name); }

                _battleMap = null;
                _movementScene = default(Scene);
                _movementPhysicsScene = default(PhysicsScene);
            }
            else
            {
                PMR3TestScene.ReleaseIsolatedScene(_movementScene);
                _movementScene = default(Scene);
                _movementPhysicsScene = default(PhysicsScene);
            }

            _formalSpawnByUid.Clear();
            _formalSlotByUid.Clear();
            _formalTeamIndexByUid.Clear();
            _formalMaxSpeedByUid.Clear();

            _playersByUid.Clear();
            _world = null;
        }

        // =================================================================================
        //  日志
        // =================================================================================

        private void LogHeartbeat()
        {
            if (_endpoint == null) { return; }

            Info("heartbeat tick=" + _tickCount.ToString(CultureInfo.InvariantCulture)
                 + " objects=" + (_world != null ? _world.ObjectCount : 0).ToString(CultureInfo.InvariantCulture)
                 + " players=" + _playersByUid.Count.ToString(CultureInfo.InvariantCulture)
                 + " udpConns=" + _endpoint.ConnectionCount.ToString(CultureInfo.InvariantCulture)
                 + " udpIn=" + _endpoint.DatagramsReceived.ToString(CultureInfo.InvariantCulture)
                 + " udpOut=" + _endpoint.DatagramsSent.ToString(CultureInfo.InvariantCulture)
                 + " unbound=" + _endpoint.DroppedUnboundDatagrams.ToString(CultureInfo.InvariantCulture)
                 + " | " + (_lobby != null ? _lobby.Describe() : "lobby=<none>"));

            HYLDDebug.FlushTrace();
        }

        private void OnRuntimeWarn(string message)
        {
            Warn("PMR3Runtime: " + message);
        }

        /// <summary>世界/桥内部的告警（注册冲突、生命周期批次、复制丢包等）。</summary>
        private void OnWorldWarn(string message)
        {
            Warn("world/bridge: " + message);
        }

        private void OnLobbyLog(string message)
        {
            Info("PMDsLobbyAgent: " + message);
        }

        private void OnLobbyWarn(string message)
        {
            Warn("PMDsLobbyAgent: " + message);
        }

        private void OnPlayerSpawned(PMR3Player player)
        {
            // 只做诊断：真正的名册登记发生在 OnConnected（那里才知道连接）。
            if (player == null) { return; }

            Info("PlayerSpawned netId=" + player.NetId.Value.ToString(CultureInfo.InvariantCulture)
                 + " uid=" + player.Uid.ToString(CultureInfo.InvariantCulture));
        }

        private void Info(string message)
        {
            HYLDDebug.Log("[PMDsSessionHost] " + message);

            Action<string> handler = Log;
            if (handler != null) { handler(message); }
        }

        private void Warn(string message)
        {
            HYLDDebug.LogWarning("[PMDsSessionHost] " + message);
        }

        private void Error(string message)
        {
            HYLDDebug.LogError("[PMDsSessionHost] " + message);
        }

        /// <summary>单行诊断摘要（测试宿主/日志对账用）。</summary>
        public string Describe()
        {
            return "match=" + (_boot != null ? _boot.MatchId : "<none>")
                   + " port=" + BoundPort.ToString(CultureInfo.InvariantCulture)
                   + " mode=" + (_contentFormal ? "formal" : "diagnostic")
                   + " digest=0x" + _selectedCollisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                   + " worldVersion=" + _selectedWorldVersion.ToString(CultureInfo.InvariantCulture)
                   + " sceneReady=" + (_sceneReady ? 1 : 0)
                   + " movementScene=" + (_movementScene.IsValid() ? _movementScene.name : "<none>")
                   + " isolated=" + (_movementQuery != null && _movementQuery.Scene.IsValid()
                                      && !_movementQuery.Scene.Equals(Physics.defaultPhysicsScene) ? 1 : 0)
                   + " players=" + _playersByUid.Count.ToString(CultureInfo.InvariantCulture)
                   + "/" + _expectedUids.Count.ToString(CultureInfo.InvariantCulture)
                   + " movementDrivers=" + _driversByUid.Count.ToString(CultureInfo.InvariantCulture)
                   + " hostTick=" + _movementHostTickId.ToString(CultureInfo.InvariantCulture)
                   + " fault=" + (_faulted ? _faultReason : "<none>");
        }
    }
}
