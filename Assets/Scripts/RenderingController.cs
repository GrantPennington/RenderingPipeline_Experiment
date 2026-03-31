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

        [SerializeField] private RenderingMode startingMode = RenderingMode.Native;
        [SerializeField] private KeyCode nativeModeKey = KeyCode.Alpha1;
        [SerializeField] private KeyCode halfResolutionKey = KeyCode.Alpha2;
        [SerializeField] private KeyCode quarterResolutionKey = KeyCode.Alpha3;

        private Camera targetCamera;
        private RenderTexture lowResolutionTexture;
        private Canvas overlayCanvas;
        private RawImage upscaleImage;
        private RenderingDebugOverlay debugOverlay;

        private RenderingMode currentMode;
        private int currentRenderWidth;
        private int currentRenderHeight;
        private int lastScreenWidth;
        private int lastScreenHeight;

        public RenderingMode CurrentMode => currentMode;
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
            EnsureOverlayExists();
            SetRenderingMode(startingMode, true);
        }

        private void Update()
        {
            HandleModeInput();

            if (Screen.width != lastScreenWidth || Screen.height != lastScreenHeight)
            {
                ApplyCurrentMode(forceRecreateTexture: true);
            }
        }

        private void OnDisable()
        {
            ResetToNativeOutput();
        }

        private void OnDestroy()
        {
            ReleaseRenderTexture();

            if (overlayCanvas != null)
            {
                Destroy(overlayCanvas.gameObject);
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
                CreateLowResolutionTexture(desiredWidth, desiredHeight);
            }

            // A RenderTexture is an off-screen color buffer. The camera renders into it
            // instead of directly into the back buffer so we can inspect or post-process it.
            targetCamera.targetTexture = lowResolutionTexture;

            if (upscaleImage != null)
            {
                // The "upscale" step here is simply drawing the smaller texture across the
                // whole screen. Bilinear filtering blends neighboring pixels together, which
                // keeps the image smooth but makes it blurrier than native rendering.
                upscaleImage.texture = lowResolutionTexture;
                upscaleImage.enabled = true;
            }

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
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            lowResolutionTexture.Create();
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

            if (upscaleImage != null && upscaleImage.texture == lowResolutionTexture)
            {
                upscaleImage.texture = null;
            }

            lowResolutionTexture.Release();
            Destroy(lowResolutionTexture);
            lowResolutionTexture = null;
        }

        private void ResetToNativeOutput()
        {
            targetCamera.targetTexture = null;
            currentRenderWidth = Screen.width;
            currentRenderHeight = Screen.height;

            if (upscaleImage != null)
            {
                upscaleImage.texture = null;
                upscaleImage.enabled = false;
            }

            ReleaseRenderTexture();
        }

        private void EnsureOverlayExists()
        {
            if (overlayCanvas != null)
            {
                return;
            }

            GameObject canvasObject = new GameObject("RenderingOverlayCanvas");
            canvasObject.transform.SetParent(transform, false);

            overlayCanvas = canvasObject.AddComponent<Canvas>();
            overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            overlayCanvas.sortingOrder = 1000;

            GameObject upscaleObject = new GameObject("UpscaledScene");
            upscaleObject.transform.SetParent(canvasObject.transform, false);

            RectTransform upscaleRect = upscaleObject.AddComponent<RectTransform>();
            upscaleRect.anchorMin = Vector2.zero;
            upscaleRect.anchorMax = Vector2.one;
            upscaleRect.offsetMin = Vector2.zero;
            upscaleRect.offsetMax = Vector2.zero;

            upscaleImage = upscaleObject.AddComponent<RawImage>();
            upscaleImage.color = Color.white;
            upscaleImage.raycastTarget = false;
            upscaleImage.enabled = false;

            GameObject debugObject = new GameObject("RenderingDebugOverlay");
            debugObject.transform.SetParent(canvasObject.transform, false);

            RectTransform debugRect = debugObject.AddComponent<RectTransform>();
            debugRect.anchorMin = new Vector2(0f, 1f);
            debugRect.anchorMax = new Vector2(0f, 1f);
            debugRect.pivot = new Vector2(0f, 1f);
            debugRect.anchoredPosition = new Vector2(16f, -16f);
            debugRect.sizeDelta = new Vector2(320f, 90f);

            Text debugText = debugObject.AddComponent<Text>();
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

            Shadow shadow = debugObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
            shadow.effectDistance = new Vector2(1f, -1f);

            debugOverlay = debugObject.AddComponent<RenderingDebugOverlay>();
            debugOverlay.Initialize(this, debugText);
        }
    }
}
