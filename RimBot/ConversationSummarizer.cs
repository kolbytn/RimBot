using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using RimBot.Models;

namespace RimBot
{
    /// <summary>
    /// Summarizes dropped conversation cycles using an LLM call.
    /// Produces a compact history of what the bot did, thought, and planned
    /// so that future cycles have context about past work.
    /// </summary>
    public static class ConversationSummarizer
    {
        private const string SummarizePrompt =
            "Summarize the following conversation cycles from a RimWorld colonist AI. " +
            "Extract the key information: what was built, what was planned, what problems were encountered, " +
            "what decisions were made, and any ongoing goals or plans. " +
            "Be concise — use 2-3 sentences per cycle. Focus on actions and outcomes, not observations. " +
            "Format as a brief timeline.";

        /// <summary>
        /// Summarize dropped cycles using an LLM call. Extracts the conversation messages
        /// for the specified cycle range and sends them to the LLM for summarization.
        /// Returns the summary text, or a fallback extraction if the LLM call fails.
        /// </summary>
        public static async Task<string> SummarizeCyclesAsync(
            List<ChatMessage> conversation, List<int> cycleStartIndices,
            int firstCycleIdx, int lastCycleIdx,
            ILanguageModel llm, string model, string apiKey, int maxTokens)
        {
            // Build the conversation excerpt to summarize
            int startMsg = cycleStartIndices[firstCycleIdx];
            int endMsg = (lastCycleIdx + 1 < cycleStartIndices.Count)
                ? cycleStartIndices[lastCycleIdx + 1]
                : conversation.Count;

            // Extract text representation of the cycles
            var excerpt = new StringBuilder();
            for (int i = startMsg; i < endMsg && i < conversation.Count; i++)
            {
                var msg = conversation[i];
                if (msg.ContentParts == null) continue;

                foreach (var part in msg.ContentParts)
                {
                    if (part.Type == "text" && !string.IsNullOrEmpty(part.Text))
                        excerpt.AppendLine("[" + msg.Role + "] " + part.Text);
                    else if (part.Type == "tool_use" && part.ToolName != null)
                        excerpt.AppendLine("[tool_call] " + part.ToolName +
                            (part.ToolArguments != null ? "(" + part.ToolArguments.ToString(Newtonsoft.Json.Formatting.None) + ")" : ""));
                    else if (part.Type == "tool_result")
                    {
                        string status = part.ToolSuccess == true ? "ok" : "FAILED";
                        string content = part.Text ?? "";
                        if (content.Length > 200) content = content.Substring(0, 200) + "...";
                        excerpt.AppendLine("[tool_result:" + status + "] " + content);
                    }
                    else if (part.Type == "thinking" && !string.IsNullOrEmpty(part.Text))
                    {
                        string thinking = part.Text;
                        if (thinking.Length > 300) thinking = thinking.Substring(0, 300) + "...";
                        excerpt.AppendLine("[thinking] " + thinking);
                    }
                    // Skip images — too large for summary
                }
            }

            string excerptText = excerpt.ToString();
            if (string.IsNullOrEmpty(excerptText))
                return null;

            // Truncate if too long to avoid exceeding token limits
            if (excerptText.Length > 8000)
                excerptText = excerptText.Substring(0, 8000) + "\n[truncated]";

            try
            {
                var messages = new List<ChatMessage>
                {
                    new ChatMessage("system", SummarizePrompt),
                    new ChatMessage("user", excerptText)
                };

                var response = await llm.SendChatRequest(messages, model, apiKey, maxTokens);
                if (response.Success && !string.IsNullOrEmpty(response.Content))
                    return response.Content.Trim();
            }
            catch { }

            // Fallback: return a basic extraction if LLM fails
            return FallbackSummary(conversation, cycleStartIndices, firstCycleIdx, lastCycleIdx);
        }

        /// <summary>
        /// Basic fallback if the LLM call fails — extract assistant text responses.
        /// </summary>
        private static string FallbackSummary(List<ChatMessage> conversation,
            List<int> cycleStartIndices, int firstCycleIdx, int lastCycleIdx)
        {
            var sb = new StringBuilder();
            for (int c = firstCycleIdx; c <= lastCycleIdx; c++)
            {
                int startMsg = cycleStartIndices[c];
                int endMsg = (c + 1 < cycleStartIndices.Count)
                    ? cycleStartIndices[c + 1]
                    : conversation.Count;

                // Find the last assistant text in this cycle
                string lastText = null;
                for (int i = startMsg; i < endMsg && i < conversation.Count; i++)
                {
                    var msg = conversation[i];
                    if (msg.Role != "assistant" || msg.ContentParts == null) continue;
                    foreach (var part in msg.ContentParts)
                    {
                        if (part.Type == "text" && !string.IsNullOrEmpty(part.Text) && part.Text.Length > 10)
                            lastText = part.Text;
                    }
                }

                if (lastText != null)
                {
                    if (lastText.Length > 150) lastText = lastText.Substring(0, 150) + "...";
                    sb.AppendLine("Cycle " + (c + 1) + ": " + lastText);
                }
            }
            return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
        }

        /// <summary>
        /// Merge a new summary into an existing accumulated summary.
        /// </summary>
        public static string MergeSummaries(string existing, string newSummary)
        {
            if (string.IsNullOrEmpty(existing))
                return newSummary;
            if (string.IsNullOrEmpty(newSummary))
                return existing;
            return existing + "\n" + newSummary;
        }
    }
}
