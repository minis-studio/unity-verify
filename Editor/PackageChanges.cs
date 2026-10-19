using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;

namespace UnityVerify
{
    /// <summary>
    /// Unity reads Packages/manifest.json and packages-lock.json again when the
    /// Editor gets focus, so a background Editor keeps the packages it had. This
    /// asks the Package Manager to resolve when either file changed since it last
    /// read them, as recorded in Library/PackageManager/ProjectCache.
    /// </summary>
    static class PackageChanges
    {
        const string RequestedKey = "UnityVerify.ResolveRequestedFor";
        const string RequestedAtKey = "UnityVerify.ResolveRequestedAt";

        // How long to wait for a requested resolve to be recorded before running
        // anyway.
        static readonly TimeSpan ResolveLimit = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Whether to wait for the Package Manager: true once the files changed,
        /// after requesting a resolve, until it has recorded them.
        /// </summary>
        internal static bool Pending()
        {
            var stamp = ChangedStamp();
            if (stamp == null)
            {
                return false;
            }
            if (SessionState.GetString(RequestedKey, "") != stamp)
            {
                SessionState.SetString(RequestedKey, stamp);
                SessionState.SetString(RequestedAtKey, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                Client.Resolve();
                return true;
            }
            long.TryParse(SessionState.GetString(RequestedAtKey, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestedAt);
            return DateTime.UtcNow - new DateTime(requestedAt, DateTimeKind.Utc) < ResolveLimit;
        }

        // The files' modification times when they differ from what the Package
        // Manager recorded, or null when they match or can't be compared.
        static string ChangedStamp()
        {
            var root = Path.GetFullPath(".");
            var cache = Path.Combine(root, "Library", "PackageManager", "ProjectCache");
            var manifest = Path.Combine(root, "Packages", "manifest.json");
            var lockFile = Path.Combine(root, "Packages", "packages-lock.json");
            if (!File.Exists(cache) || !File.Exists(manifest))
            {
                return null;
            }
            var recordedManifest = RecordedSeconds(cache, "m_ManifestFileStatus:");
            var recordedLock = RecordedSeconds(cache, "m_LockFileStatus:");
            if (recordedManifest == null)
            {
                return null;
            }
            var manifestSeconds = Seconds(manifest);
            var lockSeconds = File.Exists(lockFile) ? Seconds(lockFile) : (long?)null;
            var changed = manifestSeconds != recordedManifest ||
                (recordedLock != null && lockSeconds != null && lockSeconds != recordedLock);
            return changed ? manifestSeconds + "/" + lockSeconds : null;
        }

        static long Seconds(string path)
        {
            return File.GetLastWriteTimeUtc(path).Ticks / TimeSpan.TicksPerSecond;
        }

        // The modification time (UTC, whole seconds) recorded under a file's status
        // block, e.g.
        //   m_ManifestFileStatus:
        //     m_FilePath: .../Packages/manifest.json
        //     m_ModificationDate:
        //       ticks: 639280127630000000
        internal static long? RecordedSeconds(string cache, string block)
        {
            using (var reader = new StreamReader(cache))
            {
                var inBlock = false;
                string line;
                for (var count = 0; count < 100 && (line = reader.ReadLine()) != null; count++)
                {
                    var trimmed = line.Trim();
                    if (trimmed == block)
                    {
                        inBlock = true;
                    }
                    else if (inBlock && trimmed.StartsWith("ticks:", StringComparison.Ordinal) &&
                        long.TryParse(trimmed.Substring(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                    {
                        return ticks / TimeSpan.TicksPerSecond;
                    }
                    else if (inBlock && trimmed.EndsWith("Status:", StringComparison.Ordinal))
                    {
                        return null;
                    }
                }
            }
            return null;
        }
    }
}
