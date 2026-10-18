using System.Diagnostics;
using NexusPipeline.Host.Desktop;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.Tests.ControlPlane;

public sealed class DesktopRelocationTests
{
    [Fact]
    public void RelocatedSignedTicketRetiresAtomicallyAndPreservesState()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-relocation-" + Guid.NewGuid().ToString("N"));
        string oldRoot = Path.Combine(root, "old"), nextRoot = Path.Combine(root, "next");
        string relative = Path.Combine(".nxp", "runtime", "desktop");
        try
        {
            var store = new DesktopSessionStore(Path.Combine(oldRoot, relative), DesktopSupervisorProtocol.RootHash(oldRoot));
            byte[] key = store.CreateKey();
            var identity = new DesktopProcessIdentity(int.MaxValue, "1", Path.Combine(oldRoot, "resources", "desktop", "NexusPipeline.Desktop.exe"));
            var record = new DesktopSessionRecord(1, DesktopSupervisorProtocol.RootHash(oldRoot), "g0170", new('b',32), new('c',64), new('d',32), identity, "hidden", "#/dashboard", "") { Family = [identity] };
            store.Save(record, key);
            string original = File.ReadAllText(Path.Combine(oldRoot, relative, "session.json"));
            File.WriteAllText(Path.Combine(oldRoot, "settings.json"), "keep-user-data");
            Directory.Move(oldRoot, nextRoot);
            var moved = new DesktopSessionStore(Path.Combine(nextRoot, relative), DesktopSupervisorProtocol.RootHash(nextRoot));
            Assert.Null(moved.Load());
            Assert.Equal("retired", moved.RecoverRelocated());
            Assert.False(moved.HasFiles);
            string retired = Assert.Single(Directory.GetDirectories(Path.Combine(nextRoot, ".nxp", "runtime"), "desktop.retired-*"));
            Assert.Equal(original, File.ReadAllText(Path.Combine(retired, "session.json")));
            Assert.Equal(key, File.ReadAllBytes(Path.Combine(retired, "session.key")));
            Assert.Equal("keep-user-data", File.ReadAllText(Path.Combine(nextRoot, "settings.json")));
            Assert.Equal("empty", moved.RecoverRelocated());
            Assert.NotEqual(key, moved.CreateKey());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void UnverifiedAndLiveTicketsArePreserved()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-relocation-invalid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DesktopSessionStore(root, new('a',64));
            byte[] key = store.CreateKey();
            var record = new DesktopSessionRecord(1,new('a',64),"g0170",new('b',32),new('c',64),new('d',32),DesktopProcessIdentity.Read(Environment.ProcessId),"hidden","#/dashboard","");
            store.Save(record,key);
            string bytes = File.ReadAllText(Path.Combine(root,"session.json"));
            Assert.Equal("current",store.RecoverRelocated());
            Assert.Equal("unverified",new DesktopSessionStore(root,new('e',64)).RecoverRelocated());
            string oldRoot = Path.Combine(root,"old");
            var liveRecord = record with { RootHash = DesktopSupervisorProtocol.RootHash(oldRoot),
                Main = record.Main with { ExecutablePath = Path.Combine(oldRoot,"resources","desktop","NexusPipeline.Desktop.exe") } };
            string liveDirectory = Path.Combine(root,"live");
            var liveStore = new DesktopSessionStore(liveDirectory,liveRecord.RootHash);
            byte[] liveKey = liveStore.CreateKey(); liveStore.Save(liveRecord,liveKey);
            var relocatedLive = new DesktopSessionStore(liveDirectory,new('e',64));
            Assert.Equal("active",relocatedLive.RecoverRelocated());
            Assert.True(relocatedLive.HasFiles);
            Assert.Equal(bytes,File.ReadAllText(Path.Combine(root,"session.json")));
            File.WriteAllText(Path.Combine(root,"session.json"),bytes.Replace("hidden","visible"));
            Assert.Equal("unverified",store.RecoverRelocated());
            File.Delete(Path.Combine(root,"session.json"));
            Assert.Equal("unverified",store.RecoverRelocated());
            Assert.Equal(key,File.ReadAllBytes(Path.Combine(root,"session.key")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
    }

    [Fact]
    public void RunningDirectoryCannotBeRenamedUntilLeaseEnds()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-directory-" + Guid.NewGuid().ToString("N"));
        string moved = root + "-moved";
        Directory.CreateDirectory(root);
        try
        {
            using (RunningDirectoryLease.Acquire(root))
            {
                File.WriteAllText(Path.Combine(root,"config.json"),"preserved");
                Assert.Throws<IOException>(()=>Directory.Move(root,moved));
            }
            Directory.Move(root,moved);
            Assert.Equal("preserved",File.ReadAllText(Path.Combine(moved,"config.json")));
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); if(Directory.Exists(moved)) Directory.Delete(moved,true); }
    }
}
