using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimBot
{
    public class ITab_RimBotHistory : ITab
    {
        private Vector2 scrollPos;
        private readonly HashSet<int> expandedEntries = new HashSet<int>();
        private const float Margin = 10f;
        private const float EntrySpacing = 4f;
        private const float ThumbnailSize = 200f;

        public ITab_RimBotHistory()
        {
            size = new Vector2(460f, 550f);
            labelKey = "RimBot";
        }

        public override bool IsVisible
        {
            get
            {
                var pawn = SelPawn;
                return pawn != null && BrainManager.GetBrain(pawn.thingIDNumber) != null;
            }
        }

        protected override void FillTab()
        {
            var pawn = SelPawn;
            if (pawn == null)
                return;

            var brain = BrainManager.GetBrain(pawn.thingIDNumber);
            if (brain == null)
                return;

            var outerRect = new Rect(0f, 0f, size.x, size.y).ContractedBy(Margin);
            float y = outerRect.y;

            // Header
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(outerRect.x, y, outerRect.width, 32f),
                "RimBot History - " + pawn.LabelShort);
            y += 34f;

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            Widgets.Label(new Rect(outerRect.x, y, outerRect.width, 20f),
                brain.Provider + " / " + brain.Model + "  |  " + brain.History.Count + " entries");
            GUI.color = Color.white;
            y += 22f;

            // Divider
            Widgets.DrawLineHorizontal(outerRect.x, y, outerRect.width);
            y += 4f;

            // Scrollable history list
            Text.Font = GameFont.Small;
            var history = brain.History;
            float scrollAreaHeight = outerRect.yMax - y;

            // Calculate total content height
            float totalHeight = 0f;
            for (int i = 0; i < history.Count; i++)
            {
                totalHeight += CalcEntryHeight(history[i], i, outerRect.width - 16f);
                totalHeight += EntrySpacing;
            }

            var scrollOuterRect = new Rect(outerRect.x, y, outerRect.width, scrollAreaHeight);
            var scrollViewRect = new Rect(0f, 0f, outerRect.width - 16f, totalHeight);

            Widgets.BeginScrollView(scrollOuterRect, ref scrollPos, scrollViewRect);

            float entryY = 0f;
            for (int i = 0; i < history.Count; i++)
            {
                var entry = history[i];
                float entryWidth = outerRect.width - 16f;
                float entryHeight = CalcEntryHeight(entry, i, entryWidth);

                DrawEntry(entry, i, new Rect(0f, entryY, entryWidth, entryHeight));
                entryY += entryHeight + EntrySpacing;
            }

            Widgets.EndScrollView();
        }

        private float CalcEntryHeight(HistoryEntry entry, int index, float width)
        {
            float height = 28f; // collapsed header row

            if (!expandedEntries.Contains(index))
                return height;

            float textWidth = width - 12f;

            // Token info line
            height += 18f;

            // Screenshot thumbnail
            if (!string.IsNullOrEmpty(entry.ScreenshotBase64))
                height += ThumbnailSize + 4f;

            // System prompt (only for first agent iteration or non-agent)
            if (!string.IsNullOrEmpty(entry.SystemPrompt))
            {
                height += 18f; // label
                height += Text.CalcHeight(entry.SystemPrompt, textWidth) + 4f;
            }

            // Thinking text
            if (!string.IsNullOrEmpty(entry.ThinkingText))
            {
                height += 18f; // label
                height += Text.CalcHeight(entry.ThinkingText, textWidth) + 4f;
            }

            // User query
            if (!string.IsNullOrEmpty(entry.UserQuery))
            {
                height += 18f; // label
                height += Text.CalcHeight(entry.UserQuery, textWidth) + 4f;
            }

            // Response
            if (!string.IsNullOrEmpty(entry.ResponseText))
            {
                height += 18f; // label
                height += Text.CalcHeight(entry.ResponseText, textWidth) + 4f;
            }

            // Tool calls
            if (entry.ToolCalls != null && entry.ToolCalls.Count > 0)
            {
                height += 18f; // "Tool Calls:" label
                foreach (var tc in entry.ToolCalls)
                {
                    string callText = tc.Name + "(" + tc.ArgumentsJson + ")";
                    height += Text.CalcHeight(callText, textWidth) + 2f;
                }
                height += 4f;
            }

            // Tool results
            if (entry.ToolResults != null && entry.ToolResults.Count > 0)
            {
                height += 18f; // "Tool Results:" label
                foreach (var tr in entry.ToolResults)
                {
                    string resultText = (tr.Success ? "[OK] " : "[FAIL] ") + tr.Content;
                    if (tr.HasImage) resultText += " [+image]";
                    height += Text.CalcHeight(resultText, textWidth) + 2f;
                }
                height += 4f;
            }

            height += 4f; // bottom padding
            return height;
        }

        private void DrawEntry(HistoryEntry entry, int index, Rect rect)
        {
            bool expanded = expandedEntries.Contains(index);

            // Background tint
            Color bgColor = entry.Success
                ? new Color(0.2f, 0.35f, 0.2f, 0.3f)
                : new Color(0.4f, 0.15f, 0.15f, 0.3f);
            Widgets.DrawBoxSolid(rect, bgColor);

            // Header row (always visible)
            var headerRect = new Rect(rect.x + 4f, rect.y + 2f, rect.width - 8f, 24f);

            // Game time
            int ticks = entry.GameTick;
            int day = ticks / 60000 + 1;
            int hour = (ticks % 60000) / 2500;

            string headerText = "Day " + day + " " + hour + "h | " + entry.Mode;
            if (entry.AgentIteration.HasValue)
                headerText += " (iter " + entry.AgentIteration.Value + ")";
            headerText += " | " + entry.Provider + "/" + entry.ModelName;
            if (!entry.Success)
                headerText += " [FAILED]";

            Text.Font = GameFont.Small;
            Widgets.Label(headerRect, headerText);

            // Click to toggle expand
            if (Widgets.ButtonInvisible(new Rect(rect.x, rect.y, rect.width, 28f)))
            {
                if (expanded)
                    expandedEntries.Remove(index);
                else
                    expandedEntries.Add(index);
            }

            if (!expanded)
                return;

            // Expanded content
            float cy = rect.y + 28f;
            float textWidth = rect.width - 12f;
            float textX = rect.x + 6f;

            // Token info line
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.6f, 0.6f, 0.8f);
            string tokenInfo = "Tokens: " + entry.InputTokens + " in / " + entry.OutputTokens + " out";
            if (entry.CacheReadTokens > 0)
                tokenInfo += " / " + entry.CacheReadTokens + " cache";
            if (entry.ReasoningTokens > 0)
                tokenInfo += " / " + entry.ReasoningTokens + " reasoning";
            Widgets.Label(new Rect(textX, cy, textWidth, 18f), tokenInfo);
            GUI.color = Color.white;
            cy += 18f;

            // Screenshot thumbnail
            if (!string.IsNullOrEmpty(entry.ScreenshotBase64))
            {
                var tex = entry.GetTexture();
                if (tex != null)
                {
                    GUI.DrawTexture(new Rect(textX, cy, ThumbnailSize, ThumbnailSize), tex, ScaleMode.ScaleToFit);
                }
                cy += ThumbnailSize + 4f;
            }

            // System prompt
            if (!string.IsNullOrEmpty(entry.SystemPrompt))
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                Widgets.Label(new Rect(textX, cy, textWidth, 18f), "System Prompt:");
                cy += 18f;
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                float sysH = Text.CalcHeight(entry.SystemPrompt, textWidth);
                Widgets.Label(new Rect(textX, cy, textWidth, sysH), entry.SystemPrompt);
                cy += sysH + 4f;
            }

            // Thinking text
            if (!string.IsNullOrEmpty(entry.ThinkingText))
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.5f, 0.8f, 0.9f);
                Widgets.Label(new Rect(textX, cy, textWidth, 18f), "Thinking:");
                cy += 18f;
                Text.Font = GameFont.Small;
                float thinkH = Text.CalcHeight(entry.ThinkingText, textWidth);
                Widgets.Label(new Rect(textX, cy, textWidth, thinkH), entry.ThinkingText);
                GUI.color = Color.white;
                cy += thinkH + 4f;
            }

            // User query
            if (!string.IsNullOrEmpty(entry.UserQuery))
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                Widgets.Label(new Rect(textX, cy, textWidth, 18f), "User Query:");
                cy += 18f;
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                float queryH = Text.CalcHeight(entry.UserQuery, textWidth);
                Widgets.Label(new Rect(textX, cy, textWidth, queryH), entry.UserQuery);
                cy += queryH + 4f;
            }

            // Response
            if (!string.IsNullOrEmpty(entry.ResponseText))
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                Widgets.Label(new Rect(textX, cy, textWidth, 18f), "Response:");
                cy += 18f;
                GUI.color = entry.Success ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.5f, 0.5f);
                Text.Font = GameFont.Small;
                float respH = Text.CalcHeight(entry.ResponseText, textWidth);
                Widgets.Label(new Rect(textX, cy, textWidth, respH), entry.ResponseText);
                GUI.color = Color.white;
                cy += respH + 4f;
            }

            // Tool calls
            if (entry.ToolCalls != null && entry.ToolCalls.Count > 0)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(1f, 0.85f, 0.4f);
                Widgets.Label(new Rect(textX, cy, textWidth, 18f), "Tool Calls:");
                cy += 18f;
                Text.Font = GameFont.Small;
                foreach (var tc in entry.ToolCalls)
                {
                    string callText = tc.Name + "(" + tc.ArgumentsJson + ")";
                    float callH = Text.CalcHeight(callText, textWidth);
                    Widgets.Label(new Rect(textX, cy, textWidth, callH), callText);
                    cy += callH + 2f;
                }
                GUI.color = Color.white;
                cy += 4f;
            }

            // Tool results
            if (entry.ToolResults != null && entry.ToolResults.Count > 0)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                Widgets.Label(new Rect(textX, cy, textWidth, 18f), "Tool Results:");
                cy += 18f;
                Text.Font = GameFont.Small;
                foreach (var tr in entry.ToolResults)
                {
                    string resultText = (tr.Success ? "[OK] " : "[FAIL] ") + tr.Content;
                    if (tr.HasImage) resultText += " [+image]";
                    GUI.color = tr.Success ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.5f, 0.5f);
                    float resH = Text.CalcHeight(resultText, textWidth);
                    Widgets.Label(new Rect(textX, cy, textWidth, resH), resultText);
                    cy += resH + 2f;
                }
                GUI.color = Color.white;
                cy += 4f;
            }
        }
    }
}
