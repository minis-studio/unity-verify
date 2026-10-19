using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityVerify
{
    /// <summary>What one verification found.</summary>
    sealed class Outcome
    {
        public const string Playing = "playing";
        public const string Busy = "busy";
        public const string CompileFailed = "compile_failed";
        public const string Tests = "tests";

        public string State;
        public readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
        public readonly List<TestResult> Results = new List<TestResult>();
        public double Duration;
        // Tests that wait for frames, which don't run.
        public readonly List<string> FrameTests = new List<string>();
    }

    sealed class Diagnostic
    {
        public string Message;
        public string File;
        public int Line;
        public int Column;
    }

    sealed class TestResult
    {
        public string FullName;
        public string Status;
        public double Duration;
        public string Message;
        public string StackTrace;
    }

    static class Runner
    {
        const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;

        sealed class Collector : ICallbacks
        {
            public readonly List<ITestResultAdaptor> Results = new List<ITestResultAdaptor>();

            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (!result.Test.IsSuite)
                {
                    Results.Add(result);
                }
            }

            public void RunFinished(ITestResultAdaptor result) { }
        }

        /// <summary>
        /// Compile the project if needed and run its EditMode tests.
        ///
        /// "playing" and "busy" (compiling, importing or reloading) ask the caller to
        /// settle the Editor and run again. Tests run with the Test Framework's
        /// runSynchronously, inside this one call, so they don't wait for Editor
        /// updates. Tests that wait for frames ([UnityTest], [UnitySetUp],
        /// [UnityTearDown]) can't run that way, so they don't run; the outcome
        /// names them.
        /// </summary>
        internal static async Task<Outcome> Run(string filter)
        {
            if (EditorApplication.isPlaying)
            {
                return new Outcome { State = Outcome.Playing };
            }
            if (!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling)
            {
                AssetDatabase.Refresh();
            }
            if (IsBusy())
            {
                return new Outcome { State = Outcome.Busy };
            }

            var compile = CompileErrors();
            if (compile.Failed || compile.Diagnostics.Count > 0)
            {
                var failed = new Outcome { State = Outcome.CompileFailed };
                failed.Diagnostics.AddRange(compile.Diagnostics);
                return failed;
            }

            var total = Stopwatch.StartNew();
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            try
            {
                // Listing waits for Editor updates, so a full run skips it unless
                // there may be frame tests to name.
                var names = new List<string>();
                var listed = !string.IsNullOrEmpty(filter) || MayNeedFrames();
                if (listed)
                {
                    var tree = new TaskCompletionSource<ITestAdaptor>();
                    api.RetrieveTestList(TestMode.EditMode, root => tree.TrySetResult(root));
                    CollectTests(await tree.Task, filter ?? "", names);
                }

                var sync = new Collector();
                if (!listed || names.Count > 0)
                {
                    // A full run selects by mode only; NUnit then leaves [Explicit]
                    // tests out by itself, without matching thousands of names.
                    var selection = string.IsNullOrEmpty(filter)
                        ? new Filter { testMode = TestMode.EditMode }
                        : new Filter { testMode = TestMode.EditMode, testNames = names.ToArray() };
                    api.RegisterCallbacks(sync);
                    try
                    {
                        api.Execute(new ExecutionSettings(selection) { runSynchronously = true });
                    }
                    finally
                    {
                        api.UnregisterCallbacks(sync);
                    }
                }

                var ran = new HashSet<string>(sync.Results.Select(result => result.Test.FullName));
                var outcome = new Outcome { State = Outcome.Tests, Duration = total.Elapsed.TotalSeconds };
                outcome.FrameTests.AddRange(names.Where(name => !ran.Contains(name)));
                outcome.Results.AddRange(sync.Results.Select(result => new TestResult
                {
                    FullName = result.Test.FullName,
                    Status = result.TestStatus.ToString(),
                    Duration = result.Duration,
                    Message = result.Message,
                    StackTrace = result.StackTrace,
                }));
                return outcome;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(api);
            }
        }

        // Whether a loaded test may be one that runSynchronously leaves out: one
        // that returns something other than void or Task (IEnumerator for
        // [UnityTest]), has an outer Unity test action, or whose fixture has
        // [UnitySetUp] or [UnityTearDown]. Mirrors the Test Framework's
        // SynchronousFilter, but errs towards true.
        static bool MayNeedFrames()
        {
            if (TypeCache.GetMethodsWithAttribute<UnityEngine.TestTools.UnitySetUpAttribute>().Count > 0 ||
                TypeCache.GetMethodsWithAttribute<UnityEngine.TestTools.UnityTearDownAttribute>().Count > 0)
            {
                return true;
            }
            var outerActions = TypeCache.GetTypesDerivedFrom<UnityEngine.TestTools.IOuterUnityTestAction>();
            if (outerActions.Any(type => IsAttribute(type) && TypeCache.GetMethodsWithAttribute(type).Count > 0))
            {
                return true;
            }
            var builders = TypeCache.GetTypesDerivedFrom<NUnit.Framework.Interfaces.ITestBuilder>()
                .Concat(TypeCache.GetTypesDerivedFrom<NUnit.Framework.Interfaces.ISimpleTestBuilder>())
                .Where(IsAttribute);
            return builders.Any(type => TypeCache.GetMethodsWithAttribute(type)
                .Any(method => method.ReturnType != typeof(void) && method.ReturnType != typeof(Task)));
        }

        static bool IsAttribute(Type type)
        {
            return !type.IsAbstract && typeof(Attribute).IsAssignableFrom(type);
        }

        // Leaf tests whose fixture or method name contains the filter. Arguments
        // are left out of the match.
        static void CollectTests(ITestAdaptor test, string filter, List<string> names)
        {
            if (test == null || IsExplicit(test))
            {
                return;
            }
            if (!test.IsSuite && !test.HasChildren)
            {
                var name = test.FullName.Split('(')[0];
                if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    names.Add(test.FullName);
                }
            }
            if (test.Children != null)
            {
                foreach (var child in test.Children)
                {
                    CollectTests(child, filter, names);
                }
            }
        }

        // Unity does not always carry a fixture's [Explicit] down to its tests.
        static bool IsExplicit(ITestAdaptor test)
        {
            if (test.RunState == RunState.Explicit)
            {
                return true;
            }
            var method = test.Method?.MethodInfo;
            var type = test.TypeInfo?.Type;
            return (method != null && method.GetCustomAttributes(true).Any(IsExplicitAttribute)) ||
                (type != null && type.GetCustomAttributes(true).Any(IsExplicitAttribute));
        }

        static bool IsExplicitAttribute(object attribute)
        {
            return attribute.GetType().Name == "ExplicitAttribute";
        }

        static object CompilationInterface()
        {
            var type = typeof(Editor).Assembly.GetType(
                "UnityEditor.Scripting.ScriptCompilation.EditorCompilationInterface", true);
            var instance = type.GetProperty("Instance", AnyMember);
            if (instance == null)
            {
                throw new Exception("EditorCompilationInterface.Instance is unavailable");
            }
            return instance.GetValue(null);
        }

        internal static bool IsBusy()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return true;
            }
            var compilation = CompilationInterface();
            var pending = compilation.GetType().GetMethod("IsScriptCompilationRequested", AnyMember);
            if (pending == null)
            {
                throw new Exception("Unity compiler state API is unavailable");
            }
            return (bool)pending.Invoke(compilation, null);
        }

        sealed class CompileState
        {
            public bool Failed;
            public readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();

            public void Add(string message, string file, int line, int column)
            {
                Diagnostics.Add(new Diagnostic { Message = message, File = file, Line = line, Column = column });
            }
        }

        // Script compiler errors, assembly setup errors (asmdef/asmref problems)
        // and compile errors shown in the Console, which Unity keeps through a
        // failed compile.
        static CompileState CompileErrors()
        {
            var state = new CompileState { Failed = EditorUtility.scriptCompilationFailed };
            var compilation = CompilationInterface();
            var compilationType = compilation.GetType();
            var getMessages = compilationType.GetMethod("GetCompileMessages", AnyMember);
            var setupProperty = compilationType.GetProperty("CompilationSetupErrorsTracker", AnyMember);
            var loadingProperty = compilationType.GetProperty("loadingAssemblyDefinition", AnyMember);
            if (getMessages == null || setupProperty == null || loadingProperty == null)
            {
                throw new Exception("Unity compiler diagnostics API is unavailable");
            }

            foreach (var message in (IEnumerable)getMessages.Invoke(compilation, null))
            {
                var messageType = message.GetType();
                if (messageType.GetField("type", AnyMember).GetValue(message).ToString() != "Error")
                {
                    continue;
                }
                state.Add(
                    (string)messageType.GetField("message", AnyMember).GetValue(message),
                    (string)messageType.GetField("file", AnyMember).GetValue(message),
                    (int)messageType.GetField("line", AnyMember).GetValue(message),
                    (int)messageType.GetField("column", AnyMember).GetValue(message));
            }

            var setupTracker = setupProperty.GetValue(compilation);
            var hasSetupErrors = (bool)setupTracker.GetType()
                .GetMethod("HaveCompilationSetupErrors", AnyMember).Invoke(setupTracker, null);
            if (hasSetupErrors)
            {
                state.Failed = true;
                var loading = loadingProperty.GetValue(compilation);
                var exceptions = loading.GetType().GetProperty("Exceptions", AnyMember)?.GetValue(loading) as IEnumerable;
                if (exceptions != null)
                {
                    foreach (Exception exception in exceptions)
                    {
                        state.Add(exception.Message, "", 0, 0);
                    }
                }
            }

            AddConsoleCompileErrors(state);
            return state;
        }

        static void AddConsoleCompileErrors(CompileState state)
        {
            var editorAssembly = typeof(Editor).Assembly;
            var logEntries = editorAssembly.GetType("UnityEditor.LogEntries", true);
            var logEntry = editorAssembly.GetType("UnityEditor.LogEntry", true);
            var flagsProperty = logEntries.GetProperty("consoleFlags", AnyMember);
            var getFilter = logEntries.GetMethod("GetFilteringText", AnyMember);
            var setFilter = logEntries.GetMethod("SetFilteringText", AnyMember);
            var startEntries = logEntries.GetMethod("StartGettingEntries", AnyMember);
            var endEntries = logEntries.GetMethod("EndGettingEntries", AnyMember);
            var getEntry = logEntries.GetMethod("GetEntryInternal", AnyMember);
            if (flagsProperty == null || getFilter == null || setFilter == null ||
                startEntries == null || endEntries == null || getEntry == null)
            {
                throw new Exception("Unity native Console diagnostics API is unavailable");
            }

            // Show every entry while reading, then restore the user's Console view.
            var originalFlags = (int)flagsProperty.GetValue(null);
            var originalFilter = (string)getFilter.Invoke(null, null);
            try
            {
                flagsProperty.SetValue(null, (originalFlags & ~1) | (1 << 7) | (1 << 8) | (1 << 9));
                setFilter.Invoke(null, new object[] { "" });
                var count = (int)startEntries.Invoke(null, null);
                try
                {
                    for (var index = 0; index < count; index++)
                    {
                        var entry = Activator.CreateInstance(logEntry);
                        if (!(bool)getEntry.Invoke(null, new object[] { index, entry }))
                        {
                            throw new Exception("Could not read Unity Console entry " + index);
                        }
                        var mode = (int)logEntry.GetField("mode", AnyMember).GetValue(entry);
                        if ((mode & (1 << 11)) == 0)
                        {
                            continue;
                        }
                        state.Add(
                            (string)logEntry.GetField("message", AnyMember).GetValue(entry),
                            (string)logEntry.GetField("file", AnyMember).GetValue(entry),
                            (int)logEntry.GetField("line", AnyMember).GetValue(entry),
                            (int)logEntry.GetField("column", AnyMember).GetValue(entry));
                    }
                }
                finally
                {
                    endEntries.Invoke(null, null);
                }
            }
            finally
            {
                setFilter.Invoke(null, new object[] { originalFilter });
                flagsProperty.SetValue(null, originalFlags);
            }
        }
    }
}
