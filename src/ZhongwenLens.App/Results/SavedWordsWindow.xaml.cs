using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using ZhongwenLens.App.Services;
using ZhongwenLens.Core.Study;

namespace ZhongwenLens.App.Results;

/// <summary>
/// The starred-word list, with export to Anki.
/// </summary>
public sealed partial class SavedWordsWindow : Window
{
    private static readonly FontFamily ChineseFont = new("Microsoft YaHei UI");

    private readonly AppServices _services;

    public SavedWordsWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        Title = "Saved words — Zhongwen Lens";
        Refresh();

        var appWindow = GetAppWindow();
        appWindow.Resize(new SizeInt32(640, 700));
    }

    private void Refresh()
    {
        WordList.Children.Clear();

        var words = _services.Study.GetAll();
        HeadingText.Text = words.Count switch
        {
            0 => "No saved words yet",
            1 => "1 saved word",
            _ => $"{words.Count} saved words",
        };

        ExportButton.IsEnabled = words.Count > 0;

        if (words.Count == 0)
        {
            StatusText.Text = "Star a word on a snip result to save it here.";
            return;
        }

        foreach (var word in words) WordList.Children.Add(BuildRow(word));

        StatusText.Text = $"Stored in {DataPaths.UserDirectory}";
    }

    private FrameworkElement BuildRow(SavedWord word)
    {
        var panel = new StackPanel { Spacing = 2 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        header.Children.Add(new TextBlock
        {
            Text = word.Simplified,
            FontSize = 20,
            FontFamily = ChineseFont,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = word.PinyinMarks,
            FontSize = 13,
            Opacity = 0.85,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (word.HskLevel is { } level)
        {
            header.Children.Add(new Border
            {
                Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = level == 7 ? "HSK 7-9" : $"HSK {level}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                },
            });
        }

        var remove = new Button
        {
            Content = "Remove",
            FontSize = 11,
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += (_, _) =>
        {
            _services.Study.Remove(word.Simplified);
            Refresh();
        };
        header.Children.Add(remove);

        panel.Children.Add(header);
        panel.Children.Add(new TextBlock
        {
            Text = word.SenseSummary,
            FontSize = 12,
            Opacity = 0.9,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });

        if (word.HasContext)
        {
            // The sentence it was met in — the reason saving is worth doing at all.
            panel.Children.Add(new TextBlock
            {
                Text = word.SourceContext,
                FontSize = 12,
                FontFamily = ChineseFont,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9, 12, 9),
            Child = panel,
        };
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(
                DataPaths.UserDirectory,
                $"zhongwen-lens-anki-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var count = AnkiExporter.Export(_services.Study.GetAll(), path);

            StatusText.Text =
                $"Exported {count} word(s) to {path}\n" +
                "In Anki: File → Import, pick this file, choose your note type, and map the columns.";
            Log.Write($"exported {count} saved words to {path}");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Export failed: {ex.Message}";
            Log.Error("anki export", ex);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DataPaths.UserDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DataPaths.UserDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open the folder: {ex.Message}";
        }
    }

    private AppWindow GetAppWindow()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
    }
}
