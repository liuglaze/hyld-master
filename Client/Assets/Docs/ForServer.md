# 客户端／DS协作说明（仅新PMNet链）

旧 BattleInfo / SavedMove / MoveAck / BattleReview 网络链已删除。以前的旧摇杆换轴、队伍镜像、同帧重复发包与客户端本地扣蓝约定不再适用；历史依据在迁移计划和历史报告中，不是当前接口。

## 唯一入口与部署

- 大厅TCP长连接保留。匹配只下发 `StartEnterBattle(30)` 中的 `PMDS1:` 入局offer。
- `UIMatchingPanel` 拒绝非PMDS1成功通知；`PMClientSessionHost` 建独立物理场景，不通过旧加载场景握手开战。
- Lobby默认只新DS，`HYLD_PMNET_DS=0` 也不能回旧链。部署需要显式DS exe/workdir/bootstrap目录和正式manifest；本地开发可用 `Server/run_lobby.bat` 的仓库相对默认值，`--check-only`不启动进程。
- UnityDS必须带合法 `-bootstrap`；无bootstrap以非0退出。正式/诊断内容按碰撞digest明确区分，不根据文件存在猜模式。
- Lobby/Client/DS必须同版本。现成HyldDS二进制可能早于源码删除，不可拿旧包验证新链。

## 坐标、身份与时间

- Unity世界坐标：Y-up、米；不再做旧“本地玩家固定+X”的队伍镜像。
- 正式出生x=±15、z=-5/0/5，地面高度由实际碰撞查询；诊断地图另有独立布局。
- 对象身份由SessionEpoch/NetId及流代次约束，Owner来自认证会话。输入帧与AuthorityServer帧分域，不用帧差乘16冒充时间转换。
- Mover Input/Sync/Aux历史负责恢复重模拟；属性复制负责状态收敛，两者不互相替代。SP插值不承担第二套权威。

## 攻击、命中、资源

- F普攻/G已支持直线大招；两端用同一 `PMCombatWeaponPlanner` / `BattleNumericConfig`。Unsupported拒绝且不扣资源，不回退旧子弹逻辑。
- 可靠 `ServerCombatAttackV1` 先授权activation，再按计划发R5 Spawn；单次扣费、重复幂等，不能客户端指定damage/spec。
- AP先假弹，权威镜像真实到达才接管；Rejected实际撤销，假弹已结束后迟到镜像不得复活。
- 客户端报候选；DS按历史帧/回溯、轨迹预算、目标形状/队伍/存活与去重校验后唯一结算。
- HP/死亡/hero/team公共复制；Mana/SuperEnergy OwnerOnly，客户端只读。Mana90、Energy200，不采用陈旧文档里的500。
- 首杀终局沿旧服规则；DS先可靠结果通知与客户端ACK（或5s宽限），再向Lobby提交冻结结果。客户端只把验证保存的结果当可信退场依据。

## 协议与验证

- 大厅proto唯一源：`ProtobufAndNotepad/Protobuf/SocketProto.proto`；Request7/8、Action31..41、MainPack13/15已reserved。
- 局内声明唯一生成集合：`Scripts/PMR3/`，锁文件 `Docs/plans/pmnet-r3-ids.json`；不能手改generated或再造第二注册表。
- `PMNetVerify`测大厅wire；`PMR4/R5/R6NetworkTest`测真实核心+Transport字节链；`PMR4UnityCheck`编真实Unity2019 API；`PMLegacyRetirementTest`防旧类型/协议/资产GUID复活。
- 所有测试须先本次build成功，静态/替身/字节链不等于Unity真实PhysX和双客户端验收。用户选择集中后置实机，不逐阶段要求重打包。
- 特殊技能/抛物线/AoE/弹射/道具、美术/摇杆/回放与R4完整Modifier等仍欠账。具体状态只看 `Docs/plans/net-architecture-migration.md`，接口改动同步 `BothSide.md`。
