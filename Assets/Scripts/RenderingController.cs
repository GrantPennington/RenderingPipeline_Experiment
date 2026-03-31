using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using System.Collections;

namespace RenderingSandbox
{
    [RequireComponent(typeof(Camera))]
    public class RenderingController : MonoBehaviour
    {
        public enum RenderingMode
        {
            Native = 1,
            HalfResolution = 2,
            QuarterResolution = 4
        }

        public enum UpscaleMode
        {
            NearestNeighbor,
            Bilinear,
            SharpenedBilinear
        }

        public enum TestingPreset
        {
            Custom,
            Baseline,
            NaiveTemporal,
            MotionAwareTemporal,
            SimpleReprojection,
            MatrixReprojection,
            JitteredTemporal
        }

        public enum DebugVisualizationMode
        {
            FinalOutput,
            CurrentUpscaledFrame,
            HistoryBuffer,
            CurrentVsHistoryDifference,
            EffectiveHistoryWeight,
            HistoryConfidence,
            HistoryRejectionAmount
        }

        public enum OverlayDetailMode
        {
            Minimal,
            Full
        }

        [Header("Startup")]
        [SerializeField] private RenderingMode startingMode = RenderingMode.Native;
        [SerializeField] private UpscaleMode startingUpscaleMode = UpscaleMode.Bilinear;

        [Header("Presentation")]
        [SerializeField] private Shader upscaleShader;
        [SerializeField, Range(0f, 1.5f)] private float sharpenStrength = 0.35f;
        [SerializeField] private bool temporalAccumulationEnabled;
        [SerializeField, Range(0f, 0.98f)] private float historyWeight = 0.85f;
        [SerializeField, Range(0.01f, 0.2f)] private float historyWeightStep = 0.05f;

        [Header("Motion Aware Temporal")]
        [SerializeField] private float motionLowThreshold = 0.002f;
        [SerializeField] private float motionHighThreshold = 0.03f;
        [SerializeField] private float historyResetMotionThreshold = 0.08f;
        [SerializeField] private float rotationContributionScale = 0.01f;

        [Header("Simple Reprojection")]
        [SerializeField] private bool simpleReprojectionEnabled;
        [SerializeField] private float translationUvScale = 0.12f;
        [SerializeField] private float rotationUvScale = 0.0025f;

        [Header("Matrix Reprojection")]
        [SerializeField] private bool matrixReprojectionEnabled;
        [SerializeField, Range(0f, 1f)] private float approximateDepth01 = 0.55f;
        [SerializeField] private bool realDepthReprojectionEnabled;

        [Header("Jitter")]
        [SerializeField] private bool jitterEnabled;
        [SerializeField] private float jitterPixels = 0.5f;

        [Header("History Clamping")]
        [SerializeField] private bool historyClampingEnabled;
        [SerializeField, Range(0.01f, 0.5f)] private float historyClampAmount = 0.12f;

        [Header("Debug")]
        [SerializeField] private DebugVisualizationMode debugVisualizationMode = DebugVisualizationMode.FinalOutput;
        [SerializeField] private OverlayDetailMode overlayDetailMode = OverlayDetailMode.Full;
        [SerializeField, Range(1f, 64f)] private float differenceDebugScale = 18f;
        [SerializeField, Range(0f, 0.5f)] private float differenceDebugThreshold = 0.12f;
        [SerializeField, Range(0.5f, 4f)] private float differenceDebugExponent = 2.2f;

        [Header("Auto Camera Motion")]
        [SerializeField] private bool autoCameraMotionEnabled;
        [SerializeField] private Vector3 autoMotionCenter = Vector3.zero;
        [SerializeField] private float autoMotionAngleDegrees = 12f;
        [SerializeField] private float autoMotionSpeed = 0.9f;

        private static readonly int SourceTextureId = Shader.PropertyToID("_SourceTexture");
        private static readonly int HistoryTextureId = Shader.PropertyToID("_HistoryTexture");
        private static readonly int SourceTexelSizeId = Shader.PropertyToID("_SourceTexelSize");
        private static readonly int SharpenStrengthId = Shader.PropertyToID("_SharpenStrength");
        private static readonly int TemporalEnabledId = Shader.PropertyToID("_TemporalEnabled");
        private static readonly int HasHistoryId = Shader.PropertyToID("_HasHistory");
        private static readonly int HistoryWeightId = Shader.PropertyToID("_HistoryWeight");
        private static readonly int HistoryUvOffsetId = Shader.PropertyToID("_HistoryUvOffset");
        private static readonly int ReprojectionModeId = Shader.PropertyToID("_ReprojectionMode");
        private static readonly int ApproximateDepth01Id = Shader.PropertyToID("_ApproximateDepth01");
        private static readonly int CurrentInverseViewProjectionId = Shader.PropertyToID("_CurrentInverseViewProjection");
        private static readonly int PreviousViewProjectionId = Shader.PropertyToID("_PreviousViewProjection");
        private static readonly int HistoryClampingEnabledId = Shader.PropertyToID("_HistoryClampingEnabled");
        private static readonly int HistoryClampAmountId = Shader.PropertyToID("_HistoryClampAmount");
        private static readonly int DebugVisualizationModeId = Shader.PropertyToID("_DebugVisualizationMode");
        private static readonly int DebugEffectiveHistoryWeightId = Shader.PropertyToID("_DebugEffectiveHistoryWeight");
        private static readonly int DifferenceDebugScaleId = Shader.PropertyToID("_DifferenceDebugScale");
        private static readonly int DifferenceDebugThresholdId = Shader.PropertyToID("_DifferenceDebugThreshold");
        private static readonly int DifferenceDebugExponentId = Shader.PropertyToID("_DifferenceDebugExponent");
        private const int PresentationLayer = 31;

