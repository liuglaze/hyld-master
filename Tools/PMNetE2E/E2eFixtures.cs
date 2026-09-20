// R2-C 端到端总门禁的声明夹具。
//
// 本文件是**声明阶段的唯一输入**：`PMNetE2E.csproj` 的 `PMNetGenE2eFixtures` target
// 就是拿这一个文件去跑 `PMNetGen --decl-gen`，产物落在 `Tools/PMNetE2E/Generated/`。
//
// 覆盖契约 `Docs/plans/net-r2-codegen-contract.md` 里会被端到端用到的三样东西：
//   [1] 复制属性：int / float / string 各一个（契约 §6 的三类线格式）+ 一个带条件的属性；
//   [2] RepNotify：`[PMRepNotify]` 指向其中一个复制成员（规则 5/6）；
//   [3] RPC：一个 `Server` RPC（ForceValidate 三态 + 真的写了 `_ForceValidate` 同伴，
//       规则 8/13）+ 一个 `Client` RPC；另有一个**数组实参**的 `Multicast` RPC
//       （验证调用点快照）与一个字符串实参的 `Client` RPC（验证入队前长度门）；
//       一个**原生 `Validate`** 档位的 `Server` RPC（false ⇒ 请求断连）。
//   [4] 创建回调：`OnReplicatedCreate` 读一遗四个复制成员，作为
//       「初值与创建同包到达」（D-R0-16）的业务侧可观察证据。
//
// 刻意**不**放任何业务逻辑：本文件同时会作为「生成物在 C# 7.3 + netstandard2.0 下真编译」
// 那条断言的输入之一，逻辑越少，编译失败时越容易定位是生成物的问题还是夹具的问题。

using System;
using PMNet;

namespace PMNetE2E
{
    /// <summary>
    /// 主力端到端夹具。
    ///
    /// 之所以用 `partial` 并直接派生 <see cref="PMNetObject"/>：规则 1 要求生成物能往
    /// 同一个类里补成员，而复制需要基类。
    /// </summary>
    [PMNetworkObject]
    public partial class E2eReplicated : PMNetObject
    {
        // ---------------------------------------------------------------- 复制属性

        /// <summary>整数属性（varint）。值未变不发、旧 ACK 不清新脏位这两条都用它。</summary>
        [PMReplicated]
        private int _health;

        /// <summary>浮点属性（fixed32）。</summary>
        [PMReplicated]
        private float _speed;

        /// <summary>字符串属性（length-delimited UTF-8，自带 1024 字节上限）。</summary>
        [PMReplicated]
        private string _title = string.Empty;

        /// <summary>
        /// 带条件的属性（`OwnerOnly`）：专门用来测 D-R0-15 的「条件由不满足变满足 ⇒ 补发当前值」。
        /// 条件必须真的能翻转，否则跃迁路径永远走不到 —— 那正是 B 块门禁里
        /// 「注入缺陷后 0 项失败」的成因（主计划 §9.5 教训 28）。
        /// </summary>
        [PMReplicated(PMCond.OwnerOnly)]
        private int _ammo;

        // ---------------------------------------------------------------- 计数器（仅测试用，不参与复制）

        /// <summary>`OnRep_Health` 真的被调用了几次。端到端断言靠它判定「OnRep 真被调用」。</summary>
        public int HealthNotifyCount;

        /// <summary>`Fire` 的业务实现被调用了几次（接收侧 Accept / Report 路径）。</summary>
        public int FireCount;

        /// <summary>`Notify` 的业务实现被调用了几次。</summary>
        public int NotifyCount;

        /// <summary>最近一次 `Fire` 收到的 targetId —— 用于证明「实参没有被后续调用覆盖」。</summary>
        public int LastFireTargetId;

        /// <summary>最近一次 `Fire` 收到的 angle。</summary>
        public float LastFireAngle;

        /// <summary>最近一次 `Notify` 收到的 code。</summary>
        public int LastNotifyCode;

        /// <summary>`Push` 的业务实现被调用了几次。</summary>
        public int PushCount;

        /// <summary>最近一次 `Push` 收到的数组长度（null 记为 -1）——用于证明快照真的是一个拷贝。</summary>
        public int LastPushLength;

        /// <summary>最近一次 `Push` 收到的首元素。`Push` 实现会**就地改写**它，所以它必须来自快照。</summary>
        public float LastPushFirst;

        /// <summary>`Checked` 的业务实现被调用了几次（原生 Validate 档位）。</summary>
        public int CheckedCount;

        /// <summary>最近一次 `Checked` 收到的 token。</summary>
        public int LastCheckedToken;

