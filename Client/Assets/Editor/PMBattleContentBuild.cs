// ============================================================================
//  PMBattleContentBuild.cs —— R4-C / C1：正式战斗内容的**构建期烘焙**
// ============================================================================
//
//  契约来源：Docs/plans/net-r4c-content-contract.md（唯一冻结源）与
//            Docs/plans/net-architecture-migration.md 的 R4-C 段（状态唯一源）。
//
//  本文件回答一个问题：**正式地图与角色表现从哪里来？**
//    · 旧链的地图不是资产，而是 `ScenseBuildLogic.InitData()` 在运行期用
//      `UnityEngine.Random`（**未播种**）逐格生成的；直接加载 HYLDGame.unity 会拉起
//      旧 BattleManger/UDPSocketManger（硬编码 UDP 7777）/UI/相机，两端必然不同图。
//    · 契约的解法：**构建期用固定种子烘焙一次**，DS 与客户端加载同一份产物
//      （Resources/PMNet/BattleMapV1.prefab），运行期不各自随机。
//
//  产物（三个，全部由本文件生成；运行期绝不回退旧内容）：
//    Assets/Resources/PMNet/BattleMapV1.prefab     Resources 键 "PMNet/BattleMapV1"
//    Assets/Resources/PMNet/PlayerVisualV1.prefab  Resources 键 "PMNet/PlayerVisualV1"
//    Assets/Resources/PMNet/BattleContentV1.json   Resources 键 "PMNet/BattleContentV1"
//
//  manifest 的 schema（formatVersion/mapId/seed/mapResource/playerResource/
//  contentDigest/collisionDigest/worldVersion）**由 C2 的 PMNet.Unity.PMBattleContentManifest
//  定义**，本文件直接复用它的类型与冻结派生规则（TryDeriveCollisionDigestAndWorldVersion），
//  不重写第二份口径 —— 两端各写一遍派生规则正是最容易在握手时才暴露的漂移。
//
//  ---------------------------------------------------------------------------
//  本文件**不做**什么（读的人最容易误解的地方）
//  ---------------------------------------------------------------------------
//    · 不调用 `ScenseBuildLogic.InitData()`（依赖全局 Random 与旧玩法数据）；
//    · 不使用 `UnityEngine.Random`（不用全局随机态；自带的 DeterministicRng 是可复现的
//      局部 PRNG，种子固定 0x52444301）。**不使用**意味着任何代码路径都不读写它 ——
//      若发现全局随机态被改动，摘要里会显式列出（见 RandomStateNote）；
//    · 不改动源资产：`Assets/Scenes/HYLDGame.unity` 与 `Resources/Remake/Player.prefab`
//      只读；角色是"克隆后清理"，绝不对原件 Destroy/Apply；
//    · 不进入 Play 模式（`Application.isPlaying` 时直接拒绝执行）；
//    · 不抢编辑器锁、不自动运行：菜单要用户点，或由 PMDsBuild 在构建前调用；
//    · **不做**UE 资产那套 MCP 读取：Unity 场景/prefab 是文本 YAML + 真实 Editor 对象，
//      按 Unity 方式读（见下）。
//
//  ---------------------------------------------------------------------------
//  源场景怎么读（这是"安全预览"的落点，也是本文件最需要解释的一处）
//  ---------------------------------------------------------------------------
//  契约要求：用 Editor 安全预览场景读取，或另一**经验证不会执行旧游戏 Awake**的编辑模式方案。
//  实测核验（本次 C1 调查的结论，见 report；其中「包内脚本无编辑模式标记」这句原先是错的，
//  复核已改正为按**脚本来源**分桶）：
//    · **工程内玩法脚本**（`Assets/**`：HYLDStaticValue / BattleManger / TouchLogic / Toolbox /
//      ScenseBuildLogic …）**一律没有**编辑模式标记 ⇒
//      **没有任何旧游戏 Awake/OnEnable 会因本文件加载场景而执行**；
//    · 工程内**带**编辑模式标记的只有第三方摇杆插件的 EasyJoystick / EasyButton：它们只动
//      自身 UI 状态与静态 EasyTouch 事件（OnDisable 会反注册），不创建/销毁场景对象、不碰游戏状态；
//    · 源场景里还有**大量 Unity 包/引擎自带的编辑模式组件**：ugui 的 `Graphic`（`Image`/`Text`
//      的基类）与 `CanvasScaler`/`Selectable`(→`Button`)/`Slider` 都带 `[ExecuteAlways]`，而
//      `ExecuteAlways.AttributeUsage.Inherited == true` ⇒ `Attribute.IsDefined(typeof(Image),
//      typeof(ExecuteAlways))` 为 true（实测读本工程编译产物 UnityEngine.UI.dll 与真实
//      UnityEngine.CoreModule.dll 的元数据）。它们**不是**未审计脚本：原实现把它们当违规项，
//      会让源场景永远过不了审计 ⇒ 烘焙恒失败（这正是复核改掉的那处缺陷）。
//  因此本文件用**两道门** + 「EditorSceneManager.OpenScene(..., Additive) 预览读取 + finally 关闭」：
//    · **打开之前**的预检 PreflightProjectEditModeAudit：读场景文本里的脚本 guid，只对
//      **工程内** `.cs` 解析类型并查编辑模式标记 ⇒ 违规时**根本不打开场景**
//      （即"audit 在 Awake 之前"是**真的**，而不是"打开后才发现"）；
//    · 打开之后的运行时复核 AuditEditModeScripts：按脚本来源（工程内 / 包与引擎 / 无法判定）分桶；
//      包与引擎的编辑模式组件只记入摘要（Unity 自带；本文件不 Save 场景，其组件写入不会被保留），
//      工程内违规与无法判定都显式失败。
//  同时保证：不改 activeScene（finally 还原）、不动用户选择（finally 还原）、
//  不改源场景脏状态（不 Save、只 Close 自己开的那一份）。
//
//  ---------------------------------------------------------------------------
//  地图几何怎么复刻（逐字段，不简化）
//  ---------------------------------------------------------------------------
//  完全按 `ScenseBuildLogic.InitData()` 的**循环结构与随机数消费顺序**复刻，只把
//  `UnityEngine.Random.Range` 换成局部确定 PRNG：
//    1) 33×21 网格（mapx/mapy 取自场景序列化值，不擅自扩到表的 35 行）；
//       `maps[i,j]` 直接读**真实类**的 `map2` 数组（不手抄 735 个数字）；
//    2) 边界墙（4 条边）；3) 外围树；4) 边界外补地板。
//  随机数消费顺序逐处对齐（**包括源码里被注释掉实例化、但仍然消费一次随机数的草丛分支**），
//  详见 BuildLayout 内注释。
//
//  世界变换的一处关键事实（很容易做错，故写在这里）：
//    源码 `MyInstantiate` = `Instantiate(t, worldPos, rot)` 后 `SetParent(MAP)`；
//    `SetParent(Transform)` 单参重载 = `worldPositionStays: true`，于是父级 `3D` 的
//    0.5 缩放被**抵消**（子物体局部值被反向放大 2 倍），tile 的**世界**变换就等于
//    `(i-16, y, j-10) / identity / 模板自身缩放`。因此烘焙产物按**世界等价**存放：
//    容器用 identity，tile 局部值 = 世界值（不做 0.5 × 2 往返，避免把地图做小一半）。
//
//  ---------------------------------------------------------------------------
//  组件白名单（与 C2 运行期校验逐条对齐，写死在这里）
//  ---------------------------------------------------------------------------
//    地图（PMUnityBattleMap.CollectAndValidate 的允许集）：
//      Transform 系 / MeshFilter / MeshRenderer / **任意 Collider** / LODGroup /
//      **kinematic** Rigidbody；其它任何类型（含 MonoBehaviour / Camera / Animator /
//      AudioListener / 动态 Rigidbody / SkinnedMeshRenderer）在 C2 侧是**致命**的 ⇒
//      本文件对地图模板采取 **fail-closed**：出现白名单外组件直接**让生成失败**，
//      而不是"报告一下继续"。源场景实测 5 类模板子树里只有
//      Transform/MeshFilter/MeshRenderer/MeshCollider/BoxCollider，白名单外为 0。
//    角色（PMUnityBattlePresentation.ValidateComponents 的允许集）：
//      Transform 系 / MeshFilter / MeshRenderer / SkinnedMeshRenderer / Animator / LODGroup；
//      其余（旧 PlayerLogic/HYLDPlayerController/missing script/Rigidbody/BoxCollider/
//      AudioSource/Canvas 系 UI/LineRenderer/SpriteRenderer/CanvasRenderer…）**删除**，
//      并逐类型计数上报（"报告"而不是"静默残留"）。
//
//  ---------------------------------------------------------------------------
//  摘要（digest）与 manifest 的发布时序
//  ---------------------------------------------------------------------------
//    contentDigest = SHA256(规范化文本流) 的小写 hex，流的内容：
//      域标签 + 冻结常量（formatVersion/mapId/seed/mapx/mapy）
//      + **三个源文件的 SHA256**（HYLDGame.unity / ScenseBuildLogic.cs / Remake/Player.prefab；
//        Player 那条就是契约要求的"源 Player 指纹"）
//      + 模板调色板逐个的规范化签名（名字/激活/层/局部变换/mesh 引用与内容锚/材质/碰撞体字段）
//      + 地面 Plane 的规范化签名
//      + 每个实例化的规范行（类别/模板序号/局部位置/旋转）
//      + 落点总数（placements.count）
//    角色表现的"清理后内容"不进 digest：它的输入就是源 prefab（已由 SHA 钉死），
//    清理规则是同一次构建里的代码常量，因此 digest 无需重复表征它。
//    规范化文本只用：源文件 SHA、对象**名字**、资产路径、mesh 顶点数/bounds、组件序列化字段值；
//    **不用**实例 ID / GetHashCode / 运行期随机 / AssetDatabase 本地子资产编号
//    （后者跨会话稳定性不由我们控制，改了会让"重复生成"摘要漂移）。
//    collisionDigest / worldVersion 由 C2 的 PMBattleContentManifest 冻结规则派生。
//
//    发布时序（"部分失败不 valid"）：先生成两个 prefab 并**回读校验**，全部通过后才写
//    manifest；开始前先删掉旧 manifest，任何失败路径都不留下 manifest。
//
//  ---------------------------------------------------------------------------
//  API（谁调用什么）
//  ---------------------------------------------------------------------------
//    [MenuItem("Build/Prepare PMNet Battle Content")] PrepareContentMenu()  —— 用户显式烘焙
//    PrepareContent()   强制完整烘焙（失败不留 manifest）；返回 bool
//    ValidateContent()  只读校验既有产物（不写任何资源），给构建 hook 用；返回 bool
//    PrepareForBuild()  PMDsBuild 构建前调用：缺产物/源较新则烘焙，然后校验；返回 bool
//
//  语言面：Unity 2019.4 / C# 7.3 / .NET Standard 2.0。不要在 Unity API 签名上有任何"想当然"：
//  本文件同时被 Tools/PMBattleContentBuildCheck 用**真实 Unity 2019.4 程序集**(C#7.3) 编译，
//  编译不过就不会交付。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PMNet.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PMNet.UnityEditor
{
    /// <summary>
    /// R4-C / C1：把「正式地图 + 干净角色表现 + 内容 manifest」在**编辑期**烘焙成 Resources 产物。
    ///
    /// 设计原则（与契约逐条对应）：
    ///   1) **确定性**：固定种子 + 局部 PRNG + 规范化摘要，重复生成摘要稳定；
    ///   2) **fail closed**：任何不确定/越界/白名单外内容都显式失败，不回退旧内容、不静默丢弃；
    ///   3) **不改源**：源场景/源 prefab 只读；临时对象全部在临时场景里，finally 清理；
    ///   4) **不假成功**：只有两个 prefab 回读校验通过后才发布 manifest。
    /// </summary>
    public static class PMBattleContentBuild
    {
        // ==================================================================== 冻结常量

        /// <summary>正式战斗场景（唯一联机入口；BuildSettings index 2）。</summary>
        public const string SourceScenePath = "Assets/Scenes/HYLDGame.unity";

        /// <summary>旧链唯一角色预制体（本文件只读它，不改它）。</summary>
        public const string SourcePlayerPath = "Assets/Resources/Remake/Player.prefab";

        /// <summary>地图生成算法与 map2 表的来源文件（进摘要；它就是"布局来源"）。</summary>
        public const string SourceLogicPath = "Assets/HYLD1.0/Scripts/OldScripts/ScenseBuildLogic.cs";

        /// <summary>三个产物必须落在的目录（与 C2 的资源键目录前缀一致）。</summary>
        public const string ResourcesDir = "Assets/Resources/PMNet";

        public const string MapPrefabPath = ResourcesDir + "/BattleMapV1.prefab";
        public const string PlayerPrefabPath = ResourcesDir + "/PlayerVisualV1.prefab";
        public const string ManifestAssetPath = ResourcesDir + "/BattleContentV1.json";

        /// <summary>固定烘焙种子（契约冻结 0x52444301 = 1380205313）。</summary>
        public const int BakeSeed = unchecked((int)0x52444301);

        /// <summary>地面来源（唯一的"地板碰撞"；floors 模板自身没有 Collider）。</summary>
        public const string GroundSourcePath = "HYLDGameTatal/MAP/Plane";

        private const string MapRootName = "BattleMapV1";
        private const string GroundContainerName = "Ground";
        private const string MapContainerName = "MAP";
        private const string PlayerRootName = "PlayerVisualV1";

        /// <summary>摘要域标签（改它就是改摘要口径，必须与主计划同步）。</summary>
        private const string DigestDomain = "PMNetBattleContentDigest/v1";

        /// <summary>地图模板允许的组件类型名（= C2 允许集；白名单外**失败**）。</summary>
        private static readonly string[] MapAllowedComponentNames =
        {
            "MeshFilter", "MeshRenderer", "LODGroup",
        };

        /// <summary>角色表现保留的组件类型名（= C2 允许集；不在列表内的一律删除并上报）。</summary>
        private static readonly string[] PlayerKeptComponentNames =
        {
            "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "Animator", "LODGroup",
        };

        /// <summary>
        /// 允许在编辑模式下执行的**工程内**脚本类型名（白名单，超出即**失败**）。
        ///
        /// 判据是脚本**来源**，不是"有没有编辑模式标记"（这一处是复核时改正的，原判据会让烘焙必失败）：
        ///   · Unity 自带/包内的编辑模式组件在源场景里大量存在：ugui 的 `Graphic`（`Image`/`Text`
        ///     的基类）与 `CanvasScaler`/`Selectable`（`Button` 的基类）/`Slider` 都带
        ///     `[ExecuteAlways]`，而 `ExecuteAlways` 的 `AttributeUsage.Inherited == true`
        ///     ⇒ `Attribute.IsDefined(typeof(Image), typeof(ExecuteAlways))` 为 **true**
        ///     （实测：读本工程编译产物 `Library/ScriptAssemblies/UnityEngine.UI.dll` 与
        ///     真实 `UnityEngine.CoreModule.dll` 的元数据）。把它们当"未审计脚本"，
        ///     源场景永远过不了审计 ⇒ `PrepareContent()` 恒为 false ⇒ 三个产物永远生成不出来；
        ///   · 真正要挡的是**工程内玩法脚本**：它们一旦带上编辑模式标记，其 Awake/OnEnable
        ///     就会在加载源场景时执行旧玩法逻辑。
        /// 因此审计按脚本资产来源分桶（见 <see cref="ClassifyEditModeScriptOrigin"/>）：
        /// 工程内（`Assets/**`）必须命中本白名单；包/引擎组件记录进摘要但不判失败；
        /// 来源无法判定的仍然 fail closed。
        ///
        /// 表里的两个都是工程内的第三方摇杆插件：其编辑模式回调只操作自身 UI 状态与
        /// 静态 EasyTouch 事件（OnDisable 反注册），不创建/销毁场景对象、不碰游戏状态。
        /// </summary>
        private static readonly string[] EditModeToleratedScriptNames =
        {
            "EasyJoystick", "EasyButton",
        };

        /// <summary>
        /// 单个带编辑模式标记的组件属于哪个来源。判定结果直接决定
        /// "是否必须命中 <see cref="EditModeToleratedScriptNames"/>"。
        /// </summary>
        private enum EditModeScriptOrigin
        {
            /// <summary>工程内脚本（`Assets/**`）：必须命中白名单，否则失败。</summary>
            Project,

            /// <summary>Unity 包/引擎自带组件：记入摘要，不判失败。</summary>
            PackageOrEngine,

            /// <summary>来源无法判定：fail closed。</summary>
            Unknown,
        }

        /// <summary>
        /// 判定一个带编辑模式标记的组件是"工程内脚本"还是"Unity 包/引擎组件"。
        ///
        /// 判据顺序（先严后兜底，确保"工程内脚本"永远走严判）：
        ///   1) MonoScript 的资产路径以 `Assets/` 开头 ⇒ **Project**（唯一需要人工审计的一类）；
        ///   2) 否则看**程序集名**前缀 `UnityEngine` / `Unity.`（`UnityEngine.UI`、`Unity.TextMeshPro`…）
        ///      ⇒ **PackageOrEngine**。这一步是必要的兜底：内置包脚本的 `GetAssetPath` 形态并不唯一
        ///      （可能是 `Packages/...`、`Library/PackageCache/...`，也可能是编辑器安装目录下的
        ///      `.../BuiltInPackages/...`），而程序集名是稳定的；
        ///   3) 路径含 `Packages/` / `PackageCache` / `BuiltInPackages` ⇒ **PackageOrEngine**
        ///      （覆盖"程序集名不是 Unity 前缀、但确实来自包"的情况）；
        ///   4) 其它 ⇒ **Unknown**（调用方 fail closed，绝不静默放过）。
        ///
        /// 为何不能只看程序集名：工程内脚本与包内脚本在运行期都落在
        /// `Library/ScriptAssemblies/*.dll`（`Assembly-CSharp.dll` 与 `UnityEngine.UI.dll` 同目录），
        /// 光看程序集名分不开"工程内玩法脚本"与"Unity 自带组件" —— 所以"路径以 Assets/ 开头"
        /// 必须是第一判据。
        /// </summary>
        private static EditModeScriptOrigin ClassifyEditModeScriptOrigin(Type type, Component component,
                                                                       out string detail)
        {
            string assetPath = null;

            MonoBehaviour behaviour = component as MonoBehaviour;
            if (behaviour != null)
            {
                MonoScript script = null;
                try
                {
                    script = MonoScript.FromMonoBehaviour(behaviour);
                }
                catch (Exception)
                {
                    script = null;
                }

                if (script != null)
                {
                    assetPath = AssetDatabase.GetAssetPath(script);
                }
            }

            string assemblyName = null;
            try
            {
                if (type.Assembly != null && type.Assembly.GetName() != null)
                {
                    assemblyName = type.Assembly.GetName().Name;
                }
            }
            catch (Exception)
            {
                assemblyName = null;
            }

            detail = "path=" + (string.IsNullOrEmpty(assetPath) ? "<无 MonoScript>" : assetPath)
                     + " assembly=" + (assemblyName == null ? "<未知>" : assemblyName);

            // 1) 工程内脚本（最严，且优先级最高）。
            if (!string.IsNullOrEmpty(assetPath)
                && assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return EditModeScriptOrigin.Project;
            }

            // 2) Unity 自带程序集名（内置包/模块的稳定标识）。
            if (!string.IsNullOrEmpty(assemblyName)
                && (assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal)
                    || assemblyName.StartsWith("Unity.", StringComparison.Ordinal)))
            {
                return EditModeScriptOrigin.PackageOrEngine;
            }

            // 3) 包路径形态（含内置包在编辑器安装目录下的形态）。
            if (!string.IsNullOrEmpty(assetPath)
                && (assetPath.StartsWith("Packages/", StringComparison.Ordinal)
                    || assetPath.IndexOf("PackageCache", StringComparison.Ordinal) >= 0
                    || assetPath.IndexOf("BuiltInPackages", StringComparison.Ordinal) >= 0))
            {
                return EditModeScriptOrigin.PackageOrEngine;
            }

            return EditModeScriptOrigin.Unknown;
        }

        // ==================================================================== 入口（菜单 / hook）

        /// <summary>
        /// 用户显式烘焙入口：Build/Prepare PMNet Battle Content。
        ///
        /// 只有在用户点菜单（或构建 hook）时才执行 —— 本文件**不会**自己启动 Unity，
        /// 也不在任何静态构造/InitializeOnLoad 里偷偷生成。
        /// </summary>
        [MenuItem("Build/Prepare PMNet Battle Content")]
        public static void PrepareContentMenu()
        {
            bool ok = PrepareContent();

            string title = ok ? "PMNet 内容烘焙完成" : "PMNet 内容烘焙失败";
            string body = ok
                ? "已生成：\n  " + MapPrefabPath + "\n  " + PlayerPrefabPath + "\n  " + ManifestAssetPath
                  + "\n\n详见 Console 的 [PMBattleContentBuild] 摘要。"
                : "生成失败，**未**发布 manifest（不会留下可被当成有效的半成品）。\n详见 Console 的 [PMBattleContentBuild] 错误。";

            if (ok)
            {
                Debug.Log("[PMBattleContentBuild] " + body.Replace("\n", " | "));
            }

            // batchmode 下弹窗会挂住进程：只在交互模式下提示。
            if (!Application.isBatchMode)
            {
                EditorUtility.DisplayDialog(title, body, "确定");
            }
        }

        /// <summary>
        /// 构建前 hook（由 <c>PMDsBuild.BuildWindowsHeadlessDs</c> 调用）。
        ///
        /// 语义：
        ///   · 产物缺失或源资产比产物新 ⇒ 先完整烘焙；
        ///   · 然后**只读校验**（manifest 内部一致 + 资源组件约束）；
        ///   · 任一步失败返回 false —— 调用方必须让构建失败，
        ///     绝不允许"缺资源的包"被当成功（契约：缺资源不能假成功）。
        ///
        /// 为什么先判"是否需要烘焙"而不是每次都重烘：正常构建不需要重打开源场景，
        /// 少一次编辑模式场景加载就少一份副作用面；而一旦源确实变了，会显式重烘。
        /// </summary>
        public static bool PrepareForBuild()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[PMBattleContentBuild] PrepareForBuild 在 Play 模式下被调用：拒绝执行"
                               + "（本文件只在编辑期烘焙，不参与运行）。");
                return false;
            }

            bool allExist = File.Exists(AbsolutePath(MapPrefabPath))
                            && File.Exists(AbsolutePath(PlayerPrefabPath))
                            && File.Exists(AbsolutePath(ManifestAssetPath));

            bool stale;
            string staleReason;
            if (!allExist)
            {
                stale = true;
                staleReason = "有产物缺失";
            }
            else
            {
                stale = IsStale(out staleReason);
            }

            if (stale)
            {
                Debug.Log("[PMBattleContentBuild] 正式内容需要重新烘焙（" + staleReason + "）。");
                if (!PrepareContent())
                {
                    return false;
                }
            }
            else
            {
                Debug.Log("[PMBattleContentBuild] 正式内容已就绪且不比源资产旧，跳过烘焙，直接校验。");
            }

            return ValidateContent();
        }

        // ==================================================================== 烘焙主流程

        /// <summary>
        /// 强制完整烘焙（菜单 / 构建 hook 的公共实现）。
        ///
        /// 顺序（失败即停止，且**不留下 manifest**）：
        ///   0) 拒绝 Play 模式；确保输出目录存在；先删除旧 manifest（发布信号最后才写）；
        ///   1) 打开源场景（Additive 预览读）→ 编辑模式脚本审计 → 取 ScenseBuildLogic；
        ///   2) 生成布局（局部 PRNG）+ 逐个模板规范化签名 → 算 contentDigest；
        ///   3) 在临时空场景里搭地图层级 → 深拷贝模板 → 真 API 存 prefab → 回读校验；
        ///   4) 克隆源角色 prefab → 清理到白名单 → 真 API 存 prefab → 回读校验；
        ///   5) 写 manifest（JsonUtility 序列化 C2 的类型）→ 再解析一次确认可读；
        ///   6) finally：关场景、还原 activeScene / 选择 / 全局随机态。
        /// </summary>
        public static bool PrepareContent()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[PMBattleContentBuild] 处于 Play 模式：拒绝烘焙（本文件只在编辑期生成资产）。");
                return false;
            }

            StringBuilder summary = new StringBuilder();
            summary.AppendLine("[PMBattleContentBuild] 开始烘焙正式内容（C1）");

            bool ok = false;
            try
            {
                EnsureResourcesDirectory();

                // 发布信号 = manifest：进流程先删掉它，任何失败路径都不会留下"有效 manifest"。
                string clearError;
                if (!RemoveManifestOrInvalidate(summary, out clearError))
                {
                    summary.AppendLine("  无法清除旧 manifest（" + clearError + "）：拒绝开始烘焙，"
                                       + "以免留下\"新 prefab + 旧 manifest\"的组合（digest 与实际内容不再对应）。");
                    return false;
                }

                summary.AppendLine("  已清除旧 manifest（发布信号最后才写）");

                ok = PrepareContentInternal(summary);
            }
            catch (Exception ex)
            {
                summary.AppendLine("  异常：" + ex.GetType().Name + " " + ex.Message);
                summary.AppendLine(ex.StackTrace);
                ok = false;
            }
            finally
            {
                if (!ok)
                {
                    // 失败兜底：绝不留"有效 manifest"（含 AssetDatabase 删不掉时的 File.Delete/作废兜底）。
                    string clearError;
                    if (!RemoveManifestOrInvalidate(summary, out clearError))
                    {
                        summary.AppendLine("  **严重**：无法清除也无法作废 manifest —— " + clearError);
                    }
                }

                summary.AppendLine(ok ? "  结果：成功" : "  结果：失败（未发布 manifest）");
                if (ok)
                {
                    Debug.Log(summary.ToString());
                }
                else
                {
                    Debug.LogError(summary.ToString());
                }
            }

            return ok;
        }

        private static bool PrepareContentInternal(StringBuilder summary)
        {
            string srcSceneSha = Sha256File(AbsolutePath(SourceScenePath));
            string srcLogicSha = Sha256File(AbsolutePath(SourceLogicPath));
            string srcPlayerSha = Sha256File(AbsolutePath(SourcePlayerPath));
            summary.AppendLine("  源文件 SHA256：scene=" + Short(srcSceneSha)
                               + " logic=" + Short(srcLogicSha) + " player=" + Short(srcPlayerSha));

            Scene activeBefore = SceneManager.GetActiveScene();
            UnityEngine.Object[] selectionBefore = Selection.objects;
            UnityEngine.Random.State randomBefore = UnityEngine.Random.state;

            SourceSceneScope scope = null;
            Scene scratchScene = default(Scene);
            bool scratchCreated = false;
            GameObject mapRoot = null;
            GameObject playerClone = null;

            try
            {
                // ---------------- 1) 打开源场景（只读）＋ 编辑模式脚本审计
                string sceneError;
                scope = SourceSceneScope.Open(SourceScenePath, summary, out sceneError);
                if (scope == null)
                {
                    summary.AppendLine("  打开源场景失败：" + sceneError);
                    return false;
                }

                ScenseBuildLogic logic = FindLogic(scope.Scene);
                if (logic == null)
                {
                    summary.AppendLine("  源场景里找不到 ScenseBuildLogic 实例（模板调色板的载体）："
                                       + "不能凭空猜模板来源，显式失败。");
                    return false;
                }

                GameObject groundPlane = FindByPath(scope.Scene, GroundSourcePath);
                if (groundPlane == null)
                {
                    summary.AppendLine("  源场景里找不到地面对象 \"" + GroundSourcePath + "\"："
                                       + "正式地图没有地面碰撞等于不可用，显式失败。");
                    return false;
                }

                // ---------------- 2) 布局（局部确定 PRNG）
                List<Placement> placements;
                string layoutError;
                if (!BuildLayout(logic, out placements, out layoutError))
                {
                    summary.AppendLine("  生成布局失败：" + layoutError);
                    return false;
                }

                // 重复生成一次，证明"同种子同输入 ⇒ 同布局"（摘要稳定的第一道证据）。
                List<Placement> placementsAgain;
                string layoutErrorAgain;
                if (!BuildLayout(logic, out placementsAgain, out layoutErrorAgain)
                    || !SamePlacements(placements, placementsAgain))
                {
                    summary.AppendLine("  布局不可复现（两次生成结果不同）：PRNG 或模板读取不确定，"
                                       + "拒绝发布。原因：" + layoutErrorAgain);
                    return false;
                }

                AccumulateLayoutCounts(summary, placements);

                // ---------------- 3) 摘要（模板源指纹 + 布局 + 源文件 SHA）
                string digestStream = BuildDigestStream(logic, placements, groundPlane,
                                                       srcSceneSha, srcLogicSha, srcPlayerSha,
                                                       summary);
                string contentDigest = Sha256Hex(Encoding.UTF8.GetBytes(digestStream));

                uint collisionDigest;
                int worldVersion;
                string deriveError;
                if (!PMBattleContentManifest.TryDeriveCollisionDigestAndWorldVersion(
                        contentDigest, out collisionDigest, out worldVersion, out deriveError))
                {
                    summary.AppendLine("  由 contentDigest 派生 collisionDigest/worldVersion 失败："
                                       + deriveError);
                    return false;
                }

                summary.AppendLine("  contentDigest=" + contentDigest);
                summary.AppendLine("  collisionDigest=" + collisionDigest.ToString(CultureInfo.InvariantCulture)
                                   + " worldVersion=" + worldVersion.ToString(CultureInfo.InvariantCulture));

                // ---------------- 4) 临时场景（放待保存的层级；不碰用户场景）
                scratchScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                scratchCreated = true;
                if (!scratchScene.IsValid())
                {
                    summary.AppendLine("  创建临时空场景失败（Additive）：拒绝在原地搭层级污染用户场景。");
                    return false;
                }

                SceneManager.SetActiveScene(scratchScene);

                // ---------------- 5) 地图 prefab
                string mapError;
                mapRoot = BuildMapHierarchy(logic, placements, groundPlane, summary, out mapError);
                if (mapRoot == null)
                {
                    summary.AppendLine("  搭建地图层级失败：" + mapError);
                    return false;
                }

                bool mapSaved;
                if (!SavePrefab(mapRoot, MapPrefabPath, out mapSaved, summary))
                {
                    return false;
                }

                // ---------------- 6) 角色 prefab
                GameObject playerSource = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePlayerPath);
                if (playerSource == null)
                {
                    summary.AppendLine("  读不到源角色 prefab：" + SourcePlayerPath + "（契约资源缺失，显式失败）");
                    return false;
                }

                string playerError;
                playerClone = BuildPlayerHierarchy(playerSource, summary, out playerError);
                if (playerClone == null)
                {
                    summary.AppendLine("  搭建角色层级失败：" + playerError);
                    return false;
                }

                bool playerSaved;
                if (!SavePrefab(playerClone, PlayerPrefabPath, out playerSaved, summary))
                {
                    return false;
                }

                // ---------------- 7) 回读校验（生成后必须读回来检查，不能只看内存）
                string verifyError;
                if (!VerifyMapPrefab(MapPrefabPath, placements, summary, out verifyError))
                {
                    summary.AppendLine("  地图 prefab 回读校验失败：" + verifyError);
                    return false;
                }

                if (!VerifyPlayerPrefab(PlayerPrefabPath, summary, out verifyError))
                {
                    summary.AppendLine("  角色 prefab 回读校验失败：" + verifyError);
                    return false;
                }

                // ---------------- 8) 发布 manifest（最后一步 = 发布信号）
                if (!WriteManifest(contentDigest, collisionDigest, worldVersion, summary))
                {
                    return false;
                }

                // 内存里的临时对象全部丢弃（临时场景 finally 里整体关闭）。
                scope.AppendRandomStateNote(summary, randomBefore);
                return true;
            }
            finally
            {
                if (mapRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(mapRoot);
                }

                if (playerClone != null)
                {
                    UnityEngine.Object.DestroyImmediate(playerClone);
                }

                if (scratchCreated && scratchScene.IsValid())
                {
                    // removeScene=true：丢弃临时场景，不提示保存、不污染用户工程。
                    EditorSceneManager.CloseScene(scratchScene, true);
                }

                if (activeBefore.IsValid())
                {
                    SceneManager.SetActiveScene(activeBefore);
                }

                Selection.objects = selectionBefore;
                UnityEngine.Random.state = randomBefore;

                if (scope != null)
                {
                    scope.Dispose();
                }
            }
        }

        // ==================================================================== 只读校验（构建 hook）

        /// <summary>
        /// 只读校验既有产物：**不写任何资源、不打开源场景、不烘焙**。
        ///
        /// 校验面（= 运行期 C2 会做的同一批约束，提前在构建期挡住）：
        ///   1) 三个产物存在且可加载；
        ///   2) manifest 能被 C2 的 TryParseJson 解析并**强校验通过**
        ///      （版本/固定值/资源键白名单/digest 格式/派生值自洽）；
        ///   3) 地图 prefab：根激活、无 missing script、组件全在 C2 允许集内、
        ///      Collider 全部 enabled 且所在物体 active、至少 1 个非 trigger Collider、
       ///      硬障碍数量有界；
        ///   4) 角色 prefab：根激活、无 missing script、无 MonoBehaviour/Collider/Rigidbody、
        ///      组件全在 C2 允许集内、Animator 关闭 root motion 且有 Float 型 "Speed" 参数。
        ///
        /// 不做的事（诚实边界）：不重算"编辑器侧内容指纹"（源场景可能不在磁盘上/已变更），
        /// 因此这里只证 **资源与 manifest 内部一致 + 组件约束**；
        /// "两端同一份内容"由 C3 的同构建 + digest 握手守护。
        /// </summary>
        public static bool ValidateContent()
        {
            StringBuilder summary = new StringBuilder();
            summary.AppendLine("[PMBattleContentBuild] 校验正式内容（只读，不生成、不修改资源）");

            bool ok = false;
            try
            {
                ok = ValidateContentInternal(summary);
            }
            catch (Exception ex)
            {
                summary.AppendLine("  异常：" + ex.GetType().Name + " " + ex.Message);
                ok = false;
            }

            summary.AppendLine(ok ? "  结果：通过" : "  结果：失败");
            if (ok)
            {
                Debug.Log(summary.ToString());
            }
            else
            {
                Debug.LogError(summary.ToString());
            }

            return ok;
        }

        private static bool ValidateContentInternal(StringBuilder summary)
        {
            // ---- 1) 产物存在性
            GameObject mapPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MapPrefabPath);
            if (mapPrefab == null)
            {
                summary.AppendLine("  缺少地图 prefab：" + MapPrefabPath
                                   + "（先执行菜单 Build/Prepare PMNet Battle Content）");
                return false;
            }

            GameObject playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (playerPrefab == null)
            {
                summary.AppendLine("  缺少角色 prefab：" + PlayerPrefabPath
                                   + "（先执行菜单 Build/Prepare PMNet Battle Content）");
                return false;
            }

            TextAsset manifestAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestAssetPath);
            if (manifestAsset == null)
            {
                summary.AppendLine("  缺少 manifest：" + ManifestAssetPath
                                   + "（manifest 是唯一的内容已就绪发布信号，缺它就等于没就绪）");
                return false;
            }

            // ---- 2) manifest 强校验（复用 C2 的规则，绝不各写一份）
            PMBattleContentManifest manifest;
            string manifestError;
            if (!PMBattleContentManifest.TryParseJson(manifestAsset.text, out manifest, out manifestError))
            {
                summary.AppendLine("  manifest 校验失败：" + manifestError);
                return false;
            }

            summary.AppendLine("  " + manifest.Describe());

            // 资源键必须与实际文件一一对应（manifest 说 key，磁盘就得有那个 key）。
            if (!string.Equals(manifest.mapResource, PMBattleContentManifest.MapResourceKey,
                               StringComparison.Ordinal)
                || !string.Equals(manifest.playerResource, PMBattleContentManifest.PlayerResourceKey,
                                  StringComparison.Ordinal))
            {
                summary.AppendLine("  manifest 资源键与冻结常量不一致（应当已被 C2 强校验挡住）。");
                return false;
            }

            // ---- 3) 地图 prefab 结构与组件约束
            string error;
            if (!VerifyMapPrefabLoaded(mapPrefab, summary, out error))
            {
                summary.AppendLine("  地图 prefab 校验失败：" + error);
                return false;
            }

            // ---- 4) 角色 prefab 结构与组件约束
            if (!VerifyPlayerPrefabLoaded(playerPrefab, summary, out error))
            {
                summary.AppendLine("  角色 prefab 校验失败：" + error);
                return false;
            }

            return true;
        }

        // ==================================================================== 源场景读取（安全预览）

        /// <summary>预检最多解析多少个脚本 guid（场景里组件上千，但这里只关心脚本引用）。</summary>
        private const int PreflightMaxScriptReferences = 512;

        /// <summary>场景 YAML 里脚本引用的前缀（ForceText 序列化下 MonoBehaviour 的 m_Script）。</summary>
        private const string PreflightScriptReferenceMarker = "m_Script: {fileID: 11500000, guid: ";

        /// <summary>
        /// **打开源场景之前**的脚本预检：从场景文本取所有 `m_Script` 的 guid，只对**工程内**
        /// （`Assets/**.cs`）的脚本解析类型并检查 `[ExecuteInEditMode]`/`[ExecuteAlways]`。
        /// 违规时直接失败 ⇒ **根本不会 OpenScene**，于是"审计发生在旧脚本 Awake 之前"是真的，
        /// 而不是"打开之后才发现它已经跑过了"（这正是契约允许的"另一经验证不会执行旧游戏 Awake
        /// 的编辑模式方案"里"先验证后执行"的那一半）。
        ///
        /// 边界（诚实说明）：
        ///   · 只对工程内 `.cs` 下结论；包/内置包脚本与 Assets 下的非 `.cs` 资产只计数，
        ///     由打开后的运行时审计按来源分桶做权威判定；
        ///   · 若场景不是 ForceText 序列化（读不到脚本引用），预检**不假装成功**：明说不可用，
        ///     由打开后的运行时审计兜底；
        ///   · 解析不到 guid 的脚本引用（脚本已删除 ⇒ missing script）不可能执行任何东西，只计数。
        /// </summary>
        private static bool PreflightProjectEditModeAudit(string assetPath, StringBuilder summary,
                                                          out string error)
        {
            error = null;

            string absolute = AbsolutePath(assetPath);
            string text;
            try
            {
                if (!File.Exists(absolute))
                {
                    error = "源场景文件不存在：" + assetPath + "（磁盘路径 " + absolute + "）";
                    return false;
                }

                text = File.ReadAllText(absolute, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                summary.AppendLine("  预检跳过（读取源场景文本失败：" + ex.GetType().Name
                                   + "）：改由打开场景后的运行时审计把关。");
                return true;
            }

            List<string> guids = new List<string>();
            int cursor = 0;
            while (cursor < text.Length && guids.Count < PreflightMaxScriptReferences)
            {
                int hit = text.IndexOf(PreflightScriptReferenceMarker, cursor, StringComparison.Ordinal);
                if (hit < 0)
                {
                    break;
                }

                int start = hit + PreflightScriptReferenceMarker.Length;
                int end = text.IndexOf('}', start);
                if (end < 0)
                {
                    break;
                }

                string guid = text.Substring(start, end - start).Trim();
                if (IsHex32(guid))
                {
                    AddOnce(guids, guid);
                }

                cursor = end;
            }

            if (guids.Count == 0)
            {
                summary.AppendLine("  预检（打开场景之前）：未从场景文本读到脚本引用（可能不是 ForceText"
                                   + " 序列化，或场景里没有 MonoBehaviour）：打开前的脚本门不可用，"
                                   + "改由打开后的运行时审计把关。");
                return true;
            }

            List<string> offenders = new List<string>();
            List<string> projectFlagged = new List<string>();
            int projectReferences = 0;
            int packageReferences = 0;
            int undecidableReferences = 0;
            int unresolvedReferences = 0;

            for (int i = 0; i < guids.Count; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path))
                {
                    unresolvedReferences++;     // 脚本已删除 ⇒ missing script，不可能执行任何东西
                    continue;
                }

                if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    packageReferences++;        // 包/内置包脚本：交给运行时审计按来源分桶
                    continue;
                }

                if (!path.EndsWith(".cs", StringComparison.Ordinal))
                {
                    undecidableReferences++;    // Assets 下的 dll 之类：预检不下结论
                    continue;
                }

                projectReferences++;

                MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                Type type = script == null ? null : script.GetClass();
                if (type == null)
                {
                    offenders.Add(path + "（工程内脚本无法解析为类型：未编译？）");
                    continue;
                }

                bool editMode = Attribute.IsDefined(type, typeof(ExecuteInEditMode))
                                || Attribute.IsDefined(type, typeof(ExecuteAlways));
                if (!editMode)
                {
                    continue;
                }

                if (!Contains(EditModeToleratedScriptNames, type.Name))
                {
                    offenders.Add(type.Name + " <- " + path);
                    continue;
                }

                AddOnce(projectFlagged, type.Name);
            }

            if (offenders.Count > 0)
            {
                error = "**打开源场景之前**的脚本预检失败：工程内脚本带编辑模式标记且不在容忍白名单："
                        + Join(offenders)
                        + "。加载这张场景会让它们的 Awake/OnEnable 在编辑期执行（可能带旧玩法副作用）。"
                        + "请先确认这些脚本的编辑模式行为，再决定是否加入 EditModeToleratedScriptNames；"
                        + "本文件不会在未确认的情况下打开场景。";
                return false;
            }

            summary.AppendLine("  预检（**打开场景之前**）：脚本引用="
                               + guids.Count.ToString(CultureInfo.InvariantCulture)
                               + "（工程内 .cs=" + projectReferences.ToString(CultureInfo.InvariantCulture)
                               + "，包/内置包=" + packageReferences.ToString(CultureInfo.InvariantCulture)
                               + "，工程内非 .cs=" + undecidableReferences.ToString(CultureInfo.InvariantCulture)
                               + "，未解析/missing script="
                               + unresolvedReferences.ToString(CultureInfo.InvariantCulture)
                               + "）；工程内带编辑模式标记且命中白名单={"
                               + (projectFlagged.Count == 0 ? "空" : Join(projectFlagged))
                               + "}（白名单={" + Join(new List<string>(EditModeToleratedScriptNames)) + "}）");
            return true;
        }

        /// <summary>是否是 32 位十六进制的 guid（避免把 YAML 里的其它 token 当成 guid）。</summary>
        private static bool IsHex32(string value)
        {
            if (value == null || value.Length != 32)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 源场景的读取作用域：Additive 打开（或用已打开的那一份）→ 审计编辑模式脚本 →
        /// Dispose 时关闭自己打开的那一份。
        ///
        /// 三条不许破的边界：
        ///   · 只 Close **自己打开的**场景（用户本来开着的场景绝不动，其脏状态因此不受影响）；
        ///   · 不 Save（源场景一个字节都不写）；
        ///   · activeScene / Selection 由调用方 finally 还原（这里只负责开/关）。
        /// </summary>
        private sealed class SourceSceneScope : IDisposable
        {
            private readonly Scene _scene;
            private readonly bool _openedByUs;
            private bool _disposed;

            private SourceSceneScope(Scene scene, bool openedByUs)
            {
                _scene = scene;
                _openedByUs = openedByUs;
            }

            public Scene Scene { get { return _scene; } }

            public static SourceSceneScope Open(string assetPath, StringBuilder summary, out string error)
            {
                error = null;

                Scene existing = SceneManager.GetSceneByPath(assetPath);
                if (existing.IsValid() && existing.isLoaded)
                {
                    // 用户已经开着这张场景：直接用，不重复打开（重复 Additive 打开会报错）。
                    summary.AppendLine("  源场景已打开，直接读取（不重复加载、不关闭、不保存）");

                    // 脏场景必须拒绝：此时磁盘 SHA256 代表不了内存里被读取到的内容，用它算 digest
                    // 会产出"不可追溯"的指纹。这条检查原先只加在"由我们打开"的分支上 —— 恰好加在
                    // 不会失败的一侧（漏掉了最可能命中它的路径），故补到这里。
                    if (existing.isDirty)
                    {
                        error = "源场景当前已打开且有**未保存改动**：磁盘 SHA256 无法代表实际被读取的内容，"
                                + "用它算出的 digest 不可追溯。请先保存（Ctrl+S）或关闭该场景，再重新执行。";
                        return null;
                    }

                    if (!AuditEditModeScripts(existing, summary, out error))
                    {
                        return null;
                    }

                    return new SourceSceneScope(existing, false);
                }

                // 打开之前的脚本预检：违规时**根本不打开场景**（避免其编辑模式回调先跑起来）。
                if (!PreflightProjectEditModeAudit(assetPath, summary, out error))
                {
                    return null;
                }

                Scene opened;
                try
                {
                    opened = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
                }
                catch (Exception ex)
                {
                    error = "OpenScene 异常：" + ex.GetType().Name + " " + ex.Message;
                    return null;
                }

                if (!opened.IsValid() || !opened.isLoaded)
                {
                    error = "OpenScene 返回了无效场景（路径=" + assetPath + "）";
                    return null;
                }

                summary.AppendLine("  已 Additive 打开源场景用于只读预览：" + assetPath);

                if (!AuditEditModeScripts(opened, summary, out error))
                {
                    EditorSceneManager.CloseScene(opened, true);
                    return null;
                }

                if (opened.isDirty)
                {
                    // 不该发生（刚打开的编辑期场景不会是脏的），但一旦发生说明"磁盘 SHA"不能代表内容。
                    error = "刚打开的源场景处于脏状态：磁盘 SHA 无法代表实际内容，拒绝用不一致的指纹烘焙。";
                    EditorSceneManager.CloseScene(opened, true);
                    return null;
                }

                return new SourceSceneScope(opened, true);
            }

            /// <summary>
            /// 编辑模式脚本审计：源场景里带 `[ExecuteInEditMode]`/`[ExecuteAlways]` 的组件按
            /// **脚本来源**分桶：
            ///   · 工程内（`Assets/**`）：必须命中容忍白名单，否则**显式失败**；
            ///   · 包 / 引擎自带（`Packages/**`、`Library/PackageCache/**`、`UnityEngine.*` 程序集）：
            ///     记入摘要但不判失败 —— 它们是 Unity 自带组件的编辑模式行为（ugui 的 `Image`/
            ///     `Text`/`CanvasScaler`/`Button`/`Slider` 等），不执行玩法逻辑；
            ///   · 来源无法判定：fail closed。
            ///
            /// 为何不能只看"是否带编辑模式标记"：源场景里有 32×`Image` / 14×`Text` /
            /// 4×`CanvasScaler` / 3×`Slider` / 2×`Button`，而 ugui 的 `Graphic`/`CanvasScaler`/
            /// `Selectable` 带 `[ExecuteAlways]`（`Inherited=true`）⇒ 旧实现会把它们全当违规项，
            /// 源场景永远过不了审计 ⇒ 烘焙恒失败。
            /// </summary>
            private static bool AuditEditModeScripts(Scene scene, StringBuilder summary, out string error)
            {
                error = null;

                List<string> projectFound = new List<string>();
                List<string> packageFound = new List<string>();
                List<string> offenders = new List<string>();
                int inspected = 0;

                GameObject[] roots = scene.GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                {
                    Component[] components = roots[i].GetComponentsInChildren<Component>(true);
                    for (int c = 0; c < components.Length; c++)
                    {
                        Component component = components[c];
                        if (component == null)
                        {
                            continue;   // missing script：本场景读取不依赖它们
                        }

                        inspected++;

                        Type type = component.GetType();
                        bool editMode = Attribute.IsDefined(type, typeof(ExecuteInEditMode))
                                        || Attribute.IsDefined(type, typeof(ExecuteAlways));
                        if (!editMode)
                        {
                            continue;
                        }

                        string detail;
                        EditModeScriptOrigin origin = ClassifyEditModeScriptOrigin(type, component, out detail);
                        string describe = type.Name + "(" + type.Namespace + ") <- " + detail;

                        if (origin == EditModeScriptOrigin.PackageOrEngine)
                        {
                            AddOnce(packageFound, type.Name);
                            continue;
                        }

                        if (origin == EditModeScriptOrigin.Project)
                        {
                            if (!Contains(EditModeToleratedScriptNames, type.Name))
                            {
                                offenders.Add(describe);
                                continue;
                            }

                            AddOnce(projectFound, type.Name);
                            continue;
                        }

                        offenders.Add(describe + " [来源无法判定：既不是工程内脚本，也不是 Unity 包/引擎程序集]");
                    }
                }

                if (offenders.Count > 0)
                {
                    error = "源场景含**未审计**的编辑模式脚本：" + Join(offenders)
                            + "。这会违反「加载源场景不执行旧游戏 Awake」的前提 —— "
                            + "请人工确认后再把它加入容忍白名单（工程内脚本，见 EditModeToleratedScriptNames）。";
                    return false;
                }

                summary.AppendLine("  编辑模式脚本审计（打开后运行时复核）：检查组件 "
                                   + inspected.ToString(CultureInfo.InvariantCulture) + " 个；"
                                   + "工程内带编辑模式标记 = {"
                                   + (projectFound.Count == 0 ? "空" : Join(projectFound)) + "}"
                                   + "（白名单 = {" + Join(new List<string>(EditModeToleratedScriptNames)) + "}）；"
                                   + "Unity 包/引擎自带 = {"
                                   + (packageFound.Count == 0 ? "空" : Join(packageFound)) + "}"
                                   + "（Unity 自带组件的编辑模式行为，不执行玩法逻辑；本文件从不 Save 场景）；"
                                   + "旧玩法脚本无编辑模式标记 ⇒ 不会执行其 Awake/OnEnable");
                return true;
            }

            /// <summary>全局随机态是否被改动（"不用全局 Random"的运行时证据，进摘要）。</summary>
            public void AppendRandomStateNote(StringBuilder summary, UnityEngine.Random.State before)
            {
                bool changed = !before.Equals(UnityEngine.Random.state);
                summary.AppendLine("  UnityEngine.Random.state 未被改动=" + (changed ? "否（异常，请核查）" : "是"));
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                if (_openedByUs && _scene.IsValid() && _scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(_scene, true);
                }
            }
        }

        // ==================================================================== 布局（确定性复刻）

        /// <summary>一个实例化落点（= 源码循环里的每次 MyInstantiate 调用）。</summary>
        private sealed class Placement
        {
            public string Category;      // "floor"/"wall"/"obstacle"/"grass"/"tree"
            public int TemplateIndex;    // 在对应模板数组里的下标
            public float LocalX;
            public float LocalY;
            public float LocalZ;
            public float RotX;
            public float RotY;
            public float RotZ;
            public float RotW;

            public string CanonicalLine(int ordinal)
            {
                return "placement[" + ordinal.ToString(CultureInfo.InvariantCulture) + "]"
                       + " cat=" + Category
                       + " idx=" + TemplateIndex.ToString(CultureInfo.InvariantCulture)
                       + " pos=(" + Num(LocalX) + "," + Num(LocalY) + "," + Num(LocalZ) + ")"
                       + " rot=(" + Num(RotX) + "," + Num(RotY) + "," + Num(RotZ) + "," + Num(RotW) + ")";
            }
        }

        /// <summary>
        /// 局部确定性 PRNG（xorshift32）。
        ///
        /// 为什么不用 UnityEngine.Random：契约要求"不用全局随机"（旧链地图不可复现的根因就是
        /// 全局未播种随机）；也不允许改全局态。这里用自带状态，种子固定 0x52444301。
        /// 语义对齐 Unity 的整数重载：<c>Range(min, max)</c> 取 [min, max)，max&lt;=min 返回 min。
        /// </summary>
        private struct DeterministicRng
        {
            private uint _state;

            public DeterministicRng(int seed)
            {
                // xorshift 状态不能为 0；契约种子非 0，这里再兜一层。
                _state = seed == 0 ? 0x9E3779B9u : unchecked((uint)seed);
            }

            public int Range(int minInclusive, int maxExclusive)
            {
                if (maxExclusive <= minInclusive)
                {
                    return minInclusive;
                }

                uint span = unchecked((uint)(maxExclusive - minInclusive));
                return minInclusive + (int)(NextUInt() % span);
            }

            private uint NextUInt()
            {
                uint x = _state;
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                _state = x;
                return x;
            }
        }

        /// <summary>
        /// 按 <c>ScenseBuildLogic.InitData()</c> 的循环结构与随机数消费顺序复刻布局。
        ///
        /// 与源码逐处对齐的地方（读代码时最容易改坏的三处）：
        ///   · `maps[i,j]==1`（草丛）分支里实例化被注释掉，但 `Random.Range(0,Grasses.Length)`
        ///     **仍然被消费一次** —— 必须照消费，否则后续所有落点整体错位；
        ///   · 墙壁 `if (i % 3 == 0) random = walll - 1`（C# 负数取模：只有 -15..15 的 3 倍数命中）；
        ///   · 树的两处循环边界不同：i 循环用 `-mapx/2-1`，j 循环用 `-mapx/2`（源码原样，不改）。
        /// </summary>
        private static bool BuildLayout(ScenseBuildLogic logic, out List<Placement> placements,
                                        out string error)
        {
            placements = null;
            error = null;

            int mapx = logic.mapx;
            int mapy = logic.mapy;

            if (logic.map2 == null)
            {
                error = "ScenseBuildLogic.map2 为 null（源类字段初始化失败）";
                return false;
            }

            int rows = logic.map2.GetLength(0);
            int cols = logic.map2.GetLength(1);
            if (rows < mapx || cols < mapy)
            {
                error = "map2 维度 " + rows.ToString(CultureInfo.InvariantCulture) + "×"
                        + cols.ToString(CultureInfo.InvariantCulture) + " 小于 mapx/mapy "
                        + mapx.ToString(CultureInfo.InvariantCulture) + "×"
                        + mapy.ToString(CultureInfo.InvariantCulture)
                        + "（契约要求维持真实的 33×21 循环范围，不擅自扩大）";
                return false;
            }

            if (logic.floors == null || logic.floors.Length == 0
                || logic.walls == null || logic.walls.Length < 2
                || logic.obstacles == null || logic.obstacles.Length == 0
                || logic.Grasses == null
                || logic.trees == null || logic.trees.Length < 3)
            {
                error = "模板数组为空或长度不足以支撑源码的随机上界"
                        + "（floors>0 / walls>=2 / obstacles>0 / Grasses 非 null / trees>=3）";
                return false;
            }

            int floorCount = logic.floors.Length;
            int wallCount = logic.walls.Length;
            int obstacleCount = logic.obstacles.Length;
            int grassCount = logic.Grasses.Length;
            int treeCount = logic.trees.Length;

            for (int i = 0; i < logic.floors.Length; i++)
            {
                if (logic.floors[i] == null) { error = "floors[" + i + "] 为 null"; return false; }
            }
            for (int i = 0; i < logic.walls.Length; i++)
            {
                if (logic.walls[i] == null) { error = "walls[" + i + "] 为 null"; return false; }
            }
            for (int i = 0; i < logic.obstacles.Length; i++)
            {
                if (logic.obstacles[i] == null) { error = "obstacles[" + i + "] 为 null"; return false; }
            }
            for (int i = 0; i < logic.trees.Length; i++)
            {
                if (logic.trees[i] == null) { error = "trees[" + i + "] 为 null"; return false; }
            }

            DeterministicRng rng = new DeterministicRng(BakeSeed);
            List<Placement> list = new List<Placement>(8192);

            // ---- 1) 33×21 网格（含障碍 + 其下地板）
            for (int i = 0; i < mapx; i++)
            {
                for (int j = 0; j < mapy; j++)
                {
                    int cell = logic.map2[i, j];

                    if (cell == 0 || cell == 1)
                    {
                        int r = rng.Range(0, floorCount);
                        list.Add(Make("floor", r, i - mapx / 2, 0.01f, j - mapy / 2));
                    }

                    if (cell == 1)
                    {
                        // 源码：Grasses 的实例化被注释掉，但随机数照样消费一次（顺序不能省）。
                        rng.Range(0, grassCount);
                    }

                    if (cell == 2)
                    {
                        int r = rng.Range(0, obstacleCount);
                        list.Add(Make("obstacle", r, i - mapx / 2, 0f, j - mapy / 2));

                        int r2 = rng.Range(0, floorCount);
                        list.Add(Make("floor", r2, i - mapx / 2, 0.01f, j - mapy / 2));
                    }

                    if (cell == 5 || cell == 6)
                    {
                        // 金库格：map2 里不存在（实测 0 处）。RedSaveBox/BlueSaveBox 是**外部 prefab**，
                        // 其内部组件未经审计，直接实例化会把未知内容带进"纯地图"。
                        // 首图不含金库 ⇒ 显式失败，而不是静默丢掉这两个格子。
                        error = "map2 在第 (" + i.ToString(CultureInfo.InvariantCulture) + ","
                                + j.ToString(CultureInfo.InvariantCulture) + ") 格出现金库值 " + cell
                                + "：金库（RedSaveBox/BlueSaveBox）是外部 prefab，其组件未纳入本轮的"
                                + "纯几何审计，故 C1 明确失败而不是静默丢弃。";
                        return false;
                    }
                }
            }

            // ---- 2) 边界墙（四条边；源码按 i、j 两个循环，随机数各自消费）
            for (int i = -mapx / 2 - 1; i <= mapx / 2 + 1; i++)
            {
                int r = rng.Range(0, wallCount - 1);
                if (i % 3 == 0)
                {
                    r = wallCount - 1;
                }

                list.Add(Make("wall", r, i, 0f, -mapy / 2 - 1));
                list.Add(Make("wall", r, i, 0f, mapy / 2 + 1));
            }

            for (int j = -mapy / 2; j < mapy / 2 + 1; j++)
            {
                int r = rng.Range(0, wallCount - 1);
                list.Add(Make("wall", r, -mapx / 2 - 1, 0f, j));
                list.Add(Make("wall", r, mapx / 2 + 1, 0f, j));
            }

            // ---- 3) 外围树（两处循环边界与源码逐字一致）
            for (int i = -mapx / 2 - 1; i <= mapx / 2 + 1; i++)
            {
                int r = rng.Range(0, treeCount - 2);
                if (i % 3 == 0)
                {
                    r = treeCount - 2 + rng.Range(0, 2);
                }

                list.Add(Make("tree", r, i, 0f, -mapy / 2 - rng.Range(3, 10)));
                list.Add(Make("tree", r, i, 0f, mapy / 2 + rng.Range(3, 10)));
            }

            for (int j = -mapy / 2; j < mapy / 2 + 1; j++)
            {
                int r = rng.Range(0, treeCount - 2);
                list.Add(Make("tree", r, -mapx / 2 - rng.Range(3, 10), 0f, j));
                list.Add(Make("tree", r, mapx / 2 + rng.Range(3, 10), 0f, j));
            }

            // ---- 4) 边界外补地板
            for (int i = 0; i < mapx * 2; i++)
            {
                for (int j = 0; j < mapy * 2; j++)
                {
                    bool outside = i - mapx < -mapx / 2 - 1
                                   || i - mapx > mapx / 2 + 1
                                   || j - mapy < -mapy / 2 - 1
                                   || j - mapy > mapy / 2 + 1;
                    if (!outside)
                    {
                        continue;
                    }

                    int r = rng.Range(0, floorCount);
                    list.Add(Make("floor", r, i - mapx, 0.01f, j - mapy));
                }
            }

            placements = list;
            return true;
        }

        private static Placement Make(string category, int templateIndex, float x, float y, float z)
        {
            Placement p = new Placement();
            p.Category = category;
            p.TemplateIndex = templateIndex;
            p.LocalX = x;
            p.LocalY = y;
            p.LocalZ = z;
            p.RotX = 0f;
            p.RotY = 0f;
            p.RotZ = 0f;
            p.RotW = 1f;
            return p;
        }

        private static bool SamePlacements(List<Placement> a, List<Placement> b)
        {
            if (a == null || b == null || a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                Placement x = a[i];
                Placement y = b[i];
                if (!string.Equals(x.Category, y.Category, StringComparison.Ordinal)
                    || x.TemplateIndex != y.TemplateIndex
                    || !x.LocalX.Equals(y.LocalX) || !x.LocalY.Equals(y.LocalY) || !x.LocalZ.Equals(y.LocalZ)
                    || !x.RotX.Equals(y.RotX) || !x.RotY.Equals(y.RotY)
                    || !x.RotZ.Equals(y.RotZ) || !x.RotW.Equals(y.RotW))
                {
                    return false;
                }
            }

            return true;
        }

        private static void AccumulateLayoutCounts(StringBuilder summary, List<Placement> placements)
        {
            int floor = 0, wall = 0, obstacle = 0, grass = 0, tree = 0, other = 0;
            for (int i = 0; i < placements.Count; i++)
            {
                switch (placements[i].Category)
                {
                    case "floor": floor++; break;
                    case "wall": wall++; break;
                    case "obstacle": obstacle++; break;
                    case "grass": grass++; break;
                    case "tree": tree++; break;
                    default: other++; break;
                }
            }

            summary.AppendLine("  布局（同种子两次生成一致）：total=" + placements.Count.ToString(CultureInfo.InvariantCulture)
                               + " floor=" + floor.ToString(CultureInfo.InvariantCulture)
                               + " wall=" + wall.ToString(CultureInfo.InvariantCulture)
                               + " obstacle=" + obstacle.ToString(CultureInfo.InvariantCulture)
                               + " grass=" + grass.ToString(CultureInfo.InvariantCulture)
                               + " tree=" + tree.ToString(CultureInfo.InvariantCulture)
                               + " other=" + other.ToString(CultureInfo.InvariantCulture));
        }

        // ==================================================================== 摘要流

        /// <summary>
        /// 构造 contentDigest 的输入流（纯文本、确定性、可人工复核）。
        /// 结构见文件头"摘要（digest）"段。
        /// </summary>
        private static string BuildDigestStream(ScenseBuildLogic logic, List<Placement> placements,
                                               GameObject groundPlane,
                                               string sceneSha, string logicSha, string playerSha,
                                               StringBuilder summary)
        {
            StringBuilder sb = new StringBuilder(1 << 20);

            sb.Append("domain=").Append(DigestDomain).Append('\n');
            sb.Append("formatVersion=").Append(PMBattleContentManifest.ExpectedFormatVersion
                      .ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("mapId=").Append(PMBattleContentManifest.ExpectedMapId).Append('\n');
            sb.Append("seed=").Append(PMBattleContentManifest.ExpectedSeed
                      .ToString(CultureInfo.InvariantCulture)).Append('\n');

            // 源文件 SHA：把"引用关系"钉死（模板/材质/mesh 的指向只要变了，SHA 就变）。
            sb.Append("source[0]=").Append(SourceScenePath).Append("|sha256=").Append(sceneSha).Append('\n');
            sb.Append("source[1]=").Append(SourceLogicPath).Append("|sha256=").Append(logicSha).Append('\n');
            sb.Append("source[2]=").Append(SourcePlayerPath).Append("|sha256=").Append(playerSha).Append('\n');

            // 布局参数 + 容器口径（世界等价存放：tile 局部值就是世界值）。
            sb.Append("layout.mapx=").Append(logic.mapx.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("layout.mapy=").Append(logic.mapy.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("layout.tileSpace=world|container=identity|tile.local==tile.world\n");

            // 模板调色板：逐个模板的规范化签名（结构 + 变换 + mesh/材质引用 + 碰撞体字段）。
            AppendPaletteSection(sb, "floors", logic.floors, summary);
            AppendPaletteSection(sb, "walls", logic.walls, summary);
            AppendPaletteSection(sb, "obstacles", logic.obstacles, summary);
            AppendPaletteSection(sb, "Grasses", logic.Grasses, summary);
            AppendPaletteSection(sb, "trees", logic.trees, summary);

            // 地面（唯一的真实"地板碰撞"来源）。
            sb.Append("ground.source=").Append(GroundSourcePath).Append('\n');
            AppendCanonicalNode(sb, groundPlane, "ground", 0);

            // 落点列表（顺序即源码调用顺序，位置不排序）。
            sb.Append("placements.count=").Append(placements.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i < placements.Count; i++)
            {
                sb.Append(placements[i].CanonicalLine(i)).Append('\n');
            }

            return sb.ToString();
        }

        private static void AppendPaletteSection(StringBuilder sb, string label, GameObject[] templates,
                                                StringBuilder summary)
        {
            sb.Append("palette.").Append(label).Append(".count=")
              .Append(templates.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');

            for (int i = 0; i < templates.Length; i++)
            {
                GameObject template = templates[i];
                sb.Append("palette.").Append(label).Append('[')
                  .Append(i.ToString(CultureInfo.InvariantCulture)).Append("].source=scene:"
                  + SceneFileIdOf(template)).Append('\n');
                AppendCanonicalNode(sb, template, label + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", 0);
            }

            if (summary != null)
            {
                summary.AppendLine("  调色板 " + label + "：模板数="
                                   + templates.Length.ToString(CultureInfo.InvariantCulture)
                                   + "（含子物体共 "
                                   + CountHierarchy(templates).ToString(CultureInfo.InvariantCulture) + " 个节点）");
            }
        }

        private static int CountHierarchy(GameObject[] templates)
        {
            int total = 0;
            for (int i = 0; i < templates.Length; i++)
            {
                total += templates[i].transform.hierarchyCount;
            }

            return total;
        }

        /// <summary>
        /// 规范化节点签名（唯一一份"逐字段"序列化口径）：
        ///   节点行（名字/激活/层/tag）→ 局部变换 → 组件字段（按类型名排序）→ 子节点（兄弟序）。
        /// 三个用途共用它：摘要输入、深拷贝正确性比对、回读校验比对。
        /// </summary>
        private static void AppendCanonicalNode(StringBuilder sb, GameObject go, string label, int depth)
        {
            sb.Append("node ").Append(label)
              .Append(" name=").Append(Quote(go.name))
              .Append(" active=").Append(go.activeSelf ? "1" : "0")
              .Append(" layer=").Append(go.layer.ToString(CultureInfo.InvariantCulture))
              .Append(" tag=").Append(Quote(go.tag))
              .Append('\n');

            Transform t = go.transform;
            sb.Append("  tf pos=").Append(Vec(t.localPosition))
              .Append(" rot=").Append(Quat(t.localRotation))
              .Append(" scale=").Append(Vec(t.localScale))
              .Append('\n');

            Component[] components = go.GetComponents<Component>();
            List<string> lines = new List<string>(components.Length);
            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                if (c == null)
                {
                    lines.Add("comp <missing-script>");
                    continue;
                }

                if (c is Transform)
                {
                    continue;   // 变换已单独输出
                }

                lines.Add("comp " + c.GetType().Name + " " + DumpComponent(c));
            }

            lines.Sort(StringComparer.Ordinal);
            for (int i = 0; i < lines.Count; i++)
            {
                sb.Append("  ").Append(lines[i]).Append('\n');
            }

            for (int i = 0; i < t.childCount; i++)
            {
                AppendCanonicalNode(sb, t.GetChild(i).gameObject,
                                    label + "/" + i.ToString(CultureInfo.InvariantCulture), depth + 1);
            }
        }

        /// <summary>
        /// 把一个组件的**全部序列化字段**规范化成一行（按字段路径排序 ⇒ 与迭代顺序无关）。
        /// 对象引用只输出"资产路径/名字/类型"（**绝不**输出实例 ID：那会跨会话漂移）。
        /// Mesh 字段额外补顶点数与 bounds 作为内容锚（防止 mesh 被换但路径名字没变）。
        /// </summary>
        private static string DumpComponent(Component component)
        {
            SerializedObject so = new SerializedObject(component);
            SerializedProperty it = so.GetIterator();

            List<string> fields = new List<string>(32);
            while (it.Next(true))
            {
                string path = it.propertyPath;
                if (IsNonDeterministicPath(path))
                {
                    continue;
                }

                if (it.propertyType == SerializedPropertyType.ArraySize)
                {
                    continue;
                }

                string value = CanonicalPropertyValue(it);

                if (string.Equals(path, "m_Mesh", StringComparison.Ordinal)
                    && it.propertyType == SerializedPropertyType.ObjectReference)
                {
                    value += MeshContentAnchor(it.objectReferenceValue as Mesh);
                }

                fields.Add(path + "=" + value);
            }

            fields.Sort(StringComparer.Ordinal);

            StringBuilder sb = new StringBuilder(512);
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(';');
                }

                sb.Append(fields[i]);
            }

            return sb.ToString();
        }

        private static string CanonicalPropertyValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return property.boolValue ? "1" : "0";
                case SerializedPropertyType.Float:
                    return Num(property.floatValue);
                case SerializedPropertyType.String:
                    return Quote(property.stringValue);
                case SerializedPropertyType.Color:
                    Color color = property.colorValue;
                    return "(" + Num(color.r) + "," + Num(color.g) + "," + Num(color.b) + "," + Num(color.a) + ")";
                case SerializedPropertyType.ObjectReference:
                    return ObjectReferenceIdentity(property.objectReferenceValue);
                case SerializedPropertyType.LayerMask:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Enum:
                    return property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2:
                    return Vec(property.vector2Value);
                case SerializedPropertyType.Vector3:
                    return Vec(property.vector3Value);
                case SerializedPropertyType.Vector4:
                    return Vec(property.vector4Value);
                case SerializedPropertyType.Rect:
                    Rect rect = property.rectValue;
                    return "(" + Num(rect.x) + "," + Num(rect.y) + "," + Num(rect.width) + "," + Num(rect.height) + ")";
                case SerializedPropertyType.ArraySize:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Character:
                    return ((int)property.intValue).ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.AnimationCurve:
                    return "<animation-curve>";
                case SerializedPropertyType.Bounds:
                    Bounds bounds = property.boundsValue;
                    return "(" + Vec(bounds.center) + "," + Vec(bounds.extents) + ")";
                case SerializedPropertyType.Gradient:
                    return "<gradient>";
                case SerializedPropertyType.Quaternion:
                    return Quat(property.quaternionValue);
                default:
                    return "<" + property.propertyType.ToString() + ">";
            }
        }

        /// <summary>
        /// 对象引用的**确定性**身份：资产用"资产路径|类型|名字"，场景内对象用"local|类型|名字"。
        /// 刻意不使用 GetInstanceID（每次加载都不同）也不使用 AssetDatabase 的本地子资产编号
        /// （其跨会话稳定性不由我们控制，会破坏"重复生成摘要稳定"）。
        /// </summary>
        private static string ObjectReferenceIdentity(UnityEngine.Object o)
        {
            if (o == null)
            {
                return "<null>";
            }

            string assetPath = AssetDatabase.GetAssetPath(o);
            if (string.IsNullOrEmpty(assetPath))
            {
                return "local|" + o.GetType().Name + "|" + Quote(o.name);
            }

            return "asset|" + assetPath + "|" + o.GetType().Name + "|" + Quote(o.name);
        }

        /// <summary>mesh 内容锚：顶点数 + bounds（不用 localID，避免跨会话漂移）。</summary>
        private static string MeshContentAnchor(Mesh mesh)
        {
            if (mesh == null)
            {
                return "";
            }

            Bounds b;
            try
            {
                b = mesh.bounds;
            }
            catch (Exception)
            {
                return "|mesh=unreadable";
            }

            return "|mesh.verts=" + mesh.vertexCount.ToString(CultureInfo.InvariantCulture)
                   + "|mesh.bounds=" + Vec(b.center) + "/" + Vec(b.extents);
        }

        /// <summary>跨会话不稳定/与内容无关的字段路径（不参与摘要，也不参与拷贝）。</summary>
        private static bool IsNonDeterministicPath(string path)
        {
            return string.Equals(path, "m_GameObject", StringComparison.Ordinal)
                   || string.Equals(path, "m_Script", StringComparison.Ordinal)
                   || string.Equals(path, "m_PrefabInstance", StringComparison.Ordinal)
                   || string.Equals(path, "m_PrefabAsset", StringComparison.Ordinal)
                   || string.Equals(path, "m_CorrespondingSourceObject", StringComparison.Ordinal)
                   || path.StartsWith("m_PrefabInternal", StringComparison.Ordinal);
        }

        // ==================================================================== 地图层级搭建

        /// <summary>
        /// 搭建"纯地图"层级：
        ///   BattleMapV1(identity)
        ///     ├── Ground(identity) → Plane(scale 10)   ← 源场景 HYLDGameTatal/MAP/Plane（地面 MeshCollider）
        ///     └── MAP(identity)    → tile_00001 …      ← 按布局逐格深拷贝模板
        ///
        /// 为什么容器用 identity：源码 `SetParent(MAP)`（worldPositionStays=true）把 `3D` 的 0.5
        /// 缩放抵消掉了，tile 的世界变换就是 `(gridPos, identity, templateScale)`；
        /// 这里按世界等价直接存放，不做 0.5×2 往返（做错就会把地图缩成一半）。
        /// </summary>
        private static GameObject BuildMapHierarchy(ScenseBuildLogic logic, List<Placement> placements,
                                                   GameObject groundPlane, StringBuilder summary,
                                                   out string error)
        {
            error = null;

            GameObject root = new GameObject(MapRootName);
            root.layer = 0;

            GameObject groundContainer = new GameObject(GroundContainerName);
            groundContainer.transform.SetParent(root.transform, false);

            GameObject groundClone = new GameObject(groundPlane.name);
            groundClone.transform.SetParent(groundContainer.transform, false);
            CopyTransformValues(groundPlane.transform, groundClone.transform);
            groundClone.layer = groundPlane.layer;
            TrySetTag(groundClone, groundPlane.tag, summary);
            groundClone.SetActive(groundPlane.activeSelf);

            List<string> stripped;
            if (!CopyComponentSet(groundPlane, groundClone, true, out stripped))
            {
                error = "地面对象含白名单外组件或字段拷贝失败：" + Join(stripped)
                        + "（对象=\"" + groundPlane.name + "\"）；纯地图必须只含几何/渲染/碰撞。";
                return null;
            }

            GameObject mapContainer = new GameObject(MapContainerName);
            mapContainer.transform.SetParent(root.transform, false);

            int ordinal = 0;
            int copiedNodes = 0;
            for (int i = 0; i < placements.Count; i++)
            {
                Placement placement = placements[i];
                ordinal++;

                GameObject template = ResolveTemplate(logic, placement.Category, placement.TemplateIndex);
                if (template == null)
                {
                    error = "落点 " + ordinal.ToString(CultureInfo.InvariantCulture) + " 解析模板失败："
                            + placement.Category + "[" + placement.TemplateIndex.ToString(CultureInfo.InvariantCulture) + "]";
                    return null;
                }

                string tileName = BuildTileName(placement, ordinal, template);

                GameObject tile = new GameObject(tileName);
                tile.transform.SetParent(mapContainer.transform, false);
                tile.transform.localPosition = new Vector3(placement.LocalX, placement.LocalY, placement.LocalZ);
                tile.transform.localRotation = new Quaternion(placement.RotX, placement.RotY, placement.RotZ,
                                                             placement.RotW);
                tile.transform.localScale = template.transform.localScale;
                tile.layer = template.layer;
                TrySetTag(tile, template.tag, summary);
                tile.SetActive(template.activeSelf);

                if (!CopyComponentSet(template, tile, true, out stripped))
                {
                    error = "模板 \"" + template.name + "\" 含白名单外组件或字段拷贝失败：" + Join(stripped)
                            + "（C2 侧会显式拒绝加载，故这里直接失败）。";
                    return null;
                }

                if (!CopyChildHierarchy(template.transform, tile.transform, true, summary,
                                        ref copiedNodes, out error))
                {
                    return null;
                }

                copiedNodes++;
            }

            summary.AppendLine("  地图层级：tile 根=" + ordinal.ToString(CultureInfo.InvariantCulture)
                               + "，含子物体共 " + copiedNodes.ToString(CultureInfo.InvariantCulture) + " 个节点");
            return root;
        }

        private static GameObject ResolveTemplate(ScenseBuildLogic logic, string category, int index)
        {
            GameObject[] array;
            switch (category)
            {
                case "floor": array = logic.floors; break;
                case "wall": array = logic.walls; break;
                case "obstacle": array = logic.obstacles; break;
                case "grass": array = logic.Grasses; break;
                case "tree": array = logic.trees; break;
                default: return null;
            }

            if (array == null || index < 0 || index >= array.Length)
            {
                return null;
            }

            return array[index];
        }

        private static string BuildTileName(Placement placement, int ordinal, GameObject template)
        {
            string prefix;
            switch (placement.Category)
            {
                case "floor": prefix = "F"; break;
                case "wall": prefix = "W"; break;
                case "obstacle": prefix = "O"; break;
                case "grass": prefix = "G"; break;
                case "tree": prefix = "T"; break;
                default: prefix = "X"; break;
            }

            return prefix + ordinal.ToString("D5", CultureInfo.InvariantCulture) + "_" + template.name;
        }

        /// <summary>
        /// 深拷贝子层（模板子物体 → tile 子物体）：局部变换逐字段复制，组件走白名单。
        /// vector3/quaternion/scale 一律直接复制，不做"统一缩放/统一朝向"之类的简化。
        /// </summary>
        private static bool CopyChildHierarchy(Transform sourceParent, Transform destParent, bool forMap,
                                              StringBuilder summary, ref int copiedNodes, out string error)
        {
            error = null;

            for (int i = 0; i < sourceParent.childCount; i++)
            {
                Transform sourceChild = sourceParent.GetChild(i);
                GameObject sourceGo = sourceChild.gameObject;

                GameObject destGo = new GameObject(sourceGo.name);
                destGo.transform.SetParent(destParent, false);
                CopyTransformValues(sourceChild, destGo.transform);
                destGo.layer = sourceGo.layer;
                TrySetTag(destGo, sourceGo.tag, summary);
                destGo.SetActive(sourceGo.activeSelf);

                List<string> stripped;
                if (!CopyComponentSet(sourceGo, destGo, forMap, out stripped))
                {
                    error = "子物体 \"" + sourceGo.name + "\" 含白名单外组件或字段拷贝失败：" + Join(stripped);
                    return false;
                }

                copiedNodes++;
                if (!CopyChildHierarchy(sourceChild, destGo.transform, forMap, summary, ref copiedNodes,
                                       out error))
                {
                    return false;
                }
            }

            return true;
        }

        private static void CopyTransformValues(Transform source, Transform dest)
        {
            dest.localPosition = source.localPosition;
            dest.localRotation = source.localRotation;
            dest.localScale = source.localScale;
        }

        // ==================================================================== 角色表现烘焙

        /// <summary>
        /// 把源角色 prefab 清理成"干净表现"：
        ///   · 克隆（**不用** PrefabUtility.InstantiatePrefab，避免把改动回写到原件）；
        ///   · 移除 missing script（GameObjectUtility.RemoveMonoBehavioursWithMissingScript）；
        ///   · 只保留 Transform 系 / MeshFilter / MeshRenderer / SkinnedMeshRenderer / Animator / LODGroup；
        ///     其余（旧脚本 / Collider / Rigidbody / AudioSource / Canvas 系 UI / LineRenderer /
        ///     SpriteRenderer / CanvasRenderer …）逐个删除并**按类型计数上报**；
        ///   · 删除旧世界空间 UI 子树 Canvas（它的驱动器就是被删掉的旧脚本）；
        ///   · Animator.applyRootMotion = false（并且校验控制器里确实有 Float 型 "Speed"）。
        /// </summary>
        private static GameObject BuildPlayerHierarchy(GameObject sourcePrefab, StringBuilder summary,
                                                      out string error)
        {
            error = null;

            GameObject clone = UnityEngine.Object.Instantiate(sourcePrefab);
            clone.name = PlayerRootName;

            // ---- 1) missing script（先删，避免后面 Destroy 空脚本组件报错）
            // 注意：GameObject 不是 Component，GetComponentsInChildren<GameObject>() 编不过；
            // 用 Transform 枚举全部节点（含未激活）再取 gameObject。
            int missingRemoved = 0;
            Transform[] allNodes = clone.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < allNodes.Length; i++)
            {
                missingRemoved += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(allNodes[i].gameObject);
            }

            // ---- 2) 删掉旧世界空间 UI 子树（Canvas）：驱动器是被删掉的旧脚本，留着就是死 UI
            int canvasSubtreeRemoved = 0;
            List<string> canvasNotes = new List<string>();
            for (int i = clone.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = clone.transform.GetChild(i);
                if (!string.Equals(child.gameObject.name, "Canvas", StringComparison.Ordinal))
                {
                    continue;
                }

                canvasSubtreeRemoved = child.hierarchyCount;
                canvasNotes.Add("子物体 \"" + child.gameObject.name + "\"（" 
                                + canvasSubtreeRemoved.ToString(CultureInfo.InvariantCulture)
                                + " 个节点，含旧血条/名字/宝石计数等世界空间 UI）");
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            }

            // ---- 3) 白名单外组件逐类型删除 + 计数
            Dictionary<string, int> removedByType = new Dictionary<string, int>(StringComparer.Ordinal);
            List<string> keptNotes = new List<string>();
            int keptComponents = 0;

            for (int i = 0; i < allNodes.Length; i++)
            {
                GameObject node = allNodes[i] == null ? null : allNodes[i].gameObject;

                if (node == null)
                {
                    continue;
                }

                Component[] components = node.GetComponents<Component>();
                for (int c = components.Length - 1; c >= 0; c--)
                {
                    Component component = components[c];
                    if (component == null)
                    {
                        continue;   // 已由 RemoveMonoBehavioursWithMissingScript 处理
                    }

                    if (component is Transform)
                    {
                        continue;   // Transform/RectTransform 是物体的固有组件，不能删
                    }

                    string typeName = component.GetType().Name;
                    if (Contains(PlayerKeptComponentNames, typeName))
                    {
                        keptComponents++;

                        if (!Contains(keptNotes.ToArray(), typeName))
                        {
                            keptNotes.Add(typeName);
                        }

                        Animator animator = component as Animator;
                        if (animator != null)
                        {
                            // 契约：root motion 关闭。源资产本来就是 0，这里显式再写一次并校验。
                            animator.applyRootMotion = false;
                        }

                        continue;
                    }

                    int count;
                    removedByType.TryGetValue(typeName, out count);
                    removedByType[typeName] = count + 1;
                    UnityEngine.Object.DestroyImmediate(component);
                }
            }

            // ---- 4) 校验：确实没有残留（Animator 必须存在且 Speed 参数存在）
            Animator[] animators = clone.GetComponentsInChildren<Animator>(true);
            if (animators.Length == 0)
            {
                error = "清理后的角色表现没有 Animator：C2 的 PMUnityBattlePresentation 需要它驱动 Speed 参数。";
                return null;
            }

            for (int i = 0; i < animators.Length; i++)
            {
                if (animators[i].applyRootMotion)
                {
                    error = "清理后的角色 Animator.applyRootMotion 仍为 true（契约要求关闭）。";
                    return null;
                }

                if (animators[i].runtimeAnimatorController == null)
                {
                    error = "清理后的角色 Animator 没有 runtimeAnimatorController（Speed 参数无从校验）。";
                    return null;
                }
            }

            bool hasSpeed = false;
            AnimatorControllerParameter[] parameters = animators[0].parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (string.Equals(parameters[i].name, "Speed", StringComparison.Ordinal)
                    && parameters[i].type == AnimatorControllerParameterType.Float)
                {
                    hasSpeed = true;
                    break;
                }
            }

            if (!hasSpeed)
            {
                error = "角色 Animator 控制器里没有 Float 型 \"Speed\" 参数："
                        + "C2 会因此完全不驱动动画（契约要求该参数供 C2 使用）。";
                return null;
            }

            // ---- 5) 摘要（"报告"而不是"静默"）
            summary.AppendLine("  角色表现清理：保留组件类型={" + Join(keptNotes) + "}（共 "
                               + keptComponents.ToString(CultureInfo.InvariantCulture) + " 个）");
            summary.AppendLine("  角色表现清理：删除 missing script="
                               + missingRemoved.ToString(CultureInfo.InvariantCulture)
                               + " 个；删除旧 UI 子树="
                               + (canvasSubtreeRemoved == 0 ? "无" : Join(canvasNotes)));
            summary.AppendLine("  角色表现清理：按类型删除组件 = " + DescribeCounts(removedByType));
            summary.AppendLine("  角色表现清理：Animator="
                               + animators.Length.ToString(CultureInfo.InvariantCulture)
                               + "，applyRootMotion=false，Speed(Float) 参数存在，节点数="
                               + clone.transform.hierarchyCount.ToString(CultureInfo.InvariantCulture));

            return clone;
        }

        // ==================================================================== 组件拷贝（逐字段）

        /// <summary>
        /// 把源物体上的组件按白名单拷到目标物体（Transform 不在此列：由调用方单独处理）。
        ///
        /// 白名单判定与"是否允许出现"的语义差异：
        ///   · forMap=true（纯地图）：白名单外**返回 false**（fail closed，C2 侧同样会拒载）；
        ///   · forMap=false（角色）：白名单外**已在 BuildPlayerHierarchy 删掉**，
        ///     这里只是把剩余组件拷过去，因此不会产生 false。
        ///
        /// `offenders` 同时承载两类失败（调用方以"含白名单外组件或字段拷贝失败"汇总上报）：
        ///   · 白名单外的组件类型名；
        ///   · 逐字段拷贝/数组回读失败（前缀 `字段拷贝失败:` / `数组回读失败:` / `数组长度不一致:`）。
        /// 后者是复核新增的：把"字段没拷过去"变成**烘焙时就失败**，而不是等出图后才发现。
        /// </summary>
        private static bool CopyComponentSet(GameObject source, GameObject dest, bool forMap,
                                            out List<string> offenders)
        {
            offenders = new List<string>();

            Component[] components = source.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                {
                    offenders.Add("<missing-script>");
                    continue;
                }

                if (component is Transform)
                {
                    continue;
                }

                if (!IsAllowedComponent(component, forMap))
                {
                    offenders.Add(component.GetType().Name);
                    continue;
                }

                Component copy = dest.AddComponent(component.GetType());
                CopyComponentFields(component, copy, offenders);
            }

            return offenders.Count == 0;
        }

        private static bool IsAllowedComponent(Component component, bool forMap)
        {
            string typeName = component.GetType().Name;

            if (forMap)
            {
                if (Contains(MapAllowedComponentNames, typeName))
                {
                    return true;
                }

                if (component is Collider)
                {
                    return true;    // 任意 Collider（保留真实形状；C2 会区分 trigger/非 trigger）
                }

                Rigidbody rigidbody = component as Rigidbody;
                if (rigidbody != null)
                {
                    return rigidbody.isKinematic;    // 动态刚体一律不允许
                }

                return false;
            }

            return Contains(PlayerKeptComponentNames, typeName);
        }

        /// <summary>
        /// 逐字段拷贝（"逐字段保留"，不是"参数近似"）：
        /// 用 SerializedObject 迭代**全部**序列化属性（含隐藏项），跳过跨会话不稳定/与内容无关的
        /// 引用字段（m_GameObject/m_Prefab*/m_CorrespondingSourceObject/m_Script），
        /// **数组先按源长度建长度、再把元素逐个拷贝**，最后回读比对每个数组的长度。
        ///
        /// 数组分支的位置是刻意的（这是一个**真缺陷的修法**）：`SerializedProperty.propertyType`
        /// 对数组属性本身就是 `ArraySize`，因此原写法「先把 `ArraySize` 跳过、再在 `isArray`
        /// 分支里设 arraySize」里的设长度那段是**不可达**的 —— 结果
        /// `MeshRenderer.m_Materials` / `LODGroup.m_LODs` 这类数组字段永远拷不过去
        /// （地图会变成"有 MeshFilter 没材质"，而旧的回读校验不查材质，于是静默出图）。
        /// 现在把数组一律放在任何 skip 之前处理，并在拷贝后回读长度。
        /// </summary>
        private static void CopyComponentFields(Component source, Component dest, List<string> failures)
        {
            SerializedObject so = new SerializedObject(source);
            SerializedObject dobj = new SerializedObject(dest);

            SerializedProperty it = so.GetIterator();
            while (it.Next(true))
            {
                string path = it.propertyPath;
                if (IsNonDeterministicPath(path))
                {
                    continue;
                }

                // 数组与固定长度缓冲：propertyType 为 ArraySize 的属性就是**数组头**，
                // 必须在任何 skip 之前处理，否则 arraySize 永远设不上（元素也就无从拷贝）。
                if (it.propertyType == SerializedPropertyType.ArraySize)
                {
                    SerializedProperty arrayTarget = dobj.FindProperty(path);
                    if (arrayTarget == null
                        || arrayTarget.propertyType != SerializedPropertyType.ArraySize)
                    {
                        if (it.isArray)
                        {
                            failures.Add("字段拷贝失败:" + source.GetType().Name + ":" + path
                                         + "(目标缺少该数组字段)");
                        }

                        continue;
                    }

                    arrayTarget.arraySize = it.arraySize;   // 元素由后续迭代逐个拷贝
                    continue;
                }

                SerializedProperty target = dobj.FindProperty(path);
                if (target == null || target.propertyType != it.propertyType)
                {
                    continue;
                }

                dobj.CopyFromSerializedProperty(it);
            }

            dobj.ApplyModifiedPropertiesWithoutUndo();

            VerifyArraySizes(so, dobj, source, failures);
        }

        /// <summary>
        /// 拷完回读：每个数组的长度必须与源一致。
        /// 不需要任何人"记得检查"：长度不一致就列入 failures，烘焙直接失败。
        /// </summary>
        private static void VerifyArraySizes(SerializedObject source, SerializedObject dest,
                                            Component sourceComponent, List<string> failures)
        {
            SerializedProperty it = source.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType != SerializedPropertyType.ArraySize)
                {
                    continue;
                }

                string path = it.propertyPath;
                if (IsNonDeterministicPath(path))
                {
                    continue;
                }

                SerializedProperty target = dest.FindProperty(path);
                if (target == null || target.propertyType != SerializedPropertyType.ArraySize)
                {
                    if (it.isArray)
                    {
                        failures.Add("数组回读失败:" + sourceComponent.GetType().Name + ":" + path
                                     + "(目标不存在该数组字段)");
                    }

                    continue;
                }

                if (target.arraySize != it.arraySize)
                {
                    failures.Add("数组长度不一致:" + sourceComponent.GetType().Name + ":" + path
                                 + "(源=" + it.arraySize.ToString(CultureInfo.InvariantCulture)
                                 + " 目标=" + target.arraySize.ToString(CultureInfo.InvariantCulture) + ")");
                }
            }
        }

        // ==================================================================== 保存 prefab（真实 API）

        private static bool SavePrefab(GameObject root, string assetPath, out bool saved,
                                       StringBuilder summary)
        {
            saved = false;

            bool success;
            GameObject result;
            try
            {
                result = PrefabUtility.SaveAsPrefabAsset(root, assetPath, out success);
            }
            catch (Exception ex)
            {
                summary.AppendLine("  保存 prefab 异常（" + assetPath + "）："
                                   + ex.GetType().Name + " " + ex.Message);
                return false;
            }

            if (!success || result == null)
            {
                summary.AppendLine("  PrefabUtility.SaveAsPrefabAsset 报告失败：" + assetPath);
                return false;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            if (AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) == null)
            {
                summary.AppendLine("  保存后立刻回读不到 prefab（说明导入失败）：" + assetPath);
                return false;
            }

            saved = true;
            summary.AppendLine("  已保存 prefab：" + assetPath);
            return true;
        }

        // ==================================================================== manifest（发布信号）

        /// <summary>
        /// 清除 manifest（发布信号），并且**自证确实清掉了**。
        ///
        /// 为什么不能只写一句 `AssetDatabase.DeleteAsset`（这是复核修掉的一处发布缺陷）：
        /// 那一步失败时它不一定会抛异常，于是"烘焙失败"可能留下一个**仍然有效的旧 manifest** ——
        /// 消费者（C2/C3）会照旧接受它，而磁盘上的 prefab 已经被本次烘焙覆盖过（digest 与实际内容
        /// 不再对应）。契约要求"失败不能留下 valid manifest"，所以这里必须自证：
        ///   1) `AssetDatabase.DeleteAsset` + `Refresh`；
        ///   2) 文件还在 ⇒ `File.Delete` + `Refresh`；
        ///   3) 还在 ⇒ 写入**无效占位 manifest**（formatVersion=0，不可能通过 C2 的强校验），
        ///      并**再解析一次确认它确实不合法**；连这一步都失败就返回 false（调用方必须中止）。
        /// 注意：本方法只处理 manifest（发布信号）；两个 prefab 不在这里删 —— 没有 manifest 的
        /// prefab 不可消费（C2 会以"缺资源"失败），所以它们残留无害。
        /// </summary>
        private static bool RemoveManifestOrInvalidate(StringBuilder summary, out string error)
        {
            error = null;

            string absolute = AbsolutePath(ManifestAssetPath);
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ManifestAssetPath) != null
                || File.Exists(absolute))
            {
                AssetDatabase.DeleteAsset(ManifestAssetPath);
                AssetDatabase.Refresh();
            }

            if (!File.Exists(absolute))
            {
                return true;
            }

            try
            {
                File.Delete(absolute);
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                if (summary != null)
                {
                    summary.AppendLine("  File.Delete 失败（" + ManifestAssetPath + "）："
                                       + ex.GetType().Name + " " + ex.Message);
                }
            }

            if (!File.Exists(absolute))
            {
                return true;
            }

            // 最后一道：让它**不再是一个有效发布信号**（格式非法 ⇒ C2 的 Validate 必拒）。
            const string poisoned =
                "{\"formatVersion\":0,\"mapId\":\"invalid\",\"seed\":0,\"mapResource\":\"\","
                + "\"playerResource\":\"\",\"contentDigest\":\"\",\"collisionDigest\":0,"
                + "\"worldVersion\":0}";

            try
            {
                File.WriteAllText(absolute, poisoned, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(ManifestAssetPath, ImportAssetOptions.ForceSynchronousImport);
            }
            catch (Exception ex)
            {
                error = "无法删除、也无法作废 manifest（" + ManifestAssetPath + "）："
                        + ex.GetType().Name + " " + ex.Message
                        + "。请手工删除该文件后重试，否则它会被后来者当成有效内容。";
                return false;
            }

            PMBattleContentManifest poisonedManifest;
            string parseError;
            if (PMBattleContentManifest.TryParseJson(poisoned, out poisonedManifest, out parseError))
            {
                error = "无法删除 manifest，且写入的无效占位竟通过了 C2 强校验（" + ManifestAssetPath
                        + "）：拒绝继续，请手工删除该文件。";
                return false;
            }

            if (summary != null)
            {
                summary.AppendLine("  **警告**：" + ManifestAssetPath
                                   + " 无法删除，已写入**无效占位**（formatVersion=0）：它不会再被当成"
                                   + "有效发布信号（C2 强校验必拒），但请手工删除该文件。");
            }

            return true;
        }

        private static bool WriteManifest(string contentDigest, uint collisionDigest, int worldVersion,
                                         StringBuilder summary)
        {
            // 直接复用 C2 的类型：字段名/类型/顺序就是协议，自己另写一个类就是制造第二份口径。
            PMBattleContentManifest manifest = new PMBattleContentManifest();
            manifest.formatVersion = PMBattleContentManifest.ExpectedFormatVersion;
            manifest.mapId = PMBattleContentManifest.ExpectedMapId;
            manifest.seed = PMBattleContentManifest.ExpectedSeed;
            manifest.mapResource = PMBattleContentManifest.MapResourceKey;
            manifest.playerResource = PMBattleContentManifest.PlayerResourceKey;
            manifest.contentDigest = contentDigest;
            manifest.collisionDigest = collisionDigest;
            manifest.worldVersion = worldVersion;

            string json = JsonUtility.ToJson(manifest);

            string absolute = AbsolutePath(ManifestAssetPath);
            try
            {
                // UTF-8 **不带 BOM**（JsonUtility 读的是纯文本；BOM 会被当成内容首字符的风险不值得冒）。
                File.WriteAllText(absolute, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                summary.AppendLine("  写 manifest 失败：" + ex.GetType().Name + " " + ex.Message);
                return false;
            }

            AssetDatabase.ImportAsset(ManifestAssetPath, ImportAssetOptions.ForceSynchronousImport);

            TextAsset written = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestAssetPath);
            if (written == null)
            {
                summary.AppendLine("  manifest 写盘后不是 TextAsset（扩展名/导入设置问题）：" + ManifestAssetPath);
                return false;
            }

            PMBattleContentManifest reparsed;
            string parseError;
            if (!PMBattleContentManifest.TryParseJson(written.text, out reparsed, out parseError))
            {
                summary.AppendLine("  写盘后的 manifest 无法被 C2 规则解析/校验：" + parseError);
                return false;
            }

            summary.AppendLine("  已发布 manifest（发布信号，最后一步）：" + json);
            return true;
        }

        // ==================================================================== 回读校验

        /// <summary>
        /// 地图 prefab 回读校验：把**磁盘上的产物**重新走一遍结构检查 + 落点逐一比对。
        /// 之所以要回读：内存里的层级正确 != 保存后的资产正确（保存会走 Unity 的序列化）。
        /// </summary>
        private static bool VerifyMapPrefab(string assetPath, List<Placement> placements,
                                           StringBuilder summary, out string error)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                error = "回读 prefab 为 null";
                return false;
            }

            Transform container = prefab.transform.Find(MapContainerName);
            if (container == null)
            {
                error = "回读的 prefab 里找不到容器 \"" + MapContainerName + "\"";
                return false;
            }

            if (container.childCount != placements.Count)
            {
                error = "tile 数量不一致：资产=" + container.childCount.ToString(CultureInfo.InvariantCulture)
                        + " 期望=" + placements.Count.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            for (int i = 0; i < placements.Count; i++)
            {
                Transform tile = container.GetChild(i);
                Placement p = placements[i];
                Vector3 expectedPos = new Vector3(p.LocalX, p.LocalY, p.LocalZ);
                Quaternion expectedRot = new Quaternion(p.RotX, p.RotY, p.RotZ, p.RotW);

                if (tile.localPosition != expectedPos || tile.localRotation != expectedRot)
                {
                    error = "tile[" + i.ToString(CultureInfo.InvariantCulture) + "] 变换不一致（资产=\""
                            + tile.name + "\" pos=" + Vec(tile.localPosition) + " rot="
                            + Quat(tile.localRotation) + "；期望 pos=" + Vec(expectedPos) + " rot="
                            + Quat(expectedRot) + "）";
                    return false;
                }
            }

            if (!VerifyMapPrefabLoaded(prefab, summary, out error))
            {
                return false;
            }

            summary.AppendLine("  地图 prefab 回读校验通过：tile="
                               + placements.Count.ToString(CultureInfo.InvariantCulture)
                               + "，节点=" + prefab.transform.hierarchyCount.ToString(CultureInfo.InvariantCulture)
                               + "，Collider=" + CountColliders(prefab).ToString(CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>地图 prefab 的组件/碰撞体约束（与 C2 的 CollectAndValidate 同口径）。</summary>
        private static bool VerifyMapPrefabLoaded(GameObject prefab, StringBuilder summary, out string error)
        {
            error = null;

            // 预制体资产根没有层级父级，activeInHierarchy == activeSelf；C2 会要求激活。
            if (!prefab.activeSelf)
            {
                error = "地图 prefab 根节点未激活（C2 会拒绝加载）。";
                return false;
            }

            Component[] components = prefab.GetComponentsInChildren<Component>(true);
            int colliders = 0;
            int triggers = 0;
            int meshFilters = 0;
            int meshRenderers = 0;
            int lodGroups = 0;
            int kinematicRigidbodies = 0;
            int renderersWithoutMaterial = 0;
            string firstRendererWithoutMaterial = null;
            int lodGroupsWithoutLevels = 0;
            string firstLodGroupWithoutLevels = null;
            Dictionary<string, int> census = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                {
                    error = "地图 prefab 含 missing script（组件数组里的 null 项）"
                            + "：C1 必须彻底删除旧脚本，而不是禁用。";
                    return false;
                }

                string typeName = component.GetType().Name;
                int count;
                census.TryGetValue(typeName, out count);
                census[typeName] = count + 1;

                if (component is Transform)
                {
                    continue;
                }

                if (component is MeshFilter)
                {
                    meshFilters++;

                    Mesh mesh = ((MeshFilter)component).sharedMesh;
                    if (mesh == null)
                    {
                        error = "地图 MeshFilter 没有网格（对象=\"" + PathOf(component.gameObject)
                                + "\"）：MeshFilter 的 m_Mesh 引用没有拷过来，产物会不可见。";
                        return false;
                    }

                    continue;
                }

                if (component is MeshRenderer)
                {
                    meshRenderers++;

                    // 材质数组是**数组字段**，正是"数组没拷过去"最先暴露的地方：这里把
                    // "渲染器一个材质都没有"变成烘焙失败，而不是静默出一张没有材质的图。
                    Material[] materials = ((MeshRenderer)component).sharedMaterials;
                    if (!HasUsableMaterial(materials))
                    {
                        renderersWithoutMaterial++;
                        if (firstRendererWithoutMaterial == null)
                        {
                            firstRendererWithoutMaterial = PathOf(component.gameObject);
                        }
                    }

                    continue;
                }

                if (component is LODGroup)
                {
                    lodGroups++;

                    if (((LODGroup)component).lodCount <= 0)
                    {
                        lodGroupsWithoutLevels++;
                        if (firstLodGroupWithoutLevels == null)
                        {
                            firstLodGroupWithoutLevels = PathOf(component.gameObject);
                        }
                    }

                    continue;
                }

                Collider collider = component as Collider;
                if (collider != null)
                {
                    if (!collider.enabled)
                    {
                        error = "地图 Collider 未启用（对象=\"" + PathOf(collider.gameObject) + "\"）";
                        return false;
                    }

                    if (!collider.gameObject.activeInHierarchy)
                    {
                        error = "地图 Collider 所在物体未激活（对象=\"" + PathOf(collider.gameObject)
                                + "\"）：C2 要求所有 Collider enabled/active。";
                        return false;
                    }

                    if (collider.isTrigger)
                    {
                        triggers++;
                    }
                    else
                    {
                        colliders++;
                    }

                    continue;
                }

                Rigidbody rigidbody = component as Rigidbody;
                if (rigidbody != null)
                {
                    if (!rigidbody.isKinematic)
                    {
                        error = "地图含动态 Rigidbody（对象=\"" + PathOf(rigidbody.gameObject) + "\"）";
                        return false;
                    }

                    kinematicRigidbodies++;
                    continue;
                }

                error = "地图 prefab 含未允许组件类型 " + component.GetType().FullName
                        + "（对象=\"" + PathOf(component.gameObject)
                        + "\"）：C2 允许集 = Transform/MeshFilter/MeshRenderer/Collider/LODGroup/kinematic Rigidbody。";
                return false;
            }

            if (colliders == 0)
            {
                error = "地图没有任何非 trigger Collider（正式地图必须有地面+墙/障碍的真实碰撞）。";
                return false;
            }

            if (renderersWithoutMaterial > 0)
            {
                error = "地图有 " + renderersWithoutMaterial.ToString(CultureInfo.InvariantCulture)
                        + " 个 MeshRenderer 没有任何可用材质（首个对象=\""
                        + (firstRendererWithoutMaterial == null ? "<未知>" : firstRendererWithoutMaterial)
                        + "\"）：材质数组（m_Materials）没有被逐字段拷贝过去，地图会渲染成无材质/默认外观。";
                return false;
            }

            if (lodGroupsWithoutLevels > 0)
            {
                error = "地图有 " + lodGroupsWithoutLevels.ToString(CultureInfo.InvariantCulture)
                        + " 个 LODGroup 没有任何 LOD 级别（首个对象=\""
                        + (firstLodGroupWithoutLevels == null ? "<未知>" : firstLodGroupWithoutLevels)
                        + "\"）：LODGroup 的 m_LODs 数组没有被拷贝过去。";
                return false;
            }

            if (summary != null)
            {
                summary.AppendLine("  地图组件清单：无材质渲染器=" + renderersWithoutMaterial.ToString(CultureInfo.InvariantCulture)
                                   + " 无 LOD 级别的 LODGroup=" + lodGroupsWithoutLevels.ToString(CultureInfo.InvariantCulture)
                                   + " | colliders=" + colliders.ToString(CultureInfo.InvariantCulture)
                                   + " triggers=" + triggers.ToString(CultureInfo.InvariantCulture)
                                   + " meshFilters=" + meshFilters.ToString(CultureInfo.InvariantCulture)
                                   + " meshRenderers=" + meshRenderers.ToString(CultureInfo.InvariantCulture)
                                   + " lodGroups=" + lodGroups.ToString(CultureInfo.InvariantCulture)
                                   + " kinematicRigidbodies=" + kinematicRigidbodies.ToString(CultureInfo.InvariantCulture)
                                   + " | 类型分布=" + DescribeCounts(census));
            }

            return true;
        }

        /// <summary>角色 prefab 回读校验。</summary>
        private static bool VerifyPlayerPrefab(string assetPath, StringBuilder summary, out string error)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                error = "回读 prefab 为 null";
                return false;
            }

            return VerifyPlayerPrefabLoaded(prefab, summary, out error);
        }

        private static bool VerifyPlayerPrefabLoaded(GameObject prefab, StringBuilder summary, out string error)
        {
            error = null;

            if (!prefab.activeSelf)
            {
                error = "角色 prefab 根节点未激活（C2 会因 activeInHierarchy=false 直接抛异常）。";
                return false;
            }

            Component[] components = prefab.GetComponentsInChildren<Component>(true);
            int animators = 0;
            int renderers = 0;
            int meshFilters = 0;
            int enabledRenderersWithoutMaterial = 0;
            string firstRendererWithoutMaterial = null;
            int skinnedWithoutMeshOrBones = 0;
            string firstSkinnedProblem = null;
            Dictionary<string, int> census = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                {
                    error = "角色 prefab 含 missing script（组件数组里的 null 项）："
                            + "C2 的 PMUnityBattlePresentation 会直接拒绝。";
                    return false;
                }

                string typeName = component.GetType().Name;
                int count;
                census.TryGetValue(typeName, out count);
                census[typeName] = count + 1;

                if (component is Transform)
                {
                    continue;
                }

                if (component is Collider)
                {
                    error = "角色 prefab 仍含 Collider（对象=\"" + PathOf(component.gameObject)
                            + "\"）：碰撞属于 DS 权威运动，表现 prefab 必须不含。";
                    return false;
                }

                if (component is Rigidbody)
                {
                    error = "角色 prefab 仍含 Rigidbody（对象=\"" + PathOf(component.gameObject) + "\"）。";
                    return false;
                }

                if (component is MonoBehaviour)
                {
                    error = "角色 prefab 仍含 MonoBehaviour（类型=" + component.GetType().FullName
                            + "，对象=\"" + PathOf(component.gameObject)
                            + "\"）：旧玩法/驱动脚本必须删除，而不是禁用。";
                    return false;
                }

                if (component is MeshFilter)
                {
                    meshFilters++;

                    // 网格引用必须真的在（否则角色只是个空壳）。
                    Mesh mesh = ((MeshFilter)component).sharedMesh;
                    if (mesh == null)
                    {
                        error = "角色 MeshFilter 没有网格（对象=\"" + PathOf(component.gameObject)
                                + "\"）：角色表现不可见。";
                        return false;
                    }

                    continue;
                }

                if (component is MeshRenderer)
                {
                    renderers++;

                    MeshRenderer meshRenderer = (MeshRenderer)component;
                    if (meshRenderer.enabled && !HasUsableMaterial(meshRenderer.sharedMaterials))
                    {
                        enabledRenderersWithoutMaterial++;
                        if (firstRendererWithoutMaterial == null)
                        {
                            firstRendererWithoutMaterial = PathOf(component.gameObject);
                        }
                    }

                    continue;
                }

                if (component is SkinnedMeshRenderer)
                {
                    renderers++;

                    // 骨骼网格是"角色可用"的另一半：网格与骨骼数组都必须真的在
                    //（骨骼对象不会被本文件删除，但字段级损坏必须在这里暴露）。
                    SkinnedMeshRenderer skinned = (SkinnedMeshRenderer)component;
                    if (skinned.sharedMesh == null || skinned.bones == null || skinned.bones.Length == 0)
                    {
                        skinnedWithoutMeshOrBones++;
                        if (firstSkinnedProblem == null)
                        {
                            firstSkinnedProblem = PathOf(component.gameObject);
                        }
                    }
                    else if (skinned.enabled && !HasUsableMaterial(skinned.sharedMaterials))
                    {
                        enabledRenderersWithoutMaterial++;
                        if (firstRendererWithoutMaterial == null)
                        {
                            firstRendererWithoutMaterial = PathOf(component.gameObject);
                        }
                    }

                    continue;
                }

                if (component is LODGroup)
                {
                    continue;
                }

                Animator animator = component as Animator;
                if (animator != null)
                {
                    animators++;
                    if (animator.applyRootMotion)
                    {
                        error = "角色 Animator.applyRootMotion=true（契约要求关闭，否则会与唯一 Transform 写者冲突）。";
                        return false;
                    }

                    continue;
                }

                error = "角色 prefab 含未允许组件类型 " + component.GetType().FullName
                        + "（对象=\"" + PathOf(component.gameObject)
                        + "\"）：C2 允许集 = Transform/MeshFilter/MeshRenderer/SkinnedMeshRenderer/Animator/LODGroup。";
                return false;
            }

            if (animators == 0)
            {
                error = "角色 prefab 没有 Animator（C2 需要它驱动 Speed）。";
                return false;
            }

            if (renderers == 0 || meshFilters == 0)
            {
                error = "角色 prefab 没有任何网格渲染（renderers=" + renderers.ToString(CultureInfo.InvariantCulture)
                        + " meshFilters=" + meshFilters.ToString(CultureInfo.InvariantCulture)
                        + "）：那不是一个可见角色。";
                return false;
            }

            if (skinnedWithoutMeshOrBones > 0)
            {
                error = "角色有 " + skinnedWithoutMeshOrBones.ToString(CultureInfo.InvariantCulture)
                        + " 个 SkinnedMeshRenderer 缺少网格或骨骼（首个对象=\""
                        + (firstSkinnedProblem == null ? "<未知>" : firstSkinnedProblem)
                        + "\"）：蒙皮网格不会跟着骨骼动，角色表现不可用。";
                return false;
            }

            if (enabledRenderersWithoutMaterial > 0)
            {
                error = "角色有 " + enabledRenderersWithoutMaterial.ToString(CultureInfo.InvariantCulture)
                        + " 个**已启用**的渲染器没有任何可用材质（首个对象=\""
                        + (firstRendererWithoutMaterial == null ? "<未知>" : firstRendererWithoutMaterial)
                        + "\"）：角色会渲染成无材质/默认外观。";
                return false;
            }

            bool hasSpeed = false;
            Animator[] allAnimators = prefab.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < allAnimators.Length && !hasSpeed; i++)
            {
                AnimatorControllerParameter[] parameters = allAnimators[i].parameters;
                for (int p = 0; p < parameters.Length; p++)
                {
                    if (string.Equals(parameters[p].name, "Speed", StringComparison.Ordinal)
                        && parameters[p].type == AnimatorControllerParameterType.Float)
                    {
                        hasSpeed = true;
                        break;
                    }
                }
            }

            if (!hasSpeed)
            {
                error = "角色 Animator 控制器里没有 Float 型 \"Speed\" 参数（C2 靠它驱动移动动画）。";
                return false;
            }

            if (summary != null)
            {
                summary.AppendLine("  角色组件清单：无材质启用渲染器=" + enabledRenderersWithoutMaterial.ToString(CultureInfo.InvariantCulture)
                                   + " 缺网格/骨骼的 SkinnedMeshRenderer=" + skinnedWithoutMeshOrBones.ToString(CultureInfo.InvariantCulture)
                                   + " | animators=" + animators.ToString(CultureInfo.InvariantCulture)
                                   + " renderers=" + renderers.ToString(CultureInfo.InvariantCulture)
                                   + " meshFilters=" + meshFilters.ToString(CultureInfo.InvariantCulture)
                                   + " 节点=" + prefab.transform.hierarchyCount.ToString(CultureInfo.InvariantCulture)
                                   + " | 类型分布=" + DescribeCounts(census));
            }

            return true;
        }

        // ==================================================================== 场景内查找

        /// <summary>
        /// 按路径在场景里找一个对象；0 个或多个匹配都返回 null（调用方给明确错误）。
        /// 刻意不用 GameObject.Find（它受激活状态影响且会命中别的已打开场景）。
        /// </summary>
        private static GameObject FindByPath(Scene scene, string path)
        {
            string[] segments = path.Split('/');
            if (segments.Length == 0)
            {
                return null;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            GameObject current = null;

            for (int i = 0; i < roots.Length; i++)
            {
                if (!string.Equals(roots[i].name, segments[0], StringComparison.Ordinal))
                {
                    continue;
                }

                if (current != null)
                {
                    return null;    // 同名根：歧义 ⇒ 不猜
                }

                current = roots[i];
            }

            for (int s = 1; s < segments.Length && current != null; s++)
            {
                Transform next = null;
                Transform parent = current.transform;
                for (int i = 0; i < parent.childCount; i++)
                {
                    if (!string.Equals(parent.GetChild(i).name, segments[s], StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (next != null)
                    {
                        return null;
                    }

                    next = parent.GetChild(i);
                }

                current = next == null ? null : next.gameObject;
            }

            return current;
        }

        private static ScenseBuildLogic FindLogic(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                ScenseBuildLogic logic = roots[i].GetComponentInChildren<ScenseBuildLogic>(true);
                if (logic != null)
                {
                    return logic;
                }
            }

            return null;
        }

        /// <summary>场景内对象的 fileID（仅用于把"模板来自哪一块"写进摘要，便于人工核对）。</summary>
        private static string SceneFileIdOf(GameObject go)
        {
            string guid;
            long localId;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(go, out guid, out localId))
            {
                return guid + ":" + localId.ToString(CultureInfo.InvariantCulture) + ":" + go.name;
            }

            return go.name;
        }

        // ==================================================================== 杂项

        /// <summary>
        /// 确保 <see cref="ResourcesDir"/> 存在。`Assets/Resources` 本身也可能不存在，因此先补父目录、
        /// 再补 `PMNet`，最后**显式确认**一次：不确认就会把失败推迟到"存 prefab 时抛一个与目录无关的错"。
        /// </summary>
        private static void EnsureResourcesDirectory()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
            {
                AssetDatabase.CreateFolder("Assets", "Resources");
                AssetDatabase.Refresh();
            }

            if (!AssetDatabase.IsValidFolder(ResourcesDir))
            {
                AssetDatabase.CreateFolder("Assets/Resources", "PMNet");
                AssetDatabase.Refresh();
            }

            if (!AssetDatabase.IsValidFolder(ResourcesDir))
            {
                throw new InvalidOperationException("无法创建资源目录：" + ResourcesDir
                                                    + "（AssetDatabase.CreateFolder 未生效）");
            }
        }

        /// <summary>源资产是否比产物新（用于构建 hook 决定要不要重烘；不做内容比对）。</summary>
        private static bool IsStale(out string reason)
        {
            reason = null;

            string[] sources = { SourceScenePath, SourceLogicPath, SourcePlayerPath };
            string[] outputs = { MapPrefabPath, PlayerPrefabPath, ManifestAssetPath };

            DateTime oldestOutput = DateTime.MaxValue;
            for (int i = 0; i < outputs.Length; i++)
            {
                string path = AbsolutePath(outputs[i]);
                if (!File.Exists(path))
                {
                    reason = "产物缺失：" + outputs[i];
                    return true;
                }

                DateTime written = File.GetLastWriteTimeUtc(path);
                if (written < oldestOutput)
                {
                    oldestOutput = written;
                }
            }

            for (int i = 0; i < sources.Length; i++)
            {
                string path = AbsolutePath(sources[i]);
                if (!File.Exists(path))
                {
                    reason = "源资产缺失：" + sources[i];
                    return true;
                }

                if (File.GetLastWriteTimeUtc(path) > oldestOutput)
                {
                    reason = "源资产较新：" + sources[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>Assets 相对路径 → 磁盘绝对路径（Unity 的 Application.dataPath 一定是 Client/Assets）。</summary>
        private static string AbsolutePath(string assetPath)
        {
            DirectoryInfo clientDir = Directory.GetParent(Application.dataPath);
            string projectRoot = clientDir == null ? Application.dataPath : clientDir.FullName;
            return Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>渲染器是否至少有一个非 null 材质（"渲染器可用"的最小判据）。</summary>
        private static bool HasUsableMaterial(Material[] materials)
        {
            if (materials == null || materials.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountColliders(GameObject root)
        {
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            return colliders.Length;
        }

        private static string PathOf(GameObject go)
        {
            if (go == null)
            {
                return "<null>";
            }

            string path = go.name;
            Transform parent = go.transform.parent;
            int guard = 0;
            while (parent != null && guard++ < 64)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        private static void TrySetTag(GameObject go, string tag, StringBuilder summary)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            try
            {
                go.tag = tag;
            }
            catch (Exception)
            {
                // tag 未在工程里定义时 Unity 会抛异常；tag 只影响物理层之外的分组语义，
                // 不值得让整次烘焙失败，但必须让用户看见。
                if (summary != null)
                {
                    summary.AppendLine("  注意：tag \"" + tag + "\" 在本工程未定义，已保持 Untagged（对象 \""
                                       + go.name + "\"）");
                }
            }
        }

        private static bool Contains(string[] array, string value)
        {
            for (int i = 0; i < array.Length; i++)
            {
                if (string.Equals(array[i], value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>往列表里加一个去重项（摘要里的"集合"语义；顺序 = 首次出现顺序）。</summary>
        private static void AddOnce(List<string> values, string value)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], value, StringComparison.Ordinal))
                {
                    return;
                }
            }

            values.Add(value);
        }

        private static string Join(List<string> values)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(values[i]);
            }

            return sb.ToString();
        }

        private static string DescribeCounts(Dictionary<string, int> counts)
        {
            List<string> keys = new List<string>(counts.Keys);
            keys.Sort(StringComparer.Ordinal);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(keys[i]).Append('=').Append(counts[keys[i]].ToString(CultureInfo.InvariantCulture));
            }

            return sb.Length == 0 ? "空" : sb.ToString();
        }

        private static string Num(float value)
        {
            // "R" + 不变文化：同一 float 在任何区域设置下都得到同一个字符串。
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Vec(Vector3 v)
        {
            return "(" + Num(v.x) + "," + Num(v.y) + "," + Num(v.z) + ")";
        }

        private static string Vec(Vector2 v)
        {
            return "(" + Num(v.x) + "," + Num(v.y) + ")";
        }

        private static string Vec(Vector4 v)
        {
            return "(" + Num(v.x) + "," + Num(v.y) + "," + Num(v.z) + "," + Num(v.w) + ")";
        }

        private static string Quat(Quaternion q)
        {
            return "(" + Num(q.x) + "," + Num(q.y) + "," + Num(q.z) + "," + Num(q.w) + ")";
        }

        private static string Quote(string value)
        {
            return value == null ? "<null>" : "\"" + value + "\"";
        }

        private static string Short(string sha)
        {
            if (string.IsNullOrEmpty(sha) || sha.Length < 12)
            {
                return sha;
            }

            return sha.Substring(0, 12);
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return sb.ToString();
            }
        }

        private static string Sha256File(string absolutePath)
        {
            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException("源文件不存在（无法计算 SHA256）：" + absolutePath, absolutePath);
            }

            using (FileStream stream = File.OpenRead(absolutePath))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return sb.ToString();
            }
        }
    }
}
