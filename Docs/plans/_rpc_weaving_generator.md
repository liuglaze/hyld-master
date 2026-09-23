# P2：生成器正式格式（冻结编织格式 v1）交付报告

> 契约：`Docs/plans/net-rpc-weaving-contract.md`（唯一新冻结接口）；
> 状态与验收仍只在 `Docs/plans/net-architecture-migration.md` 登记（T-W3 / T-W5 的生成器侧）。
> P1 证据见 `_rpc_weaver_proof.md`。本文件是 P2「生成器侧」的交付与证据。
>
> 写入边界（本轮**只**动这些）：`Tools/PMNetGen/{DeclEmitter,DeclScanner,DeclValidation}.cs`、
> `Tools/PMDeclCheck/{Program.cs,Fixtures/Bad.cs}`、`Tools/PMNetWeaverTest/{Program.cs,PMNetWeaverTest.csproj}`、
> 删除 `Tools/PMNetWeaverTest/Fixture.GeneratedGuard.cs`、本报告。
> **`Tools/PMNetWeaver/**` 一行未改**（P1 已真实证明的工具体，本轮只读参考）。

---

## 0. 一句话结论

生成器现在**自己**产出冻结格式 v1：含 RPC 的类会得到 `private` 发送 helper、
`PMNet_GetRpcWeaveVersion()`（编译前 0）、`PMNet_RequireRpcWeave()`（版本 != 1 抛明确异常）、
实例 `readonly` gate 字段，且 `PMNet_BuildEntry()` 的**第一条语句**就是 `PMNet_RequireRpcWeave();`；
`RegisterAll` 在**任何** `RegisterClass` 之前先求值全部 `BuildEntry`（含 RPC 类的 guard），
因此未编织程序集既不能 `new` 也不能注册，且不会留下「非 RPC 类先登记、RPC 类才发现未 weave」的半注册。

门禁结果：

| 门禁 | 结果 | 说明 |
|---|---|---|
| `Tools/PMDeclCheck` | **107 / 0（exit 0）** | 本轮改动前实测 70 / 1；新增 §13/§14/§15 共 37 项 |
| `Tools/PMNetWeaverTest` | **192 / 0（exit 0）** | P1 为 141 / 0；本轮新增 51 项，原 141 项检测力全部保留 |
| `PMNetGen` 构建 | 0 警告 / 0 错误 | 默认 `bin/Release/net8.0` 副本已更新（见 §5 说明） |
| `PMNetGen --decl-scan Client/Assets/Scripts/PMR3` | 0 错误、13 RPC | `PMR3Player` hash 仍 `0xB09BCD1C`、ID 无漂移 |
| `PMNetGen --decl-check`（PMR3 生产产物） | exit 2（预期中间态） | 见 §6：生产 Generated 由另一组重生成 |

---

## 1. 已读文档（按委派指定顺序）与它们如何决定首搜入口

| 顺序 | 文档 | 它决定了什么 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 仓库入口与必读路由（客户端/服务端文档、主计划） |
| 2 | `Client/Assets/AGENTS.md` | §6 资产/生成约定（声明入口 `Tools/PMNetGen --decl-gen … --id-lock`）、§7 门禁口径；**编码约定 UTF-8 BOM + CRLF** |
| 3 | `Server/AGENTS.md` | 「PMNet 框架源码链接自 Client/Assets；PMR3 声明产物两端同集合」——决定了本轮只看生成器与门禁，不碰 Lobby |
| 4 | `Docs/plans/net-architecture-migration.md`（末尾「RPC自然C#接口／IL编织」段 + `T-W1..T-W6` 表） | 冻结了 P1/P2/P3 分工、T-W3「生成器/PMR3 迁移及稳定协议」、T-W5「增量构建与跨层回归」；明确「旧前缀不再业务可见、真实字节链不回退」 |
| 5 | `Docs/plans/net-rpc-weaving-contract.md`（完整） | **唯一冻结接口**：§2 的助记名与形态（`PMNet_<M>` 必须 private、`PMNet_RpcInvoke_<M>`、`PMNet_GetRpcWeaveVersion()`、`PMNet_RequireRpcWeave()`、实例 readonly gate、`PMNet_BuildEntry` 开头 Require）、§3 的明确拒绝形态、§5 的 T-W1..T-W5 验收口径 |
| 6 | `Docs/plans/net-r2-codegen-contract.md`（完整） | §4 生成物 API 面、§4.3.1 闭包捕获、§4.3.2 校验路由、§5 规则 1..13、§6 类型集——本轮所有改动都不动这些既有语义 |
| 7 | `Docs/plans/_rpc_weaver_proof.md`（§2 / §5 / §6 为重点，§3/§4/§7 作口径参考） | **§5.3 = P2 前置条件三条**（发送 helper 改 private、产出版本/守卫、BuildEntry 插 guard）及其**连锁影响**（调用点必须同批迁移）；**§5.4 = 版本方法 IL 形态随 Debug/Release 不同**（决定版本方法必须写成常量返回）；§5.1/§5.2 的 Cecil/ReplaceFile 经验本轮不涉及；§3 的「明确拒绝形态」清单是本轮补声明的直接依据 |
| 8 | `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md` | bash 工具按 Bash 解析；本环境只用 `dotnet`/`rm`/`git status`，未混用 PowerShell 语法 |

