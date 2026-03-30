## ADDED Requirements

### Requirement: MatchingController SHALL hand off a structured match result to battle creation
The server SHALL convert a successful match room into an explicit match result structure before battle creation. The handoff structure SHALL preserve each matched player's uid, room identity, team identity, and the selected fight pattern.

#### Scenario: Match room becomes a battle creation request
- **WHEN** a BattleRoom reaches the conditions required to start fighting
- **THEN** MatchingController SHALL build a structured match result from the room state and pass that result to the battle creation path instead of relying on untyped tuple values alone

### Requirement: Battle creation SHALL be separated from room aggregation responsibilities
The server SHALL keep room aggregation and battle instantiation as separate lifecycle responsibilities. MatchingController SHALL decide who is matched, and BattleManage or a dedicated battle creation entry point SHALL decide how the battle context and BattleController are created.

#### Scenario: Match success triggers battle creation through a dedicated entry point
- **WHEN** matching succeeds for a room
- **THEN** MatchingController SHALL invoke a dedicated battle creation entry point, and the battle layer SHALL construct the battle context and controller for that match

### Requirement: Match room exit SHALL clean player-to-room state
The server SHALL remove player-to-room mappings when players leave a match room so that stale room associations do not survive after cancellation, disconnect, or room completion.

#### Scenario: Player leaves matching before battle starts
- **WHEN** a player or team exits a match room before battle creation
- **THEN** MatchingController SHALL remove the corresponding PlayerIDMapRoomDic entries for those players and keep the remaining room state consistent

### Requirement: Empty match rooms SHALL be reclaimed
The server SHALL remove empty match rooms from the per-pattern room collection after all players have left or the room has been consumed for battle creation.

#### Scenario: Room becomes empty after exit
- **WHEN** the last player leaves a match room and the room no longer contains any team members
- **THEN** MatchingController SHALL remove that room from the fight pattern room list so that future joins do not traverse stale empty rooms
