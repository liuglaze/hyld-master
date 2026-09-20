// ============================================================================
//  PMMoverModel —— 纯 C# 可回滚运动模型（R4-A / M08）
// ============================================================================
//
//  契约来源：Docs/plans/net-r4-prediction-contract.md「Mover 冻结字段与机制」；
//  共享只读接口：Client/Assets/Scripts/PMPrediction/PMPredictionContracts.cs
//  （IPMPredictionModel<TInput,TSync,TAux>，namespace PMNet.Prediction）。
//
//  本文件实现该接口的 Mover 具体化：
//      TInput = PMMoverInput, TSync = PMMoverSyncState, TAux = PMMoverAuxState
//
//  确定性铁律（契约「确定性」+ skill 红线 2/3）：
//    · 相同 (step, input, start, aux) 必须给出**逐位相同**的输出；
//      禁止墙钟、随机、静态可变数据、对象身份哈希、字典遍历顺序。
//    · 不得修改 start（引用型成员一律按不可变对待）；不得产生任何外部副作用
//      （不改 Unity 物体、不发网络、不写文件、不打日志）。
//    · IsResimulating 不改变任何行为：重放必须复现同一结果。
//
//  本批**明确不做**（报告"未实现项"逐条列出，不注释假装有）：
//    · 子步循环 / "剩余时间让给下一模式"（一帧一次积分：模式切换从下一帧起生效）
//    · 斜坡、台阶（step-up）、移动平台（BaseCarry）
//    · 空中操控（Falling 不施加水平控制，只保留水平速度）
//    · Modifier（数据类/行为类全部后置）、行为类补偿
//    · 效果"拒绝裁决"（谁有权下发、如何撤销）——本批只消费已鉴权的可信命令
//    · Unity 物理等价性（PMMoverTestWorld 是确定性替身，不是 PhysX）
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet.Prediction;

namespace PMNet.Mover
{
    /// <summary>
    /// 四模式（Walking/Falling/Flying/Inactive）+ 两层混合（Additive/Override）+ 两阶段
    /// 瞬时效果的纯状态运动模型。
    ///
    /// 每帧管线（顺序即契约，逐条对应报告里的"实际验证"）：
    ///   0. 校验 step / input / start / aux（非法即**抛出**，不静默修正）
    ///   1. 深拷贝 start → 工作状态（start 之后只读）
    ///   2. **阶段 A**：消费 InputCmd 携带的 Effect 队列（帧前）
    ///   3. 叠加层命令：显式移除 → 新增/替换 → 帧首清扫到期层 → 规范排序
    ///   4. 层混合：Additive 求和 / Override 覆盖（存在 Override 即排除全部 Additive）
    ///   5. 模式推进：Walking 地面查询+平面速度 / Falling 重力+落地 / Flying 三维控制 /
    ///      Inactive 零位移；积分走"扫掠 + 有界滑动"
    ///   6. **阶段 B**：消费**帧内**新入队的 Effect（本批唯一生产者：Falling→Walking
    ///      落地时的竖直速度钳制，对应 UE 在 CaptureFinalState 写回 OutputState 的语义）
    ///   7. 帧末：活跃层 ElapsedMs 按**原 dt** 累加
    ///   8. 输出新 SyncState（新数组，无别名）+ Aux 拷贝 + **空事件集**
    ///
    /// 事件说明（诚实口径）：本批模型**不产生**任何不可逆事件。
    /// Mode / Grounded / GroundNormal 都是可回滚的 SyncState 状态，不是事件；
    /// 不可逆事件的去重与"只随 ConfirmedFrame 通知一次"属预测组 timeline 的职责
    /// （P4A1），因此这里返回空数组而不是造一个假的稳定 Key 体系。
    ///
    /// 线程纪律：本类无静态可变状态，但只允许在主线程（D13 局内权威在主线程）使用。
    /// </summary>
    public sealed class PMMoverModel : IPMPredictionModel<PMMoverInput, PMMoverSyncState, PMMoverAuxState>
    {
        private readonly IPMMoverCollisionQuery _query;

        /// <summary>
        /// 用给定的碰撞查询构造模型。"用哪个碰撞世界"只有这一处注入点。
        /// </summary>
        public PMMoverModel(IPMMoverCollisionQuery query)
        {
            if (query == null)
            {
                throw new ArgumentNullException("query");
            }

            _query = query;
        }

        /// <summary>本模型使用的碰撞查询（诊断/测试断言用，只读）。</summary>
        public IPMMoverCollisionQuery CollisionQuery { get { return _query; } }

        // =================================================================================
        //  深拷贝（契约「深 clone 三类，不得产生数组别名」）
        // =================================================================================

        public PMMoverInput CloneInput(PMMoverInput input)
        {
            PMMoverInput copy = input;

            // 列表必须逐元素复制：直接赋引用会让历史缓冲与调用方共享同一份命令数组，
            // 之后任何一处改写都会污染回滚重放。
            if (input.Effects != null)
            {
                PMMoverEffectRequest[] effects = new PMMoverEffectRequest[input.Effects.Length];
                Array.Copy(input.Effects, effects, input.Effects.Length);
                copy.Effects = effects;
            }

            if (input.Layers != null)
            {
                PMMoverLayerRequest[] layers = new PMMoverLayerRequest[input.Layers.Length];
                Array.Copy(input.Layers, layers, input.Layers.Length);
                copy.Layers = layers;
            }

            if (input.RemovedLayerIds != null)
            {
                uint[] ids = new uint[input.RemovedLayerIds.Length];
                Array.Copy(input.RemovedLayerIds, ids, input.RemovedLayerIds.Length);
                copy.RemovedLayerIds = ids;
            }

            return copy;
        }

        public PMMoverSyncState CloneSync(PMMoverSyncState sync)
        {
            PMMoverSyncState copy = sync;

            // 数组是唯一的引用型成员，必须复制；其余全是值类型，赋值即完整拷贝。
            copy.ActiveLayers = CloneLayers(sync.ActiveLayers);
            return copy;
        }

