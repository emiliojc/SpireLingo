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
using SpireLingo.SpireLingoCode.Discovery;

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

    private static LocString TipTitle() => new("gameplay_ui", "SPIRELINGO-TRANSLATION.title");

    /// <summary>
    /// The title our tips show, in the game's language.
    /// </summary>
    internal static string TipTitleText => TipTitle().GetFormattedText();

    /// <summary>
    /// No translation tips while discovery mode is on, so the screen stays uncluttered.
    /// </summary>
    private static bool Skip => !SpireLingoConfig.ShowTranslationTips || TargetText.IsActive || !TargetText.IsUseful || DiscoveryMode.IsActive;

    private static IHoverTip MakeTip(string title, string description, string id)
    {
        var body = string.IsNullOrEmpty(description) ? $"[gold]{title}[/gold]" : $"[gold]{title}[/gold]\n{description}";
        return new HoverTip(TipTitle(), body) { Id = $"{TipIdPrefix}-{id}" };
    }

    /// <summary>
    /// Re-reads a model's own first tip in the target language and appends it as a new tip.
    /// </summary>
    private static IEnumerable<IHoverTip> AppendFromOwnTip(IEnumerable<IHoverTip> tips, AbstractModel model,
        Func<IEnumerable<IHoverTip>> regenerate)
    {
        if (Skip) return tips;

        try
        {
            // A HoverTip formats its title and description when it is made, so it is made inside the flag.
            var translated = TargetText.Format<(string title, string description)?>(() =>
                regenerate().FirstOrDefault() is HoverTip own ? (own.Title ?? "", own.Description ?? "") : null);
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
            if (!SpireLingoConfig.ShowOnCards || Skip) return;

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
    /// The game builds an intent's tip with <see cref="AbstractIntent.GetHoverTip"/> both when the intent
    /// icon is hovered and when the creature is (as part of <see cref="Creature.HoverTips"/>), then shows the
    /// list with <see cref="NCreature.ShowHoverTips"/>. Each built tip's translation is remembered here and
    /// placed right after it when the list is shown.
    /// </summary>
    [ThreadStatic] private static List<(HoverTip original, IHoverTip translation)>? _pendingIntentTips;

    // Tips are shown right after they are built; more than this waiting means they were built for something else.
    private const int MaxPendingIntentTips = 16;

    [HarmonyPatch(typeof(AbstractIntent), nameof(AbstractIntent.GetHoverTip))]
    private static class IntentTipBuilt
    {
        [HarmonyPostfix]
        static void Postfix(AbstractIntent __instance, IEnumerable<Creature> targets, Creature owner, HoverTip __result)
        {
            if (!SpireLingoConfig.ShowOnIntents || Skip) return;

            var intent = __instance;
            try
            {
                // The damage and hit count come from the same calculation the intent label uses.
                var tip = TargetText.Format(() => intent.GetHoverTip(targets, owner));
                var pending = _pendingIntentTips ??= [];
                if (pending.Count >= MaxPendingIntentTips) pending.RemoveAt(0);
                pending.Add((__result,
                    MakeTip(tip.Title ?? "", tip.Description ?? "", $"INTENT-{intent.IntentType}")));
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"Could not translate intent {intent.GetType().Name}: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(NCreature), nameof(NCreature.ShowHoverTips))]
    private static class IntentTipsShown
    {
        [HarmonyPrefix]
        static void Prefix(ref IEnumerable<IHoverTip> hoverTips)
        {
            var pending = _pendingIntentTips;
            if (pending is not { Count: > 0 }) return;

            var tips = new List<IHoverTip>();
            foreach (var tip in hoverTips)
            {
                tips.Add(tip);
                if (tip is not HoverTip shown) continue;
                var index = pending.FindIndex(p => p.original.Id == shown.Id && p.original.Description == shown.Description);
                if (index < 0) continue;
                tips.Add(pending[index].translation);
                pending.RemoveAt(index);
            }
            // Tips built for anything that was not shown are dropped, so they cannot match a later list.
            pending.Clear();
            hoverTips = tips;
        }
    }
}
