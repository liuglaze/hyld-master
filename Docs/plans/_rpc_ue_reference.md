# UE 原生 RPC 编程体验参考（服务于 hyld 移植）

> 调查类型：只读核对（explore）。核对对象：`ProjectMecury`（UE 5.6 + 引擎 MOD）与 `D:/UE_Project/UnrealEngine-5.6`。
> 代表性样本：`UPMHeroComponent::ChangeFood`（`UFUNCTION(BlueprintCallable, Server, Reliable)`）。
> 结论按「事实 / 推断」区分；所有结论均给出可复核的 `文件:行`。

---

## 已确认

### A. 命名规则（事实）

| 角色 | 写法 | 谁声明 | 谁生成 |
|---|---|---|---|
| 普通名字 | `void ChangeFood(const FName FoodInventoryID);` | 业务在类头写，带 `UFUNCTION(...)` 修饰 | UHT 之后生成**函数体**（不是声明） |
| `_Implementation` | `void ChangeFood_Implementation(const FName FoodInventoryID);` | 业务写声明 + 实现 | 由 UHT 在 `.generated.h` 的 `RPC_WRAPPERS` 宏里补 `virtual` 声明 |
| `_Validate`（原生） | `bool ChangeFood_Validate(const FName FoodInventoryID);` | 业务可写（`WithValidation`）；若没写，UHT 自动补声明 | thunk 里被调用（仅有 `NetValidate` flag 时） |
| `_ForceValidate`（本项目扩展） | `virtual ENetValidateResult ChangeFood_ForceValidate(const FName FoodInventoryID);` | **只能业务手写**，UHT 不生成 | UHT 按名字**探测**是否存在，存在就注入 thunk 调用 |

- 普通名字/`_Implementation`/`_Validate` 的命名关系由 UHT 固定推导：`CppImplName = EngineName + "_Implementation"`，`CppValidationImplName = EngineName + "_Validate"`。
  证据：`EpicGames.UHT/Parsers/UhtFunctionParser.cs:687`、`EpicGames.UHT/Types/UhtFunction.cs:152-160`。
- `_ForceValidate` 名字为 `EngineName + "_ForceValidate"`，且**只对 `FUNC_NetServer` 的上行 RPC 生效**。
  证据：`EpicGames.UHT/Types/UhtFunction.cs:601-608`。
- 项目样本：
  - 头（普通名字 + ForceValidate 声明）：`Source/MecuryGame/Gameplay/Pawn/Hero/PMHeroComponent.h:103-106`
  - 实现：`.../PMHeroComponent.cpp:169-185`（`_Implementation`）、`.../PMHeroComponent.cpp:2188-2216`（`_ForceValidate`）
  - 生成的声明：`Intermediate/.../UHT/PMHeroComponent.generated.h:63-95`
  - 生成的函数体 / thunk：`.../UHT/PMHeroComponent.gen.cpp:329-335`、`373-384`、`1510-1511`

**强制规则（事实，来自 UHT MOD）**：
- 项目模块/插件的 Server RPC 若缺 `_ForceValidate` → UHT **error**（引擎模块降级为 warning）。
  证据：`EpicGames.UHT/Types/UhtFunction.cs:853-895`、`Types/UhtModule.cs:112-116`。
- `_ForceValidate` 必须声明为 `virtual`，否则 UHT error。证据：同上 `:893-896`。
- 已有 `WithValidation`（原生 `_Validate`）的 Server RPC **豁免** `_ForceValidate`；两者**并存会 warning**（推荐二选一）。
  证据：同上 `:869-878`。全项目仅剩一处原生 `WithValidation`：`Plugins/Behavior/Source/BehaviorCore/Classes/BehaviorAbilityComponent.h:570`。
- 全局总闸：环境变量 `UE_FORCEVALIDATE_DISABLE=1/true` 可整体关闭强制检查（不必重编 UHT）。证据：`UhtFunction.cs:1031-1043`。
- 引擎会自动 `#include "UObject/NetValidateResult.h"` 进 `.generated.h`（业务头无需自己 include）。
  证据：`UhtHeaderCodeGeneratorHFile.cs:99-100`、`UhtHeaderCodeGeneratorCppFile.cs:55,88-89`；实测 `PMHeroComponent.generated.h:16`。

### B. 「用户直接调用普通名字」到底走什么（事实，核心答案）

