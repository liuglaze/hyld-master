## ADDED Requirements

### Requirement: Battle-phase UDP traffic SHALL use a server-managed unified NetSim entrypoint
The system SHALL route all battle-phase UDP traffic through a server-managed unified NetSim entrypoint so that network delay, drop, and jitter decisions are made from a single battle-scoped source of truth. This requirement applies only during the active battle lifecycle and SHALL NOT change non-battle UDP behavior. The implementation SHALL achieve bidirectional simulation effects without requiring the Unity client to add a separate local NetSim module.

#### Scenario: Battle lifecycle enables unified NetSim
- **WHEN** a battle enters its active simulation phase
- **THEN** the server SHALL enable battle-scoped NetSim for battle UDP processing
- **AND** battle-related UDP packets SHALL be evaluated by the unified NetSim entrypoint before business handling or actual send
- **AND** the Unity client SHALL continue using its existing networking code without requiring a separate local NetSim module for this change

#### Scenario: Non-battle UDP bypasses battle NetSim
- **WHEN** UDP traffic is sent or received outside the active battle lifecycle
- **THEN** the system SHALL bypass the battle-scoped NetSim behavior
- **AND** non-battle UDP behavior SHALL remain unchanged

### Requirement: Unified NetSim SHALL cover both uplink and downlink battle data paths
The system SHALL apply battle-scoped NetSim to both client-to-server battle UDP inputs and server-to-client battle UDP outputs so that RTT observation and battle frame transport share the same simulated network conditions.

#### Scenario: Uplink battle operation is simulated before business processing
- **WHEN** the server receives a battle operation UDP packet during an active battle
- **THEN** the packet SHALL be evaluated by the unified NetSim layer before it is forwarded to battle business logic
- **AND** the resulting delay or drop SHALL affect when or whether battle logic receives that operation

#### Scenario: Uplink and downlink directions are explicitly modeled
- **WHEN** battle-phase UDP packet types are processed during an active battle
- **THEN** the system SHALL treat `BattleReady`, `BattlePushDowmPlayerOpeartions`, `Ping`, and `ClientSendGameOver` as client-to-server traffic
- **AND** the system SHALL treat `BattleStart`, `Pong`, `BattlePushDowmAllFrameOpeartions`, and `BattlePushDowmGameOver` as server-to-client traffic

#### Scenario: Downlink authority frame is simulated before send
- **WHEN** the server sends an authority frame UDP packet during an active battle
- **THEN** the packet SHALL be evaluated by the unified NetSim layer before the underlying socket send occurs
- **AND** a dropped frame SHALL NOT be sent to the client

#### Scenario: Ping and Pong share the same battle-scoped network model
- **WHEN** Ping or Pong packets are processed during an active battle
- **THEN** they SHALL be evaluated by the same battle-scoped NetSim parameter source used for battle data packets
- **AND** RTT samples SHALL therefore reflect the same simulated battle network conditions

### Requirement: Unified NetSim SHALL support packet-type strategy tiers within battle scope
The unified NetSim layer SHALL allow battle UDP packets to share one framework while using packet-type strategy tiers, so that data packets and control packets can be handled with different default risk levels without bypassing the common entrypoint.

#### Scenario: Data packets use full battle NetSim strategy
- **WHEN** a battle data packet of type Ping, Pong, battle operation upload, or authority frame is processed during an active battle
- **THEN** the unified NetSim layer SHALL apply the configured delay, drop, and jitter behavior for battle data traffic

#### Scenario: Control packets use conservative defaults
- **WHEN** a battle control packet of type BattleStart, ClientSendGameOver, or BattlePushDowmGameOver is processed during an active battle
- **THEN** the packet SHALL still enter the unified NetSim framework
- **AND** the default policy SHALL allow delay while using a conservative drop policy compared with battle data packets

#### Scenario: BattleReady uses route-establishment protection within the unified framework
- **WHEN** a BattleReady packet is processed for endpoint-to-battle routing establishment
- **THEN** the packet SHALL still enter the unified NetSim framework
- **AND** the system SHALL NOT subject it to the same aggressive drop behavior as high-frequency battle data packets
- **AND** endpoint route establishment SHALL remain reliable enough to start battle synchronization

### Requirement: Legacy battle-specific NetSim branches SHALL be removed after unification
After the unified battle-phase UDP NetSim entrypoint is introduced, the system SHALL remove legacy packet-specific NetSim branches that would otherwise create inconsistent or duplicate simulation behavior.

#### Scenario: Pong no longer uses a special-case local simulation path
- **WHEN** Pong is sent during an active battle after the unified NetSim change
- **THEN** its delay or drop behavior SHALL come from the unified NetSim entrypoint
- **AND** no separate Pong-only NetSim branch shall remain active

#### Scenario: Authority frame drop is a real drop
- **WHEN** the unified NetSim layer decides to drop an authority frame during an active battle
- **THEN** the system SHALL skip the actual UDP send for that frame
- **AND** the server SHALL NOT emit a misleading drop log while still transmitting the packet

### Requirement: Unified NetSim SHALL emit consistent observability data
The system SHALL emit unified observability data for battle-phase UDP simulation decisions so that developers can verify whether RTT samples, uplink arrival timing, and downlink authority-frame delivery are governed by the same simulation model.

#### Scenario: Delay decision is logged with packet context
- **WHEN** the unified NetSim layer delays a battle UDP packet
- **THEN** the system SHALL log the packet direction, packet type, battle identifier, endpoint, and delay duration

#### Scenario: Drop decision is logged with packet context
- **WHEN** the unified NetSim layer drops a battle UDP packet
- **THEN** the system SHALL log the packet direction, packet type, battle identifier, endpoint, and drop decision
- **AND** the log SHALL represent the actual outcome applied to the packet
