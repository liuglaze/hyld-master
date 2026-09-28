// ============================================================================
//  PMUnityBattleLighting —— T-PLAY3 客户端专有光照（正式本局，独立且可精确释放）
// ============================================================================
//
//  契约来源：Docs/plans/net-architecture-migration.md 末尾「T-PLAY 第一阶段接口与验收细化」B 组：
//    · 「B 组仅 PMUnityBattleMap 与新增独立 PMUnityBattleLighting(+meta)/必要测试，在客户端本局
//       独立场景重建旧 MAP/lights 蓝方向光与青聚光的运行时表现，DS 不得创建 Light，
//       不修改地图 prefab/manifest/LightmapSettings/RenderSettings 或源场景」
//
//  参数来源（**真实序列化值**，逐字段抄自源场景 YAML，不靠记忆/推测）：
//    Client/Assets/Scenes/HYLDGame.unity → HYLDGameTatal/MAP/lights（3 盏子灯）：
//      · lights 自身       Transform fileID 2140687510989074931（localPosition (0,-8.1,0)、rot identity、scale 1）
//      · Directional Light GameObject 2140687511124058765 / Light 2140687511124058766 / Transform 2140687511124058767
//      · Spot Light #1     GameObject 2140687511585649917 / Light 2140687511585649918 / Transform 2140687511585649919
//      · Spot Light #2     GameObject 2140687512317984506 / Light 2140687512317984507 / Transform 2140687512317984508
//    父链 HYLDGameTatal（Transform 1530457320478066174）与 MAP（Transform 525959233547396898）都是
//    **单位 TRS**（pos (0,0,0)、rot identity、scale 1）⇒ lights 的 local TRS 就是它的**世界** TRS。
//    因此本文件把容器摆回源场景同一个世界位置 (0,-8.1,0)，再把 3 盏子灯按**原 local TRS** 挂上去，
//    重建后的世界位置/旋转与源场景逐位一致，不使用任何"看起来差不多"的近似。
//    ★ 明确**未复制**、也未伪称等价的源字段：m_Cookie/m_Flare/m_DrawHalo（均 0/None）、
//      m_Shape（0=Cone，聚光默认形状）、m_CookieSize（10，仅 cookie 非空时有意义）、
//      m_ShadowRadius/m_ShadowAngle（0，仅软点光/面光有意义）、m_ColorTemperature（6570，但
//      m_UseColorTemperature=0 ⇒ 不生效）。其余（type/颜色/intensity/range/spotAngle/innerSpotAngle/
//      阴影四项/剔除掩码/渲染模式/弹射强度/位置/旋转）**全部复制**，见定义表的列。
//    ★ **一处 Editor/Player 分叉（已由真实 Player 程序集门禁发现并修正）**：源场景的
//      `m_Lightmapping = 4`（Realtime）只能通过**编辑器API** `Light.lightmapBakeType` 赋值；
//      该属性在 Player 变体的 UnityEngine 程序集里**不存在**，无条件赋值会让 Build Player 以
//      `CS1061` 失败（用户在 14:49 真实遇到，Editor 播放却正常）。因此该赋值用
//      `#if UNITY_EDITOR` 圈定：**Player 下不设置它是等价语义**（运行时新建的灯本来就是实时灯，
//      打包产物内也没有可参与烘焙的场景光照数据），而不是功能缺失。
//
//  --- 诚实边界（不得读成"逐像素还原了旧 HYLDGame 画面"）---
//    1) **不搬运任何场景级光照数据**：源场景本身没有烘焙 lightmap（HYLDGame.unity 内无 m_Lightmaps、
//       无 m_Lightmap 引用）；RenderSettings/LightmapSettings 是场景级数据，无法随 prefab 带走。
//       本实现也**不写** RenderSettings / LightmapSettings / QualitySettings，不加 postprocessing。
//    2) **HYLDStart 的暖方向光仍在叠加**：正式对局时活动场景仍是 HYLDStart，其 Directional Light
//       （颜色 (1, 0.95686275, 0.8392157)、intensity 1）与天光/环境（ambientMode=0 Skybox、
//       ambientSkyColor (0.212,0.227,0.259) 等）继续参与渲染。本实现**不修改、不禁用、不复制**它。
//       所以最终画面 = HYLDStart 暖方向光 + 本文件三盏灯 + HYLDStart 环境/天空盒，
//       **不等于**原 HYLDGame 场景的逐像素结果（原场景还有它自己的 RenderSettings/Sun 口径）。
//    3) 由此 T-PLAY3 本轮的**可观察口径只有**：参数与源 YAML 逐位一致 + 正式客户端恰好 3 盏 +
//       DS 恰好 0 盏 + 退局精确销毁 + 灯不参与物理/不改地图源。**颜色最终观感必须由用户实机
//       （T-PLAY5）确认**，本文件不声称颜色已经准确。
//
//  --- 身份与生命周期（对应 T-PLAY3a 的 4 条负例口径）---
//    · **DS 零灯**：TryCreate 的第一句就是 PMNetRuntime.IsDedicatedServer 判定（双保险：调用方
//      PMUnityBattleMap.ApplyClientLighting 也先判一次）；DS 上不 new 任何 GameObject/Light。
//    · **归入本局隔离物理场景**：容器是场景内的**独立根节点**（不是地图 prefab 根的子节点），
//      SceneManager.MoveGameObjectToScene 后**校验 scene.handle 一致**，不一致就销毁并失败。
//      因此它既落在本局 LocalPhysicsScene，又**不进入** PMUnityBattleMap 的 Collider 白名单遍历
//      （白名单只扫地图实例子树），也不进 GetSceneDigest ⇒ DS/客户端摘要仍可比。
//    · **灯不参与物理/不改地图源**：只 new GameObject + AddComponent<Light> + 写 Transform/Light
//      属性；**不建** Collider/Rigidbody，**不写**任何资产（无 AssetDatabase/PrefabUtility/Material）。
//    · **精确释放**：Dispose() 幂等，销毁整个容器（子灯随之销毁）；PMUnityBattleMap.Dispose 在销毁
//      地图根**之前**先调它 ⇒ Stop/换局/入局失败都不残留灯。诊断模式（PMR3TestScene 胶囊路径）
//      根本不经过 PMUnityBattleMap，因此不触碰本文件。
//
//  --- 编译面（受写入边界限制的已知妥协，已登记为阻塞项，见报告「登记项」）---
//    Tools/PMClientCheck/ClientStubs.cs 的 Light 只有 intensity/color；Tools/PMUnityGlueCheck/
//    UnityStubs.cs **完全没有 Light 类型**（也没有 GameObject.AddComponent(Type)）。这两个桩件都**不在**
//    本轮可写边界内，而它们的 csproj 用 PMUnity\**\*.cs 通配符把本文件编进来 ⇒ 若把真实 Light API
//    暴露给它们，两个既有门禁会直接编译失败（CS0246 / CS0117）。因此：
//      · **纯数据契约区**（PMBattleLightDefinition / PMBattleLightingSource）保持零 UnityEngine 依赖，
//        所有编译面都能编（含两个桩件门禁）；
//      · **Unity 实现区**（PMUnityBattleLighting）用 #if UNITY_2019_1_OR_NEWER || UNITY_EDITOR 圈定：
//          - 真实 Unity 构建（Editor 与所有 Player 目标）都定义 UNITY_2019_1_OR_NEWER ⇒ 实现区
//            **真实存在于正式客户端/DS 二进制**（不是编辑器专用、不是被裁掉的死码）；
//          - Tools/PMR4UnityCheck 定义 UNITY_EDITOR 且引用**真实 Unity 2019.4 DLL** ⇒ 实现区的
//            Light API 仍被"真实 Unity2019 API 编译门"覆盖（这就是本轮 T-PLAY3a 的 API 证据）；
//          - 两个桩件门禁不定义任何 UNITY_* ⇒ 只编纯数据区，保持它们原有的绿灯。
//        要彻底去掉这个 #if，需要（**不在本轮写入清单内**，故只登记、不代改）：
//          1) 给 ClientStubs.Light 补 type/range/spotAngle/innerSpotAngle/shadows/shadowStrength/
//             shadowBias/shadowNormalBias/shadowNearPlane/cullingMask/renderMode/lightmapBakeType/
//             bounceIntensity/useColorTemperature，并补 LightType/LightShadows/LightRenderMode/LightmapBakeType；
//          2) 给 UnityStubs 新增同样的 Light 面；
//          3) 给 Tools/PMBattleContentRuntimeCheck.csproj 补一行
//             Compile Include="..\..\Client\Assets\Scripts\PMUnity\PMUnityBattleLighting.cs"
//             （该工程用显式 Compile Include，不通配 PMUnity 目录）。
// ============================================================================

