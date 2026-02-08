using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimBot
{
    public static class ArchitectMode
    {
        public static bool AutoStart = true;

        private static bool isRunning;
        private static bool autoStartChecked;
        private static int roomsPlaced;

        public static bool IsRunning => isRunning;
        public static int RoomsPlaced => roomsPlaced;

        private const string SystemPrompt =
            "You are an architect AI for a top-down colony game. You are looking at a 48x48 tile "
            + "area centered on the observer at (0, 0). Coordinates range from (-24,-24) to (24,24). "
            + "Positive X is east (right), positive Z is north (up).\n\n"
            + "Your task: design wall placements for a rectangular room. Output the coordinates of "
            + "every wall tile forming the room's perimeter. Leave a 1-tile gap on one side for a "
            + "doorway. Rooms should be between 5x5 and 7x7 tiles.\n\n"
            + "Respond with ONLY coordinates, one per line, in the format (x, z). Nothing else.";

        private const string QueryNoStructures =
            "Place a new room on open, flat ground. Avoid trees, rocks, and water. "
            + "The room should have walls on the perimeter and a 1-tile door gap. "
            + "Where should the walls go?";

        private const string QueryExistingStructures =
            "There are existing walls or blueprints visible in the image (semi-transparent outlines). "
            + "Place a new room that shares a wall with an existing structure to create a connected base. "
            + "Do not place walls where structures already exist. "
            + "Where should the new walls go?";

        public static void Toggle()
        {
            isRunning = !isRunning;
            roomsPlaced = 0;
            Log.Message("[RimBot] Architect mode " + (isRunning ? "STARTED" : "STOPPED"));
        }

        public static void CheckAutoStart()
        {
            if (autoStartChecked) return;
            autoStartChecked = true;
            if (AutoStart)
            {
                isRunning = true;
                Log.Message("[RimBot] Architect mode auto-started.");
            }
        }

        public static void ProcessCapture(List<Brain> brains, List<Pawn> pawns, string[] results)
        {
            for (int i = 0; i < brains.Count; i++)
            {
                var brain = brains[i];
                var pawn = pawns[i];
                var base64 = results[i];

                if (base64 == null)
                    continue;

                bool hasStructures = DetectExistingStructures(pawn, 24f);
                string query = hasStructures ? QueryExistingStructures : QueryNoStructures;

                Log.Message("[RimBot] [ARCHITECT] [" + brain.PawnLabel + "] Placing room... "
                    + (hasStructures ? "(near existing structures)" : "(new placement)"));

                var map = Find.CurrentMap;
                brain.GenerateArchitectPlan(base64, SystemPrompt, query, pawn.Position, (label, worldCoords) =>
                {
                    PlaceWallBlueprints(map, label, worldCoords);
                });
            }
        }

        private static void PlaceWallBlueprints(Map map, string pawnLabel, List<IntVec3> worldCoords)
        {
            var wallDef = ThingDefOf.Wall;
            var woodDef = ThingDefOf.WoodLog;
            int placed = 0;
            int skipped = 0;

            foreach (var cell in worldCoords)
            {
                if (!cell.InBounds(map))
                {
                    skipped++;
                    continue;
                }

                if (cell.GetEdifice(map) != null)
                {
                    skipped++;
                    continue;
                }

                // Skip cells that already have a blueprint
                bool hasBlueprint = false;
                var thingList = cell.GetThingList(map);
                for (int i = 0; i < thingList.Count; i++)
                {
                    if (thingList[i] is Blueprint)
                    {
                        hasBlueprint = true;
                        break;
                    }
                }
                if (hasBlueprint)
                {
                    skipped++;
                    continue;
                }

                GenConstruct.PlaceBlueprintForBuild(wallDef, cell, map, Rot4.North, Faction.OfPlayer, woodDef);
                placed++;
            }

            Log.Message("[RimBot] [ARCHITECT] [" + pawnLabel + "] Placed " + placed
                + " wall blueprints (" + skipped + " skipped)");

            if (placed > 0)
                roomsPlaced++;
        }

        private static bool DetectExistingStructures(Pawn observer, float radius)
        {
            var map = Find.CurrentMap;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (!thing.Spawned)
                    continue;

                bool isWall = thing.def == ThingDefOf.Wall;
                bool isWallBlueprint = thing is Blueprint && thing.def.entityDefToBuild == ThingDefOf.Wall;

                if (!isWall && !isWallBlueprint)
                    continue;

                if (observer.Position.DistanceTo(thing.Position) <= radius)
                    return true;
            }
            return false;
        }
    }
}
