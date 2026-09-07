using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Photon.Pun;
using UnboundLib.GameModes;

namespace EPDiag
{
    /// <summary>
    /// A snapshot of the things that decide whether a round can progress.
    ///
    /// The whole diagnosis last time turned on questions this answers directly: was the battle
    /// running, did the players exist, were they simulated, was this client still connected and
    /// did it agree with everyone else about who is in the room. Attaching it to every traced
    /// step means we can see the exact moment two clients stop agreeing.
    /// </summary>
    internal static class State
    {
        /// <summary>
        /// PlayerVelocity.simulated is not public, and it is the single best indicator of the
        /// failure we are chasing: a client where the round never really started has players that
        /// exist but are not being simulated. Worth reaching for by reflection.
        /// </summary>
        private static readonly FieldInfo SimulatedField =
            AccessTools.Field(AccessTools.TypeByName("PlayerVelocity"), "simulated");

        private static bool IsSimulated(Player p)
        {
            try
            {
                if (SimulatedField == null || p?.data?.playerVel == null) return false;
                return (bool)SimulatedField.GetValue(p.data.playerVel);
            }
            catch { return false; }
        }

        /// <summary>Compact form, appended to trace lines.</summary>
        internal static string Brief()
        {
            try
            {
                var sb = new StringBuilder(48);
                sb.Append("  [");

                var gm = GameManager.instance;
                sb.Append("battle=").Append(gm != null && gm.battleOngoing ? "Y" : "n");

                var pm = PlayerManager.instance;
                if (pm?.players != null)
                {
                    int alive = 0, sim = 0;
                    foreach (var p in pm.players)
                    {
                        if (p == null) continue;
                        try { if (p.data != null && !p.data.dead) alive++; } catch { }
                        if (IsSimulated(p)) sim++;
                    }
                    sb.Append(" players=").Append(pm.players.Count)
                      .Append(" alive=").Append(alive)
                      .Append(" sim=").Append(sim);
                }
                else sb.Append(" players=?");

                var cc = CardChoice.instance;
                if (cc != null) sb.Append(cc.IsPicking ? " PICKING" : "");

                sb.Append(']');
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>Fuller form, written on the heartbeat and whenever something notable happens.</summary>
        internal static string Full()
        {
            var sb = new StringBuilder(160);

            try
            {
                sb.Append("state:");
                sb.Append(Brief());

                if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null)
                {
                    sb.Append(" room=").Append(PhotonNetwork.CurrentRoom.PlayerCount)
                      .Append(PhotonNetwork.IsMasterClient ? " MASTER" : " client");

                    // Who this client believes is present. A disagreement between two players'
                    // files here explains a great deal on its own.
                    sb.Append(" actors=");
                    bool first = true;
                    foreach (var kv in PhotonNetwork.CurrentRoom.Players)
                    {
                        if (!first) sb.Append(',');
                        sb.Append(kv.Key).Append(':').Append(kv.Value?.NickName ?? "?");
                        first = false;
                    }
                }
                else sb.Append(" room=none");

                sb.Append(" ping=").Append(PhotonNetwork.GetPing()).Append("ms");

                try
                {
                    var handler = GameModeManager.CurrentHandler;
                    sb.Append(" mode=").Append(handler?.Name ?? "none");
                }
                catch { }
            }
            catch (Exception e) { sb.Append(" <state failed: ").Append(e.Message).Append('>'); }

            return sb.ToString();
        }
    }
}
