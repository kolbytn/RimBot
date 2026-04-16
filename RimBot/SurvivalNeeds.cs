using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimBot
{
    /// <summary>
    /// Defines the tiered survival needs system. Each need has a human-readable prompt
    /// and a programmatic check function. Bots must complete all needs in a tier before
    /// advancing to the next. Tiers are re-evaluated each cycle so regression is automatic.
    /// </summary>
    public static class SurvivalNeeds
    {
        public class Need
        {
            public string Name { get; }
            public string Prompt { get; }
            public Func<Map, int, bool> IsMet { get; }

            public Need(string name, string prompt, Func<Map, int, bool> isMet)
            {
                Name = name;
                Prompt = prompt;
                IsMet = isMet;
            }
        }

        public class Tier
        {
            public int Level { get; }
            public string Name { get; }
            public List<Need> Needs { get; }

            public Tier(int level, string name, List<Need> needs)
            {
                Level = level;
                Name = name;
                Needs = needs;
            }
        }

        private static List<Tier> tiers;

        public static IReadOnlyList<Tier> Tiers
        {
            get
            {
                if (tiers == null) Initialize();
                return tiers;
            }
        }

        private static void Initialize()
        {
            tiers = new List<Tier>
            {
                // === TIER 0: Bootstrapping ===
                new Tier(0, "Bootstrapping", new List<Need>
                {
                    new Need(
                        "Stockpile zone",
                        "Create a stockpile zone so items can be hauled and organized.",
                        (map, pawnId) => map.zoneManager.AllZones.Any(z => z is Zone_Stockpile)
                    ),
                    new Need(
                        "Haul starting resources",
                        "Haul starting resources (meals, wood, steel, components) to the stockpile.",
                        (map, pawnId) =>
                        {
                            // Met if there are items inside a stockpile zone
                            foreach (var zone in map.zoneManager.AllZones)
                            {
                                if (!(zone is Zone_Stockpile stockpile)) continue;
                                foreach (var cell in stockpile.Cells)
                                {
                                    foreach (var thing in cell.GetThingList(map))
                                    {
                                        if (thing.def.category == ThingCategory.Item && thing.def.EverHaulable)
                                            return true;
                                    }
                                }
                            }
                            return false;
                        }
                    ),
                }),

                // === TIER 1: Immediate Survival ===
                new Tier(1, "Immediate Survival", new List<Need>
                {
                    new Need(
                        "Access to food",
                        "Secure access to food — meals in a stockpile, raw food, or foraged berries.",
                        (map, pawnId) =>
                        {
                            int meals = CountMeals(map);
                            if (meals > 0) return true;
                            // Check for any nutrition-giving items on map
                            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
                            {
                                if (thing.Faction == Faction.OfPlayer || thing.Faction == null)
                                    return true;
                            }
                            return false;
                        }
                    ),
                    new Need(
                        "Basic shelter",
                        "Build an enclosed shelter — walls on all sides with a door, and a roof.",
                        (map, pawnId) => HasEnclosedRoom(map)
                    ),
                    new Need(
                        "Sleeping spot",
                        "Place a bed or sleeping spot under a roof.",
                        (map, pawnId) =>
                            HasBuilding(map, "Bed") || HasBuilding(map, "DoubleBed") || HasBuilding(map, "RoyalBed")
                            || HasBuilding(map, "SleepingSpot") || HasBlueprintOrFrame(map, "Bed") || HasBlueprintOrFrame(map, "DoubleBed")
                    ),
                }),

                // === TIER 2: Sustenance Infrastructure ===
                new Tier(2, "Sustenance Infrastructure", new List<Need>
                {
                    new Need(
                        "Cooking station",
                        "Place a cook stove inside an enclosed room.",
                        (map, pawnId) => HasBuilding(map, "FueledStove") || HasBuilding(map, "ElectricStove")
                            || HasBlueprintOrFrame(map, "FueledStove") || HasBlueprintOrFrame(map, "ElectricStove")
                    ),
                    new Need(
                        "Butchering capability",
                        "Place a butcher table for processing animal corpses into meat.",
                        (map, pawnId) => HasBuilding(map, "TableButcher") || HasBlueprintOrFrame(map, "TableButcher")
                    ),
                    new Need(
                        "Food crops",
                        "Create a growing zone and plant food crops.",
                        (map, pawnId) =>
                        {
                            foreach (var zone in map.zoneManager.AllZones)
                            {
                                if (zone is Zone_Growing growing)
                                {
                                    var plantDef = growing.GetPlantDefToGrow();
                                    if (plantDef != null && plantDef.plant.harvestedThingDef != null
                                        && plantDef.plant.harvestedThingDef.IsNutritionGivingIngestible)
                                        return true;
                                }
                            }
                            return false;
                        }
                    ),
                    new Need(
                        "Hunt animals",
                        "Designate animals for hunting to supplement food supply. (Optional if food surplus > 10 meals.)",
                        (map, pawnId) =>
                        {
                            // Optional if we have plenty of food
                            if (CountMeals(map) > 10) return true;
                            // Check for active hunt designations or raw meat in stockpile
                            if (map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.Hunt).Any())
                                return true;
                            var meatDef = ThingCategoryDefOf.MeatRaw;
                            if (meatDef != null)
                            {
                                foreach (var thing in map.listerThings.AllThings)
                                {
                                    if (thing.def.thingCategories != null && thing.def.thingCategories.Contains(meatDef)
                                        && thing.Faction == Faction.OfPlayer)
                                        return true;
                                }
                            }
                            return false;
                        }
                    ),
                    new Need(
                        "Cooking bills",
                        "Set up cooking bills on the stove so meals are produced automatically.",
                        (map, pawnId) =>
                        {
                            // Check if any stove has bills configured
                            foreach (var building in map.listerBuildings.allBuildingsColonist)
                            {
                                if (building.def.defName == "FueledStove" || building.def.defName == "ElectricStove")
                                {
                                    var billGiver = building as IBillGiver;
                                    if (billGiver != null && billGiver.BillStack.Bills.Count > 0)
                                        return true;
                                }
                            }
                            // Also met if stove is still being built
                            return !HasBuilding(map, "FueledStove") && !HasBuilding(map, "ElectricStove");
                        }
                    ),
                }),

                // === TIER 3: Resilience ===
                new Tier(3, "Resilience", new List<Need>
                {
                    new Need(
                        "Food surplus",
                        "Build up a food surplus of more than 10 meals in the stockpile.",
                        (map, pawnId) => CountMeals(map) > 10
                    ),
                    new Need(
                        "Temperature regulation",
                        "Place a heating or cooling device (heater, cooler, campfire, passive cooler) inside the shelter.",
                        (map, pawnId) =>
                            HasBuilding(map, "Heater") || HasBuilding(map, "Cooler")
                            || HasBuilding(map, "Campfire") || HasBuilding(map, "PassiveCooler")
                            || HasBlueprintOrFrame(map, "Heater") || HasBlueprintOrFrame(map, "Cooler")
                            || HasBlueprintOrFrame(map, "Campfire") || HasBlueprintOrFrame(map, "PassiveCooler")
                    ),
                    new Need(
                        "Research bench",
                        "Place a research bench to begin researching new technologies.",
                        (map, pawnId) =>
                            HasBuilding(map, "SimpleResearchBench") || HasBuilding(map, "HiTechResearchBench")
                            || HasBlueprintOrFrame(map, "SimpleResearchBench") || HasBlueprintOrFrame(map, "HiTechResearchBench")
                    ),
                    new Need(
                        "Roofed storage",
                        "Ensure the stockpile zone is roofed to prevent item deterioration.",
                        (map, pawnId) =>
                        {
                            foreach (var zone in map.zoneManager.AllZones)
                            {
                                if (!(zone is Zone_Stockpile)) continue;
                                bool allRoofed = true;
                                foreach (var cell in zone.Cells)
                                {
                                    if (!cell.Roofed(map))
                                    {
                                        allRoofed = false;
                                        break;
                                    }
                                }
                                if (allRoofed && zone.Cells.Count > 0) return true;
                            }
                            return false;
                        }
                    ),
                    new Need(
                        "Medicine access",
                        "Have medicine (herbal or better) accessible in the stockpile.",
                        (map, pawnId) =>
                        {
                            int herbal = map.resourceCounter.GetCount(ThingDefOf.MedicineHerbal);
                            int industrial = map.resourceCounter.GetCount(ThingDefOf.MedicineIndustrial);
                            return herbal + industrial > 0;
                        }
                    ),
                    new Need(
                        "Recreation",
                        "Place at least one joy source building (horseshoe pin, chess table, etc.).",
                        (map, pawnId) =>
                        {
                            foreach (var building in map.listerBuildings.allBuildingsColonist)
                            {
                                if (building.def.building != null && building.def.building.joyKind != null)
                                    return true;
                            }
                            return false;
                        }
                    ),
                }),

                // === TIER 4: Stability ===
                new Tier(4, "Stability", new List<Need>
                {
                    new Need(
                        "Power infrastructure",
                        "Establish power generation with a generator and battery, or multiple generators.",
                        (map, pawnId) =>
                        {
                            var powerNets = map.powerNetManager.AllNetsListForReading;
                            if (powerNets == null) return false;
                            int producers = 0;
                            bool hasBattery = false;
                            foreach (var net in powerNets)
                            {
                                foreach (var comp in net.powerComps)
                                {
                                    if (comp.PowerOutput > 0) producers++;
                                }
                                if (net.batteryComps.Count > 0) hasBattery = true;
                            }
                            return (producers >= 1 && hasBattery) || producers >= 2;
                        }
                    ),
                    new Need(
                        "Diverse crops",
                        "Grow at least 2 different crop types in growing zones.",
                        (map, pawnId) =>
                        {
                            var cropTypes = new HashSet<string>();
                            foreach (var zone in map.zoneManager.AllZones)
                            {
                                if (zone is Zone_Growing growing)
                                {
                                    var plantDef = growing.GetPlantDefToGrow();
                                    if (plantDef != null)
                                        cropTypes.Add(plantDef.defName);
                                }
                            }
                            return cropTypes.Count >= 2;
                        }
                    ),
                    new Need(
                        "Upgraded beds",
                        "Replace sleeping spots with real beds for all colonists.",
                        (map, pawnId) =>
                        {
                            // Must have real beds (not sleeping spots)
                            bool hasRealBed = HasBuilding(map, "Bed") || HasBuilding(map, "DoubleBed") || HasBuilding(map, "RoyalBed");
                            if (!hasRealBed) return HasBlueprintOrFrame(map, "Bed") || HasBlueprintOrFrame(map, "DoubleBed");
                            return true;
                        }
                    ),
                    new Need(
                        "Active research",
                        "Have a research project selected and in progress.",
                        (map, pawnId) => Find.ResearchManager.GetProject() != null
                    ),
                    new Need(
                        "Tamed animals",
                        "Tame animals for colony use — hauling, rescue, combat, wool/milk production, or companionship.",
                        (map, pawnId) =>
                            map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer)
                                .Any(p => p.RaceProps.Animal)
                    ),
                    new Need(
                        "Room beauty",
                        "Place a beauty-adding item in a room (sculpture, flower pot, carpet, etc.).",
                        (map, pawnId) =>
                        {
                            foreach (var building in map.listerBuildings.allBuildingsColonist)
                            {
                                if (building.GetStatValue(StatDefOf.Beauty) > 0)
                                {
                                    var room = building.GetRoom();
                                    if (room != null && !room.TouchesMapEdge && !room.IsDoorway)
                                        return true;
                                }
                            }
                            return false;
                        }
                    ),
                }),

                // === TIER 5: Growth ===
                new Tier(5, "Growth", new List<Need>
                {
                    new Need(
                        "Fireproof base",
                        "Replace wood walls with stone walls to prevent fire destruction.",
                        (map, pawnId) =>
                        {
                            int woodWalls = 0;
                            int stoneWalls = 0;
                            foreach (var building in map.listerBuildings.allBuildingsColonist)
                            {
                                if (building.def.defName != "Wall") continue;
                                if (building.Stuff != null && building.Stuff.defName == "WoodLog")
                                    woodWalls++;
                                else
                                    stoneWalls++;
                            }
                            // Met when no wood walls remain (or no walls at all yet)
                            return woodWalls == 0 && stoneWalls > 0;
                        }
                    ),
                    new Need(
                        "Freezer",
                        "Build a freezer room held below 0°C for long-term food storage.",
                        (map, pawnId) =>
                        {
                            // Check for a stockpile room with sub-zero temperature
                            foreach (var zone in map.zoneManager.AllZones)
                            {
                                if (!(zone is Zone_Stockpile)) continue;
                                if (zone.Cells.Count == 0) continue;
                                var room = zone.Cells[0].GetRoom(map);
                                if (room == null || room.TouchesMapEdge) continue;
                                // Check temperature of the room via a cell
                                float temp = GenTemperature.GetTemperatureForCell(zone.Cells[0], map);
                                if (temp < 0f) return true;
                            }
                            return false;
                        }
                    ),
                    new Need(
                        "Tailoring",
                        "Place a tailoring bench to produce clothing for colonists.",
                        (map, pawnId) =>
                            HasBuilding(map, "ElectricTailoringBench") || HasBuilding(map, "HandTailoringBench")
                            || HasBlueprintOrFrame(map, "ElectricTailoringBench") || HasBlueprintOrFrame(map, "HandTailoringBench")
                    ),
                    new Need(
                        "Impressive room",
                        "Improve at least one room to 'slightly impressive' quality (impressiveness >= 50).",
                        (map, pawnId) =>
                        {
                            var checkedRooms = new HashSet<int>();
                            foreach (var building in map.listerBuildings.allBuildingsColonist)
                            {
                                var room = building.GetRoom();
                                if (room == null || room.TouchesMapEdge || room.IsDoorway) continue;
                                if (!checkedRooms.Add(room.ID)) continue;
                                if (room.GetStat(RoomStatDefOf.Impressiveness) >= 50f)
                                    return true;
                            }
                            return false;
                        }
                    ),
                }),
            };
        }

        // ===== Context generation helpers =====

        /// <summary>
        /// Returns static system prompt text describing how the tier system works.
        /// This does not change between cycles.
        /// </summary>
        public static string GetSystemPromptSection()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Survival tiers:");
            foreach (var tier in Tiers)
            {
                sb.AppendLine("Tier " + tier.Level + " — " + tier.Name + ":");
                foreach (var need in tier.Needs)
                    sb.AppendLine("  - " + need.Name);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Returns dynamic context text for a specific pawn showing current tier progress.
        /// Called each agent cycle from BuildContext.
        /// </summary>
        public static string GetContextSection(Map map, int pawnId)
        {
            var sb = new StringBuilder();

            int currentTier = -1;
            List<Need> unmetNeeds = null;
            List<Need> metNeeds = null;

            // Find the lowest tier with unmet needs
            foreach (var tier in Tiers)
            {
                var unmet = new List<Need>();
                var met = new List<Need>();
                foreach (var need in tier.Needs)
                {
                    try
                    {
                        if (need.IsMet(map, pawnId))
                            met.Add(need);
                        else
                            unmet.Add(need);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("[RimBot] Survival need check failed for '" + need.Name + "': " + ex.Message);
                        unmet.Add(need);
                    }
                }

                if (unmet.Count > 0)
                {
                    currentTier = tier.Level;
                    unmetNeeds = unmet;
                    metNeeds = met;
                    break;
                }
            }

            if (currentTier == -1)
            {
                // All tiers complete
                sb.AppendLine("SURVIVAL: All tiers complete. Continue expanding and improving the colony.");
            }
            else
            {
                var tier = Tiers[currentTier];
                sb.AppendLine("SURVIVAL TIER " + tier.Level + " — " + tier.Name + ":");

                if (currentTier > 0)
                    sb.AppendLine("  Tiers 0-" + (currentTier - 1) + " complete.");

                foreach (var need in metNeeds)
                    sb.AppendLine("  [DONE] " + need.Name);
                foreach (var need in unmetNeeds)
                    sb.AppendLine("  [TODO] " + need.Name + " — " + need.Prompt);
            }

            return sb.ToString().TrimEnd();
        }

        // ===== Shared helper methods =====

        private static int CountMeals(Map map)
        {
            int count = map.resourceCounter.GetCount(ThingDefOf.MealSimple)
                      + map.resourceCounter.GetCount(ThingDefOf.MealFine);
            var survivalMealDef = DefDatabase<ThingDef>.GetNamed("MealSurvivalPack", false);
            if (survivalMealDef != null)
            {
                foreach (var t in map.listerThings.ThingsOfDef(survivalMealDef))
                    count += t.stackCount;
            }
            return count;
        }

        private static bool HasEnclosedRoom(Map map)
        {
            foreach (var building in map.listerBuildings.allBuildingsColonist)
            {
                if (building == null || !building.Spawned) continue;
                var room = building.GetRoom();
                if (room != null && !room.TouchesMapEdge && !room.IsDoorway)
                    return true;
            }
            return false;
        }

        public static bool HasBuilding(Map map, string defName)
        {
            var def = DefDatabase<ThingDef>.GetNamed(defName, false);
            if (def == null) return false;
            return map.listerBuildings.ColonistsHaveBuilding(def);
        }

        public static bool HasBlueprintOrFrame(Map map, string defName)
        {
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing.Faction != Faction.OfPlayer) continue;
                BuildableDef target = null;
                if (thing is Blueprint bp) target = bp.def.entityDefToBuild;
                else if (thing is Frame fr) target = fr.def.entityDefToBuild;
                if (target != null && target.defName == defName)
                    return true;
            }
            return false;
        }
    }
}
