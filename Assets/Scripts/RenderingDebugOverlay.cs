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
                $"Mode: {GetModeName(controller.CurrentMode)}\n" +
                $"Render Resolution: {controller.RenderWidth}x{controller.RenderHeight}\n" +
                $"Screen Resolution: {controller.ScreenWidth}x{controller.ScreenHeight}\n" +
                "Keys: 1 Native | 2 Half | 3 Quarter";
        }

        private static string GetModeName(RenderingController.RenderingMode mode)
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
    }
}
