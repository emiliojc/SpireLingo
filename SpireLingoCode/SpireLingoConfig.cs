using BaseLib.Config;

namespace SpireLingo.SpireLingoCode;

/// <summary>
/// Language codes as the game names its localization folders.
/// </summary>
public enum TargetLanguage
{
    eng,
    deu,
    fra,
    spa,
    esp,
    ita,
    pol,
    ptb,
    rus,
    tur,
    jpn,
    kor,
    zhs,
    tha
}

/// <summary>
/// Keys that can toggle discovery mode. F2 is left out because the game opens its feedback screen with it.
/// </summary>
public enum DiscoveryKey
{
    F8,
    F12,
    F1,
    F3,
    F4,
    F5,
    F6,
    F7,
    F9,
    F10
}

[ConfigHoverTipsByDefault]
internal class SpireLingoConfig : SimpleModConfig
{
    [ConfigSection("TranslationSection")]

    // Read when the game loads its text, so a change applies after a restart.
    public static TargetLanguage TargetLanguage { get; set; } = TargetLanguage.eng;

    [ConfigSection("ShowOnSection")]
    public static bool ShowOnCards { get; set; } = true;
    public static bool ShowOnRelics { get; set; } = true;
    public static bool ShowOnPotions { get; set; } = true;
    public static bool ShowOnPowers { get; set; } = true;
    public static bool ShowOnIntents { get; set; } = true;

    [ConfigSection("DiscoverySection")]
    public static DiscoveryKey DiscoveryKey { get; set; } = DiscoveryKey.F8;

    [ConfigSlider(0, 0.5, 0.05)]
    public static double DiscoveryDim { get; set; } = 0.2;
}
