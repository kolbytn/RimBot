using System.Collections.Generic;

namespace RimBot.Tools
{
    public static class ToolRegistry
    {
        private static readonly Dictionary<string, ITool> tools = new Dictionary<string, ITool>();
        private static PlacementMode? lastMode;

        public static void EnsureInitialized(PlacementMode mode)
        {
            if (lastMode.HasValue && lastMode.Value == mode)
                return;

            tools.Clear();
            lastMode = mode;

            Register(new GetScreenshotTool());
            Register(new ArchitectStructureTool());
            Register(new InspectCellTool());
            Register(new ScanAreaTool());
            Register(new FindOnMapTool());
            Register(new GetPawnStatusTool());
            Register(new DesignateTool());
        }

        private static void Register(ITool tool)
        {
            tools[tool.Name] = tool;
        }

        public static ITool GetTool(string name)
        {
            ITool tool;
            tools.TryGetValue(name, out tool);
            return tool;
        }

        public static List<ToolDefinition> GetAllDefinitions()
        {
            var result = new List<ToolDefinition>();
            foreach (var tool in tools.Values)
                result.Add(tool.GetDefinition());
            return result;
        }
    }
}
