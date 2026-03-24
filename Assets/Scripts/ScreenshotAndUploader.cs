using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;

public class ScreenshotAndUploader : MonoBehaviour
{
    [System.Serializable]
    public class AnnotationMetadata
    {
        public float videoTime;
        public float bboxX;
        public float bboxY;
        public float bboxWidth;
        public float bboxHeight;
        public int videoWidth;
        public int videoHeight;
    }

    [System.Serializable]
    public class APIResponse
    {
        public string message;
        public string result;
        // Add other fields based on your actual API response structure
    }

    [Header("API")]
    public string uploadUrl = "https://server.botclub.in/generate_annotations";

    [Header("UI Elements")]
    public TMP_Text inputText; // Text to send with the image
    public TMP_Text outputText; // Display API response

    // Captures RenderTexture -> Texture2D -> draws bounding box -> encodes PNG -> posts multipart form
    public IEnumerator CaptureRenderTextureAndUpload(RenderTexture rt, AnnotationMetadata meta)
    {
        if (rt == null)
        {
            Debug.LogError("RenderTexture is null");
            UpdateOutput("Error: RenderTexture is null");
            yield break;
        }

        // Validate UI references
        if (inputText == null)
        {
            Debug.LogError("Input TextMeshPro is not assigned!");
            UpdateOutput("Error: Input text field not assigned");
            yield break;
        }

        if (outputText == null)
        {
            Debug.LogWarning("Output TextMeshPro is not assigned!");
        }

        // Show uploading status
        UpdateOutput("Uploading...");

        // read RT into Texture2D
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;

        // draw bbox (outline) onto texture
        DrawBoundingBoxOnTexture(tex, (int)meta.bboxX, (int)meta.bboxY, (int)meta.bboxWidth, (int)meta.bboxHeight);

        byte[] png = tex.EncodeToPNG();
        Object.Destroy(tex);

        // prepare multipart form
        WWWForm form = new WWWForm();
        form.AddBinaryData("image", png, "annotation.png", "image/png");

        string json = JsonUtility.ToJson(meta);
        form.AddField("metadata", json);

        // Add the text from input field
        form.AddField("text", inputText.text);

        using (UnityWebRequest www = UnityWebRequest.Post(uploadUrl, form))
        {
            www.timeout = 30; // seconds
            yield return www.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
            if (www.result != UnityWebRequest.Result.Success)
#else
            if (www.isNetworkError || www.isHttpError)
#endif
            {
                string errorMsg = "Upload failed: " + www.error;
                if (!string.IsNullOrEmpty(www.downloadHandler.text))
                {
                    errorMsg += "\nResponse: " + www.downloadHandler.text;
                }
                Debug.LogError(errorMsg);
                UpdateOutput(errorMsg);
                // TODO: add retry logic or offline queue if needed
            }
            else
            {
                Debug.Log("Upload success: " + www.downloadHandler.text);

                // Parse and display the response
                try
                {
                    APIResponse response = JsonUtility.FromJson<APIResponse>(www.downloadHandler.text);
                    string displayText = "Success!\n";
                    if (!string.IsNullOrEmpty(response.message))
                        displayText += "Message: " + response.message + "\n";
                    if (!string.IsNullOrEmpty(response.result))
                        displayText += "Result: " + response.result;

                    UpdateOutput(displayText);
                }
                catch (System.Exception e)
                {
                    // If JSON parsing fails, just show raw response
                    Debug.LogWarning("Could not parse response as JSON: " + e.Message);
                    UpdateOutput("Response:\n" + www.downloadHandler.text);
                }
            }
        }
    }

    void DrawBoundingBoxOnTexture(Texture2D t, int x, int y, int w, int h)
    {
        x = Mathf.Clamp(x, 0, t.width - 1);
        y = Mathf.Clamp(y, 0, t.height - 1);
        w = Mathf.Clamp(w, 1, t.width);
        h = Mathf.Clamp(h, 1, t.height);

        int thickness = Mathf.Max(2, (int)(Mathf.Min(t.width, t.height) * 0.003f));

        // top & bottom lines
        for (int i = x; i < x + w; i++)
        {
            for (int tline = 0; tline < thickness; tline++)
            {
                int tyTop = Mathf.Clamp(y + h - 1 - tline, 0, t.height - 1);
                int tyBot = Mathf.Clamp(y + tline, 0, t.height - 1);
                t.SetPixel(i, tyTop, Color.red);
                t.SetPixel(i, tyBot, Color.red);
            }
        }

        // left & right lines
        for (int j = y; j < y + h; j++)
        {
            for (int tline = 0; tline < thickness; tline++)
            {
                int txLeft = Mathf.Clamp(x + tline, 0, t.width - 1);
                int txRight = Mathf.Clamp(x + w - 1 - tline, 0, t.width - 1);
                t.SetPixel(txLeft, j, Color.red);
                t.SetPixel(txRight, j, Color.red);
            }
        }

        t.Apply();
    }

    // Helper method to update output text
    void UpdateOutput(string message)
    {
        if (outputText != null)
        {
            outputText.text = message;
        }
    }
}