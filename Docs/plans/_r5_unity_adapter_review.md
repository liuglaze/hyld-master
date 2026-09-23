# R5-C1/C2 投射物 Unity 适配器 —— 独立复核与返工报告

> 任务类型：**comprehensive**（独立复核 `PMUnityProjectileMotion` / `PMUnityProjectilePresentation`，
> 在授权边界内返工真缺陷、补齐两个旧编译替身缺的 API、做受控正反测试并出报告）。
>
> 只读输入（按委派顺序）：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`
> → `Docs/plans/net-r5-network-contract.md`（末段「B2b/C适配可并行边界」的 C1/C2 为准）
> → `Docs/plans/_r5_unity_adapter_report.md`（被复核对象）
> → `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。
> 另按需只读：`Docs/plans/_r5_coordinator_review.md`（§1.3 hook fail closed / §3 API 变更）、
> `Docs/plans/net-r5-projectile-contract.md`、`PMProjectileCoordinator.cs` 的
> `IPMProjectileHostMotion` 接口与 `AdvanceMotion` 的 TryStep/TryStop 调用序、
> `Tools/PMR4CollisionAdapterTest/*`（既有受控回归模式）、四个 `.csproj`。
>
> **未接 host**（`PMDsSessionHost` / `PMClientSessionHost` / `PMR5ProjectileDriver` 一行未动）、
> **未扩大**到 Mover 查询修改（`PMUnityMoverCollisionQuery.cs` 只读）、
> **未启动 Unity**、**未提交**（git/svn 均无写操作）、**未递归委派**。
> World 单位：**Y-up 米**，Yaw 度。

---

## 0. 结论与验证结果（本机实测）

**结论：两个适配器的实现方向正确，但有 2 个真缺陷、1 处加固缺口，本批已全部返工；
另有 2 个旧编译替身缺 API 已收口。** 断言数 **63 → 74**，五个门禁全部 exit 0。

| # | 命令 | 结果 |
|---|---|---|
| ① | `dotnet build Tools/PMR5UnityCheck/PMR5UnityCheck.csproj -c Release`（**真实 Unity 2019.4.8f1 DLL**，netstandard2.0 + C#7.3） | **0 警告 / 0 错误** |
| ② | `dotnet build Tools/PMR5UnityAdapterTest/PMR5UnityAdapterTest.csproj -c Release` | **0 警告 / 0 错误** |
| ③ | `dotnet Tools/PMR5UnityAdapterTest/bin/Release/net8.0/PMR5UnityAdapterTest.dll` | **74 通过 / 0 失败**，退出码 0 |
| ④ | `dotnet build Tools/PMClientCheck/PMClientCheck.csproj -c Release` | **0 警告 / 0 错误**（复核前为 **3 个错误**） |
| ⑤ | `dotnet build Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj -c Release` | **0 警告 / 0 错误**（复核前同类缺 API） |
| ⑥ | `python Tools/check_cs_braces.py <四个 .cs>` | BALANCED，深度全程非负且末尾归零，PASS |
| ⑦ | 编码自检（六个 `.cs`） | 全部 `BOM=True CRLF=全部 loneLF=0 utf-8 ok` |

分段：A 4 / B 5 / C 5 / D 4 / E 5 / F 5 / G 2 / H 3 / I 11 / **J 9**（原 4，本批改写+扩充）/
K 12 / **L 4**（原 3，新增"不半写"）/ **M 5**（本批新增，构造失败清理）= 74。

> 真实 PhysX 的行为**仍未验证**：本批按用户要求不启动 Unity。受控替身只钉"适配器的判定逻辑"，
> 不代替真机（见 §7 P1–P5）。

---

## 1. 独立复核发现的真缺陷与处置

### 1.1 【真缺陷，已修】停止标记表满时"淘汰最旧" ⇒ 把"这一步撞墙了"静默变成"没撞"（等价漏墙）

**证据（复核前）**：`PMUnityProjectileMotion.RecordStopMark` 在
`_stopMarks.Count >= MaxStopMarkRecords (256)` 时按插入序号**淘汰最旧的一条**并计数
`EvictedStopMarks`。被淘汰的是一条**尚未被 `TryStop` 消费的已接受标记**。

**危害链（可复现，不是理论）**：
1. 整合器 `AdvanceMotion` 的正常调用序是 `TryStep` → `TryAdvanceMotion` → `TryStop`；
   但**存在合法提前返回**（`spec.LifetimeMs` 到期 `RecordStop` 后直接 return，
   或运动账本返回 `NotMovable` / 其他非 `Advanced`）——那条路径**不会调用 `TryStop`**，
   于是该 key 的停止标记会**留在表里**（陈旧标记）。
