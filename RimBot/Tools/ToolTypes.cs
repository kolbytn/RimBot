using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Verse;

namespace RimBot.Tools
{
    public class ToolDefinition
    {
        public string Name;
        public string Description;
        public string ParametersJson; // JSON Schema string for the parameters object
    }

    public class ToolCall
    {
        public string Id;           // Provider-assigned (e.g. "toolu_xxx", "call_xxx", synthetic for Google)
        public string Name;         // "get_screenshot" or "architect_structure"
        public JObject Arguments;   // Parsed arguments
    }

    public class ToolResult
    {
        public string ToolCallId;
        public string ToolName;     // Needed for Google's functionResponse format
        public bool Success;
        public string Content;      // Text result
        public string ImageBase64;  // Optional image (for get_screenshot)
        public string ImageMediaType;
    }

    public class ToolContext
    {
        public int PawnId;
        public string PawnLabel;
        public IntVec3 PawnPosition;
        public Map Map;
        public Brain Brain;  // For tools that need LLM access (architect_structure_image)
    }

    public interface ITool
    {
        string Name { get; }
        ToolDefinition GetDefinition();
        void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete);
    }
}
