#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""hyld-master 运行准入 runner（CODE_ONLY：不启动 Unity、不打开大厅、不做双客户端实机）。

背景与边界（只讲事实，不冒充验收）
----------------------------------
`Docs/plans/net-architecture-migration.md` 末尾「运行准入测试集开工」冻结了本 runner 的交付：
统一、可重复、失败即停、构建 0 才运行、绝不在构建失败后跑旧 DLL、超时/取消只清理自己启动的进程树、
机器报告明确 `code_only=true` / Unity `NOT_RUN`。

本脚本**只做一件事**：按固定清单串行「构建 → 本次新产物 → 运行」，把每一步的命令、退出码、
耗时、build/run 状态与日志路径写进 `Tools/NetAcceptance/bin/<run>/summary.json|.md`。

它刻意不做的事
--------------
* 不启动/停止 Unity，不打开编辑器，不打包 Player，不启动 Lobby/DS，不请求用户真实大厅；
  Unity 相关项永远只是 `NOT_RUN`，不会被自动判为通过。
* 不解析几行 stdout 猜「全局功能通过」：判定只看进程退出码 + 本次构建产物是否真的存在。
* 不通过 shell 字符串拼接命令（`shell=False`，参数一律 argv 列表），因此 Windows 路径带空格也安全。
  （内部回环 TCP 是**允许**的：PMR3RuntimeTest/PMNetSessionTest/PMDsControlTest 等自己建 loopback
  监听，属被测工具自身的受控闭环，不是外部依赖。）
* 不递归委托、不写 git/SVN、不修改业务源码。运行产物只落在 `Tools/NetAcceptance/bin/<run>/`
  （该目录被仓库 `.gitignore` 的 `/Tools/**/bin/` 覆盖），仓库级独占锁放在同一 `bin/` 下。

用法（冻结 CLI）
----------------
    python Tools/run_net_acceptance.py --suite smoke|full      # 默认 smoke
    python Tools/run_net_acceptance.py --list                  # 只列清单，无副作用、无需 dotnet
    python Tools/run_net_acceptance.py --timeout-seconds 900    # 单阶段（每次 build / 每次 run）期限

退出码
------
    0 全部通过 / 1 存在失败 / 2 预检失败（缺 dotnet、缺工程、空清单）/ 3 仓库级锁冲突
    4 超时 / 5 取消 / 6 参数不合法
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import socket
import subprocess
import sys
import time
import uuid
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

RUNNER_VERSION = "1.0"

# 从**脚本自身**定位仓库，不依赖当前工作目录。
REPO_ROOT = Path(__file__).resolve().parents[1]
OUTPUT_ROOT = REPO_ROOT / "Tools" / "NetAcceptance"
BIN_ROOT = OUTPUT_ROOT / "bin"
LOCK_PATH = BIN_ROOT / "runner.lock"

DEFAULT_TIMEOUT_SECONDS = 900
FAILURE_TAIL_LINES = 30

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_PREFLIGHT = 2
EXIT_LOCKED = 3
EXIT_TIMEOUT = 4
EXIT_CANCELLED = 5
EXIT_USAGE = 6

TAG_BUILD = "build"          # 编译门（可能是 build-only，也可能是可运行门禁）
TAG_ALGORITHM = "algorithm"  # 纯算法/纯数据结构断言
TAG_WIRE = "wire"            # 真实 Transport 字节链
TAG_WEAVING = "weaving"      # 生成器 + IL 编织（含真实编译夹具）
TAG_VOCABULARY = (TAG_BUILD, TAG_ALGORITHM, TAG_WIRE, TAG_WEAVING)

UNITY_NOTE = (
    "本 runner 不启动 Unity、不开编辑器、不打包 Player、不做双客户端实机；"
    "Unity 永不自动判通过，机器报告只代表 CODE_ONLY 范围。"
)


# =====================================================================================
#  清单（冻结：只列核实存在、且不需要用户真实大厅/Unity 进程的工具）
# =====================================================================================

@dataclass(frozen=True)
class Entry:
    """一个待执行项：test = 构建后运行；build = 只构建（类库编译门）；script = 直接跑脚本。"""

    name: str
    kind: str
    tags: tuple
    description: str
    project: str = ""            # 仓库相对 csproj（kind = test|build）
    assembly: str = ""           # 产物 DLL 名（默认 = name）
    run_args: tuple = ()
    script: tuple = ()           # kind = script：[仓库相对脚本路径, *参数]
    collect_sandbox_logs: bool = False

    def assembly_name(self) -> str:
        return self.assembly or self.name


def _test(name: str, tags: tuple, description: str, run_args: tuple = ()) -> Entry:
    return Entry(name=name, kind="test", tags=tags, description=description,
                 project="Tools/%s/%s.csproj" % (name, name), run_args=run_args)


