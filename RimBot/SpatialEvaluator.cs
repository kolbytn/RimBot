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
    /// Checks both completed buildings AND blueprints/frames.
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

        // --- Spatial heuristics — flag bad placement patterns ---

        private static void LogSpatialWarnings(Map map, int tick)
        {
            float day = tick / 60000f;
            var warnings = new List<string>();

            int exposedBeds = 0;
            int blockedWorkbenches = 0;
            int outdoorBlueprints = 0;

            // Check completed buildings
            foreach (var building in map.listerBuildings.allBuildingsColonist)
            {
                if (building == null || !building.Spawned) continue;

                if (building.def.IsBed)
                {
                    var room = building.GetRoom();
                    if (room == null || room.TouchesMapEdge)
                        exposedBeds++;
                }

                if (building.def.hasInteractionCell)
                {
                    if (IsCellBlocked(map, building.InteractionCell, building))
                    {
                        blockedWorkbenches++;
                        warnings.Add(building.def.label + " at (" + building.Position.x + "," +
                            building.Position.z + ") has blocked interaction spot");
                    }
                }
            }

            // Check blueprints and frames for the same issues
            foreach (var thing in map.listerThings.AllThings)
            {
                BuildableDef targetDef = null;
                if (thing is Blueprint bp)
                    targetDef = bp.def.entityDefToBuild;
                else if (thing is Frame fr)
                    targetDef = fr.def.entityDefToBuild;
                else
                    continue;

                if (thing.Faction != Faction.OfPlayer) continue;

                var thingDef = targetDef as ThingDef;
                if (thingDef == null) continue;

                // Production building blueprints placed outdoors
                if (thingDef.building != null && thingDef.hasInteractionCell)
                {
                    var room = thing.GetRoom();
                    if (room == null || room.TouchesMapEdge)
                        outdoorBlueprints++;
                }

                // Bed blueprints outside rooms
                if (thingDef.IsBed)
                {
                    var room = thing.GetRoom();
                    if (room == null || room.TouchesMapEdge)
                        exposedBeds++;
                }
            }

            if (exposedBeds > 0)
                warnings.Add(exposedBeds + " bed(s)/bed blueprint(s) not in enclosed rooms");
            if (outdoorBlueprints > 0)
                warnings.Add(outdoorBlueprints + " workbench blueprint(s) placed outdoors");

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

        private static bool IsCellBlocked(Map map, IntVec3 cell, Thing ignore)
        {
            if (!cell.InBounds(map)) return true;
            foreach (var thing in cell.GetThingList(map))
            {
                if (thing == ignore) continue;
                if (thing.def.passability == Traversability.Impassable)
                    return true;
            }
            return false;
        }

        // --- Colony quality score ---

        private static void LogColonyScore(Map map, int tick)
        {
            float day = tick / 60000f;
            int score = 0;
            var details = new List<string>();

            // --- Positive: enclosed rooms discovered via player buildings ---
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

            // --- Positive: completed production buildings ---
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

            // --- Positive: zones ---
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

            // --- Positive: furniture ---
            var table1 = DefDatabase<ThingDef>.GetNamed("Table1x2c", false);
            var table2 = DefDatabase<ThingDef>.GetNamed("Table2x2c", false);
            if ((table1 != null && map.listerBuildings.ColonistsHaveBuilding(table1)) ||
                (table2 != null && map.listerBuildings.ColonistsHaveBuilding(table2)))
            {
                score += 5;
                details.Add("table (+5)");
            }

            // --- Positive: research ---
            int finishedResearch = 0;
            foreach (var proj in DefDatabase<ResearchProjectDef>.AllDefs)
            {
                if (proj.IsFinished) finishedResearch++;
            }
            int botResearch = Math.Max(0, finishedResearch - 7);
            score += botResearch * 5;
            if (botResearch > 0) details.Add("researched=" + botResearch + " (+" + (botResearch * 5) + ")");

            // --- Penalties ---

            // Exposed beds (completed)
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

            // Blocked workbenches (completed)
            int blockedBenches = 0;
            foreach (var building in map.listerBuildings.allBuildingsColonist)
            {
                if (building == null || !building.Spawned || !building.def.hasInteractionCell) continue;
                if (IsCellBlocked(map, building.InteractionCell, building))
                    blockedBenches++;
            }
            if (blockedBenches > 0)
            {
                int penalty = blockedBenches * 8;
                score -= penalty;
                details.Add("blocked_benches=" + blockedBenches + " (-" + penalty + ")");
            }

            // Outdoor workbench blueprints/frames — indicates confused placement
            int outdoorProdBlueprints = 0;
            int stalledBlueprints = 0;
            foreach (var thing in map.listerThings.AllThings)
            {
                BuildableDef targetDef = null;
                if (thing is Blueprint bp)
                    targetDef = bp.def.entityDefToBuild;
                else if (thing is Frame fr)
                    targetDef = fr.def.entityDefToBuild;
                else
                    continue;

                if (thing.Faction != Faction.OfPlayer) continue;

                var thingDef = targetDef as ThingDef;
                if (thingDef == null) continue;

                // Count all outstanding blueprints/frames
                stalledBlueprints++;

                // Outdoor production blueprints
                if (thingDef.hasInteractionCell)
                {
                    var room = thing.GetRoom();
                    if (room == null || room.TouchesMapEdge)
                        outdoorProdBlueprints++;
                }
            }

            if (outdoorProdBlueprints > 0)
            {
                int penalty = outdoorProdBlueprints * 5;
                score -= penalty;
                details.Add("outdoor_workbench_bp=" + outdoorProdBlueprints + " (-" + penalty + ")");
            }

            // Track total unbuilt blueprints for context (not a penalty, just info)
            if (stalledBlueprints > 0)
                details.Add("pending_blueprints=" + stalledBlueprints);

            Log.Message("[RimBot] [COLONY_SCORE] Day " + string.Format("{0:F1}", day) +
                " | Score: " + score + " | " + string.Join(", ", details));
        }
    }
}
