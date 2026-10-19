using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace UnityVerify
{
    /// <summary>The printed report and exit code for an outcome.</summary>
    static class Report
    {
        // Every test is expected to finish within ExpectedSeconds. Run-to-run
        // variance is allowed up to 4x that before a warning; a test over
        // FailSeconds fails even when its assertions pass.
        internal const double ExpectedSeconds = 0.05;
        internal const double WarnSeconds = 0.2;
        internal const double FailSeconds = 1.0;

        internal const int Success = 0;
        internal const int TestFailure = 1;
        internal const int CompilationFailure = 2;
        internal const int EnvironmentFailure = 3;

        static readonly Regex SourceLocation = new Regex(@"\bin\s+(.+?):(\d+)\s*$");
        static readonly Regex CompilerDiagnostic = new Regex(
            @"^(?<file>.+?)\((?<line>\d+),(?<column>\d+)\):\s*error\s+(?<code>[A-Za-z]+\d+):\s*(?<message>.+)$");
        static readonly Regex PlainCompilerError = new Regex(
            @"^error(?:\s+(?<code>[A-Za-z]+\d+))?:\s*(?<message>.+)$", RegexOptions.IgnoreCase);
        static readonly Regex AssemblyPath = new Regex(@"(?<file>(?:Assets|Packages)/.*?\.asm(?:def|ref))");

        internal struct Text
        {
            public int Code;
            public string Body;
        }

        internal static Text Format(Outcome outcome, string filter, string path, string projectRoot)
        {
            switch (outcome.State)
            {
                case Outcome.Tests:
                    return Tests(outcome, filter, projectRoot);
                case Outcome.CompileFailed:
                    return CompileErrors(outcome.Diagnostics, path, projectRoot);
                default:
                    return Error($"runner; cause=unexpected state {outcome.State}; fix=update unity-verify");
            }
        }

        internal static Text Error(string fields)
        {
            return new Text { Code = EnvironmentFailure, Body = "ERR " + fields };
        }

        /// <summary>One compact, field-safe cause.</summary>
        internal static string Cause(string detail)
        {
            var lines = CompactMessage(detail);
            return lines.Count > 0 ? Truncate(lines[0].Replace(";", ","), 160) : "unknown";
        }

        static Text CompileErrors(List<Diagnostic> found, string path, string projectRoot)
        {
            var diagnostics = found
                .Select(item => Parse(item, projectRoot))
                .Where(item => item != null)
                .Distinct()
                .OrderBy(item => item.File.ToLowerInvariant(), StringComparer.Ordinal)
                .ThenBy(item => item.Line)
                .ThenBy(item => item.Column)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.Message, StringComparer.Ordinal)
                .ToList();
            if (diagnostics.Count == 0)
            {
                return Error($"compile_verify; stage={path}; cause=Unity reports compilation errors " +
                    "but returned no diagnostics; tests=skipped");
            }
            var lines = new List<string> { $"ERR compile_failed; errors={diagnostics.Count}; tests=skipped" };
            lines.AddRange(diagnostics.Select(item => item.ToString()));
            return new Text { Code = CompilationFailure, Body = string.Join("\n", lines) };
        }

        sealed class CompileError : IEquatable<CompileError>
        {
            public string File = "";
            public int Line;
            public int Column;
            public string Code = "";
            public string Message = "";

            public override string ToString()
            {
                var location = File;
                if (location.Length > 0 && Line > 0)
                {
                    location += ":" + Line + (Column > 0 ? ":" + Column : "");
                }
                var label = "error" + (Code.Length > 0 ? " " + Code : "");
                return string.Join(" ", new[] { location, label + ":", Message }.Where(part => part.Length > 0));
            }

            public bool Equals(CompileError other)
            {
                return other != null && File == other.File && Line == other.Line && Column == other.Column &&
                    Code == other.Code && Message == other.Message;
            }

            public override bool Equals(object other) => Equals(other as CompileError);
            public override int GetHashCode() => (File + "|" + Line + "|" + Column + "|" + Code + "|" + Message).GetHashCode();
        }

        static CompileError Parse(Diagnostic diagnostic, string projectRoot)
        {
            var lines = (diagnostic.Message ?? "").Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
            if (lines.Count == 0)
            {
                return null;
            }
            var text = lines.FirstOrDefault(line => CompilerDiagnostic.IsMatch(line)) ?? lines[0];

            var match = CompilerDiagnostic.Match(text);
            if (match.Success)
            {
                return new CompileError
                {
                    File = CleanPath(match.Groups["file"].Value, projectRoot),
                    Line = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture),
                    Column = int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture),
                    Code = match.Groups["code"].Value.ToUpperInvariant(),
                    Message = match.Groups["message"].Value.Trim(),
                };
            }

            var code = "";
            var plain = PlainCompilerError.Match(text);
            if (plain.Success)
            {
                code = plain.Groups["code"].Value.ToUpperInvariant();
                text = plain.Groups["message"].Value.Trim();
            }
            var file = CleanPath(diagnostic.File, projectRoot);
            if (file.Length == 0)
            {
                var assembly = AssemblyPath.Match(text);
                if (assembly.Success)
                {
                    file = CleanPath(assembly.Groups["file"].Value, projectRoot);
                }
            }
            return new CompileError
            {
                File = file,
                Line = Math.Max(diagnostic.Line, 0),
                Column = Math.Max(diagnostic.Column, 0),
                Code = code,
                Message = text,
            };
        }

        static string CleanPath(string path, string projectRoot)
        {
            var value = (path ?? "").Trim().Replace('\\', '/');
            var root = (projectRoot ?? "").Replace('\\', '/').TrimEnd('/');
            if (root.Length > 0 && value.StartsWith(root + "/", StringComparison.Ordinal))
            {
                value = value.Substring(root.Length + 1);
            }
            return value.StartsWith("./", StringComparison.Ordinal) ? value.Substring(2) : value;
        }

        static Text Tests(Outcome outcome, string filter, string projectRoot)
        {
            var results = outcome.Results;
            int Count(string status) => results.Count(result => result.Status == status);
            var total = results.Count;
            var passed = Count("Passed");
            var failed = Count("Failed");
            var skipped = Count("Skipped");
            var inconclusive = Count("Inconclusive");

            if (total == 0)
            {
                var none = Error($"tests_none; filter={(string.IsNullOrEmpty(filter) ? "none" : filter)}; " +
                    "mode=EditMode; fix=check filter/tests");
                var notRun = new List<string> { none.Body };
                AddFrameTests(outcome, notRun);
                none.Body = string.Join("\n", notRun);
                return none;
            }

            // Slowest first. Passed tests over the limit count as failures.
            var byTime = results.OrderByDescending(result => result.Duration).ToList();
            var tooSlow = byTime.Where(result => result.Status == "Passed" && result.Duration > FailSeconds).ToList();
            var slow = byTime.Where(result => result.Duration > WarnSeconds && result.Duration <= FailSeconds).ToList();
            passed -= tooSlow.Count;
            failed += tooSlow.Count;

            var pass = failed == 0 && inconclusive == 0;
            var fields = new List<string> { $"total={total}", $"passed={passed}", $"failed={failed}" };
            if (tooSlow.Count > 0)
            {
                fields.Add($"too_slow={tooSlow.Count}");
            }
            if (skipped > 0)
            {
                fields.Add($"skipped={skipped}");
            }
            if (inconclusive > 0)
            {
                fields.Add($"inconclusive={inconclusive}");
            }
            if (outcome.FrameTests.Count > 0)
            {
                fields.Add($"frame_skipped={outcome.FrameTests.Count}");
            }
            fields.Add("duration=" + outcome.Duration.ToString("0.00", CultureInfo.InvariantCulture) + "s");
            fields.Add("mode=EditMode");
            if (!string.IsNullOrEmpty(filter))
            {
                fields.Add("filter=" + filter);
            }
            var lines = new List<string> { (pass ? "PASS" : "FAIL") + " tests; " + string.Join(" ", fields) };

            var index = 0;
            foreach (var failure in results.Where(result => result.Status == "Failed"))
            {
                lines.Add($"[{++index}] {Name(failure)}");
                lines.AddRange(CompactMessage(failure.Message).Select(line => "  " + line));
                lines.AddRange(CompactLocations(failure.StackTrace, projectRoot).Select(location => "  at " + location));
            }
            var expectation = $"tests fail over {FormatSeconds(FailSeconds)} and are expected " +
                $"to finish in under {FormatSeconds(ExpectedSeconds)}";
            foreach (var result in tooSlow)
            {
                lines.Add($"[{++index}] {Name(result)}");
                lines.Add($"  Took {FormatSeconds(result.Duration)}; {expectation}.");
            }

            if (slow.Count > 0)
            {
                lines.Add($"WARN slow_tests; count={slow.Count}; over={FormatSeconds(WarnSeconds)}; hint={expectation}");
                lines.AddRange(slow.Select(result => $"  {FormatSeconds(result.Duration)} {Name(result)}"));
            }

            AddFrameTests(outcome, lines);
            return new Text { Code = pass ? Success : TestFailure, Body = string.Join("\n", lines) };
        }

        // Tests that wait for frames don't run; name them, so what they cover counts
        // as unverified.
        static void AddFrameTests(Outcome outcome, List<string> lines)
        {
            if (outcome.FrameTests.Count == 0)
            {
                return;
            }
            lines.Add($"WARN frame_tests_skipped; count={outcome.FrameTests.Count}; " +
                "hint=[UnityTest], [UnitySetUp] and [UnityTearDown] tests don't run; " +
                "write [Test]s that call Editor-update code directly");
            lines.AddRange(outcome.FrameTests.Select(name => "  " + name));
        }

        static string Name(TestResult result)
        {
            return string.IsNullOrEmpty(result.FullName) ? "<unnamed test>" : result.FullName;
        }

        internal static string FormatSeconds(double seconds)
        {
            return seconds >= 1
                ? seconds.ToString("G3", CultureInfo.InvariantCulture) + "s"
                : (seconds * 1000).ToString("G3", CultureInfo.InvariantCulture) + "ms";
        }

        internal static List<string> CompactMessage(string message)
        {
            var lines = (message ?? "").Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
            var kept = lines.Take(12).Select(line => line.Length > 300 ? line.Substring(0, 300) + "..." : line).ToList();
            if (lines.Count > kept.Count)
            {
                kept.Add($"... {lines.Count - kept.Count} more message lines");
            }
            return kept;
        }

        static List<string> CompactLocations(string stackTrace, string projectRoot)
        {
            var locations = new List<string>();
            var fallback = new List<string>();
            var root = (projectRoot ?? "").TrimEnd('/') + "/";
            foreach (var rawLine in (stackTrace ?? "").Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }
                fallback.Add(line);
                var match = SourceLocation.Match(line);
                if (!match.Success)
                {
                    continue;
                }
                var path = match.Groups[1].Value;
                if (root.Length > 1 && path.StartsWith(root, StringComparison.Ordinal))
                {
                    path = path.Substring(root.Length);
                }
                var location = path + ":" + match.Groups[2].Value;
                if (!locations.Contains(location))
                {
                    locations.Add(location);
                }
            }
            return locations.Count > 0 ? locations.Take(3).ToList() : fallback.Take(1).ToList();
        }

        static string Truncate(string text, int length)
        {
            return text.Length > length ? text.Substring(0, length) : text;
        }
    }
}
