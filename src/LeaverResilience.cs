using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnboundLib.GameModes;
using UnityEngine;

namespace EveryonePicks
{
    /// <summary>
    /// Keeps a match alive when a transition throws.
    ///
    /// RWF sizes its round-counter arrays from the team count and never shrinks them, so after a
    /// leaver ShowRoundCounterSmall throws. It sits between battleOngoing = true and
    /// StartCoroutine(DoPointStart()) in PointTransition, and an exception ends a coroutine, so
    /// the round never really starts: guns fire and players move, but nothing is simulated.
    ///
    /// Finalizers on the fragile cosmetic calls, plus a watchdog for transitions that die anyway.
    /// </summary>
    internal static class LeaverResilience
    {
        private static bool armed;
        private static float deadline;
        private static bool firedThisRound;

        // The round-start watchdog above arms on PickEnd, so it cannot help a client that dies
        // DURING a phase. This one runs off Update and wall-clock time only: no hook, RPC or
        // coroutine of ours, any of which could be what broke.
        private static bool phaseSeen;
        private static float phaseSince;
        private static float desyncSince;

        /// <summary>Backstop for a phase that simply never ends.</summary>
        private const float StuckPhaseGrace = 300f;

        /// <summary>How long a definite desync must hold before we act on it.</summary>
        private const float DesyncGrace = 8f;

        /// <summary>Seconds a transition is allowed before we assume it died and start the round.</summary>
        private const float TransitionGrace = 25f;

        internal static void Apply(Harmony h)
        {
            int guarded = 0;
            guarded += Guard(h, "RWF.Patches.RoundCounter_Patch_ReDraw", "Postfix");
            guarded += Guard(h, "RWF.RoundCounterExtensions", "UpdateRounds");
            guarded += Guard(h, "RWF.UIHandlerExtensions", "ShowRoundCounterSmall");
            guarded += Guard(h, "RWF.PointVisualizerExtensions", "ResetBalls");
            guarded += Guard(h, "ModdingUtils.AIMinion.AIMinionHandler", "GetPlayerOrAIWithID");

            // ArtHandler.NextArt runs from MapTransition.DelayEvent, a coroutine. An exception
            // there kills the coroutine, so the rest of the transition never happens: no players
            // spawned, an empty map, and no further pick phase for the rest of the match. Observed
            // live - PerformanceImprovements' postfix throws a NullReference here, usually
            // harmlessly, but once at the wrong moment it ended a player's game for good.
            guarded += Guard(h, "PerformanceImprovements.Patches.ArtHandler_Patch", "Postfix");
            guarded += Guard(h, "ArtHandler", "NextArt");

            if (guarded > 0)
                EveryonePicksPlugin.Log("Leaver resilience: " + guarded + " transition call(s) guarded.");

            GameModeManager.AddHook("PickEnd", gm => ArmWatchdog(), 100);
            GameModeManager.AddHook("BattleStart", gm => Disarm());
            GameModeManager.AddHook("GameEnd", gm => Disarm());
        }

        /// <summary>
        /// Swallow an exception thrown by a cosmetic call so the coroutine around it survives.
        /// Harmony treats a finalizer returning null as "handled".
        /// </summary>
        private static int Guard(Harmony h, string typeName, string methodName)
        {
            var t = AccessTools.TypeByName(typeName);
            var m = t != null ? AccessTools.Method(t, methodName) : null;
            if (m == null) return 0;

            try
            {
                h.Patch(m, finalizer: new HarmonyMethod(typeof(LeaverResilience), nameof(Swallow)));
                return 1;
            }
            catch (Exception e)
            {
                EveryonePicksPlugin.Warn("Could not guard " + typeName + "." + methodName + ": " + e.Message);
                return 0;
            }
        }

        private static Exception Swallow(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null) return null;

            string where = __originalMethod != null
                ? (__originalMethod.DeclaringType != null ? __originalMethod.DeclaringType.Name : "?") + "." + __originalMethod.Name
                : "?";

