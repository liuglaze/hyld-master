// ============================================================================
//  PMBattleSimTest —— P3'-3 仿真数学抽取的**行为等价性**证明
// ============================================================================
//
//  这个测试在证明什么
//  ------------------
//  把服务端/客户端各自内联的仿真公式收敛到 `PMNet.Shared.PMBattleSim` 时，
//  最容易犯的错误不是编译不过，而是**公式被悄悄改了**。例如：
//    · 浮点结合顺序从 `(a*b)*c` 变成 `a*(b*c)` —— 结果差最后一位；
//    · 把 `moveSpeed * frameTimeSec * frameCount` 写成 `moveSpeed * (frameTimeSec * frameCount)`；
//    · 扇形步长从 `total/(count-1)` 写成 `total/count`；
//    · 归一化阈值从 `1e-6` 写成别的小数。
//  这类错误在**编译期完全不可见**，在运行期表现为「客户端预测与服务端权威缓慢对不上」，
//  排查成本极高（历史上已经踩过一次，见 Client/Assets/AGENTS.md §9 的子弹方向镜像）。
//
//  因此本测试：
//    1) 把**改造前的公式逐字复制**成 oracle（下方 OldServer / OldClient）；
//    2) 在大量随机输入 + 边界输入上，把 oracle 与 PMBattleSim 的输出**逐位**比对；
//    3) 逐位 = 比较 float 的 4 个字节，能区分 ±0.0 与 NaN，`==` 则不能。
//
//  oracle 的来源（逐条对应改造前的源码位置）
//  ------------------------------------------
//    · OldServer.SimulateAuthoritativeMove  ← BattleController.Network.cs:800-826
//    · OldServer.CalculateAuthoritativeVelocity ← BattleController.Network.cs:829-846
//    · OldServer.SpawnDirection / SpawnFanDirection ← BattleController.Bullets.cs:108-146
//    · OldServer.BulletStep ← BattleController.Bullets.cs:299-300（与追帧 :261-262 同式）
//    · OldServer.CheckHit ← BattleController.Bullets.cs:197-199
//    · OldServer.RechargeEnergy ← BattleController.Bullets.cs:205-210
//    · OldClient.ToMoveDirection / ToWorldDirection ← Math/BattleFloatMath.cs:7-20
//
//  已知的、**有意**的差异（单独断言，不混在「全等」里）
//  --------------------------------------------------
//  客户端 `ToWorldDirection` 走的是 `UnityEngine.Vector3.normalized`，
//  它内部的判零阈值是 Unity 的 `kEpsilon = 1e-5`，而服务端一路用的是 `1e-6`。
//  合并后统一用服务端的 `1e-6`（权威口径）。因此对「模长落在 (1e-6, 1e-5] 区间」的输入，
//  新旧结果不同（旧：零向量；新：单位向量）。这个区间在实战中不可达 ——
//  摇杆输入经 `TouchLogic` 死区（0.02 / 0.12）过滤，非零时模长远大于 1e-5。
//  测试把这个区间**显式计数**，而不是悄悄放过。
//
//  运行：dotnet Tools/PMBattleSimTest/bin/Release/net8.0/PMBattleSimTest.dll
//  退出码：0 = 全部等价；1 = 存在非预期差异

using System;
using System.Collections.Generic;
using PMNet.Shared;

namespace PMBattleSimTest
{
    // ========================================================================
    //  改造前的服务端公式（oracle）—— 逐字复制，不做任何「等价整理」
    // ========================================================================
    internal static class OldServer
    {
        internal struct V3
        {
            public float X, Y, Z;
            public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
        }

        // ServerVector3.Normalized()（Server/Server/ServerVector3.cs:20-24）
        internal static V3 Normalized(V3 v)
        {
            float m = (float)Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            if (m > 1e-6f) return new V3(v.X / m, v.Y / m, v.Z / m);
            return new V3(0f, 0f, 0f);
        }

        internal static float Magnitude(V3 v)
        {
            return (float)Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        }

