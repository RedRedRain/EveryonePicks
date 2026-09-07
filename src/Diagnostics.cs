using System;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace EveryonePicks
{
    /// <summary>
    /// Tells the player what went wrong, on screen, in plain language.
    ///
    /// When this mod fails the symptom is almost always "the game is stuck" or "nothing is
    /// happening", which tells nobody anything and sends people hunting through a BepInEx log.
    /// Anything that would otherwise be a silent stall is announced here instead.
    /// </summary>
    internal static class Diagnostics
    {
        private class Notice
        {
            public string text;
            public float until;
            public bool bad;
        }

        private static readonly List<Notice> notices = new List<Notice>();

        /// <summary>Version reported by each actor in the room, keyed by Photon actor number.</summary>
        private static readonly Dictionary<int, string> versions = new Dictionary<int, string>();

        private static bool mismatchAnnounced;

        private static GUIStyle style;
        private static Texture2D bg;

        // ---------------------------------------------------------------- notices

        internal static void Report(string message, bool bad = true, float seconds = 9f)
        {
            if (string.IsNullOrEmpty(message)) return;

            if (bad) EveryonePicksPlugin.Warn(message);
            else EveryonePicksPlugin.Log(message);

            if (EveryonePicksPlugin.ShowProblems == null || !EveryonePicksPlugin.ShowProblems.Value) return;

            // Never stack the same complaint twice.
            foreach (var n in notices)
            {
                if (n.text == message) { n.until = Time.realtimeSinceStartup + seconds; return; }
            }

            if (notices.Count >= 4) notices.RemoveAt(0);
            notices.Add(new Notice { text = message, until = Time.realtimeSinceStartup + seconds, bad = bad });
        }

        internal static void Clear() => notices.Clear();

        // ---------------------------------------------------------------- version handshake

        /// <summary>
        /// Announce our version to the room. A lobby running mixed versions desyncs the pick
        /// phase, and every symptom it produces looks like a different bug, so it is worth saying
        /// out loud rather than leaving people to work it out.
        /// </summary>
        internal static void SayHello()
        {
            try
            {
                versions[PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : 0]
                    = EveryonePicksPlugin.Version;

                if (PhotonNetwork.OfflineMode || !PhotonNetwork.InRoom) return;

                UnboundLib.NetworkingManager.RPC_Others(
                    typeof(EveryonePicksPlugin), "RPCA_SimulHello",
                    new object[] { PhotonNetwork.LocalPlayer.ActorNumber, EveryonePicksPlugin.Version });
            }
            catch (Exception e)
            {
                EveryonePicksPlugin.Warn("Version announce failed: " + e.Message);
            }
        }

        internal static void StoreHello(int actorNumber, string version)
        {
            versions[actorNumber] = version ?? "?";
            CheckVersions();
        }

        private static void CheckVersions()
        {
            if (mismatchAnnounced) return;

            var mine = EveryonePicksPlugin.Version;
            var others = new List<string>();

            foreach (var kv in versions)
            {
                if (kv.Value != mine && !others.Contains(kv.Value)) others.Add(kv.Value);
            }

            if (others.Count == 0) return;

            mismatchAnnounced = true;
            Report("Version mismatch  you " + mine + "  others " +
                   string.Join(", ", others.ToArray()), true, 20f);
        }

        /// <summary>
        /// Anyone picking who never announced a version is either on a build older than the
        /// handshake or does not have the mod at all. Either way the lobby is not uniform.
        /// </summary>
        internal static void CheckSilentPlayers()
        {
            if (mismatchAnnounced) return;
            if (PhotonNetwork.OfflineMode || !PhotonNetwork.InRoom) return;

            try
            {
                var quiet = new List<string>();

                foreach (var kv in State.entitlements)
                {
                    var p = State.FindPlayer(kv.Key);
                    if (p == null || State.HasLeftRoom(p)) continue;

                    int actor = -1;
                    try { actor = p.data.view.Owner.ActorNumber; } catch { continue; }

                    if (!versions.ContainsKey(actor)) quiet.Add(State.DisplayName(kv.Key));
                }

                if (quiet.Count == 0) return;

                mismatchAnnounced = true;
                Report("Different or missing version  " + string.Join(", ", quiet.ToArray()),
                       true, 20f);
            }
            catch { }
        }

        internal static void ResetRoom()
        {
            versions.Clear();
            mismatchAnnounced = false;
            Clear();
        }

        // ---------------------------------------------------------------- blaming the right mod

        /// <summary>First dotted token of a mod's types -> that mod's display name.</summary>
        private static readonly Dictionary<string, string> owners = new Dictionary<string, string>();

        private static readonly Dictionary<string, float> lastBlamed = new Dictionary<string, float>();
        private static bool reporting;
        private static bool hooked;

        /// <summary>
        /// Build a map from code to mod name, so an exception can be attributed to whoever wrote
        /// it. "Something went wrong" is useless to a player; "PickTimer threw during the pick
        /// phase" is something they can act on.
        /// </summary>
        internal static void BuildOwnerMap()
        {
            if (hooked) return;
            hooked = true;

            try
            {
                foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                {
                    var info = kv.Value;
                    if (info == null || info.Instance == null || info.Metadata == null) continue;

                    string name = info.Metadata.Name;
                    if (string.IsNullOrEmpty(name)) continue;

                    Type[] types;
                    try { types = info.Instance.GetType().Assembly.GetTypes(); }
                    catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t == null) continue;
                        string token = !string.IsNullOrEmpty(t.Namespace)
                            ? t.Namespace.Split('.')[0]
                            : t.Name;
                        if (string.IsNullOrEmpty(token) || token.Length < 3) continue;
                        if (!owners.ContainsKey(token)) owners[token] = name;
                    }
                }

                // Never blame ourselves for someone else's throw, or vice versa.
                owners["EveryonePicks"] = EveryonePicksPlugin.ModName;

                EveryonePicksPlugin.Log("Error attribution ready: " + owners.Count + " code prefixes mapped to mods.");
            }
            catch (Exception e)
            {
                EveryonePicksPlugin.Warn("Could not build the mod attribution map: " + e.Message);
            }

            Application.logMessageReceived += OnUnityLog;
        }

        internal static void Unhook()
        {
            if (!hooked) return;
            hooked = false;
            Application.logMessageReceived -= OnUnityLog;
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error) return;
            if (reporting) return;                    // our own Report logs; do not re-enter
            if (!State.phaseActive) return;           // only speak up about the pick phase
            if (string.IsNullOrEmpty(stackTrace)) return;

            string mod = Blame(stackTrace);
            if (mod == null || mod == EveryonePicksPlugin.ModName) return;

            // One complaint per mod per 30s, however hard it is spamming.
            float now = Time.realtimeSinceStartup;
            if (lastBlamed.TryGetValue(mod, out float when) && now - when < 30f) return;
            lastBlamed[mod] = now;

            string kind = condition ?? "";
            int colon = kind.IndexOf(':');
            if (colon > 0) kind = kind.Substring(0, colon);

            reporting = true;
            try
            {
                Report(mod + " threw an error  (" + kind.Trim() + ")");
            }
            finally { reporting = false; }
        }

        /// <summary>Walk a Unity stack trace and name the first mod that owns a frame.</summary>
        private static string Blame(string stackTrace)
        {
            try
            {
                foreach (var raw in stackTrace.Split(new[] { (char)10, (char)13 }))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;

                    int paren = line.IndexOf('(');
                    if (paren > 0) line = line.Substring(0, paren).Trim();

                    string token = line.Split('.')[0].Split('+')[0].Trim();
                    if (token.Length < 3) continue;

                    if (owners.TryGetValue(token, out string mod)) return mod;
                }
            }
            catch { }
            return null;
        }

        // ---------------------------------------------------------------- stuck detection

        private static float nextNag;

        /// <summary>
        /// Say who the round is waiting on before the budget runs out, rather than leaving people
        /// staring at a frozen screen wondering whether it is broken.
        /// </summary>
        internal static void TickStuck()
        {
            if (!State.phaseActive) { nextNag = 0f; return; }

            // The barrier has already decided to move on. Naming the same player again here is
            // what put a fresh "waiting" banner on screen moments before the next round started.
            if (State.waitingOver) return;

            float now = Time.realtimeSinceStartup;
            float elapsed = now - State.phaseStartTime;
            if (elapsed < 25f) return;
            if (now < nextNag) return;
            nextNag = now + 15f;

            try
            {
                var waiting = new List<string>();
                foreach (var kv in State.entitlements)
                {
                    State.sessionsDone.TryGetValue(kv.Key, out int done);
                    if (done >= kv.Value) continue;
                    var p = State.FindPlayer(kv.Key);
                    if (p == null || State.HasLeftRoom(p)) continue;
                    waiting.Add(State.DisplayName(kv.Key));
                }

                if (waiting.Count == 0) return;

                Report("Waiting on " + string.Join(", ", waiting.ToArray()) +
                       "  " + Mathf.FloorToInt(elapsed) + "s", true, 12f);
            }
            catch { }
        }

        // ---------------------------------------------------------------- drawing

        private static void Build()
        {
            bg = new Texture2D(1, 1);
            bg.SetPixel(0, 0, new Color(0.05f, 0.05f, 0.07f, 0.88f));
            bg.Apply();
            bg.hideFlags = HideFlags.HideAndDontSave;

            style = new GUIStyle
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = Color.white }
            };
        }

        internal static void Draw()
        {
            if (notices.Count == 0) return;
            if (Event.current.type != EventType.Repaint) return;
            if (style == null) Build();

            float now = Time.realtimeSinceStartup;
            for (int i = notices.Count - 1; i >= 0; i--)
                if (notices[i].until <= now) notices.RemoveAt(i);

            if (notices.Count == 0) return;

            float width = Mathf.Min(760f, Screen.width * 0.6f);
            float x = (Screen.width - width) * 0.5f;
            float y = 26f;

            foreach (var n in notices)
            {
                float h = Mathf.Max(30f, style.CalcHeight(new GUIContent(n.text), width - 24f) + 14f);

                GUI.DrawTexture(new Rect(x, y, width, h), bg);

                GUI.color = n.bad ? new Color(0.95f, 0.42f, 0.40f) : new Color(0.44f, 0.83f, 0.48f);
                GUI.DrawTexture(new Rect(x, y, 3f, h), bg);
                GUI.color = Color.white;

                style.normal.textColor = n.bad ? new Color(1f, 0.82f, 0.80f) : Color.white;
                GUI.Label(new Rect(x + 12f, y, width - 24f, h), n.text, style);

                y += h + 6f;
            }
        }
    }
}