以 `ChangeFood(SomeName)` 为例，编译期看到的是 UHT 生成的函数体（不是内联到 `_Implementation`）：

```cpp
// Intermediate/.../UHT/PMHeroComponent.gen.cpp:329-335
struct PMHeroComponent_eventChangeFood_Parms { FName FoodInventoryID; };
static FName NAME_UPMHeroComponent_ChangeFood = FName(TEXT("ChangeFood"));
void UPMHeroComponent::ChangeFood(const FName FoodInventoryID)
{
    PMHeroComponent_eventChangeFood_Parms Parms;
    Parms.FoodInventoryID = FoodInventoryID;
    UFunction* Func = FindFunctionChecked(NAME_UPMHeroComponent_ChangeFood);
    ProcessEvent(Func, &Parms);          // ← 打包到 Parms → ProcessEvent
}
```

完整执行链（按调用顺序）：

1. **普通名字 = UHT 生成的「打包 + ProcessEvent」包装函数**。
   生成逻辑在 `UhtHeaderCodeGeneratorCppFile.cs:1602-1652`。注释明确写了：网络函数**不能**走 BlueprintNativeEvent 的 “直接调 `_Implementation`” 优化，因为 `ProcessEvent` 承担了必须保留的复制/网络行为（`isNetEvent` → `doNativeImplOptimization == false`，`:1602-1618`）。
2. `UObject::ProcessEvent(Function, Parms)`（`Runtime/CoreUObject/Private/UObject/ScriptCore.cpp:2038`）。
   因为 RPC 带 `FUNC_Native`，`ProcessEvent` 先做 callspace 判定（`:2072-2082`）：
   ```cpp
   if ((Function->FunctionFlags & FUNC_Native) != 0) {
       int32 FunctionCallspace = GetFunctionCallspace(Function, NULL);
       if (FunctionCallspace & FunctionCallspace::Remote) { CallRemoteFunction(Function, Parms, NULL, NULL); }
       if ((FunctionCallspace & FunctionCallspace::Local) == 0) { return; }   // 纯 Remote：到此为止
   }
   ```
   → 然后继续走完函数：建 `FFrame NewStack`、拷贝参数，最后 `Function->Invoke(this, NewStack, ReturnValueAddress)`（`:2164-2222`）。
3. `UFunction::Invoke` 对 native 函数就是**调 native 函数指针**：
   ```cpp
   // Runtime/CoreUObject/Private/UObject/Class.cpp:7442-7453
   return (*Func)(Obj, Stack, RESULT_PARAM);   // Func = execChangeFood
   ```
   该指针由 UHT 的 native 注册表绑定：`PMHeroComponent.gen.cpp:1510-1511` 的 `{ "ChangeFood", &UPMHeroComponent::execChangeFood }`。
4. **native thunk `execChangeFood`**（`PMHeroComponent.gen.cpp:373-384`，模板见 `UhtHeaderCodeGeneratorCppFile.cs:2490-2546`）：
   ```cpp
   DEFINE_FUNCTION(UPMHeroComponent::execChangeFood)
   {
       P_GET_PROPERTY(FNameProperty, Z_Param_FoodInventoryID);   // 从 FFrame 弹参
       P_FINISH;
       P_NATIVE_BEGIN;
       if (FNetForceValidate::HandleResult(P_THIS, TEXT("ChangeFood"),
               P_THIS->ChangeFood_ForceValidate(Z_Param_FoodInventoryID)))   // ← 项目扩展：先校验
       { return; }                                                          // NetReject：跳过实现、不断连
       P_THIS->ChangeFood_Implementation(Z_Param_FoodInventoryID);           // ← 真正业务体
       P_NATIVE_END;
   }
   ```
5. **网络发送**在 callspace 里完成：`CallRemoteFunction` → `AActor::CallRemoteFunction`（`Runtime/Engine/Private/Actor.cpp:5676-5697`）→ `NetDriver->ProcessRemoteFunction(...)`。

**callspace 决定「本地执行哪些步骤」**（`AActor::GetFunctionCallspace`，`Actor.cpp:5475-5672`；`UActorComponent` 直接转发给 Owner，`Components/ActorComponent.cpp:1309-1319`）：

