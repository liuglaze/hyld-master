## Context

当前战斗链路使用 UDP 上传 `BattleInfo.SelfOperation`，移动输入通过 `OperationID`/`SyncFrameId` 进入服务端严格按帧消费窗口，攻击输入通过 `AttackId` 进入待确认重发窗口。现状下，普通连续移动依赖持续采样自然覆盖丢包，但停步 zero-move 与新攻击都属于关键边沿：前者一旦关键帧丢失，服务端会继续沿用 `lastValidMove` fallback；后者虽然已有 `pendingAttacks` 跨帧重发，但首帧丢失仍会拉高首发确认延迟。

本次方案选择“方案 A”：当单个本地逻辑帧包含关键输入（停步或新攻击）时，客户端在该逻辑帧内立即重复发送同一份 `SelfOperation` 2~3 次。该方案不修改协议，不引入可靠通道，只利用现有服务端 move 覆盖与 attack 去重语义提升首帧送达概率。

约束：
- 不改变 `OperationID`、`ClientAckedFrame`、`AttackId` 语义。
- 不把所有输入都重复发送，只针对关键输入帧。
- 服务端现有严格当前帧消费语义必须保持不变。

## Goals / Non-Goals

**Goals:**
- 提高停步 zero-move 帧与新攻击帧在单次逻辑帧内的送达概率。
- 保持服务端现有移动缓冲严格按帧消费与攻击去重语义不变。
- 不新增协议字段或新消息类型。
- 为联调增加可验证日志，能够区分“普通单发帧”和“关键输入 burst-send 帧”。

**Non-Goals:**
- 不提供可靠传输或 ACK/重传协议。
- 不改变攻击现有 `pendingAttacks` 跨帧重发窗口。
- 不调整服务端 fallback 策略、buffer 大小或 target-frame 算法。
- 不实现“跨多个 BattleTick 的 resend window”；本 change 仅覆盖同帧 burst-send。

## Decisions

### Decision: 关键输入采用同帧 burst-send，而不是所有输入全局重复发送
- 选择：仅当本地逻辑帧检测到“停步边沿”或“本帧新生成攻击”时，发送 2~3 次完全相同的 `BattleInfo`。
- 原因：
  - 停步和攻击是体感敏感的关键边沿，收益远高于连续移动。
  - 全局重复发送会显著增加带宽和日志噪音，但对普通连续移动收益有限。
- 备选方案：对全部操作包三连发。
  - 放弃原因：收益/成本比差，且会放大服务端 buffer 更新噪音。

### Decision: burst-send 的重复包保持完全相同的操作语义
- 选择：同一逻辑帧内的所有重复包复用相同 `OperationID`、`ClientAckedFrame`、移动值和 `AttackId` 集合。
- 原因：
  - 服务端对同一 `SyncFrameId` 的移动输入已有覆盖语义。
  - 服务端对同一 `AttackId` 已有去重语义。
  - 保持包内容完全一致，最容易证明幂等性。
- 备选方案：为重复包分配不同 frame 或不同临时序号。
  - 放弃原因：会破坏现有 target-frame/AttackId 语义，并引入新的协议复杂度。

### Decision: 攻击 burst-send 只增强首帧送达，不替代 pendingAttacks
- 选择：攻击仍由 `pendingAttacks` 负责跨帧持续重发；若当前帧包含新攻击，则该帧额外 burst-send。
- 原因：
  - 现有 pending 机制已提供应用层重发窗口。
  - burst-send 的目标只是降低首发漏包概率，不应重写攻击确认语义。
- 备选方案：只保留 burst-send，移除 pendingAttacks。
  - 放弃原因：会降低攻击在连续丢包下的恢复能力，风险过高。

### Decision: 为 burst-send 增加显式验证日志
- 选择：客户端记录本帧是否触发关键输入 burst、重复发送次数与触发原因；服务端记录重复 move update / duplicate attack skip 的关键验证点。
- 原因：
  - 该 change 的价值依赖网络丢包下的行为验证，必须快速区分“没触发 burst”与“触发了但仍丢”。
- 备选方案：仅依赖现有日志。
  - 放弃原因：难以从现有日志中直接看出同帧发送了多少次。

## Risks / Trade-offs

- [带宽与日志噪音上升] → 仅对关键输入帧 burst-send，并限制重复次数为 2~3 次。
- [同帧多次发送仍可能全丢] → 明确该方案只提高概率，不承诺可靠送达。
- [攻击与停步同帧同时出现时包体重复更多] → 复用同一份操作快照，避免分别构造两类特殊包。
- [服务端 move 更新日志数量增加] → 将 burst 验证日志限定为关键路径，避免每次覆盖都输出高噪音详细日志。
- [未来若切到跨 tick resend window，语义可能冲突] → 本设计明确限定为“同一逻辑帧内 burst-send”，便于后续独立扩展。

## Migration Plan

1. 客户端增加关键输入判定：识别停步边沿与本帧新攻击。
2. 在发送入口支持按指定次数重复发送同一份 `BattleInfo`。
3. 保持服务端幂等语义，仅补充验证日志，不改变消费规则。
4. 在压测档丢包环境下验证：停步前弹出现频率下降，攻击首发确认率不下降。
5. 若验证失败，可将重复次数配置回 1，回退到现有单发逻辑。

## Open Questions

- 默认重复次数最终取 2 还是 3；是否需要抽成可配置常量。
- “新攻击”判定是按本帧新增 `AttackId`，还是只要 `AttackOperations.Count > 0` 就 burst；推荐前者，但实现前需统一口径。
- 客户端是否需要对 burst-send 的第 2/3 次发送省略部分高频日志，避免本地日志刷屏。
