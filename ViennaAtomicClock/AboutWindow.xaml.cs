using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace ViennaAtomicClock;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = BuildInfo.SemanticVersion;
        CommitText.Text = BuildInfo.GitCommit == "unknown" ? Loc.T("about.unknown") : BuildInfo.GitCommit;
        BuildDateText.Text = BuildInfo.BuildDateUtc is { } built
            ? built.ToLocalTime().ToString("g", Loc.Culture)
            : Loc.T("about.unknown");
        RuntimeText.Text = $".NET {RuntimeInformation.FrameworkDescription.Replace(".NET ", "", StringComparison.Ordinal)}";
        DataFolderText.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaAtomicClock");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
