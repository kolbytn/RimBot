using System.Collections.Generic;

namespace RimBot.Tools
{
    public static class ToolRegistry
    {
        private static readonly Dictionary<string, ITool> tools = new Dictionary<string, ITool>();
        private static bool initialized;

        public static void EnsureInitialized()
        {
            if (initialized)
                return;

            initialized = true;

            Register(new GetScreenshotTool());
            Register(new ArchitectOrdersTool());
            Register(new ArchitectZoneTool());
            Register(new ListBuildablesTool());

            foreach (var cat in new[] { "Structure", "Production", "Furniture", "Power",
                "Security", "Misc", "Floors", "Ship", "Temperature", "Joy" })
                Register(new ArchitectBuildTool(cat));

            Register(new InspectCellTool());
            Register(new ScanAreaTool());
            Register(new FindOnMapTool());
            Register(new GetPawnStatusTool());

            // Colony management tools
            Register(new ListWorkPrioritiesTool());
            Register(new SetWorkPriorityTool());
            Register(new ListScheduleTool());
            Register(new SetScheduleTool());
            Register(new ListAnimalsTool());
            Register(new SetAnimalTrainingTool());
            Register(new SetAnimalOperationTool());
            Register(new SetAnimalMasterTool());
            Register(new ListWildlifeTool());
            Register(new SetWildlifeOperationTool());
            Register(new ListResearchTool());
            Register(new SetResearchTool());
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
