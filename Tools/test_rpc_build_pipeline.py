"""Test the real RPC generation/build/weave pipeline in a disposable repository subset."""
from pathlib import Path
import hashlib
import shutil
import subprocess
import tempfile
import sys

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    passed = 0
    with tempfile.TemporaryDirectory(prefix="pmnet-build-", ignore_cleanup_errors=True) as temp:
        repo = Path(temp)
        for folder in ["Tools/PMNetGen", "Tools/PMDeclModel", "Tools/PMNetWeaver",
                       "Tools/PMR3RuntimeTest", "Client/Assets/Scripts/PMNet",
                       "Client/Assets/Scripts/PMR3"]:
            shutil.copytree(ROOT / folder, repo / folder,
                            ignore=shutil.ignore_patterns("bin", "obj", "editor-tool", "*.log"))
        for name in ["Directory.Build.targets", "Docs/plans/pmnet-r3-ids.json",
                     "Client/Assets/Scripts/Server/Boot/PMDsLobbyAgent.cs"]:
            target = repo / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / name, target)
        project = repo / "Tools/PMR3RuntimeTest/PMR3RuntimeTest.csproj"
        output = repo / "out"
        generated = repo / "Client/Assets/Scripts/PMR3/Generated"
        player_gen = generated / "PMNet.PMNet.R3.PMR3Player.g.cs"
        player = repo / "Client/Assets/Scripts/PMR3/PMR3Player.cs"
        id_lock = repo / "Docs/plans/pmnet-r3-ids.json"
        lock_before = digest(id_lock)
        dll = output / "PMR3RuntimeTest.dll"
        pdb = output / "PMR3RuntimeTest.pdb"
        build_index = 0

        def build(ok=True):
            nonlocal build_index
            build_index += 1
            result = subprocess.run(["dotnet", "build", str(project), "-c", "Release",
                                     "-o", str(output), "-v", "minimal", "-nologo"],
                                    cwd=repo, capture_output=True, timeout=120)
            text = (result.stdout + result.stderr).decode("utf-8", errors="replace")
            (ROOT / "Tools" / ("rpc-build-sandbox-%d.log" % build_index)).write_text(text, encoding="utf-8")
            if (result.returncode == 0) != ok:
                raise AssertionError("build %d exit %d\n%s" % (build_index, result.returncode, text[-6000:]))
            return text

        def verify(expected_count):
            weaver = repo / "Tools/PMNetWeaver/bin/Release/net8.0/PMNetWeaver.dll"
            result = subprocess.run(["dotnet", str(weaver), "--check", str(dll), "--require-rpcs"],
                                    cwd=repo, capture_output=True, timeout=30)
            text = (result.stdout + result.stderr).decode("utf-8", errors="replace")
            if result.returncode:
                raise AssertionError(text)
            # The CLI prints the actual inspected count, not a fixture constant.
            import re
            counts = re.findall(r"RPC\s*方法数\s*[:：]\s*(\d+)", text)
            assert counts and int(counts[0]) == expected_count, text

        # No generated sources at evaluation time: gen must run and refresh Compile items.
        for source in generated.glob("*.g.cs"):
            source.unlink()
        build()
        verify(13)
        assert digest(id_lock) == lock_before
        passed += 1
        print("PASS cold checkout: restore tools + generate + compile + weave + check")

        before = (digest(dll), digest(pdb), digest(player_gen), digest(id_lock))
        build()
        verify(13)
        assert before == (digest(dll), digest(pdb), digest(player_gen), digest(id_lock))
        passed += 1
        print("PASS incremental build: DLL/PDB/generated/IDs byte-identical")

        text = player.read_text(encoding="utf-8-sig")
        needle = "Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate"
        assert needle in text
        player.write_text(text.replace(needle, "Reliability = PMRpcReliability.Unreliable, Validator = PMRpcValidator.ForceValidate", 1), encoding="utf-8-sig")
        before = digest(player_gen)
        build()
        verify(13)
        assert digest(player_gen) != before and digest(id_lock) == lock_before
        passed += 1
        print("PASS attribute-only change: stale metadata regenerated without ID drift")

        marker = "public partial class PMR3Player : PMNetObject\n    {"
        text = player.read_text(encoding="utf-8-sig")
        assert text.count(marker) == 1
        text = text.replace(marker, marker + "\n        [PMClientRpc] public void AddedAfterFirstBuild(int n) { ProbeSentCount += n; }", 1)
        player.write_text(text, encoding="utf-8-sig")
        build()
        verify(14)
        assert "PMGeneratedRpcId_AddedAfterFirstBuild" in player_gen.read_text(encoding="utf-8-sig")
        passed += 1
        print("PASS new ordinary RPC automatically generated and woven")

        extra = repo / "Client/Assets/Scripts/PMR3/NewRpcObject.cs"
        extra.write_text("using PMNet; namespace PMNet.R3 { [PMNetworkObject] public partial class NewRpcObject : PMNetObject { public int Calls; [PMClientRpc] public void Ping(int n) { Calls += n; } } }", encoding="utf-8")
        xml = project.read_text(encoding="utf-8-sig")
        xml = xml.replace('<Compile Include="Program.cs" />', '<Compile Include="Program.cs" /><Compile Include="../../Client/Assets/Scripts/PMR3/NewRpcObject.cs" />', 1)
        project.write_text(xml, encoding="utf-8-sig")
        build()
        verify(15)
        assert (generated / "PMNet.PMNet.R3.NewRpcObject.g.cs").exists()
        passed += 1
        print("PASS newly generated class file included in this same compilation")

        before = {p.name: digest(p) for p in generated.glob("*.g.cs")}
        lock_before = digest(id_lock)
        player.write_text(text.replace("public void AddedAfterFirstBuild", "public virtual void AddedAfterFirstBuild"), encoding="utf-8-sig")
        result = build(ok=False)
        assert "virtual" in result
        assert before == {p.name: digest(p) for p in generated.glob("*.g.cs")}
        assert digest(id_lock) == lock_before
        passed += 1
        print("PASS unsupported declaration fails build without overwriting generated/ID files")
    print("RPC build pipeline: %d passed, 0 failed" % passed)
    return 0


if __name__ == "__main__":
    sys.exit(main())
