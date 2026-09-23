// R2 声明门禁的**非法**测试夹具（Tools/PMDeclCheck）。
//
// 契约 §5 的 12 条规则里，规则 1–9 与 11 都能在**源码层**构造出非法声明，
// 因此它们各在这里有至少一个用例（本文件只被语法解析，不需要能被编译）。
//
// 规则 10 与 12 不在这里：它们判的是 IR 级不变量（ID 唯一性、掩码区间），
// 而 ID 与掩码区间都是生成器自己分配的，源码层几乎构不出非法值。
// 门禁对这两条改用"注入一个手工损坏的 PMDeclModel"来验证校验器真的会拦住它
// （见 PMDeclCheck/Program.cs 的 CheckInjectedModel）。
//
// 唯一例外：规则 10 的"同一成员拆在两个 partial 部分里各声明一次"可以在源码层构造
// （两个部分稳定键相同 ⇒ PropertyId 相同），因此这里也放了一个。

using System;
using System.Collections.Generic;
using PMNet;

namespace PMNetBadFixtures
{
    /// <summary>普通基类（不是网络对象），用于构造"推不到 PMNetObject"的用例。</summary>
    public class PlainBase
    {
    }

    /// <summary>普通业务类，用于承载"声明在非网络类上"的用例。</summary>
    public class PlainHost
    {
        /// <summary>规则 7 附带检查：[PMServerRpc] 不在 [PMNetworkObject] 类里，会被完全忽略。</summary>
        [PMServerRpc(Validator = PMRpcValidator.ForceValidate)]
        public void RpcOnPlainClass()
        {
        }
    }

    /// <summary>被 [PMNetworkStruct] 标记的类型（契约 §6：R2 不实现，声明即报错）。</summary>
    [PMNetworkStruct]
    public struct BadNetStruct
    {
        /// <summary>字段。</summary>
        public int X;
    }

    // =========================================================================================
    //  规则 1：[PMNetworkObject] 的类必须是 partial 且能推到 PMNetObject
    // =========================================================================================

    /// <summary>规则 1 用例 a：不是 partial。</summary>
    [PMNetworkObject]
    public class BadNotPartial : PMNetObject
    {
        /// <summary>复制成员。</summary>
        [PMReplicated]
        public int Hp;
    }

    /// <summary>规则 1 用例 b：是 partial，但基类推不到 PMNetObject。</summary>
    [PMNetworkObject]
    public partial class BadWrongBase : PlainBase
    {
    }

    /// <summary>规则 1 用例 c：[PMNetworkObject] 标在 struct 上（无法继承 PMNetObject）。</summary>
    [PMNetworkObject]
    public partial struct BadStructHost
    {
    }

    // =========================================================================================
    //  规则 2：不得是泛型类
    // =========================================================================================

    /// <summary>规则 2 用例：泛型网络类。</summary>
    [PMNetworkObject]
    public partial class BadGeneric<T> : PMNetObject
    {
    }

    // =========================================================================================
    //  规则 3：[PMReplicated] 不得是 static / const / readonly
    // =========================================================================================

    /// <summary>规则 3 用例：static / readonly / const 各一个。</summary>
    [PMNetworkObject]
    public partial class BadModifiers : PMNetObject
    {
        /// <summary>static。</summary>
        [PMReplicated]
        public static int StaticHp;

        /// <summary>readonly。</summary>
        [PMReplicated]
        public readonly int ReadOnlyHp;

        /// <summary>const。</summary>
        [PMReplicated]
        public const int ConstHp = 1;
    }

    // =========================================================================================
    //  规则 4：类型必须落在支持的类型集内（契约 §6）
    // =========================================================================================

    /// <summary>规则 4 用例：集合类型 / 网络结构 / char / RPC 参数类型 都不在类型集内。</summary>
    [PMNetworkObject]
    public partial class BadTypes : PMNetObject
    {
        /// <summary>泛型集合不在类型集内。</summary>
        [PMReplicated]
        public List<int> BadList;

