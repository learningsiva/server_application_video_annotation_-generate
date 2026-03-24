using UnityEngine;

public class WebGLMobileFix : MonoBehaviour
{
    void Start()
    {
        // Force the web canvas to use the full device pixel ratio (Sharp Text!)
#if UNITY_WEBGL && !UNITY_EDITOR
            Application.targetFrameRate = 60; // Smooth 60 FPS
#endif
    }
}