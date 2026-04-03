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

    // =====================================================================
    // SPEED FLASH FIX
    // After every Play(), call ApplySpeed() immediately.
    // In WebGL: jslib SetVideoPlaybackRate() runs synchronously in the same
    //           JS call stack — before Chrome paints any frame. Zero flash.
    // In Editor: 1-frame coroutine fallback (Chrome reset doesn't apply).
    // The index.html prototype patch is the third safety net.
    // =====================================================================
    private const float PLAYBACK_SPEED = 0.75f;

#if UNITY_WEBGL && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void SetVideoPlaybackRate(float rate);
#endif

    private void ApplySpeed()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        SetVideoPlaybackRate(PLAYBACK_SPEED);
#else
        StartCoroutine(ApplySpeedEditorFallback());
#endif
    }

    private IEnumerator ApplySpeedEditorFallback()
    {
        yield return null;
        if (videoPlayer.isPrepared)
            videoPlayer.playbackSpeed = PLAYBACK_SPEED;
    }

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

    // =====================================================================
    // FRAME PREVIEW STATE
    // Used by the frameReady path so we know which frame we asked for
    // and can update slider + text the moment the texture is ready.
    // =====================================================================
    private bool _waitingForFrame = false;
    private float _pendingFrameTime = 0f;

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

    // ==================================================================
    void Awake()
    {
        videoDisplayRect = videoDisplay.GetComponent<RectTransform>();

        // TAP TRIGGER — PointerClick is fine for annotation; phantom-tap
        // is handled by the isProcessing / isPlaying guard below.
        videoDisplay.raycastTarget = true;
        EventTrigger videoTrigger = videoDisplay.gameObject.GetComponent<EventTrigger>();
        if (videoTrigger == null) videoTrigger = videoDisplay.gameObject.AddComponent<EventTrigger>();
        videoTrigger.triggers.Clear();
        EventTrigger.Entry clickEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
        clickEntry.callback.AddListener((data) => { OnVideoTapped((PointerEventData)data); });
        videoTrigger.triggers.Add(clickEntry);

        // BUTTONS
        if (playButton) playButton.onClick.AddListener(OnPlayClicked);
        if (pauseButton) pauseButton.onClick.AddListener(OnPauseClicked);
        if (overlayPlayButton) overlayPlayButton.onClick.AddListener(OnPlayClicked);
        if (forwardButton) forwardButton.onClick.AddListener(() => { if (videoPlayer.isPrepared) videoPlayer.time += 2.0f; });
        if (backwardButton) backwardButton.onClick.AddListener(() => { if (videoPlayer.isPrepared) videoPlayer.time -= 2.0f; });
        if (saveAndFinishButton) saveAndFinishButton.onClick.AddListener(OnSaveAndFinishClicked);

        // SLIDER
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
                if (videoPlayer.isPrepared)
                {
                    videoPlayer.time = seekSlider.value;
                    ApplySpeed(); // re-apply after seek
                }
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

        // VIDEO PLAYER EVENTS
        videoPlayer.prepareCompleted += OnVideoPrepareCompleted;
        videoPlayer.loopPointReached += OnVideoReachedEnd;
        videoPlayer.errorReceived += OnVideoError;

        // =====================================================================
        // FRAME PREVIEW SETUP
        // sendFrameReadyEvents lets us request a single frame decode without
        // calling Play(). We enable it here so the callback is always wired.
        // We only ACT on the callback when _waitingForFrame is true.
        // =====================================================================
        videoPlayer.sendFrameReadyEvents = true;
        videoPlayer.frameReady += OnFrameReady;

        if (finalPanel) finalPanel.SetActive(true);
        if (stagedCountText) stagedCountText.text = "0 annotations";

        SetPlayPauseUI(isPlaying: false);
        UpdateOutput("Waiting for Task Data...");

#if UNITY_EDITOR
        TestDatabaseConnection();
#endif
    }

    // ==================================================================
    void Update()
    {
        if (videoPlayer.isPrepared && videoPlayer.isPlaying)
        {
            if (!isUserDraggingSlider && seekSlider)
                seekSlider.value = (float)videoPlayer.time;

            if (currentFrameText) currentFrameText.text = FormatTime(videoPlayer.time);
        }

        if (overlayPlayButton)
            overlayPlayButton.gameObject.SetActive(!videoPlayer.isPlaying && !isProcessing);
    }

    // ==================================================================
    // FRAME READY CALLBACK
    // Fires when the video decoder has pushed a new frame into the texture.
    // We use this to display the first/resume frame immediately on load,
    // without calling Play() — which would be blocked by Chrome's autoplay
    // policy before the user has tapped anything.
    // ==================================================================
    private void OnFrameReady(VideoPlayer vp, long frameIndex)
    {
        // Always keep the display texture in sync
        if (videoDisplay.texture == null)
            videoDisplay.texture = vp.texture;

        if (!_waitingForFrame) return;
        _waitingForFrame = false;

        // Frame is now in the texture — update all UI immediately
        videoDisplay.texture = vp.texture;

        if (seekSlider)
        {
            seekSlider.value = _pendingFrameTime;
        }
        if (currentFrameText)
        {
            currentFrameText.text = FormatTime(_pendingFrameTime);
        }

        SetPlayPauseUI(isPlaying: false);
        isLoadingState = false;

        string msg = _pendingFrameTime > 0f ? "[Resumed] — tap to play." : "Ready — tap to play.";
        UpdateOutput(msg);
    }

    // ==================================================================
    // SHOW FRAME WITHOUT PLAYING
    // Seeks to 'time' and requests one frame decode.
    // Works in WebGL before any user gesture because we never call Play().
    // Also updates total-length text and slider range immediately.
    // ==================================================================
    private void ShowFrameAt(float time)
    {
        _pendingFrameTime = time;
        _waitingForFrame = true;

        videoPlayer.time = time;

        // Requesting a frame decode: in WebGL, setting .time on a prepared
        // VideoPlayer with sendFrameReadyEvents=true triggers the decoder
        // to output that frame into the texture and fire frameReady.
        // No Play() call needed — Chrome autoplay policy is not triggered.
#if !UNITY_WEBGL || UNITY_EDITOR
        // In Editor the decoder needs a nudge — Play+Pause for 1 frame.
        StartCoroutine(EditorFrameNudge(time));
#endif
    }