只读参考的**实码**（用于把「拒绝形态」与「门禁断言」写成与真实工具体一致的口径）：

- `Tools/PMNetWeaver/RpcAssemblyWeaver.cs`：`ValidateClassGuard`（版本方法存在/守卫读版本/BuildEntry 调守卫）、
  `ReadWeaveVersion`（恰好 1 个 `ret`、无调用、恰好 1 个整型常量）、`ValidateRpcSignature`（同名重载 / ctor / static /
  virtual / abstract / extern-native / 泛型 / 非 void / 无体 / async / ref-out-in / default / params / 指针）、
  `ValidateUnwovenClass`（发送 helper 必须 private、两个 helper 各恰一次调用原方法）。
- `Tools/PMNetWeaverTest/Fixture.cs`：6 个 pre-weave 用例 + 47 个 woven 用例的**既有断言口径**（本轮不改它，必须继续通过）。
- `Tools/PMNetWeaverTest/Fixture.GeneratedGuard.cs`：冻结格式 v1 的**模板**（本轮把它固化进生成器后删除）。
- `Client/Assets/Scripts/PMR3/PMR3Player.cs`：真实 13 条 RPC 的形态（全部普通实例 void 方法、无重载、无默认值/ref-out/params），
  用于确认新增拒绝规则**不会**误拒生产代码。

---

## 2. 修改清单（逐文件）

### 2.1 `Tools/PMNetGen/DeclEmitter.cs`

1. 新增 `EmitWeaveGuard(sb, cls, indent)`，**只对含 RPC 的类**发射（非 RPC 类不发射，否则「哪些类需要编织」不可判定）：

```csharp
internal static int PMNet_GetRpcWeaveVersion()
{
    return 0;
}

private static int PMNet_RequireRpcWeave()
{
    if (PMNet_GetRpcWeaveVersion() != 1)
    {
        throw new System.InvalidOperationException(
            "PMNet RPC 未编织：本程序集仍处于编译后未处理状态。"
            + "请先运行 `dotnet PMNetWeaver.dll --weave <assembly.dll>`（构建脚本应在复制 DLL 后执行）。");
    }

    return 1;
}

#pragma warning disable 0414
private readonly int PMNet_rpcWeaveGate = PMNet_RequireRpcWeave();
#pragma warning restore 0414
```

   设计与 P1 实证对齐的点：版本方法**只写常量返回**（`_rpc_weaver_proof.md` §5.4：Debug 与 Release 的 IL 形态不同，
   weaver 用「恰好 1 个 ret / 不含调用 / 恰好 1 个整型常量」读版本，不能按固定指令序列写）；
   gate 字段用 `#pragma warning disable 0414` 压 CS0414（与模板逐字一致）。
2. 发送 helper `public void PMNet_<M>(` → **`private void PMNet_<M>(`**，并把注释改成
   「业务请调用普通名 `M`（编织后它是网络入口），不要直接调用本方法」。
