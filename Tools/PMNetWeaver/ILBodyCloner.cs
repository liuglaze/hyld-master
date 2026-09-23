// ILBodyCloner —— 把一个方法的业务体**完整**克隆成一个新的私有方法。
//
// 契约：Docs/plans/net-rpc-weaving-contract.md（§2 编织第 2 步）
//   "完整克隆原业务体到 private `PMNet_RpcBody_<M>`：参数/局部/分支/switch/异常处理/
//    调试sequence point正确重映射；原调用点身份保留，业务体无RPC Attribute。"
//
// 为什么需要它：编织的语义是"同一份业务体、两条入口"（本地直调 / 收包），
// 因此克隆必须逐位保留控制流与调试信息；任何"重新生成一份等价源码"的做法都会在
// switch 跳转表、异常处理区间、闭包捕获、局部变量槽位上产生偏差。
//
// 实现要点（逐条对应 Mono.Cecil 的表示）：
//   1. 指令按 OpCode **原样**复制（保留 short/long 形式），第二遍再重映射操作数；
//   2. 分支目标 / switch 跳转表 / 异常处理边界一律按 Instruction 映射表重定向；
//   3. 局部变量按**下标顺序**复制，VariableDefinition 操作数按下标重映射；
//   4. 参数（含隐式 this）操作数重映射到新方法的参数；
//   5. sequence point 按"旧 IL 偏移 → 旧指令 → 新指令"重映射；局部变量作用域树同步复制；
//   6. **不复制** RPC Attribute（契约明确要求业务体无 RPC Attribute）；
//   7. 任何无法映射的形态（未知操作数类型、越界局部/参数）都抛 WeaverException，
//      不允许"猜一个值继续写"。