        // ServerVector3.Distance(a,b)
        internal static float Distance(V3 a, V3 b)
        {
            float dx = a.X - b.X; float dy = a.Y - b.Y; float dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // BattleController.Network.cs:800-826
        internal static V3 SimulateAuthoritativeMove(V3 startPos, float moveX, float moveY,
            float teamSign, float moveSpeed, float frameTimeSec, int frameCount)
        {
            if (frameCount <= 0) return startPos;

            float len = (float)Math.Sqrt(moveX * moveX + moveY * moveY);
            if (len <= 1e-6f) return startPos;

            float mx = moveX / len;
            float mz = moveY / len;

            float distance = moveSpeed * frameTimeSec * frameCount;

            V3 result = startPos;
            result.X += -mx * teamSign * distance;
            result.Z += mz * teamSign * distance;
            return result;
        }

        // BattleController.Network.cs:829-846
        internal static V3 CalculateAuthoritativeVelocity(float moveX, float moveY,
            float teamSign, float moveSpeed)
        {
            float len = (float)Math.Sqrt(moveX * moveX + moveY * moveY);
            if (len <= 1e-6f) return new V3(0f, 0f, 0f);

            float mx = moveX / len;
            float mz = moveY / len;

            return new V3(-mx * teamSign * moveSpeed, 0f, mz * teamSign * moveSpeed);
        }

        // BattleController.Bullets.cs:108-116（基准方向）
        internal static V3 SpawnDirection(float towardX, float towardY, float teamSign)
        {
            float baseX = -towardY * teamSign;
            float baseZ = towardX * teamSign;
            return Normalized(new V3(baseX, 0f, baseZ));
        }

        // BattleController.Bullets.cs:121-146 —— 注意要复刻**分支结构**，不只是公式。
        //
        // 服务端对 bulletCount <= 1 根本不进扇形代码：它在那之前就
        // `bullets.Add(CreateServerBullet(..., baseDir, ...))` 直接用了已归一化的 baseDir。
        // 初版 oracle 只搬了扇形公式，于是对 count=1 凭空造出一个「被转了 -total/2」的结果，
        // 反过来诬陷了正确的实现 —— 这是 oracle 保真度问题，不是实现问题。
        internal static V3 SpawnBulletDirection(V3 baseDir, float totalAngle,
            int bulletCount, int index)
        {
            // 单发分支：方向就是 baseDir 本身，不旋转、也不重新归一化
            if (bulletCount <= 1) return baseDir;

            // 扇形分支
            float step = totalAngle / (bulletCount - 1);
            float startAngle = -totalAngle / 2f;
            float angleDeg = startAngle + step * index;
            float angleRad = angleDeg * (float)(Math.PI / 180.0);
            float cos = (float)Math.Cos(angleRad);
            float sin = (float)Math.Sin(angleRad);
            return Normalized(new V3(
                baseDir.X * cos - baseDir.Z * sin,
                0f,
                baseDir.X * sin + baseDir.Z * cos));
        }

        /// <summary>
        /// 复刻改造前 `SpawnServerBullets` 的**整个控制流**（分支 + 循环），
        /// 产出一整批子弹方向。
        ///
        /// P3'-3 把它改成了一个单循环（单发由核心原样返回，不再分支），
        /// 所以必须有一道测试证明「分支版」与「循环版」产出同一个方向序列 ——
        /// 这是**结构性**改动，单靠公式级比对盖不到。
        /// 同时它也复刻了 bulletCount<=1 时「走单发分支」这个既有行为
        /// （含 SpawnBulletCount 为 0 / 负数时仍然产出一颗弹）。
        /// </summary>
        internal static List<V3> SpawnBulletDirectionsOld(V3 baseDir, float totalAngle, int spawnBulletCount)
        {
            var list = new List<V3>();
            if (baseDir.X == 0f && baseDir.Z == 0f) return list; // 方向无效：调用方直接 return

            if (spawnBulletCount <= 1)
            {
                list.Add(baseDir);
            }
            else
            {
                for (int i = 0; i < spawnBulletCount; i++)
                    list.Add(SpawnBulletDirection(baseDir, totalAngle, spawnBulletCount, i));
            }
            return list;
        }

        // BattleController.Bullets.cs:299-300 / :261-262
        internal static float BulletStep(float speed, float frameTimeSec)
        {
            return speed * frameTimeSec;
        }

        // BattleController.Bullets.cs:196-199
        internal static bool CheckHit(V3 bulletPos, V3 targetPos, float hitRadius)
        {
            float dist = Distance(bulletPos, targetPos);
            return dist <= hitRadius;
        }

        // BattleController.Bullets.cs:207-210
        internal static int RechargeEnergy(int current, int damage, int max)
        {
            return Math.Min(max, current + damage / 2);
        }
    }

    // ========================================================================
    //  改造前的客户端公式（oracle）
    // ========================================================================
    internal static class OldClient
    {
        // Math/BattleFloatMath.cs:15-20
        internal static void ToMoveDirection(float moveX, float moveY, int sign,
            out float dirX, out float dirZ)
        {
            float x = -moveX * sign;
            float z = moveY * sign;
            float len = (float)Math.Sqrt(x * x + z * z);
            if (len <= 1e-6f) { dirX = 0f; dirZ = 0f; return; }
            dirX = x / len;
            dirZ = z / len;
        }

        // Math/BattleFloatMath.cs:7-13
        // 注意：这里包含 UnityEngine.Vector3.normalized 的 kEpsilon = 1e-5 阈值。
        internal static void ToWorldDirection(float towardX, float towardY, int sign,
            out float dirX, out float dirZ)
        {
            float x = towardY;
            float z = towardX;
            float m = (float)Math.Sqrt(x * x + z * z);
            if (m > 1e-5f) { x /= m; z /= m; }
            else { x = 0f; z = 0f; }

            x *= -1f * sign;
            z *= sign;
            dirX = x;
            dirZ = z;
        }
    }

    internal static class Program
    {
        private static int _failures;
        private static int _checks;