        private Camera targetCamera;
        private RenderTexture lowResolutionTexture;
        private RenderTexture historyReadTexture;
        private RenderTexture historyWriteTexture;
        private Camera presentationCamera;
        private Transform presentationQuad;
        private Canvas overlayCanvas;
        private RenderingDebugOverlay debugOverlay;
        private Material upscaleMaterial;
        private int originalCameraCullingMask;
        private bool hasHistoryFrame;
        private Coroutine historyUpdateCoroutine;
        private float effectiveHistoryWeight;
        private float cameraMotionAmount;
        private Vector3 lastCameraPosition;
        private Quaternion lastCameraRotation;
        private Vector3 savedManualCameraPosition;
        private Quaternion savedManualCameraRotation;
        private Vector3 autoMotionBaseOffset;
        private bool autoMotionPoseInitialized;
        private Vector3 lastPositionDelta;
        private Vector2 historyUvOffset;
        private Matrix4x4 currentInverseViewProjectionMatrix;
        private Matrix4x4 previousViewProjectionMatrix;
        private int jitterFrameIndex;
        private Vector2 currentJitterOffsetPixels;
        private Matrix4x4 baseProjectionMatrix;
        private TestingPreset currentPreset = TestingPreset.Custom;

        private RenderingMode currentMode;
        private UpscaleMode currentUpscaleMode;
        private int currentRenderWidth;
        private int currentRenderHeight;
        private int lastScreenWidth;
        private int lastScreenHeight;

        public RenderingMode CurrentMode => currentMode;
        public UpscaleMode CurrentUpscaleMode => currentUpscaleMode;
        public bool TemporalAccumulationEnabled => temporalAccumulationEnabled;
        public bool SimpleReprojectionEnabled => simpleReprojectionEnabled;
        public bool MatrixReprojectionEnabled => matrixReprojectionEnabled;
        public bool RealDepthReprojectionEnabled => realDepthReprojectionEnabled;
        public bool JitterEnabled => jitterEnabled;
        public bool HistoryClampingEnabled => historyClampingEnabled;
        public bool AutoCameraMotionEnabled => autoCameraMotionEnabled;
        public string CurrentPresetName => GetPresetLabel(currentPreset);
        public DebugVisualizationMode CurrentDebugVisualizationMode => debugVisualizationMode;
        public OverlayDetailMode CurrentOverlayDetailMode => overlayDetailMode;
        public string CurrentDebugVisualizationModeLabel => GetDebugVisualizationModeLabel(debugVisualizationMode);
        public float HistoryWeight => historyWeight;
        public float EffectiveHistoryWeight => effectiveHistoryWeight;
        public float CameraMotionAmount => cameraMotionAmount;
        public float DifferenceDebugThreshold => differenceDebugThreshold;
        public float HistoryClampAmount => historyClampAmount;
        public float HistoryRejectionStrength => Mathf.InverseLerp(0.5f, 0.01f, historyClampAmount);
        public Vector2 HistoryUvOffset => historyUvOffset;
        public Vector2 CurrentJitterOffsetPixels => currentJitterOffsetPixels;
        public string CurrentReprojectionModeLabel => GetReprojectionModeLabel();
        public int RenderWidth => currentMode == RenderingMode.Native ? Screen.width : currentRenderWidth;
        public int RenderHeight => currentMode == RenderingMode.Native ? Screen.height : currentRenderHeight;
        public int ScreenWidth => Screen.width;
        public int ScreenHeight => Screen.height;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            Camera mainCamera = Camera.main;
            if (mainCamera == null)
            {
                Debug.LogWarning("RenderingController could not find a camera tagged MainCamera.");
                return;
            }

            if (mainCamera.GetComponent<RenderingController>() == null)
            {
                mainCamera.gameObject.AddComponent<RenderingController>();
            }
        }

        private void Awake()
        {
            targetCamera = GetComponent<Camera>();
            originalCameraCullingMask = targetCamera.cullingMask;
            ConfigureSceneDepthAccess();
            EnsurePresentationMaterial();
            EnsurePresentationObjects();
            EnsureOverlayExists();
            baseProjectionMatrix = targetCamera.projectionMatrix;
            lastCameraPosition = targetCamera.transform.position;
            lastCameraRotation = targetCamera.transform.rotation;
            UpdateCurrentMatrices();
            previousViewProjectionMatrix = GetCurrentViewProjectionMatrix();
            effectiveHistoryWeight = historyWeight;
            SetUpscaleMode(startingUpscaleMode, true);
            SetRenderingMode(startingMode, true);
        }

        private void OnEnable()
        {
            if (historyUpdateCoroutine == null)
            {
                historyUpdateCoroutine = StartCoroutine(UpdateHistoryAtEndOfFrame());
            }
        }

        private void Update()
        {
            HandleModeInput();
            UpdateAutoCameraMotion();
            ApplyJitterToCameraProjection();

            if (Screen.width != lastScreenWidth || Screen.height != lastScreenHeight)
            {
                UpdatePresentationQuadScale();
                ApplyCurrentMode(forceRecreateTexture: true);
            }
        }

        private void LateUpdate()
        {
            UpdateCameraMotionState();

            if (jitterEnabled)
            {
                // Jitter only helps if the sample position changes over time. Advancing the
                // pattern every frame makes temporal accumulation combine slightly different
                // subpixel information instead of repeating the same sample forever.
                jitterFrameIndex++;
            }
        }

        private void OnDisable()
        {
            if (historyUpdateCoroutine != null)
            {
                StopCoroutine(historyUpdateCoroutine);
                historyUpdateCoroutine = null;
            }

            RestoreBaseProjectionMatrix();
            targetCamera.cullingMask = originalCameraCullingMask;
            ResetToNativeOutput();
        }

        private void OnDestroy()
        {
            ReleaseRenderTexture();
            ReleaseHistoryTextures();

            if (overlayCanvas != null)
            {
                Destroy(overlayCanvas.gameObject);
            }

            if (upscaleMaterial != null)
            {
                Destroy(upscaleMaterial);
            }

            if (presentationCamera != null)
            {
                Destroy(presentationCamera.gameObject);
            }
        }

        public void SetRenderingMode(RenderingMode mode, bool forceApply = false)
        {
            if (!forceApply && currentMode == mode)
            {
                return;
            }

            currentMode = mode;
            ResetHistory();
            ApplyCurrentMode(forceApply);
        }

        public void SetUpscaleMode(UpscaleMode mode, bool forceApply = false)
        {
            if (!forceApply && currentUpscaleMode == mode)
            {
                return;
            }

            currentUpscaleMode = mode;

            if (lowResolutionTexture != null)
            {
                ApplySamplingState();
            }

            ResetHistory();

            // Upscale mode changes affect both the RenderTexture filter and the presentation
            // material. This is where the runtime switch between nearest, bilinear, and
            // sharpened bilinear is actually applied.
            UpdatePresentationMaterial();

            if (debugOverlay != null)
            {
                debugOverlay.Refresh();
            }
        }