3. `EmitBuildEntry`：RPC 类的**第一条语句**发射 `PMNet_RequireRpcWeave();`
   （按硬要求「首行 Require」，注释放在 doc comment 里，避免第一条语句变成注释）。
4. `EmitRegistry`：在 `RegisterClass` 循环前补注释，把「先求值全部 `BuildEntry`（含 guard）→ 再统一登记」写成顺序契约。
   **两阶段本来就是既有结构**（先填 `entries[]` 再 `RegisterClass`），本轮把它显式化为契约并加门禁断言，
   这样「非 RPC 类先登记成功、后一个 RPC 类才发现未 weave」的半注册状态在结构上不可能出现。
5. `EmitAll`：新增扫描完整性 **fail-closed**（见 2.3 的配套）——
   `facts.SyntaxErrorCount > 0 || facts.ReadFailures.Count > 0` ⇒ 抛 `InvalidOperationException`，拒绝发射。
   放在发射器而不是只放 CLI：`EmitAll` 是公开 API（CLI 之外还有门禁、编辑器便利层等调用方）。

### 2.2 `Tools/PMNetGen/DeclScanner.cs`

1. `PMDeclRawFacts` 新增 `SyntaxErrorFiles` / `ReadFailures`。
2. **读取失败不再只是告警跳过**：记入 `facts.ReadFailures`（跳过 = 声明集静默少一部分，而它生成的注册表「看着正常」）。
3. **语法错误**除计数外记录首例文件与诊断。
4. RPC 收集重构：先把一个方法上的**全部** RPC 标记收集起来，再落**一条**记录（旧实现是「每个标记各建一条记录」，
   会把「一个方法写两个 RPC 标记」折叠成两条同名记录，最终报成规则 10 的「RpcId 重复」——
   报错原因与真实原因不符）。新记录字段：
   `MultipleRpcAttributes` / `IsVirtual` / `IsAbstract` / `IsExtern` / `IsAsync` / `HasTypeParameters` /
   `HasBody`（`Body != null || ExpressionBody != null`）/ `UnsupportedParamModifiers`（`ref`/`out`/`in`/`params`/`default`）。
5. `PMDeclRpcFact` 扩出上述字段（**冻结 IR `PMDeclRpc` 一个字段都不动**，事实走 facts 通道）；
   `BuildClass` 计算 `HasOverload`（同名方法计数 > 1，与 weaver `ValidateRpcSignature` 同口径：按类型里同名方法个数）。
6. 行尾/BOM 归一为 **UTF-8 BOM + CRLF**（该文件此前是 LF 且无 BOM，与本目录兄弟文件及项目编码约定不一致；
   纯格式归一，内容逐字节等价——见 §5 的核查方法）。

### 2.3 `Tools/PMNetGen/DeclValidation.cs`

1. 规则 7 拆三段：
   - **7a**（IR 级，既有）：`static`、非 `void`。保持单一报点，不在 7b 重复报 `static`。
   - **7b**（语法事实，新增）：同名重载 / 同一方法多个 RPC 标记 / `virtual` / `abstract` / `extern`-native /
     `async` / 泛型方法 / 无体（`abstract`/`extern` 本来就没有托管体，已由 7b 前两条报出，故**不重复**报无体）/
     形参 `ref`·`out`·`in`·`params`·`default`。
   - **7c**（既有）：RPC 声明在非 `[PMNetworkObject]` 类上（会被完全忽略）。
   每条消息都点名「编织器为什么不支持」并给出修法方向。
2. `RuleTitles[6]` 更新为「必须是可编织的普通实例方法：返回 void、不得 static/virtual/abstract/extern/async/泛型/无体、
   不得同名重载，形参不得 ref/out/in/params/default」。
3. 新增 `ScanIntegrityGate(model, facts)`：把读取失败 / 语法错误升级为**错误**（`[扫描完整性]` 前缀，不带规则号，
   因此不污染 §5 规则命中表）。`Program.RunDecl` 在 `Errors` 非空时直接拒绝生成，配合 2.1.5 的发射器 fail-closed，
   形成「CLI 与公开 API 两条路都拒绝」的双门。

### 2.4 `Tools/PMDeclCheck/Program.cs`

