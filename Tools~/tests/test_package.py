"""Tests for the packaging layer and the tool's warm and cold paths, without Unity.

Run from the package root: python3 -m unittest discover -s 'Tools~/tests'
"""

from __future__ import annotations

import fcntl
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest
from importlib.machinery import SourceFileLoader
from pathlib import Path


PACKAGE_ROOT = Path(__file__).resolve().parents[2]
TOOLS = PACKAGE_ROOT / "Tools~"
PACKAGE_JSON = json.loads((PACKAGE_ROOT / "package.json").read_text())
PACKAGE = PACKAGE_JSON["name"]
PIPELINE = PACKAGE_JSON["dependencies"]["com.unity.pipeline"]

# Stands in for Tools~/unity-verify: reports how it was called.
STUB_TOOL = textwrap.dedent(
    """\
    #!/usr/bin/env python3
    import json, sys
    print(json.dumps({"tool": __file__, "argv": sys.argv[1:]}))
    raise SystemExit(7)
    """
)

# Stands in for the Unity CLI: answers run_script, in the open Editor or in a
# headless one (run --command run_script), with the next reply in
# $FAKE_UNITY_REPLIES (a JSON list, consumed in order) and logs each command.
# A reply's "log" is written to the headless Editor's --log-file, and a
# reply without "pipeline" reports the version package.json depends on.
FAKE_UNITY = textwrap.dedent(
    """\
    #!/usr/bin/env python3
    import json, os, sys
    args = sys.argv[1:]
    if args == ["--version"]:
        print("1.0.0-beta.12")
        raise SystemExit(0)
    name = args[args.index("command") + 5] if "command" in args else args[5]
    with open(os.environ["FAKE_UNITY_LOG"], "a") as log:
        log.write(name + "\\n")
    if name in ("run_script", "run"):
        path = os.environ["FAKE_UNITY_REPLIES"]
        replies = json.load(open(path))
        reply = replies.pop(0)
        json.dump(replies, open(path, "w"))
        if "log" in reply:
            open(args[args.index("--log-file") + 1], "w").write(reply["log"])
        if "error" in reply:
            print(json.dumps({"success": False, "errors": [{"message": reply["error"]}]}))
            raise SystemExit(1)
        reply.setdefault("pipeline", os.environ["FAKE_UNITY_PIPELINE"])
        print(json.dumps({"success": True, "data": {"result": {"success": True, "result": reply}}}))
    elif name == "editor_stop":
        print(json.dumps({"success": True, "data": {"result": "Exited play mode"}}))
    else:
        print(json.dumps({"success": False, "errors": [{"message": "no Editor"}]}))
        raise SystemExit(1)
    """
)


def make_project(root: Path, dependency: str | None = None) -> Path:
    (root / "Assets").mkdir(parents=True)
    (root / "ProjectSettings").mkdir()
    (root / "ProjectSettings" / "ProjectVersion.txt").write_text("m_EditorVersion: 6000.3.10f1\n")
    (root / "Packages").mkdir()
    dependencies = {"com.unity.test-framework": "1.6.0"}
    if dependency:
        dependencies[PACKAGE] = dependency
    (root / "Packages" / "manifest.json").write_text(
        json.dumps({"dependencies": dependencies}, indent=2) + "\n"
    )
    return root


def make_stub_package(root: Path) -> Path:
    (root / "Tools~").mkdir(parents=True)
    tool = root / "Tools~" / "unity-verify"
    tool.write_text(STUB_TOOL)
    tool.chmod(0o755)
    return root


def install_shim(project: Path) -> Path:
    shim = project / "unity-verify"
    shutil.copy(TOOLS / "project-shim", shim)
    return shim


def git(*arguments: str, cwd: Path) -> str:
    return subprocess.run(
        ["git", "-c", "user.name=t", "-c", "user.email=t@t", "-c", "init.defaultBranch=main", *arguments],
        cwd=cwd, check=True, capture_output=True, text=True,
    ).stdout.strip()


class TempDirTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = Path(tempfile.mkdtemp(prefix="unity-verify-test-")).resolve()
        self.addCleanup(shutil.rmtree, self.temp, ignore_errors=True)
        self.environment = {**os.environ, "XDG_CACHE_HOME": str(self.temp / "cache")}

    def run_shim(self, shim: Path, *arguments: str, **environment: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(shim), *arguments],
            cwd=self.temp, env={**self.environment, **environment},
            capture_output=True, text=True, timeout=60,
        )

    def assert_ran(self, completed: subprocess.CompletedProcess[str], package: Path, project: Path) -> None:
        self.assertEqual(completed.returncode, 7, completed.stderr)
        call = json.loads(completed.stdout)
        self.assertEqual(Path(call["tool"]), package.resolve() / "Tools~" / "unity-verify")
        self.assertEqual(call["argv"], ["--project-path", str(project), "Board"])


class ShimTests(TempDirTestCase):
    def test_runs_package_from_package_cache(self) -> None:
        project = make_project(self.temp / "game", f"https://example.invalid/x.git#v1")
        package = make_stub_package(project / "Library" / "PackageCache" / f"{PACKAGE}@4ee62b097584")
        self.assert_ran(self.run_shim(install_shim(project), "Board"), package, project)

    def test_missing_dependency_is_reported(self) -> None:
        completed = self.run_shim(install_shim(make_project(self.temp / "game")))
        self.assertEqual(completed.returncode, 3)
        self.assertIn("ERR package_missing", completed.stderr)

    def test_fetches_pinned_tag_when_nothing_is_resolved(self) -> None:
        source = make_stub_package(self.temp / "source")
        # The real .gitignore, which must keep Tools~ despite global "*~" ignores.
        shutil.copy(PACKAGE_ROOT / ".gitignore", source / ".gitignore")
        git("init", cwd=source)
        git("add", "-A", cwd=source)
        git("commit", "-m", "v1", cwd=source)
        git("tag", "v1", cwd=source)
        (source / "Tools~" / "unity-verify").write_text(STUB_TOOL.replace("7", "8"))
        git("commit", "-am", "after v1", cwd=source)

        project = make_project(self.temp / "game", f"file://{source}#v1")
        completed = self.run_shim(install_shim(project), "Board")
        cached = self.temp / "cache" / "unity-verify" / "v1"
        self.assert_ran(completed, cached, project)  # exit 7: the tag, not the branch tip

        shutil.rmtree(source)  # a second run must not need the network
        self.assert_ran(self.run_shim(project / "unity-verify", "Board"), cached, project)


class ToolTests(TempDirTestCase):
    def run_tool(self, *arguments: str, cwd: Path) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(TOOLS / "unity-verify"), *arguments],
            cwd=cwd, env=self.environment, capture_output=True, text=True, timeout=60,
        )

    def test_reports_missing_project(self) -> None:
        completed = self.run_tool(cwd=self.temp)
        self.assertEqual(completed.returncode, 3)
        self.assertIn("ERR project_missing", completed.stderr)

    def load_tool(self):
        loader = SourceFileLoader("unity_verify", str(TOOLS / "unity-verify"))
        tool = importlib.util.module_from_spec(importlib.util.spec_from_loader(loader.name, loader))
        sys.modules[loader.name] = tool  # dataclasses look their module up here
        self.addCleanup(sys.modules.pop, loader.name, None)
        loader.exec_module(tool)
        return tool

    def test_finds_project_above_working_directory(self) -> None:
        project = make_project(self.temp / "game")
        tool = self.load_tool()
        self.assertEqual(tool.find_project_root(project / "Assets"), project)
        self.assertIsNone(tool.find_project_root(self.temp))

    def test_requires_the_pipeline_version_the_package_depends_on(self) -> None:
        self.assertEqual(self.load_tool().REQUIRED_PIPELINE_VERSION, PIPELINE)

    def test_rejects_unknown_arguments_with_tooling_exit(self) -> None:
        completed = self.run_tool("--nope", cwd=make_project(self.temp / "game"))
        self.assertEqual(completed.returncode, 3)
        self.assertIn("ERR args", completed.stderr)


def test_result(name: str, status: str = "Passed", duration: float = 0.01) -> dict:
    result = {"FullName": name, "Status": status, "Duration": duration, "Message": "", "StackTrace": ""}
    if status == "Failed":
        result["Message"] = "Expected: 1\n  But was:  2"
        result["StackTrace"] = f"at {name} () [0x0] in /p/Assets/Tests/BoardTests.cs:12"
    return result


