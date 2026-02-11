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
