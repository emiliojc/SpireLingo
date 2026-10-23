using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// In discovery mode tooltips open and close as usual, but a removal waits a moment and is skipped while the
/// mouse is over that tooltip, so the player can move from a card onto its tooltip and click its words.
/// </summary>
internal static class TipKeeper
{
    private const ulong GraceMs = 350;

    private static readonly FieldInfo? ActiveSets = AccessTools.Field(typeof(NHoverTipSet), "_activeHoverTips");
    private static readonly FieldInfo? TextContainer = AccessTools.Field(typeof(NHoverTipSet), "_textHoverTipContainer");
    private static readonly FieldInfo? CardContainer = AccessTools.Field(typeof(NHoverTipSet), "_cardHoverTipContainer");

    // Owner -> when the mouse was last on its tooltip (or when the game asked to remove it).
    private static readonly Dictionary<Control, ulong> Pending = [];
    // Owners whose tooltip opened while the mouse was on another tooltip (say one lying over another enemy);
    // it stays hidden until the mouse is off that other tooltip.
    private static readonly HashSet<Control> Hidden = [];
    private static bool _bypass;

    private static IDictionary? Sets => ActiveSets?.GetValue(null) as IDictionary;

    /// <summary>
    /// Hides the tips titled <paramref name="title"/> in tooltips that are already open. The game shows each tip
    /// as a node with a %Title label.
    /// </summary>
    internal static void HideTips(string title)
    {
        if (Sets is not { } sets || TextContainer == null) return;
        foreach (var set in sets.Values)
        {
            if (set is not Node node || !GodotObject.IsInstanceValid(node) || TextContainer.GetValue(set) is not Node container) continue;
            foreach (var tip in container.GetChildren())
            {
                if (tip is CanvasItem item && tip.GetNodeOrNull<Label>("%Title") is { } label &&
                    string.Equals(label.Text, title, StringComparison.OrdinalIgnoreCase))
                    item.Visible = false;
            }
        }
    }

    /// <summary>
    /// Called by the patches: true when the game's removal should run now.
    /// </summary>
    internal static bool AllowRemove(Control owner)
    {
        if (_bypass || !DiscoveryMode.IsActive) return true;
        Pending.TryAdd(owner, Time.GetTicksMsec());
        return false;
    }

    internal static bool AllowClear()
    {
        if (_bypass || !DiscoveryMode.IsActive) return true;
        if (Sets is { } sets)
            foreach (var owner in sets.Keys)
                if (owner is Control control) Pending.TryAdd(control, Time.GetTicksMsec());
        return false;
    }

    /// <summary>
    /// A new tooltip for an owner replaces the delayed one right away.
    /// </summary>
    internal static void BeforeShow(Control owner)
    {
        if (Pending.Remove(owner)) RemoveNow(owner);
    }

    /// <summary>
    /// Hides a tooltip that opened under the mouse while the mouse is reading another tooltip.
    /// </summary>
    internal static void AfterShow(Control owner, NHoverTipSet? shown)
    {
        if (!DiscoveryMode.IsActive || shown == null || !GodotObject.IsInstanceValid(shown) || Sets is not { } sets) return;
        var mouse = owner.GetViewport().GetMousePosition();
        foreach (DictionaryEntry entry in sets)
        {
            if (entry.Key == owner || entry.Value == shown || !IsOver(entry.Value, mouse)) continue;
            shown.Visible = false;
            Hidden.Add(owner);
            return;
        }
    }

    /// <summary>
    /// Runs every frame in discovery mode: closes delayed tooltips the mouse is not on, except the one holding
    /// <paramref name="selected"/>, the label whose word the popup is showing.
    /// </summary>
    internal static void Update(Vector2 mouse, Node? selected)
    {
        if (Pending.Count == 0 && Hidden.Count == 0) return;
        var sets = Sets;
        RevealHidden(sets, mouse);
        var now = Time.GetTicksMsec();
        foreach (var owner in Pending.Keys.ToList())
        {
            if (!GodotObject.IsInstanceValid(owner) || sets == null || !sets.Contains(owner))
            {
                Pending.Remove(owner);
                continue;
            }
            var set = sets[owner];
            if (IsOver(set, mouse) || (selected != null && set is Node node && node.IsAncestorOf(selected))) Pending[owner] = now;
            else if (now - Pending[owner] >= GraceMs)
            {
                Pending.Remove(owner);
                RemoveNow(owner);
            }
        }
    }

    /// <summary>
    /// Shows hidden tooltips again once the mouse is off every other tooltip (or, with <paramref name="all"/>,
    /// right away).
    /// </summary>
    private static void RevealHidden(IDictionary? sets, Vector2 mouse, bool all = false)
    {
        foreach (var owner in Hidden.ToList())
        {
            if (!GodotObject.IsInstanceValid(owner) || sets == null || !sets.Contains(owner) || sets[owner] is not Control set ||
                !GodotObject.IsInstanceValid(set))
            {
                Hidden.Remove(owner);
                continue;
            }
            if (!all && sets.Keys.Cast<object>().Any(other => other != owner && IsOver(sets[other], mouse))) continue;
            set.Visible = true;
            Hidden.Remove(owner);
        }
    }

    /// <summary>
    /// Leaving discovery mode closes everything that was only waiting, and shows what was hidden.
    /// </summary>
    internal static void Flush()
    {
        foreach (var owner in Pending.Keys.ToList())
            if (GodotObject.IsInstanceValid(owner)) RemoveNow(owner);
        Pending.Clear();
        RevealHidden(Sets, Vector2.Zero, all: true);
    }

    private static bool IsOver(object? set, Vector2 mouse)
    {
        if (set == null) return false;
        foreach (var field in new[] { TextContainer, CardContainer })
        {
            if (field?.GetValue(set) is not Control container || !GodotObject.IsInstanceValid(container) || !container.IsVisibleInTree()) continue;
            var local = container.GetGlobalTransformWithCanvas().AffineInverse() * mouse;
            if (new Rect2(Vector2.Zero, container.Size).HasPoint(local)) return true;
        }
        return false;
    }

    private static void RemoveNow(Control owner)
    {
        _bypass = true;
        try
        {
            NHoverTipSet.Remove(owner);
        }
        finally
        {
            _bypass = false;
        }
    }
}

[HarmonyPatch(typeof(NHoverTipSet))]
static class TipKeeperPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(NHoverTipSet.Remove))]
    static bool Remove(Control owner) => TipKeeper.AllowRemove(owner);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(NHoverTipSet.Clear))]
    static bool Clear() => TipKeeper.AllowClear();
}

// Both CreateAndShow overloads take the owner first.
[HarmonyPatch]
static class TipKeeperShowPatch
{
    static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(NHoverTipSet)).Where(m => m.Name == nameof(NHoverTipSet.CreateAndShow));

    [HarmonyPrefix]
    static void Prefix(Control owner) => TipKeeper.BeforeShow(owner);

    [HarmonyPostfix]
    static void Postfix(Control owner, NHoverTipSet? __result) => TipKeeper.AfterShow(owner, __result);
}
