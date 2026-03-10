using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Real-time audio feature extraction for installations.
/// Works with AudioSource playback now; later you can swap the audio input source,
/// while keeping the same outputs/events.
///
/// Outputs:
/// - RMS, Peak (time-domain)
/// - Bands (frequency-domain)
/// - Bass/Mid/Treble summaries
/// - Spectral Centroid ("brightness")
/// - Onset/BeatPulse event (energy spike detector, focused on bass by default)
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
public class RealTimeAudioFeatures : MonoBehaviour
{
    [Header("Source")]
    public AudioSource source;

    [Header("Input Mode")]
    [Tooltip("AudioSource plays a clip. Microphone uses live mic or aux-in.")]
    public InputMode inputMode = InputMode.AudioSource;

    [Tooltip("Device name for microphone mode. Leave empty for the default device. " +
             "See Microphone.devices[] for available names.")]
    public string microphoneDevice = "";

    [Tooltip("Play microphone back through speakers. " +
             "Turn OFF when using a microphone (avoids feedback). " +
             "NOTE: volume is set to 0.0001 when off — NOT muted — so audio analysis still works.")]
    public bool micPlaythrough = false;

    [Header("Buffers")]
    [Tooltip("Waveform buffer size (power of 2 recommended).")]
    public int waveformSize = 1024;

    [Tooltip("Spectrum size (power of 2). Typical: 512/1024/2048.")]
    public int spectrumSize = 1024;

    [Header("Spectrum Bands")]
    [Tooltip("Number of bands to compress spectrum into.")]
    public int bandCount = 8;

    [Tooltip("Use log-ish band spacing (better for music).")]
    public bool logBands = true;

    [Header("Smoothing")]
    [Range(0f, 0.99f)] public float smoothing = 0.7f;

    [Header("Onset / Beat Pulse")]
    [Tooltip("Which metric drives onset detection. 'Bass' is most beat-like.")]
    public OnsetSource onsetSource = OnsetSource.Bass;

    [Tooltip("Exponential moving average speed for onset baseline. Higher = faster baseline.")]
    [Range(0.001f, 0.5f)] public float baselineAlpha = 0.02f;

    [Tooltip("How much above baseline triggers an onset (multiplier).")]
    [Range(1.05f, 5f)] public float onsetThreshold = 1.6f;

    [Tooltip("Minimum time between onsets (seconds).")]
    [Range(0.05f, 1f)] public float onsetCooldown = 0.15f;

    [Tooltip("If true, requires RMS to be above a small gate to allow onsets.")]
    public bool useRmsGate = true;

    [Tooltip("RMS gate threshold (adjust to ignore silence/noise).")]
    public float rmsGate = 0.0015f;

    [Header("Debug/Readout (live)")]
    public float rms;
    public float peak;
    public float bass;
    public float mid;
    public float treble;
    public float centroidHz;

    public float[] bands;         // raw bands
    public float[] bandsSmoothed; // smoothed bands

    public bool onsetThisFrame;   // true only on the frame an onset is detected
    public float onsetStrength;   // (metric / baseline) when triggered

    public event Action OnOnset;

    public enum OnsetSource { RMS, Peak, Bass, Mid, Treble }
    public enum InputMode { AudioSource, Microphone }

    private float[] waveform;
    private float[] spectrum;
    private float[] bandWork;
    private int[] bandEdges;

    // onset internals
    private float onsetBaseline = 1e-6f;
    private float lastOnsetTime = -999f;

    // mic internals
    private InputMode _appliedInputMode;
    private string _activeMicDevice;
    private AudioClip _savedClip;
    private bool _savedLoop;
    private bool _savedMute;
    private bool _micActive;

    private void Reset()
    {
        source = GetComponent<AudioSource>();
    }

    private void Awake()
    {
        if (source == null) source = GetComponent<AudioSource>();

        waveformSize = ClosestPowerOfTwo(Mathf.Max(128, waveformSize));
        spectrumSize = ClosestPowerOfTwo(Mathf.Max(128, spectrumSize));

        waveform = new float[waveformSize];
        spectrum = new float[spectrumSize];

        bandCount = Mathf.Max(1, bandCount);
        bands = new float[bandCount];
        bandsSmoothed = new float[bandCount];
        bandWork = new float[bandCount];

        bandEdges = logBands
            ? BuildLogBandEdges(spectrumSize, bandCount)
            : BuildLinearBandEdges(spectrumSize, bandCount);
    }

    private void Start()
    {
        _appliedInputMode = inputMode;
        if (inputMode == InputMode.Microphone)
            StartCoroutine(StartMicrophoneRoutine(microphoneDevice));
    }

