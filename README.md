# Unity Verify

One command that tells a coding agent (or a human) whether a Unity project still
compiles and its EditMode tests still pass:

```console
$ ./unity-verify
PASS tests; total=212 passed=212 failed=0 duration=8.41s mode=EditMode
```

It reuses the open Unity Editor through Unity Pipeline (`com.unity.pipeline`)
when one is running (leaving Play Mode first), and otherwise compiles and tests
in a headless Editor. Output is compact and machine-readable, and exit codes are
stable, so agents can act on them without reading Unity logs.

## Requirements

- Unity 6 (`6000.0`+). The package pulls in `com.unity.pipeline`.
- Unity CLI `>= 1.0.0-beta.8` on `PATH` as `unity`.
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
   "dev.minis.unity-verify": "https://github.com/minis-studio/unity-verify.git#v0.1.0"
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

- Validate every change, including script-only changes, by running `./unity-verify` from the repository root with no arguments. It compiles the project and runs the full EditMode suite, reusing the open Unity Editor (leaving Play Mode first) or starting a headless one.
- Do not preflight the Unity CLI/MCP or run Unity test commands directly; use only the wrapper.
- Exit codes: `0` pass, `1` failed or inconclusive tests, `2` compile errors, `3` tooling/environment error, `130` interrupted. On `3`, keep your changes and report the `ERR` line verbatim; if the Unity CLI is missing, ask the user to install or repair it, then rerun.
- PlayMode tests are not run. Add EditMode tests for changed behavior and list PlayMode-only behavior as unverified.
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
| `1` | Failed or inconclusive tests | `FAIL tests; total=212 passed=210 failed=2 ...` |
| `2` | Compilation errors (tests skipped) | `ERR compile_failed; errors=1; tests=skipped` |
| `3` | Tooling or environment problem | `ERR cli_version; got=1.0.0-beta.3; min=1.0.0-beta.8; fix=unity upgrade` |
| `130` | Interrupted | `ERR interrupted; stage=unknown; fix=rerun` |

Failures and compiler errors follow the summary line, one per entry, with
project-relative `path:line` locations.

## How it is laid out

The tool lives in the package, in `Tools~/` (Unity ignores `~` folders, so no
`.meta` files or compilation). The version a project runs is whatever its
manifest pins, and `packages-lock.json` records the exact commit.

The committed `./unity-verify` shim only finds that copy and runs it with
`--project-path` set to the project. It looks, in order, at:

1. `$UNITY_VERIFY_PACKAGE`, if set (for developing the tool);
2. `Packages/dev.minis.unity-verify/` (embedded package or git submodule);
3. a `file:` dependency in `Packages/manifest.json`;
4. `Library/PackageCache/dev.minis.unity-verify@*/`;
5. the main checkout's `Library/PackageCache`, when run from a git worktree;
6. a shallow clone of the locked commit in `~/.cache/unity-verify/`, for fresh
   clones Unity has not opened yet.

## Updating

```bash
curl -fsSL https://raw.githubusercontent.com/minis-studio/unity-verify/main/Tools~/install | python3 - --version v0.2.0
```

or change the `#v…` suffix in `Packages/manifest.json`. The shim rarely changes;
the installer refreshes it either way.

## Developing

```bash
# Point one project at your checkout without touching its manifest:
UNITY_VERIFY_PACKAGE=path/to/unity-verify ./unity-verify

# Or pin the checkout in a project with a file: dependency:
python3 path/to/unity-verify/Tools~/install --local --project-path path/to/project

# Tests (no Unity needed):
python3 -m unittest discover -s 'Tools~/tests'
```

Releasing: update `version` in `package.json` and `CHANGELOG.md`, then tag
`v<version>`. The installer pins the version in `package.json` on `main`, so
push the tag before (or with) that commit.
