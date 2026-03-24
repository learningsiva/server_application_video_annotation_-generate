using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;
using TMPro;
using System;
using UnityEngine.EventSystems;

#if UNITY_EDITOR
using MySql.Data.MySqlClient;
#endif

#if !UNITY_WEBGL || UNITY_EDITOR
using NativeGalleryNamespace;
#endif

// ---------------------------------------------------------
// DATA CLASSES
// ---------------------------------------------------------
[Serializable]
public class SimpleBoundingBox
{
    public float normalizedX;
    public float normalizedY;
}

[Serializable]
public class SimpleContent
{
    public string heading;
    public string body;
}

[Serializable]
public class StagedAnnotation
{
    public float timestamp;
    public SimpleBoundingBox boundingBox;
    public SimpleContent content;
}

[Serializable]
public class AnnotationListWrapper
{
    public List<StagedAnnotation> Items;
}

public class VideoAnnotationManager : MonoBehaviour
{
    [Header("UI Elements")]
    public RawImage videoDisplay;
    public Button playButton;
    public Button pauseButton;
    public Button overlayPlayButton;
    public TMP_Text outputText;

    [Header("Floating UI Prefabs")]
    public GameObject floatingPanelPrefab;
    public GameObject headingPrefab;
    public GameObject paragraphPrefab;

    [Header("Action Buttons (Prefabs)")]
    public GameObject stageButtonPrefab;
    public GameObject deleteButtonPrefab;

    [Header("Final Panel (List)")]
    public GameObject finalPanel;
    public Transform finalContainer;
    public GameObject stagedItemPrefab;
    public Button saveAndFinishButton;
    public TMP_Text stagedCountText;

    [Header("Controls")]
    public Slider seekSlider;
    public Button forwardButton;
    public Button backwardButton;
    public TMP_Text currentFrameText;
    public TMP_Text totalFramesText;

    [Header("Settings")]
    public float autoResumeTimeout = 30.0f;
    public int requestTimeout = 120;
    public RectTransform boundingBoxPrefab;
    public VideoPlayer videoPlayer;

    [Header("MySQL Settings (Editor Only)")]
    public string dbHost = "62.72.59.109";
    public string dbPort = "3306";
    public string dbName = "bot_club_prod_crm";
    public string dbUser = "conbig_remote_user";
    public string dbPassword = "M1b2v3L4P5Q6";
    public bool saveToDatabase = true;

    // API ENDPOINTS
    private string apiLoadUrl = "https://botclub.conbig.com/api/v1/get_annotation_progress";
    private string apiSaveUrl = "https://botclub.conbig.com/api/v1/save_annotation_progress";

    // INTERNAL STATE
    private RectTransform videoDisplayRect;
    private RectTransform currentBoundingBox;
    private GameObject currentFloatingPanelObj;
    private FloatingPanelController currentPanelController;
    private AnnotationItemUI activeHeadingUI;
    private AnnotationItemUI activeBodyUI;
    private GameObject activeStageButton;
    private GameObject activeDeleteButton;

    private bool isProcessing = false;
    private bool wasPlayingBeforePause = false;
    private bool isUserDraggingSlider = false;
    private bool isLoadingState = false;

    // DATA
    private long currentTaskId = -1;
    private string currentTopic;
    private string currentDescription;
    private string currentClass;
    private string currentSubject;

    private List<StagedAnnotation> stagedList = new List<StagedAnnotation>();
    private StagedAnnotation currentWorkingItem;
    private FrameData currentFrameData;

    [Serializable] public class APIResponse { public string[] annotations; public string headline; }

    [Serializable]
    public class FrameData
    {
        public float timestamp;
        public SimpleBoundingBox boundingBox;
        public string[] annotations;
        public string headline;
    }

    [Serializable]
    public class SaveRequestPayload
    {
        public long task_id;
        public int percentage;
        public float current_time;
        public float total_frame;
        public string annotations_json;
    }