        // 确定性伪随机（自实现 LCG，保证可复现，不依赖 .NET 版本）
        private static uint _seed = 20240613u;
        private static float RandFloat(float lo, float hi)
        {
            _seed = _seed * 1664525u + 1013904223u;
            float t = (_seed >> 8) / (float)(1 << 24);
            return lo + (hi - lo) * t;
        }
        private static int RandInt(int lo, int hi)
        {
            _seed = _seed * 1664525u + 1013904223u;
            return lo + (int)(_seed % (uint)(hi - lo + 1));
        }

        private static void Check(bool ok, string what)
        {
            _checks++;
            if (!ok)
            {
                _failures++;
                if (_failures <= 20) Console.WriteLine("    [FAIL] " + what);
            }
        }

        /// <summary>
        /// 逐位比较 float。
        ///
        /// <para>
        /// 唯一被放行的非逐位差异是 **±0.0 的符号**：IEEE754 下 <c>-0.0 == 0.0</c>，
        /// 且下游所有使用（<c>Vector3</c> 加减、<c>Distance</c>、归一化）对二者不可区分。
        /// 之所以会出现：旧客户端路径写作 <c>x *= -1f * sign</c>，当 x=0 且 sign=+1 时
        /// 得 <c>-0.0</c>；而合并后的核心在「方向无效」分支里显式写 <c>0f</c>（正零）。
        /// 二者语义相同（都表示「没有方向」），所以把零的符号归一后再比。
        /// </para>
        ///
        /// <para>
        /// 非零值的逐位相等仍然严格要求 —— 那才是「公式被改坏」真正会出现的地方。
        /// </para>
        /// </summary>
        private static bool BitsEqual(float a, float b, out int ulpDiff)
        {
            // 先把 ±0 都归为正零
            if (a == 0f) a = 0f;
            if (b == 0f) b = 0f;

            int ia = BitConverter.SingleToInt32Bits(a);
            int ib = BitConverter.SingleToInt32Bits(b);
            if (ia == ib) { ulpDiff = 0; return true; }
            if (float.IsNaN(a) && float.IsNaN(b)) { ulpDiff = 0; return true; }

            // 用「单调映射」把浮点序映射成整数序，便于算 ULP 距离
            long la = ia < 0 ? (long)int.MinValue - ia : ia;
            long lb = ib < 0 ? (long)int.MinValue - ib : ib;
            long d = la > lb ? la - lb : lb - la;
            ulpDiff = d > int.MaxValue ? int.MaxValue : (int)d;
            return false;
        }

        private static int _worstUlp;
        private static string _worstUlpWhere;

        private static void CheckFloat(float actual, float expected, string where)
        {
            _checks++;
            int ulp;
            if (BitsEqual(actual, expected, out ulp)) return;
            _failures++;
            if (ulp > _worstUlp) { _worstUlp = ulp; _worstUlpWhere = where; }
            if (_failures <= 20)
            {
                Console.WriteLine(string.Format(
                    "    [FAIL] {0}: 实际 {1:R} (0x{2:X8}) != 期望 {3:R} (0x{4:X8}) ULP={5}",
                    where, actual, BitConverter.SingleToInt32Bits(actual),
                    expected, BitConverter.SingleToInt32Bits(expected), ulp));
            }
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("---- " + title + " ----");
        }

        /// <summary>打印本节统计。注意要传本节开始时的失败数，否则会打印累计值。</summary>
        private static void Result(string title, int checksBefore, int failuresBefore)
        {
            int n = _checks - checksBefore;
            int f = _failures - failuresBefore;
            Console.WriteLine(string.Format("  {0,-34} {1,7} 项比对，{2} 项失败", title, n, f));
        }

        private static int Main()
        {
            Console.WriteLine("=======================================================================");
            Console.WriteLine(" PMBattleSimTest —— P3'-3 仿真数学抽取的行为等价性");
            Console.WriteLine(" 方法：把改造前的公式逐字复制成 oracle，与 PMBattleSim 逐位比对");
            Console.WriteLine("=======================================================================");

            TestMoveDirection();
            TestAdvancePosition();
            TestVelocity();
            TestAimDirection();
            TestFanSpread();
            TestBulletStep();
            TestHit();
            TestRechargeEnergy();
            TestSpawnLoopEquivalence();
            TestClientMathCompat();
            TestIntentionalDifferences();

            Console.WriteLine();
            Console.WriteLine("=======================================================================");
            if (_failures == 0)
            {
                Console.WriteLine(string.Format(" 结果: PASS —— {0} 项检查，0 项失败", _checks));
                Console.WriteLine(" PMBattleSim 与改造前的服务端/客户端公式逐位等价。");
                return 0;
            }
            Console.WriteLine(string.Format(" 结果: FAIL —— {0} 项检查，{1} 项失败", _checks, _failures));
            if (_worstUlp > 0)
                Console.WriteLine(" 最大 ULP 偏差 " + _worstUlp + " 于 " + _worstUlpWhere);
            return 1;
        }

        // ====================================================================

        private static readonly float[] Speeds = { 3.78f, 3.90f, 3.96f, 4.05f, 4.08f, 4.20f };
        private static readonly float[] Signs = { 1f, -1f };

