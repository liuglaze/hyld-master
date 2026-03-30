## ADDED Requirements

### Requirement: Projectile hit resolution uses path-based authoritative queries
The system SHALL resolve query-enabled projectile hits using a path-based spatial query from the projectile's previous position to its next position, rather than relying only on distance checks at the updated position.

#### Scenario: Resolve projectile path for one simulation step
- **WHEN** the server advances a query-enabled projectile during a battle frame
- **THEN** it submits a path query covering the projectile movement segment for that frame
- **THEN** the authoritative hit decision is based on the first valid result returned for that segment

### Requirement: Projectile queries return first blocking or hittable object
The system SHALL allow projectile queries to identify the first valid collider encountered along the movement path, including players, walls, or obstacles that are configured to participate in projectile blocking or hits.

#### Scenario: Wall blocks projectile before target player
- **WHEN** a projectile path intersects a wall collider before intersecting an opposing player proxy
- **THEN** the query result identifies the wall as the first collision
- **THEN** the main server does not treat the player behind the wall as hit for that projectile step

### Requirement: Main server remains owner of hit consequences
The system SHALL apply damage, kill, and event publication only after the main server consumes a valid projectile query result.

#### Scenario: Main server applies hit result after projectile query
- **WHEN** a projectile query reports a hit on a valid target
- **THEN** the main server applies damage and kill logic using existing authoritative combat rules
- **THEN** the main server publishes the resulting hit event through the existing combat pipeline

### Requirement: Projectile query rollout can be limited by projectile type or feature flag
The system SHALL support enabling path-based authoritative projectile queries only for selected projectile classes, skills, or rollout scopes during staged adoption.

#### Scenario: Only selected projectile classes use authoritative path queries
- **WHEN** a battle contains both query-enabled and legacy projectile types
- **THEN** only the configured projectile classes use the Unity query service for hit resolution
- **THEN** legacy projectile classes continue to use the existing server-side fallback path until migrated