1. `FindUnexpectedSystemUsage` 白名单加 `System.InvalidOperationException`
   （冻结契约 §2 指定的守卫异常类型，属生成物**必须**发射的代码；断言标题同步更新）。
2. 新增 **§13 冻结编织格式 v1**（12 项）：private 发送 helper（逐 RPC 名，不用 `public void PMNet_` 宽匹配，
   避免命中 `PMNet_Set<成员>`）/ 版本方法常量 0 / 守卫真的读版本 / 抛 `System.InvalidOperationException` /
   实例 gate 字段 / `PMNet_BuildEntry` 首句 Require / 非 RPC 类**不**发射版本门 / 注册表「全部 BuildEntry 在第一个
   `RegisterClass` 之前」/ BuildEntry 数 == 类数。
3. 新增 **§14 扫描完整性门**（10 项）：注入 `ReadFailures` 与 `SyntaxErrorCount` 各验「Validate 报错 + `EmitAll` 抛异常」；
   真实沙箱（`%TEMP%/PMDeclCheck/integrity/**`）造语法错误源文件与**独占锁（`FileShare.None`）**源文件各跑一遍
   扫→校验→发射链。非 Windows 平台对该独占锁用例打印显式 NOTE（不静默跳过）；本仓库与门禁均为 Windows。
4. 新增 **§15 规则 7 补充形态**（15 项）：按**逐条关键词**断言 9 个形态与 5 种形参修饰各自命中，
   并额外断言规则 7 命中数 ≥ 10。不用「规则 7 命中数 > 0」——规则 7 本来就有 static/非 void 两个老用例，
   只数条数会让新增八项一项不生效也能变绿。
5. 头部注释与 BCL 白名单标题同步。

### 2.5 `Tools/PMDeclCheck/Fixtures/Bad.cs`

新增 4 个用例类，覆盖规则 7 补充形态：`BadRpcWeaveShapes`（virtual / abstract / extern / 无体）、
`BadRpcAsyncGeneric`（async / 泛型方法）、`BadRpcOverloadAndMultiAttr`（同名重载 / 一个方法两个 RPC 标记）、
`BadRpcParamModifiers`（ref / out / in / params / default）。每条 RPC 都配了 `_ForceValidate` 同伴，
使该用例只暴露它本意要测的那条规则。文件仍保持**语法合法**（`void M();`、`extern`、`abstract` 只做语法解析，
`Bad.cs` 从不参与编译门禁），实测扫描期语法错误 0 处。

### 2.6 `Tools/PMNetWeaverTest/Program.cs`

1. **不再用临时字符串补格式**：`BuildFrozenFormatGenerated`（把 `public` 改 `private`、给 `BuildEntry` 插 guard）
   整体替换为 `AssertFrozenFormatGenerated`（**只校验不补**），并断言每条锚点**恰好命中一次**：
   private 发送 helper（逐 RPC）×2、版本方法、`return 0;` 唯一、守卫读版本、守卫抛异常、gate 字段、BuildEntry 首句、
   注册表顺序。旧写法会**掩盖生成器本身的回退**（生成器不产出 guard 时补丁把它补上，门禁仍然全绿）。
2. **新增负例**「BuildEntry 缺 gate（注册入口没调 Require）」⇒ 必须失败并命中 weaver 的
   「没有调用 PMNet_RequireRpcWeave()」——这条正好覆盖本轮新加的发射点。
3. **负例注入全部只改 `%TEMP%` 沙箱副本**（每个变体一个独立目录，`WriteVariant` 不再写任何 guard 文件）；
   仓库里的生成器与夹具源码一行不动。
4. 「损坏发送 helper」的注入锚点从 12 空格改为**精确 16 空格**，并断言恰好命中一处。
   旧 12 空格锚点会命中 16 空格行的**后缀**（`IndexOf` 子串匹配），注入位置其实不对——它当时"能用"纯属巧合。
5. 缺 guard 变体改为在沙箱副本里删掉版本方法并断开守卫对它的引用（否则失败的是 C# 编译错误，而不是要验的那条预检）。
6. §0 新增断言：手工 guard 副本文件必须**已删除**。

