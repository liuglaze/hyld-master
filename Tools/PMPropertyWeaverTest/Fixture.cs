// PMPropertyWeaverTest 的夹具源（**同时**是 PMNetGen 的声明输入与临时夹具工程的源文件）。
//
// 契约：Docs/plans/net-property-authoring-contract.md（§2 冻结生成接口、§5 T-P2）
//
// ★ 本文件**不再包含任何手写生成物**。
//
//   历史形态（已删除，不再接受）：本文件曾用 `GEN-EXCLUDE` 区块手写
//   `PMGeneratedPropertyIndex_<P>` / `PMNet_PropertyRawSet_<P>` / `PMNet_PropertySet_<P>` /
//   `PMNet_Read_<P>` / 版本门 / 实例 gate / `PMNet_BuildEntry` / `CollectLifetimeReplicatedProps` /
//   `PMNet_OnRepDispatch` —— 那是「按契约手写的独立实现」，只能证明 weave 认识某种形状，
//   不能证明「真实生成物恰好落在编织器可接受的形状内」。自动属性生成器（A 组）落地后，
//   这里只保留**声明与业务代码**，全部生成物由**真实 PMNetGen --decl-gen** 产出：
//
//     [1] 复制属性声明：`[PMReplicated] public int Hp { get; private set; }`（自然 C#）；
//     [2] 业务代码：普通赋值（`Hp = value` / `Hp -= amount`），不含任何 PMNet_ 前缀调用；
//     [3] 驱动：在**被加载进来的夹具程序集内部**跑断言，只把结论（字符串）交回门禁。
//
//   `PropertyDriver` 里**没有**任何 `PMNet_PropertyRawSet_*` / `PMNet_PropertySet_*` /
//   `PMGeneratedPropertyIndex_*` 的定义，只有对**生成物**的引用（常量、Reader、OnRep 分发表）。
//   缺 helper 时构建/weave 必须失败，绝不用字符串模板补出来。
//
// 区块标记：
//   `PROPERTY-ONLY-REGION` 只用于给「纯属性零 RPC」场景切出一份**独立的真实生成输入**
//   （PropFixture + PropertyDriver），它不是"生成物排除区"，区域内同样是纯声明/业务/驱动。
//
// 语言面：C# 7.3（Unity 2019.4 上限）；不引用 Unity，不引用任何第三方包。

using PMNet;

// >>>PROPERTY-ONLY-REGION>>>
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using PMNet;

namespace PMWeave.Property
{
    /// <summary>
    /// auto-property 复制夹具。
    ///
    /// 业务侧只写**普通 C# 自动属性赋值**（`Hp -= amount;`），不调用任何 PMNet_Set_ 前缀；
    /// 编织器把编译器生成的 setter 改写成转发桩后，赋值就会自动「存值 + 变化时按权威标脏」。
    /// </summary>
    [PMNetworkObject]
    public partial class PropFixture : PMNetObject
    {
        // ---------------------------------------------------------------- 本地记账（不参与复制）

        /// <summary>OnRep 回调被调用的次数（本夹具只有复制层分发会调用它）。</summary>
        public int OnRepCount;

        /// <summary>业务赋值入口被调用的次数（证明驱动走的是业务普通赋值，不是手写 setter）。</summary>
        public int HpSetCount;

        // ---------------------------------------------------------------- 复制属性声明（自然 C#）

        /// <summary>公有读、私有写的普通 auto-property：赋值即自动标脏（PushBased 默认 true）。</summary>
        [PMReplicated]
        public int Hp { get; private set; }

        /// <summary>OwnerOnly 条件的 auto-property（条件写在声明上，与字段旧模式一致）。</summary>
        [PMReplicated(PMCond.OwnerOnly)]
        public int Mana { get; private set; }

        /// <summary>PushBased=false：赋值仍存值，但**不得**由 setter 标脏（轮询侧负责采样）。</summary>
        [PMReplicated(PushBased = false)]
        public int Polled { get; private set; }

        /// <summary>数组只跟踪整体引用替换（同引用重赋 / 原地改元素不承诺自动标脏）。</summary>
        [PMReplicated]
        public byte[] Blob { get; private set; }

        /// <summary>带初始化器的 auto-property：构造期原值必须保留，且不依赖标脏。</summary>
        [PMReplicated]
        public int Inited { get; private set; } = 42;

        /// <summary>
        /// 原 setter 带 `[MethodImpl]`（Synchronized | NoInlining）的 auto-property。
        ///
        /// 为什么必须有这个形态：`[MethodImpl(MethodImplOptions.Synchronized)]` 的实例方法
        /// 由运行时对 `this` 加监视器锁（JIT 发射 Monitor.Enter/Exit，IL 体本身不变）。
        /// 编织后**本地赋值**走 `set_Locked → PropertySet → RawSet`，锁由 setter 持有；
        /// 而**收包路径**是 `PMNet_Read_Locked → RawSet`（绕过 setter），
        /// 若 RawSet 不带同一实现标志，收包写就**不受同一把锁保护**（与本地赋值并发即数据竞争）。
        /// 因此契约要求 RawSet 继承原 setter 的实现标志；本属性是这个断言的唯一真实载体。
        ///
        /// 属性本身**不能**直接挂 `[MethodImpl]`（CS0592：该特性只对构造函数/方法有效），
        /// 必须挂在访问器上。
        /// </summary>
        [PMReplicated]
        public int Locked { get; [MethodImpl(MethodImplOptions.Synchronized | MethodImplOptions.NoInlining)] private set; }

        /// <summary>表现回调（只由复制层的 PMNet_OnRepDispatch 调用）。</summary>
        [PMRepNotify(nameof(Hp))]
        private void OnRep_Hp()
        {
            OnRepCount++;
        }

