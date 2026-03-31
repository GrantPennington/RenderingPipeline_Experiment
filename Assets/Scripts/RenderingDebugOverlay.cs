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

            if (controller.CurrentOverlayDetailMode == RenderingController.OverlayDetailMode.Minimal)
            {
                label.text =
                    $"Preset: {controller.CurrentPresetName}\n" +
                    $"Render Scale: {GetRenderModeName(controller.CurrentMode)}\n" +
                    $"Upscale: {GetUpscaleModeName(controller.CurrentUpscaleMode)}\n" +
                    $"Temporal: {(controller.TemporalAccumulationEnabled ? "On" : "Off")}\n" +
                    $"Debug View: {controller.CurrentDebugVisualizationModeLabel}\n" +
                    "Keys: V View | B Overlay | F1-F6 Presets";
                return;
            }

            label.text =
                $"Preset: {controller.CurrentPresetName}\n" +
                $"Render Scale: {GetRenderModeName(controller.CurrentMode)}\n" +
                $"Upscale: {GetUpscaleModeName(controller.CurrentUpscaleMode)}\n" +
                $"Temporal: {(controller.TemporalAccumulationEnabled ? "On" : "Off")}\n" +
                $"History Clamping: {(controller.HistoryClampingEnabled ? "On" : "Off")}\n" +
                $"Debug View: {controller.CurrentDebugVisualizationModeLabel}\n" +
                $"Reprojection Mode: {controller.CurrentReprojectionModeLabel}\n" +
                $"Reprojection: {(controller.SimpleReprojectionEnabled ? "On" : "Off")}\n" +
                $"Matrix Reprojection: {(controller.MatrixReprojectionEnabled ? "On" : "Off")}\n" +
                $"Base History Weight: {controller.HistoryWeight:0.00}\n" +
                $"Effective History Weight: {controller.EffectiveHistoryWeight:0.00}\n" +
                $"Camera Motion: {controller.CameraMotionAmount:0.000}\n" +
                $"History UV Offset: {controller.HistoryUvOffset.x:0.000}, {controller.HistoryUvOffset.y:0.000}\n" +
                $"Jitter: {(controller.JitterEnabled ? "On" : "Off")}\n" +
                $"Jitter Offset: {controller.CurrentJitterOffsetPixels.x:0.00}, {controller.CurrentJitterOffsetPixels.y:0.00} px\n" +
                $"Auto Motion: {(controller.AutoCameraMotionEnabled ? "On" : "Off")}\n" +
                $"Render Resolution: {controller.RenderWidth}x{controller.RenderHeight}\n" +
                $"Screen Resolution: {controller.ScreenWidth}x{controller.ScreenHeight}\n" +
                "Keys: 1/2/3 Scale | Q/W/E Upscale | T Temporal | P Clamp | U Reprojection | I Matrix Reprojection | O Jitter | V View | B Overlay | [ ] Weight | Y Auto Motion | F1-F6 Presets";
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