        private static void TestMoveDirection()
        {
            int b = _checks, bf = _failures;
            Section("移动方向 TryGetMoveDirection");

            foreach (float sign in Signs)
            {
                // 随机输入
                for (int i = 0; i < 3000; i++)
                {
                    float mx = RandFloat(-1.5f, 1.5f);
                    float my = RandFloat(-1.5f, 1.5f);
                    float ox, oz;
                    OldClient.ToMoveDirection(mx, my, (int)sign, out ox, out oz);
                    float nx, nz;
                    bool ok = PMBattleSim.TryGetMoveDirection(mx, my, sign, out nx, out nz);
                    Check(ok == (ox != 0f || oz != 0f), "有效性与客户端 oracle 一致");
                    CheckFloat(nx, ox, string.Format("moveDir mx={0:R} my={1:R} sign={2}", mx, my, sign));
                    CheckFloat(nz, oz, string.Format("moveDir mx={0:R} my={1:R} sign={2}", mx, my, sign));
                }

                // 边界：零输入与极小输入
                foreach (float eps in new[] { 0f, 1e-30f, 1e-7f, 1e-6f, 9.9e-7f, 1.1e-6f, 1e-5f, 1e-4f })
                {
                    float ox, oz, nx, nz;
                    OldClient.ToMoveDirection(eps, 0f, (int)sign, out ox, out oz);
                    PMBattleSim.TryGetMoveDirection(eps, 0f, sign, out nx, out nz);
                    CheckFloat(nx, ox, "moveDir 边界 x=" + eps.ToString("R"));
                    CheckFloat(nz, oz, "moveDir 边界 x=" + eps.ToString("R"));

                    OldClient.ToMoveDirection(0f, eps, (int)sign, out ox, out oz);
                    PMBattleSim.TryGetMoveDirection(0f, eps, sign, out nx, out nz);
                    CheckFloat(nx, ox, "moveDir 边界 y=" + eps.ToString("R"));
                    CheckFloat(nz, oz, "moveDir 边界 y=" + eps.ToString("R"));
                }
            }

            // 无效输入时明确返回 false 且输出为 +0
            float dx, dz;
            Check(!PMBattleSim.TryGetMoveDirection(0f, 0f, 1f, out dx, out dz), "零输入返回 false");
            Check(BitConverter.SingleToInt32Bits(dx) == 0, "零输入输出 +0（不是 -0）");
            Check(BitConverter.SingleToInt32Bits(dz) == 0, "零输入输出 +0（不是 -0）");

            Result("TryGetMoveDirection", b, bf);
        }

        private static void TestAdvancePosition()
        {
            int b = _checks, bf = _failures;
            Section("位置积分 TryAdvancePosition");

            for (int i = 0; i < 6000; i++)
            {
                var start = new OldServer.V3(RandFloat(-40f, 40f), 1f, RandFloat(-40f, 40f));
                float mx = RandFloat(-1.5f, 1.5f);
                float my = RandFloat(-1.5f, 1.5f);
                float sign = RandFloat(0f, 1f) < 0.5f ? 1f : -1f;
                float speed = Speeds[RandInt(0, Speeds.Length - 1)];
                float ft = 0.016f;
                int frames = RandInt(1, 8);

                var o = OldServer.SimulateAuthoritativeMove(start, mx, my, sign, speed, ft, frames);
                float nx, nz;
                PMBattleSim.TryAdvancePosition(start.X, start.Z, mx, my, sign, speed, ft, frames,
                    out nx, out nz);

                CheckFloat(nx, o.X, string.Format("advance mx={0:R} my={1:R} sign={2} speed={3:R} n={4}",
                    mx, my, sign, speed, frames));
                CheckFloat(nz, o.Z, string.Format("advance mx={0:R} my={1:R} sign={2} speed={3:R} n={4}",
                    mx, my, sign, speed, frames));
                // Y 永不变化
                Check(o.Y == start.Y, "advance 不改 Y");
            }

            // frameCount <= 0：位置原样
            foreach (int n in new[] { 0, -1, -100 })
            {
                var start = new OldServer.V3(1.25f, 1f, -2.5f);
                var o = OldServer.SimulateAuthoritativeMove(start, 0.5f, 0.5f, 1f, 3.9f, 0.016f, n);
                float nx, nz;
                bool moved = PMBattleSim.TryAdvancePosition(start.X, start.Z, 0.5f, 0.5f, 1f, 3.9f, 0.016f, n,
                    out nx, out nz);
                CheckFloat(nx, o.X, "advance n=" + n);
                CheckFloat(nz, o.Z, "advance n=" + n);
                Check(!moved, "advance n=" + n + " 报告未移动");
            }

            // 零输入：位置原样（无惯性）
            {
                var start = new OldServer.V3(3f, 1f, 4f);
                var o = OldServer.SimulateAuthoritativeMove(start, 0f, 0f, 1f, 3.9f, 0.016f, 1);
                float nx, nz;
                bool moved = PMBattleSim.TryAdvancePosition(start.X, start.Z, 0f, 0f, 1f, 3.9f, 0.016f, 1,
                    out nx, out nz);
                CheckFloat(nx, o.X, "advance 零输入");
                CheckFloat(nz, o.Z, "advance 零输入");
                Check(!moved, "advance 零输入报告未移动");
            }

            // 单步位移的绝对值（0.0624 是既有行为，不是巧合：3.9 * 0.016）
            {
                float nx, nz;
                PMBattleSim.TryAdvancePosition(0f, 0f, 0f, 1f, 1f, 3.9f, 0.016f, 1, out nx, out nz);
                CheckFloat(nz, 3.9f * 0.016f, "单步位移 = speed*frameTime");
            }

            Result("TryAdvancePosition", b, bf);
        }