        /// <summary>
        /// 把生成物的私有 OnRep 分发表转发成 public。
        ///
        /// 与生产 `PMR3Player.PMR3DispatchOnRep` / `PMR5Projectile.PMR5DispatchOnRep` **逐字同一做法**：
        /// `PMNet_OnRepDispatch` 是生成物里的 `private static`，只有本类的 partial 能访问，
        /// 而复制通道需要把它注册进 OnRep 分发表（`PMReplicationChannel.RegisterOnRepDispatcher`）。
        /// 这是 1 行接线，**不是**手写的分发表：真正的 switch 分发是生成物。
        /// </summary>
        public static void PMDispatchOnRep(PMNetObject target, ushort onRepMethodId)
        {
            PMNet_OnRepDispatch(target, onRepMethodId);
        }

        // ---------------------------------------------------------------- 业务赋值（普通 C#，不是手写 setter）

        public void BusinessSetHp(int value)
        {
            HpSetCount++;
            Hp = value;
        }

        public void BusinessDamage(int amount)
        {
            Hp -= amount;
        }

        public void BusinessSetMana(int value)
        {
            Mana = value;
        }

        public void BusinessSetPolled(int value)
        {
            Polled = value;
        }

        public void BusinessSetBlob(byte[] value)
        {
            Blob = value;
        }

        /// <summary>同引用重赋（契约：不承诺自动标脏）。</summary>
        public void BusinessReassignSameBlob()
        {
            Blob = Blob;
        }

        /// <summary>新引用、同内容（契约：可以标脏；线层值比较抑制无效包）。</summary>
        public void BusinessNewSameContentBlob()
        {
            Blob = new byte[] { 1, 2, 3 };
        }

        /// <summary>同数组原地改元素（契约：不承诺自动标脏；需要显式 PushBased=false 轮询）。</summary>
        public void BusinessMutateBlobInPlace()
        {
            if (Blob != null && Blob.Length > 0)
            {
                Blob[0] = 99;
            }
        }

        /// <summary>同步属性的普通业务赋值（走被改写的 setter）。</summary>
        public void BusinessSetLocked(int value)
        {
            Locked = value;
        }

        // ---------------------------------------------------------------- 监视器探针（与复制属性同一个 this）

        /// <summary>持有监视器期间置位（探针线程侧等待）。</summary>
        public readonly ManualResetEvent LockHeld = new ManualResetEvent(false);

        /// <summary>放锁信号。</summary>
        public readonly ManualResetEvent LockRelease = new ManualResetEvent(false);

        /// <summary>
        /// 让**另一个线程**长时间持有 `this` 的监视器。
        ///
        /// 只有 `[MethodImpl(MethodImplOptions.Synchronized)]` 才会让运行时对 `this` 加锁，
        /// 因此本方法是「把锁拿在别人手里」的唯一手段：它一进入就持有监视器，
        /// 直到信号到达才返回。用它来观察「接收侧 RawSet 到底有没有走同一把锁」。
        /// </summary>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public void HoldMonitorUntilReleased()
        {
            LockHeld.Set();
            LockRelease.WaitOne(10000);
        }
    }

    /// <summary>
    /// 驱动：在**被加载进来的夹具程序集内部**跑完所有断言，只把结论（字符串）交回门禁。
    ///
    /// 反射只用于三处，且都是「调用生成物 / 读取元数据」，绝不反射写字段冒充普通赋值：
    ///   · 调用生成物的私有静态收包 Reader（`PMNet_Read_<P>`）；
    ///   · 调用生成物的私有静态 OnRep 分发（仅**隔离低层**用例；真实复制链见 RunReplication）；
    ///   · 读取 setter / RawSet 的 `MethodImplAttributes`（实现标志核对）。
    /// </summary>
    public static class PropertyDriver
    {
        /// <summary>未编织形态：版本必须为 0，`new` 与注册入口都必须被 guard 拒绝。</summary>
        public static string[] RunPreWeave()
        {
            List<string> lines = new List<string>();

            object version = typeof(PropFixture)
                .GetMethod("PMNet_GetRpcWeaveVersion", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);
            Add(lines, version != null && (int)version == 0, "preweave-version-zero",
                "PMNet_GetRpcWeaveVersion() = " + (version == null ? "<缺失>" : version.ToString()));

            bool newBlocked = false;
            string newDetail = null;
            try
            {
                PropFixture unused = new PropFixture();
                newDetail = "new 竟然成功（未编织程序集必须先被拒绝）";
            }
            catch (InvalidOperationException ex)
            {
                newBlocked = true;
                newDetail = "new 被拒：" + ex.Message;
            }
            catch (Exception ex)
            {
                newDetail = "new 抛了非 InvalidOperationException：" + ex.GetType().Name;
            }

            Add(lines, newBlocked, "preweave-new-blocked", newDetail);

            bool entryBlocked = false;
            string entryDetail = null;
            try
            {
                PropFixture.PMNet_BuildEntry();
                entryDetail = "PMNet_BuildEntry() 竟然成功（未编织程序集必须先被拒绝）";
            }
            catch (InvalidOperationException ex)
            {
                entryBlocked = true;
                entryDetail = "PMNet_BuildEntry 被拒：" + ex.Message;
            }
            catch (Exception ex)
            {
                entryDetail = "PMNet_BuildEntry 抛了非 InvalidOperationException：" + ex.GetType().Name;
            }

            Add(lines, entryBlocked, "preweave-buildentry-blocked", entryDetail);

            return lines.ToArray();
        }

        /// <summary>已编织形态的行为断言（含实现标志与监视器行为）。</summary>
        public static string[] RunWoven()
        {
            List<string> lines = new List<string>();
            string fatal = null;

            try
            {
                RunWovenCore(lines);
            }
            catch (Exception ex)
            {
                fatal = ex.GetType().Name + ": " + ex.Message;
            }

            if (fatal != null)
            {
                lines.Add("FAIL fatal :: " + fatal);
            }

            return lines.ToArray();
        }