        /// <summary>
        /// Aux 全是值类型（3 个字段：向量 + 2 个 int），赋值本身就是完整拷贝。
        /// 这里显式返回而不是返回 null，是为了让契约"三类都可深拷贝"在代码层成立。
        /// </summary>
        public PMMoverAuxState CloneAux(PMMoverAuxState aux)
        {
            return aux;
        }

        // =================================================================================
        //  模拟
        // =================================================================================

        public PMSimulationResult<PMMoverSyncState, PMMoverAuxState> Simulate(
            PMTimeStep step, PMMoverInput input, PMMoverSyncState start, PMMoverAuxState aux)
        {
            ValidateStep(step);
            ValidateInput(input);
            ValidateSync(start);
            ValidateAux(aux);

            int stepMs = (int)step.StepMs;
            float dtSec = step.StepMs * 0.001f;

            // ---- 1) 工作状态：start 之后只读 ----
            WorkState work = new WorkState();
            work.Position = start.Position;
            work.CleanVelocity = start.PreAdditiveVelocity;
            work.FinalVelocity = start.Velocity;
            work.YawDegrees = start.YawDegrees;
            work.Mode = start.Mode;
            work.Grounded = start.Grounded;
            work.GroundNormal = start.GroundNormal;
            work.Scale = start.Scale;
            work.MaxSpeed = start.MaxSpeed;
            work.Acceleration = start.Acceleration;
            work.Braking = start.Braking;
            work.GravityScale = start.GravityScale;
            work.JumpSpeed = start.JumpSpeed;
            work.Layers = CopyLayers(start.ActiveLayers);

            // 本帧的效果队列：阶段 A 装入 InputCmd 的可信命令；移动过程中模型自己入队（阶段 B 消费）。
            List<PMMoverEffectRequest> pending = CloneEffectQueue(input.Effects);

            // ---- 2) 阶段 A：帧前消费 ----
            DrainEffects(pending, ref work);

            // ---- 3) 层命令 ----
            ApplyLayerCommands(input, ref work);

            // ---- 4)+5) 层混合 + 模式推进 + 扫掠积分 ----
            LayerMix mix = ComputeLayerMix(work.Layers);
            StepModes(ref work, input, aux, mix, dtSec, pending);

            // ---- 6) 阶段 B：帧内排队后消费 ----
            DrainEffects(pending, ref work);

            // ---- 7) 层进度按原 dt 累加（Inactive 也走这里：时间轴不停）----
            TickLayerElapsed(work.Layers, stepMs);

            // ---- 8) 输出 ----
            PMMoverSyncState result = new PMMoverSyncState();
            result.Position = work.Position;
            result.Velocity = work.FinalVelocity;
            result.PreAdditiveVelocity = work.CleanVelocity;
            result.YawDegrees = NormalizeDegrees(work.YawDegrees);
            result.Mode = work.Mode;
            result.Grounded = work.Grounded;
            result.GroundNormal = work.GroundNormal;
            result.Scale = work.Scale;
            result.MaxSpeed = work.MaxSpeed;
            result.Acceleration = work.Acceleration;
            result.Braking = work.Braking;
            result.GravityScale = work.GravityScale;
            result.JumpSpeed = work.JumpSpeed;
            result.ActiveLayers = work.Layers.Count == 0 ? null : work.Layers.ToArray();

            PMSimulationResult<PMMoverSyncState, PMMoverAuxState> output =
                new PMSimulationResult<PMMoverSyncState, PMMoverAuxState>();
            output.Sync = result;
            output.Aux = aux;
            output.Events = new PMPredictionEvent[0];
            return output;
        }

        // =================================================================================
        //  reconcile
        // =================================================================================

        /// <summary>
        /// 比较顺序即契约「模式差异优先」：先离散状态（Mode/Grounded），再精确配置
        /// （Scale/有效参数/活跃层实例定义），后连续量（位置/速度/朝向/地面法线），最后 Aux。
        ///
        /// 阈值取自 <see cref="PMMoverDefaults"/>（契约：位置 0.05m、速度 0.01m/s、朝向 1 度）。
        /// 层比较用"实例定义"（不含 ElapsedMs 进度），理由见 PMMoverLayer 注释。
        /// </summary>
        public bool ShouldReconcile(PMMoverSyncState predicted, PMMoverSyncState authority,
                                    PMMoverAuxState predictedAux, PMMoverAuxState authorityAux)
        {
            // 1) 模式优先（离散）
            if (predicted.Mode != authority.Mode)
            {
                return true;
            }

            // 2) 着地（离散；它决定 Walking/Falling 的后续判定）
            if (predicted.Grounded != authority.Grounded)
            {
                return true;
            }

            // 3) 精确配置：缩放 + 有效运动参数（任何一处不同都是真实配置分歧）
            if (predicted.Scale != authority.Scale
                || predicted.MaxSpeed != authority.MaxSpeed
                || predicted.Acceleration != authority.Acceleration
                || predicted.Braking != authority.Braking
                || predicted.GravityScale != authority.GravityScale
                || predicted.JumpSpeed != authority.JumpSpeed)
            {
                return true;
            }

            // 4) 活跃层实例定义（按 InstanceId 匹配，不依赖数组顺序）
            if (!LayersMatchDefinition(predicted.ActiveLayers, authority.ActiveLayers))
            {
                return true;
            }

            // 5) 连续量阈值比较
            if (PMVector3.Distance(predicted.Position, authority.Position)
                > PMMoverDefaults.PositionToleranceMeters)
            {
                return true;
            }

            if (PMVector3.Distance(predicted.Velocity, authority.Velocity)
                > PMMoverDefaults.VelocityToleranceMetersPerSecond)
            {
                return true;
            }

            if (PMVector3.Distance(predicted.PreAdditiveVelocity, authority.PreAdditiveVelocity)
                > PMMoverDefaults.VelocityToleranceMetersPerSecond)
            {
                return true;
            }

            if (YawDeltaDegrees(predicted.YawDegrees, authority.YawDegrees)
                > PMMoverDefaults.YawToleranceDegrees)
            {
                return true;
            }

            // 6) 地面法线：只在双方都着地时比较（未着地时法线无意义）
            if (predicted.Grounded && authority.Grounded
                && AngleBetweenDegrees(predicted.GroundNormal, authority.GroundNormal)
                   > PMMoverDefaults.GroundNormalToleranceDegrees)
            {
                return true;
            }

            // 7) Aux：重力与版本变化都参与（契约「AuxGravity/版本变化参与 reconcile」）
            if (predictedAux.CollisionWorldVersion != authorityAux.CollisionWorldVersion)
            {
                return true;
            }

            if (predictedAux.ConfigVersion != authorityAux.ConfigVersion)
            {
                return true;
            }

            if (VectorAbsDiffExceeds(predictedAux.Gravity, authorityAux.Gravity,
                                    PMMoverDefaults.GravityToleranceMetersPerSecondSquared))
            {
                return true;
            }

            return false;
        }

