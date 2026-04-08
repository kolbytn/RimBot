using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimBot
{
    public class OwnershipTracker : MapComponent
    {
        private Dictionary<int, int> thingOwners = new Dictionary<int, int>();
        private Dictionary<int, int> zoneOwners = new Dictionary<int, int>();
        private Dictionary<int, int> designationOwners = new Dictionary<int, int>();

        // --- Overlay state (static: shared across maps, resets on game restart) ---

        private static readonly HashSet<int> highlightedPawns = new HashSet<int>();
        private Dictionary<int, List<IntVec3>> overlayCache;
        private int lastCacheTick = -999;
        private const int CacheRefreshInterval = 60;

        private static readonly Color[] overlayPalette =
        {
            new Color(1f, 0.25f, 0.25f),   // red
            new Color(0.3f, 0.55f, 1f),     // blue
            new Color(0.25f, 0.9f, 0.25f),  // green
            new Color(1f, 0.9f, 0.2f),      // yellow
            new Color(1f, 0.25f, 0.85f),    // magenta
            new Color(0.2f, 0.95f, 0.9f),   // cyan
            new Color(1f, 0.55f, 0.15f),    // orange
            new Color(0.65f, 0.3f, 1f),     // purple
        };

        private static readonly Dictionary<int, Material> fillMaterialCache = new Dictionary<int, Material>();

        public OwnershipTracker(Map map) : base(map) { }

        public static OwnershipTracker Get(Map map)
        {
            return map?.GetComponent<OwnershipTracker>();
        }

        // --- Overlay API ---

        /// <summary>Set true to suppress overlay rendering (e.g. during screenshot capture).</summary>
        public static bool SuppressOverlay;

        public static void ToggleHighlight(int pawnId)
        {
            if (!highlightedPawns.Remove(pawnId))
                highlightedPawns.Add(pawnId);
        }

        public static bool IsHighlighted(int pawnId)
        {
            return highlightedPawns.Contains(pawnId);
        }

        public static Color GetPawnColor(int pawnId)
        {
            return overlayPalette[Math.Abs(pawnId) % overlayPalette.Length];
        }

        // --- Thing ownership ---

        public void SetThingOwner(int thingId, int pawnId)
        {
            thingOwners[thingId] = pawnId;
        }

        /// <returns>Owner pawn ID, or -1 if unowned.</returns>
        public int GetThingOwner(int thingId)
        {
            int owner;
            return thingOwners.TryGetValue(thingId, out owner) ? owner : -1;
        }

        public void RemoveThing(int thingId)
        {
            thingOwners.Remove(thingId);
        }

        /// <summary>Transfer ownership from one thing ID to another (blueprint -> frame -> building).</summary>
        public void TransferThingOwner(int oldId, int newId)
        {
            int owner;
            if (thingOwners.TryGetValue(oldId, out owner))
            {
                thingOwners.Remove(oldId);
                thingOwners[newId] = owner;
            }
        }

        // --- Zone ownership ---

        public void SetZoneOwner(int zoneId, int pawnId)
        {
            zoneOwners[zoneId] = pawnId;
        }

        /// <returns>Owner pawn ID, or -1 if unowned.</returns>
        public int GetZoneOwner(int zoneId)
        {
            int owner;
            return zoneOwners.TryGetValue(zoneId, out owner) ? owner : -1;
        }

        public void RemoveZone(int zoneId)
        {
            zoneOwners.Remove(zoneId);
        }

        // --- Designation ownership ---

        public void SetDesignationOwner(Designation d, int pawnId)
        {
            designationOwners[GetDesignationKey(d)] = pawnId;
        }

        /// <returns>Owner pawn ID, or -1 if unowned.</returns>
        public int GetDesignationOwner(Designation d)
        {
            int owner;
            return designationOwners.TryGetValue(GetDesignationKey(d), out owner) ? owner : -1;
        }

        public void RemoveDesignation(Designation d)
        {
            designationOwners.Remove(GetDesignationKey(d));
        }

        // --- Bulk operations ---

        /// <summary>Release all ownership records for a pawn (e.g. when it dies). Items become unowned.</summary>
        public void ReleaseAll(int pawnId)
        {
            RemoveByValue(thingOwners, pawnId);
            RemoveByValue(zoneOwners, pawnId);
            RemoveByValue(designationOwners, pawnId);
        }

        // --- Persistence ---

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref thingOwners, "thingOwners", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref zoneOwners, "zoneOwners", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref designationOwners, "designationOwners", LookMode.Value, LookMode.Value);

            if (thingOwners == null) thingOwners = new Dictionary<int, int>();
            if (zoneOwners == null) zoneOwners = new Dictionary<int, int>();
            if (designationOwners == null) designationOwners = new Dictionary<int, int>();
        }

        // --- Overlay rendering ---

        public override void MapComponentUpdate()
        {
            if (highlightedPawns.Count == 0 || SuppressOverlay)
                return;

            int tick = Find.TickManager.TicksGame;
            if (tick - lastCacheTick >= CacheRefreshInterval || overlayCache == null)
            {
                RebuildOverlayCache();
                lastCacheTick = tick;
            }

            foreach (var kvp in overlayCache)
            {
                if (kvp.Value.Count == 0)
                    continue;

                var fillMat = GetFillMaterial(kvp.Key);
                var cells = kvp.Value;

                for (int i = 0; i < cells.Count; i++)
                {
                    Vector3 drawPos = cells[i].ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
                    Graphics.DrawMesh(MeshPool.plane10, drawPos, Quaternion.identity, fillMat, 0);
                }

                GenDraw.DrawFieldEdges(cells, GetPawnColor(kvp.Key));
            }
        }

        private void RebuildOverlayCache()
        {
            if (overlayCache == null)
                overlayCache = new Dictionary<int, List<IntVec3>>();
            else
            {
                foreach (var list in overlayCache.Values)
                    list.Clear();
            }

            // Things: iterate all map things, check if owned by a highlighted pawn
            var things = map.listerThings.AllThings;
            for (int i = 0; i < things.Count; i++)
            {
                var t = things[i];
                int owner;
                if (thingOwners.TryGetValue(t.thingIDNumber, out owner) && highlightedPawns.Contains(owner))
                {
                    var cellRect = t.OccupiedRect();
                    foreach (var cell in cellRect)
                        AddToCache(owner, cell);
                }
            }

            // Zones: iterate all zones, check if owned
            var zones = map.zoneManager.AllZones;
            for (int i = 0; i < zones.Count; i++)
            {
                var zone = zones[i];
                int owner;
                if (zoneOwners.TryGetValue(zone.ID, out owner) && highlightedPawns.Contains(owner))
                {
                    foreach (var cell in zone.Cells)
                        AddToCache(owner, cell);
                }
            }

            // Designations: iterate all designation defs via public API
            var dm = map.designationManager;
            var allDefs = DefDatabase<DesignationDef>.AllDefsListForReading;
            for (int d = 0; d < allDefs.Count; d++)
            {
                var desigs = dm.SpawnedDesignationsOfDef(allDefs[d]);
                foreach (var des in desigs)
                {
                    int key = GetDesignationKey(des);
                    int owner;
                    if (designationOwners.TryGetValue(key, out owner) && highlightedPawns.Contains(owner))
                    {
                        if (des.target.HasThing && des.target.Thing != null)
                            AddToCache(owner, des.target.Thing.Position);
                        else
                            AddToCache(owner, des.target.Cell);
                    }
                }
            }
        }

        private void AddToCache(int pawnId, IntVec3 cell)
        {
            List<IntVec3> list;
            if (!overlayCache.TryGetValue(pawnId, out list))
            {
                list = new List<IntVec3>();
                overlayCache[pawnId] = list;
            }
            list.Add(cell);
        }

        private static Material GetFillMaterial(int pawnId)
        {
            Material mat;
            if (!fillMaterialCache.TryGetValue(pawnId, out mat))
            {
                var baseColor = GetPawnColor(pawnId);
                var fillColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.15f);
                var req = default(MaterialRequest);
                req.mainTex = BaseContent.WhiteTex;
                req.shader = ShaderDatabase.MetaOverlay;
                req.color = fillColor;
                mat = MaterialPool.MatFrom(req);
                fillMaterialCache[pawnId] = mat;
            }
            return mat;
        }

        // --- Conflict detection ---

        /// <summary>
        /// Collects all cells within viewRect that belong to zones, blueprints, frames, or
        /// structures owned by a bot other than pawnId.
        /// </summary>
        public void GetOtherBotCells(int pawnId, CellRect viewRect, List<IntVec3> cells)
        {
            // Zones
            var zones = map.zoneManager.AllZones;
            for (int i = 0; i < zones.Count; i++)
            {
                int owner;
                if (zoneOwners.TryGetValue(zones[i].ID, out owner) && owner != pawnId)
                {
                    foreach (var cell in zones[i].Cells)
                    {
                        if (viewRect.Contains(cell))
                            cells.Add(cell);
                    }
                }
            }

            // Things (blueprints, frames, buildings)
            var things = map.listerThings.AllThings;
            for (int i = 0; i < things.Count; i++)
            {
                var t = things[i];
                int owner;
                if (!thingOwners.TryGetValue(t.thingIDNumber, out owner) || owner == pawnId)
                    continue;
                if (!(t is Blueprint) && !(t is Frame) && (t.def.building == null))
                    continue;

                var rect = t.OccupiedRect();
                foreach (var cell in rect)
                {
                    if (viewRect.Contains(cell))
                        cells.Add(cell);
                }
            }
        }

        /// <summary>
        /// Checks if a cell has a zone or thing owned by a different bot.
        /// Returns the conflicting owner pawn ID, or -1 if no conflict.
        /// </summary>
        public int GetCellConflictOwner(IntVec3 cell, int pawnId)
        {
            var zone = map.zoneManager.ZoneAt(cell);
            if (zone != null)
            {
                int zoneOwner = GetZoneOwner(zone.ID);
                if (zoneOwner >= 0 && zoneOwner != pawnId)
                    return zoneOwner;
            }

            var things = cell.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                int thingOwner = GetThingOwner(things[i].thingIDNumber);
                if (thingOwner >= 0 && thingOwner != pawnId)
                    return thingOwner;
            }

            return -1;
        }

        // --- Asset summary for context injection ---

        public class OwnedAsset
        {
            public string Name;        // "bedroom", "kitchen", "outdoor walls", "stockpile"
            public string Building;    // "Building A", "Building B", etc. Null for non-room assets.
            public string Description;  // "bed, torch lamp, stool" or "25 cells"
            public string Status;       // "enclosed" or "INCOMPLETE: 3 gaps" or null for zones
            public IntVec3 Center;      // approximate center for relative coord display
            public int MinX, MinZ, MaxX, MaxZ; // absolute bounding box
            public int RoomId = -1;    // RimWorld room ID for enclosed rooms, -1 otherwise
        }

        /// <summary>
        /// Computes a list of named assets owned by the given pawn. Groups things into
        /// rooms (using the game's room system for enclosed rooms) and categories.
        /// </summary>
        public List<OwnedAsset> GetOwnedAssets(int pawnId)
        {
            var assets = new List<OwnedAsset>();

            // Collect all owned things that still exist on the map
            var ownedThings = new List<Thing>();
            foreach (var kvp in thingOwners)
            {
                if (kvp.Value != pawnId) continue;
                // Find the thing on the map
                foreach (var thing in map.listerThings.AllThings)
                {
                    if (thing.thingIDNumber == kvp.Key && thing.Spawned)
                    {
                        ownedThings.Add(thing);
                        break;
                    }
                }
            }

            // Separate walls/doors from other buildings
            var walls = new List<Thing>();
            var doors = new List<Thing>();
            var furniture = new List<Thing>(); // non-wall buildings, blueprints, frames
            foreach (var thing in ownedThings)
            {
                string defName = GetEntityDefName(thing);
                if (defName == "Wall") walls.Add(thing);
                else if (defName == "Door") doors.Add(thing);
                else if (defName != null) furniture.Add(thing);
            }

            // Group furniture into rooms using the game's room system
            // A room is enclosed if Room != null && !TouchesMapEdge && !IsDoorway
            var roomThings = new Dictionary<int, List<Thing>>(); // room ID -> things in that room
            var outdoorFurniture = new List<Thing>();

            foreach (var thing in furniture)
            {
                var room = thing.Position.GetRoom(map);
                if (room != null && !room.TouchesMapEdge && !room.IsDoorway)
                {
                    if (!roomThings.ContainsKey(room.ID))
                        roomThings[room.ID] = new List<Thing>();
                    roomThings[room.ID].Add(thing);
                }
                else
                {
                    outdoorFurniture.Add(thing);
                }
            }

            // Also check zones in rooms
            var roomZones = new Dictionary<int, List<Zone>>();
            foreach (var zone in map.zoneManager.AllZones)
            {
                int zoneOwner;
                if (!zoneOwners.TryGetValue(zone.ID, out zoneOwner) || zoneOwner != pawnId)
                    continue;

                // Check if zone is primarily inside an enclosed room
                // Use the first cell to determine
                if (zone.Cells.Count > 0)
                {
                    var room = zone.Cells[0].GetRoom(map);
                    if (room != null && !room.TouchesMapEdge && !room.IsDoorway)
                    {
                        if (!roomZones.ContainsKey(room.ID))
                            roomZones[room.ID] = new List<Zone>();
                        roomZones[room.ID].Add(zone);
                        continue;
                    }
                }

                // Outdoor zone — add as its own asset
                var zoneBounds = GetBoundingBox(zone.Cells);
                string zoneType = zone is Zone_Growing ? "growing zone" : "stockpile";
                assets.Add(new OwnedAsset
                {
                    Name = zoneType,
                    Description = zone.Cells.Count + " cells",
                    Center = new IntVec3((zoneBounds[0] + zoneBounds[2]) / 2, 0, (zoneBounds[1] + zoneBounds[3]) / 2),
                    MinX = zoneBounds[0], MinZ = zoneBounds[1], MaxX = zoneBounds[2], MaxZ = zoneBounds[3]
                });
            }

            // Build room assets from enclosed rooms that contain owned things
            foreach (var kvp in roomThings)
            {
                var things = kvp.Value;
                var contents = new List<string>();
                foreach (var thing in things)
                {
                    string label = GetEntityLabel(thing);
                    if (IsBlueprint(thing)) label += " (blueprint)";
                    else if (thing is Frame) label += " (building)";
                    contents.Add(label);
                }

                // Add zones in this room
                List<Zone> zones;
                if (roomZones.TryGetValue(kvp.Key, out zones))
                {
                    foreach (var zone in zones)
                    {
                        string zType = zone is Zone_Growing ? "growing zone" : "stockpile";
                        contents.Add(zType + " (" + zone.Cells.Count + " cells)");
                    }
                }

                string roomName = DetermineRoomName(things);

                // Compute bounding box from room cells
                // Use owned things positions as approximation
                int rMinX = int.MaxValue, rMinZ = int.MaxValue, rMaxX = int.MinValue, rMaxZ = int.MinValue;
                foreach (var t in things)
                {
                    if (t.Position.x < rMinX) rMinX = t.Position.x;
                    if (t.Position.z < rMinZ) rMinZ = t.Position.z;
                    if (t.Position.x > rMaxX) rMaxX = t.Position.x;
                    if (t.Position.z > rMaxZ) rMaxZ = t.Position.z;
                }

                assets.Add(new OwnedAsset
                {
                    Name = roomName,
                    Description = string.Join(", ", contents),
                    Status = "enclosed",
                    Center = new IntVec3((rMinX + rMaxX) / 2, 0, (rMinZ + rMaxZ) / 2),
                    MinX = rMinX, MinZ = rMinZ, MaxX = rMaxX, MaxZ = rMaxZ,
                    RoomId = kvp.Key
                });
            }

            // Unfinished room or outdoor walls — walls/doors not in an enclosed room
            if (walls.Count > 0 || doors.Count > 0)
            {
                int wMinX = int.MaxValue, wMinZ = int.MaxValue, wMaxX = int.MinValue, wMaxZ = int.MinValue;
                int blueprintWalls = 0, completedWalls = 0;
                foreach (var w in walls)
                {
                    if (w.Position.x < wMinX) wMinX = w.Position.x;
                    if (w.Position.z < wMinZ) wMinZ = w.Position.z;
                    if (w.Position.x > wMaxX) wMaxX = w.Position.x;
                    if (w.Position.z > wMaxZ) wMaxZ = w.Position.z;
                    if (IsBlueprint(w) || w is Frame) blueprintWalls++;
                    else completedWalls++;
                }
                foreach (var d in doors)
                {
                    if (d.Position.x < wMinX) wMinX = d.Position.x;
                    if (d.Position.z < wMinZ) wMinZ = d.Position.z;
                    if (d.Position.x > wMaxX) wMaxX = d.Position.x;
                    if (d.Position.z > wMaxZ) wMaxZ = d.Position.z;
                }

                // Check for gaps in the perimeter
                var wallSet = new HashSet<long>();
                foreach (var w in walls)
                    wallSet.Add((long)w.Position.x << 32 | (long)(uint)w.Position.z);
                foreach (var d in doors)
                    wallSet.Add((long)d.Position.x << 32 | (long)(uint)d.Position.z);

                int gaps = 0;
                int perimeterSize = 0;
                for (int x = wMinX; x <= wMaxX; x++)
                {
                    perimeterSize += 2;
                    if (!wallSet.Contains((long)x << 32 | (long)(uint)wMinZ)) gaps++;
                    if (!wallSet.Contains((long)x << 32 | (long)(uint)wMaxZ)) gaps++;
                }
                for (int z = wMinZ + 1; z < wMaxZ; z++)
                {
                    perimeterSize += 2;
                    if (!wallSet.Contains((long)wMinX << 32 | (long)(uint)z)) gaps++;
                    if (!wallSet.Contains((long)wMaxX << 32 | (long)(uint)z)) gaps++;
                }

                // Determine if this looks like a room attempt (has a door and reasonable shape)
                bool looksLikeRoom = doors.Count > 0 && walls.Count >= 4 &&
                    (wMaxX - wMinX) <= 12 && (wMaxZ - wMinZ) <= 12;

                // Determine name from furniture inside the wall area
                var allContents = new List<Thing>(outdoorFurniture);
                string roomName = looksLikeRoom ? DetermineRoomName(allContents) : "outdoor walls";

                // Determine construction status
                string status;
                if (gaps == 0 && walls.Count >= 8)
                    status = "walls complete, awaiting enclosure";
                else if (completedWalls == 0 && blueprintWalls > 0)
                    status = "blueprint, " + gaps + " gaps";
                else if (blueprintWalls > 0)
                    status = "under construction, " + gaps + " gaps remaining";
                else if (gaps > 0)
                    status = "INCOMPLETE: " + gaps + " gaps in perimeter";
                else
                    status = "built";

                // Build description
                var descParts = new List<string>();
                descParts.Add(completedWalls + " walls built");
                if (blueprintWalls > 0) descParts.Add(blueprintWalls + " wall blueprints");
                foreach (var f in outdoorFurniture)
                {
                    string label = GetEntityLabel(f);
                    if (IsBlueprint(f)) label += " (blueprint)";
                    else if (f is Frame) label += " (building)";
                    descParts.Add(label);
                }

                assets.Add(new OwnedAsset
                {
                    Name = roomName,
                    Description = string.Join(", ", descParts),
                    Status = status,
                    Center = new IntVec3((wMinX + wMaxX) / 2, 0, (wMinZ + wMaxZ) / 2),
                    MinX = wMinX, MinZ = wMinZ, MaxX = wMaxX, MaxZ = wMaxZ
                });
            }

            // Outdoor furniture with no walls at all
            if (walls.Count == 0 && doors.Count == 0 && outdoorFurniture.Count > 0)
            {
                foreach (var f in outdoorFurniture)
                {
                    string label = GetEntityLabel(f);
                    if (IsBlueprint(f)) label += " (blueprint)";
                    assets.Add(new OwnedAsset
                    {
                        Name = label,
                        Description = "outdoors, not in a room",
                        Center = f.Position,
                        MinX = f.Position.x, MinZ = f.Position.z, MaxX = f.Position.x, MaxZ = f.Position.z
                    });
                }
            }

            // --- Group enclosed rooms into buildings via door connectivity ---
            AssignBuildings(assets, map);

            return assets;
        }

        /// <summary>
        /// Groups room assets into buildings by finding doors that connect enclosed rooms.
        /// Uses union-find to cluster connected rooms, then assigns building labels.
        /// </summary>
        private static void AssignBuildings(List<OwnedAsset> assets, Map map)
        {
            // Collect room assets with valid RoomIds
            var roomAssets = new Dictionary<int, OwnedAsset>(); // roomId -> asset
            foreach (var asset in assets)
            {
                if (asset.RoomId >= 0)
                    roomAssets[asset.RoomId] = asset;
            }

            if (roomAssets.Count == 0) return;

            // Union-find: group rooms connected by doors
            var parent = new Dictionary<int, int>();
            foreach (var id in roomAssets.Keys)
                parent[id] = id;

            // Check all doors on the map for connections between our rooms
            foreach (var thing in map.listerThings.AllThings)
            {
                if (!thing.def.IsDoor || !thing.Spawned) continue;

                // A door connects rooms on opposite sides. Check all cardinal neighbors.
                var connectedRooms = new HashSet<int>();
                foreach (var dir in GenAdj.CardinalDirections)
                {
                    var adjCell = thing.Position + dir;
                    if (!adjCell.InBounds(map)) continue;
                    var room = adjCell.GetRoom(map);
                    if (room != null && !room.TouchesMapEdge && !room.IsDoorway && roomAssets.ContainsKey(room.ID))
                        connectedRooms.Add(room.ID);
                }

                // Union all connected rooms
                int[] ids = new int[connectedRooms.Count];
                connectedRooms.CopyTo(ids);
                for (int i = 1; i < ids.Length; i++)
                    Union(parent, ids[0], ids[i]);
            }

            // Collect groups
            var groups = new Dictionary<int, List<int>>(); // root -> list of roomIds
            foreach (var id in roomAssets.Keys)
            {
                int root = UFFind(parent, id);
                if (!groups.ContainsKey(root))
                    groups[root] = new List<int>();
                groups[root].Add(id);
            }

            // Only label buildings if there are rooms to group (skip if all rooms are singletons
            // and there's only one building — no need for labels)
            if (groups.Count <= 1 && groups.Values.All(g => g.Count <= 1))
                return;

            // Sort groups by size (largest first) and assign labels
            var sorted = groups.OrderByDescending(g => g.Value.Count).ToList();
            char label = 'A';
            for (int i = 0; i < sorted.Count; i++)
            {
                string buildingName = "Building " + label;
                foreach (var roomId in sorted[i].Value)
                    roomAssets[roomId].Building = buildingName;
                label++;
            }
        }

        private static int UFFind(Dictionary<int, int> parent, int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        private static void Union(Dictionary<int, int> parent, int a, int b)
        {
            int ra = UFFind(parent, a), rb = UFFind(parent, b);
            if (ra != rb) parent[rb] = ra;
        }

        /// <summary>Determine room name based on contents, with priority ordering.</summary>
        private static string DetermineRoomName(List<Thing> contents)
        {
            bool hasBed = false, hasStove = false, hasButcher = false;
            bool hasResearchBench = false, hasCrafting = false;
            int bedCount = 0;

            foreach (var thing in contents)
            {
                string defName = GetEntityDefName(thing);
                if (defName == null) continue;

                if (defName == "Bed" || defName == "DoubleBed" || defName == "RoyalBed")
                { hasBed = true; bedCount++; }
                else if (defName == "FueledStove" || defName == "ElectricStove") hasStove = true;
                else if (defName == "TableButcher") hasButcher = true;
                else if (defName == "SimpleResearchBench" || defName == "HiTechResearchBench") hasResearchBench = true;
                else if (defName == "CraftingSpot" || defName == "HandTailoringBench" || defName == "ElectricSmelter" ||
                         defName == "TableStonecutter") hasCrafting = true;
            }

            // Priority: bedroom > barracks > kitchen > research lab > workshop > room
            if (hasBed && bedCount >= 2) return "barracks";
            if (hasBed) return "bedroom";
            if (hasStove || hasButcher) return "kitchen";
            if (hasResearchBench) return "research lab";
            if (hasCrafting) return "workshop";
            return "room";
        }

        /// <summary>Get the underlying def name for a thing, blueprint, or frame.</summary>
        private static string GetEntityDefName(Thing thing)
        {
            if (thing is Blueprint bp)
                return bp.def.entityDefToBuild?.defName;
            if (thing is Frame fr)
                return fr.def.entityDefToBuild?.defName;
            if (thing.def.building != null)
                return thing.def.defName;
            return null;
        }

        /// <summary>Get a human-readable label for a thing, blueprint, or frame.</summary>
        private static string GetEntityLabel(Thing thing)
        {
            if (thing is Blueprint bp)
                return bp.def.entityDefToBuild?.label ?? thing.def.label;
            if (thing is Frame fr)
                return fr.def.entityDefToBuild?.label ?? thing.def.label;
            return thing.def.label;
        }

        private static bool IsBlueprint(Thing thing)
        {
            return thing is Blueprint;
        }

        private static int[] GetBoundingBox(IList<IntVec3> cells)
        {
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i].x < minX) minX = cells[i].x;
                if (cells[i].z < minZ) minZ = cells[i].z;
                if (cells[i].x > maxX) maxX = cells[i].x;
                if (cells[i].z > maxZ) maxZ = cells[i].z;
            }
            return new int[] { minX, minZ, maxX, maxZ };
        }

        /// <summary>
        /// Returns a condensed summary of all other bots' assets for context injection.
        /// Each entry: asset name, owner name, bounding rectangle.
        /// </summary>
        public List<(string OwnerName, string AssetName, int MinX, int MinZ, int MaxX, int MaxZ)> GetOtherBotsAssetsSummary(int pawnId)
        {
            var result = new List<(string, string, int, int, int, int)>();

            foreach (var otherPawnId in BrainManager.ActivePawnIds)
            {
                if (otherPawnId == pawnId) continue;

                var otherPawn = BrainManager.FindPawnById(otherPawnId);
                if (otherPawn == null) continue;
                string ownerName = otherPawn.LabelShort;

                var assets = GetOwnedAssets(otherPawnId);
                foreach (var asset in assets)
                {
                    result.Add((ownerName, asset.Name, asset.MinX, asset.MinZ, asset.MaxX, asset.MaxZ));
                }
            }

            return result;
        }

        // --- Helpers ---

        private static int GetDesignationKey(Designation d)
        {
            if (d.target.HasThing && d.target.Thing != null)
                return d.target.Thing.thingIDNumber;
            return d.target.Cell.GetHashCode() ^ (d.def.shortHash << 16);
        }

        private static void RemoveByValue(Dictionary<int, int> dict, int value)
        {
            var keys = dict.Where(kvp => kvp.Value == value).Select(kvp => kvp.Key).ToList();
            foreach (var key in keys)
                dict.Remove(key);
        }
    }
}