def _build(name: str, tags: tuple, description: str, project: str = "", assembly: str = "") -> Entry:
    return Entry(name=name, kind="build", tags=tags, description=description,
                 project=project or "Tools/%s/%s.csproj" % (name, name), assembly=assembly)


def _script(name: str, tags: tuple, description: str, script: tuple,
            collect_sandbox_logs: bool = False) -> Entry:
    return Entry(name=name, kind="script", tags=tags, description=description,
                 script=script, collect_sandbox_logs=collect_sandbox_logs)


def smoke_plan() -> list:
    """smoke：12 个可运行门禁 + 2 个 build-only 编译门（顺序即执行顺序）。"""
    return [
        _test("PMDeclCheck", (TAG_BUILD,),
              "声明/生成器门禁：真实生成 + 非法声明 fail-closed + C# 7.3 沙盒编译"),
        _test("PMReplicationTest", (TAG_ALGORITHM,),
              "复制/轮询/条件/可见性与公平预算算法（含 14 项缺陷注入）"),
        _test("PMNetE2E", (TAG_WIRE,),
              "端到端：声明→生成→零反射注册表→复制收敛→RPC 双向真实字节链"),
        _test("PMR3RuntimeTest", (TAG_WIRE,),
              "R3 运行时接线 + 真实 Transport 字节链 + 真实 loopback 控制通道"),
        _test("PMR3IntegrationTest", (TAG_WIRE,),
              "R3 集成：会话/控制代理/半包/MAC/结果重发的真实链路"),
        _test("PMR4NetworkTest", (TAG_WIRE, TAG_ALGORITHM),
              "R4 运动字节链：codec 边界、预测与权威快照、NaN/越界拒绝"),
        _test("PMR5NetworkTest", (TAG_WIRE,),
              "R5 投射物字节链：Spawn/Verify/Hit/Decision 与幂等裁决"),
        _test("PMR6NetworkTest", (TAG_WIRE,),
              "R6 战斗字节链：攻击授权→命中→资源/HP/死亡/结果 ACK"),
        _test("PMNetSessionTest", (TAG_WIRE,),
              "会话/传输：分片、ACK、畸形包、超限拒绝与断连语义"),
        _test("PMNetWorldTest", (TAG_ALGORITHM,),
              "World 生命周期：NetId 单 epoch、预留令牌、容量与初始状态原子发布"),
        _test("PMLegacyRetirementTest", (TAG_BUILD,),
              "旧链退役静态禁回归门禁（Client/Assets + Server 扫描 + 负例自测）"),
        _test("PMNetWeavingEditorTest", (TAG_WEAVING,),
              "编织便利层：真实子进程执行器/策略/指纹，不启动 Unity"),
        _build("PMClientCheck", (TAG_BUILD,),
               "客户端全玩法层替身编译门（netstandard2.0 + C# 7.3，不运行）"),
        _build("PMR4UnityCheck", (TAG_BUILD,),
               "真实 Unity 2019 DLL 编译门（两个宿主 + HUD；不启动 Unity、不运行）"),
    ]


