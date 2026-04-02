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
    public Button overlayPlayButton; // Visual only — no click handling
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
    // FIX 1 — SPEED RACER
    // Chrome resets video.playbackRate to 1.0 every time Play() is called.
    // We do NOT set speed before or during Play().
    // We wait 5 frames AFTER Play() then set it — by that time Chrome has
    // finished its internal reset and our value sticks.
    // =====================================================================
    private const float PLAYBACK_SPEED = 0.75f;

    // =====================================================================
    // FIX 2 — UI GHOST
    // Chrome takes several frames to confirm isPlaying=true after Play().
    // We track our OWN intended play state that flips instantly on the same
    // frame as the user action — never waiting for Chrome's reply.
    // =====================================================================
    private bool _intendedPlaying = false;

    // =====================================================================
    // FIX 4 — PHANTOM TAP
    // Chrome sends a synthetic second click ~50ms after every real tap.
    // Unity sees Click#1 → starts video. Click#2 → wrongly triggers annotation.
    //
    // We use a STATE MACHINE. Each state has exactly ONE allowed action.
    // Even if 10 duplicate events arrive, they all see the same state and
    // do the same (correct) thing — or are explicitly blocked (Annotating).
    //
    // WaitingFirstTap → tap → starts video → Playing
    // Playing         → tap → pauses + screenshot → Annotating
    // Annotating      → tap → BLOCKED (waiting for server response)
    // Paused          → tap → resumes video → Playing
    //
    // Additionally: minimum 0.5s gap between any two accepted taps.
    // =====================================================================
    private enum VideoState { WaitingFirstTap, Playing, Annotating, Paused }
    private VideoState _state = VideoState.WaitingFirstTap;
    private float _lastTapTime = -999f;
    private const float MIN_TAP_INTERVAL = 0.5f;

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

    // ==================================================================
    void Awake()
    {
        videoDisplayRect = videoDisplay.GetComponent<RectTransform>();

        // -----------------------------------------------------------------
        // FIX 5 — GIANT HITBOX
        // The overlayPlayButton was a large transparent button covering the
        // entire screen. Any click anywhere triggered OnPlayClicked().
        // Fix: Disable raycastTarget on EVERY graphic inside it so it is
        // purely visual and catches zero clicks. Clicks fall through to the
        // videoDisplay RawImage directly beneath it.
        // Also: we do NOT wire overlayPlayButton.onClick to anything.
        // -----------------------------------------------------------------
        if (overlayPlayButton != null)
        {
            foreach (var graphic in overlayPlayButton.GetComponentsInChildren<Graphic>(true))
                graphic.raycastTarget = false;
        }

        // -----------------------------------------------------------------
        // FIX 4 — PHANTOM TAP (C# side)
        // Use PointerDown NOT PointerClick.
        // PointerClick fires for BOTH the real press AND Chrome's synthetic
        // follow-up click. PointerDown fires exactly once per physical press.
        // -----------------------------------------------------------------
        videoDisplay.raycastTarget = true;
        EventTrigger videoTrigger = videoDisplay.gameObject.GetComponent<EventTrigger>();
        if (videoTrigger == null) videoTrigger = videoDisplay.gameObject.AddComponent<EventTrigger>();
        videoTrigger.triggers.Clear();

        EventTrigger.Entry tapEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
        tapEntry.callback.AddListener((data) => { OnVideoTapped((PointerEventData)data); });
        videoTrigger.triggers.Add(tapEntry);

        // Control buttons (play/pause in the UI bar — NOT the overlay)
        if (playButton) playButton.onClick.AddListener(OnPlayClicked);
        if (pauseButton) pauseButton.onClick.AddListener(OnPauseClicked);
        if (forwardButton) forwardButton.onClick.AddListener(() => { if (videoPlayer.isPrepared) videoPlayer.time += 2.0f; });
        if (backwardButton) backwardButton.onClick.AddListener(() => { if (videoPlayer.isPrepared) videoPlayer.time -= 2.0f; });
        if (saveAndFinishButton) saveAndFinishButton.onClick.AddListener(OnSaveAndFinishClicked);

        // Seek slider
        if (seekSlider != null)
        {
            EventTrigger sliderTrigger = seekSlider.gameObject.GetComponent<EventTrigger>();
            if (sliderTrigger == null) sliderTrigger = seekSlider.gameObject.AddComponent<EventTrigger>();

            EventTrigger.Entry downEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            downEntry.callback.AddListener((data) => { isUserDraggingSlider = true; });
            sliderTrigger.triggers.Add(downEntry);

            EventTrigger.Entry upEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp };
            upEntry.callback.AddListener((data) =>
            {
                isUserDraggingSlider = false;
                if (videoPlayer.isPrepared)
                {
                    videoPlayer.time = seekSlider.value;
                    StartCoroutine(ApplySpeedAfterDelay()); // FIX 1
                }
            });
            sliderTrigger.triggers.Add(upEntry);

            seekSlider.onValueChanged.AddListener((val) =>
            {
                if (isUserDraggingSlider && videoPlayer.isPrepared)
                {
                    videoPlayer.time = val;
                    if (currentFrameText) currentFrameText.text = FormatTime(val);
                }
            });
        }

        // FIX 3 — MEMORY WALL: errorReceived catches Chrome killing the
        // video process when WebGL memory is exceeded (high DPR + 4K video).
        videoPlayer.loopPointReached += OnVideoReachedEnd;
        videoPlayer.errorReceived += OnVideoError;
        videoPlayer.prepareCompleted += OnVideoPrepareCompleted;

        if (finalPanel) finalPanel.SetActive(true);
        if (stagedCountText) stagedCountText.text = "0 annotations";

        _state = VideoState.WaitingFirstTap;
        SyncPlayPauseButtons();
        UpdateOutput("Waiting for Task Data...");

