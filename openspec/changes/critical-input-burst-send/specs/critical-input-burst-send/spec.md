## ADDED Requirements

### Requirement: Client burst-sends critical input frames within the same local tick
The client MUST detect when a local battle tick contains a critical input frame and MUST send the same battle operation packet multiple times within that same local tick. A critical input frame includes a stop-transition frame that changes movement from non-zero to zero, or a frame that introduces at least one newly generated attack input.

#### Scenario: Stop transition triggers same-tick burst-send
- **WHEN** a local battle tick changes movement input from non-zero to zero and prepares an upload operation for that tick
- **THEN** the client sends the same operation packet 2~3 times within that local tick
- **THEN** each repeated packet carries the same `OperationID`, movement snapshot, and `ClientAckedFrame`

#### Scenario: New attack triggers same-tick burst-send
- **WHEN** a local battle tick adds a new attack with a fresh `AttackId` to the outgoing operation
- **THEN** the client sends the same operation packet 2~3 times within that local tick
- **THEN** each repeated packet carries the same `OperationID`, attack payload, and `AttackId`

### Requirement: Server treats repeated critical input packets as idempotent
The server MUST treat same-tick repeated critical input packets as idempotent duplicates. Repeated movement packets for the same declared `SyncFrameId` MUST resolve to the final buffered value for that frame without consuming the frame early. Repeated attack packets carrying an already processed `AttackId` MUST NOT create a second authoritative attack event.

#### Scenario: Repeated stop packet updates the same buffered movement frame
- **WHEN** the server receives multiple movement packets for the same player and the same declared `SyncFrameId` within one client tick
- **THEN** the server keeps only one buffered movement entry for that `SyncFrameId`
- **THEN** the buffered entry reflects the latest received value without advancing authoritative consumption early

#### Scenario: Repeated attack packet is deduplicated by AttackId
- **WHEN** the server receives repeated attack packets that carry the same `AttackId`
- **THEN** the server processes that attack at most once
- **THEN** later duplicates of the same `AttackId` do not create an extra authoritative attack event

### Requirement: Burst-send behavior is observable in verification logs
The client and server MUST emit verification logs that allow operators to confirm when critical-input burst-send was triggered and how repeated packets were handled.

#### Scenario: Client logs burst-send trigger and repeat count
- **WHEN** a local battle tick triggers critical-input burst-send
- **THEN** the client emits a verification log that includes the trigger reason and repeat count

#### Scenario: Server logs idempotent handling of repeated critical packets
- **WHEN** the server handles repeated critical-input packets for the same frame or attack
- **THEN** the server emits verification logs showing movement overwrite or attack dedup decisions
