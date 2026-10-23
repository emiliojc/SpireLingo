using System.Reflection;
using BaseLib.Config;
using Godot;
using Godot.Bridge;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using SpireLingo.SpireLingoCode.Discovery;

namespace SpireLingo.SpireLingoCode;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "SpireLingo";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        var assembly = Assembly.GetExecutingAssembly();
        // Lets Godot create this mod's own node types, like the discovery mode layer.
        ScriptManagerBridge.LookupScriptsInAssembly(assembly);

        ModConfigRegistry.Register(ModId, new SpireLingoConfig());

        Harmony harmony = new(ModId);
        harmony.PatchAll(assembly);

        DiscoveryMode.Install();

        Logger.Info($"Loaded; target language is {SpireLingoConfig.TargetLanguage}");
    }
}
