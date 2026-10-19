using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace UnityVerify
{
    /// <summary>
    /// Answers one request in a batch-mode Editor, then exits with its code:
    ///
    ///   Unity -batchmode -projectPath PROJECT
    ///     -executeMethod UnityVerify.Batch.Run -unityVerifyRequest FILE
    ///
    /// When the project doesn't compile, batch mode stops before this runs, and
    /// the unity-verify script reads the errors from the Editor's log.
    /// </summary>
    public static class Batch
    {
        static readonly TimeSpan BusyLimit = TimeSpan.FromMinutes(2);

        public static void Run()
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "-unityVerifyRequest");
            if (index < 0 || index + 1 >= args.Length)
            {
                Debug.LogError("unity-verify: -unityVerifyRequest FILE is required");
                EditorApplication.Exit(Report.EnvironmentFailure);
                return;
            }
            Execute(Protocol.Read(args[index + 1]));
        }

        static async void Execute(Protocol.Request request)
        {
            Report.Text text;
            try
            {
                text = request.Header == Protocol.Version ? await Verify(request) : Protocol.Mismatch(request);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                text = Report.Error($"runner; cause={Report.Cause(exception.Message)}; fix=check Editor/log");
            }
            Protocol.Respond(request, text);
            EditorApplication.Exit(text.Code);
        }

        static async Task<Report.Text> Verify(Protocol.Request request)
        {
            var waited = Stopwatch.StartNew();
            while (true)
            {
                var outcome = await Runner.Run(request.Filter);
                if (outcome.State != Outcome.Busy)
                {
                    return Report.Format(outcome, request.Filter, "cold", Protocol.ProjectRoot);
                }
                if (waited.Elapsed > BusyLimit)
                {
                    return Report.Error($"busy; cause=Editor stayed busy for {BusyLimit.TotalSeconds:0}s; fix=check Editor/log");
                }
                await Task.Delay(250);
            }
        }
    }
}