#if !UNITY_WEBGL || UNITY_EDITOR
    private IEnumerator EditorFrameNudge(float time)
    {
        videoPlayer.playbackSpeed = PLAYBACK_SPEED;
        videoPlayer.Play();
        yield return null;
        yield return null;
        videoPlayer.Pause();
        // OnFrameReady will fire and handle the rest
    }
#endif

    // ==================================================================
    // SET TASK DATA
    // In the Unity Editor on Windows, WMF (the video backend) cannot follow
    // HTTP 302 redirects. Rails Active Storage /redirect/ URLs always return
    // a 302 to the real file on S3/GCS. WMF aborts with 0x80004004.
    // Fix: in Editor, resolve the redirect first with a HEAD request and give
    // WMF the final direct URL. WebGL/Chrome handles redirects natively.
    // ==================================================================
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
#if UNITY_EDITOR
            // Resolve any redirect before handing the URL to WMF
            StartCoroutine(ResolveAndLoad(url));
#else
            videoPlayer.url = url;
            videoPlayer.Prepare();
#endif
        }
    }

#if UNITY_EDITOR
    // Follows redirects by issuing a HEAD request. UnityWebRequest follows
    // 302s internally and exposes the final landed URL via .url.
    // WMF gets the resolved direct URL — no more 0x80004004 E_ABORT.
    private IEnumerator ResolveAndLoad(string originalUrl)
    {
        UpdateOutput("Resolving video URL...");
        using (UnityWebRequest head = UnityWebRequest.Head(originalUrl))
        {
            head.timeout = 10;
            yield return head.SendWebRequest();

            string finalUrl = originalUrl; // fallback
            if ((head.result == UnityWebRequest.Result.Success ||
                 head.result == UnityWebRequest.Result.ProtocolError) &&
                !string.IsNullOrEmpty(head.url))
            {
                finalUrl = head.url;
            }
            else
            {
                Debug.LogWarning($"[URL Resolve] HEAD failed ({head.error}), using original URL.");
            }

            Debug.Log($"[URL Resolve] Original : {originalUrl}");
            Debug.Log($"[URL Resolve] Resolved : {finalUrl}");
            UpdateOutput("Loading Video...");
            videoPlayer.url = finalUrl;
            videoPlayer.Prepare();
        }
    }
