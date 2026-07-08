using SSSGame;
using UnityEngine;

namespace ZeroTaskWorkersMod
{
    // Polling MonoBehaviour (never subscribe to game Action events — see project gotchas).
    //
    // Diagnostics-only world gate: BlueprintConditionsDatabase existing == a world is loaded.
    // Used by patches to tag log lines with how long the world has been loaded, to help
    // distinguish world-load-time task setup from later hire/task events.
    public class ZeroTaskTracker : MonoBehaviour
    {
        public static float WorldGateSeenAt = -1f;

        public static string WorldTag => WorldGateSeenAt < 0 ? "no-world" : $"+{(Time.time - WorldGateSeenAt):F1}s";

        private float _pollTimer;
        private const float PollInterval = 1f;   // 1 Hz — world-gate detection doesn't need per-frame polling

        void Update()
        {
            _pollTimer += Time.deltaTime;
            if (_pollTimer < PollInterval) return;
            _pollTimer = 0f;

            var db = UnityEngine.Object.FindAnyObjectByType<BlueprintConditionsDatabase>();
            if (db == null)
            {
                WorldGateSeenAt = -1f;
                return;
            }

            if (WorldGateSeenAt < 0)
                WorldGateSeenAt = Time.time;
        }
    }
}
