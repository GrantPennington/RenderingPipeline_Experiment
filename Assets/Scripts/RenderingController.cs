using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

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

        [Header("Startup")]
        [SerializeField] private RenderingMode startingMode = RenderingMode.Native;
        [SerializeField] private UpscaleMode startingUpscaleMode = UpscaleMode.Bilinear;

        [Header("Presentation")]
        [SerializeField] private Shader upscaleShader;
        [SerializeField, Range(0f, 1.5f)] private float sharpenStrength = 0.35f;

        private static readonly int SourceTextureId = Shader.PropertyToID("_SourceTexture");
        private static readonly int SourceTexelSizeId = Shader.PropertyToID("_SourceTexelSize");
        private static readonly int SharpenStrengthId = Shader.PropertyToID("_SharpenStrength");
        private const int PresentationLayer = 31;

        private Camera targetCamera;
        private RenderTexture lowResolutionTexture;
        private Camera presentationCamera;
        private Transform presentationQuad;
        private Canvas overlayCanvas;
        private RenderingDebugOverlay debugOverlay;
        private Material upscaleMaterial;
        private int originalCameraCullingMask;

        private RenderingMode currentMode;
        private UpscaleMode currentUpscaleMode;
        private int currentRenderWidth;
        private int currentRenderHeight;
        private int lastScreenWidth;
        private int lastScreenHeight;

        public RenderingMode CurrentMode => currentMode;
        public UpscaleMode CurrentUpscaleMode => currentUpscaleMode;
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
            EnsurePresentationMaterial();
            EnsurePresentationObjects();
            EnsureOverlayExists();
            SetUpscaleMode(startingUpscaleMode, true);
            SetRenderingMode(startingMode, true);
        }

        private void Update()
        {
            HandleModeInput();

            if (Screen.width != lastScreenWidth || Screen.height != lastScreenHeight)
            {
                UpdatePresentationQuadScale();
                ApplyCurrentMode(forceRecreateTexture: true);
            }
        }

        private void OnDisable()
        {
            targetCamera.cullingMask = originalCameraCullingMask;
            ResetToNativeOutput();
        }

        private void OnDestroy()
        {
            ReleaseRenderTexture();

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
            }
            else if (keyboard.digit2Key.wasPressedThisFrame || keyboard.numpad2Key.wasPressedThisFrame)
            {
                SetRenderingMode(RenderingMode.HalfResolution);
            }
            else if (keyboard.digit3Key.wasPressedThisFrame || keyboard.numpad3Key.wasPressedThisFrame)
            {
                SetRenderingMode(RenderingMode.QuarterResolution);
            }

            if (keyboard.qKey.wasPressedThisFrame)
            {
                SetUpscaleMode(UpscaleMode.NearestNeighbor);
            }
            else if (keyboard.wKey.wasPressedThisFrame)
            {
                SetUpscaleMode(UpscaleMode.Bilinear);
            }
            else if (keyboard.eKey.wasPressedThisFrame)
            {
                SetUpscaleMode(UpscaleMode.SharpenedBilinear);
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

        private void UpdatePresentationMaterial()
        {
            if (upscaleMaterial == null)
            {
                return;
            }

            if (lowResolutionTexture == null)
            {
                upscaleMaterial.SetTexture(SourceTextureId, Texture2D.blackTexture);
                upscaleMaterial.SetVector(SourceTexelSizeId, Vector4.zero);
                upscaleMaterial.SetFloat(SharpenStrengthId, 0f);
                return;
            }

            upscaleMaterial.SetTexture(SourceTextureId, lowResolutionTexture);
            upscaleMaterial.SetVector(
                SourceTexelSizeId,
                new Vector4(
                    1f / lowResolutionTexture.width,
                    1f / lowResolutionTexture.height,
                    lowResolutionTexture.width,
                    lowResolutionTexture.height));
            upscaleMaterial.SetFloat(SharpenStrengthId, currentUpscaleMode == UpscaleMode.SharpenedBilinear ? sharpenStrength : 0f);
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
            debugRect.sizeDelta = new Vector2(420f, 120f);

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
