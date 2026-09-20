// ============================================================================
//  PMBattleContentBuildCheck / HostDependencies.cs —— ScenseBuildLogic 的**字段边界替身**
// ============================================================================
//
//  为什么需要这个文件：
//    R4-C / C1 的构建脚本（Client/Assets/Editor/PMBattleContentBuild.cs）必须读取源场景里
//    `ScenseBuildLogic` 的**序列化字段**（mapx/mapy/模板数组/map2 表）。
//    而真实类（Client/Assets/HYLD1.0/Scripts/OldScripts/ScenseBuildLogic.cs）位于旧链里，
//    它 `using LongZhiJie;`、`using UnityEngine.UI;`，并引用 HYLDStaticValue ——
//    把真实类编进本门禁等于把整套旧玩法拖进来，而门禁要证明的是
//    「C1 的编辑期烘焙代码能在**真实 Unity 2019.4 API** 上编译」。
//
//  边界纪律（本文件必须守住的三条）：
//    1) **只替旧链边界，不替 Unity API**：UnityEngine/UnityEditor 引用的是
//       D:/Unity/2019.4.8f1/Editor/Data/Managed 下的**真实** DLL（见 csproj），
//       本文件只提供 ScenseBuildLogic 这一个类型的替身。
//    2) **签名与真实字段逐字一致**：类型名（全局命名空间）、基类（MonoBehaviour）、
//       字段名与类型（int / GameObject[] / Transform / int[,]）必须与真实类相同，
//       否则门禁会给假绿灯（Unity 里编译不过，或读错字段）。
//    3) **刻意不提供真实类里那些"不该被 C1 使用"的成员**：
//        · 没有 `InitData()` —— 契约明令 C1 不得调用它（依赖全局 Random 与旧玩法数据）。
//          如果构建脚本哪天写了 `logic.InitData()`，本门禁会**编译失败**，
//          这正是我们要的结构性保证；
//        · 没有 `maps` / `mapDictionary` / `current_mode` / `InitFinish` ——
//          运行期由 InitData 决定的东西一律不进边界：C1 只允许读 `map2`（契约冻结首图）。
//
//  唯一被构建脚本使用的成员（其余字段列出来是为了"字段名/类型"可核对）：
//    mapx, mapy, map2, floors, walls, obstacles, Grasses, trees
// ============================================================================

using UnityEngine;

/// <summary>
/// <c>Client/Assets/HYLD1.0/Scripts/OldScripts/ScenseBuildLogic.cs</c> 的**门禁替身**
/// （只保留 C1 烘焙脚本读取的序列化字段 + 契约要求的模板/容器字段）。
///
/// 真实类 = MonoBehaviour + mapx/mapy + floors/walls/obstacles/Grasses/trees +
/// RedSaveBox/BlueSaveBox + MAP + maps/map1..map4 + mapDictionary + current_mode + InitData()。
/// 本替身**不含** InitData / maps / mapDictionary，理由见文件头第 3 条。
/// </summary>
public class ScenseBuildLogic : MonoBehaviour
{
    /// <summary>网格列数（场景序列化值 = 33；契约要求维持真实循环范围，不扩到表的 35 行）。</summary>
    public int mapx = 33;

    /// <summary>网格行数（场景序列化值 = 21）。</summary>
    public int mapy = 21;

    /// <summary>地板模板调色板（场景内联对象，4 项）。</summary>
    public GameObject[] floors;

    /// <summary>边界墙模板调色板（4 项）。</summary>
    public GameObject[] walls;

    /// <summary>障碍模板调色板（8 项）。</summary>
    public GameObject[] obstacles;

    /// <summary>草丛模板调色板（1 项；源码里实例化被注释，但随机数仍被消费）。</summary>
    public GameObject[] Grasses;

    /// <summary>外围树模板调色板（7 项）。</summary>
    public GameObject[] trees;

    /// <summary>红方金库（外部 prefab；首图 map2 不含金库格）。</summary>
    public GameObject RedSaveBox;

    /// <summary>蓝方金库（外部 prefab；首图 map2 不含金库格）。</summary>
    public GameObject BlueSaveBox;

    /// <summary>运行期生成物的父节点（源码 `MyInstantiate` 的 SetParent 目标）。</summary>
    public Transform MAP;

    /// <summary>
    /// 首图（map2）的格子表（35×21；C1 只读前 mapx×mapy 行/列）。
    ///
    /// 真实类里 `map2` 是**字段初始化器**（不是序列化数据），因此本替身同样只是一个字段。
    /// </summary>
    public int[,] map2;
}
