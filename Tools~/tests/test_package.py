"""Tests for the scripts in Tools~, with stand-ins for Unity.

The Editor code has its own EditMode tests in Tests/Editor. Run these from the
package root: python3 -m unittest discover -s 'Tools~/tests'
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import textwrap
import time
import unittest
from pathlib import Path


PACKAGE_ROOT = Path(__file__).resolve().parents[2]
TOOLS = PACKAGE_ROOT / "Tools~"
PACKAGE = json.loads((PACKAGE_ROOT / "package.json").read_text())["name"]
BASH = "/bin/bash"

# Stands in for Tools~/unity-verify: reports how it was called.
STUB_TOOL = textwrap.dedent(
    """\
    #!/bin/bash
    echo "$0"
    for argument in "$@"; do echo "$argument"; done
    exit 7
    """
)

# Stands in for a batch-mode Unity: records its arguments and answers the request
# with $FAKE_UNITY_REPLY, or stops early the way $FAKE_UNITY_MODE says.
FAKE_UNITY = textwrap.dedent(
    """\
    #!/bin/bash
    { printf '%s\\n' "$@"; echo "ADB_LIBUSB=${ADB_LIBUSB-unset}"; } > "$FAKE_UNITY_ARGS"
    while [ $# -gt 0 ]; do
      case "$1" in
        -logFile) log=$2; shift ;;
        -projectPath) project=$2; shift ;;
        -unityVerifyRequest) request=$2; shift ;;
      esac
      shift
    done
    cp "$request" "$FAKE_UNITY_ARGS.request"
    case "$FAKE_UNITY_MODE" in
      abort)
        printf 'Loading\\nAborting batchmode due to failure:\\nNo valid Unity Editor license found.\\n' > "$log"
        exit 1 ;;
      compile)
        {
          echo "-executeMethod"
          echo "Assets/B.cs(7,1): error CS1002: ; expected"
          echo "$project/Assets/D.cs(1,1): error CS0246: The type 'Gone' could not be found"
          echo "Assets/A.cs(3,5): error CS0103: The name 'x' does not exist"
          echo "Assets/B.cs(7,1): error CS1002: ; expected"
          echo "Assets/C.cs(2,2): warning CS0168: The variable 'y' is declared but never used"
          echo "Assembly with name 'Game' already exists (Assets/Game/Game.asmdef)"
          echo "  LogAssemblyErrors (0ms)"
          echo "Aborting batchmode due to failure:"
          echo "Scripts have compiler errors."
        } > "$log"
        exit 1 ;;
    esac
    responses=$(dirname "$(dirname "$request")")/responses
    id=$(basename "$request" .request)
    cp "$FAKE_UNITY_REPLY" "$responses/$id.response"
    rm -f "$request"
    exit "$(head -n 1 "$FAKE_UNITY_REPLY")"
    """
)

# Stands in for an open Editor (its command line names the project like
# Unity's does) that answers requests the way $FAKE_EDITOR_MODE says.
FAKE_EDITOR = textwrap.dedent(
    """\
    #!/bin/bash
    box=$2/Library/UnityVerify
    mkdir -p "$box/requests" "$box/responses"
    echo $$ > "$box/heartbeat"
    while :; do
      for request in "$box"/requests/*.request; do
        [ -f "$request" ] || continue
        id=$(basename "$request" .request)
        cp "$request" "$FAKE_EDITOR_SEEN"
        echo running > "$box/responses/$id.state"
        [ "$FAKE_EDITOR_MODE" = exit ] && exit 0
        cp "$FAKE_EDITOR_REPLY" "$box/responses/$id.response.tmp"
        mv "$box/responses/$id.response.tmp" "$box/responses/$id.response"
        rm -f "$request"
      done
      sleep 0.05
    done
    """
)

FAILING_REPORT = "1\nFAIL tests; total=2 passed=1 failed=1 duration=0.50s mode=EditMode\n[1] BoardTests.Fails\n"


def make_project(root: Path, dependency: str | None = None, version: str = "6000.3.10f1") -> Path:
    (root / "Assets").mkdir(parents=True)
    (root / "ProjectSettings").mkdir()
    (root / "ProjectSettings" / "ProjectVersion.txt").write_text(f"m_EditorVersion: {version}\n")
    (root / "Packages").mkdir()
    dependencies = {"com.unity.test-framework": "1.6.0"}
    if dependency:
        dependencies[PACKAGE] = dependency
    (root / "Packages" / "manifest.json").write_text(
        json.dumps({"dependencies": dependencies}, indent=2) + "\n"
    )
    return root.resolve()


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
        self.environment.pop("UNITY_EDITOR", None)

    def write_script(self, name: str, text: str) -> Path:
        path = self.temp / "bin" / name
        path.parent.mkdir(exist_ok=True)
        path.write_text(text)
        path.chmod(0o755)
        return path

    def run_shim(self, shim: Path, *arguments: str, **environment: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [BASH, str(shim), *arguments],
            cwd=self.temp, env={**self.environment, **environment},
            capture_output=True, text=True, timeout=60,
        )

    def assert_ran(self, completed: subprocess.CompletedProcess[str], package: Path, project: Path) -> None:
        self.assertEqual(completed.returncode, 7, completed.stderr)
        self.assertEqual(completed.stdout.splitlines(), [
            str(package.resolve() / "Tools~" / "unity-verify"), "--project-path", str(project), "Board",
        ])


class ShimTests(TempDirTestCase):
    def test_runs_package_from_package_cache(self) -> None:
        project = make_project(self.temp / "game", "https://example.invalid/x.git#v1")
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
    def run_tool(self, *arguments: str, cwd: Path, **environment: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [BASH, str(TOOLS / "unity-verify"), *arguments],
            cwd=cwd, env={**self.environment, **environment}, capture_output=True, text=True, timeout=60,
        )

    def test_reports_missing_project(self) -> None:
        completed = self.run_tool(cwd=self.temp)
        self.assertEqual(completed.returncode, 3)
        self.assertIn("ERR project_missing", completed.stderr)

    def test_rejects_unknown_arguments_with_tooling_exit(self) -> None:
        completed = self.run_tool("--nope", cwd=make_project(self.temp / "game"))
        self.assertEqual(completed.returncode, 3)
        self.assertIn("ERR args", completed.stderr)

    def test_hands_python_callers_over_to_bash(self) -> None:
        completed = subprocess.run(
            [sys.executable, "-W", "error", str(TOOLS / "unity-verify"), "--help"],
            env=self.environment, capture_output=True, text=True, timeout=60,
        )
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertTrue(completed.stdout.startswith("usage: unity-verify"))


class ColdPathTests(TempDirTestCase):
    def run_cold(self, mode: str = "respond", reply: str = FAILING_REPORT, *arguments: str, cwd: Path | None = None):
        unity = self.write_script("Unity", FAKE_UNITY)
        (self.temp / "reply").write_text(reply)
        project = make_project(self.temp / "game")
        completed = subprocess.run(
            [BASH, str(TOOLS / "unity-verify"), *arguments],
            cwd=cwd or project / "Assets",
            env={
                **self.environment,
                "UNITY_EDITOR": str(unity),
                "FAKE_UNITY_MODE": mode,
                "FAKE_UNITY_REPLY": str(self.temp / "reply"),
                "FAKE_UNITY_ARGS": str(self.temp / "args"),
            },
            capture_output=True, text=True, timeout=60,
        )
        return completed, project

    def test_starts_a_batch_editor_that_answers_the_request(self) -> None:
        completed, project = self.run_cold("respond", FAILING_REPORT, "Board")
        self.assertEqual(completed.returncode, 1, completed.stderr)
        self.assertEqual(completed.stdout, FAILING_REPORT.split("\n", 1)[1])
        arguments = (self.temp / "args").read_text().splitlines()
        self.assertEqual(arguments[:3], ["-batchmode", "-projectPath", str(project)])
        self.assertEqual(arguments[arguments.index("-executeMethod") + 1], "UnityVerify.Batch.Run")
        self.assertEqual(arguments[-1], "ADB_LIBUSB=0")
        self.assertEqual((self.temp / "args.request").read_text(), "unity-verify 1\nBoard\n")
        box = project / "Library" / "UnityVerify"
        self.assertEqual(list((box / "requests").iterdir()) + list((box / "responses").iterdir()), [])

    def test_reads_compile_errors_from_the_log_when_batch_mode_stops_on_them(self) -> None:
        completed, _ = self.run_cold("compile")
        self.assertEqual(completed.returncode, 2, completed.stderr)
        self.assertEqual(completed.stdout.splitlines(), [
            "ERR compile_failed; errors=4; tests=skipped",
            "Assets/A.cs:3:5 error CS0103: The name 'x' does not exist",
            "Assets/B.cs:7:1 error CS1002: ; expected",
            "Assets/D.cs:1:1 error CS0246: The type 'Gone' could not be found",
            "Assets/Game/Game.asmdef error: Assembly with name 'Game' already exists (Assets/Game/Game.asmdef)",
        ])

    def test_reports_why_unity_stopped_before_the_request(self) -> None:
        completed, _ = self.run_cold("abort")
        self.assertEqual(completed.returncode, 3)
        self.assertEqual(completed.stdout, (
            "ERR unity_exited; code=1; cause=No valid Unity Editor license found.; "
            "log=Library/UnityVerify/editor.log; fix=check the log\n"
        ))

    def test_reports_a_missing_editor_version(self) -> None:
        project = make_project(self.temp / "game", version="1.2.3f4")
        completed = subprocess.run(
            [BASH, str(TOOLS / "unity-verify"), "--project-path", str(project)],
            env={**self.environment, "HOME": str(self.temp), "XDG_CONFIG_HOME": str(self.temp / "config")},
            capture_output=True, text=True, timeout=60,
        )
        self.assertEqual(completed.returncode, 3)
        self.assertIn("ERR editor_missing; version=1.2.3f4;", completed.stdout)

    @unittest.skipUnless(sys.platform == "darwin", "Unity Hub's macOS layout")
    def test_finds_an_editor_added_to_unity_hub_from_disk(self) -> None:
        app = self.temp / "Unity-1.2.3" / "Unity.app"
        binary = app / "Contents" / "MacOS" / "Unity"
        binary.parent.mkdir(parents=True)
        binary.write_text(FAKE_UNITY)
        binary.chmod(0o755)
        hub = self.temp / "Library" / "Application Support" / "UnityHub"
        hub.mkdir(parents=True)
        (hub / "editors-v2.json").write_text(json.dumps({"schema_version": "2", "data": [
            {"version": "1.0.0f1", "location": ["/nowhere/Unity.app"], "manual": True},
            {"version": "1.2.3f4", "location": [str(app)], "manual": True, "buildPlatforms": [{"name": "iOS"}]},
        ]}, separators=(",", ":")))
        (self.temp / "reply").write_text("0\nPASS tests\n")
        project = make_project(self.temp / "game", version="1.2.3f4")
        completed = subprocess.run(
            [BASH, str(TOOLS / "unity-verify"), "--project-path", str(project)],
            env={
                **self.environment, "HOME": str(self.temp), "FAKE_UNITY_MODE": "respond",
                "FAKE_UNITY_REPLY": str(self.temp / "reply"), "FAKE_UNITY_ARGS": str(self.temp / "args"),
            },
            capture_output=True, text=True, timeout=60,
        )
        self.assertEqual((completed.returncode, completed.stdout), (0, "PASS tests\n"), completed.stderr)


class WarmPathTests(TempDirTestCase):
    def run_warm(self, mode: str = "respond", reply: str = FAILING_REPORT):
        editor = self.write_script("Unity", FAKE_EDITOR)
        (self.temp / "reply").write_text(reply)
        project = make_project(self.temp / "game")
        environment = {
            **self.environment,
            "FAKE_EDITOR_MODE": mode,
            "FAKE_EDITOR_REPLY": str(self.temp / "reply"),
            "FAKE_EDITOR_SEEN": str(self.temp / "seen"),
        }
        # Detached, so it is reaped when it exits, as a real Editor is.
        pid = int(subprocess.run(
            [BASH, "-c", '"$0" "$@" > /dev/null 2>&1 & echo $!', str(editor), "-projectpath", str(project)],
            env=environment, capture_output=True, text=True, check=True,
        ).stdout)
        self.addCleanup(lambda: subprocess.run(["kill", "-9", str(pid)], capture_output=True))
        heartbeat = project / "Library" / "UnityVerify" / "heartbeat"
        for _ in range(100):
            if heartbeat.exists():
                break
            time.sleep(0.05)
        completed = subprocess.run(
            [BASH, str(TOOLS / "unity-verify"), "--project-path", str(project), "Board"],
            # No batch-mode Unity: a cold run would fail on this.
            env={**environment, "UNITY_EDITOR": "/nonexistent/Unity"},
            capture_output=True, text=True, timeout=60,
        )
        return completed, project

    def test_hands_the_request_to_the_open_editor(self) -> None:
        completed, project = self.run_warm()
        self.assertEqual(completed.returncode, 1, completed.stdout + completed.stderr)
        self.assertEqual(completed.stdout, FAILING_REPORT.split("\n", 1)[1])
        self.assertEqual((self.temp / "seen").read_text(), "unity-verify 1\nBoard\n")
        self.assertEqual(list((project / "Library" / "UnityVerify" / "responses").iterdir()), [])

    def test_reports_an_editor_that_exits_during_the_run(self) -> None:
        completed, _ = self.run_warm("exit")
        self.assertEqual(completed.returncode, 3)
        self.assertTrue(completed.stdout.startswith("ERR editor_exited; pid="), completed.stdout)


class InstallerTests(TempDirTestCase):
    def run_install(self, project: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [BASH, str(TOOLS / "install"), "--project-path", str(project), *arguments],
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
                json.loads(manifest.read_text())

    def test_updates_the_pin_in_place(self) -> None:
        project = make_project(self.temp / "game")
        manifest = project / "Packages" / "manifest.json"
        manifest.write_text(json.dumps(
            {"dependencies": {PACKAGE: "https://example.invalid/x.git#v1", "zz.last": "1"}}, indent=2
        ) + "\n")
        self.assertEqual(self.run_install(project, "--version", "v2").returncode, 0)
        self.assertEqual(json.loads(manifest.read_text())["dependencies"], {
            PACKAGE: "https://github.com/minis-studio/unity-verify.git#v2", "zz.last": "1",
        })

    def test_fills_empty_dependencies(self) -> None:
        project = make_project(self.temp / "game")
        manifest = project / "Packages" / "manifest.json"
        manifest.write_text('{\n  "dependencies": {}\n}\n')
        self.assertEqual(self.run_install(project, "--version", "v2").returncode, 0)
        self.assertEqual(list(json.loads(manifest.read_text())["dependencies"]), [PACKAGE])

    def test_is_idempotent(self) -> None:
        project = make_project(self.temp / "game")
        self.assertEqual(self.run_install(project).returncode, 0)
        before = (project / "Packages" / "manifest.json").read_text()
        self.assertEqual(self.run_install(project).returncode, 0)
        self.assertEqual((project / "Packages" / "manifest.json").read_text(), before)

    def test_refuses_to_replace_a_real_script_without_force(self) -> None:
        project = make_project(self.temp / "game")
        (project / "unity-verify").write_text("#!/bin/bash\n# not a shim\n")
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