2. 表被这类陈旧标记填满后（`LiveStopMarks == 256`），一次"真的撞墙"的步会淘汰掉**别人的**
   已接受标记来给自己腾位。
3. 被淘汰那条的 `TryStep` 明明返回了 `true + 接触点 + 零速度`，其随后的 `TryStop` 却只能返回
   `false`（= "本步不停止"）。对 `IPMProjectileHostMotion` 的语义而言，这就等于
   **把那一步的阻挡结论丢掉了**——与 C1 契约"必须按 Key 记账且有界清理或 TryStop 即消费，
   不能共享单个 mutable lastHit 串弹"以及整合器 `_r5_coordinator_review.md` §1.3
   的 fail-closed 精神直接冲突（宁可显式失败，不可静默丢结论）。

**返工（真实方案）**：`RecordStopMark` → **`TryRecordStopMark` 返回 bool**
（`PMUnityProjectileMotion.cs:576`），满且本 key 不在表中 ⇒ **返回 false，不淘汰、不覆盖**；
`TryStep` 的两个"受阻"分支（起点重叠 `:354`、沿线接触 `:432`）在记账失败时
`_stopMarkCapacityFailures++` 并**显式 `return false`**（out 参数保持开头写的安全值：
位置停在 `snapshot.Position`、零速度）。

**语义边界（写进代码注释与文档）**：
- **只在"本步确实需要记一条新标记"（= 受阻）时才失败**；表满时**无阻挡**的步照样返回
  `true` + 原样回填直线（不需要记账就不该被表满影响）——用例 **J7** 钉住。
- `ForfeitStopMark`（`TryStep` 开头作废**本 key** 的上一条标记）保留：它是"同一 key 最新一步胜出"
  语义 —— 不作废会把**上一步**的停止点当成**这一步**的结果，那是另一种错误。
  它与容量路径无关（不会为别的 key 腾位）——用例 **J1** 断言 `ForfeitedStopMarks == 0`。
- 清空通道只有两个显式入口：`Clear()`（宿主在对账/重连准备阶段丢弃陈旧标记，
  也是**从饱和中恢复记录能力**的手段）与 `Dispose()`——用例 **J8 / J9**。
- 新增可观测量 `PMUnityProjectileMotionStats.StopMarkCapacityFailures`（原 `EvictedStopMarks` 删除，
  详见 §2）。**非 0 = 宿主纪律告警**：说明有未被消费的陈旧标记撑满了表，
  宿主必须调 `Clear()`；这正是本适配器的运行契约，已写进文件头的"宿主前置条件 3)"。

**正反测试（新 J 段，4 → 9 项）**：

| 用例 | 类型 | 内容 |
|---|---|---|
| J1 | 正 | 300 个受阻步：前 256 个**全部被接受**（`accepted == live == 256`），`ForfeitedStopMarks == 0` |
| J2 | 反 | 表满后 44 步**显式返回 false**，且输出回填的是安全值（停在原位、零速度），不是 `true+直线` |
| J3 | 反 | `StopMarkCapacityFailures == 44`，`BlockedSteps == 300`（如实计数） |
| **J4** | **正（关键）** | **最旧那条已接受标记仍可被 `TryStop` 逐字段消费**（证明没有淘汰最旧） |
| J5 | 正 | 最新那条同样可消费（保住的不是"只有尾巴"） |
| J6 | 反 | 被拒的那一步**没有**留下标记（`TryStop == false`），不会被误读成"撞墙了" |
| J7 | 正 | 表满时**无阻挡**的步仍返回 `true`，且不改变表长（fail closed 只发生在需要记账的步） |
| J8 | 正 | `Clear()` 清空并**恢复记录能力**（新 key 的 `TryStep == true` 且可消费） |
| J9 | 正 | `Dispose()` 幂等（清空标记），之后 `TryStep`/`TryStop` 恒 `false` 且不抛异常 |

### 1.2 【真缺陷，已修】表现层构造中途失败 ⇒ 自建对象与已克隆材质永久泄漏

