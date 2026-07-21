using System;
using SSSGame.UI;
using UnityEngine;

namespace CraftFromStorageMod;

// v0.5.0 (idea-17 UI follow-up, timing fix): replaces the old postfix-time rewrite with POLLING.
// In-game evidence (v0.4.3) established the postfix rewrite could never work: ItemThumbnailPanel.
// _UpdateAvailablility()/._UpdateAvailabilityStatus() DO fire (AOT inlining ruled out), but at the
// moment they run, count.text is still the literal prefab placeholder "99" - the real "0/2"/"6/10"
// string is written LATER by other vanilla code. A registered MonoBehaviour with an Update() timer
// (the DayTracker/RegenTracker/TorchFuelTracker/NeedsController/CraftWatcher idiom already used
// throughout this project) re-checks the panels on an interval instead, so it always reads whatever
// vanilla most recently wrote. PURELY ADDITIVE - never writes game state, only rewrites displayed
// TMP text (via CraftUiAvailability.PollApply); the pull/verify/sweep-back logic in CraftTransfer.cs
// is untouched. Registered the same way CraftWatcher/StorageCensus are (Plugin.cs Load()).
public class CraftUiPoller : MonoBehaviour
{
    private const float DefaultPollSeconds = 0.2f;
    private const int MaxSweepCountLogs = 5;

    private float _accumulator;

    // Diagnostics: how many panels were found per sweep, logged for the first few sweeps only so a
    // zero-panel walk is visible (silence would look identical to "feature working, nothing to
    // rewrite") without spamming every tick thereafter.
    private static int _sweepCountLogs;

    // World-leave: this component holds no interop wrappers of its own (it reads
    // CraftUiState.ActiveCraftMenu fresh every tick, and that reference is cleared separately by
    // CraftUiState.ClearWorldState()) - this just re-arms the one-shot "panels found" diagnostic so
    // a second world load logs its own evidence, same reasoning as CraftUiAvailability.ClearWorldState.
    internal static void ClearWorldState()
    {
        _sweepCountLogs = 0;
    }

    private void Update()
    {
        try { Tick(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] CraftUiPoller.Update error: {ex}"); }
    }

    private void Tick()
    {
        bool showInUi = true;
        try { showInUi = Plugin.ShowSettlementStockInUI?.Value ?? true; } catch { }
        if (!showInUi) return;

        CraftMenu? menu = CraftUiState.ActiveCraftMenu;
        if (menu == null) return;

        float interval = DefaultPollSeconds;
        try { interval = Plugin.UiPollSeconds?.Value ?? DefaultPollSeconds; } catch { }
        if (interval <= 0f) interval = DefaultPollSeconds;

        _accumulator += Time.deltaTime;
        if (_accumulator < interval) return;
        _accumulator = 0f;

        bool diagnostics = true;
        try { diagnostics = Plugin.UiDiagnostics?.Value ?? true; } catch { }

        Transform? root;
        try { root = menu.transform; } catch { root = null; }
        if (root == null) return;

        int found = 0;
        try { found = WalkAndApply(root, 0, menu, diagnostics); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] CraftUiPoller.Tick walk error: {ex}"); }

        if (diagnostics && _sweepCountLogs < MaxSweepCountLogs)
        {
            _sweepCountLogs++;
            Plugin.Logger.LogInfo($"[CFS] UI poll sweep [{_sweepCountLogs}/{MaxSweepCountLogs}]: {found} " +
                "ItemThumbnailPanel(s) with an ItemInfo found under the active craft menu.");
        }
    }

    // Manual per-node GetComponent walk, depth-limited to 8: the PLURAL GetComponentsInChildren<T>(bool)
    // throws MissingMethodException through the interop trampoline (project-wide gotcha), so the
    // hierarchy must be walked by hand with the singular generic - same idiom as CraftUiAvailability.
    // WalkForText and every other subtree walk in this project. Returns the count of panels that had
    // a non-null ItemInfo (i.e. were actually handed to PollApply), for the sweep-count diagnostic.
    private int WalkAndApply(Transform t, int depth, CraftMenu menu, bool diagnostics)
    {
        int count = 0;
        if (depth > 8) return count;

        try
        {
            var panel = t.gameObject.GetComponent<ItemThumbnailPanel>();
            if (panel != null)
            {
                bool hasInfo = false;
                try { hasInfo = panel.ItemInfo != null; } catch { }
                if (hasInfo)
                {
                    count++;
                    try { CraftUiAvailability.PollApply(panel, menu, diagnostics); }
                    catch (Exception ex) { Plugin.Logger.LogError($"[CFS] CraftUiPoller PollApply error: {ex}"); }
                }
            }
        }
        catch { }

        int n;
        try { n = t.childCount; } catch { return count; }
        for (int i = 0; i < n; i++)
        {
            Transform? c = null;
            try { c = t.GetChild(i); } catch { }
            if (c != null) count += WalkAndApply(c, depth + 1, menu, diagnostics);
        }
        return count;
    }
}