        /// <summary>`Say` 的业务实现被调用了几次。</summary>
        public int SayCount;

        /// <summary>最近一次 `Say` 收到的字符串。</summary>
        public string LastSay;

        /// <summary>`Warp` 的业务实现被调用了几次（ForceValidate 三态 + 非法值）。</summary>
        public int WarpCount;

        /// <summary>最近一次 `Warp` 收到的 verdict 实参。</summary>
        public int LastWarpVerdict;

        // ---------------------------------------------------------------- 创建回调快照（D-R0-16）

        /// <summary>`OnReplicatedCreate` 被调用的次数。默认 0 = 创建回调从未发生。</summary>
        public int CreateCallbackCount;

        /// <summary>创建回调里读到的 `_health`。默认 `int.MinValue` = 回调从未发生（与合法的负值区分开）。</summary>
        public int CreateHealthSeen = int.MinValue;

        /// <summary>创建回调里读到的 `_speed`。默认 NaN = 回调从未发生。</summary>
        public float CreateSpeedSeen = float.NaN;

        /// <summary>创建回调里读到的 `_title`。默认 null = 回调从未发生。</summary>
        public string CreateTitleSeen;

        /// <summary>创建回调里读到的 `_ammo`（OwnerOnly）。默认 `int.MinValue` = 回调从未发生。</summary>
        public int CreateAmmoSeen = int.MinValue;

        /// <summary>
        /// 接收侧创建完成回调：在这里读一遗全部复制成员。
        ///
        /// 这是"创建与初始状态原子到达"（D-R0-16）在业务侧唯一可见的证据：
        /// 创建回调里读到的必须已经是**初值**，而不是等后续更新来补齐 ——
        /// 否则存在一个"对象已 Active 但字段还是默认值"的可见窗口。
        /// </summary>
        protected internal override void OnReplicatedCreate()
        {
            CreateCallbackCount++;
            CreateHealthSeen = _health;
            CreateSpeedSeen = _speed;
            CreateTitleSeen = _title;
            CreateAmmoSeen = _ammo;
        }

        // ---------------------------------------------------------------- RepNotify

        /// <summary>
        /// 复制属性 `_health` 变化后的表现回调。
        /// 无参、返回 void（规则 6）；`ForMember` 指向一个已声明的 `[PMReplicated]` 成员（规则 5）。
        /// </summary>
        [PMRepNotify(nameof(_health))]
        private void OnRep_Health()
        {
            HealthNotifyCount++;
        }

        // ---------------------------------------------------------------- RPC

        /// <summary>
        /// 上行（Server）RPC：项目红线要求必须声明反外挂校验（规则 8）。
        /// 三态判定放在同伴方法里，以便端到端分别走通 Accept / Reject / Report 三条分支。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void Fire(int targetId, float angle)
        {
            FireCount++;
            LastFireTargetId = targetId;
            LastFireAngle = angle;
        }

        /// <summary>
        /// `Fire` 的三态校验同伴。规则 13 要求声明了 `ForceValidate` 就必须真的存在它。
        ///
        /// 三条分支的划分方式是刻意的：端到端要**在同一段断言代码**里分别验证
        /// 「Reject ⇒ 跳过实现、不断连」「Report ⇒ 上报后仍执行」「Accept ⇒ 静默放行」。
        /// </summary>
        private PMRpcValidation Fire_ForceValidate(int targetId, float angle)
        {
            if (targetId < 0)
            {
                return PMRpcValidation.Reject;
            }

            if (targetId == 7)
            {
                return PMRpcValidation.Report;
            }

            return PMRpcValidation.Accept;
        }

        /// <summary>下行（Client）RPC：用来验证「按方向位生成调用桩」这一条。</summary>
        [PMClientRpc(Reliability = PMRpcReliability.Reliable)]
        public void Notify(int code)
        {
            NotifyCount++;
            LastNotifyCode = code;
        }

