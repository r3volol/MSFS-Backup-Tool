using System.Windows;
using MsfsBackup.Core;

namespace MsfsBackupTool;

public partial class ConflictDialog : Window
{
    public ConflictMode Choice { get; private set; }

    public ConflictDialog(ConflictSummary summary)
    {
        InitializeComponent();
        var parts = new List<string>();
        if (summary.Files > 0) parts.Add($"{summary.Files:N0} file{(summary.Files == 1 ? "" : "s")}");
        if (summary.Links > 0) parts.Add($"{summary.Links} add-on link{(summary.Links == 1 ? "" : "s")}");
        Message.Text = $"{string.Join(" and ", parts)} already exist{(summary.Files + summary.Links == 1 ? "s" : "")} in the destination folders. What should happen to them?";
    }

    void Choose(ConflictMode mode)
    {
        Choice = mode;
        DialogResult = true;
    }

    void Overwrite_Click(object sender, RoutedEventArgs e) => Choose(ConflictMode.Overwrite);
    void Skip_Click(object sender, RoutedEventArgs e) => Choose(ConflictMode.Skip);
    void Newer_Click(object sender, RoutedEventArgs e) => Choose(ConflictMode.KeepNewer);
}