using System;
using System.Globalization;
using System.Text;

#if UNITY_2019_1_OR_NEWER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.SceneManagement;
#endif

namespace PMNet.Unity
{
    /// <summary>
    /// 一条**冻结**的客户端光照定义（纯数据，零 UnityEngine 依赖 ⇒ 任何编译面都能编）。
    ///
    /// 数值一字不改地来自源场景 HYLDGameTatal/MAP/lights 的序列化 YAML；
    /// 位置/旋转是**相对容器**的 local TRS（容器本身在 (0,-8.1,0) 且单位旋转，与源场景同一世界位置）。
    /// </summary>
    public struct PMBattleLightDefinition
    {
        /// <summary>Unity LightType.Spot 的序列化值（源 m_Type）。</summary>
        public const int KindSpot = 0;

        /// <summary>Unity LightType.Directional 的序列化值（源 m_Type）。</summary>
        public const int KindDirectional = 1;

        /// <summary>Unity LightShadows.None 的序列化值（源 m_Shadows.m_Type）。</summary>
        public const int ShadowNone = 0;

        /// <summary>Unity LightShadows.Hard 的序列化值（源三盏灯都是它）。</summary>
        public const int ShadowHard = 1;

        /// <summary>Unity LightShadows.Soft 的序列化值（源场景没有用到）。</summary>
        public const int ShadowSoft = 2;

