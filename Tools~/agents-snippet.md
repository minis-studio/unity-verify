## Unity verification

Verify every change by running `./unity-verify` with no arguments. It compiles the project and runs all EditMode tests, whether or not the Editor is open; don't check the Unity CLI/MCP first or run tests any other way. PlayMode tests aren't run: cover changed behavior with EditMode tests and list PlayMode-only behavior as unverified.
