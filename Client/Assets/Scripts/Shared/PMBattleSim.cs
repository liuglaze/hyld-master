// ============================================================================
//  PMBattleSim —— 战斗仿真的**唯一数学事实源**（P3'-3）
// ============================================================================
//
//  为什么会有这个文件
//  ------------------
//  P3'-2 消除了「数值」的两份副本（服务端 HeroConfig vs 客户端英雄表）。
//  但**公式**当时仍是两份：
//
//    · 服务端 `BattleController.Network.cs:801 SimulateAuthoritativeMove`
//    · 服务端 `BattleController.Network.cs:829 CalculateAuthoritativeVelocity`
//    · 服务端 `BattleController.Bullets.cs:112-143 SpawnServerBullets`（方向 + 扇形）
//    · 服务端 `BattleController.Bullets.cs:197-199 CheckBulletCollision`（命中）
//    · 服务端 `Battle.cs:732 UpdatePlayerPositions`（死代码里的第三份）
//    · 客户端 `Math/BattleFloatMath.cs`（Vector3 版）
//
//  两份独立实现只要有一处漂移，客户端预测与服务端权威就会持续对不上，
//  表现是 MoveAck 每帧回校正（角色被「拉回」）。这类漂移**编译期看不出来**，
//  历史上已经踩过（`Client/Assets/AGENTS.md` §9 的「服务端子弹方向镜像」）。
//
//  做法：把这批公式收敛到本文件，两侧都调它。
//
//  设计约束
//  --------
//  1) **零依赖**。不引用 `UnityEngine`、不引用 `Google.Protobuf`、不引用任何项目类型。
//     服务端用 `<Compile Include>` 链接同一份源码，Unity 客户端也编它。
//     `Tools/PMSharedConfigCheck`（netstandard2.0 + C# 7.3）会强制这一点。
//  2) **只收标量，不收向量类型**。服务端用 `ServerVector3`、客户端用 `UnityEngine.Vector3`，
//     两者都不是本文件能引用的。传 float 分量进去、传 float 分量出来，
//     调用方各自组装自己的向量类型 —— 这样才可能「零依赖」。
//  3) **逐位复刻服务端现有公式**。服务端是在线权威，改它的数值必须是可证明的零变化。
//     浮点结合顺序（`A*B*C` 先算哪一步）都会影响最后一位，
//     所以本文件里的表达式顺序是从服务端源码**原样搬**过来的，没有做「等价整理」。
//     `Tools/PMBattleSimTest` 用差分测试逐位比对，证明这次抽取没改行为。
//
//  关于 `sign`（队伍镜像）为什么是参数而不是算出来的
//  ----------------------------------------------
//  两端的坐标系**锚点不同**，且都是对的：
//    · 服务端：`baseTeamId = min(teamIds)` 在 X=+15，`teamSign = (tid != baseTeamId) ? -1 : 1`
//    · 客户端：把**本地玩家**放在 X=+15（自锚定），`sign = (tid != selfTeam) ? -1 : 1`
//  两者相差一个「全局镜像」；客户端在**上传**预测位置时用 `GetClientToServerSign()`
//  做这个镜像转换（`BattleData.Prediction.cs:245`）。也就是说
//  「客户端自锚定 + 上传时镜像」与「服务端基锚定」是等价的两种写法。
//
//  因此本文件**不负责决定锚点**，只接收调用方算好的 `sign`/`teamSign`。
//  强行统一锚点会破坏客户端的自锚定坐标系（它到处依赖「自己在 +15」）。
//
//  精度说明
//  --------
//  两端都是 float32 全精度、无量化、无定点数。
//  `BattleFloatMath.cs` 里的类名 `Float` 指的是「用 float 做数学」，不是「浮点→定点」。
//  所以这里也一律 float32，不做任何量化补偿 —— 行为等价优先于「看起来更精确」。

using System;

namespace PMNet.Shared
{
    /// <summary>
    /// 战斗仿真的纯数学核心。所有方法都是静态、无状态、无副作用的；
    /// 输入输出全部是 <see cref="float"/>/<see cref="int"/> 标量，
    /// 以便服务端（<c>ServerVector3</c>）与客户端（<c>UnityEngine.Vector3</c>）共用。
    /// </summary>
    public static class PMBattleSim
    {
        /// <summary>
        /// 输入/方向判零阈值。
        /// 服务端原来在多处写 `1e-6f`（`Network.cs:810`、`Network.cs:591`、
        /// `Battle.cs:709`、`ServerVector3.Normalized`），这里收敛成一个名字。
        /// </summary>
        public const float ZeroEpsilon = 1e-6f;

