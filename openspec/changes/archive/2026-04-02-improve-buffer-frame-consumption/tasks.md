## 1. 客户端目标帧调整

- [x] 1.1 修改 `BattleManger.CalcTargetFrame`，将目标帧公式切换为完整 RTT + 抖动余量 + 安全余量
- [x] 1.2 校准客户端 fallback lead / 旧 `inputBufferSize` 的使用方式，避免与新安全余量重复叠加
- [x] 1.3 更新客户端调试日志，输出 `sync_frameID`、`targetFrame`、RTT/抖动余量与上报 `OperationID`

## 2. 服务端严格帧消费语义

- [x] 2.1 修改 `BattleController.CollectAndBroadcastCurrentFrame`，仅消费 `SyncFrameId == frameid` 的移动输入
- [x] 2.2 调整缺帧分支，缺失当前帧输入时仅沿用 `lastValidMove`，不提前消费未来帧
- [x] 2.3 调整 `UpdatePlayerOperation` 的陈旧输入判定与缓冲保留语义，保证未来帧输入可等待其对应权威帧
- [x] 2.4 更新服务端验证日志，区分“当前帧命中”“当前帧缺失 fallback”“拒绝陈旧输入”“满缓冲淘汰”

## 3. 规格与联调文档同步

- [x] 3.1 对齐相关注释与文档，更新客户端/服务端 CLAUDE 文档中的目标帧与消费语义说明
- [x] 3.2 更新 `BothSide.md`，记录 `OperationID`、target frame 与严格帧消费的新跨端约定
- [x] 3.3 复核 OpenSpec 变更与实现结果，确保 proposal/design/spec/tasks 与最终代码一致
