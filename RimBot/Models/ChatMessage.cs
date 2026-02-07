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
