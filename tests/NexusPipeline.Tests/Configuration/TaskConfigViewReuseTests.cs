using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class TaskConfigViewReuseTests
{
    [Fact]
    public void RestrictedOkRuntimeAllowsOnlyOperationalAppFieldsToChange()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-ok-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "config.json");
            string app = Path.Combine(root, "app.json");
            string head = Path.Combine(root, "HEAD");
            const string originalApp = """{"name":"ok-nte","current_version":"v1.4.2","update_state":"idle","running":false,"last_start":"old"}""";
            File.WriteAllText(config, "{\"tasks\":[]}");
            File.WriteAllText(app, originalApp);
            File.WriteAllText(head, "original-head");
            var view = new TaskConfigView();
            view.AddConfig("config:DailyRoutineTask.json", config, "json");
            view.AddResource("runtime-app", app, "json");
            view.AddResource("runtime-head", head, "text");

            File.WriteAllText(app, originalApp.Replace("false", "true").Replace("old", "new"));
            view.VerifyRestrictedOkRuntimeUnchanged();
            Assert.Throws<InvalidDataException>(() => view.VerifyUnchanged());
            File.WriteAllText(app, originalApp.Replace(",\"last_start\":\"old\"", ""));
            view.VerifyRestrictedOkRuntimeUnchanged();

            File.WriteAllText(app, originalApp.Replace("v1.4.2", "v1.4.3"));
            Assert.Throws<InvalidDataException>(() => view.VerifyRestrictedOkRuntimeUnchanged());
            File.WriteAllText(app, originalApp.Replace("false", "\"false\""));
            Assert.Throws<InvalidDataException>(() => view.VerifyRestrictedOkRuntimeUnchanged());
            File.WriteAllText(app, originalApp);
            File.WriteAllText(head, "changed-head");
            Assert.Throws<InvalidDataException>(() => view.VerifyRestrictedOkRuntimeUnchanged());
            File.WriteAllText(head, "original-head");
            File.WriteAllText(config, "{\"tasks\":[1]}");
            view.VerifyRestrictedOkRuntimeUnchanged();
            Assert.Throws<InvalidDataException>(() => view.VerifyUnchanged());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void OneImmutableViewParsesOnceAcrossRepeatedReadsAndSelectorsButStillChecksLiveBytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-view-reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "config.json");
        try
        {
            File.WriteAllText(path, "{\"target\":\"old\",\"enabled\":true}");
            var view = new TaskConfigView();
            view.AddConfig("main", path, "json");
            Assert.Equal(0, view.ParseCount);

            string first = view.ReadConfig("main");
            for (int index = 0; index < 20; index++)
            {
                Assert.Equal(first, view.ReadConfig("main"));
                Assert.True(view.TryResolveDeclaredTarget("main", new JsonArray("target"),
                    out TaskDeclaredTarget? target, out string status));
                Assert.Equal("old", target!.Value);
                Assert.Equal("present", status);
            }
            Assert.Equal(1, view.ParseCount);

            File.WriteAllText(path, "{\"target\":\"new\",\"enabled\":true}");
            Assert.Throws<InvalidDataException>(() => view.VerifyUnchanged());
            Assert.Equal(1, view.ParseCount);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(root);
        }
    }
}
