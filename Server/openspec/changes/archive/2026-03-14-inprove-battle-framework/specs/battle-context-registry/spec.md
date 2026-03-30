## ADDED Requirements

### Requirement: BattleManage SHALL expose a battle context registry
The server SHALL maintain a unified battle context registry for active battles. Each battle context SHALL own the battleId, fight pattern, participating players, player uid list, uid-to-battlePlayerId mapping, and the corresponding BattleController instance.

#### Scenario: Create battle context after a successful match
- **WHEN** MatchingController completes a valid match and requests battle creation
- **THEN** BattleManage SHALL allocate a new battleId, create a battle context containing all battle metadata, register the BattleController for that context, and make the context available for later queries

### Requirement: BattleManage SHALL provide stable query interfaces for battle lookup
The server SHALL provide query methods that allow callers to retrieve a battle context by battleId and to resolve a player's uid to the active battle context without directly reading BattleManage internal dictionaries.

#### Scenario: Clear scene logic resolves battle context by uid
- **WHEN** ClearSenceController receives a clear-ready message for a uid that belongs to an active battle
- **THEN** it SHALL obtain the battle context through BattleManage query interfaces and use the context's player list instead of directly composing internal dictionary lookups

#### Scenario: Late packet resolves no active battle
- **WHEN** a controller or UDP boundary component queries BattleManage with a uid or battleId that no longer belongs to an active battle
- **THEN** BattleManage SHALL return a failed lookup result and the caller SHALL be able to reject or drop the packet without throwing an exception

### Requirement: Battle context cleanup SHALL be atomic at battle end
When a battle ends, the server SHALL remove the active battle context and all related uid-to-battle mappings as one lifecycle operation so that no stale battle metadata remains reachable after cleanup.

#### Scenario: Finish battle removes active context and uid mappings
- **WHEN** BattleController finishes a battle and notifies BattleManage
- **THEN** BattleManage SHALL remove the battle context and every participating uid mapping in the same cleanup flow, and later lookups for that battle SHALL fail cleanly
