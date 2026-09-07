using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EveryonePicks
{
    /// <summary>
    /// Live pick reveal. As each player locks a card in, that card appears on everyone's screen
    /// with the player above it, so the people who finished first watch the round fill in instead
    /// of staring at a static board. Everything stays up briefly after the last pick, then clears.
    ///
    /// This replaces ModdingUtils' card-bar crawl, which fed cards into the side bar one at a
    /// time after the phase and turned the end of a simultaneous round back into a queue.
    ///
    /// All of it is local presentation driven by RPCA_SimulResult, which every client already
    /// receives. Nothing extra is networked. The card is the same Instantiate the pick UI uses,
    /// and the player figure is a copy of that player's sprite renderers only - no scripts, no
    /// PhotonView, no colliders - so it cannot interact with anything.
    /// </summary>
    internal static class PickReveal
    {
        private const int MaxSpritesPerPlayer = 32;

        private class Entry
        {
            public int playerID;
            public string name;
            public Color color;
            public GameObject card;
            public GameObject figure;
            public float bornAt;
        }

        private static readonly List<Entry> entries = new List<Entry>();
        private static readonly HashSet<string> seen = new HashSet<string>();

        /// <summary>Original sorting order of each copied sprite, so LiftAbove stays idempotent.</summary>
        private static readonly Dictionary<SpriteRenderer, int> baseOrders = new Dictionary<SpriteRenderer, int>();

        private static GUIStyle nameStyle;
        private static Texture2D chipTex;

        internal static bool Active => entries.Count > 0;

        // ---------------------------------------------------------------- live feed

        /// <summary>
        /// A pick landed. Called from State.StoreResult on every client, including the picker's.
        /// </summary>
        internal static void Add(int playerID, int pickIdx, string cardName)
        {
            if (EveryonePicksPlugin.ShowReveal == null || !EveryonePicksPlugin.ShowReveal.Value) return;
            if (string.IsNullOrEmpty(cardName)) return;
            if (!State.phaseActive) return;   // a late result must not resurrect a torn-down reveal

            string key = playerID + "|" + pickIdx;
            if (!seen.Add(key)) return;

            try
            {
                var info = State.FindCard(cardName);
                if (info == null) return;

                var card = CardChoice.instance.AddCardVisual(info, Vector3.zero);
                if (card == null) { seen.Remove(key); return; }
                Sanitise(card);
                card.transform.position = Parked;

                // One figure and one name plate per PLAYER, however many cards they took.
                bool alreadyShown = false;
                foreach (var prev in entries) if (prev.playerID == playerID) { alreadyShown = true; break; }

                entries.Add(new Entry
                {
                    playerID = playerID,
                    name = State.DisplayName(playerID),
                    color = State.PlayerColor(playerID),
                    card = card,
                    figure = alreadyShown ? null : TryMakeFigure(playerID),
                    bornAt = Time.realtimeSinceStartup
                });

                Layout();
            }
            catch (Exception e)
            {
                seen.Remove(key);
                EveryonePicksPlugin.Warn("Reveal add failed: " + e.Message);
            }
        }

#if EP_DEBUG
        /// <summary>
        /// Debug-only insert. Same path as a real pick, but tolerates a made-up player id so the
        /// layout can be exercised solo. Compiled out of release builds.
        /// </summary>
        internal static void AddDebug(int playerID, int pickIdx, string cardName) =>
            Add(playerID, pickIdx, cardName);
#endif

        /// <summary>Hold whatever is on screen, then clear. Called at the end of the barrier.</summary>
        internal static IEnumerator Hold()
        {
            if (entries.Count == 0) { Cleanup(); yield break; }

            float wait = Mathf.Clamp(EveryonePicksPlugin.RevealSeconds.Value, 0.5f, 10f);
            float newest = 0f;
            foreach (var e in entries) newest = Mathf.Max(newest, e.bornAt);

            float remaining = wait - (Time.realtimeSinceStartup - newest);
            if (remaining > 0f) yield return new WaitForSecondsRealtime(remaining);

            Cleanup();
        }

        // ---------------------------------------------------------------- the player figure

        /// <summary>
        /// A stand-in for the player, built by copying only their SpriteRenderers. ROUNDS has no
        /// portrait asset: PlayerSkinBank stores four colours and PlayerFace stores eye/mouth ids,
        /// neither of which renders on its own. Cloning the whole player object would run its
        /// Awake and drag networking and physics along with it, so we take the sprites and nothing
        /// else. Returns null if the player has no usable sprites, and the caller falls back to
        /// the coloured name plate alone.
        /// </summary>
        private static GameObject TryMakeFigure(int playerID)
        {
            try
            {
                // A debug reveal uses invented ids offset by 1000; fall back to the real player
                // behind the id so the figure still renders while testing solo.
                var player = State.FindPlayer(playerID) ?? State.FindPlayer(playerID % 1000);
                if (player == null) return null;

                var root = player.gameObject;
                if (root == null) return null;

                var sprites = root.GetComponentsInChildren<SpriteRenderer>(true);
                if (sprites == null || sprites.Length == 0) return null;

                var holder = new GameObject("EveryonePicks_Figure_" + playerID);
                int made = 0;

                foreach (var src in sprites)
                {
                    if (made >= MaxSpritesPerPlayer) break;
                    if (src == null || src.sprite == null) continue;

                    // Cards and other mods parent effect art to the player. Only copy what is
                    // actually being drawn on the real player right now, or the figure turns
                    // into a pile of card effects as the match goes on.
                    if (!src.enabled || !src.gameObject.activeInHierarchy) continue;

                    var piece = new GameObject(src.name);
                    piece.transform.SetParent(holder.transform, false);

                    // Preserve the sprite's placement relative to the player root so the figure
                    // keeps its proportions.
                    piece.transform.localPosition = root.transform.InverseTransformPoint(src.transform.position);
                    piece.transform.localRotation = Quaternion.Inverse(root.transform.rotation) * src.transform.rotation;
                    piece.transform.localScale = src.transform.lossyScale;

                    var dst = piece.AddComponent<SpriteRenderer>();
                    dst.sprite = src.sprite;
                    dst.color = src.color;
                    dst.flipX = src.flipX;
                    dst.flipY = src.flipY;
                    dst.sortingLayerID = src.sortingLayerID;
                    dst.sortingOrder = src.sortingOrder;
                    baseOrders[dst] = src.sortingOrder;
                    dst.material = src.sharedMaterial;
                    dst.enabled = true;
                    made++;
                }

                if (made == 0)
                {
                    // Retry with no filter before giving up. PlayerManager.SetPlayersVisible only
                    // parks players 200 units up rather than deactivating them, so the filter
                    // should never empty the list, but a card or mod could still leave the body
                    // art disabled and a missing figure is worse than an imperfect one.
                    foreach (var src in sprites)
                    {
                        if (made >= MaxSpritesPerPlayer) break;
                        if (src == null || src.sprite == null) continue;

                        var piece = new GameObject(src.name);
                        piece.transform.SetParent(holder.transform, false);
                        piece.transform.localPosition = root.transform.InverseTransformPoint(src.transform.position);
                        piece.transform.localRotation = Quaternion.Inverse(root.transform.rotation) * src.transform.rotation;
                        piece.transform.localScale = src.transform.lossyScale;

                        var dst = piece.AddComponent<SpriteRenderer>();
                        dst.sprite = src.sprite;
                        dst.color = src.color;
                        dst.flipX = src.flipX;
                        dst.flipY = src.flipY;
                        dst.sortingLayerID = src.sortingLayerID;
                        dst.sortingOrder = src.sortingOrder;
                        dst.material = src.sharedMaterial;
                        dst.enabled = true;
                        baseOrders[dst] = src.sortingOrder;
                        made++;
                    }
                }

                if (made == 0)
                {
                    EveryonePicksPlugin.Warn("No usable sprites to build a figure for player " +
                                             playerID + "; showing the name plate only.");
                    UnityEngine.Object.Destroy(holder);
                    return null;
                }
                return holder;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- layout

        /// <summary>
        /// Hidden only while THIS player is choosing, so the reveal cannot cover their own hand.
        /// Anyone who has already picked, or who won the round and is not picking, sees each card
        /// land live as it happens.
        /// </summary>
        private static bool LocalStillPicking() => State.LocalBusy;

        /// <summary>
        /// Somewhere off camera. Objects are parked rather than deactivated because an inactive
        /// renderer reports zero bounds, so deactivating them would mean they could never be
        /// measured and therefore never appear.
        /// </summary>
        private static readonly Vector3 Parked = new Vector3(0f, 2000f, 0f);

        private static bool shown;
        private static Camera cachedCam;

        /// <summary>
        /// ROUNDS keeps its camera on MainCam, and Camera.main only resolves if a camera is
        /// tagged MainCamera. Relying on Camera.main meant the reveal could never be positioned,
        /// so everything stayed parked off screen and the phase looked empty.
        /// </summary>
        private static Camera ResolveCamera()
        {
            if (cachedCam != null) return cachedCam;

            try
            {
                var t = AccessTools.TypeByName("MainCam");
                if (t != null)
                {
                    var inst = AccessTools.Field(t, "instance")?.GetValue(null);
                    if (inst != null)
                    {
                        var cam = AccessTools.Field(t, "cam")?.GetValue(inst) as Camera;
                        if (cam != null) { cachedCam = cam; return cachedCam; }
                    }
                }
            }
            catch { }

            if (Camera.main != null) { cachedCam = Camera.main; return cachedCam; }

            try
            {
                foreach (var c in Camera.allCameras)
                    if (c != null && c.isActiveAndEnabled) { cachedCam = c; return cachedCam; }
            }
            catch { }

            return null;
        }

        private static void Park()
        {
            shown = false;

            // Restack while out of sight, so a hand does not come back already open because the
            // cursor happened to be over it when the reveal was hidden.
            var keys = new List<int>(hoverMemory.Keys);
            foreach (var k in keys) hoverMemory[k] = 0f;
            foreach (var sl in slots) sl.hover = 0f;

            foreach (var e in entries)
            {
                try
                {
                    if (e.card != null) e.card.transform.position = Parked;
                    if (e.figure != null) e.figure.transform.position = Parked;
                }
                catch { }
            }
        }

        private class Slot
        {
            public int playerID;
            public string name;
            public Color color;
            public GameObject figure;
            public readonly List<GameObject> cards = new List<GameObject>();
            public Vector3 centre;
            public float topWorld;
            public float halfWidth;

            /// <summary>0 stacked, 1 fully spread. Eased so the change reads as a movement.</summary>
            public float hover;
        }

        private static readonly List<Slot> slots = new List<Slot>();

        /// <summary>Hover amount per player, surviving the per-frame slot rebuild.</summary>
        private static readonly Dictionary<int, float> hoverMemory = new Dictionary<int, float>();

        /// <summary>How far apart cards sit when spread, as a fraction of card width.</summary>
        private const float SpreadStep = 1.08f;

        /// <summary>Seconds-ish to open or close a stack.</summary>
        private const float HoverSpeed = 5f;

        /// <summary>How far each extra card is pushed across, as a fraction of card width.</summary>
        private const float FanStep = 0.34f;

        /// <summary>Sorting block each fanned card gets, so they layer instead of interleaving.</summary>
        private const int CardSortBase = 200;
        private const int CardSortStep = 20;

        /// <summary>Group the flat entry list into one slot per player, order preserved.</summary>
        private static void BuildSlots()
        {
            slots.Clear();
            foreach (var e in entries)
            {
                Slot slot = null;
                foreach (var existing in slots)
                    if (existing.playerID == e.playerID) { slot = existing; break; }

                if (slot == null)
                {
                    slot = new Slot { playerID = e.playerID, name = e.name, color = e.color };

                    // BuildSlots runs every frame, so carry the eased hover value forward or it
                    // would reset to zero before the animation could ever be seen.
                    if (hoverMemory.TryGetValue(e.playerID, out float remembered))
                        slot.hover = remembered;

                    slots.Add(slot);
                }

                if (e.figure != null) slot.figure = e.figure;
                if (e.card != null) slot.cards.Add(e.card);
            }
        }

        private static void Layout()
        {
            var cam = ResolveCamera();
            if (cam == null || entries.Count == 0) return;

            if (LocalStillPicking()) { Park(); return; }

            BuildSlots();
            if (slots.Count == 0) return;

            float depth = Mathf.Abs(cam.transform.position.z);
            Vector3 vLeft = cam.ViewportToWorldPoint(new Vector3(0.05f, 0.5f, depth));
            Vector3 vRight = cam.ViewportToWorldPoint(new Vector3(0.95f, 0.5f, depth));
            Vector3 vTop = cam.ViewportToWorldPoint(new Vector3(0.5f, 0.84f, depth));
            Vector3 vBottom = cam.ViewportToWorldPoint(new Vector3(0.5f, 0.22f, depth));
            float centreX = cam.ViewportToWorldPoint(new Vector3(0.5f, 0.5f, depth)).x;

            float usableW = vRight.x - vLeft.x;
            float usableH = Mathf.Abs(vTop.y - vBottom.y);
            float midY = (vTop.y + vBottom.y) * 0.5f;

            float cardWnat = 0f, cardHnat = 0f;
            foreach (var e in entries)
            {
                cardWnat = Mathf.Max(cardWnat, RawWidth(e.card));
                cardHnat = Mathf.Max(cardHnat, RawHeight(e.card));
            }
            if (cardWnat <= 0.0001f) { Park(); return; }

            // Wrap into a grid so a ten-player lobby does not shrink into an unreadable strip.
            int n = slots.Count;
            int rows = Mathf.Max(1, Mathf.CeilToInt(n / 6f));
            int cols = Mathf.Max(1, Mathf.CeilToInt(n / (float)rows));

            int maxCards = 1;
            foreach (var sl in slots) maxCards = Mathf.Max(maxCards, sl.cards.Count);
            float fanSpread = 1f + FanStep * (maxCards - 1);

            float cellW = usableW / cols;
            float cellH = usableH / rows;

            float scale = Mathf.Min(
                (cellW * 0.86f) / (cardWnat * fanSpread),
                (cellH * 0.52f) / cardHnat);
            scale *= Mathf.Clamp(EveryonePicksPlugin.RevealCardSize.Value, 0.2f, 6f);
            scale = Mathf.Clamp(scale, 0.02f, 60f);

            float cardW = cardWnat * scale;
            float cardH = cardHnat * scale;

#if EP_DEBUG
            DumpMeasurements(cam, entries[0].card, cardWnat, cardHnat, scale, usableW, cols, rows);
#endif

            shown = true;

            for (int i = 0; i < n; i++)
            {
                var sl = slots[i];
                int row = i / cols;
                int col = i % cols;

                int inThisRow = Mathf.Min(cols, n - row * cols);
                float rowWidth = inThisRow * cellW;
                float x = centreX - rowWidth * 0.5f + cellW * (col + 0.5f);
                float y = rows == 1 ? midY : vTop.y - cellH * (row + 0.5f);

                sl.centre = new Vector3(x, y, 0f);
                sl.halfWidth = cardW * fanSpread * 0.5f;

                // Hit-test against the STACKED footprint so the target does not move as it opens,
                // which would make the stack flicker between states under a still cursor.
                bool over = false;
                if (sl.cards.Count > 1)
                {
                    try
                    {
                        // This player's own stacked width, not the widest stack in the lobby, or a
                        // pair of cards would answer to the cursor well outside where they sit.
                        float ownHalf = cardW * (1f + FanStep * (sl.cards.Count - 1)) * 0.5f;
                        float pad = cardW * 0.06f;

                        var c0 = cam.WorldToScreenPoint(new Vector3(x - ownHalf - pad, y - cardH * 0.5f - pad, 0f));
                        var c1 = cam.WorldToScreenPoint(new Vector3(x + ownHalf + pad, y + cardH * 0.5f + pad, 0f));
                        var m = Input.mousePosition;

                        over = m.x >= Mathf.Min(c0.x, c1.x) && m.x <= Mathf.Max(c0.x, c1.x)
                            && m.y >= Mathf.Min(c0.y, c1.y) && m.y <= Mathf.Max(c0.y, c1.y);
                    }
                    catch { }
                }

                sl.hover = Mathf.MoveTowards(sl.hover, over ? 1f : 0f,
                                             Time.unscaledDeltaTime * HoverSpeed);
                hoverMemory[sl.playerID] = sl.hover;

                FanCards(sl, x, y, cardW, scale);

                float top = y + cardH * 0.5f;

                if (sl.figure != null)
                {
                    float figH = RawHeight(sl.figure);
                    if (figH <= 0.0001f)
                    {
                        sl.figure.transform.position = Parked;
                    }
                    else
                    {
                        float figScale = Mathf.Clamp((cardH * 0.38f) / figH, scale * 0.1f, scale * 8f);
                        float drawnH = figH * figScale;
                        sl.figure.transform.localScale = Vector3.one * figScale;
                        sl.figure.transform.position = new Vector3(x, top + drawnH * 0.6f, 0f);
                        top = top + drawnH * 1.15f;
                        LiftAbove(sl.figure, sl.cards.Count > 0 ? sl.cards[0] : null);
                    }
                }

                sl.topWorld = top;
            }
        }

        /// <summary>
        /// Spread one player's cards like a held hand: offset across, fanned open, each later
        /// card sitting in front of the one before it.
        /// </summary>
        private static void FanCards(Slot sl, float x, float y, float cardW, float scale)
        {
            int k = sl.cards.Count;
            if (k == 0) return;

            float mid = (k - 1) * 0.5f;

            // Smoothstep so the stack opens and closes with a bit of weight rather than linearly.
            float t = sl.hover;
            t = t * t * (3f - 2f * t);

            // Spreading can overlap a neighbouring player in a tight grid, so the stack being
            // looked at is raised above the rest rather than being clipped by them.
            int hoverLift = t > 0.01f ? 1000 : 0;

            float step = Mathf.Lerp(FanStep, SpreadStep, t);
            float tilt = Mathf.Lerp(8f, 0f, t);
            float sag = Mathf.Lerp(0.05f, 0f, t);
            float lift = Mathf.Lerp(1f, 1.06f, t);

            for (int c = 0; c < k; c++)
            {
                var card = sl.cards[c];
                if (card == null) continue;

                float off = (c - mid) * cardW * step;
                float angle = -(c - mid) * tilt;
                float drop = Mathf.Abs(c - mid) * cardW * sag;

                card.transform.localScale = Vector3.one * scale * lift;
                card.transform.position = new Vector3(x + off, y - drop, 0f);
                card.transform.rotation = Quaternion.Euler(0f, 0f, angle);

                // Give each card its own sorting block so a fanned hand layers cleanly instead of
                // interleaving. A card's canvas does not set overrideSorting by default, so the
                // previous version silently skipped every one of them and the cards clipped
                // through each other. These are throwaway display copies, so overriding is safe.
                try
                {
                    var canvases = card.GetComponentsInChildren<Canvas>(true);
                    foreach (var cv in canvases)
                    {
                        if (cv == null) continue;
                        cv.overrideSorting = true;
                        cv.sortingOrder = CardSortBase + c * CardSortStep + hoverLift;
                    }

                    // Backstop for anything drawn outside a canvas.
                    foreach (var sr in card.GetComponentsInChildren<SpriteRenderer>(true))
                    {
                        if (sr == null) continue;
                        sr.sortingOrder = CardSortBase + c * CardSortStep + hoverLift;
                    }
                }
                catch { }
            }
        }

#if EP_DEBUG
        private static int dumpedForToken = -1;
#endif

        /// <summary>
        /// Log what each measurement route actually returns, once per phase. The reveal has been
        /// sized wrong several times and every fix so far has been a guess at which number is
        /// right; this prints them side by side against the card's real on-screen height.
        /// </summary>
#if EP_DEBUG
        private static void DumpMeasurements(Camera cam, GameObject card, float cardWnat,
                                             float cardHnat, float scale, float usableW,
                                             int cols, int rows)
        {
            if (card == null || dumpedForToken == State.phaseToken) return;
            dumpedForToken = State.phaseToken;

            try
            {
                var ui = UiSize(card);
                var rend = RendererSize(card);
                var canv = CanvasSize(card);
                int nGraphics = 0;
                try { nGraphics = card.GetComponentsInChildren<UnityEngine.UI.Graphic>(true).Length; } catch { }

                EveryonePicksPlugin.Log(string.Format(
                    "[measure] players={0} cards={1} grid={2}x{3} | natural={4:F2}x{5:F2} " +
                    "(canvas {6:F2} ui {7:F2} renderer {8:F2}, {9} graphics) sizeCfg={10:F2} " +
                    "scale={11:F3} -> drawn {12:F2} of {13:F2} usable ({14:F1}% of screen)",
                    slots.Count, entries.Count, cols, rows,
                    cardWnat, cardHnat, canv.x, ui.x, rend.x, nGraphics,
                    EveryonePicksPlugin.RevealCardSize.Value, scale,
                    cardWnat * scale, usableW,
                    (cardWnat * scale) / Mathf.Max(0.001f, usableW) * 100f));
            }
            catch (Exception e) { EveryonePicksPlugin.Warn("measure dump failed: " + e.Message); }
        }

        /// <summary>Renderer-only size, kept separate so the dump can compare the two routes.</summary>
        private static Vector3 RendererSize(GameObject go)
        {
            try
            {
                var rs = go.GetComponentsInChildren<Renderer>(true);
                if (rs == null || rs.Length == 0) return Vector3.zero;
                bool any = false; Bounds b = default(Bounds);
                foreach (var r in rs)
                {
                    if (r == null || !r.enabled) continue;
                    if (r.bounds.size.sqrMagnitude <= 0.000001f) continue;
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
                if (!any) return Vector3.zero;
                var s = go.transform.localScale;
                return new Vector3(
                    Mathf.Approximately(s.x, 0f) ? b.size.x : b.size.x / s.x,
                    Mathf.Approximately(s.y, 0f) ? b.size.y : b.size.y / s.y,
                    b.size.z);
            }
            catch { return Vector3.zero; }
        }

        /// <summary>World size of the card's own Canvas rect, which is its visible frame.</summary>
#endif

        private static Vector3 CanvasSize(GameObject go)
        {
            try
            {
                var canvases = go.GetComponentsInChildren<Canvas>(true);
                if (canvases == null || canvases.Length == 0) return Vector3.zero;

                // The innermost canvas that actually belongs to the card, i.e. the smallest one.
                bool any = false;
                Vector3 best = Vector3.zero;
                var corners = new Vector3[4];

                foreach (var cv in canvases)
                {
                    if (cv == null) continue;
                    var rt = cv.GetComponent<RectTransform>();
                    if (rt == null) continue;
                    if (rt.rect.width <= 0.0001f || rt.rect.height <= 0.0001f) continue;

                    // Corners come back in world space, so a tilted card measures WIDER than a
                    // flat one: the diagonal picks up part of the card's height. Since this
                    // number sets the shared scale for every card on screen, that made flattening
                    // one player's fan quietly enlarge everyone else's cards. Converting into the
                    // card's own local space removes rotation and scale together, and gives the
                    // card's true size in natural units.
                    rt.GetWorldCorners(corners);
                    var a = go.transform.InverseTransformPoint(corners[0]);
                    var b = go.transform.InverseTransformPoint(corners[2]);

                    var size = new Vector3(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y), Mathf.Abs(b.z - a.z));
                    if (size.x <= 0.0001f) continue;

                    if (!any || size.x < best.x) { best = size; any = true; }
                }

                if (!any) return Vector3.zero;

                return best;
            }
            catch { return Vector3.zero; }
        }

        /// <summary>World size of a UI subtree, via RectTransform corners.</summary>
        private static Vector3 UiSize(GameObject go)
        {
            try
            {
                // The card's own Canvas rect is its real frame. The union of every drawn graphic
                // still came out about five times too large, because art inside the card (glow,
                // outline, backing) extends well beyond the frame you actually see.
                var canvasRect = CanvasSize(go);
                if (canvasRect.sqrMagnitude > 0.000001f) return canvasRect;

                var graphics = go.GetComponentsInChildren<UnityEngine.UI.Graphic>(true);
                if (graphics == null || graphics.Length == 0) return Vector3.zero;

                bool any = false;
                Vector3 min = Vector3.zero, max = Vector3.zero;
                var corners = new Vector3[4];

                foreach (var g in graphics)
                {
                    if (g == null || !g.enabled) continue;
                    if (!g.gameObject.activeInHierarchy) continue;

                    var rt = g.rectTransform;
                    if (rt == null) continue;
                    if (rt.rect.width <= 0.0001f || rt.rect.height <= 0.0001f) continue;

                    // Local space again, for the reason given in CanvasSize: a world-space union
                    // grows with rotation, and this figure scales every card on screen.
                    rt.GetWorldCorners(corners);
                    foreach (var c in corners)
                    {
                        var local = go.transform.InverseTransformPoint(c);
                        if (!any) { min = max = local; any = true; continue; }
                        min = Vector3.Min(min, local);
                        max = Vector3.Max(max, local);
                    }
                }

                if (!any) return Vector3.zero;

                return max - min;
            }
            catch { return Vector3.zero; }
        }

        /// <summary>Unscaled size, so repeated layouts don't compound the scale.</summary>
        /// <summary>
        /// Put the figure's sprites in front of the card. They are copied from the player, so
        /// they inherit the player's sorting order, which sits behind the pick-phase card art.
        /// </summary>
        private static void LiftAbove(GameObject figure, GameObject card)
        {
            if (figure == null) return;

            try
            {
                // Cards are UI, so their order lives on Canvas components, not Renderers. Reading
                // Renderer order alone returned nothing and left the figure sorted BEHIND the very
                // card it is meant to stand on top of.
                int top = int.MinValue;
                string layer = null;
                int lowest = int.MaxValue;

                if (card != null)
                {
                    foreach (var cv in card.GetComponentsInChildren<Canvas>(true))
                    {
                        if (cv == null) continue;
                        if (cv.sortingOrder > top) { top = cv.sortingOrder; layer = cv.sortingLayerName; }
                    }

                    foreach (var r in card.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null) continue;
                        if (r.sortingOrder > top) { top = r.sortingOrder; layer = r.sortingLayerName; }
                    }
                }

                // Clear the whole fan, however many cards it holds.
                top = Mathf.Max(top, CardSortBase) + CardSortStep * 10;

                var parts = figure.GetComponentsInChildren<SpriteRenderer>(true);
                foreach (var sr in parts)
                {
                    if (sr == null) continue;
                    if (baseOrders.TryGetValue(sr, out int b) && b < lowest) lowest = b;
                }
                if (lowest == int.MaxValue) lowest = 0;

                foreach (var sr in parts)
                {
                    if (sr == null) continue;
                    if (!string.IsNullOrEmpty(layer)) sr.sortingLayerName = layer;

                    // Read from the snapshot, never from the current value: feeding the output
                    // back in every frame made the figure's layers strobe.
                    baseOrders.TryGetValue(sr, out int b);
                    sr.sortingOrder = top + (b - lowest);
                }
            }
            catch { }
        }

        /// <summary>
        /// Re-run the layout each frame: renderer bounds are not valid on the frame a card is
        /// instantiated, so the first placement is always measured against zeros.
        /// </summary>
        internal static void Tick()
        {
            if (entries.Count == 0) return;
            try { Layout(); } catch { }
        }

        private static float RawWidth(GameObject go) => RawSize(go).x;
        private static float RawHeight(GameObject go) => RawSize(go).y;

        private static Vector3 RawSize(GameObject go)
        {
            if (go == null) return Vector3.zero;
            try
            {
                // A ROUNDS card's face is UI drawn by CanvasRenderer, which classic Renderer
                // bounds do not see at all. Measuring only Renderers gave a size for the few
                // sprite bits and missed the card itself, so every scale and offset here was
                // computed from the wrong number.
                var ui = UiSize(go);
                if (ui.sqrMagnitude > 0.000001f) return ui;

                var rs = go.GetComponentsInChildren<Renderer>(true);
                if (rs == null || rs.Length == 0) return Vector3.zero;

                // Only renderers that are actually drawing. A disabled renderer reports a
                // zero-size bounds at the world origin, and these objects sit parked well away
                // from it, so encapsulating one produced a bounding box thousands of units tall.
                // Everything then scaled down to nothing and the reveal looked empty.
                bool any = false;
                Bounds b = default(Bounds);

                foreach (var r in rs)
                {
                    if (r == null || !r.enabled) continue;

                    var rb = r.bounds;
                    if (rb.size.sqrMagnitude <= 0.000001f) continue;

                    if (!any) { b = rb; any = true; }
                    else b.Encapsulate(rb);
                }

                if (!any) return Vector3.zero;

                var s = go.transform.localScale;
                return new Vector3(
                    Mathf.Approximately(s.x, 0f) ? b.size.x : b.size.x / s.x,
                    Mathf.Approximately(s.y, 0f) ? b.size.y : b.size.y / s.y,
                    b.size.z);
            }
            catch { return Vector3.zero; }
        }

        /// <summary>
        /// These are decoration. Anything that could collide, be networked or run game logic comes
        /// off - loose card objects with live colliders is a bug this modpack has already paid for.
        /// </summary>
        private static void Sanitise(GameObject go)
        {
            try
            {
                foreach (var c in go.GetComponentsInChildren<Collider2D>(true)) c.enabled = false;
                foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;

                foreach (var rb in go.GetComponentsInChildren<Rigidbody2D>(true))
                {
                    rb.simulated = false;
                    rb.isKinematic = true;
                }

                foreach (var pv in go.GetComponentsInChildren<Photon.Pun.PhotonView>(true))
                    UnityEngine.Object.Destroy(pv);
            }
            catch { }
        }

        internal static void Cleanup()
        {
            foreach (var e in entries)
            {
                try
                {
                    if (e.card != null) UnityEngine.Object.Destroy(e.card);
                    if (e.figure != null) UnityEngine.Object.Destroy(e.figure);
                }
                catch { }
            }
            entries.Clear();
            slots.Clear();
            hoverMemory.Clear();
            seen.Clear();
            baseOrders.Clear();
#if EP_DEBUG
            dumpedForToken = -1;
#endif
            shown = false;
            cachedCam = null;
        }

        // ---------------------------------------------------------------- name plates

        private static void Build()
        {
            chipTex = new Texture2D(1, 1);
            chipTex.SetPixel(0, 0, Color.white);
            chipTex.Apply();
            chipTex.hideFlags = HideFlags.HideAndDontSave;

            nameStyle = new GUIStyle
            {
                fontSize = 17,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
        }

        internal static void DrawLabels()
        {
            if (entries.Count == 0 || !shown || slots.Count == 0) return;
            if (Event.current.type != EventType.Repaint) return;
            if (nameStyle == null) Build();

            var cam = ResolveCamera();
            if (cam == null) return;

            foreach (var sl in slots)
            {
                if (sl.cards.Count == 0) continue;

                Vector3 screen;
                try { screen = cam.WorldToScreenPoint(new Vector3(sl.centre.x, sl.topWorld, 0f)); }
                catch { continue; }
                if (screen.z <= 0f) continue;

                Vector3 edge;
                try { edge = cam.WorldToScreenPoint(new Vector3(sl.centre.x + sl.halfWidth, sl.topWorld, 0f)); }
                catch { continue; }

                float halfW = Mathf.Max(50f, Mathf.Abs(edge.x - screen.x));
                float cx = screen.x;
                float labelY = Screen.height - screen.y - 30f;

                var chip = new Rect(cx - halfW * 0.55f, labelY + 21f, halfW * 1.1f, 3f);
                GUI.color = sl.color;
                GUI.DrawTexture(chip, chipTex);
                GUI.color = Color.white;

                GUI.Label(new Rect(cx - halfW, labelY, halfW * 2f, 22f), sl.name, nameStyle);
            }
        }
    }
}
