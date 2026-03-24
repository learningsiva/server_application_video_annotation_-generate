using UnityEngine;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class AnnotationItemUI : MonoBehaviour, IPointerClickHandler
{
    public TMP_InputField inputField;
    public Image background;
    public GameObject penIcon;

    private System.Action<AnnotationItemUI> onSelectCallback;
    private bool isEditing = false;

    // 🔥 FIX: Changed float to int to match TMP_InputField.caretWidth type
    private int defaultCaretWidth = 2;

    private void Awake()
    {
        if (inputField != null) defaultCaretWidth = inputField.caretWidth;
    }

    public void Initialize(string text, System.Action<AnnotationItemUI> onSelect)
    {
        inputField.text = text;
        onSelectCallback = onSelect;
        SetViewMode();
    }

    public string GetText() => inputField.text;

    public void SetViewMode()
    {
        isEditing = false;

        // Keep interactable TRUE so hover colors work
        inputField.interactable = true;

        // Prevent typing
        inputField.readOnly = true;

        // Hide the blinking cursor
        inputField.caretWidth = 0;

        if (background) background.color = Color.white;
        if (penIcon) penIcon.SetActive(true);
    }

    public void SetEditMode()
    {
        isEditing = true;

        inputField.interactable = true;
        inputField.readOnly = false;

        // Restore cursor width
        inputField.caretWidth = defaultCaretWidth;

        if (background) background.color = new Color(0.9f, 1f, 0.9f);
        if (penIcon) penIcon.SetActive(false);
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (!isEditing)
        {
            onSelectCallback?.Invoke(this);

            // Manually focus after selection logic is done
            inputField.ActivateInputField();
            inputField.MoveTextEnd(false);
        }
    }
}