        private static void TestVelocity()
        {
            int b = _checks, bf = _failures;
            Section("权威速度 TryGetVelocity");

            for (int i = 0; i < 3000; i++)
            {
                float mx = RandFloat(-1.5f, 1.5f);
                float my = RandFloat(-1.5f, 1.5f);
                float sign = RandFloat(0f, 1f) < 0.5f ? 1f : -1f;
                float speed = Speeds[RandInt(0, Speeds.Length - 1)];

                var o = OldServer.CalculateAuthoritativeVelocity(mx, my, sign, speed);
                float vx, vz;
                PMBattleSim.TryGetVelocity(mx, my, sign, speed, out vx, out vz);

                CheckFloat(vx, o.X, string.Format("velocity mx={0:R} my={1:R}", mx, my));
                CheckFloat(vz, o.Z, string.Format("velocity mx={0:R} my={1:R}", mx, my));
            }

            // 零输入
            {
                var o = OldServer.CalculateAuthoritativeVelocity(0f, 0f, 1f, 3.9f);
                float vx, vz;
                bool ok = PMBattleSim.TryGetVelocity(0f, 0f, 1f, 3.9f, out vx, out vz);
                CheckFloat(vx, o.X, "velocity 零输入");
                CheckFloat(vz, o.Z, "velocity 零输入");
                Check(!ok, "velocity 零输入返回 false");
            }

            Result("TryGetVelocity", b, bf);
        }

        private static void TestAimDirection()
        {
            int b = _checks, bf = _failures;
            Section("攻击朝向 TryGetAimDirection（对照**服务端** oracle）");

            for (int i = 0; i < 4000; i++)
            {
                float tx = RandFloat(-1.5f, 1.5f);
                float ty = RandFloat(-1.5f, 1.5f);
                float sign = RandFloat(0f, 1f) < 0.5f ? 1f : -1f;

                var o = OldServer.SpawnDirection(tx, ty, sign);
                float dx, dz;
                bool ok = PMBattleSim.TryGetAimDirection(tx, ty, sign, out dx, out dz);

                bool oIsZero = o.X == 0f && o.Z == 0f;
                Check(ok == !oIsZero, "aim 有效性与服务端 oracle 一致");
                CheckFloat(dx, o.X, string.Format("aim tx={0:R} ty={1:R} sign={2}", tx, ty, sign));
                CheckFloat(dz, o.Z, string.Format("aim tx={0:R} ty={1:R} sign={2}", tx, ty, sign));
            }

            Result("TryGetAimDirection", b, bf);
        }