        /// <summary>角度转弧度的乘数。与服务端 `(float)(Math.PI / 180.0)` 逐位一致。</summary>
        private const float DegToRad = (float)(Math.PI / 180.0);

        // ====================================================================
        //  移动
        // ====================================================================

        /// <summary>
        /// 移动输入 → 归一化世界方向（XZ 平面，Y 恒 0）。
        ///
        /// <para>
        /// **输入被归一化**：摇杆的模拟量大小在这一步被丢弃，只保留方向。
        /// 所以「轻轻推」与「推到底」速度相同 —— 这是既有行为，不是缺陷。
        /// </para>
        ///
        /// <para>
        /// 服务端出处：<c>BattleController.Network.cs:807-819</c>；
        /// 客户端出处：<c>Math/BattleFloatMath.cs:15 ToMoveDirection</c>。
        /// </para>
        /// </summary>
        /// <param name="moveX">客户端上报的横轴输入（未归一化）。</param>
        /// <param name="moveY">客户端上报的纵轴输入（未归一化；对应世界 Z 轴）。</param>
        /// <param name="sign">队伍镜像符号（+1 或 -1），由调用方按自己的坐标系算好。</param>
        /// <param name="dirX">归一化后的世界 X 方向分量。</param>
        /// <param name="dirZ">归一化后的世界 Z 方向分量。</param>
        /// <returns>输入有效返回 true；零输入（长度 ≤ <see cref="ZeroEpsilon"/>）返回 false，且方向输出为 0。</returns>
        public static bool TryGetMoveDirection(float moveX, float moveY, float sign,
            out float dirX, out float dirZ)
        {
            // 归一化长度用 (moveX, moveY) 本身算，不含 sign —— sign² == 1，不影响长度。
            float len = (float)Math.Sqrt(moveX * moveX + moveY * moveY);
            if (len <= ZeroEpsilon)
            {
                dirX = 0f;
                dirZ = 0f;
                return false;
            }

            float mx = moveX / len;
            float mz = moveY / len;

            // 世界 X 取反（俯视角摇杆轴与世界轴互换），再加队伍镜像。
            dirX = -mx * sign;
            dirZ = mz * sign;
            return true;
        }

        /// <summary>
        /// 位置积分：<c>pos += 归一化方向 * 移速 * 每帧秒数 * 帧数</c>。
        ///
        /// <para>
        /// 服务端出处：<c>BattleController.Network.cs:800-826 SimulateAuthoritativeMove</c>
        /// （这是 <c>playerPositions</c> 的唯一写入路径）。
        /// </para>
        ///
        /// <para>
        /// **模型极简，且这是刻意的**：没有重力、没有摩擦、没有加速度、没有碰撞、没有寻路；
        /// Y 轴永不变化（出生时固定 1.0）；输入为零就完全不动（无惯性滑行）。
        /// 客户端预测必须用同一个模型，否则每帧都会对不上。
        /// </para>
        /// </summary>
        /// <param name="frameCount">本次要推进的逻辑帧数。服务端按客户端 move 的帧差一次推进多帧；客户端预测恒为 1。</param>
        /// <returns>真正发生了位移返回 true；<paramref name="frameCount"/> ≤ 0 或零输入返回 false（位置原样输出）。</returns>
        public static bool TryAdvancePosition(float posX, float posZ, float moveX, float moveY,
            float sign, float moveSpeed, float frameTimeSec, int frameCount,
            out float outX, out float outZ)
        {
            if (frameCount <= 0)
            {
                outX = posX;
                outZ = posZ;
                return false;
            }

            float dirX, dirZ;
            if (!TryGetMoveDirection(moveX, moveY, sign, out dirX, out dirZ))
            {
                outX = posX;
                outZ = posZ;
                return false;
            }

            // 注意结合顺序：服务端是 (moveSpeed * frameTimeSec) * frameCount。
            // 写成 moveSpeed * (frameTimeSec * frameCount) 会在最后一位产生差异。
            float distance = moveSpeed * frameTimeSec * frameCount;

            outX = posX + dirX * distance;
            outZ = posZ + dirZ * distance;
            return true;
        }