        /// <summary>[PMNetworkStruct] 标记的类型：R2 不实现。</summary>
        [PMReplicated]
        public BadNetStruct BadStructField;

        /// <summary>char 不在类型集内。</summary>
        [PMReplicated]
        public char BadChar;

        /// <summary>RPC 参数类型也必须落在同类型集内（契约 §6 的"复制与 RPC 参数共用"）。</summary>
        [PMServerRpc(Validator = PMRpcValidator.ForceValidate)]
        public void BadParamRpc(List<string> items)
        {
        }

        /// <summary>规则 13 的配套同伴（让本条用例只暴露它本意要测的那条规则）。</summary>
        private PMNet.PMRpcValidation BadParamRpc_ForceValidate()
        {
            return PMNet.PMRpcValidation.Accept;
        }
    }

    // =========================================================================================
    //  规则 5 / 6：[PMRepNotify]
    // =========================================================================================

    /// <summary>规则 5 用例：ForMember 拼错（找不到对应的复制成员）。</summary>
    [PMNetworkObject]
    public partial class BadRepNotifyForMember : PMNetObject
    {
        /// <summary>复制成员。</summary>
        [PMReplicated]
        public int Hp;

        /// <summary>ForMember 拼错。</summary>
        [PMRepNotify("DoesNotExist")]
        public void OnRep_Missing()
        {
        }
    }

    /// <summary>规则 6 用例：带参 / 非 void。</summary>
    [PMNetworkObject]
    public partial class BadRepNotifySignature : PMNetObject
    {
        /// <summary>复制成员。</summary>
        [PMReplicated]
        public int Hp;

        /// <summary>带参。</summary>
        [PMRepNotify(nameof(Hp))]
        public void OnRep_HpWithParam(int oldValue)
        {
        }

        /// <summary>非 void。</summary>
        [PMRepNotify(nameof(Hp))]
        public int OnRep_HpNotVoid()
        {
            return 0;
        }
    }

    // =========================================================================================
    //  规则 7：RPC 必须返回 void、不得 static
    // =========================================================================================

    /// <summary>规则 7 用例：static / 非 void。</summary>
    [PMNetworkObject]
    public partial class BadRpcSignature : PMNetObject
    {
        /// <summary>static。</summary>
        [PMServerRpc(Validator = PMRpcValidator.ForceValidate)]
        public static void StaticRpc()
        {
        }

        /// <summary>规则 13 的配套同伴（让本条用例只暴露它本意要测的那条规则）。</summary>
        private PMNet.PMRpcValidation StaticRpc_ForceValidate()
        {
            return PMNet.PMRpcValidation.Accept;
        }

        /// <summary>非 void。</summary>
        [PMServerRpc(Validator = PMRpcValidator.ForceValidate)]
        public int NonVoidRpc()
        {
            return 0;
        }
    }

    // =========================================================================================
    //  规则 8：[PMServerRpc] 必须声明校验（项目反外挂红线）
    // =========================================================================================

    /// <summary>规则 8 用例：既没有 Validator 也没有 _ForceValidate 同伴。</summary>
    [PMNetworkObject]
    public partial class BadNoValidation : PMNetObject
    {
        /// <summary>红线用例。</summary>
        [PMServerRpc]
        public void NoValidatorRpc()
        {
        }

        /// <summary>合法对照：WithValidation = true 等价 Validator = Validate。</summary>
        [PMServerRpc(WithValidation = true)]
        public void OkWithValidationRpc()
        {
        }

        /// <summary>
        /// 规则 13 要求配套的同伴方法：`WithValidation = true` 等价 `Validate` 档位，
        /// 发射器会生成对 `&lt;M&gt;_Validate` 的调用；只声明档位不写同伴，
        /// 就会退化成「声称有校验、实际没有」（返回 false ⇒ 请求断连）。
        /// </summary>
        private bool OkWithValidationRpc_Validate()
        {
            return true;
        }