        private static void TestFanSpread()
        {
            int b = _checks, bf = _failures;
            Section("散弹扇形 SpreadDirection");

            // 用真实的 LaunchAngle / 弹数组合（取自共享表里的实际配置）
            var cases = new[]
            {
                // (totalAngle, bulletCount) —— 覆盖单发、多发的典型值
                Tuple.Create(0f, 1), Tuple.Create(0f, 2), Tuple.Create(10f, 1), Tuple.Create(10f, 3),
                Tuple.Create(15f, 1), Tuple.Create(20f, 4), Tuple.Create(30f, 5), Tuple.Create(40f, 10),
                Tuple.Create(45f, 3), Tuple.Create(45f, 15), Tuple.Create(50f, 2), Tuple.Create(20f, 1),
                Tuple.Create(-30f, 5), Tuple.Create(360f, 7),
            };

            foreach (var c in cases)
            {
                float totalAngle = c.Item1;
                int count = c.Item2;
                for (int i = 0; i < count; i++)
                {
                    float tx = RandFloat(-1f, 1f);
                    float ty = RandFloat(-1f, 1f);
                    float sign = RandFloat(0f, 1f) < 0.5f ? 1f : -1f;
                    var baseDir = OldServer.SpawnDirection(tx, ty, sign);
                    if (baseDir.X == 0f && baseDir.Z == 0f) continue; // 退化方向，调用方会跳过

                    var o = OldServer.SpawnBulletDirection(baseDir, totalAngle, count, i);
                    float dx, dz;
                    PMBattleSim.SpreadDirection(baseDir.X, baseDir.Z, totalAngle, count, i,
                        out dx, out dz);

                    string where = string.Format("fan angle={0:R} count={1} i={2}", totalAngle, count, i);
                    CheckFloat(dx, o.X, where);
                    CheckFloat(dz, o.Z, where);
                }
            }

            // 单发（count ≤ 1）必须**原样返回 baseDir** —— 服务端在那种情况下不进扇形代码。
            {
                float bx = 0.6f, bz = 0.8f; // 已归一化
                float dx, dz;
                PMBattleSim.SpreadDirection(bx, bz, 30f, 1, 0, out dx, out dz);
                CheckFloat(dx, bx, "单发扇形原样返回 dirX");
                CheckFloat(dz, bz, "单发扇形原样返回 dirZ");

                OldServer.SpawnBulletDirection(new OldServer.V3(bx, 0f, bz), 30f, 1, 0);
                // count = 0 也应原样返回（防御）
                PMBattleSim.SpreadDirection(bx, bz, 30f, 0, 0, out dx, out dz);
                CheckFloat(dx, bx, "count=0 原样返回 dirX");
                CheckFloat(dz, bz, "count=0 原样返回 dirZ");
            }

            // 首尾张角必须等于总张角 —— 这正是 `/(count-1)` 与 `/(count)` 的可观测差别。
            //
            // 注意不要用「单颗的绝对角度」来断言：旋转公式 (x·cos−z·sin, x·sin+z·cos)
            // 绕的是 -Y 方向，atan2(x,z) 测得的角度符号与 angleDeg 相反，那样写会自找麻烦。
            // 「首尾夹角 == 总张角」与约定无关，才是真正要守的性质。
            foreach (var probe in new[] { Tuple.Create(40f, 5), Tuple.Create(30f, 2), Tuple.Create(45f, 15), Tuple.Create(20f, 4) })
            {
                float totalAngle = probe.Item1;
                int count = probe.Item2;
                float ax, az, bx2, bz2;
                PMBattleSim.SpreadDirection(0f, 1f, totalAngle, count, 0, out ax, out az);
                PMBattleSim.SpreadDirection(0f, 1f, totalAngle, count, count - 1, out bx2, out bz2);

                double dot = (double)ax * bx2 + (double)az * bz2;
                if (dot > 1.0) dot = 1.0;
                if (dot < -1.0) dot = -1.0;
                float spreadDeg = (float)(Math.Acos(dot) * 180.0 / Math.PI);

                Check(Math.Abs(spreadDeg - totalAngle) < 0.05f,
                    string.Format("首尾张角 = 总张角 (angle={0:R} count={1}，实测 {2:F3})",
                        totalAngle, count, spreadDeg));
            }

            Result("SpreadDirection", b, bf);
        }

        private static void TestBulletStep()
        {
            int b = _checks, bf = _failures;
            Section("子弹步长 BulletStepDistance");

            // 真实配置里的弹速
            var speeds = new[] { 5f, 8f, 10f, 11f, 12f, 13f, 14f, 16f, 18f, 19f };
            foreach (float s in speeds)
            {
                float o = OldServer.BulletStep(s, 0.016f);
                float n = PMBattleSim.BulletStepDistance(s, 0.016f);
                CheckFloat(n, o, "bulletStep speed=" + s.ToString("R"));
            }

            for (int i = 0; i < 2000; i++)
            {
                float s = RandFloat(0f, 40f);
                float ft = RandFloat(0.001f, 0.05f);
                CheckFloat(PMBattleSim.BulletStepDistance(s, ft), OldServer.BulletStep(s, ft),
                    string.Format("bulletStep speed={0:R} ft={1:R}", s, ft));
            }

            // 超距判定
            Check(PMBattleSim.IsBulletExpired(6f, 6f), "traveled == max 视为超距");
            Check(PMBattleSim.IsBulletExpired(6.1f, 6f), "traveled > max 视为超距");
            Check(!PMBattleSim.IsBulletExpired(5.9f, 6f), "traveled < max 未超距");

            Result("BulletStepDistance", b, bf);
        }

        private static void TestHit()
        {
            int b = _checks, bf = _failures;
            Section("命中判定 IsHit");

            for (int i = 0; i < 8000; i++)
            {
                var bp = new OldServer.V3(RandFloat(-20f, 20f), 1f, RandFloat(-20f, 20f));
                var tp = new OldServer.V3(RandFloat(-20f, 20f), 1f, RandFloat(-20f, 20f));
                float r = 0.8f;

                bool o = OldServer.CheckHit(bp, tp, r);
                bool n = PMBattleSim.IsHit(bp.X, bp.Y, bp.Z, tp.X, tp.Y, tp.Z, r);
                Check(o == n, string.Format("hit bp=({0:R},{1:R}) tp=({2:R},{3:R})", bp.X, bp.Z, tp.X, tp.Z));
            }

            // 恰好等于半径（`<=` 而不是 `<`）
            Check(PMBattleSim.IsHit(0f, 1f, 0f, 0.8f, 1f, 0f, 0.8f), "距离恰好等于半径算命中（<=）");
            Check(!PMBattleSim.IsHit(0f, 1f, 0f, 0.8000001f, 1f, 0f, 0.8f), "略超半径不算命中");

            // Y 参与距离：虽然实战中 Y 差恒为 0，公式本身是 3D 的
            Check(PMBattleSim.IsHit(0f, 0f, 0f, 0f, 0.5f, 0f, 0.8f), "Y 差参与距离（3D）");
            Check(!PMBattleSim.IsHit(0f, 0f, 0f, 0f, 1.0f, 0f, 0.8f), "Y 差超半径不命中");

            Result("IsHit", b, bf);
        }

