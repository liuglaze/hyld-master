## ADDED Requirements

### Requirement: Server-side retroactive bullet simulation (catch-up)
When the server receives a delayed attack (clientFrameId < current server frameid), it SHALL perform a full retroactive simulation: spawn the bullet at the attacker's historical position from `positionHistory[clientFrameId]`, then simulate the bullet forward frame-by-frame from `clientFrameId` to the current server frame, using `positionHistory[f]` for each intermediate frame `f` to perform collision detection against all players' positions at that historical frame. If the bullet hits a target during catch-up, the server SHALL generate a HitEvent immediately. If the bullet survives catch-up without hitting anything, it SHALL be added to `activeBullets` and continue normal per-frame simulation from the current frame onward.

#### Scenario: Delayed attack with 4-frame lag, no hit during catch-up
- **WHEN** server is at frame 20 and receives an attack with `clientFrameId=16` that exists in `positionHistory`
- **THEN** server SHALL spawn bullet at attacker's frame-16 position, simulate frames 16→17→18→19→20 using historical player positions for each frame, and if no collision occurs, add the bullet to `activeBullets` at its frame-20 position

#### Scenario: Delayed attack hits target during catch-up at intermediate frame
- **WHEN** server simulates a catch-up bullet from frame 16 to 20, and at frame 18 the bullet position is within hit radius of a target's frame-18 historical position
- **THEN** server SHALL generate a HitEvent with `hitFrameId=current server frame`, apply HP damage, stop the bullet (remove from simulation), and NOT add it to `activeBullets`

#### Scenario: Attack arrives with clientFrameId equal to current frame (no delay)
- **WHEN** `clientFrameId == current server frameid`
- **THEN** server SHALL spawn bullet at current position with no catch-up, directly add to `activeBullets` (equivalent to V1 behavior)

### Requirement: Server writes spawn position into AttackOperation for downstream broadcast
The server SHALL populate `spawn_pos_x`, `spawn_pos_y`, `spawn_pos_z` fields on each `AttackOperation` in the downstream frame with the attacker's position at the time of attack (the historical position used for bullet spawn).

#### Scenario: Downstream frame contains attack with spawn position
- **WHEN** server broadcasts a frame containing an AttackOperation that was ACCEPT-ed
- **THEN** the AttackOperation in the downstream `AllPlayerOperation` SHALL have `spawn_pos_x/y/z` set to the attacker's position at `clientFrameId`

### Requirement: Proto extension for spawn position
`AttackOperation` message in `SocketProto.proto` SHALL include three new float fields: `spawn_pos_x`, `spawn_pos_y`, `spawn_pos_z`.

#### Scenario: Proto backward compatibility
- **WHEN** an older client or server processes an AttackOperation without the new fields
- **THEN** the fields SHALL default to 0 (protobuf default), causing no behavioral change (client falls back to current position)

### Requirement: Client visual bullet spawn at historical position (Path B)
The client SHALL use the server-provided `spawn_pos_x/y/z` from the AttackOperation (when non-zero) as the spawn position for Path B visual bullets, instead of the player's current interpolated position.

#### Scenario: Remote player attack with valid spawn position
- **WHEN** client receives an authority frame containing another player's AttackOperation with non-zero `spawn_pos_x/y/z`
- **THEN** client SHALL spawn the visual bullet at (`spawn_pos_x`, `spawn_pos_y`, `spawn_pos_z`)

#### Scenario: Remote player attack with zero spawn position (fallback)
- **WHEN** client receives an AttackOperation with `spawn_pos_x/y/z` all equal to 0
- **THEN** client SHALL spawn the visual bullet at the player's current position (existing behavior)

### Requirement: Client visual bullet mid-air advance (Path B)
The client SHALL calculate the elapsed flight time for Path B visual bullets and advance their initial position along the flight direction by the corresponding distance.

#### Scenario: Bullet position advanced on spawn
- **WHEN** client spawns a Path B visual bullet with `elapsedFrames = authorityFrameId - clientFrameId`
- **THEN** the bullet's initial position SHALL be advanced by `elapsedFrames * bulletSpeed * frameTime` along the bullet's flight direction

#### Scenario: Elapsed frames is zero or negative
- **WHEN** `elapsedFrames <= 0`
- **THEN** the bullet SHALL spawn at the spawn position without any advance (no backward movement)

### Requirement: Local prediction bullets unaffected (Path A)
Local player's predicted visual bullets (Path A) SHALL NOT be modified by this change. They SHALL continue to spawn at the local player's current predicted position with zero delay.

#### Scenario: Local player attacks
- **WHEN** local player triggers an attack and the prediction system generates a Path A visual bullet
- **THEN** the bullet SHALL spawn at the local player's current predicted position, ignoring spawn_pos fields
