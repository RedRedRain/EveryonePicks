using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace EPDiag
{
    /// <summary>
    /// Instruments every step of the round loop.
    ///
    /// The problem this exists to solve: a client stops advancing rounds and NOTHING is logged,
    /// by any mod, so all we can say afterwards is "it stopped somewhere". Silence is not
    /// evidence. Patching each step to announce itself turns that silence into a precise last
    /// known position.
    ///
    /// Most of these methods are COROUTINES, which matters enormously. A prefix on a coroutine
    /// only tells you it was asked for, not that it ran, and a coroutine that dies halfway
    /// through leaves no trace at all - which is exactly the failure we are chasing. So the
    /// returned enumerator is wrapped and each step counted, giving us "died on step 7 of
    /// PointTransition" instead of "PointTransition was called".
    /// </summary>
    internal static class Trace
    {
        /// <summary>Depth of nesting, so the log reads as a call tree.</summary>
        private static int depth;

        internal static int Applied { get; private set; }

        // The round loop, in the order it normally runs. RWFGameMode is what actually drives a
        // modded lobby; the vanilla modes are patched too in case a game is not using RWF.
        private static readonly string[] Types =
        {
            "RWF.GameModes.RWFGameMode",
            "GM_ArmsRace",
            "GM_Deathmatch",
            "GM_Test",
        };

        private static readonly string[] Methods =
        {
            "StartGame", "DoStartGame",
            "PlayerDied",
            "PointOver", "PointTransition", "DoPointStart",
            "RoundOver", "RoundTransition", "DoRoundStart",
            "GameOver", "GameOverTransition",
            "ResetMatch", "DoRestart",
            // The sync handshake. If one client never answers or never hears the answer, the
            // round simply never begins for it, which looks exactly like our symptom.
            "SyncBattleStart", "RPC_SyncBattleStart", "RPC_SyncBattleStartResponse",
            "RPC_RequestSync", "RPC_SyncResponse", "RPCA_NextRound", "WaitForSyncUp",
        };

        internal static void Apply(Harmony h)
        {
            var pre  = new HarmonyMethod(typeof(Trace), nameof(Enter));
            var post = new HarmonyMethod(typeof(Trace), nameof(Exit));
            var fin  = new HarmonyMethod(typeof(Trace), nameof(Threw));

            foreach (string typeName in Types)
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null) continue;

                foreach (string methodName in Methods)
                {
                    MethodInfo m;
                    try { m = AccessTools.Method(t, methodName); }
                    catch { continue; }
                    if (m == null || m.IsAbstract) continue;

                    try
                    {
                        h.Patch(m, prefix: pre, postfix: post, finalizer: fin);
                        Applied++;
                    }
                    catch (Exception e)
                    {
                        Log.Line("could not instrument " + typeName + "." + methodName + ": " + e.Message);
                    }
                }
            }

            // Picking, which is where our own mod lives, so its steps can be lined up too.
            foreach (var pair in new[]
            {
                new[] { "CardChoice", "StartPick" },
                new[] { "CardChoice", "DoPick" },
                new[] { "CardChoice", "IDoEndPick" },
                new[] { "CardChoice", "RPCA_DoEndPick" },
                new[] { "CardChoice", "RPCA_DonePicking" },
                new[] { "PlayerManager", "RevivePlayers" },
                new[] { "PlayerManager", "SetPlayersSimulated" },
                new[] { "MapManager", "LoadLevelFromID" },
                new[] { "MapManager", "CallInNewMap" },
            })
            {
                var t = AccessTools.TypeByName(pair[0]);
                var m = t != null ? AccessTools.Method(t, pair[1]) : null;
                if (m == null) continue;
                try { h.Patch(m, prefix: pre, postfix: post, finalizer: fin); Applied++; }
                catch { }
            }
        }

        private static string Name(MethodBase m) =>
            (m?.DeclaringType != null ? m.DeclaringType.Name : "?") + "." + (m?.Name ?? "?");

        private static string Pad() => depth <= 0 ? "" : new string(' ', Math.Min(depth, 8) * 2);

        private static void Enter(MethodBase __originalMethod)
        {
            Log.Line(Pad() + "-> " + Name(__originalMethod) + State.Brief());
            depth++;
        }

        private static void Exit(MethodBase __originalMethod, ref object __result)
        {
            depth = Math.Max(0, depth - 1);

            // A coroutine has only been HANDED to us here. Wrap it so we find out whether it
            // actually finishes, and if not, exactly how far it got.
            if (__result is IEnumerator inner)
            {
                __result = Watch(Name(__originalMethod), inner);
                Log.Line(Pad() + "<- " + Name(__originalMethod) + " (coroutine handed over)");
                return;
            }

            Log.Line(Pad() + "<- " + Name(__originalMethod));
        }

        private static Exception Threw(MethodBase __originalMethod, Exception __exception)
        {
            depth = Math.Max(0, depth - 1);
            if (__exception != null)
                Log.Line(Pad() + "!! " + Name(__originalMethod) + " THREW " + __exception);

            return __exception;   // observe only, never swallow
        }

        /// <summary>
        /// Steps a coroutine on the game's behalf, counting and reporting. The yield sits outside
        /// the try because C# forbids yielding from inside one, which is also why the exception is
        /// rethrown rather than handled: we are here to watch, not to change behaviour.
        /// </summary>
        private static IEnumerator Watch(string name, IEnumerator inner)
        {
            if (inner == null)
            {
                Log.Line("   [" + name + "] returned a NULL coroutine");
                yield break;
            }

            int step = 0;

            while (true)
            {
                object current;

                try
                {
                    if (!inner.MoveNext())
                    {
                        Log.Line("   [" + name + "] completed after " + step + " step(s)" + State.Brief());
                        yield break;
                    }
                    current = inner.Current;
                }
                catch (Exception e)
                {
                    Log.Line("   [" + name + "] DIED on step " + step + State.Brief());
                    Log.Line("   [" + name + "] " + e);
                    throw;
                }

                step++;
                yield return current;
            }
        }
    }
}
