using UnityEngine;

public class ChangingLight : MonoBehaviour
{
    [SerializeField] private Material glowEmission;
    [SerializeField] private RealTimeAudioFeatures audioSource;
    void Start()
    {
        glowEmission.SetColor("_TextureColor", Color.cyan);
    }
    private void Update()
    {
        glowEmission.SetColor("_TextureColor", new Color(audioSource.bass, audioSource.peak-0.1f, 1));
    }
}
