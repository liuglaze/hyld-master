## ADDED Requirements

### Requirement: Query service hosts a server-only physics world
The system SHALL provide a Unity headless query service that can start without graphics, load a server-only collision world for a battle, and maintain only the colliders and proxy objects required for authoritative spatial queries.

#### Scenario: Start headless query service for a battle
- **WHEN** the server creates a battle that is configured to use authoritative spatial queries
- **THEN** the query service creates or allocates a battle-specific physics world
- **THEN** the physics world loads the server scene or collider set for the selected map
- **THEN** no rendering-only objects are required for the battle to become query-ready

### Requirement: Query service mirrors authoritative player proxies
The system SHALL maintain a proxy collider for each active battle player in the query world, and the proxy state SHALL be driven by authoritative state synchronized from the main server.

#### Scenario: Synchronize player proxy state into query world
- **WHEN** the main server sends the current authoritative player states for a frame
- **THEN** the query service updates the corresponding player proxy positions and active states for that battle
- **THEN** dead or removed players no longer participate in hit queries for that battle

### Requirement: Query service scope remains spatial-only
The system MUST limit the query service to spatial responsibilities and MUST NOT move ownership of damage, buff, kill, or game-over decisions out of the main server.

#### Scenario: Spatial query returns only spatial result data
- **WHEN** the query service completes a projectile, line-of-sight, or overlap query
- **THEN** the response contains spatial hit or block information only
- **THEN** the main server remains responsible for interpreting that result into gameplay consequences
