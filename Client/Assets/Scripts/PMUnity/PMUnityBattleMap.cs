// ============================================================================
//  PMUnityBattleMap —— R4-C / C2 正式地图的运行时加载与出生位解析
// ============================================================================
//
//  契约来源：Docs/plans/net-r4c-content-contract.md 的「C2 运行内容适配」一节。逐条落点：
//    · 「静态 TryLoad(out map,out error) 读取 manifest + map prefab 到专属
//       LocalPhysicsMode.Physics3D Scene」            → TryLoad / TryLoadManifest
//    · 「保证非默认 physics、先临时 activeScene 实例化且 finally 还原」 → 与 R3-B 的
//       PMR3TestScene.BuildIsolated 同一套做法（先校验非默认物理世界，再临时切换活动场景）
//    · 「加载纯地图 Collider 清单有界 4096，所有 Collider enabled/active，
//       排除 trigger 不作硬障碍，验证无任何 MonoBehaviour/动态刚体/旧驱动/相机」
//                                                     → CollectAndValidate
//    · 「query 使用现有 PMUnityMoverCollisionQuery 全参绑定，不新增第二物理框架」
//                                                     → 全参构造 (scene, layerMask, worldVersion, allowlist)
//    · 「提供 Manifest / Scene / PhysicsScene / Colliders / Query / TryGetSpawn / GetSceneDigest」
//    · 「TryGetSpawn(teamIndex,slot,out PMMoverSyncState,out error)：x=±15, z=-5/0/5，
//       向下合理有界查询找真实足底 + 半高并 Overlap 拒绝障碍」 → TryGetSpawn
//
//  三条硬约束（本文件的设计落点）：
//    1) **拒绝默认物理世界**：GetPhysicsScene() 一旦等于 Physics.defaultPhysicsScene 就失败。
//       理由与 R3-B 一致——大厅/旧场景同层 Collider 会参与命中甚至顶满查询缓冲，
//       "以为隔离了、其实建在默认世界"是最贵的错误。绝不退回默认世界假隔离。
//    2) **fail closed，不替换假地面**：manifest 缺失/不合法、Resources 缺 prefab、
//       出生位找不到真实地面或与障碍重叠，都返回失败 + 可归因原因；**不**回退临时地图、
//       **不**静默把 y 设成一个"看起来合理"的常量。
//    3) **纯读的世界**：本文件只做查询（Ground/Capsule 重叠），不移动 Transform、
//       不 Physics.Simulate、不改全局物理开关。唯一的一次写是加载末尾的
//       Physics.SyncTransforms()（适配器要求的宿主前置条件：查询前把 Transform 提交给 PhysX）。
//
//  ---------------------------------------------------------------------------
//  诚实边界（供 C3/后续批次处理，不许被读成"任意网格精确 MTD"）
//  ---------------------------------------------------------------------------
//    · 移动碰撞仍由 PMUnityMoverCollisionQuery 承担，它对**非 BoxCollider**（旋转 Box、
//      MeshCollider 等）的几何分类用的是 Collider.bounds（世界 AABB）超集近似：
//      保守早挡、绝不漏墙，但不承诺最小推出量/精确 MTD。正式地图模板里
//      MeshCollider（多为 convex=0）与旋转 Box 都存在，因此这条限制**会**在正式地图上生效。
//      本类把白名单里的非 Box Collider 数量（NonBoxColliderCount）单独统计出来，
//      C3/后续批次可直接拿它 + Query.GetStats().NonBoxBoundsClassifications 对账。
//    · GetSceneDigest() 是**运行时结构摘要**（类型 + 世界 AABB + layer + 版本），
//      **不是** C1 的编辑器内容摘要，二者不可比较：模板源指纹/mesh 引用/源 Player 指纹
//      在运行期不可得。它只用于"同一构建的两端加载到同一份几何"这一口径，
//      编码遵循 manifest 的数据协议（uint、非 0）
//    · 本类不声称资源防篡改，也不做反作弊。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using PMNet.Mover;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PMNet.Unity
{
    /// <summary>
    /// 一份加载好的正式地图（隔离物理世界 + Collider 白名单 + 运动碰撞查询 + 出生位解析）。
    ///
    /// 生命周期：<see cref="TryLoad"/> → 使用 → <see cref="Dispose"/>（幂等）。
    /// 线程：与 Unity 一致——构造/查询/释放都必须在主线程（底层适配器自己也会校验构造线程）。
    /// </summary>
    public sealed class PMUnityBattleMap : IDisposable
    {
        // ---------------------------------------------------------------- 冻结/有界常量

        /// <summary>硬障碍 Collider 数量上限（契约：加载清单有界 4096，超出显式失败）。</summary>
        /// <remarks>
        /// 注意（复核登记的真实风险）：本上限约束的是**本类的白名单长度**，而底层适配器
        /// `PMUnityMoverCollisionQuery` 的**每次查询命中缓冲**上限是 32（`HitBufferCapacity`），
        /// 且饱和时按契约**显式抛异常**（绝不静默漏墙）。若正式地图把大量 Collider 堆在同一个
        /// 查询体附近（同一 layer 上），查询会以 `InvalidOperationException` 失败 —— 表现是
        /// "出生位解析失败 / 驱动报错"，而不是静默错值。适配器不在本文件的写入边界内（不得改），
        /// 因此这里只登记风险：C4 应在真机读取 `Query.GetStats().SaturationFailures` 与
        /// <see cref="NonBoxColliderCount"/> 量化后再决定是否需要收紧 layer 或分批查询。
        /// </remarks>
        public const int MaxColliderCount = 4096;

        /// <summary>队伍数（0/1 两个出生侧）。</summary>
        public const int TeamCount = 2;

        /// <summary>每队出生槽位数（z = -5/0/5）。</summary>
        public const int SpawnSlotCount = 3;

        /// <summary>teamIndex=0 的出生 X（正 X 侧）。</summary>
        public const float SpawnXPositiveSide = 15f;

        /// <summary>teamIndex=1 的出生 X（负 X 侧）。</summary>
        public const float SpawnXNegativeSide = -15f;

        /// <summary>出生位探测的起始中心 Y（米）。必须高于地图内一切几何。</summary>
        public const float SpawnProbeStartY = 25f;

        /// <summary>出生位向下探测的最大距离（米，有界）。</summary>
        public const float SpawnGroundProbeMeters = 40f;

        /// <summary>支撑面法线与 +Y 的最小点积（低于它说明找到的是斜面/墙顶，不作为出生地面）。</summary>
        public const float SpawnSupportUpMinDot = 0.99f;

        /// <summary>出生中心 Y 的合理下界（米）。越界说明探测到了地图外的几何。</summary>
        public const float SpawnCenterYMin = -10f;

        /// <summary>出生中心 Y 的合理上界（米）。</summary>
        public const float SpawnCenterYMax = 20f;

        /// <summary>
        /// 判定"某个 Collider 属于脚下支撑面（而不是挡在身上的障碍）"的容差（米）：
        /// bounds 顶面不高于支撑面顶 + 该容差的 Collider 一律不算障碍。
        /// 取 1e-3 与适配器的 CollisionSkinMeters 同量级：既排除地面本身
        /// （PhysX 的 overlap 语义包含"接触"，contactOffset 默认 0.01），
        /// 又不会把真正插进胶囊的墙漏判（墙顶远高于此）。
        /// </summary>
        /// <remarks>
        /// 为什么这条容差**足够**（复核推导，供 C4 在真机复核）：
        /// <see cref="TryGetSpawn"/> 用 `supportTopY = SpawnProbeStartY - halfHeight - ground.Distance`，
        /// 而 `ground.Distance` 来自 PhysX 向下的 CapsuleCast（自胶囊**足底**起算）。若地面 AABB 顶面
        /// 就是支撑面，相减恰好回到地面顶面 Y；PhysX 的 TOI 扫掠是**保守偏早**的（非精确扫掠最多早
        /// 约一个 contactOffset ≈ 0.01 m），于是 `supportTopY` 只会 **≥** 地面顶面
        /// ⇒ `bounds.max.y <= supportTopY + 1e-3` 必然成立 ⇒ **地面不会被误判成障碍**。
        /// 反向偏差（扫掠偏晚、supportTopY 低于地面顶）在 PhysX 语义下不会出现。
        /// 因此"脚下地面把 6 个出生位全判成嵌墙"这条风险在**几何与查询语义**层面已排除；
        /// 真实数值仍需 C4 在真机 PhysX 上量一次（本仓库的命令行门禁不跑物理）。
        /// </remarks>
        public const float SpawnSupportToleranceMeters = 1e-3f;

        /// <summary>出生位重叠查询的固定缓冲容量（满即显式失败，与适配器同一纪律）。</summary>
        public const int SpawnOverlapCapacity = 64;

        /// <summary>首版出生朝向（度）。</summary>
        /// <remarks>
        /// 固定 0° 的理由：现有 DS 的 BuildSpawnSync 就是 0°，而 PlayerVisualV1 的模型前向轴
        /// 尚未实机确认（C1 只做"干净化"，不保证朝向）。本类**不猜**模型前向；
        /// 若 C4 实机发现需要朝向对方一侧，只改这一个常量。
        /// </remarks>
        public const float SpawnYawDegrees = 0f;

        /// <summary>结构摘要的几何量化（每米 1e4 格）：把浮点噪声挡在摘要之外。</summary>
        public const float DigestQuantizationPerMeter = 10000f;

        /// <summary>每队出生槽位的 Z 列表（契约冻结：-5 / 0 / 5）。</summary>
        private static readonly float[] SpawnZBySlot = { -5f, 0f, 5f };

        // ---------------------------------------------------------------- 状态

        private readonly PMBattleContentManifest _manifest;
        private readonly GameObject _root;
        private readonly Scene _scene;
        private readonly PhysicsScene _physicsScene;
        private readonly Collider[] _colliders;
        private readonly PMUnityMoverCollisionQuery _query;
        private readonly int _triggerColliderCount;
        private readonly int _kinematicRigidbodyCount;
        private readonly int _boxColliderCount;
        private readonly int _nonBoxColliderCount;
        private readonly uint _sceneDigest;
        private readonly Collider[] _spawnOverlapBuffer = new Collider[SpawnOverlapCapacity];

        private int _spawnQueries;
        private bool _disposed;

        /// <summary>隔离场景名的自增序号（同一进程内 CreateScene 禁止重名）。</summary>
        private static int _isolatedSceneSequence;

        private PMUnityBattleMap(PMBattleContentManifest manifest, GameObject root, Scene scene,
                                 PhysicsScene physicsScene, Collider[] colliders,
                                 int triggerColliderCount, int kinematicRigidbodyCount,
                                 int boxColliderCount, int nonBoxColliderCount, uint sceneDigest,
                                 int layerMask)
        {
            _manifest = manifest;
            _root = root;
            _scene = scene;
            _physicsScene = physicsScene;
            _colliders = colliders;
            _triggerColliderCount = triggerColliderCount;
            _kinematicRigidbodyCount = kinematicRigidbodyCount;
            _boxColliderCount = boxColliderCount;
            _nonBoxColliderCount = nonBoxColliderCount;
            _sceneDigest = sceneDigest;

            // worldVersion 取 manifest 的派生值：两端加载同一构建 ⇒ 同一值，快照的
            // Aux.CollisionWorldVersion 才可能一致（契约 §B1）。C3 必须把宿主里
            // 那个写死的 MovementWorldVersion 常量换成这里查询到的值（见报告）。
            _query = new PMUnityMoverCollisionQuery(physicsScene, layerMask, manifest.worldVersion, colliders);
        }

        // ---------------------------------------------------------------- 属性

        /// <summary>本图的 manifest（已通过强校验；Dispose 后仍然可读——它是纯数据，供宿主记账）。</summary>
        public PMBattleContentManifest Manifest { get { return _manifest; } }

        /// <summary>隔离场景；已释放时为 default(Scene)（IsValid()==false）。</summary>
        public Scene Scene { get { return _disposed ? default(Scene) : _scene; } }

        /// <summary>隔离物理世界；已释放时为 default(PhysicsScene)（IsValid()==false）。</summary>
        public PhysicsScene PhysicsScene { get { return _disposed ? default(PhysicsScene) : _physicsScene; } }

        /// <summary>
        /// 硬障碍 Collider 白名单（**不含** trigger；顺序与 prefab 层级一致）。
        /// 已释放时为零长度数组。宿主不要修改返回的数组内容。
        /// </summary>
        public Collider[] Colliders { get { return _disposed ? EmptyColliders : _colliders; } }

        /// <summary>运动碰撞查询；已释放时为 null。</summary>
        public PMUnityMoverCollisionQuery Query { get { return _disposed ? null : _query; } }

        /// <summary>地图实例根节点；已释放时为 null。</summary>
        public GameObject Root { get { return _disposed ? null : _root; } }

        /// <summary>被排除出硬障碍的 trigger Collider 数量（契约要求"排除 trigger 不作硬障碍"）。</summary>
        public int TriggerColliderCount { get { return _triggerColliderCount; } }

        /// <summary>
        /// 白名单里 kinematic Rigidbody 的数量。
        /// 动态（非 kinematic）刚体是**致命错误**（见 CollectAndValidate），
        /// kinematic 刚体不影响 PhysX 查询语义，但会被计数并在报告里体现。
        /// </summary>
        public int KinematicRigidbodyCount { get { return _kinematicRigidbodyCount; } }

        /// <summary>白名单里 BoxCollider 数量（这些形状上适配器的几何分类是精确的）。</summary>
        public int BoxColliderCount { get { return _boxColliderCount; } }

        /// <summary>
        /// 白名单里**非** BoxCollider 数量（MeshCollider / 旋转 Box / Sphere…）：
        /// 适配器在这些形状上用 bounds 超集近似（保守早挡、绝不漏墙，但非精确 MTD）。
        /// 该计数是"限制会在正式地图上生效"的量化证据。
        /// </summary>
        public int NonBoxColliderCount { get { return _nonBoxColliderCount; } }

        /// <summary>TryGetSpawn 调用次数（诊断）。</summary>
        public int SpawnQueryCount { get { return _spawnQueries; } }

        /// <summary>是否已释放。</summary>
        public bool Disposed { get { return _disposed; } }

        private static readonly Collider[] EmptyColliders = new Collider[0];

        // ---------------------------------------------------------------- 加载

        /// <summary>
        /// 只加载 + 校验 manifest（不做任何 prefab/场景动作）。供只需要 manifest
        /// （例如构造角色表现）的调用方使用，也便于把"资源没生成"和"资源坏了"分开报。
        /// </summary>
        public static bool TryLoadManifest(out PMBattleContentManifest manifest, out string error)
        {
            manifest = null;
            error = null;

            TextAsset asset;
            try
            {
                asset = Resources.Load<TextAsset>(PMBattleContentManifest.ManifestResourceKey);
            }
            catch (Exception ex)
            {
                error = "读取 manifest 资源失败（键 \"" + PMBattleContentManifest.ManifestResourceKey + "\"）："
                        + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            if (asset == null)
            {
                error = "Resources 缺少 manifest（键 \"" + PMBattleContentManifest.ManifestResourceKey
                        + "\"）：正式内容尚未生成。先执行 Editor 菜单 Build/Prepare PMNet Battle Content（C1）；"
                        + "本版**不**回退临时地图或旧战斗。";
                return false;
            }

            return PMBattleContentManifest.TryParseJson(asset.text, out manifest, out error);
        }

        /// <summary>
        /// 加载正式地图：manifest → map prefab → 专属 LocalPhysicsMode.Physics3D 场景 →
        /// Collider 白名单 → 运动碰撞查询。任何一步失败都释放已建资源并返回 false
        /// （绝不留下半个场景或"有效但内容不对"的 map）。
        /// </summary>
        public static bool TryLoad(out PMUnityBattleMap map, out string error)
        {
            map = null;

            PMBattleContentManifest manifest;
            if (!TryLoadManifest(out manifest, out error))
            {
                return false;
            }

            GameObject prefab;
            try
            {
                prefab = Resources.Load<GameObject>(manifest.mapResource);
            }
            catch (Exception ex)
            {
                error = "读取地图 prefab 失败（键 \"" + manifest.mapResource + "\"）："
                        + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            if (prefab == null)
            {
                error = "Resources 缺少地图 prefab（键 \"" + manifest.mapResource
                        + "\"）：拒绝回退临时地图；先执行 C1 的 Editor 菜单烘焙。";
                return false;
            }

            int sequence = System.Threading.Interlocked.Increment(ref _isolatedSceneSequence);
            string sceneName = "[PMNetBattleMap]Physics" + sequence.ToString(CultureInfo.InvariantCulture);

            Scene scene;
            try
            {
                scene = SceneManager.CreateScene(sceneName, new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            }
            catch (Exception ex)
            {
                error = "创建本地物理场景失败：" + ex.GetType().Name + " " + ex.Message
                        + "（SceneManager.CreateScene 是**运行时** API；编辑模式请在 Play 模式下运行）";
                return false;
            }

            if (!scene.IsValid() || !scene.isLoaded)
            {
                error = "本地物理场景无效（IsValid=" + scene.IsValid() + " isLoaded=" + scene.isLoaded + "）";
                return false;
            }

            PhysicsScene physicsScene;
            try
            {
                physicsScene = scene.GetPhysicsScene();
            }
            catch (Exception ex)
            {
                ReleaseScene(scene);
                error = "取 PhysicsScene 失败：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            if (!physicsScene.IsValid())
            {
                ReleaseScene(scene);
                error = "本地物理场景没有可用的 PhysicsScene（IsValid=false）";
                return false;
            }

            if (physicsScene.Equals(Physics.defaultPhysicsScene))
            {
                ReleaseScene(scene);
                error = "本地物理场景回退到了**默认物理世界**（GetPhysicsScene() == Physics.defaultPhysicsScene）："
                        + "本平台/模式下不支持隔离物理世界，拒绝把默认世界当隔离世界"
                        + "（编辑模式请在 Play 模式下运行）。";
                return false;
            }

            // 实例化：对象落在**活动场景**，所以临时把活动场景切到隔离场景，
            // 并在 finally 里还原（契约：活动场景语义属于宿主/用户，不能被抢占）。
            Scene previousActive = SceneManager.GetActiveScene();
            bool switched = false;
            GameObject instance = null;

            try
            {
                switched = SceneManager.SetActiveScene(scene);
                instance = UnityEngine.Object.Instantiate(prefab);
                instance.name = "[PMNetBattleMap]" + prefab.name;
            }
            catch (Exception ex)
            {
                error = "实例化地图 prefab 失败：" + ex.GetType().Name + " " + ex.Message;
                instance = null;
            }
            finally
            {
                if (switched)
                {
                    SceneManager.SetActiveScene(previousActive);
                }
            }

            if (instance == null)
            {
                ReleaseScene(scene);
                return false;
            }

            // 活动场景切换可能不生效：显式搬迁 + 校验对象确实在隔离物理世界里。
            if (!instance.scene.IsValid() || instance.scene.handle != scene.handle)
            {
                SceneManager.MoveGameObjectToScene(instance, scene);
            }

            if (!instance.scene.IsValid() || instance.scene.handle != scene.handle)
            {
                DestroyObject(instance);
                ReleaseScene(scene);
                error = "地图实例没有进入隔离物理场景（scene mismatch）：拒绝在默认世界里跑正式地图。";
                return false;
            }

            Collider[] obstacles;
            int triggerCount;
            int kinematicRigidbodies;
            int boxCount;
            int nonBoxCount;

            if (!CollectAndValidate(instance, out obstacles, out triggerCount, out kinematicRigidbodies,
                                    out boxCount, out nonBoxCount, out error))
            {
                DestroyObject(instance);
                ReleaseScene(scene);
                return false;
            }

            int layerMask = 0;
            for (int i = 0; i < obstacles.Length; i++)
            {
                layerMask |= 1 << obstacles[i].gameObject.layer;
            }

            if (layerMask == 0)
            {
                DestroyObject(instance);
                ReleaseScene(scene);
                error = "图层的掩码为 0（没有任何可查询的 Collider 层）：拒绝用空查询跑正式地图。";
                return false;
            }

            // 适配器的宿主前置条件：查询前把 Transform 改动提交给 PhysX（只做一次，不每帧扫描同步）。
            //
            // 诚实边界（2019.4 真实 API 面）：本版本**没有** `PhysicsScene.SyncTransforms`
            // （已核 UnityEngine.PhysicsModule.dll 的公开成员），`Physics.SyncTransforms()` 的口径是
            // "默认物理场景"，**不保证**覆盖这里用 LocalPhysicsMode.Physics3D 建出来的隔离场景。
            // 本类因此**不依赖**它：实例化之后本类不再写任何 Transform，也没有 Rigidbody 会自己动，
            // 静态 Collider 在创建时就把当时的 transform 烘进本地 PxScene，查询所见即 prefab 几何。
            // 保留这一行是为了与"宿主每帧同步"的既有约定一致，并覆盖将来有人写 Transform 的情形。
            Physics.SyncTransforms();

            uint sceneDigest = ComputeSceneDigest(manifest, obstacles, layerMask, triggerCount, nonBoxCount);

            PMUnityBattleMap created = new PMUnityBattleMap(manifest, instance, scene, physicsScene, obstacles,
                                                            triggerCount, kinematicRigidbodies,
                                                            boxCount, nonBoxCount, sceneDigest, layerMask);

            if (!created.ValidateManifestConsistency(out error))
            {
                created.Dispose();
                return false;
            }

            map = created;
            return true;
        }

        // ---------------------------------------------------------------- 几何/组件校验

        /// <summary>
        /// 遍历地图实例（含未激活节点），建立硬障碍白名单并做"纯几何"校验。
        ///
        /// 判定规则（契约逐条）：
        ///   · **允许**：Transform / MeshFilter / MeshRenderer / Collider / LODGroup /
        ///     kinematic Rigidbody（计数，不影响查询语义）；
        ///   · **致命**：missing script（GetComponents 的 null 项）、任何 MonoBehaviour（旧驱动/玩法脚本）、
        ///     Camera、Animator、AudioListener、**动态（非 kinematic）Rigidbody**、以及任何其它组件
        ///     （报告类型全名，便于 C1 精确删掉它）；
        ///   · Collider 必须 enabled 且所在 GameObject activeInHierarchy：世界几何必须整体可用，
        ///     "藏着一个没启用的 Collider"在运行期等于几何缺失，必须显式失败；
        ///   · trigger Collider **排除**出硬障碍（计数保留），不作硬障碍；
        ///   · **致命（可用性，复核新增）**：MeshFilter 的 `sharedMesh` 为空 —— 白名单只保证
        ///     **类型**合法，"引用没落地"必须在这里显式失败，否则会静默产出一张看不见的地图
        ///     （对应 C1 侧"SerializedObject 字段没拷贝"，实测发生过）。
        ///     材质可用性由 C1 的烘焙回读自检把关（C2 侧不引用 `Material`/`Renderer.sharedMaterials`，
        ///     因为那会给 Tools/PMUnityGlueCheck 的桩件增加另一组必须同步维护的成员）；
        ///   · 白名单长度有界（<see cref="MaxColliderCount"/>），超出显式失败。
        /// </summary>
        private static bool CollectAndValidate(GameObject instance, out Collider[] obstacles,
                                              out int triggerCount, out int kinematicRigidbodies,
                                              out int boxCount, out int nonBoxCount, out string error)
        {
            obstacles = null;
            triggerCount = 0;
            kinematicRigidbodies = 0;
            boxCount = 0;
            nonBoxCount = 0;
            error = null;

            if (instance == null)
            {
                error = "地图实例为 null";
                return false;
            }

            if (!instance.activeInHierarchy)
            {
                error = "地图实例根节点处于未激活状态（activeInHierarchy=false）：世界几何不可用。";
                return false;
            }

            Component[] components;
            try
            {
                components = instance.GetComponentsInChildren<Component>(true);
            }
            catch (Exception ex)
            {
                error = "遍历地图组件失败：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            List<Collider> list = new List<Collider>();
            int meshFiltersWithoutMesh = 0;
            string firstMeshFilterWithoutMesh = null;

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];

                if (component == null)
                {
                    error = "地图 prefab 含 **missing script**（组件数组中存在 null 项）：C1 必须把旧脚本彻底删除"
                            + "（不是禁用），本版本拒绝加载带缺失脚本的资源。";
                    return false;
                }

                if (component is Transform) { continue; }        // Transform / RectTransform

                if (component is MeshFilter)
                {
                    // "几何可用"：网格引用必须真的在。白名单只保证**类型**合法，
                    // 缺网格的 MeshFilter 在渲染上等于空物体 —— 这类"引用没拷过去"的损坏
                    // 必须在加载时就显式失败，而不是给玩家一张看不见的地图。
                    MeshFilter meshFilter = (MeshFilter)component;
                    if (meshFilter.sharedMesh == null)
                    {
                        meshFiltersWithoutMesh++;
                        if (firstMeshFilterWithoutMesh == null)
                        {
                            firstMeshFilterWithoutMesh = PathOf(meshFilter.gameObject);
                        }
                    }

                    continue;
                }

                if (component is MeshRenderer)
                {
                    // 这里**不**查材质：`Material` / `Renderer.sharedMaterials` 未在
                    // Tools/PMUnityGlueCheck 的桩件里（那是另一组的文件，本文件不改它，
                    // 也不为它增加必须同步维护的成员）。材质落地由 C1 的烘焙回读自检
                    // （VerifyMapPrefabLoaded 的"无材质渲染器"判定）在**产出侧**把关。
                    continue;
                }

                if (component is LODGroup) { continue; }

                Collider collider = component as Collider;
                if (collider != null)
                {
                    if (!collider.enabled)
                    {
                        error = "地图 Collider 未启用（enabled=false，对象=\"" + PathOf(collider.gameObject)
                                + "\"）：契约要求所有 Collider enabled/active。";
                        return false;
                    }

                    if (!collider.gameObject.activeInHierarchy)
                    {
                        error = "地图 Collider 所在 GameObject 未激活（对象=\"" + PathOf(collider.gameObject)
                                + "\"）：契约要求所有 Collider enabled/active。";
                        return false;
                    }

                    if (collider.isTrigger)
                    {
                        triggerCount++;
                        continue;                                 // 排除：trigger 不作硬障碍
                    }

                    if (list.Count >= MaxColliderCount)
                    {
                        error = "地图硬障碍 Collider 数量达到上限 " + MaxColliderCount.ToString(CultureInfo.InvariantCulture)
                                + "：拒绝静默截断（截断等于凭空去掉几何）。";
                        return false;
                    }

                    list.Add(collider);

                    if (collider is BoxCollider) { boxCount++; }
                    else { nonBoxCount++; }

                    continue;
                }

                Rigidbody rigidbody = component as Rigidbody;
                if (rigidbody != null)
                {
                    if (!rigidbody.isKinematic)
                    {
                        error = "地图 prefab 含**动态 Rigidbody**（对象=\"" + PathOf(rigidbody.gameObject)
                                + "\"）：纯几何地图只允许静态几何；C1 必须删除动态刚体。";
                        return false;
                    }

                    kinematicRigidbodies++;
                    continue;
                }

                // 以下都是致命项，按"最容易误留"的顺序报出更有用的原因。
                if (component is MonoBehaviour)
                {
                    error = "地图 prefab 含 MonoBehaviour（类型=" + component.GetType().FullName
                            + "，对象=\"" + PathOf(component.gameObject)
                            + "\"）：纯几何地图必须 0 旧脚本/0 旧驱动。";
                    return false;
                }

                if (component is Camera)
                {
                    error = "地图 prefab 含 Camera（对象=\"" + PathOf(component.gameObject)
                            + "\"）：相机属于宿主/表现，不得进地图资源。";
                    return false;
                }

                if (component is Animator)
                {
                    error = "地图 prefab 含 Animator（对象=\"" + PathOf(component.gameObject)
                            + "\"）：地图不得含动画驱动。";
                    return false;
                }

                if (component is AudioListener)
                {
                    error = "地图 prefab 含 AudioListener（对象=\"" + PathOf(component.gameObject)
                            + "\"）：地图不得含音频监听器。";
                    return false;
                }

                error = "地图 prefab 含未允许的组件类型 " + component.GetType().FullName
                        + "（对象=\"" + PathOf(component.gameObject)
                        + "\"）：允许集 = Transform/MeshFilter/MeshRenderer/Collider/LODGroup/kinematic Rigidbody；"
                        + "其余一律显式失败而不是静默残留。";
                return false;
            }

            if (meshFiltersWithoutMesh > 0)
            {
                error = "地图有 " + meshFiltersWithoutMesh.ToString(CultureInfo.InvariantCulture)
                        + " 个 MeshFilter 没有网格（首个对象=\""
                        + (firstMeshFilterWithoutMesh == null ? "<未知>" : firstMeshFilterWithoutMesh)
                        + "\"）：几何不可见，说明资源里的 mesh 引用没有落地（拒绝加载，不做无声降级）。";
                return false;
            }

            if (list.Count == 0)
            {
                error = "地图没有任何硬障碍 Collider（非 trigger）：正式地图必须有真实碰撞几何"
                        + "（地面 + 墙/障碍），拒绝加载空几何地图。";
                return false;
            }

            obstacles = list.ToArray();
            return true;
        }

        // ---------------------------------------------------------------- 出生位

        /// <summary>
        /// teamId（新链 <c>PMDsRosterIdentity.TeamId</c>）→ 本类 teamIndex 的**唯一**映射。
        ///
        /// 与现有 DS 侧约定一致（PMDsSessionHost.BuildSpawnSync：team 1 → 负 X，team 2 → 正 X）：
        ///   teamId 2 → teamIndex 0（x=+15）
        ///   teamId 1 → teamIndex 1（x=-15）
        /// 其它 teamId 一律失败（不做"行号奇偶"之类的兜底猜测——那正是旧链镜像逻辑的坑）。
        /// </summary>
        public static bool TryResolveSpawnTeamIndex(int teamId, out int teamIndex)
        {
            teamIndex = 0;

            if (teamId == 2) { teamIndex = 0; return true; }
            if (teamId == 1) { teamIndex = 1; return true; }
            return false;
        }

        /// <summary>teamIndex → 出生 X（0 = +15，1 = -15；非法索引返回 NaN）。</summary>
        public static float SpawnXForTeamIndex(int teamIndex)
        {
            if (teamIndex == 0) { return SpawnXPositiveSide; }
            if (teamIndex == 1) { return SpawnXNegativeSide; }
            return float.NaN;
        }

        /// <summary>slot → 出生 Z（-5/0/5；非法槽位返回 NaN）。</summary>
        public static float SpawnZForSlot(int slot)
        {
            if (slot < 0 || slot >= SpawnSlotCount) { return float.NaN; }
            return SpawnZBySlot[slot];
        }

        /// <summary>
        /// 解出一个出生位（世界坐标）：x = ±15、z = -5/0/5，**y 由真实地面查询 + 胶囊半高得到**。
        ///
        /// 步骤（每步失败都返回可归因原因，**不**静默换假地面）：
        ///   1) 从 <see cref="SpawnProbeStartY"/> 向下用 <see cref="IPMMoverCollisionQuery.QueryGround"/>
        ///      找真实支撑面（探测距离有界 <see cref="SpawnGroundProbeMeters"/>）；
        ///   2) 支撑面必须是朝上的平面（法线与 +Y 的点积 ≥ <see cref="SpawnSupportUpMinDot"/>）；
        ///   3) 中心 Y = 支撑面顶 + 半高，且必须落在 [<see cref="SpawnCenterYMin"/>,
        ///      <see cref="SpawnCenterYMax"/>] 内（越界说明找到了不该有的几何）；
        ///   4) 用真实 Unity 胶囊重叠查询（同一物理场景 / 同一 layerMask / 同一白名单）
        ///      确认该姿态**不与障碍重叠**；地面本身按"顶面不高于支撑面顶"排除。
        ///
        /// 团队/槽位编号：teamIndex ∈ {0,1}（0 = 正 X 侧，1 = 负 X 侧），slot ∈ {0,1,2}。
        /// **不做本地全局镜像**：世界坐标就是网络坐标，两端用同一个 teamIndex→x 映射
        /// （teamId→teamIndex 用 <see cref="TryResolveSpawnTeamIndex"/>）。
        /// </summary>
        public bool TryGetSpawn(int teamIndex, int slot, out PMMoverSyncState state, out string error)
        {
            state = PMMoverSyncState.CreateDefault();
            error = null;

            RequireAlive();

            if (teamIndex < 0 || teamIndex >= TeamCount)
            {
                error = "teamIndex=" + teamIndex.ToString(CultureInfo.InvariantCulture)
                        + " 越界（合法 0..1）";
                return false;
            }

            if (slot < 0 || slot >= SpawnSlotCount)
            {
                error = "slot=" + slot.ToString(CultureInfo.InvariantCulture)
                        + " 越界（合法 0..2）";
                return false;
            }

            _spawnQueries++;
            string site = "(teamIndex=" + teamIndex.ToString(CultureInfo.InvariantCulture)
                          + ", slot=" + slot.ToString(CultureInfo.InvariantCulture) + ")";

            float x = SpawnXForTeamIndex(teamIndex);
            float z = SpawnZBySlot[slot];

            float radius = PMMoverDefaults.CapsuleRadiusMeters * PMMoverDefaults.DefaultScale;
            float halfHeight = PMMoverDefaults.CapsuleHalfHeightMeters * PMMoverDefaults.DefaultScale;

            PMVector3 probeCenter = new PMVector3(x, SpawnProbeStartY, z);

            PMMoverGround ground;
            try
            {
                ground = _query.QueryGround(probeCenter, radius, halfHeight, SpawnGroundProbeMeters);
            }
            catch (Exception ex)
            {
                error = "出生位" + site + "地面查询异常：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            if (!ground.Found)
            {
                error = "出生位" + site + "在向下 " + SpawnGroundProbeMeters.ToString("R", CultureInfo.InvariantCulture)
                        + " 米内有界查询**未找到真实地面**：拒绝用假地面顶替"
                        + "（先确认 C1 资源已生成、地图含地面 Collider、且该点在地面范围内）。";
                return false;
            }

            if (!ground.Normal.IsFinite || DotUp(ground.Normal) < SpawnSupportUpMinDot)
            {
                error = "出生位" + site + "找到的支撑面不是朝上的平面（法线=" + ground.Normal
                        + "，与 +Y 点积=" + DotUp(ground.Normal).ToString("R", CultureInfo.InvariantCulture)
                        + "）：拒绝在斜面/墙顶上出生。";
                return false;
            }

            if (ground.Distance < 0f)
            {
                error = "出生位" + site + "的探测起点已与支撑面穿透（signed distance="
                        + ground.Distance.ToString("R", CultureInfo.InvariantCulture)
                        + " 米）：探测起点 Y=" + SpawnProbeStartY.ToString("R", CultureInfo.InvariantCulture)
                        + " 过低或地图几何异常。";
                return false;
            }

            float supportTopY = SpawnProbeStartY - halfHeight - ground.Distance;
            float centerY = supportTopY + halfHeight;

            if (centerY < SpawnCenterYMin || centerY > SpawnCenterYMax)
            {
                error = "出生位" + site + "解出的中心 Y=" + centerY.ToString("R", CultureInfo.InvariantCulture)
                        + " 超出合理范围 [" + SpawnCenterYMin.ToString("R", CultureInfo.InvariantCulture)
                        + ", " + SpawnCenterYMax.ToString("R", CultureInfo.InvariantCulture)
                        + "]（支撑面顶 Y=" + supportTopY.ToString("R", CultureInfo.InvariantCulture) + "）。";
                return false;
            }

            // 真实 Unity 胶囊重叠查询：同一物理场景 / 同一层 / 同一白名单。
            string overlapError;
            if (!HasObstacleOverlap(new PMVector3(x, centerY, z), radius, halfHeight, supportTopY, site, out overlapError))
            {
                error = overlapError;
                return false;
            }

            state.Position = new PMVector3(x, centerY, z);
            state.Velocity = PMVector3.Zero;
            state.PreAdditiveVelocity = PMVector3.Zero;
            state.YawDegrees = SpawnYawDegrees;
            state.Mode = PMMoverMode.Walking;
            state.Grounded = true;
            state.GroundNormal = PMVector3.Up;
            state.ActiveLayers = null;
            return true;
        }

        /// <summary>
        /// 校验全部队伍/槽位（2×3）。供 C3 在"上报就绪之前"做一次 fail-fast 自检，
        /// 也供 C4 实机时把 6 个出生位一次性打印出来对账。
        /// </summary>
        public bool TryValidateAllSpawns(out string error)
        {
            error = null;
            RequireAlive();

            for (int teamIndex = 0; teamIndex < TeamCount; teamIndex++)
            {
                for (int slot = 0; slot < SpawnSlotCount; slot++)
                {
                    PMMoverSyncState state;
                    if (!TryGetSpawn(teamIndex, slot, out state, out error))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>把全部出生位（成功或失败）写成一维多行文本，便于进日志/报告。</summary>
        public string DescribeSpawns()
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();

            for (int teamIndex = 0; teamIndex < TeamCount; teamIndex++)
            {
                for (int slot = 0; slot < SpawnSlotCount; slot++)
                {
                    if (teamIndex > 0 || slot > 0) { builder.Append(" | "); }

                    PMMoverSyncState state;
                    string error;
                    bool ok = TryGetSpawn(teamIndex, slot, out state, out error);
                    string site = "team" + teamIndex.ToString(CultureInfo.InvariantCulture)
                                  + "/slot" + slot.ToString(CultureInfo.InvariantCulture);

                    if (ok)
                    {
                        builder.Append(site).Append("=(")
                               .Append(state.Position.X.ToString("F3", CultureInfo.InvariantCulture)).Append(", ")
                               .Append(state.Position.Y.ToString("F3", CultureInfo.InvariantCulture)).Append(", ")
                               .Append(state.Position.Z.ToString("F3", CultureInfo.InvariantCulture)).Append(")");
                    }
                    else
                    {
                        builder.Append(site).Append("=FAIL(").Append(error).Append(")");
                    }
                }
            }

            return builder.ToString();
        }

        private bool HasObstacleOverlap(PMVector3 center, float radius, float halfHeight,
                                        float supportTopY, string site, out string error)
        {
            error = null;

            float offset = halfHeight - radius;
            if (offset < 0f) { offset = 0f; }

            Vector3 c = new Vector3(center.X, center.Y, center.Z);
            Vector3 p1 = c + Vector3.up * offset;
            Vector3 p2 = c - Vector3.up * offset;

            int count;
            try
            {
                count = _physicsScene.OverlapCapsule(p1, p2, radius, _spawnOverlapBuffer,
                                                     LayerMaskOf(_colliders), QueryTriggerInteraction.Ignore);
            }
            catch (Exception ex)
            {
                error = "出生位" + site + "重叠查询异常：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            if (count >= SpawnOverlapCapacity)
            {
                error = "出生位" + site + "重叠查询缓冲饱和（count=" + count.ToString(CultureInfo.InvariantCulture)
                        + "，容量=" + SpawnOverlapCapacity.ToString(CultureInfo.InvariantCulture)
                        + "）：按契约显式失败，绝不静默取「碰巧拿到的」那一个。";
                return false;
            }

            float topLimit = supportTopY + SpawnSupportToleranceMeters;

            for (int i = 0; i < count; i++)
            {
                Collider collider = _spawnOverlapBuffer[i];
                if (collider == null) { continue; }
                if (!IsObstacle(collider)) { continue; }
                if (collider.isTrigger) { continue; }

                // 支撑面自身（顶面不高于支撑面顶）不算障碍；真正插进胶囊的墙/障碍会被这里抓到。
                if (collider.bounds.max.y <= topLimit) { continue; }

                error = "出生位" + site + "与障碍重叠（Collider=" + collider.GetType().Name
                        + "，对象=\"" + PathOf(collider.gameObject)
                        + "\"，bounds.max.y=" + collider.bounds.max.y.ToString("F3", CultureInfo.InvariantCulture)
                        + " > 支撑面顶+" + SpawnSupportToleranceMeters.ToString("R", CultureInfo.InvariantCulture)
                        + "）：拒绝在嵌墙/嵌障碍的位置出生。";
                return false;
            }

            return true;
        }

        private bool IsObstacle(Collider collider)
        {
            if (_colliders == null) { return false; }

            for (int i = 0; i < _colliders.Length; i++)
            {
                if (ReferenceEquals(_colliders[i], collider))
                {
                    return true;
                }
            }

            return false;
        }

        private static int LayerMaskOf(Collider[] colliders)
        {
            int mask = 0;
            if (colliders == null) { return mask; }

            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                {
                    mask |= 1 << colliders[i].gameObject.layer;
                }
            }

            return mask;
        }

        // ---------------------------------------------------------------- 摘要与一致性

        /// <summary>
        /// 运行时**结构摘要**（uint，非 0，编码遵循 manifest 数据协议）。输入：白名单里每个
        /// Collider 的类型序号 + layer + enabled/trigger 标记 + 世界 AABB（量化 1e-4 米），
        /// 外加 manifest.worldVersion 与数量；排序后再哈希，因此与层级/返回顺序无关。
        ///
        /// **不可**与 manifest.contentDigest / collisionDigest 比较：那两个覆盖的是编辑器侧
        /// 的内容指纹（模板源、mesh 引用、源 Player 指纹），运行期拿不到，本类不假装重算。
        /// 它只回答"C3 的两个对端是否加载到同一份几何"（同一构建、同一平台）。
        ///
        /// 释放后仍返回加载时算好的缓存值（纯数字，便于宿主记账）；想拿“当前是否还活着”
        /// 请看 <see cref="Disposed"/>。
        /// </summary>
        public uint GetSceneDigest()
        {
            return _sceneDigest;
        }

        /// <summary>
        /// 用 manifest 的冻结字段口径做一次加载后自检：
        ///   · manifest 仍通过强校验（防止外部改写）；
        ///   · 硬障碍数量在用界内且非 0；
        ///   · 运动查询绑定的 WorldVersion == manifest.worldVersion；
        ///   · 结构摘要非 0。
        /// 该检查在 <see cref="TryLoad"/> 末尾已经跑过一次（失败即拒绝加载），
        /// 暴露出来是给 C3 在"上报就绪之前"再确认一次。
        /// </summary>
        public bool ValidateManifestConsistency(out string error)
        {
            error = null;
            RequireAlive();

            if (!PMBattleContentManifest.Validate(_manifest, out error))
            {
                return false;
            }

            if (_colliders == null || _colliders.Length == 0)
            {
                error = "地图没有硬障碍 Collider。";
                return false;
            }

            if (_colliders.Length > MaxColliderCount)
            {
                error = "地图硬障碍 Collider 数量 " + _colliders.Length.ToString(CultureInfo.InvariantCulture)
                        + " 超过上限 " + MaxColliderCount.ToString(CultureInfo.InvariantCulture) + "。";
                return false;
            }

            if (_query == null)
            {
                error = "运动碰撞查询为 null。";
                return false;
            }

            if (_query.WorldVersion != _manifest.worldVersion)
            {
                error = "运动查询 WorldVersion=" + _query.WorldVersion.ToString(CultureInfo.InvariantCulture)
                        + " 与 manifest.worldVersion=" + _manifest.worldVersion.ToString(CultureInfo.InvariantCulture)
                        + " 不一致。";
                return false;
            }

            if (_sceneDigest == 0u)
            {
                error = "场景结构摘要为 0（契约要求非 0）。";
                return false;
            }

            return true;
        }

        /// <summary>一行摘要（进日志/报告用）。</summary>
        public string Describe()
        {
            if (_disposed)
            {
                return "PMUnityBattleMap(disposed)";
            }

            return "PMUnityBattleMap(scene=\"" + _scene.name + "\""
                   + ", colliders=" + _colliders.Length.ToString(CultureInfo.InvariantCulture)
                   + " (box=" + _boxColliderCount.ToString(CultureInfo.InvariantCulture)
                   + ", nonBox=" + _nonBoxColliderCount.ToString(CultureInfo.InvariantCulture) + ")"
                   + ", triggers=" + _triggerColliderCount.ToString(CultureInfo.InvariantCulture)
                   + ", kinematicRigidbodies=" + _kinematicRigidbodyCount.ToString(CultureInfo.InvariantCulture)
                   + ", layerMask=0x" + LayerMaskOf(_colliders).ToString("X8", CultureInfo.InvariantCulture)
                   + ", worldVersion=" + _manifest.worldVersion.ToString(CultureInfo.InvariantCulture)
                   + ", sceneDigest=" + _sceneDigest.ToString(CultureInfo.InvariantCulture)
                   + ", spawnQueries=" + _spawnQueries.ToString(CultureInfo.InvariantCulture) + ")";
        }

        // ---------------------------------------------------------------- 释放

        /// <summary>
        /// 幂等释放：销毁地图实例（其对象都在隔离场景里）→ 卸载隔离场景 → 清空查询引用。
        ///
        /// 释放后：
        ///   · <see cref="Root"/> = null、<see cref="Query"/> = null、
        ///     <see cref="Colliders"/> 为空数组、<see cref="Scene"/>/<see cref="PhysicsScene"/> 为 default；
        ///   · <see cref="Manifest"/> 仍可读（纯数据，宿主记账/握手可能还要用）；
        ///   · <see cref="TryGetSpawn"/> 等实例方法抛 ObjectDisposedException（不静默返回坏数据）。
        /// 活动场景语义不被本类改写：加载期的临时切换在 finally 里已还原，释放期不再切场景。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            GameObject root = _root;
            if (root != null)
            {
                DestroyObject(root);
            }

            ReleaseScene(_scene);
        }

        // ---------------------------------------------------------------- 内部工具

        private void RequireAlive()
        {
            if (_disposed || _query == null)
            {
                throw new ObjectDisposedException("PMUnityBattleMap",
                    "地图已释放：释放后不得再查询出生位/碰撞（宿主必须先停止驱动再 Dispose）。");
            }
        }

        /// <summary>
        /// 只做**运行时**释放（<see cref="SceneManager.UnloadSceneAsync(Scene)"/>）：
        /// 本文件同时被 DS 构建与客户端编译，不允许引用 UnityEditor。
        /// 与 PMR3TestScene.ReleaseIsolatedScene 同一语义（本类不引用 Server 层，故各自实现）。
        /// </summary>
        private static void ReleaseScene(Scene scene)
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
                // 清理路径不再抛：释放失败也不能把宿主带崩（后续故障由宿主日志体现）。
            }
        }

        private static void DestroyObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static float DotUp(PMVector3 v)
        {
            return v.Y;
        }

        private static string PathOf(GameObject go)
        {
            if (go == null) { return "<null>"; }

            Transform current = go.transform;
            string path = go.name;

            while (current.parent != null)
            {
                current = current.parent;
                path = current.name + "/" + path;
            }

            return path;
        }

        private static uint ComputeSceneDigest(PMBattleContentManifest manifest, Collider[] colliders,
                                              int layerMask, int triggerCount, int nonBoxCount)
        {
            int count = colliders.Length;
            ColliderDescriptor[] descriptors = new ColliderDescriptor[count];

            for (int i = 0; i < count; i++)
            {
                Collider collider = colliders[i];
                Bounds bounds = collider.bounds;

                ColliderDescriptor descriptor = new ColliderDescriptor();
                descriptor.TypeOrdinal = TypeOrdinal(collider);
                descriptor.Layer = collider.gameObject.layer;
                descriptor.Flags = (collider.enabled ? 1 : 0) | (collider.isTrigger ? 2 : 0);
                descriptor.MinX = Quantize(bounds.min.x);
                descriptor.MinY = Quantize(bounds.min.y);
                descriptor.MinZ = Quantize(bounds.min.z);
                descriptor.MaxX = Quantize(bounds.max.x);
                descriptor.MaxY = Quantize(bounds.max.y);
                descriptor.MaxZ = Quantize(bounds.max.z);
                descriptors[i] = descriptor;
            }

            SortDescriptors(descriptors);

            uint hash = 2166136261u;   // FNV-1a 32 位 offset basis

            hash = HashInt32(hash, manifest.worldVersion);
            hash = HashInt32(hash, layerMask);
            hash = HashInt32(hash, count);
            hash = HashInt32(hash, triggerCount);
            hash = HashInt32(hash, nonBoxCount);

            for (int i = 0; i < count; i++)
            {
                ColliderDescriptor descriptor = descriptors[i];
                hash = HashInt32(hash, descriptor.TypeOrdinal);
                hash = HashInt32(hash, descriptor.Layer);
                hash = HashInt32(hash, descriptor.Flags);
                hash = HashInt32(hash, descriptor.MinX);
                hash = HashInt32(hash, descriptor.MinY);
                hash = HashInt32(hash, descriptor.MinZ);
                hash = HashInt32(hash, descriptor.MaxX);
                hash = HashInt32(hash, descriptor.MaxY);
                hash = HashInt32(hash, descriptor.MaxZ);
            }

            if (hash == 0u)
            {
                hash = 1u;   // 与 manifest 派生规则同一纪律：摘要 0 无意义，取 1
            }

            return hash;
        }

        /// <summary>
        /// 形状序号：只用**类型**（不读 mesh 引用，运行期也不该读）。
        /// Box=1 / Sphere=2 / Capsule=3 / Mesh=4 / Terrain=5 / 其它=99。
        /// </summary>
        private static int TypeOrdinal(Collider collider)
        {
            if (collider is BoxCollider) { return 1; }
            if (collider is SphereCollider) { return 2; }
            if (collider is CapsuleCollider) { return 3; }
            if (collider is MeshCollider) { return 4; }
            if (collider is TerrainCollider) { return 5; }
            return 99;
        }

        private static int Quantize(float value)
        {
            double scaled = (double)value * (double)DigestQuantizationPerMeter;
            double rounded = Math.Round(scaled, MidpointRounding.AwayFromZero);

            if (rounded > int.MaxValue) { return int.MaxValue; }
            if (rounded < int.MinValue) { return int.MinValue; }
            return (int)rounded;
        }

        private static uint HashInt32(uint hash, int value)
        {
            unchecked
            {
                uint v = (uint)value;
                hash = (hash ^ (v & 0xffu)) * 16777619u;
                hash = (hash ^ ((v >> 8) & 0xffu)) * 16777619u;
                hash = (hash ^ ((v >> 16) & 0xffu)) * 16777619u;
                hash = (hash ^ ((v >> 24) & 0xffu)) * 16777619u;
                return hash;
            }
        }

        private static void SortDescriptors(ColliderDescriptor[] descriptors)
        {
            // 插入排序：n ≤ 4096 且只在地图加载时跑一次；结果与输入顺序无关（确定性优先于常数）。
            for (int i = 1; i < descriptors.Length; i++)
            {
                ColliderDescriptor key = descriptors[i];
                int j = i - 1;

                while (j >= 0 && Compare(descriptors[j], key) > 0)
                {
                    descriptors[j + 1] = descriptors[j];
                    j--;
                }

                descriptors[j + 1] = key;
            }
        }

        private static int Compare(ColliderDescriptor a, ColliderDescriptor b)
        {
            int result = CompareInt(a.TypeOrdinal, b.TypeOrdinal); if (result != 0) { return result; }
            result = CompareInt(a.Layer, b.Layer); if (result != 0) { return result; }
            result = CompareInt(a.Flags, b.Flags); if (result != 0) { return result; }
            result = CompareInt(a.MinX, b.MinX); if (result != 0) { return result; }
            result = CompareInt(a.MinY, b.MinY); if (result != 0) { return result; }
            result = CompareInt(a.MinZ, b.MinZ); if (result != 0) { return result; }
            result = CompareInt(a.MaxX, b.MaxX); if (result != 0) { return result; }
            result = CompareInt(a.MaxY, b.MaxY); if (result != 0) { return result; }
            return CompareInt(a.MaxZ, b.MaxZ);
        }

        private static int CompareInt(int a, int b)
        {
            if (a < b) { return -1; }
            if (a > b) { return 1; }
            return 0;
        }

        /// <summary>摘要用的 Collider 描述（值类型；只含可见几何与形状类别）。</summary>
        private struct ColliderDescriptor
        {
            public int TypeOrdinal;
            public int Layer;
            public int Flags;
            public int MinX;
            public int MinY;
            public int MinZ;
            public int MaxX;
            public int MaxY;
            public int MaxZ;
        }
    }
}