        private static void RunWovenCore(List<string> lines)
        {
            // ── 1. 结构：stamp 必须变 1 ─────────────────────────────────────
            object version = typeof(PropFixture)
                .GetMethod("PMNet_GetRpcWeaveVersion", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);
            Add(lines, version != null && (int)version == 1, "stamp-is-one",
                "PMNet_GetRpcWeaveVersion() = " + (version == null ? "<缺失>" : version.ToString()));

            // ── 2. 初始化器：构造期原值保留，且构造不依赖标脏 ────────────────
            PropFixture fresh = new PropFixture();
            Add(lines, fresh.Inited == 42 && !fresh.Dirty.HasAny, "initializer-preserved",
                "Inited=" + fresh.Inited + " HasAnyDirty=" + fresh.Dirty.HasAny);

            // ── 3. 权威侧：普通赋值存值 + 标脏 ──────────────────────────────
            PropFixture authority = new PropFixture();
            authority.Role = PMNetRole.Authority;
            authority.BusinessSetHp(10);
            Add(lines,
                authority.Hp == 10 && authority.Dirty.IsDirty(PropFixture.PMGeneratedPropertyIndex_Hp),
                "authority-dirty",
                "Hp=" + authority.Hp + " dirty=" + authority.Dirty.IsDirty(PropFixture.PMGeneratedPropertyIndex_Hp));

            // ── 4. 相同值不标脏（only-if-changed）──────────────────────────
            authority.Dirty.Clear();
            authority.BusinessSetHp(10);
            Add(lines, authority.Hp == 10 && !authority.Dirty.HasAny, "authority-same-value-not-dirty",
                "Hp=" + authority.Hp + " HasAnyDirty=" + authority.Dirty.HasAny);

            // ── 5. 复合赋值同样走 setter ──────────────────────────────────
            authority.Dirty.Clear();
            authority.BusinessDamage(3);
            Add(lines,
                authority.Hp == 7 && authority.Dirty.IsDirty(PropFixture.PMGeneratedPropertyIndex_Hp),
                "compound-assignment-dirty",
                "Hp=" + authority.Hp + " HpSetCount=" + authority.HpSetCount);

            // ── 6. 客户端本地改自己副本：存值但不产生权威上行 ───────────────
            PropFixture client = new PropFixture();
            client.Role = PMNetRole.None;
            client.BusinessSetHp(99);
            Add(lines, client.Hp == 99 && !client.Dirty.HasAny, "client-not-dirty",
                "Hp=" + client.Hp + " HasAnyDirty=" + client.Dirty.HasAny);

            // ── 7. 槽位隔离：改一个属性不得标到另一个槽位 ────────────────────
            PropFixture isolate = new PropFixture();
            isolate.Role = PMNetRole.Authority;
            isolate.BusinessSetMana(5);
            isolate.Dirty.Clear();
            isolate.BusinessSetHp(123);
            Add(lines,
                isolate.Hp == 123 && isolate.Mana == 5
                && isolate.Dirty.IsDirty(PropFixture.PMGeneratedPropertyIndex_Hp)
                && !isolate.Dirty.IsDirty(PropFixture.PMGeneratedPropertyIndex_Mana),
                "rawset-correct-field-and-slot",
                "Hp=" + isolate.Hp + " Mana=" + isolate.Mana + " hpDirty="
                + isolate.Dirty.IsDirty(PropFixture.PMGeneratedPropertyIndex_Hp));

            // ── 8. PushBased=false：存值但 setter 不标脏 ────────────────────
            isolate.Dirty.Clear();
            isolate.BusinessSetPolled(7);
            Add(lines,
                isolate.Polled == 7 && !isolate.Dirty.IsDirty(PropFixture.PMGeneratedPropertyIndex_Polled),
                "pushbased-false-not-dirty",
                "Polled=" + isolate.Polled + " polledDirty="
                + isolate.Dirty.IsDirty(PropFixture.PMGeneratedPropertyIndex_Polled));

            // ── 9. 数组：整体引用替换标脏 ──────────────────────────────────
            PropFixture array = new PropFixture();
            array.Role = PMNetRole.Authority;
            array.BusinessSetBlob(new byte[] { 1, 2, 3 });
            bool wholeRefDirty = array.Dirty.IsDirty(PropFixture.PMGeneratedPropertyIndex_Blob);
            Add(lines, array.Blob != null && wholeRefDirty, "array-whole-ref-dirty",
                "len=" + (array.Blob == null ? -1 : array.Blob.Length) + " dirty=" + wholeRefDirty);

            // ── 10. 数组：新引用同内容可以标脏（线层抑制无效包）────────────
            array.Dirty.Clear();
            array.BusinessNewSameContentBlob();
            Add(lines, array.Dirty.IsDirty(PropFixture.PMGeneratedPropertyIndex_Blob),
                "array-new-ref-same-content-dirty",
                "dirty=" + array.Dirty.IsDirty(PropFixture.PMGeneratedPropertyIndex_Blob));

            // ── 11. 数组：同引用重赋不承诺自动标脏（实际为不标）──────────────
            array.Dirty.Clear();
            array.BusinessReassignSameBlob();
            Add(lines, !array.Dirty.HasAny, "array-same-ref-not-dirty",
                "HasAnyDirty=" + array.Dirty.HasAny);

            // ── 12. 数组：原地改元素不承诺自动标脏（实际为不标）──────────────
            array.Dirty.Clear();
            array.BusinessMutateBlobInPlace();
            Add(lines, array.Blob[0] == 99 && !array.Dirty.HasAny, "array-inplace-not-dirty",
                "elem0=" + array.Blob[0] + " HasAnyDirty=" + array.Dirty.HasAny);

            // ── 13. 收包 Reader 必须只走 RawSet：存值 + 不标脏 ──────────────
            PropFixture source = new PropFixture();
            source.Role = PMNetRole.None;
            source.BusinessSetHp(777);

            PMNetWriter writer = new PMNetWriter();
            PrivateStatic("PMNet_Write_Hp").Invoke(null, new object[] { source, writer });
            byte[] payload = writer.ToArray();

            PropFixture receiver = new PropFixture();
            receiver.Role = PMNetRole.Authority; // 就算在权威端，收包也不得反向标脏
            receiver.BusinessSetHp(1);
            receiver.Dirty.Clear();

            PMNetReader reader = new PMNetReader(payload, 0, payload.Length);
            PrivateStatic("PMNet_Read_Hp").Invoke(null, new object[] { receiver, reader });
            Add(lines, receiver.Hp == 777 && !receiver.Dirty.HasAny, "reader-apply-not-dirty",
                "Hp=" + receiver.Hp + " HasAnyDirty=" + receiver.Dirty.HasAny
                + "（payload " + payload.Length + " 字节）");

            // ── 14. OnRep：正常的 setter / 收包都不得直接通知 ───────────────
            Add(lines, authority.OnRepCount == 0 && receiver.OnRepCount == 0, "setter-and-reader-no-onrep",
                "authority.OnRepCount=" + authority.OnRepCount + " receiver.OnRepCount=" + receiver.OnRepCount);

            // ── 15. OnRep：由分发入口调用并**恰好一次** ──────────────────────
            //       这一条是**隔离低层**用例（直接调生成物的分发表入口）；
            //       「真复制层提交后恰好一次」由 RunReplication 的真实链覆盖。
            PrivateStatic("PMNet_OnRepDispatch").Invoke(null, new object[] { receiver, (ushort)1 });
            Add(lines, receiver.OnRepCount == 1, "onrep-dispatch-once",
                "receiver.OnRepCount=" + receiver.OnRepCount);

            // ── 16. 生成物的旧 public 访问器只转发赋值（不再二次 Mark）──────
            PropFixture legacy = new PropFixture();
            legacy.Role = PMNetRole.Authority;
            legacy.PMNet_SetHp(55);
            Add(lines,
                legacy.Hp == 55 && legacy.Dirty.IsDirty(PropFixture.PMGeneratedPropertyIndex_Hp),
                "legacy-setter-forwards-and-pushes",
                "Hp=" + legacy.Hp + " dirty=" + legacy.Dirty.IsDirty(PropFixture.PMGeneratedPropertyIndex_Hp));

            // ── 17. OwnerOnly / PushBased 条件必须与注册表一致 ─────────────
            PMRepList list = legacy.GetLifetimeReplicatedProps();
            PMLifetimeProperty ownerOnly;
            PMLifetimeProperty pushOff;
            PMLifetimeProperty locked;
            bool hasOwnerOnly = list.TryGet(PropFixture.PMGeneratedPropertyIndex_Mana, out ownerOnly);
            bool hasPushOff = list.TryGet(PropFixture.PMGeneratedPropertyIndex_Polled, out pushOff);
            bool hasLocked = list.TryGet(PropFixture.PMGeneratedPropertyIndex_Locked, out locked);
            Add(lines,
                list.Count == 6
                && hasOwnerOnly && ownerOnly.Condition == PMCond.OwnerOnly && ownerOnly.PushBased
                && hasPushOff && pushOff.Condition == PMCond.None && !pushOff.PushBased
                && hasLocked && locked.Condition == PMCond.None && locked.PushBased,
                "registration-conditions",
                "Count=" + list.Count
                + " Mana.Cond=" + (hasOwnerOnly ? ownerOnly.Condition.ToString() : "<缺>")
                + " Polled.Push=" + (hasPushOff ? pushOff.PushBased.ToString() : "<缺>")
                + " Locked.Cond=" + (hasLocked ? locked.Condition.ToString() : "<缺>"));

            // ── 18. 原 setter 的 [MethodImpl] 标志必须保留，且 RawSet 必须继承 ──
            RunImplementationFlagProbes(lines);

            // ── 19. 行为证据：接收侧写入口必须与同步 setter 共用同一把监视器锁 ──
            RunMonitorContentionProbes(lines);
        }

