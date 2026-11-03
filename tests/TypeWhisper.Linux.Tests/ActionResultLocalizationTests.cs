using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ActionResultLocalizationTests
{
    [Theory]
    [InlineData("en", "Action completed.", "Action failed.", "Open result", "Could not open result.")]
    [InlineData("de", "Aktion abgeschlossen.", "Aktion fehlgeschlagen.", "Ergebnis öffnen", "Ergebnis konnte nicht geöffnet werden.")]
    [InlineData("es", "Acción completada.", "La acción ha fallado.", "Abrir resultado", "No se pudo abrir el resultado.")]
    [InlineData("ru", "Действие выполнено.", "Не удалось выполнить действие.", "Открыть результат", "Не удалось открыть результат.")]
    public void Catalog_contains_real_action_result_translations(
        string language,
        string completed,
        string failed,
        string open,
        string openFailed
    )
    {
        var catalog = Load(language);

        Assert.Equal(completed, catalog["ActionResult.Completed"]);
        Assert.Equal(failed, catalog["ActionResult.Failed"]);
        Assert.Equal(open, catalog["ActionResult.Open"]);
        Assert.Equal(openFailed, catalog["ActionResult.OpenFailed"]);
    }

    private static Dictionary<string, string> Load(
        string language,
        [CallerFilePath] string thisFile = ""
    )
    {
        var testDirectory = Path.GetDirectoryName(thisFile)!;
        var path = Path.GetFullPath(
            Path.Join(
                testDirectory,
                "..",
                "..",
                "src",
                "TypeWhisper.Linux",
                "Resources",
                "Localization",
                $"{language}.json"
            )
        );
        return JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(path)
        )!;
    }
}
