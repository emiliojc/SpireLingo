using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// Adds a "Vocabulary" button to the main menu, right below Compendium. It is a copy of the Compendium button
/// (so it looks and animates like the game's own), without that button's signal connections.
/// </summary>
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
static class MainMenuButton
{
    private const string NodeName = "SpireLingoVocabularyButton";

    [HarmonyPostfix]
    static void Postfix(NMainMenu __instance)
    {
        try
        {
            if (__instance.GetNodeOrNull<NMainMenuTextButton>("MainMenuTextButtons/CompendiumButton") is not { } compendium) return;
            var parent = compendium.GetParent();
            if (parent.GetNodeOrNull(NodeName) != null) return;

            var flags = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation);
            if (compendium.Duplicate(flags) is not NMainMenuTextButton button) return;
            button.Name = NodeName;
            parent.AddChild(button);
            parent.MoveChild(button, compendium.GetIndex() + 1);
            button.Visible = true;
            button.SetLocalization("SPIRELINGO_VOCABULARY");
            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => DiscoveryMode.Instance?.OpenVocabulary()));
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not add the Vocabulary button to the main menu: {e.Message}");
        }
    }
}