        /// <summary>合法对照：显式 ForceValidate。</summary>
        [PMServerRpc(Validator = PMRpcValidator.ForceValidate)]
        public void OkForceValidateRpc()
        {
        }

        /// <summary>规则 13 要求配套的同伴方法（ForceValidate 三态）。</summary>
        private PMNet.PMRpcValidation OkForceValidateRpc_ForceValidate()
        {
            return PMNet.PMRpcValidation.Accept;
        }
    }

    // =========================================================================================
    //  规则 9：WithValidation 与 Validator = ForceValidate 互斥
    // =========================================================================================

    /// <summary>规则 9 用例：两者同时写。</summary>
    [PMNetworkObject]
    public partial class BadValidatorConflict : PMNetObject
    {
        /// <summary>互斥用例。</summary>
        [PMServerRpc(WithValidation = true, Validator = PMRpcValidator.ForceValidate)]
        public void ConflictRpc()
        {
        }

        /// <summary>规则 13 的配套同伴（让本条用例只暴露它本意要测的那条规则）。</summary>
        private PMNet.PMRpcValidation ConflictRpc_ForceValidate()
        {
            return PMNet.PMRpcValidation.Accept;
        }
    }

    // =========================================================================================
    //  规则 10：同一类内 PropertyId / RpcId 不得重复
    // =========================================================================================

    /// <summary>规则 10 用例（第一段）：同一成员名。</summary>
    [PMNetworkObject]
    public partial class BadDuplicateMember : PMNetObject
    {
        /// <summary>与第二段重复。</summary>
        [PMReplicated]
        public int Hp;
    }

    /// <summary>规则 10 用例（第二段）：稳定键与第一段相同 ⇒ PropertyId 相同。</summary>
    [PMNetworkObject]
    public partial class BadDuplicateMember : PMNetObject
    {
        /// <summary>与第一段重复。</summary>
        [PMReplicated]
        public int Hp;
    }

    // =========================================================================================
    //  规则 11：条件必须是 D-R0-14 的 8 项之一
    // =========================================================================================

    /// <summary>规则 11 用例：未实现的 3 种写法（名字、命名参数、强制转换数值）。</summary>
    [PMNetworkObject]
    public partial class BadCondition : PMNetObject
    {
        /// <summary>未实现的成员名写法。</summary>
        [PMReplicated(PMCond.InitialOnly)]
        public int LegacyInitialOnly;

        /// <summary>未实现的命名参数写法。</summary>
        [PMReplicated(Condition = PMCond.NetGroup)]
        public int LegacyNetGroup;

        /// <summary>未实现的数值写法（1 = InitialOnly）。</summary>
        [PMReplicated((PMCond)1)]
        public int LegacyCasted;

        /// <summary>合法对照：8 项之一。</summary>
        [PMReplicated(PMCond.Never)]
        public int Fine;
    }

    // =========================================================================================
    //  规则 13：声明的校验档位必须有可调用的同伴方法
    // =========================================================================================

    /// <summary>
    /// 规则 13 用例：声明了 ForceValidate 档位，但没有 `_ForceValidate` 同伴。
    ///
    /// 为什么这必须报错而不是"退化成不校验"：发射器会为它生成对
    /// `&lt;M&gt;_ForceValidate` 的调用；同伴不存在时只剩两条路 ——
    /// 调用不存在的方法（编译失败，还算显式），或退化成
    /// 「**声称有校验、实际没有**」（描述符里写着 ForceValidate，反外挂链路上却什么都没有）。
    /// </summary>
    [PMNetworkObject]
    public partial class BadValidatorCompanionMissing : PMNetObject
    {
        /// <summary>声明了 ForceValidate，但没有 `MissingCompanion_ForceValidate`。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void MissingCompanion(int value)
        {
        }

        /// <summary>对照：这个**有**同伴，不应触发规则 13。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void HasCompanion(int value)
        {
        }

        private PMNet.PMRpcValidation HasCompanion_ForceValidate(int value)
        {
            return PMNet.PMRpcValidation.Accept;
        }
    }