**证据（复核前）**：`PMUnityProjectilePresentation(string, Transform)` 的构造函数
**没有任何失败清理**。任何一步抛异常（`GameObject.CreatePrimitive` 失败、
`new Material(source)` 失败、`renderer.sharedMaterial = clone` 失败…）都会留下
**已创建的根节点 / primitive 子节点 / 已克隆材质**。而构造抛异常时调用方**拿不到 `this`**，
**没有任何入口能再调 `Dispose`** ⇒ 泄漏是永久的（直到场景卸载）。

**返工**：构造函数体包进 `try/catch`；`catch` 中先清空 `_root` / `_body` / `_material`
并置 `_disposed = true`，再销毁**已自建的** primitive 子节点、根节点、克隆材质，最后 `throw;`
（原异常原样抛出，不吞、不换成别的异常）。同时把 `CreatePlaceholderMaterial` 里的
`_material = material;` 提到 `renderer.sharedMaterial = material;` **之前**，
使"克隆成功但挂载失败"这条路径也能被 `catch` 清掉。

**正反测试（新 M 段 5 项）**（受控替身新增 3 个**测试专用**钩子：
`GameObject.FailNextCreatePrimitive`、`Material.ThrowOnClone`、`Renderer.ThrowOnSharedMaterialSet` +
`Material.LastCreated` / `Material.IsClone` 观测面）：

| 用例 | 失败注入点 | 断言 |
|---|---|---|
| M1 | primitive 创建失败 | 抛异常 + `AliveCount` 回到构造前 + 无多余材质（**根节点也被清掉**） |
| M2 | 克隆材质构造失败（在创建任何东西之前抛） | 抛异常 + `AliveCount` 回到构造前 + 无多余材质 + `LastCreated == null` |
| M3 | **克隆成功、挂载失败** | 抛异常 + 对象清掉 + **已创建的克隆材质 `Destroyed == true`**（无半成品泄漏） |
| M4 | 正常路径 | 占位材质是**克隆体**（占位色生效）且 **primitive 内建共享材质未被修改**（仍是白） |
| M5 | 正常路径 Dispose | `AliveCount` 回到构造前 + 占位材质已销毁 |

### 1.3 【加固缺口，已补】Dispose 的"帧末延迟销毁"窗口内不得残留可查询碰撞体

**复核**：`Object.Destroy` 在运行时是**帧末**生效的，`DestroyImmediate` 仅编辑器（本类用
`Application.isPlaying` 选，属生命周期 API、不用于身份判定，正确）。占位球自带的
`Collider`/`Rigidbody` 在构造期已 **先 `enabled = false` 再 `Destroy`**（两层都要：
只销毁会留下"仍可被 PhysX 命中"的窗口；只禁用会留下可被重新启用/序列化的组件），方向正确。

**处置**：`Dispose` 在真正销毁前**再幂等确保一次** `StripPhysicsComponents(body.gameObject)`
（防止宿主/后续代码把组件启回来），并在注释里写明"延迟销毁窗口内不得存在可查询的 Collider"。
`Dispose` 顺序也调整为**先材质、后根节点**（材质不再被任何 renderer 引用后再销毁）。

### 1.4 【核查通过，无需改动】Motion 的其余复核项（委派点名逐条）