        /// <summary>源 GameObject 名（保真用；"Spot Light" 在源场景里本就重名，Unity 允许）。</summary>
        public readonly string Name;

        /// <summary>灯类型（<see cref="KindSpot"/> / <see cref="KindDirectional"/>）。</summary>
        public readonly int Kind;

        /// <summary>颜色 R（源 m_Color.r）。</summary>
        public readonly float ColorR;

        /// <summary>颜色 G（源 m_Color.g）。</summary>
        public readonly float ColorG;

        /// <summary>颜色 B（源 m_Color.b）。</summary>
        public readonly float ColorB;

        /// <summary>强度（源 m_Intensity）。</summary>
        public readonly float Intensity;

        /// <summary>范围（源 m_Range；方向光不生效，但仍按源值复制）。</summary>
        public readonly float Range;

        /// <summary>聚光角（源 m_SpotAngle；方向光不生效，但仍按源值复制）。</summary>
        public readonly float SpotAngle;

        /// <summary>内聚光角（源 m_InnerSpotAngle；方向光不生效，但仍按源值复制）。</summary>
        public readonly float InnerSpotAngle;

        /// <summary>相对容器的 local X（源 Transform m_LocalPosition.x）。</summary>
        public readonly float LocalPositionX;

        /// <summary>相对容器的 local Y（源 Transform m_LocalPosition.y）。</summary>
        public readonly float LocalPositionY;

        /// <summary>相对容器的 local Z（源 Transform m_LocalPosition.z）。</summary>
        public readonly float LocalPositionZ;

        /// <summary>旋转四元数 X（源 Transform m_LocalRotation.x）。</summary>
        public readonly float LocalRotationX;

        /// <summary>旋转四元数 Y（源 Transform m_LocalRotation.y）。</summary>
        public readonly float LocalRotationY;

        /// <summary>旋转四元数 Z（源 Transform m_LocalRotation.z）。</summary>
        public readonly float LocalRotationZ;

        /// <summary>旋转四元数 W（源 Transform m_LocalRotation.w）。</summary>
        public readonly float LocalRotationW;

        /// <summary>阴影类型（<see cref="ShadowNone"/> / <see cref="ShadowHard"/> / <see cref="ShadowSoft"/>）。</summary>
        public readonly int ShadowType;

        /// <summary>阴影强度（源 m_Shadows.m_Strength）。</summary>
        public readonly float ShadowStrength;

        /// <summary>阴影深度偏移（源 m_Shadows.m_Bias）。</summary>
        public readonly float ShadowBias;

        /// <summary>阴影法线偏移（源 m_Shadows.m_NormalBias）。</summary>
        public readonly float ShadowNormalBias;

        /// <summary>阴影近裁面（源 m_Shadows.m_NearPlane）。</summary>
        public readonly float ShadowNearPlane;

