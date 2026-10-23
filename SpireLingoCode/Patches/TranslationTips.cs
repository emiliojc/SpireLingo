using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace SpireLingo.SpireLingoCode.Patches;

/// <summary>
/// Appends a hover tip with the target-language text of the hovered card, relic, potion, power or enemy intent.
/// The text is produced by re-running the game's own formatting with target-language lookups,
/// so numbers like damage and block match what the card shows.
/// </summary>
[HarmonyPatch]
static class TranslationTips
{
    private const string TipIdPrefix = "SPIRELINGO-TRANSLATION";

    private static readonly PropertyInfo? TitleProperty = AccessTools.Property(typeof(HoverTip), "Title");

    private static LocString TipTitle() => new("gameplay_ui", "SPIRELINGO-TRANSLATION.title");

    private static IHoverTip MakeTip(string title, string description, string id)
    {
        var body = string.IsNullOrEmpty(description) ? $"[gold]{title}[/gold]" : $"[gold]{title}[/gold]\n{description}";
        return new HoverTip(TipTitle(), body) { Id = $"{TipIdPrefix}-{id}" };
    }

    private static string TextOf(object? value) => value switch
    {
        null => "",
        string s => s,
        LocString loc => loc.GetFormattedText(),
        _ => value.ToString() ?? ""
    };

    /// <summary>
    /// Re-reads a model's own first tip in the target language and appends it as a new tip.
    /// </summary>
    private static IEnumerable<IHoverTip> AppendFromOwnTip(IEnumerable<IHoverTip> tips, AbstractModel model,
        Func<IEnumerable<IHoverTip>> regenerate)
    {
        if (TargetText.IsActive || !TargetText.IsUseful) return tips;

        try
        {
            // Formatting happens inside the flag, including any title that is still a LocString.
            var translated = TargetText.Format<(string title, string description)?>(() =>
            {
                if (regenerate().FirstOrDefault() is not HoverTip own) return null;
                return (TextOf(TitleProperty?.GetValue(own)), own.Description ?? "");
            });
            if (translated == null) return tips;

            return tips.Append(MakeTip(translated.Value.title, translated.Value.description, model.Id.ToString()));
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not translate tip for {model.Id}: {e.Message}");
            return tips;
        }
    }

    [HarmonyPatch(typeof(RelicModel), nameof(RelicModel.HoverTips), MethodType.Getter)]
    private static class RelicTips
    {
        [HarmonyPostfix]
        static void Postfix(RelicModel __instance, ref IEnumerable<IHoverTip> __result)
        {
            if (!SpireLingoConfig.ShowOnRelics) return;
            var relic = __instance;
            __result = AppendFromOwnTip(__result, relic, () => relic.HoverTips);
        }
    }