def tests_reply(*results: dict, frame_tests: int = 0) -> dict:
    statuses = [result["Status"] for result in results]
    return {
        "state": "tests",
        "Summary": {
            "Total": len(results),
            **{status: statuses.count(status) for status in ("Passed", "Failed", "Skipped", "Inconclusive")},
        },
        "Results": list(results),
        "Duration": 0.5,
        "Mode": "EditMode",
        "FrameTests": frame_tests,
        "FrameDuration": 4.0 if frame_tests else 0,
    }


class FakeUnityTestCase(TempDirTestCase):
    def run_fake(self, *replies: dict, editor_open: bool) -> tuple[subprocess.CompletedProcess[str], list[str]]:
        bin_directory = self.temp / "bin"
        bin_directory.mkdir()
        (bin_directory / "unity").write_text(FAKE_UNITY)
        (bin_directory / "unity").chmod(0o755)
        (self.temp / "replies.json").write_text(json.dumps(list(replies)))
        project = make_project(self.temp / "game")
        (project / "Temp").mkdir()
        with open(project / "Temp" / "UnityLockfile", "w") as lock:
            if editor_open:
                fcntl.flock(lock, fcntl.LOCK_EX)  # what an Editor with the project open holds
            completed = subprocess.run(
                [sys.executable, str(TOOLS / "unity-verify"), "--project-path", str(project)],
                env={
                    **self.environment,
                    "PATH": f"{bin_directory}{os.pathsep}{os.environ['PATH']}",
                    "FAKE_UNITY_REPLIES": str(self.temp / "replies.json"),
                    "FAKE_UNITY_LOG": str(self.temp / "calls.log"),
                    "FAKE_UNITY_PIPELINE": PIPELINE,
                },
                capture_output=True, text=True, timeout=60,
            )
        return completed, (self.temp / "calls.log").read_text().split()


