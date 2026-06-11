## ADDED Requirements

### Requirement: Repository defines a single authoritative proto source
The repository MUST define exactly one authoritative `SocketProto.proto` source file for shared protocol evolution, and client/server runtime protocol code generation MUST originate from that source.

#### Scenario: Runtime protocol source is unambiguous
- **WHEN** a maintainer needs to add or modify a protocol field
- **THEN** the repository identifies one authoritative `SocketProto.proto` file as the only supported source of truth
- **THEN** the maintainer does not need to choose among multiple competing proto sources for runtime changes

### Requirement: Runtime and historical protocol artifacts are explicitly classified
The repository MUST classify protocol-related files by role, distinguishing runtime artifacts from tool outputs, historical snapshots, and deprecated duplicates.

#### Scenario: Historical proto files are not mistaken for runtime sources
- **WHEN** a maintainer inspects protocol files under client, server, or tooling directories
- **THEN** each protocol file location is documented or marked with its role
- **THEN** historical or tooling-only files are not presented as equal runtime authorities

### Requirement: Protocol generation flow is documented for client and server outputs
The repository MUST define how the authoritative proto source generates the runtime `SocketProto.cs` outputs consumed by the client and the server.

#### Scenario: Maintainer regenerates runtime protocol outputs
- **WHEN** the authoritative proto source changes
- **THEN** the repository provides a documented generation flow for updating `Client/Assets/Scripts/Server/SocketProto.cs` and `Server/Server/SocketProto.cs`
- **THEN** the expected runtime outputs can be regenerated without reverse-engineering ad hoc tooling steps

### Requirement: Protocol source governance excludes battle message redesign
This governance change MUST constrain itself to source-of-truth and generation ownership, and MUST NOT redefine existing battle message semantics as part of this capability.

#### Scenario: Governance work does not implicitly change wire semantics
- **WHEN** maintainers apply the protocol source governance rules
- **THEN** existing battle message meanings, field layouts, and wire compatibility remain unchanged by this capability alone
- **THEN** message redesign work is handled by a separate change with its own requirements