        // =================================================================================
        //  实现标志与锁行为（RawSet 必须继承原 setter 的 MethodImpl）
        // =================================================================================

        /// <summary>
        /// 反射核对：原 setter 的实现标志被保留，且 RawSet（收包路径的唯一写入口）**继承同一批标志**。
        ///
        /// 契约依据（net-property-authoring-contract.md §2）：
        /// `RawSet stub 改为 this,value→stfld &lt;P&gt;k__BackingField→ret（保留 setter MethodImpl 标志，
        /// 必要同步标志不能丢）`。
        ///
        /// 为什么这是真缺陷而不只是形式要求：`[MethodImpl(Synchronized)]` 的实例方法由运行时对
        /// `this` 加监视器锁。编织后本地赋值与收包写是两条路径（setter / RawSet），
        /// RawSet 不带 Synchronized 就等于「收包写不受锁保护」——与本地赋值并发时是数据竞争。
        /// </summary>
        private static void RunImplementationFlagProbes(List<string> lines)
        {
            PropertyInfo property = typeof(PropFixture).GetProperty("Locked");
            MethodInfo setter = property.GetSetMethod(true);
            MethodInfo rawSet = typeof(PropFixture)
                .GetMethod("PMNet_PropertyRawSet_Locked", BindingFlags.NonPublic | BindingFlags.Instance);

            MethodImplAttributes setterFlags = setter.GetMethodImplementationFlags();
            MethodImplAttributes rawSetFlags = rawSet.GetMethodImplementationFlags();

            Add(lines,
                (setterFlags & MethodImplAttributes.Synchronized) != 0,
                "locked-setter-impl-flags-preserved",
                "set_Locked ImplAttributes=" + ((int)setterFlags).ToString()
                + "（期望含 Synchronized=" + ((int)MethodImplAttributes.Synchronized).ToString() + "）");

            Add(lines,
                rawSetFlags == setterFlags && (rawSetFlags & MethodImplAttributes.Synchronized) != 0,
                "rawset-inherits-setter-impl-flags",
                "PMNet_PropertyRawSet_Locked ImplAttributes=" + ((int)rawSetFlags).ToString()
                + "，set_Locked ImplAttributes=" + ((int)setterFlags).ToString()
                + "（必须逐位相同，否则收包写丢锁）");

            // 对照：没有 Synchronized 的属性，其 RawSet 也不该带 Synchronized
            // （防止"给所有 RawSet 都硬塞 Synchronized"这种把断言蒙过去的做法）。
            MethodInfo plainRawSet = typeof(PropFixture)
                .GetMethod("PMNet_PropertyRawSet_Hp", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodImplAttributes plainFlags = plainRawSet.GetMethodImplementationFlags();
            Add(lines,
                (plainFlags & MethodImplAttributes.Synchronized) == 0,
                "plain-rawset-not-synchronized",
                "PMNet_PropertyRawSet_Hp ImplAttributes=" + ((int)plainFlags).ToString()
                + "（无 [MethodImpl] 的属性不得被强行加锁）");
        }

        /// <summary>
        /// 行为证据：让**另一个线程**通过 `HoldMonitorUntilReleased`（Synchronized）持有 `this` 的
        /// 监视器，然后观察三条路径是否被阻塞：
        ///   · `PMNet_Read_Locked`（收包路径 → RawSet，必须阻塞）；
        ///   · `BusinessSetLocked`（本地 setter 路径，必须阻塞）；
        ///   · `PMNet_Read_Hp`（无 Synchronized 的属性，负向对照：必须**不**阻塞）。
        ///
        /// 为什么必须做行为验证：反射标志只证明"元数据里写着 Synchronized"，
        /// 不能证明运行期真的对 `this` 加锁。持锁期间 `Monitor.TryEnter(this, 0)` 必须失败
        /// （`monitor-hold-observable`）—— 这一条同时是上面三个观察的**前置条件校验**：
        /// 若它失败，说明锁根本没被拿住，后面三条观察都不成立。
        /// </summary>
        private static void RunMonitorContentionProbes(List<string> lines)
        {
            PropFixture target = new PropFixture();
            target.Role = PMNetRole.Authority;
            target.BusinessSetLocked(1); // 预热（也证明同步 setter 自己可以正常工作）

            Thread holder = new Thread(delegate () { target.HoldMonitorUntilReleased(); });
            holder.IsBackground = true;
            holder.Start();

            bool held = target.LockHeld.WaitOne(5000);
            bool monitorAcquired = Monitor.TryEnter(target, 0);
            bool monitorHeld = held && !monitorAcquired;
            if (monitorAcquired)
            {
                // 只有真的拿到锁才需要放（拿不到说明锁确实在持有线程手里）。
                Monitor.Exit(target);
            }

            Add(lines, monitorHeld, "monitor-hold-observable",
                "另一线程持锁可观测（LockHeld=" + held + "，TryEnter(0) 拿到锁=" + monitorAcquired
                + "；必须是 True/False）");

            bool lockedReaderBlocked = !CompletesWhileMonitorHeld(ReadProbe(target, "PMNet_Read_Locked"), 750);
            bool setterBlocked = !CompletesWhileMonitorHeld(SetterProbe(target), 750);
            bool plainReaderBlocked = !CompletesWhileMonitorHeld(ReadProbe(target, "PMNet_Read_Hp"), 750);

            target.LockRelease.Set();
            holder.Join(5000);
            Add(lines, !Monitor.IsEntered(target), "monitor-released-after-probe",
                "放锁后当前线程持有监视器=" + Monitor.IsEntered(target) + "（必须为 false）");

            Add(lines, lockedReaderBlocked, "rawset-locked-blocks-while-monitor-held",
                "持锁期间 PMNet_Read_Locked（→ RawSet）是否被阻塞=" + lockedReaderBlocked
                + "（必须为 true：收包写要与同步 setter 共用同一把锁）");

            Add(lines, setterBlocked, "locked-setter-shares-monitor",
                "持锁期间 BusinessSetLocked（→ setter）是否被阻塞=" + setterBlocked + "（必须为 true）");

            Add(lines, !plainReaderBlocked, "rawset-unlocked-not-blocked",
                "持锁期间 PMNet_Read_Hp（→ 无 Synchronized 的 RawSet）是否被阻塞=" + plainReaderBlocked
                + "（负向对照，必须为 false：观察手段能区分有无同步标志）");
        }

        private static Func<object> ReadProbe(PropFixture target, string readerName)
        {
            MethodInfo read = typeof(PropFixture).GetMethod(
                readerName, BindingFlags.NonPublic | BindingFlags.Static);
            if (read == null)
            {
                throw new MissingMethodException("夹具缺少生成物 Reader " + readerName);
            }

            // 目标对象在这里**捕获**（而不是在委托执行时读静态字段）：被阻塞的探针线程
            // 会在放锁之后才真正执行，那时静态字段可能已经变了。
            return delegate ()
            {
                PMNetWriter writer = new PMNetWriter(8);
                writer.WriteInt32(7);
                byte[] payload = writer.ToArray();
                return read.Invoke(null, new object[] { target, new PMNetReader(payload, 0, payload.Length) });
            };
        }

        private static Func<object> SetterProbe(PropFixture target)
        {
            return delegate ()
            {
                target.BusinessSetLocked(7);
                return null;
            };
        }

        /// <summary>
        /// 在**另一个线程**上执行 <paramref name="action"/>，返回它是否在 <paramref name="waitMs"/> 内完成。
        /// 调用方必须在持有监视器期间调用（否则只测到"线程调度快慢"）。
        /// 未完成的探针线程会在放锁后自然结束，这里不等它（它已经不在锁内，不影响后续观察）。
        /// </summary>
        private static bool CompletesWhileMonitorHeld(Func<object> action, int waitMs)
        {
            bool[] done = new bool[1];
            Exception[] error = new Exception[1];
            Thread worker = new Thread(delegate ()
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error[0] = ex;
                }

                done[0] = true;
            });

