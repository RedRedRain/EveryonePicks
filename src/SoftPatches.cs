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

        private static Type nullCardInfoType;
        private static FieldInfo nullSourceField;
        private static FieldInfo nullManagerInstance;
        private static MethodInfo nullValueMethod;
        private static MethodInfo nullInfoMethod;
        private static MethodInfo adjustNullsMethod;

        /// <summary>
        /// NullManager's own wire format for a null card: this prefix plus the nulled card's
        /// object name. Its RPCA_AssignCard prefix, its GetCardInfoWithName postfix and its
        /// GameSaver serialisation all speak it, so our results do too.
        /// </summary>
        internal const string NullPrefix = "___NULL___";

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
            ResolveNullManager();
        }

        /// <summary>
        /// PickTimer auto-picks by indexing CardChoice.spawnedCards, which we clear when tearing
        /// down a local session, so an in-flight timer hits an empty list and throws out of its
        /// coroutine, killing the pick phase. Block every timer-start path while we own the
        /// phase. We have our own per-player timer, so nothing is lost.
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

        // ---------- nulls ----------

        private static void ResolveNullManager()
        {
            nullCardInfoType = AccessTools.TypeByName("Nullmanager.NullCardInfo");
            if (nullCardInfoType == null) return;

            // Note the typo in the field name - it is NullManager's, not ours.
            nullSourceField = AccessTools.Field(nullCardInfoType, "NulledSorce");

            var mgrType = AccessTools.TypeByName("Nullmanager.NullManager");
            if (mgrType != null)
            {
                nullManagerInstance = AccessTools.Field(mgrType, "instance");
                nullValueMethod = AccessTools.Method(mgrType, "GetNullValue", new[] { typeof(CardInfo.Rarity) });
                // Two overloads, by id and by Player - pin the one we want.
                nullInfoMethod = AccessTools.Method(mgrType, "GetNullCardInfo", new[] { typeof(string), typeof(int) });
            }

            var extType = AccessTools.TypeByName("Nullmanager.CharacterStatModifiersExtension");
            if (extType != null)
                adjustNullsMethod = AccessTools.Method(extType, "AjustNulls", new[] { typeof(CharacterStatModifiers), typeof(int) });

            EveryonePicksPlugin.Log("NullManager detected - null picks will be relayed as " + NullPrefix + " assignments.");
        }

        private static object NullManagerInstance()
        {
            try { return nullManagerInstance != null ? nullManagerInstance.GetValue(null) : null; }
            catch { return null; }
        }

        internal static bool IsNullCard(CardInfo card)
        {
            try { return nullCardInfoType != null && card != null && nullCardInfoType.IsInstanceOfType(card); }
            catch { return false; }
        }

        /// <summary>
        /// The name a null card has to travel under, or null if this is not one.
        ///
        /// A NullCardInfo is a component NullManager adds to its OWN GameObject at runtime, so
        /// every one of them reports the same Unity object name and none of them is in the card
        /// registry. Reporting that name left remote clients with a pick they could not resolve,
        /// so the card never reached their copy of the player: nulls, anti-cards and reforges all
        /// read an empty hand everywhere but the picker's screen.
        /// </summary>
        internal static string NullWireName(CardInfo card)
        {
            try
            {
                if (!IsNullCard(card) || nullSourceField == null) return null;
                var source = nullSourceField.GetValue(card) as CardInfo;
                if (source == null || string.IsNullOrEmpty(source.name)) return null;
                return NullPrefix + source.name;
            }
            catch { return null; }
        }

        internal static bool IsNullWireName(string cardName)
        {
            return !string.IsNullOrEmpty(cardName) && cardName.StartsWith(NullPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// The NullCardInfo a wire name stands for, built for this specific player so the card bar
        /// and the reveal show their null count rather than a stranger's. NullManager caches these
        /// per player, so repeated calls return the same instance.
        /// </summary>
        internal static CardInfo ResolveNullCard(string cardName, int playerID)
        {
            try
            {
                if (!IsNullWireName(cardName) || nullInfoMethod == null) return null;
                var mgr = NullManagerInstance();
                if (mgr == null) return null;
                return nullInfoMethod.Invoke(mgr, new object[] { cardName.Substring(NullPrefix.Length), playerID }) as CardInfo;
            }
            catch { return null; }
        }

        /// <summary>
        /// Spend the nulls a relayed null pick cost.
        ///
        /// NullManager charges them from its ApplyCardStats.ApplyStats postfix, which only runs
        /// where the draft card exists - and draft cards are spawned local to the picker. Its
        /// RPCA_AssignCard prefix files the card but never touches the counter, because in a
        /// normal round every client has already charged itself. So every client but the picker's
        /// has to be charged here, or their copy of that player keeps nulls already spent and goes
        /// on offering them cards to null.
        /// </summary>
        internal static void ChargeNulls(Player player, CardInfo nullCard)
        {
            try
            {
                if (player == null || player.data == null || player.data.stats == null) return;
                if (!IsNullCard(nullCard)) return;
                if (adjustNullsMethod == null || nullValueMethod == null) return;

                var mgr = NullManagerInstance();
                if (mgr == null) return;

                int cost = (int)nullValueMethod.Invoke(mgr, new object[] { nullCard.rarity });
                if (cost <= 0) return;

                adjustNullsMethod.Invoke(null, new object[] { player.data.stats, -cost });
                EveryonePicksPlugin.Log("Null pick relayed: charged " + cost + " null(s) to player " + player.playerID + ".");
            }
            catch (Exception e)
            {
                EveryonePicksPlugin.Warn("Null charge failed: " + (e.InnerException?.Message ?? e.Message));
            }
        }
    }
}
