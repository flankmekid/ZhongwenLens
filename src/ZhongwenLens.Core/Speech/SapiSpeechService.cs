using System.Speech.Synthesis;
using ZhongwenLens.Core.Text;

namespace ZhongwenLens.Core.Speech;

public interface ISpeechService : IDisposable
{
    /// <summary>False when no Chinese voice is installed; callers should disable their buttons.</summary>
    bool IsChineseVoiceAvailable { get; }

    /// <summary>Name of the selected voice, for settings and diagnostics.</summary>
    string? VoiceName { get; }

    void Speak(string? text);

    void Stop();
}

/// <summary>
/// Offline text-to-speech through SAPI.
/// </summary>
/// <remarks>
/// Windows ships <c>Microsoft Huihui Desktop (zh-CN)</c>, so this works with no download and no
/// network. Huihui sounds dated next to a neural voice, but it is intelligible and it keeps the
/// app fully offline, which is the point. A neural implementation would be a second
/// <see cref="ISpeechService"/> and nothing else would change.
/// </remarks>
public sealed class SapiSpeechService : ISpeechService
{
    private readonly SpeechSynthesizer? _synthesizer;
    private readonly Lock _gate = new();

    public SapiSpeechService()
    {
        try
        {
            var synthesizer = new SpeechSynthesizer();
            var voice = SelectChineseVoice(synthesizer);

            if (voice is null)
            {
                synthesizer.Dispose();
                return;
            }

            synthesizer.SelectVoice(voice);
            synthesizer.SetOutputToDefaultAudioDevice();

            // Slightly under default: learners need to hear tone contours, and SAPI's normal
            // rate clips them on short words.
            synthesizer.Rate = -1;

            _synthesizer = synthesizer;
            VoiceName = voice;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            // No audio device or no speech support: the app stays usable without audio.
            _synthesizer = null;
        }
    }

    public bool IsChineseVoiceAvailable => _synthesizer is not null;

    public string? VoiceName { get; }

    private static string? SelectChineseVoice(SpeechSynthesizer synthesizer)
    {
        var voices = synthesizer.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo)
            .ToList();

        // Prefer mainland Mandarin, then any Chinese variant.
        return voices.FirstOrDefault(v => v.Culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))?.Name
            ?? voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName
                .Equals("zh", StringComparison.OrdinalIgnoreCase))?.Name;
    }

    public void Speak(string? text)
    {
        if (_synthesizer is null) return;

        // Filtered here rather than at each call site, so every path — the whole selection, a
        // single word card, speak-on-capture — gets the same treatment. A snip of a dictionary
        // page catches headings and buttons alongside the Chinese, and a Mandarin voice reading
        // "Simplified Chinese" and "Learn More" is worse than useless.
        var speakable = SpeechText.Extract(text);
        if (speakable.Length == 0) return;

        lock (_gate)
        {
            // Cancel whatever is playing first, so repeatedly clicking the speaker button
            // replaces the audio instead of queuing several utterances back to back.
            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.SpeakAsync(speakable);
        }
    }

    public void Stop()
    {
        if (_synthesizer is null) return;

        lock (_gate)
        {
            _synthesizer.SpeakAsyncCancelAll();
        }
    }

    public void Dispose()
    {
        Stop();
        _synthesizer?.Dispose();
    }
}