            worker.IsBackground = true;
            worker.Start();
            worker.Join(waitMs);

            bool finished = done[0];
            if (error[0] != null)
            {
                throw new InvalidOperationException("探针线程抛出：" + error[0].GetType().Name + " " + error[0].Message);
            }

            return finished;
        }

        // =================================================================================
        //  真实复制链：PMNetWorld（生命周期 / 工厂 / 世界）+ PMReplicationChannel（字节 / ACK / 条件）
        // =================================================================================

        /// <summary>
        /// 真实复制链断言（独立入口，由门禁在**已编织**程序集上调用）。
        ///
        /// 为什么必须有这一层：`onrep-dispatch-once` 这类隔离用例只能证明「分发表入口被调用时恰好一次」，
        /// 不能证明「复制层提交后恰好一次」，更不能证明字节真的过了 PMReplicationChannel。
        /// 这里用生产注册表 + 真实世界 + 真实通道，走
        /// `Tick → Send(字节) → OnMessage(解码/暂存/提交) → BuildAckMessage → 发送侧 OnMessage(ACK)`
        /// 的完整回路。
        /// </summary>
        public static string[] RunReplication()
        {
            List<string> lines = new List<string>();
            string fatal = null;

            try
            {
                RunReplicationCore(lines);
            }
            catch (Exception ex)
            {
                fatal = ex.GetType().Name + ": " + ex.Message;
            }

            if (fatal != null)
            {
                lines.Add("FAIL fatal :: " + fatal);
            }

            return lines.ToArray();
        }

