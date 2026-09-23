// ============================================================================
//  PMBattleContentSceneFactsCheck —— R4-C / C1 崩溃修复的**源真相门禁**
// ============================================================================
//
//  为什么需要它（它是这次崩溃修复唯一能在 CLI 里钉死的东西）：
//    `Client/Assets/Editor/PMBattleContentBuild.cs` 的 SIGSEGV 修复依赖三条**源场景事实**：
//      1) `ScenseBuildLogic.floors` 指向的地板模板在源场景里就是 `m_IsActive: 0`（且没有 Collider）
//         ⇒ 「旧运行期这些实例不可见/无碰撞 ⇒ 默认跳过」这句话才有出处；
//      2) `walls`/`obstacles`/`trees`/`Grasses` 的模板是 active ⇒ 跳过策略不会把墙/障碍也一起跳掉；
//      3) 地面 `HYLDGameTatal/MAP/Plane` 存在、active、且带 `MeshCollider`（classID 64）
//         ⇒ 「地面是唯一碰撞地板，若 inactive 必须显式失败」这句话才有意义（floors 模板没有 Collider）。
//    这三条只要有一条在源场景里变了，修复的前提就变了（例如有人把地板模板改成 active，
//    或把地面删了）—— 那种情况下需要的不是"继续烘焙"，而是重新评估策略。
//    本工具把这三条钉成**可重复执行的断言**，并且**失败时打印实际值**（不是只说"不符"）。
//
//  它证明什么 / 不证明什么（口径必须写清）：
//    · 证明：文本 YAML 里的上述源事实（含组件 classID 与层级路径）、以及 C1 源码里 F1–F7 的
//      **结构化标记**仍然存在（见 §E，"静态结构断言"，文本级 —— 它防的是"把修复改回去"，
//      **不是**行为验证）。
//    · **不证明**：Unity 编辑期的真实行为（AddComponent 语义、interests 注销、SerializedProperty
//      的 arraySize 报错、真实烘焙是否成功）。这些只能在 Unity 里跑出来，见报告「仍未验证项」。
//
//  纯 C#、net8、零外部依赖：只把 .unity 当**文本 YAML** 读（Unity 的 ForceText 序列化），
//  不加载任何引擎。这也意味着它**不**校验"Unity 能不能正确反序列化这张场景"。
//
//  §H（第四轮，显式拷贝）：两类新断言 ——
//    A) **源数据级**：模板层级 + 地面的组件 classID 集合必须 ⊆ C1 显式拷贝的支持集；
//       `m_LightProbeVolumeOverride` / `m_StaticBatchRoot` / `m_LightmapParameters` / `m_ProbeAnchor`
//       必须全为 {fileID: 0}（它们没有可写的公开属性，C1 只靠源数据门兜住“不静默丢引用”）；
//    B) **静态结构级**：C1 不再有通用序列化写入（`CopyFromSerializedProperty` /
//       `ApplyModifiedProperties*` / `.arraySize` / `TryGetArraySize` / `VerifyArraySizes`），
//       且 `IsExplicitCopySupported` 覆盖支持集里的每个类型名。
//    口径：A 是源数据，B 是文本结构；两者都**不**证明 Unity 真能把字段拷过去（那要在 Unity 内跑）。
//
//  运行：dotnet build Tools/PMBattleContentSceneFactsCheck -c Release
//        dotnet Tools/PMBattleContentSceneFactsCheck/bin/Release/net8.0/PMBattleContentSceneFactsCheck.dll
//  可选参数：--scene <路径>  覆盖默认的 Client/Assets/Scenes/HYLDGame.unity
//  退出码：0 = 全部通过；1 = 存在失败
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PMBattleContentSceneFactsCheck
{
    internal static class Program
    {
        // 期望的地图循环范围（契约冻结：维持真实 mapx=33 / mapy=21，不擅自把表全部 35 行纳入）
        private const int ExpectedMapX = 33;
        private const int ExpectedMapY = 21;

        // 地面路径（与 C1 的 PMBattleContentBuild.GroundSourcePath 一致）
        private const string GroundSourcePath = "HYLDGameTatal/MAP/Plane";

        // 运行期 tile 容器：ScenseBuildLogic.MAP 指向的 Transform 所在对象（与 C1 的世界变换口径相关）
        private const string RuntimeMapContainerPath = "HYLDGameTatal/3D/MAP";

        // ScenseBuildLogic 组件所在的 GameObject（源场景实测：3D）
        private const string LogicHostPath = "HYLDGameTatal/3D";

        // 单位源脚本相对路径（用它的 .meta 取 guid，而不是把 guid 抄死）
        private const string LogicScriptPath = "Client/Assets/HYLD1.0/Scripts/OldScripts/ScenseBuildLogic.cs";
        private const string SceneRelativePath = "Client/Assets/Scenes/HYLDGame.unity";
        private const string BuildScriptRelativePath = "Client/Assets/Editor/PMBattleContentBuild.cs";

        // Unity classID：Collider 家族（判"模板子树里有没有碰撞"用；CharacterController 不是 Collider）
        private static readonly HashSet<int> ColliderClassIds = new HashSet<int> { 64, 65, 135, 136, 146, 154 };

        private static int _checks;
        private static int _failed;

        private static int Main(string[] args)
        {
            Console.WriteLine("PMBattleContentSceneFactsCheck —— R4-C / C1 崩溃修复源真相门禁");
            Console.WriteLine("(net8 纯 C#：把 HYLDGame.unity 当文本 YAML 读；不加载 Unity)");
            Console.WriteLine();

            string repoRoot = FindRepoRoot();
            if (repoRoot == null)
            {
                Console.WriteLine("失败：无法从可执行文件目录向上找到「同时含 Client 与 Tools」的仓库根。");
                Console.WriteLine("提示：本工具的 §A–§D 需要真实仓库；请从仓库内运行（dotnet <dll>）。");
                _failed++;
                return Finish();
            }

            string scenePath = Path.Combine(repoRoot, SceneRelativePath.Replace('/', Path.DirectorySeparatorChar));
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--scene", StringComparison.Ordinal) && i + 1 < args.Length)
                {
                    scenePath = args[i + 1];
                }
            }

            Console.WriteLine("仓库根：" + repoRoot);
            Console.WriteLine("源场景：" + scenePath);
            Console.WriteLine();

            if (!File.Exists(scenePath))
            {
                Console.WriteLine("失败：源场景文件不存在：" + scenePath);
                _failed++;
                return Finish();
            }

            SceneModel scene = SceneModel.Load(scenePath);

            Section("A. 场景反序列化面（先证明解析器真的读到了东西）");
            CheckParsedDocuments(scene);

            Section("B. ScenseBuildLogic 序列化字段（mapx/mapy + 模板数组）");
            LogicFields logic = CheckLogicFields(repoRoot, scene);

            Section("C. floors：默认跳过策略的**源真相**（inactive 且无 Collider）");
            CheckFloorTemplates(scene, logic);

            Section("D. 其余模板 active + 地面存在且有 MeshCollider");
            CheckOtherTemplatesActive(scene, logic);
            CheckGround(scene);

            Section("E. C1 源码的修复结构（静态结构断言，文本级；不替代 Unity 内行为验证）");
            CheckBuildScriptStructure(repoRoot);

            Section("F. 第三轮依赖的源事实（模板组件类型序列 / P_PROP_well 五组件且无子物体）");
            CheckTemplateComponentFacts(scene, logic);

            Section("G. 第三轮源码不变量（F8 物理隔离门 / F9 摘要确定性 / F10 插桩即停 / F11 只读诊断）");
            CheckRound3Invariants(repoRoot);

            Section("H. 第四轮显式拷贝：源数据组件类型 ⊆ 支持集 + 禁用通用序列化写入");
            CheckTemplateComponentSupport(scene, logic, repoRoot, scenePath);

            return Finish();
        }

        // ==================================================================== A

        private static void CheckParsedDocuments(SceneModel scene)
        {
            Check(scene.DocumentCount > 1000,
                  "场景文档数 > 1000（实际=" + scene.DocumentCount.ToString(CultureInfo.InvariantCulture)
                  + "）：证明按 `--- !u!<classID> &<fileID>` 切分有效");
            Check(scene.GameObjectCount > 100,
                  "GameObject 文档数 > 100（实际=" + scene.GameObjectCount.ToString(CultureInfo.InvariantCulture) + "）");
            Check(scene.TransformCount > 100,
                  "Transform/RectTransform 文档数 > 100（实际=" + scene.TransformCount.ToString(CultureInfo.InvariantCulture) + "）");
            Check(scene.MonoBehaviourCount > 10,
                  "MonoBehaviour 文档数 > 10（实际=" + scene.MonoBehaviourCount.ToString(CultureInfo.InvariantCulture) + "）");
            Check(scene.RootGameObjectCount >= 2,
                  "根 GameObject 数 >= 2（实际=" + scene.RootGameObjectCount.ToString(CultureInfo.InvariantCulture) + "）");
        }

        // ==================================================================== B

        private sealed class LogicFields
        {
            public string ScriptGuid;
            public string LogicHostActualPath;
            public int MapX;
            public int MapY;
            public readonly List<long> Floors = new List<long>();
            public readonly List<long> Walls = new List<long>();
            public readonly List<long> Obstacles = new List<long>();
            public readonly List<long> Grasses = new List<long>();
            public readonly List<long> Trees = new List<long>();
            public long MapTransformFileId;
        }

        private static LogicFields CheckLogicFields(string repoRoot, SceneModel scene)
        {
            string metaPath = Path.Combine(repoRoot, LogicScriptPath.Replace('/', Path.DirectorySeparatorChar)) + ".meta";
            string guid = null;
            if (File.Exists(metaPath))
            {
                Match m = Regex.Match(File.ReadAllText(metaPath), @"^\s*guid:\s*([0-9a-fA-F]{32})\s*$",
                                      RegexOptions.Multiline);
                if (m.Success)
                {
                    guid = m.Groups[1].Value;
                }
            }

            Check(guid != null, "ScenseBuildLogic.cs.meta 里解析出 guid（实际=" + (guid ?? "<未解析>") + "）");
            if (guid == null)
            {
                return null;
            }

            SceneDocument doc = scene.FindMonoBehaviourByScriptGuid(guid);
            Check(doc != null, "源场景里存在引用该 guid 的 MonoBehaviour（guid=" + guid + "）");
            if (doc == null)
            {
                return null;
            }

            LogicFields logic = new LogicFields();
            logic.ScriptGuid = guid;
            logic.LogicHostActualPath = scene.PathOfGameObject(doc.GameObjectFileId);
            logic.MapX = ReadInt(doc.Text, "mapx", int.MinValue);
            logic.MapY = ReadInt(doc.Text, "mapy", int.MinValue);
            ReadFileIdArray(doc.Text, "floors", logic.Floors);
            ReadFileIdArray(doc.Text, "walls", logic.Walls);
            ReadFileIdArray(doc.Text, "obstacles", logic.Obstacles);
            ReadFileIdArray(doc.Text, "Grasses", logic.Grasses);
            ReadFileIdArray(doc.Text, "trees", logic.Trees);
            logic.MapTransformFileId = ReadFileId(doc.Text, "MAP");

            Check(logic.MapX == ExpectedMapX,
                  "mapx == " + ExpectedMapX + "（实际=" + logic.MapX.ToString(CultureInfo.InvariantCulture) + "）");
            Check(logic.MapY == ExpectedMapY,
                  "mapy == " + ExpectedMapY + "（实际=" + logic.MapY.ToString(CultureInfo.InvariantCulture) + "）");
            Check(logic.Floors.Count > 0,
                  "floors 数组非空（实际条数=" + logic.Floors.Count.ToString(CultureInfo.InvariantCulture) + "）");
            Check(logic.Walls.Count > 0, "walls 数组非空（实际=" + logic.Walls.Count.ToString(CultureInfo.InvariantCulture) + "）");
            Check(logic.Obstacles.Count > 0,
                  "obstacles 数组非空（实际=" + logic.Obstacles.Count.ToString(CultureInfo.InvariantCulture) + "）");
            Check(logic.Trees.Count > 0, "trees 数组非空（实际=" + logic.Trees.Count.ToString(CultureInfo.InvariantCulture) + "）");
            Check(logic.Grasses.Count > 0,
                  "Grasses 数组非空（实际=" + logic.Grasses.Count.ToString(CultureInfo.InvariantCulture) + "）");

            Check(string.Equals(logic.LogicHostActualPath, LogicHostPath, StringComparison.Ordinal),
                  "ScenseBuildLogic 挂在 \"" + LogicHostPath + "\"（实际=\"" + logic.LogicHostActualPath + "\"）");

            return logic;
        }

        // ==================================================================== C

        private static void CheckFloorTemplates(SceneModel scene, LogicFields logic)
        {
            if (logic == null || logic.Floors.Count == 0)
            {
                Check(false, "前置缺失：B 段没拿到 floors 数组，C 段无法判定（这是失败，不是跳过）");
                return;
            }

            int distinct = 0;
            HashSet<long> seen = new HashSet<long>();
            for (int i = 0; i < logic.Floors.Count; i++)
            {
                long fileId = logic.Floors[i];
                SceneDocument go = scene.FindGameObject(fileId);
                string path = scene.PathOfGameObject(fileId);

                if (go == null)
                {
                    Check(false, "floors[" + i.ToString(CultureInfo.InvariantCulture)
                                 + "] fileID=" + fileId.ToString(CultureInfo.InvariantCulture)
                                 + " 在场景里找不到 GameObject（解析器或场景已变）");
                    continue;
                }

                string active = ReadToken(go.Text, "m_IsActive");
                List<int> classIds = scene.ComponentClassIdsOf(fileId);
                bool hasCollider = false;
                for (int c = 0; c < classIds.Count; c++)
                {
                    if (ColliderClassIds.Contains(classIds[c]))
                    {
                        hasCollider = true;
                    }
                }

                Check(string.Equals(active, "0", StringComparison.Ordinal),
                      "floors[" + i.ToString(CultureInfo.InvariantCulture) + "] \"" + path
                      + "\" 的 m_IsActive == 0（实际=" + (active ?? "<缺字段>")
                      + "）；这条就是「默认跳过未激活落点」的源真相");
                Check(!hasCollider,
                      "floors[" + i.ToString(CultureInfo.InvariantCulture) + "] \"" + path
                      + "\" 没有任何 Collider 组件（实际 classID=" + JoinInts(classIds)
                      + "）；这条是「跳过它不会丢碰撞」的源真相");

                if (seen.Add(fileId))
                {
                    distinct++;
                }
            }

            Console.WriteLine("      floors 条数=" + logic.Floors.Count.ToString(CultureInfo.InvariantCulture)
                              + "，去重后模板数=" + distinct.ToString(CultureInfo.InvariantCulture)
                              + "（全部都必须 inactive 且无 Collider）");
        }

        // ==================================================================== D

        private static void CheckOtherTemplatesActive(SceneModel scene, LogicFields logic)
        {
            if (logic == null)
            {
                Check(false, "前置缺失：B 段失败，D 段无法判定");
                return;
            }

            CheckActiveArray(scene, "walls", logic.Walls);
            CheckActiveArray(scene, "obstacles", logic.Obstacles);
            CheckActiveArray(scene, "trees", logic.Trees);
            CheckActiveArray(scene, "Grasses", logic.Grasses);
        }

        private static void CheckActiveArray(SceneModel scene, string label, List<long> fileIds)
        {
            if (fileIds.Count == 0)
            {
                Check(false, label + " 数组为空：跳过策略的\"不会把墙/障碍也跳掉\"无从判定");
                return;
            }

            for (int i = 0; i < fileIds.Count; i++)
            {
                long fileId = fileIds[i];
                SceneDocument go = scene.FindGameObject(fileId);
                string path = scene.PathOfGameObject(fileId);
                if (go == null)
                {
                    Check(false, label + "[" + i.ToString(CultureInfo.InvariantCulture) + "] fileID="
                                 + fileId.ToString(CultureInfo.InvariantCulture) + " 找不到 GameObject");
                    continue;
                }

                string active = ReadToken(go.Text, "m_IsActive");
                Check(string.Equals(active, "1", StringComparison.Ordinal),
                      label + "[" + i.ToString(CultureInfo.InvariantCulture) + "] \"" + path
                      + "\" 是 active（m_IsActive=1，实际=" + (active ?? "<缺字段>")
                      + "）：这些模板必须保留，不能被跳过策略误伤");
            }
        }

        private static void CheckGround(SceneModel scene)
        {
            long groundGo = scene.FindGameObjectByPath(GroundSourcePath);
            Check(groundGo != 0,
                  "地面对象 \"" + GroundSourcePath + "\" 存在（实际="
                  + (groundGo == 0 ? "<找不到>" : groundGo.ToString(CultureInfo.InvariantCulture)) + "）");
            if (groundGo == 0)
            {
                return;
            }

            SceneDocument go = scene.FindGameObject(groundGo);
            string active = ReadToken(go.Text, "m_IsActive");
            Check(string.Equals(active, "1", StringComparison.Ordinal),
                  "地面 \"" + GroundSourcePath + "\" 是 active（m_IsActive=1，实际=" + (active ?? "<缺字段>")
                  + "）：C1 对 inactive 地面显式失败，active 是当前源场景的事实");

            List<int> classIds = scene.ComponentClassIdsOf(groundGo);
            bool meshCollider = classIds.Contains(64);
            Check(meshCollider,
                  "地面 \"" + GroundSourcePath + "\" 带 MeshCollider（classID 64）（实际 classID=" + JoinInts(classIds)
                  + "）：它是正式地图唯一的碰撞地板来源（floors 模板自身没有 Collider）");

            long runtimeMap = scene.FindGameObjectByPath(RuntimeMapContainerPath);
            Check(runtimeMap != 0,
                  "运行期 tile 容器 \"" + RuntimeMapContainerPath + "\" 存在（实际="
                  + (runtimeMap == 0 ? "<找不到>" : runtimeMap.ToString(CultureInfo.InvariantCulture))
                  + "）：旧链 MyInstantiate 的 SetParent(MAP) 目标");
        }

        // ==================================================================== E

        /// <summary>
        /// 静态结构断言：C1 源码里 F1–F7 的关键标记仍在。
        ///
        /// **口径声明**：这是**文本级**断言，只防“把修复改回去/删掉”，
        /// 它**不是**行为验证（行为只能在 Unity 内验证，见报告「仍未验证项」）。
        /// 之所以仍然写它：这次的修复最容易的回归方式就是有人把 SetActive 挪回 AddComponent 之前，
        /// 而那种改动在 CLI 里没有任何其它信号。
        /// </summary>
        private static void CheckBuildScriptStructure(string repoRoot)
        {
            string path = Path.Combine(repoRoot, BuildScriptRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Check(File.Exists(path), "C1 烘焙源码存在：" + BuildScriptRelativePath);
            if (!File.Exists(path))
            {
                return;
            }

            string text = File.ReadAllText(path, Encoding.UTF8);

            // **先去掉注释再做 §E 的全部断言**：这些标记/顺序必须出现在**代码**里，
            // 出现在文档注释里的不算（否则“修好的代码 + 描述旧行为的注释”会被误判成已修复）。
            string code = StripComments(text);

            CheckContains(code, "public static bool SkipInactivePlacements = true;",
                          "F2 开关存在且**默认跳过**（SkipInactivePlacements = true）");
            CheckContains(code, "private static bool SelectPlacements(",
                          "F2 落点选择是独立函数（跳过决策可计数、可进 digest）");
            CheckContains(code, "layout.skippedInactive.count=",
                          "F2 跳过计数进 digest（不得静默丢弃）");
            // **第四轮**：通用序列化字段写入整体删除，数组不再经 SerializedObject。
            // 这里只保留“三道门 + 顺序”的结构断言；真正的“源数据能不能被支持集覆盖”在 H 段。
            CheckContains(code, "private static bool TryCopyComponentFields(",
                          "第四轮：显式字段拷贝的分发点存在（按具体类型分派到独立函数）");
            CheckContains(code, "private static bool IsExplicitCopySupported(",
                          "第四轮：支持集判定函数存在（未实现类型在 AddComponent 之前拒绝）");
            CheckContains(code, "private static bool ResolveDeferredReferences(",
                          "第四轮：延后引用重绑定入口存在（LODGroup Renderer / probeAnchor）");
            CheckContains(code, "private static void ResolveLodGroupFixup(",
                          "第四轮：LODGroup 按源→目标映射重绑定 Renderer 的实现存在");
            CheckContains(code, "dest.probeAnchor = mapped;",
                          "第四轮：probeAnchor 重绑定到目标 Transform（不得指源）");
            CheckContains(code, "context.NodeMap[sourceChild] = destGo.transform;",
                          "第四轮：子物体拷贝时登记源→目标节点映射（重绑定的依据）");
            CheckContains(code, "private static void SelfTestCheck(",
                          "第四轮：最小自测的断言辅助存在（自测调用生产拷贝函数）");
            CheckContains(code, "[MenuItem(\"Build/Self-test PMNet Content Copy (no bake)\")]",
                          "第四轮：最小自测菜单存在（只由用户手动执行）");
            Check(
                !code.Contains("CopyFromSerializedProperty")
                && !code.Contains("ApplyModifiedProperties")
                && !code.Contains(".arraySize"),
                "第四轮：源码里不再有通用序列化写入（CopyFromSerializedProperty / ApplyModifiedProperties / .arraySize）");

            CheckContains(code, "Component existing = dest.GetComponent(componentType);",
                          "F3 AddComponent 之前的判重");
            CheckContains(code, "offenders.Add(\"AddComponent 返回 null:",
                          "F3 AddComponent 返回 null 不再继续 new SerializedObject(null)");
            CheckContains(code, "private static void DestroySelfBuiltRoot(",
                          "F5 销毁自建 root 前先 SetActive(false)");
            CheckContains(code, "private static void EnsureScratchSceneEmpty(",
                          "F5 关临时场景前确认自建对象已全部销毁");
            CheckContains(code, "BakeLogRelativePath",
                          "F6 逐步落盘日志");
            CheckContains(code, "FileShare.ReadWrite",
                          "F6 日志以 FileShare 方式打开（崩溃后仍可被外部读取）");

            // F1 的**顺序**才是关键：SetActive 必须在组件拷贝之后。
            CheckOrder(code, "CopyComponentSet(groundPlane, groundClone, true, groundContext, out stripped)",
                       "groundClone.SetActive(groundPlane.activeSelf)",
                       "F1 地面：先拷组件、后施加最终激活态");
            CheckOrder(code, "CopyComponentSet(template, tile, true, tileContext, out stripped)",
                       "tile.SetActive(template.activeSelf)",
                       "F1 tile 根：先拷组件、后施加最终激活态");
            CheckOrder(code, "CopyComponentSet(sourceGo, destGo, forMap, context, out stripped)",
                       "destGo.SetActive(sourceGo.activeSelf)",
                       "F1 子物体：先拷组件、后施加最终激活态");

            // 契约要求：LODGroup 的 Renderer 必须在**整棵目标子树建完之后**重绑定。
            CheckOrder(code, "CopyChildHierarchy(template.transform, tile.transform, true, tileContext, summary,",
                       "ResolveDeferredReferences(tileContext, tileFixupFailures)",
                       "第四轮：延后重绑定在子层搭完之后、施加最终激活态之前");

            // F3：判重必须在 AddComponent 之前。
            CheckOrder(code, "Component existing = dest.GetComponent(componentType);",
                       "Component copy = dest.AddComponent(componentType);",
                       "F3 判重在 AddComponent 之前（顺序）");

            // 第四轮：支持集判定必须在 AddComponent 之前（不允许“先建出来再静默丢字段”）。
            CheckOrder(code, "if (!IsExplicitCopySupported(componentType, out unsupportedReason))",
                       "Component copy = dest.AddComponent(componentType);",
                       "第四轮：未实现字段拷贝的类型在 AddComponent 之前就被拒绝（fail closed）");

            // 旧的不变量（已随通用序列化拷贝删除）：
            //   · F4 的 `TryGetArraySize` 守卫 —— 已经不存在（数组改走公开 API + 回读核对）；
            //   · 下面这条是更强、更简单的替代（含逐处违规定位）。
            CheckNoGenericSerializedWrites(code);
        }

        /// <summary>
        /// 第四轮：旧的“arraySize 读取面收敛在 TryGetArraySize 里”这条不变量**已不再适用** ——
        /// 数组不再经 `SerializedObject` 读写（`MeshRenderer.sharedMaterials` / `LODGroup` 的
        /// `GetLODs/SetLODs` 都是普通公开 API），因此断言换成更强、更简单的一条：
        /// **整个文件的代码里不得再出现 `.arraySize`**（也不得再有 `TryGetArraySize` / `VerifyArraySizes`）。
        /// 它直接锁死“有人把通用序列化拷贝又加回来”这类回归。
        /// </summary>
        private static void CheckNoGenericSerializedWrites(string code)
        {
            List<string> offenders = new List<string>();
            int inspected = 0;
            int index = code.IndexOf(".arraySize", StringComparison.Ordinal);
            while (index >= 0)
            {
                inspected++;
                offenders.Add("位置 " + index.ToString(CultureInfo.InvariantCulture) + "：" + Excerpt(code, index));
                index = code.IndexOf(".arraySize", index + 1, StringComparison.Ordinal);
            }

            Check(offenders.Count == 0,
                  "第四轮：代码里不再有 .arraySize 读写（共 " + inspected.ToString(CultureInfo.InvariantCulture)
                  + " 处；违规=" + (offenders.Count == 0 ? "无" : string.Join(" | ", offenders.ToArray())) + "）");

            Check(!code.Contains("CopyFromSerializedProperty"),
                  "第四轮：代码里不再调用 SerializedObject.CopyFromSerializedProperty");
            Check(!code.Contains("ApplyModifiedProperties"),
                  "第四轮：代码里不再调用 ApplyModifiedProperties*（通用写入路径已删除）");
            Check(!code.Contains("TryGetArraySize") && !code.Contains("VerifyArraySizes"),
                  "第四轮：旧的数组守卫函数（TryGetArraySize / VerifyArraySizes）已彻底删除");
        }

        // ==================================================================== F

        /// <summary>第三轮：P_PROP_well 在源场景里的路径（任务/日志里给出的真实路径）。</summary>
        private const string WellTemplatePath = "HYLDGameTatal/3D/Use/Obstacles/P_PROP_well";

        /// <summary>
        /// 第三轮依赖的源事实（把“重复组件是误报”的前提钉住）：
        ///   · 逐个调色板模板的 组件类型序列 / 子物体数 / 激活态（全量普查，值变了就能看出来）；
        ///   · **P_PROP_well 恰好 5 个组件（Transform 4 / MeshFilter 33 / MeshRenderer 23 /
        ///     MeshCollider 64 / BoxCollider 65）、每个类型只出现一次、且**没有子物体**。
        ///     这两条合起来才能推翻“模板自身含重复组件”：既没有重复，也没有子物体可供重复。
        ///   · obstacles 里至少有一个模板同时带 MeshCollider + BoxCollider
        ///     （= 第二轮真实失败落点 O00005_P_PROP_well 的形状）。
        /// </summary>
        private static void CheckTemplateComponentFacts(SceneModel scene, LogicFields logic)
        {
            if (logic == null)
            {
                Check(false, "前置缺失：B 段失败，F 段无法判定");
                return;
            }

            CensusTemplates(scene, "floors", logic.Floors);
            CensusTemplates(scene, "walls", logic.Walls);
            CensusTemplates(scene, "obstacles", logic.Obstacles);
            CensusTemplates(scene, "Grasses", logic.Grasses);
            CensusTemplates(scene, "trees", logic.Trees);

            // 至少一个 obstacle 同时带 MeshCollider(64) + BoxCollider(65)：这是第二轮失败落点的形状。
            bool sawMeshAndBox = false;
            for (int i = 0; i < logic.Obstacles.Count; i++)
            {
                List<int> ids = scene.ComponentClassIdsOf(logic.Obstacles[i]);
                if (ids.Contains(64) && ids.Contains(65))
                {
                    sawMeshAndBox = true;
                    break;
                }
            }

            Check(sawMeshAndBox,
                  "obstacles 里至少一个模板同时带 MeshCollider(64) + BoxCollider(65)"
                  + "（= 第二轮 O00005_P_PROP_well 的形状；它决定“为什么是第 5 个 tile 先报错”）");

            long pot = scene.FindGameObjectByPath("HYLDGameTatal/3D/Use/Obstacles/P_PROP_cookingpot");
            Check(pot != 0, "P_PROP_cookingpot 源模板存在");
            if (pot != 0)
            {
                List<int> potIds = scene.ComponentClassIdsOf(pot);
                int boxes = 0;
                foreach (int id in potIds) { if (id == 65) boxes++; }
                Check(boxes == 2, "P_PROP_cookingpot 合法包含两个 BoxCollider（实际=" + boxes + "）");
            }

            // P_PROP_well 的硬事实。
            long well = scene.FindGameObjectByPath(WellTemplatePath);
            Check(well != 0, "模板 \"" + WellTemplatePath + "\" 存在（实际="
                             + (well == 0 ? "<找不到>" : well.ToString(CultureInfo.InvariantCulture)) + "）");
            if (well == 0)
            {
                return;
            }

            List<int> wellIds = scene.ComponentClassIdsOf(well);
            string wellIdText = JoinInts(wellIds);

            Check(wellIds.Count == 5,
                  "P_PROP_well 恰好 5 个组件（实际=" + wellIds.Count.ToString(CultureInfo.InvariantCulture)
                  + "，classID=" + wellIdText + "）：这是“模板自身没有重复组件”的前提");

            Check(HasEachClassIdOnce(wellIds),
                  "P_PROP_well 的每个组件类型只出现一次（classID=" + wellIdText + "）");

            Check(wellIds.Contains(4) && wellIds.Contains(33) && wellIds.Contains(23)
                  && wellIds.Contains(64) && wellIds.Contains(65),
                  "P_PROP_well 组件集 = Transform(4)/MeshFilter(33)/MeshRenderer(23)/MeshCollider(64)/BoxCollider(65)"
                  + "（实际=" + wellIdText + "）");

            SceneDocument wellDoc = scene.FindGameObject(well);
            string wellActive = ReadToken(wellDoc.Text, "m_IsActive");
            Check(string.Equals(wellActive, "1", StringComparison.Ordinal),
                  "P_PROP_well 是 active（m_IsActive=1，实际=" + (wellActive ?? "<缺字段>")
                  + "）：它不是被跳过的那类落点，会真的被建出来");

            int wellChildren = scene.ChildCountOf(well);
            Check(wellChildren == 0,
                  "P_PROP_well **没有子物体**（实际 childCount=" + wellChildren.ToString(CultureInfo.InvariantCulture)
                  + "）：因此“重复组件”不可能来自子物体拷贝路径（CopyChildHierarchy）");
        }

        /// <summary>把一份模板数组的 激活态 / 组件类型序列 / 子物体数 全量打出来（值变了看得见）。</summary>
        private static void CensusTemplates(SceneModel scene, string label, List<long> fileIds)
        {
            int meshCollider = 0;
            int boxCollider = 0;
            for (int i = 0; i < fileIds.Count; i++)
            {
                List<int> ids = scene.ComponentClassIdsOf(fileIds[i]);
                if (ids.Contains(64))
                {
                    meshCollider++;
                }

                if (ids.Contains(65))
                {
                    boxCollider++;
                }
            }

            Console.WriteLine("  调色板 " + label + "：模板数=" + fileIds.Count.ToString(CultureInfo.InvariantCulture)
                              + "（含 MeshCollider=" + meshCollider.ToString(CultureInfo.InvariantCulture)
                              + "，含 BoxCollider=" + boxCollider.ToString(CultureInfo.InvariantCulture) + "）");
        }

        /// <summary>每个出现的 classID 是否都只出现一次（模板里没有重复组件）。</summary>
        private static bool HasEachClassIdOnce(List<int> classIds)
        {
            HashSet<int> seen = new HashSet<int>();
            for (int i = 0; i < classIds.Count; i++)
            {
                if (!seen.Add(classIds[i]))
                {
                    return false;
                }
            }

            return true;
        }

        // ==================================================================== G

        /// <summary>
        /// 第三轮源码不变量（文本级；不替代 Unity 内行为验证）。
        ///
        /// 它防的是四类回归：
        ///   F8 把临时场景换回 `EditorSceneManager.NewScene(Additive)`（既会被未保存场景阻断，
        ///      又回到“与源场景共享默认物理世界”）；
        ///   F9 又向摘要里写 AssetDatabase 编号 / 实例 ID；
        ///   F10 把“第一处 Unity 组件表异常就停”改成继续跑；
        ///   F11 诊断菜单变成“会创建对象”的（那就失去零风险意义）。
        /// </summary>
        private static void CheckRound3Invariants(string repoRoot)
        {
            string path = Path.Combine(repoRoot, BuildScriptRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Check(File.Exists(path), "C1 烘焙源码存在（G 段）：" + BuildScriptRelativePath);
            if (!File.Exists(path))
            {
                return;
            }

            string code = StripComments(File.ReadAllText(path, Encoding.UTF8));

            // ---- F8：物理世界隔离门
            CheckContains(code, "private static bool TryCreateIsolatedScratchScene(",
                          "F8 存在“先证明、再动手”的临时场景建立函数");
            CheckContains(code, "SceneManager.CreateScene(ScratchSceneName,",
                          "F8 候选 (a)：SceneManager.CreateScene（唯一有“场景自有 3D 物理场景”文档的 API）");
            CheckContains(code, "new CreateSceneParameters(LocalPhysicsMode.Physics3D)",
                          "F8 候选 (a) 显式请求 LocalPhysicsMode.Physics3D");
            CheckContains(code, "EditorSceneManager.NewPreviewScene()",
                          "F8 候选 (b)：EditorSceneManager.NewPreviewScene（编辑期必然合法）");
            CheckContains(code, "private static bool SceneOwnsSeparatePhysics(",
                          "F8 物理隔离的**运行期硬证明**函数存在");
            CheckContains(code, "Physics.defaultPhysicsScene",
                          "F8 隔离判据用 Physics.defaultPhysicsScene（与共享默认世界区分）");
            CheckContains(code, "scene.GetPhysicsScene()",
                          "F8 隔离判据用 Scene.GetPhysicsScene()（扩展方法，PhysicsModule）");

            // F8 的顺序：必须先建立并证明隔离世界，再搭地图（否则就已经在共享世界上添碰撞体了）。
            CheckOrder(code, "if (!TryCreateIsolatedScratchScene(", "mapRoot = BuildMapHierarchy(",
                       "F8 顺序：先证明隔离世界，再搭地图层级");

            // F8 不再用被“未保存场景”阻断的 NewScene(Additive)。
            Check(!code.Contains("EditorSceneManager.NewScene("),
                  "F8 不再使用 EditorSceneManager.NewScene(（它会被未保存场景阻断，且回到共享物理世界）");

            // F8：所有自建对象必须显式搬进隔离场景（不能靠 ActiveScene）。
            CheckContains(code, "private static GameObject NewScratchObject(",
                          "F8 自建对象统一走 NewScratchObject（显式 MoveGameObjectToScene）");
            CheckContains(code, "SceneManager.MoveGameObjectToScene(go, _scratchScene);",
                          "F8 自建对象被显式搬进隔离场景（预览场景不能当活动场景）");

            // F8：对象创建面收敛（BuildMapHierarchy 里不得再出现裸的 new GameObject）。
            string buildMapRegion = Region(code, "private static GameObject BuildMapHierarchy(",
                                          "// ==================================================================== F8");
            Check(buildMapRegion != null && !buildMapRegion.Contains("new GameObject("),
                  "F8 BuildMapHierarchy 里已无裸 new GameObject(（全部走 NewScratchObject）");

            // ---- F9：摘要确定性
            Check(!code.Contains("TryGetGUIDAndLocalFileIdentifier"),
                  "F9 摘要流不再使用 AssetDatabase.TryGetGUIDAndLocalFileIdentifier（本地编号）");
            Check(!code.Contains("SceneFileIdOf"),
                  "F9 旧的 SceneFileIdOf（guid:localId:name）已彻底删除");
            CheckContains(code, "EditorUtility.IsPersistent(o)",
                          "F9 对象引用身份的分支判据改为内容判据 EditorUtility.IsPersistent");
            CheckContains(code, "\"scene-node|GameObject|\"",
                          "F9 场景内对象用纯结构身份（类型 + 场景内名字路径）写进摘要");
            CheckContains(code, "if (!TryWriteTextFile(DigestDumpRelativePath, digestStream, out digestDumpError))",
                          "F9 整份摘要流落盘（跨会话逐字符比对的依据）");
            CheckContains(code, "string digestStreamAgain = BuildDigestStream(",
                          "F9 同一次烘焙内**算两遍并比对**摘要流");

            // 摘要构造区（BuildDigestStream … 地图层级搭建）不得出现实例 ID。
            string digestRegion = Region(code, "private static string BuildDigestStream(",
                                        "private static GameObject BuildMapHierarchy(");
            Check(digestRegion != null && !digestRegion.Contains("GetInstanceID"),
                  "F9 摘要构造区（BuildDigestStream / AppendCanonicalNode / DumpComponent / "
                  + "CanonicalPropertyValue / ObjectReferenceIdentity）里没有 GetInstanceID");

            // ---- F10：插桩 + 第一处异常即停
            CheckContains(code, "Application.logMessageReceived += OnUnityLog;",
                          "F10 装上 Unity 自家日志钩子（抓 CheckConsistency）");
            CheckContains(code, "private const string UnityConsistencyMarker = \"CheckConsistency:\";",
                          "F10 一致性标记常量为 CheckConsistency:");
            CheckContains(code, "HashSet<Type> addedByUs = new HashSet<Type>();",
                          "F10 判重改用我们自己的 addedByUs 记账（GetComponent 不再是唯一判据）");
            CheckContains(code, "if (owner != dest)",
                          "F10 拷完字段直接问组件“你属于谁”（不依赖日志过滤的 fail-fast）");
            CheckContains(code, "Unity组件登记异常:",
                          "F10 把“记账没拷过但 GetComponent 命中”单独归类上报（不是“模板含重复组件”）");
            CheckContains(code, "private static string DescribeComponentInventory(GameObject go)",
                          "F10 能打出 dest 完整组件清单（含每个组件的 owner）");
            CheckContains(code, "private static string DescribeSourceComponents(Component[] components)",
                          "F10 能打出 source 组件类型序列与逐类型个数");
            CheckContains(code, "private static void NoteDestCreated(GameObject go)",
                          "F10 记录 dest 创建序号 / instanceID / 父路径（同一 dest 被处理两次能直接看出）");

            CheckContains(code, "AnimatorControllerParameter[] definitions = controller.parameters;",
                          "Editor参数校验读取控制器定义（静态约束）");
            Check(!code.Contains("animators[0].parameters") && !code.Contains("allAnimators[i].parameters"),
                  "Editor不从未初始化prefab Animator读取运行时参数（静态约束）");

            // F10 的顺序：记账判重必须在 AddComponent 之前。
            CheckOrder(code, "if (!(component is Collider) && addedByUs.Contains(componentType))",
                       "Component copy = dest.AddComponent(componentType);",
                       "F10 记账判重在 AddComponent 之前（顺序）");

            // ---- F11：只读诊断菜单
            CheckContains(code, "[MenuItem(\"Build/Diagnose PMNet Battle Content (no bake)\")]",
                          "F11 存在只读诊断菜单 Build/Diagnose PMNet Battle Content (no bake)");
            CheckContains(code, "public static bool DiagnoseContent()",
                          "F11 诊断实现存在");
            CheckContains(code, "[MenuItem(\"Build/Diagnose PMNet Physics Isolation (empty scene probe)\")]",
                          "F11 存在物理隔离探针菜单（解决问题1 BLOCKED 所需的最小 Unity 内动作）");

            // F11 的硬约束：诊断实现里不得创建任何对象。
            string diagnoseRegion = Region(code, "public static bool DiagnoseContent()",
                                          "private static void DumpEnvironmentFacts(");
            Check(diagnoseRegion != null
                  && !diagnoseRegion.Contains("NewScratchObject(")
                  && !diagnoseRegion.Contains("new GameObject(")
                  && !diagnoseRegion.Contains("AddComponent("),
                  "F11 只读诊断实现里**没有**任何对象/组件创建（不创建 GameObjects、不加 Collider）");
        }

        // ==================================================================== H

        /// <summary>
        /// 显式字段拷贝的**支持集**（classID，与 C1 的 `IsExplicitCopySupported` 一一对应）。
        ///   4 / 224 = Transform / RectTransform（拷贝时跳过，但允许出现）
        ///   33 = MeshFilter，23 = MeshRenderer，205 = LODGroup
        ///   65 = BoxCollider，64 = MeshCollider，135 = SphereCollider，136 = CapsuleCollider
        ///   54 = Rigidbody（仅 kinematic）
        /// </summary>
        private static readonly Dictionary<int, string> ExplicitCopySupportedClassIds =
            new Dictionary<int, string>
            {
                { 4, "Transform" },
                { 224, "RectTransform" },
                { 33, "MeshFilter" },
                { 23, "MeshRenderer" },
                { 205, "LODGroup" },
                { 65, "BoxCollider" },
                { 64, "MeshCollider" },
                { 135, "SphereCollider" },
                { 136, "CapsuleCollider" },
                { 54, "Rigidbody" },
            };

        /// <summary>
        /// 第四轮：C1 把“通用序列化字段搬运”换成了“明确类型的公开 API 拷贝”，并规定**未实现的类型在
        /// AddComponent 之前拒绝**。于是“支持集是否覆盖真实源数据”变成一个**会让烘焙直接失败**的前提 ——
        /// 它必须在 CLI 里可见，而不是等用户在 Unity 里点一次菜单才发现。
        ///
        /// 本段做两件事：
        ///   A) **源数据级**：把 floors/walls/obstacles/Grasses/trees 五套调色板的模板**及其全部后代**、
        ///      以及地面对象，逐个节点的组件 classID 穷举出来，断言它们全部落在支持集内（不在就失败，
        ///      并打印是哪个路径的哪个 classID）；
        ///   B) **源码级**：断言 C1 真的不再有通用序列化写入（`CopyFromSerializedProperty` /
        ///      `ApplyModifiedProperties*` / `.arraySize` / `TryGetArraySize` / `VerifyArraySizes`），
        ///      并且支持集的每个类型名都在 `IsExplicitCopySupported` 里出现（两边不会各说各话）。
        ///
        /// 口径：A 是**源数据**检查（不是字符串检查）；B 是**静态结构**检查。两者都**不**证明
        /// Unity 真的能把这些字段拷过去 —— 那只能在 Unity 内跑自测/烘焙（本轮 PENDING_USER）。
        ///
        /// 已知局限（诚实记录）：A 用“路径前缀”判定后代，因此**同路径重名对象会被一并计入**；
        /// 源场景里模板路径唯一，所以当前不受影响。若将来出现重名，这段会宁多报不少报。
        /// </summary>
        private static void CheckTemplateComponentSupport(SceneModel scene, LogicFields logic, string repoRoot,
                                                         string scenePath)
        {
            if (logic == null)
            {
                Check(false, "前置缺失：B 段失败，H 段无法判定");
                return;
            }

            Dictionary<int, int> census = new Dictionary<int, int>();
            List<string> unsupported = new List<string>();
            HashSet<long> visitedNodes = new HashSet<long>();

            CheckPaletteSupport(scene, "floors", logic.Floors, census, unsupported, visitedNodes);
            CheckPaletteSupport(scene, "walls", logic.Walls, census, unsupported, visitedNodes);
            CheckPaletteSupport(scene, "obstacles", logic.Obstacles, census, unsupported, visitedNodes);
            CheckPaletteSupport(scene, "Grasses", logic.Grasses, census, unsupported, visitedNodes);
            CheckPaletteSupport(scene, "trees", logic.Trees, census, unsupported, visitedNodes);

            // 地面也走同一条拷贝路径（BuildMapHierarchy 的 groundClone）。
            long ground = scene.FindGameObjectByPath(GroundSourcePath);
            if (ground != 0)
            {
                CheckOneNodeSupport(scene, ground, census, unsupported, visitedNodes, "地面 " + GroundSourcePath);
            }
            else
            {
                Check(false, "地面 \"" + GroundSourcePath + "\" 找不到：H 段无法覆盖它的组件集合");
            }

            Check(unsupported.Count == 0,
                  "地图模板/地面层级里的组件类型全部落在显式拷贝支持集内（检查节点="
                  + visitedNodes.Count.ToString(CultureInfo.InvariantCulture)
                  + "；不支持=" + (unsupported.Count == 0 ? "无" : string.Join(" | ", unsupported.ToArray())) + "）");

            Console.WriteLine("      组件 classID 普查（模板 + 地面，含全部后代）：");
            List<int> keys = new List<int>(census.Keys);
            keys.Sort();
            for (int i = 0; i < keys.Count; i++)
            {
                Console.WriteLine("        classID " + keys[i].ToString(CultureInfo.InvariantCulture)
                                  + " (" + ClassIdName(keys[i]) + ") × "
                                  + census[keys[i]].ToString(CultureInfo.InvariantCulture));
            }

            CheckContainsSourceSupportSet(repoRoot);
            CheckNoUncopyableSourceReferenceFields(scenePath);
        }

        /// <summary>
        /// 源数据门：`Renderer` 上有一批**指向其他对象的序列化引用**在 Unity 2019.4 的公开 API 面上不可搬
        /// （`m_LightProbeVolumeOverride` / `m_StaticBatchRoot` / `m_LightmapParameters` 没有可写公开属性；
        /// `m_ProbeAnchor` 有公开属性，C1 会按源→目标映射重绑定 —— 但当前源上全是空），而第四轮又禁止用通用
        /// 序列化写入去搬它们。因此用**源数据**兜住：源场景里它们要么不出现，要么是 `{fileID: 0}`。
        ///
        /// 一旦真的出现非空值，C1 的显式拷贝就搬不了它 —— 那时候必须是门禁失败，而不是静默丢引用。
        /// （源场景实测：这四个字段全部 291/21 均为 `{fileID: 0}`；`m_LightProbeProxyVolume` 则根本不出现。）
        /// </summary>
        private static void CheckNoUncopyableSourceReferenceFields(string scenePath)
        {
            if (!File.Exists(scenePath))
            {
                Check(false, "H 源数据：源场景文件不存在，无法检查不可搬运的引用字段：" + scenePath);
                return;
            }

            string text = File.ReadAllText(scenePath, Encoding.UTF8);
            string[] fields =
            {
                "m_LightProbeProxyVolume",
                "m_LightProbeVolumeOverride",
                "m_StaticBatchRoot",
                "m_LightmapParameters",
                "m_ProbeAnchor",
            };

            for (int f = 0; f < fields.Length; f++)
            {
                string field = fields[f];
                MatchCollection matches = Regex.Matches(text,
                    Regex.Escape(field) + @":\s*\{fileID:\s*(-?\d+)\}");

                int nonZero = 0;
                for (int i = 0; i < matches.Count; i++)
                {
                    if (long.Parse(matches[i].Groups[1].Value, CultureInfo.InvariantCulture) != 0)
                    {
                        nonZero++;
                    }
                }

                Check(nonZero == 0,
                      "H 源数据：" + field + " 全部为 {fileID: 0} 或不出现（出现次数="
                      + matches.Count.ToString(CultureInfo.InvariantCulture) + "，非零="
                      + nonZero.ToString(CultureInfo.InvariantCulture)
                      + "）：该字段在 2019.4 没有可写的公开属性，靠这条门兜住“不静默丢引用”");
            }
        }

        private static void CheckPaletteSupport(SceneModel scene, string label, List<long> fileIds,
                                                Dictionary<int, int> census, List<string> unsupported,
                                                HashSet<long> visitedNodes)
        {
            for (int i = 0; i < fileIds.Count; i++)
            {
                CheckOneNodeSupport(scene, fileIds[i], census, unsupported, visitedNodes,
                                    label + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
            }
        }

        private static void CheckOneNodeSupport(SceneModel scene, long rootFileId,
                                                Dictionary<int, int> census, List<string> unsupported,
                                                HashSet<long> visitedNodes, string label)
        {
            List<long> nodes = scene.SelfAndDescendantGameObjectFileIds(rootFileId);
            if (nodes.Count == 0)
            {
                Check(false, label + " 在场景里展开不出任何节点（路径解析失败或对象不存在）");
                return;
            }

            for (int n = 0; n < nodes.Count; n++)
            {
                long goFileId = nodes[n];
                visitedNodes.Add(goFileId);

                List<int> classIds = scene.ComponentClassIdsOf(goFileId);
                for (int c = 0; c < classIds.Count; c++)
                {
                    int classId = classIds[c];
                    int count;
                    census.TryGetValue(classId, out count);
                    census[classId] = count + 1;

                    if (!ExplicitCopySupportedClassIds.ContainsKey(classId))
                    {
                        unsupported.Add(label + " → \"" + scene.PathOfGameObject(goFileId)
                                        + "\" classID=" + classId.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        private static string ClassIdName(int classId)
        {
            string name;
            return ExplicitCopySupportedClassIds.TryGetValue(classId, out name)
                ? name
                : "<支持集外>";
        }

        /// <summary>
        /// 源码级：C1 不再有通用序列化写入；支持集的每个类型名都出现在 `IsExplicitCopySupported` 里。
        /// </summary>
        private static void CheckContainsSourceSupportSet(string repoRoot)
        {
            string path = Path.Combine(repoRoot, BuildScriptRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Check(File.Exists(path), "C1 烘焙源码存在（H 段）：" + BuildScriptRelativePath);
            if (!File.Exists(path))
            {
                return;
            }

            string code = StripComments(File.ReadAllText(path, Encoding.UTF8));

            Check(!code.Contains("CopyFromSerializedProperty"),
                  "H 源码级：C1 不再调用 SerializedObject.CopyFromSerializedProperty（通用隐藏字段写入已删除）");
            Check(!code.Contains("ApplyModifiedProperties"),
                  "H 源码级：C1 不再调用 ApplyModifiedProperties / ApplyModifiedPropertiesWithoutUndo");
            Check(!code.Contains(".arraySize"),
                  "H 源码级：C1 不再读写 .arraySize（数组改由公开 API + 回读核对处理）");
            Check(!code.Contains("TryGetArraySize"),
                  "H 源码级：旧的 TryGetArraySize 守卫已随通用拷贝整体删除");
            Check(!code.Contains("VerifyArraySizes"),
                  "H 源码级：旧的 VerifyArraySizes 已随通用拷贝整体删除");

            string supportRegion = Region(code, "private static bool IsExplicitCopySupported(",
                                          "private static GameObject SafeOwner(");
            Check(supportRegion != null, "H 源码级：能定位 IsExplicitCopySupported 方法体");
            if (supportRegion == null)
            {
                return;
            }

            string[] names =
            {
                "MeshFilter", "MeshRenderer", "LODGroup", "BoxCollider",
                "SphereCollider", "CapsuleCollider", "MeshCollider", "Rigidbody",
            };
            for (int i = 0; i < names.Length; i++)
            {
                CheckContains(supportRegion, "typeof(" + names[i] + ")",
                              "H 源码级：IsExplicitCopySupported 覆盖 " + names[i]);
            }
        }

        /// <summary>取两个锚点之间的代码区段（用于“局部不得出现某记号”的断言）。</summary>
        private static string Region(string code, string startMarker, string endMarker)
        {
            int start = code.IndexOf(startMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            int end = code.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                end = code.Length;
            }

            return code.Substring(start, end - start);
        }

        private static string Excerpt(string text, int index)
        {
            int start = Math.Max(0, index - 32);
            int end = Math.Min(text.Length, index + 32);
            return text.Substring(start, end - start).Replace("\r", " ").Replace("\n", " ");
        }

        private static void CheckContains(string text, string needle, string label)
        {
            Check(text.Contains(needle), label + "（标记：" + Truncate(needle) + "）");
        }

        /// <summary>
        /// 去掉 C# 注释（字符串/字符字面量里的符号不动）。
        ///
        /// 为什么 §E 必须先剥注释：本文件的文档注释里**故意**写下了旧码的错误写法
        /// （`tile.SetActive(template.activeSelf)` 在 CopyComponentSet 之前）作为解释，
        /// 若直接在全文本里找字符串，会把“解释旧行为的注释”当成“还没修好”。
        /// </summary>
        private static string StripComments(string source)
        {
            StringBuilder sb = new StringBuilder(source.Length);
            int i = 0;
            while (i < source.Length)
            {
                char c = source[i];

                // 字符串字面量（含 verbatim 字符串 “@"...” 里的 "" 转义）
                if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
                {
                    sb.Append(c).Append('"');
                    i += 2;
                    while (i < source.Length)
                    {
                        char d = source[i];
                        sb.Append(d);
                        i++;
                        if (d == '"')
                        {
                            if (i < source.Length && source[i] == '"')
                            {
                                sb.Append('"');
                                i++;
                                continue;
                            }

                            break;
                        }
                    }

                    continue;
                }

                if (c == '"')
                {
                    sb.Append(c);
                    i++;
                    while (i < source.Length)
                    {
                        char d = source[i];
                        sb.Append(d);
                        i++;
                        if (d == '\\' && i < source.Length)
                        {
                            sb.Append(source[i]);
                            i++;
                            continue;
                        }

                        if (d == '"')
                        {
                            break;
                        }
                    }

                    continue;
                }

                if (c == '\'')
                {
                    sb.Append(c);
                    i++;
                    while (i < source.Length)
                    {
                        char d = source[i];
                        sb.Append(d);
                        i++;
                        if (d == '\\' && i < source.Length)
                        {
                            sb.Append(source[i]);
                            i++;
                            continue;
                        }

                        if (d == '\'')
                        {
                            break;
                        }
                    }

                    continue;
                }

                if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                    {
                        if (source[i] == '\n')
                        {
                            sb.Append('\n');   // 保留换行，便于看位置
                        }

                        i++;
                    }

                    i = Math.Min(i + 2, source.Length);
                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        private static void CheckOrder(string text, string first, string second, string label)
        {
            int a = text.IndexOf(first, StringComparison.Ordinal);
            int b = text.IndexOf(second, StringComparison.Ordinal);
            Check(a >= 0 && b >= 0 && a < b,
                  label + "（要求 \"" + Truncate(first) + "\" 出现在 \"" + Truncate(second) + "\" 之前；实际位置 "
                  + a.ToString(CultureInfo.InvariantCulture) + " / " + b.ToString(CultureInfo.InvariantCulture) + "）");
        }

        private static string Truncate(string value)
        {
            if (value == null)
            {
                return "<null>";
            }

            return value.Length <= 48 ? value : value.Substring(0, 48) + "…";
        }

        // ==================================================================== 纯文本 YAML 解析（最小面）

        private sealed class SceneDocument
        {
            public int ClassId;
            public long FileId;
            public string TypeName;
            public string Text;
            public long GameObjectFileId;
        }

        /// <summary>
        /// 只做本门禁需要的那部分文本解析：
        ///   · 按 `--- !u!<classID> &<fileID>` 切文档；
        ///   · GameObject 文档：m_Name / m_IsActive / m_Component 列表；
        ///   · Transform 文档：m_GameObject / m_Father / m_Children → 层级路径；
        ///   · MonoBehaviour 文档：m_Script 的 guid。
        /// 刻意不实现完整 YAML（那是另一个工程），因此每一条断言都带着"解析器读到了什么"的实据。
        /// </summary>
        private sealed class SceneModel
        {
            private readonly Dictionary<long, SceneDocument> _byFileId = new Dictionary<long, SceneDocument>();
            private readonly List<SceneDocument> _monoBehaviours = new List<SceneDocument>();
            private readonly Dictionary<long, long> _transformOfGameObject = new Dictionary<long, long>();
            private readonly Dictionary<long, long> _gameObjectOfTransform = new Dictionary<long, long>();
            private readonly Dictionary<long, long> _fatherOfTransform = new Dictionary<long, long>();
            private readonly Dictionary<string, long> _gameObjectByPath = new Dictionary<string, long>(StringComparer.Ordinal);
            private readonly Dictionary<long, string> _pathOfGameObject = new Dictionary<long, string>();

            public int DocumentCount { get; private set; }
            public int GameObjectCount { get; private set; }
            public int TransformCount { get; private set; }
            public int MonoBehaviourCount { get; private set; }
            public int RootGameObjectCount { get; private set; }

            public static SceneModel Load(string absolutePath)
            {
                SceneModel model = new SceneModel();
                string[] documents = Regex.Split(File.ReadAllText(absolutePath, Encoding.UTF8),
                                                 @"(?m)^--- !u!");
                for (int i = 0; i < documents.Length; i++)
                {
                    string body = documents[i];
                    Match header = Regex.Match(body, @"^(\d+) &(\d+)\r?\n(\w+):");
                    if (!header.Success)
                    {
                        continue;
                    }

                    SceneDocument doc = new SceneDocument();
                    doc.ClassId = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                    doc.FileId = long.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);
                    doc.TypeName = header.Groups[3].Value;
                    doc.Text = body;
                    model.DocumentCount++;

                    if (!model._byFileId.ContainsKey(doc.FileId))
                    {
                        model._byFileId[doc.FileId] = doc;
                    }

                    if (doc.ClassId == 1)
                    {
                        model.GameObjectCount++;
                    }
                    else if (doc.ClassId == 4 || doc.ClassId == 224)
                    {
                        model.TransformCount++;

                        long owner = ReadFileId(doc.Text, "m_GameObject");
                        long father = ReadFileId(doc.Text, "m_Father");
                        model._transformOfGameObject[owner] = doc.FileId;
                        model._gameObjectOfTransform[doc.FileId] = owner;
                        model._fatherOfTransform[doc.FileId] = father;
                    }
                    else if (doc.ClassId == 114)
                    {
                        model.MonoBehaviourCount++;
                        doc.GameObjectFileId = ReadFileId(doc.Text, "m_GameObject");
                        model._monoBehaviours.Add(doc);
                    }
                }

                model.BuildPaths();
                return model;
            }

            private void BuildPaths()
            {
                foreach (KeyValuePair<long, long> pair in _transformOfGameObject)
                {
                    long transform = pair.Value;
                    List<string> parts = new List<string>();
                    long cursor = transform;
                    int guard = 0;
                    while (cursor != 0 && guard++ < 128)
                    {
                        long owner;
                        if (!_gameObjectOfTransform.TryGetValue(cursor, out owner))
                        {
                            break;
                        }

                        SceneDocument go = FindGameObject(owner);
                        parts.Add(go == null ? "<" + owner.ToString(CultureInfo.InvariantCulture) + ">" : ReadName(go.Text));

                        long father;
                        _fatherOfTransform.TryGetValue(cursor, out father);
                        cursor = father;
                    }

                    parts.Reverse();
                    string path = string.Join("/", parts.ToArray());
                    _pathOfGameObject[pair.Key] = path;

                    if (!string.IsNullOrEmpty(path))
                    {
                        string key = path.Replace(@"\u", string.Empty)   // Unity 会把部分字符转义成 \uXXXX
                                         .Replace("\"", string.Empty)
                                         .Trim();
                        if (!_gameObjectByPath.ContainsKey(key))
                        {
                            _gameObjectByPath[key] = pair.Key;
                        }
                    }
                }

                foreach (KeyValuePair<long, string> pair in _pathOfGameObject)
                {
                    long transform;
                    if (_transformOfGameObject.TryGetValue(pair.Key, out transform))
                    {
                        long father;
                        if (_fatherOfTransform.TryGetValue(transform, out father) && (father == 0))
                        {
                            RootGameObjectCount++;
                        }
                    }
                }
            }

            public SceneDocument FindGameObject(long fileId)
            {
                SceneDocument doc;
                if (_byFileId.TryGetValue(fileId, out doc) && doc.ClassId == 1)
                {
                    return doc;
                }

                return null;
            }

            public long FindGameObjectByPath(string path)
            {
                long fileId;
                return _gameObjectByPath.TryGetValue(path, out fileId) ? fileId : 0;
            }

            public string PathOfGameObject(long fileId)
            {
                string path;
                return _pathOfGameObject.TryGetValue(fileId, out path) ? path : "<" + fileId.ToString(CultureInfo.InvariantCulture) + ">";
            }

            public SceneDocument FindMonoBehaviourByScriptGuid(string guid)
            {
                for (int i = 0; i < _monoBehaviours.Count; i++)
                {
                    SceneDocument doc = _monoBehaviours[i];
                    Match m = Regex.Match(doc.Text, @"m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-fA-F]{32})");
                    if (m.Success && string.Equals(m.Groups[1].Value, guid, StringComparison.Ordinal))
                    {
                        return doc;
                    }
                }

                return null;
            }

            /// <summary>
            /// 直接子物体数（按路径前缀精确匹配：前缀相同且剩余部分不再含 `/`）。
            /// 第三轮用它钉住 “P_PROP_well 没有子物体” 这个源事实 —— 那就是“重复组件不可能来自子物体”的证据。
            /// </summary>
            public int ChildCountOf(long gameObjectFileId)
            {
                string path = PathOfGameObject(gameObjectFileId);
                if (string.IsNullOrEmpty(path))
                {
                    return 0;
                }

                string prefix = path + "/";
                int count = 0;
                foreach (KeyValuePair<long, string> pair in _pathOfGameObject)
                {
                    if (!pair.Value.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (pair.Value.Substring(prefix.Length).IndexOf('/') < 0)
                    {
                        count++;
                    }
                }

                return count;
            }

            /// <summary>该 GameObject 的组件 classID 列表（按 m_Component 顺序）。</summary>
            public List<int> ComponentClassIdsOf(long gameObjectFileId)
            {
                List<int> classIds = new List<int>();
                SceneDocument go = FindGameObject(gameObjectFileId);
                if (go == null)
                {
                    return classIds;
                }

                Match components = Regex.Match(go.Text, @"(?s)m_Component:\s*\r?\n((?:\s*-\s*component:\s*\{fileID:\s*-?\d+\}\r?\n?)+)");
                if (!components.Success)
                {
                    return classIds;
                }

                foreach (Match m in Regex.Matches(components.Groups[1].Value, @"component:\s*\{fileID:\s*(-?\d+)\}"))
                {
                    long fileId = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    SceneDocument component;
                    if (_byFileId.TryGetValue(fileId, out component))
                    {
                        classIds.Add(component.ClassId);
                    }
                }

                return classIds;
            }

            /// <summary>
            /// 该 GameObject **自身 + 全部后代**的 fileID（按路径前缀，与 <see cref="ChildCountOf"/> 同源）。
            /// 第四轮用它把“模板子树里到底有哪些组件类型”穷举出来。
            /// </summary>
            public List<long> SelfAndDescendantGameObjectFileIds(long gameObjectFileId)
            {
                List<long> result = new List<long>();
                string path = PathOfGameObject(gameObjectFileId);

                // 找不到路径时返回空列表（调用方会把它当成失败，而不是当成“没有组件”）。
                if (string.IsNullOrEmpty(path) || path.StartsWith("<", StringComparison.Ordinal))
                {
                    return result;
                }

                string prefix = path + "/";
                foreach (KeyValuePair<long, string> pair in _pathOfGameObject)
                {
                    if (string.Equals(pair.Value, path, StringComparison.Ordinal)
                        || pair.Value.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        result.Add(pair.Key);
                    }
                }

                return result;
            }
        }

        // ==================================================================== 文本取值helpers

        private static int ReadInt(string text, string field, int fallback)
        {
            Match m = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(field) + @":\s*(-?\d+)\s*$");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : fallback;
        }

        private static string ReadToken(string text, string field)
        {
            Match m = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(field) + @":\s*(\S+)\s*$");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        /// <summary>取 GameObject 名（可含空格，因此不能用 <see cref="ReadToken"/> 的 \S+ 规则）。</summary>
        private static string ReadName(string text)
        {
            Match m = Regex.Match(text, @"(?m)^\s*m_Name:[ \t]*(.*?)\s*$");
            return m.Success ? m.Groups[1].Value.Trim() : string.Empty;
        }

        private static long ReadFileId(string text, string field)
        {
            Match m = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(field) + @":\s*\{fileID:\s*(-?\d+)\}");
            return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        private static void ReadFileIdArray(string text, string field, List<long> target)
        {
            Match m = Regex.Match(text, @"(?ms)^\s*" + Regex.Escape(field) + @":\s*\r?\n((?:\s*-\s*\{fileID:\s*-?\d+\}\r?\n?)+)");
            if (!m.Success)
            {
                return;
            }

            foreach (Match item in Regex.Matches(m.Groups[1].Value, @"\{fileID:\s*(-?\d+)\}"))
            {
                target.Add(long.Parse(item.Groups[1].Value, CultureInfo.InvariantCulture));
            }
        }

        private static string JoinInts(List<int> values)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append("/");
                }

                sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }

            return sb.Length == 0 ? "<无>" : sb.ToString();
        }

        // ==================================================================== 基础设施

        private static string FindRepoRoot()
        {
            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Client"))
                    && Directory.Exists(Path.Combine(dir.FullName, "Tools")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            return null;
        }

        private static void Section(string title)
        {
            Console.WriteLine("== " + title);
        }

        private static void Check(bool ok, string label)
        {
            _checks++;
            if (!ok)
            {
                _failed++;
            }

            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + label);
        }

        private static int Finish()
        {
            Console.WriteLine();
            Console.WriteLine("PMBattleContentSceneFactsCheck: 共 " + _checks.ToString(CultureInfo.InvariantCulture)
                              + " 项，失败 " + _failed.ToString(CultureInfo.InvariantCulture) + " 项。");
            Console.WriteLine(_failed == 0 ? "结果：全部通过" : "结果：存在失败（退出码 1）");
            return _failed == 0 ? 0 : 1;
        }
    }
}
