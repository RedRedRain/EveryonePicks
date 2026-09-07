using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EveryonePicks
{
    /// <summary>
    /// Reflection-driven integration with mods that may or may not be installed. Nothing here is a
    /// hard dependency; every hook no-ops when its target is absent.
    /// </summary>
    internal static class SoftPatches
    {
        private static Type cursedCardType;
        private static Type curseMgrType;
        private static Type pickTrackerType;
        private static Type cardBarUtilsType;

        internal static void Apply(Harmony h)
        {
            // --- Rounds2Mod card delete: pointless (and destructive) during a simultaneous draft.
            var deleteType = AccessTools.TypeByName("Rounds2Mod.CardDeleteManager") ?? AccessTools.TypeByName("CardDeleteManager");
            var beginPick = deleteType != null ? AccessTools.Method(deleteType, "BeginPickPhase") : null;
            if (beginPick != null)
            {
                h.Patch(beginPick, prefix: new HarmonyMethod(typeof(SoftPatches), nameof(SkipWhenPhaseActive)));
                EveryonePicksPlugin.Log("Card Delete Mechanic detected - delete disabled during simultaneous picks.");
            }

            // --- RWF match reset must clear our state or the next match inherits a stale barrier.
            var rwfType = AccessTools.TypeByName("RWF.GameModes.RWFGameMode");
            var resetMatch = rwfType != null ? AccessTools.Method(rwfType, "ResetMatch") : null;
            if (resetMatch != null)
                h.Patch(resetMatch, postfix: new HarmonyMethod(typeof(SoftPatches), nameof(OnResetMatch)));

            // RWF resolves the full pick order right after PickStart; read it so every picker is
            // registered at once instead of trickling in as its sequential loop walks.
            var pmExt = AccessTools.TypeByName("RWF.PlayerManagerExtensions");
            var pickOrder = pmExt != null ? AccessTools.Method(pmExt, "GetPickOrder") : null;
            if (pickOrder != null)
            {
                h.Patch(pickOrder, postfix: new HarmonyMethod(typeof(SoftPatches), nameof(OnPickOrderResolved)));
                EveryonePicksPlugin.Log("RoundsWithFriends detected - pick order will be read up front.");
            }

            NeutralisePickTimer(h);
            LeaverResilience.Apply(h);

            cursedCardType = AccessTools.TypeByName("AALUND13Cards.ExtraCards.Cards.CursedCard")
                             ?? AccessTools.TypeByName("AALUND13Cards.Cards.CursedCard")
                             ?? AccessTools.TypeByName("CursedCard");
            curseMgrType = AccessTools.TypeByName("WillsWackyManagers.Utils.CurseManager");
            pickTrackerType = AccessTools.TypeByName("AALUND13Cards.Handlers.PickCardTracker")
                              ?? AccessTools.TypeByName("PickCardTracker");
            cardBarUtilsType = AccessTools.TypeByName("ModdingUtils.Utils.CardBarUtils");
        }

        /// <summary>
        /// otDan-PickTimer (and the PickTimerCompat shim bundled inside Root-PickPhaseImprovements)
        /// auto-picks on a timer by indexing CardChoice.spawnedCards:
        ///
        ///     instance.Pick(spawnedCards[Random.Next(0, spawnedCards.Count)], false)
        ///
        /// EveryonePicks clears spawnedCards when it tears a local session down, so an in-flight timer
        /// resumes against an empty list and throws ArgumentOutOfRangeException from inside the
        /// coroutine. That kills the pick phase: no cards spawn for anyone, and 0.2.1's barrier then
        /// silently no-opped for the rest of the match.
        ///
        /// Rather than merely warning (which 0.2.1 did, and nobody noticed), stop every timer-start
        /// path from running while we own the pick phase. EveryonePicks provides its own per-player
        /// timer, so no functionality is lost.
        /// </summary>
        private static void NeutralisePickTimer(Harmony h)
        {
            if (AccessTools.TypeByName("PickTimer.PickTimer") == null) return;

            EveryonePicksPlugin.Log("PickTimer detected - suppressing its auto-pick timer during simultaneous picks " +
                                 "(EveryonePicks has its own per-player timer).");

            int patched = 0;

            // Coroutine-returning starts: hand back an empty enumerator instead.
            patched += PatchEnumerator(h, "PickTimer.Util.PickTimerController", "StartPickTimer");
            patched += PatchEnumerator(h, "PickTimer.Util.TimerHandler", "Start");
            patched += PatchEnumerator(h, "PickTimerCompat.Plugin", "FixedStartPickTimer");

            // Void starts: just skip.
            patched += PatchVoid(h, "PickTimerCompat.Plugin", "RetriggerTimer");

            EveryonePicksPlugin.Log("PickTimer suppression: " + patched + " entry point(s) patched.");
        }

        private static int PatchEnumerator(Harmony h, string typeName, string methodName)
        {
            var t = AccessTools.TypeByName(typeName);
            var m = t != null ? AccessTools.Method(t, methodName) : null;
            if (m == null) return 0;
            try
            {
                h.Patch(m, prefix: new HarmonyMethod(typeof(SoftPatches), nameof(SuppressEnumerator)) { priority = Priority.First });
                return 1;
            }
            catch (Exception e)
            {
                EveryonePicksPlugin.Warn("Could not suppress " + typeName + "." + methodName + ": " + e.Message);
                return 0;
            }
        }

        private static int PatchVoid(Harmony h, string typeName, string methodName)
        {
            var t = AccessTools.TypeByName(typeName);
            var m = t != null ? AccessTools.Method(t, methodName) : null;
            if (m == null) return 0;
            try
            {
                h.Patch(m, prefix: new HarmonyMethod(typeof(SoftPatches), nameof(SkipWhenPhaseActive)) { priority = Priority.First });
                return 1;
            }
            catch (Exception e)
            {
                EveryonePicksPlugin.Warn("Could not suppress " + typeName + "." + methodName + ": " + e.Message);
                return 0;
            }
        }

        private static bool SuppressEnumerator(ref IEnumerator __result)
        {
            if (!State.phaseActive && !State.localPickActive) return true;
            __result = EmptyRoutine();
            return false;
        }

        private static IEnumerator EmptyRoutine() { yield break; }

        private static bool SkipWhenPhaseActive() => !State.phaseActive && !State.localPickActive;

        private static void OnResetMatch() => State.HardReset("RWFGameMode.ResetMatch");

        private static void OnPickOrderResolved(List<Player> __result)
        {
            try { State.SeedPickers(__result); }
            catch (Exception e) { EveryonePicksPlugin.Warn("Seeding pick order failed: " + e.Message); }
        }

        // ---------- helpers ----------

        private static object ResolveSingleton(Type t)
        {
            if (t == null) return null;

            var field = AccessTools.Field(t, "instance") ?? AccessTools.Field(t, "Instance");
            if (field != null)
            {
                try { return field.GetValue(null); } catch { }
            }

            // CurseManager.instance is a PROPERTY, not a field - missing this made the curse relay
            // dead code in an earlier build.
            var getter = AccessTools.PropertyGetter(t, "instance") ?? AccessTools.PropertyGetter(t, "Instance");
            if (getter != null)
            {
                try { return getter.Invoke(null, null); } catch { }
            }
            return null;
        }

        internal static bool IsCursed(GameObject card)
        {
            try { return cursedCardType != null && card.GetComponent(cursedCardType) != null; }
            catch { return false; }
        }

        internal static void CursePlayer(Player p)
        {
            try
            {
                var method = curseMgrType != null
                    ? AccessTools.Method(curseMgrType, "CursePlayer", new[] { typeof(Player) })
                    : null;
                if (method == null) return;

                method.Invoke(method.IsStatic ? null : ResolveSingleton(curseMgrType), new object[] { p });
                EveryonePicksPlugin.Log("Cursed pick relayed: CursePlayer(" + p.playerID + ").");
            }
            catch (Exception e)
            {
                EveryonePicksPlugin.Warn("CursePlayer relay failed: " + (e.InnerException?.Message ?? e.Message));
            }
        }

        internal static void FeedPickTracker(CardInfo card)
        {
            try
            {
                if (pickTrackerType == null || card == null) return;
                var method = AccessTools.Method(pickTrackerType, "AddCardPickedInPickPhase");
                if (method == null) return;
                method.Invoke(method.IsStatic ? null : ResolveSingleton(pickTrackerType), new object[] { card });
            }
            catch { }
        }

        internal static void QueueReveal(Player p, CardInfo card)
        {
            try
            {
                if (cardBarUtilsType == null) return;
                var method = AccessTools.Method(cardBarUtilsType, "ShowAtEndOfPhase", new[] { typeof(Player), typeof(CardInfo) });
                var instance = ResolveSingleton(cardBarUtilsType);
                if (method == null || instance == null) return;
                method.Invoke(instance, new object[] { p, card });
            }
            catch { }
        }
    }
}
