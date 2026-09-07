using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EveryonePicks
{
    /// <summary>
    /// Top-right board showing who has locked in a card and who everyone is still waiting on.
    /// Purely local presentation - it reads the replicated State.statuses table and draws it.
    /// </summary>
    internal static class PickBoard
    {
        private static Texture2D panelTex;
        private static Texture2D rowTex;
        private static Texture2D barTex;
        private static GUIStyle titleStyle;
        private static GUIStyle nameStyle;
        private static GUIStyle statusStyle;
        private static bool built;

        private static readonly Color DoneColor = new Color(0.44f, 0.83f, 0.48f);
        private static readonly Color PickingColor = new Color(1.00f, 0.79f, 0.28f);
        private static readonly Color StalledColor = new Color(0.95f, 0.42f, 0.40f);

        /// <summary>Seconds left on a pick before the board calls the player out.</summary>
        private const float UrgentUnder = 10f;

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        private static void Build()
        {
            panelTex = Solid(new Color(0.06f, 0.06f, 0.08f, 0.82f));
            rowTex = Solid(new Color(1f, 1f, 1f, 0.05f));
            barTex = Solid(Color.white);

            titleStyle = new GUIStyle
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.75f, 0.76f, 0.80f) }
            };

            nameStyle = new GUIStyle
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white }
            };

            statusStyle = new GUIStyle
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = Color.white }
            };

            built = true;
        }

        private struct Row
        {
            public int playerID;
            public string name;
            public Color color;
            public bool done;
            public int remaining;
            public float secondsLeft;
        }

        internal static void Draw()
        {
            if (EveryonePicksPlugin.ShowPickBoard == null || !EveryonePicksPlugin.ShowPickBoard.Value) return;
            if (!State.phaseActive) return;
            if (State.entitlements.Count == 0) return;
            if (Event.current.type != EventType.Repaint) return;

            if (!built) Build();

            var rows = new List<Row>();
            float now = Time.realtimeSinceStartup;

            foreach (var kv in State.entitlements)
            {
                int pid = kv.Key;

                // Someone who left the room is not "still picking" - don't hold a row for a ghost.
                var who = State.FindPlayer(pid);
                if (who == null || State.HasLeftRoom(who)) continue;
                State.statuses.TryGetValue(pid, out var st);
                State.sessionsDone.TryGetValue(pid, out int done);

                bool isDone = (st != null && st.done) || done >= kv.Value;

                // Count DOWN to the auto-pick, not up from the start of the phase. Each status
                // update (session start, pick taken, extra pick granted) restarts a player's
                // allowance, so measuring from lastUpdate tracks the deadline they are actually on.
                // Use the allowance the picking client reported. PickTimeSeconds is per-client
                // config, so recomputing from ours showed the wrong number for anyone who changed it.
                float since = st != null ? now - st.lastUpdate : now - State.phaseStartTime;
                float allowance = (st != null && st.pickSeconds > 0f) ? st.pickSeconds : State.PickSeconds;
                float left = Mathf.Max(0f, allowance - since);

                rows.Add(new Row
                {
                    playerID = pid,
                    name = State.DisplayName(pid),
                    color = State.PlayerColor(pid),
                    done = isDone,
                    remaining = st != null ? st.remaining : kv.Value,
                    secondsLeft = left
                });
            }

            if (rows.Count == 0) return;

            // Still-picking first, so the person holding everyone up is at the top.
            rows = rows.OrderBy(r => r.done ? 1 : 0).ThenBy(r => r.name).ToList();

            float scale = Mathf.Clamp(EveryonePicksPlugin.PickBoardScale.Value, 0.6f, 2f);
            float rowH = 24f * scale;
            float pad = 10f * scale;
            float width = 250f * scale;
            float titleH = 20f * scale;
            float height = pad * 2f + titleH + rows.Count * rowH;

            // Bottom left: the card bar and pick UI live on the right.
            float x = 18f;
            float y = Screen.height - height - 18f;

            titleStyle.fontSize = Mathf.RoundToInt(13 * scale);
            nameStyle.fontSize = Mathf.RoundToInt(15 * scale);
            statusStyle.fontSize = Mathf.RoundToInt(14 * scale);

            GUI.DrawTexture(new Rect(x, y, width, height), panelTex);

            int outstanding = rows.Count(r => !r.done);
            string title = outstanding == 0
                ? "ALL PICKED"
                : (outstanding == 1 ? "WAITING ON 1" : "WAITING ON " + outstanding);

            GUI.Label(new Rect(x + pad, y + pad, width - pad * 2f, titleH), title, titleStyle);

            float ry = y + pad + titleH;
            foreach (var r in rows)
            {
                var rect = new Rect(x + pad * 0.5f, ry, width - pad, rowH);

                if (!r.done) GUI.DrawTexture(rect, rowTex);

                // Player colour flash down the left edge.
                var swatch = new Rect(rect.x + 2f, ry + 4f * scale, 3f * scale, rowH - 8f * scale);
                GUI.color = r.color;
                GUI.DrawTexture(swatch, barTex);
                GUI.color = Color.white;

                nameStyle.normal.textColor = r.done ? new Color(0.62f, 0.64f, 0.68f) : Color.white;
                GUI.Label(new Rect(rect.x + 12f * scale, ry, rect.width * 0.58f, rowH), r.name, nameStyle);

                string status;
                Color statusColor;

                if (r.done)
                {
                    status = "picked";
                    statusColor = DoneColor;
                }
                else
                {
                    status = r.remaining > 1 ? "picking x" + r.remaining : "picking";

                    // The number counts DOWN to the auto-pick. Printing "0s" next to a banner that
                    // is counting UP read as a contradiction, so once the clock is spent say so
                    // rather than showing a zero that looks like time elapsed.
                    int left = Mathf.CeilToInt(r.secondsLeft);
                    status = left > 0 ? status + "  " + left + "s" : status + "  overdue";
                    statusColor = r.secondsLeft <= UrgentUnder ? StalledColor : PickingColor;
                }

                statusStyle.normal.textColor = statusColor;
                GUI.Label(new Rect(rect.x + rect.width * 0.5f - 8f * scale, ry, rect.width * 0.5f, rowH),
                          status, statusStyle);

                ry += rowH;
            }
        }
    }
}
