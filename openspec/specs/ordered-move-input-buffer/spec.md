## Purpose

TBD: 定义服务端移动输入缓冲在乱序网络下的按序消费、跳缺帧推进、迟到旧帧拒绝、满缓冲淘汰与惯性兜底语义。

## Requirements

### Requirement: Server consumes movement inputs in ascending frame order
The server MUST consume buffered movement inputs in ascending `SyncFrameId` order for each player. When the exact next expected movement frame is present and within the future tolerance window, the server MUST consume that frame before any larger movement frame.

#### Scenario: Consume exact next frame in order
- **WHEN** a player's last consumed movement frame is `31`, and the buffered legal movement inputs include `32`, `33`, and `34`
- **THEN** the server consumes frame `32` first and updates the player's last consumed movement frame to `32`

### Requirement: Server skips missing movement frames without waiting
If the exact next expected movement frame is missing, the server MUST NOT stall battle progression waiting for it. Instead, the server MUST consume the smallest buffered movement frame that is greater than the last consumed movement frame and still within the future tolerance window.

#### Scenario: Skip a missing frame and advance with the smallest available larger frame
- **WHEN** a player's last consumed movement frame is `31`, frame `32` is missing, and the buffered legal movement inputs include `33` and `34`
- **THEN** the server consumes frame `33` and updates the player's last consumed movement frame to `33`

### Requirement: Server rejects stale late-arriving movement inputs
The server MUST reject any incoming movement input whose `SyncFrameId` is less than or equal to that player's last consumed movement frame. Rejected stale movement inputs MUST NOT enter the movement buffer.

#### Scenario: Drop a late packet older than consumed progress
- **WHEN** a player's last consumed movement frame is `33`, and a movement input with `SyncFrameId=32` arrives later
- **THEN** the server rejects that input and keeps the movement buffer unchanged

### Requirement: Server evicts the oldest buffered movement input when the movement buffer is full
When a player's movement buffer is already at capacity and a newer non-stale movement input arrives, the server MUST evict the buffered input with the smallest `SyncFrameId` and keep the newer input.

#### Scenario: Keep the newest sliding window when full
- **WHEN** a player's movement buffer capacity is `3`, the buffer currently contains movement inputs `31`, `32`, and `33`, and a non-stale movement input `34` arrives
- **THEN** the server evicts frame `31` and the buffer becomes `32`, `33`, `34`

### Requirement: Server keeps inertial fallback when no legal new movement input can be consumed
If no buffered movement input is both newer than the last consumed movement frame and within the future tolerance window, the server MUST keep battle progression running and continue using the last valid movement direction as fallback. In this case, the server MUST NOT advance the consumed movement frame progress.

#### Scenario: Continue using last valid movement when no legal candidate exists
- **WHEN** a player's last consumed movement frame is `40`, all buffered movement inputs are greater than the allowed future tolerance window, and the player has a stored last valid movement direction
- **THEN** the server uses the stored last valid movement direction for this tick and keeps the last consumed movement frame at `40`

### Requirement: Server logs ordered-consumption decisions for verification
The server MUST emit distinct verification logs for ordered movement consumption decisions, including in-order acceptance, gap-skip acceptance, stale rejection, and full-buffer oldest eviction.

#### Scenario: Emit a log when full-buffer eviction occurs
- **WHEN** a new non-stale movement input is admitted into a full movement buffer and the oldest buffered frame is removed
- **THEN** the server emits a movement-buffer log that identifies the oldest-eviction event and the involved frame IDs