### 2.7 `Tools/PMNetWeaverTest/PMNetWeaverTest.csproj`

去掉 `Fixture.GeneratedGuard.cs` 的 `<Compile Remove … />` 与相关注释（文件已删）。

### 2.8 删除 `Tools/PMNetWeaverTest/Fixture.GeneratedGuard.cs`

P2 之后版本/守卫由生成器写进 `.g.cs`，手写副本不再需要（任务明确允许删除该夹具）。

---

## 3. 生成物的实际形态（夹具 `WeaveFixture` 实测，`--decl-gen` 输出）

```csharp
        // ---------------- 编织版本门（冻结格式 v1；见 Docs/plans/net-rpc-weaving-contract.md）----------------

        internal static int PMNet_GetRpcWeaveVersion()
        {
            return 0;
        }

        private static int PMNet_RequireRpcWeave()
        {
            if (PMNet_GetRpcWeaveVersion() != 1)
            {
                throw new System.InvalidOperationException("PMNet RPC 未编织：…");
            }

            return 1;
        }

        #pragma warning disable 0414
        private readonly int PMNet_rpcWeaveGate = PMNet_RequireRpcWeave();
        #pragma warning restore 0414
```

```csharp
        private void PMNet_ServerAttack(int p0)      // ← 发送 helper：private
        {
            …callspace 判定 / 数组调用点快照 / 长度门（既有语义未变）…
            if (PMNet.PMRpcDispatch.ShouldExecuteLocal(callspace))
            {
                ServerAttack(p0);                     // ← weaver 只把这一处改指 PMNet_RpcBody_ServerAttack
            }
            …EnqueueRemote(闭包捕获实参)…
        }

        internal static PMNet.PMNetClassEntry PMNet_BuildEntry()
        {
            PMNet_RequireRpcWeave();                  // ← 第一条语句

            PMNet.PMPropertyDescriptor[] props = new PMNet.PMPropertyDescriptor[0];
            …
        }
```

```csharp
        public static void RegisterAll()
        {
            if (PMNet.PMNetRegistry.IsSealed) { return; }

            PMNet.PMNetClassEntry[] entries = new PMNet.PMNetClassEntry[1];
            entries[0] = global::PMWeave.Fixture.WeaveFixture.PMNet_BuildEntry(); // ← 全部 BuildEntry 先求值
            // ★ 顺序是契约的一部分：…（含 RPC 的类会在入口调 PMNet_RequireRpcWeave）…
            for (int i = 0; i < entries.Length; i++)
            {
                PMNet.PMNetRegistry.RegisterClass(entries[i]);   // ← 再统一登记
            }

            PMNet.PMNetRegistry.Seal(PMNet.PMStableHash.GlobalProtocolHash(entries));
        }
```

未改动（**刻意未动**）：`PMNet_RpcInvoke_<M>` 收发结构与校验路由、调用桩的 callspace 判定、数组调用点快照与入队前长度门、
`PMNet_Set<成员>` / 读写器 / OnRep 分发 / 描述符与 ID、协议摘要与线布局。
即「业务 `M` 仍不是 Implementation」：weaver 把业务体拆进 `PMNet_RpcBody_<M>`，两个 helper 唯一的那次调用改指过去。

---

## 4. 门禁证据（可复现命令）

```bash
# 生成器（默认 CLI 副本，供 PMNetWeaverTest 定位；见 §5）
dotnet build Tools/PMNetGen/PMNetGen.csproj -c Release          # 0 警告 / 0 错误

# 声明门禁（含 §13/§14/§15 新增 37 项）
dotnet build Tools/PMDeclCheck/PMDeclCheck.csproj -c Release
dotnet Tools/PMDeclCheck/bin/Release/net8.0/PMDeclCheck.dll     # PASS 107 / FAIL 0（exit 0）

# 编织门禁（消费真实新生成产物，负例全部 TEMP 沙箱）
dotnet build Tools/PMNetWeaverTest/PMNetWeaverTest.csproj -c Release -o <独立目录>
dotnet <独立目录>/PMNetWeaverTest.dll --repo D:/UGit/hyld-master # 通过 192 / 失败 0（exit 0）

# 生产源码只读核查（不写盘）
dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll --decl-scan Client/Assets/Scripts/PMR3
```

