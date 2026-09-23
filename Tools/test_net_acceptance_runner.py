#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""`Tools/run_net_acceptance.py` 的自测（unittest）。

只验证 runner 自身的机器行为，不冒充业务验收：
  * 不启动 Unity、不打开大厅、不跑 smoke 全套（主侧/用户另行执行 runner 的 smoke/full）；
  * 不修改业务源码，只在 `tempfile` 沙盒里造临时仓库与临时工程；
  * 需要真实子进程的场景用**本机真实 dotnet** 构建几个一秒钟的迷你工程，而不是让 runner 假装成功。

覆盖的验收点（对应运行准入开工段）：
  1. 清单：smoke/full 组成、标签词表、禁止项（不跑 PMServerSmokeTest/UnitySmoke/不用真实大厅）。
  2. build 非 0 **绝不**运行任何产物；build=0 但产物 DLL 不存在同样判失败。
  3. 任一项失败即停，剩余项一律 NOT_RUN；不解析几行 stdout 猜通过。
  4. 超时/取消：非 0 退出、保留部分汇总、只清理自己启动的进程树（不杀无关服务）。
  5. 预检：缺 dotnet / 缺工程 / 空选择 ⇒ 非 0，且不写假绿。
  6. `--list` 无副作用、无需 dotnet、可从任意 cwd 执行（repo 由脚本自身定位）。
  7. 仓库级独占锁：并发冲突拒绝启动且不终止他人进程、不删除他人锁；陈旧/半写锁保守拒绝，人工确认后清理。
  8. 命令一律 argv 列表（Windows 路径带空格安全），源码中不出现 shell=True、不按进程名批量杀。

运行：python Tools/test_net_acceptance_runner.py -v
退出码：0 = 全部通过；非 0 = 存在失败。
"""

import importlib.util
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO_ROOT = Path(__file__).resolve().parents[1]
RUNNER_PATH = REPO_ROOT / "Tools" / "run_net_acceptance.py"


def load_runner():
    """按文件路径加载被测 runner（不依赖包结构，也避免被当成测试模块导入）。

    刻意临时关掉字节码缓存：否则自测会在仓库 `Tools/` 下留一个未被 .gitignore 覆盖的
    `__pycache__`，也就把「自测零副作用」破坏了。
    """
    spec = importlib.util.spec_from_file_location("pmnet_acceptance_runner_under_test", str(RUNNER_PATH))
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    saved = sys.dont_write_bytecode
    sys.dont_write_bytecode = True
    try:
        spec.loader.exec_module(module)
    finally:
        sys.dont_write_bytecode = saved
    return module


R = load_runner()
SMOKE = R.build_plan("smoke")
FULL = R.build_plan("full")

SMOKE_NAMES = [
    "PMDeclCheck", "PMReplicationTest", "PMNetE2E", "PMR3RuntimeTest", "PMR3IntegrationTest",
    "PMR4NetworkTest", "PMR5NetworkTest", "PMR6NetworkTest", "PMNetSessionTest", "PMNetWorldTest",
    "PMLegacyRetirementTest", "PMNetWeavingEditorTest", "PMClientCheck", "PMR4UnityCheck",
]
FULL_ONLY_NAMES = [
    "PMPropertyWeaverTest", "PMNetWeaverTest", "PMDsControlTest", "PMDsLobbyTest", "PMTransportTest",
    "PMCallspaceCheck", "PMR4IntegrationTest", "PMR5DeclarationTest", "PMR6DeclarationTest",
    "PMCombatCoreTest", "PMBattleContentManifestTest", "PMBattleContentSessionTest",
    "PMBattleContentSceneFactsCheck", "PMNetVerify", "PMDsHostCheck", "PMNetWeavingEditorCheck",
    "PMUnityGlueCheck", "PMNetLangCheck", "Server",
    "test_rpc_build_pipeline",
]
FORBIDDEN_NAMES = {
    "PMServerSmokeTest", "PMR3UnitySmoke", "PMUnitySmoke", "UnitySmoke", "PMServerSmoke",
    "PMR3UnitySmokeTest", "PMDsProbe", "PMUdpRouterTest", "PMUdpRouterCheck",
}


# =====================================================================================
#  测试替身与工具
# =====================================================================================

class FakeExecutor:
    """不启动任何进程的执行器：按脚本返回结果，并可真实地创建/不创建产物 DLL。"""

    def __init__(self, build_exit=0, run_exit=0, write_dll=True,
                 build_timeout=False, run_timeout=False, interrupt_on=None):
        self.build_exit = build_exit
        self.run_exit = run_exit
        self.write_dll = write_dll
        self.build_timeout = build_timeout
        self.run_timeout = run_timeout
        self.interrupt_on = interrupt_on
        self.calls = []

    def _stage(self, argv):
        if len(argv) > 1 and argv[1] == "build":
            return "build"
        if Path(argv[0]).stem.lower().startswith("python"):
            return "script"
        return "run"

    def run(self, argv, cwd, timeout_seconds, log_path):
        argv = [str(a) for a in argv]
        log_path = Path(log_path)
        log_path.parent.mkdir(parents=True, exist_ok=True)
        log_path.write_text("$ %s\n" % R.display_command(argv), encoding="utf-8")
        stage = self._stage(argv)
        self.calls.append({"stage": stage, "argv": argv, "cwd": str(cwd), "log": str(log_path)})

        if self.interrupt_on == stage:
            raise KeyboardInterrupt

        if stage == "build":
            if self.build_timeout:
                return R.ExecResult(1, 0.01, timed_out=True)
            if self.build_exit == 0 and self.write_dll:
                out_dir = Path(argv[argv.index("-o") + 1])
                out_dir.mkdir(parents=True, exist_ok=True)
                (out_dir / (Path(argv[2]).stem + ".dll")).write_text("fake-dll", encoding="utf-8")
            return R.ExecResult(self.build_exit, 0.01)

        if stage == "script":
            if self.run_timeout:
                return R.ExecResult(1, 0.01, timed_out=True)
            return R.ExecResult(self.run_exit, 0.01)

        if self.run_timeout:
            return R.ExecResult(1, 0.01, timed_out=True)
        return R.ExecResult(self.run_exit, 0.01)

    def stages(self):
        return [call["stage"] for call in self.calls]


CONSOLE_CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>{name}</AssemblyName>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
{extra}
</Project>
"""

