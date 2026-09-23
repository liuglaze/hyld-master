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
// R5-C（DS 真实宿主接线，诊断投射物；契约 Docs/plans/net-r5-network-contract.md 末段「C宿主首个可执行入口」）：
//   本文件是**唯一**被授权的 C 宿主改动面，只做接线，**不改** driver / core / 适配器 / 合同：
//     · 场景就绪后、连接接入前创建**每会话**的 PMProjectileHistory / PMUnityProjectileMotion /
//       PMR5ProjectileDriver（History 由本宿主按真实权威运动逐帧填样本，driver 只读）；
//     · 每个已认证 player 在 OnConnected 里 BindPlayer，断线收尾里 UnbindPlayer（取消预留/退休本地账）；
//     · 每宿主帧：先 PumpMovement（运动权威）→ 再为所有运动 driver 记一次胶囊历史样本 →
//       投射物 driver 按**固定 16ms 累加器（单帧最多 8 子步）**推进；零子步也 Pump(0) 排空声明入站；
//     · 可信授权走私有 IPMR5ProjectileAuthorityPolicy（只认已认证绑定/名册/单调 activation/
//       200ms 墙钟间隔/未 Freeze 的运动 driver + 共享诊断 spec + DS 权威枪口位置）；
//       命中过滤走私有 IPMProjectileHitFilter（按名册身份做 self/队内过滤，缺数据一律拒绝）；
//     · 结算只作**诊断可观察命中计数 + 日志**（非 R6、不扣血，**不接**旧普通攻击/资源/HP）。
//   本文件是 DS 宿主，因此**不创建任何表现对象**（C2 表现层在 DS 上会显式抛异常）。
//
// R6-C（DS 权威战斗接线；契约 Docs/plans/net-r6-combat-contract.md 尾段「C宿主接线冻结」）：
//   · **只有非 `-server-smoke`（正式玩法）**才建立权威战斗：PMCombatSession(epoch) →
//     `PMR6CombatDriver.CreateAuthorityPolicy(model, 真实运动位置, () => 本帧墙钟)` → PMR5ProjectileDriver →
//     PMR6CombatDriver。创建次序冻结在 CreateProjectileWiring 里（policy 必须早于 R5 driver 构造）；
//   · **smoke 保持原诊断路径**：仍由本宿主订阅 R5 的 Settlement 只做计数/日志（R3 探针自动结果不变）；
//     正式玩法则**不再订阅**诊断 Settlement、**不做**宿主侧 Drain —— R6 driver 是该会话唯一的结算消费者
//     （否则「诊断消费者 + R6」双订阅会把同一条结算消费两次）；
//   · 名册身份（uid/team/hero）只来自引导文件名册（`_rosterTeamByUid` / `_rosterHeroByUid`），
//     **绝不**把正式内容模式的归一化 teamIndex(formalTeamIndex0/1) 当 team 传给 core；
//   · 每帧次序：endpoint → movement → **R6.Pump(now)**（先 Tick 回蓝、再处理攻击/授权账）→ R5 全部子步
//     → **R6.FlushState(now)**（最新 HP/资源/胜负）；三个 core 入口共用**同一个**单调墙钟；
//   · 死亡由 R6 的 `PlayerDied` 驱动：Freeze 对应 Mover + **立即**补一条 Alive=false 历史
//     （真实 AuthorityServer 帧 + 真实输入边界）；R5 命中过滤复核 core 的 TeamId/Connected/Dead；
//   · 终局由 `OutcomeFrozen` 驱动：冻结**全部** Mover，此后 R5 只 `Pump(0)` 排空声明入站、不再推进；
//   · 断线走 `CombatDriver.UnbindPlayer`（内含 R5 unbind + core.Disconnect ⇒ 结果可出）；
//     **死亡 ≠ 掉线**：死者仍在线、仍要 ACK 结果，因此死亡路径不 unbind；
//   · `ResultReadyForLobby` 后**单次** `Lobby.SubmitResult(WinnerTeamId, 冻结摘要)`，既有 ResultAck/Exited 链不变。
//
// 语言面：Unity 侧（需要 GameObject/BoxCollider 建场景），但**不使用任何渲染/输入/UI API**。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Logging;
using PMNet;
using PMNet.Combat;
using PMNet.Control;
using PMNet.Mover;
using PMNet.Prediction;
using PMNet.Projectile;
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

        /// <summary>
        /// R5-C 诊断日志节流：历史被拒 / 策略拒绝 / 命中被拒各自最多刷这么多条日志。
        /// **只影响日志**，计数照常累加（不静默、也不刷屏）。
        /// </summary>
        private const long MaxProjectileDiagnosticWarnings = 20;

        /// <summary>名册内相邻两行在 Z 轴上的间距（米）。只用于把同队玩家错开，避免重叠。</summary>
        private const float RosterRowSpacingMeters = 1.5f;

        /// <summary>出生点距墙中心的水平偏移（米）。墙盒 x∈[-0.5,0.5]，±3 保证在墙外。</summary>
        private const float SpawnLateralOffsetMeters = 3f;

        /// <summary>
        /// R6-C：正式玩法（非 smoke）的开局等待期限（毫秒）——自场景就绪起算。
        /// 到点仍缺名册玩家就**明确失败**，绝不伪造一个胜负（smoke 走自己的探针期限，不走本门）。
        /// </summary>
        public const int CombatStartDeadlineMs = 30000;

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

        /// <summary>
        /// R6-C：uid → 名册英雄编号。core.AddPlayer 的 heroId 必须来自**可信名册**，
        /// 不能从客户端自报字段采纳（契约：「hero 必须 0..19 不能走共享 Get 兜底」）。
        /// </summary>
        private readonly Dictionary<int, int> _rosterHeroByUid = new Dictionary<int, int>();

        /// <summary>名册人数（决定 Z 行居中量）。</summary>
        private int _rosterCount;

        /// <summary>DS 自己决定的权威服务帧（独立于客户端预测帧，不与 AP 帧做算术）。</summary>
        private PMFrameId _movementServerFrame;

        /// <summary>宿主 tick 序号（显式传给 Driver，同一 tick 不重复充值信用）。</summary>
        private long _movementHostTickId;

        /// <summary>上一次运动 Pump 的墙钟（毫秒），用于算实际 elapsed。</summary>
        private long _lastMovementPumpMs;

        // ---- R5-C：诊断投射物宿主接线（每会话一份；非 R6 完整玩法） ----

        /// <summary>
        /// 运动碰撞白名单（与 <see cref="_movementQuery"/> 同一批 Collider）。投射物运动 hook 必须复用
        /// **同一份现存字段**：诊断 = 本地物理场景里刚建的地板/墙；正式 = <see cref="PMUnityBattleMap.Colliders"/>。
        /// </summary>
        private Collider[] _movementAllowlist;

        /// <summary>本会话的目标历史（由本宿主逐帧按真实权威运动填样本；driver 只读）。</summary>
        private PMProjectileHistory _projectileHistory;

        /// <summary>C1 适配器：真实 Unity PhysX 的投射物运动 hook（绑定隔离物理场景，绝不打默认物理世界）。</summary>
        private PMUnityProjectileMotion _projectileMotion;

        /// <summary>R5-B2b 会话级投射物网络驱动（上行/下行/运动/结算的唯一出口）。</summary>
        private PMR5ProjectileDriver _projectileDriver;

        /// <summary>DS 可信授权策略（本文件私有实现；客户端不注入）。</summary>
        private IPMR5ProjectileAuthorityPolicy _projectilePolicy;

        /// <summary>DS 权威命中过滤（名册身份 self/队内过滤）。</summary>
        private IPMProjectileHitFilter _projectileHitFilter;

        /// <summary>每 owner 已授权的最大 activationId（单调、不重用；缺项 = 尚未授权过）。</summary>
        private readonly Dictionary<uint, uint> _projectileLastActivationByOwner = new Dictionary<uint, uint>();

        /// <summary>每 owner 上一次授权开火的墙钟（毫秒），用于 200ms 间隔判定。</summary>
        private readonly Dictionary<uint, double> _projectileLastFireWallMsByOwner = new Dictionary<uint, double>();

        /// <summary>本帧投射物 Pump 的墙钟（同一 monotonic clock；策略在 Pump 内被回调时需要它）。</summary>
        private double _projectileWallNowMs;

        /// <summary>固定步长累加器的余量（毫秒）。**不**用大 elapsed 直接放大 dt。</summary>
        private double _projectileAccumulatorMs;

        /// <summary>上一次投射物 Pump 的墙钟（毫秒）；与运动 Pump 同一时钟口径。</summary>
        private long _lastProjectilePumpMs;

        /// <summary>诊断可观察：累计已消费的结算条数（≠ 扣血次数）。</summary>
        private long _diagnosticProjectileSettlementCount;

        /// <summary>诊断可观察：累计已消费的命中条数（settlement.HitCount 之和；≠ 扣血）。</summary>
        private long _diagnosticProjectileHitCount;

        /// <summary>诊断可观察：策略拒绝次数（身份/名册/Freeze/间隔/单调）。</summary>
        private long _diagnosticProjectilePolicyRejections;

        /// <summary>诊断可观察：命中过滤拒绝次数（self/队内/缺数据）。</summary>
        private long _diagnosticProjectileFilterRejections;

        /// <summary>诊断可观察：历史样本记录失败次数（不静默吞）。</summary>
        private long _diagnosticProjectileHistoryRejects;

        /// <summary>诊断可观察：因累加器上限被丢弃的毫秒数（有界追赶的证据）。</summary>
        private double _diagnosticProjectileDroppedMs;

        /// <summary>诊断可观察：落在驱动待取缓冲里（订阅者未交付）的结算条数。</summary>
        private long _diagnosticProjectileUnconsumedSettlements;

        /// <summary>R5-C 复核（F1）：DS 已排空并丢弃的「视图脏增量」条数。DS 没有表现消费者，
        /// 但驱动的脏通道只有 DrainViewChanges 才清，因此必须每帧显式消费掉（见 DrainDsProjectileViews）。</summary>
        private long _diagnosticProjectileViewDrains;

        /// <summary>R5-C 复核（F2）：断线时为该副本补写的「不可命中（Alive=false）」历史样本数。</summary>
        private long _diagnosticProjectileDisconnectSamples;

        /// <summary>R5-C 复核（F8）：结算诊断日志自身抛异常的次数（吞掉它才能保住「一条结算只计一次」）。</summary>
        private long _diagnosticProjectileSettlementLogFailures;

        // ---- R6-C：正式玩法（非 smoke）权威战斗接线 ----

        /// <summary>
        /// 本局是否启用权威战斗核心（= 非 `-server-smoke`）。
        /// smoke 保留 R3 探针自动结果 + R5 诊断计数，**不**建立 PMCombatSession/PMR6CombatDriver，
        /// 也**不**要求 smoke 脚本懂战斗（否则等于无条件要求 CLI 烟测会打架）。
        /// </summary>
        private bool _combatEnabled;

        /// <summary>权威战斗核心（仅正式玩法；smoke 恒为 null）。</summary>
        private PMCombatSession _combatModel;

        /// <summary>会话级战斗网络驱动（仅正式玩法；smoke 恒为 null）。</summary>
        private PMR6CombatDriver _combatDriver;

        /// <summary>名册是否已开战（StartMatch 只允许成功一次）。</summary>
        private bool _combatStarted;

        /// <summary>权威终局是否已冻结（冻结后全部 Mover 停止推进，R5 只 Pump(0) 排入站）。</summary>
        private bool _combatOutcomeFrozen;

        /// <summary>权威战斗结果是否已提交 Lobby（单次；已提交不重复提交、也不会再走 smoke 提交）。</summary>
        private bool _combatSubmitted;

        /// <summary>正式玩法开局期限（绝对墙钟毫秒；0 = 尚未起算，即在场景就绪后的第一帧起算）。</summary>
        private long _combatStartDeadlineMs;

        /// <summary>R6-C 观测：由权威死亡驱动的 Mover 冻结次数（= R6 的 PlayerDied 次数）。</summary>
        private long _combatDeathsReported;

        /// <summary>R6-C 观测：终局冻结 Mover 的次数（R6 的 OutcomeFrozen 只来一次）。</summary>
        private long _combatTerminalFreezes;

        /// <summary>R6-C 观测：死亡时**立即**补写的 Alive=false 历史样本数。</summary>
        private long _combatDeathHistorySamples;

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

        /// <summary>
        /// R5-C：本会话的会话级投射物驱动（后续测试/诊断用）。未接线或已释放时为 null。
        /// **只读观察**：写入口全部在宿主内部（上行只经声明 RPC，下行只经生成复制）。
        /// </summary>
        public PMR5ProjectileDriver ProjectileDriver { get { return _projectileDriver; } }

        /// <summary>
        /// R5-C 诊断可观察：累计已消费的**命中条数**（settlement.HitCount 之和）。
        /// **不是**扣血次数 —— 本批没有 R6 消费者，只有诊断计数与日志（non-gameplay）。
        /// </summary>
        public long DiagnosticProjectileHitCount { get { return _diagnosticProjectileHitCount; } }

        /// <summary>R5-C 诊断可观察：累计已消费的结算条数（≠ 扣血次数）。</summary>
        public long DiagnosticProjectileSettlementCount { get { return _diagnosticProjectileSettlementCount; } }

        // ---- R6-C：公开读视图（供后续测试/诊断使用；写入口全部在宿主内部） ----

        /// <summary>
        /// R6-C：本会话的权威战斗网络驱动（正式玩法专用）。smoke 或未接线/已释放时为 null。
        ///
        /// **只读观察**：上行只经声明 RPC，下行只经生成复制；宿主是它唯一的驱动点。
        /// </summary>
        public PMR6CombatDriver CombatDriver { get { return _combatDriver; } }

        /// <summary>R6-C：本局是否启用权威战斗核心（= 非 `-server-smoke`）。</summary>
        public bool CombatEnabled { get { return _combatEnabled; } }

        /// <summary>R6-C：全名册接入后是否已开战（StartMatch 只成功一次）。</summary>
        public bool CombatStarted { get { return _combatStarted; } }

        /// <summary>R6-C：权威战斗结果是否已提交 Lobby（单次；不重复提交）。</summary>
        public bool CombatResultSubmitted { get { return _combatSubmitted; } }

        /// <summary>R6-C：权威终局是否已冻结（此后全部 Mover 停止推进、R5 只 Pump(0)）。</summary>
        public bool CombatOutcomeFrozen { get { return _combatOutcomeFrozen; } }

        /// <summary>
        /// R5-C 诊断计数（<see cref="DiagnosticProjectileHitCount"/> / <see cref="DiagnosticProjectileSettlementCount"/>）
        /// **只在 smoke 有效**：正式玩法由 R6 driver 消费结算、宿主不再订阅也不 Drain，因此这两个值恒为 0。
        /// </summary>
        public bool DiagnosticCountersMeaningfulOnlyInSmoke { get { return !_combatEnabled; } }

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

            // R6-C：**只有正式玩法**（非 `-server-smoke`）启用权威战斗核心。
            // smoke 是连通性/探针验收，不是玩法验收：让它去懂战斗只会把烟测变成玩法门槛。
            _combatEnabled = !options.ServerSmoke;

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
                    // R6-C：英雄也来自**同一份可信名册**（core.AddPlayer 的 heroId 必须有真实来源，
                    // 不能读客户端自报、也不能走 BattleNumericConfig 的共享兜底）。
                    _rosterHeroByUid[roster[i].Identity.Uid] = roster[i].Identity.HeroId;
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

                // R5-C：投射物运动 hook 用正式地图自己的 Collider 白名单（不碰默认物理世界）。
                _movementAllowlist = battleMap.Colliders;

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

                // R5-C：投射物运动 hook 必须复用**同一批** Collider（诊断 = 刚建在本地物理场景里的地板/墙）。
                _movementAllowlist = allowlist;
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

            // 7c) R5-C：投射物接线（History / Motion / Driver）必须在**场景就绪之前、玩家接入之前**建立。
            //     玩家接入（OnConnected）发生在 _endpoint.Pump 里，而 endpoint 在下面才 OpenServer，
            //     因此这里建好的 driver 一定早于任何 BindPlayer——否则第一名玩家的上行会被当成「未接线」丢弃。
            //     失败即启动失败（不回退：诊断探针不可用就不该谎报就绪）。
            if (!CreateProjectileWiring(out error))
            {
                ReleaseProjectileWiring();
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

            // R6-C：本帧**唯一**的单调墙钟。同一个值喂给 R6.Pump / R5.Pump / R6.FlushState
            // （契约「同一帧所有 core 入口共用同一个 clock」），也是 R6 授权策略的时钟来源。
            // 必须在 `_endpoint.Pump` **之前**设好：本帧接入的玩家会在握手回调（OnConnected）里
            // 用它发布初始 combatstate（首个桥 flush 之前）。
            _projectileWallNowMs = (double)nowMs;

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

            // R6-C：战斗命令队列（上行攻击/裁决/结果 + core.Tick 回蓝）必须**先于** R5 的授权/生成处理。
            // 两重理由：① core 的回蓝只发生在 Tick 里，先 Tick 才能让「刚好回满」的攻击合法；
            // ② 同一帧到达的「攻击声明 + 逐颗 spawn」必须先把授权账建好，否则生成会被当成未批准。
            PumpCombatOnce(_projectileWallNowMs);
            if (_faulted) { return; }

            // R5-C：先记**本帧**真实权威运动样本（在投射物推进之前），再按固定 16ms 累加器推进投射物。
            // 次序是契约冻结的：历史采样 after PumpMovement / before projectile Pump。
            PumpProjectiles(nowMs);
            if (_faulted) { return; }

            // R6-C：结算发生在 R5.Pump 内部（订阅回调），因此状态复制必须在它**之后**：
            // 本帧的伤害/死亡/胜负与回蓝在同一个 FlushState 里一起发布。
            FlushCombatOnce(_projectileWallNowMs);
            if (_faulted) { return; }

            // R6-C：开战门 → 开局期限 → 单次结果提交（smoke 路径全部早退）。
            TryStartCombatMatch();
            if (_faulted) { return; }

            CheckCombatStartDeadline(nowMs);
            if (_faulted) { return; }

            SubmitCombatResultIfReady();
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

                    // R6-C：冻结后不得再推进运动。`Freeze()` 只冻结 AP 时间轴与表现，
                    // **Authority 的 Pump 不看 `_frozen`**（见 PMR4MovementDriver.Freeze/Pump）——
                    // 因此宿主才是「冻结 Mover」这句话的唯一执行点：死亡者与终局之后都不再步进。
                    // 仍然调用 Update：入站队列要照常排空（否则冻结副本的待处理载荷会无界堆积）。
                    if (driver.IsFrozen || (_combatEnabled && _combatModel != null && _combatModel.Ended)) { continue; }

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

                // F2：先拿断线前最后一份权威事实补一条 Alive=false 样本，**再**释放驱动。
                // 顺序反了就没得可取（driver 已 Dispose），断线者会留在历史里继续可命中。
                RecordProjectileDisconnectSample(uid, player, driver);

                try { driver.Freeze(); }
                catch (Exception ex)
                {
                    Warn("断线驱动冻结异常（uid=" + uid.ToString(CultureInfo.InvariantCulture) + "）：" + ex.GetType().Name);
                }
                // Freeze 失败也必须独立尝试释放，不能跳过 Dispose。
                try { driver.Dispose(); }
                catch (Exception ex)
                {
                    Warn("断线驱动释放异常（uid=" + uid.ToString(CultureInfo.InvariantCulture) + "）：" + ex.GetType().Name);
                }

                _driversByUid.Remove(uid);

                // 断线必须摘掉投射物接缝。R5 的 UnbindPlayer 会取消该 owner 的未消费 NetId 预留、
                // 丢掉它的入站队列、退休本地账，并销毁它悬空的权威对象；不摘就等于把预留/队列挂到 Dispose。
                //
                // R6-C：正式玩法走 `CombatDriver.UnbindPlayer`（它同时做 R5 unbind + `core.Disconnect`）——
                // 后者保留水位与死亡真值，并在**只剩唯一在线队伍**时判 Forfeit 获胜 ⇒ 结果可出。
                // 这条路径**只**用于真断线；死亡不是断线，绝不走这里（死者仍要 ACK 结果）。
                if (_combatEnabled && _combatDriver != null && !_combatDriver.IsDisposed)
                {
                    try
                    {
                        _combatDriver.UnbindPlayer(player);
                    }
                    catch (Exception ex)
                    {
                        Fail("断线收尾：战斗接缝摘除失败（uid=" + uid.ToString(CultureInfo.InvariantCulture)
                             + "）：" + ex.GetType().Name + " " + ex.Message);
                        return;
                    }
                }
                else if (_projectileDriver != null && !_projectileDriver.IsDisposed)
                {
                    try
                    {
                        _projectileDriver.UnbindPlayer(player);
                    }
                    catch (Exception ex)
                    {
                        Fail("断线收尾：投射物接缝摘除失败（uid=" + uid.ToString(CultureInfo.InvariantCulture)
                             + "）：" + ex.GetType().Name + " " + ex.Message);
                        return;
                    }
                }

                Warn("玩家断线：已尝试冻结和释放运动驱动、投射物接缝已摘（异常见前述日志）（uid="
                     + uid.ToString(CultureInfo.InvariantCulture) + "）");
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

            // 接缝绑定：正式玩法走 R6 战斗驱动（内含 core.AddPlayer + R5 绑定）；smoke 保持 R5 诊断绑定。
            if (_combatEnabled)
            {
                string combatError;
                if (!AttachCombatPlayer(uid, player, out combatError))
                {
                    Fail("战斗接线失败（uid=" + uid.ToString(CultureInfo.InvariantCulture) + "）：" + combatError);
                    return;
                }
            }
            else if (_projectileDriver == null || !_projectileDriver.BindPlayer(player))
            {
                // R5-C：每个**已认证** player 创建即绑定投射物接缝。
                // 回调携带 player 身份，因此上行 owner 只认 player.NetId，绝不采信 payload 自报。
                Fail("投射物驱动绑定失败（uid=" + uid.ToString(CultureInfo.InvariantCulture) + "）");
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

        // =================================================================================
        //  R6-C：正式玩法（非 smoke）权威战斗接线
        // =================================================================================

        /// <summary>
        /// 正式玩法：把一名**已认证**玩家接进权威战斗（core.AddPlayer + 接缝绑定），并在首个生命周期
        /// flush 之前发布初始战斗状态。
        ///
        /// 身份只来自引导文件名册：uid/teamId/heroId 三者都取自 `_roster*`。
        /// **绝不**把正式内容模式的归一化 teamIndex（formalTeamIndex0/1）当 team 传给 core ——
        /// 那是出生几何用的索引，不是名册里真实的 TeamId；混用会让 core 的敌我判定与客户端不一致。
        /// </summary>
        private bool AttachCombatPlayer(int uid, PMR3Player player, out string error)
        {
            error = null;

            if (_combatDriver == null || _combatModel == null)
            {
                error = "权威战斗驱动/核心未建立";
                return false;
            }

            int teamId;
            if (!_rosterTeamByUid.TryGetValue(uid, out teamId))
            {
                error = "uid=" + uid.ToString(CultureInfo.InvariantCulture) + " 不在引导文件名册内（无 TeamId 来源）";
                return false;
            }

            int heroId;
            if (!_rosterHeroByUid.TryGetValue(uid, out heroId))
            {
                error = "uid=" + uid.ToString(CultureInfo.InvariantCulture) + " 不在引导文件名册内（无 HeroId 来源）";
                return false;
            }

            if (teamId <= 0)
            {
                error = "名册 TeamId 非法（" + teamId.ToString(CultureInfo.InvariantCulture)
                        + "）：core 要求正数真实队伍号";
                return false;
            }

            if (!_combatDriver.AddPlayer(player, uid, teamId, heroId))
            {
                error = "CombatDriver.AddPlayer 失败（uid=" + uid.ToString(CultureInfo.InvariantCulture)
                        + " team=" + teamId.ToString(CultureInfo.InvariantCulture)
                        + " hero=" + heroId.ToString(CultureInfo.InvariantCulture) + "）";
                return false;
            }

            // 契约：「场景/actor 初始 combatstate 必须在首个 flush 前发布」。
            // 理由与运动初值完全同一条：本回调由 `_endpoint.Pump` 的握手接纳段调用，而桥的
            // Update/生命周期派发在那之后 —— 此刻写下的 9 条战斗复制值会随 Create 记录一同到达客户端；
            // 否则客户端会先拿到 hero/team/HP 全 0 的对象（等于身份未就绪就允许开火）。
            _combatDriver.FlushState(_projectileWallNowMs);
            if (FaultHostIfCombatDriverFaulted("OnConnected(初始发布)"))
            {
                error = "初始 combatstate 发布后驱动 fault：" + _combatDriver.FaultError;
                return false;
            }

            return true;
        }

        /// <summary>
        /// 正式玩法的开战门：**全部期望名册**都已连接就绪且已接进战斗（core 名册齐 + 运动 driver 就绪）
        /// 才 `StartMatch`，且只成功一次。
        ///
        /// 为什么不按「已有多少人」开局：core 的 StartMatch 要求人数与名册完全一致且每一名成员在线未死；
        /// 提前开局会让「名册还没接完就先按已有玩家判 forfeit」变成真实的提前终局。
        /// </summary>
        private void TryStartCombatMatch()
        {
            if (!_combatEnabled || _combatStarted || _faulted) { return; }
            if (_combatDriver == null || _combatDriver.IsDisposed || _combatModel == null) { return; }
            if (_expectedUids.Count == 0 || _playersByUid.Count != _expectedUids.Count) { return; }

            foreach (KeyValuePair<int, PMR3Player> kv in _playersByUid)
            {
                PMR3Player member = kv.Value;
                if (member == null) { return; }
                if (member.OwnerConnection == null || !member.OwnerConnection.IsReady) { return; }
                if (!_combatDriver.IsPlayerBound(member)) { return; }
                if (_combatModel.GetPlayer(member.NetId.Value) == null) { return; }

                PMR4MovementDriver driver;
                if (!_driversByUid.TryGetValue(kv.Key, out driver) || driver == null || driver.IsDisposed)
                {
                    return;
                }
            }

            if (!_combatDriver.StartMatch(_expectedUids.Count))
            {
                Fail("正式玩法 StartMatch 失败（名册 " + _expectedUids.Count.ToString(CultureInfo.InvariantCulture)
                     + " 人全部就绪却开不了战：core 名册人数/在线态不一致）");
                return;
            }

            _combatStarted = true;
            Info("R6-C 权威战斗已开战：名册 " + _expectedUids.Count.ToString(CultureInfo.InvariantCulture)
                 + " 人全部接入（uid/team/hero 全部来自引导文件名册）");
        }

        /// <summary>
        /// 正式玩法开局期限：自场景就绪（本宿主第一帧，Initialize 已把 `_sceneReady` 置位）起
        /// <see cref="CombatStartDeadlineMs"/> 内仍未开战 ⇒ **明确失败**。
        /// 刻意不伪造胜负（不为某一队发一个「胜利」）；smoke 不走本门。
        /// </summary>
        private void CheckCombatStartDeadline(long nowMs)
        {
            if (!_combatEnabled || _combatStarted || _faulted) { return; }

            if (_combatStartDeadlineMs == 0L)
            {
                _combatStartDeadlineMs = nowMs + (long)CombatStartDeadlineMs;
            }

            if (nowMs < _combatStartDeadlineMs) { return; }

            Fail("正式玩法开局超时（" + CombatStartDeadlineMs.ToString(CultureInfo.InvariantCulture)
                 + "ms，自场景就绪起）：名册 " + _expectedUids.Count.ToString(CultureInfo.InvariantCulture)
                 + " 人，已接入 " + _playersByUid.Count.ToString(CultureInfo.InvariantCulture) + " 人");
        }

        /// <summary>
        /// R6-C：宿主每帧驱动战斗驱动（**必须先于 R5.Pump**）。
        /// 驱动内部次序：推进权威时钟（回蓝）→ 处理上行攻击/裁决/结果命令队列（授权账先于生成）→ Drain 兜底。
        /// </summary>
        private void PumpCombatOnce(double nowMs)
        {
            if (_combatDriver == null || _combatDriver.IsDisposed) { return; }
            if (FaultHostIfCombatDriverFaulted("Pump(入口)")) { return; }

            try
            {
                _combatDriver.Pump(nowMs);
            }
            catch (Exception ex)
            {
                Fail("战斗驱动异常（Pump）：" + ex.GetType().Name + " " + ex.Message);
                return;
            }

            FaultHostIfCombatDriverFaulted("Pump");
        }

        /// <summary>
        /// R6-C：宿主每帧把权威战斗状态复制出去（**必须晚于 R5.Pump**）——这样本帧结算产生的
        /// HP/死亡/胜负与随墙钟变化的回蓝在**同一个** FlushState 里一起发布。
        /// </summary>
        private void FlushCombatOnce(double nowMs)
        {
            if (_combatDriver == null || _combatDriver.IsDisposed) { return; }
            if (FaultHostIfCombatDriverFaulted("FlushState(入口)")) { return; }

            try
            {
                _combatDriver.FlushState(nowMs);
            }
            catch (Exception ex)
            {
                Fail("战斗驱动异常（FlushState）：" + ex.GetType().Name + " " + ex.Message);
                return;
            }

            FaultHostIfCombatDriverFaulted("FlushState");
        }

        /// <summary>
        /// R6-C：战斗驱动一旦进入会话失败态 ⇒ **立刻** Fail 整个宿主（不吞、不继续）。
        ///
        /// 返回 true = 「宿主已因此 Fail」。失败原因包括发送失败/队列溢出/结算应用异常/结果冲突 ——
        /// 这些情况下数据已经不可信，只 log 仍继续跑等于把一个坏会话当好的用。
        /// </summary>
        private bool FaultHostIfCombatDriverFaulted(string where)
        {
            if (_combatDriver == null || !_combatDriver.IsFaulted) { return false; }

            Fail("战斗驱动会话失败（" + where + "：" + _combatDriver.FaultReason + "）：" + _combatDriver.FaultError);
            return true;
        }

        /// <summary>
        /// R6-C：权威死亡（R6 的 `PlayerDied`，每名玩家每会话只来一次）。
        ///
        /// 两件事必须同时发生，顺序也是冻结的：
        ///   ① 用**最后一份权威事实**补一条 Alive=false 历史样本（真实 AuthorityServer 帧 +
        ///      该副本真实输入边界）—— 历史没有「按目标移除」，不补的话死亡者还会被旧帧锚当可命中目标；
        ///   ② `MovementDriver.Freeze()`：死亡后不得再推进运动（`Freeze` 只冻结 AP 时间轴/表现，
        ///      Authority 的 Pump 不看 `_frozen`，所以 PumpMovement 里冻结者不再被 Pump）。
        ///
        /// **死亡 ≠ 掉线**：本方法**不**摘接缝、**不** unbind —— 死者仍在名册与连接上，
        /// 终局结果仍要等它的 ResultAck 才闭环（判据混用会让战斗少一个 ACK 等待者而提前就绪）。
        /// </summary>
        private void OnCombatPlayerDied(PMR3Player player)
        {
            if (player == null || _disposed) { return; }

            int uid = player.Uid;

            // 两条**必达**不变量：① 历史立刻补 Alive=false（否则死者还会被旧帧锚当可命中目标）；
            // ② 该副本的 Mover 真的不再推进。任一条没做到 ⇒ 本局的死亡真值已与运动/命中集合不同步，
            // 只能**整体失败**，绝不能只 Warn 后继续跑（那等于把坏会话当好的用、日志还谎报成功）。
            //
            // 为什么必须**自己**判定而不能靠「抛异常让驱动发现」：R6 驱动对订阅者异常是**逐订阅者隔离**
            // （只计数 + 告警，权威状态不受影响），异常逃出去就是被吞掉；而那个告警出口
            // （`PMR3Player.CombatWarn`）在本宿主未接线，等于无人知晓 —— 正是「假成功」。
            // 这里也只 Fault、**不**重放任何结算：重放等于为一次回调/日志异常重复伤害。
            string failure = null;

            PMR4MovementDriver driver;
            if (_driversByUid.TryGetValue(uid, out driver) && driver != null && !driver.IsDisposed)
            {
                if (RecordProjectileUnavailableSample(uid, player, driver, false))
                {
                    _combatDeathHistorySamples++;
                }
                else
                {
                    failure = "历史 Alive=false 样本未写入";
                }

                try
                {
                    driver.Freeze();
                    if (!driver.IsFrozen) { failure = CombineDeathFailure(failure, "Freeze 未生效"); }
                }
                catch (Exception ex)
                {
                    failure = CombineDeathFailure(failure, "Freeze 抛异常 " + ex.GetType().Name);
                }
            }

            // 没有运动 driver 时两种情况都已由别的路径覆盖：断线收尾已写过 Alive=false 并 Dispose 过它；
            // 且 core 的结算会跳过非 Connected 目标 ⇒ 走不到「无 driver 的死亡」。因此这里不算失败。

            _combatDeathsReported++;

            if (failure != null)
            {
                Fail("权威死亡收尾失败（uid=" + uid.ToString(CultureInfo.InvariantCulture) + "）：" + failure
                     + "；history 与 movement 已不同步，拒绝假成功");
                return;
            }

            // 日志放在状态收尾**之后**：日志自身抛异常不得被当成状态失败（更不得触发重放/重复伤害）。
            Info("R6-C 权威死亡：uid=" + uid.ToString(CultureInfo.InvariantCulture)
                 + " netId=" + player.NetId.Value.ToString(CultureInfo.InvariantCulture)
                 + " 的 Mover 已 Freeze、历史已补 Alive=false（仍在线上，仍需 ACK 结果）");
        }

        /// <summary>合成死亡收尾的两条并列失败原因（null 安全；只用于故障文本）。</summary>
        private static string CombineDeathFailure(string existing, string addition)
        {
            return existing == null ? addition : existing + "；" + addition;
        }

        /// <summary>
        /// R6-C：权威终局（R6 的 `OutcomeFrozen`，只来一次）。冻结**全部**运动驱动；
        /// 之后 <see cref="PumpProjectiles"/> 只走 `Pump(0)` 排空声明入站，不再推进任何运动子步。
        /// </summary>
        private void OnCombatOutcomeFrozen(uint outcomeId, int winnerTeamId)
        {
            _combatOutcomeFrozen = true;
            _combatTerminalFreezes++;

            int frozen = 0;
            List<int> uids = new List<int>(_driversByUid.Keys);
            for (int i = 0; i < uids.Count; i++)
            {
                PMR4MovementDriver driver;
                if (!_driversByUid.TryGetValue(uids[i], out driver) || driver == null || driver.IsDisposed)
                {
                    continue;
                }

                try
                {
                    driver.Freeze();
                    frozen++;
                }
                catch (Exception ex)
                {
                    Warn("终局冻结运动驱动异常（uid=" + uids[i].ToString(CultureInfo.InvariantCulture) + "）："
                         + ex.GetType().Name);
                }
            }

            Info("R6-C 权威终局：outcome=" + outcomeId.ToString(CultureInfo.InvariantCulture)
                 + " winner=" + winnerTeamId.ToString(CultureInfo.InvariantCulture)
                 + "（名册原始 TeamId）已冻结 " + frozen.ToString(CultureInfo.InvariantCulture)
                 + " 个 Mover；此后 R5 仅 step0 排入站");
        }

        /// <summary>
        /// R6-C：`ResultReadyForLobby` 后**单次**提交 Lobby（winner 用冻结的名册原始 TeamId、摘要用冻结副本）。
        /// 既有 ResultAck → Exited 闭环不变；smoke 路径完全不经过这里（也不会重复提交）。
        /// </summary>
        private void SubmitCombatResultIfReady()
        {
            if (!_combatEnabled || _combatSubmitted || _faulted) { return; }
            if (_combatDriver == null || _combatDriver.IsDisposed || _lobby == null) { return; }
            if (!_combatDriver.ResultReadyForLobby) { return; }

            if (_lobby.HasResult)
            {
                // Lobby 侧已经有结果（幂等重入/重复帧）：不再提交第二次。
                _combatSubmitted = true;
                return;
            }

            byte[] summary = _combatDriver.ResultSummary;
            if (summary == null || summary.Length == 0)
            {
                Fail("权威战斗结果摘要为空，拒绝向 Lobby 提交");
                return;
            }

            string error;
            if (!_lobby.SubmitResult(_combatDriver.WinnerTeamId, summary, out error))
            {
                Fail("提交权威战斗结果失败：" + error);
                return;
            }

            _combatSubmitted = true;
            Info("R6-C 已提交权威战斗结果：winner=" + _combatDriver.WinnerTeamId.ToString(CultureInfo.InvariantCulture)
                 + "（名册原始 TeamId）outcome=" + _combatDriver.FrozenOutcomeId.ToString(CultureInfo.InvariantCulture)
                 + " reason=" + _combatDriver.FrozenEndReason.ToString()
                 + " summary=" + summary.Length.ToString(CultureInfo.InvariantCulture)
                 + "B 已确认 owner=" + _combatDriver.ResultAckCount.ToString(CultureInfo.InvariantCulture)
                 + "（摘要已冻结，不因断线/重复结算改变）");
        }

        /// <summary>
        /// R6-C 授权策略的**真实位置**来源（`PMCombatTryGetOwnerPosition`）：
        /// 只认「core 说这名 owner 仍在册且 Connected 未死」+「本局确有它的运动 driver 且未 Freeze/未释放」
        /// +「权威位置有限」。任一不成立即返回 false ⇒ 策略拒绝授权（fail closed），
        /// 绝不拿 0 向量去生成一颗弹。
        /// </summary>
        private bool TryGetCombatOwnerPosition(PMR3Player player, out PMVector3 position)
        {
            position = PMVector3.Zero;

            if (player == null || !player.NetId.IsValid || player.NetId.Value == 0u) { return false; }

            if (_combatModel != null)
            {
                PMCombatPlayerSnapshot snapshot = _combatModel.GetPlayer(player.NetId.Value);
                if (snapshot == null || !snapshot.Connected || snapshot.Dead) { return false; }
            }

            PMR4MovementDriver driver;
            if (!_driversByUid.TryGetValue(player.Uid, out driver) || driver == null || driver.IsDisposed)
            {
                return false;
            }

            if (driver.IsFrozen) { return false; }

            PMMoverSyncState sync = driver.GetAuthoritativeSync();
            if (!sync.Position.IsFinite) { return false; }

            position = sync.Position;
            return true;
        }

        /// <summary>
        /// R6-C 授权策略的墙钟：**本帧唯一的 Pump 墙钟**（<see cref="_projectileWallNowMs"/>）。
        ///
        /// 为什么必须是这个值：R6 契约要求「同一帧所有 core 入口共用同一个单调 clock」，而策略是在
        /// `R5.Pump` 内被回调的 —— 它必须与 R6.Pump / R6.FlushState 拿到的是同一个 `now`。
        /// 也正因为如此，策略**不读** UnityEngine.Time（R6 驱动引擎无关的硬约束：位置与时钟都由宿主注入）。
        /// </summary>
        private double GetCombatPumpWallNowMs()
        {
            return _projectileWallNowMs;
        }

        /// <summary>
        /// R6-C 权威命中过滤：**只用 core 的真实队伍与存活事实**（不用出生几何的归一化 teamIndex）。
        ///
        /// 与诊断过滤的差别（这正是 R6-C 要换掉它的原因）：
        ///   · 队伍来自 <see cref="PMCombatSession"/> 名册（真实 TeamId），因此「正式模式 teamIndex 0/1」
        ///     不会与 core 的 TeamId 冲突；
        ///   · 存活/连接来自 core（`Connected &amp;&amp; !Dead`），与 `ApplySettlement` 的判据同源；
        ///   · 身份不在 core 名册（未知 netId）⇒ 拒绝（fail closed）。
        /// </summary>
        private bool AcceptCombatHit(PMProjectileKey projectile, PMProjectileTargetSample target,
                                     PMVector3 sanitizedImpact)
        {
            if (_combatModel == null) { return RejectProjectileHit("权威战斗核心未建立"); }

            PMCombatPlayerSnapshot owner = _combatModel.GetPlayer(projectile.OwnerNetId);
            PMCombatPlayerSnapshot victim = _combatModel.GetPlayer(target.NetId);

            if (owner == null || victim == null)
            {
                return RejectProjectileHit("身份不在权威战斗名册（owner="
                    + projectile.OwnerNetId.ToString(CultureInfo.InvariantCulture) + " target="
                    + target.NetId.ToString(CultureInfo.InvariantCulture) + "）");
            }

            if (owner.NetId == victim.NetId)
            {
                return RejectProjectileHit("self 命中（netId="
                    + owner.NetId.ToString(CultureInfo.InvariantCulture) + "）");
            }

            if (!owner.Connected || owner.Dead)
            {
                return RejectProjectileHit("owner 不可用（connected=" + (owner.Connected ? 1 : 0)
                                           + " dead=" + (owner.Dead ? 1 : 0) + "）");
            }

            if (!victim.Connected || victim.Dead)
            {
                return RejectProjectileHit("target 不可用（connected=" + (victim.Connected ? 1 : 0)
                                           + " dead=" + (victim.Dead ? 1 : 0) + "）");
            }

            if (owner.TeamId == victim.TeamId)
            {
                return RejectProjectileHit("同队命中（真实 TeamId="
                    + owner.TeamId.ToString(CultureInfo.InvariantCulture) + "）");
            }

            return true;
        }

        /// <summary>R6-C 权威命中过滤器：把宿主的 core 判定接到 R5 的注入面（driver 只见接口）。</summary>
        private sealed class CombatModelHitFilter : IPMProjectileHitFilter
        {
            private readonly PMDsSessionHost _host;

            public CombatModelHitFilter(PMDsSessionHost host)
            {
                _host = host;
            }

            public bool Accept(PMProjectileKey projectile, PMProjectileTargetSample target,
                               PMVector3 sanitizedImpact)
            {
                return _host.AcceptCombatHit(projectile, target, sanitizedImpact);
            }
        }

        /// <summary>R6-C 单行诊断摘要（进 <see cref="Describe"/> 与心跳日志）。</summary>
        private string DescribeCombatWiring()
        {
            if (!_combatEnabled) { return "disabled(smoke)"; }
            if (_combatDriver == null) { return "<none>"; }

            return "started=" + (_combatStarted ? 1 : 0)
                   + " frozen=" + (_combatOutcomeFrozen ? 1 : 0)
                   + " submitted=" + (_combatSubmitted ? 1 : 0)
                   + " outcome=" + _combatDriver.FrozenOutcomeId.ToString(CultureInfo.InvariantCulture)
                   + " winner=" + _combatDriver.WinnerTeamId.ToString(CultureInfo.InvariantCulture)
                   + " acks=" + _combatDriver.ResultAckCount.ToString(CultureInfo.InvariantCulture)
                   + " ready=" + (_combatDriver.ResultReadyForLobby ? 1 : 0)
                   + " applied=" + _combatDriver.SettlementsApplied.ToString(CultureInfo.InvariantCulture)
                   + " rejected=" + _combatDriver.SettlementsRejected.ToString(CultureInfo.InvariantCulture)
                   + " deaths=" + _combatDeathsReported.ToString(CultureInfo.InvariantCulture)
                   + " deathSamples=" + _combatDeathHistorySamples.ToString(CultureInfo.InvariantCulture)
                   + " terminalFreezes=" + _combatTerminalFreezes.ToString(CultureInfo.InvariantCulture)
                   + " faulted=" + (_combatDriver.IsFaulted ? _combatDriver.FaultReason.ToString() : "<none>");
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

            // R5-C：投射物接线的释放次序（契约：Driver → Motion → 地图）：
            //   · Driver.Dispose 取消全部未消费 NetId 预留、销毁本会话权威对象（走桥 ⇒ 必须先于
            //     _bridge.Dispose）、并摘掉本 world 的事件订阅；**不留**悬空预留/事件；
            //   · Motion.Dispose 释放查询缓冲与停止标记表（不持有 GameObject）；
            //   · 地图/场景对象的释放仍在下面（本接线只持 Collider 引用副本，不持场景所有权）。
            //   放在 Detach 之前是刻意的：让释放发生在「世界/桥仍然完整接线」的稳态下。
            ReleaseProjectileWiring();

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
            _rosterHeroByUid.Clear();

            _playersByUid.Clear();
            _world = null;
        }

        // =================================================================================
        //  R5-C：诊断投射物接线（History / Motion / Driver / 可信策略 / 身份过滤）
        // =================================================================================

        /// <summary>
        /// 建立本会话的投射物接线。**必须在场景就绪之前、任何 player 接入之前**调用。
        ///
        /// 依赖的每一件东西都是**现存字段**，不另造第二套：
        ///   · 物理场景 = <see cref="_movementPhysicsScene"/>（本地物理场景，已校验 != 默认物理世界）；
        ///   · 白名单   = <see cref="_movementAllowlist"/>（诊断 = 本地场景地板/墙；正式 = 正式地图 Collider）；
        ///   · 层掩码   = <see cref="_movementQuery"/>.LayerMask（与运动查询同口径）。
        /// 任何一项不成立即失败：**不**退回默认物理世界，也**不**用空白名单冒充「无阻挡」。
        /// </summary>
        private bool CreateProjectileWiring(out string error)
        {
            error = null;

            if (!_movementPhysicsScene.IsValid() || _movementPhysicsScene.Equals(Physics.defaultPhysicsScene))
            {
                error = "投射物运动 hook 需要独立的本地物理场景（当前无效或等于 Physics.defaultPhysicsScene）";
                return false;
            }

            if (_movementQuery == null)
            {
                error = "运动碰撞查询未建立，无法取得同口径层掩码";
                return false;
            }

            if (_movementAllowlist == null || _movementAllowlist.Length == 0)
            {
                error = "投射物运动白名单为空（拒绝以空白名单冒充无障碍）";
                return false;
            }

            // epoch 已在 Initialize 校验非 0；history 与 driver 都固定本会话 epoch。
            _projectileHistory = new PMProjectileHistory(_boot.Epoch);

            try
            {
                _projectileMotion = new PMUnityProjectileMotion(_movementPhysicsScene, _movementAllowlist,
                                                               _movementQuery.LayerMask);
            }
            catch (Exception ex)
            {
                error = "投射物运动适配器构造失败：" + ex.GetType().Name + " " + ex.Message;
                // F5：每条失败路径都自清理（幂等），不让半成品接线悬空到 Dispose/调用方。
                ReleaseProjectileWiring();
                return false;
            }

            // R6-C：创建次序冻结 —— 正式玩法必须**先有 core**，再由 core 造出授权策略（policy 是 R5 的
            // 构造参数），最后才能建 R5 driver；smoke 保持原来的诊断策略/过滤。
            if (_combatEnabled)
            {
                try
                {
                    _combatModel = new PMCombatSession(_boot.Epoch);
                }
                catch (Exception ex)
                {
                    error = "权威战斗核心构造失败：" + ex.GetType().Name + " " + ex.Message;
                    ReleaseProjectileWiring();
                    return false;
                }

                try
                {
                    // 策略的每一处信息都来自 core + 宿主真实运动位置 + 宿主墙钟；
                    // 客户端自报的 spec/伤害/位置**一律不采信**（payload 里根本没有 spec 面）。
                    _projectilePolicy = PMR6CombatDriver.CreateAuthorityPolicy(
                        _combatModel, TryGetCombatOwnerPosition, GetCombatPumpWallNowMs);
                    _projectileHitFilter = new CombatModelHitFilter(this);
                }
                catch (Exception ex)
                {
                    error = "权威授权策略构造失败：" + ex.GetType().Name + " " + ex.Message;
                    ReleaseProjectileWiring();
                    return false;
                }
            }
            else
            {
                _projectilePolicy = new DiagnosticProjectileAuthorityPolicy(this);
                _projectileHitFilter = new RosterIdentityHitFilter(this);
            }

            try
            {
                _projectileDriver = new PMR5ProjectileDriver(_world, _bridge, _boot.Epoch, _projectileHistory,
                                                            _projectilePolicy, _projectileMotion, _projectileHitFilter);
            }
            catch (Exception ex)
            {
                error = "投射物驱动构造失败：" + ex.GetType().Name + " " + ex.Message;
                ReleaseProjectileWiring();
                return false;
            }

            if (_combatEnabled)
            {
                try
                {
                    // R6 driver 在本会话里是 R5 Settlement 的**唯一**消费者（它在构造里订阅自己那一份）。
                    _combatDriver = new PMR6CombatDriver(_world, _bridge, _boot.Epoch, _projectileDriver, _combatModel);
                }
                catch (Exception ex)
                {
                    error = "战斗网络驱动构造失败：" + ex.GetType().Name + " " + ex.Message;
                    ReleaseProjectileWiring();
                    return false;
                }

                _combatDriver.PlayerDied += OnCombatPlayerDied;
                _combatDriver.OutcomeFrozen += OnCombatOutcomeFrozen;
            }
            else
            {
                // 结算出口：订阅 = 唯一消费者（smoke 只记诊断计数/日志，不扣血）。
                // 正式玩法**不**在这里订阅：R6 driver 已是唯一消费者，再订一次就是「同一条结算双消费」。
                _projectileDriver.Settlement += OnProjectileSettlement;
            }

            if (_projectileDriver.HostMotion == null || _projectileDriver.History == null
                || _projectileDriver.Policy == null)
            {
                error = "投射物驱动接线不完整（hostMotion / history / policy 至少一项未注入）";
                // F5：本分支下 driver 已构造且已订阅，必须在此处自行释放（幂等：调用方/Dispose 再调无害）。
                ReleaseProjectileWiring();
                return false;
            }

            Info("R5-C 诊断投射物接线：隔离物理场景="
                 + (_movementScene.IsValid() ? _movementScene.name : "<none>")
                 + " isDefaultWorld=" + _movementPhysicsScene.Equals(Physics.defaultPhysicsScene)
                 + " 白名单Collider=" + _projectileMotion.AllowlistCount.ToString(CultureInfo.InvariantCulture)
                 + " layerMask=0x" + _movementQuery.LayerMask.ToString("X8", CultureInfo.InvariantCulture)
                 + " epoch=" + _boot.Epoch.ToString(CultureInfo.InvariantCulture)
                 + " 步长=" + PMProjectileDiagnosticConfig.StepMs.ToString(CultureInfo.InvariantCulture) + "ms"
                 + " 单帧最多=" + PMProjectileDiagnosticConfig.MaxStepsPerPump.ToString(CultureInfo.InvariantCulture) + "步"
                 + " 开火间隔=" + PMProjectileDiagnosticConfig.FireIntervalMs.ToString(CultureInfo.InvariantCulture) + "ms"
                 + " 枪口偏移=" + PMProjectileDiagnosticConfig.MuzzleOffsetM.ToString("0.###", CultureInfo.InvariantCulture) + "m");
            if (_combatEnabled)
            {
                Info("R6-C 权威战斗接线：PMCombatSession(epoch=" + _boot.Epoch.ToString(CultureInfo.InvariantCulture)
                     + ") → CreateAuthorityPolicy(model, 真实运动位置, 本帧墙钟) → R5(policy) → PMR6CombatDriver；"
                     + "R5 Settlement 的**唯一**消费者是 R6 driver（宿主不再订阅诊断结算、也不做宿主侧 Drain）。");
                Info("R6-C 权威战斗口径：名册 uid/team/hero 全部来自引导文件名册（team 用**原始 TeamId**，"
                     + "不用 formalTeamIndex0/1）；History.Alive = core.Connected && !Dead（未知=false）；"
                     + "命中过滤按 core 真实 TeamId/存活；死亡 Freeze 对应 Mover 并立即补 Alive=false；"
                     + "终局冻结全部 Mover 且 R5 只 Pump(0) 排入站。");
            }
            else
            {
                Info("R5-C 声明：这是**诊断投射物探针**（非英雄普通攻击/大招/资源校验，非 R6 伤害）；"
                     + "结算只记可观察命中计数与日志，不扣血、不消费旧 CommandManger/BattleManger/旧 shell。"
                     + "（smoke 路径；正式玩法改走 R6-C 权威战斗接线）");
            }

            return true;
        }

        /// <summary>
        /// 释放投射物接线（幂等、可被失败路径调用）。次序固定：**CombatDriver → R5 Driver → Motion**。
        /// **不**触碰地图/场景对象（那是宿主原有的释放职责，且在更后面）。
        ///
        /// 为什么战斗驱动在最前：它是 R5 的消费者（订阅 + 持有 R5 引用），必须先摘掉自己那一份，
        /// 否则 R5 Dispose 之后它仍会通过订阅/引用触达已释放对象。
        /// </summary>
        private void ReleaseProjectileWiring()
        {
            // R6-C：释放次序冻结 —— **先**摘掉战斗驱动（含它自己那一份 R5 Settlement 订阅 + 本地账），
            // **再**释放 R5，最后释放运动 hook。反序会让战斗驱动继续持有已释放的 R5 引用。
            if (_combatDriver != null)
            {
                try
                {
                    _combatDriver.PlayerDied -= OnCombatPlayerDied;
                    _combatDriver.OutcomeFrozen -= OnCombatOutcomeFrozen;
                }
                catch (Exception ex) { Error("解除战斗驱动事件订阅异常：" + ex.GetType().Name); }

                try { _combatDriver.Dispose(); }
                catch (Exception ex) { Error("释放战斗驱动异常：" + ex.GetType().Name + " " + ex.Message); }

                _combatDriver = null;
            }

            // 核心不是 IDisposable（纯数据 + 本地账），只放引用即可；下一局会是新对象（epoch 不复用）。
            _combatModel = null;

            if (_projectileDriver != null)
            {
                try { _projectileDriver.Settlement -= OnProjectileSettlement; }
                catch (Exception ex) { Error("解除投射物结算订阅异常：" + ex.GetType().Name); }

                try { _projectileDriver.Dispose(); }
                catch (Exception ex) { Error("释放投射物驱动异常：" + ex.GetType().Name + " " + ex.Message); }

                _projectileDriver = null;
            }

            if (_projectileMotion != null)
            {
                try { _projectileMotion.Dispose(); }
                catch (Exception ex) { Error("释放投射物运动适配器异常：" + ex.GetType().Name + " " + ex.Message); }

                _projectileMotion = null;
            }

            _projectileHistory = null;
            _projectilePolicy = null;
            _projectileHitFilter = null;
            _movementAllowlist = null;
            _projectileLastActivationByOwner.Clear();
            _projectileLastFireWallMsByOwner.Clear();

            _projectileAccumulatorMs = 0.0;
            _lastProjectilePumpMs = 0L;
        }

        /// <summary>
        /// 每宿主帧推进投射物。次序（契约冻结）：
        ///   ① 为**所有**运动 driver 记一次本帧权威胶囊样本（after PumpMovement / before 投射物推进）；
        ///   ② 固定 16ms 累加器推进（单帧最多 <see cref="PMProjectileDiagnosticConfig.MaxStepsPerPump"/> 子步），
        ///      **绝不用大 elapsed 直接放大 dt**；剩余累积量钳到单帧上限（有界追赶，丢弃量计数）；
        ///   ③ 零子步时仍 Pump(0)：把本帧到达的声明入站（上行生成/命中、下行裁决）排空，不让裁决压到下一帧。
        /// 暂停/断线/故障：断线与故障的清理在 <see cref="PruneDisconnectedDrivers"/> / <see cref="Fail"/> 路径上，
        /// 不在这里伪造推进；长时间停滞后由 ② 的上限钳制，不会一次性补上巨大 dt。
        /// </summary>
        private void PumpProjectiles(long nowMs)
        {
            if (_projectileDriver == null || _projectileDriver.IsDisposed) { return; }

            // F7：驱动已经是会话失败态（例：下行裁决 RPC 发送失败）⇒ 立刻 Fail **整个宿主**。
            // 这里（而不是只在 PumpProjectileOnce 里）再查一次，是为了让「任何非本轮投射物 Pump
            // 路径造成的驱动 fault」也不会被当成没发生过继续跑。
            if (FaultHostIfProjectileDriverFaulted("PumpProjectiles")) { return; }

            double elapsedMs = 0.0;
            if (_lastProjectilePumpMs != 0L && nowMs > _lastProjectilePumpMs)
            {
                elapsedMs = (double)(nowMs - _lastProjectilePumpMs);
            }

            _lastProjectilePumpMs = nowMs;
            _projectileWallNowMs = (double)nowMs;

            // ① 历史：必须在本帧投射物推进之前写入（否则本帧命中验证读到的是上一帧的位置）。
            RecordProjectileHistory(_projectileWallNowMs);

            // R6-C 终局：只 `Pump(0)` 排空声明入站，**不再推进任何运动子步**（契约「R5 仅 step0 排入站」）。
            // 理由：终局后新命中不可能产生结算（core 已 MatchEnded），继续推进只会让冻结前的弹继续走、
            // 继续消耗 CPU 与视图脏量，还会在下行制造无意义的裁决包。
            if (_combatOutcomeFrozen)
            {
                _projectileAccumulatorMs = 0.0;
                if (!PumpProjectileOnce(0)) { return; }
                DrainDsProjectileViews();
                return;
            }

            // ② 固定步长累加器的推进口径：elapsed **只在本帧累加一次**，每个子步恰好扣掉一个
            //    stepMs，因此一帧的真实推进量 ≈ elapsed（不双计，也不用大 elapsed 直接放大 dt）。
            //    循环中一旦宿主已失效立刻中止：不再做任何剩余子步（避免失败后继续驱动驱动）。
            int stepMs = PMProjectileDiagnosticConfig.StepMs;
            int maxSteps = PMProjectileDiagnosticConfig.MaxStepsPerPump;

            _projectileAccumulatorMs += elapsedMs;

            int steps = 0;
            while (_projectileAccumulatorMs >= (double)stepMs && steps < maxSteps)
            {
                if (_faulted) { return; }
                if (!PumpProjectileOnce(stepMs)) { return; }
                _projectileAccumulatorMs -= (double)stepMs;
                steps++;
            }

            double capMs = (double)stepMs * (double)maxSteps;
            if (_projectileAccumulatorMs > capMs)
            {
                _diagnosticProjectileDroppedMs += _projectileAccumulatorMs - capMs;
                _projectileAccumulatorMs = capMs;
            }

            // ③ 零子步也要排空声明入站（stepMs=0 不推进运动）。
            if (steps == 0)
            {
                if (!PumpProjectileOnce(0)) { return; }
            }

            // R6-C：正式玩法的结算消费者是 R6 driver（它自己在 Pump 里做 Drain 兜底）。
            // 宿主若也在这里 Drain，就是让同一条结算从两个入口被消费 —— 双 Drain 会毁掉伤害归属
            //（谁扣的血、谁判的死都不可判定）。因此诊断 Drain **只在 smoke** 生效。
            if (!_combatEnabled)
            {
                DrainDiagnosticSettlementBuffer();
            }

            // F1：DS **不消费表现**，但驱动的视图脏通道只有 DrainViewChanges 才清。
            // 不排空就会一路堆到驱动的 MaxDirtyViews(4096) 才整体作废自清（该诊断量也会失真）。
            DrainDsProjectileViews();
        }

        /// <summary>一次投射物 Pump；任何异常 / 驱动 fault 都走宿主 Fail（不吞、不继续）。</summary>
        private bool PumpProjectileOnce(int stepMs)
        {
            try
            {
                _projectileDriver.Pump(_projectileWallNowMs, stepMs);
            }
            catch (Exception ex)
            {
                Fail("投射物驱动异常（stepMs=" + stepMs.ToString(CultureInfo.InvariantCulture) + "）："
                     + ex.GetType().Name + " " + ex.Message);
                return false;
            }

            return !FaultHostIfProjectileDriverFaulted("PumpProjectileOnce");
        }

        /// <summary>
        /// F7：驱动已处于会话失败态 ⇒ **立刻 Fail 整个宿主**（不吞、不继续）。
        ///
        /// 返回 true = 「宿主已因此 Fail」，调用方据此中止本帧剩余工作。
        /// 语义边界：驱动 Fault 的原因包括发送失败 / 队列溢出 / Coordinator faulted，
        /// 这些都不能只 log 仍继续（那些情况实际上就是「数据已经不可信」）。
        /// </summary>
        private bool FaultHostIfProjectileDriverFaulted(string where)
        {
            if (_projectileDriver == null || !_projectileDriver.IsFaulted) { return false; }

            Fail("投射物驱动会话失败（" + where + "：" + _projectileDriver.FaultReason + "）："
                 + _projectileDriver.FaultError);
            return true;
        }

        /// <summary>
        /// 为**所有**运动 driver 记一条本帧权威胶囊样本。七元组全部取真实来源：
        ///   Epoch/NetId/StreamVersion = 会话 epoch / 该副本 NetId / 运动 driver 的流代次；
        ///   ServerFrame   = 本宿主自己决定的 AuthorityServer 帧（<see cref="_movementServerFrame"/>）；
        ///   OutputFrame   = 运动 driver 的权威输出边界（Authority 角色 ⇒ Input 域）；
        ///   TotalSimTimeMs= 运动 driver 的权威累计仿真时间；WorldTimeMs = **同一** monotonic 墙钟（= Pump 用值）；
        ///   Position      = 权威 SyncState 位置；胶囊尺寸 = PMMoverDefaults × Sync.Scale（有效缩放）。
        /// 记录失败**不静默吞**：计数 + 限流告警（历史是命中验证的唯一位置来源，缺它必须看得见）。
        /// </summary>
        private void RecordProjectileHistory(double worldNowMs)
        {
            if (_projectileHistory == null || _driversByUid.Count == 0) { return; }

            List<int> uids = new List<int>(_driversByUid.Keys);
            for (int i = 0; i < uids.Count; i++)
            {
                int uid = uids[i];

                PMR4MovementDriver driver;
                if (!_driversByUid.TryGetValue(uid, out driver) || driver == null || driver.IsDisposed) { continue; }

                PMR3Player player;
                if (!_playersByUid.TryGetValue(uid, out player) || player == null || !player.NetId.IsValid) { continue; }

                PMMoverSyncState sync = driver.GetAuthoritativeSync();
                float scale = sync.Scale;
                if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f)
                {
                    RejectProjectileHistory("uid=" + uid.ToString(CultureInfo.InvariantCulture)
                        + " 的 Sync.Scale 非法（" + scale.ToString("R", CultureInfo.InvariantCulture) + "）");
                    continue;
                }

                PMProjectileTargetSample sample = new PMProjectileTargetSample();
                sample.Epoch = _boot.Epoch;
                sample.NetId = player.NetId.Value;
                sample.StreamVersion = driver.StreamVersion;
                sample.ServerFrame = _movementServerFrame;
                sample.OutputFrame = driver.OutputBoundary;
                // F3 复核：AuthorityTotalSimTimeMs 是**只读**观察量，无输入帧（占位步 StepMs==0）不推进它。
                // 历史 Record 只要求「同 stream 内不回退」（相等合法），帧锚新鲜度也按仿真时间算，
                // 因此取它就是正确口径；契约明令禁止用「帧号 × 16ms」换算代替它。
                sample.TotalSimTimeMs = driver.AuthorityTotalSimTimeMs;
                sample.WorldTimeMs = worldNowMs;
                sample.Position = sync.Position;
                sample.RadiusM = PMMoverDefaults.CapsuleRadiusMeters * scale;
                sample.HalfHeightM = PMMoverDefaults.CapsuleHalfHeightMeters * scale;
                // 没有传送判定源：恒 false，**不伪造**未观测的事实（见报告诚实边界）。
                sample.Teleported = false;

                // R6-C：正式玩法的 Alive 来自**权威战斗核心**（Connected && !Dead；未知 netId ⇒ false），
                // 因此死亡/断线者立刻退出可命中集合。smoke 没有权威生命值系统，沿用恒 true 的诊断口径
                //（那里「命中」只是诊断计数，不产生伤害）。
                if (_combatEnabled)
                {
                    PMCombatPlayerSnapshot combat = _combatModel != null
                        ? _combatModel.GetPlayer(player.NetId.Value)
                        : null;
                    sample.Alive = combat != null && combat.Connected && !combat.Dead;
                }
                else
                {
                    sample.Alive = true;
                }

                string rejectReason;
                if (!_projectileHistory.Record(sample, out rejectReason))
                {
                    RejectProjectileHistory("uid=" + uid.ToString(CultureInfo.InvariantCulture)
                        + " 样本被拒：" + rejectReason);
                }
            }
        }

        private void RejectProjectileHistory(string reason)
        {
            _diagnosticProjectileHistoryRejects++;
            if (_diagnosticProjectileHistoryRejects <= MaxProjectileDiagnosticWarnings)
            {
                Warn("R5-C 历史采样（第 " + _diagnosticProjectileHistoryRejects.ToString(CultureInfo.InvariantCulture)
                     + " 次）：" + reason);
            }
        }

        /// <summary>
        /// **smoke 路径**的结算出口（诊断）：累计可观察命中计数 + 日志。
        /// **不扣血、不写 HP/Mana/SuperEnergy、不接旧普通攻击/资源系统**。
        ///
        /// R6-C：本订阅**只在 smoke** 建立。正式玩法由 `PMR6CombatDriver` 消费结算
        ///（它才是该会话的唯一消费者），宿主的这两个诊断计数因此恒为 0
        ///（见 <see cref="DiagnosticCountersMeaningfulOnlyInSmoke"/>）。
        /// </summary>
        private void OnProjectileSettlement(PMProjectileSettlement settlement)
        {
            _diagnosticProjectileSettlementCount++;
            _diagnosticProjectileHitCount += settlement.HitCount;

            // F8 复核：**一条结算只能计一次**。
            // 驱动把「订阅者抛异常」当成本次未交付，会把同一条结算转进有界待取缓冲，
            // 而本宿主的兜底排空又会在那里再计一次 —— 那就是「事件 + drain 双重计数」。
            // 计数在日志之前完成，因此只要日志自身不把异常抛回驱动，双重计数就不可能出现。
            try
            {
                LogProjectileSettlement(settlement);
            }
            catch (Exception ex)
            {
                _diagnosticProjectileSettlementLogFailures++;
                if (_diagnosticProjectileSettlementLogFailures <= MaxProjectileDiagnosticWarnings)
                {
                    Warn("R5-C 结算诊断日志抛出异常（第 "
                         + _diagnosticProjectileSettlementLogFailures.ToString(CultureInfo.InvariantCulture)
                         + " 次，已吞掉以保证计数只发生一次）：" + ex.GetType().Name + " " + ex.Message);
                }
            }
        }

        /// <summary>结算的诊断日志（与计数分离：它抛异常不得影响「一条结算只计一次」的不变式）。</summary>
        private void LogProjectileSettlement(PMProjectileSettlement settlement)
        {
            Info("R5-C 诊断结算（non-gameplay，不扣血）：key=" + DescribeProjectileKey(settlement.Key)
                 + " activation=" + settlement.ActivationId.ToString(CultureInfo.InvariantCulture)
                 + " authority=" + settlement.AuthorityNetId.ToString(CultureInfo.InvariantCulture)
                 + " origin=" + settlement.Origin
                 + " stopOnHit=" + (settlement.StopOnHit ? 1 : 0)
                 + " hits=" + settlement.HitCount.ToString(CultureInfo.InvariantCulture)
                 + "（累计命中=" + _diagnosticProjectileHitCount.ToString(CultureInfo.InvariantCulture) + "）");

            if (settlement.Hits == null) { return; }

            for (int i = 0; i < settlement.Hits.Length; i++)
            {
                Info("R5-C 诊断结算目标#" + i.ToString(CultureInfo.InvariantCulture)
                     + " netId=" + settlement.Hits[i].TargetNetId.ToString(CultureInfo.InvariantCulture)
                     + " stream=" + settlement.Hits[i].TargetStreamVersion.ToString(CultureInfo.InvariantCulture)
                     + " 还原=" + settlement.Hits[i].Resolution
                     + " impact=(" + settlement.Hits[i].ImpactPoint.X.ToString("0.###", CultureInfo.InvariantCulture)
                     + "," + settlement.Hits[i].ImpactPoint.Y.ToString("0.###", CultureInfo.InvariantCulture)
                     + "," + settlement.Hits[i].ImpactPoint.Z.ToString("0.###", CultureInfo.InvariantCulture) + ")");
            }
        }

        /// <summary>
        /// 兜底排空驱动的**待取结算缓冲**：正常情况下（订阅生效且订阅者不抛）它恒为空；
        /// 一旦有内容，说明订阅者**确实没交付**（例如未订阅），必须显式看见并继续计数，
        /// 绝不静默丢真实伤害。
        ///
        /// F8：本路径与事件路径**不会**重复计同一条 —— 驱动的交付是「事件 XOR 待取缓冲」，
        /// 而本宿主的订阅者已保证不抛异常（见 <see cref="OnProjectileSettlement"/>），
        /// 因此驱动不会把已交付的条目再入缓冲。
        /// </summary>
        private void DrainDiagnosticSettlementBuffer()
        {
            if (_projectileDriver == null || _projectileDriver.IsDisposed) { return; }
            if (_projectileDriver.PendingSettlementCount <= 0) { return; }

            PMProjectileSettlement[] buffered;
            int drained = _projectileDriver.DrainSettlements(PMR5ProjectileDriver.MaxApplyPerPump, out buffered);
            for (int i = 0; i < drained; i++)
            {
                _diagnosticProjectileUnconsumedSettlements++;
                Warn("R5-C 结算未经订阅交付而进入待取缓冲（第 "
                     + _diagnosticProjectileUnconsumedSettlements.ToString(CultureInfo.InvariantCulture)
                     + " 条）：已按诊断口径消费并计数，绝不丢。");
                OnProjectileSettlement(buffered[i]);
            }
        }

        /// <summary>
        /// F1：DS **必须**周期性排空驱动的视图脏增量，即使它不消费表现。
        ///
        /// 为什么：`PMR5ProjectileDriver.RebuildViews` 每帧把新增/变化/移除的 key 标脏，
        /// 而 `_dirtyViewOrder` / `_dirtyViewSet` **只有** `DrainViewChanges` 才清。
        /// DS 上没有 C 表现消费者，不排空就会一路堆到驱动的 `MaxDirtyViews`(4096) 才整体作废自清，
        /// 该诊断量（`ViewDirtyOverflows`）也会跟着失真；对 DS 而言那是纯浪费。
        ///
        /// 上界：驱动自身保证脏集合 ≤ MaxDirtyViews，因此「取空」本身是有界的；
        /// 排空量 = 本帧真实脏数（DS 是唯一消费者 ⇒ 不会与任何其它消费者抢）。
        /// 丢弃是**正确**的语义：DS 禁止创建表现对象（C2 在 DS 上会显式抛）。
        /// </summary>
        private void DrainDsProjectileViews()
        {
            if (_projectileDriver == null || _projectileDriver.IsDisposed) { return; }

            PMR5ProjectileView[] changes;
            int drained = _projectileDriver.DrainViewChanges(PMR5ProjectileDriver.MaxDirtyViews, out changes);
            if (drained > 0) { _diagnosticProjectileViewDrains += drained; }
        }

        /// <summary>
        /// F2 / R6-C：断线时用「断线前最后一份权威事实」补一条 <c>Alive=false</c> 的历史样本。
        /// 实际写入在 <see cref="RecordProjectileUnavailableSample"/>（与 R6-C 的权威死亡共用同一条口径）。
        /// </summary>
        private void RecordProjectileDisconnectSample(int uid, PMR3Player player, PMR4MovementDriver driver)
        {
            if (RecordProjectileUnavailableSample(uid, player, driver, true))
            {
                _diagnosticProjectileDisconnectSamples++;
            }
        }

        /// <summary>
        /// 补一条 <c>Alive=false</c>（**不可命中**）的历史样本。断线（<paramref name="disconnect"/>=true）与
        /// R6-C 权威死亡（false）共用：两者要求逐条相同 —— 用「最后一份权威事实 + 真实 AuthorityServer 帧 +
        /// 该副本真实输入边界」把目标**立刻**标成不可命中，区别只在归属计数。
        ///
        /// 为什么必须补：历史没有「按目标移除」的 API（也不应该由宿主伪造一套），
        /// 不补的话该副本留在历史里的最后一条样本仍是 <c>Alive=true</c>，会被旧帧锚继续当作可命中目标。
        /// 补样本用的是同一 AuthorityServer 帧 + 同一 OutputFrame ⇒ 走历史 Record 的「同帧取最后输出」分支，
        /// 把本帧那条样本覆盖为不可命中；之后样本自然随保留窗口过期。
        /// 另有一道**实时**兜底：正式玩法由 core 的 Connected/Dead 与 R5 命中过滤复核（见 AcceptCombatHit）。
        /// </summary>
        /// <returns>样本是否真的写入成功（失败原因由 <see cref="RejectProjectileHistory"/> 记账）。</returns>
        private bool RecordProjectileUnavailableSample(int uid, PMR3Player player, PMR4MovementDriver driver,
                                                       bool disconnect)
        {
            if (_projectileHistory == null || player == null || driver == null) { return false; }
            if (!player.NetId.IsValid || player.NetId.Value == 0u) { return false; }
            if (driver.StreamVersion == 0u) { return false; }
            if (_movementServerFrame.Domain != PMFrameDomain.AuthorityServer) { return false; }

            PMMoverSyncState sync = driver.GetAuthoritativeSync();
            float scale = sync.Scale;
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f) { return false; }

            PMProjectileTargetSample sample = new PMProjectileTargetSample();
            sample.Epoch = _boot.Epoch;
            sample.NetId = player.NetId.Value;
            sample.StreamVersion = driver.StreamVersion;
            sample.ServerFrame = _movementServerFrame;
            sample.OutputFrame = driver.OutputBoundary;
            sample.TotalSimTimeMs = driver.AuthorityTotalSimTimeMs;
            sample.WorldTimeMs = _projectileWallNowMs;
            sample.Position = sync.Position;
            sample.RadiusM = PMMoverDefaults.CapsuleRadiusMeters * scale;
            sample.HalfHeightM = PMMoverDefaults.CapsuleHalfHeightMeters * scale;
            sample.Teleported = false;
            sample.Alive = false;   // ← 断线/死亡 = 不可命中

            string rejectReason;
            if (!_projectileHistory.Record(sample, out rejectReason))
            {
                RejectProjectileHistory((disconnect ? "断线样本" : "死亡样本")
                                        + "被拒（uid=" + uid.ToString(CultureInfo.InvariantCulture)
                                        + "）：" + rejectReason);
                return false;
            }

            return true;
        }

        /// <summary>
        /// F2：该 uid 当前是不是「可被命中的权威目标」——连接就绪 + 运动 driver 存活且未 Freeze。
        /// 历史样本的 Alive 只反映**采样那一刻**的事实，断线后旧帧锚仍可能解析到活样本，
        /// 所以命中过滤必须拿实时连接事实兜底（缺数据 fail closed）。
        /// </summary>
        private bool IsProjectileTargetAuthoritative(int uid)
        {
            PMR3Player player;
            if (!_playersByUid.TryGetValue(uid, out player) || player == null) { return false; }
            if (player.OwnerConnection == null || !player.OwnerConnection.IsReady) { return false; }

            PMR4MovementDriver driver;
            if (!_driversByUid.TryGetValue(uid, out driver) || driver == null || driver.IsDisposed) { return false; }

            return !driver.IsFrozen;
        }

        // ---------------------------------------------------------------- F6：上行重复意图
        //
        // F6（同一 activation 的重传不得反向撤销已确认弹）的**根治点已收口到 driver**
        //（PMR5ProjectileDriver.HandleServerSpawn 的「已受理 key 无副作用幂等门」），
        // 因此这里**不再**需要宿主侧装饰器去改写 player.ProjectileDriver：
        //   · 装饰器会破坏驱动的 UnbindPlayer/Dispose 身份判定（ReferenceEquals 只认自己的适配器）；
        //   · 「完全相同指纹 + 32 条环」只是窗口假设，驱动侧用**自己的登记事实**收口才是通用幂等。
        // 保留本注释以说明这里为什么是空的（不要再装回去）。

        /// <summary>
        /// 可信授权（由 <see cref="DiagnosticProjectileAuthorityPolicy"/> 回调）。
        /// 只认 DS 自己知道的事实：已认证绑定、名册、单调 activation、200ms 间隔、未 Freeze 的运动 driver，
        /// 以及 DS 自己的权威位置；trustedSpec 来自共享诊断工厂（上行根本没有 spec 面可以伪造）。
        ///
        /// 返回 false = 本次不予授权（driver 会取消预留并向 owner 下行 Rejected）。
        /// </summary>
        private bool TryAuthorizeDiagnosticSpawn(PMR3Player player, PMProjectileSpawnIntent intent,
            out PMProjectileSpec trustedSpec, out PMVector3 ownerPosition,
            out PMActivationResult verdict, out string error)
        {
            trustedSpec = null;
            ownerPosition = PMVector3.Zero;
            verdict = PMActivationResult.Rejected;
            error = null;

            if (player == null || intent == null)
            {
                error = "player/intent 为 null";
                return RejectProjectileRequest(error);
            }

            if (!player.NetId.IsValid || player.NetId.Value == 0u)
            {
                error = "player NetId 无效";
                return RejectProjectileRequest(error);
            }

            int uid = player.Uid;
            if (uid <= 0 || !_expectedUids.Contains(uid))
            {
                error = "uid=" + uid.ToString(CultureInfo.InvariantCulture) + " 不在本局名册内（无名册不授权）";
                return RejectProjectileRequest(error);
            }

            // 已认证绑定：本副本必须真的挂在投射物驱动接缝上。
            if (_projectileDriver == null || !_projectileDriver.IsPlayerBound(player))
            {
                error = "uid=" + uid.ToString(CultureInfo.InvariantCulture) + " 未绑定投射物驱动接缝";
                return RejectProjectileRequest(error);
            }

            PMTransportConnection ownerConnection = player.OwnerConnection as PMTransportConnection;
            if (ownerConnection == null || !ownerConnection.IsReady)
            {
                error = "uid=" + uid.ToString(CultureInfo.InvariantCulture) + " 的 owner 连接未就绪";
                return RejectProjectileRequest(error);
            }

            uint ownerNetId = player.NetId.Value;

            // activation：单 owner 单调且**不重用**（重放/回退一律拒）。
            // 拒绝是终态，不会把已 Confirmed 的 legacy id 覆成 Rejected（协调器 AlreadyTerminal 保护）。
            uint lastActivation;
            if (_projectileLastActivationByOwner.TryGetValue(ownerNetId, out lastActivation)
                && intent.ActivationId <= lastActivation)
            {
                error = "activationId=" + intent.ActivationId.ToString(CultureInfo.InvariantCulture)
                        + " 非单调（本 owner 已授权到 " + lastActivation.ToString(CultureInfo.InvariantCulture) + "）";
                return RejectProjectileRequest(error);
            }

            // 200ms 墙钟间隔（时钟 = 本帧投射物 Pump 的 wallNowMs，与驱动同一 monotonic 口径）。
            double lastFireWall;
            if (_projectileLastFireWallMsByOwner.TryGetValue(ownerNetId, out lastFireWall))
            {
                if (_projectileWallNowMs < lastFireWall)
                {
                    error = "墙钟倒退（now=" + _projectileWallNowMs.ToString("0.###", CultureInfo.InvariantCulture)
                            + " last=" + lastFireWall.ToString("0.###", CultureInfo.InvariantCulture) + "）";
                    return RejectProjectileRequest(error);
                }

                if (_projectileWallNowMs - lastFireWall < (double)PMProjectileDiagnosticConfig.FireIntervalMs)
                {
                    error = "开火间隔不足 " + PMProjectileDiagnosticConfig.FireIntervalMs.ToString(CultureInfo.InvariantCulture)
                            + "ms（实际 " + (_projectileWallNowMs - lastFireWall).ToString("0.###", CultureInfo.InvariantCulture) + "ms）";
                    return RejectProjectileRequest(error);
                }
            }

            // 已存在运动 driver 且未 Freeze/未释放：没有它就没有权威位置，也就没有权威枪口。
            PMR4MovementDriver driver;
            if (!_driversByUid.TryGetValue(uid, out driver) || driver == null || driver.IsDisposed)
            {
                error = "uid=" + uid.ToString(CultureInfo.InvariantCulture) + " 缺少运动 driver";
                return RejectProjectileRequest(error);
            }

            if (driver.IsFrozen)
            {
                error = "uid=" + uid.ToString(CultureInfo.InvariantCulture) + " 的运动 driver 已 Freeze（拒绝授权）";
                return RejectProjectileRequest(error);
            }

            PMMoverSyncState sync = driver.GetAuthoritativeSync();
            if (!sync.Position.IsFinite
                || float.IsNaN(sync.YawDegrees) || float.IsInfinity(sync.YawDegrees))
            {
                error = "权威位置/朝向非有限";
                return RejectProjectileRequest(error);
            }

            // 共享只读诊断 spec 工厂：**每次都给新实例**，且上行 payload 根本表达不了 spec。
            PMProjectileSpec spec = PMProjectileDiagnosticConfig.CreateSpec();
            if (spec == null)
            {
                error = "诊断 spec 工厂返回 null";
                return RejectProjectileRequest(error);
            }

            // DS 权威枪口 = 权威位置 + 权威朝向 * 共享枪口偏移（客户端自报的位置/朝向只用于入队断言）。
            float yawRadians = sync.YawDegrees * ((float)Math.PI / 180f);
            PMVector3 forward = new PMVector3((float)Math.Sin(yawRadians), 0f, (float)Math.Cos(yawRadians));
            PMVector3 muzzle = sync.Position + forward * PMProjectileDiagnosticConfig.MuzzleOffsetM;
            if (!muzzle.IsFinite)
            {
                error = "DS 枪口位置非有限";
                return RejectProjectileRequest(error);
            }

            // 记账（单调 + 间隔）后放行：拒绝路径**不**写账，因此拒绝不会占用配额。
            _projectileLastActivationByOwner[ownerNetId] = intent.ActivationId;
            _projectileLastFireWallMsByOwner[ownerNetId] = _projectileWallNowMs;

            trustedSpec = spec;
            ownerPosition = muzzle;
            // 本批不接资源/HP 门，因此立即确认（可靠激活路径完整，但 Pending/显式 ResolveActivation 未被本宿主使用）。
            verdict = PMActivationResult.Confirmed;
            return true;
        }

        /// <summary>记一次策略拒绝并返回 false（调用点写作 <c>return RejectProjectileRequest(error);</c>）。</summary>
        private bool RejectProjectileRequest(string error)
        {
            _diagnosticProjectilePolicyRejections++;
            if (_diagnosticProjectilePolicyRejections <= MaxProjectileDiagnosticWarnings)
            {
                Warn("R5-C 拒绝授权（第 " + _diagnosticProjectilePolicyRejections.ToString(CultureInfo.InvariantCulture)
                     + " 次）：" + error);
            }

            return false;
        }

        /// <summary>
        /// 权威命中过滤：**self / 同队**必须拒（名册身份解析不出来也拒，fail closed）。
        /// 身份只从 DS 名册与已创建副本的 NetId 反查，绝不用上行自报字段。
        /// </summary>
        private bool AcceptDiagnosticHit(PMProjectileKey projectile, PMProjectileTargetSample target,
                                         PMVector3 sanitizedImpact)
        {
            int ownerUid;
            int targetUid;
            int ownerTeam;
            int targetTeam;

            if (!TryResolveUidByNetId(projectile.OwnerNetId, out ownerUid)
                || !TryResolveUidByNetId(target.NetId, out targetUid)
                || !TryResolveAuthoritativeTeam(ownerUid, out ownerTeam)
                || !TryResolveAuthoritativeTeam(targetUid, out targetTeam))
            {
                return RejectProjectileHit("身份不可解析（owner="
                    + projectile.OwnerNetId.ToString(CultureInfo.InvariantCulture) + " target="
                    + target.NetId.ToString(CultureInfo.InvariantCulture) + "）");
            }

            if (ownerUid == targetUid)
            {
                return RejectProjectileHit("self 命中（uid=" + ownerUid.ToString(CultureInfo.InvariantCulture) + "）");
            }

            // F2：断线/已 Freeze 的副本不得再被命中。历史里的 Alive 只是采样那一刻的事实，
            // 断线后旧帧锚仍可能解析到活样本，因此这里拿**实时**连接事实兜底（fail closed）。
            if (!IsProjectileTargetAuthoritative(ownerUid))
            {
                return RejectProjectileHit("owner 已断线/无可信运动 driver（uid="
                    + ownerUid.ToString(CultureInfo.InvariantCulture) + "）");
            }

            if (!IsProjectileTargetAuthoritative(targetUid))
            {
                return RejectProjectileHit("target 已断线/无可信运动 driver（uid="
                    + targetUid.ToString(CultureInfo.InvariantCulture) + "）");
            }

            if (ownerTeam == targetTeam)
            {
                return RejectProjectileHit("同队命中（team=" + ownerTeam.ToString(CultureInfo.InvariantCulture) + "）");
            }

            return true;
        }

        private bool RejectProjectileHit(string reason)
        {
            _diagnosticProjectileFilterRejections++;
            if (_diagnosticProjectileFilterRejections <= MaxProjectileDiagnosticWarnings)
            {
                Warn("R5-C 拒绝命中（第 " + _diagnosticProjectileFilterRejections.ToString(CultureInfo.InvariantCulture)
                     + " 次）：" + reason);
            }

            return false;
        }

        /// <summary>副本 NetId → uid（只在本局**已创建**的权威副本里反查）。</summary>
        private bool TryResolveUidByNetId(uint netId, out int uid)
        {
            uid = 0;
            if (netId == 0u) { return false; }

            foreach (KeyValuePair<int, PMR3Player> kv in _playersByUid)
            {
                if (kv.Value != null && kv.Value.NetId.IsValid && kv.Value.NetId.Value == netId)
                {
                    uid = kv.Key;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 权威队伍身份（**只用于 self/同队过滤**，不参与任何伤害/判定）。
        ///
        ///   · 正式内容：用已校验的名册队伍索引（<see cref="_formalTeamIndexByUid"/>，TeamId 2 → 0 / 1 → 1）；
        ///   · 诊断场景：名册 TeamId 为 1/2 时直接用；否则退到**与出生侧同一条规则**（名册行奇偶）——
        ///     这条规则本来就是本宿主给诊断模式定侧的依据（见 <see cref="BuildSpawnSync"/>），
        ///     因此不是「凭空分队」，而是复用同一份名册事实。
        /// 任一侧解析不出来 ⇒ 返回 false（调用方拒绝命中）。
        /// </summary>
        private bool TryResolveAuthoritativeTeam(int uid, out int team)
        {
            team = 0;

            if (_contentFormal)
            {
                return _formalTeamIndexByUid.TryGetValue(uid, out team);
            }

            int rosterTeam;
            if (!_rosterTeamByUid.TryGetValue(uid, out rosterTeam)) { return false; }

            if (rosterTeam == 1 || rosterTeam == 2)
            {
                team = rosterTeam;
                return true;
            }

            int row;
            if (!_rosterRowByUid.TryGetValue(uid, out row)) { return false; }

            team = (row % 2 == 0) ? 1 : 2;   // 与 BuildSpawnSync 的出生侧规则逐字一致
            return true;
        }

        /// <summary>私有可信授权策略：把宿主的判定接到 driver 的注入面（driver 只见接口，反向依赖为零）。</summary>
        private sealed class DiagnosticProjectileAuthorityPolicy : IPMR5ProjectileAuthorityPolicy
        {
            private readonly PMDsSessionHost _host;

            public DiagnosticProjectileAuthorityPolicy(PMDsSessionHost host)
            {
                _host = host;
            }

            public bool TryAuthorizeSpawn(PMR3Player player, PMProjectileSpawnIntent intent,
                out PMProjectileSpec trustedSpec, out PMVector3 ownerPosition,
                out PMActivationResult verdict, out string error)
            {
                return _host.TryAuthorizeDiagnosticSpawn(player, intent, out trustedSpec, out ownerPosition,
                                                        out verdict, out error);
            }
        }

        /// <summary>私有权威命中过滤：按 DS 名册身份做 self/同队过滤（缺数据 fail closed）。</summary>
        private sealed class RosterIdentityHitFilter : IPMProjectileHitFilter
        {
            private readonly PMDsSessionHost _host;

            public RosterIdentityHitFilter(PMDsSessionHost host)
            {
                _host = host;
            }

            public bool Accept(PMProjectileKey projectile, PMProjectileTargetSample target,
                               PMVector3 sanitizedImpact)
            {
                return _host.AcceptDiagnosticHit(projectile, target, sanitizedImpact);
            }
        }

        /// <summary>投射物接线的单行诊断摘要（进 <see cref="Describe"/>）。</summary>
        private string DescribeProjectileWiring()
        {
            if (_projectileDriver == null) { return "<none>"; }

            return "faulted=" + (_projectileDriver.IsFaulted ? _projectileDriver.FaultReason.ToString() : "<none>")
                   + " bound=" + _projectileDriver.BoundPlayerCount.ToString(CultureInfo.InvariantCulture)
                   + " authority=" + _projectileDriver.AuthorityObjectCount.ToString(CultureInfo.InvariantCulture)
                   + " reserved=" + _projectileDriver.ReservationCount.ToString(CultureInfo.InvariantCulture)
                   + " views=" + _projectileDriver.ViewCount.ToString(CultureInfo.InvariantCulture)
                   + " historyTargets=" + (_projectileHistory != null ? _projectileHistory.TargetCount : 0).ToString(CultureInfo.InvariantCulture)
                   + " historyRejects=" + _diagnosticProjectileHistoryRejects.ToString(CultureInfo.InvariantCulture)
                   + " policyRejects=" + _diagnosticProjectilePolicyRejections.ToString(CultureInfo.InvariantCulture)
                   + " filterRejects=" + _diagnosticProjectileFilterRejections.ToString(CultureInfo.InvariantCulture)
                   + " settlements=" + _diagnosticProjectileSettlementCount.ToString(CultureInfo.InvariantCulture)
                   + " hits=" + _diagnosticProjectileHitCount.ToString(CultureInfo.InvariantCulture)
                   + " droppedMs=" + _diagnosticProjectileDroppedMs.ToString("0.###", CultureInfo.InvariantCulture)
                   + " viewDrains=" + _diagnosticProjectileViewDrains.ToString(CultureInfo.InvariantCulture)
                   + " disconnectSamples=" + _diagnosticProjectileDisconnectSamples.ToString(CultureInfo.InvariantCulture)
                   + " settlementLogFailures=" + _diagnosticProjectileSettlementLogFailures.ToString(CultureInfo.InvariantCulture)
                   + " unconsumedSettlements=" + _diagnosticProjectileUnconsumedSettlements.ToString(CultureInfo.InvariantCulture);
        }

        private static string DescribeProjectileKey(PMProjectileKey key)
        {
            return "e" + key.Epoch.ToString(CultureInfo.InvariantCulture)
                   + "/o" + key.OwnerNetId.ToString(CultureInfo.InvariantCulture)
                   + "/p" + key.ProjectileId.ToString(CultureInfo.InvariantCulture)
                   + "/" + key.Origin;
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
                 + " | combat=" + DescribeCombatWiring()
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
                   + " projectile=" + DescribeProjectileWiring()
                   + " combat=" + DescribeCombatWiring()
                   + " fault=" + (_faulted ? _faultReason : "<none>");
        }
    }
}