        /// <summary>
        /// 构造一条定义。参数顺序即 PMBattleLightingSource.DefinitionsTable 的列顺序
        /// （**T-PLAY3a 静态对照锚点依赖这个顺序，勿改**）：
        /// name, kind, r, g, b, intensity, range, spotAngle, innerSpotAngle,
        /// posX, posY, posZ, rotX, rotY, rotZ, rotW,
        /// shadowType, shadowStrength, shadowBias, shadowNormalBias, shadowNearPlane。
        /// </summary>
        public PMBattleLightDefinition(string name, int kind,
                                       float colorR, float colorG, float colorB,
                                       float intensity, float range, float spotAngle, float innerSpotAngle,
                                       float localPositionX, float localPositionY, float localPositionZ,
                                       float localRotationX, float localRotationY, float localRotationZ, float localRotationW,
                                       int shadowType, float shadowStrength, float shadowBias,
                                       float shadowNormalBias, float shadowNearPlane)
        {
            Name = name;
            Kind = kind;
            ColorR = colorR;
            ColorG = colorG;
            ColorB = colorB;
            Intensity = intensity;
            Range = range;
            SpotAngle = spotAngle;
            InnerSpotAngle = innerSpotAngle;
            LocalPositionX = localPositionX;
            LocalPositionY = localPositionY;
            LocalPositionZ = localPositionZ;
            LocalRotationX = localRotationX;
            LocalRotationY = localRotationY;
            LocalRotationZ = localRotationZ;
            LocalRotationW = localRotationW;
            ShadowType = shadowType;
            ShadowStrength = shadowStrength;
            ShadowBias = shadowBias;
            ShadowNormalBias = shadowNormalBias;
            ShadowNearPlane = shadowNearPlane;
        }

        /// <summary>一行摘要（日志/报告用）。</summary>
        public string Describe()
        {
            return Name + "(kind=" + Kind.ToString(CultureInfo.InvariantCulture)
                   + ", rgb=(" + ColorR.ToString("R", CultureInfo.InvariantCulture)
                   + ", " + ColorG.ToString("R", CultureInfo.InvariantCulture)
                   + ", " + ColorB.ToString("R", CultureInfo.InvariantCulture) + ")"
                   + ", intensity=" + Intensity.ToString("R", CultureInfo.InvariantCulture)
                   + ", pos=(" + LocalPositionX.ToString("R", CultureInfo.InvariantCulture)
                   + ", " + LocalPositionY.ToString("R", CultureInfo.InvariantCulture)
                   + ", " + LocalPositionZ.ToString("R", CultureInfo.InvariantCulture) + "))";
        }
    }

    /// <summary>
    /// T-PLAY3 的**冻结光照契约**（纯数据 + 纯校验，零 UnityEngine 依赖）。
    ///
    /// 唯一权威来源：源场景 Client/Assets/Scenes/HYLDGame.unity 的 HYLDGameTatal/MAP/lights。
    /// 本类只描述"源场景那三盏灯是什么"，**不**描述"渲染出来长什么样"（见文件头的诚实边界）。
    /// </summary>
    public static class PMBattleLightingSource
    {
        /// <summary>源场景相对路径（仅供报告/诊断引用，本类不读文件）。</summary>
        public const string SourceSceneRelativePath = "Client/Assets/Scenes/HYLDGame.unity";

        /// <summary>源层级路径（父链两级的 TRS 都是单位值，故其 local TRS 即世界 TRS）。</summary>
        public const string SourceHierarchyPath = "HYLDGameTatal/MAP/lights";

        /// <summary>本局创建的容器对象名（独立根节点；**不是**地图 prefab 根的子节点）。</summary>
        public const string ContainerObjectName = "[PMNetBattleLights]";

        /// <summary>容器世界 X（= 源 lights Transform 的 m_LocalPosition.x，父链为单位 TRS）。</summary>
        public const float ContainerPositionX = 0f;

        /// <summary>容器世界 Y（= 源 lights Transform 的 m_LocalPosition.y = -8.1）。</summary>
        public const float ContainerPositionY = -8.1f;

        /// <summary>容器世界 Z（= 源 lights Transform 的 m_LocalPosition.z）。</summary>
        public const float ContainerPositionZ = 0f;

        /// <summary>容器旋转四元数 X（源 lights 的 m_LocalRotation.x）。</summary>
        public const float ContainerRotationX = 0f;

        /// <summary>容器旋转四元数 Y（源 lights 的 m_LocalRotation.y）。</summary>
        public const float ContainerRotationY = 0f;

        /// <summary>容器旋转四元数 Z（源 lights 的 m_LocalRotation.z）。</summary>
        public const float ContainerRotationZ = 0f;

