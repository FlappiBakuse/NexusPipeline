using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Xml.Linq;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class StartupEffectsTests
{
    [Fact(DisplayName = "The Typed Startup Port Creates Only A New Owned Definition And Removes Only Its Matching Registration")]
    public void RegistersOwnedTasks()
    {
        var fixture = new Fixture();
        var command = fixture.Command;
        Assert.Equal(StartupRegistrationEffect.Created, fixture.Effects.CreateIfMissing(command));
        var original = File.ReadAllBytes(fixture.Path);
        Assert.Equal(StartupRegistrationState.Matches, fixture.Effects.Inspect(command.Definition).State);
        Assert.Equal(StartupRegistrationEffect.AlreadySatisfied, fixture.Effects.CreateIfMissing(command));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        Assert.Equal(1, fixture.Creates);
        Assert.Equal(StartupRegistrationEffect.Removed, fixture.Effects.RemoveIfMatching(command.Definition));
        Assert.Equal(StartupRegistrationEffect.AlreadySatisfied, fixture.Effects.RemoveIfMatching(command.Definition));
        Assert.False(File.Exists(fixture.Path));
        Assert.Equal(1, fixture.Deletes);
    }

    [Theory(DisplayName = "A Foreign Startup Definition Cannot Be Replaced Or Removed")]
    [InlineData("owner")]
    [InlineData("arguments")]
    [InlineData("user")]
    public void PreservesForeignTasks(string difference)
    {
        var fixture = new Fixture();
        var definition = fixture.Command.Definition;
        var foreign = difference switch
        {
            "owner" => definition with { RegistrationId = Guid.NewGuid() },
            "arguments" => definition with { Arguments = ["--different"] },
            _ => definition with { UserSid = "S-1-5-18" }
        };
        File.WriteAllText(fixture.Path, StartupTaskXml.Encode(foreign));
        var before = File.ReadAllBytes(fixture.Path);
        Assert.Throws<IOException>(() => fixture.Effects.CreateIfMissing(fixture.Command));
        Assert.Throws<IOException>(() => fixture.Effects.RemoveIfMatching(definition));
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Equal(0, fixture.Creates);
        Assert.Equal(0, fixture.Deletes);
    }

    [Fact(DisplayName = "Changes Observed Immediately Before The Startup Effect Preserve The Foreign Registration")]
    public void RechecksChangedTasks()
    {
        var fixture = new Fixture();
        var definition = fixture.Command.Definition;
        var foreignXml = StartupTaskXml.Encode(definition with { RegistrationId = Guid.NewGuid() });
        fixture.BeforeValidation = () => File.WriteAllText(fixture.Path, foreignXml);
        Assert.Throws<IOException>(() => fixture.Effects.CreateIfMissing(fixture.Command));
        Assert.Equal(foreignXml, File.ReadAllText(fixture.Path));
        Assert.Equal(0, fixture.Creates);
        fixture.BeforeValidation = null;
        File.WriteAllText(fixture.Path, StartupTaskXml.Encode(definition));
        fixture.AfterRead = () => File.WriteAllText(fixture.Path, foreignXml);
        Assert.Throws<IOException>(() => fixture.Effects.RemoveIfMatching(definition));
        Assert.Equal(foreignXml, File.ReadAllText(fixture.Path));
        Assert.Equal(0, fixture.Deletes);
    }

    [Theory(DisplayName = "Native Port Failures Never Trigger An Automatic Compensating Startup Effect")]
    [InlineData("query")]
    [InlineData("validate")]
    [InlineData("create-unknown")]
    [InlineData("delete-unknown")]
    public void RejectsFailureCompensation(string fault)
    {
        var fixture = new Fixture();
        fixture.Fault = fault;
        if (fault == "delete-unknown") File.WriteAllText(fixture.Path, StartupTaskXml.Encode(fixture.Command.Definition));
        if (fault == "delete-unknown") Assert.Throws<IOException>(() => fixture.Effects.RemoveIfMatching(fixture.Command.Definition));
        else Assert.Throws<IOException>(() => fixture.Effects.CreateIfMissing(fixture.Command));
        Assert.Equal(fault == "create-unknown" ? 1 : 0, fixture.Creates);
        Assert.Equal(fault == "delete-unknown" ? 1 : 0, fixture.Deletes);
        Assert.Equal(fault == "create-unknown", File.Exists(fixture.Path));
    }

    [Theory(DisplayName = "Startup Creation Rejects Changed Executable Bytes Or Another Windows User Before Opening The Scheduler")]
    [InlineData("hash")]
    [InlineData("user")]
    public void VerifiesExecutableIdentity(string fault)
    {
        var fixture = new Fixture();
        var command = fault == "hash" ? fixture.Command with { ExecutableSha256 = new('0', 64) }
            : fixture.Command with { Definition = fixture.Command.Definition with { UserSid = "S-1-5-18" } };
        Assert.Throws<IOException>(() => fixture.Effects.CreateIfMissing(command));
        Assert.Equal(0, fixture.Opens);
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact(DisplayName = "Startup Creation Retains The Verified Executable And Directory Until The Native Effect Returns")]
    public void HoldsExecutableInputs()
    {
        var fixture = new Fixture(copyExecutable: true);
        var executable = fixture.Command.Definition.ExecutablePath;
        fixture.BeforeCreate = () =>
        {
            Assert.Throws<IOException>(() => File.WriteAllText(executable, "changed"));
            Assert.Throws<IOException>(() => File.Delete(executable));
            Assert.Throws<IOException>(() => Directory.Move(System.IO.Path.GetDirectoryName(executable)!, executable + ".moved"));
        };
        Assert.Equal(StartupRegistrationEffect.Created, fixture.Effects.CreateIfMissing(fixture.Command));
        File.WriteAllText(executable, "released");
        Assert.Equal("released", File.ReadAllText(executable));
    }

    private sealed class Fixture
    {
        internal string Path { get; }
        internal StartupRegistrationCommand Command { get; }
        internal StartupRegistrationEffects Effects { get; }
        internal int Creates, Deletes, Opens;
        internal Action? BeforeValidation, AfterRead, BeforeCreate;
        internal string? Fault;

        internal Fixture(bool copyExecutable = false)
        {
            var root = System.IO.Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "startup-port-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "registration.xml");
            var image = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
            if (copyExecutable)
            {
                var target = System.IO.Path.Combine(root, "image");
                Directory.CreateDirectory(target);
                var destination = System.IO.Path.Combine(target, "fixture.exe");
                File.Copy(image, destination);
                image = destination;
            }
            var identity = ProcessIdentityReader.Read(Environment.ProcessId);
            Command = new(new(Guid.NewGuid(), Guid.NewGuid(), identity.UserSid, image,
                ImmutableArray.Create("--fixture-only"), root, DateTimeOffset.UtcNow),
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(image))));
            Effects = new(() => { Opens++; return new Session(this); });
        }

        private sealed class Session(Fixture owner) : IStartupTaskScheduler
        {
            public string? Read(string name)
            {
                if (owner.Fault == "query") throw new IOException("fixture_query_failure");
                Assert.Equal(StartupTaskXml.TaskName(owner.Command.Definition), name);
                var xml = File.Exists(owner.Path) ? File.ReadAllText(owner.Path) : null;
                var afterRead = owner.AfterRead;
                owner.AfterRead = null;
                afterRead?.Invoke();
                return xml;
            }
            public string Normalize(string xml) => StartupTaskXml.Read(xml).ToString(SaveOptions.DisableFormatting);
            public void Validate(string name, string xml, string userSid)
            {
                owner.BeforeValidation?.Invoke();
                if (owner.Fault == "validate") throw new IOException("fixture_validation_failure");
            }
            public void Create(string name, string xml, string userSid)
            {
                owner.BeforeCreate?.Invoke();
                using (var stream = new FileStream(owner.Path, FileMode.CreateNew))
                using (var writer = new StreamWriter(stream)) writer.Write(xml);
                owner.Creates++;
                if (owner.Fault == "create-unknown") throw new IOException("fixture_unknown_create");
            }
            public void Delete(string name)
            {
                File.Delete(owner.Path);
                owner.Deletes++;
                if (owner.Fault == "delete-unknown") throw new IOException("fixture_unknown_delete");
            }
            public void Dispose() { }
        }
    }
}
