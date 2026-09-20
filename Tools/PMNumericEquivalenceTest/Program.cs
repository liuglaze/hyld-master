using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PMNet.Shared;

namespace PMNumericEquivalenceTest
{
    /// <summary>
    /// P3'-2 验收：证明「数值源从两份副本换成共享表」是**行为等价**的。
    ///
    /// 基准是迁移前真实的 <c>Server/Server/HeroConfig.cs</c> 取值（冻结为 JSON 快照）。
    /// 本测试不重新抄写那些数字，而是直接读快照，因此它校验的是「新表 == 旧服务端实际行为」这件事本身。
    /// </summary>
    internal static class Program
    {
        private static int _checks;
        private static int _failures;
        private static readonly List<string> _failLines = new List<string>();

        private static readonly string[] ORDER =
        {
            "XueLi", "KeErTe", "PeiPei", "PanNi", "BaLi", "GongNiu", "DaLiEr", "GeEr", "BuLuoKe",
            "BaoPoMaiKe", "ABo", "DiKe", "BeiYa", "TaLa", "MaiKeSi", "SiPaiKe", "HeiYa", "LiAng",
            "PaMu", "RuiKe"
        };

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("===== 数值迁移等价性测试（P3'-2）=====");
            Console.WriteLine();

            // 快照查找顺序：命令行参数 → 输出目录（csproj 已 CopyToOutputDirectory） → Tools 目录。
            // 优先输出目录，因为那是最常见的运行位置（dotnet bin/.../PMNumericEquivalenceTest.dll）。
            string snapshotPath = args.Length > 0 ? args[0] : null;
            if (snapshotPath == null)
            {
                string[] candidates =
                {
                    Path.Combine(AppContext.BaseDirectory, "基准快照.json"),
                    Path.Combine(AppContext.BaseDirectory, "_old_heroconfig_snapshot.json"),
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "_old_heroconfig_snapshot.json"),
                };

