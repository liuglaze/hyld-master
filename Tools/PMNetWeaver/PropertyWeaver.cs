// PropertyWeaver —— auto-property 复制属性的 IL 预检 / 编织 / 校验。
//
// 契约：Docs/plans/net-property-authoring-contract.md §2（冻结生成接口）
//
// 背景：字段旧模式必须手动调用 `PMNet_Set_<P>`（赋值 + 标脏）才能同步，漏调用是最难查的一类
// 网络缺陷。本文件实现的是「普通 C# 自动属性赋值即自动标脏」这一半：生成器为每个
// [PMReplicated] auto-property 发射三个成员，编织器把它们接通：
//
//   public const int PMGeneratedPropertyIndex_<P> = <indexBase + slot>;      // 冻结的槽位常量
//   private void PMNet_PropertyRawSet_<P>(T value) { throw ...; }           // 编织前：抛异常占位
//   private void PMNet_PropertySet_<P>(T value)                             // 冻结的赋值语义
//   {
//       bool changed = !EqualityComparer<T>.Default.Equals(P, value);
//       PMNet_PropertyRawSet_<P>(value);
//       if (changed && HasAuthority) MarkPropertyDirty(PMGeneratedPropertyIndex_<P>);   // 仅 PushBased 发射
//   }
//
// 编织器只改两个方法（**不动 getter / 构造初始化器 / 其它业务体，也不重写程序集里其它 stfld**）：
//   1. `set_<P>`：IL 换成 `this, value → call PMNet_PropertySet_<P> → ret`；
//   2. `PMNet_PropertyRawSet_<P>`：抛异常 stub 换成 `this, value → stfld <P>k__BackingField → ret`
//      （**继承原 setter 的实现标志**：Synchronized / NoInlining / AggressiveInlining 等逐位抄过来）。
//
//   为什么第 2 步必须是「继承」而不是「保持 RawSet 自己的标志」：
//   `[MethodImpl(MethodImplOptions.Synchronized)]` 的实例方法由运行时对 `this` 加监视器锁，
//   IL 体本身不变。编织后**本地赋值**走 `setter → PropertySet → RawSet`（锁由 setter 持有），
//   而**收包路径**是 `PMNet_Read_<P> → RawSet`（故意绕过 setter，以免反向标脏/触发副作用）。
//   生成物的 RawSet 桩默认没有任何实现标志，若不在编织时继承，收包写就**不受同一把锁保护**
//   —— 与本地赋值并发即数据竞争。因此这里必须抄标志，并且由 check 核对它们逐位相同。
// 两个被改写的方法清掉失效的 sequence point（合成桩的旧行号指向已经不存在的源码位置），
// 落盘后由 RpcAssemblyWeaver 用**独立读取器**（System.Reflection.Metadata）逐方法核对符号。
//
// check 不能只是「信 stamp、数调用次数」。这里逐条验证：
//   · 普通 setter 确实转发到 `PMNet_PropertySet_<P>`（不是被塞了别的业务）；
//   · RawSet 确实 `stfld` 到**本属性自己的** CompilerGenerated backing field；
//   · RawSet 与原 setter 的 `ImplAttributes` **逐位相同**（否则收包路径丢锁：Synchronized 等同步标志必须在）；
//   · 赋值语义 helper 真的实现了 `changed = !EqualityComparer<T>.Default.Equals(属性, value)`，
//     真的只在 `changed && HasAuthority` 时用**本属性自己的槽位常量**标脏，且 PushBased=false 时
//     完全不读 HasAuthority、完全不标脏；
//   · 生成物的收包 Reader 只走 RawSet，**不调用普通 setter / 赋值 helper**。
// 判定方式是有界抽象求值：白名单指令精确求值（含四种 (值是否变化 × 是否权威) 组合），
// 不认识就明确失败，绝不「大概是这样」放行。
//
// 本文件只复用 RpcAssemblyWeaver.PdbProbe 作为符号探针载体，保证**没有第二个写盘器**。

