using System;
using System.Collections;
using BepInEx;
using HarmonyLib;
using Photon.Pun;
using UnboundLib.GameModes;
using UnityEngine;

namespace EPDiag
{
    /// <summary>
    /// A private diagnostic mod. It changes nothing about the game: it only watches and writes a
    /// file, so it can be handed to a few players and taken away again without consequence.
    ///
    /// It exists because the last two investigations both ended at the same wall - a client stops
    /// advancing rounds, no mod logs anything, and the shared log is both overwritten on restart
    /// and buried under thousands of unrelated exceptions. This produces one clean, timestamped
    /// file per player per launch, so two players' files can be laid side by side and read as the
    /// same story from two points of view.
    /// </summary>
    [BepInPlugin(ModId, "EPDiag", Version)]
    [BepInProcess("Rounds.exe")]
    public class EPDiagPlugin : BaseUnityPlugin
    {
        internal const string ModId = "com.redredrain.rounds.epdiag";
        internal const string Version = "1.0.0";

        internal static EPDiagPlugin Instance;

        /// <summary>Seconds between state snapshots while a match is running.</summary>
        private const float HeartbeatSeconds = 2f;

        private float nextBeat;
        private string lastBrief = "";

        private void Awake()
        {
            Instance = this;

            Log.Open(PhotonNetwork.LocalPlayer?.NickName ?? "offline");

            var h = new Harmony(ModId);
            try
            {
                Trace.Apply(h);
                Log.Line("instrumented " + Trace.Applied + " round-loop method(s)");
            }
            catch (Exception e) { Log.Line("instrumentation failed: " + e); }

            // Every hook UnboundLib fires, in one place. If the chain stops, this stops with it,
            // and the last hook named is where it stopped.
            foreach (string hook in new[]
            {
                "GameStart", "GameEnd", "PickStart", "PickEnd", "RoundStart", "RoundEnd",
                "PointStart", "PointEnd", "BattleStart", "PlayerPickStart", "PlayerPickEnd",
            })
            {
                string captured = hook;
                try { GameModeManager.AddHook(captured, gm => Announce(captured)); }
                catch (Exception e) { Log.Line("could not hook " + captured + ": " + e.Message); }
            }

            // Unity's own errors, captured with the game state attached, so an exception can be
            // placed against what the round was doing at that instant.
            Application.logMessageReceived += OnUnityLog;

            Log.Line(State.Full());
        }

        private IEnumerator Announce(string hook)
        {
            Log.Line("HOOK " + hook + "  " + State.Full());
            yield break;
        }

        private void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception) return;

            // The interesting part is the first frame; the rest is usually Unity plumbing and
            // this file has to stay readable.
            string first = "";
            if (!string.IsNullOrEmpty(stackTrace))
            {
                int nl = stackTrace.IndexOf('\n');
                first = nl > 0 ? stackTrace.Substring(0, nl).Trim() : stackTrace.Trim();
            }

            Log.Line("ERR  " + condition + (first.Length > 0 ? "  @ " + first : ""));
        }

        private void Update()
        {
            if (Time.unscaledTime < nextBeat) return;
            nextBeat = Time.unscaledTime + HeartbeatSeconds;

            try
            {
                if (!PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode) return;

                // Only write when something actually changed, so a stalled client produces a
                // short readable file rather than thousands of identical lines. The stall itself
                // still shows clearly: the timestamps simply stop moving.
                string brief = State.Brief();
                if (brief == lastBrief) return;
                lastBrief = brief;

                Log.Line("beat " + State.Full());
            }
            catch { }
        }

        private void OnDestroy()
        {
            try { Application.logMessageReceived -= OnUnityLog; } catch { }
            Log.Close();
        }

        private void OnApplicationQuit()
        {
            Log.Line("application quitting");
            Log.Close();
        }
    }
}
