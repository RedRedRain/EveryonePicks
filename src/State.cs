using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnboundLib;
using UnboundLib.Utils;
using UnityEngine;

namespace EveryonePicks
{
    /// <summary>
    /// Per-player pick state, mirrored on every client so the board can show who is still choosing.
    /// </summary>
    internal class PickStatus
    {
        public int remaining;      // picks this player still owes (>=1 while choosing)
        public bool done;          // their session finished
        public float lastUpdate;   // realtime of the last status we heard
        public float pickSeconds;  // the SENDER's allowance, so our countdown matches their clock
    }

    internal static class State
    {
        internal static int phaseToken;
        internal static float phaseStartTime;
        internal static bool phaseActive;

        /// <summary>
        /// True once the barrier has stopped waiting, whether everyone reported or we gave up on
        /// someone. The phase stays active past this point while results are applied and revealed,
        /// so anything that nags about slow players has to stop here or it carries on naming a
        /// player we have already decided to continue without.
        /// </summary>
        internal static bool waitingOver;
        internal static bool passThroughActive;

        internal static readonly Dictionary<int, int> entitlements = new Dictionary<int, int>();
        internal static readonly Dictionary<int, PickerType> entitledType = new Dictionary<int, PickerType>();
        internal static readonly Dictionary<int, int> sessionsDone = new Dictionary<int, int>();
        internal static readonly Dictionary<int, List<ResultEntry>> results = new Dictionary<int, List<ResultEntry>>();
        internal static readonly Dictionary<int, int> producedCount = new Dictionary<int, int>();
        internal static readonly HashSet<string> locallyProduced = new HashSet<string>();

        /// <summary>Live per-player pick status, driven by RPCA_SimulStatus. Feeds the pick board.</summary>
        internal static readonly Dictionary<int, PickStatus> statuses = new Dictionary<int, PickStatus>();

        /// <summary>Realtime of the most recent progress signal from ANY client this phase.</summary>
        internal static float lastProgressTime;

        internal static string[] pendingApplySet;
        internal static bool localPickActive;
        internal static bool allowNativeVisualCall;
        internal static int currentSessionPlayer = -1;

        internal static readonly List<GameObject> localCards = new List<GameObject>();
        internal static readonly List<ResultEntry> currentSessionPicks = new List<ResultEntry>();
        internal static readonly Queue<KeyValuePair<int, PickerType>> localQueue = new Queue<KeyValuePair<int, PickerType>>();
        internal static bool localRunnerBusy;

        /// <summary>True while this client still owes or is running a pick of its own.</summary>
        internal static bool LocalBusy
        {
            get
            {
                try { return localPickActive || localRunnerBusy || localQueue.Count > 0; }
                catch { return false; }
            }
        }
        private static Coroutine runner;

        /// <summary>Set only while we drive the native StartPick, so our own flow is never re-intercepted.</summary>
        internal static bool startingOwnSession;

        /// <summary>
        /// Non-zero only while a draft card is being spawned. The instantiate redirect used to
        /// cover the whole session, so swords, minions and anything else a card spawned were made
        /// local-only and destroyed at teardown. Counted, since card spawns can re-enter.
        /// </summary>
        internal static int cardSpawnDepth;

        internal static bool SpawningCard => cardSpawnDepth > 0;

        /// <summary>
        /// Track an object for teardown only if it is actually a draft card. Anything else
        /// belongs to a card effect and must outlive the pick.
        /// </summary>
        internal static void TrackLocalCard(GameObject go)
        {
            if (go == null) return;
            try { if (go.GetComponentInChildren<CardInfo>(true) == null) return; }
            catch { return; }
            if (!localCards.Contains(go)) localCards.Add(go);
        }

        /// <summary>Picks a client actually produced, reported via RPCA_SimulSessionDone's count.</summary>
        internal static readonly Dictionary<int, int> observedPicks = new Dictionary<int, int>();

        /// <summary>Entitlements resolved up front from the pick order, awaiting their DoPick call.</summary>
        internal static readonly Dictionary<int, int> seeded = new Dictionary<int, int>();

        /// <summary>
        /// Seeding happened this phase. Must NOT be inferred from `seeded` being non-empty:
        /// ConsumeSeed drains that dictionary as the pick loop walks, which re-opened the gate and
        /// let a second GetPickOrder call seed the whole lobby again. The barrier then waited on
        /// sessions that were never going to exist (observed live: 4 players, 7 sessions).
        /// </summary>
        private static bool seededThisPhase;

        /// <summary>
        /// Protocol messages that arrived for the NEXT phase before our own PickStart hook ran.
        /// Seeding removed the per-player sync delay that used to hide this, so a fast client's
        /// results can now genuinely outrun a slow client's phase increment. Dropping them lost
        /// that player's card everywhere but their own screen.
        /// </summary>
        private static readonly List<KeyValuePair<int, Action>> earlyArrivals = new List<KeyValuePair<int, Action>>();

        private static bool DeferIfEarly(int token, Action apply)
        {
            if (token != phaseToken + 1) return false;
            if (earlyArrivals.Count >= 128) earlyArrivals.RemoveAt(0);
            earlyArrivals.Add(new KeyValuePair<int, Action>(token, apply));
            return true;
        }

        private static void ReplayEarlyArrivals()
        {
            if (earlyArrivals.Count == 0) return;
            var due = new List<Action>();
            for (int i = earlyArrivals.Count - 1; i >= 0; i--)
            {
                if (earlyArrivals[i].Key > phaseToken) continue;
                if (earlyArrivals[i].Key == phaseToken) due.Add(earlyArrivals[i].Value);
                earlyArrivals.RemoveAt(i);
            }
            for (int i = due.Count - 1; i >= 0; i--)
            {
                try { due[i](); } catch (Exception e) { EveryonePicksPlugin.Warn("Replay failed: " + e.Message); }
            }
        }

