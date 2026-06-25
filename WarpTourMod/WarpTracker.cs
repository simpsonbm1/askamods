using UnityEngine;

namespace WarpTourMod;

// Minimal injected MonoBehaviour: only Update(), so Il2CppInterop injects it cleanly. All logic +
// state lives in the plain static Warp class.
public class WarpTracker : MonoBehaviour
{
    void Update() => Warp.Tick();
}
