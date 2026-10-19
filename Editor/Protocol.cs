using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UnityVerify.Editor.Tests")]

namespace UnityVerify
{
    /// <summary>
    /// The files the unity-verify script and the Editor exchange, in Library/UnityVerify:
    ///
    ///   requests/ID.request    "unity-verify 1", then the test filter (may be empty)
    ///   responses/ID.state     what the Editor is doing with the request
    ///   responses/ID.response  the exit code, then the report
    ///   heartbeat              the Editor's process id; touched every second
    ///
    /// A request is deleted once its response is written, so one that a domain
    /// reload interrupts is picked up again by the next domain.
    /// </summary>
    static class Protocol
    {
        internal const string Version = "unity-verify 1";

        // Requests older than this were abandoned by their script.
        static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(15);

        internal static readonly string ProjectRoot = Path.GetFullPath(".").Replace('\\', '/');
        static readonly string Root = Path.Combine(ProjectRoot, "Library", "UnityVerify");
        static readonly string Requests = Path.Combine(Root, "requests");
        static readonly string Responses = Path.Combine(Root, "responses");
        static readonly string Heartbeat = Path.Combine(Root, "heartbeat");

        internal sealed class Request
        {
            public string Id;
            public string Path;
            public string Header;
            public string Filter;
        }

        internal static Request Read(string path)
        {
            var lines = File.ReadAllText(path).Replace("\r", "").Split('\n');
            return new Request
            {
                Id = System.IO.Path.GetFileNameWithoutExtension(path),
                Path = path,
                Header = lines[0],
                Filter = lines.Length > 1 ? lines[1] : "",
            };
        }

        /// <summary>The oldest pending request, or null.</summary>
        internal static Request Next()
        {
            if (!Directory.Exists(Requests))
            {
                return null;
            }
            foreach (var path in Directory.GetFiles(Requests, "*.request").OrderBy(File.GetLastWriteTimeUtc))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > Abandoned)
                {
                    File.Delete(path);
                    continue;
                }
                return Read(path);
            }
            return null;
        }

        internal static void Beat(int processId)
        {
            Directory.CreateDirectory(Root);
            if (File.Exists(Heartbeat))
            {
                File.SetLastWriteTimeUtc(Heartbeat, DateTime.UtcNow);
            }
            else
            {
                File.WriteAllText(Heartbeat, processId + "\n");
            }
        }

        internal static void Start(int processId)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Heartbeat, processId + "\n");
        }

        internal static void SetState(Request request, string state)
        {
            WriteAtomically(Path.Combine(Responses, request.Id + ".state"), state + "\n");
        }

        internal static void Respond(Request request, Report.Text text)
        {
            WriteAtomically(Path.Combine(Responses, request.Id + ".response"), text.Code + "\n" + text.Body + "\n");
            File.Delete(request.Path);
        }

        internal static Report.Text Mismatch(Request request)
        {
            return Report.Error($"protocol; got={Report.Cause(request.Header)}; want={Version}; " +
                "fix=update ./unity-verify by rerunning the installer");
        }

        static void WriteAtomically(string path, string text)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, text);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temporary, path);
        }
    }
}
