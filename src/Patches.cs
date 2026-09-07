using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnboundLib;
using UnityEngine;

namespace EveryonePicks
{
    // ---------------------------------------------------------------- pick orchestration

    [HarmonyPatch(typeof(CardChoice), "DoPick")]
    internal static class Patch_DoPick
    {
        /// <summary>
        /// Every pick request that arrives while a phase is live becomes an entitlement, whoever
        /// asked for it.
        ///
        /// 0.2.1 gated this on a whitelist of caller namespaces (RWF.GameModes, PickNCards,
        /// GM_ArmsRace, GM_Test). WillsWackyManagers grants its extra picks from its own
        /// PlayerPickEnd hook and matches none of them, so those picks fell through to a
        /// sequential vanilla pass-through whose run-or-skip decision was an unsynchronised
        /// local wall-clock compare. In a real six-player round that silently destroyed three of
        /// five granted extra picks and stalled the phase for 60 seconds on a fourth.
        ///
        /// A whitelist can only know about the mods it was written against, so there isn't one
        /// any more.
        /// </summary>
        private static bool Prefix(int picksToSet, int picketIDToSet, PickerType pType,
                                   ref IEnumerator __result, bool __runOriginal)
        {
            if (!State.phaseActive) return true;

            // Our own session drives the native flow through StartPick, not DoPick. This guard is
            // belt and braces against a third-party patch bouncing us back in here.
            if (State.startingOwnSession) return true;

            int picks = Mathf.Max(1, picksToSet);

            // A higher-priority prefix already cancelled this pick (PickNCards can set the count
            // to zero). Release whatever the pick order optimistically seeded for it.
            if (!__runOriginal)
            {
                State.CancelSeededPick(picketIDToSet, picks);
                return false;
            }

            // This pick may already have been registered from the resolved pick order; only the
            // surplus (extra picks granted by cards) is new.
            int fresh = picks - State.ConsumeSeed(picketIDToSet, picks);
            if (fresh > 0)
            {
                var player = State.FindPlayer(picketIDToSet);

                // ONLY register a surplus pick for our own player. Mods can issue DoPick for a
                // remote player off client-local state - WillsWackyManagers' Group Winnings walks
                // every player and spends a `shuffles` counter that only exists on the client that
                // picked the curse. Recording that as an obligation created a session no other
                // client knew about and that this client could never run, so the barrier waited
                // out its entire budget. Remote picks are learned from their own broadcasts.
                if (player != null && State.IsLocalPlayer(player))
                {
                    for (int i = 0; i < fresh; i++)
                    {
                        State.RecordEntitlement(picketIDToSet, pType);
                        State.EnqueueLocalPick(picketIDToSet, pType);
                    }
                }
            }

            __result = Noop();
            return false;
        }

        private static IEnumerator Noop() { yield break; }
    }

    /// <summary>
    /// Capture every card the player actually ACQUIRES during their session, not just the one the
    /// pick UI reported.
    ///
    /// Capturing CardChoice.Pick only ever saw the single clicked card. A mod that hands the
    /// player more cards during the same pick - Nulled Cards' Distill Acquisition sweeps the whole
    /// remaining hand, for instance - routes each one through ApplyCardStats.Pick without ever
    /// touching CardChoice.Pick. Those cards applied on the picker's machine and nowhere else,
    /// because RPCA_Pick is localised while a session runs. Capturing here instead means anything
    /// a player ends up holding gets into the apply-set, whichever mod granted it, with no
    /// per-mod knowledge required.
    ///
    /// ApplyCardStats' own `done` flag guarantees exactly one record per card object.
    /// </summary>
    [HarmonyPatch(typeof(ApplyCardStats), "Pick")]
    internal static class Patch_Pick_Capture
    {
        private static readonly FieldInfo F_sourceCard = AccessTools.Field(typeof(CardInfo), "sourceCard");

        private static void Prefix(ApplyCardStats __instance, int pickerID, ref bool ___done)
        {
            if (!State.localPickActive || ___done) return;
            if (pickerID != State.currentSessionPlayer) return;

            GameObject root;
            try { root = __instance.transform.root.gameObject; }
            catch { return; }
            if (root == null) return;

            var info = root.GetComponentInChildren<CardInfo>(true);
            if (info == null) return;

            CardInfo source = null;
            try { source = (CardInfo)F_sourceCard.GetValue(info); } catch { }

            string name = source != null ? source.name : info.name.Replace("(Clone)", "");
            if (string.IsNullOrEmpty(name)) return;

            State.currentSessionPicks.Add(new ResultEntry
            {
                card = name,
                cursed = SoftPatches.IsCursed(root)
            });
        }
    }

