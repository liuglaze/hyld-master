// R2 声明门禁的**合法**测试夹具（Tools/PMDeclCheck）。
//
// 本文件同时充当三种用途：
//   1. 声明扫描/发射/ID 锁的正向输入；
//   2. 「重排不变」不变性的源：门禁会把本文件的类型声明与成员声明的顺序整体反转，
//      再生成一次并要求产物逐字节相同；
//   3. 「生成物可编译」门禁的输入：本文件与生成产物、PMNet 运行时一起做一次
//      C# 7.3 编译，要求零错误。
//
// 因此这里刻意覆盖了首版类型集（契约 §6）的每一类：整数 / 布尔 / 浮点 / 字符串 /
// 枚举 / 一维数组，以及三种 RPC 方向与两种校验档位。
//
// 注意：本文件不需要真的被 Unity 编译，但它必须是一段**语法与语义都成立**的 C#，
// 否则「生成物可编译」那道门禁会给出一堆与生成器无关的噪声错误。

using System;
using PMNet;

namespace PMNetFixtures
{
    /// <summary>夹具用枚举（验证"任意底层为整型的 enum"这一条）。</summary>
    public enum FixtureSlot
    {
        /// <summary>空。</summary>
        None = 0,

        /// <summary>甲。</summary>
        First = 1,

        /// <summary>乙。</summary>
        Second = 2,
    }

    /// <summary>
    /// 主力夹具类型：覆盖全部支持类型 + 三种 RPC 方向。
    ///
    /// 第二段 partial 声明见本文件末尾（验证"一个类拆在多个 partial 部分里"的聚合）。
    /// </summary>
    [PMNetworkObject]
    public partial class FixturePlayer : PMNetObject
    {
        /// <summary>整数（规则 4 的基线类型）。</summary>
        [PMReplicated]
        private int _hp;

        /// <summary>带条件的复制（OwnerOnly）+ RepNotify。</summary>
        [PMReplicated(PMCond.OwnerOnly)]
        private int _mana;

        /// <summary>浮点。</summary>
        [PMReplicated]
        private float _speed;

        /// <summary>布尔。</summary>
        [PMReplicated]
        private bool _alive;

        /// <summary>字符串。</summary>
        [PMReplicated]
        private string _nickName;

        /// <summary>枚举。</summary>
        [PMReplicated]
        private FixtureSlot _slot;

        /// <summary>一维数组（长度 + 元素 + null 哨兵）。</summary>
        [PMReplicated]
        private int[] _scores;

        /// <summary>量化器（浮点定点化）。</summary>
        [PMReplicated]
        [PMQuantized(7)]
        private float _aimYaw;

        /// <summary>
        /// RepNotify 目标：必须无参、返回 void（规则 6），ForMember 必须指向一个已声明的
        /// [PMReplicated] 成员（规则 5）。这里用 `nameof` 写法是有意的：
        /// 真实业务会这么写，生成器必须能解析出 `_mana` 而不是字面量 `nameof(_mana)`。
        /// </summary>
        [PMRepNotify(nameof(_mana))]
        private void OnRep_Mana()
        {
            // 只做表现层的事。
        }

        /// <summary>Server RPC：项目反外挂红线要求必须声明校验（规则 8）。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void Fire(int targetId, float angle)
        {
            // DS 侧的业务实现。
        }

        /// <summary>
        /// 与 `Fire` 同名的三态校验同伴。规则 13 要求：声明了 `ForceValidate` 档位
        /// 就必须真的存在这个同伴，否则发射器会生成一次对不存在方法的调用 ——
        /// 或者更糟：退化成「声称有校验、实际没有」。
        /// </summary>
        private PMNet.PMRpcValidation Fire_ForceValidate(int targetId, float angle)
        {
            if (targetId <= 0)
            {
                // 真实的项目实现会在这里调 NET_FORCE_VALIDATE_REASON(...) 再返回 Reject。
                return PMNet.PMRpcValidation.Reject;
            }

            return PMNet.PMRpcValidation.Accept;
        }

        /// <summary>Client RPC。</summary>
        [PMClientRpc(Reliability = PMRpcReliability.Reliable)]
        public void OnDamaged(int amount)
        {
            // 客户端表现。
        }

        /// <summary>NetMulticast RPC。</summary>
        [PMNetMulticast]
        public void OnEffect(FixtureSlot slot, string tag)
        {
            // 所有相关连接都执行。
        }

        /// <summary>查询当前 HP（避免私有字段被判定为"未使用"而影响可编译性门禁的噪声）。</summary>
        public int ReadHp()
        {
            return _hp;
        }
    }

