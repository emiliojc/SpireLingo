using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// Keeps a card lifted while the word popup shows one of its words. Godot sends mouse enter/exit to controls
/// before any node's _Input, so the card's hitbox unfocuses as soon as the mouse leaves it; that unfocus is
/// held back here and run when the popup closes.
/// </summary>
internal static class HoverHold
{
    private static readonly MethodInfo? Unfocus = AccessTools.Method(typeof(NCardHolder), "OnUnfocus");
    private static readonly List<NCardHolder> Held = [];

    /// <summary>
    /// The label whose word the popup is showing, if any.
    /// </summary>
    internal static Control? SelectedLabel { get; set; }

    internal static bool ShouldHold(NCardHolder holder)
    {
        if (!DiscoveryMode.IsActive || SelectedLabel is not { } label || !GodotObject.IsInstanceValid(label) || !holder.IsAncestorOf(label))
            return false;
        if (!Held.Contains(holder)) Held.Add(holder);
        return true;
    }

    /// <summary>
    /// While a hand card is held up, other hand cards don't take focus, since the hand lifts only one card.
    /// </summary>
    internal static bool BlocksFocus(NCardHolder holder) =>
        holder is NHandCardHolder && Held.Any(h => h != holder && h is NHandCardHolder && GodotObject.IsInstanceValid(h));

    /// <summary>
    /// Lets held cards drop back, unless the mouse is on one again (then it is still rightly lifted).
    /// </summary>
    internal static void Release(Vector2 mouse)
    {
        SelectedLabel = null;
        foreach (var holder in Held.ToList())
        {
            if (!GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree()) continue;
            var hitbox = holder.Hitbox;
            if (hitbox != null && GodotObject.IsInstanceValid(hitbox) &&
                new Rect2(Vector2.Zero, hitbox.Size).HasPoint(hitbox.GetGlobalTransformWithCanvas().AffineInverse() * mouse))
                continue;
            Unfocus?.Invoke(holder, []);
        }
        Held.Clear();
    }
}

[HarmonyPatch]
static class HoverHoldPatch
{
    // NHandCardHolder and NPreviewCardHolder override OnUnfocus; the base covers the others.
    static IEnumerable<MethodBase> TargetMethods() =>
        new[] { typeof(NCardHolder), typeof(NHandCardHolder), typeof(NPreviewCardHolder) }
            .Select(t => AccessTools.DeclaredMethod(t, "OnUnfocus"))
            .Where(m => m != null)!;

    [HarmonyPrefix]
    static bool Prefix(NCardHolder __instance) => !HoverHold.ShouldHold(__instance);
}

[HarmonyPatch(typeof(NHandCardHolder), "OnFocus")]
static class HoverHoldFocusPatch
{
    [HarmonyPrefix]
    static bool Prefix(NCardHolder __instance) => !HoverHold.BlocksFocus(__instance);
}
