using Newtonsoft.Json.Linq;

namespace RimBot.Models
{
    public class ContentPart
    {
        public string Type { get; set; }
        public string Text { get; set; }
        public string MediaType { get; set; }
        public string Base64Data { get; set; }

        // Tool use fields
        public string ToolCallId { get; set; }
        public string ToolName { get; set; }
        public JObject ToolArguments { get; set; }
        public bool? ToolSuccess { get; set; }

        // Thinking fields
        public string ThinkingId { get; set; }
        public string Signature { get; set; }        // Anthropic thinking signature
        public string RedactedData { get; set; }     // Anthropic redacted_thinking data
        public bool IsRedacted { get; set; }          // True for redacted_thinking parts
        public bool IsThought { get; set; }           // Google thought indicator

        // Google thought signature (must be echoed back for Gemini 3 tool calling)
        public string ThoughtSignature { get; set; }

        public static ContentPart FromText(string text)
        {
            return new ContentPart { Type = "text", Text = text };
        }

        public static ContentPart FromImage(string base64Data, string mediaType)
        {
            return new ContentPart { Type = "image_url", MediaType = mediaType, Base64Data = base64Data };
        }

        public static ContentPart FromToolUse(string id, string name, JObject arguments)
        {
            return new ContentPart
            {
                Type = "tool_use",
                ToolCallId = id,
                ToolName = name,
                ToolArguments = arguments
            };
        }

        public static ContentPart FromToolResult(string toolCallId, string toolName, bool success,
            string text, string imageBase64 = null, string imageMediaType = null)
        {
            return new ContentPart
            {
                Type = "tool_result",
                ToolCallId = toolCallId,
                ToolName = toolName,
                ToolSuccess = success,
                Text = text,
                Base64Data = imageBase64,
                MediaType = imageMediaType
            };
        }

        public static ContentPart FromThinking(string id, string text, string signature = null)
        {
            return new ContentPart
            {
                Type = "thinking",
                ThinkingId = id,
                Text = text,
                Signature = signature
            };
        }

        public static ContentPart FromRedactedThinking(string data)
        {
            return new ContentPart
            {
                Type = "thinking",
                IsRedacted = true,
                RedactedData = data
            };
        }
    }
}
