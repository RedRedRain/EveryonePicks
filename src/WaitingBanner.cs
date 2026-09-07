using System;
using UnityEngine;

namespace EveryonePicks
{
    /// <summary>
    /// Puts WAITING up in the game's own big screen text while you have nothing to look at.
    ///
    /// Uses UIHandler.DisplayScreenTextLoop, the same call ROUNDS uses for its own large
    /// messages, so it matches the game rather than looking bolted on. It clears itself the
    /// moment the first card lands, so it never sits on top of the reveal.
    /// </summary>
    internal static class WaitingBanner
    {
        /// <summary>ROUNDS' warning red.</summary>
        private static readonly Color Red = new Color(0.85f, 0.15f, 0.22f);

        /// <summary>
        /// Only ever stop the loop we started. The game uses the same screen text for its own
        /// messages, and clearing one of those would be someone else's bug to chase.
        /// </summary>
        private static bool showing;

        internal static void Tick()
        {
            bool want;
            try { want = ShouldShow(); }
            catch { want = false; }

            if (want == showing) return;

            try
            {
                if (want)
                {
                    UIHandler.instance.DisplayScreenTextLoop(Red, "WAITING");
                    showing = true;
                }
                else
                {
                    UIHandler.instance.StopScreenTextLoop();
                    showing = false;
                }
            }
            catch
            {
                // If the UI is not up yet, try again next frame rather than latching a wrong state.
                showing = false;
            }
        }

        private static bool ShouldShow()
        {
            if (EveryonePicksPlugin.ShowWaitingText == null || !EveryonePicksPlugin.ShowWaitingText.Value)
                return false;

            if (!State.phaseActive) return false;

            // You are choosing: your hand is on screen, so nothing to wait for.
            if (State.LocalBusy) return false;

            // Something has been revealed, so there is something to look at instead.
            if (PickReveal.Active) return false;

            // The barrier has given up waiting, so we are no longer waiting on anyone even though
            // their session never completed.
            if (State.waitingOver) return false;

            return SomeoneStillOwesAPick();
        }

        private static bool SomeoneStillOwesAPick()
        {
            foreach (var kv in State.entitlements)
            {
                State.sessionsDone.TryGetValue(kv.Key, out int done);
                if (done >= kv.Value) continue;

                var p = State.FindPlayer(kv.Key);
                if (p == null || State.HasLeftRoom(p)) continue;

                return true;
            }
            return false;
        }

        /// <summary>Clear it unconditionally, for phase end and hard resets.</summary>
        internal static void ForceHide()
        {
            if (!showing) return;
            showing = false;
            try { UIHandler.instance.StopScreenTextLoop(); } catch { }
        }
    }
}