        // =================================================================================
        //  插值（SP：首批只插值，不外推）
        // =================================================================================

        /// <summary>
        /// 连续量插值、离散量取 <paramref name="to"/>。
        ///
        ///   · 连续：Position / Velocity / PreAdditiveVelocity（线性），YawDegrees（最短弧）
        ///   · 离散取 To：Mode / Grounded / Scale / 有效参数 / 活跃层集合
        ///   · GroundNormal：两端着地状态一致时插值后归一化，否则取 To
        ///
        /// 返回的 SyncState 持有**新**层数组（无别名）。
        /// </summary>
        public PMMoverSyncState Interpolate(PMMoverSyncState from, PMMoverSyncState to, float alpha)
        {
            float a = alpha;
            if (float.IsNaN(a))
            {
                a = 0f;
            }

            if (a < 0f) { a = 0f; }
            if (a > 1f) { a = 1f; }

            PMMoverSyncState r = new PMMoverSyncState();
            r.Position = Lerp(from.Position, to.Position, a);
            r.Velocity = Lerp(from.Velocity, to.Velocity, a);
            r.PreAdditiveVelocity = Lerp(from.PreAdditiveVelocity, to.PreAdditiveVelocity, a);
            r.YawDegrees = LerpYawShortestArc(from.YawDegrees, to.YawDegrees, a);

            // 离散量：取 To（插值一个模式/着地位没有意义）
            r.Mode = to.Mode;
            r.Grounded = to.Grounded;
            r.Scale = to.Scale;
            r.MaxSpeed = to.MaxSpeed;
            r.Acceleration = to.Acceleration;
            r.Braking = to.Braking;
            r.GravityScale = to.GravityScale;
            r.JumpSpeed = to.JumpSpeed;

            if (from.Grounded == to.Grounded)
            {
                PMVector3 blended = Lerp(from.GroundNormal, to.GroundNormal, a);
                r.GroundNormal = blended.LengthSquared > PMVector3.Epsilon
                    ? PMVector3.Normalized(blended)
                    : to.GroundNormal;
            }
            else
            {
                r.GroundNormal = to.GroundNormal;
            }

            r.ActiveLayers = CloneLayers(to.ActiveLayers);
            return r;
        }

        // =================================================================================
        //  工作状态
        // =================================================================================

        /// <summary>帧内可变的工作副本。全部字段都是从 start 拷来的，绝不回写 start。</summary>
        private struct WorkState
        {
            public PMVector3 Position;
            public PMVector3 CleanVelocity;
            public PMVector3 FinalVelocity;
            public float YawDegrees;
            public PMMoverMode Mode;
            public bool Grounded;
            public PMVector3 GroundNormal;
            public float Scale;
            public float MaxSpeed;
            public float Acceleration;
            public float Braking;
            public float GravityScale;
            public float JumpSpeed;
            public List<PMMoverLayer> Layers;
        }

        /// <summary>活跃层的混合结果。"存在 Override"由 HasOverride 表达（它是门槛开关）。</summary>
        private struct LayerMix
        {
            public bool HasOverride;
            public PMVector3 OverrideVelocity;
            public PMVector3 AdditiveSum;
        }

        // =================================================================================
        //  帧内步骤
        // =================================================================================

        /// <summary>
        /// 消费一整个效果队列并清空。阶段 A（帧前）与阶段 B（帧内新入队）复用同一实现，
        /// 保证两条消费路径的语义完全一致。
        /// </summary>
        private void DrainEffects(List<PMMoverEffectRequest> pending, ref WorkState work)
        {
            if (pending == null || pending.Count == 0)
            {
                return;
            }

            for (int i = 0; i < pending.Count; i++)
            {
                ApplyEffect(pending[i], ref work);
            }

            pending.Clear();
        }

        /// <summary>
        /// 应用一条效果。**只写工作状态**，不碰引擎、不发网络。
        ///
        /// 未知种类 / 未知模式值 -> 抛出（"明确拒绝"，禁止默认回退到 Walking）。
        /// 数值合法性在 <see cref="ValidateInput"/> 已校验；这里再查一次枚举是因为
        /// 阶段 B 的入队者也可能犯错，而"静默采用一个猜测值"是最坏的失败方式。
        /// </summary>
        private static void ApplyEffect(PMMoverEffectRequest effect, ref WorkState work)
        {
            switch (effect.Kind)
            {
                case PMMoverEffectKind.SetVelocity:
                    // SetVelocity 是"速度命令"：同时写最终速度与干净基速。
                    // 层的贡献在下一帧按定义重新计算（因此不会残留）。
                    work.CleanVelocity = effect.Vector;
                    work.FinalVelocity = effect.Vector;
                    return;

                case PMMoverEffectKind.Teleport:
                    work.Position = effect.Vector;
                    return;

                case PMMoverEffectKind.SetMode:
                    if (!PMMoverModes.IsDefined(effect.Mode))
                    {
                        throw new ArgumentException(
                            "[PMMoverModel] SetMode 收到未知模式值 " + effect.Mode
                            + "：必须显式拒绝，禁止默认回退到 Walking。");
                    }

                    work.Mode = (PMMoverMode)effect.Mode;
                    return;

                case PMMoverEffectKind.SetParameters:
                    work.MaxSpeed = effect.MaxSpeed;
                    work.Acceleration = effect.Acceleration;
                    work.Braking = effect.Braking;
                    work.GravityScale = effect.GravityScale;
                    work.JumpSpeed = effect.JumpSpeed;
                    return;

                default:
                    throw new ArgumentException(
                        "[PMMoverModel] 未知 Effect 种类 " + (int)effect.Kind + "（拒绝执行）。");
            }
        }

