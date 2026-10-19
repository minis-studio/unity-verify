# Unity Verify

One command that tells a coding agent (or a human) whether a Unity project still
compiles and its EditMode tests still pass:

```console
$ ./unity-verify
PASS tests; total=212 passed=212 failed=0 duration=8.41s mode=EditMode
```

It reuses the open Unity Editor through Unity Pipeline (`com.unity.pipeline`)
when one is running (leaving Play Mode first), and otherwise compiles and tests
in one headless Editor. Either way the tests run synchronously, so a run takes
little more than the tests' own code (see [Fast tests](#fast-tests)).
Output is compact and machine-readable, and exit codes are stable, so agents can
act on them without reading Unity logs.

## Requirements

- Unity 6 (`6000.0`+). The package pulls in `com.unity.pipeline`; a project
  that pins it below the version in `package.json` gets `ERR pipeline_version`.
- Unity CLI `>= 1.0.0-beta.12` on `PATH` as `unity`.
- Python 3.9+, macOS or Linux.

## Install

From the Unity project root:

```bash
curl -fsSL https://raw.githubusercontent.com/minis-studio/unity-verify/main/Tools~/install | python3 -
```

This pins the latest release in `Packages/manifest.json`, writes a small
`./unity-verify` shim, and prints the agent snippet below. Commit
`Packages/manifest.json`, `Packages/packages-lock.json` and `unity-verify`.

<details>
<summary>Manual install</summary>

1. Add the package (Package Manager → *Add package from git URL*, or edit
   `Packages/manifest.json`):

   ```json
   "dev.minis.unity-verify": "https://github.com/minis-studio/unity-verify.git#v0.4.0"
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

Verify every change by running `./unity-verify` with no arguments. It compiles the project and runs all EditMode tests, whether or not the Editor is open; don't check the Unity CLI/MCP first or run tests any other way. PlayMode tests aren't run: cover changed behavior with EditMode tests and list PlayMode-only behavior as unverified. Tests run synchronously, so write `[Test]`s that call Editor-update code directly instead of `[UnityTest]`s that wait for frames. Keep every test under 50 ms: tests over 200 ms get a warning and tests over 1 s fail.
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
| `3` | Tooling or environment problem | `ERR cli_version; got=1.0.0-beta.3; min=1.0.0-beta.12; fix=unity upgrade` |
| `130` | Interrupted | `ERR interrupted; stage=unknown; fix=rerun` |

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

A test's time includes its `[SetUp]` and `[TearDown]`. Frame tests in the open
Editor (see below) also include the time spent waiting for Editor updates.

## Fast tests

In the open Editor, `unity-verify` refreshes assets, checks for compile errors
and runs the tests in a single call. The tests run with the Test Framework's
synchronous mode: the whole suite runs inside one Editor update, instead of
taking several Editor updates per test, which a background Editor serves only a
few times a second. The Editor doesn't respond to input until the run ends.

Without an open Editor, one headless Editor opens the project, which compiles
it, and runs the same tests the same way before it quits. If scripts don't
compile, it stops before the tests and the compile errors are read from its log.

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

- Tests that need frames (`[UnityTest]`, or a fixture with `[UnitySetUp]` or
  `[UnityTearDown]`) still run, in a second, normal pass after the synchronous
  one. In the open Editor that pass costs a few seconds on its own, and the
  report says so:

  ```text
  NOTE frame_tests; count=1; duration=5.13s; hint=use [Test], not [UnityTest]/[UnitySetUp]/[UnityTearDown]
  ```

## How it is laid out

The tool lives in the package, in `Tools~/` (Unity ignores `~` folders, so no
`.meta` files or compilation). The version a project runs is whatever its
manifest pins, and `packages-lock.json` records the exact commit. It runs
`Tools~/UnityVerifyRunner.cs` through Unity Pipeline's `run_script` command, in
the open Editor or in a headless one (`unity run --command run_script`).
`run_script` compiles the file in memory, so it is never imported into the
project.

The committed `./unity-verify` shim only finds that copy and runs it with
`--project-path` set to the project. It runs the copy Unity resolved into
`Library/PackageCache/dev.minis.unity-verify@*/`. Until Unity has opened the
checkout (right after install, a fresh clone or a new git worktree), it runs a
shallow clone of the pinned tag in `~/.cache/unity-verify/` instead.

## Updating

```bash
curl -fsSL https://raw.githubusercontent.com/minis-studio/unity-verify/main/Tools~/install | python3 - --version v0.4.0
```

or change the `#v…` suffix in `Packages/manifest.json`. The shim rarely changes;
the installer refreshes it either way.

## Developing

```bash
# Run your checkout's tool against a project:
path/to/unity-verify/Tools~/unity-verify --project-path path/to/project

# Tests (no Unity needed):
python3 -m unittest discover -s 'Tools~/tests'
```

Releasing: update `version` in `package.json` and `CHANGELOG.md`, then tag
`v<version>`. The installer pins the version in `package.json` on `main`, so
push the tag before (or with) that commit.
