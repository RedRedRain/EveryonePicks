using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnboundLib.GameModes;
using UnityEngine;

namespace EveryonePicks
{
    /// <summary>
    /// Makes cards that hand out a follow-up pick work inside a simultaneous phase.
    ///
    /// Those cards count up in OnAddCard and spend the count in a PlayerPickEnd hook that walks
    /// every player and calls DoPick (MFM's Blood Pact, Calibrate and Immunize, PCE's Shuffle, The
    /// Fool...). The orchestrator fires PlayerPickEnd right after our DoPick, which returns at
    /// once, so the hook ran before anyone had chosen. The pick slipped to the next round, and the
    /// restricted pool those cards set up from PlayerPickStart missed it.
    ///
    /// So while this client has a pick of its own in progress, the hooks of those mods (and only
    /// those) wait, and run once its session ends. Every other mod's hooks still fire exactly when
    /// they always did: moving those to one client mid-phase broke class managers, shops, timers
    /// and anything else that expects the whole lobby to run them together.
    /// </summary>
    internal static class FollowUpPicks
    {
        /// <summary>
        /// Assemblies whose PlayerPickEnd hooks grant follow-up picks by walking every player and
        /// calling DoPick. Matched against the name UnboundLib records for each hook. Mods that
        /// grant picks to OTHER players for later rounds (WWM) or resolve theirs after the phase
        /// (GearUp) are deliberately absent: they work as they are.
        /// </summary>
        private static readonly HashSet<string> Granters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MFM", "bellusfennec-BFRM", "PCE", "ExtraGameCards", "Arcana", "Supcom2Cards"
        };

        /// <summary>Seconds one hook may run during our replay before we move past it.</summary>
        private const float HookLimit = 15f;

        private static FieldInfo F_hooks;
        private static FieldInfo F_onceHooks;
        private static PropertyInfo P_hook;
        private static PropertyInfo P_identifier;
        private static MethodInfo M_errorTolerant;

        /// <summary>False if UnboundLib's internals were not where we expect, in which case none of this runs.</summary>
        internal static bool Ready { get; private set; }

        /// <summary>
        /// True only while one step of our replay is executing. A hook's own TriggerHook and DoPick
        /// calls happen inside that step, so this tells them apart from the orchestrator's loop,
        /// which runs as a separate coroutine and never inside it.
        /// </summary>
        internal static bool InReplayStep { get; private set; }

        /// <summary>Follow-up picks queued by the current replay step, so they keep their order.</summary>
        internal static int frontInserts;

        /// <summary>Bumped when a phase is torn down, so a replay left running by StopCoroutine stops itself.</summary>
        private static int generation;

        internal static void Init()
        {
            try
            {
                F_hooks = AccessTools.Field(typeof(GameModeManager), "hooks");
                F_onceHooks = AccessTools.Field(typeof(GameModeManager), "onceHooks");
                var regType = AccessTools.Inner(typeof(GameModeHooks), "HookRegistration");
                P_hook = regType != null ? AccessTools.Property(regType, "Hook") : null;
                P_identifier = regType != null ? AccessTools.Property(regType, "Identifier") : null;
                M_errorTolerant = AccessTools.Method(typeof(GameModeManager), "ErrorTolerantHook");

                Ready = F_hooks != null && F_onceHooks != null && P_hook != null && P_identifier != null
                        && F_hooks.GetValue(null) is IDictionary && F_onceHooks.GetValue(null) is IDictionary;
            }
            catch { Ready = false; }

            if (!Ready)
                EveryonePicksPlugin.Warn("UnboundLib's hook table has changed shape; follow-up picks from cards " +
                                         "will arrive a round late, as they did before 0.3.37.");
        }

        /// <summary>True while DrainApplied runs: any pick a follow-up hook asks for then is swallowed.</summary>
        internal static bool Draining { get; private set; }

        internal static void Cancel()
        {
            generation++;
            InReplayStep = false;
            Draining = false;
            frontInserts = 0;
        }

        // ---------------------------------------------------------------- routing

