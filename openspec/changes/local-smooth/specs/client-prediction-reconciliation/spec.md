## ADDED Requirements

### Requirement: Local input prediction SHALL drive immediate simulation
In online battle mode, the client SHALL apply the local player's input to local simulation immediately after input capture and outbound send, without waiting for server authority frame arrival.

#### Scenario: Move input gets immediate local response
- **WHEN** the local player produces a valid move input in online battle
- **THEN** the client updates the local predicted state in the same simulation tick

#### Scenario: Attack input gets immediate local response
- **WHEN** the local player produces a valid attack input in online battle
- **THEN** the client starts predicted attack simulation before receiving the corresponding authority frame

### Requirement: Client SHALL maintain rollback-ready input and state history
The client SHALL maintain a bounded history window keyed by frame identifier and operation identifier, including local input records and rollback-required state snapshot data.

#### Scenario: History entry is written for each predicted tick
- **WHEN** a predicted tick is executed in online battle
- **THEN** the client stores frame-linked input and required rollback snapshot fields in history

#### Scenario: History window is bounded
- **WHEN** history size reaches configured capacity
- **THEN** the oldest entries are evicted in order while preserving continuous latest replay window

### Requirement: Authority reconciliation SHALL compare predicted and server-authoritative state
Upon receiving server-authoritative battle data, the client SHALL perform frame-aligned reconciliation against predicted state using frame and operation correlation.

#### Scenario: Reconciliation runs on authority frame arrival
- **WHEN** an authority frame is received for a frame present in local history
- **THEN** the client compares predicted state and authoritative state for configured reconciliation fields

#### Scenario: Out-of-window authority frame is handled safely
- **WHEN** an authority frame is older than the retained rollback history window
- **THEN** the client skips rollback replay and applies configured safe correction behavior without crashing

### Requirement: Client SHALL rollback and replay when divergence exceeds threshold
If predicted state diverges from authoritative state beyond configured thresholds, the client SHALL rollback to the authoritative anchor frame and replay subsequent local inputs in order.

#### Scenario: Divergence above threshold triggers rollback replay
- **WHEN** reconciliation detects divergence above configured threshold on a rollback-enabled field
- **THEN** the client restores authoritative anchor state and re-simulates subsequent buffered inputs by frame order

#### Scenario: Divergence within threshold avoids full rollback
- **WHEN** reconciliation detects divergence that does not exceed configured threshold
- **THEN** the client uses lightweight correction path and does not execute full rollback replay

### Requirement: Visual correction SHALL be smoothed after logical reconciliation
After logical state reconciliation completes, the client SHALL apply short-window smoothing for visual representation fields to reduce visible snapping.

#### Scenario: Position correction uses smoothing window
- **WHEN** logical reconciliation outputs corrected position for a visible entity
- **THEN** render-facing position transitions within configured smoothing window instead of instant hard snap

### Requirement: Prediction and rollback SHALL be isolated to online mode
Prediction, rollback, and reconciliation behavior MUST be enabled only for online battle paths and MUST NOT alter standalone/single-player battle behavior.

#### Scenario: Online mode enables prediction pipeline
- **WHEN** battle mode is online
- **THEN** prediction history, reconciliation, and rollback logic are active

#### Scenario: Standalone mode bypasses prediction pipeline
- **WHEN** battle mode is standalone or offline
- **THEN** prediction history, reconciliation, and rollback logic remain inactive and existing standalone flow is preserved
