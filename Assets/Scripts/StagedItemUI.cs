using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class StagedItemUI : MonoBehaviour
{
    public TMP_Text timestampText;
    public TMP_Text headingText;
    public TMP_Text bodyText; 
    public Button editButton;

    private StagedAnnotation myData;
    private System.Action<StagedAnnotation> onEditClick;

    public void Initialize(StagedAnnotation data, System.Action<StagedAnnotation> editCallback)
    {
        myData = data;
        onEditClick = editCallback;

        if (timestampText) timestampText.text = FormatTime(myData.timestamp);
        if (headingText) headingText.text = myData.content.heading;

        // 2. Display the body (Optional)
        if (bodyText) bodyText.text = myData.content.body;

        if (editButton)
        {
            editButton.onClick.RemoveAllListeners();
            editButton.onClick.AddListener(() => onEditClick?.Invoke(myData));
        }
    }

    private string FormatTime(float s)
    {
        int minutes = Mathf.FloorToInt(s / 60F);
        int seconds = Mathf.FloorToInt(s % 60F);
        return string.Format("{0:00}:{1:00}", minutes, seconds);
    }
}