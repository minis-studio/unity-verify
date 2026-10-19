# Changelog

## [0.5.0]

- The package no longer depends on `com.unity.pipeline`, the Unity CLI or
  Python. It depends on `com.unity.test-framework`, and `Tools~/unity-verify`,
  the shim and the installer are bash scripts
  (`curl … /Tools~/install | bash`).
- The runner is a small Editor-only assembly, `UnityVerify.Editor`, which runs
  the tests and also writes the report and exit code. The open Editor answers
  requests left in `Library/UnityVerify/` on Editor updates, through compiles,
  reloads and Play Mode, and keeps itself ticking while a request is in
  progress. Concurrent runs each get their own request.
- Without an open Editor, `unity-verify` starts the Unity binary itself, in
  batch mode with `-executeMethod UnityVerify.Batch.Run`, found the way Unity
  Hub installs it or from `UNITY_EDITOR`. Its log is kept in
  `Library/UnityVerify/editor.log`. When scripts don't compile, batch mode stops
  right away and the compiler and assembly definition errors are read from that
  log. A run waits for another batch-mode Editor that has the project open.
- Errors about the Unity CLI and Pipeline (`cli_*`, `pipeline_version`,
  `unity_command`) are gone. New ones: `editor_missing`, `editor_unprepared`
  (the open Editor hasn't loaded the package), `editor_unresponsive`,
  `editor_exited`, `unity_exited`, `project_busy`, and `protocol` (a shim and
  package that don't match). A run in the open Editor that takes too long
  reports `timeout` with what the Editor was doing.
- The tool still runs when started with `python3`: it hands over to bash.

## [0.4.0]

- Breaking: a test that takes longer than 1 s fails, even when its assertions
  pass. The summary line counts these in `failed` and adds `too_slow=N`, and
  each one is listed with its time and the expectation that every test
  finishes in under 50 ms.
- Tests over 200 ms are listed, slowest first, after a `WARN slow_tests` line.
- The runner returns every test's result with its duration, and the version of
  `com.unity.pipeline` running it. Below the version `package.json` depends on,
  the tool stops with `ERR pipeline_version`.
- Without an open Editor, one headless Editor (`unity run --command
  run_script`) opens the project and runs the same runner as the open Editor,
  so tests run synchronously there too, with frame tests in a second pass. It
  replaces the separate compile run and `unity test` run. Batch mode stops
  before the runner when scripts don't compile, so compile errors come from
  its log, and any other early exit reports Unity's own reason.
- The headless Editor gets `ADB_LIBUSB=0` unless it is set: the Android
  module's adb server then starts in a fraction of a second instead of about
  3 s on macOS.
- The open-Editor call is skipped when no Editor holds the project's
  `Temp/UnityLockfile`.
- A headless run times out after 660 s, for import, compilation and tests
  together.
- Requires Unity CLI `1.0.0-beta.12`, for `unity run --log-file` and
  `--no-tail`.

## [0.3.0]

- In the open Editor, one `run_script` call of `Tools~/UnityVerifyRunner.cs`
  refreshes assets, reports compile errors and runs the tests. It replaces the
  separate `editor_status`, `recompile`, `recompile_status`, `eval` and
  `run_tests` calls, and play mode is still stopped first.
- Tests run with the Test Framework's `runSynchronously`, inside one Editor
  update instead of several per test. `EditorApplication.update` and
  `delayCall` callbacks no longer run between tests.
- Tests that need frames (`[UnityTest]`, `[UnitySetUp]`, `[UnityTearDown]`)
  run in a second, normal pass, and the report adds a `NOTE frame_tests` line
  with its cost.
- Full runs no longer list the tests first unless a second pass may be needed.

## [0.2.0]

- The `./unity-verify` shim only supports the GitHub git dependency: it runs
  the copy in `Library/PackageCache`, or a shallow clone of the pinned tag
  until Unity has resolved it. `$UNITY_VERIFY_PACKAGE`, embedded packages and
  `file:` dependencies are no longer looked up, and `install --local` is gone;
  run `Tools~/unity-verify --project-path <project>` from a checkout instead.
- `ERR cli_missing` now says `fix=ask user to install CLI or add it to PATH`.
- Shorter AGENTS.md snippet.

## [0.1.0]

- Initial release: the `unity-verify` tool, the `./unity-verify` project shim
  and `Tools~/install`.