def full_plan() -> list:
    """full = smoke + 编织/控制/传输/声明/资源/SDK 沙盒；最后跑 SDK 构建沙盒脚本。"""
    plan = list(smoke_plan())
    plan += [
        _test("PMPropertyWeaverTest", (TAG_WEAVING,),
              "自动属性复制：真实生成/编译/编织/守卫与真实复制链，含负例"),
        _test("PMNetWeaverTest", (TAG_WEAVING,),
              "RPC 编织：真实夹具编译 + 编织 + 克隆执行，含漏织/损坏负例"),
        _test("PMDsControlTest", (TAG_ALGORITHM,),
              "控制协议/票据/进程协调器：正常路径 + 故障注入（只操作自己的子进程）"),
        _test("PMDsLobbyTest", (TAG_ALGORITHM,),
              "Lobby/DS 编排：可信名册、端口占用、结果幂等与回收（loopback 替身，不用真实大厅）"),
        _test("PMTransportTest", (TAG_WIRE,),
              "Transport 独立性：序号/ACK/分片/重传窗口与超限拒绝"),
        _test("PMCallspaceCheck", (TAG_ALGORITHM,),
              "RPC callspace 16 分支真值表 + 归属校验 + 身份/帧契约"),
        _test("PMR4IntegrationTest", (TAG_WIRE,),
              "R4 集成：预测/回滚/重模拟与权威终态一致性"),
        _test("PMR5DeclarationTest", (TAG_WEAVING,),
              "R5 声明与生成物：ID 锁不漂移、权限/OnRep 与注册表一致"),
        _test("PMR6DeclarationTest", (TAG_WEAVING,),
              "R6 声明与生成物：公共/OwnerOnly 属性与可靠 RPC 布局不变"),
        _test("PMCombatCoreTest", (TAG_ALGORITHM,),
              "战斗核心：planner 数值/方向/弹数 + 资源/授权/ID/容量/终局"),
        _test("PMBattleContentManifestTest", (TAG_ALGORITHM,),
              "资源 manifest 纯规则：冻结 schema、摘要派生、白名单与负例"),
        _test("PMBattleContentSessionTest", (TAG_ALGORITHM,),
              "资源会话：manifest 校验/加载顺序/拒绝入局（不加载 Unity）"),
        _test("PMBattleContentSceneFactsCheck", (TAG_ALGORITHM,),
              "源场景事实门禁：把 HYLDGame.unity 当文本读的事实断言（不加载引擎）"),
        _test("PMNetVerify", (TAG_WIRE,),
              "大厅 protobuf 与 protoc 独立字节 oracle"),
        _test("PMDsHostCheck", (TAG_ALGORITHM,),
              "DS wrapper 初始化/失败/Dispose 受控替身门禁，不启动 DS"),
        _build("PMNetWeavingEditorCheck", (TAG_BUILD,),
               "编织 Editor 接口真实 Unity2019 API 编译，不执行回调"),
        _build("PMUnityGlueCheck", (TAG_BUILD,),
               "Unity 替身胶水编译门（netstandard2.0 + C# 7.3，不运行）"),
        _build("PMNetLangCheck", (TAG_BUILD,),
               "PMNet 语言面门禁：netstandard2.0 + C# 7.3 编译（不运行）"),
        _build("Server", (TAG_BUILD,),
               "Lobby 独立输出构建（-o 到本轮唯一目录，不覆盖运行中的 Server.dll）",
               project="Server/Server.csproj", assembly="Server"),
        _script("test_rpc_build_pipeline", (TAG_BUILD, TAG_WEAVING),
                "SDK 构建沙盒：冷/增量/属性变更/新增 RPC/非法声明的真实 build pipeline",
                script=("Tools/test_rpc_build_pipeline.py",), collect_sandbox_logs=True),
    ]
    return plan


SUITES = {"smoke": smoke_plan, "full": full_plan}


def build_plan(suite: str) -> list:
    builder = SUITES.get(suite)
    if builder is None:
        raise ValueError("未知 suite：%s" % suite)
    return builder()


# =====================================================================================
#  命令构造（一律 argv 列表，禁止 shell 字符串拼接）
# =====================================================================================

def build_command(dotnet_host: str, project: Path, out_dir: Path) -> list:
    return [dotnet_host, "build", str(project), "-c", "Release",
            "-o", str(out_dir), "-v", "minimal", "-nologo"]


def run_command(dotnet_host: str, dll: Path, run_args=()) -> list:
    return [dotnet_host, str(dll)] + [str(a) for a in run_args]


def script_command(script: Path, extra=()) -> list:
    python = sys.executable or shutil.which("python") or "python"
    return [python, str(script)] + [str(a) for a in extra]


def display_command(argv) -> str:
    return " ".join('"%s"' % a if (" " in a or "\t" in a) else str(a) for a in argv)


def child_env() -> dict:
    env = dict(os.environ)
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    # 让 python 子进程（full 末项 SDK 沙盒）在管道里也输出 UTF-8，日志可直接阅读。
    env["PYTHONIOENCODING"] = "utf-8"
    return env


def tail_lines(path: Path, count: int = FAILURE_TAIL_LINES) -> list:
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return []
    lines = text.splitlines()
    if len(lines) <= count:
        return lines
    return ["...（仅示最后 %d 行，完整日志见 %s）" % (count, path.name)] + lines[-count:]


# =====================================================================================
#  进程执行器（超时/取消只清理**自己启动**的进程树）
# =====================================================================================

@dataclass
class ExecResult:
    exit_code: int
    duration_seconds: float
    timed_out: bool = False


def kill_process_tree(proc: subprocess.Popen) -> None:
    """只用于本 runner 自己 Popen 出来的句柄；绝不按进程名批量杀。"""
    if proc.poll() is not None:
        return
    if os.name == "nt":
        try:
            subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                           check=False, timeout=30)
        except Exception:
            pass
    else:
        try:
            os.killpg(os.getpgid(proc.pid), 9)
        except Exception:
            pass
    try:
        proc.kill()
    except Exception:
        pass
    try:
        proc.wait(timeout=30)
    except Exception:
        pass


