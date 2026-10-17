# Changelog

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
