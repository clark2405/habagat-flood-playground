using System;
using System.Collections.Generic;
using UnityEngine;

namespace Habagat.Audio
{
    /// <summary>
    /// The cue set from <c>src/audio.js</c>, baked into clips and played back.
    ///
    /// The reference builds a fresh Web Audio node graph per sound and throws it away
    /// when it stops. Unity has no equivalent, so every cue is synthesised once in
    /// <see cref="Awake"/> — about 40 ms of arithmetic — and then played as a clip.
    /// Baking on first use instead would put that cost inside the first brush stroke,
    /// which is the one moment it would be felt.
    ///
    /// Two AudioSources, not one: the rain bed loops and needs its own volume ramped
    /// independently, while everything else overlaps through PlayOneShot on a shared
    /// source. Both are <c>spatialBlend = 0</c> — these are interface sounds, not
    /// things standing somewhere on the map, so panning them by position would only
    /// make the same click sound different depending on where the camera had drifted.
    /// </summary>
    public class SoundEngine : MonoBehaviour
    {
        /// <summary>
        /// Everything is scaled by this on the way out. The per-cue gains are the
        /// reference's, which were chosen against a browser's output stage and land
        /// quiet here; this lifts the whole mix without disturbing the balance.
        /// </summary>
        [Range(0f, 4f)] public float masterVolume = 2.2f;

        public bool Muted { get; private set; }
        public bool Storm { get; private set; }

        /// <summary>The looping rain bed, so the self-test can read its level.</summary>
        public AudioSource RainSource { get; private set; }
        public AudioClip RainClip { get; private set; }

        private AudioSource _cues;
        private AudioClip _raise, _lower, _splash, _thunder;
        private readonly Dictionary<(int hz, Wave wave), AudioClip> _pops = new();
        private readonly Dictionary<AudioClip, float> _lastPlayed = new();

        private int _rate;

        // The reference's own numbers, kept as named constants so the mapping back to
        // audio.js is checkable line by line.
        private const float RainGain = 0.12f;
        private const float FadeTau = 0.5f;   // setTargetAtTime time constant

        private void Awake()
        {
            // Nothing is heard without a listener, and this scene is assembled in code
            // — `new GameObject` plus AddComponent<Camera>() does NOT bring one along
            // the way the editor's default camera object does. Adding it here rather
            // than only in MakeScene keeps the harness and any hand-built scene working
            // too, and a second listener would warn on every frame, so check first.
            if (FindFirstObjectByType<AudioListener>() == null)
            {
                var cam = Camera.main;
                (cam != null ? cam.gameObject : gameObject).AddComponent<AudioListener>();
            }

            _rate = AudioSettings.outputSampleRate;
            if (_rate <= 0) _rate = 44100;

            _cues = gameObject.AddComponent<AudioSource>();
            _cues.playOnAwake = false;
            _cues.spatialBlend = 0f;
            _cues.volume = masterVolume;

            _raise = Bake("Raise", Synth.Tone(_rate, 0.12, Wave.Triangle, 180, 280, false, 0.12, 0.001));
            _lower = Bake("Lower", Synth.Tone(_rate, 0.12, Wave.Triangle, 320, 160, false, 0.12, 0.001));
            _thunder = Bake("Thunder", Synth.Tone(_rate, 0.8, Wave.Saw, 80, 30, true, 0.3, 0.001));

            // Seeded, not Random.value: a cue that is a slightly different noise burst
            // on every launch is a cue nobody can A/B against a previous build.
            var rng = new System.Random(4242);
            _splash = Bake("Splash", Synth.Noise(_rate, 0.15,
                Synth.Biquad.BandPass(800, 3.0, _rate), 0.2, 0.001, rng));

            RainClip = Bake("RainLoop", Synth.Loop(_rate, 2.0,
                Synth.Biquad.LowPass(1200, 0.707, _rate), new System.Random(7)));

            RainSource = gameObject.AddComponent<AudioSource>();
            RainSource.clip = RainClip;
            RainSource.loop = true;
            RainSource.playOnAwake = false;
            RainSource.spatialBlend = 0f;
            RainSource.volume = 0f;
            RainSource.Play();
        }

        private AudioClip Bake(string name, float[] samples)
        {
            var clip = AudioClip.Create(name, samples.Length, 1, _rate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private void Update()
        {
            // Web Audio's setTargetAtTime is an exponential approach to a target with
            // a time constant, which is this expression exactly — not a Lerp with a
            // constant factor, which would be frame-rate dependent.
            float target = Muted || !Storm ? 0f : RainGain * masterVolume;
            RainSource.volume = Mathf.Lerp(RainSource.volume, target,
                                           1f - Mathf.Exp(-Time.unscaledDeltaTime / FadeTau));
            _cues.volume = masterVolume;
        }

        /// <summary>
        /// Play a clip, optionally refusing to retrigger for a while.
        ///
        /// The throttle is a deliberate divergence from the reference, which fires a
        /// cue from inside the per-cell paint loop: at a brush radius of 3 that is up
        /// to 29 oscillators per call and roughly 1700 a second while the mouse is
        /// held. A browser absorbs it as a buzz; Unity has a hard voice limit, so the
        /// same code here runs the mixer out of voices and the result is crackle. One
        /// cue per 90 ms is what the reference sounds like it was trying to be.
        /// </summary>
        private void Fire(AudioClip clip, float minInterval)
        {
            if (Muted || clip == null || _cues == null) return;
            if (minInterval > 0f)
            {
                if (_lastPlayed.TryGetValue(clip, out float t) &&
                    Time.unscaledTime - t < minInterval) return;
                _lastPlayed[clip] = Time.unscaledTime;
            }
            _cues.PlayOneShot(clip);
        }

        private const float BrushInterval = 0.09f;

        /// <summary>
        /// A short blip that rises a fifth — the interface's click. Baked on demand
        /// and cached, since the pitch is chosen by the caller and there is no list of
        /// them to bake up front.
        /// </summary>
        public void PlayPop(float freq, Wave wave = Wave.Sine, float minInterval = 0f)
        {
            var key = (Mathf.RoundToInt(freq), wave);
            if (!_pops.TryGetValue(key, out var clip))
            {
                clip = Bake($"Pop{key.Item1}", Synth.Tone(_rate, 0.08, wave,
                            freq, freq * 1.5f, true, 0.15, 0.001));
                _pops[key] = clip;
            }
            Fire(clip, minInterval);
        }

        public void PlayTerraform(bool raise) => Fire(raise ? _raise : _lower, BrushInterval);
        public void PlayWaterSplash() => Fire(_splash, BrushInterval);
        // Throttled like the terraform cues: these are painted, so they come from the
        // same held drag and would stack the same way.
        public void PlayPlant() => PlayPop(520f, Wave.Sine, BrushInterval);
        public void PlayBuild() => PlayPop(340f, Wave.Square, BrushInterval);
        public void PlayThunder() => Fire(_thunder, 0f);

        /// <summary>
        /// Storm on brings up the rain bed and cracks once. The thunder is tied to the
        /// button rather than to the lightning flashes the weather already produces,
        /// which is how the reference has it — worth revisiting, but not silently.
        /// </summary>
        public void SetStorm(bool storm)
        {
            Storm = storm;
            if (storm) PlayThunder();
        }

        public bool ToggleMute()
        {
            Muted = !Muted;
            if (Muted) _cues.Stop();
            return Muted;
        }
    }
}
