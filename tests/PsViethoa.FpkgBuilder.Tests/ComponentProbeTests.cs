using PsViethoa.FpkgBuilder.Core.ExFat;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

public sealed class ComponentProbeTests
{
    [Fact]
    public void Run_ReportsEveryComponentWithLocalizedText()
    {
        var rows = ComponentProbe.Run();
        Assert.Equal(new[] { "engine", "keys", "kraken", "oodle", "mount", "sleep" }, rows.Select(r => r.Id).ToArray());
        Assert.All(rows, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Name));
            Assert.False(string.IsNullOrWhiteSpace(r.Detail));
            Assert.DoesNotContain("Comp.", r.Name);
            Assert.DoesNotContain("Comp.", r.Detail);
        });

        var engine = rows.Single(r => r.Id == "engine");
        Assert.Equal(ComponentState.Ok, engine.State);
        Assert.Contains(BuildEngine.LibraryVersion, engine.Detail);

        var kraken = rows.Single(r => r.Id == "kraken");
        Assert.Equal(ComponentState.Ok, kraken.State);

        var mount = rows.Single(r => r.Id == "mount");
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(ExFatMounter.IsAvailable ? ComponentState.Ok : ComponentState.Missing, mount.State);
            Assert.False(mount.CanFix);
        }
        else if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(ComponentState.NotApplicable, mount.State);
        }

        var oodle = rows.Single(r => r.Id == "oodle");
        Assert.Equal(OperatingSystem.IsWindows() ? oodle.State is ComponentState.Ok or ComponentState.Warning : oodle.State == ComponentState.NotApplicable, true);
    }

    [Fact]
    public void Report_ListsOneLinePerComponent()
    {
        var rows = new[]
        {
            new ComponentStatus("a", "Alpha", ComponentState.Ok, "fine"),
            new ComponentStatus("b", "Beta", ComponentState.Warning, "hmm"),
            new ComponentStatus("c", "Gamma", ComponentState.Missing, "gone", CanFix: true),
            new ComponentStatus("d", "Delta", ComponentState.NotApplicable, "n/a"),
        };

        var report = ComponentProbe.Report(rows, "Header 1.0");
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Header 1.0", lines[0]);
        Assert.Equal("[OK] Alpha: fine", lines[1]);
        Assert.Equal("[!!] Beta: hmm", lines[2]);
        Assert.Equal("[--] Gamma: gone", lines[3]);
        Assert.Equal("[n/a] Delta: n/a", lines[4]);
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public void DokanProbe_IsHonestOffWindows()
    {
        var probe = DokanImageMounter.Probe();
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(probe.Installed);
            Assert.False(probe.Running);
            Assert.Null(probe.DriverVersion);
        }
        else
        {
            Assert.Equal(DokanImageMounter.IsDriverInstalled, probe.Installed);
        }
    }
}
