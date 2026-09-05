namespace ContrabandCases.Client.Audio;

internal enum MechanicalCue { Tick, Latch, Secure, Upgrade, Replace, Loss }

/// <summary>Original deterministic UI foley; its local noise never uses gameplay RNG.</summary>
internal static class MechanicalUiSound
{
    internal const int SampleRate = 44_100;
    internal static float[] Create(MechanicalCue cue)
    {
        if (!Enum.IsDefined(typeof(MechanicalCue), cue)) throw new ArgumentOutOfRangeException(nameof(cue));
        var duration = cue switch { MechanicalCue.Tick => 0.045, MechanicalCue.Latch => 0.18, MechanicalCue.Upgrade => 0.56, _ => 0.32 };
        var samples = new float[(int)(duration * SampleRate)];
        uint noiseState = 0x6c8e9cf5;
        var previousNoise = 0d;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)SampleRate;
            noiseState ^= noiseState << 13;
            noiseState ^= noiseState >> 17;
            noiseState ^= noiseState << 5;
            var noise = noiseState / (double)uint.MaxValue * 2 - 1;
            var highPass = (noise - previousNoise) * 0.5;
            previousNoise = noise;
            var transient = highPass * Math.Exp(-t * 150) * 0.55;
            var frequency = cue switch { MechanicalCue.Tick => 1350, MechanicalCue.Loss => 135, MechanicalCue.Replace => 280, _ => 410 };
            var resonance = Math.Sin(2 * Math.PI * frequency * t) * Math.Exp(-t * (cue == MechanicalCue.Tick ? 100 : 30)) * 0.28;
            var delayed = t - 0.065;
            var latch = cue != MechanicalCue.Tick && delayed > 0
                ? highPass * Math.Exp(-delayed * 100) * 0.40 + Math.Sin(2 * Math.PI * 690 * delayed) * Math.Exp(-delayed * 45) * 0.12 : 0;
            var accent = 0d;
            if (cue == MechanicalCue.Upgrade && t >= 0.12)
            {
                var local = t - 0.12;
                accent = (Math.Sin(2 * Math.PI * 660 * local) + Math.Sin(2 * Math.PI * 990 * local) * 0.4) * Math.Exp(-local * 8) * 0.13;
            }
            var fade = Math.Min(1, t / 0.0015) * Math.Min(1, (samples.Length - 1 - i) / (0.01 * SampleRate));
            samples[i] = (float)Math.Max(-0.85, Math.Min(0.85, (transient + resonance + latch + accent) * fade));
        }
        return samples;
    }
}