        /// <summary>
        /// 叠加层命令：显式移除 → 新增/替换 → 帧首清扫到期 → 规范排序。
        ///
        /// 顺序理由：移除必须先于新增（同一帧"先删后加"是唯一可解释的语义）；
        /// 清扫放在新增之后，是为了让"刚加入的层"（进度 0）不会被误判为到期。
        /// </summary>
        private static void ApplyLayerCommands(PMMoverInput input, ref WorkState work)
        {
            if (input.RemovedLayerIds != null)
            {
                for (int i = 0; i < input.RemovedLayerIds.Length; i++)
                {
                    RemoveLayer(work.Layers, input.RemovedLayerIds[i]);
                }
            }

            if (input.Layers != null)
            {
                for (int i = 0; i < input.Layers.Length; i++)
                {
                    PMMoverLayer layer = input.Layers[i].ToLayer();
                    int existing = IndexOfLayer(work.Layers, layer.InstanceId);
                    if (existing >= 0)
                    {
                        // 同 id 再请求 = 替换（进度重置）。这比"静默忽略"更可解释：
                        // 层身份相同意味着调用方在表达"这个实例重新开始"。
                        work.Layers[existing] = layer;
                    }
                    else
                    {
                        work.Layers.Add(layer);
                    }
                }
            }

            for (int i = work.Layers.Count - 1; i >= 0; i--)
            {
                if (work.Layers[i].IsExpired)
                {
                    work.Layers.RemoveAt(i);
                }
            }

            SortLayers(work.Layers);
        }

        /// <summary>
        /// 层混合。
        ///
        /// 语义（契约「AdditiveVelocity 与 OverrideVelocity 按 priority/id 确定顺序」+
        /// 「Additive 门槛只排除 OverrideVelocity」）：
        ///   · 已按 (Priority, InstanceId) **升序**排列，因此遍历中"后写入者胜出"，
        ///     等价于"最大 (Priority, InstanceId) 的 Override 层获胜"；
        ///   · 只要存在任意 OverrideVelocity 层，**本帧全部 Additive 层都不参与混合**；
        ///   · 否则最终叠加量 = 各 Additive 速度之和（按升序相加，保证逐位确定）。
        /// </summary>
        private static LayerMix ComputeLayerMix(List<PMMoverLayer> layers)
        {
            LayerMix mix = new LayerMix();
            mix.HasOverride = false;
            mix.OverrideVelocity = PMVector3.Zero;
            mix.AdditiveSum = PMVector3.Zero;

            for (int i = 0; i < layers.Count; i++)
            {
                PMMoverLayer layer = layers[i];
                switch (layer.Kind)
                {
                    case PMMoverLayerKind.AdditiveVelocity:
                        mix.AdditiveSum = mix.AdditiveSum + layer.Velocity;
                        break;

                    case PMMoverLayerKind.OverrideVelocity:
                        mix.HasOverride = true;
                        mix.OverrideVelocity = layer.Velocity;
                        break;

                    default:
                        // ValidateSync 已经拒绝过未知种类；这里保留 default 是为了
                        // "任何 switch 都必须有 default"这一条（漏 case 曾导致穿墙类缺陷）。
                        throw new InvalidOperationException(
                            "[PMMoverModel] 未知叠加层种类 " + (int)layer.Kind + "。");
                }
            }

            return mix;
        }

        /// <summary>把模式自驱速度与层贡献混合成最终速度。</summary>
        private static PMVector3 MixVelocity(PMVector3 modeVelocity, LayerMix mix)
        {
            if (mix.HasOverride)
            {
                return mix.OverrideVelocity;
            }

            return modeVelocity + mix.AdditiveSum;
        }

