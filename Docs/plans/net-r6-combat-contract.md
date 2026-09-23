# R6-A 正式直线攻击与权威战斗闭环

状态唯一源net-architecture-migration.md；用户持续授权先代码、实机集中后置。R6整体仍包含特殊技能/道具/UI/旧链退役/性能，本批只先打通正式直线攻击资源伤害死亡胜负，不宣称完整R6。

## 事实与取舍
BattleNumericConfig唯一数值源，MaxHp沿旧服不是DesignHp，ManaMax90/PerSegment30，SuperEnergyMax200。ResolveAttack普通SpawnBulletCount=PerShot，大招=Total，两端统一这一现存权威口径；不照搬旧客户端Total多画子弹。PMBattleSim.SpreadDirection复用世界X/Z方向，不用旧队伍镜像换轴。原服务端首杀即终局保留；断线仅剩一个有效队伍时判该队赢，无唯一队伍则winner0（不硬编码1）。
IsParabola=true明确UnsupportedAttack且不扣资源，不作为直线模拟；无TryGetSuper或特殊非投射物大招同样拒绝。保留配置dmg0（斯派克）不擅自填值。NormalAttackManaRecover尚无旧实现，本批继续不消费，后续完整技能明确。普通攻击扣NormalAttackManaCost且回蓝计时重置，每ReloadSeconds回30封顶90；死者/断线/已结束不回蓝。大招需满200后清0，普通Mana不变；有效伤害回能damage/2封顶200，防重复结算。发射间隔本批下限100ms（显式安全预算），可再按共享EachShotInterval取max，但不把它当全弹幕调度。

## A1纯核心
新目录PMCombat（namespace PMNet.Combat）仅依赖PMNet/PMMover/PMProjectile/Shared，不引Unity/R3。PMCombatWeaponPlanner生成深clone攻击计划，PMCombatSession权威只读快照/攻击账本/资源/伤害/胜负。共享Contracts由主Agent冻结。
每玩家身份Epoch+NetId，uid/team/hero来自DS名册，hero必须0..19不能走共享Get兜底。最多6玩家、最多2队（队号使用真实名册TeamId，不用formalTeamIndex0/1）。攻击ID=activationId单owner单epoch递增，非0不回绕；幂等重复不得扣两次。记录容量session512，TTL5000ms（>=最大弹寿命+墓碑；planner需拒超此预算或明确提升TTL），水位保留；同ID冲突不能把已Confirmed结果翻Rejected。攻击接受原子：先校验/容量/方向/cooldown/资源/计划成功再扣。任何Rejected不得影响既有批准攻击。记录消失后旧ID不会被当新请求。
计划含N<=64条世界方向与同一Spec：Speed来自ResolvedAttack.BulletSpeed，Lifetime=ceil(ShootDistance/Speed*1000)且边界有界，Radius=.1m（R5胶囊0.4+容差0.3+.1=旧0.8默认侧向阈值，不复用ShootWidth）；每bullet伤害来自ResolvedAttack.BulletDamage；StopOnHit=true。配置异常/非finite failclosed。
TryAuthorizeProjectile(owner,intent,wallNow)只认已批准activation，按计划未消费方向槽匹配（角度容差1度），最多N颗、每ProjectileKey一次，Origin只能ClientPredicted、Epoch/Owner相同。位置约束R5再次检查trusted ownerPosition。必须存可信key→攻击/伤害元数据以接Settlement；不信上行任何damage/spec。重复key回既有授权但不能再次占槽（R5还会在更早处幂等）。该授权与R5真正生成失败之间允许资源消耗不退款（一次攻击已授权成本，不能回滚攻击账本造重复资源）。
ApplySettlement只收DS R5唯一出口，核epoch/key/activation/已授权bullet/attacker和target当前存活/敌队/TargetStreamVersion在host先复核，命中每key-target一次。HP clamp[0,max]，0置Dead，伤害为冻结批准plan，不因之后配置改动改变。首杀锁定MatchEnded/WinnerTeamId/OutcomeId单次，不重复结算/发胜负；Apply后有变化可通过GetPlayer/CapturePlayers读到。资源、战斗状态snapshot不借出内部mutable引用。Tick单调墙钟，非法/倒退不改，补回蓝用有界算术不巨大while。Disconnect留NetId水位/死亡真值，最多2队保证唯一remaining判断；加入尚未全部到齐前不得因只一个已连接队早判终局，由host明确StartMatch在全名册接入后启用攻防。

