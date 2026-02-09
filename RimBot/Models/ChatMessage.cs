using System.Collections.Generic;
using System.Linq;

namespace RimBot.Models
{
    public class ChatMessage
    {
        public string Role { get; set; }
        public List<ContentPart> ContentParts { get; set; }

        public string Content => ContentParts?.FirstOrDefault(p => p.Type == "text")?.Text;

        public bool HasImages => ContentParts?.Any(p => p.Type == "image_url") == true;

        public bool HasToolUse => ContentParts?.Any(p => p.Type == "tool_use") == true;

        public bool HasToolResult => ContentParts?.Any(p => p.Type == "tool_result") == true;

        public IEnumerable<ContentPart> ToolUseParts =>
            ContentParts?.Where(p => p.Type == "tool_use") ?? Enumerable.Empty<ContentPart>();

        public IEnumerable<ContentPart> ToolResultParts =>
            ContentParts?.Where(p => p.Type == "tool_result") ?? Enumerable.Empty<ContentPart>();

        public ChatMessage(string role, string content)
        {
            Role = role;
            ContentParts = new List<ContentPart> { ContentPart.FromText(content) };
        }

        public ChatMessage(string role, List<ContentPart> parts)
        {
            Role = role;
            ContentParts = parts;
        }
    }
}
