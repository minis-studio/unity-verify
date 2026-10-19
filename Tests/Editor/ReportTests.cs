using NUnit.Framework;

namespace UnityVerify
{
    public class ReportTests
    {
        const string Root = "/p";

        static Outcome Tests(params TestResult[] results)
        {
            var outcome = new Outcome { State = Outcome.Tests, Duration = 0.5 };
            outcome.Results.AddRange(results);
            return outcome;
        }

        static TestResult Test(string name, string status = "Passed", double duration = 0.01)
        {
            var result = new TestResult { FullName = name, Status = status, Duration = duration };
            if (status == "Failed")
            {
                result.Message = "Expected: 1\n  But was:  2\n";
                result.StackTrace = $"at {name} () [0x00000] in /p/Assets/Tests/BoardTests.cs:12\n";
            }
            return result;
        }

        static string[] Lines(Report.Text text)
        {
            return text.Body.Split('\n');
        }

        [Test]
        public void PassingRunPrintsOneLine()
        {
            var text = Report.Format(Tests(Test("BoardTests.A"), Test("BoardTests.B")), "", "warm", Root);
            Assert.That(text.Code, Is.EqualTo(Report.Success));
            Assert.That(Lines(text), Is.EqualTo(new[]
            {
                "PASS tests; total=2 passed=2 failed=0 duration=0.50s mode=EditMode",
            }));
        }

        [Test]
        public void FailuresListTheMessageAndProjectRelativeLocation()
        {
            var text = Report.Format(Tests(Test("BoardTests.Passes"), Test("BoardTests.Fails", "Failed")), "Board", "warm", Root);
            Assert.That(text.Code, Is.EqualTo(Report.TestFailure));
            Assert.That(Lines(text), Is.EqualTo(new[]
            {
                "FAIL tests; total=2 passed=1 failed=1 duration=0.50s mode=EditMode filter=Board",
                "[1] BoardTests.Fails",
                "  Expected: 1",
                "  But was:  2",
                "  at Assets/Tests/BoardTests.cs:12",
            }));
        }

        [Test]
        public void PassedTestsOverOneSecondFailAndTestsOver200msAreListed()
        {
            var text = Report.Format(Tests(
                Test("BoardTests.Quick", duration: 0.2),
                Test("BoardTests.Slow", duration: 0.35),
                Test("BoardTests.Fails", "Failed", 1.2),
                Test("BoardTests.Slower", duration: 1.0),
                Test("BoardTests.TooSlow", duration: 1.5)), "", "warm", Root);
            const string expectation = "tests fail over 1s and are expected to finish in under 50ms";
            Assert.That(text.Code, Is.EqualTo(Report.TestFailure));
            Assert.That(Lines(text), Is.EqualTo(new[]
            {
                "FAIL tests; total=5 passed=3 failed=2 too_slow=1 duration=0.50s mode=EditMode",
                "[1] BoardTests.Fails",
                "  Expected: 1",
                "  But was:  2",
                "  at Assets/Tests/BoardTests.cs:12",
                "[2] BoardTests.TooSlow",
                $"  Took 1.5s; {expectation}.",
                $"WARN slow_tests; count=2; over=200ms; hint={expectation}",
                "  1s BoardTests.Slower",
                "  350ms BoardTests.Slow",
            }));
        }

        [Test]
        public void SlowTestsAloneStillPass()
        {
            var text = Report.Format(Tests(Test("BoardTests.Slow", duration: 0.25)), "", "warm", Root);
            Assert.That(text.Code, Is.EqualTo(Report.Success));
            Assert.That(Lines(text)[1], Does.StartWith("WARN slow_tests; count=1; over=200ms;"));
            Assert.That(Lines(text)[2], Is.EqualTo("  250ms BoardTests.Slow"));
        }

        [Test]
        public void FrameTestsAreNoted()
        {
            var outcome = Tests(Test("BoardTests.A"));
            outcome.FrameTests = 1;
            outcome.FrameDuration = 4;
            var text = Report.Format(outcome, "", "warm", Root);
            Assert.That(Lines(text)[1], Is.EqualTo(
                "NOTE frame_tests; count=1; duration=4.00s; hint=use [Test], not [UnityTest]/[UnitySetUp]/[UnityTearDown]"));
        }

        [Test]
        public void NoMatchingTestsIsAnEnvironmentFailure()
        {
            var text = Report.Format(Tests(), "Nope", "cold", Root);
            Assert.That(text.Code, Is.EqualTo(Report.EnvironmentFailure));
            Assert.That(text.Body, Is.EqualTo("ERR tests_none; filter=Nope; mode=EditMode; fix=check filter/tests"));
        }

        [Test]
        public void CompileErrorsAreDeduplicatedSortedAndProjectRelative()
        {
            var outcome = new Outcome { State = Outcome.CompileFailed };
            outcome.Diagnostics.Add(new Diagnostic { Message = "Assets/B.cs(7,1): error CS1002: ; expected\n" });
            outcome.Diagnostics.Add(new Diagnostic { Message = "/p/Assets/A.cs(3,5): error cs0103: The name 'x' does not exist" });
            outcome.Diagnostics.Add(new Diagnostic { Message = "Assets/B.cs(7,1): error CS1002: ; expected" });
            outcome.Diagnostics.Add(new Diagnostic { Message = "error: Assembly has reference to non-existent assembly 'Gone' (Assets/Game.asmdef)" });
            var text = Report.Format(outcome, "", "warm", Root);
            Assert.That(text.Code, Is.EqualTo(Report.CompilationFailure));
            Assert.That(Lines(text), Is.EqualTo(new[]
            {
                "ERR compile_failed; errors=3; tests=skipped",
                "Assets/A.cs:3:5 error CS0103: The name 'x' does not exist",
                "Assets/B.cs:7:1 error CS1002: ; expected",
                "Assets/Game.asmdef error: Assembly has reference to non-existent assembly 'Gone' (Assets/Game.asmdef)",
            }));
        }

        [Test]
        public void CompileFailureWithoutDiagnosticsIsAnEnvironmentFailure()
        {
            var text = Report.Format(new Outcome { State = Outcome.CompileFailed }, "", "cold", Root);
            Assert.That(text.Code, Is.EqualTo(Report.EnvironmentFailure));
            Assert.That(text.Body, Does.StartWith("ERR compile_verify; stage=cold;"));
        }

        [Test]
        public void LongMessagesAreShortened()
        {
            var lines = Report.CompactMessage(string.Join("\n", new string('x', 310), "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14"));
            Assert.That(lines[0], Is.EqualTo(new string('x', 300) + "..."));
            Assert.That(lines.Count, Is.EqualTo(13));
            Assert.That(lines[12], Is.EqualTo("... 2 more message lines"));
        }

        [Test]
        public void CausesAreOneFieldSafeLine()
        {
            Assert.That(Report.Cause("first; part\nsecond"), Is.EqualTo("first, part"));
            Assert.That(Report.Cause(""), Is.EqualTo("unknown"));
        }
    }
}
