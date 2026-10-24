using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// During a fight, the discovery button sits in the game's top bar, just before the map button, a little smaller
/// than it (like the bar's other icons with their padding) and centred on it. Elsewhere it is hidden (F8 works
/// everywhere).
/// It is added to the map button's own box container, so the bar lays it out with the timer, map and deck.
/// </summary>
[HarmonyPatch(typeof(NTopBar), nameof(NTopBar._Ready))]
static class TopBarButton
{
    /// <summary>
    /// The button in the current top bar, if there is one.
    /// </summary>
    internal static TextureButton? Button { get; private set; }

    private static Control? _map;

    [HarmonyPostfix]
    static void Postfix(NTopBar __instance)
    {
        try
        {
            var map = __instance.Map;
            if (map?.GetParent() is not BoxContainer row)
            {
                MainFile.Logger.Warn("Discovery: the top bar's map button isn't in a box container; the button stays in the corner");
                return;
            }

            var button = new TextureButton
            {
                Name = "SpireLingoDiscoverButton",
                TextureNormal = Ui.MagnifierIcon(),
                IgnoreTextureSize = true,
                StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
                FocusMode = Control.FocusModeEnum.None,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            };
            Ui.GrowOnHover(button);
            Ui.AttachDiscoveryTip(button, DiscoveryMode.KeyName);
            button.Pressed += () => DiscoveryMode.Instance?.Toggle();
            row.AddChild(button);
            row.MoveChild(button, map.GetIndex());
            Button = button;
            _map = map;
            Update();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Discovery: could not add the button to the top bar: {e.Message}");
        }
    }

    /// <summary>
    /// Keeps the button at 80% of the map button's height (known only after layout), shown during fights per the
    /// setting.
    /// Returns whether it is on screen.
    /// </summary>
    internal static bool Update()
    {
        if (Button == null || !GodotObject.IsInstanceValid(Button))
        {
            Button = null;
            return false;
        }
        Button.Visible = SpireLingoConfig.ShowDiscoveryButton && CombatManager.Instance is { IsInProgress: true };
        if (_map != null && GodotObject.IsInstanceValid(_map) && _map.Size.Y > 0)
        {
            var size = new Vector2(_map.Size.Y, _map.Size.Y) * 0.8f;
            if (Button.CustomMinimumSize != size)
            {
                Button.CustomMinimumSize = size;
                Button.PivotOffset = size / 2;
            }
        }
        return Button.IsVisibleInTree();
    }
}