    private void Update()
    {
        if (source == null) return;

        // Detect inspector / runtime changes to inputMode
        if (inputMode != _appliedInputMode)
            ApplyInputMode(inputMode);

        onsetThisFrame = false;
        onsetStrength = 0f;

        // --- Time-domain features ---
        source.GetOutputData(waveform, 0);

        float sumSq = 0f;
        float maxAbs = 0f;
        for (int i = 0; i < waveform.Length; i++)
        {
            float x = waveform[i];
            sumSq += x * x;
            float ax = Mathf.Abs(x);
            if (ax > maxAbs) maxAbs = ax;
        }

        float newRms = Mathf.Sqrt(sumSq / waveform.Length);
        float newPeak = maxAbs;

        // Smooth time-domain outputs
        rms = Mathf.Lerp(rms, newRms, 1f - smoothing);
        peak = Mathf.Lerp(peak, newPeak, 1f - smoothing);

        // --- Frequency-domain features ---
        source.GetSpectrumData(spectrum, 0, FFTWindow.BlackmanHarris);

        ComputeBands(spectrum, bandWork, bandEdges);

        for (int b = 0; b < bandCount; b++)
        {
            bands[b] = bandWork[b];
            bandsSmoothed[b] = Mathf.Lerp(bandsSmoothed[b], bands[b], 1f - smoothing);
        }

        // Summaries: bass/mid/treble (based on band partitions)
        ComputeBassMidTreble(bandsSmoothed, out float bVal, out float mVal, out float tVal);
        bass = Mathf.Lerp(bass, bVal, 1f - smoothing);
        mid = Mathf.Lerp(mid, mVal, 1f - smoothing);
        treble = Mathf.Lerp(treble, tVal, 1f - smoothing);

        // Spectral centroid (brightness)
        centroidHz = Mathf.Lerp(centroidHz, ComputeCentroidHz(spectrum), 1f - smoothing);

        // --- Onset detection (beat-like pulse) ---
        float metric = GetOnsetMetric();
        bool gated = !useRmsGate || rms >= rmsGate;

        // Update baseline EMA (only when we have some signal to avoid baseline collapsing)
        if (gated)
        {
            onsetBaseline = Mathf.Lerp(onsetBaseline, metric, baselineAlpha);
            onsetBaseline = Mathf.Max(onsetBaseline, 1e-6f);

            float ratio = metric / onsetBaseline;
            bool cooldownOk = (Time.time - lastOnsetTime) >= onsetCooldown;

            if (cooldownOk && ratio >= onsetThreshold)
            {
                onsetThisFrame = true;
                onsetStrength = ratio;
                lastOnsetTime = Time.time;
                OnOnset?.Invoke();
            }
        }
        else
        {
            // If silent, gently decay baseline toward small value
            onsetBaseline = Mathf.Lerp(onsetBaseline, 1e-6f, baselineAlpha);
        }
    }

    // ----------------- Input mode switching -----------------

    /// <summary>Switch input mode at runtime (also reacts to Inspector changes).</summary>
    public void ApplyInputMode(InputMode mode)
    {
        if (mode == InputMode.Microphone)
        {
            if (_micActive) return; // already running
            StartCoroutine(StartMicrophoneRoutine(microphoneDevice));
        }
        else
        {
            StopMicrophone();
        }
        _appliedInputMode = mode;
        inputMode = mode;
    }

    private IEnumerator StartMicrophoneRoutine(string device)
    {
        if (source == null) yield break;

        // Save current AudioSource state
        _savedClip  = source.clip;
        _savedLoop  = source.loop;
        _savedMute  = source.mute;

        _activeMicDevice = device;

        // Use a 2-second ring buffer so there is always headroom between
        // the mic write head and the AudioSource read head.
        int sr = AudioSettings.outputSampleRate;
        AudioClip micClip = Microphone.Start(_activeMicDevice, true, 2, sr);
        source.clip   = micClip;
        source.loop   = true;
        // IMPORTANT: volume must stay at 1f.
        // GetOutputData and GetSpectrumData read the post-volume DSP buffer, so reducing
        // volume scales down all extracted features — the same problem as muting.
        // To suppress speaker output without breaking analysis: assign this AudioSource
        // to a silent AudioMixerGroup (set the group's volume to -80 dB in the Mixer window).
        // micPlaythrough is preserved as a label; actual speaker suppression requires a MixerGroup.
        source.mute   = false;
        source.volume = 1f;

        // Wait until the mic has buffered at least 256 ms worth of samples
        int minSamples = sr / 4;
        while (Microphone.GetPosition(_activeMicDevice) < minSamples)
            yield return null;

        // Start the AudioSource slightly behind the current write head so it
        // always reads real mic data rather than the unwritten part of the buffer.
        int writeHead  = Microphone.GetPosition(_activeMicDevice);
        int readOffset = Mathf.Max(0, writeHead - 1024); // ~23 ms behind
        source.timeSamples = readOffset;
        source.Play();

        _micActive = true;
        _appliedInputMode = InputMode.Microphone;
    }

