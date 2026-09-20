// PMNet 线格式基础类型。
// 本项目保留 protobuf 作为线格式（决策 D3），因此这里的枚举值与 protobuf 规范一致。

namespace PMNet
{
    /// <summary>
    /// protobuf wire type（低 3 位）。
    /// 与 Google.Protobuf 的 WireFormat.WireType 取值一一对应，用于逐字节兼容。
    /// </summary>
    public enum PMWireType
    {
        /// <summary>可变长整数（varint）。int32/int64/uint32/uint64/bool/enum 等。</summary>
        Varint = 0,

        /// <summary>固定 8 字节，小端。double/fixed64/sfixed64。</summary>
        Fixed64 = 1,

        /// <summary>长度前缀 + 原始字节。string/bytes/嵌套 message/packed repeated。</summary>
        LengthDelimited = 2,

        /// <summary>固定 4 字节，小端。float/fixed32/sfixed32。</summary>
        Fixed32 = 5,
    }

    /// <summary>
    /// protobuf 标量类型。生成器据此选择编码方式。
    /// </summary>
    public enum PMScalarType
    {
        None = 0,
        Double,
        Float,
        Int32,
        Int64,
        UInt32,
        UInt64,
        SInt32,
        SInt64,
        Fixed32,
        Fixed64,
        SFixed32,
        SFixed64,
        Bool,
        String,
        Bytes,
    }

    /// <summary>
    /// 复制条件（对应 UE 的 ELifetimeCondition / COND_*）。
    /// 决定一个已注册复制的属性在什么条件下允许发给某条连接。
    /// </summary>
    /// <summary>
    /// 属性复制条件（对应 UE 的 `ELifetimeCondition`）。
    ///
    /// **数值刻意与 UE 一致**（`Source/Runtime/CoreUObject/Public/UObject/CoreNetTypes.h`）：
    /// UE 共 18 项（含 `COND_Max` 哨兵），本项目首版只实现 D-R0-14 冻结的 8 项。
    /// 沿用 UE 原值的好处是迁移期可以直接对照引擎源码与工具输出，
    /// 不需要在两套编号之间来回换算——换算表是最容易悄悄写错一行的东西。
    ///
    /// **明确不做**（定义时会被生成器报错拒绝，不做静默降级）：
    /// `InitialOnly(1) / SimulatedOrPhysics(6) / InitialOrOwner(7) / ReplayOrOwner(9) /
    /// ReplayOnly(10) / SimulatedOnlyNoReplay(11) / SimulatedOrPhysicsNoReplay(12) /
    /// SkipReplay(13) / NetGroup(16)`。
    /// 其中 `NetGroup` 在 UE 注释里写明「Not usable on properties」。
    ///
    /// 语义口径以 **legacy（`RepLayout`）为准**，不是 Iris —— 两者在这些条件上有实测差异
    /// （R0_3 C-14），不能混用。
    /// </summary>
    public enum PMCond : byte
    {
        /// <summary>无条件：变化就发。对应 UE `COND_None = 0`。</summary>
        None = 0,

        /// <summary>只发给拥有该对象的连接。对应 UE `COND_OwnerOnly = 2`。用于修 mana/能量信息泄露。</summary>
        OwnerOnly = 2,

        /// <summary>除拥有者外都发。对应 UE `COND_SkipOwner = 3`。用于模拟移动等。</summary>
        SkipOwner = 3,

        /// <summary>只发给非拥有者（即模拟副本）。对应 UE `COND_SimulatedOnly = 4`。</summary>
        SimulatedOnly = 4,

        /// <summary>只发给拥有者（即自治副本）。对应 UE `COND_AutonomousOnly = 5`。</summary>
        AutonomousOnly = 5,

        /// <summary>
        /// 条件本身不表达任何排除，但允许运行期用 `SetCustomIsActiveOverride` 按连接开/关。
        /// 对应 UE `COND_Custom = 8`。
        /// </summary>
        Custom = 8,

        /// <summary>
        /// 允许运行期把条件**改写成另一条**条件；未覆盖前默认「总是复制」。
        /// 对应 UE `COND_Dynamic = 14`。
        ///
        /// 与 <see cref="Custom"/> 的区别（这两个极易混）：
        /// `Custom` 是「条件照旧、额外给一个按连接的开关」；
        /// `Dynamic` 是「换掉判定条件本身」。
        /// </summary>
        Dynamic = 14,

        /// <summary>永不复制。对应 UE `COND_Never = 15`。</summary>
        Never = 15,
    }

    /// <summary>
    /// 网络休眠状态（对应 UE 的 ENetDormancy）。
    /// </summary>
    public enum PMNetDormancy : byte
    {
        /// <summary>不休眠，每帧都参与调度。</summary>
        Never = 0,

        /// <summary>初始休眠：首次同步后即休眠，直到被唤醒。</summary>
        Initial = 1,

        /// <summary>变更休眠：有变更时自动唤醒。</summary>
        Partial = 2,
    }
}