    // ---------------------------------------------------------------- visuals

    [HarmonyPatch(typeof(CardChoiceVisuals), "Show")]
    internal static class Patch_Visuals_Show
    {
        private static bool Prefix()
        {
            if (State.passThroughActive || State.allowNativeVisualCall) return true;
            if (State.phaseActive || State.localPickActive) return false;
            return true;
        }
    }

    [HarmonyPatch(typeof(CardChoiceVisuals), "Hide")]
    internal static class Patch_Visuals_Hide
    {
        private static bool Prefix()
        {
            if (State.passThroughActive || State.allowNativeVisualCall) return true;
            if (State.phaseActive || State.localPickActive) return false;
            return true;
        }
    }

    /// <summary>
    /// Kills the "avatar parade". The orchestrator still walks every picker in turn (our DoPick
    /// prefix returns instantly), and each step raised the vanilla "X is picking" banner, so the
    /// phase opened by flicking through everyone before landing on you. Only the session actually
    /// running on this client is allowed to raise it.
    /// </summary>
    [HarmonyPatch(typeof(UIHandler), "ShowPicker")]
    internal static class Patch_UIHandler_ShowPicker
    {
        private static bool Prefix(int pickerID)
        {
            if (State.passThroughActive || State.allowNativeVisualCall) return true;
            if (!State.phaseActive && !State.localPickActive) return true;
            return State.localPickActive && pickerID == State.currentSessionPlayer;
        }
    }

    // ---------------------------------------------------------------- local-only spawning

