namespace MsfsBackup.Core;

/// <summary>
/// MSFS Addons Linker keeps add-ons in its own library folder and links them into Community.
/// Those links belong to Addons Linker, so they are left out of backups and never touched.
/// Its settings live in %ProgramData%\MSFS Addons Linker (2020) and ...\MSFS Addons Linker 2024.
/// </summary>
public static class AddonsLinker
{
    const string SettingsFile = "MSFS_Addons_Linker.ini";
    const string LibraryKey = "MyAddons_Path=";

    static IReadOnlyList<string>? _libraries;

    /// <summary>Library folders from every Addons Linker install found on this PC.</summary>
    public static IReadOnlyList<string> Libraries => _libraries ??= ReadLibraries();

    /// <summary>Reads the settings again, e.g. after the user changed them.</summary>
    public static void Reload() => _libraries = null;

    /// <summary>Test hook: use these library folders instead of the real settings.</summary>
    internal static void UseLibraries(params string[] folders) => _libraries = folders.Select(PathUtil.Normalize).ToList();

    public static bool Owns(LinkRecord link) =>
        Libraries.Any(lib => PathUtil.IsSameOrUnder(link.ResolvedTarget, lib));

    static IReadOnlyList<string> ReadLibraries()
    {
        var result = new List<string>();
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(programData, "MSFS Addons Linker*"))
            {
                string ini = Path.Combine(dir, SettingsFile);
                if (!File.Exists(ini))
                    continue;
                foreach (var line in File.ReadLines(ini))
                {
                    if (!line.StartsWith(LibraryKey, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string path = line[LibraryKey.Length..].Trim().Trim('"');
                    // A drive root would match every link on that drive, so it is ignored.
                    if (!Path.IsPathFullyQualified(path))
                        continue;
                    path = PathUtil.Normalize(path);
                    if (!PathUtil.SamePath(path, Path.GetPathRoot(path)!) && !result.Any(p => PathUtil.SamePath(p, path)))
                        result.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }
}