    /// <summary>规则 13 用例（第二条）：声明 Validate 档位但没有 `_Validate` 同伴。</summary>
    [PMNetworkObject]
    public partial class BadNativeValidateCompanionMissing : PMNetObject
    {
        /// <summary>声明了 Validate（UE 原生语义：返回 false ⇒ 请求断连），但没有同伴。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.Validate)]
        public void NativeMissing(int value)
        {
        }
    }

    // =========================================================================================
    //  规则 7（补充）：编织器明确拒绝的方法形态（契约 net-rpc-weaving-contract.md §3）
    //
    //  为什么这些必须在**声明期**报错：这些形态在编译后的 IL 编织阶段必然失败，
    //  但那时生成物已经写出去、业务也已经照着它编译过了 ---- 失败点离声明点太远。
    //  因此把它们收成语法事实，由规则 7 在生成前拦下。
    //
    //  本文件只做**语法解析**（不需要能编译），因此 `void M();`、`extern`、
    //  `abstract` 这些语义不合法的写法在这里仍然是合法的输入。
    // =========================================================================================

    /// <summary>规则 7 补充：`virtual` / `abstract` / `extern` / `async` / 泛型 / 无体。</summary>
    [PMNetworkObject]
    public abstract partial class BadRpcWeaveShapes : PMNetObject
    {
        /// <summary>virtual：编织器不处理虚方法/网络继承。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public virtual void VirtualRpc(int value)
        {
        }

        private PMNet.PMRpcValidation VirtualRpc_ForceValidate(int value)
        {
            return PMNet.PMRpcValidation.Accept;
        }

        /// <summary>abstract：没有业务体可拆。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public abstract void AbstractRpc(int value);

        private PMNet.PMRpcValidation AbstractRpc_ForceValidate(int value)
        {
            return PMNet.PMRpcValidation.Accept;
        }

        /// <summary>extern/native：没有托管业务体可拆。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public extern void ExternRpc(int value);

        private PMNet.PMRpcValidation ExternRpc_ForceValidate(int value)
        {
            return PMNet.PMRpcValidation.Accept;
        }

        /// <summary>无体（既非 abstract 也非 extern）：`void M();` 这种写法拆不出业务体。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void NoBodyRpc(int value);

        private PMNet.PMRpcValidation NoBodyRpc_ForceValidate(int value)
        {
            return PMNet.PMRpcValidation.Accept;
        }
    }

    /// <summary>规则 7 补充：`async` / 泛型方法。</summary>
    [PMNetworkObject]
    public partial class BadRpcAsyncGeneric : PMNetObject
    {
        /// <summary>async：状态机会改写方法体。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public async void AsyncRpc(int value)
        {
        }

        private PMNet.PMRpcValidation AsyncRpc_ForceValidate(int value)
        {
            return PMNet.PMRpcValidation.Accept;
        }

        /// <summary>泛型方法。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void GenericRpc<T>()
        {
        }

        private PMNet.PMRpcValidation GenericRpc_ForceValidate<T>()
        {
            return PMNet.PMRpcValidation.Accept;
        }
    }

    /// <summary>规则 7 补充：同名重载 / 一个方法带多个 RPC 标记。</summary>
    [PMNetworkObject]
    public partial class BadRpcOverloadAndMultiAttr : PMNetObject
    {
        /// <summary>同名重载：RPC 方法名在类型里出现两次。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void OverloadedRpc(int value)
        {
        }

        private PMNet.PMRpcValidation OverloadedRpc_ForceValidate(int value)
        {
            return PMNet.PMRpcValidation.Accept;
        }

        /// <summary>与上面同名的普通重载（未标记，但足以让编织器拒绝）。</summary>
        public void OverloadedRpc(string other)
        {
        }

        /// <summary>一个方法带两个 RPC 标记：会生成两个同名 helper（直接编译失败）。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        [PMClientRpc(Reliability = PMRpcReliability.Reliable)]
        public void MultiAttrRpc(int value)
        {
        }