## A2声明（与核心并行）
PMR3Player新增public复制字段：_combatHeroId(int),_combatTeamId(int),_combatHp(int),_combatMaxHp(int),_combatDead(bool),_combatMatchEnded(bool),_combatWinnerTeamId(int)；OwnerOnly _combatMana(int),_combatSuperEnergy(int)。只读CombatHeroId等同名Pascal getter，PublishCombatState(int heroId,int teamId,int hp,int maxHp,bool dead,int mana,int energy,bool ended,int winner)通过各generated setter。每属性RepNotify统一通知CombatStateUpdated事件(允许同一批多次，不能在回调发RPC)；public IPMCombatNetworkDriver CombatDriver无核心依赖，OnCombatStateReplicated()只入队。
新增可靠RPC与接口：ServerCombatAttackV1(uint activationId,bool isSuper,float aimX,float aimZ) / OnServerAttack同签名；ClientCombatAttackResultV1(uint activationId,bool accepted,int reason) / OnClientAttackResult；ClientCombatMatchResultV1(uint outcomeId,int winnerTeamId) / OnClientMatchResult；ServerCombatResultAckV1(uint outcomeId) / OnServerResultAck。两ServerRpc ForceValidate要求driver!=null/nonzeroID/finite nonzero方向，字面范围<=1.001（最终core归一化），ResultAck拒0。实际Owner在PMNetReceive校验，可靠同域保证Attack声明在相应Spawn前到达；宿主队列处理必须先战斗授权后ProjectilePump。生成集合/锁唯一，旧ID不变，OwnerOnly不得泄露。

## B整合与宿主（消费A真实API顺序）
PMR6CombatDriver纯网络适配为session级，BindPlayer转接声明，DS来自trusted model、AP不写复制字段。客户端TryAttack先共用planner建立本地计划，再PMNet_ServerCombatAttackV1，按计划N方向调用R5.TryFire(同activation，多projectileId)；不等服务器才预测，Rejected必须通过R5真实撤销该activation对应假弹（新增可靠授权取消接口如必要），不能只改UI。F普攻/G大招（先沿现有键位，摇杆后置），初始hero/team从DS复制且核自身offer身份，不能取旧UI自选。
DS policy替换DiagnosticProjectileAuthorityPolicy为R6已批准plan授权适配；host真实owner位置/当前alive来自model+movement，不允许payload任意选spec。R6处理命令队列在R5.Pump之前，Settlement只由R6 Apply且发布纯字段复制；死亡立即freeze对应Mover并history.Alive=false，战斗结束禁止所有新攻击与推进。若server-smoke仍走R3诊断probe结果，不因R6未准备误终局，不自动要求所有smoke脚本会攻击。
可靠ClientMatchResult逐owner下发，客户端幂等显示与ServerResultAck，DS待全在线玩家ACK或5000ms宽限后再Lobby.SubmitResult（沿既有ResultAck/Exited控制闭环）；每1000ms重发相同Outcome，结算内容冻结，不能让DS立即退出吞掉客户端结果。超时是有界退场而非结果已被所有client观察的证明。winner使用名册真实TeamId。Lobby gateway当前只log不承担新客户端结果；本批由DS可靠通知补齐，不伪造旧BattleReview。

## 验收
T6A1 planner所有20英雄 normal与super支持/拒绝表，弹数/扇形/速度/寿命与共享配置一致；抛物线/无配置拒不扣。
T6A2资源/ID/原子授权、重复/乱序/满表/超时、可信spec与N上限。
T6A3真实R5结论→去重HP/能量/死亡/首杀/断线winner；未知/错owner/友军/重复结论无写；StartMatch gate。
T6A4声明OwnerOnly两连接真实传输、初始HP、改动OnRep、RPC归属与稳定ID。
T6B真实字节链R6Attack→R5Spawn→命中→HP+资源复制→结果+ACK，AP/SP不执行权威写。
T6C真实UnityAPI编译与既有回归；T46/T47实机全局继续PENDING_USER，不宣称全部技能/道具/旧链退役完成。


