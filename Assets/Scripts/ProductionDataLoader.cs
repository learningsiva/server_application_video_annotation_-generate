using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Runtime.InteropServices;

public class ProductionDataLoader : MonoBehaviour
{
    [Header("Dependencies")]
    public VideoAnnotationManager annotationManager;

    [Header("Backend Configuration")]
    public string backendApiUrl = "https://botclub.conbig.com/api/v1/get_task";

    [Header("Testing")]
    [Tooltip("Enter an ID here to test inside Unity Editor (e.g., 1)")]
    public string testId = " ";

    // Import the JS function (only works in WebGL build)
    [DllImport("__Internal")]
    private static extern string GetQueryParam(string paramId);

    // -----------------------------------------------------------
    // JSON CLASSES
    // -----------------------------------------------------------
    [System.Serializable]
    public class RootResponse
    {
        public string message;
        public TaskDetails annotation_data;
    }

    [System.Serializable]
    public class TaskDetails
    {
        public string video_url;
        public string topic;
        public string subject;
        public string description;
        public string class_name;
    }
    // -----------------------------------------------------------

    IEnumerator Start()
    {
        if (annotationManager == null)
        {
            Debug.LogError("❌ [Loader] AnnotationManager is NOT assigned in the Inspector!");
            yield break;
        }

        yield return null;
        

        string taskIdStr = GetTaskId();

        if (!string.IsNullOrEmpty(taskIdStr))
        {
            Debug.Log($"🚀 [Loader] Fetching data for Task ID: {taskIdStr}...");
            yield return StartCoroutine(FetchTaskData(taskIdStr));
        }
        else
        {
            Debug.Log("⚠️ [Loader] No Task ID found. Waiting for manual input.");
        }
    }

    private string GetTaskId()
    {
#if UNITY_EDITOR
        return testId;
#elif UNITY_WEBGL
        return GetQueryParam("id"); 
#else
        return testId; 
#endif
    }

    IEnumerator FetchTaskData(string idStr)
    {
        string url = $"{backendApiUrl}?id={idStr}";

        using (UnityWebRequest www = UnityWebRequest.Get(url))
        {
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"❌ API Error: {www.error} - URL: {url}");
                if (annotationManager.outputText != null)
                    annotationManager.outputText.text = "Error loading task data.";
            }
            else
            {
                Debug.Log($"✅ API Response: {www.downloadHandler.text}");

                // 🔥 FIX: Pass the ID string down to processing
                ProcessData(www.downloadHandler.text, idStr);
            }
        }
    }

    void ProcessData(string json, string idStr)
    {
        try
        {
            // 🔥 FIX: Parse ID string to int
            int taskId = 0;
            if (!int.TryParse(idStr, out taskId))
            {
                Debug.LogError($"❌ Could not parse Task ID '{idStr}' to integer.");
                return;
            }

            RootResponse response = JsonUtility.FromJson<RootResponse>(json);

            if (response != null && response.annotation_data != null)
            {
                TaskDetails data = response.annotation_data;

                if (annotationManager != null)
                {
                    Debug.Log($"🎥 Loading Video: {data.video_url}");

                    string safeTopic = !string.IsNullOrEmpty(data.topic) ? data.topic : "Untitled Topic";
                    string safeDesc = !string.IsNullOrEmpty(data.description) ? data.description : "No Description";
                    string safeClass = !string.IsNullOrEmpty(data.class_name) ? data.class_name : "";
                    string safeSubject = !string.IsNullOrEmpty(data.subject) ? data.subject : "";

                    // 🔥 FIX: Added 'taskId' as the first argument
                    annotationManager.SetTaskData(
                        taskId,          // 1. Task ID (Int)
                        data.video_url,  // 2. Video URL
                        safeTopic,       // 3. Topic
                        safeDesc,        // 4. Description
                        safeClass,       // 5. Class
                        safeSubject      // 6. Subject
                    );
                }
            }
            else
            {
                Debug.LogError("❌ JSON parsed, but 'annotation_data' was missing.");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"❌ JSON Parse Error: {ex.Message}");
        }
    }
}