        /// <summary>
        /// Called from our TriggerHook prefix. Returns true and a replacement enumerator when this
        /// trigger has to be split; false leaves UnboundLib to run it untouched.
        /// </summary>
        internal static bool Intercept(string key, out IEnumerator replacement)
        {
            replacement = null;
            if (!Ready) return false;

            bool start = string.Equals(key, "PlayerPickStart", StringComparison.OrdinalIgnoreCase);
            bool end = string.Equals(key, "PlayerPickEnd", StringComparison.OrdinalIgnoreCase);
            if (!start && !end) return false;

            // Checked before the phase flags: DrainApplied runs after the barrier has stopped
            // waiting, and its nested triggers must be filtered all the same.
            if (InReplayStep)
            {
                // A follow-up mod is triggering this itself, around the DoPick it just issued.
                // Only its own hooks get to see it, even if it has none here: in vanilla the whole
                // lobby runs this together, and running everyone else's on this client alone is
                // what broke other mods. PlayerPickStart starts the restricted-pool watcher for
                // the coming pick now; PlayerPickEnd's work (restoring the pool) waits until that
                // pick has happened.
                replacement = start ? RunSubset(key, true) : Nothing();
                return true;
            }

            if (!State.phaseActive || State.waitingOver) return false;

            // No follow-up mod has a hook here: nothing to separate, behave exactly as before.
            if (!HasGranters(key)) return false;

            if (State.LocalBusy)
            {
                // The orchestrator's loop: everyone else's hooks run now, as always. The follow-up
                // mods' wait for our session, so they can't spend a pick on a hand still open.
                replacement = RunSubset(key, false);
                return true;
            }

            return false;
        }

        private static IEnumerator Nothing() { yield break; }

        /// <summary>True when a follow-up mod has a hook registered for this key right now.</summary>
        internal static bool HasGranters(string key)
        {
            if (!Ready) return false;
            try
            {
                var dict = (IDictionary)F_hooks.GetValue(null);
                var list = dict[key.ToLowerInvariant()] as IList;
                if (list == null) return false;
                foreach (var reg in list)
                    if (reg != null && IsGranter(reg)) return true;
            }
            catch { }
            return false;
        }

        private static bool IsGranter(object reg)
        {
            try
            {
                if (P_identifier.GetValue(reg, null) is string id && Granters.Contains(id)) return true;

                // Also check where the hook's code lives, in case it was registered through a helper.
                var hook = P_hook.GetValue(reg, null) as GameModeHooks.Hook;
                var asm = hook?.Action?.Method?.DeclaringType?.Assembly;
                return asm != null && Granters.Contains(asm.GetName().Name);
            }
            catch { return false; }
        }

        private static int PriorityOf(object reg)
        {
            try { return (P_hook.GetValue(reg, null) as GameModeHooks.Hook)?.Priority ?? 0; }
            catch { return 0; }
        }

        /// <summary>
        /// UnboundLib's TriggerHook, restricted to the follow-up mods' hooks or to everyone else's.
        /// Same order (priority, highest first), same per-hook error handling, and the subset's
        /// once-hooks are retired afterwards the way TriggerHook retires them. The other subset's
        /// once-hooks stay registered until it runs.
        /// </summary>
        private static IEnumerator RunSubset(string key, bool granters)
        {
            key = key.ToLowerInvariant();
            var hooks = (IDictionary)F_hooks.GetValue(null);
            var onceHooks = (IDictionary)F_onceHooks.GetValue(null);
            var list = hooks[key] as IList;
            var once = onceHooks[key] as IList;
            var handler = GameModeManager.CurrentHandler;

            if (list != null && handler != null)
            {
                var picked = new List<object>();
                foreach (var reg in list)
                    if (reg != null && IsGranter(reg) == granters) picked.Add(reg);

                foreach (var reg in picked.OrderByDescending(PriorityOf).ToList())
                    yield return Invoke(key, reg, handler);
            }

            if (once == null || handler == null) yield break;

            var retire = new List<object>();
            foreach (var reg in once)
                if (reg != null && IsGranter(reg) == granters) retire.Add(reg);

            foreach (var reg in retire)
            {
                once.Remove(reg);
                (hooks[key] as IList)?.Remove(reg);
            }
            if (once.Count == 0 && ReferenceEquals(onceHooks[key], once)) onceHooks.Remove(key);
        }

        private static IEnumerator Invoke(string key, object reg, IGameModeHandler handler)
        {
            if (M_errorTolerant != null)
            {
                IEnumerator wrapped = null;
                try { wrapped = M_errorTolerant.Invoke(null, new[] { key, reg }) as IEnumerator; } catch { }
                if (wrapped != null) return wrapped;
            }

            // UnboundLib's wrapper is gone: run the hook directly. RunBounded still contains a throw.
            var hook = P_hook.GetValue(reg, null) as GameModeHooks.Hook;
            return hook?.Action?.Invoke(handler);
        }

        // ---------------------------------------------------------------- after a local pick

        /// <summary>
        /// Runs the follow-up mods' PlayerPickEnd hooks once a local session is over, before it
        /// reports, so a pick they grant is queued by the time we say whether we are done.
        /// </summary>
        internal static IEnumerator RunAfterSession(int token)
        {
            if (!Ready || !HasGranters("PlayerPickEnd")) yield break;

            int gen = generation;
            Func<bool> cancelled = () => gen != generation || token != State.phaseToken
                                         || !State.phaseActive || State.waitingOver;
            if (cancelled()) yield break;

            frontInserts = 0;
            yield return RunBounded(RunSubset("PlayerPickEnd", true), cancelled);

            // A replay cancelled while waiting can resume after a newer one has started; leave
            // that one's counter alone.
            if (gen == generation) frontInserts = 0;
        }