        private static void TestRechargeEnergy()
        {
            int b = _checks, bf = _failures;
            Section("大招回能 RechargeSuperEnergy");

            var damages = new[] { 0, 1, 2, 3, 45, 60, 80, 260, 320, 340, 400, 448, 460, 520, 650, 680, 800, 816, 840, 1155 };
            int max = 200;

            foreach (int d in damages)
                foreach (int cur in new[] { 0, 1, 100, 199, 200, 250 })
                {
                    int o = OldServer.RechargeEnergy(cur, d, max);
                    int n = PMBattleSim.RechargeSuperEnergy(cur, d, max);
                    Check(o == n, string.Format("energy cur={0} dmg={1} -> {2} vs {3}", cur, d, n, o));
                }

            // 整数除法：45 伤害只回 22
            Check(PMBattleSim.RechargeSuperEnergy(0, 45, 200) == 22, "45 伤害回 22（整数除法向下取整）");
            Check(PMBattleSim.RechargeSuperEnergy(0, 1, 200) == 0, "1 伤害回 0（整数除法）");
            Check(PMBattleSim.RechargeSuperEnergy(199, 45, 200) == 200, "回能封顶 200");

            Result("RechargeSuperEnergy", b, bf);
        }

        // ====================================================================
        //  子弹生成：分支版（旧）→ 单循环版（新）的结构等价
        // ====================================================================

        /// <summary>
        /// 复刻新 `SpawnServerBullets` 的方向生成段：一个循环，单发由核心原样返回。
        /// </summary>
        private static List<OldServer.V3> SpawnBulletDirectionsNew(float baseDirX, float baseDirZ,
            float totalAngle, int spawnBulletCount)
        {
            var list = new List<OldServer.V3>();
            if (baseDirX == 0f && baseDirZ == 0f) return list;

            int bulletCount = spawnBulletCount;
            if (bulletCount < 1) bulletCount = 1;

            for (int i = 0; i < bulletCount; i++)
            {
                float dirX, dirZ;
                PMBattleSim.SpreadDirection(baseDirX, baseDirZ, totalAngle, bulletCount, i,
                    out dirX, out dirZ);
                if (dirX == 0f && dirZ == 0f) continue;
                list.Add(new OldServer.V3(dirX, 0f, dirZ));
            }
            return list;
        }

        private static void TestSpawnLoopEquivalence()
        {
            int b = _checks, bf = _failures;
            Section("子弹生成：分支版(旧) vs 单循环版(新)");

            // 真实的 LaunchAngle × 弹数组合（取自共享表配置）+ 病态值
            var cases = new[]
            {
                Tuple.Create(0f, 1), Tuple.Create(0f, 2), Tuple.Create(10f, 1), Tuple.Create(10f, 3),
                Tuple.Create(15f, 1), Tuple.Create(20f, 1), Tuple.Create(20f, 4), Tuple.Create(30f, 5),
                Tuple.Create(40f, 10), Tuple.Create(45f, 3), Tuple.Create(45f, 15), Tuple.Create(50f, 2),
                Tuple.Create(0f, 0), Tuple.Create(30f, -3),
            };

            foreach (var c in cases)
            {
                for (int k = 0; k < 400; k++)
                {
                    float tx = RandFloat(-1f, 1f);
                    float ty = RandFloat(-1f, 1f);
                    float sign = RandFloat(0f, 1f) < 0.5f ? 1f : -1f;

                    var baseDir = OldServer.SpawnDirection(tx, ty, sign);

                    var oldList = OldServer.SpawnBulletDirectionsOld(baseDir, c.Item1, c.Item2);
                    var newList = SpawnBulletDirectionsNew(baseDir.X, baseDir.Z, c.Item1, c.Item2);

                    string where = string.Format("spawn angle={0:R} count={1} tx={2:R} ty={3:R} sign={4}",
                        c.Item1, c.Item2, tx, ty, sign);

                    Check(oldList.Count == newList.Count, where + " 弹数一致");
                    int n = oldList.Count < newList.Count ? oldList.Count : newList.Count;
                    for (int i = 0; i < n; i++)
                    {
                        CheckFloat(newList[i].X, oldList[i].X, where + " dirX#" + i);
                        CheckFloat(newList[i].Z, oldList[i].Z, where + " dirZ#" + i);
                    }
                }
            }

            // 明确断言病态值的行为：SpawnBulletCount = 0 / 负数 仍产生一颗弹（既有行为）
            {
                var baseDir = OldServer.SpawnDirection(0f, 1f, 1f);
                foreach (int bad in new[] { 0, -1, -5 })
                {
                    var oldList = OldServer.SpawnBulletDirectionsOld(baseDir, 30f, bad);
                    var newList = SpawnBulletDirectionsNew(baseDir.X, baseDir.Z, 30f, bad);
                    Check(oldList.Count == 1, "旧：count=" + bad + " 产出一颗");
                    Check(newList.Count == 1, "新：count=" + bad + " 产出一颗");
                    CheckFloat(newList[0].Z, oldList[0].Z, "count=" + bad + " 方向未旋转");
                }
            }

            // 方向无效（toward 全零）时两边都产 0 颗
            {
                var baseDir = OldServer.SpawnDirection(0f, 0f, 1f);
                Check(baseDir.X == 0f && baseDir.Z == 0f, "零朝向产生零基准方向");
                Check(OldServer.SpawnBulletDirectionsOld(baseDir, 30f, 5).Count == 0, "旧：零方向不生成");
                Check(SpawnBulletDirectionsNew(baseDir.X, baseDir.Z, 30f, 5).Count == 0, "新：零方向不生成");
            }

            Result("子弹生成结构等价", b, bf);
        }

