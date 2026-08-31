using System.Text.Json.Nodes;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class AppearanceServiceTests
{
    private const string FirstId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SecondId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Normalize_ClampsSurfaceTransparencyToFiftyPercent()
    {
        string root = NewTempDir();
        try
        {
            WriteConfig(root, new[] { FirstId }, "off", 30, 0, 99);

            AppearanceSnapshot snapshot = CreateService(root).GetSnapshot();

            Assert.Equal(50, snapshot.Effects.SurfaceTransparencyPercent);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void Normalize_DoesNotAssignAPluginProviderByDefault()
    {
        string root = NewTempDir();
        try
        {
            AppearanceSnapshot snapshot = CreateService(root).GetSnapshot();

            Assert.Equal("", snapshot.Provider.PluginName);
            Assert.False(snapshot.Provider.Enabled);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void LegacyConfig_DefaultsSecondarySurfaceTransparencyToEnabled()
    {
        string root = NewTempDir();
        try
        {
            WriteConfig(root, new[] { FirstId }, "off", 30, 0, 0);

            AppearanceSnapshot snapshot = CreateService(root).GetSnapshot();

            Assert.True(snapshot.Effects.ApplyTransparencyToSecondarySurfaces);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void ConfigCanDisableSecondarySurfaceTransparency()
    {
        string root = NewTempDir();
        try
        {
            WriteConfig(root, new[] { FirstId }, "off", 30, 0, 0, applyTransparencyToSecondarySurfaces: false);

            AppearanceSnapshot snapshot = CreateService(root).GetSnapshot();

            Assert.False(snapshot.Effects.ApplyTransparencyToSecondarySurfaces);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void TimerRotation_UsesOneServerRandomResultPerSlot()
    {
        string root = NewTempDir();
        try
        {
            long nowMilliseconds = 1_700_000_000_000;
            WriteConfig(root, new[] { FirstId, SecondId }, "timer", 1, nowMilliseconds, 0);
            AppearanceService service = CreateService(
                root,
                () => DateTimeOffset.FromUnixTimeMilliseconds(nowMilliseconds));

            AppearanceSnapshot first = service.GetSnapshot();
            AppearanceSnapshot sameSlot = service.GetSnapshot();
            Assert.Equal(first.CurrentId, sameSlot.CurrentId);

            nowMilliseconds += 60_000;
            AppearanceSnapshot nextSlot = service.GetSnapshot();
            Assert.NotEqual(first.CurrentId, nextSlot.CurrentId);
            Assert.Contains(nextSlot.CurrentId, new[] { FirstId, SecondId });
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void TimerRotation_WithOneWallpaperNeverLoopsOrLosesCurrentId()
    {
        string root = NewTempDir();
        try
        {
            long nowMilliseconds = 1_700_000_000_000;
            WriteConfig(root, new[] { FirstId }, "timer", 1, nowMilliseconds, 0);
            AppearanceService service = CreateService(
                root,
                () => DateTimeOffset.FromUnixTimeMilliseconds(nowMilliseconds));

            Assert.Equal(FirstId, service.GetSnapshot().CurrentId);
            nowMilliseconds += 120_000;
            Assert.Equal(FirstId, service.GetSnapshot().CurrentId);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void TimerRotation_RecoversWhenRuntimePointsToDeletedWallpaper()
    {
        string root = NewTempDir();
        try
        {
            long nowMilliseconds = 1_700_000_000_000;
            WriteConfig(root, new[] { FirstId, SecondId }, "timer", 1, nowMilliseconds, 0);
            string runtimePath = Path.Combine(root, "appearance-runtime.json");
            File.WriteAllText(runtimePath, new JsonObject
            {
                ["lastRandomId"] = "cccccccccccccccccccccccccccccccc",
                ["timerSlot"] = 0,
                ["timerEpochUnixMs"] = nowMilliseconds,
                ["timerIntervalMinutes"] = 1,
            }.ToJsonString());

            AppearanceSnapshot snapshot = CreateService(
                root,
                () => DateTimeOffset.FromUnixTimeMilliseconds(nowMilliseconds)).GetSnapshot();

            Assert.Contains(snapshot.CurrentId, new[] { FirstId, SecondId });
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void PickRandomId_ExcludesPreviousIdWhenThereAreAlternatives()
    {
        Assert.Equal(FirstId, AppearanceService.PickRandomId(new[] { FirstId }, FirstId));
        Assert.Equal("", AppearanceService.PickRandomId(Array.Empty<string>(), FirstId));
        Assert.Contains(
            AppearanceService.PickRandomId(new[] { FirstId, SecondId }, FirstId),
            new[] { SecondId });
    }

    private static AppearanceService CreateService(string root, Func<DateTimeOffset>? utcNow = null)
    {
        return new AppearanceService(
            configPath: Path.Combine(root, "appearance.json"),
            runtimePath: Path.Combine(root, "appearance-runtime.json"),
            assetsDir: Path.Combine(root, "assets"),
            stagingDir: Path.Combine(root, "staging"),
            utcNow: utcNow);
    }

    private static void WriteConfig(
        string root,
        IReadOnlyList<string> order,
        string mode,
        int intervalMinutes,
        long epochUnixMs,
        int surfaceTransparency,
        bool? applyTransparencyToSecondarySurfaces = null)
    {
        var assets = new JsonArray();
        foreach (string id in order)
        {
            assets.Add(new JsonObject
            {
                ["id"] = id,
                ["originalName"] = id + ".png",
                ["mimeType"] = "image/png",
                ["sizeBytes"] = 1,
                ["sha256"] = new string('a', 64),
                ["paletteVersion"] = 0,
                ["palette"] = new JsonObject(),
            });
        }
        var effects = new JsonObject
        {
            ["surfaceTransparencyPercent"] = surfaceTransparency,
        };
        if (applyTransparencyToSecondarySurfaces is bool applyTransparency)
        {
            effects["applyTransparencyToSecondarySurfaces"] = applyTransparency;
        }
        var config = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["revision"] = 1,
            ["provider"] = new JsonObject
            {
                ["pluginName"] = "custom-wallpaper",
                ["enabled"] = false,
            },
            ["assets"] = assets,
            ["order"] = new JsonArray(order.Select(id => JsonValue.Create(id)).ToArray()),
            ["selectedId"] = order[0],
            ["rotation"] = new JsonObject
            {
                ["mode"] = mode,
                ["intervalMinutes"] = intervalMinutes,
                ["epochUnixMs"] = epochUnixMs,
            },
            ["effects"] = effects,
        };
        File.WriteAllText(Path.Combine(root, "appearance.json"), config.ToJsonString());
    }

    private static string NewTempDir()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempDir(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
