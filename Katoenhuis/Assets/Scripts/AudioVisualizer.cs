using UnityEngine;

/// <summary>
/// 2-D grid visualizer driven by RealTimeAudioFeatures.
///
/// Renders via Graphics.DrawMeshInstanced — no per-block GameObjects.
/// Supports thousands of blocks at the cost of a single material draw call
/// per 1023 blocks. The block prefab's material MUST have GPU Instancing enabled.
///
/// Animation channels:
///   Y Position  – vertical displacement
///   Scale       – uniform size
///   Rotation    – spin around an axis
/// </summary>
public class AudioVisualizer : MonoBehaviour
{
    // ── Reference ────────────────────────────────────────────────────────────
    [Header("Reference")]
    public RealTimeAudioFeatures audio;

    // ── Grid ─────────────────────────────────────────────────────────────────
    [Header("Grid")]
    [Tooltip("Prefab whose MeshFilter + MeshRenderer are used for instanced rendering. " +
             "Its material must have GPU Instancing enabled.")]
    public MeshRenderer blockPrefab;

    [Tooltip("Number of columns (time axis in spectrogram mode).")]
    public int columns = 32;

    [Tooltip("Number of rows (frequency axis in spectrogram mode).")]
    public int rows = 8;

    [Tooltip("Width of cubes")]
    [Range(0.2f, 2f)]
    [SerializeField] private float xScale;

    [Tooltip("Height of cubes")]
    [Range(0.2f, 2f)]
    [SerializeField] private float zScale;

    [Tooltip("World-space distance between block centres on X. " +
             "Set equal to block width for zero gap.")]
    public float spacingX = 1f;

    [Tooltip("World-space distance between block centres on Z. " +
             "Set equal to block depth for zero gap.")]
    public float spacingZ = 1f;

    // ── Animation slots ───────────────────────────────────────────────────────
    [Header("Y Position")]
    public AudioDrivenSlot yPosition = new()
        { enabled = true, source = AudioFeature.BandCol, baseValue = 0f, multiplier = 3f };

    [Header("Scale (uniform)")]
    public AudioDrivenSlot scale = new()
        { enabled = false, source = AudioFeature.BandRow, baseValue = 1f, multiplier = 1f };

    [Header("Rotation")]
    public AudioDrivenSlot rotationSpeed = new()
        { enabled = false, source = AudioFeature.RMS, baseValue = 0f, multiplier = 90f };
    public Vector3 rotationAxis = Vector3.up;

    // ── Spectrogram Mode ──────────────────────────────────────────────────────
    [Header("Spectrogram Mode")]
    [Tooltip("Columns = time (scrolling history), Rows = frequency (row 0 = bass, last row = treble).")]
    public bool spectrogramMode = true;

    [Tooltip("How many columns advance per second. Lower = slower scroll / longer history visible.")]
    public float scrollSpeed = 8f;

    [Tooltip("Newest data appears at the centre and travels outward to both edges.")]
    public bool outwardFromCenter = true;

    // ── Debug ─────────────────────────────────────────────────────────────────
    [Header("Debug")]
    [Tooltip("Show an on-screen overlay with live values.")]
    public bool showDebugOverlay = true;

    // Live readout (visible in Inspector during play mode)
    [HideInInspector] public float dbgBand0;
    [HideInInspector] public float dbgYBlock00;
    [HideInInspector] public float dbgColVal0;
    [HideInInspector] public int   dbgBlockCount;

    // ── Instanced rendering ───────────────────────────────────────────────────
    private Mesh       _mesh;
    private Material   _material;
    private Vector3[]  _basePositions; // XZ world positions (Y=0), length = rows*columns
    private Matrix4x4[] _matrices;    // per-block TRS, rebuilt every frame
    private Quaternion[] _rotations;  // per-block accumulated rotation (only if rotation enabled)

    // Scratch buffer for chunked DrawMeshInstanced calls (max 1023 per call)
    private static readonly Matrix4x4[] _batch = new Matrix4x4[1023];

    // ── Spectrogram history ───────────────────────────────────────────────────
    // Circular ring buffer [historySlots, rows].
    // historySlots = columns/2 when outwardFromCenter, else columns.
    // _histHead is where the NEXT sample will be written.
    private float[,] _historyBuffer;
    private int      _histHead;
    private float    _scrollAccum;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        if (blockPrefab == null)
        {
            Debug.LogError("[AudioVisualizer] blockPrefab is not assigned!", this);
            return;
        }

