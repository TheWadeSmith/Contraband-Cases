using Comfort.Common;
using EFT.UI;
using UnityEngine;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;

namespace ContrabandCases.Client.Audio;

/// <summary>
/// Plays a short procedurally-synthesized "tick" through a dedicated
/// AudioSource. Waveforms are generated via AudioClip.Create/SetData;
/// result tones reuse the same source and mixer routing.
///
/// This is a pure cosmetic nicety: every public member is defensive, and a
/// failure anywhere in Unity's audio pipeline degrades to silence rather
/// than interrupting the reveal it accompanies.
/// </summary>
internal sealed class TickSoundPlayer : IDisposable
{
    private const int SampleRate = 44100;

    private readonly UiAudioChannel _channel;
    private readonly UiAudioChannel _cueChannel;
    private AudioClip? _tick;
    private readonly List<AudioClip> _clips = [];
    private readonly Dictionary<MechanicalCue, AudioClip> _outcomes = [];
    private bool _disposed;
    public string Status => "Card ticks: " + _channel.Status + "\nCues: " + _cueChannel.Status;

    public TickSoundPlayer(Action<string>? diagnostic = null)
    {
        _channel = new UiAudioChannel("ContrabandCasesTickAudio", diagnostic);
        // Card ticks restart frequently. Keep latch/result cues on their own source
        // so the first tick cannot cut off the opening latch.
        _cueChannel = new UiAudioChannel("ContrabandCasesCueAudio", diagnostic);
    }

    public void PlayOutcome(ManifestRelayResult? outcome, RewardRarity rarity, float volume) =>
        PlayCue(outcome switch
        {
            ManifestRelayResult.Confiscated => MechanicalCue.Loss,
            ManifestRelayResult.Sidegrade => MechanicalCue.Replace,
            ManifestRelayResult.Upgrade => MechanicalCue.Upgrade,
            _ => rarity == RewardRarity.BlackLabel ? MechanicalCue.Upgrade : MechanicalCue.Secure
        }, volume);

    public void PlayLatch(float volume) => PlayCue(MechanicalCue.Latch, volume);

    private void PlayCue(MechanicalCue cue, float volume)
    {
        if (_disposed || volume <= 0f) return;
        try
        {
            var source = _cueChannel.Acquire();
            if (source == null) return;
            if (!_outcomes.TryGetValue(cue, out var clip))
            {
                var samples = MechanicalUiSound.Create(cue);
                clip = AudioClip.Create($"ContrabandCases{cue}", samples.Length, 1, SampleRate, false);
                if (!clip.SetData(samples, 0))
                {
                    UnityEngine.Object.Destroy(clip);
                    throw new InvalidOperationException("Result waveform upload failed.");
                }
                _clips.Add(clip);
                _outcomes.Add(cue, clip);
            }
            source.Stop();
            source.pitch = 1f;
            source.volume = Mathf.Clamp01(volume);
            source.PlayOneShot(clip);
            _cueChannel.ReportPlayback(source);
        }
        catch (Exception exception)
        {
            _cueChannel.ReportFailure(exception);
        }
    }

    /// <summary>
    /// Plays one tick. <paramref name="pitch"/> tracks the reveal's current
    /// spin speed so ticks stretch with the spin's own easing curve --
    /// rapid near the start, further apart and lower as it settles.
    /// </summary>
    public void Play(float pitch, float volume)
    {
        if (_disposed || volume <= 0f)
        {
            return;
        }

        try
        {
            var source = _channel.Acquire();
            if (source == null) return;
            if (_tick == null)
            {
                _tick = BuildClip();
                _clips.Add(_tick);
            }
            source.pitch = Mathf.Clamp(pitch, 0.4f, 2.5f);
            source.volume = Mathf.Clamp01(volume);
            source.clip = _tick;
            source.Stop();
            source.Play();
            _channel.ReportPlayback(source);
        }
        catch (Exception exception)
        {
            _channel.ReportFailure(exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _channel.Dispose();
            _cueChannel.Dispose();
            foreach (var clip in _clips) UnityEngine.Object.Destroy(clip);
            _clips.Clear();
            _outcomes.Clear();
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
    }

    private static AudioClip BuildClip()
    {
        var samples = MechanicalUiSound.Create(MechanicalCue.Tick);
        var clip = AudioClip.Create("ContrabandCasesTick", samples.Length, 1, SampleRate, false);

        if (!clip.SetData(samples, 0))
        {
            UnityEngine.Object.Destroy(clip);
            throw new InvalidOperationException("Tick waveform upload failed.");
        }
        return clip;
    }
}