关键实测数字：

| 项 | 值 |
|---|---|
| PMDeclCheck | `PASS 107 / FAIL 0`；临时工作区 `%TEMP%/PMDeclCheck`（保留供 `fc /b` 复现） |
| PMNetWeaverTest | `通过 192 项，失败 0 项`；`PMNetWeaver CLI 调用次数：28；夹具真实编译次数：11` |
| 141 → 192 的构成 | +44（`AssertFrozenFormatGenerated`：22 项 × Debug/Release）+ 6（新负例「BuildEntry 缺 gate」）+ 1（手工 guard 副本已删除） |
| 70 → 107 的构成 | §13 = 12、§14 = 10、§15 = 15 |
| 夹具生成期全局摘要 | `0x4CCAD4FD`（与 P1 一致 ⇒ ID / ParamLayoutId / 线协议未漂移） |
| PMR3 生产扫描 | 0 错误、13 RPC、`PMR3Player` hash `0xB09BCD1C`（未变） |
| 编织后稳定 ID | 门禁逐项比对「生成物常量 / 未编织 DLL / 编织后 DLL」三者一致（既有断言保留） |
| 负例全族 | 未知版本 / 假 stamp / 缺 guard / **缺 gate（新增）** / 坏收包 helper / 坏发送 helper / 强名称 —— 全部明确失败、错误信息命中**预期关键词**、DLL/PDB 哈希不变、`--check` 亦失败 |

负例的实际失败信息（证明失败的是**预期那条检查**）：

```
未知版本            : … 返回未知版本 2：只支持 0（未编织）/ 1（已编织）。未知版本必须明确失败，不能用 define 跳过。
假 stamp            : … 返回 1（声称已编织），但缺少私有业务体 PMNet_RpcBody_ClientNotify：损坏/半编织，明确失败。
缺 guard            : … 声明了 PMNet RPC，但缺少生成物方法 PMNet_GetRpcWeaveVersion()…
缺 gate（本轮新增）  : … 的 PMNet_BuildEntry 没有调用 PMNet_RequireRpcWeave()：契约要求注册入口开头调用 guard…
坏收包 helper       : 收包 helper …PMNet_RpcInvoke_ServerAttack 必须**恰好一次**调用 ServerAttack（实际 0）…
坏发送 helper       : 发送 helper …PMNet_ServerAttack 必须**恰好一次**调用 ServerAttack（实际 2）…
强名称              : 输入程序集带公钥（强名称）：编织会破坏签名，契约明确不支持。…
```

---

## 5. 校验方法与格式核查（含一处格式归一）

- **编码/行尾**：对 8 个改动文件逐个按字节核查「UTF-8 BOM + CRLF、裸 LF = 0」。
  `Tools/PMNetGen/DeclScanner.cs` 在改动前即为 **LF 且无 BOM**（与 `git show HEAD:` 的 blob 一致，也与 git 的
  「LF will be replaced by CRLF」提示一致），与同目录兄弟文件及项目约定不符 ⇒ **归一为 BOM + CRLF**；
  内容字节除 `\r\n` 与 BOM 外逐字节等价，`dotnet build` 与两道门禁归一后复跑仍为 107/0 与 192/0。
  其余 7 个文件本就已是 BOM + CRLF，编辑后保持。
- **未越界**：本轮所有写操作只落在 §0 列出的文件；`Tools/PMNetWeaver/**` 只读；
  `Docs/plans/pmnet-ids.json`（ID 锁）与 `Client/Assets/Scripts/PMR3/Generated/**` 未写（
  `--decl-gen` 的 ID 锁一律写 `%TEMP%`）。
- **必要的构建副本（按要求向主侧说明）**：`PMNetWeaverTest` 用 `ProjectReference … Private="false"`
  只构建工具、不复制其产物，`LocateToolDll` 的候选是「测试输出目录 → `Tools/<工具>/bin/{Release,Debug}/net8.0`」，
  因此本轮把 `Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll` 更新成了新的生成器副本（构建产物，非源码）。
  测试自身仍按约定构建到独立输出目录（`%TEMP%/pmweaver-out*`）。