CONSOLE_PROGRAM = ("using System;\n"
                   "using System.IO;\n"
                   "internal static class Program\n"
                   "{\n"
                   "    private static int Main()\n"
                   "    {\n"
                   "        string marker = Environment.GetEnvironmentVariable(\"PMTEST_MARKER\");\n"
                   "        if (!string.IsNullOrEmpty(marker)) { File.AppendAllText(marker, \"run\\n\"); }\n"
                   "        return int.Parse(Environment.GetEnvironmentVariable(\"PMTEST_EXIT\") ?? \"0\");\n"
                   "    }\n"
                   "}\n")

BROKEN_PROGRAM = "this is not valid C#\n"

DELETE_OUTPUT_TARGET = ("  <Target Name=\"RemoveOutput\" AfterTargets=\"CopyFilesToOutputDirectory\">\n"
                        "    <Delete Files=\"$(TargetPath)\" />\n"
                        "  </Target>\n")


class Sandbox:
    """在系统临时目录里造一个「带空格的假仓库」，里面放被测 runner 的副本。"""

    def __init__(self, with_space=True, with_dotnet_stub=False):
        parent = tempfile.mkdtemp(prefix="pmnet-acceptance-selftest-")
        name = "repo with space" if with_space else "repo"
        self.root = Path(parent) / name
        (self.root / "Tools").mkdir(parents=True)
        shutil.copy2(str(RUNNER_PATH), str(self.root / "Tools" / "run_net_acceptance.py"))
        # 阻断 MSBuild 向上继承宿主目录里的 Directory.Build.props/targets（临时仓库保持自洽）。
        (self.root / "Directory.Build.props").write_text("<Project></Project>\n", encoding="utf-8")
        (self.root / "Directory.Build.targets").write_text("<Project></Project>\n", encoding="utf-8")
        # 强制离线还原：迷你工程不引任何 PackageReference，不需要 NuGet 源。
        (self.root / "NuGet.config").write_text(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n"
            "    <clear />\n  </packageSources>\n</configuration>\n", encoding="utf-8")
        for entry in SMOKE:
            if entry.project:
                target = self.root / entry.project
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text("<Project></Project>\n", encoding="utf-8")
        self.empty_path_dir = Path(parent) / "empty-path"
        self.empty_path_dir.mkdir()

    # -------- 工程内容 --------

    def write_console_project(self, name, program=None, extra_target="", assembly=None):
        folder = self.root / "Tools" / name
        folder.mkdir(parents=True, exist_ok=True)
        (folder / ("%s.csproj" % name)).write_text(
            CONSOLE_CSPROJ.format(name=assembly or name, extra=extra_target), encoding="utf-8")
        (folder / "Program.cs").write_text(program or CONSOLE_PROGRAM, encoding="utf-8")

    def write_broken_project(self, name):
        folder = self.root / "Tools" / name
        folder.mkdir(parents=True, exist_ok=True)
        (folder / ("%s.csproj" % name)).write_text(
            CONSOLE_CSPROJ.format(name=name, extra=""), encoding="utf-8")
        (folder / "Program.cs").write_text(BROKEN_PROGRAM, encoding="utf-8")

    def write_dll_deleting_project(self, name):
        self.write_console_project(name, extra_target=DELETE_OUTPUT_TARGET)

    # -------- 执行 --------

    def runner_script(self):
        return self.root / "Tools" / "run_net_acceptance.py"

    def run_runner(self, args, env_extra=None, extra_path=None, timeout=600, cwd=None):
        env = dict(os.environ)
        env["PYTHONIOENCODING"] = "utf-8"
        if extra_path is not None:
            env["PATH"] = extra_path
        if env_extra:
            env.update(env_extra)
        proc = subprocess.run(
            [sys.executable, str(self.runner_script())] + list(args),
            cwd=str(cwd or self.root), env=env, capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=timeout)
        return proc

    def bin_root(self):
        return self.root / "Tools" / "NetAcceptance" / "bin"

    def lock_path(self):
        return self.bin_root() / "runner.lock"

    def run_dirs(self):
        root = self.bin_root()
        if not root.is_dir():
            return []
        return sorted(p for p in root.iterdir() if p.is_dir() and not p.name.startswith("."))

    def summary_of_single_run(self):
        dirs = self.run_dirs()
        assert len(dirs) == 1, "期望恰好一个 run 目录，实际 %r" % dirs
        return json.loads((dirs[0] / "summary.json").read_text(encoding="utf-8")), dirs[0]

    def cleanup(self):
        shutil.rmtree(str(self.root.parent), ignore_errors=True)


