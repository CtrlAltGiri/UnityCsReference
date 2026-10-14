# UN3-8 runtime harness (Unity 6000.3.26f1)

Not part of any csproj. Runs the UN3-5 traversal plus the UN3-8 kill switch and verify mode, ported onto the
6000.3 internals, next to a stock 6000.3 panel, and checks the acceptance criteria.

- Copy `harness.uss` to `Assets/UN3_8Harness/` and the `.cs` and `.asmdef` files to `Assets/UN3_8Harness/Editor/`
  of a 6000.3 project. The asmdef must be named `Assembly-CSharp-Editor-testable`, which gives access to the
  UIElements internals.
- Run `Unity -batchmode -nographics -projectPath <project> -executeMethod UN3_8Harness.HarnessRunner.Run -logFile <log> -quit`.
  It exits with 0 when every check passes. `harness-log.txt` holds the `[UN3-8]` lines of a run.
- `HarnessTestSetup.EnableVerification` is what a test suite's `[SetUpFixture]` would do.