        /// <summary>
        /// 模式推进 + 积分。模式切换用"重新分派"结构而不是递归，
        /// 并带转换次数上限（防御性：R0 契约要求"连续满额退还需上限保护"）。
        ///
        /// 本批**没有子步循环**：一帧只积分一次，因此 Walking→Falling 的切换在**本帧**
        /// 立即用 Falling 的规则积分整段 dt（不做"剩余时间移交"），而 Falling→Walking
        /// 的落地切换在**下一帧**才用 Walking 规则。这是 R4-A 的明确取舍，已在报告登记。
        /// </summary>
        private void StepModes(ref WorkState work, PMMoverInput input, PMMoverAuxState aux,
                              LayerMix mix, float dtSec, List<PMMoverEffectRequest> inFrame)
        {
            work.YawDegrees = input.YawDegrees;

            int transitions = 0;

            while (true)
            {
                bool switched = false;

                switch (work.Mode)
                {
                    case PMMoverMode.Walking:
                    {
                        float radius = EffectiveRadius(work.Scale);
                        float halfHeight = EffectiveHalfHeight(work.Scale);

                        PMMoverGround ground = _query.QueryGround(
                            work.Position, radius, halfHeight, PMMoverDefaults.GroundProbeMeters);

                        if (!ground.Found)
                        {
                            // 失去支撑：本帧改按 Falling 规则走（地面状态有 1 帧滞后的反面——
                            // 这里是"先感知再积分"，所以不会悬空停住）。
                            work.Mode = PMMoverMode.Falling;
                            work.Grounded = false;
                            work.GroundNormal = PMVector3.Up;
                            switched = true;
                            break;
                        }

                        work.Grounded = true;
                        work.GroundNormal = ground.Normal;

                        PMVector3 planar = NormalizePlanar(input.MoveX, input.MoveZ);
                        PMVector3 target = planar * work.MaxSpeed;
                        PMVector3 current = new PMVector3(work.CleanVelocity.X, 0f, work.CleanVelocity.Z);
                        float rate = planar.LengthSquared <= PMVector3.Epsilon
                            ? work.Braking
                            : work.Acceleration;
                        current = MoveTowards(current, target, rate * dtSec);

                        if (input.JumpPressed)
                        {
                            // 起跳边沿：切 Falling，并把竖直速度设为起跳速度；
                            // 本帧剩下的积分按 Falling（含重力）走，不做剩余时间移交。
                            work.CleanVelocity = new PMVector3(current.X, work.JumpSpeed, current.Z);
                            work.Mode = PMMoverMode.Falling;
                            work.Grounded = false;
                            work.GroundNormal = PMVector3.Up;
                            switched = true;
                            break;
                        }

                        // Walking 平面速度：竖直分量恒为 0（首批不做斜坡/台阶）
                        work.CleanVelocity = new PMVector3(current.X, 0f, current.Z);
                        work.FinalVelocity = MixVelocity(work.CleanVelocity, mix);
                        Integrate(ref work, work.FinalVelocity * dtSec, radius, halfHeight);
                        return;
                    }

                    case PMMoverMode.Falling:
                    {
                        float radius = EffectiveRadius(work.Scale);
                        float halfHeight = EffectiveHalfHeight(work.Scale);

                        float gravityY = aux.Gravity.Y * work.GravityScale;
                        work.CleanVelocity = new PMVector3(
                            work.CleanVelocity.X,
                            work.CleanVelocity.Y + gravityY * dtSec,
                            work.CleanVelocity.Z);
                        work.FinalVelocity = MixVelocity(work.CleanVelocity, mix);

                        PMVector3 delta = work.FinalVelocity * dtSec;

                        if (delta.Y <= 0f)
                        {
                            // 落地判定复用同一地面查询：探测距离 = 本帧下落量 + 极小余量。
                            PMMoverGround ground = _query.QueryGround(
                                work.Position, radius, halfHeight,
                                -delta.Y + PMMoverDefaults.LandingProbeEpsilonMeters);

                            if (ground.Found)
                            {
                                // 把角色放到支撑面上（Distance<0 即穿透时向上顶出）。
                                work.Position = new PMVector3(
                                    work.Position.X,
                                    work.Position.Y - ground.Distance,
                                    work.Position.Z);
                                delta = new PMVector3(delta.X, 0f, delta.Z);

                                work.Grounded = true;
                                work.GroundNormal = ground.Normal;
                                work.Mode = PMMoverMode.Walking;

                                // 帧内入队（阶段 B 消费）：落地后竖直速度必须清零，
                                // 但**本帧位移仍使用落地前速度**——这正是"两阶段消费"在
                                // 本批唯一的、真实存在的帧内生产者（对应 UE 在
                                // CaptureFinalState 里写回 OutputState 的语义）。
                                inFrame.Add(PMMoverEffectRequest.SetVelocity(
                                    new PMVector3(work.FinalVelocity.X, 0f, work.FinalVelocity.Z)));
                            }
                            else
                            {
                                work.Grounded = false;
                                work.GroundNormal = PMVector3.Up;
                            }
                        }

                        Integrate(ref work, delta, radius, halfHeight);

                        // 落地后的模式切换从下一帧生效（无子步循环，见方法注释）。
                        return;
                    }

                    case PMMoverMode.Flying:
                    {
                        float radius = EffectiveRadius(work.Scale);
                        float halfHeight = EffectiveHalfHeight(work.Scale);

                        PMVector3 dir = PMVector3.Normalized(
                            new PMVector3(input.MoveX, input.MoveY, input.MoveZ));
                        PMVector3 target = dir * work.MaxSpeed;
                        float rate = dir.LengthSquared <= PMVector3.Epsilon
                            ? work.Braking
                            : work.Acceleration;
                        work.CleanVelocity = MoveTowards(work.CleanVelocity, target, rate * dtSec);
                        work.FinalVelocity = MixVelocity(work.CleanVelocity, mix);

                        Integrate(ref work, work.FinalVelocity * dtSec, radius, halfHeight);

                        // 飞行不切换模式，但刷新地面信息：Grounded/GroundNormal 属 SyncState，
                        // 表现与 reconcile 都要用（例如"飞行贴着地面"）。
                        PMMoverGround ground = _query.QueryGround(
                            work.Position, radius, halfHeight, PMMoverDefaults.GroundProbeMeters);
                        work.Grounded = ground.Found;
                        work.GroundNormal = ground.Found ? ground.Normal : PMVector3.Up;
                        return;
                    }

                    case PMMoverMode.Inactive:
                        // 零位移：模式自驱速度为 0，且层在本模式下不参与位移（"零位移"是硬语义）。
                        // 但时间轴继续：层的 ElapsedMs 仍按原 dt 累加（见 TickLayerElapsed）。
                        work.CleanVelocity = PMVector3.Zero;
                        work.FinalVelocity = PMVector3.Zero;
                        return;

                    default:
                        throw new ArgumentException(
                            "[PMMoverModel] 未知运动模式 " + (int)work.Mode + "（拒绝推进）。");
                }

                if (switched)
                {
                    transitions++;
                    if (transitions > PMMoverDefaults.MaxModeTransitionsPerStep)
                    {
                        throw new InvalidOperationException(
                            "[PMMoverModel] 单帧模式切换次数超过上限 "
                            + PMMoverDefaults.MaxModeTransitionsPerStep + "（疑似抖动切换）。");
                    }

                    continue;
                }

                return;
            }
        }