| 场景 | GetFunctionCallspace 结果 | 是否 CallRemoteFunction | 是否跑 execX（含校验） |
|---|---|---|---|
| 客户端调用 Server RPC | `Remote`（`:5671`，走完整个函数未命中任何提前 return） | 是（发往服务器） | **否**（`Local` 位未置，`ProcessEvent:2079-2082` 提前 return） |
| 服务器收到 Server RPC | `Local`（`:5564-5570`，`bIsServer && !NetClient` → `return Callspace`） | 否 | **是** |
| 服务器调用 Client/Multicast RPC | `Local \| Remote`（多播，`:5556-5566`） | 是 | **是** |
| Standalone / DS 自发调用 Server RPC | `Local` | 否 | **是** |

**服务端收包入口**（两条网络栈都汇到 `ProcessEvent`）：
- 传统复制：`FObjectReplicator::ReceivedRPC` → `CallProcessEventForReceivedRPC`（`Runtime/Engine/Private/DataReplication.cpp:1483,1511-1517`）→ `Object->ProcessEvent(Function, Params)`。
- Iris：`NetRPC.cpp:618-628` → `Object->ProcessEvent(ActualFunction, FunctionParameters)`。
- 两条路径在 `ProcessEvent` **之前**都会注入连接：`FNetForceValidate::SetCurrentNetConnectionUserData(Connection)`（`DataReplication.cpp:1455-1470` 的 `FScopedForceValidateConnection`；`NetRPC.cpp:625-627`）。

### C. 本地调用是否校验（事实）

**会执行校验函数本体，但默认不会产生拦截/上报效果。** 分两层看：

1. **校验调用本身**：`_Validate` 与 `_ForceValidate` 的调用都写在 native thunk `execX` 内（`UhtHeaderCodeGeneratorCppFile.cs:2503-2530`）。
   `execX` 只在 callspace 含 `Local` 时执行（`ScriptCore.cpp:2072-2082` / `CallFunction:1130-1180`）。
   → **客户端本地调用 Server RPC：不跑校验**（纯 Remote）；**服务器收发两侧的本地执行路径：跑校验**（含 DS 自己发起的本地调用）。
2. **校验效果**：
   - 原生 `_Validate`：返回 false → `RPC_ValidateFailed(TEXT("..._Validate"))`（thunk 内）→ 仅把原因写进全局 `GLastRPCFailedReason`（`CoreNet.cpp:665-668`）→ 收包侧 `ReceivedRPC` 检测到非空后 `return false`（`DataReplication.cpp:1497-1501`）→ **上层关闭通道/连接**（这就是原生 `_Validate` 的「硬断连」语义）。
   - 项目 `_ForceValidate`：返回 `ENetValidateResult`，交给 `FNetForceValidate::HandleResult` 处置（`NetValidateResult.cpp:113-165`）：
     - `net.ForceValidate.Enabled=0` → 短路放行（不记录）。
     - **`net.ForceValidate.SkipLocalCalls=1`（默认）且 `GetCurrentNetConnectionUserData()==nullptr`（= 本地调用）→ 直接放行、不 Record、不拦截**（`NetValidateResult.cpp:20-35,118-150`）。
     - `NetAccept` → 放行；`NetReport` → 记录后放行；`NetReject` → 记录后拦截（跳过 `_Implementation`），**绝不调用断连 API**。
   - 关键细节：`_ForceValidate` 是 `HandleResult(...)` 的**实参**，所以本地调用时它**仍会被完整执行**（业务判据、payload 收集都会跑），只是 `HandleResult` 提前返回 false，NetReject 无法拦本地调用。
     证据：thunk 生成 `${CppForceValidationImplName}(...)` 作为实参（`UhtHeaderCodeGeneratorCppFile.cs:2522-2525`）+ HandleResult 短路（`NetValidateResult.cpp:140-150`）。
   - 编辑器自动化测试豁免本地短路：`GIsAutomationTesting` 为真时不短路（`NetValidateResult.cpp:141-146`），否则测试断言会静默失效。

### D. ForceValidate 引擎补丁清单（移植时必须一起搬）

不是纯项目代码，是**引擎 + UHT + UBT 共享模型**三处补丁：