        /// <summary>连接替身：把发出的载荷收集起来，便于按真实字节投递（与 PMReplicationTest 同形）。</summary>
        private sealed class TestConnection : PMNetConnection
        {
            public readonly List<byte[]> Sent = new List<byte[]>();

            public TestConnection(int connectionId, bool serverSide)
                : base(connectionId, serverSide)
            {
            }

            public override bool IsReady { get { return true; } }

            public override void Send(byte[] payload, PMRpcReliability reliability)
            {
                Sent.Add(payload);
            }
        }

        /// <summary>一条客户端副本链路（连接 + 世界 + 复制通道 + 本地副本）。</summary>
        private sealed class Replica
        {
            public int ConnectionId;
            public bool IsOwner;
            public TestConnection Conn;
            public PMNetWorld World;
            public PMReplicationChannel Channel;
            public PropFixture Copy;
        }

        private static void RunReplicationCore(List<string> lines)
        {
            // 生产注册路径：显式的生成注册表（未编织程序集在这里就被 guard 拒绝）。
            global::PMNet.Generated.PMNetGeneratedRegistry.RegisterAll();

            uint classId = PropFixture.PMGeneratedClassId;

            PMNetWorld serverWorld = new PMNetWorld(new PMSession(11u, true));
            serverWorld.RegisterClass(classId, delegate () { return new PropFixture(); });

            PropFixture server = new PropFixture();
            if (!serverWorld.Spawn(server, classId))
            {
                throw new InvalidOperationException("服务端 Spawn 失败（对象状态 / 容量）");
            }

            // 初值全部走**业务普通 C# 赋值**（服务端世界 ⇒ Role=Authority ⇒ 自动标脏）。
            server.BusinessSetHp(100);
            server.BusinessSetMana(50);
            server.BusinessSetPolled(7);
            server.BusinessSetBlob(new byte[] { 1, 2, 3 });
            // Inited = 42 来自初始化器（初始全量不依赖标脏）

            PMReplicationChannel sender = new PMReplicationChannel(new PMRepOptions());
            sender.RegisterObject(server);
            sender.RegisterOnRepDispatcher(classId, PropFixture.PMDispatchOnRep);

            Replica owner = AddReplica(serverWorld, sender, classId, 9001, true);
            Replica observer = AddReplica(serverWorld, sender, classId, 9002, false);

            // OwnerOnly 可见性走**真实决策点**（ViewRoleResolver）：拥有者 = Autonomous。
            int ownerConnectionId = owner.ConnectionId;
            sender.ViewRoleResolver = delegate (PMNetObject o, PMNetConnection c)
            {
                return c.ConnectionId == ownerConnectionId
                    ? PMRepViewRole.Autonomous
                    : PMRepViewRole.Simulated;
            };

            DeliverLifecycle(serverWorld, owner);
            DeliverLifecycle(serverWorld, observer);
            owner.Copy = FindCopy(owner, server);
            observer.Copy = FindCopy(observer, server);

            // ── 第 1 轮：初始全量（基线缺失 ⇒ 每个可见槽位都发）──────────────
            sender.Tick();
            List<int> ownerSlots = CollectSlots(owner.Conn);
            List<int> observerSlots = CollectSlots(observer.Conn);

            int appliedOwner = ApplyAndAck(owner, sender);
            int appliedObserver = ApplyAndAck(observer, sender);

            int hp = PropFixture.PMGeneratedPropertyIndex_Hp;
            int mana = PropFixture.PMGeneratedPropertyIndex_Mana;
            int polled = PropFixture.PMGeneratedPropertyIndex_Polled;
            int blob = PropFixture.PMGeneratedPropertyIndex_Blob;
            int inited = PropFixture.PMGeneratedPropertyIndex_Inited;

            Add(lines,
                appliedOwner >= 1 && appliedObserver >= 1
                && owner.Copy != null && owner.Copy.Hp == 100 && owner.Copy.Mana == 50
                && owner.Copy.Polled == 7 && owner.Copy.Blob != null && owner.Copy.Blob.Length == 3
                && owner.Copy.Inited == 42,
                "rep-initial-full-state",
                "applied(owner/observer)=" + appliedOwner + "/" + appliedObserver
                + " Hp=" + Show(owner.Copy, "Hp") + " Mana=" + Show(owner.Copy, "Mana")
                + " Polled=" + Show(owner.Copy, "Polled") + " Inited=" + Show(owner.Copy, "Inited"));

            Add(lines,
                ownerSlots.Contains(hp) && ownerSlots.Contains(mana) && ownerSlots.Contains(polled)
                && ownerSlots.Contains(blob) && ownerSlots.Contains(inited),
                "rep-owner-only-sent-to-owner",
                "拥有者连接收到的槽位=" + Describe(ownerSlots));

            Add(lines,
                !observerSlots.Contains(mana) && observerSlots.Contains(hp)
                && observer.Copy != null && observer.Copy.Mana == 0 && observer.Copy.Hp == 100,
                "rep-owner-only-not-sent-to-nonowner",
                "非拥有者连接收到的槽位=" + Describe(observerSlots)
                + " Mana=" + Show(observer.Copy, "Mana"));

            // Reader 把值写进活对象时必须**不反向标脏**（收包不是本地业务赋值）。
            Add(lines,
                owner.Copy != null && !owner.Copy.Dirty.HasAny
                && observer.Copy != null && !observer.Copy.Dirty.HasAny,
                "rep-reader-apply-not-dirty",
                "owner.Dirty=" + (owner.Copy == null ? "<null>" : owner.Copy.Dirty.HasAny.ToString())
                + " observer.Dirty=" + (observer.Copy == null ? "<null>" : observer.Copy.Dirty.HasAny.ToString()));

            // 真实复制层提交后 OnRep 恰好一次（不是反射直接调分发表）。
            Add(lines, owner.Copy != null && owner.Copy.OnRepCount == 1, "rep-onrep-dispatched-once",
                "owner.Copy.OnRepCount=" + Show(owner.Copy, "OnRepCount")
                + "（Hp 有 OnRep，本轮只发一次）");

            // ── 第 2 轮：没有任何变化 ⇒ 一条记录都不发 ───────────────────────
            sender.Tick();
            Add(lines, owner.Conn.Sent.Count == 0 && observer.Conn.Sent.Count == 0,
                "rep-no-repeat-without-change",
                "owner/observer 待发消息数=" + owner.Conn.Sent.Count + "/" + observer.Conn.Sent.Count);

            // ── ACK 真的推进了基线 ─────────────────────────────────────────
            byte[] hpBaseline;
            bool hasBaseline = sender.TryGetBaselineValue(
                owner.Conn, server.NetId.Value, hp, out hpBaseline);
            Add(lines, hasBaseline && hpBaseline != null, "rep-ack-advances-baseline",
                "Hp 基线是否已建立=" + hasBaseline
                + "（字节数=" + (hpBaseline == null ? -1 : hpBaseline.Length) + "）");

            // ── 第 3 轮：ACK 之后再改一次 ⇒ 必须重新发 ───────────────────────
            server.BusinessSetHp(101);
            sender.Tick();
            List<int> afterAckSlots = CollectSlots(owner.Conn);
            ApplyAndAck(owner, sender);
            ApplyAndAck(observer, sender);

            Add(lines,
                afterAckSlots.Contains(hp) && owner.Copy != null && owner.Copy.Hp == 101
                && owner.Copy.OnRepCount == 2,
                "rep-change-after-ack-resent",
                "本轮槽位=" + Describe(afterAckSlots) + " Hp=" + Show(owner.Copy, "Hp")
                + " OnRepCount=" + Show(owner.Copy, "OnRepCount"));

            // ── 第 4 轮：PushBased=false 的属性在脏位为空时仍被轮询发出 ──────
            server.Dirty.Clear();
            server.BusinessSetPolled(9);
            bool polledDirty = server.Dirty.IsDirty(polled);
            sender.Tick();
            List<int> pollSlots = CollectSlots(owner.Conn);
            ApplyAndAck(owner, sender);
            ApplyAndAck(observer, sender);

            Add(lines,
                !polledDirty && pollSlots.Contains(polled) && !pollSlots.Contains(hp)
                && owner.Copy != null && owner.Copy.Polled == 9,
                "rep-poll-after-ack-without-dirty",
                "本属性脏位=" + polledDirty + " 本轮槽位=" + Describe(pollSlots)
                + " Polled=" + Show(owner.Copy, "Polled")
                + "（轮询必须发 Polled，且未被改动的 Hp 必须仍被基线比较抑制）");
        }

