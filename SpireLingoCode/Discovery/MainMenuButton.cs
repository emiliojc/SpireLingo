using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// Remembers the main menu's patch notes button, which tells the discovery layer the main menu is showing (the
/// corner magnifier stays hidden there).
/// </summary>
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
static class MainMenuButton
{
    private static readonly AccessTools.FieldRef<NMainMenu, NPatchNotesButton?> PatchNotesField =
        AccessTools.FieldRefAccess<NMainMenu, NPatchNotesButton?>("_patchNotesButtonNode");

    /// <summary>
    /// The main menu's patch notes button (top right); shown while the main menu is.
    /// </summary>
    internal static Control? PatchNotes { get; private set; }

    [HarmonyPostfix]
    static void Postfix(NMainMenu __instance)
    {
        try
        {
            PatchNotes = PatchNotesField(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not find the main menu's patch notes button: {e.Message}");
        }
    }
}

/// <summary>
/// Adds a "Vocabulary" entry to the Compendium (the submenu with the card library, relics, bestiary and
/// statistics), right after Statistics. It is a copy of the Statistics button, so it looks and animates like the
/// game's own, without that button's signal connections. The vocabulary screen opens on the discovery layer,
/// above the Compendium, and Esc there closes only the vocabulary screen.
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
            if (__instance.GetNodeOrNull<NCompendiumBottomButton>("%StatisticsButton") is not { } statistics) return;
            var parent = statistics.GetParent();
            if (parent.GetNodeOrNull(NodeName) != null) return;

            var flags = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation);
            if (statistics.Duplicate(flags) is not NCompendiumBottomButton button) return;
            button.Name = NodeName;
            // The copy keeps Statistics' unique name; drop it so "%StatisticsButton" still finds the original.
            button.UniqueNameInOwner = false;
            parent.AddChild(button);
            parent.MoveChild(button, statistics.GetIndex() + 1);
            button.Visible = true;
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