        /// <summary>容器旋转四元数 W（源 lights 的 m_LocalRotation.w = 1，单位旋转）。</summary>
        public const float ContainerRotationW = 1f;

        /// <summary>期望的灯数量（源 lights 恰好 3 盏：1 蓝方向光 + 2 青聚光）。</summary>
        public const int ExpectedLightCount = 3;

        /// <summary>弹射强度（源三盏灯 m_BounceIntensity 都是 1）。</summary>
        public const float BounceIntensity = 1f;

        /// <summary>
        /// 源三盏灯的**唯一**参数表（顺序 = 源 lights Transform 的 m_Children 顺序）。
        ///
        /// 列顺序（与 <see cref="PMBattleLightDefinition"/> 构造函数一致，**勿改**）：
        /// name, kind, r, g, b, intensity, range, spotAngle, innerSpotAngle,
        /// posX, posY, posZ, rotX, rotY, rotZ, rotW,
        /// shadowType, shadowStrength, shadowBias, shadowNormalBias, shadowNearPlane。
        /// </summary>
        // >>> PMLIGHT-DEFINITION-TABLE-BEGIN（T-PLAY3a 静态对照锚点：一条定义一行，机器可读，勿改格式）
        private static readonly PMBattleLightDefinition[] DefinitionsTable =
        {
            new PMBattleLightDefinition("Directional Light", 1, 0f, 0.2460041f, 1f, 3f, 10f, 30f, 21.80208f, 0f, 22.85f, 0f, -0.33057782f, 0.06202134f, -0.010192308f, -0.9416835f, 1, 1f, 0.05f, 0.4f, 0.2f),
            new PMBattleLightDefinition("Spot Light", 0, 0f, 0.9638109f, 1f, 10f, 17.867682f, 51.75067f, 3.960653f, 7.9f, 17.1f, -8.8f, 0.39040673f, -0.3226305f, 0.0016521374f, 0.8622583f, 1, 1f, 0.02f, 0.1f, 0.1f),
            new PMBattleLightDefinition("Spot Light", 0, 0f, 0.9638109f, 1f, 3f, 14.529544f, 77.368996f, 2.1699755f, 0.3f, 17.08f, -15.9f, 0.4257336f, -0.14970408f, 0.09832193f, 0.8869456f, 1, 0.76f, 0.02f, 0.1f, 1.6f),
        };
        // <<< PMLIGHT-DEFINITION-TABLE-END

        /// <summary>冻结定义表（只读视图；调用方不得改动数组内容）。</summary>
        public static PMBattleLightDefinition[] Definitions { get { return DefinitionsTable; } }