class WarmPathTests(FakeUnityTestCase):
    def run_warm(self, *replies: dict) -> tuple[subprocess.CompletedProcess[str], list[str]]:
        return self.run_fake(*replies, editor_open=True)

    def test_waits_through_compile_and_reload_then_reports_failures(self) -> None:
        completed, calls = self.run_warm(
            {"state": "busy"},
            {"error": "Network error: connection reset"},
            tests_reply(test_result("BoardTests.Passes"), test_result("BoardTests.Fails", "Failed")),
        )
        self.assertEqual(completed.returncode, 1, completed.stdout)
        self.assertEqual(calls, ["run_script"] * 3)
        lines = completed.stdout.splitlines()
        self.assertTrue(lines[0].startswith("FAIL tests; total=2 passed=1 failed=1 "), lines)
        self.assertEqual(lines[1:], [
            "[1] BoardTests.Fails", "  Expected: 1", "  But was:  2", "  at /p/Assets/Tests/BoardTests.cs:12",
        ])

    def test_stops_play_mode_once_and_notes_frame_tests(self) -> None:
        completed, calls = self.run_warm(
            {"state": "playing"},
            {"state": "playing"},
            tests_reply(test_result("BoardTests.A"), test_result("BoardTests.B"), frame_tests=1),
        )
        self.assertEqual(completed.returncode, 0, completed.stdout)
        self.assertEqual(calls, ["run_script", "editor_stop", "run_script", "run_script"])
        self.assertTrue(completed.stdout.startswith("PASS tests; total=2 passed=2 failed=0 "))
        self.assertIn("NOTE frame_tests; count=1; duration=4.00s;", completed.stdout)

    def test_fails_passed_tests_over_one_second_and_warns_over_200ms(self) -> None:
        completed, _ = self.run_warm(tests_reply(
            test_result("BoardTests.Quick", duration=0.2),
            test_result("BoardTests.Slow", duration=0.35),
            test_result("BoardTests.Fails", "Failed", duration=1.2),
            test_result("BoardTests.Slower", duration=1.0),
            test_result("BoardTests.TooSlow", duration=1.5),
        ))
        self.assertEqual(completed.returncode, 1, completed.stdout)
        lines = completed.stdout.splitlines()
        self.assertTrue(lines[0].startswith("FAIL tests; total=5 passed=3 failed=2 too_slow=1 "), lines)
        expectation = "tests fail over 1s and are expected to finish in under 50ms"
        self.assertEqual(lines[1:], [
            "[1] BoardTests.Fails", "  Expected: 1", "  But was:  2", "  at /p/Assets/Tests/BoardTests.cs:12",
            "[2] BoardTests.TooSlow", f"  Took 1.5s; {expectation}.",
            f"WARN slow_tests; count=2; over=200ms; hint={expectation}",
            "  1s BoardTests.Slower", "  350ms BoardTests.Slow",
        ])

    def test_slow_tests_alone_still_pass(self) -> None:
        completed, _ = self.run_warm(tests_reply(test_result("BoardTests.Slow", duration=0.25)))
        self.assertEqual(completed.returncode, 0, completed.stdout)
        self.assertEqual(completed.stdout.splitlines()[1:], [
            "WARN slow_tests; count=1; over=200ms; "
            "hint=tests fail over 1s and are expected to finish in under 50ms",
            "  250ms BoardTests.Slow",
        ])

    def test_reports_compile_errors(self) -> None:
        completed, _ = self.run_warm({
            "state": "compile_failed",
            "diagnostics": [{"message": "Assets/A.cs(3,5): error CS1002: ; expected", "file": "", "line": 0, "column": 0}],
        })
        self.assertEqual(completed.returncode, 2)
        self.assertEqual(completed.stdout.splitlines(), [
            "ERR compile_failed; errors=1; tests=skipped", "Assets/A.cs:3:5 error CS1002: ; expected",
        ])

    def test_refuses_an_older_pipeline(self) -> None:
        completed, calls = self.run_warm({"state": "busy", "pipeline": "0.5.0-exp.1"})
        self.assertEqual(completed.returncode, 3)
        self.assertEqual(calls, ["run_script"])
        self.assertIn(f"ERR pipeline_version; got=0.5.0-exp.1; min={PIPELINE};", completed.stdout)

    def test_reports_a_failing_first_call_without_retrying(self) -> None:
        completed, calls = self.run_warm({"error": "run_script is disabled"})
        self.assertEqual(completed.returncode, 3)
        self.assertEqual(calls, ["run_script"])
        self.assertIn("ERR unity_command; stage=warm; cause=run_script is disabled;", completed.stdout)

    def test_falls_back_to_a_headless_editor_without_a_warm_one(self) -> None:
        completed, calls = self.run_warm(
            {"error": "No Pipeline instance found for project"}, tests_reply(test_result("BoardTests.A"))
        )
        self.assertEqual(calls, ["run_script", "run"])
        self.assertEqual(completed.returncode, 0, completed.stdout)
        self.assertTrue(completed.stdout.startswith("PASS tests; total=1 passed=1 failed=0 "))


class ColdPathTests(FakeUnityTestCase):
    def run_cold(self, *replies: dict) -> tuple[subprocess.CompletedProcess[str], list[str]]:
        return self.run_fake(*replies, editor_open=False)

    def test_runs_one_headless_editor_when_no_editor_has_the_project_open(self) -> None:
        completed, calls = self.run_cold(
            tests_reply(test_result("BoardTests.Passes"), test_result("BoardTests.Fails", "Failed"))
        )
        self.assertEqual(calls, ["run"])
        self.assertEqual(completed.returncode, 1, completed.stdout)
        self.assertTrue(completed.stdout.startswith("FAIL tests; total=2 passed=1 failed=1 "))

    def test_reports_compile_errors_from_the_editor_log(self) -> None:
        error = "Assets/A.cs(3,5): error CS1002: ; expected"
        completed, _ = self.run_cold({
            "error": "Unity exited with code 1 before the Pipeline server became ready.",
            "log": f"{error}\n##utp:{json.dumps({'type': 'Compiler', 'message': error})}\n"
                   "Aborting batchmode due to failure:\nScripts have compiler errors.\n",
        })
        self.assertEqual(completed.returncode, 2, completed.stdout)
        self.assertEqual(completed.stdout.splitlines(), [
            "ERR compile_failed; errors=1; tests=skipped", "Assets/A.cs:3:5 error CS1002: ; expected",
        ])

    def test_reports_why_batch_mode_stopped(self) -> None:
        completed, _ = self.run_cold({
            "error": "Unity exited with code 1 before the Pipeline server became ready.",
            "log": "Aborting batchmode due to failure:\nNo valid Unity Editor license found.\n",
        })
        self.assertEqual(completed.returncode, 3)
        self.assertIn("ERR unity_command; stage=cold; cause=No valid Unity Editor license found.;", completed.stdout)