using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PMNetWeaver
{
    /// <summary>把一个方法的业务体克隆成另一个方法。</summary>
    internal static class ILBodyCloner
    {
        /// <summary>
        /// 克隆 <paramref name="source"/> 的方法体，返回一个新的私有实例方法。
        ///
        /// 新方法**不带**任何自定义 Attribute、不带 virtual/abstract 等修饰，
        /// 只保留：返回类型、参数（名字/类型）、局部变量、指令、异常处理、调试信息。
        /// </summary>
        public static MethodDefinition CloneBody(MethodDefinition source, string newName)
        {
            if (source == null)
            {
                throw new WeaverException("内部错误：待克隆方法为空。");
            }

            if (!source.HasBody)
            {
                throw new WeaverException("内部错误：待克隆方法没有方法体：" + source.FullName);
            }

            if (string.IsNullOrEmpty(newName))
            {
                throw new WeaverException("内部错误：新方法名为空。");
            }

            MethodDefinition clone = new MethodDefinition(
                newName,
                MethodAttributes.Private | MethodAttributes.HideBySig,
                source.ReturnType);

            // 保留Synchronized/NoInlining等实现标志。收包直接调用私有体，不能只在wrapper保留锁。
            clone.ImplAttributes = source.ImplAttributes;

            for (int i = 0; i < source.Parameters.Count; i++)
            {
                ParameterDefinition p = source.Parameters[i];
                // 只保留名字与类型：契约禁止 ref/out/in/params/默认值，因此不复制这些修饰。
                ParameterDefinition np = new ParameterDefinition(p.Name, ParameterAttributes.None, p.ParameterType);
                clone.Parameters.Add(np);
            }

            MethodBody sb = source.Body;
            MethodBody nb = clone.Body;
            nb.InitLocals = sb.InitLocals;
            nb.MaxStackSize = sb.MaxStackSize;

            // ── 局部变量：按顺序复制，下标保持一致 ──────────────────────────
            VariableDefinition[] oldLocals = new VariableDefinition[sb.Variables.Count];
            for (int i = 0; i < sb.Variables.Count; i++)
            {
                oldLocals[i] = sb.Variables[i];
            }

            VariableDefinition[] newLocals = new VariableDefinition[oldLocals.Length];
            for (int i = 0; i < oldLocals.Length; i++)
            {
                newLocals[i] = new VariableDefinition(oldLocals[i].VariableType);
                nb.Variables.Add(newLocals[i]);
            }

            Dictionary<Instruction, Instruction> instructionMap = new Dictionary<Instruction, Instruction>();
            Dictionary<int, Instruction> byOffset = new Dictionary<int, Instruction>();

            for (int i = 0; i < sb.Instructions.Count; i++)
            {
                Instruction old = sb.Instructions[i];
                Instruction created = CreateShell(old);
                instructionMap[old] = created;
                nb.Instructions.Add(created);

                if (byOffset.ContainsKey(old.Offset))
                {
                    throw new WeaverException(
                        "内部错误：源方法 " + source.FullName + " 存在重复 IL 偏移 " + old.Offset + "。");
                }

                byOffset[old.Offset] = old;
            }

            for (int i = 0; i < sb.Instructions.Count; i++)
            {
                Instruction old = sb.Instructions[i];
                Instruction created = instructionMap[old];
                created.Operand = RemapOperand(old, instructionMap, oldLocals, newLocals, sb, nb, clone, source);
            }

            // ── 异常处理：四个边界 + filter 起点全部重映射 ──────────────────
            for (int i = 0; i < sb.ExceptionHandlers.Count; i++)
            {
                ExceptionHandler old = sb.ExceptionHandlers[i];
                ExceptionHandler created = new ExceptionHandler(old.HandlerType);
                created.TryStart = MapInstruction(old.TryStart, instructionMap);
                created.TryEnd = MapInstruction(old.TryEnd, instructionMap);
                created.HandlerStart = MapInstruction(old.HandlerStart, instructionMap);
                created.HandlerEnd = MapInstruction(old.HandlerEnd, instructionMap);
                created.FilterStart = MapInstruction(old.FilterStart, instructionMap);
                created.CatchType = old.CatchType;
                nb.ExceptionHandlers.Add(created);
            }

            CloneDebugInformation(source, clone, instructionMap, byOffset, newLocals);
            return clone;
        }

        // =================================================================================
        //  指令
        // =================================================================================

        /// <summary>
        /// 生成一个"壳"指令：OpCode 原样，操作数先指向**旧**值（只为满足 Create 的类型校验），
        /// 第二遍统一重映射。Cecil 的 Instruction(OpCode, object) 构造函数是 internal，
        /// 因此必须按 OperandType 分派到对应的 Create 重载。
        /// </summary>
        private static Instruction CreateShell(Instruction old)
        {
            object operand = old.Operand;
            switch (old.OpCode.OperandType)
            {
                case OperandType.InlineNone:
                    return Instruction.Create(old.OpCode);

                case OperandType.InlineType:
                    return Instruction.Create(old.OpCode, (TypeReference)operand);

                case OperandType.InlineField:
                    return Instruction.Create(old.OpCode, (FieldReference)operand);

                case OperandType.InlineMethod:
                    return Instruction.Create(old.OpCode, (MethodReference)operand);

                case OperandType.InlineTok:
                {
                    TypeReference type = operand as TypeReference;
                    if (type != null)
                    {
                        return Instruction.Create(old.OpCode, type);
                    }

                    FieldReference field = operand as FieldReference;
                    if (field != null)
                    {
                        return Instruction.Create(old.OpCode, field);
                    }

                    MethodReference method = operand as MethodReference;
                    if (method != null)
                    {
                        return Instruction.Create(old.OpCode, method);
                    }

                    throw new WeaverException("无法克隆 InlineTok 指令（操作数类型 " + Describe(operand) + "）：" + old.OpCode);
                }

                case OperandType.InlineString:
                    return Instruction.Create(old.OpCode, (string)operand);

                case OperandType.ShortInlineI:
                {
                    if (operand is sbyte)
                    {
                        return Instruction.Create(old.OpCode, (sbyte)operand);
                    }

                    if (operand is byte)
                    {
                        return Instruction.Create(old.OpCode, (byte)operand);
                    }

                    throw new WeaverException("无法克隆 ShortInlineI 指令（操作数类型 " + Describe(operand) + "）：" + old.OpCode);
                }

                case OperandType.InlineI:
                    return Instruction.Create(old.OpCode, (int)operand);

                case OperandType.InlineI8:
                    return Instruction.Create(old.OpCode, (long)operand);

                case OperandType.ShortInlineR:
                    return Instruction.Create(old.OpCode, (float)operand);

                case OperandType.InlineR:
                    return Instruction.Create(old.OpCode, (double)operand);

                case OperandType.InlineBrTarget:
                case OperandType.ShortInlineBrTarget:
                    return Instruction.Create(old.OpCode, (Instruction)operand);

                case OperandType.InlineSwitch:
                    return Instruction.Create(old.OpCode, (Instruction[])operand);

                case OperandType.InlineVar:
                case OperandType.ShortInlineVar:
                {
                    VariableDefinition local = operand as VariableDefinition;
                    if (local != null)
                    {
                        return Instruction.Create(old.OpCode, local);
                    }

                    ParameterDefinition parameter = operand as ParameterDefinition;
                    if (parameter != null)
                    {
                        return Instruction.Create(old.OpCode, parameter);
                    }

                    throw new WeaverException("无法克隆 变量/参数 指令（操作数类型 " + Describe(operand) + "）：" + old.OpCode);
                }

                case OperandType.InlineArg:
                case OperandType.ShortInlineArg:
                    return Instruction.Create(old.OpCode, (ParameterDefinition)operand);

                case OperandType.InlineSig:
                    return Instruction.Create(old.OpCode, (CallSite)operand);

                default:
                    throw new WeaverException(
                        "无法克隆的指令操作数类型 " + old.OpCode.OperandType + "（" + old.OpCode + "）：明确失败，不猜。");
            }
        }

        private static object RemapOperand(
            Instruction old,
            Dictionary<Instruction, Instruction> instructionMap,
            VariableDefinition[] oldLocals,
            VariableDefinition[] newLocals,
            MethodBody sourceBody,
            MethodBody newBody,
            MethodDefinition clone,
            MethodDefinition source)
        {
            object operand = old.Operand;
            if (operand == null)
            {
                return null;
            }

            Instruction[] targets = operand as Instruction[];
            if (targets != null)
            {
                Instruction[] mapped = new Instruction[targets.Length];
                for (int i = 0; i < targets.Length; i++)
                {
                    mapped[i] = MapInstruction(targets[i], instructionMap);
                }

                return mapped;
            }

            Instruction target = operand as Instruction;
            if (target != null)
            {
                return MapInstruction(target, instructionMap);
            }

            VariableDefinition local = operand as VariableDefinition;
            if (local != null)
            {
                for (int i = 0; i < oldLocals.Length; i++)
                {
                    if (ReferenceEquals(oldLocals[i], local))
                    {
                        return newLocals[i];
                    }
                }

                throw new WeaverException(
                    "内部错误：指令 " + old.OpCode + " 引用了方法 " + source.FullName + " 之外的局部变量。");
            }

            ParameterDefinition parameter = operand as ParameterDefinition;
            if (parameter != null)
            {
                if (ReferenceEquals(parameter, sourceBody.ThisParameter) || parameter.Index < 0)
                {
                    ParameterDefinition self = newBody.ThisParameter;
                    if (self == null)
                    {
                        throw new WeaverException(
                            "内部错误：克隆 " + source.FullName + " 时无法取得新方法的隐式 this。");
                    }

                    return self;
                }

                if (parameter.Index >= 0 && parameter.Index < clone.Parameters.Count)
                {
                    return clone.Parameters[parameter.Index];
                }

                throw new WeaverException(
                    "内部错误：指令 " + old.OpCode + " 引用了越界参数（Index=" + parameter.Index + "）。");
            }

            // TypeReference / FieldReference / MethodReference / string / 各类常量：同一模块内无需重定向。
            return operand;
        }

        private static Instruction MapInstruction(
            Instruction instruction,
            Dictionary<Instruction, Instruction> instructionMap)
        {
            if (instruction == null)
            {
                return null;
            }

            Instruction mapped;
            if (!instructionMap.TryGetValue(instruction, out mapped))
            {
                throw new WeaverException(
                    "内部错误：控制流/异常处理边界指向了方法体外的指令（offset " + instruction.Offset + "）。");
            }

            return mapped;
        }

        private static string Describe(object operand)
        {
            return operand == null ? "<null>" : operand.GetType().Name;
        }

        // =================================================================================
        //  调试信息
        // =================================================================================

        private static void CloneDebugInformation(
            MethodDefinition source,
            MethodDefinition clone,
            Dictionary<Instruction, Instruction> instructionMap,
            Dictionary<int, Instruction> byOffset,
            VariableDefinition[] newLocals)
        {
            MethodDebugInformation sourceDebug = source.DebugInformation;
            MethodDebugInformation cloneDebug = clone.DebugInformation;

            if (sourceDebug == null || cloneDebug == null)
            {
                return;
            }

            // sequence point：按旧 IL 偏移定位旧指令，再映射到新指令（行/列/隐藏位原样保留）。
            for (int i = 0; i < sourceDebug.SequencePoints.Count; i++)
            {
                SequencePoint old = sourceDebug.SequencePoints[i];
                Instruction oldInstruction;
                if (!byOffset.TryGetValue(old.Offset, out oldInstruction))
                {
                    throw new WeaverException(
                        "内部错误：sequence point 的偏移 " + old.Offset + " 在方法 " + source.FullName
                        + " 里找不到对应指令，无法正确重映射调试信息。");
                }

                SequencePoint created = new SequencePoint(instructionMap[oldInstruction], old.Document);
                created.StartLine = old.StartLine;
                created.StartColumn = old.StartColumn;
                created.EndLine = old.EndLine;
                created.EndColumn = old.EndColumn;
                cloneDebug.SequencePoints.Add(created);
            }

            // 局部变量作用域树（局部变量名/槽位）。
            cloneDebug.Scope = CloneScope(sourceDebug.Scope, instructionMap, byOffset, newLocals);

            // 方法级自定义调试信息（例如 Debug 构建里的 EnC 槽位映射）：
            // 与源方法共享同一份数据 —— 局部变量下标在克隆里保持不变，因此仍然自洽。
            if (sourceDebug.HasCustomDebugInformations)
            {
                for (int i = 0; i < sourceDebug.CustomDebugInformations.Count; i++)
                {
                    cloneDebug.CustomDebugInformations.Add(sourceDebug.CustomDebugInformations[i]);
                }
            }
        }

        private static ScopeDebugInformation CloneScope(
            ScopeDebugInformation source,
            Dictionary<Instruction, Instruction> instructionMap,
            Dictionary<int, Instruction> byOffset,
            VariableDefinition[] newLocals)
        {
            if (source == null)
            {
                return null;
            }

            Instruction start = MapBoundary(source.Start, instructionMap, byOffset);
            Instruction end = MapBoundary(source.End, instructionMap, byOffset);
            ScopeDebugInformation clone = new ScopeDebugInformation(start, end);

            for (int i = 0; i < source.Variables.Count; i++)
            {
                VariableDebugInformation v = source.Variables[i];
                if (v.Index < 0 || v.Index >= newLocals.Length)
                {
                    throw new WeaverException(
                        "内部错误：作用域里的局部变量下标 " + v.Index + " 越界（局部变量数 " + newLocals.Length + "）。");
                }

                VariableDebugInformation created = new VariableDebugInformation(newLocals[v.Index], v.Name);
                created.Attributes = v.Attributes;
                clone.Variables.Add(created);
            }

            if (source.Import != null)
            {
                clone.Import = source.Import;
            }

            if (source.HasCustomDebugInformations)
            {
                for (int i = 0; i < source.CustomDebugInformations.Count; i++)
                {
                    clone.CustomDebugInformations.Add(source.CustomDebugInformations[i]);
                }
            }

            for (int i = 0; i < source.Scopes.Count; i++)
            {
                clone.Scopes.Add(CloneScope(source.Scopes[i], instructionMap, byOffset, newLocals));
            }

            return clone;
        }

        private static Instruction MapBoundary(
            InstructionOffset offset,
            Dictionary<Instruction, Instruction> instructionMap,
            Dictionary<int, Instruction> byOffset)
        {
            if (offset.IsEndOfMethod)
            {
                return null;
            }

            Instruction old;
            if (!byOffset.TryGetValue(offset.Offset, out old))
            {
                // 作用域边界指向方法末尾之外的偏移（编译器可能给"结束于方法尾"的作用域一个
                // 越界偏移）；此时按"到方法尾"处理，不猜具体指令。
                return null;
            }

            return instructionMap[old];
        }
    }
}
