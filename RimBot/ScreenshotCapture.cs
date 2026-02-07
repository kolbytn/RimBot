using System;
using UnityEngine;
using Verse;

namespace RimBot
{
    public static class ScreenshotCapture
    {
        private static bool captureRequested;
        private static int captureSize;
        private static Action<string> captureCallback;

        static ScreenshotCapture()
        {
            Camera.onPostRender += OnPostRender;
        }

        /// <summary>
        /// Requests a capture of the current camera view. The callback fires
        /// during the render phase of the same frame (from Camera.onPostRender),
        /// with the base64-encoded PNG or null on failure.
        /// </summary>
        public static void RequestCurrentViewCapture(int size, Action<string> callback)
        {
            captureRequested = true;
            captureSize = size;
            captureCallback = callback;
        }

        private static void OnPostRender(Camera cam)
        {
            if (!captureRequested)
                return;
            if (cam != Find.Camera)
                return;

            captureRequested = false;
            var callback = captureCallback;
            captureCallback = null;
            var size = captureSize;

            try
            {
                // The camera just finished rendering to the screen buffer.
                // Read the full viewport, then resize to the requested size.
                int w = cam.pixelWidth;
                int h = cam.pixelHeight;

                var screenTex = new Texture2D(w, h, TextureFormat.RGB24, false);
                screenTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                screenTex.Apply();

                // Blit into a square RenderTexture at the target resolution
                var rt = RenderTexture.GetTemporary(size, size);
                Graphics.Blit(screenTex, rt);
                UnityEngine.Object.Destroy(screenTex);

                RenderTexture.active = rt;
                var resized = new Texture2D(size, size, TextureFormat.RGB24, false);
                resized.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                resized.Apply();
                RenderTexture.active = null;
                RenderTexture.ReleaseTemporary(rt);

                var png = resized.EncodeToPNG();
                UnityEngine.Object.Destroy(resized);

                callback?.Invoke(Convert.ToBase64String(png));
            }
            catch (Exception ex)
            {
                Log.Error("[RimBot] Screenshot capture failed: " + ex.Message);
                callback?.Invoke(null);
            }
        }

        /// <summary>
        /// Captures an arbitrary map area. For future use — must be called during
        /// the render phase (e.g. from a camera callback), not from a tick.
        /// </summary>
        public static string CaptureArea(IntVec3 centerCell, float cameraSize = 24f, int pixelSize = 512)
        {
            try
            {
                var cam = Find.Camera;
                var originalTarget = cam.targetTexture;
                var originalPos = cam.transform.position;
                var originalOrthoSize = cam.orthographicSize;

                var worldPos = centerCell.ToVector3Shifted();
                cam.transform.position = new Vector3(worldPos.x, originalPos.y, worldPos.z);
                cam.orthographicSize = cameraSize;

                var rt = RenderTexture.GetTemporary(pixelSize, pixelSize, 24);
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = originalTarget;

                cam.transform.position = originalPos;
                cam.orthographicSize = originalOrthoSize;

                RenderTexture.active = rt;
                var tex = new Texture2D(pixelSize, pixelSize, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, pixelSize, pixelSize), 0, 0);
                tex.Apply();
                RenderTexture.active = null;

                var png = tex.EncodeToPNG();
                UnityEngine.Object.Destroy(tex);
                RenderTexture.ReleaseTemporary(rt);

                return Convert.ToBase64String(png);
            }
            catch (Exception ex)
            {
                Log.Error("[RimBot] Area capture failed: " + ex.Message);
                return null;
            }
        }
    }
}
