using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using MsfsBackup.Core;

namespace MsfsBackupTool;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class LocationRow : Bindable
{
    bool _selected;
    string _status = "";

    public LocationRow(SimLocation location)
    {
        Location = location;
        RefreshStatus();
        _selected = Directory.Exists(location.Path) && _status != "empty";
    }

    public SimLocation Location { get; }
    public string SimName => Names.Sim(Location.Sim);
    public string StoreName => Names.Store(Location.Store);
    public string FolderName => Names.Folder(Location.Kind);

    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public string Path
    {
        get => Location.Path;
        set
        {
            if (Location.Path == value) return;
            Location.Path = value;
            Raise(nameof(Path));
            RefreshStatus();
        }
    }

    public void RefreshStatus()
    {
        if (!Directory.Exists(Location.Path))
        {
            Status = "not found";
            return;
        }
        try
        {
            var (items, links, linker) = Scanner.CountTopLevel(Location.Path, Location.Filter);
            Status = (items == 0 ? "empty" : links > 0 ? $"{items} items ({links} links)" : $"{items} items")
                + (linker > 0 ? $", {linker} Addons Linker skipped" : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserFacingException)
        {
            Status = "can't read";
        }
    }
}

public sealed class RestoreRow : Bindable
{
    bool _selected = true;
    string _target;

    public RestoreRow(ManifestLocation location, string target)
    {
        Location = location;
        _target = target;
    }

    public ManifestLocation Location { get; }
    public string Name => Location.DisplayName;
    public string Summary => $"{Location.FileCount:N0} files, {Fmt.Bytes(Location.TotalBytes)}, {Location.Links.Count} links";
    public string OriginalPathTip => $"Backed up from: {Location.OriginalPath}";

    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public string TargetPath { get => _target; set => Set(ref _target, value); }
}

public sealed class DeadLinkRow : Bindable
{
    bool _include;
    string _newTarget = "";

    public DeadLinkRow(PendingLink link) => Link = link;

    public PendingLink Link { get; }
    public string Name => System.IO.Path.GetFileName(Link.LinkPath);
    public string Kind => Link.Record.Type == LinkType.Junction ? "Junction" : "Symlink";
    public string OriginalTarget => Link.ResolvedTarget;

    public bool Include { get => _include; set => Set(ref _include, value); }
    public string NewTarget { get => _newTarget; set { Set(ref _newTarget, value); if (value.Length > 0) Include = true; } }
}