| # | 文件 | 作用 |
|---|---|---|
| 1 | `Runtime/CoreUObject/Public/UObject/NetValidateResult.h`（新增） | `ENetValidateResult` 三态枚举、`FNetForceValidateRecord`、`FNetForceValidate`（`HandleResult`/`IsEnabled`/`OnRecord`/`SetPayload`/`AppendPayloadKV`/`SetCurrentNetConnectionUserData`/`GetCurrentNetConnectionUserData`/`IsCurrentCallDirectlyFromNetwork`）、宏 `NET_FORCE_VALIDATE_REASON`/`NET_FORCE_VALIDATE_PAYLOAD`、日志类 `LogNetForceValidate` |
| 2 | `Runtime/CoreUObject/Private/UObject/NetValidateResult.cpp`（新增） | 实现 + 2 个 CVar：`net.ForceValidate.Enabled`（默认 1）、`net.ForceValidate.SkipLocalCalls`（默认 1）；`thread_local` 连接/payload/一次性标志 |
| 3 | `Programs/Shared/EpicGames.UHT/Types/UhtFunction.cs` | 探测 `<Name>_ForceValidate` 声明（`:601-608`）、强制声明/必须 virtual/并存 warning/`UE_FORCEVALIDATE_DISABLE` 总闸（`:853-895,1031-1043`） |
| 4 | `Programs/Shared/EpicGames.UHT/Exporters/CodeGen/UhtHeaderCodeGeneratorCppFile.cs` | thunk 注入 `FNetForceValidate::HandleResult(...)`（`:2515-2530`）与自动 include（`:55,88-89`） |
| 5 | `Programs/Shared/EpicGames.UHT/Exporters/CodeGen/UhtHeaderCodeGeneratorHFile.cs` | `.generated.h` 自动 include（`:99-100`） |
| 6 | `Programs/Shared/EpicGames.Core/UHTTypes.cs` | manifest 模型 `UHTManifest.Module.EnforceForceValidate`（`:134-135`） |
| 7 | `Runtime/Engine/Private/DataReplication.cpp` | 传统复制收包注入 `SetCurrentNetConnectionUserData`（`:1455-1470`） |
| 8 | `Runtime/Experimental/Iris/Core/Private/Iris/ReplicationSystem/NetBlob/NetRPC.cpp` | Iris 收包注入（`:618-628`） |

补丁特征串：`PM ENGINE MOD BY ZiLin`（UHT/CoreUObject/RPC 侧）与 `PM ENGINE MOD BY liuxiang12`（`ProcessEvent` 里的 `PM_PROLOGUE_OBFUSCATE`，与本机制无关）。

---

## 高概率推断（依据与置信度）

1. **`EnforceForceValidate` 的 UBT 侧 population 在本引擎树中缺失 → 实际生效的是默认规则**（工程模块/插件 error、引擎模块 warn）。
   依据：`UHTTypes.cs:134` 注释指向 `ExternalExecution`，但 `Programs/UnrealBuildTool/System/ExternalExecution.cs:1084-1100` 的 `CreateUHTManifest` 并未写该字段；`ModuleRules` 中也没有 `bEnforceForceValidate`。`UhtModule.cs:116` 的 `?? !IsPartOfEngine` 兜底因此是唯一实际路径。
   置信度：中高（可能是 UBT 源码被还原/未同步，或该字段靠反射填充——未在 .cs 中看到反射填充代码）。
2. **hyld 若已有一个 `_ForceValidate` 类似物，命名/语义可能与 `ENetValidateResult` 不同**，移植时要对齐三态语义（Accept/Report/Reject）与「Reject 不断连」的约定。
   依据：`NetValidateResult.h:12-25` 明确把「与引擎既有 `_Validate`（失败即断连）并行、本机制失败不断连」写进设计注释。
   置信度：中（未读 hyld 现状；仅在 `Docs/plans/` 看到既有 `_r2_fix_rpc.md` / `_r2_review_rpc.md`）。
3. **模板/宏展开顺序上，`_ForceValidate` 与 `_Validate` 是互斥生成**（二选一），而不是叠加。
   依据：`UhtHeaderCodeGeneratorCppFile.cs:2517-2519` 的 `!HasAnyFlags(NetValidate)` 守卫 + `UhtFunction.cs:869-878` 的 warning 文案。
   置信度：高。

---

## 无法确定（缺少证据）

