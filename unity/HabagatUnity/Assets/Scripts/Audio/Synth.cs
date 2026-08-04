using System;

namespace Habagat.Audio
{
    /// <summary>Which waveform an oscillator runs. Web Audio's four, same names.</summary>
    public enum Wave { Sine, Triangle, Square, Saw }

    /// <summary>
    /// PCM generated from scratch, the audio counterpart of every mesh and sprite in
    /// this project: no imported .wav files, and a cue stays a handful of numbers in
    /// code rather than a binary somebody has to open an editor to inspect.
    ///
    /// The reference is <c>src/audio.js</c>, which builds each sound out of Web Audio
    /// nodes — an oscillator with a frequency ramp into a gain with an exponential
    /// decay. Unity has no node graph, so the same envelopes are evaluated here per
    /// sample and baked into a clip. The parameters are carried across literally.
    ///
    /// Nothing here touches UnityEngine, for the same reason the flood simulation
    /// does not: it can be run and checked without a graphics device.
    /// </summary>
    public static class Synth
    {
        /// <summary>
        /// Waveforms are summed from harmonics rather than switched on the sign of a
        /// phase, because a naive square or saw aliases — the harmonics above Nyquist
        /// fold back down as inharmonic whistling, worst on exactly the low sweeps
        /// this project uses for thunder. Web Audio's oscillators are band-limited for
        /// the same reason; this is what that costs when you write it yourself.
        ///
        /// Capped at 32 partials: past that the bake gets slow and nothing is audibly
        /// added, since 32 harmonics of the 340 Hz build cue already reach 11 kHz.
        /// </summary>
        public const int MaxHarmonics = 32;

        /// <summary>
        /// A direct-form-1 biquad, coefficients from the RBJ audio cookbook — the same
        /// derivation Web Audio's BiquadFilterNode uses, so the filter curves match
        /// rather than merely resemble each other.
        /// </summary>
        public struct Biquad
        {
            private double _b0, _b1, _b2, _a1, _a2;
            private double _x1, _x2, _y1, _y2;

            private static Biquad Make(double b0, double b1, double b2,
                                       double a0, double a1, double a2)
            {
                var f = new Biquad
                {
                    _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0,
                    _a1 = a1 / a0, _a2 = a2 / a0,
                };
                return f;
            }

            public static Biquad LowPass(double freq, double q, int rate)
            {
                double w0 = 2 * Math.PI * freq / rate;
                double cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
                return Make((1 - cos) / 2, 1 - cos, (1 - cos) / 2,
                            1 + alpha, -2 * cos, 1 - alpha);
            }

            public static Biquad BandPass(double freq, double q, int rate)
            {
                double w0 = 2 * Math.PI * freq / rate;
                double cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
                // Constant 0 dB peak gain, which is the form Web Audio implements.
                return Make(alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha);
            }

            public double Process(double x)
            {
                double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x;
                _y2 = _y1; _y1 = y;
                return y;
            }
        }

        /// <summary>
        /// One sample of a band-limited waveform at the given accumulated phase.
        ///
        /// Phase is passed in rather than derived from time because every cue here
        /// sweeps its frequency, and <c>sin(2*pi*f(t)*t)</c> is not the same signal as
        /// an oscillator running at f(t) — it sweeps at roughly twice the rate and
        /// arrives somewhere else entirely. The phase has to be integrated.
        /// </summary>
        public static double Eval(Wave wave, double phase, double freq, int rate)
        {
            if (wave == Wave.Sine) return Math.Sin(phase);

            int n = (int)Math.Min(MaxHarmonics, Math.Floor(rate * 0.5 / Math.Max(freq, 1.0)));
            if (n < 1) n = 1;
            double sum = 0;

            switch (wave)
            {
                case Wave.Saw:
                    for (int k = 1; k <= n; k++)
                        sum += (k % 2 == 1 ? 1 : -1) * Math.Sin(k * phase) / k;
                    return sum * (2.0 / Math.PI);

                case Wave.Square:
                    for (int k = 1; k <= n; k += 2)
                        sum += Math.Sin(k * phase) / k;
                    return sum * (4.0 / Math.PI);

                default: // Triangle
                    for (int k = 1; k <= n; k += 2)
                        sum += ((k - 1) / 2 % 2 == 0 ? 1 : -1) * Math.Sin(k * phase) / (double)(k * k);
                    return sum * (8.0 / (Math.PI * Math.PI));
            }
        }

        /// <summary>
        /// An oscillator sweeping <paramref name="f0"/> to <paramref name="f1"/> under
        /// a gain falling <paramref name="g0"/> to <paramref name="g1"/>.
        ///
        /// The gain always decays exponentially, matching Web Audio's
        /// <c>exponentialRampToValueAtTime</c>, which every cue in the reference uses
        /// and which is why they read as plucks rather than as beeps. The frequency
        /// ramp can be either shape because the reference uses both.
        ///
        /// Peak gains are the reference's own — 0.15 for a pop, 0.3 for thunder — so
        /// the balance between cues carries across even though the absolute level is
        /// quiet by Unity's conventions. Raise the whole mix at the AudioSource, never
        /// one cue here, or the relationship is lost.
        /// </summary>
        public static float[] Tone(int rate, double seconds, Wave wave,
                                   double f0, double f1, bool expFreq,
                                   double g0, double g1)
        {
            int n = Math.Max(1, (int)(rate * seconds));
            var buf = new float[n];
            double phase = 0, dt = 1.0 / rate;

            for (int i = 0; i < n; i++)
            {
                double t = i * dt / seconds;
                double f = expFreq ? f0 * Math.Pow(f1 / f0, t) : f0 + (f1 - f0) * t;
                buf[i] = (float)(Eval(wave, phase, f, rate) * g0 * Math.Pow(g1 / g0, t));
                phase += 2 * Math.PI * f * dt;
            }
            return buf;
        }

        /// <summary>Filtered white noise under an exponential decay — the splash.</summary>
        public static float[] Noise(int rate, double seconds, Biquad filter,
                                    double g0, double g1, Random rng)
        {
            int n = Math.Max(1, (int)(rate * seconds));
            var buf = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)n;
                buf[i] = (float)(filter.Process(rng.NextDouble() * 2 - 1) * g0 * Math.Pow(g1 / g0, t));
            }
            return buf;
        }

        /// <summary>
        /// The rain bed: filtered noise, built to loop without a click.
        ///
        /// The filter runs over the buffer TWICE and only the second pass is kept. A
        /// biquad has memory, so a single pass starts from silence and ends somewhere
        /// else, and the seam where the clip wraps steps between those two states —
        /// audible as a tick once every two seconds, which is exactly the kind of
        /// artefact the ear locks onto. After a warm-up pass the filter arrives at the
        /// wrap in the state it starts from. The noise itself still jumps at the seam,
        /// but white noise jumps at every sample, so there is nothing to hear.
        /// </summary>
        public static float[] Loop(int rate, double seconds, Biquad filter, Random rng)
        {
            int n = Math.Max(1, (int)(rate * seconds));
            var raw = new double[n];
            for (int i = 0; i < n; i++) raw[i] = rng.NextDouble() * 2 - 1;

            var buf = new float[n];
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n; i++)
                {
                    double y = filter.Process(raw[i]);
                    if (pass == 1) buf[i] = (float)y;
                }
            return buf;
        }
    }
}