## B驱动冻结补充
R6 driver只定义在PMR3/PMR6CombatDriver.cs（不新增NetworkObject），与已落地PMCombatSession/WeaponPlanner和A2接口交互；主侧已给现有glob消费者加PMCombat/Shared include，Lobby排除R6 driver保留声明。DS与Client per-world driver IDiposable，有BindPlayer/UnbindPlayer/Pump/FlushState；创建先后R6 model→R6授权policy对象（可同driver嵌套类）→R5 driver→R6 driver绑定R5。policy可以由R6 driver静态CreateAuthorityPolicy(model,Func<PMR3Player,PMVector3?>或TryGetPosition delegate,Func<double> now)提供，但不得读UnityTime；宿主显式传单调时钟与真实位置/Freeze信息。
R6消费R5唯一Settlement出口（不要与诊断消费者双订阅）；新增R5.CancelPredictedActivation(PMR3Player owner,uint activationId,double wallNow)或等价owner受限API给ClientAttackResult拒绝撤销，不得只改计数。R6 Pump命令→R5 Pump→R6 FlushState为host固定次序；R6发送全部generated RPC，sender对四条新RPC桥拒绝也必须throw+fault（按ClassId+RpcId复合判据）。
接受攻击返回Accepted decision后向R5.ResolveActivation确认（未知spawn也可先有激活账本），拒绝同理，但duplicate Accepted/oldID不得被后来拒绝反向撤销；core返回duplicate旧decision所以driver按原接受态处理。TryFire多弹失败不能假成功：driver session fault+清该activation假弹，host终止；不可把只发出部分散弹视为完整请求。
ResultReadyForLobby仅DS使用，首次Ended冻结outcome+摘要；逐connectedowner可靠发送ClientMatchResult，同outcome每秒重发，ACK或5s后ready；client幂等保存winner并在Pump生成Ack（不是OnRep里发送），重复winner/outcome冲突failclosed，不执行本地伤害。超时ready不宣称client都看到了，统计可观测。R6 model与R5 history Alive由host最终对齐；完整UI/回放留后续，不混旧BattleReview。


## C宿主接线冻结
DS非ServerSmoke使用PMCombatSession(epoch)→CreateAuthorityPolicy(model,真实运动位置有效性delegate,()=>本Pump墙钟)创建R5→PMR6CombatDriver创建与订阅；不同时挂Diagnostic OnProjectileSettlement（只ServerSmoke保留原R3/R5诊断路径）。OnConnected从认证match.Roster取Uid/TeamId/HeroId，经CombatDriver.AddPlayer，原始TeamId不可替换成formalTeamIndex0/1；全部expected名册均已绑定/ready才StartMatch，默认等候30s超时Fail非伪胜负，smoke不走该门。初始state必须在首次flush前发布，客户端自己身份与offer核对。
DS Pump endpoint→movement→R6.Pump(now)（已改为先Tick资源再请求）→R5.Pump(now,step)→R6.FlushState(now)；同一单调clock值所有core请求一致。死亡对应MovementDriver.Freeze，history.Alive=false且filter复核model.Dead/Connected；终局冻结所有运动，R5只Pump0排协议不再推进；ResultReadyForLobby后一次SubmitResult(winner原始TeamId,frozen summary)，既有LobbyAck→Exited不改。断线用CombatDriver.UnbindPlayer使core.Disconnect生效；smoke仍掉线Fail。
Client构造R5后创建R6Driver并bind所有player；移除常规F键diagnostic TryFire，F普攻/G大招均改TryAttack（position=predicted pos+base aim*0.6，同次所有弹同muzzle）；本地planner gate仅优化，不写HP/Mana/Energy，服务器裁决唯一。200ms诊断固定门替换成planner.FireIntervalMs门且允许服务器不足资源拒绝。OnReplicatedCreate绑定CombatDriver，可信Hero/Team及MaxHp准备后才开火；本人Hero/Team必须与offer一致，不等于0默认即可放行。
Client Pump顺序R6.Pump→Movement（若死/结果则Freeze）→各R5子步+候选→R6.FlushState；死亡/终局停止输入，dead targets候选不收，R5 OwnerOnly资源镜像供显示。最小PMUnityCombatHud只读显示本人HP/MaxHp/Mana/SuperEnergy、F/G提示、拒绝原因和胜负，不复用旧HP写入/旧BattleReview。
结果从ClientCombatMatchResult幂等到达后ACK，预期DS退出不能被OnEndpointFailed误标战斗失败；保存static LastCombatOutcome/winner/matchId并触发新结果事件给后续UI，正常释放session与场景、回到原大厅（原场景一直存在），终局HUD可保留只读结论至新入局/显式Stop清理，禁止对已dispose的Endpoint继续Pump。所有事件归本会话并在退出注销。结算HUD不是完整旧UI迁移，原英雄子弹美术/摇杆仍后置。
