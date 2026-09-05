using Comfort.Common;
using EFT.UI;
using UnityEngine;

namespace ContrabandCases.Client.Audio;

/// <summary>Owns one menu-audio source, acquired only after EFT's UI mixer is ready.</summary>
internal sealed class UiAudioChannel : IDisposable
{
    private readonly string _name;
    private readonly Action<string>? _diagnostic;
    private GameObject? _host;
    private AudioSource? _source;
    private string? _lastFailure;
    private bool _reportedPlayback;
    private bool _disposed;
    public string Status { get; private set; } = "Audio has not been tested yet.";

    public UiAudioChannel(string name, Action<string>? diagnostic)
    {
        _name = name;
        _diagnostic = diagnostic;
    }

    public AudioSource? Acquire()
    {
        if (_disposed) return null;
        if (!Singleton<GUISounds>.Instantiated) { Status = "Interface audio is not ready. Return to the stash and retry."; return null; }
        var mixer = Singleton<GUISounds>.Instance.GetCommonSoundsMixerGroup();
        if (mixer == null) { Status = "Interface mixer unavailable. Reward processing is unaffected."; return null; }
        if (_source == null)
        {
            if (_host != null) UnityEngine.Object.Destroy(_host);
            _host = new GameObject(_name);
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _source = _host.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
            // Menu effects must remain audible while gameplay audio is paused.
            // The UI mixer still applies the player's interface/master volume.
            _source.ignoreListenerPause = true;
            _source.priority = 64;
        }
        _source.outputAudioMixerGroup = mixer;
        return _source;
    }

    public void ReportPlayback(AudioSource source)
    {
        Status = $"Playback requested through {source.outputAudioMixerGroup.name}; source playing: {source.isPlaying}.\n" +
            "This checks routing, not audibility. If silent, check Interface and Master volume.";
        if (_reportedPlayback) return;
        _reportedPlayback = true;
        _diagnostic?.Invoke($"{_name}: playing={source.isPlaying}, mixer={source.outputAudioMixerGroup.name}, " +
            $"listenerPaused={AudioListener.pause}, listenerVolume={AudioListener.volume:0.##}, volume={source.volume:0.##}.");
    }

    public void ReportFailure(Exception exception)
    {
        var message = $"{exception.GetType().Name}: {exception.Message}";
        Status = "Sound unavailable: " + message;
        if (_lastFailure == message) return;
        _lastFailure = message;
        _diagnostic?.Invoke($"{_name} unavailable: {message}. Reward processing is unaffected.");
    }

    public void Stop()
    {
        if (_source != null) _source.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_host != null) UnityEngine.Object.Destroy(_host);
        _host = null;
        _source = null;
    }
}