        /// <summary>
        /// 权威速度（units/sec），用于 <c>MoveAck</c> 回带给客户端做校正后的速度参考。
        ///
        /// <para>服务端出处：<c>BattleController.Network.cs:829 CalculateAuthoritativeVelocity</c>。</para>
        /// </summary>
        /// <returns>零输入返回 false，速度输出为 0。</returns>
        public static bool TryGetVelocity(float moveX, float moveY, float sign, float moveSpeed,
            out float velX, out float velZ)
        {
            float dirX, dirZ;
            if (!TryGetMoveDirection(moveX, moveY, sign, out dirX, out dirZ))
            {
                velX = 0f;
                velZ = 0f;
                return false;
            }

            velX = dirX * moveSpeed;
            velZ = dirZ * moveSpeed;
            return true;
        }

        // ====================================================================
        //  攻击朝向 / 子弹
        // ====================================================================

        /// <summary>
        /// 攻击朝向 → 归一化世界方向。含「proto 轴互换」与「队伍镜像」两步。
        ///
        /// <para>
        /// proto 字段语义是俯视角下的**摇杆轴**，不是世界轴：
        /// <c>TowardY → 世界 X</c>、<c>TowardX → 世界 Z</c>。
        /// 客户端消费侧写作
        /// <c>dir = xAndY2UnitVector3(Towardy, Towardx); dir.x *= -1*sign; dir.z *= sign;</c>，
        /// 本方法复刻的正是这条链（先换轴、再取反、再镜像，最后归一化）。
        /// </para>
        ///
        /// <para>服务端出处：<c>BattleController.Bullets.cs:112-116</c>。</para>
        /// </summary>
        /// <returns>方向有效返回 true；退化方向返回 false，输出为 0（调用方据此跳过生成子弹）。</returns>
        public static bool TryGetAimDirection(float towardX, float towardY, float sign,
            out float dirX, out float dirZ)
        {
            // 换轴 + 取反 + 镜像：世界 X 取负（对应客户端 dir.x *= -1），两轴都乘队伍镜像。
            float baseX = -towardY * sign;
            float baseZ = towardX * sign;

            // 逐位等价于 ServerVector3.Normalized()：y 分量为 0，所以范数里只加 x²+z²。
            float len = (float)Math.Sqrt(baseX * baseX + baseZ * baseZ);
            if (len <= ZeroEpsilon)
            {
                dirX = 0f;
                dirZ = 0f;
                return false;
            }

            dirX = baseX / len;
            dirZ = baseZ / len;
            return true;
        }

        /// <summary>
        /// 散弹扇形：把基准方向绕 Y 轴均匀展开 <paramref name="bulletCount"/> 份。
        ///
        /// <para>
        /// 服务端出处：<c>BattleController.Bullets.cs:129-145</c>。
        /// 步长是 <c>total / (count - 1)</c>，两端取 <c>±total/2</c>（**首尾包含**）。
        /// </para>
        ///
        /// <para>
        /// ⚠ 客户端表现层**故意不用**这个公式：`HYLDBulletManger.SpawnFanPattern`
        /// 用的是 <c>total / count</c> 步长外加 <c>Random.Range(0.8f, 1.2f)</c> 的抖动，
        /// 目的是让弹幕看起来自然。那是表现层编排（S7 Q7(A) 第 4 条），
        /// **不属于**仿真合并面 —— 玩家的命中判定由服务端本方法的结果决定，
        /// 客户端画出来的扇形好看与否不影响结果。这里的单一源只服务于权威侧。
        /// </para>
        /// </summary>
        /// <param name="index">第几颗（0 基）。</param>
        /// <param name="totalAngleDeg">扇形总张角（度）。</param>
        public static void SpreadDirection(float baseDirX, float baseDirZ, float totalAngleDeg,
            int bulletCount, int index, out float dirX, out float dirZ)
        {
            // 单发（count ≤ 1）**不做任何旋转**。
            //
            // 服务端原本在这种情况下根本不进扇形分支 —— 它 `if (bulletCount <= 1)` 直接
            // 用已归一化的 baseDir 生成一颗弹（Bullets.cs:122-126），扇形代码只在
            // `else` 里。所以这里必须返回 baseDir 本身，否则单发会被白白转掉
            // `-totalAngle/2` 度（这正是本方法初版写出过的错误，被差分测试当场抓住）。
            //
            // 不重新归一化：调用方（服务端的 SpawnServerBullets）传进来的 baseDir
            // 已经过 Normalized()，再除一次范数会因为浮点误差而不再逐位相同。
            if (bulletCount <= 1)
            {
                dirX = baseDirX;
                dirZ = baseDirZ;
                return;
            }

            float step = totalAngleDeg / (bulletCount - 1);
            float startAngle = -totalAngleDeg / 2f;
            float angleDeg = startAngle + step * index;
            float angleRad = angleDeg * DegToRad;

            // 用 double 的 Cos/Sin 再转 float —— 与服务端 Math.Cos/Math.Sin 的调用形态一致。
            float cos = (float)Math.Cos(angleRad);
            float sin = (float)Math.Sin(angleRad);

            // 绕 Y 轴旋转 (baseDirX, baseDirZ)。
            float rotX = baseDirX * cos - baseDirZ * sin;
            float rotZ = baseDirX * sin + baseDirZ * cos;

            float len = (float)Math.Sqrt(rotX * rotX + rotZ * rotZ);
            if (len <= ZeroEpsilon)
            {
                dirX = 0f;
                dirZ = 0f;
                return;
            }

            dirX = rotX / len;
            dirZ = rotZ / len;
        }