        /// <summary>
        /// Runs the follow-up mods' PlayerPickStart hooks just before a local session starts, as
        /// vanilla fires PlayerPickStart before every pick. Their once-hooks are what watch for the
        /// player's next hand and narrow its pool (MFM's Necronomicon), and the orchestrator's own
        /// PlayerPickStart only reaches them while this client is idle.
        /// </summary>
        internal static IEnumerator RunBeforeSession(int token)
        {
            if (!Ready || !HasGranters("PlayerPickStart")) yield break;

            int gen = generation;
            Func<bool> cancelled = () => gen != generation || token != State.phaseToken
                                         || !State.phaseActive || State.waitingOver;
            if (cancelled()) yield break;

            yield return RunBounded(RunSubset("PlayerPickStart", true), cancelled);
        }

        /// <summary>
        /// Runs from the barrier right after the round's cards are applied. Cards applied here
        /// for OTHER players just bumped their follow-up counters on this machine, but those picks
        /// were already taken, on the picker's own machine. Spending the counters now, with the
        /// picks swallowed, keeps every client's counters level, so nothing later in the round
        /// (GearUp's after-phase draws fire PlayerPickEnd) starts a vanilla pick here for a player
        /// whose own client will never deal it.
        /// </summary>
        internal static IEnumerator DrainApplied(int token)
        {
            if (!Ready || !HasGranters("PlayerPickEnd")) yield break;

            int gen = generation;
            Func<bool> cancelled = () => gen != generation || token != State.phaseToken;
            if (cancelled()) yield break;

            Draining = true;
            yield return RunBounded(RunSubset("PlayerPickEnd", true), cancelled);
            if (gen == generation) Draining = false;
        }

        /// <summary>
        /// Steps a hook chain the way Unity would, with three differences: it stops when the phase
        /// it belongs to is torn down (StopCoroutine on our runner does not stop a routine nested
        /// in it), one throwing hook ends only itself, and a hook that runs past HookLimit is cut
        /// off without taking the rest of the chain with it. InReplayStep is set for exactly the
        /// span of each step, so only calls made by the hooks themselves see it.
        /// </summary>
        private static IEnumerator RunBounded(IEnumerator root, Func<bool> cancelled)
        {
            var stack = new List<IEnumerator>();
            if (root != null) stack.Add(root);

            float hookDeadline = Time.realtimeSinceStartup + HookLimit;
            int spins = 0;

            while (stack.Count > 0)
            {
                if (cancelled())
                {
                    Unwind(stack, 0);
                    yield break;
                }

                // A new hook starts each time the chain is back down to the subset runner.
                if (stack.Count == 1) hookDeadline = Time.realtimeSinceStartup + HookLimit;
                else if (Time.realtimeSinceStartup > hookDeadline)
                {
                    EveryonePicksPlugin.Warn("A follow-up pick hook was still running after " + HookLimit +
                                             "s; skipping the rest of it.");
                    Unwind(stack, 1);
                    continue;
                }

                var top = stack[stack.Count - 1];
                object current;
                bool moved;

                InReplayStep = true;
                try
                {
                    moved = top.MoveNext();
                    current = moved ? top.Current : null;
                }
                catch (Exception e)
                {
                    // Logged as an exception so Diagnostics names the mod it came from.
                    EveryonePicksPlugin.Warn("A follow-up pick hook threw; skipping the rest of it.");
                    UnityEngine.Debug.LogException(e);
                    moved = false;
                    current = null;
                }
                finally
                {
                    InReplayStep = false;
                }

                if (!moved)
                {
                    Unwind(stack, stack.Count - 1);
                    continue;
                }

                // Nested routines, CustomYieldInstructions included, are stepped here too so the
                // limits and the step flag cover them.
                if (current is IEnumerator nested)
                {
                    stack.Add(nested);

                    // A chain that keeps nesting without ever waiting would spin this frame forever.
                    if (++spins > 256)
                    {
                        spins = 0;
                        yield return null;
                    }
                    continue;
                }

                spins = 0;
                yield return current;
            }
        }

        /// <summary>Pops down to <paramref name="depth"/> entries, disposing each so its finally blocks run.</summary>
        private static void Unwind(List<IEnumerator> stack, int depth)
        {
            while (stack.Count > depth)
            {
                var e = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                try { (e as IDisposable)?.Dispose(); } catch { }
            }
        }
    }
}