    [Serializable] public class OuterApiResponse { public string message; public string response; }
    [Serializable] public class InnerDataResponse { public string completed_percentage; public float last_edited_frame; public float total_frame; public string annotations_json; }

    void Awake()
    {
        videoDisplayRect = videoDisplay.GetComponent<RectTransform>();

        // 1. SETUP TAP TRIGGER
        videoDisplay.raycastTarget = true;
        EventTrigger videoTrigger = videoDisplay.gameObject.GetComponent<EventTrigger>();
        if (videoTrigger == null) videoTrigger = videoDisplay.gameObject.AddComponent<EventTrigger>();
        videoTrigger.triggers.Clear();
        EventTrigger.Entry clickEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
        clickEntry.callback.AddListener((data) => { OnVideoTapped((PointerEventData)data); });
        videoTrigger.triggers.Add(clickEntry);

        // 2. SETUP BUTTONS
        if (playButton) playButton.onClick.AddListener(OnPlayClicked);
        if (pauseButton) pauseButton.onClick.AddListener(OnPauseClicked);
        if (overlayPlayButton) overlayPlayButton.onClick.AddListener(OnPlayClicked);

        if (forwardButton) forwardButton.onClick.AddListener(() => { if (videoPlayer.isPrepared) videoPlayer.time += 2.0f; });
        if (backwardButton) backwardButton.onClick.AddListener(() => { if (videoPlayer.isPrepared) videoPlayer.time -= 2.0f; });
        if (saveAndFinishButton) saveAndFinishButton.onClick.AddListener(OnSaveAndFinishClicked);

        // 3. SETUP SLIDER
        if (seekSlider != null)
        {
            EventTrigger sliderTrigger = seekSlider.gameObject.GetComponent<EventTrigger>();
            if (sliderTrigger == null) sliderTrigger = seekSlider.gameObject.AddComponent<EventTrigger>();

            EventTrigger.Entry downEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            downEntry.callback.AddListener((data) => { isUserDraggingSlider = true; });
            sliderTrigger.triggers.Add(downEntry);

            EventTrigger.Entry upEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp };
            upEntry.callback.AddListener((data) => {
                isUserDraggingSlider = false;
                if (videoPlayer.isPrepared) videoPlayer.time = seekSlider.value;
            });
            sliderTrigger.triggers.Add(upEntry);

            seekSlider.onValueChanged.AddListener((val) => {
                if (isUserDraggingSlider && videoPlayer.isPrepared)
                {
                    videoPlayer.time = val;
                    if (currentFrameText) currentFrameText.text = FormatTime(val);
                }
            });
        }

        videoPlayer.prepareCompleted += OnVideoPrepareCompleted;

        if (finalPanel) finalPanel.SetActive(true);
        if (stagedCountText) stagedCountText.text = "0 annotations";