#if UNITY_EDITOR
        TestDatabaseConnection();
#endif
    }

    // ==================================================================
    void Update()
    {
        if (videoPlayer.isPrepared)
        {
            if (!isUserDraggingSlider && !isLoadingState && seekSlider)
                seekSlider.value = (float)videoPlayer.time;

            if (currentFrameText) currentFrameText.text = FormatTime(videoPlayer.time);
        }
        SyncPlayPauseButtons();
    }

    // -----------------------------------------------------------------
    // FIX 2 — UI GHOST
    // Buttons are driven by _intendedPlaying (our own flag) — not by
    // videoPlayer.isPlaying which Chrome takes several frames to update.
    // Our flag flips on the exact frame the user clicks — always correct.
    // -----------------------------------------------------------------
    private void SyncPlayPauseButtons()
    {
        if (playButton) playButton.gameObject.SetActive(!_intendedPlaying);
        if (pauseButton) pauseButton.gameObject.SetActive(_intendedPlaying);
        // overlayPlayButton: visible when paused, purely visual (no raycast)
        if (overlayPlayButton) overlayPlayButton.gameObject.SetActive(!_intendedPlaying && !isProcessing);
    }

    // -----------------------------------------------------------------
    // FIX 1 — SPEED RACER
    // Wait 5 frames after Play() before setting speed. By then Chrome has
    // finished its internal reset and our 0.75x value will stick.
    // -----------------------------------------------------------------
    private IEnumerator ApplySpeedAfterDelay()
    {
        for (int i = 0; i < 5; i++) yield return null;
        if (videoPlayer.isPrepared)
            videoPlayer.playbackSpeed = PLAYBACK_SPEED;
    }

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
            videoPlayer.url = url;
            videoPlayer.Prepare();
        }
    }

    void OnVideoPrepareCompleted(VideoPlayer vp)
    {
        Debug.Log("✅ Video Prepared!");
        videoDisplay.texture = vp.texture;
        vp.playbackSpeed = PLAYBACK_SPEED;

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
            StartCoroutine(LoadPreviousProgressAPI());
        }
        else
        {
            StartCoroutine(ForceRenderFrame(0f));
        }
    }

    // FIX 3 — MEMORY WALL: video reached end cleanly
    void OnVideoReachedEnd(VideoPlayer vp)
    {
        Debug.Log("[Video] Reached end.");
        vp.Pause();
        vp.time = 0;
        _intendedPlaying = false;
        _state = VideoState.WaitingFirstTap;
        if (seekSlider) seekSlider.value = 0;
        UpdateOutput("Video ended. Tap video to restart.");
    }

    // FIX 3 — MEMORY WALL: Chrome killed the video (memory overload / error)
    void OnVideoError(VideoPlayer vp, string message)
    {
        Debug.LogError($"[VideoPlayer] Chrome error: {message}");
        _intendedPlaying = false;
        isProcessing = false;
        isLoadingState = false;
        _state = VideoState.WaitingFirstTap;
        UpdateOutput("Video error — please reload the page.");
    }

    // In WebGL, Play() called from a coroutine (not a user gesture) is
    // blocked by Chrome's autoplay policy. We only seek — no Play+Pause.
    IEnumerator ForceRenderFrame(float time)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        videoPlayer.time = time;
        yield return null;
        yield return null;
        if (seekSlider) seekSlider.value = time;
        _intendedPlaying = false;
        _state           = VideoState.WaitingFirstTap;
        isLoadingState   = false;
        UpdateOutput("Ready — tap the video to play.");
