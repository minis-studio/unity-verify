# Changelog

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