        private PMNet.PMRpcValidation MultiAttrRpc_ForceValidate(int value)
        {
            return PMNet.PMRpcValidation.Accept;
        }
    }

    /// <summary>规则 7 补充：`ref` / `out` / `in` / `params` / 默认值形参。</summary>
    [PMNetworkObject]
    public partial class BadRpcParamModifiers : PMNetObject
    {
        /// <summary>ref。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void RefRpc(ref int value)
        {
        }

        private PMNet.PMRpcValidation RefRpc_ForceValidate(ref int value)
        {
            return PMNet.PMRpcValidation.Accept;
        }

        /// <summary>out。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void OutRpc(out int value)
        {
            value = 0;
        }

        private PMNet.PMRpcValidation OutRpc_ForceValidate(out int value)
        {
            value = 0;
            return PMNet.PMRpcValidation.Accept;
        }

        /// <summary>in。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void InRpc(in int value)
        {
        }

        private PMNet.PMRpcValidation InRpc_ForceValidate(in int value)
        {
            return PMNet.PMRpcValidation.Accept;
        }

        /// <summary>params。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void ParamsRpc(params int[] values)
        {
        }

        private PMNet.PMRpcValidation ParamsRpc_ForceValidate(params int[] values)
        {
            return PMNet.PMRpcValidation.Accept;
        }

        /// <summary>默认值形参。</summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void DefaultRpc(int value = 3)
        {
        }

        private PMNet.PMRpcValidation DefaultRpc_ForceValidate(int value)
        {
            return PMNet.PMRpcValidation.Accept;
        }
    }

    // =========================================================================================
    //  规则 14：自动属性复制只支持普通实例 auto-property
    //
    //  契约 Docs/plans/net-property-authoring-contract.md §1：
    //    “新自动模式仅支持普通实例 auto-property（get;set;，允许访问性不同和初始化器）；
    //     拒绍自定义 getter/setter、只读、indexer、virtual/abstract/override、static、
    //     ref-return、显式接口等不支持形态。”
    //
    //  为什么这些必须在**声明期**报错：到编织期才发现、或生成器静默跳过，都会变成
    //  “写了标记、但完全不参与复制”的静默缺陷。
    //  本文件只做**语法解析**（不需要能编译），因此 ref-return / explicit interface 等
    //  写法在这里仍然是合法的输入。
    // =========================================================================================

    /// <summary>规则 14 用例：自定义 getter/setter、只读、表达式体、只写。</summary>
    [PMNetworkObject]
    public partial class BadPropShapes : PMNetObject
    {
        private int _backing;

        /// <summary>自定义 get + 自定义 set（不是 auto-property）。</summary>
        [PMReplicated]
        public int CustomAccessors
        {
            get { return _backing; }
            set { _backing = value; }
        }

        /// <summary>只读（自动 getter，没有 setter）：没有可改写的 setter。</summary>
        [PMReplicated]
        public int ReadOnlyProp { get; }

        /// <summary>表达式体属性：没有 auto-property 的 backing field。</summary>
        [PMReplicated]
        public int ExpressionBodiedProp => _backing;

        /// <summary>只写（没有 getter）：复制层的 Writer 读不到值。</summary>
        [PMReplicated]
        public int WriteOnlyProp
        {
            set { _backing = value; }
        }

        /// <summary>static 自动属性：规则 3 拦下（规则 14 不重复报）。</summary>
        [PMReplicated]
        public static int StaticProp { get; set; }
    }

    /// <summary>规则 14 用例：indexer（`this[...]`）。</summary>
    [PMNetworkObject]
    public partial class BadPropIndexer : PMNetObject
    {
        private int _backing;

        /// <summary>indexer：成员名不是合法标识符，无法生成 PMNet_Set / 槽位常量。</summary>
        [PMReplicated]
        public int this[int index]
        {
            get { return _backing; }
            set { _backing = value; }
        }
    }

