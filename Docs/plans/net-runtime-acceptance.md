# 网络运行准入测试集（实机操作规范）

本文件是**执行规范**，不是状态源。进度/决策/验收唯一源仍是
`Docs/plans/net-architecture-migration.md`（尤其末尾「运行准入测试集开工（本轮）」段）。
本文件不维护第二套「已通过」状态；实机项没有本轮实测证据时始终 PENDING。

范围：Unity2019.4.8f1 / C#7.3 / net8.0 大厅；工作副本 `D:/UGit/hyld-master`。
**不启动第二个 Unity、不抢工程锁、不代提交、不主动停用户服务、不私自装弱网工具或改网卡。**
无可用计数器/代理故障注入时，实机子项必须标 **BLOCKED**，由代码门禁覆盖，不得用「应该没问题」充当通过。

---

## 1. 首次运行最小路线（照着做）

约定：客户端 A = 编辑器 Play；客户端 B = 同源 StandaloneWindows64 构建（**在同一 Unity 实例内构建**，输出到独立目录，不覆盖 `HyldDS/`）。

0. **维护窗口 + 冻结代码（不覆盖用户正在跑的 Server 输出）**
   - 需要一个独占窗口：TCP `7778`（Lobby）、控制端口 `127.0.0.1:7800`、DS 端口池 `7801..7899` 全部空闲。
     端口被占用时用环境变量改（`HYLD_PMNET_DS_CONTROL_PORT`、`HYLD_PMNET_DS_PORT_FIRST/LAST`），**不要**去杀用户进程。
   - 用户若已有 Lobby 在跑：**不覆盖** `Server/bin/Debug/net8.0/Server.dll`。本次改用独立输出构建并从该目录启动；
     替换/停服由用户在自己的维护窗口决定。DS 一律由 Lobby 拉起，不手动跑 `run_ds.bat`（除非单独诊断）。
   - 记录 `git -C D:/UGit/hyld-master rev-parse HEAD` 与 `git status --porcelain` 条数/摘要（见 §3）。
1. **统一重建 Lobby/DS/Client（三者必须同源同版本，旧 `HyldDS/` 包可能仍含已删除分支，不能用来验证当前源码）**
   - 资源门：Unity 菜单 `Build/Prepare PMNet Battle Content`（缺资源/校验失败即中止，不得跳过）。
   - DS：菜单 `Build/Build HyldDS (Windows Headless)` → 产物 `D:/UGit/hyld-master/HyldDS/HyldDS.exe`（附 `run_ds.bat`）。
   - 客户端 B：Unity 自带 `File > Build Settings` → Standalone Windows64 → 输出到独立目录（如 `D:/UGit/hyld-master/ClientBuild/`）。
   - Lobby：`dotnet build D:/UGit/hyld-master/Server/Server.csproj`（用户 Lobby 在跑时加 `-o <独立输出目录>`）；
     然后 `Server/run_lobby.bat --check-only`（**只检查路径存在**，不启服务、不证明二进制新鲜或协议一致）。
2. **编织 reload/repair（失败即停止，不得带着未编织映像入局）**
   - 菜单 `Tools/PMNet/Weaving/Log Status`：确认「编织状态=Ok」且「待重载=false」。
   - 未织/待重载时按序：`Refresh Generated Metadata (decl-check/decl-gen)` → 等 Unity 重编译完成 →
     仍要求则 `Repair: Weave Assembly-CSharp Now` → `Request Script Reload (after repair)` → **等脚本重载真正结束**
     （磁盘改好 ≠ 内存新鲜；重载前 Play/Build 硬门会拦）。
   - 收尾必做 `Verify: Check Assembly-CSharp (+require RPCs)`；**任一步失败 → 停止本轮，记录日志，不入局。**
3. **启动 Lobby**：默认输出且已重建时用 `Server/run_lobby.bat`。注意该脚本**固定运行 `Server/bin/Debug/net8.0/Server.dll`**，不会使用上面的独立 `-o` 输出。采用独立输出时，在用户维护窗口的新 PowerShell 终端按下方命令启动；不要编译了新目录却跑旧脚本。确认实际路径与本轮构建记录一致。
4. **启动两个客户端（不同账号）**
   - A：Unity 打开 `Assets/Scenes/HYLDLogon.unity` → Play → 登录账号 A（不存在的账号会自动创建）。
   - B：用 `& '<客户端B绝对exe路径>' -logFile '<本轮独立目录>/ClientB.log'` 启动 StandaloneWindows64 产物 → 登录账号 B（与 A 不同名）。显式分日志，避免 B 与 DS 共用默认 Player.log。
   - 两端各自进入主界面后，**各自点击「开始匹配」**。宝石争霸为 1v1：服务端把两次 `AddMatchingPlayer` 合进同一房间、
     生成两个**不同真实 Team**（房间队伍容量 = RoomMaxNumber/maxTeamnum = 1）。
     **不要**用「创建房间/邀请好友」把两人塞进同一请求——该路径会给两人建**同一支队伍**，1v1 房间永远填不满、不会开局。