using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PMNetWeaver
{
    /// <summary>一个 [PMReplicated] auto-property 的成员定位、槽位与语义事实。</summary>
    internal sealed class PropertyPlan
    {
        /// <summary>声明类型。</summary>
        public TypeDefinition Type;

        /// <summary>属性定义。</summary>
        public PropertyDefinition Property;

        /// <summary>属性名（生成物 `<P>` 占位符，例如 `_combatHp`）。</summary>
        public string MemberName;

        /// <summary>属性类型（冻结的闭合泛型参数：EqualityComparer&lt;T&gt; 的 T）。</summary>
        public TypeReference ValueType;

        /// <summary>Roslyn 生成的 backing field（`&lt;P&gt;k__BackingField`）。</summary>
        public FieldDefinition Backing;

        /// <summary>纯 auto getter（编织不改）。</summary>
        public MethodDefinition Getter;

        /// <summary>setter（编织后转发到 PropertySet）。</summary>
        public MethodDefinition Setter;

        /// <summary>RawSet（编织前抛异常 stub，编织后 stfld backing）。</summary>
        public MethodDefinition RawSet;

        /// <summary>PropertySet（冻结的 changed / RawSet / Authority / 标脏语义）。</summary>
        public MethodDefinition PropertySet;

        /// <summary>槽位常量字段 `PMGeneratedPropertyIndex_&lt;P&gt;`。</summary>
        public FieldDefinition IndexConst;

        /// <summary>槽位常量值。</summary>
        public int IndexValue;

        /// <summary>[PMReplicated] 的 PushBased（唯一权威来源，默认 true）。</summary>
        public bool PushBased;

        /// <summary>[PMReplicated] 的 Condition（PMCond 底层整型）。</summary>
        public int Condition;

        /// <summary>生成物的收包 Reader `PMNet_Read_&lt;P&gt;`（必须只走 RawSet）。</summary>
        public MethodDefinition Reader;
    }

    /// <summary>auto-property 编织器（预检 / 编织 / 校验）。</summary>
    internal static class PropertyWeaver
    {
        /// <summary>精确全类型名：只认本项目的复制标记，不碰第三方同名 Attribute。</summary>
        public const string ReplicatedAttributeFullName = "PMNet.PMReplicatedAttribute";

        /// <summary>RawSet helper 前缀（冻结）。</summary>
        public const string RawSetPrefix = "PMNet_PropertyRawSet_";

        /// <summary>PropertySet helper 前缀（冻结）。</summary>
        public const string PropertySetPrefix = "PMNet_PropertySet_";

        /// <summary>槽位常量前缀（冻结）。</summary>
        public const string IndexConstPrefix = "PMGeneratedPropertyIndex_";

        /// <summary>槽位基址常量名（生成物既有）。</summary>
        public const string IndexBaseConstName = "PMGeneratedPropertyIndexBase";

        /// <summary>变更掩码位数常量名（生成物既有）。</summary>
        public const string ChangeMaskBitCountConstName = "PMGeneratedChangeMaskBitCount";

        /// <summary>生成物收包 Reader 前缀（既有命名，未改）。</summary>
        public const string ReaderPrefix = "PMNet_Read_";

        /// <summary>Roslyn 自动属性 backing field 名前缀（`&lt;P&gt;k__BackingField`）。</summary>
        public const string BackingFieldPrefix = "<";

        /// <summary>Roslyn 自动属性 backing field 后缀。</summary>
        public const string BackingFieldSuffix = ">k__BackingField";

        /// <summary>复制属性注册方法名（生成物既有）。</summary>
        public const string CollectPropsName = "CollectLifetimeReplicatedProps";

        private const string CompilerGeneratedAttributeFullName =
            "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

        private const string EqualityComparerElementFullName = "System.Collections.Generic.EqualityComparer`1";

        private const string PMNetObjectFullName = "PMNet.PMNetObject";

        private const string RepListFullName = "PMNet.PMRepList";

        private const string InvalidOperationExceptionFullName = "System.InvalidOperationException";

        // =================================================================================
        //  收集
        // =================================================================================

        /// <summary>精确判定属性是否带 `PMNet.PMReplicatedAttribute`。</summary>
        public static bool IsReplicatedProperty(PropertyDefinition property)
        {
            return HasExactAttribute(property, ReplicatedAttributeFullName);
        }

        /// <summary>收集类型上全部 [PMReplicated] 属性（[PMReplicated] 字段是旧模式，刻意不在此列）。</summary>
        public static List<PropertyDefinition> Collect(TypeDefinition type)
        {
            List<PropertyDefinition> result = new List<PropertyDefinition>();
            if (type == null || !type.HasProperties)
            {
                return result;
            }

            for (int i = 0; i < type.Properties.Count; i++)
            {
                PropertyDefinition property = type.Properties[i];
                if (IsReplicatedProperty(property))
                {
                    result.Add(property);
                }
            }

            return result;
        }

        // =================================================================================
        //  预检 / 校验入口
        // =================================================================================

        /// <summary>
        /// 分析一个类型的全部 auto-property 复制成员；任何形态不符**明确失败**（不静默跳过）。
        /// <paramref name="woven"/> = true 表示当前程序集自称已编织（stamp=1），此时校验编织后形态。
        /// </summary>
        public static PropertyPlan[] Analyze(TypeDefinition type, bool woven)
        {
            List<PropertyDefinition> properties = Collect(type);
            if (properties.Count == 0)
            {
                return new PropertyPlan[0];
            }

            PropertyPlan[] plans = new PropertyPlan[properties.Count];
            for (int i = 0; i < properties.Count; i++)
            {
                plans[i] = Locate(type, properties[i]);
            }

            // 槽位 / 条件 / Push 必须与生成物注册表一致（不只是「常量自洽」）。
            ValidateRegistration(type, plans);

            for (int i = 0; i < plans.Length; i++)
            {
                PropertyPlan plan = plans[i];
                RequireBackingField(plan);
                RequirePureGetter(plan);

                if (woven)
                {
                    RequireWovenSetter(plan);
                    RequireWovenRawSet(plan);
                    RequireSameImplementationFlags(plan);
                }
                else
                {
                    RequireUnwovenSetter(plan);
                    RequireUnwovenRawSetStub(plan);
                }

                RequireAssignmentHelper(plan);
                RequireReaderUsesRawSet(plan);
            }

            return plans;
        }

        /// <summary>
        /// 定位一个属性的全部成员并校验「与编织无关的静态事实」：
        /// 纯 auto-property、CompilerGenerated backing field、槽位常量、helper 存在且签名正确、
        /// 以及从 [PMReplicated] 读出的 Condition / PushBased。
        /// </summary>
        private static PropertyPlan Locate(TypeDefinition type, PropertyDefinition property)
        {
            PropertyPlan plan = new PropertyPlan();
            plan.Type = type;
            plan.Property = property;
            plan.MemberName = property.Name;
            plan.ValueType = property.PropertyType;

            string what = "类型 " + type.FullName + " 的复制属性 " + property.Name;

            if (property.Parameters != null && property.Parameters.Count != 0)
            {
                throw new WeaverException(
                    what + " 是索引器（带 " + property.Parameters.Count + " 个参数）："
                    + "契约只支持普通实例 auto-property，明确拒绝。");
            }

            MethodDefinition getter = property.GetMethod;
            MethodDefinition setter = property.SetMethod;
            if (getter == null || setter == null)
            {
                throw new WeaverException(
                    what + " 不是可读可写的 auto-property（getter=" + (getter == null ? "缺" : "有")
                    + "、setter=" + (setter == null ? "缺" : "有") + "）："
                    + "只读 / 只写属性无法承载「赋值即标脏」，明确拒绝。");
            }

            plan.Getter = getter;
            plan.Setter = setter;

            if (getter.IsStatic || setter.IsStatic)
            {
                throw new WeaverException(what + " 是 static：契约只支持实例 auto-property，明确拒绝。");
            }

            if (getter.IsVirtual || setter.IsVirtual || getter.IsAbstract || setter.IsAbstract)
            {
                throw new WeaverException(
                    what + " 的访问器是 virtual/abstract：契约不支持虚属性（不猜重写继承链），明确拒绝。");
            }

            if (!string.Equals(getter.Name, "get_" + property.Name, StringComparison.Ordinal)
                || !string.Equals(setter.Name, "set_" + property.Name, StringComparison.Ordinal))
            {
                throw new WeaverException(
                    what + " 的访问器名不符（getter=" + getter.Name + "、setter=" + setter.Name
                    + "）：显式接口实现 / 自定义访问器形态不支持，明确拒绝。");
            }

            if (plan.ValueType == null)
            {
                throw new WeaverException(what + " 缺少类型信息。");
            }

            if (plan.ValueType.IsByReference || plan.ValueType.IsPointer)
            {
                throw new WeaverException(what + " 是 ref-return / 指针类型：契约不支持，明确拒绝。");
            }

            if (getter.Parameters.Count != 0
                || getter.ReturnType == null
                || !string.Equals(getter.ReturnType.FullName, plan.ValueType.FullName, StringComparison.Ordinal))
            {
                throw new WeaverException(what + " 的 getter 签名不符（应为 T " + getter.Name + "()）。");
            }

            if (setter.Parameters.Count != 1
                || setter.ReturnType == null
                || setter.ReturnType.MetadataType != MetadataType.Void
                || setter.Parameters[0].ParameterType == null
                || !string.Equals(setter.Parameters[0].ParameterType.FullName, plan.ValueType.FullName, StringComparison.Ordinal))
            {
                throw new WeaverException(what + " 的 setter 签名不符（应为 void " + setter.Name + "(T)）。");
            }

            if (!getter.HasBody || !setter.HasBody)
            {
                throw new WeaverException(what + " 的访问器没有方法体：auto-property 形态不符。");
            }

            // backing field：严格按 Roslyn 自动属性的命名与 CompilerGenerated 标记定位。
            string backingName = BackingFieldPrefix + property.Name + BackingFieldSuffix;
            plan.Backing = FindField(type, backingName);
            if (plan.Backing == null)
            {
                throw new WeaverException(
                    what + " 找不到 CompilerGenerated backing field " + backingName
                    + "：这不是 auto-property（自定义 getter/setter 不支持），明确拒绝。");
            }

            // 槽位常量：`public const int PMGeneratedPropertyIndex_<P> = <indexBase + slot>;`
            plan.IndexConst = FindField(type, IndexConstPrefix + property.Name);
            if (plan.IndexConst == null)
            {
                throw new WeaverException(
                    what + " 缺少生成物槽位常量 " + IndexConstPrefix + property.Name
                    + "：有属性无生成物 ⇒ 明确失败。修复：先运行 PMNetGen --decl-gen 重新生成。");
            }

            if (!plan.IndexConst.IsStatic || !plan.IndexConst.IsLiteral
                || plan.IndexConst.FieldType == null || plan.IndexConst.FieldType.MetadataType != MetadataType.Int32
                || plan.IndexConst.Constant == null)
            {
                throw new WeaverException(
                    what + " 的槽位常量 " + plan.IndexConst.Name
                    + " 形态不符（冻结格式要求 `public const int`）。");
            }

            plan.IndexValue = Convert.ToInt32(
                plan.IndexConst.Constant, System.Globalization.CultureInfo.InvariantCulture);

            plan.RawSet = FindMethod(type, RawSetPrefix + property.Name);
            if (plan.RawSet == null)
            {
                throw new WeaverException(
                    what + " 缺少生成物 helper " + RawSetPrefix + property.Name
                    + "：有属性无生成物 ⇒ 明确失败。修复：先运行 PMNetGen --decl-gen 重新生成。");
            }

            if (plan.RawSet.IsStatic || plan.RawSet.Parameters.Count != 1
                || plan.RawSet.ReturnType == null || plan.RawSet.ReturnType.MetadataType != MetadataType.Void
                || plan.RawSet.Parameters[0].ParameterType == null
                || !string.Equals(plan.RawSet.Parameters[0].ParameterType.FullName, plan.ValueType.FullName, StringComparison.Ordinal))
            {
                throw new WeaverException(
                    "生成物 helper " + Loc(plan.RawSet) + " 签名不符（应为 private void " + RawSetPrefix
                    + "<P>(T) 实例方法）。");
            }

            if (!plan.RawSet.HasBody)
            {
                throw new WeaverException("生成物 helper " + Loc(plan.RawSet) + " 没有方法体。");
            }

            plan.PropertySet = FindMethod(type, PropertySetPrefix + property.Name);
            if (plan.PropertySet == null)
            {
                throw new WeaverException(
                    what + " 缺少生成物 helper " + PropertySetPrefix + property.Name
                    + "：有属性无生成物 ⇒ 明确失败。修复：先运行 PMNetGen --decl-gen 重新生成。");
            }

            if (plan.PropertySet.IsStatic || plan.PropertySet.Parameters.Count != 1
                || plan.PropertySet.ReturnType == null || plan.PropertySet.ReturnType.MetadataType != MetadataType.Void
                || plan.PropertySet.Parameters[0].ParameterType == null
                || !string.Equals(plan.PropertySet.Parameters[0].ParameterType.FullName, plan.ValueType.FullName, StringComparison.Ordinal))
            {
                throw new WeaverException(
                    "生成物 helper " + Loc(plan.PropertySet) + " 签名不符（应为 private void "
                    + PropertySetPrefix + "<P>(T) 实例方法）。");
            }

            if (!plan.PropertySet.HasBody)
            {
                throw new WeaverException("生成物 helper " + Loc(plan.PropertySet) + " 没有方法体。");
            }

            ReadReplicatedAttribute(property, out bool pushBased, out int condition, what);
            plan.PushBased = pushBased;
            plan.Condition = condition;

            plan.Reader = FindMethod(type, ReaderPrefix + property.Name);

            return plan;
        }

        private static void ReadReplicatedAttribute(
            PropertyDefinition property,
            out bool pushBased,
            out int condition,
            string what)
        {
            pushBased = true;
            condition = 0;

            if (!property.HasCustomAttributes)
            {
                throw new WeaverException(what + " 找不到 " + ReplicatedAttributeFullName + "（内部一致性错误）。");
            }

            for (int i = 0; i < property.CustomAttributes.Count; i++)
            {
                CustomAttribute attribute = property.CustomAttributes[i];
                if (attribute.AttributeType == null
                    || !string.Equals(attribute.AttributeType.FullName, ReplicatedAttributeFullName, StringComparison.Ordinal))
                {
                    continue;
                }

                // 构造参数：`[PMReplicated(PMCond.OwnerOnly)]`。
                if (attribute.ConstructorArguments.Count > 0)
                {
                    condition = Convert.ToInt32(
                        attribute.ConstructorArguments[0].Value, System.Globalization.CultureInfo.InvariantCulture);
                }

                // 具名字段：`[PMReplicated(PushBased = false)]` / `[PMReplicated(Condition = ...)]`。
                for (int k = 0; k < attribute.Fields.Count; k++)
                {
                    CustomAttributeNamedArgument named = attribute.Fields[k];
                    if (string.Equals(named.Name, "PushBased", StringComparison.Ordinal))
                    {
                        pushBased = Convert.ToBoolean(
                            named.Argument.Value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (string.Equals(named.Name, "Condition", StringComparison.Ordinal))
                    {
                        condition = Convert.ToInt32(
                            named.Argument.Value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                return;
            }

            throw new WeaverException(what + " 找不到 " + ReplicatedAttributeFullName + "（内部一致性错误）。");
        }

        private static void RequireBackingField(PropertyPlan plan)
        {
            FieldDefinition field = plan.Backing;
            string what = "类型 " + plan.Type.FullName + " 的属性 " + plan.MemberName
                          + " 的 backing field " + field.Name;

            if (field.IsStatic)
            {
                throw new WeaverException(what + " 是 static：auto-property 形态不符。");
            }

            if (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly || field.IsAssembly)
            {
                throw new WeaverException(what + " 不是私有字段：不是 Roslyn 自动属性形态，明确拒绝。");
            }

            if (field.FieldType == null
                || !string.Equals(field.FieldType.FullName, plan.ValueType.FullName, StringComparison.Ordinal))
            {
                throw new WeaverException(what + " 的类型与属性类型不一致，明确拒绝。");
            }

            if (!HasExactAttribute(field, CompilerGeneratedAttributeFullName))
            {
                throw new WeaverException(
                    what + " 没有 " + CompilerGeneratedAttributeFullName
                    + "：不是编译器生成的 auto-property backing field，明确拒绝"
                    + "（契约只支持纯 auto-property；字段旧模式请继续用 PMNet_Set_<P> 显式标脏）。");
            }
        }

        // =================================================================================
        //  槽位 / 注册表交叉核对
        // =================================================================================

        /// <summary>生成物注册表里的一条 `outProps.Add(index, condition, pushBased)`。</summary>
        private struct RegisteredProp
        {
            public int Index;
            public int Condition;
            public bool PushBased;
        }

        /// <summary>
        /// 交叉核对槽位：属性上的 [PMReplicated]（Condition / PushBased）必须与生成物注册表
        /// `CollectLifetimeReplicatedProps` 里**同一槽位**的那条 Add 一致，槽位常量也必须是常量 int。
        ///
        /// 为什么不能只信槽位常量自己：常量与注册表一旦分叉，运行期 `MarkPropertyDirty(常量)`
        /// 会把脏位打到**别的属性**上（静默串位），或落在根本没有属性的槽位上
        /// （永远清不掉的伪位 ⇒ 对象终身每 Tick 空扫）。
        /// </summary>
        private static void ValidateRegistration(TypeDefinition type, PropertyPlan[] plans)
        {
            List<RegisteredProp> registered = ReadRegistration(type);

            int registrationBase = -1;
            int registeredCount = registered.Count;
            bool[] seen = new bool[registeredCount];
            for (int i = 0; i < registeredCount; i++)
            {
                if (registrationBase < 0 || registered[i].Index < registrationBase)
                {
                    registrationBase = registered[i].Index;
                }
            }

            for (int i = 0; i < registeredCount; i++)
            {
                int offset = registered[i].Index - registrationBase;
                if (offset < 0 || offset >= registeredCount || seen[offset])
                {
                    throw new WeaverException(
                        "类型 " + type.FullName + " 的复制属性注册槽位不连续（重复或空洞）：槽位 "
                        + registered[i].Index + "（基址 " + registrationBase + "、条数 " + registeredCount
                        + "）。生成物与运行期注册表不一致，明确失败。");
                }

                seen[offset] = true;
            }

            FieldDefinition baseField = FindField(type, IndexBaseConstName);
            if (baseField != null && registeredCount > 0)
            {
                int declaredBase = ReadConstInt32(baseField, IndexBaseConstName);
                if (declaredBase != registrationBase)
                {
                    throw new WeaverException(
                        "类型 " + type.FullName + " 的 " + IndexBaseConstName + " = " + declaredBase
                        + "，但注册表最小槽位是 " + registrationBase + "：生成物自相矛盾，明确失败。");
                }
            }

            FieldDefinition maskField = FindField(type, ChangeMaskBitCountConstName);
            if (maskField != null)
            {
                int declaredMask = ReadConstInt32(maskField, ChangeMaskBitCountConstName);
                if (declaredMask != registeredCount)
                {
                    throw new WeaverException(
                        "类型 " + type.FullName + " 的 " + ChangeMaskBitCountConstName + " = " + declaredMask
                        + "，但注册表有 " + registeredCount + " 条：变更掩码位数与复制属性数不一致，明确失败。");
                }
            }

            HashSet<int> planIndices = new HashSet<int>();
            for (int i = 0; i < plans.Length; i++)
            {
                PropertyPlan plan = plans[i];
                string what = "类型 " + type.FullName + " 的属性 " + plan.MemberName;

                if (!planIndices.Add(plan.IndexValue))
                {
                    throw new WeaverException(
                        what + " 的槽位常量 " + plan.IndexConst.Name + " = " + plan.IndexValue
                        + " 与同类另一个属性重复：槽位必须唯一，明确失败。");
                }

                int match = -1;
                for (int k = 0; k < registeredCount; k++)
                {
                    if (registered[k].Index == plan.IndexValue)
                    {
                        match = k;
                        break;
                    }
                }

                if (match < 0)
                {
                    throw new WeaverException(
                        what + " 的槽位常量 = " + plan.IndexValue + " 在生成物注册表里不存在"
                        + "（注册表槽位 [" + registrationBase + ", " + (registrationBase + registeredCount - 1)
                        + "]）：属性常量与注册表分叉，明确失败。");
                }

                if (registered[match].Condition != plan.Condition)
                {
                    throw new WeaverException(
                        what + " 的复制条件与注册表不一致（[PMReplicated]=" + plan.Condition
                        + "、注册表=" + registered[match].Condition + "）：来源分叉，明确失败。");
                }

                if (registered[match].PushBased != plan.PushBased)
                {
                    throw new WeaverException(
                        what + " 的 PushBased 与注册表不一致（[PMReplicated]=" + plan.PushBased
                        + "、注册表=" + registered[match].PushBased + "）：来源分叉，明确失败。");
                }
            }
        }

        private static int ReadConstInt32(FieldDefinition field, string what)
        {
            if (!field.IsStatic || !field.IsLiteral || field.Constant == null)
            {
                throw new WeaverException(
                    "生成物常量 " + field.DeclaringType.FullName + "." + what + " 形态不符（应为 const）。");
            }

            return Convert.ToInt32(field.Constant, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 解析 `CollectLifetimeReplicatedProps` 里**本类**直接发出的 `outProps.Add(index, cond, push)`。
        /// 用一个只认「常量 / 参数」两级栈的最小模拟器；基类调用按普通调用处理。
        /// </summary>
        private static List<RegisteredProp> ReadRegistration(TypeDefinition type)
        {
            List<RegisteredProp> result = new List<RegisteredProp>();

            MethodDefinition method = FindMethod(type, CollectPropsName);
            if (method == null || !method.HasBody)
            {
                return result;
            }

            List<object> stack = new List<object>();
            List<Instruction> instructions = new List<Instruction>(method.Body.Instructions);
            for (int i = 0; i < instructions.Count; i++)
            {
                Instruction ins = instructions[i];
                Code code = ins.OpCode.Code;

                if (code == Code.Nop)
                {
                    continue;
                }

                if (IsLoadConstInt32(ins, out int constant))
                {
                    stack.Add(constant);
                    continue;
                }

                if (code == Code.Ldarg_0 || code == Code.Ldarg_1 || code == Code.Ldarg_2
                    || code == Code.Ldarg_3 || code == Code.Ldarg || code == Code.Ldarg_S)
                {
                    stack.Add(null);
                    continue;
                }

                if (code == Code.Call || code == Code.Callvirt)
                {
                    MethodReference target = ins.Operand as MethodReference;
                    int argc = target == null ? 0 : target.Parameters.Count;
                    if (target != null && target.HasThis)
                    {
                        argc++;
                    }

                    if (argc > stack.Count)
                    {
                        stack.Clear();
                        continue;
                    }

                    List<object> args = new List<object>();
                    for (int k = 0; k < argc; k++)
                    {
                        args.Insert(0, stack[stack.Count - 1]);
                        stack.RemoveAt(stack.Count - 1);
                    }

                    if (target != null && argc == 4
                        && string.Equals(target.Name, "Add", StringComparison.Ordinal)
                        && target.DeclaringType != null
                        && string.Equals(target.DeclaringType.FullName, RepListFullName, StringComparison.Ordinal)
                        && args[1] is int && args[2] is int && args[3] is int)
                    {
                        RegisteredProp entry = new RegisteredProp();
                        entry.Index = (int)args[1];
                        entry.Condition = (int)args[2];
                        entry.PushBased = (int)args[3] != 0;
                        result.Add(entry);
                    }

                    if (target != null && target.ReturnType != null
                        && target.ReturnType.MetadataType != MetadataType.Void)
                    {
                        stack.Add(null);
                    }

                    continue;
                }

                if (code == Code.Ret)
                {
                    break;
                }

                if (code == Code.Br || code == Code.Br_S
                    || code == Code.Brtrue || code == Code.Brtrue_S
                    || code == Code.Brfalse || code == Code.Brfalse_S)
                {
                    continue;
                }

                stack.Clear();
            }

            return result;
        }

        // =================================================================================
        //  访问器形态校验
        // =================================================================================

        private enum StackKind
        {
            Unset,
            This,
            Field,
            Int,
            Param,
        }

        /// <summary>
        /// 纯 auto getter：只能把 `this` 上的**本属性 backing field**读出来并返回。
        /// 允许 Debug 构建的「返回值先落局部变量再返回」形态（`ldfld; stloc; br; ldloc; ret`）。
        /// 编织不改 getter，因此这个断言在编织前后都成立。
        /// </summary>
        private static void RequirePureGetter(PropertyPlan plan)
        {
            MethodDefinition method = plan.Getter;
            string what = "类型 " + plan.Type.FullName + " 的属性 " + plan.MemberName
                          + " 的 getter " + method.Name;

            if (method.Body.HasExceptionHandlers)
            {
                throw new WeaverException(what + " 带异常处理：不是纯 auto getter，明确拒绝。");
            }

            StackKind[] locals = new StackKind[method.Body.Variables.Count];
            List<StackKind> stack = new List<StackKind>();
            List<Instruction> instructions = new List<Instruction>(method.Body.Instructions);
            Dictionary<Instruction, int> indexOf = BuildIndex(instructions);

            int pc = 0;
            int steps = 0;
            int maxSteps = (instructions.Count * 8) + 64;

            while (true)
            {
                if (++steps > maxSteps || pc < 0 || pc >= instructions.Count)
                {
                    throw new WeaverException(what + " 的控制流无法在有限步内判定，形态无法识别，拒绝猜测。");
                }

                Instruction ins = instructions[pc];
                Code code = ins.OpCode.Code;
                int next = pc + 1;

                switch (code)
                {
                    case Code.Nop:
                        break;

                    case Code.Ldarg_0:
                    case Code.Ldarg_1:
                        stack.Add(code == Code.Ldarg_0 ? StackKind.This : StackKind.Param);
                        break;

                    case Code.Ldfld:
                    {
                        if (stack.Count == 0)
                        {
                            throw new WeaverException(what + " 栈下溢，形态不符。");
                        }

                        StackKind owner = Pop(stack);
                        FieldReference field = ins.Operand as FieldReference;
                        if (owner != StackKind.This || field == null || !Targets(field, plan.Backing))
                        {
                            throw new WeaverException(
                                what + " 读取的不是本属性的 backing field " + plan.Backing.Name
                                + "：不是纯 auto getter，明确拒绝。");
                        }

                        stack.Add(StackKind.Field);
                        break;
                    }

                    case Code.Stloc_0:
                    case Code.Stloc_1:
                    case Code.Stloc_2:
                    case Code.Stloc_3:
                    case Code.Stloc_S:
                    case Code.Stloc:
                    {
                        int index = LocalIndex(ins);
                        if (index < 0 || index >= locals.Length)
                        {
                            throw new WeaverException(what + " 写入了越界局部变量，形态不符。");
                        }

                        locals[index] = Pop(stack, what);
                        break;
                    }

                    case Code.Ldloc_0:
                    case Code.Ldloc_1:
                    case Code.Ldloc_2:
                    case Code.Ldloc_3:
                    case Code.Ldloc_S:
                    case Code.Ldloc:
                    {
                        int index = LocalIndex(ins);
                        if (index < 0 || index >= locals.Length || locals[index] == StackKind.Unset)
                        {
                            throw new WeaverException(what + " 读取了未初始化局部变量，形态不符。");
                        }

                        stack.Add(locals[index]);
                        break;
                    }

                    case Code.Br:
                    case Code.Br_S:
                        next = TargetIndex(ins.Operand as Instruction, indexOf, what);
                        break;

                    case Code.Ret:
                    {
                        if (stack.Count != 1 || stack[0] != StackKind.Field)
                        {
                            throw new WeaverException(
                                what + " 的返回值不是本属性 backing field 的值：不是纯 auto getter，明确拒绝。");
                        }

                        return;
                    }

                    default:
                        throw new WeaverException(
                            what + " 含不属于纯 auto getter 的指令 " + ins.OpCode
                            + "（IL_" + ins.Offset.ToString("x4") + "）：明确拒绝。");
                }

                pc = next;
            }
        }

        /// <summary>编织前 setter：只允许 `this, value → stfld backing`。</summary>
        private static void RequireUnwovenSetter(PropertyPlan plan)
        {
            MethodDefinition method = plan.Setter;
            string what = "类型 " + plan.Type.FullName + " 的属性 " + plan.MemberName
                          + " 的 setter " + method.Name;

            if (method.Body.HasExceptionHandlers)
            {
                throw new WeaverException(what + " 带异常处理：不是纯 auto setter，明确拒绝。");
            }

            StackKind[] locals = new StackKind[method.Body.Variables.Count];
            List<StackKind> stack = new List<StackKind>();
            List<Instruction> instructions = new List<Instruction>(method.Body.Instructions);
            Dictionary<Instruction, int> indexOf = BuildIndex(instructions);

            int stores = 0;
            int pc = 0;
            int steps = 0;
            int maxSteps = (instructions.Count * 8) + 64;

            while (true)
            {
                if (++steps > maxSteps || pc < 0 || pc >= instructions.Count)
                {
                    throw new WeaverException(what + " 的控制流无法在有限步内判定，形态无法识别，拒绝猜测。");
                }

                Instruction ins = instructions[pc];
                Code code = ins.OpCode.Code;
                int next = pc + 1;

                switch (code)
                {
                    case Code.Nop:
                        break;

                    case Code.Ldarg_0:
                        stack.Add(StackKind.This);
                        break;

                    case Code.Ldarg_1:
                        stack.Add(StackKind.Param);
                        break;

                    case Code.Stfld:
                    {
                        if (stack.Count < 2)
                        {
                            throw new WeaverException(what + " 栈下溢，形态不符。");
                        }

                        StackKind value = Pop(stack);
                        StackKind owner = Pop(stack);
                        FieldReference field = ins.Operand as FieldReference;
                        if (owner != StackKind.This || value != StackKind.Param
                            || field == null || !Targets(field, plan.Backing))
                        {
                            throw new WeaverException(
                                what + " 不是「this, value → stfld " + plan.Backing.Name
                                + "」：不是纯 auto setter，明确拒绝（自定义 setter 不支持）。");
                        }

                        stores++;
                        break;
                    }

                    case Code.Ldloc_0:
                    case Code.Ldloc_1:
                    case Code.Ldloc_2:
                    case Code.Ldloc_3:
                    case Code.Ldloc_S:
                    case Code.Ldloc:
                    {
                        int index = LocalIndex(ins);
                        if (index < 0 || index >= locals.Length || locals[index] == StackKind.Unset)
                        {
                            throw new WeaverException(what + " 读取了未初始化局部变量，形态不符。");
                        }

                        stack.Add(locals[index]);
                        break;
                    }

                    case Code.Stloc_0:
                    case Code.Stloc_1:
                    case Code.Stloc_2:
                    case Code.Stloc_3:
                    case Code.Stloc_S:
                    case Code.Stloc:
                    {
                        int index = LocalIndex(ins);
                        if (index < 0 || index >= locals.Length)
                        {
                            throw new WeaverException(what + " 写入了越界局部变量，形态不符。");
                        }

                        locals[index] = Pop(stack, what);
                        break;
                    }

                    case Code.Br:
                    case Code.Br_S:
                        next = TargetIndex(ins.Operand as Instruction, indexOf, what);
                        break;

                    case Code.Ret:
                    {
                        if (stores != 1 || stack.Count != 0)
                        {
                            throw new WeaverException(
                                what + " 的形态不是「恰好一次 stfld backing field 后返回」：明确拒绝。");
                        }

                        return;
                    }

                    default:
                        throw new WeaverException(
                            what + " 含不属于纯 auto setter 的指令 " + ins.OpCode
                            + "（IL_" + ins.Offset.ToString("x4") + "）：明确拒绝。");
                }

                pc = next;
            }
        }

        /// <summary>编织后 setter：必须是 `this, value → call PMNet_PropertySet_&lt;P&gt; → ret`。</summary>
        private static void RequireWovenSetter(PropertyPlan plan)
        {
            MethodDefinition method = plan.Setter;
            string what = "类型 " + plan.Type.FullName + " 的属性 " + plan.MemberName
                          + " 的 setter " + method.Name;

            List<Instruction> body = StripNops(method);
            if (body.Count != 4
                || !IsLdarg(body[0], 0)
                || !IsLdarg(body[1], 1)
                || body[2].OpCode.Code != Code.Call
                || !Targets(body[2].Operand as MethodReference, plan.PropertySet)
                || body[3].OpCode.Code != Code.Ret)
            {
                throw new WeaverException(
                    what + " 的 IL 不是「this, value → call " + PropertySetPrefix + plan.MemberName
                    + " → ret」（实际 " + Describe(body) + "）：stamp=1 但 setter 未改写"
                    + "（不能只有 stamp 就算绿），明确失败。");
            }

            if (method.Body.HasExceptionHandlers || method.Body.Variables.Count != 0)
            {
                throw new WeaverException(what + " 不应有异常处理 / 局部变量（转发桩形态）。");
            }
        }

        /// <summary>编织后 RawSet：必须是 `this, value → stfld &lt;P&gt;k__BackingField → ret`。</summary>
        private static void RequireWovenRawSet(PropertyPlan plan)
        {
            MethodDefinition method = plan.RawSet;
            string what = "生成物 helper " + Loc(method);

            List<Instruction> body = StripNops(method);
            if (body.Count != 4
                || !IsLdarg(body[0], 0)
                || !IsLdarg(body[1], 1)
                || body[2].OpCode.Code != Code.Stfld
                || !Targets(body[2].Operand as FieldReference, plan.Backing)
                || body[3].OpCode.Code != Code.Ret)
            {
                throw new WeaverException(
                    what + " 的 IL 不是「this, value → stfld " + plan.Backing.Name
                    + " → ret」（实际 " + Describe(body) + "）：RawSet 没有精确写入本属性的 backing field"
                    + "（写错字段会静默串到别的属性上），明确失败。");
            }

            if (method.Body.HasExceptionHandlers || method.Body.Variables.Count != 0)
            {
                throw new WeaverException(what + " 不应有异常处理 / 局部变量（RawSet 形态）。");
            }
        }

        /// <summary>
        /// 编织后：RawSet 必须与原 setter 带**完全相同**的实现标志。
        ///
        /// 为什么这条不能省：`[MethodImpl(MethodImplOptions.Synchronized)]` 的实例方法由运行时
        /// 对 `this` 加监视器锁。本地赋值（setter → PropertySet → RawSet）在锁内，
        /// 而收包路径（Reader → RawSet）绕过 setter —— 两者必须落在**同一把锁**上，
        /// 否则「收包写」与「本地赋值」可以并发交错。
        /// 只检查「RawSet 有没有 Synchronized」不够：那会放行
        /// 「setter 带 NoInlining、RawSet 带 Synchronized」这类标志分叉；
        /// 契约为「保留 setter MethodImpl 标志」，因此这里要求逐位相同。
        /// </summary>
        private static void RequireSameImplementationFlags(PropertyPlan plan)
        {
            MethodImplAttributes setterFlags = plan.Setter.ImplAttributes;
            MethodImplAttributes rawSetFlags = plan.RawSet.ImplAttributes;
            if (rawSetFlags != setterFlags)
            {
                throw new WeaverException(
                    "类型 " + plan.Type.FullName + " 的属性 " + plan.MemberName + " 的 RawSet "
                    + "实现标志与 setter 不一致（RawSet=" + (int)rawSetFlags
                    + "、setter=" + (int)setterFlags
                    + "）：收包路径会丢锁（Synchronized / NoInlining 等必要标志必须在），明确失败。"
                    + "修复：重新运行 --weave（编织器会把 setter 的实现标志继承给 RawSet）。");
            }
        }

        /// <summary>编织前 RawSet：必须是抛 `System.InvalidOperationException` 的占位 stub。</summary>
        private static void RequireUnwovenRawSetStub(PropertyPlan plan)
        {
            MethodDefinition method = plan.RawSet;
            string what = "生成物 helper " + Loc(method);

            List<Instruction> body = StripNops(method);
            if (body.Count != 3
                || body[0].OpCode.Code != Code.Ldstr
                || body[1].OpCode.Code != Code.Newobj
                || !IsInvalidOperationExceptionCtor(body[1].Operand as MethodReference)
                || body[2].OpCode.Code != Code.Throw)
            {
                throw new WeaverException(
                    what + " 不是「抛出 " + InvalidOperationExceptionFullName
                    + " 的未编织占位 stub」（实际 " + Describe(body) + "）：冻结格式要求编织前它是抛异常 stub"
                    + "（否则无法区分「已编织」与「生成物自带实现」），明确失败。");
            }
        }

        // =================================================================================
        //  赋值语义 helper：changed / RawSet / Authority / 标脏（有界抽象求值）
        // =================================================================================

        /// <summary>一次抽象求值观察到的行为。</summary>
        private sealed class HelperRun
        {
            public int RawSetCalls;
            public int RawSetArgKindOk;
            public bool AuthorityRead;
            public readonly List<int> DirtyIndices = new List<int>();
            public bool ComparisonPresent;
            public bool Returned;
        }

        private enum HelperKind
        {
            Unset,
            This,
            Comparer,
            Prop,
            Param,
            Equal,
            NotEqual,
            Authority,
            Int,
        }

        private struct HelperValue
        {
            public HelperKind Kind;
            public int Int;
        }

        private static HelperValue Hv(HelperKind kind)
        {
            HelperValue value = new HelperValue();
            value.Kind = kind;
            return value;
        }

        private static HelperValue HvInt(int number)
        {
            HelperValue value = new HelperValue();
            value.Kind = HelperKind.Int;
            value.Int = number;
            return value;
        }

        /// <summary>
        /// 对 `PMNet_PropertySet_&lt;P&gt;` 做**行为**求值（不是数调用次数）：
        /// 在 (属性值 == 参值 / != 参值) × (HasAuthority / 否) 四种组合下，
        /// 观察它是否真的调用了 RawSet、标了哪个槽位。
        ///
        /// 必须这样做的原因：只有「调了 RawSet + 调了 MarkPropertyDirty」这两条计数，
        /// `RawSet(value); MarkPropertyDirty(别的槽位);` 或「不管变没变都标脏」都能蒙过去；
        /// 契约要求的却是「只有变化且 HasAuthority 且 PushBased 才标脏」。
        /// </summary>
        private static HelperRun EvaluateHelper(PropertyPlan plan, bool assumeEqual, bool assumeAuthority)
        {
            MethodDefinition method = plan.PropertySet;
            string what = "生成物 helper " + Loc(method);

            HelperRun run = new HelperRun();
            List<HelperValue> stack = new List<HelperValue>();
            HelperValue[] locals = new HelperValue[method.Body.Variables.Count];
            List<Instruction> instructions = new List<Instruction>(method.Body.Instructions);
            Dictionary<Instruction, int> indexOf = BuildIndex(instructions);

            int pc = 0;
            int steps = 0;
            int maxSteps = (instructions.Count * 8) + 64;

            while (true)
            {
                if (++steps > maxSteps)
                {
                    throw new WeaverException(what + "：控制流无法在有限步内求值（疑似循环），拒绝猜测。");
                }

                if (pc < 0 || pc >= instructions.Count)
                {
                    throw new WeaverException(what + "：控制流跳到了方法体之外，拒绝猜测。");
                }

                Instruction ins = instructions[pc];
                Code code = ins.OpCode.Code;
                int next = pc + 1;

                if (code == Code.Nop)
                {
                    pc = next;
                    continue;
                }

                if (IsLoadConstInt32(ins, out int constant))
                {
                    stack.Add(HvInt(constant));
                    pc = next;
                    continue;
                }

                switch (code)
                {
                    case Code.Ldarg_0:
                    case Code.Ldarg_1:
                    {
                        stack.Add(Hv(code == Code.Ldarg_0 ? HelperKind.This : HelperKind.Param));
                        break;
                    }

                    case Code.Ldsfld:
                    {
                        FieldReference field = ins.Operand as FieldReference;
                        if (field == null || !Targets(field, plan.IndexConst) || plan.IndexConst.Constant == null)
                        {
                            throw new WeaverException(
                                what + "：读取了非本属性槽位常量的静态字段（"
                                + (field == null ? "<null>" : field.Name) + "），形态不符，明确失败。");
                        }

                        stack.Add(HvInt(Convert.ToInt32(
                            plan.IndexConst.Constant, System.Globalization.CultureInfo.InvariantCulture)));
                        break;
                    }

                    case Code.Stloc_0:
                    case Code.Stloc_1:
                    case Code.Stloc_2:
                    case Code.Stloc_3:
                    case Code.Stloc_S:
                    case Code.Stloc:
                    {
                        int index = LocalIndex(ins);
                        if (index < 0 || index >= locals.Length)
                        {
                            throw new WeaverException(what + "：写入了越界局部变量，拒绝猜测。");
                        }

                        locals[index] = PopValue(stack, what);
                        break;
                    }

                    case Code.Ldloc_0:
                    case Code.Ldloc_1:
                    case Code.Ldloc_2:
                    case Code.Ldloc_3:
                    case Code.Ldloc_S:
                    case Code.Ldloc:
                    {
                        int index = LocalIndex(ins);
                        if (index < 0 || index >= locals.Length || locals[index].Kind == HelperKind.Unset)
                        {
                            throw new WeaverException(what + "：读取了未初始化局部变量，拒绝猜测。");
                        }

                        stack.Add(locals[index]);
                        break;
                    }

                    case Code.Pop:
                        PopValue(stack, what);
                        break;

                    case Code.Br:
                    case Code.Br_S:
                        next = TargetIndex(ins.Operand as Instruction, indexOf, what);
                        break;

                    case Code.Brtrue:
                    case Code.Brtrue_S:
                    case Code.Brfalse:
                    case Code.Brfalse_S:
                    {
                        HelperValue value = PopValue(stack, what);
                        bool truth;
                        switch (value.Kind)
                        {
                            case HelperKind.NotEqual:
                                truth = !assumeEqual;
                                break;
                            case HelperKind.Equal:
                                truth = assumeEqual;
                                break;
                            case HelperKind.Authority:
                                truth = assumeAuthority;
                                break;
                            case HelperKind.Int:
                                truth = value.Int != 0;
                                break;
                            default:
                                throw new WeaverException(
                                    what + "：分支条件来自不受支持的值（" + value.Kind
                                    + "），形态无法识别，拒绝猜测。");
                        }

                        bool taken = code == Code.Brtrue || code == Code.Brtrue_S ? truth : !truth;
                        if (taken)
                        {
                            next = TargetIndex(ins.Operand as Instruction, indexOf, what);
                        }

                        break;
                    }

                    case Code.Ceq:
                    {
                        HelperValue b = PopValue(stack, what);
                        HelperValue a = PopValue(stack, what);
                        bool aIsCmp = a.Kind == HelperKind.Equal;
                        bool bIsCmp = b.Kind == HelperKind.Equal;
                        bool aIsZero = a.Kind == HelperKind.Int && a.Int == 0;
                        bool bIsZero = b.Kind == HelperKind.Int && b.Int == 0;

                        if ((aIsCmp && bIsZero) || (bIsCmp && aIsZero))
                        {
                            // `!Equals(...)`：Roslyn 发射 `ldc.i4.0; ceq`。
                            stack.Add(Hv(HelperKind.NotEqual));
                            run.ComparisonPresent = true;
                        }
                        else
                        {
                            throw new WeaverException(
                                what + "：ceq 的两个操作数不是「等值比较结果与 0」（" + a.Kind + " / " + b.Kind
                                + "），形态无法识别，拒绝猜测。");
                        }

                        break;
                    }

                    case Code.Call:
                    case Code.Callvirt:
                    {
                        MethodReference target = ins.Operand as MethodReference;
                        if (target == null)
                        {
                            throw new WeaverException(what + "：调用指令缺少目标，拒绝猜测。");
                        }

                        if (IsEqualityComparerDefault(target, plan))
                        {
                            stack.Add(Hv(HelperKind.Comparer));
                            break;
                        }

                        if (Targets(target, plan.Getter))
                        {
                            HelperValue owner = PopValue(stack, what);
                            if (owner.Kind != HelperKind.This)
                            {
                                throw new WeaverException(what + "：调用属性 getter 时的接收者不是 this，形态不符。");
                            }

                            stack.Add(Hv(HelperKind.Prop));
                            break;
                        }

                        if (IsEqualityComparerEquals(target, plan))
                        {
                            HelperValue valueOperand = PopValue(stack, what);
                            HelperValue propertyOperand = PopValue(stack, what);
                            HelperValue comparerOperand = PopValue(stack, what);
                            if (valueOperand.Kind != HelperKind.Param || propertyOperand.Kind != HelperKind.Prop
                                || comparerOperand.Kind != HelperKind.Comparer)
                            {
                                throw new WeaverException(
                                    what + "：等值比较不是 `EqualityComparer<T>.Default.Equals(属性, value)`"
                                    + "（实际 " + comparerOperand.Kind + " / " + propertyOperand.Kind + " / "
                                    + valueOperand.Kind + "），冻结语义不符，明确失败。");
                            }

                            stack.Add(Hv(HelperKind.Equal));
                            break;
                        }

                        if (Targets(target, plan.RawSet))
                        {
                            HelperValue valueOperand = PopValue(stack, what);
                            HelperValue owner = PopValue(stack, what);
                            if (owner.Kind != HelperKind.This)
                            {
                                throw new WeaverException(what + "：调用 RawSet 时的接收者不是 this，形态不符。");
                            }

                            run.RawSetCalls++;
                            if (valueOperand.Kind == HelperKind.Param)
                            {
                                run.RawSetArgKindOk++;
                            }

                            break;
                        }

                        if (IsHasAuthorityGetter(target))
                        {
                            HelperValue owner = PopValue(stack, what);
                            if (owner.Kind != HelperKind.This)
                            {
                                throw new WeaverException(what + "：读 HasAuthority 的接收者不是 this，形态不符。");
                            }

                            run.AuthorityRead = true;
                            stack.Add(Hv(HelperKind.Authority));
                            break;
                        }

                        if (IsMarkPropertyDirty(target))
                        {
                            HelperValue indexOperand = PopValue(stack, what);
                            HelperValue owner = PopValue(stack, what);
                            if (owner.Kind != HelperKind.This || indexOperand.Kind != HelperKind.Int)
                            {
                                throw new WeaverException(
                                    what + "：MarkPropertyDirty 的参数不是整型常量（或接收者不是 this），形态不符。");
                            }

                            run.DirtyIndices.Add(indexOperand.Int);
                            break;
                        }

                        throw new WeaverException(
                            what + "：调用了非冻结语义的方法（" + DescribeCall(target)
                            + "，IL_" + ins.Offset.ToString("x4") + "）：明确失败，拒绝猜测。");
                    }

                    case Code.Ret:
                        run.Returned = true;
                        if (stack.Count != 0)
                        {
                            throw new WeaverException(what + "：ret 时栈上仍有值，形态不符。");
                        }

                        return run;

                    default:
                        throw new WeaverException(
                            what + "：含求值器不支持的指令 " + ins.OpCode
                            + "（IL_" + ins.Offset.ToString("x4") + "）：拒绝猜测。");
                }

                pc = next;
            }
        }

        /// <summary>
        /// 校验赋值语义 helper：
        ///   · 恰好一次 RawSet(入参)；
        ///   · PushBased=true ⇒ 恰好标脏一次，且**只在** changed && HasAuthority 时；
        ///   · PushBased=false ⇒ 完全不读 HasAuthority、完全不标脏。
        /// </summary>
        private static void RequireAssignmentHelper(PropertyPlan plan)
        {
            string what = "类型 " + plan.Type.FullName + " 的属性 " + plan.MemberName + " 的赋值 helper "
                          + PropertySetPrefix + plan.MemberName;

            HelperRun equalAuthority = EvaluateHelper(plan, true, true);
            HelperRun equalNoAuthority = EvaluateHelper(plan, true, false);
            HelperRun changedAuthority = EvaluateHelper(plan, false, true);
            HelperRun changedNoAuthority = EvaluateHelper(plan, false, false);

            HelperRun[] runs = new HelperRun[]
            {
                equalAuthority, equalNoAuthority, changedAuthority, changedNoAuthority,
            };

            for (int i = 0; i < runs.Length; i++)
            {
                if (!runs[i].Returned)
                {
                    throw new WeaverException(what + "：求值没有走到 ret，形态不符。");
                }

                if (runs[i].RawSetCalls != 1 || runs[i].RawSetArgKindOk != 1)
                {
                    throw new WeaverException(
                        what + "：必须**恰好一次**把入参写到 RawSet（实际 " + runs[i].RawSetCalls
                        + " 次，其中入参形态 " + runs[i].RawSetArgKindOk + " 次）：冻结语义不符，明确失败。");
                }
            }

            if (!plan.PushBased)
            {
                for (int i = 0; i < runs.Length; i++)
                {
                    if (runs[i].AuthorityRead || runs[i].DirtyIndices.Count != 0)
                    {
                        throw new WeaverException(
                            what + "：PushBased=false 的属性不得读 HasAuthority / 标脏（实际 HasAuthority="
                            + runs[i].AuthorityRead + "、标脏 " + runs[i].DirtyIndices.Count
                            + " 次）：契约要求只发射 changed+RawSet 两行，明确失败。");
                    }
                }

                return;
            }

            if (!changedAuthority.ComparisonPresent)
            {
                throw new WeaverException(
                    what + "：PushBased=true 必须发射 "
                    + "`changed = !EqualityComparer<T>.Default.Equals(属性, value)`：冻结语义不符，明确失败。");
            }

            if (equalAuthority.DirtyIndices.Count != 0)
            {
                throw new WeaverException(
                    what + "：值没有变化时也标脏了（槽位 " + JoinInts(equalAuthority.DirtyIndices)
                    + "）：契约要求 only-if-changed，明确失败。");
            }

            if (changedNoAuthority.DirtyIndices.Count != 0)
            {
                throw new WeaverException(
                    what + "：没有权威（HasAuthority=false）时也标脏了（槽位 "
                    + JoinInts(changedNoAuthority.DirtyIndices)
                    + "）：本地副本不得产生权威上行，明确失败。");
            }

            if (changedAuthority.DirtyIndices.Count != 1)
            {
                throw new WeaverException(
                    what + "：值变化且有权威时必须**恰好**标脏一次（实际 "
                    + changedAuthority.DirtyIndices.Count + " 次）：冻结语义不符，明确失败。");
            }

            if (changedAuthority.DirtyIndices[0] != plan.IndexValue)
            {
                throw new WeaverException(
                    what + "：标脏的槽位是 " + changedAuthority.DirtyIndices[0]
                    + "，但本属性的槽位常量 " + plan.IndexConst.Name + " = " + plan.IndexValue
                    + "：槽位串位（会静默标到别的属性上），明确失败。");
            }
        }

        /// <summary>
        /// 生成物的收包 Reader 必须**只走 RawSet**、不调用普通 setter。
        /// 否则收包会经 setter → PropertySet →（在权威端）反向标脏 / 触发业务副作用。
        /// </summary>
        private static void RequireReaderUsesRawSet(PropertyPlan plan)
        {
            MethodDefinition reader = plan.Reader;
            if (reader == null)
            {
                throw new WeaverException(
                    "类型 " + plan.Type.FullName + " 的属性 " + plan.MemberName + " 缺少生成物收包 Reader "
                    + ReaderPrefix + plan.MemberName + "：无法验证「收包只走 RawSet」⇒ 明确失败"
                    + "（修复：确认生成器为该属性发射了 Reader）。");
            }

            if (!reader.HasBody)
            {
                throw new WeaverException("生成物 Reader " + Loc(reader) + " 没有方法体。");
            }

            string what = "生成物 Reader " + Loc(reader);
            int setterCalls = CountCallsTo(reader, plan.Setter);
            int rawSetCalls = CountCallsTo(reader, plan.RawSet);
            int propertySetCalls = CountCallsTo(reader, plan.PropertySet);

            if (setterCalls != 0)
            {
                throw new WeaverException(
                    what + " 调用了普通 setter " + plan.Setter.Name + "（" + setterCalls + " 次）："
                    + "契约要求 auto-property 的收包先解码到临时值、再直接调用 " + RawSetPrefix + plan.MemberName
                    + "，不得经普通 setter（否则收包会反向标脏 / 触发 setter 副作用），明确失败。");
            }

            if (propertySetCalls != 0)
            {
                throw new WeaverException(
                    what + " 调用了赋值 helper " + plan.PropertySet.Name + "（" + propertySetCalls + " 次）："
                    + "收包路径不得走赋值语义（会反向标脏），明确失败。");
            }

            if (rawSetCalls < 1)
            {
                throw new WeaverException(
                    what + " 没有调用 " + RawSetPrefix + plan.MemberName
                    + "：收包无法把解码结果写进本属性（且不得走普通 setter），明确失败。");
            }
        }

        // =================================================================================
        //  编织
        // =================================================================================

        /// <summary>
        /// 改写 setter 与 RawSet，并为两者登记 PDB 探针（sequence point 必须被清空）。
        /// **不触碰 getter、构造初始化器、其它业务体，也不重写程序集里其它 stfld。**
        /// </summary>
        public static void Weave(PropertyPlan[] plans, List<RpcAssemblyWeaver.PdbProbe> probes)
        {
            for (int i = 0; i < plans.Length; i++)
            {
                PropertyPlan plan = plans[i];

                BuildRawSet(plan);
                AddProbe(probes, plan, plan.RawSet);

                BuildSetterForwarder(plan);
                AddProbe(probes, plan, plan.Setter);
            }
        }

        /// <summary>check 模式下的符号探针（被改写的方法都必须没有残留行号）。</summary>
        public static void AddCheckProbes(PropertyPlan[] plans, List<RpcAssemblyWeaver.PdbProbe> probes)
        {
            for (int i = 0; i < plans.Length; i++)
            {
                AddProbe(probes, plans[i], plans[i].Setter);
                AddProbe(probes, plans[i], plans[i].RawSet);
            }
        }

        private static void AddProbe(
            List<RpcAssemblyWeaver.PdbProbe> probes,
            PropertyPlan plan,
            MethodDefinition method)
        {
            RpcAssemblyWeaver.PdbProbe probe = new RpcAssemblyWeaver.PdbProbe();
            probe.TypeFullName = plan.Type.FullName;
            probe.MethodName = method.Name;
            probe.ParameterCount = method.Parameters.Count;
            probe.ExpectedSequencePoints = 0;
            probes.Add(probe);
        }

        private static void BuildSetterForwarder(PropertyPlan plan)
        {
            MethodDefinition method = plan.Setter;
            MethodImplAttributes before = method.ImplAttributes;

            MethodBody body = method.Body;
            body.Instructions.Clear();
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            body.InitLocals = false;
            body.MaxStackSize = 2;

            body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
            body.Instructions.Add(Instruction.Create(OpCodes.Call, plan.PropertySet));
            body.Instructions.Add(Instruction.Create(OpCodes.Ret));

            if (method.ImplAttributes != before)
            {
                throw new WeaverException(
                    "内部一致性错误：改写 setter 时实现标志被改变（" + Loc(method) + "）。");
            }

            ClearSequencePoints(method);
        }

        private static void BuildRawSet(PropertyPlan plan)
        {
            MethodDefinition method = plan.RawSet;
            MethodImplAttributes setterFlags = plan.Setter.ImplAttributes;

            // 只允许抄「标志位」，不允许把 setter 的实现代码类型（IL/Native/Runtime、托管/非托管）
            // 一并抄到收包路径上：我们改写的是 IL 体，代码类型必须保持 IL + Managed。
            const MethodImplAttributes codeMask =
                MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask;
            if ((setterFlags & codeMask) != (method.ImplAttributes & codeMask))
            {
                throw new WeaverException(
                    "类型 " + plan.Type.FullName + " 的属性 " + plan.MemberName + " 的 setter 实现代码类型与 RawSet 不一致"
                    + "（setter=" + (int)setterFlags + "、RawSet=" + (int)method.ImplAttributes
                    + "）：拒绝把非 IL/托管标志抄到收包路径上。");
            }

            MethodBody body = method.Body;
            body.Instructions.Clear();
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            body.InitLocals = false;
            body.MaxStackSize = 2;

            body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
            body.Instructions.Add(Instruction.Create(OpCodes.Stfld, plan.Backing));
            body.Instructions.Add(Instruction.Create(OpCodes.Ret));

            // ★ 继承原 setter 的实现标志（契约 §2：RawSet stub 改为 stfld 形态时
            //   「保留 setter MethodImpl 标志，必要同步标志不能丢」）。
            method.ImplAttributes = setterFlags;
            if (method.ImplAttributes != setterFlags)
            {
                throw new WeaverException(
                    "内部一致性错误：把 setter 的实现标志继承给 RawSet 时被改变（" + Loc(method) + "）。");
            }

            ClearSequencePoints(method);
        }

        /// <summary>
        /// 清空被改写方法的 sequence point（合成桩的旧行号指向已经不存在的源码位置），
        /// 与 RPC 编织器的 wrapper 处理一致：**清空而不新增**。
        /// </summary>
        private static void ClearSequencePoints(MethodDefinition method)
        {
            MethodDebugInformation debug = method.DebugInformation;
            if (debug != null)
            {
                debug.SequencePoints.Clear();
                debug.Scope = null;
            }
        }

        // =================================================================================
        //  通用工具
        // =================================================================================

        private static string Loc(MethodDefinition method)
        {
            return method.DeclaringType.FullName + "." + method.Name;
        }

        private static MethodDefinition FindMethod(TypeDefinition type, string name)
        {
            for (int i = 0; i < type.Methods.Count; i++)
            {
                if (string.Equals(type.Methods[i].Name, name, StringComparison.Ordinal))
                {
                    return type.Methods[i];
                }
            }

            return null;
        }

        private static FieldDefinition FindField(TypeDefinition type, string name)
        {
            for (int i = 0; i < type.Fields.Count; i++)
            {
                if (string.Equals(type.Fields[i].Name, name, StringComparison.Ordinal))
                {
                    return type.Fields[i];
                }
            }

            return null;
        }

        private static bool HasExactAttribute(ICustomAttributeProvider provider, string fullName)
        {
            if (provider == null || !provider.HasCustomAttributes)
            {
                return false;
            }

            for (int i = 0; i < provider.CustomAttributes.Count; i++)
            {
                TypeReference attributeType = provider.CustomAttributes[i].AttributeType;
                if (attributeType != null && string.Equals(attributeType.FullName, fullName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Targets(MethodReference reference, MethodDefinition target)
        {
            if (reference == null || target == null)
            {
                return false;
            }

            if (!string.Equals(reference.Name, target.Name, StringComparison.Ordinal))
            {
                return false;
            }

            TypeReference declaring = reference.DeclaringType;
            if (declaring == null
                || !string.Equals(declaring.FullName, target.DeclaringType.FullName, StringComparison.Ordinal))
            {
                return false;
            }

            if (reference.Parameters.Count != target.Parameters.Count)
            {
                return false;
            }

            for (int i = 0; i < target.Parameters.Count; i++)
            {
                string a = target.Parameters[i].ParameterType == null
                    ? null
                    : target.Parameters[i].ParameterType.FullName;
                string b = reference.Parameters[i].ParameterType == null
                    ? null
                    : reference.Parameters[i].ParameterType.FullName;
                if (!string.Equals(a, b, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Targets(FieldReference reference, FieldDefinition target)
        {
            if (reference == null || target == null)
            {
                return false;
            }

            if (!string.Equals(reference.Name, target.Name, StringComparison.Ordinal))
            {
                return false;
            }

            TypeReference declaring = reference.DeclaringType;
            return declaring != null
                   && string.Equals(declaring.FullName, target.DeclaringType.FullName, StringComparison.Ordinal);
        }

        private static int CountCallsTo(MethodDefinition owner, MethodDefinition target)
        {
            if (owner == null || !owner.HasBody || target == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < owner.Body.Instructions.Count; i++)
            {
                Instruction ins = owner.Body.Instructions[i];
                if (ins.OpCode.Code != Code.Call && ins.OpCode.Code != Code.Callvirt)
                {
                    continue;
                }

                if (Targets(ins.Operand as MethodReference, target))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool IsEqualityComparerDefault(MethodReference reference, PropertyPlan plan)
        {
            if (reference == null
                || !string.Equals(reference.Name, "get_Default", StringComparison.Ordinal)
                || reference.HasThis)
            {
                return false;
            }

            return MatchesEqualityComparerOf(reference, plan);
        }

        private static bool IsEqualityComparerEquals(MethodReference reference, PropertyPlan plan)
        {
            if (reference == null
                || !string.Equals(reference.Name, "Equals", StringComparison.Ordinal)
                || reference.Parameters.Count != 2)
            {
                return false;
            }

            return MatchesEqualityComparerOf(reference, plan);
        }

        private static bool MatchesEqualityComparerOf(MethodReference reference, PropertyPlan plan)
        {
            GenericInstanceType generic = reference.DeclaringType as GenericInstanceType;
            if (generic == null
                || !string.Equals(generic.ElementType.FullName, EqualityComparerElementFullName, StringComparison.Ordinal)
                || generic.GenericArguments.Count != 1)
            {
                return false;
            }

            return string.Equals(
                generic.GenericArguments[0].FullName, plan.ValueType.FullName, StringComparison.Ordinal);
        }

        private static bool IsHasAuthorityGetter(MethodReference reference)
        {
            return reference != null
                   && string.Equals(reference.Name, "get_HasAuthority", StringComparison.Ordinal)
                   && reference.DeclaringType != null
                   && string.Equals(reference.DeclaringType.FullName, PMNetObjectFullName, StringComparison.Ordinal)
                   && reference.Parameters.Count == 0
                   && reference.HasThis;
        }

        private static bool IsMarkPropertyDirty(MethodReference reference)
        {
            return reference != null
                   && string.Equals(reference.Name, "MarkPropertyDirty", StringComparison.Ordinal)
                   && reference.DeclaringType != null
                   && string.Equals(reference.DeclaringType.FullName, PMNetObjectFullName, StringComparison.Ordinal)
                   && reference.Parameters.Count == 1
                   && reference.HasThis;
        }

        private static bool IsInvalidOperationExceptionCtor(MethodReference reference)
        {
            return reference != null
                   && string.Equals(reference.Name, ".ctor", StringComparison.Ordinal)
                   && reference.DeclaringType != null
                   && string.Equals(
                       reference.DeclaringType.FullName, InvalidOperationExceptionFullName, StringComparison.Ordinal)
                   && reference.Parameters.Count == 1;
        }

        private static string DescribeCall(MethodReference reference)
        {
            if (reference == null)
            {
                return "<null>";
            }

            string owner = reference.DeclaringType == null ? "<未知类型>" : reference.DeclaringType.FullName;
            return owner + "::" + reference.Name + "，参数 " + reference.Parameters.Count + " 个";
        }

        private static string JoinInts(List<int> values)
        {
            List<string> parts = new List<string>();
            for (int i = 0; i < values.Count; i++)
            {
                parts.Add(values[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return string.Join(", ", parts.ToArray());
        }

        private static Dictionary<Instruction, int> BuildIndex(List<Instruction> instructions)
        {
            Dictionary<Instruction, int> index = new Dictionary<Instruction, int>();
            for (int i = 0; i < instructions.Count; i++)
            {
                index[instructions[i]] = i;
            }

            return index;
        }

        private static int TargetIndex(Instruction target, Dictionary<Instruction, int> indexOf, string what)
        {
            if (target == null || !indexOf.TryGetValue(target, out int index))
            {
                throw new WeaverException(what + "：分支目标不在方法体内，拒绝猜测。");
            }

            return index;
        }

        private static HelperValue PopValue(List<HelperValue> stack, string what)
        {
            if (stack.Count == 0)
            {
                throw new WeaverException(what + "：求值时栈下溢，形态无法识别，拒绝猜测。");
            }

            HelperValue value = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            return value;
        }

        private static StackKind Pop(List<StackKind> stack, string what)
        {
            if (stack.Count == 0)
            {
                throw new WeaverException(what + " 栈下溢，形态不符。");
            }

            StackKind value = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            return value;
        }

        private static StackKind Pop(List<StackKind> stack)
        {
            StackKind value = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            return value;
        }

        private static List<Instruction> StripNops(MethodDefinition method)
        {
            List<Instruction> list = new List<Instruction>();
            for (int i = 0; i < method.Body.Instructions.Count; i++)
            {
                Instruction ins = method.Body.Instructions[i];
                if (ins.OpCode.Code != Code.Nop)
                {
                    list.Add(ins);
                }
            }

            return list;
        }

        private static string Describe(List<Instruction> body)
        {
            List<string> parts = new List<string>();
            for (int i = 0; i < body.Count && i < 12; i++)
            {
                parts.Add(body[i].OpCode.ToString());
            }

            if (body.Count > 12)
            {
                parts.Add("…共 " + body.Count + " 条");
            }

            return string.Join(" ", parts.ToArray());
        }

        private static bool IsLdarg(Instruction ins, int ilIndex)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldarg_0:
                    return ilIndex == 0;
                case Code.Ldarg_1:
                    return ilIndex == 1;
                case Code.Ldarg_2:
                    return ilIndex == 2;
                case Code.Ldarg_3:
                    return ilIndex == 3;
                default:
                    return false;
            }
        }

        private static int LocalIndex(Instruction ins)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldloc_0:
                case Code.Stloc_0:
                    return 0;
                case Code.Ldloc_1:
                case Code.Stloc_1:
                    return 1;
                case Code.Ldloc_2:
                case Code.Stloc_2:
                    return 2;
                case Code.Ldloc_3:
                case Code.Stloc_3:
                    return 3;
                default:
                {
                    VariableDefinition local = ins.Operand as VariableDefinition;
                    return local == null ? -1 : local.Index;
                }
            }
        }

        private static bool IsLoadConstInt32(Instruction ins, out int value)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldc_I4_M1: value = -1; return true;
                case Code.Ldc_I4_0: value = 0; return true;
                case Code.Ldc_I4_1: value = 1; return true;
                case Code.Ldc_I4_2: value = 2; return true;
                case Code.Ldc_I4_3: value = 3; return true;
                case Code.Ldc_I4_4: value = 4; return true;
                case Code.Ldc_I4_5: value = 5; return true;
                case Code.Ldc_I4_6: value = 6; return true;
                case Code.Ldc_I4_7: value = 7; return true;
                case Code.Ldc_I4_8: value = 8; return true;
                case Code.Ldc_I4_S:
                    value = Convert.ToInt32((sbyte)ins.Operand, System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                case Code.Ldc_I4:
                    value = Convert.ToInt32(ins.Operand, System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                default:
                    value = 0;
                    return false;
            }
        }
    }
}