class InstallerTests(TempDirTestCase):
    def run_install(self, project: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(TOOLS / "install"), "--project-path", str(project), *arguments],
            env=self.environment, capture_output=True, text=True, timeout=60,
        )

    def test_adds_dependency_shim_and_prints_snippet(self) -> None:
        project = make_project(self.temp / "game")
        completed = self.run_install(project, "--version", "v9.9.9")
        self.assertEqual(completed.returncode, 0, completed.stderr)
        manifest = json.loads((project / "Packages" / "manifest.json").read_text())
        self.assertEqual(
            list(manifest["dependencies"]),
            sorted([PACKAGE, "com.unity.test-framework"]),
        )
        self.assertTrue(manifest["dependencies"][PACKAGE].endswith("unity-verify.git#v9.9.9"))
        self.assertTrue(os.access(project / "unity-verify", os.X_OK))
        self.assertIn("## Unity verification", completed.stdout)

    def test_preserves_manifest_formatting(self) -> None:
        for indent in (2, 4):
            with self.subTest(indent=indent):
                project = make_project(self.temp / f"game{indent}")
                manifest = project / "Packages" / "manifest.json"
                original = json.dumps(
                    {"dependencies": {"com.adjust.sdk": "1", "com.unity.ugui": "2"}, "testables": []},
                    indent=indent,
                ) + "\n"
                manifest.write_text(original)
                self.assertEqual(self.run_install(project).returncode, 0)
                # Ignore trailing commas: appending after the last entry adds one to it.
                before = {line.rstrip(",") for line in original.splitlines()}
                added = [line for line in manifest.read_text().splitlines() if line.rstrip(",") not in before]
                self.assertEqual(len(added), 1, added)
                self.assertTrue(added[0].startswith(" " * indent * 2 + f'"{PACKAGE}": "https://'))

    def test_is_idempotent(self) -> None:
        project = make_project(self.temp / "game")
        self.assertEqual(self.run_install(project).returncode, 0)
        before = (project / "Packages" / "manifest.json").read_text()
        self.assertEqual(self.run_install(project).returncode, 0)
        self.assertEqual((project / "Packages" / "manifest.json").read_text(), before)

    def test_refuses_to_replace_a_real_script_without_force(self) -> None:
        project = make_project(self.temp / "game")
        (project / "unity-verify").write_text("#!/usr/bin/env python3\n# not a shim\n")
        completed = self.run_install(project)
        self.assertEqual(completed.returncode, 3)
        self.assertIn("ERR shim_exists", completed.stderr)
        self.assertNotIn(PACKAGE, (project / "Packages" / "manifest.json").read_text())
        self.assertEqual(self.run_install(project, "--force").returncode, 0)

    def test_installed_shim_runs_resolved_package(self) -> None:
        project = make_project(self.temp / "game")
        self.assertEqual(self.run_install(project).returncode, 0)
        resolved = project / "Library" / "PackageCache" / f"{PACKAGE}@4ee62b097584" / "Tools~"
        resolved.mkdir(parents=True)
        shutil.copy(TOOLS / "unity-verify", resolved)
        completed = self.run_shim(project / "unity-verify", "--nope")
        self.assertEqual(completed.returncode, 3)
        self.assertIn("ERR args", completed.stderr)  # reached the real tool through the shim


class DocumentationTests(unittest.TestCase):
    def test_readme_snippet_matches_installer_snippet(self) -> None:
        readme = (PACKAGE_ROOT / "README.md").read_text()
        block = readme.split("<!-- agents-snippet:start -->\n```markdown\n", 1)[1]
        block = block.split("```\n<!-- agents-snippet:end -->", 1)[0]
        self.assertEqual(block, (TOOLS / "agents-snippet.md").read_text())


if __name__ == "__main__":
    unittest.main()
