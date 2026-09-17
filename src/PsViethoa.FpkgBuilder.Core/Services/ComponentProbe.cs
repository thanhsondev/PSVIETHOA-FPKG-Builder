using System.Text;
using PsViethoa.FpkgBuilder.Core.ExFat;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Services;

public enum ComponentState
{
    Ok,
    Warning,
    Missing,
    NotApplicable,
}

/// <summary>Một thành phần / "plugin" của ứng dụng và tình trạng hoạt động thật của nó.</summary>
public sealed record ComponentStatus(string Id, string Name, ComponentState State, string Detail, bool CanFix = false);

/// <summary>
/// Kiểm tra từng thành phần có hoạt động không (không chỉ có mặt): engine LibProsperoPkg, khoá debug PS5, bộ nén Kraken,
/// Oodle gốc (libScePubTools.dll), cách gắn ảnh không sao chép (hdiutil / driver Dokan đang chạy) và chống ngủ máy.
/// Dùng cho ô "Thành phần & plugin" trong ứng dụng và lệnh <c>fpkg-cli info</c>.
/// </summary>
public static class ComponentProbe
{
    public static IReadOnlyList<ComponentStatus> Run(string? publishingToolsPath = null)
    {
        var rows = new List<ComponentStatus>(6)
        {
            Engine(),
            Keys(),
            new ComponentStatus("kraken", Loc.T("Comp.Kraken"), ComponentState.Ok, Loc.T("Comp.KrakenOk")),
            Oodle(publishingToolsPath),
            Mount(),
            SleepGuard(),
        };
        return rows;
    }

    /// <summary>Báo cáo văn bản (để sao chép / gửi hỗ trợ).</summary>
    public static string Report(IEnumerable<ComponentStatus> rows, string header)
    {
        var builder = new StringBuilder();
        builder.AppendLine(header);
        foreach (var row in rows)
        {
            var marker = row.State switch
            {
                ComponentState.Ok => "[OK]",
                ComponentState.Warning => "[!!]",
                ComponentState.Missing => "[--]",
                _ => "[n/a]",
            };
            builder.Append(marker).Append(' ').Append(row.Name).Append(": ").AppendLine(row.Detail);
        }

        return builder.ToString().TrimEnd();
    }

    private static ComponentStatus Engine()
    {
        var version = BuildEngine.LibraryVersion;
        return version == "?"
            ? new ComponentStatus("engine", Loc.T("Comp.Engine"), ComponentState.Missing, Loc.T("Comp.EngineMissing"))
            : new ComponentStatus("engine", Loc.T("Comp.Engine"), ComponentState.Ok, Loc.F("Comp.EngineOk", version));
    }

    private static ComponentStatus Keys() =>
        BuildEngine.KeysAvailable
            ? new ComponentStatus("keys", Loc.T("Comp.Keys"), ComponentState.Ok, Loc.T("Comp.KeysOk"))
            : new ComponentStatus("keys", Loc.T("Comp.Keys"), ComponentState.Missing, Loc.T("Comp.KeysMissing"));

    private static ComponentStatus Oodle(string? explicitPath)
    {
        if (!PublishingToolsLocator.IsSupportedPlatform)
        {
            return new ComponentStatus("oodle", Loc.T("Comp.Oodle"), ComponentState.NotApplicable, Loc.T("Comp.OodleNa"));
        }

        var found = PublishingToolsLocator.Find(explicitPath);
        return found != null
            ? new ComponentStatus("oodle", Loc.T("Comp.Oodle"), ComponentState.Ok, Loc.F("Comp.OodleOk", found))
            : new ComponentStatus("oodle", Loc.T("Comp.Oodle"), ComponentState.Warning, Loc.T("Comp.OodleMissing"));
    }

    private static ComponentStatus Mount()
    {
        if (OperatingSystem.IsMacOS())
        {
            return ExFatMounter.IsAvailable
                ? new ComponentStatus("mount", Loc.T("Comp.Mount"), ComponentState.Ok, Loc.T("Comp.MountHdiutil"))
                : new ComponentStatus("mount", Loc.T("Comp.Mount"), ComponentState.Missing, Loc.T("Comp.MountHdiutilMissing"));
        }

        if (OperatingSystem.IsWindows())
        {
            var probe = DokanImageMounter.Probe();
            if (!probe.Installed)
            {
                return new ComponentStatus(
                    "mount",
                    Loc.T("Comp.Mount"),
                    ComponentState.Missing,
                    Loc.T(DokanInstaller.IsBundled ? "Comp.MountDokanMissingBundled" : "Comp.MountDokanMissing"),
                    CanFix: DokanInstaller.IsBundled);
            }

            var version = probe.DriverVersion ?? DokanImageMounter.DriverVersion ?? "?";
            return probe.Running
                ? new ComponentStatus("mount", Loc.T("Comp.Mount"), ComponentState.Ok, Loc.F("Comp.MountDokanOk", version))
                : new ComponentStatus("mount", Loc.T("Comp.Mount"), ComponentState.Warning, Loc.F("Comp.MountDokanNotRunning", DokanImageMounter.DriverVersion ?? "?", probe.Error ?? string.Empty).Trim());
        }

        if (OperatingSystem.IsLinux())
        {
            if (FuseImageMounter.IsAvailable)
            {
                return new ComponentStatus("mount", Loc.T("Comp.Mount"), ComponentState.Ok, Loc.F("Comp.MountFuseOk", FuseImageMounter.LibraryVersion ?? "3"));
            }

            return new ComponentStatus(
                "mount",
                Loc.T("Comp.Mount"),
                ComponentState.Missing,
                Loc.T(FuseImageMounter.FindFusermount() == null ? "Comp.MountFusermountMissing" : "Comp.MountFuseMissing"));
        }

        return new ComponentStatus("mount", Loc.T("Comp.Mount"), ComponentState.NotApplicable, Loc.T("Comp.MountNone"));
    }

    private static ComponentStatus SleepGuard()
    {
        if (OperatingSystem.IsMacOS())
        {
            return File.Exists("/usr/bin/caffeinate")
                ? new ComponentStatus("sleep", Loc.T("Comp.Sleep"), ComponentState.Ok, Loc.T("Comp.SleepCaffeinate"))
                : new ComponentStatus("sleep", Loc.T("Comp.Sleep"), ComponentState.Warning, Loc.T("Comp.SleepMissing"));
        }

        if (OperatingSystem.IsWindows())
        {
            return new ComponentStatus("sleep", Loc.T("Comp.Sleep"), ComponentState.Ok, Loc.T("Comp.SleepWindows"));
        }

        if (OperatingSystem.IsLinux())
        {
            return SleepInhibitor.FindSystemdInhibit() != null
                ? new ComponentStatus("sleep", Loc.T("Comp.Sleep"), ComponentState.Ok, Loc.T("Comp.SleepSystemd"))
                : new ComponentStatus("sleep", Loc.T("Comp.Sleep"), ComponentState.Warning, Loc.T("Comp.SleepMissing"));
        }

        return new ComponentStatus("sleep", Loc.T("Comp.Sleep"), ComponentState.NotApplicable, Loc.T("Comp.SleepMissing"));
    }
}