        private static Replica AddReplica(
            PMNetWorld serverWorld,
            PMReplicationChannel sender,
            uint classId,
            int connectionId,
            bool isOwner)
        {
            Replica replica = new Replica();
            replica.ConnectionId = connectionId;
            replica.IsOwner = isOwner;
            replica.Conn = new TestConnection(connectionId, true);
            replica.World = new PMNetWorld(new PMSession(11u, false));
            replica.World.RegisterClass(classId, delegate () { return new PropFixture(); });

            serverWorld.AddConnection(replica.Conn);
            sender.AddConnection(replica.Conn);

            replica.Channel = new PMReplicationChannel(new PMRepOptions());
            replica.Channel.World = replica.World;
            replica.Channel.RegisterOnRepDispatcher(classId, PropFixture.PMDispatchOnRep);
            return replica;
        }

        private static void DeliverLifecycle(PMNetWorld serverWorld, Replica replica)
        {
            byte[] batch = serverWorld.BuildLifecycleBatch(replica.Conn);
            if (batch != null && batch.Length > 0)
            {
                replica.World.OnLifecycleMessage(batch, 0, batch.Length);
            }
        }

        private static PropFixture FindCopy(Replica replica, PropFixture server)
        {
            PMNetObject found;
            if (!replica.World.TryFind(server.NetId, out found) || found == null)
            {
                throw new InvalidOperationException(
                    "客户端世界没有创建出 " + server.NetId + " 的副本（生命周期路径有问题）");
            }

            return (PropFixture)found;
        }

        /// <summary>把该连接待发的载荷按真实字节投递给接收通道，再把它产生的 ACK 回灌给发送侧。</summary>
        private static int ApplyAndAck(Replica replica, PMReplicationChannel sender)
        {
            int applied = 0;
            List<byte[]> sent = replica.Conn.Sent;
            for (int i = 0; i < sent.Count; i++)
            {
                applied += replica.Channel.OnMessage(replica.Conn, sent[i], 0, sent[i].Length);
            }

            sent.Clear();

            byte[] ack = replica.Channel.BuildAckMessage(replica.Conn);
            if (ack != null)
            {
                sender.OnMessage(replica.Conn, ack, 0, ack.Length);
            }

            return applied;
        }

        private static List<int> CollectSlots(TestConnection conn)
        {
            List<int> slots = new List<int>();
            for (int i = 0; i < conn.Sent.Count; i++)
            {
                PMRepMessage message = new PMRepMessage();
                string error;
                if (!PMReplicationReader.TryRead(new PMNetReader(conn.Sent[i]), message, out error))
                {
                    throw new InvalidOperationException("复制载荷解码失败：" + error);
                }

                for (int r = 0; r < message.Updates.Count; r++)
                {
                    for (int k = 0; k < message.Updates[r].Slots.Length; k++)
                    {
                        int slot = message.Updates[r].Slots[k];
                        if (!slots.Contains(slot))
                        {
                            slots.Add(slot);
                        }
                    }
                }
            }

            return slots;
        }

