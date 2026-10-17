"""Tests for the packaging layer: project discovery, the shim and the installer.

Run from the package root: python3 -m unittest discover -s 'Tools~/tests'
"""

from __future__ import annotations

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
PACKAGE = json.loads((PACKAGE_ROOT / "package.json").read_text())["name"]

# Stands in for Tools~/unity-verify: reports how it was called.
STUB_TOOL = textwrap.dedent(
    """\
    #!/usr/bin/env python3
    import json, sys
    print(json.dumps({"tool": __file__, "argv": sys.argv[1:]}))
    raise SystemExit(7)
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

    def test_finds_project_above_working_directory(self) -> None:
        project = make_project(self.temp / "game")
        loader = SourceFileLoader("unity_verify", str(TOOLS / "unity-verify"))
        tool = importlib.util.module_from_spec(importlib.util.spec_from_loader(loader.name, loader))
        sys.modules[loader.name] = tool  # dataclasses look their module up here
        self.addCleanup(sys.modules.pop, loader.name, None)
        loader.exec_module(tool)
        self.assertEqual(tool.find_project_root(project / "Assets"), project)
        self.assertIsNone(tool.find_project_root(self.temp))

    def test_rejects_unknown_arguments_with_tooling_exit(self) -> None:
        completed = self.run_tool("--nope", cwd=make_project(self.temp / "game"))
        self.assertEqual(completed.returncode, 3)
        self.assertIn("ERR args", completed.stderr)


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