class SubprocessExecutor:
    """子进程直接写日志文件，避免无换行输出或后代继承管道导致无界读取/等EOF。"""

    def __init__(self, mono=time.monotonic):
        self._mono = mono

    def run(self, argv, cwd: Path, timeout_seconds: float, log_path: Path) -> ExecResult:
        log_path.parent.mkdir(parents=True, exist_ok=True)
        start = self._mono()
        timed_out = False
        options = ({"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP}
                   if os.name == "nt" else {"start_new_session": True})
        with log_path.open("w", encoding="utf-8", newline="\n") as log:
            log.write("$ %s\n# cwd: %s\n\n" % (display_command(argv), cwd))
            log.flush()
            proc = subprocess.Popen([str(a) for a in argv], cwd=str(cwd),
                                    stdin=subprocess.DEVNULL, stdout=log,
                                    stderr=subprocess.STDOUT, env=child_env(), **options)
            try:
                proc.wait(timeout=float(timeout_seconds))
            except subprocess.TimeoutExpired:
                timed_out = True
                kill_process_tree(proc)
            except BaseException:
                kill_process_tree(proc)
                raise
            code = proc.returncode if proc.returncode is not None else -1
            log.write("\n# exit=%s timed_out=%s\n" % (code, timed_out))
        return ExecResult(code, self._mono() - start, timed_out)


# =====================================================================================
#  仓库级独占锁（自防并发；冲突时绝不终止/删除他人状态）
# =====================================================================================

class LockBusy(RuntimeError):
    pass


class RepoLock:
    """`Tools/NetAcceptance/bin/runner.lock` 独占锁（O_CREAT|O_EXCL 原子创建）。"""

    def __init__(self, path=None, printer=print):
        self.path = Path(path) if path is not None else LOCK_PATH
        self.printer = printer
        self._acquired = False

    def _read_info(self) -> dict:
        try:
            return json.loads(self.path.read_text(encoding="utf-8"))
        except Exception:
            return {}

    def acquire(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        payload = json.dumps({
            "pid": os.getpid(),
            "host": socket.gethostname(),
            "started_at": now_iso(),
            "purpose": "run_net_acceptance",
        }, ensure_ascii=False)
        try:
            fd = os.open(str(self.path), os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644)
        except FileExistsError:
            info = self._read_info()
            # 不自动回收：空/半写JSON可能是刚拿锁尚未写完；PID也可能属于别台机器。
            # 人工确认没有本轮runner或其子进程后才能删除陈旧锁。
            raise LockBusy("锁已存在（pid=%s host=%s）；不抢锁、不终止他人进程。"
                           "请等待结束；若异常遗留，人工确认进程与子进程已退出后删除 %s。"
                           % (info.get("pid"), info.get("host"), self.path))
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            handle.write(payload)
        self._acquired = True

    def release(self) -> None:
        if not self._acquired:
            return
        self._acquired = False
        try:
            info = self._read_info()
            if int(info.get("pid", -1)) == os.getpid():
                os.remove(str(self.path))
        except Exception:
            pass


# =====================================================================================
#  预检 / 报告 / 执行
# =====================================================================================

def now_iso() -> str:
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def make_run_id() -> str:
    """时间戳 + pid + 随机后缀：同一秒内的不同进程/重复调用也不会撞同一个输出目录。"""
    return "%s-p%d-%s" % (datetime.now().strftime("%Y%m%d-%H%M%S"), os.getpid(),
                           uuid.uuid4().hex[:6])


def preflight(plan: list, repo: Path, which=None) -> tuple:
    """返回 (dotnet_host, problems)。problems 非空 ⇒ 非 0 退出，绝不写假绿。

    `which` 默认在调用时解析（不在 def 期绑定），否则外部替换 shutil.which 对预检无效。
    """
    if which is None:
        which = shutil.which
    problems = []
    if not plan:
        problems.append("空选择：清单为空，拒绝运行（不允许以空清单冒充通过）。")
    dotnet_host = which("dotnet")
    if not dotnet_host:
        problems.append("缺少 dotnet：PATH 中找不到 dotnet 宿主，无法构建/运行（拒绝写假绿）。")
    for entry in plan:
        if entry.kind in ("test", "build"):
            if not (repo / entry.project).is_file():
                problems.append("工程不存在：%s（清单与实际仓库不一致，拒绝继续）。" % entry.project)
        elif entry.kind == "script":
            if not entry.script or not (repo / entry.script[0]).is_file():
                problems.append("脚本不存在：%s（清单与实际仓库不一致，拒绝继续）。"
                                % (entry.script[0] if entry.script else "<empty>"))
        else:
            problems.append("未知条目类型：%s（%s）" % (entry.kind, entry.name))
    return dotnet_host, problems


def new_report(suite: str, run_id: str, repo: Path, timeout_seconds: float) -> dict:
    return {
        "runner": "Tools/run_net_acceptance.py",
        "runner_version": RUNNER_VERSION,
        "suite": suite,
        "run_id": run_id,
        "repo": str(repo),
        "started_at": now_iso(),
        "finished_at": None,
        "duration_seconds": None,
        "timeout_seconds": timeout_seconds,
        "code_only": True,
        "unity": {"status": "NOT_RUN", "reason": UNITY_NOTE},
        "status": "RUNNING",
        "exit_code": None,
        "stop_reason": None,
        "items": [],
        "notes": [
            "失败即停：任一项 build 或 run 非 0，后续项一律 NOT_RUN。",
            "build 非 0 绝不运行任何产物；build 0 但产物 DLL 不存在同样判失败。",
            "超时/取消只清理本 runner 自己启动的进程树。",
            "本报告只代表 CODE_ONLY（编译 + 纯网络/算法门禁）；不含任何实机通过结论。",
        ],
    }


def new_item(entry: Entry, index: int) -> dict:
    return {
        "index": index,
        "name": entry.name,
        "kind": entry.kind,
        "tags": list(entry.tags),
        "description": entry.description,
        "target": entry.project or (" ".join(entry.script) if entry.script else ""),
        "status": "PENDING",
        "note": "",
        "build": {"status": "N/A"},
        "run": {"status": "N/A"},
        "dll": None,
    }


def stage_record(argv, result: ExecResult, log_path: Path, run_dir: Path) -> dict:
    if result.timed_out:
        status = "TIMEOUT"
    elif result.exit_code == 0:
        status = "PASS"
    else:
        status = "FAIL"
    return {
        "command": display_command(argv),
        "exit_code": result.exit_code,
        "duration_seconds": round(result.duration_seconds, 3),
        "status": status,
    }


def _relative(path: Path, base: Path) -> str:
    try:
        return str(Path(path).relative_to(base)).replace("\\", "/")
    except ValueError:
        return str(path).replace("\\", "/")


def collect_sandbox_logs(repo: Path, run_dir: Path, since: float) -> list:
    """`test_rpc_build_pipeline.py` 固定写仓库根的 Tools/rpc-build-sandbox-N.log（不主张完全隔离）。

    这里把本轮产生的副本收集到 <run>/logs/sdk-sandbox/，让报告自洽；不改写原文件。
    """
    copied = []
    source_dir = repo / "Tools"
    dest_dir = run_dir / "logs" / "sdk-sandbox"
    dest_dir.mkdir(parents=True, exist_ok=True)
    for source in sorted(source_dir.glob("rpc-build-sandbox-*.log")):
        try:
            if source.stat().st_mtime + 2.0 < since:
                continue
            shutil.copy2(str(source), str(dest_dir / source.name))
            copied.append(_relative(dest_dir / source.name, run_dir))
        except OSError:
            continue
    return copied


def execute_entry(entry: Entry, repo: Path, run_dir: Path, timeout_seconds: float,
                  executor, dotnet_host: str, printer=print, item=None, on_progress=None) -> tuple:
    item = item if item is not None else new_item(entry, 0)
    logs_dir = run_dir / "logs"

    def execute_stage(stage, argv, log_path):
        item[stage] = {"status": "RUNNING", "command": display_command(argv),
                       "log": _relative(log_path, run_dir)}
        if on_progress:
            on_progress()
        try:
            return executor.run(argv, repo, timeout_seconds, log_path)
        except BaseException as exc:
            item[stage]["status"] = "CANCELLED" if isinstance(exc, KeyboardInterrupt) else "FAIL"
            raise

    if entry.kind in ("test", "build"):
        out_dir = run_dir / "out" / entry.name
        if out_dir.exists():
            shutil.rmtree(str(out_dir), ignore_errors=True)
        out_dir.mkdir(parents=True, exist_ok=True)

        argv = build_command(dotnet_host, repo / entry.project, out_dir)
        log_path = logs_dir / ("%s.build.log" % entry.name)
        result = execute_stage("build", argv, log_path)
        item["build"] = stage_record(argv, result, log_path, run_dir)
        item["build"]["log"] = _relative(log_path, run_dir)
        printer("   build exit=%s in %.1fs -> %s"
                % (result.exit_code, result.duration_seconds, item["build"]["status"]))
        if result.timed_out:
            item["status"] = "TIMEOUT"
            item["note"] = "构建阶段超时（单阶段期限 %ss）；不运行任何产物。" % timeout_seconds
            return item, "TIMEOUT"
        if result.exit_code != 0:
            item["status"] = "FAIL"
            item["note"] = "构建失败（exit %s）；**不运行**旧/未更新产物。" % result.exit_code
            return item, "FAIL"

        dll = out_dir / (entry.assembly_name() + ".dll")
        if not dll.is_file():
            item["status"] = "FAIL"
            item["note"] = ("构建 exit 0 但产物 DLL 不存在（%s）；拒绝把「零错误」当通过，"
                            "也不运行任何旧产物。" % _relative(dll, run_dir))
            return item, "FAIL"
        item["dll"] = _relative(dll, run_dir)

        if entry.kind == "build":
            item["run"] = {"status": "N/A", "note": "build-only（类库编译门，不运行）"}
            item["status"] = "PASS"
            return item, None

        argv = run_command(dotnet_host, dll, entry.run_args)
        log_path = logs_dir / ("%s.run.log" % entry.name)
        result = execute_stage("run", argv, log_path)
        item["run"] = stage_record(argv, result, log_path, run_dir)
        item["run"]["log"] = _relative(log_path, run_dir)
        printer("   run   exit=%s in %.1fs -> %s"
                % (result.exit_code, result.duration_seconds, item["run"]["status"]))
        if result.timed_out:
            item["status"] = "TIMEOUT"
            item["note"] = "运行阶段超时（单阶段期限 %ss）；只清理本 runner 自己启动的进程树。" % timeout_seconds
            return item, "TIMEOUT"
        if result.exit_code != 0:
            item["status"] = "FAIL"
            item["note"] = "运行退出码非 0（exit %s）。" % result.exit_code
            return item, "FAIL"
        item["status"] = "PASS"
        return item, None

    if entry.kind == "script":
        started = time.time()
        argv = script_command(repo / entry.script[0], entry.script[1:])
        log_path = logs_dir / ("%s.log" % entry.name)
        item["build"] = {"status": "N/A", "note": "脚本阶段：无独立构建步骤"}
        result = execute_stage("run", argv, log_path)
        item["run"] = stage_record(argv, result, log_path, run_dir)
        item["run"]["log"] = _relative(log_path, run_dir)
        printer("   script exit=%s in %.1fs -> %s"
                % (result.exit_code, result.duration_seconds, item["run"]["status"]))
        if entry.collect_sandbox_logs:
            copied = collect_sandbox_logs(repo, run_dir, started)
            item["sandbox_logs"] = copied
            item["note"] = ("SDK 沙盒脚本固定写仓库根 Tools/rpc-build-sandbox-N.log；"
                            "本轮副本已收集到 %s（不伪造完全隔离）。"
                            % ("logs/sdk-sandbox/" if copied else "（本轮未发现新副本）"))
        if result.timed_out:
            item["status"] = "TIMEOUT"
            return item, "TIMEOUT"
        if result.exit_code != 0:
            item["status"] = "FAIL"
            item["note"] = (item["note"] + " " if item["note"] else "") + \
                           "脚本退出码非 0（exit %s）。" % result.exit_code
            return item, "FAIL"
        item["status"] = "PASS"
        return item, None

    item["status"] = "FAIL"
    item["note"] = "未知条目类型：%s" % entry.kind
    return item, "FAIL"


_REASON_EXIT = {
    "FAIL": EXIT_FAILED,
    "TIMEOUT": EXIT_TIMEOUT,
    "CANCELLED": EXIT_CANCELLED,
    "LOCK": EXIT_LOCKED,
    "PREFLIGHT": EXIT_PREFLIGHT,
}


def run_plan(plan: list, repo: Path, run_dir: Path, timeout_seconds: float,
             executor, dotnet_host: str, suite: str = "smoke", run_id: str = "",
             printer=print, writer=None, clock=time.monotonic) -> tuple:
    """串行执行清单；返回 (report, exit_code)。失败即停，剩余 NOT_RUN。"""
    report = new_report(suite, run_id, repo, timeout_seconds)
    write = writer or write_report
    started = clock()
    stop_reason = None
    exit_code = EXIT_OK

    if not plan:
        report["status"] = "PREFLIGHT_FAILED"
        report["stop_reason"] = "PREFLIGHT"
        report["exit_code"] = EXIT_PREFLIGHT
        report["notes"].append("空选择：清单为空，拒绝运行（不允许以空清单冒充通过）。")
        finish_report(report, started, clock)
        write(run_dir, report)
        return report, EXIT_PREFLIGHT

    report["items"] = [new_item(entry, index) for index, entry in enumerate(plan, 1)]
    write(run_dir, report)
    for index, entry in enumerate(plan, 1):
        item = report["items"][index - 1]
        if stop_reason is not None:
            item["status"] = "NOT_RUN"
            item["note"] = "前序 %s，失败即停；本项未运行。" % stop_reason
            printer("-- [%d/%d] %s -> NOT_RUN（前序 %s）" % (index, len(plan), entry.name, stop_reason))
            write(run_dir, report)
            continue

        printer("== [%d/%d] %s (kind=%s, tags=%s)"
                % (index, len(plan), entry.name, entry.kind, ",".join(entry.tags)))
        try:
            executed, reason = execute_entry(entry, repo, run_dir, timeout_seconds,
                                             executor, dotnet_host, printer=printer, item=item,
                                             on_progress=lambda: write(run_dir, report))
        except KeyboardInterrupt:
            item["status"] = "CANCELLED"
            item["note"] = "收到中断；只清理本 runner 自己启动的进程树，保留部分报告。"
            stop_reason = "CANCELLED"
            exit_code = EXIT_CANCELLED
            write(run_dir, report)
            printer("!! %s 被取消；已保留部分报告。" % entry.name)
            continue
        except Exception as exc:  # 执行器/宿主级意外：仍然给报告，不给裸 traceback
            item["status"] = "FAIL"
            item["note"] = "执行器异常：%s: %s" % (type(exc).__name__, exc)
            stop_reason = "FAIL"
            exit_code = EXIT_FAILED
            printer("!! %s 执行异常：%s: %s" % (entry.name, type(exc).__name__, exc))
            write(run_dir, report)
            continue

        item.update({k: v for k, v in executed.items() if k != "index"})
        item["index"] = index
        if reason is not None:
            stop_reason = reason
            exit_code = _REASON_EXIT.get(reason, EXIT_FAILED)
            failed_log = None
            for stage in (item.get("run"), item.get("build")):
                if stage and stage.get("status") == "FAIL" and stage.get("log"):
                    failed_log = run_dir / stage["log"]
                    break
            if failed_log is not None:
                for line in tail_lines(failed_log, FAILURE_TAIL_LINES):
                    printer("     | " + line)
        write(run_dir, report)

    report["stop_reason"] = stop_reason
    report["status"] = "PASS" if exit_code == EXIT_OK else stop_reason
    report["exit_code"] = exit_code
    finish_report(report, started, clock)
    write(run_dir, report)
    return report, exit_code


def finish_report(report: dict, started: float, clock) -> None:
    report["finished_at"] = now_iso()
    report["duration_seconds"] = round(clock() - started, 3)


def render_markdown(report: dict) -> str:
    lines = []
    lines.append("# 运行准入报告（CODE_ONLY）")
    lines.append("")
    lines.append("- run_id：`%s`" % report["run_id"])
    lines.append("- suite：`%s`" % report["suite"])
    lines.append("- 仓库：`%s`" % report["repo"])
    lines.append("- 开始/结束：%s → %s（%.1fs）"
                 % (report["started_at"], report.get("finished_at") or "-",
                    report.get("duration_seconds") or 0.0))
    lines.append("- 单阶段期限：%ss（每次 build / 每次 run 各自计时）" % report["timeout_seconds"])
    lines.append("- code_only：**true**")
    lines.append("- Unity：**NOT_RUN** —— %s" % report["unity"]["reason"])
    lines.append("- 总体：**%s**（exit=%s%s）"
                 % (report["status"], report.get("exit_code"),
                    "，停止原因 %s" % report["stop_reason"] if report.get("stop_reason") else ""))
    lines.append("")
    lines.append("## 明细")
    lines.append("")
    lines.append("| # | 项 | 类别 | 标签 | build（exit/耗时/状态） | run（exit/耗时/状态） | 结论 |")
    lines.append("|---|---|---|---|---|---|---|")
    for item in report["items"]:
        build = item.get("build") or {}
        run = item.get("run") or {}
        lines.append("| %s | %s | %s | %s | %s | %s | **%s** |"
                     % (item["index"], item["name"], item["kind"], ",".join(item["tags"]),
                        _stage_cell(build), _stage_cell(run), item["status"]))
    lines.append("")
    lines.append("## 标签口径")
    lines.append("")
    lines.append("- `build`：编译门（含 build-only；只证明编得过，不证明行为）。")
    lines.append("- `algorithm`：纯算法/数据断言。")
    lines.append("- `wire`：真实 Transport 字节链。")
    lines.append("- `weaving`：生成器 + IL 编织（真实夹具编译/执行）。")
    lines.append("- **没有**任何「实机/Unity 通过」标记；Unity 相关项恒为 `NOT_RUN`。")
    lines.append("")
    lines.append("## 说明")
    lines.append("")
    for note in report.get("notes", []):
        lines.append("- %s" % note)
    lines.append("")
    lines.append("## 项备注 / 日志")
    lines.append("")
    for item in report["items"]:
        rel = []
        if (item.get("build") or {}).get("log"):
            rel.append("build=`%s`" % item["build"]["log"])
        if (item.get("run") or {}).get("log"):
            rel.append("run=`%s`" % item["run"]["log"])
        if item.get("sandbox_logs"):
            rel.append("sandbox=%s" % ", ".join("`%s`" % p for p in item["sandbox_logs"]))
        lines.append("- **%s**（%s）：%s%s%s"
                     % (item["name"], item["status"],
                        item.get("note") or item["description"],
                        "；" if rel else "", "；".join(rel)))
    lines.append("")
    return "\n".join(lines)


def _stage_cell(stage: dict) -> str:
    if not stage or stage.get("status") in (None, "N/A"):
        return stage.get("note", "N/A") if stage else "N/A"
    if "exit_code" in stage:
        return "%s / %.1fs / %s" % (stage["exit_code"], stage.get("duration_seconds", 0.0),
                                    stage["status"])
    return stage.get("status", "N/A")


def write_report(run_dir: Path, report: dict) -> None:
    run_dir = Path(run_dir)
    run_dir.mkdir(parents=True, exist_ok=True)
    for name, text in (("summary.json", json.dumps(report, ensure_ascii=False, indent=2)),
                       ("summary.md", render_markdown(report))):
        temporary = run_dir / (name + ".tmp")
        temporary.write_text(text, encoding="utf-8", newline="\n")
        os.replace(temporary, run_dir / name)


# =====================================================================================
#  清单打印 / CLI
# =====================================================================================

def print_manifest(suite: str, printer=print) -> None:
    plan = build_plan(suite)
    printer("suite=%s  项数=%d（串行；失败即停，剩余 NOT_RUN）" % (suite, len(plan)))
    printer("标签：build=编译门 / algorithm=纯算法 / wire=真实 Transport 字节链 / weaving=生成+编织")
    printer("Unity：NOT_RUN —— %s" % UNITY_NOTE)
    printer("")
    for index, entry in enumerate(plan, 1):
        target = entry.project or (" ".join(entry.script) if entry.script else "")
        printer("[%02d] %-32s kind=%-6s tags=%-20s %s"
                % (index, entry.name, entry.kind, ",".join(entry.tags), target))
        printer("     %s" % entry.description)
    printer("")
    printer("以上仅为将要执行的清单本身：不代表任何项已通过，也不含实机结论。")


def positive_int(text: str) -> int:
    try:
        value = int(text)
    except ValueError:
        raise argparse.ArgumentTypeError("必须是正整数：%r" % text)
    if value <= 0:
        raise argparse.ArgumentTypeError("必须是正整数：%r" % text)
    return value


class _ArgumentParser(argparse.ArgumentParser):
    """参数不合法时统一用 EXIT_USAGE(6)，与预检失败(2)区分开。"""

    def error(self, message):
        self.print_usage(sys.stderr)
        self.exit(EXIT_USAGE, "%s: error: %s\n" % (self.prog, message))


def parse_args(argv=None):
    parser = _ArgumentParser(
        prog="run_net_acceptance.py",
        description="hyld-master 运行准入 runner（CODE_ONLY；不启动 Unity / 大厅）")
    parser.add_argument("--suite", choices=sorted(SUITES.keys()), default="smoke",
                        help="执行清单（默认 smoke）")
    parser.add_argument("--list", dest="list_only", action="store_true",
                        help="只列出清单：不构建、不运行、不写任何文件、不需要 dotnet")
    parser.add_argument("--timeout-seconds", dest="timeout_seconds", type=positive_int,
                        default=DEFAULT_TIMEOUT_SECONDS, metavar="N",
                        help="单阶段期限（每次 build / 每次 run，默认 %d 秒）" % DEFAULT_TIMEOUT_SECONDS)
    return parser.parse_args(argv)


def configure_stdio() -> None:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass


def main(argv=None) -> int:
    configure_stdio()
    args = parse_args(argv)

    if args.list_only:
        print_manifest(args.suite)
        return EXIT_OK

    plan = build_plan(args.suite)
    run_id = make_run_id()
    run_dir = BIN_ROOT / run_id

    lock = RepoLock(LOCK_PATH, printer=print)
    try:
        lock.acquire()
    except LockBusy as exc:
        print("[lock] %s" % exc)
        return EXIT_LOCKED

    try:
        run_dir.mkdir(parents=True, exist_ok=True)
        dotnet_host, problems = preflight(plan, REPO_ROOT)
        if problems:
            report = new_report(args.suite, run_id, REPO_ROOT, args.timeout_seconds)
            report["status"] = "PREFLIGHT_FAILED"
            report["stop_reason"] = "PREFLIGHT"
            report["exit_code"] = EXIT_PREFLIGHT
            report["notes"].extend(problems)
            for index, entry in enumerate(plan, 1):
                item = new_item(entry, index)
                item["status"] = "NOT_RUN"
                item["note"] = "预检失败，未运行。"
                report["items"].append(item)
            report["finished_at"] = now_iso()
            report["duration_seconds"] = 0.0
            write_report(run_dir, report)
            print("[preflight] 失败，拒绝写假绿：")
            for problem in problems:
                print("  - %s" % problem)
            print("[report] %s" % (run_dir / "summary.json"))
            return EXIT_PREFLIGHT

        print("[run] suite=%s run_id=%s" % (args.suite, run_id))
        print("[run] 输出/日志/summary 唯一目录：%s" % run_dir)
        report, exit_code = run_plan(plan, REPO_ROOT, run_dir, args.timeout_seconds,
                                     SubprocessExecutor(), dotnet_host,
                                     suite=args.suite, run_id=run_id)
        print("")
        print("[done] suite=%s status=%s exit=%s code_only=true unity=NOT_RUN"
              % (args.suite, report["status"], exit_code))
        print("[report] %s" % (run_dir / "summary.json"))
        print("[report] %s" % (run_dir / "summary.md"))
        return exit_code
    finally:
        lock.release()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(EXIT_CANCELLED)