#else
        videoPlayer.playbackSpeed = PLAYBACK_SPEED;
        videoPlayer.time = time;
        videoPlayer.Play();
        yield return null;
        yield return null;
        videoPlayer.Pause();
        if (seekSlider) seekSlider.value = time;
        _intendedPlaying = false;
        _state = VideoState.WaitingFirstTap;
        isLoadingState = false;
        UpdateOutput("Ready...");
#endif
    }

    // ==================================================================
    // RE-EDIT: Jump to annotation
    // ==================================================================
    void OnReEditItem(StagedAnnotation itemToEdit)
    {
        if (_intendedPlaying) OnPauseClicked();
        currentWorkingItem = itemToEdit;
        if (currentBoundingBox) Destroy(currentBoundingBox.gameObject);
        DestroyFloatingPanel();
        StartCoroutine(JumpToAnnotationRoutine(itemToEdit));
    }

    IEnumerator JumpToAnnotationRoutine(StagedAnnotation item)
    {
        UpdateOutput("Seeking...");
        videoPlayer.time = item.timestamp;

#if !UNITY_WEBGL || UNITY_EDITOR
        videoPlayer.playbackSpeed = PLAYBACK_SPEED;
        videoPlayer.Play();
        yield return null;
        yield return null;
        videoPlayer.Pause();
#else
        yield return null;
        yield return null;
#endif

        _intendedPlaying = false;
        if (seekSlider) seekSlider.value = item.timestamp;

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
        UpdateOutput("Saving...");
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

            if (w.result == UnityWebRequest.Result.Success) { UpdateOutput("Saved!"); stagedList.Clear(); RefreshFinalPanel(); }
            else { UpdateOutput("❌ Save Failed"); }
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
    // INTERACTION — VIDEO TAP
    // Only fires from PointerDown on the videoDisplay RawImage.
    // ==================================================================
    public void OnVideoTapped(PointerEventData data)
    {
        if (!videoPlayer.isPrepared) return;
        if (currentFloatingPanelObj != null) return;

        // -----------------------------------------------------------------
        // FIX 5 — GIANT HITBOX (mathematical fence)
        // Even though overlayPlayButton has no raycast, Unity's canvas input
        // can still route clicks from outside the video rect to us via
        // bubbling. We explicitly check that the click landed inside the
        // videoDisplay RectTransform bounds. If not — ignore it completely.
        // -----------------------------------------------------------------
        if (!RectTransformUtility.RectangleContainsScreenPoint(
                videoDisplayRect, data.position, data.pressEventCamera))
        {
            Debug.Log("[Tap] Ignored — outside video bounds.");
            return;
        }

        // FIX 4 — PHANTOM TAP: minimum gap between accepted taps
        float now = Time.unscaledTime;
        if (now - _lastTapTime < MIN_TAP_INTERVAL) return;
        _lastTapTime = now;

        Debug.Log($"[Tap] Accepted. State={_state}");

        // FIX 4 — PHANTOM TAP: state machine — one action per state
        switch (_state)
        {
            // -----------------------------------------------------------
            // First tap: ONLY start the video — never annotate
            // -----------------------------------------------------------
            case VideoState.WaitingFirstTap:
                _state = VideoState.Playing;
                OnPlayClicked();
                break;

            // -----------------------------------------------------------
            // Playing: annotation tap — pause + capture + send screenshot
            // -----------------------------------------------------------
            case VideoState.Playing:
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    videoDisplayRect, data.position, data.pressEventCamera, out Vector2 localPoint);
                _state = VideoState.Annotating;
                wasPlayingBeforePause = true;
                OnPauseClicked();
                isProcessing = true;
                UpdateOutput("Annotations Generating...");
                SpawnBoundingBox(localPoint);
                FrameData frameData = CaptureFrameData(localPoint);
                StartCoroutine(ProcessApiRequest(frameData, localPoint));
                break;

            // -----------------------------------------------------------
            // Annotating: server request in flight — block all taps
            // -----------------------------------------------------------
            case VideoState.Annotating:
                Debug.Log("[Tap] Blocked — annotation in progress.");
                break;

            // -----------------------------------------------------------
            // Paused: resume playback
            // -----------------------------------------------------------
            case VideoState.Paused:
                _state = VideoState.Playing;
                OnPlayClicked();
                break;
        }
    }

    // -----------------------------------------------------------------
    // FIX 1 + 2: OnPlayClicked
    // _intendedPlaying flips instantly (FIX 2 — no waiting for Chrome).
    // ApplySpeedAfterDelay runs 5 frames later (FIX 1 — after Chrome reset).
    // -----------------------------------------------------------------
    public void OnPlayClicked()
    {
        _intendedPlaying = true;
        if (_state != VideoState.Annotating)
            _state = VideoState.Playing;
        videoPlayer.Play();
        StartCoroutine(ApplySpeedAfterDelay()); // FIX 1
    }

    public void OnPauseClicked()
    {
        _intendedPlaying = false;
        if (_state == VideoState.Playing)
            _state = VideoState.Paused;
        videoPlayer.Pause();
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
            if (child.GetComponent<AnnotationItemUI>() == activeHeadingUI || child.gameObject == selectedUI.gameObject || child.gameObject == activeStageButton) continue;
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
    // PROCESS API REQUEST
    // Captures the video frame via RenderTexture blit (WebGL-safe).
    // Screen.ReadPixels reads the main framebuffer which in WebGL does
    // NOT contain the video frame — video lives on its own GL texture.
    // ==================================================================
    IEnumerator ProcessApiRequest(FrameData frameData, Vector2 localPos)
    {
        yield return new WaitForEndOfFrame();

        byte[] imageBytes = null;
        Texture videoTex = videoPlayer.texture;

        if (videoTex != null)
        {
            // Blit video texture → our own RenderTexture → read pixels safely
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
            // Fallback: screen capture (Editor only)
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

        using (UnityWebRequest w = UnityWebRequest.Post("https://server.botclub.in/generate_annotations", f))
        {
            var asyncOp = w.SendWebRequest();
            float timer = 0f;

            while (!asyncOp.isDone)
            {
                timer += Time.unscaledDeltaTime;
                if (timer > autoResumeTimeout)
                {
                    w.Abort();
                    Debug.LogWarning("[API] Timed out.");
                    break;
                }
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
                    ResumeAfterFailure("Parse error. Resuming...");
                }
            }
            else
            {
                Debug.LogWarning($"[API] Failed: {w.error}");
                ResumeAfterFailure("Timeout. Resuming...");
            }
        }
    }

    private void ResumeAfterFailure(string message)
    {
        if (currentBoundingBox) Destroy(currentBoundingBox.gameObject);
        currentBoundingBox = null;
        isProcessing = false;
        _state = VideoState.Paused;
        UpdateOutput(message);
        StartCoroutine(DelayedResume());
    }

    private IEnumerator DelayedResume()
    {
        yield return new WaitForSecondsRealtime(1f);
        OnPlayClicked();
        UpdateOutput("Ready...");
    }

    // ==================================================================
    // DATABASE (EDITOR ONLY)
    // ==================================================================
    public void TestDatabaseConnection()
    {
#if UNITY_EDITOR
        using (MySqlConnection connection = new MySqlConnection(GetConnectionString()))
        {
            try { connection.Open(); Debug.Log("✅ Test Connection Successful!"); }
            catch (Exception ex) { Debug.LogError($"Database Connection Failed: {ex.Message}"); }
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