        /// <summary>
        /// 有界扫掠滑动。保证三件事：不穿墙、不除零、不无限循环。
        ///
        /// 算法（每一步都只做"到接触点 + 沿法线推出 skin + 去掉法向分量"）：
        ///   1. 对剩余位移做一次扫掠；未阻挡则走完并结束；
        ///   2. 阻挡则先移动到接触点（Fraction 已钳到 [0,1]）；
        ///   3. 沿**背离障碍物**的法线推出 <see cref="PMMoverDefaults.CollisionSkinMeters"/>，
        ///      以免下一次扫掠在零距离上重复命中同一面；
        ///   4. 去掉剩余位移的法向分量，进入下一轮。
        ///
        /// 收敛保护：退化法线（长度≈0）直接结束；"Fraction≤0 且法向投影≈0"（完全无法前进）
        /// 也直接结束。迭代上限后**丢弃**剩余位移——宁可少走一帧，绝不穿墙。
        /// </summary>
        private void Integrate(ref WorkState work, PMVector3 delta, float radius, float halfHeight)
        {
            PMVector3 remaining = delta;

            for (int i = 0; i < PMMoverDefaults.MaxSlideIterations; i++)
            {
                if (remaining.LengthSquared <= PMVector3.Epsilon)
                {
                    return;
                }

                PMMoverHit hit = _query.Sweep(work.Position, remaining, radius, halfHeight);
                if (!hit.Blocking)
                {
                    work.Position = work.Position + remaining;
                    return;
                }

                float fraction = Clamp01(hit.Fraction);
                if (fraction > 0f)
                {
                    work.Position = work.Position + remaining * fraction;
                }

                PMVector3 normal = hit.Normal;
                float normalLength = normal.Length;
                if (normalLength <= PMVector3.Epsilon)
                {
                    // 退化法线：无法确定滑动方向，直接结束（不猜、不除零）。
                    return;
                }

                normal = normal * (1f / normalLength);
                work.Position = work.Position + normal * PMMoverDefaults.CollisionSkinMeters;

                float projection = PMVector3.Dot(remaining, normal);
                remaining = remaining - normal * projection;

                if (fraction <= 0f && Math.Abs(projection) <= PMVector3.Epsilon)
                {
                    // 起点已重叠且没有切向分量：再迭代也不会前进。
                    return;
                }
            }
        }

