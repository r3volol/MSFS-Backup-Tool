using System.IO;
using System.Windows;
using Microsoft.Win32;
using MsfsBackup.Core;

namespace MsfsBackupTool;

public partial class DeadLinksDialog : Window
{
    readonly List<DeadLinkRow> _rows;

    public DeadLinksDialog(IEnumerable<PendingLink> links)
    {
        InitializeComponent();
        _rows = links.Select(l => new DeadLinkRow(l)).ToList();
        LinksGrid.ItemsSource = _rows;
    }

    /// <summary>Links the user chose to re-point, with their new absolute target.</summary>
    public List<(PendingLink Link, string Target)> Selected { get; } = new();

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeadLinkRow row) return;
        if (row.Link.Record.IsDirectory)
        {
            var d = new OpenFolderDialog { Title = $"New location for {row.Name}" };
            if (d.ShowDialog(this) == true) row.NewTarget = d.FolderName;
        }
        else
        {
            var d = new OpenFileDialog { Title = $"New location for {row.Name}" };
            if (d.ShowDialog(this) == true) row.NewTarget = d.FileName;
        }
    }

    void Create_Click(object sender, RoutedEventArgs e)
    {
        Selected.Clear();
        foreach (var row in _rows.Where(r => r.Include))
        {
            string target = row.NewTarget.Trim();
            if (target.Length == 0)
            {
                MessageBox.Show(this, $"Choose a new target for \"{row.Name}\", or untick it.", Title);
                return;
            }
            bool exists = row.Link.Record.IsDirectory ? Directory.Exists(target) : File.Exists(target);
            if (!exists)
            {
                MessageBox.Show(this, $"\"{target}\" doesn't exist.", Title);
                return;
            }
            Selected.Add((row.Link, Path.GetFullPath(target)));
        }
        DialogResult = true;
    }
}
