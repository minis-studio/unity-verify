using System.Reflection;
using NUnit.Framework;

namespace UnityVerify
{
    public class MaxTimeTests
    {
        [MaxTime(3000)]
        static void LimitedTo3s() { }

        static void Unlimited() { }

        static MethodInfo Method(string name)
        {
            return typeof(MaxTimeTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        }

        static TestResult Failed(string message, double duration)
        {
            return new TestResult { FullName = "LevelTests.Build", Status = "Failed", Duration = duration, Message = message };
        }

        [Test]
        public void TheTestsMaxTimeIsItsLimit()
        {
            var test = new TestResult { Status = "Passed", Duration = 2.5 };
            Runner.ApplyMaxTime(test, Method(nameof(LimitedTo3s)));
            Assert.That(test.MaxTime, Is.EqualTo(3));
            Assert.That(test.Status, Is.EqualTo("Passed"));
        }

        [Test]
        public void TestsWithoutMaxTimeKeepTheDefaultLimits()
        {
            var test = new TestResult { Status = "Passed", Duration = 2.5 };
            Runner.ApplyMaxTime(test, Method(nameof(Unlimited)));
            Assert.That(test.MaxTime, Is.Null);
        }

        [Test]
        public void NUnitsMaxTimeFailureIsReportedAsTooSlow()
        {
            var test = Failed("Elapsed time of 3200.5ms exceeds maximum of 3000ms", 3.2);
            Runner.ApplyMaxTime(test, Method(nameof(LimitedTo3s)));
            var outcome = new Outcome { State = Outcome.Tests, Duration = 0.5 };
            outcome.Results.Add(test);
            var text = Report.Format(outcome, "", "warm", "/p");
            Assert.That(text.Code, Is.EqualTo(Report.TestFailure));
            Assert.That(text.Body.Split('\n'), Is.EqualTo(new[]
            {
                "FAIL tests; total=1 passed=0 failed=1 too_slow=1 duration=0.50s mode=EditMode",
                "[1] LevelTests.Build",
                "  Took 3.2s; its [MaxTime] is 3s.",
            }));
        }

        [Test]
        public void OtherFailuresStayFailures()
        {
            var test = Failed("Expected: 1\n  But was:  2", 3.2);
            Runner.ApplyMaxTime(test, Method(nameof(LimitedTo3s)));
            Assert.That(test.Status, Is.EqualTo("Failed"));
            Assert.That(test.Message, Does.StartWith("Expected: 1"));
        }
    }
}
