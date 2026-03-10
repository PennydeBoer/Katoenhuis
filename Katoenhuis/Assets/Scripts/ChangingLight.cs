using UnityEngine;

public class ChangingLight : MonoBehaviour
{
    [SerializeField] private Material glowEmission;
    [SerializeField] private RealTimeAudioFeatures audioSource;
    [SerializeField] private float intensity;
    [SerializeField] private Camera cam;
    void Start()
    {
        glowEmission.SetColor("_TextureColor", Color.cyan);
    }
    private void Update()
    {
        Color glowcolor = new Color(0, audioSource.peak - 0.1f, 1);
        glowEmission.SetColor("_EmissiveColor",glowcolor* intensity);
    }
}
