## MODIFIED Requirements

### Requirement: Server consumes movement inputs only on the matching authoritative frame
The server MUST consume a buffered movement input for a player only when the input's `SyncFrameId` exactly matches the current authoritative `frameid`. If the current authoritative frame has no matching buffered movement input, the server MUST NOT consume any larger future movement frame during that tick. Repeated uploads for the same `SyncFrameId` MUST remain buffered as a single logical frame and MUST NOT cause early consumption.

#### Scenario: Consume current authoritative frame when present
- **WHEN** the server is processing authoritative frame `32`, and the buffered movement inputs include `32`, `33`, and `34`
- **THEN** the server consumes frame `32` during that tick
- **THEN** the buffered inputs for `33` and `34` remain buffered for later authoritative frames

#### Scenario: Do not skip ahead to a larger buffered frame
- **WHEN** the server is processing authoritative frame `32`, frame `32` is missing, and the buffered movement inputs include `33` and `34`
- **THEN** the server consumes neither `33` nor `34` during frame `32`

#### Scenario: Repeated uploads for one frame stay as one buffered frame
- **WHEN** the server receives multiple movement uploads for the same player and the same `SyncFrameId = 32` before authoritative frame `32` is processed
- **THEN** the movement buffer retains only one logical buffered entry for frame `32`
- **THEN** authoritative frame `32` is consumed at most once when frame `32` arrives
