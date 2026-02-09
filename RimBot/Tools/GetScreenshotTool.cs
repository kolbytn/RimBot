using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Verse;

namespace RimBot.Tools
{
    public class GetScreenshotTool : ITool
    {
        public string Name => "get_screenshot";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Capture a screenshot of the area around you. Returns a top-down view image. " +
                    "The size parameter controls how many tiles are visible in each direction from center " +
                    "(e.g. size=24 shows a 48x48 tile area). Your position is at the center of the image.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{\"size\":{\"type\":\"integer\"," +
                    "\"description\":\"Tiles visible in each direction from center (8=close 16x16, 24=standard 48x48, 48=wide 96x96)\"," +
                    "\"minimum\":8,\"maximum\":48}},\"required\":[]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            int size = 24;
            if (call.Arguments != null && call.Arguments["size"] != null)
            {
                size = call.Arguments["size"].Value<int>();
                size = Math.Max(8, Math.Min(48, size));
            }

            var requests = new List<ScreenshotCapture.CaptureRequest>
            {
                new ScreenshotCapture.CaptureRequest
                {
                    CenterCell = context.PawnPosition,
                    CameraSize = size,
                    PixelSize = 512
                }
            };

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] get_screenshot(size=" + size + ")");

            ScreenshotCapture.StartBatchCapture(requests, results =>
            {
                var base64 = results[0];
                if (base64 != null)
                {
                    onComplete(new ToolResult
                    {
                        ToolCallId = call.Id,
                        ToolName = Name,
                        Success = true,
                        Content = "Screenshot captured. The image shows a " + (size * 2) + "x" + (size * 2) +
                            " tile area centered on your position. You are at the center. " +
                            "+X is east (right), +Z is north (up). Coordinates relative to you at (0,0).",
                        ImageBase64 = base64,
                        ImageMediaType = "image/png"
                    });
                }
                else
                {
                    onComplete(new ToolResult
                    {
                        ToolCallId = call.Id,
                        ToolName = Name,
                        Success = false,
                        Content = "Screenshot capture failed."
                    });
                }
            });
        }
    }
}
