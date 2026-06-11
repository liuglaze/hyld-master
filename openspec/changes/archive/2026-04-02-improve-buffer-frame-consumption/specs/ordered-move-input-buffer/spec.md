## MODIFIED Requirements

### Requirement: Server consumes movement inputs only on the matching authoritative frame
The server MUST consume a buffered movement input for a player only when the input's `SyncFrameId` exactly matches the current authoritative `frameid`. If the current authoritative frame has no matching buffered movement input, the server MUST NOT consume any larger future movement frame during that tick.

#### Scenario: Consume current authoritative frame when present
- **WHEN** the server is processing authoritative frame `32`, and the buffered movement inputs include `32`, `33`, and `34`
- **THEN** the server consumes frame `32` during that tick
- **THEN** the buffered inputs for `33` and `34` remain buffered for later authoritative frames

#### Scenario: Do not skip ahead to a larger buffered frame
- **WHEN** the server is processing authoritative frame `32`, frame `32` is missing, and the buffered movement inputs include `33` and `34`
- **THEN** the server consumes neither `33` nor `34` during frame `32`

### Requirement: Server rejects stale late-arriving movement inputs
The server MUST reject any incoming movement input whose `SyncFrameId` is less than or equal to that player's current authoritative frame or less than or equal to that player's already consumed movement progress. Rejected stale movement inputs MUST NOT enter the movement buffer.

#### Scenario: Drop a late packet older than the current authoritative frame
- **WHEN** the server is processing authoritative frame `33`, and a movement input with `SyncFrameId = 32` arrives later
- **THEN** the server rejects that input and keeps the movement buffer unchanged

### Requirement: Server evicts the oldest buffered movement input when the movement buffer is full
When a player's movement buffer is already at capacity and a newer non-stale movement input arrives, the server MUST evict the buffered input with the smallest `SyncFrameId` and keep the newer input.

#### Scenario: Keep the newest sliding window when full
- **WHEN** a player's movement buffer capacity is `3`, the buffer currently contains movement inputs `31`, `32`, and `33`, and a non-stale movement input `34` arrives
- **THEN** the server evicts frame `31` and the buffer becomes `32`, `33`, `34`

### Requirement: Server keeps inertial fallback when the current authoritative movement input is missing
If the current authoritative frame has no buffered movement input for a player, the server MUST keep battle progression running and continue using the last valid movement direction as fallback. In this case, the server MUST NOT advance consumed movement progress and MUST NOT consume a future movement frame early.

#### Scenario: Continue using last valid movement when current frame input is missing
- **WHEN** the server is processing authoritative frame `40`, there is no buffered movement input with `SyncFrameId = 40`, and the player has a stored last valid movement direction
- **THEN** the server uses the stored last valid movement direction for this tick
- **THEN** the server keeps the consumed movement progress unchanged

### Requirement: Server logs strict-frame movement consumption decisions for verification
The server MUST emit distinct verification logs for strict-frame movement consumption decisions, including exact-current-frame acceptance, missing-current-frame fallback, stale rejection, and full-buffer oldest eviction.

#### Scenario: Emit a log when current-frame input is missing and fallback is used
- **WHEN** the current authoritative frame has no buffered movement input and the server uses the stored last valid movement direction instead
- **THEN** the server emits a movement-buffer log that identifies the missing-current-frame fallback event