        // Extract mesh and material from prefab — no Instantiate needed.
        var filter = blockPrefab.GetComponent<MeshFilter>() ?? blockPrefab.GetComponentInChildren<MeshFilter>();
        _mesh      = filter?.sharedMesh;
        _material = blockPrefab.sharedMaterial;

        if (_mesh == null || _material == null)
        {
            Debug.LogError("[AudioVisualizer] blockPrefab needs a MeshFilter and MeshRenderer.", this);
            return;
        }

        int total = rows * columns;
        _basePositions = new Vector3[total];
        _matrices      = new Matrix4x4[total];
        _rotations     = new Quaternion[total];

        for (int i = 0; i < total; i++)
            _rotations[i] = Quaternion.identity;

        float offsetX = (columns - 1) * spacingX * 0.5f;
        float offsetZ = (rows    - 1) * spacingZ * 0.5f;

        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
                _basePositions[r * columns + c] = transform.position + new Vector3(
                    c * spacingX - offsetX,
                    0f,
                    r * spacingZ - offsetZ);

        // History buffer
        int histSlots  = outwardFromCenter ? Mathf.Max(1, columns / 2) : columns;
        _historyBuffer = new float[histSlots, rows];
        _histHead      = 0;
    }

    private void Update()
    {
        if (audio == null || _matrices == null) return;

        // ── Debug readouts ────────────────────────────────────────────────────
        dbgBlockCount = rows * columns;
        if (audio.bandsSmoothed != null && audio.bandsSmoothed.Length > 0)
            dbgBand0 = audio.bandsSmoothed[0];
        dbgColVal0 = GetBandValue(0, columns);

        // ── Spectrogram: advance circular history buffer ──────────────────────
        if (spectrogramMode && _historyBuffer != null)
        {
            _scrollAccum += Time.deltaTime * scrollSpeed;
            int steps = Mathf.FloorToInt(_scrollAccum);
            _scrollAccum -= steps;

            int histSlots = _historyBuffer.GetLength(0);
            for (int s = 0; s < steps; s++)
            {
                for (int r = 0; r < rows; r++)
                    _historyBuffer[_histHead, r] = GetBandValue(r, rows);
                _histHead = (_histHead + 1) % histSlots;
            }
        }

        // ── Build per-block matrices ──────────────────────────────────────────
        bool rotEnabled = rotationSpeed.enabled;
        Vector3 rotAxisN = rotationAxis.normalized;
        float dt = Time.deltaTime;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                int i = r * columns + c;

                // Resolve audio value for this block
                float colVal, rowVal, crossVal;
                if (spectrogramMode && _historyBuffer != null)
                {
                    int histSlots = _historyBuffer.GetLength(0);
                    int age;
                    if (outwardFromCenter)
                    {
                        int half = columns / 2;
                        age = (c < half) ? (half - 1 - c) : (c - half);
                    }
                    else
                    {
                        age = columns - 1 - c;
                    }
                    age = Mathf.Clamp(age, 0, histSlots - 1);
                    int idx = ((_histHead - 1 - age) % histSlots + histSlots) % histSlots;
                    float v = _historyBuffer[idx, r];
                    colVal = v; rowVal = v; crossVal = v;
                }
                else
                {
                    colVal   = GetBandValue(c, columns);
                    rowVal   = GetBandValue(r, rows);
                    crossVal = colVal * rowVal;
                }

                // Position
                Vector3 pos = _basePositions[i];
                if (yPosition.enabled)
                {
                    float y = Evaluate(yPosition, colVal, rowVal, crossVal);
                    if (r == 0 && c == 0) dbgYBlock00 = y;
                    pos.y = y;
                }

                // Scale
                float s = scale.enabled
                    ? Mathf.Max(0.001f, Evaluate(scale, colVal, rowVal, crossVal))
                    : 1f;

                // Rotation (accumulated)
                if (rotEnabled)
                {
                    float deg = Evaluate(rotationSpeed, colVal, rowVal, crossVal) * dt;
                    _rotations[i] *= Quaternion.AngleAxis(deg, rotAxisN);
                }

                _matrices[i] = Matrix4x4.TRS(pos, _rotations[i], new Vector3(xScale, 1, zScale) * s);
            }
        }

        // ── Draw in chunks of 1023 (GPU instancing limit per call) ────────────
        int total = rows * columns;
        int offset = 0;
        while (offset < total)
        {
            int count = Mathf.Min(1023, total - offset);
            System.Array.Copy(_matrices, offset, _batch, 0, count);
            Graphics.DrawMeshInstanced(_mesh, 0, _material, _batch, count);
            offset += count;
        }
    }

    // ── Debug overlay ─────────────────────────────────────────────────────────

    private void OnGUI()
    {
        if (!showDebugOverlay) return;

        int pad = 10;
        float w = 380f;
        var rect = new Rect(pad, pad, w, 300f);
        GUI.Box(rect, GUIContent.none);

        GUILayout.BeginArea(new Rect(pad + 6, pad + 4, w - 12, rect.height));

        GUILayout.Label("── AudioVisualizer Debug ──");
        GUILayout.Label($"Blocks : {dbgBlockCount}  ({rows} x {columns})  [instanced]");
        GUILayout.Space(4);

        if (audio == null)
        {
            GUILayout.Label("!! audio ref = NULL  -- assign RealTimeAudioFeatures");
        }
        else
        {
            var src = audio.source;
            bool playing = src != null && src.isPlaying;
            GUILayout.Label($"inputMode      : {audio.inputMode}");
            GUILayout.Label($"source.isPlaying : {playing}");
            GUILayout.Label($"source.mute    : {(src != null ? src.mute.ToString() : "?")}");

            string[] devs = Microphone.devices;
            GUILayout.Label(devs.Length == 0
                ? "Mic devices    : none found"
                : $"Mic devices    : {string.Join(", ", devs)}");

            GUILayout.Space(4);
            GUILayout.Label($"rms    : {audio.rms:F5}");
            GUILayout.Label($"bass   : {audio.bass:F5}");
            GUILayout.Label($"mid    : {audio.mid:F5}");
            GUILayout.Label($"treble : {audio.treble:F5}");
            GUILayout.Space(4);
            GUILayout.Label($"bands[0]       : {dbgBand0:F5}");
            GUILayout.Label($"colVal[0]      : {dbgColVal0:F5}");
            GUILayout.Label($"yPos [0,0]     : {dbgYBlock00:F4}  (mult={yPosition.multiplier})");
            GUILayout.Space(4);

            if (!playing)
                GUILayout.Label("!! AudioSource not playing");
            else if (audio.rms < 1e-4f)
                GUILayout.Label("!! rms zero — source plays but produces silence");
            else if (dbgYBlock00 < 0.01f && yPosition.enabled)
                GUILayout.Label("!! yPos tiny — raise Y Position multiplier (try 50-200)");
            else
                GUILayout.Label("OK — values look alive");
        }

        GUILayout.EndArea();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private float GetBandValue(int index, int total)
    {
        float[] b = audio.bandsSmoothed;
        if (b == null || b.Length == 0) return 0f;

        int bandIdx = total <= 1
            ? 0
            : Mathf.RoundToInt(index / (float)(total - 1) * (b.Length - 1));

        return b[Mathf.Clamp(bandIdx, 0, b.Length - 1)];
    }

    private float Evaluate(AudioDrivenSlot slot, float colVal, float rowVal, float crossVal)
    {
        float raw = slot.source switch
        {
            AudioFeature.RMS         => audio.rms,
            AudioFeature.Peak        => audio.peak,
            AudioFeature.Bass        => audio.bass,
            AudioFeature.Mid         => audio.mid,
            AudioFeature.Treble      => audio.treble,
            AudioFeature.BandCol     => colVal,
            AudioFeature.BandRow     => rowVal,
            AudioFeature.BandColXRow => crossVal,
            _                        => 0f
        };
        return slot.baseValue + raw * slot.multiplier;
    }
}

// ── Shared types ──────────────────────────────────────────────────────────────

/// <summary>Which audio feature drives an animation slot.</summary>
public enum AudioFeature
{
    RMS,
    Peak,
    Bass,
    Mid,
    Treble,
    BandCol,
    BandRow,
    BandColXRow
}

/// <summary>Output = baseValue + audioValue * multiplier.</summary>
[System.Serializable]
public class AudioDrivenSlot
{
    [Tooltip("Uncheck to disable this animation channel.")]
    public bool enabled = true;

    [Tooltip("Which audio feature drives this slot.")]
    public AudioFeature source = AudioFeature.BandCol;

    [Tooltip("Added to the output.")]
    public float baseValue = 0f;

    [Tooltip("Scales the raw audio value.")]
    [Range(0f, 200f)]
    public float multiplier = 1f;
}