| 复核项 | 判定 | 证据 |
|---|---|---|
| **指定 physicsScene** | ✅ 正确 | 全部查询走 `_scene.SphereCast` / `_scene.OverlapSphere`；文件内**没有** `Physics.SphereCast`/`Physics.OverlapSphere`/`Physics.SyncTransforms`/`autoSimulation`/`queriesHitTriggers`。用例 **B4** 断言 `Physics.StaticQueryCalls == 0`；真实签名由 **PMR5UnityCheck 引真实 DLL** 校验 |
| **必须独立有效 PhysicsScene** | ✅ 正确 | `!IsValid()` ⇒ `ArgumentException`；`Equals(Physics.defaultPhysicsScene)` ⇒ `ArgumentException`（用例 I1/I2） |
| **白名单** | ✅ 正确 | **引用相等**过滤（不用 `LayerMask`/名字/标签近似）+ 构造期逐项校验（非 null / `gameObject` 可取 / `scene.IsValid()` / `scene.GetPhysicsScene().Equals(physicsScene)`）；空/null 白名单显式失败（用例 I3/I4/I5/I6、H1/H2/H3） |
| **忽略 trigger** | ✅ 正确 | 查询参数恒 `QueryTriggerInteraction.Ignore`，且逐候选**再判一次** `isTrigger`（两层都在，因为参数可被宿主改动/替身差异）；用例 H1/H2 |
| **起点正重叠** | ✅ 正确 | `OverlapSphere` 权威判定（语义 "touching or inside"），命中 ⇒ **不推进**（停在起点）+ 零速度 + 记标记；**短路**不再扫掠（用例 E2 断言 `SphereCastCalls` 不增长）；饱和 ⇒ 显式 `false`（用例 G2） |
| **零 TOI** | ✅ 正确 | `distance == 0` 就是**最早**，取 min 必然命中 ⇒ 不会被跳过；用例 F1/F3/F4（含"只有扫掠报零距离"的路径） |
| **距离** | ✅ 正确 | 只按距离取最小值（`earliest`），**完全不读 `RaycastHit.normal`**；`hitDistance` 非 finite / 负 / `> distance` 的候选被丢弃 |
| **skin** | ✅ 正确 | `travel = max(0, earliest − ContactSkinMeters)`，`contact = start + Δ·(travel/|Δ|)`；**不做尺寸收缩**（收缩会让弹挤过窄缝 = 漏墙）；回退是保守的（只会更早停） |
| **异常** | ✅ 正确 | 查询期异常 `catch → _queryFailures++ → return false`（**绝不回退直线**，那等于穿墙）；整合器据此走 `HostMotionFaulted` 就地停止 |
| **主线程** | ✅ 正确 | 构造记 `Thread.CurrentThread.ManagedThreadId`；查询期 `RequireMainThread()` 不等即抛 `InvalidOperationException`（用例 I11 起真线程验证）；`TryStop` 同样受约束（整合器 catch ⇒ fail-closed stop） |
| **每 key stop 缓存** | ⚠️ 有真缺陷 | 见 §1.1，已修 |

### 1.5 【核查通过，无需改动】Presentation 的其余复核项

| 复核项 | 判定 | 证据 |
|---|---|---|
| **不修改原材质** | ✅ 正确 | 构造期 `new Material(primitive 内建共享材质)` **只克隆一次**，随后只写克隆体；源材质/任何资产只被**读**。新用例 **M4** 断言克隆体 `IsClone` 且内建源材质仍是白（未被写）；K9 断言创建数 == 1 且 `renderer.sharedMaterial` 与该实例同一引用 |
| **DS 拒绝** | ✅ 正确 | 构造与工厂在 `PMNetRuntime.IsDedicatedServer` 为真时**直接抛** `InvalidOperationException`，且**一个 GameObject 都不建**（用例 K1）；判定走 `PMNetRuntime`（唯一身份来源），**不**用 `Application.isEditor`/场景名代替 |
| **Apply 只更新位置/yaw/尺寸/hidden** | ✅ 正确 | 写入面恰好 4 项；位置唯一来源 `state.Position`（不读 `SpawnPosition`/`PreviousPosition`/`Velocity` 去猜，用例 K5）；`Stopped` 只作为 hidden 的输入，**无任何一次性副作用**（本类没有音效/事件出口，"二次音效"不可表达；用例 K8 断言重复 Apply 对象数/组件数/位置/可见性不变） |
| **Apply 非法不半写** | ✅ 正确（本批新增断言） | 校验（`RequireAlive` → null → `RequireFinite`）**全部先于任何写入**，因此不存在"先写位置再发现朝向非法"的幽灵位置。新用例 **L4**：位置合法 + 朝向 NaN / 半径 0 / 半径 Inf 三种失败调用之后，位置 / 旋转 / 缩放 / `Hidden` / `ApplyCount` / `LastPosition` **逐字段未变** |
| **Dispose 清理且不碰别人** | ✅ 正确 | 只销毁自建的根/子节点与克隆材质；**不触碰** `Camera.main`、已有角色、地图、UI（用例 K10/K11/K12）。受控替身里销毁是同步的，真机的"帧末延迟"窗口由 §1.3 的加固覆盖 |
| **不含旧脚本 / 无 Collider / 无 Rigidbody** | ✅ 正确 | primitive 自带 Collider **先禁用再销毁**；Rigidbody 若存在先 `isKinematic = true` 再销毁；用例 K4 断言 `Collider`/`Rigidbody`/`MonoBehaviour` 都不存在而 `Renderer` 在 |
| **只是诊断占位，不称外观已迁移** | ✅ 正确 | 根名字带 `diagnostic-sphere-not-hero-art`；不引用任何英雄外观配置/Prefab（用例 K3） |

---

## 2. API 变更清单（本批对 C 适配器公开面的唯一破坏性变更）

