#if EP_DEBUG
using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace EveryonePicks
{
    /// <summary>
    /// Fakes a full reveal on a solo machine so the visuals can be checked without a lobby.
    ///
    /// Everything the reveal draws - card size, the fan for a player with several picks, the
    /// grid for a big lobby, layering, figures and name plates - is local presentation driven by
    /// nothing more than a list of (player, card name). So it can be exercised with invented
    /// data, and does not need three clients and two sleeping friends to check.
    ///
    /// It borrows real state (phaseActive, entitlements) so the reveal and banner behave as they
    /// do in a match, and has to give it back: a stuck phaseActive makes every interception in
    /// Patches think a phase is running. Unity does not reliably run a coroutine finally block on
    /// StopCoroutine, so pressing F10 mid-run would strand the restore and the next run would save
    /// the fake state as real. Hence an idempotent restore, run before every trigger, plus a
    /// watchdog.
    ///
    /// Offline or single-player only.
    /// </summary>
    internal static class DebugReveal
    {
        private static Coroutine running;

        /// <summary>How many fake players each press cycles through.</summary>
        private static readonly int[] Sizes = { 2, 4, 6, 10 };
        private static int step;

        // Borrowed real state, held here rather than in coroutine locals so it can be given back
        // even when the coroutine never reaches its own finally.
        private static bool borrowed;
        private static bool hadPhase;
        private static readonly Dictionary<int, int> savedEnt = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> savedDone = new Dictionary<int, int>();

        /// <summary>Unscaled time by which a run must have finished, or it is assumed dead.</summary>
        private static float deadline;

        internal static bool Allowed()
        {
            if (!PhotonNetwork.OfflineMode && PhotonNetwork.CurrentRoom != null
                && PhotonNetwork.CurrentRoom.PlayerCount > 1) return false;
            return CardChoice.instance != null
                   && CardChoice.instance.cards != null
                   && CardChoice.instance.cards.Length > 0
                   && PlayerManager.instance != null;
        }

        internal static void Trigger(MonoBehaviour host)
        {
            // Whatever the last run left behind, hand it back before borrowing again.
            if (running != null) { host.StopCoroutine(running); running = null; }
            Release();
            PickReveal.Cleanup();
            WaitingBanner.ForceHide();

            int players = Sizes[step % Sizes.Length];
            step++;

            running = host.StartCoroutine(Run(players));
        }

        /// <summary>
        /// Called every frame. Ends a run that outlived its schedule, or one still going when a
        /// real match starts, so simulated state can never follow the player into a lobby.
        /// </summary>
        internal static void Watchdog(MonoBehaviour host)
        {
            if (!borrowed) return;

            bool realMatch = !PhotonNetwork.OfflineMode && PhotonNetwork.CurrentRoom != null
                             && PhotonNetwork.CurrentRoom.PlayerCount > 1;

            if (!realMatch && Time.unscaledTime < deadline) return;

            EveryonePicksPlugin.Log(realMatch
                ? "Debug reveal: real match started, dropping simulated state."
                : "Debug reveal: run overran, dropping simulated state.");

            if (running != null) { host.StopCoroutine(running); running = null; }
            PickReveal.Cleanup();
            WaitingBanner.ForceHide();
            Release();
        }

        private static void Borrow(float expectedSeconds)
        {
            if (borrowed) return;      // never save state the sim itself wrote

            hadPhase = State.phaseActive;

            savedEnt.Clear();
            foreach (var kv in State.entitlements) savedEnt[kv.Key] = kv.Value;
            savedDone.Clear();
            foreach (var kv in State.sessionsDone) savedDone[kv.Key] = kv.Value;

            borrowed = true;
            deadline = Time.unscaledTime + expectedSeconds + 10f;

            State.phaseActive = true;
        }

        private static void Release()
        {
            if (!borrowed) return;
            borrowed = false;

            State.entitlements.Clear();
            foreach (var kv in savedEnt) State.entitlements[kv.Key] = kv.Value;
            State.sessionsDone.Clear();
            foreach (var kv in savedDone) State.sessionsDone[kv.Key] = kv.Value;

            savedEnt.Clear();
            savedDone.Clear();

            State.phaseActive = hadPhase;
        }

        private static IEnumerator Run(int players)
        {
            float hold = Mathf.Clamp(EveryonePicksPlugin.RevealSeconds.Value, 1f, 10f) + 4f;
            Borrow(4f + players * 0.35f + hold);

            EveryonePicksPlugin.Log("Debug reveal: simulating " + players +
                                    " player(s). Press F10 again to cycle 2 / 4 / 6 / 10. " +
                                    "Hover a stack of 2-3 cards to spread it.");

            try
            {
                var pool = CardChoice.instance.cards;
                var real = PlayerManager.instance.players;

                // Show the real sequence: WAITING first, then cards arriving, which clears it.
                // The banner deliberately hides whenever anything is on screen, so a sim that
                // opened straight onto cards could never demonstrate it.
                if (real != null && real.Count > 0)
                {
                    State.entitlements.Clear();
                    State.sessionsDone.Clear();
                    for (int i = 0; i < players; i++)
                    {
                        int waitPid = real[i % real.Count].playerID + i * 1000;
                        State.entitlements[waitPid] = 1;
                    }

                    EveryonePicksPlugin.Log("Debug reveal: showing WAITING for 4s before the cards.");
                    yield return new WaitForSecondsRealtime(4f);
                }

                for (int i = 0; i < players; i++)
                {
                    // Reuse real player ids where they exist so figures and colours are genuine.
                    int pid = (real != null && real.Count > 0) ? real[i % real.Count].playerID : i;

                    // Several multi-card hands, including neighbours, so hovering can be checked
                    // where a spread stack has to overlap the players sitting next to it.
                    int cards = 1;
                    if (i == 1) cards = 2;
                    else if (i == 2 && players >= 4) cards = 3;
                    else if (i == 4 && players >= 6) cards = 2;
                    else if (i == 5 && players >= 6) cards = 2;

                    for (int c = 0; c < cards; c++)
                    {
                        var info = pool[Random.Range(0, pool.Length)];
                        if (info == null) continue;

                        // Unique index per fake entry, and a unique player key per column so the
                        // grid gets the right number of slots even in a one-player lobby.
                        PickReveal.AddDebug(pid + i * 1000, c, info.name);
                    }

                    yield return new WaitForSecondsRealtime(0.35f);
                }

                yield return new WaitForSecondsRealtime(hold);
            }
            finally
            {
                PickReveal.Cleanup();
                WaitingBanner.ForceHide();
                Release();
                running = null;
            }
        }
    }
}
#endif