1. **`ENetValidateResult` 未来扩展位是否有「有状态」新状态**：头注释说无状态新状态只改 `HandleResult`，有状态（如限流）需同步改 UHT thunk 模板（`NetValidateResult.h:20-22`）。hyld 若要加限流类状态，需自行确认 thunk 模板是否够用——本次未审查任何限流实现。
2. **项目侧桥接层（消费 `OnRecord` 的上报/限流/DDoS 侧）未核对**：只知 `OnRecord().Broadcast(...)` 是唯一出口（`NetValidateResult.cpp:108-110`），消费端在本项目里如何落地（`Doc/RPC校验/` 及桥接代码）**超出本次允许边界**，未查。
3. **hyld 当前引擎是否已打同构补丁、补丁版本是否一致**：未访问 `D:/UGit/hyld-master` 的引擎树（授权边界只给了 ProjectMecury 与 UnrealEngine-5.6）。
4. **`Remote` 说明符（`FUNC_Net` 且非 Client/Server/Multicast）路径**：见到 `UE::Net::Private::FScopedRemoteRPCMode`（`Runtime/CoreUObject/Public/UObject/CoreNetContext.h`）与 `UhtHeaderCodeGeneratorCppFile.cs:1640-1643` 的 Sending 栈，但未确认本项目是否真的使用该类 RPC，也未追其 callspace 语义。
5. **原生 `_Validate` 失败后「close channel」的确切调用点**：确认了 `ReceivedRPC` 返回 false（`DataReplication.cpp:1497-1501`）与其唯一调用者（`UActorChannel::ProcessBunch` 附近），但未逐行读到 `Close(...)` 那一句（`DataChannel.cpp` 里 grep `RPC_GetLastFailedReason` 无命中，说明关闭在更上层）。

---

## 已检查范围

**必读文档（按任务指定顺序，全部读完）**
- `D:/UE_Project/ProjectMecury/AGENTS.md`（全文）
- `D:/UE_Project/ProjectMecury/AI-Instruction.md`（全文）
- `D:/UE_Project/ProjectMecury/Docs/战斗模块架构地图.md`（全文 532 行，63KB，分两次读完）
- `D:/UE_Project/ProjectMecury/Docs/项目基建/客户端与服务器架构梳理.md`：§4.5（两条通信链路）、§5.1（两套网络栈）、§7（局内数据流）、§9（RPC 框架实现原理，全文）。**结论：该文档的「RPC」节讲的是 Mos Python 集群 RPC（`@rpc_method` 空装饰器 + `getattr` 反射 + `req_id` 配对），与 UE 原生 `UFUNCTION(Server/Client)` 机制无关；UE 侧只有 §5.1/§7 提到「栈 B 用 UE 原生复制 + Iris」，没有讲 UE RPC 生成代码/thunk/校验。**
- `D:/UE_Project/ProjectMecury/.agents/skills/ue-project-search/SKILL.md`（全文）
- `D:/UE_Project/ProjectMecury/.agents/skills/forcevalidate-implementation/SKILL.md`（全文；用于确认 `_ForceValidate` 的项目约定）

**路由与工具状态**
- 先查 `ue-code-server_ue_get_status`：`index_ready=true`，engine/project class+function 注册表全 ready（109407 文件）。
- 按 skill 路由：结构化符号查询优先 `ue-code-server`；本项目该问题集中在「生成产物 + 引擎源码」，直接按 `文件:行` 精读，未扩散到业务技能。
- `forcevalidate-implementation` skill 的 `references/` 与 `scripts/` **未读**（任务明确「不要调查业务技能」，且其正文已足够给出命名与三态约定）。

**项目侧直接引用（边界内）**
- `Source/MecuryGame/Gameplay/Pawn/Hero/PMHeroComponent.h:103-110`（普通名字 + `_ForceValidate` 声明）
- `Source/MecuryGame/Gameplay/Pawn/Hero/PMHeroComponent.cpp:169-185,2188-2216`（`_Implementation` / `_ForceValidate`）
- `Intermediate/Build/Win64/UnrealEditor/Inc/MecuryGame/UHT/PMHeroComponent.generated.h:16,63-95,134`
- `Intermediate/.../UHT/PMHeroComponent.gen.cpp:324-385,1510-1511`
- 旁证 grep（未逐行读实现）：`Source/**` `_ForceValidate` 声明/定义共 40+ 处（Mover/Quest/Indicator/Interact/Hero/Arena 等），确认该命名是全局约定；`WithValidation` 仅 `BehaviorAbilityComponent.h:570` 一处。