        private void HandleModeInput()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (keyboard.digit1Key.wasPressedThisFrame || keyboard.numpad1Key.wasPressedThisFrame)
            {
                SetRenderingMode(RenderingMode.Native);
                MarkPresetCustom();
            }
            else if (keyboard.digit2Key.wasPressedThisFrame || keyboard.numpad2Key.wasPressedThisFrame)
            {
                SetRenderingMode(RenderingMode.HalfResolution);
                MarkPresetCustom();
            }
            else if (keyboard.digit3Key.wasPressedThisFrame || keyboard.numpad3Key.wasPressedThisFrame)
            {
                SetRenderingMode(RenderingMode.QuarterResolution);
                MarkPresetCustom();
            }

            if (keyboard.qKey.wasPressedThisFrame)
            {
                SetUpscaleMode(UpscaleMode.NearestNeighbor);
                MarkPresetCustom();
            }
            else if (keyboard.wKey.wasPressedThisFrame)
            {
                SetUpscaleMode(UpscaleMode.Bilinear);
                MarkPresetCustom();
            }
            else if (keyboard.eKey.wasPressedThisFrame)
            {
                SetUpscaleMode(UpscaleMode.SharpenedBilinear);
                MarkPresetCustom();
            }

            if (keyboard.tKey.wasPressedThisFrame)
            {
                temporalAccumulationEnabled = !temporalAccumulationEnabled;
                ResetHistory();
                UpdateEffectiveHistoryWeight();
                UpdatePresentationMaterial();
                MarkPresetCustom();
                debugOverlay.Refresh();
            }

            if (keyboard.leftBracketKey.wasPressedThisFrame)
            {
                historyWeight = Mathf.Clamp(historyWeight - historyWeightStep, 0f, 0.98f);
                ResetHistory();
                UpdateEffectiveHistoryWeight();
                UpdatePresentationMaterial();
                MarkPresetCustom();
                debugOverlay.Refresh();
            }

            if (keyboard.jKey.wasPressedThisFrame)
            {
                differenceDebugThreshold = Mathf.Clamp(differenceDebugThreshold - 0.01f, 0f, 0.5f);
                UpdatePresentationMaterial();
                MarkPresetCustom();
                debugOverlay.Refresh();
            }
            else if (keyboard.kKey.wasPressedThisFrame)
            {
                differenceDebugThreshold = Mathf.Clamp(differenceDebugThreshold + 0.01f, 0f, 0.5f);
                UpdatePresentationMaterial();
                MarkPresetCustom();
                debugOverlay.Refresh();
            }

            if (keyboard.nKey.wasPressedThisFrame)
            {
                // In this sandbox, the clamp amount is the allowed color distance between
                // current and history. Larger amounts mean weaker rejection, while smaller
                // amounts make the filter reject stale history more aggressively.
                historyClampAmount = Mathf.Clamp(historyClampAmount + 0.01f, 0.01f, 0.5f);
                UpdatePresentationMaterial();
                MarkPresetCustom();
                debugOverlay.Refresh();
            }
            else if (keyboard.mKey.wasPressedThisFrame)
            {
                historyClampAmount = Mathf.Clamp(historyClampAmount - 0.01f, 0.01f, 0.5f);
                UpdatePresentationMaterial();
                MarkPresetCustom();
                debugOverlay.Refresh();
            }
            else if (keyboard.rightBracketKey.wasPressedThisFrame)
            {
                historyWeight = Mathf.Clamp(historyWeight + historyWeightStep, 0f, 0.98f);
                ResetHistory();
                UpdateEffectiveHistoryWeight();
                UpdatePresentationMaterial();
                MarkPresetCustom();
                debugOverlay.Refresh();
            }

            if (keyboard.yKey.wasPressedThisFrame)
            {
                ToggleAutoCameraMotion();
                MarkPresetCustom();
            }

            if (keyboard.uKey.wasPressedThisFrame)
            {
                simpleReprojectionEnabled = !simpleReprojectionEnabled;
                ResetHistory();
                UpdatePresentationMaterial();
                MarkPresetCustom();
                debugOverlay.Refresh();
            }

            if (keyboard.iKey.wasPressedThisFrame)
            {
                matrixReprojectionEnabled = !matrixReprojectionEnabled;
                ResetHistory();
                UpdateCurrentMatrices();
                previousViewProjectionMatrix = GetCurrentViewProjectionMatrix();
                UpdatePresentationMaterial();
                MarkPresetCustom();
                debugOverlay.Refresh();
            }

            if (keyboard.lKey.wasPressedThisFrame)
            {
                realDepthReprojectionEnabled = !realDepthReprojectionEnabled;
                ResetHistory();
                UpdateCurrentMatrices();
                previousViewProjectionMatrix = GetCurrentViewProjectionMatrix();
                UpdatePresentationMaterial();
                MarkPresetCustom();
                debugOverlay.Refresh();
            }

            if (keyboard.oKey.wasPressedThisFrame)
            {
                jitterEnabled = !jitterEnabled;
                jitterFrameIndex = 0;
                currentJitterOffsetPixels = Vector2.zero;
                ResetHistory();
                ApplyJitterToCameraProjection();
                UpdateCurrentMatrices();
                previousViewProjectionMatrix = GetCurrentViewProjectionMatrix();
                UpdatePresentationMaterial();
                MarkPresetCustom();
                debugOverlay.Refresh();
            }

            if (keyboard.pKey.wasPressedThisFrame)
            {
                historyClampingEnabled = !historyClampingEnabled;
                ResetHistory();
                UpdatePresentationMaterial();
                MarkPresetCustom();
                debugOverlay.Refresh();
            }

            if (keyboard.vKey.wasPressedThisFrame)
            {
                CycleDebugVisualizationMode();
                UpdatePresentationMaterial();
                debugOverlay.Refresh();
            }

            if (keyboard.bKey.wasPressedThisFrame)
            {
                overlayDetailMode =
                    overlayDetailMode == OverlayDetailMode.Full
                        ? OverlayDetailMode.Minimal
                        : OverlayDetailMode.Full;
                debugOverlay.Refresh();
            }

