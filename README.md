# Unity Verify

One command that tells a coding agent (or a human) whether a Unity project still
compiles and its EditMode tests still pass:

```console
$ ./unity-verify
PASS tests; total=212 passed=212 failed=0 duration=8.41s mode=EditMode
```

It runs the tests in the Unity Editor that has the project open (leaving Play
Mode first), and otherwise in a batch-mode Editor it starts for the run. Either
way the tests run synchronously, so a run takes little more than the tests' own
code (see [Fast tests](#fast-tests)). Output is compact and machine-readable,
and exit codes are stable, so agents can act on them without reading Unity logs.

## Requirements

- Unity 6 (`6000.0`+). The package pulls in `com.unity.test-framework`.
- For runs without an open Editor, the project's Editor version installed
  through Unity Hub, or `UNITY_EDITOR` set to the Unity binary.
- bash, on macOS or Linux.

## Install

From the Unity project root:

```bash
curl -fsSL https://raw.githubusercontent.com/minis-studio/unity-verify/main/Tools~/install | bash
```

This pins the latest release in `Packages/manifest.json`, writes a small
`./unity-verify` shim, and prints the agent snippet below. Commit
`Packages/manifest.json`, `Packages/packages-lock.json` and `unity-verify`.

<details>
<summary>Manual install</summary>

1. Add the package (Package Manager → *Add package from git URL*, or edit
   `Packages/manifest.json`):

   ```json
   "dev.minis.unity-verify": "https://github.com/minis-studio/unity-verify.git#v0.7.0"
   ```

2. Copy [`Tools~/project-shim`](Tools~/project-shim) to `<project>/unity-verify`
   and `chmod +x` it.

</details>

## Agent integration

Paste into `AGENTS.md` (or `CLAUDE.md`; a `CLAUDE.md` that only contains
`@AGENTS.md` picks it up too):

<!-- agents-snippet:start -->
```markdown
## Unity verification

Verify every change by running `./unity-verify` with no arguments. It compiles the project and runs all EditMode tests, whether or not the Editor is open; don't check the Unity CLI/MCP first or run tests any other way. PlayMode tests and tests that wait for frames (`[UnityTest]`, `[UnitySetUp]`, `[UnityTearDown]`) don't run: write `[Test]`s that call Editor-update code directly, and list behavior only those could cover as unverified. Keep every test under 50 ms: tests over 200 ms get a warning and tests over 1 s fail, unless the test has its own `[MaxTime]`; don't add or raise one unless asked.
```
<!-- agents-snippet:end -->

Keep project-specific rules (for example, a cheaper check for data-only
changes) next to it in your own file.

## Usage

```text
./unity-verify [FILTER]
```

`FILTER` runs only tests whose fixture or method name contains it
(case-insensitive). With no filter, every EditMode test runs.

| Exit | Meaning | Example first line |
|-----:|---------|--------------------|
| `0` | Compiled, all tests passed | `PASS tests; total=212 passed=212 failed=0 ...` |
| `1` | Failed or inconclusive tests, or tests over 1 s | `FAIL tests; total=212 passed=210 failed=2 ...` |
| `2` | Compilation errors (tests skipped) | `ERR compile_failed; errors=1; tests=skipped` |
| `3` | Tooling or environment problem | `ERR editor_missing; version=6000.3.10f1; fix=install it with Unity Hub, ...` |
| `130` | Interrupted | `ERR interrupted; fix=rerun` |

Failures and compiler errors follow the summary line, one per entry, with
project-relative `path:line` locations.

## Test time limits

Every test is expected to finish in under 50 ms. Timings vary between runs and
machines, so a test only gets a warning above 200 ms (4× that), and it fails
above 1 s even when its assertions pass:

```text
FAIL tests; total=212 passed=210 failed=2 too_slow=1 duration=9.02s mode=EditMode
[1] BoardTests.RejectsOverlap
  Expected: False
  But was:  True
  at Assets/Tests/BoardTests.cs:41
[2] LevelTests.BuildsEveryLevel
  Took 3.2s; tests fail over 1s and are expected to finish in under 50ms.
WARN slow_tests; count=1; over=200ms; hint=tests fail over 1s and are expected to finish in under 50ms
  340ms SnapshotTests.SavesScene
```

A test's time includes its `[SetUp]` and `[TearDown]`.

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

## Fast tests

In the open Editor, `unity-verify` refreshes assets, checks for compile errors
and runs the tests in a single Editor update. The tests run with the Test
Framework's synchronous mode: the whole suite runs at once, instead of taking
several Editor updates per test, which a background Editor serves only a few
times a second. The Editor doesn't respond to input until the run ends.

Without an open Editor, a batch-mode Editor opens the project, which compiles
it, and runs the same tests the same way before it quits. If scripts don't
compile, batch mode stops right away and the errors are read from its log.

This changes what a test can rely on:

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
  `[UnityTearDown]`) don't run, like PlayMode tests: synchronous mode can't run
  them, and a background Editor would spend seconds on even one. They don't
  change the exit code; the summary counts them and the report names them, so
  what they cover counts as unverified:

  ```text
  PASS tests; total=211 passed=211 failed=0 frame_skipped=1 duration=8.02s mode=EditMode
  WARN frame_tests_skipped; count=1; hint=[UnityTest], [UnitySetUp] and [UnityTearDown] tests don't run; write [Test]s that call Editor-update code directly
    MaterialTests.UpdatesOnTheNextFrame
  ```

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
  `UnityVerify.Batch.Run` answers one request and exits.
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
curl -fsSL https://raw.githubusercontent.com/minis-studio/unity-verify/main/Tools~/install | bash -s -- --version v0.7.0
```

or change the `#v…` suffix in `Packages/manifest.json`. The shim rarely changes;
the installer refreshes it either way.

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
