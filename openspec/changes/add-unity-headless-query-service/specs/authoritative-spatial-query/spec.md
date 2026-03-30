## ADDED Requirements

### Requirement: System provides authoritative line-of-sight queries
The system SHALL provide an authoritative line-of-sight query that determines whether a configured blocking collider exists between two points in a battle physics world.

#### Scenario: Determine whether target is blocked by obstacle
- **WHEN** the main server requests a line-of-sight query between an origin point and a target point
- **THEN** the query result indicates whether a blocking collider exists on the configured blocking mask
- **THEN** the response identifies the first blocking object when one exists

### Requirement: System provides authoritative overlap queries for area effects
The system SHALL provide authoritative overlap queries that return the set of colliders within a configured volume for area-of-effect or region-based combat logic.

#### Scenario: Query area effect targets in a radius
- **WHEN** the main server requests an overlap query for a battle frame using a center point and radius
- **THEN** the query result returns all colliders in scope that match the configured mask for that query
- **THEN** the main server can evaluate gameplay rules against that returned set

### Requirement: Spatial queries honor participation masks and active state
The system SHALL evaluate line-of-sight and overlap queries using configured participation masks and the active state of mirrored battle objects.

#### Scenario: Ignore inactive or excluded colliders in overlap query
- **WHEN** a collider is inactive for battle participation or excluded by the query mask
- **THEN** it is not returned as a hit or blocking result for that spatial query

### Requirement: Spatial queries are reusable by future combat features
The system SHALL expose line-of-sight and overlap query semantics independently from any single skill implementation so they can be reused by future projectiles, explosions, or environment-driven combat rules.

#### Scenario: Reuse overlap query for a future area skill
- **WHEN** a new area skill needs authoritative target acquisition
- **THEN** it can consume the existing overlap query capability without redefining the underlying query semantics