    /// <summary>Marks the window in which the game is building a draft card.</summary>
    [HarmonyPatch(typeof(CardChoice), "SpawnUniqueCard")]
    internal static class Patch_CardSpawnWindow
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix() => State.cardSpawnDepth++;
        private static void Finalizer() { if (State.cardSpawnDepth > 0) State.cardSpawnDepth--; }
    }

    [HarmonyPatch(typeof(CardChoice), "AddCard")]
    internal static class Patch_CardSpawnWindow_AddCard
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix() => State.cardSpawnDepth++;
        private static void Finalizer() { if (State.cardSpawnDepth > 0) State.cardSpawnDepth--; }
    }

    [HarmonyPatch(typeof(CardChoice), "Spawn")]
    internal static class Patch_Spawn_Local
    {
        private static bool Prefix(GameObject objToSpawn, Vector3 pos, Quaternion rot, ref GameObject __result)
        {
            if (!State.localPickActive) return true;

            __result = UnityEngine.Object.Instantiate(objToSpawn, pos, rot);
            foreach (var view in __result.GetComponentsInChildren<PhotonView>(true))
            {
                if (view.ViewID == 0)
                {
                    try { PhotonNetwork.AllocateViewID(view); } catch { }
                }
            }
            State.TrackLocalCard(__result);
            return false;
        }
    }

    [HarmonyPatch(typeof(CardChoice), "SpawnUniqueCard")]
    internal static class Patch_SpawnUniqueCard_Track
    {
        private static void Postfix(GameObject __result)
        {
            if (!State.localPickActive || __result == null) return;

            State.TrackLocalCard(__result);

            foreach (var view in __result.GetComponentsInChildren<PhotonView>(true))
            {
                if (view.ViewID == 0)
                {
                    try { PhotonNetwork.AllocateViewID(view); } catch { }
                }
            }
        }
    }

    [HarmonyPatch(typeof(PhotonNetwork), "Instantiate")]
    internal static class Patch_Photon_Instantiate
    {
        private static readonly FieldInfo instantiationDataField = AccessTools.Field(typeof(PhotonView), "instantiationDataField");

        private static bool Prefix(string prefabName, Vector3 position, Quaternion rotation, object[] data, ref GameObject __result)
        {
            // Only redirect while a DRAFT CARD is being spawned. Redirecting for the whole
            // session localised everything a card effect created and then destroyed it.
            if (!State.localPickActive || !State.SpawningCard) return true;

            GameObject go = null;
            try { go = PhotonNetwork.PrefabPool.Instantiate(prefabName, position, rotation); }
            catch { }

            if (go == null)
            {
                EveryonePicksPlugin.Warn("Local instantiate failed for " + prefabName + " - spawn suppressed.");
                __result = null;
                return false;
            }

            foreach (var view in go.GetComponentsInChildren<PhotonView>(true))
            {
                if (view.ViewID == 0)
                {
                    try { PhotonNetwork.AllocateViewID(view); }
                    catch (Exception e) { EveryonePicksPlugin.Warn("AllocateViewID: " + e.Message); }
                }
                if (data != null && instantiationDataField != null)
                {
                    try { instantiationDataField.SetValue(view, data); } catch { }
                }
            }

            go.SetActive(true);

            // PUN hands this callback a real PhotonMessageInfo built around the new view.
            // default(PhotonMessageInfo).photonView is NULL, so any card that reads
            // info.photonView.InstantiationData - which is how NullManager rebuilds a null card -
            // threw immediately and silently produced an uninitialised card.
            var firstView = go.GetComponentInChildren<PhotonView>(true);
            var info = new PhotonMessageInfo(PhotonNetwork.LocalPlayer, PhotonNetwork.ServerTimestamp, firstView);

            foreach (var cb in go.GetComponents<IPunInstantiateMagicCallback>())
            {
                try { cb.OnPhotonInstantiate(info); }
                catch (Exception e) { EveryonePicksPlugin.Warn("OnPhotonInstantiate: " + e.Message); }
            }

            State.TrackLocalCard(go);
            __result = go;
            return false;
        }
    }

    // ---------------------------------------------------------------- send shield

    [HarmonyPatch(typeof(PhotonView), "RPC", new[] { typeof(string), typeof(RpcTarget), typeof(object[]) })]
    internal static class Patch_PhotonView_RPC
    {
        // (component type, rpc name) -> handler. 0.2.1 re-resolved this per call, producing the
        // "AccessTools.Method: Could not find method" storm seen in the logs.
        private static readonly Dictionary<string, MethodInfo> cache = new Dictionary<string, MethodInfo>();

        private static bool Prefix(PhotonView __instance, string methodName, object[] parameters)
        {
            if (!State.localPickActive) return true;
            if (!State.RedirectLocalRpcs.Contains(methodName)) return true;

            InvokeLocally(__instance, methodName, parameters);
            return false;
        }

        internal static void InvokeLocally(PhotonView view, string methodName, object[] parameters)
        {
            foreach (var mb in view.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;

                var type = mb.GetType();
                string key = type.FullName + "::" + methodName;

                if (!cache.TryGetValue(key, out var method))
                {
                    method = AccessTools.Method(type, methodName);
                    cache[key] = method;
                }
                if (method == null) continue;

                try { method.Invoke(mb, parameters ?? Array.Empty<object>()); }
                catch (Exception e)
                {
                    EveryonePicksPlugin.Warn("Local " + methodName + " threw: " + (e.InnerException?.Message ?? e.Message));
                }
                return;
            }

            EveryonePicksPlugin.Warn("Local invoke: no handler for " + methodName + " on view " + view.ViewID);
        }
    }

    [HarmonyPatch(typeof(NetworkingManager), "RPC", new[] { typeof(Type), typeof(string), typeof(object[]) })]
    internal static class Patch_NetworkingManager_RPC
    {
        private static bool Prefix(Type targetType, string methodName, object[] data)
            => HandleManagerRpc(targetType, methodName, data);

        internal static bool HandleManagerRpc(Type targetType, string methodName, object[] data)
        {
            if (!State.localPickActive) return true;
            if (!State.LocalizedManagerRpcs.Contains(methodName)) return true;

            var method = AccessTools.Method(targetType, methodName);
            if (method != null)
            {
                try { method.Invoke(null, data ?? Array.Empty<object>()); }
                catch (Exception e)
                {
                    EveryonePicksPlugin.Warn("Localized " + methodName + " threw: " + (e.InnerException?.Message ?? e.Message));
                }
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(NetworkingManager), "RPC_Others", new[] { typeof(Type), typeof(string), typeof(object[]) })]
    internal static class Patch_NetworkingManager_RPC_Others
    {
        private static bool Prefix(Type targetType, string methodName, object[] data)
        {
            // Our own protocol must always reach the wire, even mid-session.
            if (targetType == typeof(EveryonePicksPlugin)) return true;
            return Patch_NetworkingManager_RPC.HandleManagerRpc(targetType, methodName, data);
        }
    }
}
