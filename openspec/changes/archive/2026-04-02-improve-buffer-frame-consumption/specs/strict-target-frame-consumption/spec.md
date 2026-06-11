## ADDED Requirements

### Requirement: Client computes movement target frames using full round-trip latency
The client MUST compute each movement input target frame from the latest authoritative `sync_frameID` plus a full RTT allowance, a bounded jitter allowance, and a small safety allowance. The resulting target frame MUST represent the authoritative server frame that the current local input is intended to reach.

#### Scenario: Initialized RTT pushes target beyond half-RTT estimate
- **WHEN** the client has `sync_frameID = 100`, an initialized smoothed RTT equivalent to 6 frames, a jitter allowance of 1 frame, and a safety allowance of 1 frame
- **THEN** the computed target frame is `108`

#### Scenario: Uninitialized RTT falls back to configured minimum lead
- **WHEN** the client has not yet initialized RTT sampling
- **THEN** the client computes a deterministic fallback target frame lead that does not depend on half-RTT estimation

### Requirement: Client binds uploaded movement input to the computed target frame
For each local battle tick, the client MUST upload movement input using an `OperationID` that represents the target authoritative frame for that input. The uploaded frame identifier MUST remain aligned with the client tick that the movement snapshot was sampled for.

#### Scenario: Client uploads movement using target-frame semantics
- **WHEN** the client samples a movement input for a predicted tick and computes target frame `124`
- **THEN** the uploaded operation declares `OperationID = 124` for that movement snapshot

### Requirement: Server consumes movement input only on its declared authoritative frame
The server MUST only consume a buffered movement input when that input's `SyncFrameId` exactly matches the current authoritative `frameid`. Buffered inputs for larger future frames MUST remain buffered until their matching authoritative frame arrives.

#### Scenario: Future input is retained until its matching server frame
- **WHEN** the server is processing authoritative frame `120` and the movement buffer contains frames `121` and `122` but not `120`
- **THEN** the server consumes neither `121` nor `122` during frame `120`
- **THEN** the buffered future inputs remain available for later frames

### Requirement: Server falls back to last valid movement when the current frame input is missing
If the authoritative frame being processed has no matching buffered movement input, the server MUST continue battle progression using the player's last valid movement direction as fallback. In this case the server MUST NOT consume a future movement frame early.

#### Scenario: Missing current-frame input uses fallback movement
- **WHEN** the server is processing authoritative frame `120`, the player has a stored last valid movement direction, and the buffer has no movement input with `SyncFrameId = 120`
- **THEN** the server applies the stored last valid movement direction for frame `120`
- **THEN** the server does not consume any buffered future movement frame during that tick
