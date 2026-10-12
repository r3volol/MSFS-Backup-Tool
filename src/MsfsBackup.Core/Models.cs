using System.Text.Json;
using System.Text.Json.Serialization;

namespace MsfsBackup.Core;

public enum SimVersion { MSFS2020, MSFS2024 }

public enum StoreType { MicrosoftStore, Steam }

public enum FolderKind
{
    Community,
    Community2024,
    /// <summary>MSFS 2024: LocalState\WASM\MSFS2020</summary>
    WasmMsfs2020,
    /// <summary>MSFS 2024: LocalState\WASM\MSFS2024</summary>
    WasmMsfs2024,
    /// <summary>MSFS 2020: per-package "work" folders where WASM modules keep their data.</summary>
    WorkFolders,
}

/// <summary>Which top-level entries of a location belong to the backup.</summary>
public enum LocationFilter
{
    All,
    /// <summary>Only package folders that contain a "work" subfolder (MSFS 2020 WASM data).</summary>
    WorkFoldersOnly,
}

public enum LinkType { Junction, SymbolicLink }

public enum ConflictMode { Ask, Overwrite, Skip, KeepNewer }

public static class Names
{
    public static string Sim(SimVersion s) => s == SimVersion.MSFS2020 ? "MSFS 2020" : "MSFS 2024";
    public static string Store(StoreType t) => t == StoreType.Steam ? "Steam" : "MS Store";
    public static string StoreShort(StoreType t) => t == StoreType.Steam ? "Steam" : "Store";

    public static string Folder(FolderKind k) => k switch
    {
        FolderKind.Community => "Community",
        FolderKind.Community2024 => "Community2024",
        FolderKind.WasmMsfs2020 => @"WASM\MSFS2020",
        FolderKind.WasmMsfs2024 => @"WASM\MSFS2024",
        FolderKind.WorkFolders => "WASM data (package work folders)",
        _ => k.ToString(),
    };
}

/// <summary>A folder the tool can back up, found on this PC.</summary>
public sealed class SimLocation
{
    public required SimVersion Sim { get; init; }
    public required StoreType Store { get; init; }
    public required FolderKind Kind { get; init; }
    public required string Path { get; set; }
    public LocationFilter Filter { get; init; }

    /// <summary>Unique on this PC, e.g. "MSFS2024-Steam/Community".</summary>
    public string Id => $"{Sim}-{Names.StoreShort(Store)}/{Kind}";

    /// <summary>Store-independent key used to match a backup to a folder on restore, e.g. "MSFS2024/Community".</summary>
    public string MatchKey => $"{Sim}/{Kind}";

    public string DisplayName => $"{Names.Sim(Sim)} ({Names.Store(Store)}) - {Names.Folder(Kind)}";
}

public sealed class SimInstall
{
    public required SimVersion Sim { get; init; }
    public required StoreType Store { get; init; }
    public required string ConfigFile { get; init; }
    public required string PackagesPath { get; init; }
    public List<SimLocation> Locations { get; } = new();

    public string DisplayName => $"{Names.Sim(Sim)} ({Names.Store(Store)})";
}

public sealed class LinkRecord
{
    /// <summary>Path of the link relative to the location root.</summary>
    public string RelativePath { get; set; } = "";
    public LinkType Type { get; set; }
    public bool IsDirectory { get; set; }
    /// <summary>Target as stored in the link. Symlinks may hold a relative path.</summary>
    public string Target { get; set; } = "";
    /// <summary>Absolute target at backup time.</summary>
    public string ResolvedTarget { get; set; } = "";
}

public sealed class ManifestLocation
{
    public string Id { get; set; } = "";
    public string MatchKey { get; set; } = "";
    public SimVersion Sim { get; set; }
    public StoreType Store { get; set; }
    public FolderKind Kind { get; set; }
    public LocationFilter Filter { get; set; }
    public string OriginalPath { get; set; } = "";
    /// <summary>Folder holding this location's files, relative to the backup folder.</summary>
    public string DataFolder { get; set; } = "";
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public List<string> Entries { get; set; } = new();
    public List<LinkRecord> Links { get; set; } = new();

    [JsonIgnore]
    public string DisplayName => $"{Names.Sim(Sim)} ({Names.Store(Store)}) - {Names.Folder(Kind)}";
}

public sealed class BackupManifest
{
    public const string FileName = "manifest.json";

    public string Tool { get; set; } = "MSFS Backup Tool";
    public int FormatVersion { get; set; } = 1;
    public string ToolVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    /// <summary>"Backup" or "Move".</summary>
    public string Operation { get; set; } = "Backup";
    public string MachineName { get; set; } = "";
    /// <summary>False until every file was copied and verified.</summary>
    public bool Complete { get; set; }
    public List<ManifestLocation> Locations { get; set; } = new();

    public static BackupManifest Load(string backupFolder)
    {
        string path = System.IO.Path.Combine(backupFolder, FileName);
        if (!File.Exists(path))
            throw new UserFacingException($"No {FileName} in \"{backupFolder}\". Pick a folder named \"MSFS Backup <date>\".");
        var m = JsonSerializer.Deserialize(File.ReadAllText(path), JsonContext.Default.BackupManifest)
            ?? throw new UserFacingException($"{path} is empty or unreadable.");
        if (m.FormatVersion > 1)
            throw new UserFacingException("This backup was made by a newer version of MSFS Backup Tool. Please update the tool.");
        return m;
    }

    public void Save(string backupFolder)
    {
        string path = System.IO.Path.Combine(backupFolder, FileName);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonContext.Default.BackupManifest));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class LinkRequest
{
    public string LinkPath { get; set; } = "";
    public string Target { get; set; } = "";
    public LinkType Type { get; set; }
    public bool IsDirectory { get; set; }
}

public sealed class LinkResult
{
    public string LinkPath { get; set; } = "";
    public bool Success { get; set; }
    public bool NeedsElevation { get; set; }
    public string? Error { get; set; }
}

/// <summary>An error with a message fit to show the user as-is.</summary>
public sealed class UserFacingException : Exception
{
    public UserFacingException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record ProgressInfo(string Phase, string? Item, long BytesDone, long BytesTotal, long FilesDone, long FilesTotal);

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(List<LinkRequest>))]
[JsonSerializable(typeof(List<LinkResult>))]
internal partial class JsonContext : JsonSerializerContext
{
}