**引擎侧直接实现（边界内；DataReplication/NetRPC 超出字面边界，但为「本地 vs 网络」判据的唯一注入点，已记录）**
- UHT：`EpicGames.UHT/Exporters/CodeGen/UhtHeaderCodeGeneratorCppFile.cs:1602-1652,2490-2546`；`.../UhtHeaderCodeGeneratorHFile.cs:99-100,1268-1289`；`EpicGames.UHT/Types/UhtFunction.cs:595-615,838-905,1031-1043`；`EpicGames.UHT/Types/UhtModule.cs:112-116`；`EpicGames.UHT/Parsers/UhtFunctionParser.cs:687`；`EpicGames.Core/UHTTypes.cs:134-135`
- Runtime/CoreUObject：`Private/UObject/ScriptCore.cpp:1114-1180,1344-1370,2038-2237`；`Private/UObject/Class.cpp:7442-7453`；`Public/UObject/NetValidateResult.h`（全文）；`Private/UObject/NetValidateResult.cpp:12-170`；`Private/UObject/CoreNet.cpp:665-668`；`Public/UObject/CoreNetContext.h`（全文）
- Runtime/Engine：`Private/Actor.cpp:5475-5697`；`Private/Components/ActorComponent.cpp:1309-1319`；`Private/DataReplication.cpp:1435-1520`
- Iris：`Private/Iris/ReplicationSystem/NetBlob/NetRPC.cpp:600-640`
- UBT：`Programs/UnrealBuildTool/System/ExternalExecution.cs:1084-1100`（确认 `EnforceForceValidate` 未被 population）

**生成文件忽略区完备性说明（按规则说明）**
- `Intermediate/` 属 `.gitignore` 忽略区，FF 内容索引不覆盖 → 生成文件（`.gen.cpp`/`.generated.h`）用手写绝对路径 + `bash find`/`grep` 实时定位，未依赖 `ffgrep`。这是允许的例外，原因：目标在 gitignored 区。
- `Source/` 的 `_ForceValidate` / `WithValidation` 扫描用了 `ffgrep`（FF 索引覆盖 Source/）。该扫描仅作「命名是全局约定」的旁证，**不作为完备性结论**；若需「全项目无遗漏」证明，应改用 `rg --no-ignore` 实时全扫（本次未做，因不是必要证据）。

---

## 建议下一步（最小补充查询 / 运行时验证）

1. **hyld 侧现状对齐（最小查询）**：只看 `D:/UGit/hyld-master` 引擎树的 `Runtime/CoreUObject/Public/UObject/NetValidateResult.h` 是否存在、`EpicGames.UHT/Types/UhtFunction.cs` 是否含 `ForceValidate` 特征串，即可判定「补丁是否已在位 / 版本是否一致」。
2. **`Remote` 路径是否需要**：`rg -n "FUNC_Net[^SCM]|UFUNCTION\(.*Remote"` 限定 hyld 的 Source/Plugins，确认是否有 `UFUNCTION(Remote)` 类 RPC；若有，需补读其 callspace 语义。
3. **原生 `_Validate` 断连点（可选，若 hyld 要保留 WithValidation）**：追 `DataReplication.cpp:1497` 返回 false 后 `UActorChannel::ProcessBunch` 的 `Close(...)` 一行即可闭环。
4. **运行时验证（最有力，一条即可）**：
   - PIE 二客户端，在 `ChangeFood_ForceValidate` 首行下断点。
   - 客户端 A 本地按键触发 → 预期**不进断点**（纯 Remote，无本地 thunk）。
   - 服务器侧收到 RPC → 预期**进断点一次**，且 `GetCurrentNetConnectionUserData() != nullptr`。
   - 服务器本地（DS console `ChangeFood`）触发 → 预期**进断点**但 `GetCurrentNetConnectionUserData() == nullptr`，且 `net.ForceValidate.SkipLocalCalls=1` 时不产生 Record。
   - 对照开关：`net.ForceValidate.Enabled 0` / `net.ForceValidate.SkipLocalCalls 0` 各跑一次，确认短路语义。
5. **移植交付物建议**：把 `已确认 §D` 的 8 个补丁点做成一份 patch 清单 + 特征串校验脚本（`grep -c "PM ENGINE MOD"`），避免 hyld 漏搬 UBT/UBT 共享模型（第 6 项最容易漏，且漏了只降级成默认规则、不报错）。
