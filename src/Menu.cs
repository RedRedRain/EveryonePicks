using TMPro;
using UnboundLib;
using UnboundLib.Utils.UI;
using UnityEngine;
using UnityEngine.UI;

namespace EveryonePicks
{
    /// <summary>
    /// Entry in the in-game Mod Options list (Unbound's mod menu).
    /// </summary>
    internal static class Menu
    {
        internal static void Register()
        {
            Unbound.RegisterMenu(EveryonePicksPlugin.ModName, () => { }, BuildMenu, null, true);

            Unbound.RegisterCredits(
                EveryonePicksPlugin.ModName,
                new[] { "RedRedRain" },
                "Thunderstore",
                "https://thunderstore.io/c/rounds/p/RedRedRain/SimulPicks/");
        }

        private static void BuildMenu(GameObject menu)
        {
            MenuHandler.CreateText("Everyone picks their card at the same time.", menu, out TextMeshProUGUI _, 30);
            MenuHandler.CreateText("Every player in the lobby needs this mod.", menu, out TextMeshProUGUI _, 22);
            Spacer(menu);

            MenuHandler.CreateText("Pick board", menu, out TextMeshProUGUI _, 26);

            MenuHandler.CreateToggle(
                EveryonePicksPlugin.ShowPickBoard.Value,
                "Show who has picked",
                menu,
                value => EveryonePicksPlugin.ShowPickBoard.Value = value,
                30);

            MenuHandler.CreateSlider(
                "Board size",
                menu,
                30,
                0.6f, 2f,
                EveryonePicksPlugin.PickBoardScale.Value,
                value => EveryonePicksPlugin.PickBoardScale.Value = value,
                out Slider _,
                false);

            Spacer(menu);
            MenuHandler.CreateText("Picked cards", menu, out TextMeshProUGUI _, 26);

            MenuHandler.CreateToggle(
                EveryonePicksPlugin.ShowReveal.Value,
                "Show picked cards on screen",
                menu,
                value => EveryonePicksPlugin.ShowReveal.Value = value,
                30);

            MenuHandler.CreateToggle(
                EveryonePicksPlugin.ShowWaitingText.Value,
                "Show WAITING while others pick",
                menu,
                value => EveryonePicksPlugin.ShowWaitingText.Value = value,
                30);

            MenuHandler.CreateSlider(
                "Card size",
                menu,
                30,
                0.2f, 6f,
                EveryonePicksPlugin.RevealCardSize.Value,
                value => EveryonePicksPlugin.RevealCardSize.Value = value,
                out Slider _,
                false);

            MenuHandler.CreateSlider(
                "How long they stay up (seconds)",
                menu,
                30,
                1f, 8f,
                EveryonePicksPlugin.RevealSeconds.Value,
                value => EveryonePicksPlugin.RevealSeconds.Value = Mathf.Round(value * 2f) / 2f,
                out Slider _,
                false);

            Spacer(menu);
            MenuHandler.CreateText("Timing", menu, out TextMeshProUGUI _, 26);

            MenuHandler.CreateSlider(
                "Pick time (seconds)",
                menu,
                30,
                10f, 180f,
                EveryonePicksPlugin.PickTimeSeconds.Value,
                value => EveryonePicksPlugin.PickTimeSeconds.Value = Mathf.RoundToInt(value),
                out Slider _,
                true);

            MenuHandler.CreateText(
                "Per pick. A card that grants extra picks gives another full allowance for each one.",
                menu, out TextMeshProUGUI _, 20);

            Spacer(menu);
            MenuHandler.CreateText("Problems", menu, out TextMeshProUGUI _, 26);

            MenuHandler.CreateToggle(
                EveryonePicksPlugin.ShowProblems.Value,
                "Say on screen when something goes wrong",
                menu,
                value => EveryonePicksPlugin.ShowProblems.Value = value,
                30);
        }

        private static void Spacer(GameObject menu)
            => MenuHandler.CreateText(" ", menu, out TextMeshProUGUI _, 30);
    }
}