            if (keyboard.f1Key.wasPressedThisFrame)
            {
                ApplyPreset(TestingPreset.Baseline);
            }
            else if (keyboard.f2Key.wasPressedThisFrame)
            {
                ApplyPreset(TestingPreset.NaiveTemporal);
            }
            else if (keyboard.f3Key.wasPressedThisFrame)
            {
                ApplyPreset(TestingPreset.MotionAwareTemporal);
            }
            else if (keyboard.f4Key.wasPressedThisFrame)
            {
                ApplyPreset(TestingPreset.SimpleReprojection);
            }
            else if (keyboard.f5Key.wasPressedThisFrame)
            {
                ApplyPreset(TestingPreset.MatrixReprojection);
            }
            else if (keyboard.f6Key.wasPressedThisFrame)
            {
                ApplyPreset(TestingPreset.JitteredTemporal);
            }
        }

        private void ApplyCurrentMode(bool forceRecreateTexture = false)
        {
            lastScreenWidth = Screen.width;
            lastScreenHeight = Screen.height;

            if (currentMode == RenderingMode.Native)
            {
                ResetToNativeOutput();
                debugOverlay.Refresh();
                return;
            }

            int divisor = (int)currentMode;
            int desiredWidth = Mathf.Max(1, Screen.width / divisor);
            int desiredHeight = Mathf.Max(1, Screen.height / divisor);

            bool needsNewTexture =
                forceRecreateTexture ||
                lowResolutionTexture == null ||
                !lowResolutionTexture.IsCreated() ||
                lowResolutionTexture.width != desiredWidth ||
                lowResolutionTexture.height != desiredHeight;

            if (needsNewTexture)
            {
                // Resolution mode changes are applied here. If the requested render size changed,
                // the old RenderTexture is released and a correctly sized one is created.
                CreateLowResolutionTexture(desiredWidth, desiredHeight);
            }

            EnsureHistoryTextures(Screen.width, Screen.height);

            // A RenderTexture is an off-screen color buffer. The scene camera writes into this
            // smaller image first so we can control how it is sampled when it is enlarged later.
            targetCamera.targetTexture = lowResolutionTexture;
            targetCamera.cullingMask = originalCameraCullingMask & ~(1 << PresentationLayer);

            if (presentationQuad != null)
            {
                presentationQuad.gameObject.SetActive(true);
            }

            if (presentationCamera != null)
            {
                presentationCamera.enabled = true;
            }

            // The presentation material reads from the low-res RenderTexture and draws it onto
            // the fullscreen quad. This is the point where the screen-facing upscale path updates.
            UpdatePresentationMaterial();
            debugOverlay.Refresh();
        }

        private void CreateLowResolutionTexture(int width, int height)
        {
            ReleaseRenderTexture();

            currentRenderWidth = width;
            currentRenderHeight = height;

            // Rendering fewer pixels is cheaper for the GPU. Half resolution shades one
            // quarter as many pixels, and quarter resolution shades one sixteenth as many.
            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 24)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false
            };

            lowResolutionTexture = new RenderTexture(descriptor)
            {
                name = $"LowResScene_{width}x{height}",
                wrapMode = TextureWrapMode.Clamp
            };

            ApplySamplingState();
            lowResolutionTexture.Create();
            ResetHistory();
            UpdatePresentationMaterial();
        }

        private void ApplySamplingState()
        {
            if (lowResolutionTexture == null)
            {
                return;
            }

            // Sampling decides how the GPU reads between texels when a texture is enlarged.
            // Point sampling snaps to one source texel, which makes edges look crisp and blocky.
            // Bilinear blends neighboring texels, which looks smoother but also blurrier.
            lowResolutionTexture.filterMode =
                currentUpscaleMode == UpscaleMode.NearestNeighbor
                    ? FilterMode.Point
                    : FilterMode.Bilinear;
        }

        private void ReleaseRenderTexture()
        {
            if (lowResolutionTexture == null)
            {
                return;
            }

            if (targetCamera != null && targetCamera.targetTexture == lowResolutionTexture)
            {
                targetCamera.targetTexture = null;
            }

            lowResolutionTexture.Release();
            Destroy(lowResolutionTexture);
            lowResolutionTexture = null;
        }

        private void ResetToNativeOutput()
        {
            targetCamera.targetTexture = null;
            targetCamera.cullingMask = originalCameraCullingMask;
            currentRenderWidth = Screen.width;
            currentRenderHeight = Screen.height;

            if (presentationQuad != null)
            {
                presentationQuad.gameObject.SetActive(false);
            }

            if (presentationCamera != null)
            {
                presentationCamera.enabled = false;
            }

            ResetHistory();
            UpdateEffectiveHistoryWeight();
            UpdatePresentationMaterial();
            ReleaseRenderTexture();
        }

        private void EnsurePresentationMaterial()
        {
            if (upscaleMaterial != null)
            {
                return;
            }

            if (upscaleShader == null)
            {
                upscaleShader = Shader.Find("Hidden/RenderingSandbox/UpscalePresent");
            }

            if (upscaleShader == null)
            {
                Debug.LogError("RenderingController could not find the UpscalePresent shader.");
                return;
            }

            upscaleMaterial = new Material(upscaleShader)
            {
                name = "Runtime Upscale Present Material",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        private void ConfigureSceneDepthAccess()
        {
            // Real depth reprojection needs the scene camera to publish a depth texture so the
            // presentation shader can reconstruct a world position from the current pixel.
            targetCamera.depthTextureMode |= DepthTextureMode.Depth;

            UniversalAdditionalCameraData additionalCameraData = targetCamera.GetComponent<UniversalAdditionalCameraData>();
            if (additionalCameraData != null)
            {
                additionalCameraData.requiresDepthOption = CameraOverrideOption.On;
            }
        }

        private void ApplyPreset(TestingPreset preset)
        {
            // Presets make visual testing more repeatable by applying a known group of settings
            // in one step. That makes side-by-side comparisons easier than trying to remember
            // a full hotkey sequence every time.
            const float highHistoryWeight = 0.9f;

            switch (preset)
            {
                case TestingPreset.Baseline:
                    SetRenderingMode(RenderingMode.HalfResolution);
                    SetUpscaleMode(UpscaleMode.Bilinear);
                    temporalAccumulationEnabled = false;
                    historyWeight = highHistoryWeight;
                    simpleReprojectionEnabled = false;
                    matrixReprojectionEnabled = false;
                    realDepthReprojectionEnabled = false;
                    jitterEnabled = false;
                    historyClampingEnabled = false;
                    SetAutoCameraMotion(false);
                    break;
                case TestingPreset.NaiveTemporal:
                    SetRenderingMode(RenderingMode.QuarterResolution);
                    SetUpscaleMode(UpscaleMode.Bilinear);
                    temporalAccumulationEnabled = true;
                    historyWeight = highHistoryWeight;
                    simpleReprojectionEnabled = false;
                    matrixReprojectionEnabled = false;
                    realDepthReprojectionEnabled = false;
                    jitterEnabled = false;
                    historyClampingEnabled = false;
                    SetAutoCameraMotion(true);
                    break;
                case TestingPreset.MotionAwareTemporal:
                    SetRenderingMode(RenderingMode.QuarterResolution);
                    SetUpscaleMode(UpscaleMode.Bilinear);
                    temporalAccumulationEnabled = true;
                    historyWeight = highHistoryWeight;
                    simpleReprojectionEnabled = false;
                    matrixReprojectionEnabled = false;
                    realDepthReprojectionEnabled = false;
                    jitterEnabled = false;
                    historyClampingEnabled = false;
                    SetAutoCameraMotion(true);
                    break;
                case TestingPreset.SimpleReprojection:
                    SetRenderingMode(RenderingMode.QuarterResolution);
                    SetUpscaleMode(UpscaleMode.Bilinear);
                    temporalAccumulationEnabled = true;
                    historyWeight = highHistoryWeight;
                    simpleReprojectionEnabled = true;
                    matrixReprojectionEnabled = false;
                    realDepthReprojectionEnabled = false;
                    jitterEnabled = false;
                    historyClampingEnabled = false;
                    SetAutoCameraMotion(true);
                    break;
                case TestingPreset.MatrixReprojection:
                    SetRenderingMode(RenderingMode.QuarterResolution);
                    SetUpscaleMode(UpscaleMode.Bilinear);
                    temporalAccumulationEnabled = true;
                    historyWeight = highHistoryWeight;
                    simpleReprojectionEnabled = false;
                    matrixReprojectionEnabled = true;
                    realDepthReprojectionEnabled = false;
                    jitterEnabled = false;
                    historyClampingEnabled = false;
                    SetAutoCameraMotion(true);
                    break;
                case TestingPreset.JitteredTemporal:
                    SetRenderingMode(RenderingMode.QuarterResolution);
                    SetUpscaleMode(UpscaleMode.Bilinear);
                    temporalAccumulationEnabled = true;
                    historyWeight = highHistoryWeight;
                    simpleReprojectionEnabled = false;
                    matrixReprojectionEnabled = false;
                    realDepthReprojectionEnabled = false;
                    jitterEnabled = true;
                    historyClampingEnabled = false;
                    SetAutoCameraMotion(true);
                    break;
                default:
                    return;
            }

            jitterFrameIndex = 0;
            currentJitterOffsetPixels = Vector2.zero;
            ApplyJitterToCameraProjection();
            UpdateCurrentMatrices();
            previousViewProjectionMatrix = GetCurrentViewProjectionMatrix();
            ResetHistory();
            UpdateEffectiveHistoryWeight();
            UpdatePresentationMaterial();
            currentPreset = preset;
            debugOverlay.Refresh();
        }

        private void SetAutoCameraMotion(bool enabled)
        {
            if (autoCameraMotionEnabled == enabled)
            {
                if (!enabled && autoMotionPoseInitialized)
                {
                    targetCamera.transform.SetPositionAndRotation(savedManualCameraPosition, savedManualCameraRotation);
                }

                return;
            }

            autoCameraMotionEnabled = enabled;

            if (autoCameraMotionEnabled)
            {
                savedManualCameraPosition = targetCamera.transform.position;
                savedManualCameraRotation = targetCamera.transform.rotation;
                autoMotionBaseOffset = savedManualCameraPosition - autoMotionCenter;
                autoMotionPoseInitialized = true;
            }
            else if (autoMotionPoseInitialized)
            {
                targetCamera.transform.SetPositionAndRotation(savedManualCameraPosition, savedManualCameraRotation);
            }

            lastCameraPosition = targetCamera.transform.position;
            lastCameraRotation = targetCamera.transform.rotation;
            lastPositionDelta = Vector3.zero;
            historyUvOffset = Vector2.zero;
        }

        private void MarkPresetCustom()
        {
            currentPreset = TestingPreset.Custom;
        }

        private string GetPresetLabel(TestingPreset preset)
        {
            switch (preset)
            {
                case TestingPreset.Baseline:
                    return "Baseline";
                case TestingPreset.NaiveTemporal:
                    return "Naive Temporal";
                case TestingPreset.MotionAwareTemporal:
                    return "Motion-Aware Temporal";
                case TestingPreset.SimpleReprojection:
                    return "Simple Reprojection";
                case TestingPreset.MatrixReprojection:
                    return "Matrix Reprojection";
                case TestingPreset.JitteredTemporal:
                    return "Jittered Temporal";
                default:
                    return "Custom";
            }
        }

        private void CycleDebugVisualizationMode()
        {
            int modeCount = System.Enum.GetValues(typeof(DebugVisualizationMode)).Length;
            debugVisualizationMode = (DebugVisualizationMode)(((int)debugVisualizationMode + 1) % modeCount);
        }

        private string GetDebugVisualizationModeLabel(DebugVisualizationMode mode)
        {
            switch (mode)
            {
                case DebugVisualizationMode.CurrentUpscaledFrame:
                    return "Current Upscaled Frame";
                case DebugVisualizationMode.HistoryBuffer:
                    return "History Buffer";
                case DebugVisualizationMode.CurrentVsHistoryDifference:
                    return "Current vs History Difference";
                case DebugVisualizationMode.EffectiveHistoryWeight:
                    return "Effective History Weight";
                case DebugVisualizationMode.HistoryConfidence:
                    return "History Confidence";
                case DebugVisualizationMode.HistoryRejectionAmount:
                    return "History Rejection Amount";
                default:
                    return "Final Output";
            }
        }

        private void ApplyJitterToCameraProjection()
        {
            if (targetCamera == null)
            {
                return;
            }

            baseProjectionMatrix = targetCamera.nonJitteredProjectionMatrix;
            currentJitterOffsetPixels = jitterEnabled ? GetJitterOffsetPixels(jitterFrameIndex) : Vector2.zero;

            // Jittered sampling nudges the projection by a tiny subpixel offset each frame.
            // Without that offset, every frame samples the same locations and temporal
            // accumulation does not gather much new information over time.
            Matrix4x4 jitteredProjection = baseProjectionMatrix;

            if (Screen.width > 0 && Screen.height > 0 && jitterEnabled)
            {
                float jitterX = (currentJitterOffsetPixels.x * 2f) / Screen.width;
                float jitterY = (currentJitterOffsetPixels.y * 2f) / Screen.height;
                jitteredProjection.m02 += jitterX;
                jitteredProjection.m12 += jitterY;
            }

            targetCamera.nonJitteredProjectionMatrix = baseProjectionMatrix;
            targetCamera.projectionMatrix = jitteredProjection;
        }

        private void RestoreBaseProjectionMatrix()
        {
            if (targetCamera == null)
            {
                return;
            }

            targetCamera.projectionMatrix = baseProjectionMatrix;
            targetCamera.nonJitteredProjectionMatrix = baseProjectionMatrix;
            currentJitterOffsetPixels = Vector2.zero;
        }

        private Vector2 GetJitterOffsetPixels(int frameIndex)
        {
            Vector2[] pattern =
            {
                new Vector2(-0.25f, -0.25f),
                new Vector2(0.25f, 0.25f),
                new Vector2(-0.25f, 0.25f),
                new Vector2(0.25f, -0.25f)
            };

            return pattern[frameIndex % pattern.Length] * jitterPixels;
        }

        private void UpdatePresentationMaterial()
        {
            if (upscaleMaterial == null)
            {
                return;
            }

            if (lowResolutionTexture == null)
            {
                upscaleMaterial.SetTexture(SourceTextureId, Texture2D.blackTexture);
                upscaleMaterial.SetTexture(HistoryTextureId, Texture2D.blackTexture);
                upscaleMaterial.SetVector(SourceTexelSizeId, Vector4.zero);
                upscaleMaterial.SetFloat(SharpenStrengthId, 0f);
                upscaleMaterial.SetFloat(TemporalEnabledId, 0f);
                upscaleMaterial.SetFloat(HasHistoryId, 0f);
                upscaleMaterial.SetFloat(HistoryWeightId, effectiveHistoryWeight);
                upscaleMaterial.SetVector(HistoryUvOffsetId, Vector4.zero);
                upscaleMaterial.SetFloat(ReprojectionModeId, 0f);
                upscaleMaterial.SetFloat(ApproximateDepth01Id, approximateDepth01);
                upscaleMaterial.SetMatrix(CurrentInverseViewProjectionId, currentInverseViewProjectionMatrix);
                upscaleMaterial.SetMatrix(PreviousViewProjectionId, previousViewProjectionMatrix);
                upscaleMaterial.SetFloat(HistoryClampingEnabledId, 0f);
                upscaleMaterial.SetFloat(HistoryClampAmountId, historyClampAmount);
                upscaleMaterial.SetFloat(DebugVisualizationModeId, (float)debugVisualizationMode);
                upscaleMaterial.SetFloat(DebugEffectiveHistoryWeightId, effectiveHistoryWeight);
                upscaleMaterial.SetFloat(DifferenceDebugScaleId, differenceDebugScale);
                upscaleMaterial.SetFloat(DifferenceDebugThresholdId, differenceDebugThreshold);
                upscaleMaterial.SetFloat(DifferenceDebugExponentId, differenceDebugExponent);
                return;
            }

            // The fullscreen presentation material reads the current low-res render target,
            // optionally mixes it with a history buffer, and outputs the final image that the
            // presentation camera shows on screen.
            upscaleMaterial.SetTexture(SourceTextureId, lowResolutionTexture);
            upscaleMaterial.SetTexture(HistoryTextureId, historyReadTexture != null ? historyReadTexture : Texture2D.blackTexture);
            upscaleMaterial.SetVector(
                SourceTexelSizeId,
                new Vector4(
                    1f / lowResolutionTexture.width,
                    1f / lowResolutionTexture.height,
                    lowResolutionTexture.width,
                    lowResolutionTexture.height));
            upscaleMaterial.SetFloat(SharpenStrengthId, currentUpscaleMode == UpscaleMode.SharpenedBilinear ? sharpenStrength : 0f);
            upscaleMaterial.SetFloat(TemporalEnabledId, temporalAccumulationEnabled ? 1f : 0f);
            upscaleMaterial.SetFloat(HasHistoryId, hasHistoryFrame ? 1f : 0f);
            upscaleMaterial.SetFloat(HistoryWeightId, effectiveHistoryWeight);
            upscaleMaterial.SetVector(HistoryUvOffsetId, historyUvOffset);
            upscaleMaterial.SetFloat(ReprojectionModeId, GetReprojectionModeValue());
            upscaleMaterial.SetFloat(ApproximateDepth01Id, approximateDepth01);
            upscaleMaterial.SetMatrix(CurrentInverseViewProjectionId, currentInverseViewProjectionMatrix);
            upscaleMaterial.SetMatrix(PreviousViewProjectionId, previousViewProjectionMatrix);
            upscaleMaterial.SetFloat(HistoryClampingEnabledId, historyClampingEnabled ? 1f : 0f);
            upscaleMaterial.SetFloat(HistoryClampAmountId, historyClampAmount);
            upscaleMaterial.SetFloat(DebugVisualizationModeId, (float)debugVisualizationMode);
            upscaleMaterial.SetFloat(DebugEffectiveHistoryWeightId, effectiveHistoryWeight);
            upscaleMaterial.SetFloat(DifferenceDebugScaleId, differenceDebugScale);
            upscaleMaterial.SetFloat(DifferenceDebugThresholdId, differenceDebugThreshold);
            upscaleMaterial.SetFloat(DifferenceDebugExponentId, differenceDebugExponent);
        }

        private void EnsureHistoryTextures(int width, int height)
        {
            bool needsNewTextures =
                historyReadTexture == null ||
                historyWriteTexture == null ||
                !historyReadTexture.IsCreated() ||
                !historyWriteTexture.IsCreated() ||
                historyReadTexture.width != width ||
                historyReadTexture.height != height;

            if (!needsNewTextures)
            {
                return;
            }

            ReleaseHistoryTextures();

            // A history buffer stores the previously presented image. Temporal techniques use
            // that old data to stabilize noise and recover detail over time.
            historyReadTexture = CreateHistoryTexture(width, height, "HistoryRead");
            historyWriteTexture = CreateHistoryTexture(width, height, "HistoryWrite");
            ResetHistory();
        }

        private RenderTexture CreateHistoryTexture(int width, int height, string textureName)
        {
            RenderTexture texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = textureName,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };

            texture.Create();
            return texture;
        }

        private void ReleaseHistoryTextures()
        {
            ReleaseTexture(ref historyReadTexture);
            ReleaseTexture(ref historyWriteTexture);
            hasHistoryFrame = false;
        }

        private void ResetHistory()
        {
            hasHistoryFrame = false;
            ClearTexture(historyReadTexture);
            ClearTexture(historyWriteTexture);
        }

        private void ToggleAutoCameraMotion()
        {
            SetAutoCameraMotion(!autoCameraMotionEnabled);
            UpdateCurrentMatrices();
            previousViewProjectionMatrix = GetCurrentViewProjectionMatrix();
            ResetHistory();
            UpdateEffectiveHistoryWeight();
            UpdatePresentationMaterial();
            debugOverlay.Refresh();
        }

        private void UpdateAutoCameraMotion()
        {
            if (!autoCameraMotionEnabled || !autoMotionPoseInitialized)
            {
                return;
            }

            // Auto camera motion provides a repeatable way to expose temporal artifacts without
            // needing manual input every time. It gently swings the camera around the scene center.
            float orbitAngle = Mathf.Sin(Time.time * autoMotionSpeed) * autoMotionAngleDegrees;
            Quaternion orbitRotation = Quaternion.AngleAxis(orbitAngle, Vector3.up);
            Vector3 orbitOffset = orbitRotation * autoMotionBaseOffset;
            Vector3 newPosition = autoMotionCenter + orbitOffset;
            Quaternion newRotation = Quaternion.LookRotation((autoMotionCenter - newPosition).normalized, Vector3.up);
            targetCamera.transform.SetPositionAndRotation(newPosition, newRotation);
        }

        private void UpdateCameraMotionState()
        {
            Vector3 currentPosition = targetCamera.transform.position;
            Quaternion currentRotation = targetCamera.transform.rotation;

            Vector3 positionDeltaVector = currentPosition - lastCameraPosition;
            lastPositionDelta = positionDeltaVector;
            float positionDelta = positionDeltaVector.magnitude;
            float rotationDelta = Quaternion.Angle(currentRotation, lastCameraRotation) * rotationContributionScale;
            cameraMotionAmount = positionDelta + rotationDelta;

            UpdateCurrentMatrices();
            UpdateHistoryUvOffset(currentRotation);

            // Naive temporal accumulation ghosts because it keeps trusting old pixels even when
            // the camera moved and those pixels no longer line up. Motion-aware weighting and a
            // simple UV offset are both rough steps toward the role motion vectors and reprojection
            // play in TAA-like systems.
            if (cameraMotionAmount > historyResetMotionThreshold && hasHistoryFrame)
            {
                ResetHistory();
            }

            UpdateEffectiveHistoryWeight();
            UpdatePresentationMaterial();

            lastCameraPosition = currentPosition;
            lastCameraRotation = currentRotation;
        }

        private void UpdateEffectiveHistoryWeight()
        {
            if (!temporalAccumulationEnabled)
            {
                effectiveHistoryWeight = 0f;
                return;
            }

            // The manual history weight is the maximum trust in old frames. As camera motion
            // increases, that trust falls off toward zero so history contributes less.
            float motionFactor = Mathf.InverseLerp(motionLowThreshold, motionHighThreshold, cameraMotionAmount);
            effectiveHistoryWeight = historyWeight * (1f - motionFactor);
        }

        private void UpdateHistoryUvOffset(Quaternion currentRotation)
        {
            if (!simpleReprojectionEnabled || !temporalAccumulationEnabled)
            {
                historyUvOffset = Vector2.zero;
                return;
            }

            // Same-UV history sampling assumes the old frame still lines up with the new one.
            // Reprojection tries to shift the history sample toward where that old data moved.
            // This slice only uses one global offset from camera transform deltas, so it is a
            // rough approximation rather than a physically correct per-pixel reprojection.
            Vector3 localTranslationDelta = targetCamera.transform.InverseTransformDirection(lastPositionDelta);
            Vector2 translationOffset = new Vector2(-localTranslationDelta.x, -localTranslationDelta.y) * translationUvScale;

            Vector3 currentEuler = currentRotation.eulerAngles;
            Vector3 previousEuler = lastCameraRotation.eulerAngles;
            float yawDelta = Mathf.DeltaAngle(previousEuler.y, currentEuler.y);
            float pitchDelta = Mathf.DeltaAngle(previousEuler.x, currentEuler.x);
            Vector2 rotationOffset = new Vector2(-yawDelta, -pitchDelta) * rotationUvScale;

            historyUvOffset = translationOffset + rotationOffset;
        }

        private void UpdateCurrentMatrices()
        {
            Matrix4x4 currentViewProjection = GetCurrentViewProjectionMatrix();
            currentInverseViewProjectionMatrix = currentViewProjection.inverse;
        }

        private Matrix4x4 GetCurrentViewProjectionMatrix()
        {
            Matrix4x4 viewMatrix = targetCamera.worldToCameraMatrix;
            Matrix4x4 projectionMatrix = GL.GetGPUProjectionMatrix(targetCamera.projectionMatrix, false);
            return projectionMatrix * viewMatrix;
        }

        private float GetReprojectionModeValue()
        {
            if (realDepthReprojectionEnabled)
            {
                return 3f;
            }

            if (matrixReprojectionEnabled)
            {
                return 2f;
            }

            return simpleReprojectionEnabled ? 1f : 0f;
        }

        private string GetReprojectionModeLabel()
        {
            if (realDepthReprojectionEnabled)
            {
                return "Real Depth";
            }

            if (matrixReprojectionEnabled)
            {
                return "Matrix";
            }

            if (simpleReprojectionEnabled)
            {
                return "Simple Offset";
            }

            return "Off";
        }

        private void ReleaseTexture(ref RenderTexture texture)
        {
            if (texture == null)
            {
                return;
            }

            texture.Release();
            Destroy(texture);
            texture = null;
        }

        private void ClearTexture(RenderTexture texture)
        {
            if (texture == null)
            {
                return;
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = previous;
        }

        private IEnumerator UpdateHistoryAtEndOfFrame()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();

                if (!temporalAccumulationEnabled || currentMode == RenderingMode.Native || lowResolutionTexture == null || historyWriteTexture == null || upscaleMaterial == null)
                {
                    continue;
                }

                // Naive temporal accumulation works by blending the current frame with a stored
                // history buffer, then saving that blended result for the next frame. This can
                // stabilize shimmering when the camera is still, but without motion vectors or
                // reprojection it will smear and ghost as soon as the scene moves.
                UpdatePresentationMaterial();
                Graphics.Blit(Texture2D.blackTexture, historyWriteTexture, upscaleMaterial);
                SwapHistoryTextures();
                hasHistoryFrame = true;
                previousViewProjectionMatrix = GetCurrentViewProjectionMatrix();
            }
        }

        private void SwapHistoryTextures()
        {
            RenderTexture previousRead = historyReadTexture;
            historyReadTexture = historyWriteTexture;
            historyWriteTexture = previousRead;
        }

        private void EnsureOverlayExists()
        {
            if (overlayCanvas != null && debugOverlay != null)
            {
                return;
            }

            Transform existingCanvasTransform = transform.Find("RenderingOverlayCanvas");
            if (existingCanvasTransform != null)
            {
                overlayCanvas = existingCanvasTransform.GetComponent<Canvas>();
            }

            if (overlayCanvas == null)
            {
                GameObject canvasObject = new GameObject("RenderingOverlayCanvas");
                canvasObject.transform.SetParent(transform, false);

                overlayCanvas = canvasObject.AddComponent<Canvas>();
                overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                overlayCanvas.sortingOrder = 1000;
            }

            Transform debugTransform = overlayCanvas.transform.Find("RenderingDebugOverlay");
            GameObject debugObject = debugTransform != null ? debugTransform.gameObject : new GameObject("RenderingDebugOverlay");
            if (debugTransform == null)
            {
                debugObject.transform.SetParent(overlayCanvas.transform, false);
            }

            // The old stacking artifact came from drawing UI over a screen that was not being
            // refreshed reliably, and duplicate overlay objects would make that even worse.
            // Reusing the same canvas, text, and shadow components keeps the overlay updating
            // in place instead of spawning extra labels on top of each other.
            RectTransform debugRect = debugObject.GetComponent<RectTransform>();
            if (debugRect == null)
            {
                debugRect = debugObject.AddComponent<RectTransform>();
            }

            debugRect.anchorMin = new Vector2(0f, 1f);
            debugRect.anchorMax = new Vector2(0f, 1f);
            debugRect.pivot = new Vector2(0f, 1f);
            debugRect.anchoredPosition = new Vector2(16f, -16f);
            debugRect.sizeDelta = new Vector2(700f, 390f);

            Text debugText = debugObject.GetComponent<Text>();
            if (debugText == null)
            {
                debugText = debugObject.AddComponent<Text>();
            }

            Font debugFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (debugFont == null)
            {
                debugFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }

            debugText.font = debugFont;
            debugText.fontSize = 18;
            debugText.alignment = TextAnchor.UpperLeft;
            debugText.horizontalOverflow = HorizontalWrapMode.Overflow;
            debugText.verticalOverflow = VerticalWrapMode.Overflow;
            debugText.color = Color.white;
            debugText.raycastTarget = false;

            Shadow shadow = debugObject.GetComponent<Shadow>();
            if (shadow == null)
            {
                shadow = debugObject.AddComponent<Shadow>();
            }

            shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
            shadow.effectDistance = new Vector2(1f, -1f);

            debugOverlay = debugObject.GetComponent<RenderingDebugOverlay>();
            if (debugOverlay == null)
            {
                debugOverlay = debugObject.AddComponent<RenderingDebugOverlay>();
            }

            debugOverlay.Initialize(this, debugText);
        }

        private void EnsurePresentationObjects()
        {
            if (presentationCamera != null && presentationQuad != null)
            {
                return;
            }

            Transform cameraTransform = transform;
            Transform presentationRoot = cameraTransform.Find("PresentationCamera");

            if (presentationRoot == null)
            {
                GameObject presentationCameraObject = new GameObject("PresentationCamera");
                presentationRoot = presentationCameraObject.transform;
                presentationRoot.SetParent(cameraTransform, false);
            }

            presentationCamera = presentationRoot.GetComponent<Camera>();
            if (presentationCamera == null)
            {
                presentationCamera = presentationRoot.gameObject.AddComponent<Camera>();
            }

            presentationCamera.orthographic = true;
            presentationCamera.orthographicSize = 1f;
            presentationCamera.clearFlags = CameraClearFlags.SolidColor;
            presentationCamera.backgroundColor = Color.black;
            presentationCamera.cullingMask = 1 << PresentationLayer;
            presentationCamera.depth = targetCamera.depth + 1f;
            presentationCamera.nearClipPlane = 0.01f;
            presentationCamera.farClipPlane = 10f;
            presentationCamera.enabled = false;

            Transform quadTransform = presentationRoot.Find("PresentationQuad");
            if (quadTransform == null)
            {
                GameObject quadObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quadObject.name = "PresentationQuad";
                quadTransform = quadObject.transform;
                quadTransform.SetParent(presentationRoot, false);

                Collider quadCollider = quadObject.GetComponent<Collider>();
                if (quadCollider != null)
                {
                    Destroy(quadCollider);
                }
            }

            presentationQuad = quadTransform;
            presentationQuad.gameObject.layer = PresentationLayer;
            presentationQuad.localPosition = new Vector3(0f, 0f, 1f);
            presentationQuad.localRotation = Quaternion.identity;
            presentationQuad.gameObject.SetActive(false);
            UpdatePresentationQuadScale();

            MeshRenderer quadRenderer = presentationQuad.GetComponent<MeshRenderer>();
            quadRenderer.sharedMaterial = upscaleMaterial;
            quadRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            quadRenderer.receiveShadows = false;
            quadRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            quadRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        private void UpdatePresentationQuadScale()
        {
            if (presentationQuad == null)
            {
                return;
            }

            float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 1f;
            presentationQuad.localScale = new Vector3(2f * aspect, 2f, 1f);
        }
    }
}
