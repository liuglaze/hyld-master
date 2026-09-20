// ============================================================================
//  BattleFloatMath —— 客户端侧的 Vector3 适配层（P3'-3 起）
// ============================================================================
//
//  历史：这个类原本是**公式本体**（客户端自己实现的一份移动/朝向换算）。
//  服务端另有一份内联的同义实现（BattleController.Network.cs / Bullets.cs）。
//  两份实现只要漂移一处，客户端预测就会与服务端权威持续对不上。
//
//  P3'-3 把公式收进了 `PMNet.Shared.PMBattleSim`（零依赖，两侧共用）。
//  本类随之降级为**薄适配层**：只负责在 `Vector3` 与标量之间转换，
//  自己不再含任何数学。这样：
//    · 保持原有 8 处调用点的签名不变（改造面最小）；
//    · 公式只有一个事实源，客户端与服务端在构造上就不可能分叉。
//
//  ⚠ 一处**有意**的行为变更
//  ------------------------
//  旧 `ToWorldDirection` 走的是 `UnityEngine.Vector3.normalized`，其内部判零阈值
//  是 Unity 的 `kEpsilon = 1e-5`；而服务端（以及本类旧 `ToMoveDirection`）用的是 `1e-6`。
//  合并后统一到**服务端的 1e-6**（权威口径）。
//  差异只可能出现在「输入模长落在 (1e-6, 1e-5]」这个薄片里，而该区间被
//  `TouchLogic` 的摇杆死区（0.02 / 0.12）完全屏蔽 —— 实战不可达。
//  `Tools/PMBattleSimTest` 对这个薄片做了显式计数，不是悄悄放过。
//
//  另：旧 `ToMoveDirection` 返回零向量时，`AdvancePosition` 靠
//  `Vector3.operator==`（近似比较）来判断"方向无效"；现在直接用核心返回的 bool，
//  语义更明确。`AdvancePosition` 本身在客户端**零调用点**（活代码走
//  `HYLDPlayerManger` 与 `BattleData.Prediction` 里的内联积分），保留是为了不动公开 API。

using UnityEngine;
using PMNet.Shared;

public static class BattleFloatMath
{
    /// <summary>
    /// 攻击朝向 → 世界方向。数学在 <see cref="PMBattleSim.TryGetAimDirection"/>。
    /// </summary>
    public static Vector3 ToWorldDirection(float towardX, float towardY, int sign)
    {
        float dirX, dirZ;
        if (!PMBattleSim.TryGetAimDirection(towardX, towardY, sign, out dirX, out dirZ))
        {
            return Vector3.zero;
        }
        return new Vector3(dirX, 0f, dirZ);
    }

    /// <summary>
    /// 移动输入 → 归一化世界方向。数学在 <see cref="PMBattleSim.TryGetMoveDirection"/>。
    /// </summary>
    public static Vector3 ToMoveDirection(float moveX, float moveY, int sign)
    {
        float dirX, dirZ;
        if (!PMBattleSim.TryGetMoveDirection(moveX, moveY, sign, out dirX, out dirZ))
        {
            return Vector3.zero;
        }
        return new Vector3(dirX, 0f, dirZ);
    }

    /// <summary>
    /// 单帧位置积分（Y 不变）。数学在 <see cref="PMBattleSim.TryAdvancePosition"/>。
    ///
    /// <para>
    /// ⚠ 服务端与客户端的**多帧**积分请直接用 <see cref="PMBattleSim.TryAdvancePosition"/>
    /// 并传 frameCount —— 不要循环调本方法，那样会把「一次推进 N 帧」的浮点结果
    /// 与「推进 N 次一帧」的结果分开（两者不等价）。
    /// </para>
    /// </summary>
    public static Vector3 AdvancePosition(Vector3 startPos, float moveX, float moveY,
        int sign, float speed, float frameTime)
    {
        float outX, outZ;
        if (!PMBattleSim.TryAdvancePosition(startPos.x, startPos.z, moveX, moveY, sign,
                speed, frameTime, 1, out outX, out outZ))
        {
            return startPos;
        }
        return new Vector3(outX, startPos.y, outZ);
    }
}
