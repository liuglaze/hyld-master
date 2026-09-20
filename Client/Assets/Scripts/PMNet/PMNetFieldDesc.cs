namespace PMNet
{
    /// <summary>
    /// 字段描述符：由生成器为每个 proto 字段产出一条，供复制层与调试工具按字段号查元信息。
    ///
    /// 对应 UE/Iris 侧的「属性描述符」概念的最小形态：
    /// P0 阶段只承载「字段号 + 名字 + 线格式 + 是否 repeated」，
    /// 后续复制层会在此基础上扩展条件、量化器与 OnRep 回调（见 Docs/plans/net-architecture-migration.md §4.3）。
    ///
    /// 使用 readonly struct 以避免装箱；对应 C# 7.2 语法，Unity 2019.4 可用。
    /// </summary>
    public readonly struct PMNetFieldDesc
    {
        /// <summary>proto 字段号。</summary>
        public readonly int Number;

        /// <summary>proto 中的字段名（原样，例如 battle_net_sim_config）。</summary>
        public readonly string Name;

        /// <summary>该字段的线格式。</summary>
        public readonly PMWireType WireType;

        /// <summary>是否 repeated。</summary>
        public readonly bool Repeated;

        public PMNetFieldDesc(int number, string name, PMWireType wireType, bool repeated)
        {
            Number = number;
            Name = name;
            WireType = wireType;
            Repeated = repeated;
        }
    }
}
