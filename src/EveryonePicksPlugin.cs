using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnboundLib.GameModes;
using UnboundLib.Networking;
using UnityEngine;

namespace EveryonePicks
{
    [BepInPlugin(ModId, ModName, Version)]
    [BepInDependency("com.willis.rounds.unbound")]
    [BepInDependency("pykess.rounds.plugins.moddingutils")]
    [BepInProcess("Rounds.exe")]
    public class EveryonePicksPlugin : BaseUnityPlugin, IInRoomCallbacks, IMatchmakingCallbacks
    {
        public const string ModId = "com.redredrain.rounds.simulpicks";
        public const string ModName = "EveryonePicks";
        public const string Version = "0.3.34";

        public static EveryonePicksPlugin Instance;

        public static ConfigEntry<int> PickTimeSeconds;
        public static ConfigEntry<bool> ShowPickBoard;
        public static ConfigEntry<float> PickBoardScale;
        public static ConfigEntry<bool> ShowReveal;
        public static ConfigEntry<bool> ShowProblems;
        public static ConfigEntry<bool> ShowWaitingText;
        public static ConfigEntry<float> RevealSeconds;
        public static ConfigEntry<float> RevealCardSize;

        private void Awake()
        {
            Instance = this;

            PickTimeSeconds = Config.Bind("EveryonePicks", "PickTimeSeconds", 45,
                "Seconds each player gets per pick before their pick is auto-completed. " +
                "Extra picks granted mid-pick each get their own allowance.");

            ShowPickBoard = Config.Bind("EveryonePicks", "ShowPickBoard", true,
                "Show the top-right board listing who has picked and who everyone is still waiting on.");

            PickBoardScale = Config.Bind("EveryonePicks", "PickBoardScale", 1.0f,
                "Size multiplier for the pick board.");

            ShowReveal = Config.Bind("EveryonePicks", "ShowReveal", true,
                "At the end of the phase, show every card picked this round at once instead of " +
                "letting them trickle into the side bar one at a time.");

            RevealSeconds = Config.Bind("EveryonePicks", "RevealSeconds", 3.0f,
                "How long the end-of-phase reveal stays on screen.");

            RevealCardSize = Config.Bind("EveryonePicks", "RevealCardSize", 1.0f,
                "Size of the revealed cards. Raise it if they look too small.");

            ShowWaitingText = Config.Bind("EveryonePicks", "ShowWaitingText", true,
                "Show WAITING in the game's big screen text while you have nothing to look at.");

            ShowProblems = Config.Bind("EveryonePicks", "ShowProblems", true,
                "Show a message on screen when something goes wrong, instead of failing quietly.");

            new Harmony(ModId).PatchAll();
            PhotonNetwork.AddCallbackTarget(this);
        }

        private void Start()
        {
            SoftPatches.Apply(new Harmony(ModId + ".soft"));

            GameModeManager.AddHook("GameStart", gm => State.OnGameStart());
            GameModeManager.AddHook("GameStart", gm => Announce());
            // High priority: phaseActive is the gate every interception reads, so it has to be set
            // before any other mod's PickStart hook can start issuing picks.
            GameModeManager.AddHook("PickStart", gm => State.OnPickStart(), 1000);
            GameModeManager.AddHook("PickEnd", gm => State.Barrier(), 600);
            GameModeManager.AddHook("GameEnd", gm => State.HardResetHook());

            Menu.Register();
            Diagnostics.BuildOwnerMap();
        }

        private void OnDestroy()
        {
            PhotonNetwork.RemoveCallbackTarget(this);
            Diagnostics.Unhook();
        }