        /// <summary>
        /// 子弹每帧推进的距离（单位）。服务端出处：<c>BattleController.Bullets.cs:300-301</c>。
        ///
        /// <para>
        /// 追帧路径（<c>:261-262</c>）与正常 tick 路径（<c>:299-300</c>）用的是**逐字相同**的公式，
        /// 这里收敛成一个调用点，避免两条路径今后漂移。
        /// </para>
        /// </summary>
        public static float BulletStepDistance(float bulletSpeed, float frameTimeSec)
        {
            return bulletSpeed * frameTimeSec;
        }

        /// <summary>子弹是否已飞满射程（超距即销毁，不产生命中事件）。</summary>
        public static bool IsBulletExpired(float traveledDistance, float maxDistance)
        {
            return traveledDistance >= maxDistance;
        }

        // ====================================================================
        //  命中 / 伤害
        // ====================================================================

        /// <summary>
        /// 命中判定：子弹（视为**一个点**）与目标的 3D 距离 ≤ 命中半径。
        ///
        /// <para>
        /// 服务端出处：<c>BattleController.Bullets.cs:197-199</c>。
        /// </para>
        ///
        /// <para>
        /// **没有**子弹半径与目标半径的合并，**没有**射线/包围盒/扫掠/空间划分 ——
        /// 是逐帧的球-点距离判定。每颗子弹每帧**至多命中一个目标**（命中即 return，
        /// 外层由调用方保证），命中即销毁。
        /// </para>
        ///
        /// <para>
        /// 用的是含 Y 的 3D 距离，但子弹 <c>dir.y == 0</c> 且玩家 Y 出生后恒为 1.0，
        /// 双方 Y 差恒为 0 → 实际等价于 XZ 平面的圆判定。保留 3D 形式是为了逐位等价。
        /// </para>
        ///
        /// <para>
        /// 半径取自**发射者**英雄的配置（<c>ServerHitRadius</c>），不是受击者 —— 这是既有行为。
        /// </para>
        /// </summary>
        public static bool IsHit(float bulletX, float bulletY, float bulletZ,
            float targetX, float targetY, float targetZ, float hitRadius)
        {
            float dx = bulletX - targetX;
            float dy = bulletY - targetY;
            float dz = bulletZ - targetZ;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return dist <= hitRadius;
        }

        /// <summary>
        /// 命中后给发射者回充的大招能量：<c>min(max, current + damage / 2)</c>。
        ///
        /// <para>
        /// 服务端出处：<c>BattleController.Bullets.cs:207-210</c>。
        /// <c>damage / 2</c> 是**整数除法（向下取整）**，不是浮点 —— 例如 45 伤害只回 22。
        /// </para>
        /// </summary>
        public static int RechargeSuperEnergy(int current, int damage, int max)
        {
            int gained = current + damage / 2;
            return gained < max ? gained : max;
        }
    }
}
