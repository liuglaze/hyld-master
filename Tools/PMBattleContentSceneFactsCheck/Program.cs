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

        // T-PLAY3（客户端专有光照）的源真相与契约源码路径
        private const string LightingSourceHierarchyPath = "HYLDGameTatal/MAP/lights";
        private const string LightingModuleRelativePath = "Client/Assets/Scripts/PMUnity/PMUnityBattleLighting.cs";
        private const string LightingMapRelativePath = "Client/Assets/Scripts/PMUnity/PMUnityBattleMap.cs";
        private const string LightingTableBeginMarker = ">>> PMLIGHT-DEFINITION-TABLE-BEGIN";
        private const string LightingTableEndMarker = "<<< PMLIGHT-DEFINITION-TABLE-END";

        /// <summary>源 lights 容器的序列化 localPosition（父链两级都是单位 TRS ⇒ 即世界位置）。</summary>
        private const double ExpectedLightingContainerY = -8.1;

        /// <summary>定义表的数值列数（name 之外的 20 列：kind..shadowNearPlane）。</summary>
        private const int LightingValueCount = 20;

        // ---- T-PLAY4（局内操控 UI）的源真相与契约源码路径 ----

        private const string GameUiPrefabRelativePath =
            "Client/Assets/HYLD1.0/Resources/Prefabs/GameUI.prefab";

        private const string GameUiPrefabMetaRelativePath =
            "Client/Assets/HYLD1.0/Resources/Prefabs/GameUI.prefab.meta";

        private const string GameUiPrefabGuid = "af998dab9af610e4986aa70670bf2cc6";

        private const string BattleControlsRelativePath =
            "Client/Assets/Scripts/PMUnity/PMUnityBattleControls.cs";

        private const string BattleControlsMetaRelativePath =
            "Client/Assets/Scripts/PMUnity/PMUnityBattleControls.cs.meta";

        // ---- T-LIVE3（世界空间瞄准指示器）的契约源码路径 ----

        private const string AimIndicatorRelativePath =
            "Client/Assets/Scripts/PMUnity/PMUnityBattleAimIndicator.cs";

        private const string R4UnityCheckCsprojRelativePath = "Tools/PMR4UnityCheck/PMR4UnityCheck.csproj";

        private const string UnityUiAssemblyRelativePath = "Client/Library/ScriptAssemblies/UnityEngine.UI.dll";

        private const string SessionHostRelativePath = "Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs";

        // 旧 prefab 里那三根 EasyJoystick 的冻结布局字段值（本工具对着 prefab 文本逐字核对）
        private const int LegacyJoystickZoneRadius = 100;
        private const int LegacyJoystickDeadZone = 20;
        private const int LegacyMoveJoystickAnchor = 7;
        private const int LegacyFireJoystickAnchor = 9;

        /// <summary>J 段要求**必须**出现在 UI 源码里的标记（缺失即失败）：只列“少了它就不可能守住契约”的那几处。</summary>
        private static readonly string[] UiRequiredTokens = new string[]
        {
            "Prefabs/GameUI",
            "Resources.Load<GameObject>(LegacyPrefabResourceKey)",
            "PMNetRuntime.IsDedicatedServer",
            "PMUnityBattleMoveInputHandler",
            "PMUnityBattleAttackInputHandler",
            "PMUnityBattleStatusQuery",
            "public void Dispose()",
            "TryNormalizeStick",
            "MinAimLength",
            "ScreenPointToLocalPointInRectangle",
            "EventSystem.current",
            "PMUnityBattleControlsPointerRelay",
            "PMNet.Shared.BattleNumericConfig.ManaMax",
            "PMNet.Shared.BattleNumericConfig.SuperEnergyMax",
            "#if UNITY_2019_1_OR_NEWER || UNITY_EDITOR",
            "#else",
            "public const bool UguiImplementationCompiled = true;",
            "public const bool UguiImplementationCompiled = false;",
            "LegacyJoystickZoneRadiusPixels = 100f",
            "LegacyJoystickDeadZoneRatio = 0.2f",
            "LegacyMoveJoystickAnchor = 7",
            "LegacyFireJoystickAnchor = 9",
            "LegacyPrefabRootWasInactive",
            "JoystickState.NoPointer",
        };

        /// <summary>
        /// J 段要求**绝不**出现在 UI 源码（去注释后）里的标记：旧脚本、旧联网、RPC/权威写、旧场景加载。
        /// 这些名字在旧 prefab 文本里确实存在（正是“零激活”必须由结构保证的原因），
        /// 所以任何一处落进可执行代码都必须让门禁失败。
        /// </summary>
        private static readonly string[] UiForbiddenTokens = new string[]
        {
            "Instantiate",
            "SetActive",
            "SendMessage",
            "FindObjectOfType",
            "FindObjectsOfType",
            "LoadScene",
            "SceneManager",
            "EasyTouch",
            "EasyJoystick",
            "EasyButton",
            "TouchLogic",
            "GameUITeamGemLogic",
            "HYLDHeropropertyUI",
            "BattleData",
            "BattleManger",
            "CommandManger",
            "UDPSocketManger",
            "HYLDManger",
            "HYLDStaticValue",
            "PMNet_",
            "ServerCombatAttackV1",
            "ClientCombatAttackResultV1",
            "PMR6CombatDriver",
            "HYLDCameraManger",
            "PlayerLogic",
        };

        /// <summary>J 段：UI 只读复用的 4 张 Sprite（节点路径 / Sprite guid / 源图相对路径）。</summary>
        private static readonly string[][] UiRequiredSprites = new string[][]
        {
            new string[] { "能量条/Background", "1ecc8b15ae5d98c41a0ecf57a4f118ed",
                           "Client/Assets/HYLD1.0/HYLDResource/SuperFireUI/FullBG.png" },
            new string[] { "能量条/Fill Area/Fill", "8e6b3e0a7f62eb044a849332e3c9a18b",
                           "Client/Assets/HYLD1.0/HYLDResource/SuperFireUI/FullPower.png" },
            new string[] { "能量条/Image", "ab50dae53b97cce488f2b49b637fe5dc",
                           "Client/Assets/HYLD1.0/HYLDResource/SuperFireUI/UnFullImage.png" },
            new string[] { "GemSelfTeamUI/Image", "6c515d343436ee4469a93989e49ec0a6",
                           "Client/Assets/HYLD1.0/Images/Gem 1.png" },
        };

        /// <summary>J 段：旧 prefab 里**确实挂着**的旧脚本（它们是“零激活”负例的事实依据）。</summary>
        private static readonly string[][] UiLegacyScripts = new string[][]
        {
            new string[] { "Android/PlayerMove", "6cb67c6dcb4e4d74eb7aed04254e4089", "EasyJoystick" },
            new string[] { "Android/FireNormal", "6cb67c6dcb4e4d74eb7aed04254e4089", "EasyJoystick" },
            new string[] { "Android/FireSuper", "6cb67c6dcb4e4d74eb7aed04254e4089", "EasyJoystick" },
            new string[] { "Android/FireNormalButton", "8011f44b5b7e78b4883c2c7968fe5e73", "EasyButton" },
            new string[] { "Android", "aed5e2a9afa81fc43a1fb0a943c8353e", "TouchLogic" },
            new string[] { "Android/EasyTouch", "42241010c6f9ddc46b78abdc21d505c5", "EasyTouch" },
            new string[] { "", "fc3a7e8d1250bcb4fbf3ec233416059a", "GameUITeamGemLogic（根）" },
            new string[] { "", "e2f754aa29fcc75459937c178b61f38f", "HYLDHeropropertyUI（根，带 backStart）" },
        };

        /// <summary>浮点字段对照容差（相对；远大于 float↔double 的十进制舍入，远小于任何真实参数差异）。</summary>
        private const double LightingTolerance = 1e-5;

        /// <summary>定义表列名（顺序 = PMBattleLightDefinition 构造函数参数顺序）。</summary>
        private static readonly string[] LightingColumnNames =
        {
            "kind", "colorR", "colorG", "colorB", "intensity", "range", "spotAngle", "innerSpotAngle",
            "localPosition.x", "localPosition.y", "localPosition.z",
            "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w",
            "shadowType", "shadowStrength", "shadowBias", "shadowNormalBias", "shadowNearPlane",
        };

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

            Section("I. T-PLAY3 客户端专有光照：源 YAML 逐字段对照 + 身份/隔离/释放静态门");
            CheckClientLightingSource(scene, repoRoot);

            Section("J. T-PLAY4 局内操控 UI：旧 GameUI 只读事实 + 零激活 + 指针/释放纯逻辑反例");
            CheckBattleControlsSource(repoRoot);

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

            /// <summary>按 fileID 取任意文档（找不到返回 null）。</summary>
            public SceneDocument FindDocument(long fileId)
            {
                SceneDocument doc;
                return _byFileId.TryGetValue(fileId, out doc) ? doc : null;
            }

            /// <summary>GameObject 的 Transform fileID（没有则返回 0）。</summary>
            public long TransformFileIdOf(long gameObjectFileId)
            {
                long transform;
                return _transformOfGameObject.TryGetValue(gameObjectFileId, out transform) ? transform : 0;
            }

            /// <summary>Transform 所属 GameObject 的 fileID（没有则返回 0）。</summary>
            public long GameObjectFileIdOfTransform(long transformFileId)
            {
                long owner;
                return _gameObjectOfTransform.TryGetValue(transformFileId, out owner) ? owner : 0;
            }

            /// <summary>该 GameObject 上指定 classID 的**第一个**组件 fileID（没有则返回 0）。</summary>
            public long ComponentFileIdOfClass(long gameObjectFileId, int classId)
            {
                SceneDocument go = FindGameObject(gameObjectFileId);
                if (go == null)
                {
                    return 0;
                }

                Match components = Regex.Match(go.Text, @"(?s)m_Component:\s*\r?\n((?:\s*-\s*component:\s*\{fileID:\s*-?\d+\}\r?\n?)+)");
                if (!components.Success)
                {
                    return 0;
                }

                foreach (Match m in Regex.Matches(components.Groups[1].Value, @"component:\s*\{fileID:\s*(-?\d+)\}"))
                {
                    long fileId = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    SceneDocument component;
                    if (_byFileId.TryGetValue(fileId, out component) && component.ClassId == classId)
                    {
                        return fileId;
                    }
                }

                return 0;
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


        // ==================================================================== I

        /// <summary>
        /// T-PLAY3 客户端专有光照的**源真相门禁**（纯文本/纯数据，不加载引擎）：
        ///   A) 源场景 HYLDGameTatal/MAP/lights 的层级、父链与三盏灯的**真实序列化值**（文本 YAML）；
        ///   B) 新模块 PMUnityBattleLighting.cs 冻结定义表的**逐字段对照**（必须与 A 同值）；
        ///   C) 对照函数本身的**负例**（改一个字段必须被判为不一致，防止"永远通过"）；
        ///   D) 身份门（DS 零灯）/隔离场景/独立根节点/不参与物理/不改全局光照/精确释放的静态结构断言。
        ///
        /// 口径（不许被读成实机证据）：A 是源数据，B 是"表是否照抄源"，C 是"对照真的会失败"，
        /// D 是**文本级结构断言**。它们都**不**证明 Unity 里渲染出什么颜色、也不证明 DS 运行期真的 0 盏灯
        /// （那要真实 Unity2019 + 双端实机，见报告 T-PLAY5 段）。本工程不加载引擎、不跑 PhysX、不启动 Unity。
        /// </summary>
        private static void CheckClientLightingSource(SceneModel scene, string repoRoot)
        {
            // ================================================================ A. 源场景层级与父链
            long lightsGo = scene.FindGameObjectByPath(LightingSourceHierarchyPath);
            Check(lightsGo != 0, "源场景存在 GameObject「" + LightingSourceHierarchyPath + "」（fileID="
                                 + lightsGo.ToString(CultureInfo.InvariantCulture) + "）");
            if (lightsGo == 0)
            {
                return;
            }

            // 父链两级都是单位 TRS —— 这是模块头部"容器 local TRS 即世界 TRS"推导的前提。
            CheckUnitTransform(scene, "HYLDGameTatal", "源父节点 HYLDGameTatal 是单位 TRS（pos 0 / rot identity / scale 1）");
            CheckUnitTransform(scene, "HYLDGameTatal/MAP", "源父节点 HYLDGameTatal/MAP 是单位 TRS（pos 0 / rot identity / scale 1）");

            long lightsTransform = scene.TransformFileIdOf(lightsGo);
            SceneDocument lightsTransformDoc = scene.FindDocument(lightsTransform);
            Check(lightsTransformDoc != null, "「lights」有 Transform 文档（fileID="
                                             + lightsTransform.ToString(CultureInfo.InvariantCulture) + "）");

            double[] containerPosition = null;
            double[] containerRotation = null;
            bool hasContainerPosition = lightsTransformDoc != null
                                        && TryReadStructComponents(lightsTransformDoc.Text, "m_LocalPosition", out containerPosition);
            bool hasContainerRotation = lightsTransformDoc != null
                                        && TryReadStructComponents(lightsTransformDoc.Text, "m_LocalRotation", out containerRotation);

            Check(hasContainerPosition && containerPosition.Length == 3, "「lights」Transform 有 m_LocalPosition（x/y/z）");
            Check(hasContainerRotation && containerRotation.Length == 4, "「lights」Transform 有 m_LocalRotation（x/y/z/w）");

            if (hasContainerPosition)
            {
                Check(Math.Abs(containerPosition[1] - ExpectedLightingContainerY) <= 1e-4,
                      "「lights」容器 localPosition.y = " + containerPosition[1].ToString("R", CultureInfo.InvariantCulture)
                      + "（期望 " + ExpectedLightingContainerY.ToString("R", CultureInfo.InvariantCulture) + "）");
            }

            if (hasContainerRotation)
            {
                Check(Math.Abs(containerRotation[0]) <= 1e-4 && Math.Abs(containerRotation[1]) <= 1e-4
                      && Math.Abs(containerRotation[2]) <= 1e-4 && Math.Abs(containerRotation[3] - 1.0) <= 1e-4,
                      "「lights」容器 localRotation 是单位旋转（0,0,0,1）");
            }

            // 源 m_Children 顺序 = 定义表行顺序（模块注释已写明依赖它，这里把它钉成事实）。
            List<long> childTransformIds = new List<long>();
            if (lightsTransformDoc != null)
            {
                ReadFileIdArray(lightsTransformDoc.Text, "m_Children", childTransformIds);
            }

            Check(childTransformIds.Count == 3, "「lights」有 3 个直系子对象（实际 "
                                                + childTransformIds.Count.ToString(CultureInfo.InvariantCulture) + " 个）");

            List<SourceLight> sources = new List<SourceLight>();
            for (int i = 0; i < childTransformIds.Count; i++)
            {
                long childGo = scene.GameObjectFileIdOfTransform(childTransformIds[i]);
                SceneDocument childGoDoc = scene.FindGameObject(childGo);
                SceneDocument childTransformDoc = scene.FindDocument(scene.TransformFileIdOf(childGo));
                long childLightId = scene.ComponentFileIdOfClass(childGo, 108);
                SceneDocument childLightDoc = scene.FindDocument(childLightId);

                string childTag = "「lights」第 " + i.ToString(CultureInfo.InvariantCulture) + " 个子对象";
                Check(childGoDoc != null && childTransformDoc != null && childLightDoc != null,
                      childTag + "有 GameObject/Transform/Light 三份文档（gameObject="
                      + childGo.ToString(CultureInfo.InvariantCulture) + "，light="
                      + childLightId.ToString(CultureInfo.InvariantCulture) + "）");
                if (childGoDoc == null || childTransformDoc == null || childLightDoc == null)
                {
                    continue;
                }

                SourceLight source = new SourceLight();
                source.Name = ReadName(childGoDoc.Text);
                source.LightComponentCount = CountComponentsOfClass(scene, childGo, 108);
                source.Type = ReadInt(childLightDoc.Text, "m_Type", -1);
                source.Lightmapping = ReadInt(childLightDoc.Text, "m_Lightmapping", -1);
                source.RenderMode = ReadInt(childLightDoc.Text, "m_RenderMode", -1);
                source.UseColorTemperature = ReadInt(childLightDoc.Text, "m_UseColorTemperature", -1);
                source.CullingMaskBits = ReadLongField(childLightDoc.Text, "m_Bits", long.MinValue);
                source.Values = ReadLightValues(childLightDoc.Text, childTransformDoc.Text);

                Check(source.LightComponentCount == 1, childTag + "（" + source.Name + "）恰好 1 个 Light 组件（实际 "
                                                       + source.LightComponentCount.ToString(CultureInfo.InvariantCulture) + "）");
                Check(source.CullingMaskBits == 4294967295L, childTag + "（" + source.Name
                                                        + "）m_CullingMask.m_Bits = 4294967295（即运行期的 ~0，实际 "
                                                        + source.CullingMaskBits.ToString(CultureInfo.InvariantCulture) + "）");
                Check(source.Lightmapping == 4, childTag + "（" + source.Name + "）m_Lightmapping = 4（Realtime，实际 "
                                                + source.Lightmapping.ToString(CultureInfo.InvariantCulture) + "）");
                Check(source.RenderMode == 0, childTag + "（" + source.Name + "）m_RenderMode = 0（Auto，实际 "
                                              + source.RenderMode.ToString(CultureInfo.InvariantCulture) + "）");
                Check(source.UseColorTemperature == 0, childTag + "（" + source.Name
                                                       + "）m_UseColorTemperature = 0（6570K 不生效，实际 "
                                                       + source.UseColorTemperature.ToString(CultureInfo.InvariantCulture) + "）");

                sources.Add(source);
            }

            // ================================================================ B. 模块冻结定义表 ↔ 源 YAML
            string modulePath = Path.Combine(repoRoot, LightingModuleRelativePath.Replace('/', Path.DirectorySeparatorChar));
            string mapPath = Path.Combine(repoRoot, LightingMapRelativePath.Replace('/', Path.DirectorySeparatorChar));

            Check(File.Exists(modulePath), "光照模块存在：" + LightingModuleRelativePath);
            if (!File.Exists(modulePath))
            {
                return;
            }

            string moduleCode = File.ReadAllText(modulePath, Encoding.UTF8);
            string moduleNoComments = StripComments(moduleCode);

            CheckContains(moduleCode, LightingTableBeginMarker, "光照模块定义表起锚点");
            CheckContains(moduleCode, LightingTableEndMarker, "光照模块定义表止锚点");

            List<ParsedLightDefinition> definitions = ParseLightDefinitions(moduleCode);
            Check(definitions.Count == 3, "定义表恰好 3 行（实际 "
                                          + definitions.Count.ToString(CultureInfo.InvariantCulture) + " 行）");

            double containerX = 0.0, containerY = 0.0, containerZ = 0.0;
            double containerRotX = 0.0, containerRotY = 0.0, containerRotZ = 0.0, containerRotW = 0.0;
            bool hasContainerXY = TryReadConstFloat(moduleCode, "ContainerPositionX", out containerX);
            bool hasContainerYY = TryReadConstFloat(moduleCode, "ContainerPositionY", out containerY);
            bool hasContainerZY = TryReadConstFloat(moduleCode, "ContainerPositionZ", out containerZ);
            bool hasContainerRX = TryReadConstFloat(moduleCode, "ContainerRotationX", out containerRotX);
            bool hasContainerRY = TryReadConstFloat(moduleCode, "ContainerRotationY", out containerRotY);
            bool hasContainerRZ = TryReadConstFloat(moduleCode, "ContainerRotationZ", out containerRotZ);
            bool hasContainerRW = TryReadConstFloat(moduleCode, "ContainerRotationW", out containerRotW);

            Check(hasContainerXY && hasContainerYY && hasContainerZY,
                  "模块声明 ContainerPositionX/Y/Z 三个常量");
            Check(hasContainerRX && hasContainerRY && hasContainerRZ && hasContainerRW,
                  "模块声明 ContainerRotationX/Y/Z/W 四个常量");
            CheckContains(moduleCode, "public const int ExpectedLightCount = 3;", "模块声明 ExpectedLightCount = 3");
            CheckContains(moduleCode, "public const float BounceIntensity = 1f;", "模块声明 BounceIntensity = 1f");

            // 容器常量必须等于源 lights Transform 的 local TRS（父链已证明是单位 TRS ⇒ 同世界 TRS）。
            if (hasContainerPosition && hasContainerXY && hasContainerYY && hasContainerZY)
            {
                Check(Math.Abs(containerX - containerPosition[0]) <= 1e-4
                      && Math.Abs(containerY - containerPosition[1]) <= 1e-4
                      && Math.Abs(containerZ - containerPosition[2]) <= 1e-4,
                      "模块容器位置常量 == 源 lights localPosition ("
                      + containerPosition[0].ToString("R", CultureInfo.InvariantCulture) + ", "
                      + containerPosition[1].ToString("R", CultureInfo.InvariantCulture) + ", "
                      + containerPosition[2].ToString("R", CultureInfo.InvariantCulture) + ")");
            }

            if (hasContainerRotation && hasContainerRX && hasContainerRY && hasContainerRZ && hasContainerRW)
            {
                Check(Math.Abs(containerRotX - containerRotation[0]) <= 1e-4
                      && Math.Abs(containerRotY - containerRotation[1]) <= 1e-4
                      && Math.Abs(containerRotZ - containerRotation[2]) <= 1e-4
                      && Math.Abs(containerRotW - containerRotation[3]) <= 1e-4,
                      "模块容器旋转常量 == 源 lights localRotation（0,0,0,1）");
            }

            CheckContains(moduleCode, "public const string SourceSceneRelativePath = \"Client/Assets/Scenes/HYLDGame.unity\";",
                          "模块写明参数来源场景路径");
            CheckContains(moduleCode, "public const string SourceHierarchyPath = \"HYLDGameTatal/MAP/lights\";",
                          "模块写明参数来源层级路径");

            List<string> mismatches = new List<string>();
            int compareCount = Math.Min(definitions.Count, sources.Count);
            for (int i = 0; i < compareCount; i++)
            {
                string mismatch = CompareLightDefinitionToSource(definitions[i], sources[i]);
                if (mismatch != null)
                {
                    mismatches.Add("第 " + i.ToString(CultureInfo.InvariantCulture) + " 盏：" + mismatch);
                }
            }

            Check(mismatches.Count == 0 && definitions.Count == sources.Count,
                  "模块定义表逐字段等于源场景 MAP/lights 的真实序列化值（3 盏 × 20 列"
                  + (mismatches.Count == 0 ? "" : "；不一致：" + string.Join(" | ", mismatches.ToArray())) + "）");

            // 表里唯一允许"不抄"的是明确登记的惰性字段（cookie/flare/halo/shape/colorTemperature）：
            // 代码里必须仍然写着这条边界，避免后来者以为"已经逐字段等价"。
            CheckContains(moduleCode, "未复制", "模块明确登记未复制的源字段（不伪称逐字段等价）");
            CheckContains(moduleCode, "HYLDStart", "模块写明 HYLDStart 暖方向光仍在叠加");
            CheckContains(moduleCode, "不等于", "模块写明不等于原场景逐像素结果");

            // ================================================================ C. 负例：对照必须真的会失败
            if (definitions.Count == 3 && sources.Count == 3)
            {
                Check(CompareLightDefinitionToSource(definitions[0], sources[0]) == null,
                      "负例前置：未改动的方向光定义与源一致（对照函数不是恒真）");

                Check(NegativeLightingCase(definitions[0], sources[0], 4, 999.0, "intensity"),
                      "负例：方向光 intensity 被改 ⇒ 对照必须判为不一致");
                Check(NegativeLightingCase(definitions[1], sources[1], 6, 1.0, "spotAngle"),
                      "负例：聚光 #1 的 spotAngle 被改 ⇒ 对照必须判为不一致");
                Check(NegativeLightingCase(definitions[1], sources[1], 2, 0.5, "colorG"),
                      "负例：聚光 #1 的 colorG 被改 ⇒ 对照必须判为不一致");
                Check(NegativeLightingCase(definitions[0], sources[0], 11, 1.0, "localRotation.x"),
                      "负例：方向光旋转 x 被改成单位值 ⇒ 对照必须判为不一致");
                Check(NegativeLightingCase(definitions[2], sources[2], 18, 9.0, "shadowNormalBias"),
                      "负例：聚光 #2 的 shadowNormalBias 被改 ⇒ 对照必须判为不一致");
                Check(NegativeLightingCase(definitions[2], sources[2], 9, -999.0, "localPosition.y"),
                      "负例：聚光 #2 的 localPosition.y 被改 ⇒ 对照必须判为不一致");

                ParsedLightDefinition renamed = definitions[0];
                renamed.Name = "Directional Light(改)";
                Check(CompareLightDefinitionToSource(renamed, sources[0]) != null,
                      "负例：方向光名称被改 ⇒ 对照必须判为不一致");
            }

            // ================================================================ D. 身份/隔离/物理/释放 静态门
            // 编译面（受写入边界限制的妥协）必须写明，避免后来者以为 #if 是"编辑器专用"。
            CheckContains(moduleCode, "#if UNITY_2019_1_OR_NEWER || UNITY_EDITOR",
                          "光照实现区用 UNITY_2019_1_OR_NEWER || UNITY_EDITOR 圈定（真实 Unity 构建含 Player 仍编入）");
            CheckContains(moduleCode, "UnityStubs.cs",
                          "光照模块注明桩件缺 Light 的编译面理由（登记项，不是静默绕过）");

            // DS 零灯：身份判定必须出现在创建任何 Unity 对象之前。
            CheckContains(moduleNoComments, "PMNet.PMNetRuntime.IsDedicatedServer", "光照模块实现 DS 身份门");
            CheckOrder(moduleNoComments, "IsDedicatedServer", "new GameObject(",
                       "光照模块：DS 判定先于任何 new GameObject/Light");

            // 隔离场景 + 独立根（不挂地图 prefab 根 ⇒ 不进 Collider 白名单、不进场景摘要）。
            CheckContains(moduleCode, "SceneManager.MoveGameObjectToScene(container, scene)",
                          "光照模块把容器显式迁入本局隔离场景");
            CheckContains(moduleCode, "container.scene.handle != scene.handle",
                          "光照模块校验容器真的进了目标场景（scene mismatch 即失败）");
            Check(!moduleCode.Contains("container.transform.parent ="),
                  "光照容器是独立根节点（源码里没有把容器挂到别的 Transform 下）");
            CheckContains(moduleCode, "ContainerObjectName = \"[PMNetBattleLights]\"",
                          "光照容器对象名与地图根前缀 [PMNetBattleMap] 不同（便于实机核对）");

            // 不参与物理 / 不改全局光照与渲染设置 / 不写资产 / 不加载旧场景。
            CheckLightingCodeHasNoForbiddenSymbols(moduleNoComments);
            CheckContains(moduleCode, "light.cullingMask = ~0;", "光照模块复制剔除掩码（= ~0，源 4294967295）");
            CheckContains(moduleCode, "light.renderMode = LightRenderMode.Auto;", "光照模块复制渲染模式（源 0=Auto）");
            CheckContains(moduleCode, "light.lightmapBakeType = LightmapBakeType.Realtime;", "光照模块复制 lightmapBakeType（源 4=Realtime）");
            // ★ 回归门（来自用户 14:49 的真实 Build Player 失败）：该赋值只能存在于 **UNITY_EDITOR** 内。
            // `Light.lightmapBakeType` 在 Player 变体的 UnityEngine 程序集里不存在，无条件赋值会让
            // Build Player 以 CS1061 失败（Editor 播放却正常，属于最容易漏检的一类分叉）。
            // 断言用「赋值点前一小段窗口内必须出现 #if UNITY_EDITOR」，不依赖换行符形态（源文件是 CRLF）。
            int bakeCount = 0;
            int bakeScan = 0;
            while (true)
            {
                int hit = moduleCode.IndexOf("light.lightmapBakeType", bakeScan, StringComparison.Ordinal);
                if (hit < 0) { break; }
                bakeCount++;
                int windowStart = hit - 160;
                if (windowStart < 0) { windowStart = 0; }
                string window = moduleCode.Substring(windowStart, hit - windowStart);
                Check(window.Contains("#if UNITY_EDITOR"),
                      "光照 lightmapBakeType 第 " + bakeCount.ToString() + " 处赋值位于 UNITY_EDITOR 内"
                      + "（Player 无此成员：无条件赋值会在 Build Player 报 CS1061）");
                bakeScan = hit + 1;
            }
            Check(bakeCount == 1, "lightmapBakeType 只在唯一的编辑器分叉处出现（实际 " + bakeCount.ToString() + " 处）");
            CheckContains(moduleCode, "light.bounceIntensity = PMBattleLightingSource.BounceIntensity;", "光照模块复制弹射强度（源 1）");
            CheckContains(moduleCode, "light.useColorTemperature = false;", "光照模块保留 useColorTemperature=0 口径");
            CheckContains(moduleCode, "public static bool TryValidateDefinitions(out string error)",
                          "光照模块提供纯数据自检（运行期与门禁同一口径）");

            // 释放：幂等 + 销毁整个容器。
            CheckContains(moduleCode, "public void Dispose()", "光照模块提供 Dispose");
            CheckContains(moduleCode, "if (_disposed)", "光照模块 Dispose 幂等");
            CheckContains(moduleCode, "GameObject container = _container;",
                          "光照模块 Dispose 取的是自己持有的容器（不是别的对象）");

            // 只在 Dispose 区里做顺序断言：DestroyObject(container) 在 TryCreate 的失败清理路径里也出现，
            // 全文件 IndexOf 会命中那一次（本轮真被它骗过一次，故写成"先截区再断言"）。
            int disposeStart = moduleCode.IndexOf("public void Dispose()", StringComparison.Ordinal);
            string disposeRegion = disposeStart < 0 ? string.Empty : moduleCode.Substring(disposeStart);
            CheckOrder(disposeRegion, "GameObject container = _container;", "DestroyObject(container);",
                       "光照模块 Dispose 销毁自己持有的容器（3 盏子灯随之销毁）");

            // 地图接线：只在正式地图加载成功后创建；DS 先判；先销毁灯再销毁地图根。
            Check(File.Exists(mapPath), "地图模块存在：" + LightingMapRelativePath);
            if (!File.Exists(mapPath))
            {
                return;
            }

            string mapCode = File.ReadAllText(mapPath, Encoding.UTF8);

            CheckContains(mapCode, "created.ApplyClientLighting();", "地图模块在正式地图加载路径调用光源创建");
            CheckOrder(mapCode, "ValidateManifestConsistency(out error)", "created.ApplyClientLighting();",
                       "地图模块：光照创建发生在 manifest/结构自检之后");
            CheckOrder(mapCode, "created.ApplyClientLighting();", "map = created;",
                       "地图模块：光照创建发生在发布 map 之前");
            CheckContains(mapCode, "private void ApplyClientLighting()",
                          "光照创建是 void（结构上不可能让 TryLoad 返回失败 ⇒ 表现层不阻断权威对局）");
            CheckOrder(mapCode, "private void ApplyClientLighting()", "PMNet.PMNetRuntime.IsDedicatedServer",
                       "地图模块：DS 判定在 ApplyClientLighting 内");
            CheckOrder(mapCode, "PMNet.PMNetRuntime.IsDedicatedServer", "PMUnityBattleLighting.TryCreate",
                       "地图模块：DS 判定先于光源创建（DS 零灯）");
            CheckContains(mapCode, "public int ClientLightCount", "地图模块暴露 ClientLightCount（可观察口径）");
            CheckContains(mapCode, "public string LightingError", "地图模块暴露 LightingError（失败可见）");
            CheckOrder(mapCode, "lighting.Dispose()", "DestroyObject(root)",
                       "地图模块 Dispose：先销毁光源容器，再销毁地图根");
            Check(!mapCode.Contains("CollectAndValidate(container"),
                  "光照容器不进地图 Collider 白名单遍历（白名单仍只扫地图 prefab 子树）");

            // 唯一引用者：诊断模式（PMR3TestScene / PMUnityMoverPresentation）不得触碰光照模块。
            CheckLightingReferencedOnlyByMap(repoRoot);
        }

        /// <summary>源场景一盏灯的真实序列化值（含与该灯对应的 Transform 与 Light 口径字段）。</summary>
        private struct SourceLight
        {
            public string Name;
            public int LightComponentCount;
            public int Type;
            public int Lightmapping;
            public int RenderMode;
            public int UseColorTemperature;
            public long CullingMaskBits;

            /// <summary>20 列，顺序 = <see cref="LightingColumnNames"/>。</summary>
            public double[] Values;
        }

        /// <summary>模块定义表里解析出来的一行。</summary>
        private struct ParsedLightDefinition
        {
            public string Name;
            public double[] Values;
        }

        /// <summary>该 GameObject 上指定 classID 的组件个数（白名单外的"重复组件"用）。</summary>
        private static int CountComponentsOfClass(SceneModel scene, long gameObjectFileId, int classId)
        {
            SceneDocument go = scene.FindGameObject(gameObjectFileId);
            if (go == null)
            {
                return 0;
            }

            Match components = Regex.Match(go.Text, @"(?s)m_Component:\s*\r?\n((?:\s*-\s*component:\s*\{fileID:\s*-?\d+\}\r?\n?)+)");
            if (!components.Success)
            {
                return 0;
            }

            int count = 0;
            foreach (Match m in Regex.Matches(components.Groups[1].Value, @"component:\s*\{fileID:\s*(-?\d+)\}"))
            {
                long fileId = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                SceneDocument component = scene.FindDocument(fileId);
                if (component != null && component.ClassId == classId)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>GameObject 路径的 Transform 必须是单位 TRS（pos 全 0 / rot identity / scale 全 1）。</summary>
        private static void CheckUnitTransform(SceneModel scene, string gameObjectPath, string label)
        {
            long go = scene.FindGameObjectByPath(gameObjectPath);
            if (go == 0)
            {
                Check(false, label + "（找不到 " + gameObjectPath + "）");
                return;
            }

            SceneDocument transform = scene.FindDocument(scene.TransformFileIdOf(go));
            if (transform == null)
            {
                Check(false, label + "（找不到 Transform）");
                return;
            }

            double[] position = null;
            double[] rotation = null;
            double[] scale = null;
            bool ok = TryReadStructComponents(transform.Text, "m_LocalPosition", out position)
                      && TryReadStructComponents(transform.Text, "m_LocalRotation", out rotation)
                      && TryReadStructComponents(transform.Text, "m_LocalScale", out scale)
                      && position != null && position.Length == 3
                      && rotation != null && rotation.Length == 4
                      && scale != null && scale.Length == 3;

            if (!ok)
            {
                Check(false, label + "（字段读不到）");
                return;
            }

            bool unit = Math.Abs(position[0]) <= 1e-4 && Math.Abs(position[1]) <= 1e-4 && Math.Abs(position[2]) <= 1e-4
                        && Math.Abs(rotation[0]) <= 1e-4 && Math.Abs(rotation[1]) <= 1e-4
                        && Math.Abs(rotation[2]) <= 1e-4 && Math.Abs(rotation[3] - 1.0) <= 1e-4
                        && Math.Abs(scale[0] - 1.0) <= 1e-4 && Math.Abs(scale[1] - 1.0) <= 1e-4
                        && Math.Abs(scale[2] - 1.0) <= 1e-4;

            Check(unit, label + "（pos=(" + position[0].ToString("R", CultureInfo.InvariantCulture) + ", "
                       + position[1].ToString("R", CultureInfo.InvariantCulture) + ", "
                       + position[2].ToString("R", CultureInfo.InvariantCulture) + "), rot=("
                       + rotation[0].ToString("R", CultureInfo.InvariantCulture) + ", "
                       + rotation[1].ToString("R", CultureInfo.InvariantCulture) + ", "
                       + rotation[2].ToString("R", CultureInfo.InvariantCulture) + ", "
                       + rotation[3].ToString("R", CultureInfo.InvariantCulture) + "), scale=("
                       + scale[0].ToString("R", CultureInfo.InvariantCulture) + ", "
                       + scale[1].ToString("R", CultureInfo.InvariantCulture) + ", "
                       + scale[2].ToString("R", CultureInfo.InvariantCulture) + ")）");
        }

        /// <summary>
        /// 从 Light 组件文本 + 它的 Transform 文本读出定义表的 20 列（顺序 = <see cref="LightingColumnNames"/>）。
        /// 读不到的字段填 NaN ⇒ 与表的对照必然失败（不静默当 0）。
        /// </summary>
        private static double[] ReadLightValues(string lightText, string transformText)
        {
            double[] values = new double[LightingValueCount];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = double.NaN;
            }

            string shadows = ExtractShadowsBlock(lightText);

            double[] color = null;
            double[] position = null;
            double[] rotation = null;
            TryReadStructComponents(lightText, "m_Color", out color);
            TryReadStructComponents(transformText, "m_LocalPosition", out position);
            TryReadStructComponents(transformText, "m_LocalRotation", out rotation);

            values[0] = ReadFloatField(lightText, "m_Type", double.NaN);
            if (color != null && color.Length >= 3)
            {
                values[1] = color[0];
                values[2] = color[1];
                values[3] = color[2];
            }

            values[4] = ReadFloatField(lightText, "m_Intensity", double.NaN);
            values[5] = ReadFloatField(lightText, "m_Range", double.NaN);
            values[6] = ReadFloatField(lightText, "m_SpotAngle", double.NaN);
            values[7] = ReadFloatField(lightText, "m_InnerSpotAngle", double.NaN);

            if (position != null && position.Length == 3)
            {
                values[8] = position[0];
                values[9] = position[1];
                values[10] = position[2];
            }

            if (rotation != null && rotation.Length == 4)
            {
                values[11] = rotation[0];
                values[12] = rotation[1];
                values[13] = rotation[2];
                values[14] = rotation[3];
            }

            values[15] = ReadFloatField(shadows, "m_Type", double.NaN);
            values[16] = ReadFloatField(shadows, "m_Strength", double.NaN);
            values[17] = ReadFloatField(shadows, "m_Bias", double.NaN);
            values[18] = ReadFloatField(shadows, "m_NormalBias", double.NaN);
            values[19] = ReadFloatField(shadows, "m_NearPlane", double.NaN);
            return values;
        }

        /// <summary>Light 文档里的 m_Shadows 块（到 m_CullingMatrixOverride 之前）。</summary>
        private static string ExtractShadowsBlock(string lightText)
        {
            Match m = Regex.Match(lightText, @"(?s)m_Shadows:\s*\r?\n(.*?)m_CullingMatrixOverride:");
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        /// <summary>定义表 ↔ 源灯逐字段对照；一致返回 null，不一致返回可读原因（负例直接复用本函数）。</summary>
        private static string CompareLightDefinitionToSource(ParsedLightDefinition definition, SourceLight source)
        {
            if (!string.Equals(definition.Name, source.Name, StringComparison.Ordinal))
            {
                return "名称 表=\"" + (definition.Name ?? "<null>") + "\" 源=\"" + (source.Name ?? "<null>") + "\"";
            }

            if (definition.Values == null || source.Values == null
                || definition.Values.Length != LightingValueCount || source.Values.Length != LightingValueCount)
            {
                return "数值列数不对（表=" + (definition.Values == null ? -1 : definition.Values.Length)
                       + "，源=" + (source.Values == null ? -1 : source.Values.Length)
                       + "，期望=" + LightingValueCount + "）";
            }

            for (int i = 0; i < LightingValueCount; i++)
            {
                double table = definition.Values[i];
                double raw = source.Values[i];
                bool isEnum = i == 0 || i == 15;   // kind / shadowType：枚举必须精确相等

                if (double.IsNaN(table) || double.IsNaN(raw))
                {
                    return LightingColumnNames[i] + " 读不到（表=" + table.ToString("R", CultureInfo.InvariantCulture)
                           + "，源=" + raw.ToString("R", CultureInfo.InvariantCulture) + "）";
                }

                if (isEnum)
                {
                    if (Math.Abs(table - raw) > 0.5)
                    {
                        return LightingColumnNames[i] + " 枚举不等（表=" + table.ToString("R", CultureInfo.InvariantCulture)
                               + "，源=" + raw.ToString("R", CultureInfo.InvariantCulture) + "）";
                    }

                    continue;
                }

                double scale = Math.Max(1.0, Math.Abs(raw));
                if (Math.Abs(table - raw) > LightingTolerance * scale)
                {
                    return LightingColumnNames[i] + " 不等（表=" + table.ToString("R", CultureInfo.InvariantCulture)
                           + "，源=" + raw.ToString("R", CultureInfo.InvariantCulture)
                           + "，容差=" + LightingTolerance.ToString("R", CultureInfo.InvariantCulture) + "）";
                }
            }

            return null;
        }

        /// <summary>负例：把表里的某一列改成别的值，对照**必须**失败。</summary>
        private static bool NegativeLightingCase(ParsedLightDefinition definition, SourceLight source,
                                                 int column, double wrongValue, string columnName)
        {
            ParsedLightDefinition mutated = definition;
            mutated.Values = (double[])definition.Values.Clone();
            mutated.Values[column] = wrongValue;
            string mismatch = CompareLightDefinitionToSource(mutated, source);

            if (mismatch == null)
            {
                Console.WriteLine("      （负例未被拒绝：column=" + columnName + " ⇒ " + wrongValue.ToString("R", CultureInfo.InvariantCulture) + "）");
                return false;
            }

            return mismatch.StartsWith(LightingColumnNames[column], StringComparison.Ordinal);
        }

        /// <summary>解析模块里的冻结定义表（起止锚点之间的一条定义一行格式）。</summary>
        private static List<ParsedLightDefinition> ParseLightDefinitions(string moduleCode)
        {
            List<ParsedLightDefinition> result = new List<ParsedLightDefinition>();

            int begin = moduleCode.IndexOf(LightingTableBeginMarker, StringComparison.Ordinal);
            int end = moduleCode.IndexOf(LightingTableEndMarker, StringComparison.Ordinal);
            if (begin < 0 || end <= begin)
            {
                return result;
            }

            string region = moduleCode.Substring(begin, end - begin);
            foreach (Match m in Regex.Matches(region,
                        @"new\s+PMBattleLightDefinition\s*\(\s*""([^""]*)""\s*,([^)]*)\)", RegexOptions.Singleline))
            {
                ParsedLightDefinition entry = new ParsedLightDefinition();
                entry.Name = m.Groups[1].Value;

                List<double> values = new List<double>();
                foreach (Match v in Regex.Matches(m.Groups[2].Value, @"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?[fF]?"))
                {
                    string token = v.Value;
                    if (token.EndsWith("f", StringComparison.OrdinalIgnoreCase))
                    {
                        token = token.Substring(0, token.Length - 1);
                    }

                    double parsed;
                    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    {
                        values.Add(parsed);
                    }
                }

                entry.Values = values.ToArray();
                result.Add(entry);
            }

            return result;
        }

        /// <summary>读 `public const float NAME = 1.5f;`。</summary>
        private static bool TryReadConstFloat(string code, string name, out double value)
        {
            value = 0.0;
            Match m = Regex.Match(code, @"public\s+const\s+float\s+" + Regex.Escape(name) + @"\s*=\s*(-?\d+(?:\.\d+)?)f\s*;");
            if (!m.Success)
            {
                return false;
            }

            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>读 `field: {x: 1, y: 2, z: 3}` / `field: {r: .., g: .., b: .., a: ..}`（保持序列化顺序）。</summary>
        private static bool TryReadStructComponents(string text, string field, out double[] values)
        {
            values = null;
            Match m = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(field) + @":\s*\{([^}]*)\}");
            if (!m.Success)
            {
                return false;
            }

            List<double> list = new List<double>();
            foreach (Match part in Regex.Matches(m.Groups[1].Value, @"([A-Za-z]+)\s*:\s*(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)"))
            {
                double parsed;
                if (double.TryParse(part.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    list.Add(parsed);
                }
            }

            values = list.ToArray();
            return values.Length > 0;
        }

        /// <summary>读单个整数字段为 long（m_Bits 这类 4294967295 用 Int32 会溢出）。</summary>
        private static long ReadLongField(string text, string field, long fallback)
        {
            Match m = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(field) + @":\s*(-?\d+)\s*$");
            if (!m.Success)
            {
                return fallback;
            }

            long parsed;
            return long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                       ? parsed
                       : fallback;
        }

        /// <summary>读单个浮点字段（`field: 1.5`）。</summary>
        private static double ReadFloatField(string text, string field, double fallback)
        {
            Match m = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(field) + @":\s*(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)\s*$");
            if (!m.Success)
            {
                return fallback;
            }

            double parsed;
            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                       ? parsed
                       : fallback;
        }

        /// <summary>读内联结构 `field: {x: a, y: b}` 的两个分量（找不到返回 false）。</summary>
        private static bool TryReadVector2Field(string text, string field, out double x, out double y)
        {
            x = 0.0;
            y = 0.0;

            Match m = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(field)
                + @":\s*\{x:\s*(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?),\s*y:\s*(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)\}");
            if (!m.Success)
            {
                return false;
            }

            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                   && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y);
        }

        /// <summary>读 Image 的 `m_Color: {r: .., g: .., b: .., a: ..}`（找不到返回 false）。</summary>
        private static bool TryReadColorField(string text, out double r, out double g, out double b, out double a)
        {
            r = 0.0;
            g = 0.0;
            b = 0.0;
            a = 0.0;

            Match m = Regex.Match(text,
                @"(?m)^\s*m_Color:\s*\{r:\s*(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?),\s*g:\s*(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?),\s*b:\s*(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?),\s*a:\s*(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)\}");
            if (!m.Success)
            {
                return false;
            }

            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out r)
                   && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out g)
                   && double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out b)
                   && double.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out a);
        }

        /// <summary>
        /// 光照实现区（已剥注释）不得出现：物理类型、全局渲染/光照设置、资产写入、旧场景加载、材质/postprocessing。
        /// "灯不参与物理、不改地图源与全局光照"这条契约在文本级被钉住。
        /// </summary>
        private static void CheckLightingCodeHasNoForbiddenSymbols(string moduleNoComments)
        {
            string[] forbidden =
            {
                "Collider", "Rigidbody", "Physics.", "PhysicsScene",
                "RenderSettings", "LightmapSettings", "QualitySettings", "Skybox",
                "Material", "PostProcess", "Volume",
                "AssetDatabase", "PrefabUtility", "SceneManager.LoadScene", "UnityEditor",
            };

            for (int i = 0; i < forbidden.Length; i++)
            {
                Check(!moduleNoComments.Contains(forbidden[i]),
                      "光照实现区不含「" + forbidden[i] + "」（灯不参与物理/不改全局光照与资产/不加载旧场景）");
            }
        }

        /// <summary>
        /// 光照模块只允许被正式地图模块引用：诊断模式（PMR3TestScene / PMUnityMoverPresentation）不得触碰它。
        /// </summary>
        private static void CheckLightingReferencedOnlyByMap(string repoRoot)
        {
            string directory = Path.Combine(repoRoot, "Client", "Assets", "Scripts", "PMUnity");
            if (!Directory.Exists(directory))
            {
                Check(false, "找不到 PMUnity 目录：" + directory);
                return;
            }

            string[] files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories);
            List<string> unexpected = new List<string>();
            bool mapFound = false;

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                string name = Path.GetFileName(file);
                string code = File.ReadAllText(file, Encoding.UTF8);
                if (code.IndexOf("PMUnityBattleLighting", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                if (string.Equals(name, "PMUnityBattleLighting.cs", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(name, "PMUnityBattleMap.cs", StringComparison.Ordinal))
                {
                    mapFound = true;
                    continue;
                }

                unexpected.Add(name);
            }

            Check(mapFound, "光照模块被正式地图模块引用（PMUnityBattleMap.cs）");
            Check(unexpected.Count == 0,
                  "光照模块只被 PMUnityBattleMap 引用（其它引用者："
                  + (unexpected.Count == 0 ? "<无>" : string.Join(", ", unexpected.ToArray()))
                  + "；诊断模式不经本模块）");
        }

        // ==================================================================== J（T-PLAY4 局内操控 UI）

        /// <summary>
        /// J 段总入口（T-PLAY4）。四块：J1 旧 GameUI.prefab 只读事实；J2 UI 源码零激活/零权威静态负例；
        /// J3 摇杆去重/释放的可执行纯逻辑反例（镜像）；J4 交付面事实（编码/meta guid/真实 UI 引用/宿主未改）。
        ///
        /// 口径（不许夸大）：本段只把 .prefab 当**文本 YAML** 读，不加载 Unity ⇒ 它不证明 Unity 能反序列化
        /// 这张 prefab，也不证明新 UI 在编辑器/Player 里显示正确（要用户实机，见报告 T-PLAY5）。
        /// J3 是**规则镜像**（本文件独立实现的可执行模型），不是“跑同一个实现”——本工具只编 Program.cs、
        /// 编不到 UI 源码；常量一致性由 J3 的源码常量交叉核对守住，结构一致性由 J2 的标记断言守住。
        /// </summary>
        private static void CheckBattleControlsSource(string repoRoot)
        {
            string prefabPath = Path.Combine(repoRoot,
                GameUiPrefabRelativePath.Replace('/', Path.DirectorySeparatorChar));

            Check(File.Exists(prefabPath), "旧局内 UI 预制体存在：" + GameUiPrefabRelativePath);
            if (!File.Exists(prefabPath))
            {
                Console.WriteLine("  跳过 J 段其余断言：源预制体不在，无法核对只读事实。");
                return;
            }

            string prefabText = File.ReadAllText(prefabPath, Encoding.UTF8);
            Check(prefabText.StartsWith("%YAML", StringComparison.Ordinal),
                  "旧局内 UI 预制体是 ForceText（YAML）序列化：可当文本读，不需要加载 Unity");

            UiPrefabModel prefab = UiPrefabModel.Load(prefabText);
            Check(prefab.NodesById.Count >= 20, "旧 UI 预制体解析出 GameObject 数 ≥ 20（实际=" + prefab.NodesById.Count.ToString(CultureInfo.InvariantCulture) + "，父边=" + prefab.ParentEdgeCount.ToString(CultureInfo.InvariantCulture) + "，根数=" + prefab.RootIds.Count.ToString(CultureInfo.InvariantCulture) + "，Transform文档=" + prefab.TransformDocCount.ToString(CultureInfo.InvariantCulture) + "）");

            UiNode root = FindRoot(prefab);
            Check(root != null && string.Equals(root.Name, "GameUI", StringComparison.Ordinal),
                  "旧 UI 根 GameObject 名恰为 GameUI（实际=" + (root == null ? "<无根>" : root.Name) + "）");
            Check(root != null && !root.Active,
                  "旧 UI 根序列化为 **inactive**（m_IsActive: 0）—— 这正是“不得加载/激活它”的源依据");

            string[] requiredNodes = new string[]
            {
                "Android", "Android/EasyTouch", "Android/PlayerMove", "Android/FireNormal",
                "Android/FireNormalButton", "Android/FireSuper", "Android/Singleton of VirtualScreen",
                "Button", "GemSelfTeamUI", "GemSelfTeamUI/Image", "GemEnemyTeamUI", "GemEnemyTeamUI/Image",
                "能量条", "能量条/Background", "能量条/Fill Area/Fill", "能量条/Image",
            };

            for (int i = 0; i < requiredNodes.Length; i++)
            {
                Check(prefab.NodesByPath.ContainsKey("/GameUI/" + requiredNodes[i]),
                      "旧 UI 关键节点存在：/" + requiredNodes[i]);
            }

            UiNode fireSuper = prefab.Find("GameUI/Android/FireSuper");
            Check(fireSuper != null && !fireSuper.Active,
                  "旧 UI FireSuper 节点序列化为 inactive（新 UI 用 SuperReady 决定可用性，不自动恢复旧节点）");

            for (int i = 0; i < UiLegacyScripts.Length; i++)
            {
                string relative = UiLegacyScripts[i][0];
                string guid = UiLegacyScripts[i][1];
                string label = UiLegacyScripts[i][2];
                UiNode node = relative.Length == 0 ? root : prefab.Find("GameUI/" + relative);

                Check(node != null && prefab.HasScriptGuid(node, guid),
                      "旧 UI 仍挂着旧脚本 " + label + "（@" + (relative.Length == 0 ? "<根>" : relative)
                      + "）：因此“零激活”必须由结构保证，不能靠自觉");
            }

            UiNode buttonNode = prefab.Find("GameUI/Button");
            string buttonText = buttonNode == null ? string.Empty : buttonNode.ComponentText;
            Check(buttonText.IndexOf("m_MethodName: backStart", StringComparison.Ordinal) >= 0,
                  "旧 UI Button 挂着 UnityEvent 持久监听（m_MethodName: backStart）：本 UI 绝不调用它");
            Check(buttonText.IndexOf("m_TargetGraphic", StringComparison.Ordinal) >= 0,
                  "旧 UI Button 的 UnityEvent 目标有序列化引用（“只禁脚本”也仍是被旧资源驱动的活对象）");

            for (int i = 0; i < UiRequiredSprites.Length; i++)
            {
                string nodePath = UiRequiredSprites[i][0];
                string spriteGuid = UiRequiredSprites[i][1];
                string sourcePath = UiRequiredSprites[i][2];

                UiNode node = prefab.Find("GameUI/" + nodePath);
                string actual = node == null ? null : prefab.SpriteGuid(node);

                Check(node != null && string.Equals(actual, spriteGuid, StringComparison.Ordinal),
                      "只读 Sprite 引用命中：" + nodePath + " → guid " + spriteGuid
                      + "（实际=" + (actual ?? "<无>") + "）");

                string absolute = Path.Combine(repoRoot, sourcePath.Replace('/', Path.DirectorySeparatorChar));
                Check(File.Exists(absolute), "Sprite 源图存在：" + sourcePath);
            }

            // ---- J1b（T-LIVE4）：大招能量条的旧 YAML 事实（圆盘底图 + 径向填充 + 小图标） ----
            CheckEnergyGaugeLegacyFacts(prefab, prefabText);

            UiNode moveNode = prefab.Find("GameUI/Android/PlayerMove");
            UiNode normalNode = prefab.Find("GameUI/Android/FireNormal");
            int zoneRadius = moveNode == null ? 0 : ReadInt(moveNode.ComponentText, "zoneRadius", 0);
            int deadZone = moveNode == null ? 0 : ReadInt(moveNode.ComponentText, "deadZone", 0);
            int moveAnchor = moveNode == null ? -1 : ReadInt(moveNode.ComponentText, "joyAnchor", -1);
            int normalAnchor = normalNode == null ? -1 : ReadInt(normalNode.ComponentText, "joyAnchor", -1);
            int superAnchor = fireSuper == null ? -1 : ReadInt(fireSuper.ComponentText, "joyAnchor", -1);

            Check(zoneRadius == LegacyJoystickZoneRadius,
                  "旧 EasyJoystick zoneRadius == " + LegacyJoystickZoneRadius.ToString(CultureInfo.InvariantCulture)
                  + "（实际=" + zoneRadius.ToString(CultureInfo.InvariantCulture) + "）：新 UI 摇杆半径常量的来源");
            Check(deadZone == LegacyJoystickDeadZone,
                  "旧 EasyJoystick deadZone == " + LegacyJoystickDeadZone.ToString(CultureInfo.InvariantCulture)
                  + "（实际=" + deadZone.ToString(CultureInfo.InvariantCulture)
                  + "）：新 UI 死区比例 0.2 = deadZone/zoneRadius 的来源");
            Check(LegacyJoystickDeadZone * 5 == LegacyJoystickZoneRadius,
                  "死区比例派生的算术前提成立：deadZone*5 == zoneRadius ⇒ deadZone/zoneRadius == 0.2");
            Check(moveAnchor == LegacyMoveJoystickAnchor,
                  "旧 PlayerMove joyAnchor == " + LegacyMoveJoystickAnchor.ToString(CultureInfo.InvariantCulture)
                  + "（JoystickAnchor.LowerLeft；实际=" + moveAnchor.ToString(CultureInfo.InvariantCulture) + "）");
            Check(normalAnchor == LegacyFireJoystickAnchor && superAnchor == LegacyFireJoystickAnchor,
                  "旧 FireNormal/FireSuper joyAnchor == " + LegacyFireJoystickAnchor.ToString(CultureInfo.InvariantCulture)
                  + "（JoystickAnchor.LowerRight；实际=" + normalAnchor.ToString(CultureInfo.InvariantCulture)
                  + "/" + superAnchor.ToString(CultureInfo.InvariantCulture) + "）");

            int scalerMode = root == null ? -1 : ReadInt(root.ComponentText, "m_UiScaleMode", -1);
            int scalerFactor = root == null ? -1 : ReadInt(root.ComponentText, "m_ScaleFactor", -1);
            int scalerRefX = root == null ? -1 : ReadScalerReferenceResolutionX(root.ComponentText);

            Check(scalerMode == 0,
                  "旧 CanvasScaler 是 ConstantPixelSize（m_UiScaleMode=0；实际="
                  + scalerMode.ToString(CultureInfo.InvariantCulture) + "）：新 UI 直接copy它的缩放口径");
            Check(scalerFactor == 1, "旧 CanvasScaler scaleFactor == 1（实际="
                  + scalerFactor.ToString(CultureInfo.InvariantCulture) + "）");
            Check(scalerRefX == 800, "旧 CanvasScaler referenceResolution.x == 800（实际="
                  + scalerRefX.ToString(CultureInfo.InvariantCulture) + "）");

            // ---- J1 负例：篡改“必需 Sprite / 根 inactive”，同一个提取器必须报失败 ----
            string mutatedMissingSprite = ReplaceFirstOccurrence(prefabText,
                "m_Sprite: {fileID: 21300000, guid: ab50dae53b97cce488f2b49b637fe5dc, type: 3}",
                "m_Sprite: {fileID: 0}");
            UiPrefabModel mutatedModel = UiPrefabModel.Load(mutatedMissingSprite);
            UiNode mutatedNode = mutatedModel.Find("GameUI/能量条/Image");
            Check(mutatedNode == null
                  || !string.Equals(mutatedModel.SpriteGuid(mutatedNode),
                                    "ab50dae53b97cce488f2b49b637fe5dc", StringComparison.Ordinal),
                  "负例：抹掉 UnFullImage 的 Sprite 引用后，J1 的 Sprite 断言会失败（提取器不是空转）");

            UiPrefabModel activeModel = UiPrefabModel.Load(BumpGameUiRootActiveFlag(prefabText));
            UiNode activeRoot = FindRoot(activeModel);
            Check(activeRoot != null && activeRoot.Active,
                  "负例：把根的 m_IsActive 改成 1 后，J1 的“根 inactive”断言会失败（该断言不是恒真）");

            // ---- J2 UI 源码：零激活 / 零权威（去注释扫描 + 变异负例）----
            string uiSourcePath = Path.Combine(repoRoot,
                BattleControlsRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Check(File.Exists(uiSourcePath), "新 UI 源码存在：" + BattleControlsRelativePath);
            if (!File.Exists(uiSourcePath))
            {
                Console.WriteLine("  跳过 J2/J3/J4：新 UI 源码不在。");
                return;
            }

            string uiSource = File.ReadAllText(uiSourcePath, Encoding.UTF8);
            string uiCode = StripComments(uiSource);

            for (int i = 0; i < UiRequiredTokens.Length; i++)
            {
                CheckContains(uiSource, UiRequiredTokens[i], "UI 源码必须含标记：" + UiRequiredTokens[i]);
            }

            int forbiddenFound = 0;
            for (int i = 0; i < UiForbiddenTokens.Length; i++)
            {
                if (uiCode.IndexOf(UiForbiddenTokens[i], StringComparison.Ordinal) >= 0)
                {
                    forbiddenFound++;
                    Check(false, "UI 源码（去注释后）**不得**出现旧脚本/旧联网/权威写标记："
                                 + UiForbiddenTokens[i]);
                }
            }

            Check(forbiddenFound == 0,
                  "UI 去注释源码对旧脚本/旧联网/RPC/旧场景加载的禁用标记命中 0 处（共查 "
                  + UiForbiddenTokens.Length.ToString(CultureInfo.InvariantCulture) + " 个标记）");

            int mutatedHits = 0;
            for (int i = 0; i < UiForbiddenTokens.Length; i++)
            {
                string injected = uiCode + "\n" + UiForbiddenTokens[i] + "();\n";
                if (injected.IndexOf(UiForbiddenTokens[i], StringComparison.Ordinal) >= 0) { mutatedHits++; }
            }

            Check(mutatedHits == UiForbiddenTokens.Length,
                  "负例：把每个禁用标记注入源码文本后扫描器 100% 命中（"
                  + mutatedHits.ToString(CultureInfo.InvariantCulture) + "/"
                  + UiForbiddenTokens.Length.ToString(CultureInfo.InvariantCulture) + "）⇒ 0 命中不是空转");

            // ---- J2 结构顺序：DS 拒绝 → 只读读取 → 建自己的对象；Dispose 先清输入再销毁 ----
            CheckContains(uiSource, "绝不激活旧资源", "UI 头注释登记“绝不激活旧资源”");
            CheckContains(uiSource, "只读", "UI 头注释登记只读复用（Sprite/布局）");
            CheckOrder(uiCode, "PMNetRuntime.IsDedicatedServer",
                       "Resources.Load<GameObject>(LegacyPrefabResourceKey)",
                       "DS 拒绝发生在读取旧 prefab **之前**");
            CheckOrder(uiCode, "Resources.Load<GameObject>(LegacyPrefabResourceKey)",
                       "AddComponent<PMUnityBattleControls>",
                       "只读读取旧 prefab 发生在建自己的对象**之前**（旧 prefab 只做数据源）");
            CheckOrder(uiCode, "AddComponent<Canvas>", "AddComponent<PMUnityBattleControlsPointerRelay>",
                       "Canvas 先于指针转发器创建（转发器挂在 Canvas 根上）");
            CheckContains(uiCode, "image.raycastTarget = raycast;", "只读控件按参数控制命中（装饰件不抢指针）");
            CheckContains(uiCode, "text.raycastTarget = false;", "文本不参与射线命中（不挡摇杆）");
            CheckContains(uiCode, "image.raycastTarget = false;", "条状填充不参与射线命中（不挡摇杆）");

            string disposeRegion = Region(uiCode, "public void Dispose()", "private void SetNoticeLocal(");
            Check(disposeRegion.Length > 0, "能定位 UI Dispose 区域（结构符合预期）");
            CheckOrder(disposeRegion, "CancelStick(_moveStick", "UnityEngine.Object.Destroy(host)",
                       "Dispose 先清掉摇杆输入，再销毁自己的对象");
            CheckOrder(disposeRegion, "_setMove = null;", "UnityEngine.Object.Destroy(host)",
                       "Dispose 先摘掉宿主出口，再销毁对象（防销毁瞬间回调仍生效）");
            CheckContains(disposeRegion, "if (_disposed) { return; }", "Dispose 幂等");
            CheckContains(disposeRegion, "_ownsEventSystem", "Dispose 只收回本局专属创建的事件系统");

            // ---- J3 指针事件去重/释放（镜像 + 源码常量交叉核对）----
            CheckUiPointerSemantics(uiSource);

            // ---- J3b（T-LIVE4）：能量表必须改用旧素材的径向填充，不得再是纯色矩形 ----
            CheckEnergyGaugeRadialSource(uiCode);

            // ---- J4 交付面 ----
            CheckBattleControlsEncodingAndMeta(repoRoot, uiSourcePath);
            CheckR4UnityCheckUiReferences(repoRoot);
            CheckBattleControlsHostWiring(repoRoot);

            // ---- J5（T-LIVE3）：新瞄准指示器文件的交付面（编码 / meta / 零 RPC / DS 拒绝）----
            CheckAimIndicatorSource(repoRoot);
        }

        /// <summary>
        /// J1b（T-LIVE4）：旧 GameUI 里「大招能量条」的**只读事实**。
        ///
        /// 实机反馈：新链把能量画成纯色矩形 + 22 号数字，而旧 UI 是
        /// FullBG 圆盘底图 + FullPower 的 <c>Image.Type=Filled</c> / <c>FillMethod=Radial360</c> 径向填充
        /// + UnFullImage 小图标。本方法把这三条钉在**源 YAML** 上（含负例：把 m_Type 改成 Simple、
        /// 把 m_FillMethod 改成别的，提取器必须报出不同值）。
        ///
        /// 口径：只当文本 YAML 读 ⇒ 证明「旧资源配置如此」，不证明新 UI 渲染出来就是那个观感（要实机截图）。
        /// </summary>
        private static void CheckEnergyGaugeLegacyFacts(UiPrefabModel prefab, string prefabText)
        {
            UiNode gauge = prefab.Find("GameUI/能量条");
            UiNode frame = prefab.Find("GameUI/能量条/Background");
            UiNode fill = prefab.Find("GameUI/能量条/Fill Area/Fill");
            UiNode icon = prefab.Find("GameUI/能量条/Image");

            Check(gauge != null && frame != null && fill != null && icon != null,
                  "T-LIVE4 旧能量条四个节点都在（能量条 / Background / Fill Area/Fill / Image）");
            if (gauge == null || frame == null || fill == null || icon == null) { return; }

            // 根几何：新 UI 的 gauge 布局只读copy自它（不自行发明坐标）。
            double sizeX;
            double sizeY;
            double posX;
            double posY;
            bool gaugeSize = TryReadVector2Field(gauge.ComponentText, "m_SizeDelta", out sizeX, out sizeY);
            bool gaugePos = TryReadVector2Field(gauge.ComponentText, "m_AnchoredPosition", out posX, out posY);

            Check(gaugeSize && Math.Abs(sizeX - 218.78491) < 0.01 && Math.Abs(sizeY - 181.83447) < 0.01,
                  "T-LIVE4 旧能量条 sizeDelta ≈ (218.78491, 181.83447)（实际="
                  + (gaugeSize ? (sizeX.ToString(CultureInfo.InvariantCulture) + "," + sizeY.ToString(CultureInfo.InvariantCulture)) : "<读不到>")
                  + "）：新 UI 的 gauge 几何只读来源");
            Check(gaugePos && Math.Abs(posX - 351.5) < 0.01 && Math.Abs(posY - (-296.6)) < 0.01,
                  "T-LIVE4 旧能量条 anchoredPosition ≈ (351.5, −296.6)（实际="
                  + (gaugePos ? (posX.ToString(CultureInfo.InvariantCulture) + "," + posY.ToString(CultureInfo.InvariantCulture)) : "<读不到>")
                  + "）");

            int frameType = ReadInt(frame.ComponentText, "m_Type", int.MinValue);
            int frameAspect = ReadInt(frame.ComponentText, "m_PreserveAspect", int.MinValue);
            Check(frameType == 0, "T-LIVE4 旧底图 Background 是 Simple（m_Type=0；实际=" + frameType.ToString(CultureInfo.InvariantCulture) + "）：圆盘底图不做填充");
            Check(frameAspect == 1, "T-LIVE4 旧底图 Background preserveAspect=1（实际=" + frameAspect.ToString(CultureInfo.InvariantCulture) + "）：圆盘不被拉伸");

            int fillType = ReadInt(fill.ComponentText, "m_Type", int.MinValue);
            int fillMethod = ReadInt(fill.ComponentText, "m_FillMethod", int.MinValue);
            int fillOrigin = ReadInt(fill.ComponentText, "m_FillOrigin", int.MinValue);
            int fillClockwise = ReadInt(fill.ComponentText, "m_FillClockwise", int.MinValue);
            int fillAmount = ReadInt(fill.ComponentText, "m_FillAmount", int.MinValue);

            Check(fillType == 3, "T-LIVE4 ★ 旧填充 Fill 是 Image.Type.Filled（m_Type=3；实际=" + fillType.ToString(CultureInfo.InvariantCulture) + "）");
            Check(fillMethod == 4, "T-LIVE4 ★ 旧填充 Fill 是 Radial360（m_FillMethod=4；实际=" + fillMethod.ToString(CultureInfo.InvariantCulture) + "）");
            Check(fillOrigin == 0, "T-LIVE4 旧填充 Fill 的 m_FillOrigin=0（Bottom；实际=" + fillOrigin.ToString(CultureInfo.InvariantCulture) + "）");
            Check(fillClockwise == 1, "T-LIVE4 旧填充 Fill 顺时针（m_FillClockwise=1；实际=" + fillClockwise.ToString(CultureInfo.InvariantCulture) + "）");
            Check(fillAmount == 1, "T-LIVE4 旧填充 Fill 初值 m_FillAmount=1（实际=" + fillAmount.ToString(CultureInfo.InvariantCulture) + "）");

            double fr;
            double fg;
            double fb;
            double fa;
            bool fillColor = TryReadColorField(fill.ComponentText, out fr, out fg, out fb, out fa);
            Check(fillColor && fa > 0.99 && (fr + fg + fb) > 0.1,
                  "T-LIVE4 旧填充色是不透明可见色（不是全 0 的隐形 tint；实际 a=" + (fillColor ? fa.ToString(CultureInfo.InvariantCulture) : "<读不到>") + "）");

            int iconType = ReadInt(icon.ComponentText, "m_Type", int.MinValue);
            double iconW;
            double iconH;
            double iconX;
            double iconY;
            bool iconSize = TryReadVector2Field(icon.ComponentText, "m_SizeDelta", out iconW, out iconH);
            bool iconPos = TryReadVector2Field(icon.ComponentText, "m_AnchoredPosition", out iconX, out iconY);

            Check(iconType == 0 && iconSize
                  && Math.Abs(iconW - 125.32214) < 0.01 && Math.Abs(iconH - 126.7554) < 0.01
                  && iconPos && Math.Abs(iconX - (-1.67)) < 0.01 && Math.Abs(iconY - (-0.48)) < 0.01,
                  "T-LIVE4 旧 UnFullImage 小图标节点是 Simple + sizeDelta ≈ (125.32, 126.76) @ (−1.67, −0.48)（实际="
                  + iconType.ToString(CultureInfo.InvariantCulture) + "/"
                  + (iconSize ? (iconW.ToString(CultureInfo.InvariantCulture) + "," + iconH.ToString(CultureInfo.InvariantCulture)) : "<读不到>") + "）");

            // ---- 负例：变异后同一个提取器必须报出不同值（断言不是恒真） ----
            UiPrefabModel mutatedType = UiPrefabModel.Load(
                ReplaceFirstOccurrence(prefabText, "m_Type: 3", "m_Type: 0"));
            UiNode mutatedFill = mutatedType.Find("GameUI/能量条/Fill Area/Fill");
            Check(mutatedFill != null && ReadInt(mutatedFill.ComponentText, "m_Type", -1) != 3,
                  "T-LIVE4 负例：把 Fill 的 m_Type 从 3（Filled）改成 0（Simple）后，上面的 Filled 断言会失败");

            // 旧 prefab 是 CRLF（ForceText + CRLF），因此定位串必须用 "\r\n"。
            UiPrefabModel mutatedMethod = UiPrefabModel.Load(
                ReplaceFirstOccurrence(prefabText,
                    "m_Type: 3\r\n  m_PreserveAspect: 0\r\n  m_FillCenter: 1\r\n  m_FillMethod: 4",
                    "m_Type: 3\r\n  m_PreserveAspect: 0\r\n  m_FillCenter: 1\r\n  m_FillMethod: 0"));
            UiNode mutatedMethodFill = mutatedMethod.Find("GameUI/能量条/Fill Area/Fill");
            Check(mutatedMethodFill != null && ReadInt(mutatedMethodFill.ComponentText, "m_FillMethod", -1) != 4,
                  "T-LIVE4 负例：把 Fill 的 m_FillMethod 从 4（Radial360）改成 0 后，上面的 Radial360 断言会失败");
        }

        /// <summary>
        /// J3b（T-LIVE4）：新 UI 的**能量表构建区**必须改用旧素材的径向填充。
        ///
        /// 判据（都在去注释后的源码上）：
        ///   · 能量表区出现 <c>CreateRadialFill(</c>（Image.Type.Filled / Radial360 + fillAmount）；
        ///   · 同一区**不得**再出现 <c>CreateFillBar(</c>（旧实现：纯色矩形 + 22 号数字 ⇒ 实机大方块）；
        ///   · 比例口径仍是 SuperEnergy / SuperEnergyMax（权威复制值只读）。
        /// </summary>
        private static void CheckEnergyGaugeRadialSource(string uiCode)
        {
            string gaugeRegion = Region(uiCode,
                "RectTransform gauge = CreateRect(rootRect, \"EnergyGauge\"",
                "RectTransform hpBar = CreateRect(rootRect, \"HpBar\"");

            Check(gaugeRegion != null && gaugeRegion.Length > 0,
                  "T-LIVE4 能定位 UI 的能量表构建区（结构符合预期）");

            Check(gaugeRegion != null && gaugeRegion.Contains("CreateRadialFill("),
                  "T-LIVE4 ★ 能量表用径向填充（Image.Type.Filled/FillMethod.Radial360 + fillAmount）而非纯色矩形");
            Check(gaugeRegion != null && !gaugeRegion.Contains("CreateFillBar("),
                  "T-LIVE4 ★ 负例：能量表构建区不得再出现 CreateFillBar（旧纯色方块 + 大号数字）");

            CheckContains(uiCode, "UnityEngine.UI.Image.Type.Filled",
                          "T-LIVE4 能量填充显式指定 Image.Type.Filled");
            CheckContains(uiCode, "UnityEngine.UI.Image.FillMethod.Radial360",
                          "T-LIVE4 能量填充显式指定 FillMethod.Radial360");
            CheckContains(uiCode, "image.fillAmount =",
                          "T-LIVE4 能量比例用 fillAmount 表达（不是锚点宽度条）");
            CheckContains(uiCode, "TryReadEnergyFillConfig",
                          "T-LIVE4 旧 Fill 的径向配置只读复用（缺配置/形态不符即显式失败，不伪造）");
            CheckContains(uiCode, "Ratio(_status.SuperEnergy, PMNet.Shared.BattleNumericConfig.SuperEnergyMax)",
                          "T-LIVE4 能量比例 = SuperEnergy / SuperEnergyMax(200)（只读复制值）");
        }

        /// <summary>
        /// J5（T-LIVE3）：新增瞄准指示器文件的交付面事实。
        ///
        /// 它只证明**源码形状**：DS 拒绝、显式可见材质/颜色、隐藏走 enabled、Dispose、零 RPC/零权威/零资产加载、
        /// 编码与 meta 纪律；**不**证明实机渲染观感（那要用户实机，见主计划的 PENDING_USER）。
        /// </summary>
        private static void CheckAimIndicatorSource(string repoRoot)
        {
            string sourcePath = Path.Combine(repoRoot,
                AimIndicatorRelativePath.Replace('/', Path.DirectorySeparatorChar));

            Check(File.Exists(sourcePath), "T-LIVE3 瞄准指示器源码存在：" + AimIndicatorRelativePath);
            if (!File.Exists(sourcePath)) { return; }

            byte[] sourceBytes = File.ReadAllBytes(sourcePath);
            bool sourceBom = sourceBytes.Length >= 3 && sourceBytes[0] == 0xEF && sourceBytes[1] == 0xBB
                             && sourceBytes[2] == 0xBF;
            Check(sourceBom, "T-LIVE3 瞄准指示器源码带 UTF-8 BOM（项目“中文源码 BOM”纪律）");

            int crlf = 0;
            int loneLf = 0;
            for (int i = 0; i < sourceBytes.Length; i++)
            {
                if (sourceBytes[i] != (byte)'\n') { continue; }
                if (i > 0 && sourceBytes[i - 1] == (byte)'\r') { crlf++; }
                else { loneLf++; }
            }

            Check(crlf > 0 && loneLf == 0,
                  "T-LIVE3 瞄准指示器源码换行是 CRLF（实际 CRLF=" + crlf.ToString(CultureInfo.InvariantCulture)
                  + " loneLF=" + loneLf.ToString(CultureInfo.InvariantCulture) + "）");

            string metaPath = Path.Combine(repoRoot,
                (AimIndicatorRelativePath + ".meta").Replace('/', Path.DirectorySeparatorChar));
            Check(File.Exists(metaPath), "T-LIVE3 瞄准指示器带 .meta（否则 Unity 会自己生成一个，等于没锁 guid）");

            if (File.Exists(metaPath))
            {
                byte[] metaBytes = File.ReadAllBytes(metaPath);
                bool metaBom = metaBytes.Length >= 3 && metaBytes[0] == 0xEF && metaBytes[1] == 0xBB
                               && metaBytes[2] == 0xBF;
                Check(!metaBom, "T-LIVE3 瞄准指示器 .meta 无 BOM");

                int metaCrlf = 0;
                for (int i = 0; i < metaBytes.Length; i++)
                {
                    if (metaBytes[i] == (byte)'\n' && i > 0 && metaBytes[i - 1] == (byte)'\r') { metaCrlf++; }
                }

                Check(metaCrlf == 0, "T-LIVE3 瞄准指示器 .meta 使用 LF 换行（与 Unity 生成的 meta 一致）");

                Match guid = Regex.Match(File.ReadAllText(metaPath, Encoding.UTF8),
                                         @"(?m)^guid: ([0-9a-f]{32})\s*$");
                Check(guid.Success, "T-LIVE3 瞄准指示器 .meta 含 32 位 hex guid");

                if (guid.Success)
                {
                    string metaGuid = guid.Groups[1].Value;
                    int occurrences = 0;
                    string[] allMetas = Directory.GetFiles(Path.Combine(repoRoot, "Client", "Assets"),
                                                           "*.meta", SearchOption.AllDirectories);
                    for (int i = 0; i < allMetas.Length; i++)
                    {
                        if (File.ReadAllText(allMetas[i], Encoding.UTF8)
                                  .IndexOf("guid: " + metaGuid, StringComparison.Ordinal) >= 0)
                        {
                            occurrences++;
                        }
                    }

                    Check(occurrences == 1, "T-LIVE3 瞄准指示器 .meta 的 guid 在 Assets 内唯一（出现次数="
                          + occurrences.ToString(CultureInfo.InvariantCulture) + "；guid=" + metaGuid + "）");
                }
            }

            string source = File.ReadAllText(sourcePath, Encoding.UTF8);
            string code = StripComments(source);

            string[] required =
            {
                "PMNetRuntime.IsDedicatedServer",
                "LineRenderer",
                "new GameObject(",
                "Shader.Find(",
                "new Material(",
                "startColor =",
                "endColor =",
                "startWidth =",
                "endWidth =",
                "enabled = false",
                "public void Dispose()",
                "#if UNITY_2019_1_OR_NEWER || UNITY_EDITOR",
                "#else",
                "public const bool LineRendererImplementationCompiled = true;",
                "public const bool LineRendererImplementationCompiled = false;",
            };

            for (int i = 0; i < required.Length; i++)
            {
                CheckContains(source, required[i], "T-LIVE3 瞄准指示器必须含标记：" + required[i]);
            }

            string[] forbidden =
            {
                "ServerCombatAttackV1", "ClientCombatAttackResultV1", "ClientCombatMatchResultV1",
                "ServerCombatResultAckV1", "PMNet_", "MarkPropertyDirty",
                "CombatHp", "CombatMana", "CombatSuperEnergy",
                "Instantiate", "Resources.Load", "LoadScene", "FindObjectOfType", "SendMessage",
                "TouchLogic", "EasyJoystick", "EasyTouch",
            };

            int hits = 0;
            for (int i = 0; i < forbidden.Length; i++)
            {
                if (code.IndexOf(forbidden[i], StringComparison.Ordinal) >= 0)
                {
                    hits++;
                    Check(false, "瞄准指示器（去注释后）不得出现 RPC/权威写/资产加载/旧链标记：" + forbidden[i]);
                }
            }

            Check(hits == 0, "T-LIVE3 瞄准指示器零 RPC / 零权威写 / 零资产加载 / 零旧链（禁用标记命中 0 处，共查 "
                  + forbidden.Length.ToString(CultureInfo.InvariantCulture) + " 个）");

            int mutatedHits = 0;
            for (int i = 0; i < forbidden.Length; i++)
            {
                if ((code + "\n" + forbidden[i] + "();\n").IndexOf(forbidden[i], StringComparison.Ordinal) >= 0)
                {
                    mutatedHits++;
                }
            }

            Check(mutatedHits == forbidden.Length,
                  "T-LIVE3 负例：把每个禁用标记注入指示器文本后扫描器 100% 命中（"
                  + mutatedHits.ToString(CultureInfo.InvariantCulture) + "/"
                  + forbidden.Length.ToString(CultureInfo.InvariantCulture) + "）⇒ 0 命中不是空转");
        }

        /// <summary>J1 用：找到唯一根节点（本 prefab 只有一个根 GameUI）。</summary>
        private static UiNode FindRoot(UiPrefabModel model)
        {
            UiNode named;
            if (model.NodesByPath.TryGetValue("/GameUI", out named)) { return named; }

            for (int i = 0; i < model.RootIds.Count; i++)
            {
                UiNode node;
                if (model.NodesById.TryGetValue(model.RootIds[i], out node)) { return node; }
            }

            return null;
        }


        /// <summary>J1 负例用：替换第一次出现的子串（找不到就原样返回）。</summary>
        private static string ReplaceFirstOccurrence(string text, string oldValue, string newValue)
        {
            int index = text.IndexOf(oldValue, StringComparison.Ordinal);
            if (index < 0) { return text; }
            return text.Substring(0, index) + newValue + text.Substring(index + oldValue.Length);
        }

        /// <summary>J1 负例夹具：把**根** GameUI 的 m_IsActive 从 0 改成 1（只动根那一处）。</summary>
        private static string BumpGameUiRootActiveFlag(string prefabText)
        {
            int nameIndex = prefabText.IndexOf("m_Name: GameUI", StringComparison.Ordinal);
            if (nameIndex < 0) { return prefabText; }

            int activeIndex = prefabText.IndexOf("m_IsActive: 0", nameIndex, StringComparison.Ordinal);
            if (activeIndex < 0) { return prefabText; }

            return prefabText.Substring(0, activeIndex) + "m_IsActive: 1"
                   + prefabText.Substring(activeIndex + "m_IsActive: 0".Length);
        }

        /// <summary>J1 用：读 `m_ReferenceResolution: {x: 800, y: 600}` 的 x（内联结构）。</summary>
        private static int ReadScalerReferenceResolutionX(string text)
        {
            Match match = Regex.Match(text, @"m_ReferenceResolution: \{x: (-?\d+(?:\.\d+)?)");
            if (!match.Success) { return -1; }

            return (int)Math.Round(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        /// <summary>J3 用：从源码文本里读一个 `public const float X = 0.05f;` 并断言取值。</summary>
        private static void CheckSourceConstFloat(string source, string name, double expected, string label)
        {
            double parsed;
            if (!TryReadConstFloat(source, name, out parsed))
            {
                Check(false, "J3 常量可读：" + label + "（源码里找不到 " + name + " 的字面常量）");
                return;
            }

            Check(Math.Abs(parsed - expected) < 1e-6,
                  "J3 常量与镜像一致：" + label + " == " + expected.ToString("R", CultureInfo.InvariantCulture)
                  + "（源码实际=" + parsed.ToString("R", CultureInfo.InvariantCulture) + "）");
        }

        /// <summary>
        /// J3：摇杆指针语义的**可执行镜像** + 与 UI 源码常量的交叉核对。
        ///
        /// 镜像按 UI 文件头登记的同一张状态机表实现：
        ///   down(可用) → 激活；重复 down → 忽略；drag(非激活者) → 忽略；
        ///   drag(激活者) → 移动推送（去重）/ 攻击只记瞄准；up(激活者) → 移动清零 + 攻击**提交一次**；
        ///   重复 up → 忽略；Cancel（死亡/终局/无快照/Dispose）→ 移动清零 + 攻击**不提交**。
        /// 屏幕口径：uGUI 本地 +Y 向上 ⇒ screenY **不取反**（契约：screen up → world −X）。
        /// </summary>
        private static void CheckUiPointerSemantics(string uiSource)
        {
            CheckSourceConstFloat(uiSource, "MinAimLength", 0.05, "MinAimLength");
            CheckSourceConstFloat(uiSource, "MovePushEpsilon", 0.0005, "MovePushEpsilon");
            CheckSourceConstFloat(uiSource, "LegacyJoystickDeadZoneRatio", 0.2, "LegacyJoystickDeadZoneRatio");
            CheckSourceConstFloat(uiSource, "LegacyJoystickZoneRadiusPixels", 100.0,
                                  "LegacyJoystickZoneRadiusPixels");

            UiPointerMirror move = new UiPointerMirror();
            move.Down(1);
            Check(move.Held && move.ActivePointer == 1, "J3 移动：down 激活该 pointerId");

            move.Down(2);
            Check(move.Held && move.ActivePointer == 1, "J3 移动：重复 down（另一指针）被忽略，不重置、不重复激活");

            bool dragOther = move.Drag(2, 80f, 0f, 100f, 0.2f);
            Check(!dragOther && move.MovePushes == 0, "J3 移动：非激活指针的 drag 被忽略（多指安全）");

            bool dragSelf = move.Drag(1, 50f, 0f, 100f, 0.2f);
            Check(dragSelf && move.MovePushes == 1, "J3 移动：激活指针 drag 推送一次");
            Check(Math.Abs(move.LastMoveX - 0.5f) < 0.0001f && Math.Abs(move.LastMoveY) < 0.0001f,
                  "J3 移动：本地 (50,0) / 半径 100 ⇒ 屏幕 (0.5, 0)");

            move.Drag(1, 50f, 0f, 100f, 0.2f);
            Check(move.MovePushes == 1, "J3 移动：同一向量重复 drag 不重复推送（去重）");

            move.Drag(1, 0f, 80f, 100f, 0.2f);
            Check(move.LastMoveY > 0.79f,
                  "J3 移动：本地 +Y（向上）⇒ screenY **为正**（uGUI 本地 +Y 与冻结屏幕口径同向，不取反）");
            Check(Math.Abs(move.LastMoveY - (-0.8f)) > 0.1f,
                  "J3 负例：若把 Y 取反，上面那条断言会失败（该断言不是恒真）");

            bool upOther = move.Up(2, 100f, 0.2f);
            Check(!upOther && move.Held, "J3 移动：非激活指针的 up 被忽略（去重）");

            move.Up(1, 100f, 0.2f);
            Check(!move.Held && Math.Abs(move.LastMoveX) < 0.0001f && Math.Abs(move.LastMoveY) < 0.0001f,
                  "J3 移动：松开清移动（推送归零向量）");
            Check(move.MovePushes == 3, "J3 移动：清零只推一次 (0,0)（实际推送="
                  + move.MovePushes.ToString(CultureInfo.InvariantCulture) + "）");

            bool upAgain = move.Up(1, 100f, 0.2f);
            Check(!upAgain && move.MovePushes == 3, "J3 移动：重复 up 被忽略（不再推送）");

            UiPointerMirror attack = new UiPointerMirror();
            attack.IsAttack = true;
            attack.Down(7);
            attack.Drag(7, 60f, 60f, 100f, 0.2f);
            Check(attack.Attacks == 0 && attack.MovePushes == 0,
                  "J3 攻击：拖动阶段**不**提交攻击（只记瞄准），也不产生移动推送");

            attack.Up(7, 100f, 0.2f);
            Check(attack.Attacks == 1, "J3 攻击：松手提交一次");
            attack.Up(7, 100f, 0.2f);
            Check(attack.Attacks == 1, "J3 攻击：重复 up 不提交第二次（边沿去重）");

            UiPointerMirror zeroAim = new UiPointerMirror();
            zeroAim.IsAttack = true;
            zeroAim.Down(3);
            zeroAim.Up(3, 100f, 0.2f);
            Check(zeroAim.Attacks == 0 && zeroAim.Suppressed == 1,
                  "J3 攻击：未给出方向（|aim| < MinAimLength）时不提交，只记抑制计数");

            UiPointerMirror deadZone = new UiPointerMirror();
            deadZone.Down(1);
            deadZone.Drag(1, 5f, 0f, 100f, 0.2f);
            Check(deadZone.MovePushes == 1 && Math.Abs(deadZone.LastMoveX) < 0.0001f,
                  "J3 死区：|local| ≤ 20（= 0.2*100）⇒ 输出显式 (0,0)，不是小数值");

            UiPointerMirror clamp = new UiPointerMirror();
            clamp.Down(1);
            clamp.Drag(1, 500f, 0f, 100f, 0.2f);
            Check(Math.Abs(clamp.LastMoveX - 1f) < 0.0001f, "J3 钳制：超出半径的输出被钳到 +1");

            UiPointerMirror disabled = new UiPointerMirror();
            disabled.Interactable = false;
            disabled.Down(1);
            Check(!disabled.Held, "J3 可用性：不可用（死亡/终局/无快照）时 down 被忽略");

            UiPointerMirror nan = new UiPointerMirror();
            nan.Down(1);
            bool nanDrag = nan.Drag(1, float.NaN, 0f, 100f, 0.2f);
            Check(!nanDrag && nan.MovePushes == 0, "J3 健壮性：NaN 本地坐标被拒绝，不推送");

            UiPointerMirror cancelMove = new UiPointerMirror();
            cancelMove.Down(1);
            cancelMove.Drag(1, 70f, 0f, 100f, 0.2f);
            int beforeCancel = cancelMove.MovePushes;
            cancelMove.Cancel();
            Check(!cancelMove.Held && cancelMove.Cancels == 1 && cancelMove.MovePushes == beforeCancel + 1
                  && Math.Abs(cancelMove.LastMoveX) < 0.0001f,
                  "J3 取消：移动摇杆 Cancel 清零；Held=false；计数 +1");

            UiPointerMirror cancelAttack = new UiPointerMirror();
            cancelAttack.IsAttack = true;
            cancelAttack.Down(4);
            cancelAttack.Drag(4, 70f, 0f, 100f, 0.2f);
            cancelAttack.Cancel();
            Check(cancelAttack.Attacks == 0 && cancelAttack.Cancels == 1,
                  "J3 取消：攻击摇杆 Cancel **不提交**攻击（死亡/终局/Dispose 路径绝不能开火）");

            UiPointerMirror cancelIdle = new UiPointerMirror();
            cancelIdle.Cancel();
            Check(cancelIdle.Cancels == 0, "J3 取消：未按住时 Cancel 是空操作（幂等）");

            UiPointerMirror disposed = new UiPointerMirror();
            disposed.Disposed = true;
            disposed.Down(1);
            disposed.Drag(1, 90f, 0f, 100f, 0.2f);
            disposed.Up(1, 100f, 0.2f);
            Check(!disposed.Held && disposed.MovePushes == 0 && disposed.Attacks == 0,
                  "J3 释放：Dispose 之后 down/drag/up 全部无效（不留卡住的方向，也不开火）");
        }

        /// <summary>
        /// J4：新 UI 源码 / `.meta` 的编码事实（契约：中文源码 UTF-8 **BOM** + CRLF；meta 无 BOM + LF），
        /// 以及 meta guid 的全仓库唯一性（否则 Unity 会把两个资源当同一个）。
        /// </summary>
        private static void CheckBattleControlsEncodingAndMeta(string repoRoot, string uiSourcePath)
        {
            byte[] sourceBytes = File.ReadAllBytes(uiSourcePath);
            bool sourceBom = sourceBytes.Length >= 3 && sourceBytes[0] == 0xEF && sourceBytes[1] == 0xBB
                             && sourceBytes[2] == 0xBF;
            Check(sourceBom, "新 UI 源码带 UTF-8 BOM（项目“中文源码 BOM”纪律）");

            int sourceCrlf = 0;
            int sourceLoneLf = 0;
            for (int i = 0; i < sourceBytes.Length; i++)
            {
                if (sourceBytes[i] != (byte)'\n') { continue; }
                if (i > 0 && sourceBytes[i - 1] == (byte)'\r') { sourceCrlf++; }
                else { sourceLoneLf++; }
            }

            Check(sourceCrlf > 0 && sourceLoneLf == 0,
                  "新 UI 源码换行是 CRLF（实际 CRLF=" + sourceCrlf.ToString(CultureInfo.InvariantCulture)
                  + " loneLF=" + sourceLoneLf.ToString(CultureInfo.InvariantCulture) + "）");

            string metaPath = Path.Combine(repoRoot,
                BattleControlsMetaRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Check(File.Exists(metaPath), "新 UI 脚本带 .meta（否则 Unity 会自己生成一个，等于没锁 guid）");
            if (!File.Exists(metaPath)) { return; }

            byte[] metaBytes = File.ReadAllBytes(metaPath);
            bool metaBom = metaBytes.Length >= 3 && metaBytes[0] == 0xEF && metaBytes[1] == 0xBB
                           && metaBytes[2] == 0xBF;
            Check(!metaBom, "新 UI .meta 无 BOM");

            int metaCrlf = 0;
            for (int i = 0; i < metaBytes.Length; i++)
            {
                if (metaBytes[i] == (byte)'\n' && i > 0 && metaBytes[i - 1] == (byte)'\r') { metaCrlf++; }
            }

            Check(metaCrlf == 0, "新 UI .meta 使用 LF 换行（与 Unity 生成的 meta 一致）");

            Match guid = Regex.Match(File.ReadAllText(metaPath, Encoding.UTF8),
                                     @"(?m)^guid: ([0-9a-f]{32})\s*$");
            Check(guid.Success, "新 UI .meta 含 32 位 hex guid");
            if (!guid.Success) { return; }

            string metaGuid = guid.Groups[1].Value;
            int occurrences = 0;
            string assetsRoot = Path.Combine(repoRoot, "Client", "Assets");
            string[] allMetas = Directory.GetFiles(assetsRoot, "*.meta", SearchOption.AllDirectories);
            for (int i = 0; i < allMetas.Length; i++)
            {
                if (File.ReadAllText(allMetas[i], Encoding.UTF8)
                          .IndexOf("guid: " + metaGuid, StringComparison.Ordinal) >= 0)
                {
                    occurrences++;
                }
            }

            Check(occurrences == 1, "新 UI .meta 的 guid 在 Assets 内唯一（出现次数="
                  + occurrences.ToString(CultureInfo.InvariantCulture) + "；guid=" + metaGuid + "）");

            string prefabMetaPath = Path.Combine(repoRoot,
                GameUiPrefabMetaRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(prefabMetaPath))
            {
                Check(File.ReadAllText(prefabMetaPath, Encoding.UTF8)
                           .IndexOf("guid: " + GameUiPrefabGuid, StringComparison.Ordinal) >= 0,
                      "旧局内 UI 预制体的 guid 与契约登记一致（" + GameUiPrefabGuid + "）");
            }
        }

        /// <summary>
        /// J4：PMR4UnityCheck 必须真的引用 **Client/Library/ScriptAssemblies/UnityEngine.UI.dll**
        /// 与 uGUI 依赖的引擎模块（UIModule / TextRenderingModule）——否则“真实 Unity 2019 API 编译门”
        /// 会给出一份“没编 uGUI 实现”的假绿。
        /// </summary>
        private static void CheckR4UnityCheckUiReferences(string repoRoot)
        {
            string csprojPath = Path.Combine(repoRoot,
                R4UnityCheckCsprojRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Check(File.Exists(csprojPath), "PMR4UnityCheck 工程存在：" + R4UnityCheckCsprojRelativePath);
            if (!File.Exists(csprojPath)) { return; }

            string csproj = File.ReadAllText(csprojPath, Encoding.UTF8);
            Check(csproj.IndexOf("UnityEngine.UI.dll", StringComparison.Ordinal) >= 0,
                  "PMR4UnityCheck 引用真实 UnityEngine.UI.dll（工程 Library/ScriptAssemblies 的包产物）");
            Check(csproj.IndexOf("UnityEngine.UIModule.dll", StringComparison.Ordinal) >= 0,
                  "PMR4UnityCheck 引用 UnityEngine.UIModule.dll（Canvas/RenderMode 所在模块）");
            Check(csproj.IndexOf("UnityEngine.TextRenderingModule.dll", StringComparison.Ordinal) >= 0,
                  "PMR4UnityCheck 引用 UnityEngine.TextRenderingModule.dll（Text/Font 所在模块）");
            Check(csproj.IndexOf("DefineConstants>UNITY_EDITOR", StringComparison.Ordinal) >= 0,
                  "PMR4UnityCheck 显式定义 UNITY_EDITOR ⇒ 会编入 UI 的**完整实现**而不是替身面");

            Check(File.Exists(Path.Combine(repoRoot,
                      UnityUiAssemblyRelativePath.Replace('/', Path.DirectorySeparatorChar))),
                  "真实 UnityEngine.UI.dll 存在于工程 Library（本机 Unity 已导入 ugui 包）："
                  + UnityUiAssemblyRelativePath);
        }

        /// <summary>
        /// J4：两组代码已经主侧集成。禁止恢复成“UI 类存在，但宿主不创建/绑定/清理”的假接线。
        /// 这是源码级门，不宣称 Unity 指针/Canvas 的实机行为已通过。
        /// </summary>
        private static void CheckBattleControlsHostWiring(string repoRoot)
        {
            string hostPath = Path.Combine(repoRoot,
                SessionHostRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Check(File.Exists(hostPath), "局内操控 UI 宿主接线：真实源码存在");
            if (!File.Exists(hostPath)) { return; }

            string host = File.ReadAllText(hostPath, Encoding.UTF8);
            Check(HasBattleControlsWiring(host),
                  "局内UI实际接线：创建、绑定三输入出口、复制快照、每帧刷新、释放均存在");

            string[] critical = {
                "session.Controls = PMUnityBattleControls.Create(offer.MatchId, out controlsError);",
                "session.Controls.Bind(TrySetUiMove, TryQueueUiAttack, QueryBattleControlsStatus, TrySetUiAim);",
                "session.Controls.UpdateStatus();",
                "controls.Dispose();",
                "session.AimIndicator = PMUnityBattleAimIndicator.Create(offer.MatchId, out aimError);",
                "UpdateAimIndicator(session);",
                "ClearUiAim(session);",
                "AimIndicator.Dispose();"
            };
            for (int i = 0; i < critical.Length; i++)
            {
                string removed = host.Replace(critical[i], string.Empty);
                Check(!string.Equals(removed, host, StringComparison.Ordinal)
                      && !HasBattleControlsWiring(removed),
                      "局内UI接线负例：删除第 " + (i + 1).ToString() + " 个关键调用必须被拒");
            }
        }

        private static bool HasBattleControlsWiring(string source)
        {
            if (string.IsNullOrEmpty(source)) { return false; }
            return source.Contains("session.Controls = PMUnityBattleControls.Create(offer.MatchId, out controlsError);")
                   && source.Contains("session.Controls.Bind(TrySetUiMove, TryQueueUiAttack, QueryBattleControlsStatus, TrySetUiAim);")
                   && source.Contains("session.Controls.UpdateStatus();")
                   && source.Contains("controls.Dispose();")
                   && source.Contains("TryGetUiCombatSnapshot(out snapshot)")
                   && source.Contains("session.Controls = null;")
                   && source.Contains("public static bool TrySetUiAim(bool isSuper, bool active, float screenX, float screenY)")
                   && source.Contains("session.AimIndicator = PMUnityBattleAimIndicator.Create(offer.MatchId, out aimError);")
                   && source.Contains("UpdateAimIndicator(session);")
                   && source.Contains("ClearUiAim(session);")
                   && source.Contains("AimIndicator.Dispose();");
        }

        /// <summary>
        /// J3 用的**可执行镜像**：按 UI 头注释登记的同一张语义表实现的摇杆状态机。
        /// 它不是 UI 的实现（本工具编不到那个文件），只用来把“去重/释放/死区/不取反”这些纯逻辑规则
        /// 跑成正负例；与实现的常量一致性由 <see cref="CheckUiPointerSemantics"/> 交叉核对。
        /// </summary>
        private sealed class UiPointerMirror
        {
            public const int NoPointer = int.MinValue;

            public bool IsAttack;
            public bool Interactable = true;
            public bool Disposed;
            public int ActivePointer = NoPointer;

            public int MovePushes;
            public int Attacks;
            public int Suppressed;
            public int Cancels;

            public float LastMoveX;
            public float LastMoveY;

            private float _aimX;
            private float _aimY;
            private bool _hasMove;

            public bool Held { get { return ActivePointer != NoPointer; } }

            public void Down(int pointerId)
            {
                if (Disposed) { return; }
                if (Held) { return; }
                if (!Interactable) { return; }
                ActivePointer = pointerId;
            }

            public bool Drag(int pointerId, float localX, float localY, float radius, float deadZoneRatio)
            {
                if (Disposed) { return false; }
                if (!Held || ActivePointer != pointerId) { return false; }

                float sx;
                float sy;
                if (!MirrorNormalize(localX, localY, radius, deadZoneRatio, out sx, out sy)) { return false; }

                _aimX = sx;
                _aimY = sy;

                if (!IsAttack) { PushMove(sx, sy); }
                return true;
            }

            public bool Up(int pointerId, float radius, float deadZoneRatio)
            {
                if (Disposed) { return false; }
                if (!Held || ActivePointer != pointerId) { return false; }

                ActivePointer = NoPointer;

                float sx = _aimX;
                float sy = _aimY;
                _aimX = 0f;
                _aimY = 0f;

                if (!IsAttack)
                {
                    PushMove(0f, 0f);
                    return true;
                }

                if (MirrorMagnitude(sx, sy) < 0.05f)
                {
                    Suppressed++;
                    return true;
                }

                Attacks++;
                return true;
            }

            public void Cancel()
            {
                if (Disposed) { return; }
                if (!Held) { return; }

                ActivePointer = NoPointer;
                Cancels++;
                _aimX = 0f;
                _aimY = 0f;

                if (!IsAttack) { PushMove(0f, 0f); }
            }

            private void PushMove(float x, float y)
            {
                if (_hasMove && Math.Abs(x - LastMoveX) <= 0.0005 && Math.Abs(y - LastMoveY) <= 0.0005)
                {
                    return;
                }

                _hasMove = true;
                LastMoveX = x;
                LastMoveY = y;
                MovePushes++;
            }

            private static float MirrorMagnitude(float x, float y)
            {
                return (float)Math.Sqrt((double)x * (double)x + (double)y * (double)y);
            }

            private static bool MirrorNormalize(float localX, float localY, float radius,
                                               float deadZoneRatio, out float screenX, out float screenY)
            {
                screenX = 0f;
                screenY = 0f;

                if (float.IsNaN(localX) || float.IsInfinity(localX)
                    || float.IsNaN(localY) || float.IsInfinity(localY))
                {
                    return false;
                }

                if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0f) { return false; }

                if (MirrorMagnitude(localX, localY) <= deadZoneRatio * radius) { return true; }

                screenX = ClampUnit(localX / radius);
                screenY = ClampUnit(localY / radius);   // uGUI 本地 +Y 向上 ⇒ 不取反
                return true;
            }

            private static float ClampUnit(float value)
            {
                if (value <= -1f) { return -1f; }
                if (value >= 1f) { return 1f; }
                return value;
            }
        }

        /// <summary>
        /// J1 用的极简 prefab 文本模型：只够核对树/节点/脚本 guid/Sprite guid（不解析 Unity 语义）。
        ///
        /// 为什么需要它：Unity 的 GameObject 文档只写“我有这些组件”的引用，组件实体
        /// （MonoBehaviour / Image / EasyJoystick 字段）在**各自的文档**里；因此每个节点要把自己的文档 +
        /// 全部组件文档拼起来（<see cref="UiNode.ComponentText"/>），否则 `m_Sprite` / `m_MethodName` /
        /// `zoneRadius` 这些字段根本读不到。
        /// </summary>
        private sealed class UiPrefabModel
        {
            public readonly Dictionary<long, UiNode> NodesById = new Dictionary<long, UiNode>();
            public readonly Dictionary<string, UiNode> NodesByPath =
                new Dictionary<string, UiNode>(StringComparer.Ordinal);
            public readonly Dictionary<long, string> ScriptGuidByComponent = new Dictionary<long, string>();
            public readonly Dictionary<long, string> SpriteGuidByComponent = new Dictionary<long, string>();
            public readonly List<long> RootIds = new List<long>();
            public int ParentEdgeCount;
            public int TransformDocCount;
            public int OwnerReadCount;

            public static UiPrefabModel Load(string text)
            {
                UiPrefabModel model = new UiPrefabModel();
                Dictionary<long, string> docByFileId = new Dictionary<long, string>();
                Dictionary<long, long> transformOwner = new Dictionary<long, long>();
                Dictionary<long, long> transformFather = new Dictionary<long, long>();
                string[] docs = Regex.Split(text, @"(?m)^--- ");

                for (int i = 0; i < docs.Length; i++)
                {
                    string doc = docs[i];
                    Match head = Regex.Match(doc, @"^!u!(?<class>\d+) &(?<id>-?\d+)");
                    if (!head.Success) { continue; }

                    int classId = int.Parse(head.Groups["class"].Value, CultureInfo.InvariantCulture);
                    long fileId = long.Parse(head.Groups["id"].Value, CultureInfo.InvariantCulture);
                    docByFileId[fileId] = doc;

                    if (classId == 1)
                    {
                        UiNode node = new UiNode();
                        node.Id = fileId;
                        node.Name = DecodeYamlName(ReadName(doc));
                        node.Active = ReadInt(doc, "m_IsActive", 1) != 0;

                        foreach (Match component in Regex.Matches(doc, @"- component: \{fileID: (-?\d+)\}"))
                        {
                            node.Components.Add(long.Parse(component.Groups[1].Value,
                                                           CultureInfo.InvariantCulture));
                        }

                        model.NodesById[fileId] = node;
                        continue;
                    }

                    if (classId == 4 || classId == 224)
                    {
                        model.TransformDocCount++;

                        // 关键：m_Children / m_Father 里是 **Transform 组件的 fileID**，不是 GameObject 的 fileID。
                        // 必须先用 m_GameObject 把 Transform 映射回 GameObject，才能建出真实层级
                        // （早期版本直接拿 m_Children 当 GameObject id，结果一个父边都建不出来）。
                        long owner = ReadFileId(doc, "m_GameObject");
                        if (owner == 0) { continue; }

                        model.OwnerReadCount++;
                        transformOwner[fileId] = owner;
                        transformFather[fileId] = ReadFileId(doc, "m_Father");
                        continue;
                    }
                    if (classId != 114) { continue; }

                    Match script = Regex.Match(doc, @"m_Script: \{fileID: -?\d+, guid: (?<guid>[0-9a-f]{32})");
                    if (script.Success) { model.ScriptGuidByComponent[fileId] = script.Groups["guid"].Value; }

                    Match sprite = Regex.Match(doc, @"m_Sprite: \{fileID: \d+, guid: (?<guid>[0-9a-f]{32})");
                    if (sprite.Success) { model.SpriteGuidByComponent[fileId] = sprite.Groups["guid"].Value; }
                }

                foreach (KeyValuePair<long, long> entry in transformOwner)
                {
                    long childGameObjectId = entry.Value;
                    long fatherTransformId;

                    if (!transformFather.TryGetValue(entry.Key, out fatherTransformId)) { continue; }
                    if (fatherTransformId == 0) { continue; }   // 根：没有父 Transform

                    long parentGameObjectId;
                    if (!transformOwner.TryGetValue(fatherTransformId, out parentGameObjectId)) { continue; }

                    UiNode parentNode;
                    UiNode childNode;
                    if (!model.NodesById.TryGetValue(parentGameObjectId, out parentNode)) { continue; }
                    if (!model.NodesById.TryGetValue(childGameObjectId, out childNode)) { continue; }

                    parentNode.Children.Add(childNode.Id);
                    childNode.HasParent = true;
                    model.ParentEdgeCount++;
                }

                foreach (KeyValuePair<long, UiNode> pair in model.NodesById)
                {
                    if (!pair.Value.HasParent) { model.RootIds.Add(pair.Key); }
                }

                for (int i = 0; i < model.RootIds.Count; i++)
                {
                    UiNode node;
                    if (model.NodesById.TryGetValue(model.RootIds[i], out node))
                    {
                        model.AssignPath(node, string.Empty, docByFileId);
                    }
                }

                return model;
            }

            private void AssignPath(UiNode node, string parentPath, Dictionary<long, string> docByFileId)
            {
                node.Path = parentPath + "/" + node.Name;
                NodesByPath[node.Path] = node;

                StringBuilder sb = new StringBuilder();
                string ownDoc;
                if (docByFileId.TryGetValue(node.Id, out ownDoc)) { sb.Append(ownDoc); }

                for (int i = 0; i < node.Components.Count; i++)
                {
                    string componentDoc;
                    if (docByFileId.TryGetValue(node.Components[i], out componentDoc))
                    {
                        sb.Append('\n').Append(componentDoc);
                    }
                }

                node.ComponentText = sb.ToString();

                for (int i = 0; i < node.Children.Count; i++)
                {
                    UiNode child;
                    if (NodesById.TryGetValue(node.Children[i], out child))
                    {
                        AssignPath(child, node.Path, docByFileId);
                    }
                }
            }

            public UiNode Find(string relativePath)
            {
                UiNode node;
                return NodesByPath.TryGetValue("/" + relativePath, out node) ? node : null;
            }

            public bool HasScriptGuid(UiNode node, string guid)
            {
                for (int i = 0; i < node.Components.Count; i++)
                {
                    string value;
                    if (ScriptGuidByComponent.TryGetValue(node.Components[i], out value)
                        && string.Equals(value, guid, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }

            public string SpriteGuid(UiNode node)
            {
                for (int i = 0; i < node.Components.Count; i++)
                {
                    string value;
                    if (SpriteGuidByComponent.TryGetValue(node.Components[i], out value)) { return value; }
                }

                return null;
            }
        }

        /// <summary>J1 用的节点（名字已解 YAML 转义；路径按 `/A/B` 记；ComponentText 含组件文档）。</summary>
        private sealed class UiNode
        {
            public long Id;
            public string Name = string.Empty;
            public bool Active;
            public bool HasParent;
            public string Path = string.Empty;
            public string ComponentText = string.Empty;
            public readonly List<long> Components = new List<long>();
            public readonly List<long> Children = new List<long>();
        }

        /// <summary>
        /// 解 YAML 双引号名字里的转义（旧 prefab 里中文节点名序列化成 "\u80FD\u91CF\u6761" 形式）。
        /// 只处理 \uXXXX 与 \ / \" —— 够用且不引入 YAML 解析器。
        /// </summary>
        private static string DecodeYamlName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) { return string.Empty; }
            if (raw.Length < 2 || raw[0] != '"' || raw[raw.Length - 1] != '"') { return raw; }

            string body = raw.Substring(1, raw.Length - 2);
            StringBuilder sb = new StringBuilder(body.Length);

            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if ((int)c != 92 || i + 1 >= body.Length)
                {
                    sb.Append(c);
                    continue;
                }

                char next = body[i + 1];
                if (next == 'u' && i + 5 < body.Length)
                {
                    int code;
                    if (int.TryParse(body.Substring(i + 2, 4), NumberStyles.HexNumber,
                                     CultureInfo.InvariantCulture, out code))
                    {
                        sb.Append((char)code);
                        i += 5;
                        continue;
                    }
                }

                if ((int)next == 92 || next == '"')
                {
                    sb.Append(next);
                    i += 1;
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
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
