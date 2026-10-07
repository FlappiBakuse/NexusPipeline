using NexusPipeline.Host.Desktop;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.Tests.ControlPlane;

public sealed class DesktopSessionStoreTests
{
    [Fact]
    public void FailedLaunchTicketRemovalOnlyDeletesTheKeyCreatedByThisAttempt()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-desktop-ticket-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DesktopSessionStore(root, new('a', 64));
            byte[] first = store.CreateKey();
            Assert.Throws<IOException>(() => store.RemoveCreatedKey(new byte[32]));
            Assert.Equal(first, File.ReadAllBytes(Path.Combine(root, "session.key")));
            store.RemoveCreatedKey(first);
            store.RemoveCreatedKey(first);
            Assert.False(store.HasFiles);
            byte[] next = store.CreateKey();
            Assert.NotEqual(first, next);
            File.WriteAllText(Path.Combine(root, "session.json"), "unknown");
            Assert.Throws<IOException>(() => store.RemoveCreatedKey(next));
            Assert.Equal("unknown", File.ReadAllText(Path.Combine(root, "session.json")));
            Assert.Equal(next, File.ReadAllBytes(Path.Combine(root, "session.key")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PrivateSessionCreationAndSignatureRejectTamperedRecord()
    {
        string root=Path.Combine(Path.GetTempPath(),"nxp-private-desktop-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new DesktopSessionStore(root,new('a',64));
            byte[] key=store.CreateKey();
            var record=new DesktopSessionRecord(1,new('a',64),"g0170",new('b',32),new('c',64),new('d',32),DesktopProcessIdentity.Read(Environment.ProcessId),"hidden","#/dashboard","");
            store.Save(record,key);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(record),System.Text.Json.JsonSerializer.Serialize(store.Load()!.Value.Record));
            string file=Path.Combine(root,"session.json");
            File.WriteAllText(file,File.ReadAllText(file).Replace("hidden","visible"));
            Assert.Null(store.Load());
            string unknown = File.ReadAllText(file);
            Assert.Throws<InvalidDataException>(() => store.Save(record, key));
            Assert.Equal(unknown, File.ReadAllText(file));
            Assert.Equal(key,File.ReadAllBytes(Path.Combine(root,"session.key")));
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