def start_dummy(seconds=120):
    """一个与本 runner 无关的长命进程（用于验证「不杀他人服务」）。"""
    return subprocess.Popen([sys.executable, "-c", "import time; time.sleep(%d)" % seconds],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def stop_process(proc):
    if proc is None:
        return
    try:
        if proc.poll() is None:
            proc.kill()
            proc.wait(timeout=30)
    except Exception:
        pass


def pid_alive(pid):
    # 仅测试用：Windows HANDLE 是指针宽度，禁止依赖 ctypes 默认 int 返回类型。
    if os.name == "nt":
        import ctypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = kernel.OpenProcess(0x1000, False, pid)
        if not handle:
            return ctypes.get_last_error() != 87  # ERROR_INVALID_PARAMETER: PID 不存在
        try:
            code = ctypes.c_ulong()
            return not kernel.GetExitCodeProcess(handle, ctypes.byref(code)) or code.value == 259
        finally:
            kernel.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False


def dead_pid():
    proc = subprocess.Popen([sys.executable, "-c", "pass"])
    proc.wait()
    return proc.pid


def inproc_run(executor, plan=None, run_dir=None, timeout_seconds=30):
    """在进程内跑 core（不启动 runner CLI），断言用。"""
    owned = run_dir is None
    if owned:
        run_dir = Path(tempfile.mkdtemp(prefix="pmnet-acceptance-inproc-"))
    report, code = R.run_plan(list(plan if plan is not None else SMOKE[:2]),
                              REPO_ROOT, Path(run_dir), timeout_seconds,
                              executor, "/usr/bin/dotnet", suite="smoke", run_id="inproc",
                              printer=lambda *a, **k: None)
    return report, code, Path(run_dir)


# =====================================================================================
#  1. 清单
# =====================================================================================

class ManifestTests(unittest.TestCase):

    def test_smoke_has_exactly_the_frozen_items(self):
        self.assertEqual([e.name for e in SMOKE], SMOKE_NAMES)
        self.assertEqual(len(SMOKE), 14)

    def test_smoke_kinds_split(self):
        tests = [e for e in SMOKE if e.kind == "test"]
        builds = [e for e in SMOKE if e.kind == "build"]
        self.assertEqual(len(tests), 12)
        self.assertEqual(sorted(e.name for e in builds), ["PMClientCheck", "PMR4UnityCheck"])

    def test_full_is_smoke_plus_frozen_additions_in_order(self):
        names = [e.name for e in FULL]
        self.assertEqual(names[:len(SMOKE_NAMES)], SMOKE_NAMES)
        self.assertEqual(names[len(SMOKE_NAMES):], FULL_ONLY_NAMES)
        self.assertEqual(names, SMOKE_NAMES + FULL_ONLY_NAMES)

    def test_full_last_item_is_sdk_pipeline_script(self):
        last = FULL[-1]
        self.assertEqual(last.name, "test_rpc_build_pipeline")
        self.assertEqual(last.kind, "script")
        self.assertEqual(last.script, ("Tools/test_rpc_build_pipeline.py",))
        self.assertTrue(last.collect_sandbox_logs)

    def test_server_entry_is_build_only_independent_output(self):
        server = [e for e in FULL if e.name == "Server"]
        self.assertEqual(len(server), 1)
        self.assertEqual(server[0].kind, "build")
        self.assertEqual(server[0].project, "Server/Server.csproj")

    def test_no_unity_or_lobby_runtime_tools(self):
        for entry in SMOKE + FULL:
            self.assertNotIn(entry.name, FORBIDDEN_NAMES, entry.name)
            lowered = entry.name.lower()
            self.assertNotIn("unitysmoke", lowered, entry.name)
            self.assertNotIn("serversmoke", lowered, entry.name)
            target = (entry.project or "") + " ".join(entry.script)
            self.assertNotIn("Unity.exe", target)
            self.assertNotIn(".unity", target)

    def test_tags_are_within_frozen_vocabulary(self):
        for entry in SMOKE + FULL:
            self.assertTrue(entry.tags, entry.name)
            for tag in entry.tags:
                self.assertIn(tag, R.TAG_VOCABULARY, entry.name)
            self.assertTrue(entry.description.strip(), entry.name)

    def test_manifest_targets_exist_in_this_repo(self):
        for entry in FULL:
            if entry.project:
                self.assertTrue((REPO_ROOT / entry.project).is_file(), entry.project)
            if entry.script:
                self.assertTrue((REPO_ROOT / entry.script[0]).is_file(), entry.script[0])

    def test_unknown_suite_rejected_by_core(self):
        with self.assertRaises(ValueError):
            R.build_plan("nope")


# =====================================================================================
#  2. 命令构造（argv 列表，绝无 shell 拼接）
# =====================================================================================

class CommandTests(unittest.TestCase):

    def test_build_command_is_argv_list_and_keeps_spaces(self):
        out_dir = Path("C:/tmp/run with space/out/PMDeclCheck")
        project = Path("C:/repo with space/Tools/PMDeclCheck/PMDeclCheck.csproj")
        argv = R.build_command("C:/Program Files/dotnet/dotnet.exe", project, out_dir)
        self.assertIsInstance(argv, list)
        self.assertEqual(argv[1], "build")
        self.assertIn(str(project), argv)
        self.assertEqual(argv[argv.index("-o") + 1], str(out_dir))
        # 单元素保持完整：没有被拆成多个参数，也没有人为加引号。
        self.assertNotIn('"', "".join(argv))

    def test_run_command_uses_single_dll_argument(self):
        dll = Path("C:/out dir/PMDeclCheck.dll")
        argv = R.run_command("dotnet", dll)
        self.assertEqual(argv[0], "dotnet")
        self.assertEqual(argv[1], str(dll))
        self.assertEqual(len(argv), 2)

    def test_script_command_uses_python_interpreter(self):
        script = Path("C:/repo with space/Tools/test_rpc_build_pipeline.py")
        argv = R.script_command(script)
        self.assertTrue(Path(argv[0]).stem.lower().startswith("python"), argv[0])
        self.assertEqual(argv[1], str(script))

    def test_display_command_quotes_only_when_needed(self):
        self.assertEqual(R.display_command(["dotnet", "build", "a"]), "dotnet build a")
        self.assertEqual(R.display_command(["dotnet", "C:/out dir/x.dll"]), 'dotnet "C:/out dir/x.dll"')

    def test_source_has_no_shell_string_execution(self):
        source = RUNNER_PATH.read_text(encoding="utf-8-sig")
        self.assertNotIn("shell=True", source)
        self.assertNotIn("shell = True", source)
        self.assertNotIn("os.system", source)
        self.assertNotIn("Invoke-Expression", source)
        # 只按 PID 杀自己启动的进程树：不允许按镜像名批量杀。
        self.assertIn("taskkill", source)
        self.assertIn('"/PID"', source)
        self.assertIn('"/T"', source)
        self.assertNotIn('"/IM"', source)


# =====================================================================================
#  3. 报告与失败传播（进程内 core + 替身执行器）
# =====================================================================================

class ReportTests(unittest.TestCase):

    def setUp(self):
        self.temp = tempfile.mkdtemp(prefix="pmnet-acceptance-report-")

    def tearDown(self):
        shutil.rmtree(self.temp, ignore_errors=True)

    def test_report_is_written_before_each_stage(self):
        run_dir = Path(self.temp) / "progress"
        observations = []
        class Observer(FakeExecutor):
            def run(self, *args):
                observations.append(json.loads((run_dir / "summary.json").read_text(encoding="utf-8")))
                return super().run(*args)
        report, code, _ = inproc_run(Observer(), plan=SMOKE[:2], run_dir=run_dir)
        self.assertEqual(code, 0)
        self.assertEqual(len(observations), 4)
        self.assertTrue(all(len(o["items"]) == 2 for o in observations))
        self.assertEqual(observations[0]["items"][0]["build"]["status"], "RUNNING")
        self.assertEqual(observations[1]["items"][0]["build"]["status"], "PASS")
        self.assertEqual(observations[1]["items"][0]["run"]["status"], "RUNNING")
        self.assertEqual(observations[2]["items"][0]["status"], "PASS")

    def test_all_pass_writes_report(self):
        executor = FakeExecutor()
        report, code, run_dir = inproc_run(executor, plan=SMOKE[:3],
                                           run_dir=Path(self.temp) / "run")
        self.assertEqual(code, R.EXIT_OK)
        self.assertEqual(report["status"], "PASS")
        self.assertTrue((run_dir / "summary.json").is_file())
        self.assertTrue((run_dir / "summary.md").is_file())
        self.assertEqual(executor.stages(), ["build", "run"] * 3)

    def test_build_failure_never_runs_any_dll(self):
        executor = FakeExecutor(build_exit=1)
        report, code, run_dir = inproc_run(executor, plan=SMOKE[:3],
                                           run_dir=Path(self.temp) / "run")
        self.assertEqual(code, R.EXIT_FAILED)
        self.assertNotIn("run", executor.stages())
        self.assertEqual(report["items"][0]["status"], "FAIL")
        self.assertEqual(report["items"][0]["build"]["status"], "FAIL")
        self.assertEqual(report["items"][0]["run"]["status"], "N/A")
        self.assertFalse((run_dir / "logs" / "PMDeclCheck.run.log").exists())
        for item in report["items"][1:]:
            self.assertEqual(item["status"], "NOT_RUN", item["name"])

    def test_build_zero_but_missing_dll_is_failure_and_never_runs(self):
        executor = FakeExecutor(write_dll=False)
        report, code, run_dir = inproc_run(executor, plan=SMOKE[:2],
                                           run_dir=Path(self.temp) / "run")
        self.assertEqual(code, R.EXIT_FAILED)
        self.assertNotIn("run", executor.stages())
        item = report["items"][0]
        self.assertEqual(item["status"], "FAIL")
        self.assertIn("DLL 不存在", item["note"])
        self.assertIsNone(item["dll"])

    def test_run_failure_stops_remaining(self):
        executor = FakeExecutor(run_exit=7)
        report, code, _ = inproc_run(executor, plan=SMOKE[:3], run_dir=Path(self.temp) / "run")
        self.assertEqual(code, R.EXIT_FAILED)
        self.assertEqual(report["items"][0]["status"], "FAIL")
        self.assertEqual(report["items"][0]["run"]["exit_code"], 7)
        for item in report["items"][1:]:
            self.assertEqual(item["status"], "NOT_RUN", item["name"])
        # 第 2 项的构建都不该发生：失败即停。
        built = [call for call in executor.calls if call["stage"] == "build"]
        self.assertEqual(len(built), 1)

    def test_build_timeout_is_nonzero_with_partial_report(self):
        executor = FakeExecutor(build_timeout=True)
        report, code, run_dir = inproc_run(executor, plan=SMOKE[:3],
                                           run_dir=Path(self.temp) / "run")
        self.assertEqual(code, R.EXIT_TIMEOUT)
        self.assertEqual(report["status"], "TIMEOUT")
        self.assertEqual(report["items"][0]["status"], "TIMEOUT")
        self.assertEqual(report["items"][0]["build"]["status"], "TIMEOUT")
        for item in report["items"][1:]:
            self.assertEqual(item["status"], "NOT_RUN")
        self.assertTrue((run_dir / "summary.json").is_file())

    def test_run_timeout_is_nonzero(self):
        executor = FakeExecutor(run_timeout=True)
        report, code, _ = inproc_run(executor, plan=SMOKE[:2], run_dir=Path(self.temp) / "run")
        self.assertEqual(code, R.EXIT_TIMEOUT)
        self.assertEqual(report["items"][0]["status"], "TIMEOUT")
        self.assertEqual(report["items"][0]["run"]["status"], "TIMEOUT")

    def test_cancel_marks_cancelled_and_keeps_partial_report(self):
        executor = FakeExecutor(interrupt_on="build")
        report, code, run_dir = inproc_run(executor, plan=SMOKE[:3],
                                           run_dir=Path(self.temp) / "run")
        self.assertEqual(code, R.EXIT_CANCELLED)
        self.assertEqual(len(report["items"]), 3, "取消不得把未执行项从报告中丢掉")
        self.assertEqual(report["items"][0]["status"], "CANCELLED")
        for item in report["items"][1:]:
            self.assertEqual(item["status"], "NOT_RUN")
        self.assertTrue((run_dir / "summary.json").is_file())
        self.assertEqual(report["stop_reason"], "CANCELLED")

    def test_empty_plan_is_rejected_without_fake_green(self):
        run_dir = Path(self.temp) / "run-empty"
        executor = FakeExecutor()
        report, code = R.run_plan([], REPO_ROOT, run_dir, 30, executor, "/usr/bin/dotnet",
                                  suite="smoke", run_id="empty", printer=lambda *a, **k: None)
        self.assertEqual(code, R.EXIT_PREFLIGHT)
        self.assertEqual(report["status"], "PREFLIGHT_FAILED")
        self.assertEqual(report["items"], [])
        self.assertEqual(executor.calls, [])
        blob = (run_dir / "summary.json").read_text(encoding="utf-8")
        self.assertIn("空选择", blob)
        self.assertNotIn('"status": "PASS"', blob)

    def test_executor_exception_is_reported_and_stops(self):
        class BoomExecutor(FakeExecutor):
            def run(self, argv, cwd, timeout_seconds, log_path):
                raise OSError("boom")

        report, code, run_dir = inproc_run(BoomExecutor(), plan=SMOKE[:2],
                                           run_dir=Path(self.temp) / "run-boom")
        self.assertEqual(code, R.EXIT_FAILED)
        self.assertEqual(report["items"][0]["status"], "FAIL")
        self.assertIn("执行器异常", report["items"][0]["note"])
        self.assertEqual(report["items"][1]["status"], "NOT_RUN")
        self.assertTrue((run_dir / "summary.json").is_file(),
                        "执行器意外也必须落盘部分报告")

    def test_report_contract_fields_and_paths(self):
        executor = FakeExecutor()
        report, code, run_dir = inproc_run(executor, plan=[SMOKE[0], SMOKE[12]],
                                           run_dir=Path(self.temp) / "run")
        self.assertEqual(code, R.EXIT_OK)
        self.assertTrue(report["code_only"])
        self.assertEqual(report["unity"]["status"], "NOT_RUN")
        self.assertIn("Unity", report["unity"]["reason"])
        self.assertEqual(report["runner"], "Tools/run_net_acceptance.py")
        self.assertEqual(report["suite"], "smoke")
        self.assertIsInstance(report["duration_seconds"], float)

        first = report["items"][0]
        self.assertEqual(first["kind"], "test")
        self.assertEqual(first["build"]["command"].split()[1], "build")
        self.assertEqual(first["build"]["exit_code"], 0)
        self.assertIsInstance(first["build"]["duration_seconds"], float)
        self.assertEqual(first["build"]["status"], "PASS")
        self.assertEqual(first["run"]["status"], "PASS")
        self.assertTrue(first["build"]["log"].startswith("logs/"))
        self.assertEqual(first["dll"], "out/PMDeclCheck/PMDeclCheck.dll")
        self.assertTrue((run_dir / first["dll"]).is_file())
        self.assertTrue(first["dll"].startswith("out/"), "产物必须在本次 run 目录内")

        build_only = report["items"][1]
        self.assertEqual(build_only["kind"], "build")
        self.assertEqual(build_only["status"], "PASS")
        self.assertEqual(build_only["run"]["status"], "N/A")

        markdown = (run_dir / "summary.md").read_text(encoding="utf-8")
        self.assertIn("CODE_ONLY", markdown)
        self.assertIn("NOT_RUN", markdown)
        self.assertIn("weaving", markdown)
        self.assertIn("PMDeclCheck", markdown)
        self.assertIn(report["run_id"], markdown)


# =====================================================================================
#  4. 预检
# =====================================================================================

class PreflightTests(unittest.TestCase):

    def test_missing_dotnet_is_reported(self):
        dotnet, problems = R.preflight(SMOKE, REPO_ROOT, which=lambda name: None)
        self.assertIsNone(dotnet)
        self.assertTrue(any("dotnet" in p for p in problems), problems)

    def test_missing_project_is_reported(self):
        ghost = R.Entry(name="Ghost", kind="test", tags=("build",), description="x",
                        project="Tools/Ghost/Ghost.csproj")
        _, problems = R.preflight([ghost], REPO_ROOT, which=lambda name: "/usr/bin/dotnet")
        self.assertTrue(any("工程不存在" in p for p in problems), problems)

    def test_empty_plan_is_reported(self):
        _, problems = R.preflight([], REPO_ROOT, which=lambda name: "/usr/bin/dotnet")
        self.assertTrue(any("空选择" in p for p in problems), problems)

    def test_main_preflight_failure_exits_nonzero_without_fake_green(self):
        temp = Path(tempfile.mkdtemp(prefix="pmnet-acceptance-preflight-"))
        self.addCleanup(shutil.rmtree, str(temp), True)
        stdout = io.StringIO()
        with mock.patch("shutil.which", return_value=None), \
                mock.patch.object(R, "BIN_ROOT", temp / "bin"), \
                mock.patch.object(R, "LOCK_PATH", temp / "bin" / "runner.lock"), \
                redirect_stdout(stdout):
            code = R.main(["--suite", "smoke"])
        self.assertEqual(code, R.EXIT_PREFLIGHT)
        runs = [p for p in (temp / "bin").iterdir() if p.is_dir()]
        self.assertEqual(len(runs), 1, runs)
        report = json.loads((runs[0] / "summary.json").read_text(encoding="utf-8"))
        self.assertEqual(report["status"], "PREFLIGHT_FAILED")
        self.assertTrue(any("dotnet" in n for n in report["notes"]), report["notes"])
        for item in report["items"]:
            self.assertEqual(item["status"], "NOT_RUN")
        self.assertFalse((temp / "bin" / "runner.lock").exists(), "预检失败后不得留下自己的锁")

    def test_usage_error_exit_code_is_six(self):
        with redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as ctx:
                R.parse_args(["--suite", "bogus"])
        self.assertEqual(ctx.exception.code, R.EXIT_USAGE)


# =====================================================================================
#  5. 独占锁
# =====================================================================================

class LockTests(unittest.TestCase):

    def setUp(self):
        self.temp = Path(tempfile.mkdtemp(prefix="pmnet-acceptance-lock-"))
        self.lock_path = self.temp / "bin" / "runner.lock"
        self.dummies = []

    def tearDown(self):
        for dummy in self.dummies:
            stop_process(dummy)
        shutil.rmtree(str(self.temp), ignore_errors=True)

    def test_incomplete_lock_is_not_stolen(self):
        self.lock_path.parent.mkdir(parents=True, exist_ok=True)
        self.lock_path.write_text("")
        with self.assertRaises(R.LockBusy):
            R.RepoLock(self.lock_path).acquire()
        self.assertEqual(self.lock_path.read_text(), "")

    def test_stale_lock_needs_manual_confirmation(self):
        self.lock_path.parent.mkdir(parents=True, exist_ok=True)
        payload = json.dumps({"pid": dead_pid(), "host": "other-machine"})
        self.lock_path.write_text(payload)
        with self.assertRaises(R.LockBusy):
            R.RepoLock(self.lock_path).acquire()
        self.assertEqual(self.lock_path.read_text(), payload)

    def test_live_lock_conflict_refuses_and_keeps_file(self):
        dummy = start_dummy()
        self.dummies.append(dummy)
        self.lock_path.parent.mkdir(parents=True, exist_ok=True)
        payload = json.dumps({"pid": dummy.pid, "host": "other", "started_at": "t"})
        self.lock_path.write_text(payload, encoding="utf-8")
        lock = R.RepoLock(self.lock_path, printer=lambda *a: None)
        with self.assertRaises(R.LockBusy):
            lock.acquire()
        self.assertTrue(self.lock_path.exists())
        self.assertEqual(self.lock_path.read_text(encoding="utf-8"), payload)
        self.assertIsNone(dummy.poll(), "禁止终止持有锁的他人进程")

    def test_release_does_not_remove_foreign_lock(self):
        self.lock_path.parent.mkdir(parents=True, exist_ok=True)
        self.lock_path.write_text(json.dumps({"pid": 4242, "host": "other"}), encoding="utf-8")
        lock = R.RepoLock(self.lock_path, printer=lambda *a: None)
        lock._acquired = True  # 模拟「被别人抢占后仍在收尾」的糟糕时序
        lock.release()
        self.assertTrue(self.lock_path.exists(), "不得删除不属于自己的锁")


# =====================================================================================
#  6. 真实子进程执行器（超时只清理自己的进程树）
# =====================================================================================

class SubprocessExecutorTests(unittest.TestCase):

    def setUp(self):
        self.temp = Path(tempfile.mkdtemp(prefix="pmnet-acceptance-exec-"))
        self.dummies = []

    def tearDown(self):
        for dummy in self.dummies:
            stop_process(dummy)
        shutil.rmtree(str(self.temp), ignore_errors=True)

    def test_captures_nonzero_exit_and_full_log(self):
        executor = R.SubprocessExecutor()
        log = self.temp / "logs" / "exit.log"
        result = executor.run([sys.executable, "-c",
                               "print('hello'); import sys; sys.exit(3)"],
                              self.temp, 60, log)
        self.assertEqual(result.exit_code, 3)
        self.assertFalse(result.timed_out)
        text = log.read_text(encoding="utf-8", errors="replace")
        self.assertIn("hello", text)
        self.assertIn("exit=3", text)

    def test_timeout_kills_own_tree_and_leaves_other_services_alone(self):
        dummy = start_dummy()
        self.dummies.append(dummy)
        pid_file = self.temp / "child.pid"
        executor = R.SubprocessExecutor()
        log = self.temp / "logs" / "timeout.log"
        script = ("import os, time, subprocess, sys; "
                  "child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)']); "
                  "open(r'%s', 'w').write(str(os.getpid()) + ',' + str(child.pid)); time.sleep(120)"
                  % pid_file)
        result = executor.run([sys.executable, "-c", script], self.temp, 3, log)
        self.assertTrue(result.timed_out)
        self.assertNotEqual(result.exit_code, 0)
        self.assertTrue(pid_file.is_file(), "子进程应已启动")
        child_pids = [int(value) for value in pid_file.read_text().strip().split(',')]
        self.assertEqual(len(child_pids), 2, "必须验证子进程及其后代，不只看父进程")
        deadline = time.time() + 20
        while time.time() < deadline and any(pid_alive(pid) for pid in child_pids):
            time.sleep(0.2)
        self.assertFalse(any(pid_alive(pid) for pid in child_pids), "超时必须清理自己启动的进程树")
        self.assertIsNone(dummy.poll(), "禁止终止与本 runner 无关的进程")
        self.assertIn("timed_out=True", log.read_text(encoding="utf-8", errors="replace"))


# =====================================================================================
#  6b. 脚本阶段（full 末项 SDK 沙盒）与沙盒日志副本收集
# =====================================================================================

class ScriptStageTests(unittest.TestCase):
    """用临时仓库验证 `kind=script` 的构建/运行状态、失败传播与副本收集（不跑真 SDK 沙盒）。"""

    def setUp(self):
        self.temp = Path(tempfile.mkdtemp(prefix="pmnet-acceptance-script-"))
        self.repo = self.temp / "repo with space"
        (self.repo / "Tools").mkdir(parents=True)
        (self.repo / "Tools" / "fake_pipeline.py").write_text("print('fake')\n", encoding="utf-8")
        self.run_dir = self.temp / "run"
        self.entry = R.Entry(name="fake_pipeline", kind="script", tags=("build", "weaving"),
                            description="x", script=("Tools/fake_pipeline.py",),
                            collect_sandbox_logs=True)

    def tearDown(self):
        shutil.rmtree(str(self.temp), ignore_errors=True)

    def _run(self, executor):
        return R.run_plan([self.entry], self.repo, self.run_dir, 30, executor,
                          "/usr/bin/dotnet", suite="full", run_id="script",
                          printer=lambda *a, **k: None)

    def test_script_pass_and_sandbox_log_copy(self):
        fresh = self.repo / "Tools" / "rpc-build-sandbox-3.log"
        fresh.write_text("fresh\n", encoding="utf-8")
        stale = self.repo / "Tools" / "rpc-build-sandbox-1.log"
        stale.write_text("stale\n", encoding="utf-8")
        old = time.time() - 3600
        os.utime(str(stale), (old, old))

        report, code = self._run(FakeExecutor())
        self.assertEqual(code, R.EXIT_OK)
        item = report["items"][0]
        self.assertEqual(item["status"], "PASS")
        self.assertEqual(item["build"]["status"], "N/A")
        self.assertEqual(item["run"]["status"], "PASS")
        self.assertEqual(item["sandbox_logs"], ["logs/sdk-sandbox/rpc-build-sandbox-3.log"])
        self.assertIn("不伪造完全隔离", item["note"])
        self.assertTrue((self.run_dir / "logs" / "sdk-sandbox" / "rpc-build-sandbox-3.log").is_file())
        self.assertFalse((self.run_dir / "logs" / "sdk-sandbox" / "rpc-build-sandbox-1.log").exists())
        # 原文件不被改写：只是收集副本
        self.assertEqual(fresh.read_text(encoding="utf-8"), "fresh\n")

    def test_script_failure_stops(self):
        report, code = self._run(FakeExecutor(run_exit=9))
        self.assertEqual(code, R.EXIT_FAILED)
        self.assertEqual(report["items"][0]["status"], "FAIL")
        self.assertEqual(report["items"][0]["run"]["exit_code"], 9)

    def test_script_timeout(self):
        report, code = self._run(FakeExecutor(run_timeout=True))
        self.assertEqual(code, R.EXIT_TIMEOUT)
        self.assertEqual(report["items"][0]["status"], "TIMEOUT")

    def test_run_id_is_unique_within_same_second(self):
        ids = {R.make_run_id() for _ in range(50)}
        self.assertEqual(len(ids), 50)


# =====================================================================================
#  7. 端到端：真实 dotnet + 临时仓库 + 真实 runner 子进程
# =====================================================================================

class EndToEndTests(unittest.TestCase):
    """真实构建几个一秒钟的迷你工程，端到端验证「失败不跑旧产物 / 失败即停」。"""

    def setUp(self):
        self.sandbox = Sandbox()
        self.sandboxes = [self.sandbox]
        self.marker = self.sandbox.root / "run-marker.txt"

    def tearDown(self):
        for sandbox in self.sandboxes:
            sandbox.cleanup()

    def _env(self, exit_code=0):
        return {"PMTEST_MARKER": str(self.marker), "PMTEST_EXIT": str(exit_code)}

    def test_build_failure_stops_remaining_and_never_runs_old_output(self):
        self.sandbox.write_console_project("PMDeclCheck")
        self.sandbox.write_broken_project("PMReplicationTest")
        proc = self.sandbox.run_runner(["--suite", "smoke", "--timeout-seconds", "300"],
                                       env_extra=self._env())
        self.assertNotEqual(proc.returncode, 0, proc.stdout)
        report, run_dir = self.sandbox.summary_of_single_run()
        self.assertEqual(proc.returncode, 1)
        self.assertTrue(report["code_only"])
        self.assertEqual(report["unity"]["status"], "NOT_RUN")

        first, second = report["items"][0], report["items"][1]
        self.assertEqual(first["name"], "PMDeclCheck")
        self.assertEqual(first["status"], "PASS")
        self.assertEqual(second["name"], "PMReplicationTest")
        self.assertEqual(second["build"]["status"], "FAIL")
        self.assertEqual(second["run"]["status"], "N/A")
        self.assertIsNone(second["dll"])
        self.assertFalse((run_dir / "logs" / "PMReplicationTest.run.log").exists())
        for item in report["items"][2:]:
            self.assertEqual(item["status"], "NOT_RUN", item["name"])

        # 只有第一项真的运行过：标记文件恰好一行。
        self.assertTrue(self.marker.is_file())
        self.assertEqual(self.marker.read_text(encoding="utf-8").count("run"), 1)

        self.assertFalse(self.sandbox.lock_path().exists(), "运行结束后必须释放自己的锁")

    def test_run_failure_stops_remaining(self):
        self.sandbox.write_console_project("PMDeclCheck")
        self.sandbox.write_console_project("PMReplicationTest")
        proc = self.sandbox.run_runner(["--suite", "smoke", "--timeout-seconds", "300"],
                                       env_extra=self._env(exit_code=5))
        self.assertEqual(proc.returncode, 1, proc.stdout)
        report, _ = self.sandbox.summary_of_single_run()
        self.assertEqual(report["items"][0]["status"], "FAIL")
        self.assertEqual(report["items"][0]["run"]["exit_code"], 5)
        for item in report["items"][1:]:
            self.assertEqual(item["status"], "NOT_RUN", item["name"])
        self.assertEqual(self.marker.read_text(encoding="utf-8").count("run"), 1)

    def test_build_success_without_dll_is_failure(self):
        self.sandbox.write_dll_deleting_project("PMDeclCheck")
        proc = self.sandbox.run_runner(["--suite", "smoke", "--timeout-seconds", "300"],
                                       env_extra=self._env())
        self.assertEqual(proc.returncode, 1, proc.stdout)
        report, run_dir = self.sandbox.summary_of_single_run()
        first = report["items"][0]
        self.assertEqual(first["build"]["status"], "PASS")
        self.assertEqual(first["status"], "FAIL")
        self.assertIn("DLL 不存在", first["note"])
        self.assertEqual(first["run"]["status"], "N/A")
        self.assertFalse((run_dir / "logs" / "PMDeclCheck.run.log").exists())
        self.assertFalse(self.marker.exists(), "产物缺失时绝不能回退执行任何旧产物")
        for item in report["items"][1:]:
            self.assertEqual(item["status"], "NOT_RUN")

    def test_list_needs_no_dotnet_and_writes_nothing(self):
        proc = self.sandbox.run_runner(["--list"], extra_path=str(self.sandbox.empty_path_dir),
                                       cwd=self.sandbox.root.parent)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertIn("suite=smoke", proc.stdout)
        self.assertIn("NOT_RUN", proc.stdout)
        self.assertIn("不代表任何项已通过", proc.stdout)
        self.assertNotIn("PASS", proc.stdout)
        self.assertFalse((self.sandbox.root / "Tools" / "NetAcceptance").exists(),
                         "--list 不得产生任何副作用")

    def test_missing_dotnet_preflight_is_nonzero_with_honest_report(self):
        proc = self.sandbox.run_runner(["--suite", "smoke"],
                                       extra_path=str(self.sandbox.empty_path_dir))
        self.assertEqual(proc.returncode, R.EXIT_PREFLIGHT, proc.stdout)
        report, _ = self.sandbox.summary_of_single_run()
        self.assertEqual(report["status"], "PREFLIGHT_FAILED")
        for item in report["items"]:
            self.assertEqual(item["status"], "NOT_RUN")
        self.assertFalse(self.sandbox.lock_path().exists())

    def test_lock_conflict_refuses_and_does_not_touch_other_service(self):
        dummy = start_dummy()
        self.addCleanup(stop_process, dummy)
        lock_path = self.sandbox.lock_path()
        lock_path.parent.mkdir(parents=True, exist_ok=True)
        payload = json.dumps({"pid": dummy.pid, "host": "other", "started_at": "t"})
        lock_path.write_text(payload, encoding="utf-8")
        proc = self.sandbox.run_runner(["--suite", "smoke"],
                                       extra_path=str(self.sandbox.empty_path_dir))
        self.assertEqual(proc.returncode, R.EXIT_LOCKED, proc.stdout)
        self.assertIn("lock", proc.stdout.lower())
        self.assertEqual(lock_path.read_text(encoding="utf-8"), payload)
        self.assertIsNone(dummy.poll(), "禁止终止持有锁的他人进程")
        self.assertEqual(self.sandbox.run_dirs(), [], "锁冲突时不应创建本轮 run 目录")

    def test_stale_lock_requires_manual_cleanup_before_next_run(self):
        lock_path = self.sandbox.lock_path()
        lock_path.parent.mkdir(parents=True, exist_ok=True)
        payload = json.dumps({"pid": dead_pid(), "host": "host"})
        lock_path.write_text(payload, encoding="utf-8")
        proc = self.sandbox.run_runner(["--list"])
        self.assertEqual(proc.returncode, 0, proc.stderr)
        proc = self.sandbox.run_runner(["--suite", "smoke"],
                                       extra_path=str(self.sandbox.empty_path_dir))
        self.assertEqual(proc.returncode, R.EXIT_LOCKED, proc.stdout)
        self.assertEqual(lock_path.read_text(encoding="utf-8"), payload)
        lock_path.unlink()  # 仅沙盒：人工已确认该测试PID结束，模拟明确清理。
        proc = self.sandbox.run_runner(["--suite", "smoke"],
                                       extra_path=str(self.sandbox.empty_path_dir))
        self.assertEqual(proc.returncode, R.EXIT_PREFLIGHT, proc.stdout)
        self.assertFalse(lock_path.exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
