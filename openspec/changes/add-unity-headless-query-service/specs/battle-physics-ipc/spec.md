## ADDED Requirements

### Requirement: Main server and query service communicate over local IPC
The system SHALL provide a local IPC channel between the main battle server and the Unity headless query service for battle-scoped synchronization and query requests.

#### Scenario: Establish IPC channel to query service
- **WHEN** the main server starts or attaches to a query-enabled battle
- **THEN** it opens a local IPC connection to the query service
- **THEN** the connection is scoped to the local machine and is not exposed as a remote gameplay API by default

### Requirement: IPC protocol separates state sync from query requests
The system SHALL separate IPC messages into state synchronization messages and query request messages so that battle state is synchronized once per frame and reused by multiple queries in that frame.

#### Scenario: Reuse synchronized state for multiple projectile queries
- **WHEN** the main server has synchronized the authoritative player states for a frame
- **THEN** it MAY issue multiple projectile or spatial queries for that same frame without resending the full player state payload per query

### Requirement: IPC responses preserve frame and battle identity
The system SHALL include battle identity and frame identity in IPC requests and responses so that the main server can validate that a query result belongs to the expected battle frame.

#### Scenario: Validate query response against current battle frame
- **WHEN** the query service returns a query result
- **THEN** the response includes the battle identifier and frame identifier associated with the request
- **THEN** the main server can detect and reject a mismatched or stale response

### Requirement: IPC failures support deterministic degradation
The system SHALL define a degradation behavior for IPC connection failures, timeouts, or query-service unavailability so that battles can continue under a known fallback policy.

#### Scenario: Query service becomes unavailable during battle
- **WHEN** the main server cannot obtain a query result because the IPC channel is unavailable or exceeds the configured timeout
- **THEN** the server applies the configured fallback behavior for that battle
- **THEN** the fallback behavior is recorded for observability and debugging