        /// <summary>活跃层存活时间按**原 dt** 累加（毫秒，整数值）。</summary>
        private static void TickLayerElapsed(List<PMMoverLayer> layers, int stepMs)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                PMMoverLayer layer = layers[i];
                layer.ElapsedMs = layer.ElapsedMs + stepMs;
                layers[i] = layer;
            }
        }

        // =================================================================================
        //  校验（fail-fast：非法输入抛出，绝不静默修正 / 默认回退）
        // =================================================================================

        /// <summary>
        /// 时间步校验。契约把首批 dt 冻结为**整毫秒 int、范围 1..50**：
        ///   · 0 仅用于 DS 缺帧占位，且"不调用模型"——所以模型收到 0 是契约违反；
        ///   · 负数/NaN/Infinity 一律拒绝；
        ///   · 非整毫秒拒绝（"不得合并或放大 dt"的另一面：也不许悄悄改变时间粒度）。
        /// </summary>
        private static void ValidateStep(PMTimeStep step)
        {
            if (!step.IsWellFormed())
            {
                throw new ArgumentException(
                    "[PMMoverModel] 非法 PMTimeStep（NaN/Infinity/负步长/负累计时间）：StepMs="
                    + step.StepMs + " BaseSimTimeMs=" + step.BaseSimTimeMs);
            }

            float ms = step.StepMs;
            if (ms < PMMoverDefaults.MinStepMs || ms > PMMoverDefaults.MaxStepMs)
            {
                throw new ArgumentOutOfRangeException(
                    "step", "[PMMoverModel] StepMs 超出首批范围 ["
                    + PMMoverDefaults.MinStepMs + ", " + PMMoverDefaults.MaxStepMs + "]：" + ms);
            }

            float rounded = (float)Math.Round((double)ms);
            if (Math.Abs(ms - rounded) > 1e-3f)
            {
                throw new ArgumentException(
                    "[PMMoverModel] StepMs 必须是整毫秒（首批冻结决策）：" + ms);
            }
        }

        private static void ValidateInput(PMMoverInput input)
        {
            RequireFinite(input.MoveX, "Input.MoveX");
            RequireFinite(input.MoveZ, "Input.MoveZ");
            RequireFinite(input.MoveY, "Input.MoveY");
            RequireFinite(input.YawDegrees, "Input.YawDegrees");

            if (input.Effects != null)
            {
                for (int i = 0; i < input.Effects.Length; i++)
                {
                    PMMoverEffectRequest effect = input.Effects[i];
                    if (!PMMoverEffectKinds.IsDefined(effect.Kind))
                    {
                        throw new ArgumentException(
                            "[PMMoverModel] 未知 Effect 种类 " + (int)effect.Kind + "（拒绝，不默认）。");
                    }

                    switch (effect.Kind)
                    {
                        case PMMoverEffectKind.SetVelocity:
                            RequireFinite(effect.Vector, "Effect[SetVelocity].Vector");
                            break;

                        case PMMoverEffectKind.Teleport:
                            RequireFinite(effect.Vector, "Effect[Teleport].Vector");
                            break;

                        case PMMoverEffectKind.SetMode:
                            if (!PMMoverModes.IsDefined(effect.Mode))
                            {
                                throw new ArgumentException(
                                    "[PMMoverModel] SetMode 未知模式值 " + effect.Mode
                                    + "（拒绝，不默认回退到 Walking）。");
                            }

                            break;

                        case PMMoverEffectKind.SetParameters:
                            RequireFinite(effect.MaxSpeed, "Effect[SetParameters].MaxSpeed");
                            RequireFinite(effect.Acceleration, "Effect[SetParameters].Acceleration");
                            RequireFinite(effect.Braking, "Effect[SetParameters].Braking");
                            RequireFinite(effect.GravityScale, "Effect[SetParameters].GravityScale");
                            RequireFinite(effect.JumpSpeed, "Effect[SetParameters].JumpSpeed");
                            RequireNonNegative(effect.MaxSpeed, "Effect[SetParameters].MaxSpeed");
                            RequireNonNegative(effect.Acceleration, "Effect[SetParameters].Acceleration");
                            RequireNonNegative(effect.Braking, "Effect[SetParameters].Braking");
                            RequireNonNegative(effect.JumpSpeed, "Effect[SetParameters].JumpSpeed");
                            break;

                        default:
                            throw new ArgumentException(
                                "[PMMoverModel] 未知 Effect 种类 " + (int)effect.Kind + "（拒绝）。");
                    }
                }
            }

            if (input.Layers != null)
            {
                for (int i = 0; i < input.Layers.Length; i++)
                {
                    PMMoverLayerRequest request = input.Layers[i];
                    if (request.InstanceId == 0u)
                    {
                        throw new ArgumentException("[PMMoverModel] 层请求 InstanceId 不能为 0。");
                    }

                    if (!PMMoverLayerKinds.IsDefined(request.Kind))
                    {
                        throw new ArgumentException(
                            "[PMMoverModel] 未知层种类 " + (int)request.Kind + "（拒绝，不默认成 Additive）。");
                    }

                    RequireFinite(request.Velocity, "LayerRequest.Velocity");

                    if (request.DurationMs < 0)
                    {
                        throw new ArgumentException(
                            "[PMMoverModel] 层请求 DurationMs 不能为负（0 表示无时长）："
                            + request.DurationMs);
                    }
                }
            }

            if (input.RemovedLayerIds != null)
            {
                for (int i = 0; i < input.RemovedLayerIds.Length; i++)
                {
                    if (input.RemovedLayerIds[i] == 0u)
                    {
                        throw new ArgumentException("[PMMoverModel] RemovedLayerIds 不能包含 0。");
                    }
                }
            }
        }

        private static void ValidateSync(PMMoverSyncState sync)
        {
            RequireFinite(sync.Position, "Sync.Position");
            RequireFinite(sync.Velocity, "Sync.Velocity");
            RequireFinite(sync.PreAdditiveVelocity, "Sync.PreAdditiveVelocity");
            RequireFinite(sync.GroundNormal, "Sync.GroundNormal");
            RequireFinite(sync.YawDegrees, "Sync.YawDegrees");
            RequireFinite(sync.Scale, "Sync.Scale");
            RequireFinite(sync.MaxSpeed, "Sync.MaxSpeed");
            RequireFinite(sync.Acceleration, "Sync.Acceleration");
            RequireFinite(sync.Braking, "Sync.Braking");
            RequireFinite(sync.GravityScale, "Sync.GravityScale");
            RequireFinite(sync.JumpSpeed, "Sync.JumpSpeed");
            RequireNonNegative(sync.MaxSpeed, "Sync.MaxSpeed");
            RequireNonNegative(sync.Acceleration, "Sync.Acceleration");
            RequireNonNegative(sync.Braking, "Sync.Braking");
            RequireNonNegative(sync.JumpSpeed, "Sync.JumpSpeed");

            if (!(sync.Scale > 0f))
            {
                throw new ArgumentException(
                    "[PMMoverModel] Sync.Scale 必须为正（它同时缩放碰撞尺寸）：" + sync.Scale);
            }

            if (!PMMoverModes.IsDefined(sync.Mode))
            {
                throw new ArgumentException(
                    "[PMMoverModel] Sync.Mode 是未知模式值 " + (int)sync.Mode + "（拒绝推进）。");
            }

            if (sync.ActiveLayers != null)
            {
                for (int i = 0; i < sync.ActiveLayers.Length; i++)
                {
                    PMMoverLayer layer = sync.ActiveLayers[i];
                    if (layer.InstanceId == 0u)
                    {
                        throw new ArgumentException("[PMMoverModel] Sync.ActiveLayers 含 InstanceId=0 的层。");
                    }

                    if (!PMMoverLayerKinds.IsDefined(layer.Kind))
                    {
                        throw new ArgumentException(
                            "[PMMoverModel] Sync.ActiveLayers 含未知层种类 " + (int)layer.Kind + "。");
                    }

                    RequireFinite(layer.Velocity, "Sync.ActiveLayers.Velocity");

                    if (layer.ElapsedMs < 0)
                    {
                        throw new ArgumentException(
                            "[PMMoverModel] Sync.ActiveLayers.ElapsedMs 不能为负：" + layer.ElapsedMs);
                    }
                }
            }
        }

        private static void ValidateAux(PMMoverAuxState aux)
        {
            RequireFinite(aux.Gravity, "Aux.Gravity");
        }

        private static void RequireFinite(float value, string what)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentException("[PMMoverModel] " + what + " 不是有限数：" + value);
            }
        }

        private static void RequireFinite(PMVector3 value, string what)
        {
            if (!value.IsFinite)
            {
                throw new ArgumentException("[PMMoverModel] " + what + " 不是有限向量：" + value);
            }
        }

        private static void RequireNonNegative(float value, string what)
        {
            if (value < 0f)
            {
                throw new ArgumentException("[PMMoverModel] " + what + " 不能为负：" + value);
            }
        }

        // =================================================================================
        //  层工具
        // =================================================================================

        private static List<PMMoverLayer> CopyLayers(PMMoverLayer[] layers)
        {
            List<PMMoverLayer> list = new List<PMMoverLayer>();
            if (layers == null)
            {
                return list;
            }

            for (int i = 0; i < layers.Length; i++)
            {
                list.Add(layers[i]);
            }

            return list;
        }

        private static PMMoverLayer[] CloneLayers(PMMoverLayer[] layers)
        {
            if (layers == null)
            {
                return null;
            }

            PMMoverLayer[] copy = new PMMoverLayer[layers.Length];
            Array.Copy(layers, copy, layers.Length);
            return copy;
        }

        private static List<PMMoverEffectRequest> CloneEffectQueue(PMMoverEffectRequest[] effects)
        {
            List<PMMoverEffectRequest> list = new List<PMMoverEffectRequest>();
            if (effects == null)
            {
                return list;
            }

            for (int i = 0; i < effects.Length; i++)
            {
                list.Add(effects[i]);
            }

            return list;
        }

        private static int IndexOfLayer(List<PMMoverLayer> layers, uint instanceId)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i].InstanceId == instanceId)
                {
                    return i;
                }
            }

            return -1;
        }

        private static void RemoveLayer(List<PMMoverLayer> layers, uint instanceId)
        {
            int index = IndexOfLayer(layers, instanceId);
            if (index >= 0)
            {
                layers.RemoveAt(index);
            }
        }

        /// <summary>规范排序：(Priority 升序, InstanceId 升序)。InstanceId 唯一 → 顺序唯一确定。</summary>
        private static void SortLayers(List<PMMoverLayer> layers)
        {
            layers.Sort(CompareLayers);
        }

        private static int CompareLayers(PMMoverLayer a, PMMoverLayer b)
        {
            if (a.Priority != b.Priority)
            {
                return a.Priority < b.Priority ? -1 : 1;
            }

            if (a.InstanceId != b.InstanceId)
            {
                return a.InstanceId < b.InstanceId ? -1 : 1;
            }

            return 0;
        }

        /// <summary>
        /// 按 InstanceId 匹配比较"活跃层实例定义"。
        /// 刻意不按数组下标比较：顺序只是规范化的产物，不该成为差异来源。
        /// </summary>
        private static bool LayersMatchDefinition(PMMoverLayer[] a, PMMoverLayer[] b)
        {
            int countA = a == null ? 0 : a.Length;
            int countB = b == null ? 0 : b.Length;
            if (countA != countB)
            {
                return false;
            }

            for (int i = 0; i < countA; i++)
            {
                PMMoverLayer layer = a[i];
                bool found = false;
                for (int j = 0; j < countB; j++)
                {
                    if (b[j].InstanceId == layer.InstanceId)
                    {
                        found = layer.SameDefinition(b[j]);
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        // =================================================================================
        //  数学工具（全部确定性、无静态状态）
        // =================================================================================

        private static float EffectiveRadius(float scale)
        {
            return PMMoverDefaults.CapsuleRadiusMeters * scale;
        }

        private static float EffectiveHalfHeight(float scale)
        {
            return PMMoverDefaults.CapsuleHalfHeightMeters * scale;
        }

        /// <summary>XZ 平面方向（Y-up 下"水平面"就是 XZ）。零输入返回零向量。</summary>
        private static PMVector3 NormalizePlanar(float x, float z)
        {
            return PMVector3.Normalized(new PMVector3(x, 0f, z));
        }

        private static PMVector3 MoveTowards(PMVector3 current, PMVector3 target, float maxDelta)
        {
            PMVector3 diff = target - current;
            float length = diff.Length;

            if (length <= maxDelta || length <= PMVector3.Epsilon)
            {
                return target;
            }

            return current + diff * (maxDelta / length);
        }

        private static PMVector3 Lerp(PMVector3 from, PMVector3 to, float alpha)
        {
            return new PMVector3(
                from.X + (to.X - from.X) * alpha,
                from.Y + (to.Y - from.Y) * alpha,
                from.Z + (to.Z - from.Z) * alpha);
        }

        private static float Clamp01(float value)
        {
            if (value < 0f) { return 0f; }
            if (value > 1f) { return 1f; }
            return value;
        }

        /// <summary>归一到 [0,360)。0 一律返回 +0.0f（避免 -0.0f 破坏逐位比较）。</summary>
        internal static float NormalizeDegrees(float degrees)
        {
            if (float.IsNaN(degrees) || float.IsInfinity(degrees))
            {
                return 0f;
            }

            float value = degrees % 360f;
            if (value < 0f)
            {
                value += 360f;
            }

            if (value == 0f)
            {
                return 0f;
            }

            return value;
        }

        /// <summary>两个朝向的最短弧夹角（度，0..180）。</summary>
        internal static float YawDeltaDegrees(float a, float b)
        {
            float diff = NormalizeDegrees(a) - NormalizeDegrees(b);

            while (diff > 180f) { diff -= 360f; }
            while (diff < -180f) { diff += 360f; }

            return Math.Abs(diff);
        }

        /// <summary>朝向最短弧插值（350°→10° 走 20° 而不是 340°）。</summary>
        internal static float LerpYawShortestArc(float from, float to, float alpha)
        {
            float diff = NormalizeDegrees(to) - NormalizeDegrees(from);

            while (diff > 180f) { diff -= 360f; }
            while (diff < -180f) { diff += 360f; }

            return NormalizeDegrees(NormalizeDegrees(from) + diff * alpha);
        }

        /// <summary>两个向量夹角（度）。任一侧退化（长度≈0）时返回 0（视为相同）。</summary>
        internal static float AngleBetweenDegrees(PMVector3 a, PMVector3 b)
        {
            float lengthA = a.Length;
            float lengthB = b.Length;
            if (lengthA <= PMVector3.Epsilon || lengthB <= PMVector3.Epsilon)
            {
                return 0f;
            }

            float cosine = PMVector3.Dot(a, b) / (lengthA * lengthB);
            if (cosine > 1f) { cosine = 1f; }
            if (cosine < -1f) { cosine = -1f; }

            return (float)(Math.Acos((double)cosine) * (180.0 / Math.PI));
        }

        /// <summary>三个分量中是否有任何一个的绝对差超过阈值。</summary>
        internal static bool VectorAbsDiffExceeds(PMVector3 a, PMVector3 b, float tolerance)
        {
            return Math.Abs(a.X - b.X) > tolerance
                   || Math.Abs(a.Y - b.Y) > tolerance
                   || Math.Abs(a.Z - b.Z) > tolerance;
        }
    }
}