        /// <summary>
        /// 纯数据自检（不依赖 Unity）：条数、类型/阴影枚举、颜色与强度/范围/聚光角/阴影数值的合理区间、
        /// 四元数是否近似单位长度。任一条不合就返回 false + 可归因原因（运行期与静态门禁共用同一口径）。
        /// </summary>
        public static bool TryValidateDefinitions(out string error)
        {
            error = null;

            if (DefinitionsTable == null || DefinitionsTable.Length == 0)
            {
                error = "冻结光照定义表为空。";
                return false;
            }

            if (DefinitionsTable.Length != ExpectedLightCount)
            {
                error = "冻结光照定义条数 " + DefinitionsTable.Length.ToString(CultureInfo.InvariantCulture)
                        + " 与期望 " + ExpectedLightCount.ToString(CultureInfo.InvariantCulture) + " 不一致。";
                return false;
            }

            for (int i = 0; i < DefinitionsTable.Length; i++)
            {
                PMBattleLightDefinition d = DefinitionsTable[i];
                string site = "定义[" + i.ToString(CultureInfo.InvariantCulture) + "](" + (d.Name ?? "<null>") + ")";

                if (string.IsNullOrEmpty(d.Name))
                {
                    error = site + " 名称为空。";
                    return false;
                }

                if (d.Kind != PMBattleLightDefinition.KindSpot && d.Kind != PMBattleLightDefinition.KindDirectional)
                {
                    error = site + " 灯类型 kind=" + d.Kind.ToString(CultureInfo.InvariantCulture)
                            + " 不是 Spot(0)/Directional(1)。";
                    return false;
                }

                if (d.ShadowType != PMBattleLightDefinition.ShadowNone
                    && d.ShadowType != PMBattleLightDefinition.ShadowHard
                    && d.ShadowType != PMBattleLightDefinition.ShadowSoft)
                {
                    error = site + " 阴影类型 shadowType=" + d.ShadowType.ToString(CultureInfo.InvariantCulture)
                            + " 不是 None(0)/Hard(1)/Soft(2)。";
                    return false;
                }

                if (!IsUnitRange(d.ColorR) || !IsUnitRange(d.ColorG) || !IsUnitRange(d.ColorB))
                {
                    error = site + " 颜色分量不在 [0,1] 内。";
                    return false;
                }

                if (!IsFinite(d.Intensity) || d.Intensity <= 0f)
                {
                    error = site + " intensity 必须为正的有限值。";
                    return false;
                }

                if (!IsFinite(d.Range) || d.Range <= 0f)
                {
                    error = site + " range 必须为正的有限值。";
                    return false;
                }

                if (!IsFinite(d.SpotAngle) || d.SpotAngle <= 0f || d.SpotAngle > 179f)
                {
                    error = site + " spotAngle 必须在 (0,179]。";
                    return false;
                }

                if (!IsFinite(d.InnerSpotAngle) || d.InnerSpotAngle < 0f || d.InnerSpotAngle > d.SpotAngle)
                {
                    error = site + " innerSpotAngle 必须在 [0, spotAngle]。";
                    return false;
                }

                if (!IsFinite(d.ShadowStrength) || d.ShadowStrength < 0f || d.ShadowStrength > 1f)
                {
                    error = site + " shadowStrength 必须在 [0,1]。";
                    return false;
                }

                if (!IsFinite(d.ShadowBias) || !IsFinite(d.ShadowNormalBias) || !IsFinite(d.ShadowNearPlane))
                {
                    error = site + " 阴影 bias/normalBias/nearPlane 必须是有限值。";
                    return false;
                }

                if (d.ShadowNearPlane < 0f || d.ShadowNearPlane >= d.Range)
                {
                    error = site + " shadowNearPlane 必须落在 [0, range) 内。";
                    return false;
                }

                if (!IsFinite(d.LocalPositionX) || !IsFinite(d.LocalPositionY) || !IsFinite(d.LocalPositionZ))
                {
                    error = site + " localPosition 必须是有限值。";
                    return false;
                }

                double quaternionLengthSquared =
                    (double)d.LocalRotationX * d.LocalRotationX + (double)d.LocalRotationY * d.LocalRotationY
                    + (double)d.LocalRotationZ * d.LocalRotationZ + (double)d.LocalRotationW * d.LocalRotationW;

                if (quaternionLengthSquared < 0.999 || quaternionLengthSquared > 1.001)
                {
                    error = site + " 旋转四元数不是单位长度（|q|^2="
                            + quaternionLengthSquared.ToString("R", CultureInfo.InvariantCulture) + "）。";
                    return false;
                }

                if (d.Kind == PMBattleLightDefinition.KindDirectional
                    && d.LocalRotationX == 0f && d.LocalRotationY == 0f
                    && d.LocalRotationZ == 0f && d.LocalRotationW == 1f)
                {
                    error = site + " 方向光的旋转是单位值（方向朝 +Z）：源场景不是这样，疑似抄漏。";
                    return false;
                }
            }

            return true;
        }

        /// <summary>一行摘要（日志/报告用；含来源路径与容器口径）。</summary>
        public static string Describe()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("PMBattleLightingSource(source=").Append(SourceSceneRelativePath)
                   .Append("#").Append(SourceHierarchyPath)
                   .Append(", container=").Append(ContainerObjectName)
                   .Append(", containerWorldPos=(")
                   .Append(ContainerPositionX.ToString("R", CultureInfo.InvariantCulture)).Append(", ")
                   .Append(ContainerPositionY.ToString("R", CultureInfo.InvariantCulture)).Append(", ")
                   .Append(ContainerPositionZ.ToString("R", CultureInfo.InvariantCulture)).Append("), lights=")
                   .Append(DefinitionsTable.Length.ToString(CultureInfo.InvariantCulture)).Append(")");
            return builder.ToString();
        }

