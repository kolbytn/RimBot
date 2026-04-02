using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace RimBot
{
    /// <summary>
    /// Evaluates colony spatial layout quality. Runs periodically to detect
    /// placement problems and score overall colony organization.
    /// </summary>
    public static class SpatialEvaluator
    {
        private static int lastEvalTick = -1;
        private const int EvalIntervalTicks = 60000; // ~1 game day

        public static void Reset()
        {
            lastEvalTick = -1;
        }

        public static void Tick()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null)
                return;

            int tick = Find.TickManager.TicksGame;
            if (lastEvalTick >= 0 && tick - lastEvalTick < EvalIntervalTicks)
                return;

            lastEvalTick = tick;
            var map = Find.CurrentMap;

            try
            {
                LogSpatialWarnings(map, tick);
                LogColonyScore(map, tick);
            }
            catch (Exception ex)
            {
                Log.Warning("[RimBot] [SPATIAL] Evaluation error: " + ex.Message);
            }
        }

        // --- #2: Spatial heuristics — flag bad placement patterns ---

        private static void LogSpatialWarnings(Map map, int tick)
        {
            float day = tick / 60000f;
            var warnings = new List<string>();

            int exposedBeds = 0;
            int blockedWorkbenches = 0;

            foreach (var building in map.listerBuildings.allBuildingsColonist)
            {
                if (building == null || !building.Spawned) continue;

                // Beds outside enclosed rooms
                if (building.def.IsBed)
                {
                    var room = building.GetRoom();
                    if (room == null || room.TouchesMapEdge)
                        exposedBeds++;
                }

                // Workbenches with blocked interaction spots
                if (building.def.hasInteractionCell)
                {
                    var cell = building.InteractionCell;
                    if (cell.InBounds(map))
                    {
                        foreach (var thing in cell.GetThingList(map))
                        {
                            if (thing.def.passability == Traversability.Impassable && thing != building)
                            {
                                blockedWorkbenches++;
                                warnings.Add(building.def.label + " at (" + building.Position.x + "," +
                                    building.Position.z + ") has blocked interaction spot");
                                break;
                            }
                        }
                    }
                }
            }

            if (exposedBeds > 0)
                warnings.Add(exposedBeds + " bed(s) not in enclosed rooms");

            if (warnings.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("[RimBot] [SPATIAL] Day " + string.Format("{0:F1}", day) + " warnings:");
                foreach (var w in warnings)
                    sb.AppendLine("  - " + w);
                Log.Message(sb.ToString().TrimEnd());
            }
            else
            {
                Log.Message("[RimBot] [SPATIAL] Day " + string.Format("{0:F1}", day) + " — no issues detected");
            }
        }

        // --- #3: Colony quality score ---

        private static void LogColonyScore(Map map, int tick)
        {
            float day = tick / 60000f;
            int score = 0;
            var details = new List<string>();

            // Discover enclosed rooms via player buildings
            var seenRooms = new HashSet<int>();
            int enclosedRooms = 0;
            int roomsWithBeds = 0;
            int roomsWithLight = 0;

            foreach (var building in map.listerBuildings.allBuildingsColonist)
            {
                if (building == null || !building.Spawned) continue;

                var room = building.GetRoom();
                if (room == null || room.TouchesMapEdge || room.IsDoorway) continue;
                if (!seenRooms.Add(room.ID)) continue;

                enclosedRooms++;

                bool hasBed = false;
                bool hasLight = false;
                foreach (var thing in room.ContainedAndAdjacentThings)
                {
                    if (thing.def.IsBed) hasBed = true;
                    if (thing.TryGetComp<CompGlower>() != null) hasLight = true;
                }
                if (hasBed) roomsWithBeds++;
                if (hasLight) roomsWithLight++;
            }

            score += enclosedRooms * 5;
            details.Add("rooms=" + enclosedRooms + " (+" + (enclosedRooms * 5) + ")");

            score += roomsWithBeds * 3;
            if (roomsWithBeds > 0)
                details.Add("bedrooms=" + roomsWithBeds + " (+" + (roomsWithBeds * 3) + ")");

            score += roomsWithLight * 2;
            if (roomsWithLight > 0)
                details.Add("lit_rooms=" + roomsWithLight + " (+" + (roomsWithLight * 2) + ")");

            // Key production buildings (completed only)
            var prodDefs = new string[]
            {
                "FueledStove", "ElectricStove", "TableButcher",
                "SimpleResearchBench", "HiTechResearchBench",
                "ElectricSmelter", "TableStonecutter", "HandTailoringBench"
            };
            var prodScores = new int[] { 10, 10, 8, 8, 12, 8, 6, 6 };

            for (int i = 0; i < prodDefs.Length; i++)
            {
                var def = DefDatabase<ThingDef>.GetNamed(prodDefs[i], false);
                if (def != null && map.listerBuildings.ColonistsHaveBuilding(def))
                {
                    score += prodScores[i];
                    details.Add(prodDefs[i] + " (+" + prodScores[i] + ")");
                }
            }

            // Zones
            int stockpiles = 0;
            int growingZones = 0;
            int totalGrowCells = 0;
            foreach (var zone in map.zoneManager.AllZones)
            {
                if (zone is Zone_Stockpile) stockpiles++;
                var gz = zone as Zone_Growing;
                if (gz != null)
                {
                    growingZones++;
                    totalGrowCells += gz.Cells.Count;
                }
            }

            score += stockpiles * 3;
            if (stockpiles > 0) details.Add("stockpiles=" + stockpiles + " (+" + (stockpiles * 3) + ")");

            score += growingZones * 3;
            if (growingZones > 0) details.Add("farms=" + growingZones + " (+" + (growingZones * 3) + ")");

            int growBonus = Math.Min(totalGrowCells / 10, 10);
            score += growBonus;
            if (growBonus > 0) details.Add("farm_cells=" + totalGrowCells + " (+" + growBonus + ")");

            // Furniture: table
            var table1 = DefDatabase<ThingDef>.GetNamed("Table1x2c", false);
            var table2 = DefDatabase<ThingDef>.GetNamed("Table2x2c", false);
            if ((table1 != null && map.listerBuildings.ColonistsHaveBuilding(table1)) ||
                (table2 != null && map.listerBuildings.ColonistsHaveBuilding(table2)))
            {
                score += 5;
                details.Add("table (+5)");
            }

            // Research progress beyond scenario defaults
            int finishedResearch = 0;
            foreach (var proj in DefDatabase<ResearchProjectDef>.AllDefs)
            {
                if (proj.IsFinished) finishedResearch++;
            }
            int botResearch = Math.Max(0, finishedResearch - 7);
            score += botResearch * 5;
            if (botResearch > 0) details.Add("researched=" + botResearch + " (+" + (botResearch * 5) + ")");

            // Penalties: exposed beds
            int exposedBeds = 0;
            foreach (var building in map.listerBuildings.allBuildingsColonist)
            {
                if (building == null || !building.Spawned || !building.def.IsBed) continue;
                var room = building.GetRoom();
                if (room == null || room.TouchesMapEdge)
                    exposedBeds++;
            }
            if (exposedBeds > 0)
            {
                int penalty = exposedBeds * 5;
                score -= penalty;
                details.Add("exposed_beds=" + exposedBeds + " (-" + penalty + ")");
            }

            Log.Message("[RimBot] [COLONY_SCORE] Day " + string.Format("{0:F1}", day) +
                " | Score: " + score + " | " + string.Join(", ", details));
        }
    }
}
