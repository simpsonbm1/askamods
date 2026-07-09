using System;

namespace DenRespawnMod;

// In-game day-number source for the timed auto-respawn rules. TreeRespawnMod's own DayTracker
// doesn't keep an absolute day count itself (it computes elapsed-days-since-felled via
// WeatherSystem.GetTimeDifferenceFromCurrentGameTimeInSeconds/dayLength instead) — but it reads
// from the same singleton, SSSGame.Weather.WeatherSystem, and docs/architecture.md documents
// WeatherSystem.GetDaysPassed() (Int32, "total days elapsed since game start") as the absolute
// day-count API on that manager. This reuses that manager + API family rather than inventing a
// new day source. No Il2CppSystem.Action subscription (project-wide gotcha) — plain poll instead,
// mirroring DayTracker's own polling style.
internal static class DayCounter
{
    internal static int CurrentDay = -1;
    private static int _lastPolledDay = -1;

    // Call periodically (DenTracker's 300-frame tick). Returns true exactly once per detected
    // in-game day change; false on the first read of a world (establishes baseline, not a "change").
    internal static bool PollDayChanged()
    {
        try
        {
            var ws = SSSGame.Weather.WeatherSystem.Instance;
            if (ws == null) return false;

            int day = ws.GetDaysPassed();
            CurrentDay = day;

            if (_lastPolledDay == -1)
            {
                _lastPolledDay = day;
                return false;
            }

            if (day != _lastPolledDay)
            {
                _lastPolledDay = day;
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DayCounter.PollDayChanged error: {ex}");
            return false;
        }
    }

    // Called from Plugin.NoteWorldLeft — WeatherSystem is a per-world native singleton; never trust
    // a cached day count across world sessions (project-wide gotcha).
    internal static void ClearTransientState()
    {
        CurrentDay = -1;
        _lastPolledDay = -1;
    }
}
