# unity-verify

[![Release](https://img.shields.io/github/v/release/minis-studio/unity-verify?sort=semver)](https://github.com/minis-studio/unity-verify/releases)
[![Script tests](https://github.com/minis-studio/unity-verify/actions/workflows/script-tests.yml/badge.svg)](https://github.com/minis-studio/unity-verify/actions/workflows/script-tests.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE.md)
[![Unity 6+](https://img.shields.io/badge/Unity-6000.0%2B-black?logo=unity)](#requirements)
[![macOS | Linux](https://img.shields.io/badge/platform-macOS%20%7C%20Linux-lightgrey)](#requirements)

One command that compiles a Unity project and runs its EditMode tests, whether
or not the Editor has the project open. It reports the result in a form a coding
agent can act on: a one-line verdict, stable exit codes, and failures with
`path:line`.

```console
$ ./unity-verify
PASS tests; total=212 passed=212 failed=0 duration=8.41s mode=EditMode

$ ./unity-verify
ERR compile_failed; errors=1; tests=skipped
Assets/Scripts/Board.cs:27:13 error CS0103: The name 'cell' does not exist in the current context

$ ./unity-verify Board
FAIL tests; total=18 passed=17 failed=1 duration=1.12s mode=EditMode
[1] BoardTests.RejectsOverlap
  Expected: False
  But was:  True
  at Assets/Tests/BoardTests.cs:41
```

Coding agents such as Claude Code, Codex, Cursor and GitHub Copilot work best
with a check they can run after every change. In Unity that check is awkward:
Unity won't open a project in batch mode while an Editor has it open, a
background Editor gives the Test Runner only a few updates a second, and
results arrive as NUnit XML next to a long Editor log. `unity-verify` is that
check as a single shell command, for agents and people alike, with no MCP
server or client setup.

## What it does

- **Works with the Editor open or closed.** In the open Editor it leaves Play
  Mode, resolves changed packages and waits out compiles and domain reloads,
  then runs the tests there. Otherwise it starts a batch-mode Editor of the
  project's version, found the way Unity Hub installs it, and quits it after
  the run.
- **Fast.** The Unity Test Framework's synchronous mode runs the whole suite in
  one Editor update, so a run takes little more than the tests' own code. A
  project with 979 EditMode tests runs them all in about 12 s in the open
  Editor, a filtered subset in about 3 s, and everything in about 23 s when a
  batch-mode Editor has to open the project first.
- **Short output.** A passing run is one line. Failures and C# compiler errors
  follow the summary, one entry each, with project-relative locations.
  Environment errors carry a `fix=` hint.
- **Stable exit codes** for passed, failed, didn't compile and couldn't run.
- **Keeps the suite fast.** Tests over 200 ms get a warning and tests over 1 s
  fail, unless a test sets its own NUnit `[MaxTime]`.
- **Small and local.** A bash script and one Editor-only assembly. No server,
  network port, Python or Node: requests are files in the project's
  `Library/UnityVerify/`.
- **Pinned per project.** `Packages/manifest.json` pins the version, so every
  clone, git worktree and agent runs the same one. Concurrent runs each get
  their own request.

## Requirements

- Unity 6 (`6000.0`+). The package pulls in `com.unity.test-framework`.
- bash, on macOS or Linux.
- For runs without an open Editor, the project's Editor version installed
  through Unity Hub, or `UNITY_EDITOR` set to the Unity binary.

## Install

From the Unity project root:

```bash
curl -fsSL https://raw.githubusercontent.com/minis-studio/unity-verify/main/Tools~/install | bash
```

The [installer](Tools~/install) is a short bash script. It pins the latest
release in `Packages/manifest.json`, writes a small `./unity-verify` shim, and
prints the agent snippet below. Commit `Packages/manifest.json`,
`Packages/packages-lock.json` and `unity-verify`, then run `./unity-verify`.

<details>
<summary>Manual install</summary>

1. Add the package (Package Manager → *Add package from git URL*, or edit
   `Packages/manifest.json`):

   ```json
   "dev.minis.unity-verify": "https://github.com/minis-studio/unity-verify.git#v0.8.0"
   ```

2. Copy [`Tools~/project-shim`](Tools~/project-shim) to `<project>/unity-verify`
   and `chmod +x` it.

</details>

## Agent setup

Any agent that can run a shell command can use `unity-verify`. Paste this into
`AGENTS.md`, or `CLAUDE.md` for Claude Code (a `CLAUDE.md` that only contains
`@AGENTS.md` picks it up too):

<!-- agents-snippet:start -->
```markdown
## Unity verification

Verify every change by running `./unity-verify` with no arguments. It compiles the project and runs all EditMode tests, whether or not the Editor is open; don't check the Unity CLI/MCP first or run tests any other way. PlayMode tests and tests that wait for frames (`[UnityTest]`, `[UnitySetUp]`, `[UnityTearDown]`) can't run, so they fail: write EditMode `[Test]`s that call Editor-update code directly, and don't `[Ignore]` a test to get past this unless asked. Keep every test under 50 ms: tests over 200 ms get a warning and tests over 1 s fail, unless the test has its own `[MaxTime]`; don't add or raise one unless asked.
```
<!-- agents-snippet:end -->

A project that also has a Unity MCP server can keep it; the snippet points
agents at `./unity-verify` for verification. Keep project-specific rules (for
example, a cheaper check for data-only changes) next to it in your own file.

## Usage

```text
./unity-verify [FILTER]
```

`FILTER` runs only tests whose fixture or method name contains it
(case-insensitive). With no filter, every EditMode test runs.

| Exit | Meaning | Example first line |
|-----:|---------|--------------------|
| `0` | Compiled, all tests passed | `PASS tests; total=212 passed=212 failed=0 ...` |
| `1` | Failed or inconclusive tests, tests over 1 s, or PlayMode or frame tests | `FAIL tests; total=212 passed=210 failed=2 ...` |
| `2` | Compilation errors (tests skipped) | `ERR compile_failed; errors=1; tests=skipped` |
| `3` | Tooling or environment problem | `ERR editor_missing; version=6000.3.10f1; fix=install it with Unity Hub, ...` |
| `130` | Interrupted | `ERR interrupted; fix=rerun` |

## Test time limits

Every test is expected to finish in under 50 ms. Timings vary between runs and
machines, so a test only gets a warning above 200 ms (4× that), and it fails
above 1 s even when its assertions pass. A test's time includes its `[SetUp]`
and `[TearDown]`.

```text
FAIL tests; total=212 passed=211 failed=1 too_slow=1 duration=9.02s mode=EditMode
[1] LevelTests.BuildsEveryLevel
  Took 3.2s; tests fail over 1s and are expected to finish in under 50ms.
WARN slow_tests; count=1; over=200ms; hint=tests fail over 1s and are expected to finish in under 50ms
  340ms SnapshotTests.SavesScene
```

A test can set its own limit with NUnit's `[MaxTime]`, in milliseconds. It then
gets no warning and fails only over that limit, as it does in the Test Runner
window:

```csharp
[Test, MaxTime(3000)]
public void BuildsEveryLevel() { ... }
```

```text
[1] LevelTests.BuildsEveryLevel
  Took 3.2s; its [MaxTime] is 3s.
```

The agent snippet asks agents not to add or raise `[MaxTime]` unless asked.

## Writing tests for synchronous runs

All tests run in one Editor update, so:

- Nothing runs between tests. `EditorApplication.update` and `delayCall`
  callbacks that a test queues run after the whole pass, so clean up with
  `Object.DestroyImmediate` and restore shared state in the test itself.
- Code that runs on Editor updates is tested by calling it directly, not by
  waiting a frame:

  ```csharp
  // Instead of `yield return null` in a [UnityTest]:
  foreach (var update in EditorApplication.update.GetInvocationList()
               .Where(update => update.Method.DeclaringType == typeof(MaterialWatcher)))
  {
      update.DynamicInvoke();
  }
  ```

- Tests that wait for frames (`[UnityTest]`, or a fixture with `[UnitySetUp]` or
  `[UnityTearDown]`) and PlayMode tests can't run: synchronous mode can't run
  them, and a background Editor would spend seconds on even one. So they fail,
  and the summary counts them in `frame_tests` and `playmode_tests`:

  ```text
  FAIL tests; total=212 passed=210 failed=2 frame_tests=1 playmode_tests=1 duration=8.02s mode=EditMode
  [1] MaterialTests.UpdatesOnTheNextFrame
    Waits for frames ([UnityTest], [UnitySetUp] or [UnityTearDown]) and can't run; make it a [Test] that calls Editor-update code directly.
  [2] LevelPlayTests.LoadsTheLevel
    PlayMode test, which can't run; make it a [Test] in an Editor-only test assembly.
  ```

- Ignored tests (`[Ignore]`, `Assert.Ignore`) and tests for other platforms
  (`[UnityPlatform]`, `[Platform]`) are skipped, whatever kind they are. The
  summary counts them in `skipped`, and they don't change the exit code.

## Limitations

- EditMode `[Test]`s only. PlayMode tests and tests that wait for frames fail.
- macOS and Linux only.
- The open Editor doesn't respond to input until a run ends.

## How it works

The package has two parts:

- `Editor/` is a small Editor-only assembly (`UnityVerify.Editor`) that compiles
  into the project. It runs the tests, applies the time limits and writes the
  report. In the open Editor it answers requests that `unity-verify` leaves in
  `Library/UnityVerify/`, on Editor updates: it leaves Play Mode first, has the
  Package Manager resolve when `Packages/manifest.json` or `packages-lock.json`
  changed (a background Editor otherwise keeps its packages until it gets
  focus), waits while the Editor compiles, imports or reloads, and keeps the
  Editor ticking while a request is in progress. In a batch-mode Editor,
  `UnityVerify.Batch.Run` answers one request and exits. If scripts don't
  compile, batch mode stops right away and the errors are read from its log.
- `Tools~/` holds bash scripts, which Unity ignores (`~` folders get no `.meta`
  files or compilation). `unity-verify` hands a request to the Editor that has
  the project open, or starts a batch-mode Editor of the project's version (as
  Unity Hub installs it) and keeps its log in `Library/UnityVerify/editor.log`.
  Either way it prints the report the Editor code wrote.

The version a project runs is whatever its manifest pins, and
`packages-lock.json` records the exact commit. The committed `./unity-verify`
shim only finds that copy of the package and runs its `Tools~/unity-verify` with
`--project-path` set to the project. It runs the copy Unity resolved into
`Library/PackageCache/dev.minis.unity-verify@*/`. Until Unity has opened the
checkout (right after install, a fresh clone or a new git worktree), it runs a
shallow clone of the pinned tag in `~/.cache/unity-verify/` instead.

## Updating

```bash
curl -fsSL https://raw.githubusercontent.com/minis-studio/unity-verify/main/Tools~/install | bash -s -- --version v0.8.0
```

or change the `#v…` suffix in `Packages/manifest.json`. The shim rarely changes;
the installer refreshes it either way. See the [changelog](CHANGELOG.md).

## Developing

```bash
# Run your checkout's tool against a project:
path/to/unity-verify/Tools~/unity-verify --project-path path/to/project

# Script tests, with stand-ins for Unity (Python 3.9+):
python3 -m unittest discover -s 'Tools~/tests'
```

The Editor code's tests are in `Tests/Editor`. To run them, add the package to
a project's `testables` in `Packages/manifest.json` and run `unity-verify`
there.

Releasing: update `version` in `package.json` and `CHANGELOG.md`, then tag
`v<version>`. The installer pins the version in `package.json` on `main`, so
push the tag before (or with) that commit.

## License

[MIT](LICENSE.md). Unity is a trademark of Unity Technologies. This project is
not affiliated with or endorsed by Unity Technologies.
