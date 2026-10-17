## Unity verification

- Validate every change, including script-only changes, by running `./unity-verify` from the repository root with no arguments. It compiles the project and runs the full EditMode suite, reusing the open Unity Editor (leaving Play Mode first) or starting a headless one.
- Do not preflight the Unity CLI/MCP or run Unity test commands directly; use only the wrapper.
- Exit codes: `0` pass, `1` failed or inconclusive tests, `2` compile errors, `3` tooling/environment error, `130` interrupted. On `3`, keep your changes and report the `ERR` line verbatim; if the Unity CLI is missing, ask the user to install or repair it, then rerun.
- PlayMode tests are not run. Add EditMode tests for changed behavior and list PlayMode-only behavior as unverified.