    [HarmonyPatch(typeof(PotionModel), nameof(PotionModel.HoverTips), MethodType.Getter)]
    private static class PotionTips
    {
        [HarmonyPostfix]
        static void Postfix(PotionModel __instance, ref IEnumerable<IHoverTip> __result)
        {
            if (!SpireLingoConfig.ShowOnPotions) return;
            var potion = __instance;
            __result = AppendFromOwnTip(__result, potion, () => potion.HoverTips);
        }
    }

    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.HoverTips), MethodType.Getter)]
    private static class PowerTips
    {
        [HarmonyPostfix]
        static void Postfix(PowerModel __instance, ref IEnumerable<IHoverTip> __result)
        {
            if (!SpireLingoConfig.ShowOnPowers) return;
            var power = __instance;
            __result = AppendFromOwnTip(__result, power, () => power.HoverTips);
        }
    }

    /// <summary>
    /// Cards have no tip of their own, so the card's description is formatted again with the
    /// arguments the game last used to show it.
    /// </summary>
    private static readonly ConditionalWeakTable<CardModel, object?[]> LastDescriptionArgs = new();

    // CardModel.DescriptionPreviewType is not public, so the overload is found by its parameter types' names.
    private static readonly MethodInfo? GetDescriptionForPile = AccessTools.GetDeclaredMethods(typeof(CardModel))
        .FirstOrDefault(m => m.Name == "GetDescriptionForPile" && m.GetParameters() is [
            { ParameterType: var pile }, { ParameterType: var preview }, { ParameterType: var target }
        ] && pile == typeof(PileType) && preview.Name == "DescriptionPreviewType" && target == typeof(Creature));

    [HarmonyPatch]
    private static class RememberCardDescriptionArgs
    {
        static bool Prepare()
        {
            if (GetDescriptionForPile == null) MainFile.Logger.Warn("CardModel.GetDescriptionForPile not found; card tooltips will show names only.");
            return GetDescriptionForPile != null;
        }

        static MethodBase TargetMethod() => GetDescriptionForPile!;

        [HarmonyPostfix]
        static void Postfix(CardModel __instance, object?[] __args)
        {
            if (TargetText.IsActive) return;
            LastDescriptionArgs.AddOrUpdate(__instance, (object?[])__args.Clone());
        }
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.HoverTips), MethodType.Getter)]
    private static class CardTips
    {
        [HarmonyPostfix]
        static void Postfix(CardModel __instance, ref IEnumerable<IHoverTip> __result)
        {
            if (!SpireLingoConfig.ShowOnCards || TargetText.IsActive || !TargetText.IsUseful) return;

            var card = __instance;
            try
            {
                var (title, description) = TargetText.Format(() =>
                {
                    var t = card.TitleLocString.GetFormattedText();
                    var d = GetDescriptionForPile != null && LastDescriptionArgs.TryGetValue(card, out var args)
                        ? GetDescriptionForPile.Invoke(card, args) as string ?? ""
                        : "";
                    return (t, d);
                });
                __result = __result.Append(MakeTip(title, description, card.Id.ToString()));
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"Could not translate card {card.Id}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// An intent hands its creature a single tip to show, so the translation is added on the way
    /// into <see cref="NCreature.ShowHoverTips"/> while that intent is being hovered.
    /// </summary>
    [ThreadStatic] private static NIntent? _hoveredIntent;

    private static readonly AccessTools.FieldRef<NIntent, AbstractIntent?> IntentField = AccessTools.FieldRefAccess<NIntent, AbstractIntent?>("_intent");
    private static readonly AccessTools.FieldRef<NIntent, IEnumerable<Creature>?> TargetsField = AccessTools.FieldRefAccess<NIntent, IEnumerable<Creature>?>("_targets");
    private static readonly AccessTools.FieldRef<NIntent, Creature?> OwnerField = AccessTools.FieldRefAccess<NIntent, Creature?>("_owner");

    // OnHovered is private, so it is named by string.
    [HarmonyPatch(typeof(NIntent), "OnHovered")]
    private static class IntentHovered
    {
        [HarmonyPrefix]
        static void Prefix(NIntent __instance) => _hoveredIntent = __instance;

        [HarmonyFinalizer]
        static void Finalizer() => _hoveredIntent = null;
    }

    [HarmonyPatch(typeof(NCreature), nameof(NCreature.ShowHoverTips))]
    private static class IntentTips
    {
        [HarmonyPrefix]
        static void Prefix(ref IEnumerable<IHoverTip> hoverTips)
        {
            var node = _hoveredIntent;
            if (node == null || !SpireLingoConfig.ShowOnIntents || TargetText.IsActive || !TargetText.IsUseful) return;

            var intent = IntentField(node);
            var owner = OwnerField(node);
            if (intent == null || owner == null) return;
            var targets = TargetsField(node) ?? [];

            try
            {
                // The damage and hit count come from the same calculation the intent label uses.
                var tip = TargetText.Format(() => intent.GetHoverTip(targets, owner));
                hoverTips = hoverTips.Append(MakeTip(tip.Title ?? "", tip.Description ?? "", $"INTENT-{intent.IntentType}"));
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"Could not translate intent {intent.GetType().Name}: {e.Message}");
            }
        }
    }
}