| 变更 | 旧 | 新 | 理由 |
|---|---|---|---|
| 停止标记统计 | `PMUnityProjectileMotionStats.EvictedStopMarks` | **`StopMarkCapacityFailures`** | §1.1：淘汰已接受标记是**缺陷**，计数被删；改成"因表满而显式 fail closed 的次数"。`EvictedStopMarks` 唯一消费者是本工程的用例与 `_r5_unity_adapter_report.md`，无生产代码依赖 |
| 停止标记记账 | `private void RecordStopMark(key, pos)`（满 ⇒ 淘汰最旧） | **`private bool TryRecordStopMark(key, pos)`**（满 ⇒ false，不淘汰） | 同上 |
| `MaxStopMarkRecords` 语义 | "超出即淘汰最旧并计数" | "满则**只在需要记账的步上**显式失败；清空只能走 `Clear`/`Dispose`" | 契约收紧（常量值与 256 上限不变） |
| `PMUnityProjectileMotion.Clear()` 语义 | "丢弃陈旧标记" | 同时是**从饱和中恢复记录能力**的唯一手段 | 同上 |
| `PMUnityProjectilePresentation` 构造 | 失败即泄漏 | **失败先清自建对象/材质再抛** | §1.2（签名与公开成员**未变**） |
| `PMUnityProjectilePresentation.Dispose()` | 只销毁 | 销毁前幂等再确保"占位不参与物理" | §1.3（签名未变） |

**未变**：`IPMProjectileHostMotion` 接口、`TryStep`/`TryStop` 签名与三条出口语义、
`HitBufferCapacity`/`OverlapBufferCapacity`/`MaxStopMarkRecords`/`ContactSkinMeters` 取值、
`Scene`/`LayerMask`/`AllowlistCount`/`MainThreadId`/`StopMarkCount`/`Disposed`、
`Apply` 签名、`PrimitiveSphereRadius`/`PlaceholderKind`/`PlaceholderColor`、
`PMProjectile*` 共享契约、整合器、driver、两个宿主。

---

## 3. 措辞纪律：移除无证据的"接触法线恒 -dir"断言

被复核对象（`_r5_unity_adapter_report.md` 引用的实现与替身）把 R4-B 的**一次** Play 观测
写成了"`normal` **恒为** `-dir`"这类**断言性**措辞。这是把观测当契约：既无充分证据
（只有一次录制，且 PhysX 不承诺该形态），也容易让后续读者据此做"法线必然不可信"的推断。
**本批已改写**（只改措辞，不改任何判定逻辑 —— 逻辑本来就**完全不读** `normal`）：

| 位置 | 处置 |
|---|---|
| `PMUnityProjectileMotion.cs` 文件头 | 改为「**一次**真实 Play 实测**观察到**……该条命中的 `normal` 为 `-dir`。这**只是一次观测**，不是 PhysX 的接口契约：**不得表述为"所有接触法线恒为 -dir"**，也不得据此推断"任何命中的法线都不可信"」；并写明适配器**完全不读** `RaycastHit.normal`，该决定与观测无关（投射物对接触只有一个合法反应 = 停止） |
| `Tools/PMR5UnityAdapterTest/UnityStubs.cs` 文件头 / `PhysicsScene` 类注释 / 内联注释 / `MeasureSphereAabb` 注释 | 把"复现 PhysX 语义"改为"**替身模型**的语义"；`-dir` 赋值标注为"只为复现**那一次**观测，适配器不读它" |
| `Tools/PMR5UnityAdapterTest/Program.cs` 文件头 + **A1** 用例名 | 改为"在本替身模型里得到 `distance=0`（normal 仅为复现那次观测，适配器不读）" |

**登记但未修改（越界，仅报告）**：

| 位置 | 现状 | 说明 |
|---|---|---|
| `Tools/PMR5UnityAdapterTest/PMR5UnityAdapterTest.csproj:25` | 「它复现的是 R4-B 真实 Play 已观测的语义（起点接触 ⇒ distance=0 且 normal=-dir）」 | **该 csproj 不在本批写入边界内**（委派只给了 6 个 `.cs` + 报告），因此**未改**。措辞仍偏"语义断言"，建议主侧后续把它改成"一次观测" |
| `Client/Assets/Scripts/PMUnity/PMUnityMoverCollisionQuery.cs:25` | 「实测事实……其 `distance == 0` 且 `normal == -dir`」 | 属 **Mover 查询文件**，委派明确"不扩大到 Mover 查询修改"，只读未改。注：该处措辞**有**具体证据（同段列出三条 Play 录制样本及其 fraction/normal 值），与本批要清的"无证据断言"不同类，仅登记 |

