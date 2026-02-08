using System.Collections.Generic;
using System.Linq;
using RimBot.Models;
using UnityEngine;
using Verse;

namespace RimBot
{
    public class SelectionTestWindow : Window
    {
        private Vector2 detailScroll;

        public override Vector2 InitialSize => new Vector2(850f, 600f);

        public SelectionTestWindow()
        {
            doCloseButton = false;
            doCloseX = true;
            draggable = true;
            resizeable = true;
            forcePause = false;
            absorbInputAroundWindow = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var y = inRect.y;

            // Title
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 36f), "Selection Test Results");
            y += 40f;

            // Status line
            Text.Font = GameFont.Small;
            var status = SelectionTest.IsRunning ? "Running" : "Stopped";
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f),
                "Status: " + status + "  |  Cycle: " + SelectionTest.CurrentCycle
                + "  |  Results: " + SelectionTest.Results.Count);
            y += 28f;

            // Summary table
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "Summary (per colonist)");
            y += 24f;

            DrawSummaryTable(new Rect(inRect.x, y, inRect.width, 130f));
            y += 134f;

            // Divider
            Widgets.DrawLineHorizontal(inRect.x, y, inRect.width);
            y += 8f;

            // Detailed results header
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "Detailed Results");
            y += 24f;

            // Detailed results scrollable area
            float detailHeight = inRect.yMax - y - 36f;
            DrawDetailTable(new Rect(inRect.x, y, inRect.width, detailHeight));
            y += detailHeight + 4f;

            // Clear button
            if (Widgets.ButtonText(new Rect(inRect.x, y, 140f, 28f), "Clear Results"))
            {
                SelectionTest.ClearResults();
            }
        }

        private void DrawSummaryTable(Rect rect)
        {
            var results = SelectionTest.Results;
            if (results.Count == 0)
            {
                Widgets.Label(new Rect(rect.x, rect.y, rect.width, 24f), "No results yet.");
                return;
            }

            // Aggregate by pawn label
            var groups = new Dictionary<string, SummaryRow>();
            foreach (var r in results)
            {
                SummaryRow row;
                if (!groups.TryGetValue(r.PawnLabel, out row))
                {
                    row = new SummaryRow { PawnLabel = r.PawnLabel, Provider = r.Provider, Mode = r.Mode };
                    groups[r.PawnLabel] = row;
                }
                row.Cycles++;
                row.TotalExpected += r.Expected;
                row.TotalMatched += r.Matched;
                row.TotalError += r.AvgError * r.Matched;
                row.TotalMatchedForError += r.Matched;
                row.TotalExtra += r.Extra;
            }

            // Header
            var colWidths = new float[] { 110f, 80f, 70f, 55f, 110f, 55f, 75f, 75f };
            var headers = new string[] { "Colonist", "Provider", "Mode", "Cycles", "Matched/Exp", "Rate", "Avg Err", "Avg Extra" };
            float x = rect.x;
            float headerY = rect.y;
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            for (int i = 0; i < headers.Length; i++)
            {
                Widgets.Label(new Rect(x, headerY, colWidths[i], 22f), headers[i]);
                x += colWidths[i];
            }
            GUI.color = Color.white;

            float rowY = rect.y + 24f;
            foreach (var kvp in groups)
            {
                var row = kvp.Value;
                float avgErr = row.TotalMatchedForError > 0 ? (float)(row.TotalError / row.TotalMatchedForError) : 0;
                float rate = row.TotalExpected > 0 ? (float)row.TotalMatched / row.TotalExpected : 0;
                float avgExtra = row.Cycles > 0 ? (float)row.TotalExtra / row.Cycles : 0;

                x = rect.x;
                Widgets.Label(new Rect(x, rowY, colWidths[0], 22f), row.PawnLabel); x += colWidths[0];
                Widgets.Label(new Rect(x, rowY, colWidths[1], 22f), row.Provider.ToString()); x += colWidths[1];
                Widgets.Label(new Rect(x, rowY, colWidths[2], 22f), row.Mode.ToString()); x += colWidths[2];
                Widgets.Label(new Rect(x, rowY, colWidths[3], 22f), row.Cycles.ToString()); x += colWidths[3];
                Widgets.Label(new Rect(x, rowY, colWidths[4], 22f), row.TotalMatched + "/" + row.TotalExpected); x += colWidths[4];
                Widgets.Label(new Rect(x, rowY, colWidths[5], 22f), (rate * 100).ToString("F0") + "%"); x += colWidths[5];
                Widgets.Label(new Rect(x, rowY, colWidths[6], 22f), avgErr.ToString("F1")); x += colWidths[6];
                Widgets.Label(new Rect(x, rowY, colWidths[7], 22f), avgExtra.ToString("F1"));
                rowY += 22f;
            }
        }

        private void DrawDetailTable(Rect rect)
        {
            var results = SelectionTest.Results;
            if (results.Count == 0)
            {
                Widgets.Label(new Rect(rect.x, rect.y, rect.width, 24f), "No results yet.");
                return;
            }

            // Header
            var colWidths = new float[] { 50f, 110f, 120f, 70f, 60f, 60f, 65f, 65f };
            var headers = new string[] { "Cycle", "Colonist", "Object", "Expected", "Matched", "Error", "Extra", "Reported" };

            float headerY = rect.y;
            float x = rect.x;
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            for (int i = 0; i < headers.Length; i++)
            {
                Widgets.Label(new Rect(x, headerY, colWidths[i], 22f), headers[i]);
                x += colWidths[i];
            }
            GUI.color = Color.white;

            float scrollAreaY = rect.y + 24f;
            float scrollAreaHeight = rect.height - 24f;
            float rowHeight = 22f;
            float totalContentHeight = results.Count * rowHeight;

            var scrollRect = new Rect(rect.x, scrollAreaY, rect.width, scrollAreaHeight);
            var viewRect = new Rect(0f, 0f, rect.width - 16f, totalContentHeight);

            Widgets.BeginScrollView(scrollRect, ref detailScroll, viewRect);

            float rowY = 0f;
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                x = 0f;
                Widgets.Label(new Rect(x, rowY, colWidths[0], rowHeight), r.Cycle.ToString()); x += colWidths[0];
                Widgets.Label(new Rect(x, rowY, colWidths[1], rowHeight), r.PawnLabel); x += colWidths[1];
                Widgets.Label(new Rect(x, rowY, colWidths[2], rowHeight), r.ObjectType ?? "?"); x += colWidths[2];
                Widgets.Label(new Rect(x, rowY, colWidths[3], rowHeight), r.Expected.ToString()); x += colWidths[3];
                Widgets.Label(new Rect(x, rowY, colWidths[4], rowHeight), r.Matched.ToString()); x += colWidths[4];
                Widgets.Label(new Rect(x, rowY, colWidths[5], rowHeight), r.AvgError.ToString("F1")); x += colWidths[5];
                Widgets.Label(new Rect(x, rowY, colWidths[6], rowHeight), r.Extra.ToString()); x += colWidths[6];
                Widgets.Label(new Rect(x, rowY, colWidths[7], rowHeight), r.Reported.ToString());
                rowY += rowHeight;
            }

            Widgets.EndScrollView();
        }

        private class SummaryRow
        {
            public string PawnLabel;
            public LLMProviderType Provider;
            public MapSelectionMode Mode;
            public int Cycles;
            public int TotalExpected;
            public int TotalMatched;
            public double TotalError;
            public int TotalMatchedForError;
            public int TotalExtra;
        }
    }
}