5. **入局**：匹配满员 → Lobby 拉起 DS → 客户端收到 `PMDS1:` 通知 → `PMClientSessionHost.Enter`。
   A/B 都应进入局内场景；`ExitMathcing` 与匹配面板按预期收起。
6. **局内操作**：WASD/方向键移动，`Space` 跳；`F` 普攻，`G` 大招（已支持的直线大招；抛物线/无配置大招应被拒绝且不扣资源）。
   HUD（只读）显示本人 HP/MaxHp/Mana/SuperEnergy、F/G 提示、最近拒绝原因、胜负。
7. **首杀终局**：一方 HP 归 0（首杀即终局）→ 两端 HUD 出胜负 → DS 可靠通知 → 客户端 ACK 或 5s 宽限 →
   Lobby 幂等受理 Result 并回 Ack → DS 发送 Exited 后退出 → **确认 OS 进程退出后**才释放端口/名册占用。
8. **退出再开**：先归档 DS 默认 Player.log（下局可能覆盖），客户端回大厅，重复第 4–7 步应能再次入局（同一 Lobby、同一 DS 产物）。

独立 Lobby 输出的启动示例（**仅用户执行**；TCP7778 已被占用就停止，不启动第二个 Lobby）：
```powershell
$repo = 'D:/UGit/hyld-master'
$out = "$repo/Tools/NetAcceptance/bin/manual-lobby"  # 本轮未被进程占用的目录
$env:HYLD_PMNET_DS_EXE = "$repo/HyldDS/HyldDS.exe"
$env:HYLD_PMNET_DS_WORKDIR = "$repo/HyldDS"
$env:HYLD_PMNET_DS_BOOTSTRAP_DIR = "$env:TEMP/HyldDSBootstrap"
$env:HYLD_PMNET_CONTENT_MANIFEST = "$repo/Client/Assets/Resources/PMNet/BattleContentV1.json"
dotnet build "$repo/Server/Server.csproj" -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw 'Lobby build failed: stop' }
Push-Location "$repo/Server"
try { dotnet "$out/Server.dll" } finally { Pop-Location }
```
上面的环境变量在该新终端内设置；已有部署自定义路径时填真实路径，不能以示例覆盖部署约定。本轮 runner 不执行这段启动命令。

---

## 2. Runner（冻结接口，可直接使用）

```
python Tools/run_net_acceptance.py --suite smoke       # 14项，快速代码门禁
python Tools/run_net_acceptance.py --suite full        # 34项，含smoke
python Tools/run_net_acceptance.py --list              # 只列清单，不跑
python Tools/run_net_acceptance.py --timeout-seconds 900  # 每次build/run各自计时
```
- 串行运行；单次唯一输出 `Tools/NetAcceptance/bin/<run>/summary.json` 与 `summary.md`。
- **先 build 0 才 run**；build 失败/超时/取消 → 不跑旧 DLL，默认失败即停，剩余项记 `NOT_RUN`。
- 报告中机器判定明确标注 `CODE_ONLY`；**Unity 永不自动判通过**。
- `full` 含 `smoke`，另加编织真实夹具、控制/传输/战斗/资源门与 SDK 沙盒。
- **不得**自动运行需要用户大厅或 Unity 进程的测试（`PMServerSmokeTest` 需真实监听中的 Lobby，属用户侧手工步骤）。
- runner 异常中断留下 `Tools/NetAcceptance/bin/runner.lock` 时不会自动抢锁；先人工确认原 runner 及其子进程均已结束再清理锁。不要据空文件/无效 PID 自动删除。
- SDK 构建沙盒还会写 `Tools/rpc-build-sandbox-*.log`，runner 收集本轮副本，不声称所有工具内部产物均隔离。

---

## 3. 构建身份与证据记录（同协议 hash ≠ 二进制新鲜）