    /// <summary>规则 14 用例：virtual 属性。</summary>
    [PMNetworkObject]
    public partial class BadPropVirtual : PMNetObject
    {
        /// <summary>virtual：编织器不处理虚成员。</summary>
        [PMReplicated]
        public virtual int VirtualProp { get; set; }
    }

    /// <summary>规则 14 用例：abstract 属性（所在类也必须 abstract）。</summary>
    [PMNetworkObject]
    public abstract partial class BadPropAbstract : PMNetObject
    {
        /// <summary>abstract：没有可改写的实现体。</summary>
        [PMReplicated]
        public abstract int AbstractProp { get; set; }
    }

    /// <summary>规则 14 用例：override 属性的基类。</summary>
    public partial class BadPropOverrideBase : PMNetObject
    {
        /// <summary>供派生类重写的虚成员（本身未被标记）。</summary>
        protected virtual int Overridable
        {
            get { return 0; }
            set { }
        }
    }

    /// <summary>规则 14 用例：override 属性。</summary>
    [PMNetworkObject]
    public partial class BadPropOverride : BadPropOverrideBase
    {
        /// <summary>override：编织器不猜重写继承链。</summary>
        [PMReplicated]
        protected override int Overridable { get; set; }
    }

    /// <summary>规则 14 用例：ref-return 属性。</summary>
    [PMNetworkObject]
    public partial class BadPropRefReturn : PMNetObject
    {
        private int _backing;

        /// <summary>ref-return：无法作为 EqualityComparer&lt;T&gt; 的闭合类型参数。</summary>
        [PMReplicated]
        public ref int RefReturnProp
        {
            get { return ref _backing; }
        }
    }

    /// <summary>规则 14 用例用的接口（显式接口实现的载体）。</summary>
    public interface IBadPropContract
    {
        /// <summary>成员。</summary>
        int Value { get; set; }
    }

    /// <summary>规则 14 用例：显式接口实现。</summary>
    [PMNetworkObject]
    public partial class BadPropExplicitInterface : PMNetObject, IBadPropContract
    {
        /// <summary>显式接口实现：元数据里的访问器名带接口前缀。</summary>
        [PMReplicated]
        int IBadPropContract.Value { get; set; }
    }

    /// <summary>
    /// 规则 14 用例：把 [PMReplicated] / [PMQuantized] 标在**不支持的成员种类**上（event）。
    ///
    /// 这类声明根本进不了 IR：若不专门扫，就会变成“标记了但完全不参与复制”的静默漏扫。
    /// </summary>
    [PMNetworkObject]
    public partial class BadReplicatedOnEvent : PMNetObject
    {
        /// <summary>字段式事件不是可复制成员。</summary>
        [PMReplicated]
        public event System.Action SomethingChanged;

        /// <summary>量化器单独写在事件上同样无效。</summary>
        [PMQuantized(3)]
        public event System.Action AnotherChanged;
    }

    /// <summary>
    /// 规则 1 / 14 用例：**嵌套类型**上的网络声明。
    ///
    /// 扫描器只处理顶层类型（生成物是「顶层 partial class &lt;TypeName&gt;」，无法与嵌套类型合并），
    /// 因此嵌套类型上的 [PMNetworkObject] 与其成员上的 [PMReplicated] 都不会生效；
    /// 若不显式扫，就是“写了声明、但完全不参与复制”的静默漏扫。
    /// </summary>
    public class BadNestedHost
    {
        /// <summary>嵌套的网络类：规则 1 用例。</summary>
        [PMNetworkObject]
        public partial class NestedNetworkObject : PMNetObject
        {
            /// <summary>嵌套类里的复制成员：规则 14 用例。</summary>
            [PMReplicated]
            public int Hp;
        }

        /// <summary>嵌套的普通类，但成员带 [PMReplicated]：规则 14 用例。</summary>
        public class NestedReplicated
        {
            /// <summary>不会生效的复制成员。</summary>
            [PMReplicated]
            public int Mana;
        }
    }
}
