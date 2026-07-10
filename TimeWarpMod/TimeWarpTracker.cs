using System;
using SSSGame.Weather;
using UnityEngine;

namespace TimeWarpMod;

public class TimeWarpTracker : MonoBehaviour
{
    private KeyCode _fastForwardKey = KeyCode.K;
    private KeyCode _skipDayKey = KeyCode.L;
    private float _nextDiagAt;
    private bool _loggedNoAuthorityCheck;

    private string _guiMessage = "";
    private float _guiMessageExpiry = 0f;

    private void Start()
    {
        _fastForwardKey = ParseKey(Plugin.FastForwardHotkey.Value, KeyCode.K);
        _skipDayKey = ParseKey(Plugin.SkipDayHotkey.Value, KeyCode.L);
    }

    private KeyCode ParseKey(string raw, KeyCode fallback)
    {
        if (Enum.TryParse<KeyCode>(raw, true, out var parsed))
            return parsed;
        Plugin.Logger.LogWarning($"[TimeWarp] Could not parse hotkey '{raw}'. Using default '{fallback}'.");
        return fallback;
    }

    private void Update()
    {
        bool textFocused = IsTextInputFocused();

        if (!textFocused && Input.GetKeyDown(_fastForwardKey))
        {
            try { DoFastForward(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[TimeWarp] DoFastForward error: {ex}"); }
        }

        if (!textFocused && Input.GetKeyDown(_skipDayKey))
        {
            try { DoSkipDay(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[TimeWarp] DoSkipDay error: {ex}"); }
        }

        if (Plugin.TimeDiagnostics.Value && Time.time >= _nextDiagAt)
        {
            _nextDiagAt = Time.time + Plugin.DiagnosticsIntervalSeconds.Value;
            try { LogTimeState("diag"); }
            catch (Exception ex) { Plugin.Logger.LogError($"[TimeWarp] Diagnostics error: {ex}"); }
        }
    }

    // Never cache the WeatherSystem wrapper — it's a per-world native singleton and caching it
    // across world sessions crashes natively (project-wide gotcha). Every action/diag call reads
    // WeatherSystem.Instance fresh.
    private void LogTimeState(string tag)
    {
        var ws = WeatherSystem.Instance;
        if (ws == null)
        {
            Plugin.Logger.LogInfo($"[TimeWarp][state] {tag}: WeatherSystem not ready");
            return;
        }

        int daysPassed = -1;
        try { daysPassed = ws.GetDaysPassed(); } catch { }
        int dayOfYear = -1;
        try { dayOfYear = ws.DayOfYear; } catch { }
        float timeOfDay = -1f;
        try { timeOfDay = ws.TimeOfDay; } catch { }
        float dayLength = -1f;
        try { dayLength = ws.dayLength; } catch { }
        int daysInYear = -1;
        try { daysInYear = ws.daysInOneYear; } catch { }
        float speedMult = -1f;
        try { speedMult = ws.TimeSpeedMultiplier; } catch { }
        bool timeRunning = false;
        try { timeRunning = ws.TimeRunningEnabled; } catch { }
        float gameTime = -1f;
        try { gameTime = ws.NetworkedCurrentGameTime; } catch { }

        Plugin.Logger.LogInfo($"[TimeWarp][state] {tag}: daysPassed={daysPassed} dayOfYear={dayOfYear} timeOfDay={timeOfDay} dayLength={dayLength} daysInYear={daysInYear} speedMult={speedMult} timeRunning={timeRunning} gameTime={gameTime}");
    }

    // Best-effort host/authority gate. WeatherSystem is a Fusion NetworkBehaviour, which exposes
    // Runner directly (confirmed via Cecil dump of SSSGame.Weather.WeatherSystem's base chain,
    // Fusion.SimulationBehaviour) — no need to go through an Object/NetworkObject indirection.
    // Any failure here is swallowed and the action proceeds (solo play = host).
    private bool CheckAuthority(WeatherSystem ws)
    {
        try
        {
            var runner = ws.Runner;
            if (runner != null && !(runner.IsServer || runner.IsSharedModeMasterClient))
            {
                ShowMessage("Host only.");
                return false;
            }
        }
        catch (Exception ex)
        {
            if (!_loggedNoAuthorityCheck)
            {
                _loggedNoAuthorityCheck = true;
                Plugin.Logger.LogInfo($"[TimeWarp] No authority check available — use host-side only. ({ex.GetType().Name})");
            }
        }

        return true;
    }

    private void DoFastForward()
    {
        var ws = WeatherSystem.Instance;
        if (ws == null)
        {
            ShowMessage("WeatherSystem not ready.");
            return;
        }

        if (!CheckAuthority(ws)) return;

        LogTimeState("pre-fastforward");

        try { ws.Rpc_ToggleFastForward(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[TimeWarp] Rpc_ToggleFastForward error: {ex}"); }

        LogTimeState("post-fastforward");

        float mult = -1f;
        try { mult = WeatherSystem.Instance?.TimeSpeedMultiplier ?? -1f; } catch { }
        ShowMessage($"Fast-forward toggled (speed x{mult})");
    }

    private void DoSkipDay()
    {
        var ws = WeatherSystem.Instance;
        if (ws == null)
        {
            ShowMessage("WeatherSystem not ready.");
            return;
        }

        if (!CheckAuthority(ws)) return;

        LogTimeState("pre-skipday");

        int day;
        try { day = ws.DayOfYear; }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[TimeWarp] Error reading DayOfYear: {ex}");
            ShowMessage("Couldn't read DayOfYear");
            return;
        }

        int daysInYear = -1;
        try { daysInYear = ws.daysInOneYear; } catch { }
        if (daysInYear > 0 && day + 1 > daysInYear)
        {
            Plugin.Logger.LogWarning($"[TimeWarp] Skipping past year end (dayOfYear={day}, daysInYear={daysInYear}) — wrap semantics unverified");
        }

        try { ws.Rpc_SetDayOfYear(day + 1); }
        catch (Exception ex) { Plugin.Logger.LogError($"[TimeWarp] Rpc_SetDayOfYear error: {ex}"); }

        LogTimeState("post-skipday");
        ShowMessage($"Skipped to day-of-year {day + 1}");
    }

    private void ShowMessage(string message)
    {
        _guiMessage = message;
        _guiMessageExpiry = Time.time + 5.0f;
    }

    // Typing guard: keystrokes in the game's text fields (e.g. structure rename) also reach
    // Input.GetKeyDown, so letter-bound hotkeys fire while the player types. Skip hotkey handling
    // whenever the UI's selected object is a text input. (confirmed leak 2026-07-10)
    private static bool IsTextInputFocused()
    {
        try
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            if (go == null) return false;
            return go.GetComponent<TMPro.TMP_InputField>() != null
                || go.GetComponent<UnityEngine.UI.InputField>() != null;
        }
        catch { return false; }
    }

    private void OnGUI()
    {
        if (Time.time < _guiMessageExpiry && !string.IsNullOrEmpty(_guiMessage))
        {
            var shadowStyle = new GUIStyle(GUI.skin.label);
            shadowStyle.fontSize = 24;
            shadowStyle.fontStyle = FontStyle.Bold;
            shadowStyle.alignment = TextAnchor.MiddleCenter;
            shadowStyle.normal.textColor = Color.black;

            var textStyle = new GUIStyle(shadowStyle);
            textStyle.normal.textColor = Color.yellow;

            GUI.Label(new Rect(0, 81, Screen.width, 100), _guiMessage, shadowStyle);
            GUI.Label(new Rect(0, 80, Screen.width, 100), _guiMessage, textStyle);
        }
    }
}