每次 run 必须记录，缺项即证明力不足：

| 项 | 取值方式 |
|---|---|
| 源码身份 | `git rev-parse HEAD` + `git status --porcelain` 条数与 diff 摘要（dirty 时必记） |
| 构建时间 | 各产物 mtime / 记录时刻（本地时区） |
| 程序集 | `Assembly-CSharp.dll`、`Server.dll`、`HyldDS.exe` 的 SHA256 |
| 内容 manifest | `Client/Assets/Resources/PMNet/BattleContentV1.json` 的 `contentDigest`/`collisionDigest`/`worldVersion` |
| 协议 | `ProtocolHash`（应为 `0xE6130FAA`）、ID 锁 SHA256（`47f0ad21…40fece8`）。注意：hash 相同**不证明**二进制新鲜，必须与上表其它项联合判定 |
| 实际路径 | Lobby 日志打印的 DS exe/workdir/bootstrap dir/manifest；本次客户端 A/B 与 DS 的真实绝对路径 |

---

## 4. U01..U12 用例（稳定编号）

标注 **[首]** = 首次必测；**[扩]** = 扩展回归。每项都先满足 runner `smoke`（或 §4 手工等价步骤）为前置。

**U01 构建与编织 [首]** — 前置：§1.1–§1.2。
操作：`--list` 确认清单 → `--suite smoke` → `--suite full`；随后 Unity `Log Status` + `Verify`。
观察证据：`summary.json/md`（FAIL 项、NOT_RUN、CODE_ONLY 标记）；`Log Status` 输出；Verify 结果。
通过标准：build 0、runner 退出码 0、Verify `--check --require-rpcs` 通过、编织状态 Ok 且待重载 false。
停止：任一 build/weave/Verify 失败；Play/Build 硬门弹窗拦住 → 停止，先修编织链路。

**U02 入局闭环 [首]** — 前置：U01 通过、Lobby 日志可见 7778 监听。
操作：两客户端 §1.4–§1.5。
观察证据：Lobby 日志 `[PMDsMatch] 新链开局已提交 …`；Lobby 拉起 DS 后 OS 进程表出现 `HyldDS.exe`；两端进入局内。
通过标准：两个不同账号被分配到同一房间的两个**不同** Team；DS 就绪后才发 offer；两端都入局且无旧链回退日志。
停止：出现非 `PMDS1` 入局通知、`[PMDsMatch] …整局拒绝`、缺人缩编、DS 启动失败。

**U03 运动与碰撞 [首]** — 前置：U02。
操作：A 连续移动/转向20秒、跳10次，向有实际Collider的墙持续移动3秒再松手静止5秒；B旁观。角色间阻挡以当前实现为准，不预设必须穿过或必须阻挡。
观察证据：录像中的位置/朝向连续，无穿过已配置墙/地板Collider、贴墙起飞、持续瞬移；静止后两端处于同一场景参照位置。
通过标准：上述操作全完成且零卡死/穿已配置墙；静止5秒无持续位置漂移。这只是视觉冒烟，缺坐标日志时精确预测误差子项标BLOCKED；未配置围栏的地图边界不冒称具有防坠落能力。
停止：穿墙、掉出地图、持续抖动/拉扯、卡死不可恢复。

**U04 普通 RPC 与属性复制 [首]** — 前置：U02。
操作：A 受击/回蓝/回能后观察 A、B 两端数值；A 端观察自身资源显示。
观察证据：公共 HP/角色/队伍/胜负两端一致；资源（Mana/SuperEnergy）只在**本人** HUD 出现。
通过标准：公共属性收敛一致；资源仅本人可见；无越权上行。
**观测边界**：HUD **不能**观察 `OwnerOnly` 是否泄漏，也**不能**观察精确 `OnRep` 次数——
该子项由代码门禁（声明/复制/字节链）覆盖；实机对应子项标 **BLOCKED**，等待抓包或计数器手段。

**U05 攻击与资源 [首]** — 前置：U04。
操作：A 用 `F` 普攻 B；资源满后用 `G` 大招；再对不支持形态（抛物线/无配置大招）触发一次。
观察证据：HUD 拒绝原因；A 端 Mana/SuperEnergy 变化；B 端 HP 变化。
通过标准：合法攻击造成 HP 下降并正确扣/回资源（普攻扣 ManaCost、大招满 200 后清 0）；伤害由 DS 裁决，客户端不自行结算。
**不支持的技能被拒绝且不扣资源 = 通过**（不得把未实现内容当本轮 bug）。
停止：伪伤害、重复扣费/重复结算、客户端本地改 HP。