    private void StopMicrophone()
    {
        if (!_micActive) return;

        source.Stop();
        Microphone.End(_activeMicDevice);

        source.clip   = _savedClip;
        source.loop   = _savedLoop;
        source.mute   = _savedMute;
        source.volume = 1f;

        _micActive = false;
    }

    private void OnDestroy()
    {
        if (_micActive) Microphone.End(_activeMicDevice);
    }

    // ----------------- Helpers -----------------

    private float GetOnsetMetric()
    {
        switch (onsetSource)
        {
            case OnsetSource.RMS: return rms;
            case OnsetSource.Peak: return peak;
            case OnsetSource.Bass: return bass;
            case OnsetSource.Mid: return mid;
            case OnsetSource.Treble: return treble;
            default: return bass;
        }
    }

    private void ComputeBands(float[] spec, float[] outBands, int[] edges)
    {
        Array.Clear(outBands, 0, outBands.Length);

        for (int b = 0; b < outBands.Length; b++)
        {
            int a = edges[b];
            int z = edges[b + 1];

            float sum = 0f;
            int count = 0;
            for (int i = a; i < z; i++)
            {
                sum += spec[i];
                count++;
            }

            outBands[b] = (count > 0) ? (sum / count) : 0f;
        }
    }

    private void ComputeBassMidTreble(float[] b, out float bassOut, out float midOut, out float trebleOut)
    {
        // Split bands into 3 chunks: low / mid / high
        // e.g. 8 bands => [0,1] bass, [2,3,4] mid, [5,6,7] treble
        int n = b.Length;

        int bassEnd = Mathf.Max(1, n / 4);          // first 25%
        int trebleStart = Mathf.Min(n - 1, (n * 3) / 4); // last 25%
        int midStart = bassEnd;
        int midEnd = trebleStart;

        bassOut = Avg(b, 0, bassEnd);
        midOut = Avg(b, midStart, midEnd);
        trebleOut = Avg(b, trebleStart, n);
    }

    private float Avg(float[] arr, int start, int endExclusive)
    {
        start = Mathf.Clamp(start, 0, arr.Length);
        endExclusive = Mathf.Clamp(endExclusive, 0, arr.Length);
        if (endExclusive <= start) return 0f;

        float sum = 0f;
        int count = 0;
        for (int i = start; i < endExclusive; i++)
        {
            sum += arr[i];
            count++;
        }
        return (count > 0) ? (sum / count) : 0f;
    }

    private float ComputeCentroidHz(float[] spec)
    {
        // spec bins cover 0..Nyquist
        float sr = AudioSettings.outputSampleRate;
        float nyquist = sr * 0.5f;

        float num = 0f;
        float den = 0f;

        // Ignore DC bin 0 to avoid bias
        for (int i = 1; i < spec.Length; i++)
        {
            float mag = spec[i];
            float freq = (i / (float)spec.Length) * nyquist;
            num += freq * mag;
            den += mag;
        }

        if (den <= 1e-9f) return 0f;
        return num / den;
    }

    private int[] BuildLinearBandEdges(int nBins, int bands)
    {
        int[] edges = new int[bands + 1];
        edges[0] = 0;
        edges[bands] = nBins;

        for (int b = 1; b < bands; b++)
        {
            edges[b] = Mathf.RoundToInt((b / (float)bands) * nBins);
            edges[b] = Mathf.Clamp(edges[b], edges[b - 1] + 1, nBins - 1);
        }
        return edges;
    }

    private int[] BuildLogBandEdges(int nBins, int bands)
    {
        // "log-ish" spacing across bins for more low-frequency resolution
        int[] edges = new int[bands + 1];
        edges[0] = 0;
        edges[bands] = nBins;

        float logMax = Mathf.Log(nBins);
        for (int b = 1; b < bands; b++)
        {
            float t = b / (float)bands;
            int idx = Mathf.RoundToInt(Mathf.Exp(t * logMax));
            edges[b] = Mathf.Clamp(idx, edges[b - 1] + 1, nBins - 1);
        }
        return edges;
    }

    private int ClosestPowerOfTwo(int x)
    {
        int p = 1;
        while (p < x) p <<= 1;
        return p;
    }
}