        internal static readonly HashSet<string> RedirectLocalRpcs = new HashSet<string>
        {
            "RPCA_DoEndPick", "RPCA_ChangeSelected", "RPCA_SetCurrentSelected",
            "RPCA_DonePicking", "RPCA_SetFace", "RPCA_Pick"
        };

        internal static readonly HashSet<string> LocalizedManagerRpcs = new HashSet<string>
        {
            "RPCO_AddRemotelySpawnedCard", "SpawnCurseDraw", "FixHandSize"
        };

        // Cached reflection. The 0.2.1 build re-resolved these on every call, which is what produced
        // the AccessTools warning storm in the logs.
        private static readonly FieldInfo F_picks = AccessTools.Field(typeof(CardChoice), "picks");
        private static readonly FieldInfo F_isPlaying = AccessTools.Field(typeof(CardChoice), "isPlaying");
        private static readonly FieldInfo F_pickrID = AccessTools.Field(typeof(CardChoice), "pickrID");
        private static readonly FieldInfo F_pickerType = AccessTools.Field(typeof(CardChoice), "pickerType");
        private static readonly FieldInfo F_spawnedCards = AccessTools.Field(typeof(CardChoice), "spawnedCards");
        private static readonly FieldInfo F_selected = AccessTools.Field(typeof(CardChoice), "currentlySelectedCard");

        internal static IEnumerator OnGameStart()
        {
            phaseToken = 0;
            statuses.Clear();
            yield break;
        }

        internal static IEnumerator OnPickStart()
        {
            phaseToken++;
            phaseStartTime = Time.realtimeSinceStartup;
            waitingOver = false;
            lastProgressTime = phaseStartTime;
            phaseActive = true;
            entitlements.Clear();
            entitledType.Clear();
            sessionsDone.Clear();
            results.Clear();
            producedCount.Clear();
            locallyProduced.Clear();
            localQueue.Clear();
            statuses.Clear();
            observedPicks.Clear();
            seeded.Clear();
            seededThisPhase = false;
            seededThisPhase = false;
            pendingApplySet = null;
            PickReveal.Cleanup();
            ReplayEarlyArrivals();
            yield break;
        }

        // ---------- player helpers ----------