**U06 终局与回收 [首]** — 前置：U05。
操作：把一方打到 HP 0（首杀即终局），等 DS 退出。
观察证据：两端 HUD 胜负一致；Lobby 日志 Result/Ack 与 DS `Exited`；端口/名册占用释放；`HyldDS.exe` 进程消失。
通过标准：结果经可靠通知→ACK/5s 宽限→Lobby 幂等受理→确认进程死后释放；不伪造旧 BattleReview。
停止：DS 提前退出吞掉结果、结果不幂等、端口/uid 未释放、无进程退出即归还。

**U07 再开局 [首]** — 前置：U06。
操作：两端回大厅，重复匹配一次。
观察证据：第二次入局成功；终局 HUD 不残留影响新会话（旧对象延迟 `OnDestroy` 不得关闭新会话）。
通过标准：二次开局与首次一致，无「旧局污染」。
停止：第二次入局失败、旧 HUD/旧对象干扰、端口冲突。

**U08 断线（有界失败） [扩]** — 前置：U02。
操作：局中停掉客户端 B（或让它掉线）。
观察证据：DS/Lobby 日志明确失败路径；无唯一胜方时 winner 0；资源有界回收。
通过标准：失败有界、不吞结果、不泄资源、不生成第二权威。
**明确不写**：「重连恢复已实现」——当前未实现重连恢复；只验证失败与回收边界。

**U09 配置与认证负例 [首，可拆小项]** — 前置：可独立于 U02。
操作：在独立终端把 `HYLD_PMNET_DS_EXE`、`HYLD_PMNET_DS_WORKDIR`、`HYLD_PMNET_CONTENT_MANIFEST` **每次只将一项设为不存在的临时路径**，运行 `Server/run_lobby.bat --check-only`，确认非0后恢复该终端变量再测下一项；不要删除正式文件。前提是默认Debug Server.dll存在，否则仅验证到了更早的DLL缺失检查。
票据/摘要/Epoch畸形输入：先运行full中的控制/会话负例。实机目前无冻结的注入入口，**该子项BLOCKED**，待受控代理/测试客户端就绪后再按具体消息协议注入，不把普通登录失败算认证负例通过。
观察证据：启动脚本对缺项明确非 0/报错；DS 缺 bootstrap 拒绝裸启动；Hash/Epoch 不符断连。
通过标准：缺配置/manifest 非法只报失败，不回退旧链、不生成第二权威；异常输入 fail-closed。
停止：静默回退、裸 `-port` 启动成功、错票据被接受。

**U10 弱网 [扩]** — 前置：U02。
建议参数（**需要受控工具，由用户自备**）：100–200ms RTT、1–3% 丢包、少量乱序/重复。
**不得**由 AI 私自安装工具或改网卡；**没有实际丢包/延迟观测数据就不能判 PASS**，只能记 BLOCKED/未测。
通过标准（有工具时）：丢包/乱序后最终收敛；无静默丢可靠消息；输入不饥饿。
关于预测：当前 `predictionMs` 首批固定 100，**不得宣传为完备预测**。

**U11 长局 [扩]** — 前置：U02。
操作：先对空攻击/移动10分钟避免首杀提前终局，再连续完成10局并逐局归档DS日志；记录每分钟私有内存与帧时间，以及每局前后DS进程/端口数量。
观察证据：内存/帧时间趋势、日志异常、对象/连接数增长、端口与名册稳定。
通过标准：10分钟零崩溃/卡死、10次结果一致且10个DS均退出；进程/端口回到局前基线。内存/帧时间缺Profiler或统一采样时性能项标BLOCKED，短测曲线稳定不能证明不存在所有泄漏。
停止：持续增长、卡死、崩溃、数据发散。

**U12 Mono / IL2CPP 双后端 [扩]** — 前置：U01。
操作：分别用 Mono 与 IL2CPP 构建 DS 与客户端各跑一遍 §1.4–§1.7。
通过标准：两后端行为一致；Player 脚本 DLL 阶段 weave/check 目标正确，缺目标必须失败而非回旧包。
**IL2CPP 不得用 Mono 结果替身通过**（T-W6/T-P6 仍 PENDING_USER）。

---

