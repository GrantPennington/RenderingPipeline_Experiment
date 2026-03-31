using UnityEngine;
using UnityEngine.UI;

namespace RenderingSandbox
{
    public class RenderingDebugOverlay : MonoBehaviour
    {
        private RenderingController controller;
        private Text label;

        public void Initialize(RenderingController renderingController, Text targetLabel)
        {
            controller = renderingController;
            label = targetLabel;
            Refresh();
        }

        private void LateUpdate()
        {
            Refresh();
        }

        public void Refresh()
        {
            if (controller == null || label == null)
            {
                return;
            }

            label.text =
                $"Render Scale: {GetRenderModeName(controller.CurrentMode)}\n" +
                $"Upscale: {GetUpscaleModeName(controller.CurrentUpscaleMode)}\n" +
                $"Render Resolution: {controller.RenderWidth}x{controller.RenderHeight}\n" +
                $"Screen Resolution: {controller.ScreenWidth}x{controller.ScreenHeight}\n" +
                "Keys: 1/2/3 Scale | Q/W/E Upscale";
        }

        private static string GetRenderModeName(RenderingController.RenderingMode mode)
        {
            switch (mode)
            {
                case RenderingController.RenderingMode.HalfResolution:
                    return "Half Resolution";
                case RenderingController.RenderingMode.QuarterResolution:
                    return "Quarter Resolution";
                default:
                    return "Native";
            }
        }

        private static string GetUpscaleModeName(RenderingController.UpscaleMode mode)
        {
            switch (mode)
            {
                case RenderingController.UpscaleMode.NearestNeighbor:
                    return "Nearest Neighbor";
                case RenderingController.UpscaleMode.SharpenedBilinear:
                    return "Sharpened Bilinear";
                default:
                    return "Bilinear";
            }
        }
    }
}