            Diagnostics.Report("Recovered from an error in " + where +
                               "  (" + __exception.GetType().Name + ")");
            return null;
        }

        // ---------------------------------------------------------------- watchdog

        private static IEnumerator ArmWatchdog()
        {
            armed = true;
            firedThisRound = false;
            deadline = Time.realtimeSinceStartup + TransitionGrace;
            yield break;
        }

        private static IEnumerator Disarm()
        {
            armed = false;
            yield break;
        }

        /// <summary>Called every frame from the plugin.</summary>
        internal static void Tick()
        {
            TickPhaseWatchdog();

            if (!armed || firedThisRound) return;
            if (Time.realtimeSinceStartup < deadline) return;

            armed = false;
            firedThisRound = true;

            try
            {
                // Only step in if the round genuinely never started. battleOngoing is set just
                // before the call that throws, so it being true while nothing is simulated is the
                // signature of a transition that died on the last line.
                if (GameManager.instance == null || !GameManager.instance.battleOngoing) return;
                if (PlayerManager.instance == null || PlayerManager.instance.players == null) return;
                if (PlayerManager.instance.players.Count == 0) return;

                var handler = GameModeManager.CurrentHandler;
                var mode = handler?.GameMode;
                if (mode == null) return;

                var start = AccessTools.Method(mode.GetType(), "DoPointStart")
                            ?? AccessTools.Method(mode.GetType(), "DoRoundStart");
                if (start == null) return;

                var mb = mode as MonoBehaviour;
                if (mb == null) return;

                var routine = start.Invoke(mode, null) as IEnumerator;
                if (routine == null) return;

                Diagnostics.Report("Round did not start; starting it manually");
                mb.StartCoroutine(routine);
            }
            catch (Exception e)
            {
                EveryonePicksPlugin.Warn("Round-start recovery failed: " + e.Message);
            }
        }

        /// <summary>
        /// Rescues a client stuck in its own pick phase. Triggers on a definite desync (battle
        /// running while still picking) after DesyncGrace, or on StuckPhaseGrace as a backstop.
        /// Both hard-reset local state, then arm the round-start recovery.
        /// </summary>
        private static void TickPhaseWatchdog()
        {
            bool active;
            try { active = State.phaseActive; }
            catch { return; }

            if (!active)
            {
                phaseSeen = false;
                desyncSince = 0f;
                return;
            }

            float now = Time.realtimeSinceStartup;

            if (!phaseSeen)
            {
                phaseSeen = true;
                phaseSince = now;
                desyncSince = 0f;
            }

            bool desynced = false;
            try
            {
                var gm = GameManager.instance;
                desynced = gm != null && gm.battleOngoing;
            }
            catch { }

            if (desynced)
            {
                if (desyncSince <= 0f) desyncSince = now;
            }
            else
            {
                desyncSince = 0f;
            }

            bool byDesync = desyncSince > 0f && now - desyncSince >= DesyncGrace;
            bool byTimeout = now - phaseSince >= StuckPhaseGrace;
            if (!byDesync && !byTimeout) return;

            phaseSeen = false;
            desyncSince = 0f;

            try
            {
                EveryonePicksPlugin.Warn("Phase watchdog: " +
                    (byDesync ? "battle running while still picking" : "pick phase never ended") +
                    " - resyncing.");
                Diagnostics.Report("Recovered a stuck pick phase");

                State.HardReset(byDesync ? "desynced from the battle" : "pick phase stalled");

                // The round may also have failed to start, which is the other half of being
                // stranded. Let the existing recovery have a go at that immediately.
                armed = true;
                firedThisRound = false;
                deadline = 0f;
            }
            catch (Exception e)
            {
                EveryonePicksPlugin.Warn("Phase watchdog failed: " + e.Message);
            }
        }

        internal static void Reset()
        {
            armed = false;
            firedThisRound = false;
            phaseSeen = false;
            desyncSince = 0f;
        }
    }
}
