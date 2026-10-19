using System.IO;
using NUnit.Framework;

namespace UnityVerify
{
    public class PackageChangesTests
    {
        const string ProjectCache = @"m_ProjectFiles:
  m_ManifestFileStatus:
    m_FilePath: /p/Packages/manifest.json
    m_PathExists: 1
    m_ContentTrackingEnabled: 1
    m_ModificationDate:
      serializedVersion: 2
      ticks: 639280127630000000
    m_Hash: 1531993715
  m_LockFileStatus:
    m_FilePath: /p/Packages/packages-lock.json
    m_PathExists: 1
    m_ContentTrackingEnabled: 1
    m_ModificationDate:
      serializedVersion: 2
      ticks: 639280128200000000
    m_Hash: 4163585407
m_EmbeddedPackageManifests:
  m_ManifestsStatus: {}
";

        string m_Path;

        [SetUp]
        public void WriteCache()
        {
            m_Path = Path.GetTempFileName();
            File.WriteAllText(m_Path, ProjectCache);
        }

        [TearDown]
        public void DeleteCache()
        {
            File.Delete(m_Path);
        }

        [Test]
        public void ReadsTheRecordedModificationTimes()
        {
            Assert.That(PackageChanges.RecordedSeconds(m_Path, "m_ManifestFileStatus:"), Is.EqualTo(63928012763));
            Assert.That(PackageChanges.RecordedSeconds(m_Path, "m_LockFileStatus:"), Is.EqualTo(63928012820));
        }

        [Test]
        public void MissingBlocksAreNotRecorded()
        {
            Assert.That(PackageChanges.RecordedSeconds(m_Path, "m_OtherFileStatus:"), Is.Null);
        }
    }
}
