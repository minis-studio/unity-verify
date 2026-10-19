using System;
using System.Diagnostics;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityVerify
{
    /// <summary>
    /// Answers the unity-verify script's requests in the open Editor, from
    /// EditorApplication.update. A request waits while the Editor plays, compiles,
    /// imports or reloads: play mode is exited first, and a request that a domain
    /// reload interrupts is run again by the next domain.
    /// </summary>
    [InitializeOnLoad]
    static class Mailbox
    {
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static readonly Action SignalTick = FindSignalTick();
        static readonly int ProcessId = Process.GetCurrentProcess().Id;
        static long s_NextBeat;
        static long s_NextCheck;
        static bool s_Running;
        static string s_StopSentFor;

        static Mailbox()
        {
            // A batch-mode Editor is driven by Batch.Run instead.
            if (Application.isBatchMode)
            {
                return;
            }
            Protocol.Start(ProcessId);
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            var now = Clock.ElapsedMilliseconds;
            if (now >= s_NextBeat)
            {
                s_NextBeat = now + 1000;
                Protocol.Beat(ProcessId);
            }
            if (s_Running)
            {
                // A background Editor ticks only a few times a second; keep it
                // ticking while a request needs updates.
                SignalTick?.Invoke();
                return;
            }
            if (now < s_NextCheck)
            {
                return;
            }
            s_NextCheck = now + 100;
            var request = Protocol.Next();
            if (request == null)
            {
                return;
            }
            SignalTick?.Invoke();
            s_Running = true;
            Handle(request);
        }

        static async void Handle(Protocol.Request request)
        {
            try
            {
                if (request.Header != Protocol.Version)
                {
                    Protocol.Respond(request, Protocol.Mismatch(request));
                    return;
                }
                Protocol.SetState(request, "running");
                var outcome = await Runner.Run(request.Filter);
                if (outcome.State == Outcome.Playing || outcome.State == Outcome.Busy)
                {
                    if (outcome.State == Outcome.Playing && s_StopSentFor != request.Id)
                    {
                        s_StopSentFor = request.Id;
                        EditorApplication.ExitPlaymode();
                    }
                    Protocol.SetState(request, outcome.State);
                    s_NextCheck = Clock.ElapsedMilliseconds + 250;
                    return;
                }
                Protocol.Respond(request, Report.Format(outcome, request.Filter, "warm", Protocol.ProjectRoot));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Protocol.Respond(request, Report.Error(
                    $"runner; cause={Report.Cause(exception.Message)}; fix=check the Editor Console"));
            }
            finally
            {
                s_Running = false;
            }
        }

        // EditorApplication.SignalTick is internal: it schedules the next Editor
        // update right away. Without it, requests are still answered, only slower.
        static Action FindSignalTick()
        {
            var method = typeof(EditorApplication).GetMethod(
                "SignalTick", BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
            return method == null ? null : (Action)Delegate.CreateDelegate(typeof(Action), method);
        }
    }
}
