using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using ZhongwenLens.App.Services;
using ZhongwenLens.Core.Dictionary;
using ZhongwenLens.Core.Lookup;
using ZhongwenLens.Core.Study;

namespace ZhongwenLens.App.Results;

/// <summary>
/// Shows the reading of a snip: the text with its pinyin, the meaning of the whole selection
/// where there is one, and a card per segmented word (DESIGN.md §3.8).
/// </summary>
public sealed partial class ResultWindow : Window
{
    /// <summary>
    /// Above this many characters the headline switches from side-by-side pinyin to ruby.
    /// Side-by-side matches how a single word is normally written out and is the default,
    /// because the common selection is one short word (§1.4); on a full sentence it becomes
    /// unreadable, and pinyin above each word does not.
    /// </summary>
    private const int RubyLayoutThreshold = 8;

    /// <summary>Senses shown per reading before the rest are summarised as a count.</summary>
    private const int MaxSensesPerReading = 6;

    private static readonly FontFamily ChineseFont = new("Microsoft YaHei UI");

    private readonly AppServices _services;
    private readonly Dictionary<string, FrameworkElement> _cardsByWord = new(StringComparer.Ordinal);

    private string _spokenText = string.Empty;

    public ResultWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        Title = "Zhongwen Lens";
        SpeakButton.IsEnabled = services.Speech.IsChineseVoiceAvailable;
        if (!services.Speech.IsChineseVoiceAvailable)
        {
            ToolTipService.SetToolTip(SpeakButton, "No Chinese voice is installed");
        }
    }

    /// <param name="anchor">The snipped region, in virtual-desktop pixels.</param>
    /// <param name="desktop">Monitor layout, for clamping into the right work area.</param>
    public void Show(
        LookupResult result, float confidence,
        System.Drawing.Rectangle anchor, ZhongwenLens.Core.Capture.VirtualDesktop desktop)
    {
        Render(result, confidence);
        PositionNear(anchor, desktop);

        Activate();
        Interop.NativeMethods.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        Root.Focus(FocusState.Programmatic);
    }

    private void Render(LookupResult result, float confidence)
    {
        HeadlinePanel.Children.Clear();
        Body.Children.Clear();
        _cardsByWord.Clear();
        _spokenText = result.SourceText;

        if (result.IsEmpty)
        {
            RenderEmpty();
            return;
        }

        SpeakButton.IsEnabled = _services.Speech.IsChineseVoiceAvailable;
        CopyButton.IsEnabled = true;

        RenderHeadline(result);
        RenderMeaning(result);
        RenderBody(result);
        RenderStatus(result, confidence);

        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        ResizeToContent(result);
    }

    private void RenderEmpty()
    {
        HeadlinePanel.Children.Add(new TextBlock
        {
            Text = "No text found",
            FontSize = 22,
            Opacity = 0.75,
        });

        MeaningPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "Try selecting a tighter region, or one with more contrast.";
        SpeakButton.IsEnabled = false;
        CopyButton.IsEnabled = false;

        Resize(440, 190);
    }

    /// <summary>
    /// Builds the clickable word chips. Every token becomes a chip so a word can be clicked to
    /// jump to its card, which is what makes a long selection navigable rather than a wall.
    /// </summary>
    private void RenderHeadline(LookupResult result)
    {
        var ruby = result.SourceText.Length > RubyLayoutThreshold;

        foreach (var token in result.Tokens)
        {
            HeadlinePanel.Children.Add(BuildChip(token, ruby));
        }

        if (ruby) return;

        // Side-by-side: the whole reading sits after the text, the way a single word is
        // normally written out.
        if (result.Pinyin.Length == 0) return;

        HeadlinePanel.Children.Add(new TextBlock
        {
            Text = result.Pinyin,
            FontSize = 19,
            Opacity = 0.85,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = true,
        });
    }

    private FrameworkElement BuildChip(LookupToken token, bool ruby)
    {
        var stack = new StackPanel { Spacing = 0 };

        if (ruby)
        {
            stack.Children.Add(new TextBlock
            {
                Text = token.Pinyin,
                FontSize = 11,
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = token.Text,
            FontSize = ruby ? 26 : 36,
            FontFamily = ChineseFont,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var chip = new Border
        {
            Child = stack,
            Padding = new Thickness(4, 1, 4, 1),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };

        // A token with no entry can't be navigated to, so it stays inert rather than
        // pretending to be clickable.
        if (!token.HasEntries) return chip;

        ToolTipService.SetToolTip(chip, BuildChipTooltip(token));

        var word = token.Text;
        chip.PointerEntered += (_, _) => chip.Background =
            (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        chip.PointerExited += (_, _) => chip.Background =
            new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        chip.PointerPressed += (_, _) => ScrollToCard(word);

        return chip;
    }

    private static string BuildChipTooltip(LookupToken token)
    {
        var sense = token.Entries[0].PrimarySense;
        return token.Pinyin.Length > 0 ? $"{token.Pinyin} — {sense}" : sense;
    }

    /// <summary>Scrolls a word's card into view and flashes it, so the jump is visible.</summary>
    private void ScrollToCard(string word)
    {
        if (!_cardsByWord.TryGetValue(word, out var card)) return;

        var transform = card.TransformToVisual(Body);
        var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        Scroller.ChangeView(null, Math.Max(0, position.Y - 8), null);

        Flash(card);
    }

    private static void Flash(FrameworkElement card)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 1.0 });
        animation.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
            Value = 0.45,
        });
        animation.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)),
            Value = 1.0,
        });

        Storyboard.SetTarget(animation, card);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void RenderMeaning(LookupResult result)
    {
        if (result.IsWholeMatch)
        {
            // A single reading is already shown, numbered and with its HSK band and measure
            // word, on the card directly below. Repeating it as a semicolon run-on here was
            // pure duplication, so the panel is reserved for the case where several readings
            // make a one-line summary genuinely useful.
            if (result.WholeMatch.Count == 1)
            {
                MeaningPanel.Visibility = Visibility.Collapsed;
                return;
            }

            MeaningPanel.Visibility = Visibility.Visible;
            MeaningLabel.Text = $"{result.WholeMatch.Count} READINGS";
            MeaningText.Text = string.Join("   ·   ",
                result.WholeMatch.Select(e => $"{e.PinyinMarks} — {e.PrimarySense}"));
            return;
        }

        if (result.LiteralChain.Length > 0)
        {
            MeaningPanel.Visibility = Visibility.Visible;
            // Labelled literal so it is never mistaken for a fluent translation — the app has
            // no translator, and saying so is more useful than implying otherwise (§1.4).
            MeaningLabel.Text = "LITERAL, WORD BY WORD";
            MeaningText.Text = result.LiteralChain;
            return;
        }

        MeaningPanel.Visibility = Visibility.Collapsed;
    }

    private void RenderBody(LookupResult result)
    {
        // A whole-selection match shows its readings directly; the single token would
        // otherwise repeat the headline.
        if (result.IsWholeMatch)
        {
            foreach (var entry in result.WholeMatch)
            {
                Body.Children.Add(BuildEntryCard(entry, result.WholeMatch.Count > 1));
            }
        }
        else
        {
            foreach (var token in result.Tokens.Where(t => t.HasEntries))
            {
                var card = BuildWordCard(token);
                _cardsByWord[token.Text] = card;
                Body.Children.Add(card);
            }
        }

        if (result.CharacterWords.Count > 0)
        {
            Body.Children.Add(BuildCharacterWordsSection(result));
        }
    }

    /// <summary>
    /// "Common words using this character" — the single-character view from §3.4. Seeing a
    /// character in real words teaches more than any amount of detail about it in isolation.
    /// </summary>
    private FrameworkElement BuildCharacterWordsSection(LookupResult result)
    {
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock
        {
            Text = $"COMMON WORDS USING {result.SourceText}",
            FontSize = 10,
            Opacity = 0.55,
        });

        foreach (var entry in result.CharacterWords)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var word = new TextBlock
            {
                Text = entry.Simplified,
                FontSize = 17,
                FontFamily = ChineseFont,
            };
            Grid.SetColumn(word, 0);

            var pinyin = new TextBlock
            {
                Text = entry.PinyinMarks,
                FontSize = 12,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(pinyin, 1);

            var gloss = new TextBlock
            {
                Text = entry.PrimarySense,
                FontSize = 12,
                Opacity = 0.85,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(gloss, 2);

            row.Children.Add(word);
            row.Children.Add(pinyin);
            row.Children.Add(gloss);
            panel.Children.Add(row);
        }

        return Card(panel);
    }

    private FrameworkElement BuildWordCard(LookupToken token)
    {
        var primary = token.Entries[0];
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(BuildCardHeader(
            token.Text, token.Pinyin, token.HskLevel, primary.Radical,
            primary.Senses, primary.HasDistinctTraditional ? primary.Traditional : null));

        // Every reading, not just the first: 行 is xíng, háng and hàng, and silently picking
        // one would teach the wrong pronunciation (§3.4).
        foreach (var entry in token.Entries.Take(3))
        {
            AppendSenses(panel, entry, showReading: token.HasMultipleReadings);
        }

        return Card(panel);
    }

    private FrameworkElement BuildEntryCard(DictEntry entry, bool showReading)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(BuildCardHeader(
            entry.Simplified, entry.PinyinMarks, entry.HskNew ?? entry.HskOld, entry.Radical,
            entry.Senses, entry.HasDistinctTraditional ? entry.Traditional : null));

        AppendSenses(panel, entry, showReading);

        if (entry.HasDistinctTraditional)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"traditional {entry.Traditional}",
                FontSize = 11,
                Opacity = 0.55,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        return Card(panel);
    }

    /// <summary>
    /// Segoe MDL2 Assets glyphs, built from their code points rather than pasted as literal
    /// characters. They live in the Unicode private-use area, so in source they are invisible in
    /// most editors and diffs; a tool that fails to preserve them corrupts the icon silently
    /// instead of failing to build. Naming the code point keeps them reviewable.
    /// </summary>
    private static readonly string GlyphSpeaker = char.ConvertFromUtf32(0xE767);

    private static readonly string GlyphStarOutline = char.ConvertFromUtf32(0xE734);

    private static readonly string GlyphStarFilled = char.ConvertFromUtf32(0xE735);

    private StackPanel BuildCardHeader(
        string word, string pinyin, int? hsk, string? radical,
        IReadOnlyList<string> senses, string? traditional)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
        };

        header.Children.Add(new TextBlock
        {
            Text = word,
            FontSize = 21,
            FontFamily = ChineseFont,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = pinyin,
            FontSize = 14,
            Opacity = 0.85,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (hsk is { } level) header.Children.Add(BuildHskBadge(level));

        if (!string.IsNullOrEmpty(radical))
        {
            header.Children.Add(new TextBlock
            {
                Text = $"radical {radical}",
                FontSize = 11,
                Opacity = 0.5,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var speak = new Button
        {
            Content = GlyphSpeaker,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Padding = new Thickness(7, 3, 7, 3),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = _services.Speech.IsChineseVoiceAvailable,
        };
        ToolTipService.SetToolTip(speak, $"Speak {word}");

        // Without this a screen reader announces the raw Segoe MDL2 glyph, which is a
        // private-use code point and means nothing to anyone.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(speak, $"Speak {word}");

        speak.Click += (_, _) => _services.Speech.Speak(word);
        header.Children.Add(speak);

        header.Children.Add(BuildStarButton(word, pinyin, senses, hsk, traditional));

        return header;
    }

    /// <summary>
    /// Star toggle. Saves the word along with the whole snip as its context, which is what makes
    /// the eventual flashcard worth having (DESIGN.md §3.7).
    /// </summary>
    private Button BuildStarButton(
        string word, string pinyin, IReadOnlyList<string> senses, int? hsk, string? traditional)
    {
        var button = new Button
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Padding = new Thickness(7, 3, 7, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };

        ApplyStarState(button, word, _services.Study.Contains(word));

        button.Click += (_, _) =>
        {
            var wasSaved = _services.Study.Contains(word);
            if (wasSaved)
            {
                _services.Study.Remove(word);
            }
            else
            {
                // The same formatting the card shows. Saving the raw senses put CC-CEDICT's
                // "CL:家[jia1],個|个[ge4]" straight onto the exported flashcard.
                var formatted = SenseFormatter.Format(senses);

                _services.Study.Save(new SavedWord(
                    Simplified: word,
                    Traditional: traditional,
                    PinyinMarks: pinyin,
                    Senses: formatted.Senses,
                    HskLevel: hsk,
                    // The whole snip, so the card carries the sentence the word was met in.
                    SourceContext: _spokenText,
                    SavedAt: DateTimeOffset.Now,
                    Classifiers: formatted.Classifiers));
            }

            ApplyStarState(button, word, !wasSaved);
            StatusText.Text = $"{(wasSaved ? "Removed" : "Saved")} {word}   ·   " +
                              $"{_services.Study.Count} saved   ·   Esc to close";
        };

        return button;
    }

    private static void ApplyStarState(Button button, string word, bool saved)
    {
        // Filled versus outline, so the state reads at a glance without hovering.
        button.Content = saved ? GlyphStarFilled : GlyphStarOutline;

        var label = saved ? $"Remove {word} from saved words" : $"Save {word}";
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
    }

    private static Border BuildHskBadge(int level) => new()
    {
        Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(6, 1, 6, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            // 7 is the combined HSK 3.0 7-9 band, not a seventh level.
            Text = level == 7 ? "HSK 7-9" : $"HSK {level}",
            FontSize = 10,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        },
    };

    private static void AppendSenses(StackPanel panel, DictEntry entry, bool showReading)
    {
        if (showReading)
        {
            panel.Children.Add(new TextBlock
            {
                Text = entry.PinyinMarks,
                FontSize = 12,
                Opacity = 0.75,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 5, 0, 0),
            });
        }

        var formatted = SenseFormatter.Format(entry.Senses);

        // Capped: grammatical particles carry enormous CC-CEDICT entries — 的 has three
        // readings and a dozen senses full of parenthetical examples — and left uncapped a
        // single common particle pushes every other word in the selection off-screen. The
        // senses are ordered by importance, so the first few are the ones worth reading.
        var shown = formatted.Senses.Take(MaxSensesPerReading).ToList();
        var hidden = formatted.Senses.Count - shown.Count;

        // Numbered, because "the multiple meanings of it" is the point of the panel.
        panel.Children.Add(new TextBlock
        {
            Text = string.Join("\n", shown.Select((sense, index) => $"{index + 1}. {sense}")),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Opacity = 0.92,
        });

        if (hidden > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"+{hidden} more sense{(hidden == 1 ? string.Empty : "s")}",
                FontSize = 11,
                Opacity = 0.5,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        if (formatted.Classifiers.Length == 0) return;

        // Measure words matter to a learner, but CC-CEDICT's raw "CL:個|个[ge4]" reads as noise
        // among the definitions, so it gets its own labelled line.
        panel.Children.Add(new TextBlock
        {
            Text = $"measure word  {formatted.Classifiers}",
            FontSize = 12,
            Opacity = 0.65,
            Margin = new Thickness(0, 4, 0, 0),
            IsTextSelectionEnabled = true,
        });
    }

    private static Border Card(UIElement content) => new()
    {
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 9, 12, 9),
        Child = content,
    };

    private void RenderStatus(LookupResult result, float confidence)
    {
        var parts = new List<string>();

        // Only mention confidence when it's low enough to be worth double-checking; a
        // permanent "100%" badge is noise.
        if (confidence < 0.85f) parts.Add($"OCR confidence {confidence:P0} — check the characters");

        var unknown = result.Tokens.Count(t => !t.HasEntries);
        if (unknown > 0) parts.Add($"{unknown} unrecognised character(s)");

        parts.Add("Esc to close");
        StatusText.Text = string.Join("   ·   ", parts);
    }

    private void OnSpeakAll(object sender, RoutedEventArgs e) => _services.Speech.Speak(_spokenText);

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_spokenText.Length == 0) return;

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(_spokenText);
        Clipboard.SetContent(package);

        StatusText.Text = "Copied   ·   Esc to close";
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;

        e.Handled = true;
        _services.Speech.Stop();
        Close();
    }

    /// <summary>
    /// Sizes from the actual content. A flat per-card guess clips entries like 马马虎虎, which
    /// carries seven senses on its own.
    /// </summary>
    private void ResizeToContent(LookupResult result)
    {
        // Counts the lines actually rendered, not every sense in the entry — they're capped at
        // MaxSensesPerReading, so summing the raw totals would oversize the window for particles.
        var senseLines = result.IsWholeMatch
            ? result.WholeMatch.Sum(e => Math.Min(e.Senses.Count, MaxSensesPerReading))
            : result.Tokens.Where(t => t.HasEntries)
                .Sum(t => t.Entries.Take(3).Sum(e => Math.Min(e.Senses.Count, MaxSensesPerReading)));

        var cards = Body.Children.Count;
        var characterRows = result.CharacterWords.Count;
        var rubyExtra = result.SourceText.Length > RubyLayoutThreshold ? 30 : 0;

        var height = 200 + rubyExtra + (cards * 56) + (senseLines * 19) + (characterRows * 26);
        Resize(560, Math.Clamp(height, 250, 800));
    }

    private void PositionNear(
        System.Drawing.Rectangle anchor, ZhongwenLens.Core.Capture.VirtualDesktop desktop)
    {
        var appWindow = GetAppWindow();
        var size = appWindow.Size;
        var work = desktop.MonitorContaining(
                new System.Drawing.Point(
                    anchor.Left + (anchor.Width / 2), anchor.Top + (anchor.Height / 2)))
            .WorkArea;

        // Below the selection by preference, above it when there's no room, so the window never
        // covers the text that was just read.
        var x = anchor.Left;
        var y = anchor.Bottom + 12;
        if (y + size.Height > work.Bottom) y = anchor.Top - size.Height - 12;

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - size.Width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - size.Height));

        appWindow.Move(new PointInt32(x, y));
    }

    private void Resize(int width, int height)
    {
        var scale = Content.XamlRoot?.RasterizationScale ?? 1.0;
        GetAppWindow().Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
    }

    private AppWindow GetAppWindow()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
    }
}
