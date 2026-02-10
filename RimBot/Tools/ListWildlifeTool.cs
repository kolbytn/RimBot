using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class ListWildlifeTool : ITool
    {
        public string Name => "list_wildlife";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "List nearby wild animals with species, name, distance, and current designation (hunt/tame/none).",
                ParametersJson = "{\"type\":\"object\",\"properties\":{},\"required\":[]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] list_wildlife()");

            var map = context.Map;
            var observerPos = context.PawnPosition;
            var dm = map.designationManager;
            var wildlife = new List<WildAnimalInfo>();

            foreach (var p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.RaceProps == null || !p.RaceProps.Animal || p.Faction != null)
                    continue;

                float dist = p.Position.DistanceTo(observerPos);
                string designation = "none";
                if (dm.DesignationOn(p, DesignationDefOf.Hunt) != null)
                    designation = "hunt";
                else if (dm.DesignationOn(p, DesignationDefOf.Tame) != null)
                    designation = "tame";

                wildlife.Add(new WildAnimalInfo
                {
                    Pawn = p,
                    Distance = dist,
                    Designation = designation
                });
            }

            if (wildlife.Count == 0)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = true,
                    Content = "No wild animals on the map."
                });
                return;
            }

            wildlife.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            var sb = new StringBuilder();
            sb.AppendLine("=== Wildlife (" + wildlife.Count + ") ===");

            foreach (var info in wildlife)
            {
                var p = info.Pawn;
                string name = p.Name != null ? p.Name.ToStringShort : p.LabelShort;
                int relX = p.Position.x - observerPos.x;
                int relZ = p.Position.z - observerPos.z;
                sb.AppendLine("  " + name + " (" + p.def.label + ") — dist " + info.Distance.ToString("F0") +
                    " at (" + relX + "," + relZ + ") — " + info.Designation);
            }

            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = sb.ToString().TrimEnd()
            });
        }

        private struct WildAnimalInfo
        {
            public Pawn Pawn;
            public float Distance;
            public string Designation;
        }
    }
}