---

## 4. 旧编译替身缺 API 收口（委派点名的 3 个错误 + 同类补齐）

**复核前** `PMClientCheck` 只剩 3 个错误（与委派描述完全一致）：

```
Client/Assets/Scripts/PMUnity/PMUnityProjectileMotion.cs(346,36): error CS1061:
  “PhysicsScene”未包含“SphereCast”的定义 …
Client/Assets/Scripts/PMUnity/PMUnityProjectileMotion.cs(489,32): error CS1061:
  “PhysicsScene”未包含“OverlapSphere”的定义 …
Client/Assets/Scripts/PMUnity/PMUnityProjectilePresentation.cs(296,37): error CS1729:
  “Material”不包含采用 1 个参数的构造函数
```

**处置（只改替身，不改生产 API）**：

| 文件 | 补充 | 形状依据 |
|---|---|---|
| `Tools/PMClientCheck/ClientStubs.cs` | `PhysicsScene.SphereCast(Vector3, float, Vector3, RaycastHit[], float, int, QueryTriggerInteraction) → int`；`PhysicsScene.OverlapSphere(Vector3, float, Collider[], int, QueryTriggerInteraction) → int`；`Material(Material)` 重载 + **显式 `Material()`** | 两个 `PhysicsScene` 重载都是**批量**形态（返回个数、结果写进数组），签名逐字取自 Unity 2019.4 真实程序集 —— 由 **`PMR5UnityCheck` 引真实 DLL 编译同一份适配器源码**独立把关（"真实 2019 DLL 才权威"） |
| `Tools/PMUnityGlueCheck/UnityStubs.cs` | 同上三项 | 同上 |

**两个容易踩的点（已在注释里写明）**：
1. **显式声明任何构造函数会取消隐式无参构造** —— 所以 `Material(Material)` 必须与 `Material()` 成对给出，
   否则会打掉别处（如 Editor 构建脚本）的 `new Material(...)` 用法。
2. 这两个替身**只保证形状、不保证语义**（无 PhysX）；门禁通过只证明"能在 netstandard2.0 + C#7.3 上编译"，
   **不**证明运行期几何正确。

**结果**：`PMClientCheck` 3 错误 → **0 警告 / 0 错误**；`PMUnityGlueCheck` 同类补齐后
**0 警告 / 0 错误**。生产 API **零改动**（§2 表里没有一项是为了让替身编过而改的）。

---

## 5. 回归测试覆盖（63 → 74）

| 段 | 变化 | 内容 |
|---|---|---|
| A/B/C/D/E/F/G/H/I/K | 不变（K 12 / I 11 / …） | 既有 63 项中 58 项原样保留并通过 |
| **J** | **4 → 9** | §1.1 的正反测试（表满不淘汰 / 显式 false / 旧标记仍可消费 / 无阻挡不受影响 / Clear 恢复 / Dispose） |
| **L** | **3 → 4** | 新增 **L4**：非法输入**不半写**（位置/旋转/缩放/可见性/ApplyCount/LastPosition 逐字段未变） |
| **M** | **0 → 5（新段）** | §1.2 的构造失败清理三态（primitive 失败 / 克隆失败 / 挂载失败）+ 不改源材质 + 正常 Dispose |

测试仍**全部使用真实现**（真 `PMUnityProjectileMotion` / `PMUnityProjectilePresentation` / 真 `PMNetRuntime`），
只通过 `IPMProjectileHostMotion` 接口驱动适配器；期望值手算（`4.5 − 0.1 = 4.4`，减 skin = `4.399`）。
新增的 3 个失败注入钩子**只存在于受控替身**，不改变任何真实引擎语义，也不进入生产源码。

### 5.1 受控替身的边界（必须与结论一起读）

`Tools/PMR5UnityAdapterTest/UnityStubs.cs` 是"**球 vs 轴对齐 AABB**"的一阶解析模型，
**不是** PhysX、**不是** Unity：

- 扫掠几何 = 射线 vs "按半径外扩的 AABB" 的 slab 测试；面接触精确，棱/角处是球体的**保守超集**
  （只会早报、不会漏报）；**有意不实现**旋转形状 / 网格 / MTD。
- `Quaternion` 只存欧拉角（无四元数乘法）；`Transform` 只做父级平移合成；
  被销毁对象仍是**非 null 托管引用**（不实现 Unity 的"已销毁 == null"语义，适配器与表现层都不依赖）。