        private static string Describe(List<int> slots)
        {
            List<string> parts = new List<string>();
            for (int i = 0; i < slots.Count; i++)
            {
                parts.Add(slots[i].ToString());
            }

            return "[" + string.Join(",", parts.ToArray()) + "]";
        }

        private static string Show(PropFixture fixture, string member)
        {
            if (fixture == null)
            {
                return "<null 副本>";
            }

            switch (member)
            {
                case "Hp": return fixture.Hp.ToString();
                case "Mana": return fixture.Mana.ToString();
                case "Polled": return fixture.Polled.ToString();
                case "Inited": return fixture.Inited.ToString();
                case "OnRepCount": return fixture.OnRepCount.ToString();
                default: return "<未知成员>";
            }
        }

        private static MethodInfo PrivateStatic(string name)
        {
            MethodInfo method = typeof(PropFixture).GetMethod(
                name, BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                throw new MissingMethodException("夹具缺少私有静态方法 " + name);
            }

            return method;
        }

        private static void Add(List<string> lines, bool ok, string name, string detail)
        {
            lines.Add((ok ? "PASS " : "FAIL ") + name + " :: " + detail);
        }
    }
}
// <<<PROPERTY-ONLY-REGION<<<

namespace PMWeave.Rpc
{
    /// <summary>
    /// 与 auto-property **同程序集**的真实 RPC 夹具：证明 RPC 与属性在同一次
    /// 预检 / stamp / 原子落盘里一起被编织（本类由 PMNetGen 真实生成产物）。
    /// </summary>
    [PMNetworkObject]
    public partial class RpcFixture : PMNetObject
    {
        /// <summary>业务体被执行的次数。</summary>
        public int PingCount;

        /// <summary>最近一次收到的 token。</summary>
        public int PingToken = int.MinValue;

        /// <summary>上行 RPC：原生 Validate 档位（负数 ⇒ 请求断连）。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.Validate)]
        public void ServerPing(int token)
        {
            PingCount++;
            PingToken = token;
        }

        /// <summary>原生校验同伴。</summary>
        private bool ServerPing_Validate(int token)
        {
            return token >= 0;
        }
    }
}

namespace PMWeave.Mixed
{
    /// <summary>
    /// **同一个类**里同时有 RPC 与 auto-property 的夹具。
    ///
    /// 为什么必须有它：分成两个类只能证明「同一个程序集里 RPC 类与属性类各自被处理」，
    /// 不能证明「同一个类在一次预检后用**同一个 stamp** 同时织好 RPC 与属性」。
    /// 这里的两条断言（版本门 = 1 且私有 RPC 业务体存在、属性赋值真的标脏）只有
    /// 「同一轮里两者都被织」才能同时成立；门禁里的失败零写反例也打在**这个类**的
    /// 生成物上（证明属性侧失败不会留下"RPC 已织、属性没织"的半成品）。
    /// </summary>
    [PMNetworkObject]
    public partial class MixedFixture : PMNetObject
    {
        /// <summary>RPC 业务体被执行的次数。</summary>
        public int RpcCount;

        /// <summary>RPC 收到的 token。</summary>
        public int RpcToken = int.MinValue;

        /// <summary>普通 Push auto-property。</summary>
        [PMReplicated]
        public int Value { get; private set; }

        /// <summary>OwnerOnly auto-property。</summary>
        [PMReplicated(PMCond.OwnerOnly)]
        public int OwnerValue { get; private set; }

        /// <summary>与属性同类的上行 RPC。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.Validate)]
        public void ServerMixed(int token)
        {
            RpcCount++;
            RpcToken = token;
            Value = token;
        }

        /// <summary>原生校验同伴。</summary>
        private bool ServerMixed_Validate(int token)
        {
            return token >= 0;
        }

        public void BusinessSetValue(int value)
        {
            Value = value;
        }

        public void BusinessSetOwnerValue(int value)
        {
            OwnerValue = value;
        }
    }

    /// <summary>
    /// 混合类的探针（**刻意放在属性区块之外**）：它只存在于混合装配里，
    /// 因此「纯属性零 RPC」场景不会引用到 RPC 侧的任何东西。
    /// </summary>
    public static class MixedDriver
    {
        /// <summary>已编织形态：同一个类里 RPC 与属性必须同时被织好。</summary>
        public static string[] RunMixedClassProbe()
        {
            List<string> lines = new List<string>();

            object version = typeof(MixedFixture)
                .GetMethod("PMNet_GetRpcWeaveVersion", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);
            lines.Add(Line(version != null && (int)version == 1, "mixed-stamp-is-one",
                "PMNet_GetRpcWeaveVersion() = " + (version == null ? "<缺失>" : version.ToString())));

            MethodInfo body = typeof(MixedFixture).GetMethod(
                "PMNet_RpcBody_ServerMixed", BindingFlags.NonPublic | BindingFlags.Instance);
            lines.Add(Line(body != null, "mixed-rpc-body-woven",
                "私有业务体 PMNet_RpcBody_ServerMixed 存在=" + (body != null)
                + "（RPC 入口已被改写为 wrapper）"));

            MixedFixture fixture = new MixedFixture();
            fixture.Role = PMNetRole.Authority;
            fixture.Dirty.Clear();
            fixture.BusinessSetValue(7);
            lines.Add(Line(fixture.Value == 7 && fixture.Dirty.IsDirty(MixedFixture.PMGeneratedPropertyIndex_Value),
                "mixed-property-woven-same-pass",
                "Value=" + fixture.Value + " dirty="
                + fixture.Dirty.IsDirty(MixedFixture.PMGeneratedPropertyIndex_Value)
                + "（与 RPC 同一次 stamp 的属性侧）"));

            return lines.ToArray();
        }

        private static string Line(bool ok, string name, string detail)
        {
            return (ok ? "PASS " : "FAIL ") + name + " :: " + detail;
        }
    }
}
