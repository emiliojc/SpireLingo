using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// Adds a "Vocabulary" entry to the Compendium (the submenu with the card library, relics, bestiary and
/// statistics), at the end of the bottom row after Statistics and Run History, which keep their places. It is a
/// copy of the Run History button (its style and book icon suit a word list), without that button's signal
/// connections. The vocabulary screen opens on the discovery layer, above the Compendium, and Esc there closes
/// only the vocabulary screen.
/// </summary>
[HarmonyPatch(typeof(NCompendiumSubmenu), nameof(NCompendiumSubmenu._Ready))]
static class CompendiumButton
{
    private const string NodeName = "SpireLingoVocabularyButton";

    [HarmonyPostfix]
    static void Postfix(NCompendiumSubmenu __instance)
    {
        try
        {
            if (__instance.GetNodeOrNull<NCompendiumBottomButton>("%RunHistoryButton") is not { } runHistory) return;
            var parent = runHistory.GetParent();
            if (parent.GetNodeOrNull(NodeName) != null) return;

            var flags = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation);
            if (runHistory.Duplicate(flags) is not NCompendiumBottomButton button) return;
            button.Name = NodeName;
            // The copy keeps Run History's unique name; drop it so "%RunHistoryButton" still finds the original.
            button.UniqueNameInOwner = false;
            parent.AddChild(button);
            parent.MoveChild(button, runHistory.GetIndex() + 1);
            button.Visible = true;
            // Run History is greyed out without any runs; the word list is always available.
            button.SetEnabled(true);
            // Reads main_menu_ui "SPIRELINGO_VOCABULARY.title".
            button.SetLocalization("SPIRELINGO_VOCABULARY");
            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => DiscoveryMode.Instance?.OpenVocabulary()));
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not add the Vocabulary button to the Compendium: {e.Message}");
        }
    }
}