- 测试专用开关 `PhysicsScene.ReportTouchingInOverlap`（默认 true）：置 false 只报真穿透（`gap < 0`），
  用来让适配器走到"**只有扫掠**报 `distance == 0`"这条路径（用例 F4）。它不改变任何真实引擎语义。
- `Camera.main` 是显式静态引用（唯一目的是断言表现层没碰过它）；`Material.CreatedTotal` 不计内建默认材质。

**因此本工程钉住的是"适配器的判定逻辑"，不是"引擎的几何结论"。**

---

## 6. 并行 driver 未收口的观察（不越界，仅记录）

复核期间工作区里存在**其它组**的在飞改动（`git status` 可见，均**非本批写入**）：
`Client/Assets/Scripts/PMR3/PMR5ProjectileDriver.cs`（未跟踪，B2b driver）、
`Client/Assets/Scripts/PMNet/World/PMNetWorld.cs`、`Client/Assets/Scripts/PMR3/{PMR3Player,PMR3Runtime}.cs`
与 `PMR3/Generated/*`、`Server/Server.csproj`、以及若干 `Tools/*.csproj` 的 include 面。

**观察与边界**：
- 主侧已按约定给 6 个 `.csproj` 补了 `PMProjectile/**` 的 include
  （`PMClientCheck.csproj`、`PMUnityGlueCheck.csproj` 末尾的 `ItemGroup` 已就位）——本批未再动 csproj。
- **未出现依赖阻塞**：`PMClientCheck` / `PMUnityGlueCheck` 在本批返工后（同一时刻、同一工作区）
  都编到 **0 错误**；两个工程都**不**编 `PMR5ProjectileDriver.cs`，因此不依赖 driver 是否收口。
- 本批**未**读取、**未**修改、**未**为这些在飞文件做任何适配；若它们的后续改动改变
  `PMR3Player` 的接口面而影响旧门禁，属另一条工作流，由主侧裁决。

---

## 7. 未做 / 剩余问题（诚实口径）

| # | 项 | 说明 |
|---|---|---|
| P1 | **真实 PhysX 行为未在真机验证** | 本批按用户要求**不启动 Unity、不点菜单、不改场景**。真实 `PhysicsScene.SphereCast`/`OverlapSphere` 在 runtime-local-physics-scene 上的实际返回仍需一次 Unity Play（可复用 `Client/Assets/Editor/PMR4UnityValidation.cs` 的菜单模式） |
| P2 | `ContactSkinMeters = 1e-3` / 接触容差收敛 | 属"待实测收敛项"：真机需量一次"是否残留嵌入 / 是否过早停" |
| P3 | 饱和上限与正式地图 Collider 密度 | `HitBufferCapacity = 32` 与 `PMUnityBattleMap.MaxColliderCount = 4096` 存在量级差；真机需读 `SaturationFailures` 决定是否收紧 `layerMask` |
| P4 | **`StopMarkCapacityFailures` 的运行期运维约束（本批新增）** | 该计数非 0 说明表被陈旧标记撑满、新的阻挡步只能 fail closed。整合器在"寿命到期"等提前返回路径上不会调 `TryStop`，因此**陈旧标记会自然积累**；宿主须周期调 `Clear()`（或保证受阻步都被消费）。**本批未改整合器**（超出边界），也未量化真实积累速度 —— 真机需实测该计数 |
| P5 | 白名单同场景校验的运行时前提 | `SceneManager.CreateScene(LocalPhysicsMode.Physics3D)` 建出的场景应与 `PMUnityBattleMap.PhysicsScene` 相等（同一 API，逻辑上一致），仍需真机确认一次 |
| P6 | 表现层材质克隆在真机客户端构建 | `new Material(primitive 内建材质)` 依赖内建材质未被剥离；取不到时本类**不创建**材质（行为仍正确，只是外观用 primitive 默认材质） |
| P7 | **C 宿主集成** | 本批**只交付可被宿主消费的 API**：未改 `PMDsSessionHost` / `PMClientSessionHost`，未把 coordinator/driver 接进 Unity 宿主 |
| P8 | 整合器侧 fail-closed 的联动 | 本工程只证明适配器**会返回 false**；`TryStep=false ⇒ HostMotionFaulted ⇒ 就地停止` 的链路由 `Tools/PMProjectileIntegrationTest` P 段独立覆盖，本批**未重跑**（避免在别人的工程目录产生构建产物），仅引用其结论 |
| P9 | 旋转/网格几何 | 全部交给 PhysX；本适配器不做 bounds 近似，因此不承担"通用形状精确 MTD"的承诺 |
| P10 | §3 登记的两处措辞 | `*AdapterTest.csproj` 与 Mover 查询文件**不在写入边界内**，未改，已报告 |
| P11 | 实机 T45 | 仍未完成；本批不改变该状态 |