        private void Update()
        {
            PickReveal.Tick();
            WaitingBanner.Tick();
            LeaverResilience.Tick();
            State.ReconcileLocalPick();
            Diagnostics.TickStuck();

#if EP_DEBUG
            // F9: offline/solo debug draft. Compiled out of releases along with the F10 reveal,
            // so a shipped build binds no keys of its own.
            if (Input.GetKeyDown(KeyCode.F9)
                && (PhotonNetwork.OfflineMode || PhotonNetwork.CurrentRoom == null || PhotonNetwork.CurrentRoom.PlayerCount <= 1)
                && !State.phaseActive && !State.localRunnerBusy
                && CardChoice.instance != null && !CardChoice.instance.IsPicking
                && PlayerManager.instance != null)
            {
                var me = PlayerManager.instance.players.FirstOrDefault(p => p != null && State.IsLocalPlayer(p));
                if (me != null)
                {
                    Log("F9 debug pick starting");
                    StartCoroutine(DebugRun(me.playerID));
                }
            }

            // F10: fake a whole reveal so the visuals can be checked without a lobby.
            // Compiled out of release builds - see EpDebug in the csproj.
            if (Input.GetKeyDown(KeyCode.F10) && DebugReveal.Allowed())
                DebugReveal.Trigger(this);

            // Simulated state must never outlive its run, or it would follow the player into a
            // real match with the phase flag still set.
            DebugReveal.Watchdog(this);
#endif
        }

        private static IEnumerator Announce()
        {
            Diagnostics.SayHello();
            yield break;
        }

#if EP_DEBUG
        private IEnumerator DebugRun(int pid)
        {
            yield return State.OnPickStart();
            State.RecordEntitlement(pid, PickerType.Player);
            State.EnqueueLocalPick(pid, PickerType.Player);
            yield return State.Barrier();
        }
#endif

        private void OnGUI()
        {
            PickBoard.Draw();
            PickReveal.DrawLabels();
            Diagnostics.Draw();
        }

        internal static void Log(string m) => Instance.Logger.LogInfo("[EveryonePicks] " + m);
        internal static void Warn(string m) => Instance.Logger.LogWarning("[EveryonePicks] " + m);

        // ---------- networked state ----------

        /// <summary>Version handshake, so a mixed-version lobby can be called out.</summary>
        [UnboundRPC]
        public static void RPCA_SimulHello(int actorNumber, string version)
            => Diagnostics.StoreHello(actorNumber, version);

        [UnboundRPC]
        public static void RPCA_SimulResult(int token, int playerID, int pickIdx, string cardName, bool cursed)
            => State.StoreResult(token, playerID, pickIdx, cardName, cursed);

        [UnboundRPC]
        public static void RPCA_SimulSessionDone(int token, int playerID, int count)
            => State.StoreSessionDone(token, playerID, count);

        [UnboundRPC]
        public static void RPCA_SimulApplySet(int token, string[] packed)
            => State.StoreApplySet(token, packed);

        /// <summary>
        /// Live "who is still picking" signal. Drives the pick board AND keeps the barrier alive
        /// while a player legitimately works through extra picks.
        /// </summary>
        [UnboundRPC]
        public static void RPCA_SimulStatus(int token, int playerID, int remaining, bool done, float pickSeconds)
            => State.StoreStatus(token, playerID, remaining, done, pickSeconds);

        // ---------- photon callbacks ----------

        public void OnLeftRoom() => State.HardReset("left room");
        public void OnJoinedRoom()
        {
            State.HardReset("joined room");
            Diagnostics.ResetRoom();
            Diagnostics.SayHello();
        }

        public void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer)
        {
            // 0.2.1 ignored this entirely, so the barrier kept waiting on a player who could never
            // report until the whole budget expired.
            try
            {
                Log("Player left the room: " + (otherPlayer != null ? otherPlayer.NickName : "?") + ".");
                State.DropDepartedPlayers();
            }
            catch (Exception e) { Warn("Leaver cleanup failed: " + e.Message); }
        }

        public void OnMasterClientSwitched(Photon.Realtime.Player newMasterClient)
        {
            if (State.phaseActive)
                Log("Master client switched mid-phase to actor " + newMasterClient?.ActorNumber + ".");
        }

        public void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer) { }
        public void OnRoomPropertiesUpdate(ExitGames.Client.Photon.Hashtable propertiesThatChanged) { }
        public void OnPlayerPropertiesUpdate(Photon.Realtime.Player targetPlayer, ExitGames.Client.Photon.Hashtable changedProps) { }
        public void OnFriendListUpdate(List<FriendInfo> friendList) { }
        public void OnCreatedRoom() { }
        public void OnCreateRoomFailed(short returnCode, string message) { }
        public void OnJoinRoomFailed(short returnCode, string message) { }
        public void OnJoinRandomFailed(short returnCode, string message) { }
    }
}
