using System;
using SSSGame;
using UnityEngine;

namespace ResourceMarkerRadiusMod
{
    // Backstop for the map/compass radius ring. The native game re-syncs OutpostStructure ranges at
    // points we hook directly (Activate, _RefreshRanges), but a map icon can also be (re)created later
    // — e.g. when you open the map — at which point the freshly built CompassObjectiveMarker reads
    // whatever objectiveMarker.range currently holds. Polling each tracked outpost re-asserts
    // range = harvestMarker.radius so the ring is correct no matter when the icon is built, and defeats
    // any resetter we haven't enumerated. Steady-state this is a cheap no-op (mismatch check first).
    public class ResourceMarkerRadiusTracker : MonoBehaviour
    {
        private float _timer;
        private const float Interval = 1.0f;

        void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < Interval) return;
            _timer = 0f;

            var tracked = OutpostStructurePatch.TrackedOutposts;
            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                var outpost = tracked[i];
                if (outpost == null)
                {
                    tracked.RemoveAt(i);
                    continue;
                }

                try
                {
                    OutpostStructurePatch.SyncObjectiveRange(outpost);
                }
                catch (Exception ex)
                {
                    ResourceMarkerRadiusPlugin.Logger.LogError($"[ResourceMarkerRadiusMod] Sync failed: {ex}");
                }
            }
        }
    }
}