        internal static Player FindPlayer(int playerID)
        {
            try
            {
                var pm = PlayerManager.instance;
                if (pm == null || pm.players == null) return null;
                for (int i = 0; i < pm.players.Count; i++)
                {
                    var p = pm.players[i];
                    if (p != null && p.playerID == playerID) return p;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// `view.Owner` is null for a window after a view appears, and SeedPickers runs the
        /// instant RWF resolves the order, so a null Owner used to read as "not us": the client
        /// queued a pick for nobody and waited forever. OwnerActorNr is a plain int on the view
        /// and is correct even then, so prefer it; Owner is the fallback.
        /// </summary>
        internal static bool IsLocalPlayer(Player p)
        {
            try
            {
                if (PhotonNetwork.OfflineMode) return true;
                if (p == null || p.data == null || p.data.view == null) return true;

                var view = p.data.view;
                if (view.OwnerActorNr > 0 && PhotonNetwork.LocalPlayer != null)
                    return view.OwnerActorNr == PhotonNetwork.LocalPlayer.ActorNumber;

                return view.Owner != null && view.Owner.IsLocal;
            }
            catch { return false; }
        }

        /// <summary>
        /// RWF keeps the Player object alive after its Photon owner leaves, so `view.Owner` stays
        /// non-null and is useless as a presence test. Use the room roster.
        /// </summary>
        internal static bool HasLeftRoom(Player p)
        {
            if (PhotonNetwork.OfflineMode || !PhotonNetwork.InRoom) return false;
            try
            {
                if (p == null || p.data == null || p.data.view == null) return true;

                var owner = p.data.view.Owner;
                if (owner == null) return true;

                var room = PhotonNetwork.CurrentRoom;
                if (room == null || room.Players == null) return false;

                return !room.Players.ContainsKey(owner.ActorNumber);
            }
            catch { return true; }
        }

        internal static string DisplayName(int playerID)
        {
            try
            {
                var p = FindPlayer(playerID);
                if (p != null && p.data != null && p.data.view != null && p.data.view.Owner != null)
                {
                    var nick = p.data.view.Owner.NickName;
                    if (!string.IsNullOrEmpty(nick)) return nick;
                }
                if (p != null && IsLocalPlayer(p) && !string.IsNullOrEmpty(PhotonNetwork.NickName))
                    return PhotonNetwork.NickName;
            }
            catch { }
            return "Player " + ((playerID % 1000) + 1);
        }

        internal static Color PlayerColor(int playerID)
        {
            try
            {
                var skin = PlayerManager.instance.GetColorFromPlayer(playerID % 1000);
                return skin.winText;
            }
            catch { return Color.white; }
        }

        // ---------- entitlement / status bookkeeping ----------

        internal static void RecordEntitlement(int playerID, PickerType pType)
        {
            entitlements.TryGetValue(playerID, out int n);
            entitlements[playerID] = n + 1;
            entitledType[playerID] = pType;

            var st = Status(playerID);
            st.remaining = Math.Max(st.remaining, entitlements[playerID]);
            st.lastUpdate = Time.realtimeSinceStartup;
        }

        /// <summary>Phase token the reconciler has already run for.</summary>
        private static int reconciledToken = -1;

        /// <summary>
        /// Starts our pick if we are entitled to one but no session was ever queued. Late owner
        /// resolution is the known cause (see IsLocalPlayer); this catches it however it happens,
        /// since the alternative is an unplayable match. Once per phase token.
        /// </summary>
        internal static void ReconcileLocalPick()
        {
            if (!phaseActive || waitingOver) return;
            if (reconciledToken == phaseToken) return;
            if (localRunnerBusy || localPickActive || localQueue.Count > 0) return;

            // Give Photon a moment to settle before concluding anything about ownership.
            if (Time.realtimeSinceStartup - phaseStartTime < 3f) return;

            try
            {
                foreach (var kv in entitlements)
                {
                    sessionsDone.TryGetValue(kv.Key, out int done);
                    if (done >= kv.Value) continue;

                    var p = FindPlayer(kv.Key);
                    if (p == null || HasLeftRoom(p) || !IsLocalPlayer(p)) continue;

                    reconciledToken = phaseToken;

                    EveryonePicksPlugin.Warn("Our own pick was never queued - starting it now.");
                    Diagnostics.Report("Recovered your pick");

                    EnqueueLocalPick(kv.Key,
                        entitledType.TryGetValue(kv.Key, out var t) ? t : PickerType.Player);
                    return;
                }
            }
            catch (Exception e) { EveryonePicksPlugin.Warn("Pick reconcile failed: " + e.Message); }
        }

        /// <summary>
        /// Registers every picker at once from RWF's resolved order and starts the local session
        /// immediately. RWF otherwise walks the order one player at a time, each step costing a
        /// WaitForSyncUp, a hook chain and 0.1s, so your own pick only began when the loop reached
        /// you. Its loop still runs behind this, harmlessly.
        /// </summary>
        internal static void SeedPickers(List<Player> order)
        {
            if (!phaseActive || order == null || order.Count == 0) return;
            if (seededThisPhase) return;
            seededThisPhase = true;

            int local = 0;
            foreach (var p in order)
            {
                if (p == null) continue;
                int pid = p.playerID;

                seeded.TryGetValue(pid, out int n);
                seeded[pid] = n + 1;

                // Set a floor, do not add. A status RPC that arrived for this phase before our own
                // PickStart hook ran is replayed at the top of OnPickStart and already registers
                // the sender, so incrementing on top counted them twice. Live symptom: "3 picker(s)
                // ... 4 session(s)", and the barrier then waited 115s on a session nobody owed.
                sessionsDone.TryGetValue(pid, out int done);
                entitlements.TryGetValue(pid, out int owed);
                int want = done + 1;

                if (owed < want)
                {
                    entitlements[pid] = want;
                    entitledType[pid] = PickerType.Player;

                    var st = Status(pid);
                    st.remaining = Math.Max(st.remaining, want - done);
                    st.lastUpdate = Time.realtimeSinceStartup;
                }

                if (IsLocalPlayer(p))
                {
                    EnqueueLocalPick(pid, PickerType.Player);
                    local++;
                }
            }

            EveryonePicksPlugin.Log("Pick order resolved up front: " + order.Count + " picker(s), " +
                                    local + " local - starting immediately. Entitlements now " +
                                    entitlements.Count + " player(s)/" + entitlements.Values.Sum() + " session(s).");
        }

        /// <summary>
        /// Another mod vetoed this pick (PickNCards set "cards to pick" to 0). Undo the seed we
        /// optimistically created for it, or the barrier waits on a session nobody will run.
        /// </summary>
        internal static void CancelSeededPick(int playerID, int count)
        {
            ConsumeSeed(playerID, count);

            entitlements.TryGetValue(playerID, out int owed);
            int left = owed - count;
            if (left <= 0)
            {
                entitlements.Remove(playerID);
                entitledType.Remove(playerID);
                statuses.Remove(playerID);
            }
            else entitlements[playerID] = left;

            if (localQueue.Count > 0)
            {
                var keep = new Queue<KeyValuePair<int, PickerType>>();
                int drop = count;
                while (localQueue.Count > 0)
                {
                    var kv = localQueue.Dequeue();
                    if (kv.Key == playerID && drop > 0) { drop--; continue; }
                    keep.Enqueue(kv);
                }
                while (keep.Count > 0) localQueue.Enqueue(keep.Dequeue());
            }

            EveryonePicksPlugin.Log("Pick for player " + playerID + " vetoed upstream; seed released.");
        }

        /// <summary>Claims up to <paramref name="want"/> already-seeded picks for this player.</summary>
        internal static int ConsumeSeed(int playerID, int want)
        {
            if (!seeded.TryGetValue(playerID, out int have) || have <= 0) return 0;
            int take = Mathf.Min(have, want);
            if (have - take <= 0) seeded.Remove(playerID);
            else seeded[playerID] = have - take;
            return take;
        }

        internal static PickStatus Status(int playerID)
        {
            if (!statuses.TryGetValue(playerID, out var st))
            {
                st = new PickStatus { remaining = 0, done = false, lastUpdate = Time.realtimeSinceStartup };
                statuses[playerID] = st;
            }
            return st;
        }

        /// <summary>Applied on every client (including the sender) from RPCA_SimulStatus.</summary>
        internal static void StoreStatus(int token, int playerID, int remaining, bool done, float senderPickSeconds)
        {
            if (DeferIfEarly(token, () => StoreStatus(token, playerID, remaining, done, senderPickSeconds))) return;
            if (token != phaseToken) return;
            var st = Status(playerID);
            st.remaining = remaining;
            st.done = done;
            st.pickSeconds = senderPickSeconds > 0f ? senderPickSeconds : PickSeconds;
            st.lastUpdate = Time.realtimeSinceStartup;
            lastProgressTime = Time.realtimeSinceStartup;

            // A client telling us it is mid-session is the ONLY trustworthy source of a remote
            // obligation, since it is the only machine that can discharge one. Make sure the
            // barrier waits for a session we now know is running.
            if (!done && remaining > 0)
            {
                // `remaining` is now the count this client still owes, so hold the barrier open
                // for all of it rather than assuming a single outstanding session.
                sessionsDone.TryGetValue(playerID, out int finished);
                entitlements.TryGetValue(playerID, out int owed);
                int needed = finished + Mathf.Max(1, remaining);
                if (owed < needed) entitlements[playerID] = needed;
            }
        }

        private static void BroadcastStatus(int token, int playerID, int remaining, bool done)
        {
            try { Broadcast("RPCA_SimulStatus", token, playerID, remaining, done, PickSeconds); }
            catch (Exception e) { EveryonePicksPlugin.Warn("Status broadcast failed: " + e.Message); }
        }

        internal static void EnqueueLocalPick(int playerID, PickerType pType)
        {
            localQueue.Enqueue(new KeyValuePair<int, PickerType>(playerID, pType));
            if (runner == null)
                runner = EveryonePicksPlugin.Instance.StartCoroutine(RunnerLoop());
        }

        internal static void StoreResult(int token, int playerID, int pickIdx, string cardName, bool cursed)
        {
            if (DeferIfEarly(token, () => StoreResult(token, playerID, pickIdx, cardName, cursed))) return;
            if (token != phaseToken) return;
            if (!results.TryGetValue(playerID, out var list))
            {
                list = new List<ResultEntry>();
                results[playerID] = list;
            }
            while (list.Count <= pickIdx) list.Add(null);
            if (list[pickIdx] == null)
                list[pickIdx] = new ResultEntry { card = cardName ?? "", cursed = cursed };
            lastProgressTime = Time.realtimeSinceStartup;

            // Show it the moment it lands, so players who finished early watch the round fill in.
            PickReveal.Add(playerID, pickIdx, cardName);
        }

        internal static void StoreSessionDone(int token, int playerID, int count)
        {
            if (DeferIfEarly(token, () => StoreSessionDone(token, playerID, count))) return;
            if (token != phaseToken) return;
            sessionsDone.TryGetValue(playerID, out int n);
            sessionsDone[playerID] = n + 1;
            lastProgressTime = Time.realtimeSinceStartup;

            // 0.2.1 threw `count` away, so the barrier budget never learned that a session had
            // actually produced several picks. Keep the largest report per player and let
            // ClusterBudget size itself from reality rather than from DoPick invocations.
            observedPicks.TryGetValue(playerID, out int seen);
            if (count > seen) observedPicks[playerID] = count;

            entitlements.TryGetValue(playerID, out int owed);
            var st = Status(playerID);
            if (sessionsDone[playerID] >= owed)
            {
                st.done = true;
                st.remaining = 0;
            }
            st.lastUpdate = Time.realtimeSinceStartup;
        }

        internal static void StoreApplySet(int token, string[] packed)
        {
            if (DeferIfEarly(token, () => StoreApplySet(token, packed))) return;
            if (token != phaseToken) return;
            pendingApplySet = packed ?? Array.Empty<string>();
        }

        private static void Broadcast(string method, params object[] args)
        {
            try
            {
                AccessTools.Method(typeof(EveryonePicksPlugin), method).Invoke(null, args);
            }
            catch (Exception e)
            {
                EveryonePicksPlugin.Warn("Local " + method + " store failed: " + e.Message);
            }
            if (!PhotonNetwork.OfflineMode && PhotonNetwork.InRoom)
                NetworkingManager.RPC_Others(typeof(EveryonePicksPlugin), method, args);
        }

        // ---------- budget ----------

        internal static float PickSeconds => Mathf.Clamp(EveryonePicksPlugin.PickTimeSeconds.Value, 10, 180);

        /// <summary>
        /// Worst-case wall time for the whole cluster: the busiest single client's session count
        /// times the per-pick allowance, plus slack. Extra picks granted mid-session are NOT
        /// visible here, which is why the barrier also honours live progress (see Barrier()).
        /// </summary>
        internal static float ClusterBudget()
        {
            // Per-player allowance from what THEY reported, not from our own config. A host with
            // a shorter pick time was otherwise cutting everyone else off at their own setting.
            float per = PickSeconds;
            try
            {
                float widest = per;
                foreach (var st in statuses.Values)
                    if (st != null && st.pickSeconds > widest) widest = st.pickSeconds;
                per = Mathf.Clamp(widest, 10f, 180f);
            }
            catch { }

            int worst = 1;
            try
            {
                if (entitlements.Count > 0)
                {
                    var byActor = new Dictionary<int, int>();
                    foreach (var kv in entitlements)
                    {
                        int actor = 0;
                        try
                        {
                            var p = FindPlayer(kv.Key);
                            if (p != null && p.data != null && p.data.view != null && p.data.view.Owner != null)
                                actor = p.data.view.Owner.ActorNumber;
                        }
                        catch { }
                        observedPicks.TryGetValue(kv.Key, out int seen);
                        statuses.TryGetValue(kv.Key, out var live);
                        int liveRemaining = live != null ? live.remaining : 0;

                        // Weight by whichever is largest: what we entitled them to, what they have
                        // reported producing, and what they say is still in front of them. A hand
                        // that grows mid-session must grow the budget with it.
                        int weight = Mathf.Max(kv.Value, Mathf.Max(seen, liveRemaining));

                        byActor.TryGetValue(actor, out int v);
                        byActor[actor] = v + weight;
                    }
                    worst = Mathf.Max(1, byActor.Values.Max());
                }
            }
            catch { }
            return per * worst + 25f;
        }

        // ---------- local session ----------

        private static IEnumerator RunnerLoop()
        {
            localRunnerBusy = true;
            try
            {
                while (localQueue.Count > 0)
                {
                    var kv = localQueue.Dequeue();
                    yield return RunLocalNativePick(kv.Key, kv.Value);
                }
            }
            finally
            {
                localRunnerBusy = false;
                runner = null;
            }
        }

        private static int SafePicks(CardChoice c)
        {
            try { return (int)F_picks.GetValue(c); } catch { return 0; }
        }

        private static IEnumerator RunLocalNativePick(int playerID, PickerType pType)
        {
            int myToken = phaseToken;
            var choice = CardChoice.instance;
            if (choice == null) yield break;

            producedCount.TryGetValue(playerID, out int baseIdx);
            currentSessionPicks.Clear();
            localCards.Clear();
            currentSessionPlayer = playerID;
            localPickActive = true;

            try
            {
                bool started = false;
                try
                {
                    WithNativeVisuals(() => CardChoiceVisuals.instance.Show(playerID, true));
                    F_pickerType.SetValue(choice, pType);
                    startingOwnSession = true;
                    try
                    {
                        Traverse.Create(choice).Method("StartPick", new object[] { 1, playerID }).GetValue();
                    }
                    finally
                    {
                        startingOwnSession = false;
                    }
                    started = true;
                }
                catch (Exception e)
                {
                    EveryonePicksPlugin.Warn("Session start failed: " + e.Message);
                }

                if (started)
                {
                    // Read AFTER StartPick: PickPhaseImprovements' PatchPickStart rewrites
                    // picksToSet by ref, so a player owed extra picks already shows >1 here.
                    int picksSeen = Mathf.Max(1, SafePicks(choice));
                    BroadcastStatus(myToken, playerID, picksSeen, false);

                    float deadline = Time.realtimeSinceStartup + PickSeconds * picksSeen;
                    bool forced = false;

                    while (SafeIsPicking(choice) && myToken == phaseToken)
                    {
                        int picksNow = SafePicks(choice);

                        if (picksNow > picksSeen)
                        {
                            // A card just granted extra picks mid-session (devil cards, Pick N Cards,
                            // MorePicksPatch...). Vanilla loops inside the SAME session, so no second
                            // DoPick arrives - give the player real time for each new pick and tell
                            // everyone else we are legitimately still going.
                            deadline += PickSeconds * (picksNow - picksSeen);
                            picksSeen = picksNow;
                            forced = false;
                            BroadcastStatus(myToken, playerID, picksNow, false);
                            EveryonePicksPlugin.Log("Extra pick(s) granted mid-session -> " + picksNow + " remaining.");
                        }
                        else if (picksNow < picksSeen)
                        {
                            // A pick was consumed; reset the clock for the next one.
                            picksSeen = picksNow;
                            deadline = Time.realtimeSinceStartup + PickSeconds;
                            forced = false;
                            BroadcastStatus(myToken, playerID, Mathf.Max(picksNow, 0), false);
                        }

                        if (Time.realtimeSinceStartup > deadline)
                        {
                            if (forced)
                            {
                                EveryonePicksPlugin.Warn("Session for player " + playerID + " stuck; abandoning.");
                                break;
                            }
                            // One force per pick, not one per session: a multi-pick hand needs to be
                            // driven to completion, and the picks-changed branches above clear this.
                            forced = true;
                            ForceCompletePick(choice);
                            deadline += 10f;
                        }
                        yield return null;
                    }

                    float settle = Time.realtimeSinceStartup + 5f;
                    while (SafeBool(F_isPlaying, choice) && Time.realtimeSinceStartup < settle)
                        yield return null;

                    yield return new WaitForSecondsRealtime(0.2f);
                }

                ScrubCardChoice(choice);
            }
            finally
            {
                CleanupLocalCards();
                currentSessionPlayer = -1;
                localPickActive = false;
                try
                {
                    WithNativeVisuals(() =>
                    {
                        UIHandler.instance.StopShowPicker();
                        CardChoiceVisuals.instance.Hide();
                    });
                }
                catch { }
            }

            ReportSession(myToken, playerID, baseIdx, currentSessionPicks);
        }

        internal static void ReportSession(int token, int playerID, int baseIdx, List<ResultEntry> picks)
        {
            int produced = 0;
            for (int i = 0; i < picks.Count; i++)
            {
                var entry = picks[i];
                if (entry == null) continue;
                try
                {
                    locallyProduced.Add(playerID + "|" + (baseIdx + i));
                    Broadcast("RPCA_SimulResult", token, playerID, baseIdx + i, entry.card, entry.cursed);
                    produced++;
                }
                catch (Exception e) { EveryonePicksPlugin.Warn("Result broadcast failed: " + e.Message); }
            }

            if (produced == 0)
            {
                try
                {
                    locallyProduced.Add(playerID + "|" + baseIdx);
                    Broadcast("RPCA_SimulResult", token, playerID, baseIdx, "", false);
                    produced = 1;
                }
                catch (Exception e) { EveryonePicksPlugin.Warn("Skip broadcast failed: " + e.Message); }
            }

            producedCount[playerID] = baseIdx + produced;

            try { Broadcast("RPCA_SimulSessionDone", token, playerID, produced); }
            catch (Exception e) { EveryonePicksPlugin.Warn("Session marker failed: " + e.Message); }

            // Do NOT announce "done" while this player still owes sessions. A queued session is
            // invisible to everyone else - nothing is broadcast until it actually starts - so
            // claiming done let the barrier close in the gap, and the queued pick then applied
            // on this machine alone.
            int stillQueued = 0;
            foreach (var kv in localQueue) if (kv.Key == playerID) stillQueued++;
            if (localPickActive && currentSessionPlayer == playerID) stillQueued++;

            BroadcastStatus(token, playerID, stillQueued, stillQueued == 0);
        }

        private static bool SafeIsPicking(CardChoice c)
        {
            try { return c != null && c.IsPicking; } catch { return false; }
        }

        private static bool SafeBool(FieldInfo f, object o)
        {
            try { return (bool)f.GetValue(o); } catch { return false; }
        }

        internal static void ScrubCardChoice(CardChoice choice)
        {
            try
            {
                choice.StopAllCoroutines();
                choice.IsPicking = false;
                F_picks.SetValue(choice, 0);
                F_isPlaying.SetValue(choice, false);
                F_pickrID.SetValue(choice, -1);

                var list = (List<GameObject>)F_spawnedCards.GetValue(choice);
                if (list == null) return;
                foreach (var go in list)
                    if (go != null && !localCards.Contains(go)) localCards.Add(go);
                list.Clear();
            }
            catch (Exception e)
            {
                EveryonePicksPlugin.Warn("Scrub failed: " + e.Message);
            }
        }

        internal static void ForceCompletePick(CardChoice choice)
        {
            try
            {
                var list = ((List<GameObject>)F_spawnedCards.GetValue(choice))?.Where(g => g != null).ToList();
                if (list == null || list.Count == 0)
                {
                    choice.IsPicking = false;
                    return;
                }
                int idx = Mathf.Clamp((int)F_selected.GetValue(choice), 0, list.Count - 1);
                EveryonePicksPlugin.Warn("Pick time expired - auto-picking selected card.");
                choice.Pick(list[idx], false);
            }
            catch (Exception e)
            {
                EveryonePicksPlugin.Warn("Force-complete failed: " + e.Message);
                try { choice.IsPicking = false; } catch { }
            }
        }

        internal static void WithNativeVisuals(Action a)
        {
            bool prev = allowNativeVisualCall;
            allowNativeVisualCall = true;
            try { a(); }
            finally { allowNativeVisualCall = prev; }
        }

        private static void CleanupLocalCards()
        {
            foreach (var go in localCards)
            {
                if (go == null) continue;
                foreach (var view in go.GetComponentsInChildren<PhotonView>(true))
                {
                    try { PhotonNetwork.LocalCleanPhotonView(view); } catch { }
                }
                UnityEngine.Object.Destroy(go);
            }
            localCards.Clear();
        }

        private static void FlushRunner(int token)
        {
            if (runner != null)
            {
                try { EveryonePicksPlugin.Instance.StopCoroutine(runner); } catch { }
                runner = null;
            }

            bool wasBusy = localRunnerBusy;
            localRunnerBusy = false;

            var choice = CardChoice.instance;
            if (choice != null && (localPickActive || wasBusy))
            {
                bool prev = localPickActive;
                localPickActive = true;
                try { ScrubCardChoice(choice); }
                finally { localPickActive = prev; }
            }

            if (localPickActive)
            {
                int pid = currentSessionPlayer;
                if (pid >= 0)
                {
                    EveryonePicksPlugin.Warn("Barrier cut a live pick session for player " + pid +
                                             " short; reporting whatever it had produced.");
                    producedCount.TryGetValue(pid, out int baseIdx);
                    ReportSession(token, pid, baseIdx, new List<ResultEntry>(currentSessionPicks));
                }
                localPickActive = false;
                currentSessionPlayer = -1;
            }

            while (localQueue.Count > 0)
            {
                var kv = localQueue.Dequeue();
                producedCount.TryGetValue(kv.Key, out int baseIdx);
                ReportSession(token, kv.Key, baseIdx, new List<ResultEntry>());
            }

            CleanupLocalCards();
            try
            {
                WithNativeVisuals(() =>
                {
                    UIHandler.instance.StopShowPicker();
                    CardChoiceVisuals.instance.Hide();
                });
            }
            catch { }
        }

        // ---------- barrier ----------

        /// <summary>
        /// Grace granted past the nominal budget while clients are still visibly making progress.
        /// Prevents a player working through extra picks from being cut off (and everyone else
        /// from being released early into a desynced state).
        /// </summary>
        private const float ProgressGrace = 30f;

        internal static IEnumerator Barrier()
        {
            int myToken = phaseToken;

            if (entitlements.Count == 0 && localQueue.Count == 0 && !localRunnerBusy)
            {
                // 0.2.1 returned here silently. When a third-party mod broke the pick phase this
                // looked exactly like EveryonePicks not being installed at all - log it instead.
                if (phaseActive)
                    EveryonePicksPlugin.Warn("PickEnd(token " + myToken + ") with no entitlements - " +
                        "EveryonePicks did not run this phase (a pick-phase mod may have thrown before DoPick).");
                phaseActive = false;
                PickReveal.Cleanup();
                WaitingBanner.ForceHide();
                yield break;
            }

            float budget = ClusterBudget();
            bool authority = PhotonNetwork.OfflineMode || !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;

            EveryonePicksPlugin.Log("Barrier(token " + myToken + "): " + entitlements.Count + " player(s), " +
                                 entitlements.Values.Sum() + " session(s), authority=" + authority +
                                 ", budget=" + budget.ToString("F0") + "s.");

            if (authority)
            {
                // Recompute the budget every frame. Freezing it at entry meant a player whose hand
                // grew mid-session was cut off against a deadline set before anyone had picked.
                while (myToken == phaseToken
                       && (localQueue.Count != 0 || localRunnerBusy || !AllSessionsIn())
                       && !BudgetExhausted(phaseStartTime + ClusterBudget()))
                    yield return null;

                if (myToken != phaseToken) yield break;

                ReportBarrierExit(myToken);
                FlushRunner(myToken);
                Broadcast("RPCA_SimulApplySet", myToken, BuildApplySet());
            }
            else
            {
                float fallback = phaseStartTime
                                 + 180f * Mathf.Max(1, entitlements.Count <= 0 ? 1 : entitlements.Values.Max())
                                 + 45f;

                // Wait for our OWN session to finish before consuming a remote apply-set. 0.2.1
                // applied the set the instant it arrived, so a slow client's picks were reported
                // after the set had already been consumed: they landed locally and nowhere else,
                // which is how a player ends up desynced from everyone around them.
                while ((pendingApplySet == null || localRunnerBusy || localPickActive || localQueue.Count > 0)
                       && myToken == phaseToken && Time.realtimeSinceStartup < fallback)
                {
                    // Host migration mid-barrier: take over if we became master.
                    if (PhotonNetwork.IsMasterClient && localQueue.Count == 0 && !localRunnerBusy
                        && (AllSessionsIn() || BudgetExhausted(phaseStartTime + ClusterBudget())))
                    {
                        FlushRunner(myToken);
                        Broadcast("RPCA_SimulApplySet", myToken, BuildApplySet());
                        break;
                    }
                    yield return null;
                }

                // Non-host clients never reach ReportBarrierExit, so clear the flag here too or
                // everyone but the host would carry on being told who they are waiting for.
                waitingOver = true;

                if (myToken != phaseToken) yield break;

                FlushRunner(myToken);
                if (pendingApplySet == null)
                {
                    EveryonePicksPlugin.Warn("Apply-set never arrived - degrading to local results.");
                    pendingApplySet = BuildApplySet();
                }
            }

            var applied = pendingApplySet ?? BuildApplySet();
            WaitingBanner.ForceHide();
            ApplySet(applied);

            if (EveryonePicksPlugin.ShowReveal.Value)
            {
                // Anything we never received live (a dropped result RPC) is added from the
                // authoritative set before the hold, so the reveal is always complete.
                foreach (var row in applied)
                {
                    if (string.IsNullOrEmpty(row)) continue;
                    var bits = row.Split(new[] { '|' }, 4);
                    if (bits.Length != 4) continue;
                    if (!int.TryParse(bits[0], out int rp)) continue;
                    if (!int.TryParse(bits[1], out int ri)) continue;
                    PickReveal.Add(rp, ri, bits[3]);
                }

                yield return PickReveal.Hold();
            }

            // Unconditional: if the option was switched off mid-match the clones from the last
            // phase would otherwise sit in the arena for the whole round.
            PickReveal.Cleanup();

            entitlements.Clear();
            entitledType.Clear();
            sessionsDone.Clear();
            results.Clear();
            statuses.Clear();
            observedPicks.Clear();
            pendingApplySet = null;
            phaseActive = false;

            try
            {
                WithNativeVisuals(() =>
                {
                    UIHandler.instance.StopShowPicker();
                    CardChoiceVisuals.instance.Hide();
                });
            }
            catch { }

            EveryonePicksPlugin.Log("Barrier complete.");
        }

        /// <summary>
        /// Say why the barrier stopped waiting, and name anyone who never reported. Without this
        /// the log showed only that a barrier opened and closed, so a phase held up by one silent
        /// client was indistinguishable from a clean one.
        /// </summary>
        private static void ReportBarrierExit(int token)
        {
            waitingOver = true;

            var missing = new List<string>();
            foreach (var kv in entitlements)
            {
                sessionsDone.TryGetValue(kv.Key, out int done);
                if (done >= kv.Value) continue;

                var p = FindPlayer(kv.Key);
                string why = (p == null || HasLeftRoom(p)) ? "left" : "no response";
                missing.Add(DisplayName(kv.Key) + " (" + why + ")");
            }

            if (missing.Count == 0)
            {
                EveryonePicksPlugin.Log("Barrier(token " + token + "): all sessions reported.");
                Diagnostics.CheckSilentPlayers();
            }
            else
            {
                EveryonePicksPlugin.Warn("Barrier(token " + token + "): released without " +
                                         string.Join(", ", missing.ToArray()) + ".");
                Diagnostics.Report("Continued without " + string.Join(", ", missing.ToArray()));
            }
        }

        /// <summary>
        /// True once we are past the nominal budget AND nobody has reported progress recently.
        /// A player working through extra picks keeps sending status updates, so they extend this.
        /// </summary>
        private static bool BudgetExhausted(float hardDeadline)
        {
            float now = Time.realtimeSinceStartup;
            if (now < hardDeadline) return false;
            return now > lastProgressTime + ProgressGrace;
        }

        internal static bool AllSessionsIn()
        {
            if (PlayerManager.instance == null) return true;

            foreach (var kv in entitlements)
            {
                sessionsDone.TryGetValue(kv.Key, out int done);
                if (done >= kv.Value) continue;

                var p = FindPlayer(kv.Key);
                // A player who left the room can never report - do not block on them.
                if (p != null && !HasLeftRoom(p)) return false;
            }
            return true;
        }

        /// <summary>Called when a Photon player leaves; releases the barrier from waiting on them.</summary>
        internal static void DropDepartedPlayers()
        {
            if (!phaseActive) return;
            var drop = new List<int>();
            foreach (var kv in entitlements)
            {
                var p = FindPlayer(kv.Key);
                if (p == null || HasLeftRoom(p)) drop.Add(kv.Key);
            }
            foreach (int pid in drop)
            {
                EveryonePicksPlugin.Log("Player " + pid + " left mid-phase - releasing their pick slot.");
                entitlements.Remove(pid);
                entitledType.Remove(pid);
                sessionsDone.Remove(pid);
                statuses.Remove(pid);
            }
            if (drop.Count > 0) lastProgressTime = Time.realtimeSinceStartup;
        }

        // ---------- apply ----------

        private static string[] BuildApplySet()
        {
            var rows = new List<string>();
            foreach (int pid in results.Keys.OrderBy(k => k))
            {
                var list = results[pid];
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (e != null && !string.IsNullOrEmpty(e.card))
                        rows.Add(pid + "|" + i + "|" + (e.cursed ? 1 : 0) + "|" + e.card);
                }
            }
            return rows.ToArray();
        }

        private static void ApplySet(string[] set)
        {
            foreach (var row in set)
            {
                try { ApplyRow(row); }
                catch (Exception e) { EveryonePicksPlugin.Warn("Apply row '" + row + "' failed: " + e.Message); }
            }
        }

        private static void ApplyRow(string row)
        {
            // Split with a limit of 4 so card names containing '|' survive intact.
            var parts = row.Split(new[] { '|' }, 4);
            if (parts.Length != 4) return;
            if (!int.TryParse(parts[0], out int pid)) return;
            if (!int.TryParse(parts[1], out int idx)) return;
            bool cursed = parts[2] == "1";
            string cardName = parts[3];

            var player = FindPlayer(pid);
            if (player == null) return;

            var info = FindCard(cardName);
            bool alreadyLocal = PhotonNetwork.OfflineMode || locallyProduced.Contains(pid + "|" + idx);

            if (!alreadyLocal)
            {
                var assign = AccessTools.Method(
                    AccessTools.TypeByName("ModdingUtils.Utils.Cards"),
                    "RPCA_AssignCard",
                    new[] { typeof(string), typeof(int), typeof(bool), typeof(string), typeof(float), typeof(float), typeof(bool) });

                if (assign == null)
                {
                    EveryonePicksPlugin.Warn("ModdingUtils RPCA_AssignCard not found - cannot apply '" + cardName + "'.");
                    return;
                }

                assign.Invoke(null, new object[] { cardName, pid, false, "", 0f, 0f, true });

                // The reveal shows every card at once; ModdingUtils' side-bar crawl is the thing
                // it replaces, so only feed that when the reveal is off.
                if (info != null && !EveryonePicksPlugin.ShowReveal.Value)
                    SoftPatches.QueueReveal(player, info);
            }

            SoftPatches.FeedPickTracker(info);

            if (cursed && !alreadyLocal && PhotonNetwork.IsMasterClient && !PhotonNetwork.OfflineMode)
                SoftPatches.CursePlayer(player);
        }

        internal static CardInfo FindCard(string objectName)
        {
            var choice = CardChoice.instance;
            if (choice != null && choice.cards != null)
            {
                foreach (var c in choice.cards)
                    if (c != null && c.name == objectName) return c;
            }
            try { return CardManager.GetCardInfoWithName(objectName); }
            catch { return null; }
        }

        // ---------- reset ----------

        internal static IEnumerator HardResetHook()
        {
            HardReset("GameEnd hook");
            yield break;
        }

        internal static void HardReset(string why)
        {
            EveryonePicksPlugin.Log("Hard reset: " + why);
            phaseToken = 0;

            if (runner != null)
            {
                try { EveryonePicksPlugin.Instance.StopCoroutine(runner); } catch { }
                runner = null;
            }

            localRunnerBusy = false;
            startingOwnSession = false;
            cardSpawnDepth = 0;
            localQueue.Clear();

            var choice = CardChoice.instance;
            if (choice != null)
            {
                bool prev = localPickActive;
                localPickActive = true;
                try { ScrubCardChoice(choice); }
                finally { localPickActive = prev; }
            }

            CleanupLocalCards();
            PickReveal.Cleanup();
            WaitingBanner.ForceHide();
            localPickActive = false;
            passThroughActive = false;
            currentSessionPlayer = -1;
            phaseActive = false;
            waitingOver = false;

            entitlements.Clear();
            entitledType.Clear();
            sessionsDone.Clear();
            results.Clear();
            producedCount.Clear();
            locallyProduced.Clear();
            statuses.Clear();
            observedPicks.Clear();
            seeded.Clear();
            currentSessionPicks.Clear();
            pendingApplySet = null;

            try
            {
                WithNativeVisuals(() =>
                {
                    UIHandler.instance.StopShowPicker();
                    CardChoiceVisuals.instance.Hide();
                });
            }
            catch { }
        }
    }

    internal class ResultEntry
    {
        public string card;
        public bool cursed;
    }
}
