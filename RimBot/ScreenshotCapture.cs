using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimBot
{
    public static class ScreenshotCapture
    {
        public struct CaptureRequest
        {
            public IntVec3 CenterCell;
            public float CameraSize;
            public int PixelSize;
        }

        public static void StartBatchCapture(List<CaptureRequest> requests, Action<string[]> onComplete)
        {
            Find.CameraDriver.StartCoroutine(CaptureCoroutine(requests, onComplete));
        }

        private static IEnumerator CaptureCoroutine(List<CaptureRequest> requests, Action<string[]> onComplete)
        {
            var results = new string[requests.Count];

            Camera camera = Find.Camera;
            CameraDriver camDriver = Find.CameraDriver;

            // Save camera state
            Vector3 originalPos = camera.transform.position;
            float originalOrthoSize = camera.orthographicSize;
            float originalFarClip = camera.farClipPlane;
            RenderTexture originalTarget = camera.targetTexture;

            // Disable CameraDriver so it doesn't fight our camera changes
            camDriver.enabled = false;

            // Spoof the ViewRect to cover the entire map.
            // This tricks RimWorld's MapDrawer into treating all sections as "visible",
            // so it regenerates meshes and submits draw calls for the whole map.
            var map = Find.CurrentMap;
            CellRect mapRect = new CellRect(0, 0, map.Size.x, map.Size.z);

            var traverse = Traverse.Create(camDriver);
            traverse.Field("lastViewRect").SetValue(mapRect);
            traverse.Field("lastViewRectGetFrame").SetValue(Time.frameCount);

            // Wait one frame — RimWorld's normal pipeline runs with the spoofed ViewRect,
            // regenerating all section meshes and drawing them into GPU memory.
            yield return new WaitForEndOfFrame();

            // Now render each colonist's view to a RenderTexture
            for (int i = 0; i < requests.Count; i++)
            {
                var req = requests[i];
                try
                {
                    Vector3 targetPos = req.CenterCell.ToVector3Shifted();
                    camera.transform.position = new Vector3(targetPos.x, originalPos.y, targetPos.z);
                    camera.orthographicSize = req.CameraSize;
                    camera.farClipPlane = originalPos.y + 6.5f;

                    var rt = RenderTexture.GetTemporary(req.PixelSize, req.PixelSize, 24);
                    camera.targetTexture = rt;
                    camera.Render();
                    camera.targetTexture = originalTarget;

                    RenderTexture.active = rt;
                    var tex = new Texture2D(req.PixelSize, req.PixelSize, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, req.PixelSize, req.PixelSize), 0, 0);
                    tex.Apply();
                    RenderTexture.active = null;
                    RenderTexture.ReleaseTemporary(rt);

                    byte[] png = tex.EncodeToPNG();
                    UnityEngine.Object.Destroy(tex);

                    results[i] = Convert.ToBase64String(png);
                }
                catch (Exception ex)
                {
                    Log.Error("[RimBot] Capture failed for request " + i + ": " + ex.Message);
                    results[i] = null;
                }
            }

            // Restore camera state
            camera.targetTexture = originalTarget;
            camera.transform.position = originalPos;
            camera.orthographicSize = originalOrthoSize;
            camera.farClipPlane = originalFarClip;
            camDriver.enabled = true;

            onComplete?.Invoke(results);
        }
    }
}