        /// <summary>
        /// **数组实参**的 Multicast RPC：验证「调用点快照」这条核心不变性。
        ///
        /// 刻意用 Multicast 而不是 Server：Multicast 在服务端是 **Local | Remote** ——
        /// 先本地执行、再外发。本实现会**就地改写**数组实参，因此只有
        /// 「快照发生在本地执行之前」才能让远端仍看到**调用时**的值。
        /// 这也是旧设计（实参帧 / 只捕获引用）真正会发错值的那一类场景。
        /// </summary>
        [PMNetMulticast(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void Push(float[] values)
        {
            PushCount++;
            LastPushLength = values == null ? -1 : values.Length;
            LastPushFirst = (values == null || values.Length == 0) ? float.NaN : values[0];

            if (values != null && values.Length > 0)
            {
                // 就地改写：远端必须仍看到调用时的值（这正是快照要解决的）。
                values[0] = 999f;
            }
        }

        /// <summary>
        /// `Push` 的三态校验同伴（规则 13）。
        /// 数组元素为负 ⇒ Report（仍执行）；null ⇒ Reject（跳过实现、不断连）。
        /// </summary>
        private PMRpcValidation Push_ForceValidate(float[] values)
        {
            if (values == null)
            {
                return PMRpcValidation.Reject;
            }

            if (values.Length > 0 && values[0] < 0f)
            {
                return PMRpcValidation.Report;
            }

            return PMRpcValidation.Accept;
        }

        /// <summary>
        /// **原生 `Validate` 档位**的 Server RPC：`_Validate` 返回 false ⇒ 请求断连。
        ///
        /// 与 `Fire` 的 `ForceValidate` 刻意形成对照：后者 `Reject` 只跳过实现、**不断连**，
        /// 而这一条必须真的向来源连接发出断连请求。该分支在 R2 首版从未被发射过。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.Validate)]
        public void Checked(int token)
        {
            CheckedCount++;
            LastCheckedToken = token;
        }

        /// <summary>`Checked` 的原生校验同伴（规则 13）。</summary>
        private bool Checked_Validate(int token)
        {
            return token >= 0;
        }

        /// <summary>
        /// ForceValidate 三态的**显式**夹具：返回值直接由实参决定，
        /// 因此可以逐条验证 Accept / Report / Reject，以及
        /// **非法返回值必须失败关闭**（不执行实现）。
        ///
        /// `(PMRpcValidation)99` 是完全合法的 C#（枚举底层是 byte，C# 不校验范围）。
        /// 旧形态的生成物写成「Reject → 跳过；Report → 上报；否则执行」，
        /// 于是 99 掉进最后一个分支 ⇒ 一次本该被拒的调用被静默放行。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void Warp(int verdict)
        {
            WarpCount++;
            LastWarpVerdict = verdict;
        }

        /// <summary>`Warp` 的三态校验同伴：原样回传实参，由测试选择放行/上报/拒绝/非法值。</summary>
        private PMRpcValidation Warp_ForceValidate(int verdict)
        {
            return (PMRpcValidation)verdict;
        }

        /// <summary>
        /// 字符串实参的 Client RPC：验证「字符串长度门在**入队前**执行」。
        /// string 不可变 ⇒ 不需要快照，只需要长度门。
        /// </summary>
        [PMClientRpc(Reliability = PMRpcReliability.Reliable)]
        public void Say(string message)
        {
            SayCount++;
            LastSay = message;
        }

        // ---------------------------------------------------------------- 夹具自用接口

        /// <summary>读回复制成员（业务侧正常路径，`PMNet_Set_*` 是生成物提供的写入口）。</summary>
        public int ReadHealth()
        {
            return _health;
        }

        /// <summary>读回复制成员。</summary>
        public float ReadSpeed()
        {
            return _speed;
        }

        /// <summary>读回复制成员。</summary>
        public string ReadTitle()
        {
            return _title;
        }

        /// <summary>读回复制成员。</summary>
        public int ReadAmmo()
        {
            return _ammo;
        }

        /// <summary>
        /// 把 `PMNet_OnRepDispatch` 转发成 public。
        ///
        /// 生成物的分发表是 `private static`（契约 §4.1 第 4 条），业务通过
        /// `PMReplicationChannel.RegisterOnRepDispatcher` 注册它；而门禁要在**同为 partial**
        /// 的这里开一个转发口，才能在「复制层真的走到分发表」这条链路上做断言。
        /// 转发本身不改变任何行为，只是把同一个私有方法暴露给门禁。
        /// </summary>
        public static void E2eDispatchOnRep(PMNetObject target, ushort onRepMethodId)
        {
            PMNet_OnRepDispatch(target, onRepMethodId);
        }
    }

    /// <summary>
    /// 第二个网络类：证明「注册表显式列出每个类、不做反射扫描」这一条在**多类**时也成立
    /// （单类时"显式列出"和"碰巧只有一个"无法区分）。
    /// </summary>
    [PMNetworkObject]
    public partial class E2eScoreboard : PMNetObject
    {
        /// <summary>整数属性（与主夹具同类线格式，但属于另一个类 ⇒ 另一个描述符）。</summary>
        [PMReplicated]
        private int _score;

        /// <summary>读回。</summary>
        public int ReadScore()
        {
            return _score;
        }
    }
}
