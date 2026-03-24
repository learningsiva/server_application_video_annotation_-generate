using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class FloatingPanelController : MonoBehaviour
{
    [Header("Static UI Elements")]
    public TMP_Text indicatorText;       // "AI SUGGESTIONS"
    public Button closeButton;           // "X" Button
    public Button backButton;            // 🔥 NEW: The Static Back Button
    public Transform contentContainer;   // Where Headings/Paragraphs spawn
}