**不做**：弹跳 / 滑行 / 穿透厚度 / 拖尾 / 音效 / 命中特效 / 插值平滑；角色伤害与骨骼精校（属 R6）。
**不声称** R5-C 阶段完成、不声称 R5-B2 完成、不声称英雄子弹外观已迁移。

---

## 8. 已检查范围

**完整读取的文档（按委派顺序）**：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`
→ `Docs/plans/net-r5-network-contract.md`（全文，以末段 C1/C2 为唯一构造/判定/边界依据）
→ `Docs/plans/_r5_unity_adapter_report.md`（全文，被复核对象）
→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（全文）。
另按需读：`Docs/plans/_r5_coordinator_review.md`（§1.3 / §3 / §5）、
`Docs/plans/net-r5-projectile-contract.md`（单位与运动 hook 语义段）。

**文档如何决定入口**：`net-r5-network-contract.md` 末段 C1/C2 给出**唯一**的构造签名、判定要求
（指定 scene / 白名单 / trigger / 起点 Overlap / 零 TOI / 饱和显式失败 / 出错返回 false /
按 Key 有界记账或 TryStop 即消费）与表现层边界（只更新位置-yaw-尺寸-hidden / Stopped 不二次副作用 /
DS 禁止创建 / 自建根 + Dispose 清理 / 材料不反复实例化 / 不动原资产），本批逐条按它复核；
`_r5_coordinator_review.md` §1.3 给出 hook 失败的 **fail-closed 形态表**（false/异常/非 finite ⇒
不推进 + 就地停止，`TryStop=false` 是正常语义），是本批"容量满也必须**显式 false**而不是静默丢标记"
的直接依据；`Client/Assets/AGENTS.md` §1.1 明确"进程形态唯一来源是 `PMNetRuntime.IsDedicatedServer`"，
所以 C2 的 DS 判定不采用任何间接条件。

**只读读取的生产源码**：`PMUnityProjectileMotion.cs` / `PMUnityProjectilePresentation.cs`（全文，复核对象）、
`PMProjectile/PMProjectileCoordinator.cs`（`IPMProjectileHostMotion` 接口 + `AdvanceMotion` 的
TryStep→TryAdvanceMotion→TryStop 调用序与 `FailClosedHostMotion`/`FailClosedHostStop` 落点）、
`PMProjectile/PMProjectileContracts.cs`、`Client/Assets/Scripts/PMUnity/PMUnityMoverCollisionQuery.cs`
（仅读，**未改**）、`Tools/PMR4CollisionAdapterTest/*`（既有受控回归模式参照）、四个相关 `.csproj`、
`Tools/PMR5UnityAdapterTest/{Program.cs,UnityStubs.cs}`（复核并返工）。

**写入的文件（仅此 7 个，未创建/修改列表外任何文件）**：

1. `Client/Assets/Scripts/PMUnity/PMUnityProjectileMotion.cs`（UTF-8 BOM + CRLF，749 行）
2. `Client/Assets/Scripts/PMUnity/PMUnityProjectilePresentation.cs`（UTF-8 BOM + CRLF，438 行）
3. `Tools/PMR5UnityAdapterTest/Program.cs`（BOM + CRLF，1392 行）
4. `Tools/PMR5UnityAdapterTest/UnityStubs.cs`（BOM + CRLF，1022 行）
5. `Tools/PMClientCheck/ClientStubs.cs`（BOM + CRLF，1490 行）
6. `Tools/PMUnityGlueCheck/UnityStubs.cs`（BOM + CRLF，683 行）
7. `Docs/plans/_r5_unity_adapter_review.md`（本报告）

**构建产物**：仅 `Tools/PMR5UnityCheck/{bin,obj}` 与 `Tools/PMR5UnityAdapterTest/{bin,obj}`
（为验证而 clean 重建过）与 `Tools/PMClientCheck/{bin,obj}`、`Tools/PMUnityGlueCheck/{bin,obj}`
的增量输出；未触发任何其它工程的构建。
