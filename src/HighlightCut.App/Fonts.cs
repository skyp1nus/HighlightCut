using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace HighlightCut.App;

/// <summary>Inter and JetBrains Mono (SIL OFL 1.1), embedded from Assets/Fonts.</summary>
public sealed class HighlightCutFontCollection() : EmbeddedFontCollection(
    new Uri("fonts:HighlightCut", UriKind.Absolute),
    new Uri("avares://HighlightCut/Assets/Fonts", UriKind.Absolute));

public static class FontSetup
{
    public const string Sans = "fonts:HighlightCut#Inter";
    public const string Mono = "fonts:HighlightCut#JetBrains Mono";

    public static AppBuilder WithHighlightCutFonts(this AppBuilder builder) =>
        builder
            .ConfigureFonts(fm => fm.AddFontCollection(new HighlightCutFontCollection()))
            .With(new FontManagerOptions { DefaultFamilyName = Sans });
}
