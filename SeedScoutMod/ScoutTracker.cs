using UnityEngine;

namespace SeedScoutMod;

// Minimal injected MonoBehaviour: only Update(), no extra methods/params, so Il2CppInterop
// injects it cleanly. All logic + state lives in the plain static Scout class.
public class ScoutTracker : MonoBehaviour
{
    void Update() => Scout.Tick();
}