        private static bool IsUnitRange(float value)
        {
            return IsFinite(value) && value >= 0f && value <= 1f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

#if UNITY_2019_1_OR_NEWER || UNITY_EDITOR
    /// <summary>
    /// T-PLAY3 的**客户端专有光源容器**：在给定（本局隔离）场景里建一个独立根节点 + 3 盏子灯，
    /// 参数逐位取自 <see cref="PMBattleLightingSource"/>，并提供幂等的精确释放。
    ///
    /// 生命周期：<see cref="TryCreate"/> → 使用（只读诊断）→ <see cref="Dispose"/>（幂等）。
    /// 归属：只由 PMUnityBattleMap.TryLoad（正式内容、非 DS）创建并由它的 Dispose 释放；
    /// 本类**不**自我注册任何全局钩子，也没有静态可变状态。
    /// </summary>
    public sealed class PMUnityBattleLighting : IDisposable
    {
        private static readonly Light[] EmptyLights = new Light[0];

        private readonly Scene _scene;
        private readonly GameObject _container;
        private readonly Light[] _lights;

        private bool _disposed;

        private PMUnityBattleLighting(Scene scene, GameObject container, Light[] lights)
        {
            _scene = scene;
            _container = container;
            _lights = lights;
        }

        /// <summary>光源容器所在的场景；已释放时为 default(Scene)（IsValid()==false）。</summary>
        public Scene Scene { get { return _disposed ? default(Scene) : _scene; } }

        /// <summary>光源容器根节点；已释放时为 null。</summary>
        public GameObject Container { get { return _disposed ? null : _container; } }

        /// <summary>创建的灯数量（纯计数，释放后仍可读，便于宿主/测试记账）。</summary>
        public int LightCount { get { return _lights.Length; } }

        /// <summary>灯组件列表（只读用途）；已释放时为零长度数组。</summary>
        public Light[] Lights { get { return _disposed ? EmptyLights : _lights; } }

        /// <summary>是否已释放。</summary>
        public bool Disposed { get { return _disposed; } }

        /// <summary>
        /// 在 <paramref name="scene"/> 里创建客户端专有光源容器。
        ///
        /// 失败原因（都不留残留：失败路径内部已销毁半成品容器）：
        ///   · 身份是 DedicatedServer（**第一句判定 ⇒ DS 零灯**）；
        ///   · 场景无效/未加载；
        ///   · 冻结定义自检失败；
        ///   · 容器没进目标场景（scene mismatch）；
        ///   · Unity 对象创建/属性写入抛异常。
        /// </summary>
        public static bool TryCreate(Scene scene, out PMUnityBattleLighting lighting, out string error)
        {
            lighting = null;
            error = null;

            // ---- 身份门（T-PLAY3a「DS 零灯」）：先判身份，再碰任何 Unity 对象。
            if (PMNet.PMNetRuntime.IsDedicatedServer)
            {
                error = "本进程是 DedicatedServer：DS 不创建任何客户端专有光源（T-PLAY3 的 DS 零灯口径）。";
                return false;
            }

            if (!scene.IsValid() || !scene.isLoaded)
            {
                error = "目标场景无效（IsValid=" + scene.IsValid().ToString()
                        + ", isLoaded=" + scene.isLoaded.ToString()
                        + "）：拒绝把客户端光源建到非本局场景。";
                return false;
            }

            string definitionError;
            if (!PMBattleLightingSource.TryValidateDefinitions(out definitionError))
            {
                error = "冻结光照定义自检失败：" + definitionError;
                return false;
            }

            GameObject container = null;

            try
            {
                container = new GameObject(PMBattleLightingSource.ContainerObjectName);
                container.transform.position = new Vector3(PMBattleLightingSource.ContainerPositionX,
                                                          PMBattleLightingSource.ContainerPositionY,
                                                          PMBattleLightingSource.ContainerPositionZ);
                container.transform.rotation = new Quaternion(PMBattleLightingSource.ContainerRotationX,
                                                              PMBattleLightingSource.ContainerRotationY,
                                                              PMBattleLightingSource.ContainerRotationZ,
                                                              PMBattleLightingSource.ContainerRotationW);
                container.transform.localScale = Vector3.one;

                // 与 PMUnityBattleMap 同一纪律：new GameObject 落在**活动场景**，必须显式迁入本局隔离场景
                // 并校验真的进去了（绝不"以为隔离了"）。
                SceneManager.MoveGameObjectToScene(container, scene);

                if (!container.scene.IsValid() || container.scene.handle != scene.handle)
                {
                    error = "光源容器没有进入本局隔离场景（scene mismatch）：拒绝在别的场景留下光源。";
                    DestroyObject(container);
                    return false;
                }

                PMBattleLightDefinition[] definitions = PMBattleLightingSource.Definitions;
                Light[] lights = new Light[definitions.Length];

                for (int i = 0; i < definitions.Length; i++)
                {
                    PMBattleLightDefinition definition = definitions[i];

                    GameObject lightObject = new GameObject(definition.Name);
                    lightObject.transform.parent = container.transform;
                    lightObject.transform.localPosition = new Vector3(definition.LocalPositionX,
                                                                     definition.LocalPositionY,
                                                                     definition.LocalPositionZ);
                    lightObject.transform.localRotation = new Quaternion(definition.LocalRotationX,
                                                                        definition.LocalRotationY,
                                                                        definition.LocalRotationZ,
                                                                        definition.LocalRotationW);
                    lightObject.transform.localScale = Vector3.one;

                    Light light = lightObject.AddComponent<Light>();
                    light.type = (definition.Kind == PMBattleLightDefinition.KindDirectional)
                                     ? LightType.Directional
                                     : LightType.Spot;
                    light.color = new Color(definition.ColorR, definition.ColorG, definition.ColorB, 1f);
                    light.intensity = definition.Intensity;
                    light.range = definition.Range;
                    light.spotAngle = definition.SpotAngle;
                    light.innerSpotAngle = definition.InnerSpotAngle;
                    light.shadows = (definition.ShadowType == PMBattleLightDefinition.ShadowSoft)
                                        ? LightShadows.Soft
                                        : ((definition.ShadowType == PMBattleLightDefinition.ShadowHard)
                                               ? LightShadows.Hard
                                               : LightShadows.None);
                    light.shadowStrength = definition.ShadowStrength;
                    light.shadowBias = definition.ShadowBias;
                    light.shadowNormalBias = definition.ShadowNormalBias;
                    light.shadowNearPlane = definition.ShadowNearPlane;
                    light.cullingMask = ~0;                                         // 源 m_CullingMask.m_Bits = 4294967295
                    light.renderMode = LightRenderMode.Auto;                        // 源 m_RenderMode = 0

                    // 源 m_Lightmapping = 4（Realtime）。
                    //
                    // **必须**留在 UNITY_EDITOR 内：`Light.lightmapBakeType` 只存在于编辑器变体的
                    // UnityEngine 程序集，Player 变体里没有这个成员 —— 无条件赋值会让 Build Player
                    // 以 CS1061 失败（用户 14:49 的真实故障，Editor 播放却完全正常）。
                    // Player 下不设置它是**等价语义**：运行时新建的灯本来就是实时灯，
                    // 打包产物里也没有可参与烘焙的场景光照数据。
#if UNITY_EDITOR
                    light.lightmapBakeType = LightmapBakeType.Realtime;
#endif

                    light.bounceIntensity = PMBattleLightingSource.BounceIntensity; // 源 m_BounceIntensity = 1
                    light.useColorTemperature = false;                              // 源 m_UseColorTemperature = 0

                    lights[i] = light;
                }

                lighting = new PMUnityBattleLighting(scene, container, lights);
                return true;
            }
            catch (Exception ex)
            {
                if (container != null)
                {
                    DestroyObject(container);
                }

                lighting = null;
                error = "创建客户端专有光源失败：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 幂等释放：销毁整个容器（3 盏子灯随之销毁）。
        ///
        /// 释放后 <see cref="Container"/> = null、<see cref="Lights"/> 为空数组、<see cref="Scene"/> 为 default；
        /// <see cref="LightCount"/> 仍返回创建时的计数（纯数字，便于宿主记账）。
        /// 本类不切活动场景、不动 RenderSettings、不影响其它对象。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            GameObject container = _container;
            if (container != null)
            {
                DestroyObject(container);
            }
        }

        /// <summary>一行摘要（日志/报告用）。</summary>
        public string Describe()
        {
            if (_disposed)
            {
                return "PMUnityBattleLighting(disposed, lights="
                       + _lights.Length.ToString(CultureInfo.InvariantCulture) + ")";
            }

            return "PMUnityBattleLighting(container=\"" + _container.name + "\""
                   + ", scene=\"" + _scene.name + "\""
                   + ", lights=" + _lights.Length.ToString(CultureInfo.InvariantCulture)
                   + ", " + PMBattleLightingSource.Describe() + ")";
        }

        /// <summary>
        /// 与 PMUnityBattleMap.DestroyObject 同一语义的运行时销毁：本文件同时进 DS 与客户端构建，
        /// 不允许引用 UnityEditor。
        /// </summary>
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
    }
#endif
}
