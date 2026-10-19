// Compiled in memory and run inside the open Editor by unity-verify, through
// Unity Pipeline's run_script command. It lives in Tools~, so Unity never
// imports it into the project.

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

public static class UnityVerifyRunner
{
    const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static;

    sealed class Collector : ICallbacks
    {
        public readonly List<ITestResultAdaptor> Results = new List<ITestResultAdaptor>();
        public readonly TaskCompletionSource<bool> Finished = new TaskCompletionSource<bool>();

        public void RunStarted(ITestAdaptor testsToRun) { }
        public void TestStarted(ITestAdaptor test) { }

        public void TestFinished(ITestResultAdaptor result)
        {
            if (!result.Test.IsSuite)
            {
                Results.Add(result);
            }
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            Finished.TrySetResult(true);
        }
    }

    /// <summary>
    /// Compile the project if needed and run its EditMode tests.
    ///
    /// Returns a "state": "playing" or "busy" (compiling, importing or reloading)
    /// ask the caller to settle the Editor and call again; "compile_failed"
    /// carries diagnostics; "tests" carries every test's result and duration,
    /// which the caller checks against its time limits. Tests run with the
    /// Test Framework's runSynchronously, inside this one call, so they don't
    /// wait for Editor updates. Tests that need frames ([UnityTest],
    /// [UnitySetUp], [UnityTearDown]) are skipped by that mode and run
    /// afterwards in a normal pass. Every reply also carries the version of
    /// com.unity.pipeline running it, which the caller checks.
    /// </summary>
    public static async Task<object> Run(string filter)
    {
        var reply = await Verify(filter);
        reply["pipeline"] = UnityEditor.PackageManager.PackageInfo
            .FindForPackageName("com.unity.pipeline")?.version ?? "";
        return reply;
    }

    static async Task<Dictionary<string, object>> Verify(string filter)
    {
        if (EditorApplication.isPlaying)
        {
            return new Dictionary<string, object>
            {
                ["state"] = "playing",
                ["detail"] = EditorApplication.isPaused ? "paused" : "playing",
            };
        }
        if (!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling)
        {
            AssetDatabase.Refresh();
        }
        if (IsBusy())
        {
            return new Dictionary<string, object> { ["state"] = "busy" };
        }

        var compile = CompileErrors();
        if (compile.Failed || compile.Diagnostics.Count > 0)
        {
            return new Dictionary<string, object>
            {
                ["state"] = "compile_failed",
                ["diagnostics"] = compile.Diagnostics,
            };
        }

        var total = Stopwatch.StartNew();
        var api = ScriptableObject.CreateInstance<TestRunnerApi>();
        try
        {
            // Listing waits for Editor updates, so a full run skips it unless
            // a second pass may be needed.
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
            var frameNames = names.Where(name => !ran.Contains(name)).ToArray();
            var frames = new Collector();
            var frameWatch = Stopwatch.StartNew();
            if (frameNames.Length > 0)
            {
                api.RegisterCallbacks(frames);
                try
                {
                    api.Execute(new ExecutionSettings(
                        new Filter { testMode = TestMode.EditMode, testNames = frameNames }));
                    await frames.Finished.Task;
                }
                finally
                {
                    api.UnregisterCallbacks(frames);
                }
            }

            var results = sync.Results.Concat(frames.Results).ToList();
            return new Dictionary<string, object>
            {
                ["state"] = "tests",
                ["Summary"] = new Dictionary<string, object>
                {
                    ["Total"] = results.Count,
                    ["Passed"] = Count(results, TestStatus.Passed),
                    ["Failed"] = Count(results, TestStatus.Failed),
                    ["Skipped"] = Count(results, TestStatus.Skipped),
                    ["Inconclusive"] = Count(results, TestStatus.Inconclusive),
                },
                ["Results"] = results.Select(Describe).ToList(),
                ["Duration"] = Math.Round(total.Elapsed.TotalSeconds, 2),
                ["Mode"] = "EditMode",
                ["FrameTests"] = frames.Results.Count,
                ["FrameDuration"] = Math.Round(frameNames.Length > 0 ? frameWatch.Elapsed.TotalSeconds : 0, 2),
            };
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

    static int Count(List<ITestResultAdaptor> results, TestStatus status)
    {
        return results.Count(result => result.TestStatus == status);
    }

    static Dictionary<string, object> Describe(ITestResultAdaptor result)
    {
        return new Dictionary<string, object>
        {
            ["FullName"] = result.Test.FullName,
            ["Status"] = result.TestStatus.ToString(),
            ["Duration"] = Math.Round(result.Duration, 4),
            ["Message"] = result.Message,
            ["StackTrace"] = result.StackTrace,
        };
    }

    // Leaf tests whose fixture or method name contains the filter. Arguments
    // are left out of the match, as in the cold path.
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

    static bool IsBusy()
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
        public readonly List<Dictionary<string, object>> Diagnostics = new List<Dictionary<string, object>>();

        public void Add(string message, string file, int line, int column)
        {
            Diagnostics.Add(new Dictionary<string, object>
            {
                ["message"] = message,
                ["file"] = file,
                ["line"] = line,
                ["column"] = column,
            });
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