        UpdateOutput("Waiting for Task Data...");

#if UNITY_EDITOR
        TestDatabaseConnection();
#endif
    }

    void Update()
    {
        if (videoPlayer.isPrepared)
        {
            if (!isUserDraggingSlider && !isLoadingState && seekSlider)
            {
                seekSlider.value = (float)videoPlayer.time;
            }
            if (currentFrameText) currentFrameText.text = FormatTime(videoPlayer.time);

            if (overlayPlayButton)
                overlayPlayButton.gameObject.SetActive(!videoPlayer.isPlaying && !isProcessing);
        }
    }

    public void SetTaskData(int taskId, string url, string topic, string desc, string cls, string subj)
    {
        currentTaskId = taskId;
        currentTopic = topic;
        currentDescription = desc;
        currentClass = cls;
        currentSubject = subj;

        if (!string.IsNullOrEmpty(url))
        {
            UpdateOutput("Loading Video...");
            videoPlayer.url = url;
            videoPlayer.Prepare();
        }
    }

    void OnVideoPrepareCompleted(VideoPlayer vp)
    {
        Debug.Log("✅ Video Prepared! Starting Progress Check...");
        videoDisplay.texture = vp.texture;
        videoPlayer.playbackSpeed = 0.75f;

        if (playButton) playButton.interactable = true;

        if (seekSlider != null)
        {
            seekSlider.minValue = 0;
            seekSlider.maxValue = (float)vp.length;
        }
        if (totalFramesText) totalFramesText.text = FormatTime(vp.length);

        UpdateOutput("Checking Progress...");

        if (currentTaskId > 0)
        {
            isLoadingState = true;
#if UNITY_EDITOR
            StartCoroutine(LoadPreviousProgressAPI());
#else
            StartCoroutine(LoadPreviousProgressAPI());
#endif
        }
        else
        {
            StartCoroutine(ForceRenderFrame(0f));
        }
    }

    IEnumerator ForceRenderFrame(float time)
    {
        videoPlayer.time = time;
        videoPlayer.Play();
        yield return null;
        yield return null;
        videoPlayer.Pause();

        if (seekSlider) seekSlider.value = time;

        if (playButton) playButton.gameObject.SetActive(true);
        if (pauseButton) pauseButton.gameObject.SetActive(false);
        if (overlayPlayButton) overlayPlayButton.gameObject.SetActive(true);

        UpdateOutput("Ready...");
    }

    // =========================================================
    // 🔥 NEW FUNCTION: SYNCED JUMP
    // =========================================================
    void OnReEditItem(StagedAnnotation itemToEdit)
    {
        // 1. Reset State
        if (videoPlayer.isPlaying) OnPauseClicked();
        currentWorkingItem = itemToEdit;

        // 2. Clear old UI immediately so user doesn't see "ghost" boxes
        if (currentBoundingBox) Destroy(currentBoundingBox.gameObject);
        DestroyFloatingPanel();

        // 3. Start the Sequence: Seek -> Wait -> Spawn
        StartCoroutine(JumpToAnnotationRoutine(itemToEdit));
    }

    IEnumerator JumpToAnnotationRoutine(StagedAnnotation item)
    {
        UpdateOutput("Seeking..."); // Optional feedback

        // --- STEP 1: SEEK VIDEO ---
        videoPlayer.time = item.timestamp;
        videoPlayer.Play();
        yield return null;
        yield return null; // Wait 2 frames for render
        videoPlayer.Pause();

        // Sync UI
        if (seekSlider) seekSlider.value = item.timestamp;
        if (playButton) playButton.gameObject.SetActive(true);
        if (pauseButton) pauseButton.gameObject.SetActive(false);
        if (overlayPlayButton) overlayPlayButton.gameObject.SetActive(true);

        // --- STEP 2: SPAWN UI (Now we are 100% sure we are on the right frame) ---

        // Calculate Position
        Vector2 boxPos = new Vector2(
            (item.boundingBox.normalizedX - 0.5f) * videoDisplayRect.rect.width,
            (item.boundingBox.normalizedY - 0.5f) * videoDisplayRect.rect.height
        );

        // Spawn Bounding Box
        currentBoundingBox = Instantiate(boundingBoxPrefab, videoDisplayRect);
        currentBoundingBox.anchorMin = new Vector2(0.5f, 0.5f);
        currentBoundingBox.anchorMax = new Vector2(0.5f, 0.5f);
        currentBoundingBox.pivot = new Vector2(0.5f, 0.5f);
        currentBoundingBox.anchoredPosition = boxPos;

        // Spawn Panel
        currentFloatingPanelObj = Instantiate(floatingPanelPrefab, videoDisplayRect);
        currentPanelController = currentFloatingPanelObj.GetComponent<FloatingPanelController>();
        RectTransform panelRect = currentFloatingPanelObj.GetComponent<RectTransform>();
        panelRect.anchoredPosition = boxPos + new Vector2(280, -20);

        // Populate Panel Data
        Transform container = currentPanelController ? currentPanelController.contentContainer : currentFloatingPanelObj.transform;
        if (currentPanelController)
        {
            if (currentPanelController.indicatorText) currentPanelController.indicatorText.text = "EDITING SAVED";
            if (currentPanelController.closeButton) currentPanelController.closeButton.onClick.AddListener(() => CloseFloatingPanel(true));
            if (currentPanelController.backButton) currentPanelController.backButton.gameObject.SetActive(false);
        }

        GameObject headObj = Instantiate(headingPrefab, container);
        activeHeadingUI = headObj.GetComponent<AnnotationItemUI>();
        activeHeadingUI.Initialize(item.content.heading, null);
        activeHeadingUI.SetEditMode();

        GameObject paraObj = Instantiate(paragraphPrefab, container);
        activeBodyUI = paraObj.GetComponent<AnnotationItemUI>();
        activeBodyUI.Initialize(item.content.body, null);
        activeBodyUI.SetEditMode();

        activeStageButton = Instantiate(stageButtonPrefab, container);
        activeStageButton.GetComponent<Button>().onClick.AddListener(OnStageClicked);

        if (deleteButtonPrefab)
        {
            activeDeleteButton = Instantiate(deleteButtonPrefab, container);
            activeDeleteButton.GetComponent<Button>().onClick.AddListener(OnDeleteClicked);
        }

        UpdateOutput("Ready...");
    }

    // =========================================================
    // API LOGIC
    // =========================================================
    IEnumerator LoadPreviousProgressAPI()
    {
        string url = $"{apiLoadUrl}?task_id={currentTaskId}";
        Debug.Log($"[API] Fetching progress from: {url}");

        using (UnityWebRequest w = UnityWebRequest.Get(url))
        {
            yield return w.SendWebRequest();

            if (w.result == UnityWebRequest.Result.Success)
            {
                string json = w.downloadHandler.text.Trim();

                if (string.IsNullOrEmpty(json) || json == "[]" || json.StartsWith("["))
                {
                    UpdateOutput("New Task (Ready)");
                    StartCoroutine(ForceRenderFrame(0f));
                    isLoadingState = false;
                    yield break;
                }

                try
                {
                    OuterApiResponse outer = JsonUtility.FromJson<OuterApiResponse>(json);
                    if (outer != null && !string.IsNullOrEmpty(outer.response) && outer.response != "null")
                    {
                        InnerDataResponse data = JsonUtility.FromJson<InnerDataResponse>(outer.response);
                        if (data != null)
                        {
                            if (!string.IsNullOrEmpty(data.annotations_json) && data.annotations_json != "null")
                            {
                                string cleanJson = data.annotations_json;
                                if (cleanJson.StartsWith("\"") && cleanJson.EndsWith("\""))
                                {
                                    cleanJson = cleanJson.Substring(1, cleanJson.Length - 2);
                                    cleanJson = cleanJson.Replace("\\\"", "\"");
                                }

                                Debug.Log($"[API] Cleaned JSON: {cleanJson}");

                                try
                                {
                                    AnnotationListWrapper wrapper = JsonUtility.FromJson<AnnotationListWrapper>(cleanJson);
                                    if (wrapper != null && wrapper.Items != null)
                                    {
                                        stagedList = wrapper.Items;
                                        RefreshFinalPanel();
                                    }
                                }
                                catch (Exception e) { Debug.LogError($"[API] JSON Error: {e.Message}"); }
                            }

                            // Resume from last edit or Frame 0
                            float resumeTime = data.last_edited_frame > 0 ? data.last_edited_frame : 0f;
                            StartCoroutine(ForceRenderFrame(resumeTime));
                            UpdateOutput("[Resumed]");
                        }
                    }
                    else
                    {
                        UpdateOutput("New Task (Ready)");
                        StartCoroutine(ForceRenderFrame(0f));
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[API] Parse Error: {e.Message}");
                    UpdateOutput("Ready (New Session)");
                    StartCoroutine(ForceRenderFrame(0f));
                }
            }
            else
            {
                Debug.LogError($"[API] Request Failed: {w.error}");
                UpdateOutput("API Error");
                StartCoroutine(ForceRenderFrame(0f));
            }
        }
        isLoadingState = false;
    }

    IEnumerator SaveProgressAPI()
    {
        UpdateOutput(" Saving...");
        SaveRequestPayload payload = new SaveRequestPayload();
        payload.task_id = currentTaskId;
        payload.current_time = (float)videoPlayer.time;
        payload.total_frame = (float)videoPlayer.length;
        payload.percentage = (payload.total_frame > 0) ? Mathf.Clamp(Mathf.RoundToInt((payload.current_time / payload.total_frame) * 100), 0, 100) : 0;

        AnnotationListWrapper wrapper = new AnnotationListWrapper { Items = stagedList };
        payload.annotations_json = JsonUtility.ToJson(wrapper);

        string jsonToSend = JsonUtility.ToJson(payload);

        using (UnityWebRequest w = new UnityWebRequest(apiSaveUrl, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonToSend);
            w.uploadHandler = new UploadHandlerRaw(bodyRaw);
            w.downloadHandler = new DownloadHandlerBuffer();
            w.SetRequestHeader("Content-Type", "application/json");

            yield return w.SendWebRequest();

            if (w.result == UnityWebRequest.Result.Success) { UpdateOutput($" Saved!"); stagedList.Clear(); RefreshFinalPanel(); }
            else { UpdateOutput($"❌ Save Failed"); }
        }
    }

#if UNITY_EDITOR
    IEnumerator LoadPreviousProgressDB() { isLoadingState = false; yield return null; }
    IEnumerator SaveAllToMySQLDB() { yield return null; }
#endif

    public void OnSaveAndFinishClicked()
    {
#if UNITY_EDITOR
        StartCoroutine(SaveAllToMySQLDB());
#else
            StartCoroutine(SaveProgressAPI()); 
#endif
    }

    // =========================================================
    // INTERACTION LOGIC
    // =========================================================
    public void OnVideoTapped(PointerEventData data)
    {
        if (isProcessing || !videoPlayer.isPrepared) return;

        if (!videoPlayer.isPlaying)
        {
            OnPlayClicked();
            return;
        }

        RectTransformUtility.ScreenPointToLocalPointInRectangle(videoDisplayRect, data.position, data.pressEventCamera, out Vector2 localPoint);
        if (currentFloatingPanelObj != null) return;

        wasPlayingBeforePause = true;
        OnPauseClicked();

        isProcessing = true;
        UpdateOutput("Annotations Generating...");
        SpawnBoundingBox(localPoint);
        FrameData frameData = CaptureFrameData(localPoint);
        StartCoroutine(ProcessApiRequest(frameData, localPoint)); 
    }

    public void OnPlayClicked()
    {
        videoPlayer.Play();
        if (playButton) playButton.gameObject.SetActive(false);
        if (pauseButton) pauseButton.gameObject.SetActive(true);
        if (overlayPlayButton) overlayPlayButton.gameObject.SetActive(false);
    }

    public void OnPauseClicked()
    {
        videoPlayer.Pause();
        if (playButton) playButton.gameObject.SetActive(true);
        if (pauseButton) pauseButton.gameObject.SetActive(false);
        if (overlayPlayButton) overlayPlayButton.gameObject.SetActive(true);
    }

    // =========================================================
    // HELPERS
    // =========================================================
    public void SpawnFloatingPanel(FrameData data, Vector2 localPos)
    {
        currentFrameData = data; currentWorkingItem = null; DestroyFloatingPanel();
        currentFloatingPanelObj = Instantiate(floatingPanelPrefab, videoDisplayRect);
        currentPanelController = currentFloatingPanelObj.GetComponent<FloatingPanelController>();
        RectTransform panelRect = currentFloatingPanelObj.GetComponent<RectTransform>();
        panelRect.anchoredPosition = localPos + new Vector2(280, -20);

        if (currentPanelController)
        {
            if (currentPanelController.indicatorText) currentPanelController.indicatorText.text = "AI SUGGESTIONS";
            if (currentPanelController.closeButton) currentPanelController.closeButton.onClick.AddListener(() => CloseFloatingPanel(true));
            if (currentPanelController.backButton) { currentPanelController.backButton.gameObject.SetActive(false); currentPanelController.backButton.onClick.AddListener(OnBackClicked); }
        }
        Transform container = currentPanelController ? currentPanelController.contentContainer : currentFloatingPanelObj.transform;
        GameObject headObj = Instantiate(headingPrefab, container);
        activeHeadingUI = headObj.GetComponent<AnnotationItemUI>();
        activeHeadingUI.Initialize(!string.IsNullOrEmpty(data.headline) ? data.headline : "New Annotation", null);
        if (data.annotations != null) foreach (string text in data.annotations)
            {
                GameObject paraObj = Instantiate(paragraphPrefab, container);
                paraObj.GetComponent<AnnotationItemUI>().Initialize(text, OnParagraphSelected);
            }
    }

    public void OnParagraphSelected(AnnotationItemUI selectedUI)
    {
        if (activeBodyUI == selectedUI) return;
        Transform container = currentPanelController ? currentPanelController.contentContainer : currentFloatingPanelObj.transform;
        foreach (Transform child in container)
        {
            if (child.GetComponent<AnnotationItemUI>() == activeHeadingUI || child.gameObject == selectedUI.gameObject || child.gameObject == activeStageButton) continue;
            child.gameObject.SetActive(false);
        }
        activeHeadingUI.SetEditMode(); selectedUI.SetEditMode(); activeBodyUI = selectedUI;
        if (currentPanelController && currentPanelController.indicatorText) currentPanelController.indicatorText.text = "EDIT SUGGESTION";
        if (!activeStageButton) { activeStageButton = Instantiate(stageButtonPrefab, container); activeStageButton.GetComponent<Button>().onClick.AddListener(OnStageClicked); }
        if (currentPanelController && currentPanelController.backButton) currentPanelController.backButton.gameObject.SetActive(true);
    }

    public void OnBackClicked()
    {
        Transform container = currentPanelController ? currentPanelController.contentContainer : currentFloatingPanelObj.transform;
        activeHeadingUI.SetViewMode(); if (activeBodyUI) activeBodyUI.SetViewMode(); activeBodyUI = null;
        foreach (Transform child in container) child.gameObject.SetActive(true);
        if (activeStageButton) { Destroy(activeStageButton); activeStageButton = null; }
        if (currentPanelController && currentPanelController.backButton) currentPanelController.backButton.gameObject.SetActive(false);
        if (currentPanelController && currentPanelController.indicatorText) currentPanelController.indicatorText.text = "AI SUGGESTIONS";
    }

    public void OnStageClicked()
    {
        if (!activeHeadingUI || !activeBodyUI) return;
        if (currentWorkingItem == null)
        {
            StagedAnnotation newItem = new StagedAnnotation { timestamp = currentFrameData.timestamp, boundingBox = currentFrameData.boundingBox, content = new SimpleContent { heading = activeHeadingUI.GetText(), body = activeBodyUI.GetText() } };
            stagedList.Add(newItem);
        }
        else { currentWorkingItem.content.heading = activeHeadingUI.GetText(); currentWorkingItem.content.body = activeBodyUI.GetText(); }
        RefreshFinalPanel(); CloseFloatingPanel(true);
    }

    public void OnDeleteClicked() { if (currentWorkingItem != null) { stagedList.Remove(currentWorkingItem); RefreshFinalPanel(); CloseFloatingPanel(true); UpdateOutput("Deleted."); } }
    void DestroyFloatingPanel() { if (currentFloatingPanelObj) Destroy(currentFloatingPanelObj); activeHeadingUI = null; activeBodyUI = null; activeStageButton = null; activeDeleteButton = null; currentPanelController = null; }
    void CloseFloatingPanel(bool resumeVideo) { DestroyFloatingPanel(); if (currentBoundingBox) Destroy(currentBoundingBox.gameObject); currentBoundingBox = null; if (resumeVideo) OnPlayClicked(); }

    void RefreshFinalPanel()
    {
        foreach (Transform child in finalContainer) Destroy(child.gameObject);
        foreach (var item in stagedList) { GameObject obj = Instantiate(stagedItemPrefab, finalContainer); obj.GetComponent<StagedItemUI>().Initialize(item, OnReEditItem); }
        if (stagedCountText) stagedCountText.text = $"{stagedList.Count} annotations"; UpdateOutput($"Staged: {stagedList.Count} items.");
    }

    IEnumerator ProcessApiRequest(FrameData frameData, Vector2 localPos)
    {
        yield return new WaitForEndOfFrame();
        Texture2D screenshot = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        screenshot.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0); screenshot.Apply(); byte[] imageBytes = screenshot.EncodeToPNG(); Destroy(screenshot);
        WWWForm f = new WWWForm(); f.AddBinaryData("image", imageBytes); f.AddField("topic", currentTopic); f.AddField("description", currentDescription); f.AddField("class", currentClass); f.AddField("subject", currentSubject);
        using (UnityWebRequest w = UnityWebRequest.Post("https://server.botclub.in/generate_annotations", f))
        {
            w.timeout = requestTimeout; var asyncOp = w.SendWebRequest(); float timer = 0;
            while (!asyncOp.isDone) { timer += Time.deltaTime; if (timer > autoResumeTimeout) break; yield return null; }
            if (w.result == UnityWebRequest.Result.Success) { var r = JsonUtility.FromJson<APIResponse>(w.downloadHandler.text); frameData.annotations = r.annotations; frameData.headline = r.headline; SpawnFloatingPanel(frameData, localPos); isProcessing = false; }
            else { UpdateOutput("Timeout. Resuming..."); yield return new WaitForSeconds(1f); isProcessing = false; CloseFloatingPanel(true); }
        }
    }

    public void TestDatabaseConnection()
    {
#if UNITY_EDITOR
        using (MySqlConnection connection = new MySqlConnection(GetConnectionString())) { try { connection.Open(); Debug.Log("✅ Test Connection Successful!"); } catch (Exception ex) { Debug.LogError($"Database Connection Failed: {ex.Message}"); } }
#endif
    }

    string FormatTime(double s) => $"{Mathf.FloorToInt((float)s / 60)}:{Mathf.FloorToInt((float)s % 60):00}";
    private string GetConnectionString() => $"Server={dbHost};Port={dbPort};Database={dbName};User ID={dbUser};Password={dbPassword};";
    void UpdateOutput(string msg) { if (outputText) outputText.text = msg; }
    void SpawnBoundingBox(Vector2 pos) { if (currentBoundingBox) Destroy(currentBoundingBox.gameObject); currentBoundingBox = Instantiate(boundingBoxPrefab, videoDisplayRect); currentBoundingBox.anchorMin = currentBoundingBox.anchorMax = currentBoundingBox.pivot = new Vector2(0.5f, 0.5f); currentBoundingBox.anchoredPosition = pos; }

    FrameData CaptureFrameData(Vector2 localPoint)
    {
        Rect rect = videoDisplayRect.rect;
        float nx = Mathf.Clamp01((localPoint.x + rect.width / 2f) / rect.width);
        float ny = Mathf.Clamp01((localPoint.y + rect.height / 2f) / rect.height);
        return new FrameData
        {
            timestamp = (float)videoPlayer.time,
            boundingBox = new SimpleBoundingBox { normalizedX = nx, normalizedY = ny }
        };
    }
}