        // ====================================================================
        //  客户端 BattleFloatMath → PMBattleSim 的等价性
        // ====================================================================

        private static void TestClientMathCompat()
        {
            int b = _checks, bf = _failures;
            Section("客户端 BattleFloatMath 语义等价（_ToMoveDirection_ 精确；ToWorldDirection 见下节）");

            // ToMoveDirection：客户端与服务端**逐位**一致（sign² == 1 不影响范数；
            // 负号在除法前后等价，因为 IEEE754 除法是符号对称的）。
            foreach (int sign in new[] { 1, -1 })
            {
                for (int i = 0; i < 3000; i++)
                {
                    float mx = RandFloat(-1.5f, 1.5f);
                    float my = RandFloat(-1.5f, 1.5f);
                    float ox, oz;
                    OldClient.ToMoveDirection(mx, my, sign, out ox, out oz);
                    float nx, nz;
                    PMBattleSim.TryGetMoveDirection(mx, my, sign, out nx, out nz);
                    CheckFloat(nx, ox, string.Format("client moveDir mx={0:R} my={1:R} sign={2}", mx, my, sign));
                    CheckFloat(nz, oz, string.Format("client moveDir mx={0:R} my={1:R} sign={2}", mx, my, sign));
                }
            }

            // 服务端与客户端两条 oracle 必须互相一致（这本身就是一个结论：
            // 「服务端内联公式」与「客户端 BattleFloatMath」在改造前就已经等价）
            for (int i = 0; i < 2000; i++)
            {
                float mx = RandFloat(-1.5f, 1.5f);
                float my = RandFloat(-1.5f, 1.5f);
                int sign = RandFloat(0f, 1f) < 0.5f ? 1 : -1;

                float cx, cz;
                OldClient.ToMoveDirection(mx, my, sign, out cx, out cz);
                var sv = OldServer.SimulateAuthoritativeMove(
                    new OldServer.V3(0f, 1f, 0f), mx, my, sign, 1f, 0.016f, 1);
                // 服务端单步位移除以 (speed*frameTime) 应等于客户端方向
                float scale = 1f * 0.016f;
                CheckFloat(cx * scale, sv.X, "server/client 移动方向一致(x)");
                CheckFloat(cz * scale, sv.Z, "server/client 移动方向一致(z)");
            }

            Result("客户端语义等价", b, bf);
        }

        private static void TestIntentionalDifferences()
        {
            int b = _checks, bf = _failures;
            Section("已知的、有意为之的差异（显式计数，不混进「全等」）");

            // 客户端 ToWorldDirection 走 Vector3.normalized（阈值 1e-5），
            // 合并后统一用服务端口径 1e-6。差异只可能出现在模长 ∈ (1e-6, 1e-5] 这个薄片。
            int sliver = 0, sliverAgree = 0;
            for (int i = 0; i < 200000; i++)
            {
                float m = RandFloat(0f, 2e-5f);
                float ang = RandFloat(0f, 6.283f);
                float tx = m * (float)Math.Cos(ang);
                float ty = m * (float)Math.Sin(ang);
                int sign = RandFloat(0f, 1f) < 0.5f ? 1 : -1;

                float ox, oz;
                OldClient.ToWorldDirection(tx, ty, sign, out ox, out oz);
                float nx, nz;
                PMBattleSim.TryGetAimDirection(tx, ty, sign, out nx, out nz);

                bool inSliver = m > 1e-6f && m <= 1e-5f;
                if (inSliver)
                {
                    sliver++;
                    if (BitConverter.SingleToInt32Bits(nx) == BitConverter.SingleToInt32Bits(ox)
                        && BitConverter.SingleToInt32Bits(nz) == BitConverter.SingleToInt32Bits(oz))
                        sliverAgree++;
                    continue; // 薄片内的差异是预期的
                }

                // 薄片之外必须逐位一致
                CheckFloat(nx, ox, string.Format("aim(非薄片) m={0:R}", m));
                CheckFloat(nz, oz, string.Format("aim(非薄片) m={0:R}", m));
            }

            Console.WriteLine(string.Format(
                "  阈值薄片 (1e-6, 1e-5] 采样 {0} 次；其中新旧恰好相同 {1} 次，不同 {2} 次",
                sliver, sliverAgree, sliver - sliverAgree));
            Console.WriteLine("  → 该区间被 TouchLogic 死区(0.02/0.12)完全屏蔽，实战不可达；");
            Console.WriteLine("    统一到服务端的 1e-6 是**有意**的，不是遗漏。");

            Result("有意差异", b, bf);
        }
    }
}
