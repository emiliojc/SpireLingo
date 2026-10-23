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
}
