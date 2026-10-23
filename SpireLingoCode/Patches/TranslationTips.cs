using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace SpireLingo.SpireLingoCode.Patches;

/// <summary>
/// Appends a hover tip with the target-language text of the hovered card, relic, potion or power.
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

    private static readonly MethodInfo? GetDescriptionForPile = AccessTools.Method(typeof(CardModel),
        nameof(CardModel.GetDescriptionForPile), [typeof(PileType), typeof(CardModel.DescriptionPreviewType), typeof(Creature)]);

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.GetDescriptionForPile),
        typeof(PileType), typeof(CardModel.DescriptionPreviewType), typeof(Creature))]
    private static class RememberCardDescriptionArgs
    {
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
}