                foreach (string candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        snapshotPath = candidate;
                        break;
                    }
                }
            }

            if (snapshotPath == null || !File.Exists(snapshotPath))
            {
                Console.WriteLine("  [FAIL] 找不到基准快照。已尝试：命令行参数 / 输出目录 / Tools 目录");
                Console.WriteLine("         基准快照必须随测试一起提供（Tools/_old_heroconfig_snapshot.json），");
                Console.WriteLine("         它记录了迁移前 Server/Server/HeroConfig.cs 的实际取值，是本次等价性判定的基准。");
                return 1;
            }

            Snapshot snap = Snapshot.Load(snapshotPath);
            Console.WriteLine("  基准快照：" + Path.GetFullPath(snapshotPath));
            Console.WriteLine("  normal=" + snap.Normal.Count + " super=" + snap.Super.Count
                + " hp=" + snap.Hp.Count + " reload=" + snap.Reload.Count);
            Console.WriteLine();

            CheckGlobalConstants(snap);
            CheckPerHero(snap);
            CheckMovementSpeedIsIntentionalChange(snap);

            Console.WriteLine();
            Console.WriteLine("== 汇总: " + _checks + " 项检查, " + _failures + " 项失败 ==");
            if (_failures > 0)
            {
                Console.WriteLine();
                Console.WriteLine("失败明细（说明迁移不是等价的，必须逐个解释）：");
                foreach (string line in _failLines)
                {
                    Console.WriteLine("  - " + line);
                }
            }

            Console.WriteLine();
            Console.WriteLine("注意：唯一有意的行为变更是**移速**（旧服务端全局 3.9 -> 新表逐英雄设计值），");
            Console.WriteLine("      它被单独断言，见上面「有意的行为变更」一组。");

            return _failures == 0 ? 0 : 1;
        }

        // ==================== 全局常量 ====================

        private static void CheckGlobalConstants(Snapshot snap)
        {
            Console.WriteLine("[1] 全局常量");
            Check("ManaMax", BattleNumericConfig.ManaMax, snap.Defaults.ManaMax);
            Check("ManaPerSegment", BattleNumericConfig.ManaPerSegment, snap.Defaults.ManaPerSegment);
            Check("SuperEnergyMax", BattleNumericConfig.SuperEnergyMax, snap.Defaults.SuperEnergyMax);
            Check("DefaultAttackManaCost", BattleNumericConfig.DefaultAttackManaCost, snap.Defaults.DefaultAttackManaCost);
            Console.WriteLine();
        }

        // ==================== 逐英雄逐字段 ====================

        private static void CheckPerHero(Snapshot snap)
        {
            Console.WriteLine("[2] 逐英雄比对（新表 vs 旧 HeroConfig 实际值）");

            int heroCount = 0;
            int superCount = 0;

            for (int id = 0; id < ORDER.Length; id++)
            {
                string key = ORDER[id];

                // ---- 旧服务端的键名是 protoc 规范化后的拼写（ABo -> Abo），做大小写不敏感查找 ----
                string nk = FindKey(snap.Normal, key);
                string hk = FindKey(snap.Hp, key);
                string rk = FindKey(snap.Reload, key);

                if (nk == null || hk == null || rk == null)
                {
                    Fail("英雄 " + key + " 在基准快照里缺失（normal=" + (nk != null)
                        + " hp=" + (hk != null) + " reload=" + (rk != null) + "）");
                    continue;
                }

                HeroNumeric hero = BattleNumericConfig.Get(id);
                HeroConfigEntry n = snap.Normal[nk];

                // 普通攻击解析结果
                ResolvedAttack atk = BattleNumericConfig.ResolveAttack(id, false);

                Check(key + ".MaxHp", hero.MaxHp, snap.Hp[hk]);
                CheckFloat(key + ".ReloadSeconds", hero.ReloadSeconds, snap.Reload[rk]);
                CheckFloat(key + ".ServerHitRadius", hero.ServerHitRadius, n.HitRadius);
                CheckFloat(key + ".ShootDistance", hero.ShootDistance, n.BulletMaxDist);
                CheckFloat(key + ".BulletSpeed", hero.BulletSpeed, n.BulletSpeed);
                Check(key + ".BulletDamage", hero.BulletDamage, (int)n.Damage);
                Check(key + ".LaunchAngle", (int)hero.LaunchAngle, (int)n.SpreadAngle);
                Check(key + ".IsParabola", hero.IsParabola, n.IsParabola);

                // 攻击解析：普通攻击的弹数必须等于旧的 BulletCount（= 每次发射数）
                Check(key + ".攻击解析.弹数(普通)", atk.SpawnBulletCount, (int)n.BulletCount);
                CheckFloat(key + ".攻击解析.射程(普通)", atk.ShootDistance, n.BulletMaxDist);
                CheckFloat(key + ".攻击解析.弹速(普通)", atk.BulletSpeed, n.BulletSpeed);
                Check(key + ".攻击解析.伤害(普通)", atk.BulletDamage, (int)n.Damage);
                CheckFloat(key + ".攻击解析.扇形角(普通)", atk.LaunchAngle, n.SpreadAngle);

                heroCount++;

                // ---- 大招 ----
                string sk = FindKey(snap.Super, key);
                SuperNumeric superConfig;
                bool hasSuper = BattleNumericConfig.TryGetSuper(id, out superConfig);

                if (sk == null)
                {
                    // 快照里没有该英雄的大招条目 -> 新表也不应有大招覆写。
                    Check(key + ".无大招覆写", hasSuper, false);
                }
                else
                {
                    Check(key + ".有大招覆写", hasSuper, true);
                    if (!hasSuper)
                    {
                        continue;
                    }

                    HeroConfigEntry sp = snap.Super[sk];
                    ResolvedAttack satk = BattleNumericConfig.ResolveAttack(id, true);

                    CheckFloat(key + ".大招.射程", satk.ShootDistance, sp.BulletMaxDist);
                    CheckFloat(key + ".大招.弹速", satk.BulletSpeed, sp.BulletSpeed);
                    Check(key + ".大招.弹数", satk.SpawnBulletCount, (int)sp.BulletCount);
                    Check(key + ".大招.伤害", satk.BulletDamage, (int)sp.Damage);
                    CheckFloat(key + ".大招.扇形角", satk.LaunchAngle, sp.SpreadAngle);
                    Check(key + ".大招.IsParabola", satk.IsParabola, sp.IsParabola);
                    superCount++;
                }
            }

            Console.WriteLine("      （比对英雄 " + heroCount + " 个，大招覆写 " + superCount + " 个）");
            Console.WriteLine();
        }

        // ==================== 有意的行为变更 ====================

        private static void CheckMovementSpeedIsIntentionalChange(Snapshot snap)
        {
            Console.WriteLine("[3] 有意的行为变更：移速");

            // 旧服务端：全局常量 3.9
            const float oldServerMoveSpeed = 3.9f;

            int changed = 0;
            List<string> changedNames = new List<string>();
            for (int id = 0; id < ORDER.Length; id++)
            {
                float spd = BattleNumericConfig.Get(id).MoveSpeed;
                if (Math.Abs(spd - oldServerMoveSpeed) > 1e-6f)
                {
                    changed++;
                    changedNames.Add(ORDER[id] + "=" + spd.ToString("F2"));
                }
            }

            // 预期恰好 5 个英雄与 3.9 不同（黑鸦 4.2 / 麦克斯 4.08 / 柯尔特 4.05 / 里昂 3.96 / 帕姆 3.78）
            Check("与旧服务端常量 3.9 不同的英雄个数（预期 5）", changed, 5);

            Console.WriteLine("      与 3.9 不同的英雄：" + string.Join(", ", changedNames.ToArray()));
            Console.WriteLine("      说明：这不是回归，而是修复 —— 客户端预测本就是逐英雄，");
            Console.WriteLine("            服务端沿用单一 3.9 会让权威位置与预测位置持续偏差、反复触发校正。");

            // 逐英雄对比客户端设计值，确认新表取的确实是客户端那一套
            for (int id = 0; id < ORDER.Length; id++)
            {
                string key = ORDER[id];
                if (snap.MoveSpeedFromClient != null && snap.MoveSpeedFromClient.ContainsKey(key))
                {
                    CheckFloat(key + ".MoveSpeed(应等于客户端设计值)",
                        BattleNumericConfig.Get(id).MoveSpeed, snap.MoveSpeedFromClient[key]);
                }
            }
            Console.WriteLine();
        }

        // ==================== 断言 ====================

        private static void Check(string name, int actual, int expected)
        {
            _checks++;
            if (actual == expected)
            {
                return;
            }

            _failures++;
            _failLines.Add(name + ": 实际=" + actual + " 期望=" + expected);
        }

        private static void Check(string name, bool actual, bool expected)
        {
            _checks++;
            if (actual == expected)
            {
                return;
            }

            _failures++;
            _failLines.Add(name + ": 实际=" + actual + " 期望=" + expected);
        }

        private static void CheckFloat(string name, float actual, float expected)
        {
            _checks++;
            if (Math.Abs(actual - expected) <= 1e-4f)
            {
                return;
            }

            _failures++;
            _failLines.Add(name + ": 实际=" + actual.ToString("G9") + " 期望=" + expected.ToString("G9"));
        }

        private static void Fail(string message)
        {
            _checks++;
            _failures++;
            _failLines.Add(message);
        }

        private static string FindKey<T>(Dictionary<string, T> dict, string name)
        {
            foreach (KeyValuePair<string, T> kv in dict)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Key;
                }
            }

            return null;
        }

        // ==================== 基准快照读取 ====================

        /// <summary>
        /// 手写的基准数据容器。
        ///
        /// <para>刻意不做成「直接反序列化到 Dictionary」的花活：这些字段名（BulletMaxDist / SpreadAngle …）
        /// 是**旧**服务端的命名，本测试的意义就是照它比对，所以结构体也按旧命名保留，
        /// 让「新旧字段对应关系」在代码里一眼可见。</para>
        /// </summary>
        private sealed class HeroConfigEntry
        {
            public float BulletSpeed { get; set; }
            public float BulletMaxDist { get; set; }
            public float HitRadius { get; set; }
            public float Damage { get; set; }
            public float BulletCount { get; set; }
            public float SpreadAngle { get; set; }
            public bool IsParabola { get; set; }
        }

        private sealed class Defaults
        {
            public int ManaMax { get; set; }
            public int ManaPerSegment { get; set; }
            public int DefaultAttackManaCost { get; set; }
            public int SuperEnergyMax { get; set; }
        }

        private sealed class Snapshot
        {
            public Dictionary<string, HeroConfigEntry> Normal { get; set; }
            public Dictionary<string, HeroConfigEntry> Super { get; set; }
            public Dictionary<string, int> Hp { get; set; }
            public Dictionary<string, float> Reload { get; set; }
            public Dictionary<string, float> MoveSpeedFromClient { get; set; }
            public Defaults Defaults { get; set; }

            public static Snapshot Load(string path)
            {
                string text = File.ReadAllText(path, Encoding.UTF8);

                // net8.0 自带 System.Text.Json；此前手搓过一个解析器，
                // 结果在 defaults 嵌套对象上就出错 —— 用标准库消除这类自造风险。
                Snapshot s = System.Text.Json.JsonSerializer.Deserialize<Snapshot>(text,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    });

                if (s == null)
                {
                    throw new InvalidDataException("基准快照反序列化结果为 null：" + path);
                }

                if (s.Normal == null) s.Normal = new Dictionary<string, HeroConfigEntry>();
                if (s.Super == null) s.Super = new Dictionary<string, HeroConfigEntry>();
                if (s.Hp == null) s.Hp = new Dictionary<string, int>();
                if (s.Reload == null) s.Reload = new Dictionary<string, float>();
                if (s.MoveSpeedFromClient == null) s.MoveSpeedFromClient = new Dictionary<string, float>();
                if (s.Defaults == null) s.Defaults = new Defaults();

                return s;
            }
        }
    }
}