## 5. 仍需 review 的四区（T-AUD1..4）与首要风险

四区专项审查**尚未启动**（只有计划）；新增测试数量**不能**代替这些审查。四区证据文件分别为
`_audit_control.md`、`_audit_netcore.md`、`_audit_prediction.md`、`_audit_combat.md`（不存在即为未做）。

| 编号 | 范围 | 仍需验证的要点 |
|---|---|---|
| T-AUD1 | Lobby/DS 入局、认证、断线/结果与进程资源 | 身份不可绕过、票据消费防重放账本、失败有界不吞结果/不泄资源 |
| T-AUD2 | 传输/RPC/复制与 World 生命周期 | 不越权、不静默确认丢状态、队列有界、断连清理成对 |
| T-AUD3 | Mover 预测回滚与宿主时间顺序 | 输入预算不可绕过、重模拟与终态一致、历史耗尽/重同步边界 |
| T-AUD4 | 攻击授权/候选验证/结算/死亡结果 | 不伪伤害/重复扣费/复活、合法攻击不永久阻断 |

**首要风险排序**：① Unity 实际编译/加载/Player 编织与**混版本**失败；② 全链会话失败回收与结果 ACK；
③ T-AUD1..4 范围的认证/生命周期/预测/结算边界；④ 弱网/长局/IL2CPP 无实测证据。

---

## 6. 三档准入口径

- **可联调**：U01、U02 通过；U03–U07 中至少行走/攻击/终局/再开各一次有日志与截图；无 P0。
- **可扩大测试**：U01–U09 全通过（U08 为有界失败），四区 T-AUD1..4 至少完成并登记证据；
  弱网/长局/IL2CPP 明确标注未测而非跳过。
- **发布未达**：U10–U12 无受控实测、T-AUD1..4 未完成、T-W6/T-P6 与真实双客户端/长局性能仍 PENDING_USER。
  当前定位就是**发布未达**，本文件不作为发布判定。

---

## 7. 结果记录模板 + P0 停止规则

```
run-id / 日期 / 执行人：
源码身份：HEAD=<sha> dirty=<条数> diff摘要=<一句话>
构建身份：Assembly-CSharp.dll=<sha256> Server.dll=<sha256> HyldDS.exe=<sha256>
          构建时间=<...> manifest contentDigest=<...> collisionDigest=<...> worldVersion=<...>
          ProtocolHash=<0xE6130FAA> ID锁SHA256=<47f0ad21…40fece8>
实际路径：DS exe=<...> workdir=<...> bootstrap dir=<...> manifest=<...> 客户端A/B=<...>
Runner：suite=smoke|full run=<run> 结论=<PASS/FAIL/NOT_RUN> summary=Tools/NetAcceptance/bin/<run>/summary.json
步骤实际值：U__ <操作> → <观察> → <结论>（逐项一行）
日志四端：ClientA=<路径> ClientB=<路径> DS=<路径> Lobby=<路径>
复现：<最小重现步骤 / 触碰的配置>
STOP原因（如适用）：<...>
```
- 日志四端：客户端 A/B 的 Unity/Player 日志、DS 的 Player.log（Lobby 拉起时未传 `-logFile`，取
  `%USERPROFILE%\AppData\LocalLow\XuanShuiLiuLi\Invosion\Player.log`；手动用 `run_ds.bat` 时在 `HyldDS/logs/`）、
  Lobby 的 `Server/log/<session>/server.log`。
- **不记录票据、HMAC 密钥、完整 offer Str**；只记长度/标识。
- **P0 停止规则**：出现任一情况立即停止本轮并保留四端日志，不继续后续用例，不清理现场掩盖证据——
  ① 身份/票据被绕过或错票据入局；② 结果为伪造/重复结算/复活；③ 端口或名册资源泄漏、DS 未退出即归还；
  ④ 崩溃/卡死不可恢复、端口冲突导致他局失败；⑤ 用旧包/未编织映像得到「通过」。

---

## 8. 与主计划的关系

- 本文件不新增状态；T-GATE1..4、T-AUD1..6、T-W6、T-P6、T46/T47 的原始状态只在主计划登记。
- 实机无证据的子项始终 PENDING/BLOCKED；不使用「HUD 看起来对」替代 OwnerOnly/OnRep/丢包等不可观测项的证明。
- 旧功能欠账（特殊技能/抛物线/AoE/道具/完整 UI/回放/完整 Modifier/长局性能）不因本文件通过而消失。
