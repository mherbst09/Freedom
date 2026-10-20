using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace ViennaAtomicClock.Tutor;

/// <summary>Tutor page: binds to <see cref="TutorViewModel"/> and provides dialogs, clipboard and layout.</summary>
public partial class TutorPage : UserControl, IUserDialogs
{
    private static readonly string[] ExampleQuestions =
    {
        "Erkläre Polymorphie mit einem Beispiel",
        "Was ist der Unterschied zwischen Update und FixedUpdate in Unity?",
        "Zeig mir das Singleton-Pattern – und wann man es nicht verwenden sollte",
        "Gib mir eine Übungsaufgabe zu LINQ (mittel), Lösung erst auf Nachfrage",
        "Zeichne ein UML-Klassendiagramm für eine Bibliothek als PlantUML",
        "Wie funktioniert ein INNER JOIN in SQL?"
    };

    private TutorViewModel? _viewModel;
    private bool _autoScroll = true;
    private bool _narrow;

    public TutorPage()
    {
        InitializeComponent();
        MarkdownView.CopyHandler = CopyText;
        IsVisibleChanged += (_, _) => OnVisibilityChanged();
        foreach (var question in ExampleQuestions)
        {
            var chip = new Button { Content = question, Margin = new Thickness(4), Height = 34, FontSize = 12.5 };
            chip.SetResourceReference(StyleProperty, "ChipButton");
            chip.Click += (_, _) =>
            {
                if (_viewModel?.Selected is { } chat)
                {
                    chat.Input = question;
                    InputBox.Focus();
                    InputBox.CaretIndex = InputBox.Text.Length;
                }
            };
            Suggestions.Children.Add(chip);
        }
    }

    public TutorViewModel? ViewModel => _viewModel;

    private void OnVisibilityChanged()
    {
        if (IsVisible && _viewModel is null)
        {
            // Created on first use so Freedom starts as fast as before.
            _viewModel = new TutorViewModel(new TutorContext(TutorPaths.Default, new HttpClient { Timeout = Timeout.InfiniteTimeSpan }), this);
            _viewModel.PropertyChanged += ViewModelChanged;
            DataContext = _viewModel;
            UpdateKnowledgeToggle();
            _viewModel.Settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SettingsViewModel.UseKnowledge)) UpdateKnowledgeToggle(); };
        }
        _viewModel?.SetActive(IsVisible);
        if (IsVisible) Dispatcher.BeginInvoke(() => InputBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TutorViewModel.Selected)) return;
        _autoScroll = true;
        MessageScroller.ScrollToEnd();
        if (_narrow) Sidebar.Visibility = Visibility.Collapsed;
    }

    /// <summary>Header text for Freedom's title bar.</summary>
    public string StatusLine => _viewModel?.ConnectionText ?? "Eagle 1.1";

    /// <summary>Sidebar beside the chat on wide windows; on narrow ones it opens over the chat via ☰.</summary>
    public void ApplyLayout(double width)
    {
        _narrow = width < 860;
        SidebarColumn.Width = new GridLength(_narrow ? 0 : width < 1100 ? 230 : 270);
        GapColumn.Width = new GridLength(_narrow ? 0 : 14);
        SidebarToggle.Visibility = _narrow ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(Sidebar, _narrow ? 3 : 1);
        Sidebar.HorizontalAlignment = _narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        Sidebar.Width = _narrow ? 270 : double.NaN;
        Sidebar.Visibility = _narrow ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e) =>
        Sidebar.Visibility = Sidebar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void MessageScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Follow the streaming answer only while the user is at the bottom; scrolling up stops following.
        if (e.ExtentHeightChange == 0) _autoScroll = MessageScroller.VerticalOffset >= MessageScroller.ScrollableHeight - 4;
        else if (_autoScroll) MessageScroller.ScrollToEnd();
    }

    private void ModelButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _viewModel?.CheckConnectionAsync();
        ModelPopup.IsOpen = true;
    }

    private void ModelItem_Click(object sender, RoutedEventArgs e) => ModelPopup.IsOpen = false;

    private void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        InputBox.Focus();
        e.Handled = true;
    }

    private void KnowledgeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.Settings.UseKnowledge = !_viewModel.Settings.UseKnowledge;
    }

    private void UpdateKnowledgeToggle()
    {
        var on = _viewModel?.Settings.UseKnowledge == true;
        KnowledgeToggle.Content = on ? "✓ Verwenden" : "○ Nicht verwenden";
        Ui.Select(KnowledgeToggle, on);
    }

    public bool Confirm(string question) =>
        MessageBox.Show(Window.GetWindow(this)!, question, "Eagle 1.1", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public IReadOnlyList<string> PickTextFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Datei anhängen",
            Multiselect = true,
            Filter = "Text, Markdown und Code|*.txt;*.md;*.cs;*.json;*.xml;*.xaml;*.sql;*.csproj;*.py;*.java;*.js;*.ts;*.puml|Alle Dateien|*.*"
        };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileNames : Array.Empty<string>();
    }

    public void SaveMarkdown(string suggestedName, string content)
    {
        var dialog = new SaveFileDialog { Title = "Als Markdown exportieren", FileName = suggestedName, Filter = "Markdown|*.md", DefaultExt = ".md" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, content);
        }
        catch (IOException ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, $"Speichern fehlgeschlagen: {ex.Message}", "Eagle 1.1", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void CopyText(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { /* Clipboard busy; the user can just click again. */ }
    }

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
