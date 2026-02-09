using System.Collections.Generic;
using Verse;

namespace RimBot
{
    public class ColonyAssignmentComponent : GameComponent
    {
        private Dictionary<int, string> assignments = new Dictionary<int, string>();
        public bool autoAssignNewColonists = false;
        private int nextRoundRobinIndex;

        // Pawn IDs spawned by the config system (living)
        public List<int> configPawnIds = new List<int>();
        // Pawn IDs of config colonists that have died (prevents respawn)
        public List<int> deadConfigPawnIds = new List<int>();

        public ColonyAssignmentComponent(Game game)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref assignments, "assignments", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref autoAssignNewColonists, "autoAssignNewColonists", false);
            Scribe_Values.Look(ref nextRoundRobinIndex, "nextRoundRobinIndex", 0);
            Scribe_Collections.Look(ref configPawnIds, "configPawnIds", LookMode.Value);
            Scribe_Collections.Look(ref deadConfigPawnIds, "deadConfigPawnIds", LookMode.Value);

            if (assignments == null)
                assignments = new Dictionary<int, string>();
            if (configPawnIds == null)
                configPawnIds = new List<int>();
            if (deadConfigPawnIds == null)
                deadConfigPawnIds = new List<int>();
        }

        public bool IsConfigPawn(int pawnId)
        {
            return configPawnIds.Contains(pawnId);
        }

        public bool IsDeadConfigPawn(int pawnId)
        {
            return deadConfigPawnIds.Contains(pawnId);
        }

        public void MarkConfigPawnDead(int pawnId)
        {
            configPawnIds.Remove(pawnId);
            if (!deadConfigPawnIds.Contains(pawnId))
                deadConfigPawnIds.Add(pawnId);
        }

        public void AddConfigPawn(int pawnId)
        {
            if (!configPawnIds.Contains(pawnId))
                configPawnIds.Add(pawnId);
        }

        public void RemoveConfigPawn(int pawnId)
        {
            configPawnIds.Remove(pawnId);
        }

        public string GetAssignment(int pawnId)
        {
            string profileId;
            assignments.TryGetValue(pawnId, out profileId);
            return profileId;
        }

        public void SetAssignment(int pawnId, string profileId)
        {
            assignments[pawnId] = profileId;
        }

        public void ClearAssignment(int pawnId)
        {
            assignments.Remove(pawnId);
        }

        public void AutoAssign(int pawnId, List<AgentProfile> profiles)
        {
            if (profiles == null || profiles.Count == 0)
                return;

            var profile = profiles[nextRoundRobinIndex % profiles.Count];
            nextRoundRobinIndex++;
            assignments[pawnId] = profile.Id;
            Log.Message("[RimBot] Auto-assigned pawn " + pawnId + " to profile " + profile.Provider + "/" + profile.Model);
        }

        public void RemoveAssignmentsForProfile(string profileId)
        {
            var toRemove = new List<int>();
            foreach (var kvp in assignments)
            {
                if (kvp.Value == profileId)
                    toRemove.Add(kvp.Key);
            }
            foreach (var id in toRemove)
                assignments.Remove(id);
        }
    }
}