    /// <summary>
    /// 第二批 partial 部分：只为证明同一类型可以拆在多个文件/多个部分里，
    /// 生成物仍然只有一个 `partial class FixturePlayer`。
    ///
    /// 注意：`[PMNetworkObject]` 只能写在**其中一个** partial 部分上
    /// （`PMNetworkObjectAttribute` 是 `AllowMultiple = false`，写两次会得到 CS0579）。
    /// </summary>
    public partial class FixturePlayer
    {
        /// <summary>拆到第二个 partial 部分里的复制成员。</summary>
        [PMReplicated(PMCond.SimulatedOnly)]
        private double _simTime;

        /// <summary>拆到第二个 partial 部分里的 Server RPC（走 `_ForceValidate` 同伴这条路）。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable)]
        public void Teleport(float x, float y, float z)
        {
            // DS 侧的业务实现。
        }

        /// <summary>
        /// 规则 8 的第二个放行条件：存在 `&lt;方法名&gt;_ForceValidate` 同伴。
        /// 返回类型必须是 `PMRpcValidation`（三态）；发射器会据它决定是否跳过实现
        /// （规则 13 会检查这个同伴是否存在）。
        /// </summary>
        private PMNet.PMRpcValidation Teleport_ForceValidate(float x, float y, float z)
        {
            // 三态校验：Accept / Report / Reject（Reject 只跳过实现、不断连）。
            return PMNet.PMRpcValidation.Accept;
        }

        /// <summary>读回拆出来的字段。</summary>
        public double ReadSimTime()
        {
            return _simTime;
        }

        /// <summary>
        /// **数组实参**的 Server RPC。
        ///
        /// 存在的理由：R2 首版**没有任何数组参数的 RPC 声明**，因此调用桩里的
        /// 数组分支（快照 + 入队前长度门）既无编译覆盖也无运行覆盖。
        /// 它同时把数组参数的写体/读体纳入「生成物 C# 7.3 可编译」这道门禁。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void PushValues(int[] values)
        {
            // DS 侧实现。
        }

        /// <summary>`PushValues` 的三态校验同伴（规则 13）。</summary>
        private PMNet.PMRpcValidation PushValues_ForceValidate(int[] values)
        {
            if (values == null)
            {
                return PMNet.PMRpcValidation.Reject;
            }

            return PMNet.PMRpcValidation.Accept;
        }

        /// <summary>
        /// **原生 `Validate` 档位**的 Server RPC（返回 false ⇒ 请求断连）。
        ///
        /// 存在的理由：该分支在 R2 首版**从未被发射过**（全仓 `.g.cs` 搜 `_Validate(` 零命中），
        /// 属纯新增覆盖；否则同伴签名 / 返回值处理 / 断连路径写错也不会有任何门禁发现。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.Validate)]
        public void CheckedTeleport(float x)
        {
            // DS 侧实现。
        }

        /// <summary>`CheckedTeleport` 的原生校验同伴（规则 13）。</summary>
        private bool CheckedTeleport_Validate(float x)
        {
            return x >= 0f;
        }
    }

    /// <summary>
    /// 显式钉住稳定键的类型：改类名后类型 ID **不变**（契约 §2.3 的第三条不变性）。
    /// 门禁会把它改名后再扫一次并断言 ClassId 未变。
    /// </summary>
    [PMNetworkObject("PMDeclCheck.Fixture.Pinned")]
    public partial class FixturePinned : PMNetObject
    {
        /// <summary>一个复制成员。</summary>
        [PMReplicated]
        private int _value;

        /// <summary>读回。</summary>
        public int ReadValue()
        {
            return _value;
        }
    }

    /// <summary>
    /// 派生自**运行时基类**（而不是直接派生 PMNetObject）的类型。
    ///
    /// 它验证规则 1 的继承链推导："能推到 PMNetObject" 必须承认
    /// `PMNetControllerObject` / `PMNetPawnObject` 这类运行时中间基类。
    /// </summary>
    [PMNetworkObject]
    public partial class FixtureController : PMNetControllerObject
    {
        /// <summary>一个复制成员。</summary>
        [PMReplicated(PMCond.AutonomousOnly)]
        private uint _pingMs;

        /// <summary>读回。</summary>
        public uint ReadPingMs()
        {
            return _pingMs;
        }
    }

    /// <summary>
    /// 第二个网络类，验证"每类一个产物 + 注册表显式列出每个类"。
    /// 它也是"改名显式"不变性的对象：它**没有**钉住稳定键，
    /// 因此门禁改名后应得到一个**新的** ClassId 并产生退役告警。
    /// </summary>
    [PMNetworkObject]
    public partial class FixtureHud : PMNetObject
    {
        /// <summary>剩余时间（秒）。</summary>
        [PMReplicated]
        private float _remainSeconds;

        /// <summary>跨类引用枚举，验证生成物复制源文件 using 之外的同命名空间类型解析。</summary>
        [PMReplicated(PMCond.Custom)]
        private FixtureSlot _highlight;

        /// <summary>读回。</summary>
        public float ReadRemainSeconds()
        {
            return _remainSeconds;
        }
    }
}
