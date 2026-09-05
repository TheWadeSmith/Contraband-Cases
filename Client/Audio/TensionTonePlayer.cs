using UnityEngine;

namespace ContrabandCases.Client.Audio;

/// <summary>
/// Plays a low, looping, procedurally-synthesized drone whose pitch and
/// volume rise together with the reveal's own spin progress -- a rising
/// "tension" bed under the discrete tick sounds that cuts out the instant
/// the reel lands. The waveform is generated in code via AudioClip.Create/SetData, exactly
/// like TickSoundPlayer.
///
/// This is a pure cosmetic nicety: every public member is defensive, and a
/// failure anywhere in Unity's audio pipeline degrades to silence rather
/// than interrupting the reveal it accompanies.
/// </summary>
internal sealed class TensionTonePlayer : IDisposable
{
    private const int SampleRate = 44100;
    private const float LoopDurationSeconds = 0.5f;
    private const float BaseToneFrequencyHz = 90f;
    private const float PeakVolume = 0.22f;
    private const float MinPitch = 0.6f;
    private const float MaxPitch = 2.2f;

    private readonly UiAudioChannel _channel;
    private AudioClip? _clip;
    private bool _disposed;
    private bool _playing;

    public TensionTonePlayer(Action<string>? diagnostic = null) =>
        _channel = new UiAudioChannel("ContrabandCasesTensionAudio", diagnostic);

    /// <summary>
    /// Drives the drone from the reveal's own 0..1 spin progress. Pitch and
    /// volume both climb with progress so the tone reads as mounting
    /// tension toward the landing tile. Starts the loop on first call and
    /// is a no-op once <see cref="Stop"/> has been called for this reveal.
    /// </summary>
    public void SetProgress(float progress, float volume = 1f)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var clamped = Mathf.Clamp01(progress);
            if (volume <= 0f)
            {
                Stop();
                return;
            }
            var source = _channel.Acquire();
            if (source == null) return;
            if (_clip == null) _clip = BuildClip();
            source.clip = _clip;
            source.loop = true;
            source.pitch = Mathf.Clamp(Mathf.Lerp(MinPitch, MaxPitch, clamped), MinPitch, MaxPitch);
            source.volume = PeakVolume * clamped * Mathf.Clamp01(volume);
            if (!_playing || !source.isPlaying)
            {
                source.Play();
                _playing = true;
                _channel.ReportPlayback(source);
            }
        }
        catch (Exception exception)
        {
            _channel.ReportFailure(exception);
        }
    }

    /// <summary>
    /// Cuts the drone immediately. Safe to call even if it was never
    /// started, and safe to call more than once (a Skip followed by the
    /// normal landing path, for example).
    /// </summary>
    public void Stop()
    {
        if (_disposed || !_playing)
        {
            return;
        }

        try
        {
            _channel.Stop();
        }
        catch (Exception)
        {
            // Best-effort only -- a stuck drone is a cosmetic annoyance,
            // not a reason to fail the reveal.
        }
        finally
        {
            _playing = false;
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
            if (_clip != null) UnityEngine.Object.Destroy(_clip);
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
    }

    private static AudioClip BuildClip()
    {
        var sampleCount = Mathf.Max(1, Mathf.RoundToInt(SampleRate * LoopDurationSeconds));
        var clip = AudioClip.Create("ContrabandCasesTension", sampleCount, 1, SampleRate, false);
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var t = (float)i / SampleRate;
            // Two detuned low sine partials for a subtle beating "engine
            // spooling up" texture, rather than a flat pure tone.
            var a = Mathf.Sin(2f * Mathf.PI * BaseToneFrequencyHz * t);
            var b = Mathf.Sin(2f * Mathf.PI * (BaseToneFrequencyHz * 1.5f) * t);
            samples[i] = a * 0.7f + b * 0.3f;
        }

        if (!clip.SetData(samples, 0))
        {
            UnityEngine.Object.Destroy(clip);
            throw new InvalidOperationException("Tension waveform upload failed.");
        }
        return clip;
    }
}
