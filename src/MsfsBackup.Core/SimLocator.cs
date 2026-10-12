using System.Text.RegularExpressions;

namespace MsfsBackup.Core;

/// <summary>Finds MSFS 2020 / 2024 installs (MS Store and Steam) and the folders to back up.</summary>
public static class SimLocator
{
    const string Store2020Package = "Microsoft.FlightSimulator_8wekyb3d8bbwe";
    const string Store2024Package = "Microsoft.Limitless_8wekyb3d8bbwe";

    static readonly Regex PackagesPathRegex = new(
        @"^\s*InstalledPackagesPath\s+(?:""(?<p>[^""]+)""|(?<p>\S.*?))\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static List<SimInstall> Detect()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var candidates = new[]
        {
            // (sim, store, folder holding UserCfg.opt, LocalState folder)
            (SimVersion.MSFS2020, StoreType.MicrosoftStore,
                Path.Combine(local, "Packages", Store2020Package, "LocalCache"),
                Path.Combine(local, "Packages", Store2020Package, "LocalState")),
            (SimVersion.MSFS2020, StoreType.Steam,
                Path.Combine(roaming, "Microsoft Flight Simulator"),
                Path.Combine(roaming, "Microsoft Flight Simulator")),
            (SimVersion.MSFS2024, StoreType.MicrosoftStore,
                Path.Combine(local, "Packages", Store2024Package, "LocalCache"),
                Path.Combine(local, "Packages", Store2024Package, "LocalState")),
            (SimVersion.MSFS2024, StoreType.Steam,
                Path.Combine(roaming, "Microsoft Flight Simulator 2024"),
                Path.Combine(roaming, "Microsoft Flight Simulator 2024")),
        };

        var installs = new List<SimInstall>();
        foreach (var (sim, store, configDir, stateDir) in candidates)
        {
            string cfg = Path.Combine(configDir, "UserCfg.opt");
            if (!File.Exists(cfg))
                continue;

            string packages = ReadInstalledPackagesPath(cfg) ?? Path.Combine(configDir, "Packages");
            var install = new SimInstall { Sim = sim, Store = store, ConfigFile = cfg, PackagesPath = packages };

            void Add(FolderKind kind, string path, LocationFilter filter = LocationFilter.All) =>
                install.Locations.Add(new SimLocation { Sim = sim, Store = store, Kind = kind, Path = path, Filter = filter });

            Add(FolderKind.Community, Path.Combine(packages, "Community"));
            if (sim == SimVersion.MSFS2024)
            {
                Add(FolderKind.Community2024, Path.Combine(packages, "Community2024"));
                Add(FolderKind.WasmMsfs2020, Path.Combine(stateDir, "WASM", "MSFS2020"));
                Add(FolderKind.WasmMsfs2024, Path.Combine(stateDir, "WASM", "MSFS2024"));
            }
            else
            {
                // MS Store: LocalState\packages\<pkg>\work   Steam: %APPDATA%\Microsoft Flight Simulator\Packages\<pkg>\work
                string workRoot = store == StoreType.Steam ? Path.Combine(stateDir, "Packages") : Path.Combine(stateDir, "packages");
                Add(FolderKind.WorkFolders, workRoot, LocationFilter.WorkFoldersOnly);
            }
            installs.Add(install);
        }
        return installs;
    }

    public static string? ReadInstalledPackagesPath(string userCfgPath)
    {
        try
        {
            var m = PackagesPathRegex.Match(File.ReadAllText(userCfgPath));
            return m.Success ? m.Groups["p"].Value.Trim() : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Where a backed-up location should go by default: the matching folder of a detected install
    /// (same store preferred), otherwise where it originally came from.
    /// </summary>
    public static string DefaultRestoreTarget(ManifestLocation m, IEnumerable<SimInstall> installs)
    {
        var matches = installs.SelectMany(i => i.Locations).Where(l => l.MatchKey == m.MatchKey).ToList();
        var best = matches.FirstOrDefault(l => l.Store == m.Store) ?? matches.FirstOrDefault();
        return best?.Path ?? m.OriginalPath;
    }
}