#endif

    void OnVideoPrepareCompleted(VideoPlayer vp)
    {
        Debug.Log("Video Prepared.");

        // Set texture reference early so RawImage doesn't stay black
        videoDisplay.texture = vp.texture;
        vp.playbackSpeed = PLAYBACK_SPEED;

        if (playButton) playButton.interactable = true;

        // Set slider range and total-length text RIGHT NOW,
        // before any tap — user can see the full duration immediately.
        if (seekSlider != null)
        {
            seekSlider.minValue = 0f;
            seekSlider.maxValue = (float)vp.length;
        }
        if (totalFramesText) totalFramesText.text = FormatTime(vp.length);

        UpdateOutput("Checking Progress...");

        if (currentTaskId > 0)
        {
            isLoadingState = true;
            StartCoroutine(LoadPreviousProgressAPI());
        }
        else
        {
            // No saved progress — show frame 0 immediately
            ShowFrameAt(0f);
        }
    }

    void OnVideoReachedEnd(VideoPlayer vp)
    {
        vp.Pause();
        vp.time = 0f;
        SetPlayPauseUI(isPlaying: false);
        if (seekSlider) seekSlider.value = 0f;
        UpdateOutput("Video ended. Tap to restart.");
    }

    void OnVideoError(VideoPlayer vp, string message)
    {
        Debug.LogError($"[VideoPlayer] Error: {message}");
        isProcessing = false;
        isLoadingState = false;
        SetPlayPauseUI(isPlaying: false);
        UpdateOutput("Video error — please reload the page.");
    }

    // ==================================================================
    // RE-EDIT: Jump to annotation
    // ==================================================================
    void OnReEditItem(StagedAnnotation itemToEdit)
    {
        if (videoPlayer.isPlaying) OnPauseClicked();
        currentWorkingItem = itemToEdit;
        if (currentBoundingBox) Destroy(currentBoundingBox.gameObject);
        DestroyFloatingPanel();
        StartCoroutine(JumpToAnnotationRoutine(itemToEdit));
    }

    IEnumerator JumpToAnnotationRoutine(StagedAnnotation item)
    {
        UpdateOutput("Seeking...");

#if UNITY_WEBGL && !UNITY_EDITOR
        // WebGL: seek only, use frameReady to confirm frame is visible
        ShowFrameAt(item.timestamp);
        // Wait until OnFrameReady clears the flag
        float timeout = 3f;
        while (_waitingForFrame && timeout > 0f)
        {
            timeout -= Time.unscaledDeltaTime;
            yield return null;
        }
#else
        videoPlayer.playbackSpeed = PLAYBACK_SPEED;
        videoPlayer.time = item.timestamp;
        videoPlayer.Play();
        yield return null;
        yield return null;
        videoPlayer.Pause();
        if (seekSlider) seekSlider.value = item.timestamp;
        if (currentFrameText) currentFrameText.text = FormatTime(item.timestamp);
#endif

        SetPlayPauseUI(isPlaying: false);

        Vector2 boxPos = new Vector2(
            (item.boundingBox.normalizedX - 0.5f) * videoDisplayRect.rect.width,
            (item.boundingBox.normalizedY - 0.5f) * videoDisplayRect.rect.height
        );

        currentBoundingBox = Instantiate(boundingBoxPrefab, videoDisplayRect);
        currentBoundingBox.anchorMin = new Vector2(0.5f, 0.5f);
        currentBoundingBox.anchorMax = new Vector2(0.5f, 0.5f);
        currentBoundingBox.pivot = new Vector2(0.5f, 0.5f);
        currentBoundingBox.anchoredPosition = boxPos;

        currentFloatingPanelObj = Instantiate(floatingPanelPrefab, videoDisplayRect);
        currentPanelController = currentFloatingPanelObj.GetComponent<FloatingPanelController>();
        RectTransform panelRect = currentFloatingPanelObj.GetComponent<RectTransform>();
        panelRect.anchoredPosition = boxPos + new Vector2(280, -20);

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

    // ==================================================================
    // API LOGIC
    // ==================================================================
    IEnumerator LoadPreviousProgressAPI()
    {
        string url = $"{apiLoadUrl}?task_id={currentTaskId}";
        Debug.Log($"[API] Fetching: {url}");

        using (UnityWebRequest w = UnityWebRequest.Get(url))
        {
            yield return w.SendWebRequest();

            if (w.result == UnityWebRequest.Result.Success)
            {
                string json = w.downloadHandler.text.Trim();

                if (string.IsNullOrEmpty(json) || json == "[]" || json.StartsWith("["))
                {
                    UpdateOutput("New Task (Ready)");
                    ShowFrameAt(0f);
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

                            float resumeTime = data.last_edited_frame > 0f ? data.last_edited_frame : 0f;

                            // ShowFrameAt updates slider + frame text inside OnFrameReady
                            // so the user sees the correct position immediately — no tap needed.
                            ShowFrameAt(resumeTime);
                        }
                    }
                    else
                    {
                        UpdateOutput("New Task (Ready)");
                        ShowFrameAt(0f);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[API] Parse Error: {e.Message}");
                    ShowFrameAt(0f);
                }
            }
            else
            {
                Debug.LogError($"[API] Request Failed: {w.error}");
                UpdateOutput("API Error");
                ShowFrameAt(0f);
            }
        }
        // Note: isLoadingState is cleared inside OnFrameReady
    }

    IEnumerator SaveProgressAPI()
    {
        UpdateOutput("Saving...");
        SaveRequestPayload payload = new SaveRequestPayload
        {
            task_id = currentTaskId,
            current_time = (float)videoPlayer.time,
            total_frame = (float)videoPlayer.length
        };
        payload.percentage = (payload.total_frame > 0)
            ? Mathf.Clamp(Mathf.RoundToInt((payload.current_time / payload.total_frame) * 100), 0, 100)
            : 0;

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

            if (w.result == UnityWebRequest.Result.Success)
            {
                UpdateOutput("Saved!");
                stagedList.Clear();
                RefreshFinalPanel();
            }
            else
            {
                UpdateOutput("Save Failed.");
            }
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

    // ==================================================================
    // INTERACTION LOGIC
    // ==================================================================
    public void OnVideoTapped(PointerEventData data)
    {
        // Block taps while loading the frame preview or processing annotation
        if (isProcessing || !videoPlayer.isPrepared || _waitingForFrame) return;

        if (!videoPlayer.isPlaying)
        {
            OnPlayClicked();
            return;
        }

        // Video is playing — this tap is an annotation tap
        if (currentFloatingPanelObj != null) return;

        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            videoDisplayRect, data.position, data.pressEventCamera, out Vector2 localPoint);

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
        ApplySpeed(); // Speed flash fix — must be right after Play()
        SetPlayPauseUI(isPlaying: true);
    }

    public void OnPauseClicked()
    {
        videoPlayer.Pause();
        SetPlayPauseUI(isPlaying: false);
    }

    // Central place for all play/pause button state so nothing gets out of sync
    private void SetPlayPauseUI(bool isPlaying)
    {
        if (playButton) playButton.gameObject.SetActive(!isPlaying);
        if (pauseButton) pauseButton.gameObject.SetActive(isPlaying);
        if (overlayPlayButton) overlayPlayButton.gameObject.SetActive(!isPlaying && !isProcessing);
    }

    // ==================================================================
    // HELPERS
    // ==================================================================
    public void SpawnFloatingPanel(FrameData data, Vector2 localPos)
    {
        currentFrameData = data;
        currentWorkingItem = null;
        DestroyFloatingPanel();

        currentFloatingPanelObj = Instantiate(floatingPanelPrefab, videoDisplayRect);
        currentPanelController = currentFloatingPanelObj.GetComponent<FloatingPanelController>();
        RectTransform panelRect = currentFloatingPanelObj.GetComponent<RectTransform>();
        panelRect.anchoredPosition = localPos + new Vector2(280, -20);

        if (currentPanelController)
        {
            if (currentPanelController.indicatorText) currentPanelController.indicatorText.text = "AI SUGGESTIONS";
            if (currentPanelController.closeButton) currentPanelController.closeButton.onClick.AddListener(() => CloseFloatingPanel(true));
            if (currentPanelController.backButton)
            {
                currentPanelController.backButton.gameObject.SetActive(false);
                currentPanelController.backButton.onClick.AddListener(OnBackClicked);
            }
        }

        Transform container = currentPanelController ? currentPanelController.contentContainer : currentFloatingPanelObj.transform;

        GameObject headObj = Instantiate(headingPrefab, container);
        activeHeadingUI = headObj.GetComponent<AnnotationItemUI>();
        activeHeadingUI.Initialize(!string.IsNullOrEmpty(data.headline) ? data.headline : "New Annotation", null);

        if (data.annotations != null)
            foreach (string text in data.annotations)
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
            if (child.GetComponent<AnnotationItemUI>() == activeHeadingUI ||
                child.gameObject == selectedUI.gameObject ||
                child.gameObject == activeStageButton) continue;
            child.gameObject.SetActive(false);
        }
        activeHeadingUI.SetEditMode();
        selectedUI.SetEditMode();
        activeBodyUI = selectedUI;
        if (currentPanelController && currentPanelController.indicatorText) currentPanelController.indicatorText.text = "EDIT SUGGESTION";
        if (!activeStageButton)
        {
            activeStageButton = Instantiate(stageButtonPrefab, container);
            activeStageButton.GetComponent<Button>().onClick.AddListener(OnStageClicked);
        }
        if (currentPanelController && currentPanelController.backButton) currentPanelController.backButton.gameObject.SetActive(true);
    }

    public void OnBackClicked()
    {
        Transform container = currentPanelController ? currentPanelController.contentContainer : currentFloatingPanelObj.transform;
        activeHeadingUI.SetViewMode();
        if (activeBodyUI) activeBodyUI.SetViewMode();
        activeBodyUI = null;
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
            StagedAnnotation newItem = new StagedAnnotation
            {
                timestamp = currentFrameData.timestamp,
                boundingBox = currentFrameData.boundingBox,
                content = new SimpleContent { heading = activeHeadingUI.GetText(), body = activeBodyUI.GetText() }
            };
            stagedList.Add(newItem);
        }
        else
        {
            currentWorkingItem.content.heading = activeHeadingUI.GetText();
            currentWorkingItem.content.body = activeBodyUI.GetText();
        }
        RefreshFinalPanel();
        CloseFloatingPanel(true);
    }

    public void OnDeleteClicked()
    {
        if (currentWorkingItem != null)
        {
            stagedList.Remove(currentWorkingItem);
            RefreshFinalPanel();
            CloseFloatingPanel(true);
            UpdateOutput("Deleted.");
        }
    }

    void DestroyFloatingPanel()
    {
        if (currentFloatingPanelObj) Destroy(currentFloatingPanelObj);
        activeHeadingUI = null;
        activeBodyUI = null;
        activeStageButton = null;
        activeDeleteButton = null;
        currentPanelController = null;
    }

    void CloseFloatingPanel(bool resumeVideo)
    {
        DestroyFloatingPanel();
        if (currentBoundingBox) Destroy(currentBoundingBox.gameObject);
        currentBoundingBox = null;
        isProcessing = false;
        if (resumeVideo) OnPlayClicked();
    }

    void RefreshFinalPanel()
    {
        foreach (Transform child in finalContainer) Destroy(child.gameObject);
        foreach (var item in stagedList)
        {
            GameObject obj = Instantiate(stagedItemPrefab, finalContainer);
            obj.GetComponent<StagedItemUI>().Initialize(item, OnReEditItem);
        }
        if (stagedCountText) stagedCountText.text = $"{stagedList.Count} annotations";
        UpdateOutput($"Staged: {stagedList.Count} items.");
    }

    // ==================================================================
    // API REQUEST — RenderTexture blit (WebGL-safe frame capture)
    // Screen.ReadPixels cannot read the video GL texture in WebGL.
    // Blitting to a RenderTexture first makes it readable everywhere.
    // ==================================================================
    IEnumerator ProcessApiRequest(FrameData frameData, Vector2 localPos)
    {
        yield return new WaitForEndOfFrame();

        byte[] imageBytes = null;
        Texture videoTex = videoPlayer.texture;

        if (videoTex != null)
        {
            RenderTexture rt = RenderTexture.GetTemporary(videoTex.width, videoTex.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(videoTex, rt);
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(videoTex.width, videoTex.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
            imageBytes = tex.EncodeToPNG();
            Destroy(tex);
        }
        else
        {
            // Editor fallback
            Texture2D screenshot = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            screenshot.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
            screenshot.Apply();
            imageBytes = screenshot.EncodeToPNG();
            Destroy(screenshot);
        }

        WWWForm f = new WWWForm();
        f.AddBinaryData("image", imageBytes);
        f.AddField("topic", currentTopic ?? "");
        f.AddField("description", currentDescription ?? "");
        f.AddField("class", currentClass ?? "");
        f.AddField("subject", currentSubject ?? "");

        // C# does not allow yield inside catch blocks.
        // We use a flag + message and yield AFTER the using block exits.
        bool failed = false;
        string failMessage = "";

        using (UnityWebRequest w = UnityWebRequest.Post("https://server.botclub.in/generate_annotations", f))
        {
            w.timeout = requestTimeout;
            var asyncOp = w.SendWebRequest();
            float timer = 0f;

            while (!asyncOp.isDone)
            {
                timer += Time.unscaledDeltaTime;
                if (timer > autoResumeTimeout) { w.Abort(); Debug.LogWarning("[API] Timed out."); break; }
                yield return null;
            }

            if (w.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var r = JsonUtility.FromJson<APIResponse>(w.downloadHandler.text);
                    frameData.annotations = r.annotations;
                    frameData.headline = r.headline;
                    isProcessing = false;
                    SpawnFloatingPanel(frameData, localPos);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[API] Parse error: {e.Message}");
                    failed = true;
                    failMessage = "Parse error. Resuming...";
                }
            }
            else
            {
                Debug.LogWarning($"[API] Failed: {w.error}");
                failed = true;
                failMessage = "Timeout. Resuming...";
            }
        }

        // Yield and resume AFTER the using block — no catch-clause restriction here
        if (failed)
        {
            UpdateOutput(failMessage);
            yield return new WaitForSecondsRealtime(1f);
            isProcessing = false;
            CloseFloatingPanel(true);
        }
    }

    // ==================================================================
    // DATABASE (EDITOR ONLY)
    // ==================================================================
    public void TestDatabaseConnection()
    {
#if UNITY_EDITOR
        using (MySqlConnection connection = new MySqlConnection(GetConnectionString()))
        {
            try { connection.Open(); Debug.Log("DB Connection OK."); }
            catch (Exception ex) { Debug.LogError($"DB Connection Failed: {ex.Message}"); }
        }
#endif
    }

    // ==================================================================
    // UTILITIES
    // ==================================================================
    string FormatTime(double s) => $"{Mathf.FloorToInt((float)s / 60)}:{Mathf.FloorToInt((float)s % 60):00}";
    private string GetConnectionString() => $"Server={dbHost};Port={dbPort};Database={dbName};User ID={dbUser};Password={dbPassword};";
    void UpdateOutput(string msg) { if (outputText) outputText.text = msg; }

    void SpawnBoundingBox(Vector2 pos)
    {
        if (currentBoundingBox) Destroy(currentBoundingBox.gameObject);
        currentBoundingBox = Instantiate(boundingBoxPrefab, videoDisplayRect);
        currentBoundingBox.anchorMin = currentBoundingBox.anchorMax = currentBoundingBox.pivot = new Vector2(0.5f, 0.5f);
        currentBoundingBox.anchoredPosition = pos;
    }

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