- **未运行**任何会自动改 E2E 源码产物的工程；未启动 Unity / 服务；未做 git 写（`git status`/`git show`/`git diff` 只读）。

---

## 6. 已知中间态、未完成项与风险（诚实口径）

1. **生产 Generated 尚未重生成（属另一并行组）**：`Client/Assets/Scripts/PMR3/Generated/**` 与
   `Tools/PMNetE2E/Generated/**` 仍是旧形态（`public void PMNet_<M>`、无版本门），
   因此现在跑 `--decl-check` 会以 exit 2 报「产物与声明不同步（`PMNet.PMNet.R3.PMR3Player.g.cs` 第 41 行附近，
   即本轮新增的编织版本门位置）」——这是**预期中间态**，不是回归；本轮按要求不写生产 Generated、不跑会自动改它们的目标。
2. **调用点迁移未做（属另一并行组）**：一旦发送 helper 变 `private`，直接调用 `obj.PMNet_<M>(…)` 的既有代码会编译不过。
   这正是 `_rpc_weaver_proof.md` §5.3 的「连锁影响」：必须与生成器改动同批迁移到普通名调用 `obj.M(…)`。
   本轮**没有**改任何调用点（含 `Tools/PMNetE2E`、各 gate、业务源码），也未运行它们。
3. **契约文本漂移（需主侧同步）**：`Docs/plans/net-r2-codegen-contract.md` §4.1/§4.3 仍把
   `public void PMNet_<方法名>` 描述为「业务可见调用桩」。冻结格式 v1 已把它改成 **private 发送 helper +
   业务调用普通名 `M`**。该文件不在本轮写入边界内，故未修改，仅在此登记为待主侧同步项。
4. **新增拒绝规则的适用范围**：规则 7 补充形态只加「编织器明确拒绝」的那些（同名重载 / 多 RPC 标记 / virtual /
   abstract / extern-native / async / 泛型方法 / 无体 / `ref`·`out`·`in`·`params`·`default`）。
   已用 `--decl-scan Client/Assets/Scripts/PMR3`（只读）证明生产 13 条 RPC **一条都不被误拒**（0 错误）。
   指针参数未单列（既有规则 4 的类型集已拒绝指针类型）；构造函数标 RPC 未单列（扫描器只看方法声明，
   该形态到不了生成物，weaver 的 ctor 分支此时不可达）。
5. **未验证**：Unity Editor `assemblyCompilationFinished` / Player `IPostBuildPlayerScriptDLLs` 接线与实机、
   IL2CPP/Mono 运行、MSBuild 增量 weave/check（T-W4/T-W6，`PENDING_USER` / 另一组）。
   本报告**不声称**任何 Unity 侧或真实双端联机结论。
6. **非 Windows 回退**：§14 的「独占锁导致读取失败」用例依赖 Windows 文件共享语义；非 Windows 平台会打印显式 NOTE
   并跳过该一条（其余 9 条仍跑）。本仓库与全部门禁均为 Windows 环境，实际按完整路径执行。

---

## 7. 产出文件一览

| 类型 | 文件 |
|---|---|
| 修改 | `Tools/PMNetGen/DeclEmitter.cs`、`Tools/PMNetGen/DeclScanner.cs`、`Tools/PMNetGen/DeclValidation.cs`、`Tools/PMDeclCheck/Program.cs`、`Tools/PMDeclCheck/Fixtures/Bad.cs`、`Tools/PMNetWeaverTest/Program.cs`、`Tools/PMNetWeaverTest/PMNetWeaverTest.csproj` |
| 删除 | `Tools/PMNetWeaverTest/Fixture.GeneratedGuard.cs`（手工 guard 副本，已被生成器取代） |
| 新增 | 本报告 `Docs/plans/_rpc_weaving_generator.md` |
| 只读（未改） | `Tools/PMNetWeaver/**`、`Tools/PMNetWeaverTest/Fixture.cs`、`Tools/PMDeclCheck/Fixtures/Good.cs`、`Docs/plans/pmnet-ids.json`、`Client/Assets/Scripts/PMR3/Generated/**`、`Tools/PMNetE2E/**` |
