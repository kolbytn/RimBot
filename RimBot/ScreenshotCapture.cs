using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace RimBot
{
    public static class ScreenshotCapture
    {
        public struct PawnLabel
        {
            public string Name;
            public IntVec3 Position;
        }

        public struct CaptureRequest
        {
            public IntVec3 CenterCell;
            public float CameraSize;
            public int PixelSize;
            public List<PawnLabel> Labels;
        }

        private static Font labelFont;
        private static Material bgMaterial;
        private static Material textMaterial;

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
                    if (req.Labels != null && req.Labels.Count > 0)
                        DrawLabels(req);

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
        private static void DrawLabels(CaptureRequest req)
        {
            if (labelFont == null)
                labelFont = Font.CreateDynamicFontFromOSFont("Arial", 16);

            // Collect all characters and request rasterization
            var allText = new System.Text.StringBuilder();
            foreach (var label in req.Labels)
                allText.Append(label.Name);
            labelFont.RequestCharactersInTexture(allText.ToString(), 16, FontStyle.Bold);

            // Create background material (solid color with alpha blending)
            if (bgMaterial == null)
            {
                bgMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
                bgMaterial.hideFlags = HideFlags.HideAndDontSave;
                bgMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                bgMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                bgMaterial.SetInt("_Cull", 0);
                bgMaterial.SetInt("_ZWrite", 0);
                bgMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
            }

            // Create text material from font material with depth test disabled
            if (textMaterial == null)
            {
                textMaterial = new Material(labelFont.material);
                textMaterial.hideFlags = HideFlags.HideAndDontSave;
                textMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
                textMaterial.SetInt("_ZWrite", 0);
            }
            // Keep font atlas texture in sync (Unity rebuilds it dynamically)
            textMaterial.mainTexture = labelFont.material.mainTexture;

            float viewSize = req.CameraSize * 2f;
            float size = req.PixelSize;
            Vector3 center = req.CenterCell.ToVector3Shifted();

            // Pre-compute label positions and widths
            var labelData = new List<LabelDrawData>();
            foreach (var label in req.Labels)
            {
                Vector3 worldPos = label.Position.ToVector3Shifted();
                float px = (worldPos.x - center.x) / viewSize * size + size / 2f;
                float py = (center.z - worldPos.z) / viewSize * size + size / 2f;

                int textWidth = 0;
                foreach (char c in label.Name)
                {
                    CharacterInfo ci;
                    if (labelFont.GetCharacterInfo(c, out ci, 16, FontStyle.Bold))
                        textWidth += ci.advance;
                }

                labelData.Add(new LabelDrawData
                {
                    Name = label.Name,
                    PixelX = px,
                    PixelY = py,
                    TextWidth = textWidth
                });
            }

            GL.PushMatrix();
            GL.LoadPixelMatrix(0, size, size, 0);

            // Pass 1: draw all background rectangles
            bgMaterial.SetPass(0);
            GL.Begin(GL.QUADS);
            foreach (var ld in labelData)
            {
                float bgX = ld.PixelX - ld.TextWidth / 2f - 3;
                float bgY = ld.PixelY - 22;
                float bgW = ld.TextWidth + 6;
                float bgH = 20;

                GL.Color(new Color(0f, 0f, 0f, 0.75f));
                GL.Vertex3(bgX, bgY, 0);
                GL.Vertex3(bgX + bgW, bgY, 0);
                GL.Vertex3(bgX + bgW, bgY + bgH, 0);
                GL.Vertex3(bgX, bgY + bgH, 0);
            }
            GL.End();

            // Pass 2: draw all text characters
            textMaterial.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(Color.white);
            foreach (var ld in labelData)
            {
                float penX = ld.PixelX - ld.TextWidth / 2f;
                float baselineY = ld.PixelY - 5;

                foreach (char c in ld.Name)
                {
                    CharacterInfo ci;
                    if (!labelFont.GetCharacterInfo(c, out ci, 16, FontStyle.Bold))
                        continue;

                    float x0 = penX + ci.minX;
                    float x1 = penX + ci.maxX;
                    float y0 = baselineY - ci.maxY;
                    float y1 = baselineY - ci.minY;

                    GL.TexCoord2(ci.uvTopLeft.x, ci.uvTopLeft.y);
                    GL.Vertex3(x0, y0, 0);
                    GL.TexCoord2(ci.uvTopRight.x, ci.uvTopRight.y);
                    GL.Vertex3(x1, y0, 0);
                    GL.TexCoord2(ci.uvBottomRight.x, ci.uvBottomRight.y);
                    GL.Vertex3(x1, y1, 0);
                    GL.TexCoord2(ci.uvBottomLeft.x, ci.uvBottomLeft.y);
                    GL.Vertex3(x0, y1, 0);

                    penX += ci.advance;
                }
            }
            GL.End();

            GL.PopMatrix();
        }

        private struct LabelDrawData
        {
            public string Name;
            public float PixelX;
            public float PixelY;
            public int TextWidth;
        }
    }
}
