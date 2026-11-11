using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UEManager.Core.Storage;

namespace UEManager.Core.Mods;

/// <summary>
/// Free machine translation of mod texts, with no account, through the public endpoint of Google's translate browser
/// extension. Every translation is kept in a file in the data folder, so a text goes out once. Into English only texts
/// with other letters are sent, into another language only texts with Latin letters: a Chinese text stays as it is.
/// An empty answer, which the service sometimes gives, is not kept: the text shows as it is and goes out again next time.
/// </summary>
public sealed partial class FreeTranslator(HttpClient http, string cacheFile)
{
    private const string Endpoint = "https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl=";

    /// <summary>About as much text as one request carries; a longer text goes on its own.</summary>
    private const int RequestChars = 4000;

    private readonly object sync = new();
    private Dictionary<string, string>? cache;

    /// <summary>The texts in <paramref name="language"/>, such as "zh-CN", in their order.</summary>
    /// <exception cref="ModException">The service could not be reached; what it translated so far is kept for next time.</exception>
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string language, CancellationToken cancellation = default)
    {
        List<string> missing;
        lock (sync)
        {
            var known = cache ??= Load();
            missing = texts.Where(t => NeedsTranslation(t, language) && !known.ContainsKey(Key(language, t))).Distinct(StringComparer.Ordinal).ToList();
        }
        try
        {
            foreach (var batch in Batches(missing))
            {
                var answers = await RequestAsync(batch, language, cancellation);
                lock (sync)
                {
                    for (var i = 0; i < batch.Count; i++)
                        if (!string.IsNullOrWhiteSpace(answers[i]))
                            cache![Key(language, batch[i])] = answers[i];
                }
            }
        }
        finally
        {
            if (missing.Count > 0)
                Save();
        }
        lock (sync)
            return texts.Select(t => cache!.TryGetValue(Key(language, t), out var done) ? GameName(done, language) : t).ToList();
    }

    /// <summary>
    /// A Chinese translation with the game's Chinese name, 影之刃零, where the service wrote a name of its own, such as
    /// 幻影之刃零 or 幻刃零式, or left it in English.
    /// </summary>
    internal static string GameName(string translated, string language) =>
        language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? WrongGameNames().Replace(translated, "影之刃零") : translated;

    /// <summary>The texts in requests of about <see cref="RequestChars"/> characters, in their order.</summary>
    internal static IEnumerable<List<string>> Batches(IReadOnlyList<string> texts)
    {
        var batch = new List<string>();
        var size = 0;
        foreach (var text in texts)
        {
            if (batch.Count > 0 && size + text.Length > RequestChars)
            {
                yield return batch;
                batch = [];
                size = 0;
            }
            batch.Add(text);
            size += text.Length;
        }
        if (batch.Count > 0)
            yield return batch;
    }

    /// <summary>
    /// The translations in an answer: ["text", "en"] for one text, [["text", "en"], …] for several. Null when the answer
    /// is not one of these, or has another number of texts.
    /// </summary>
    internal static IReadOnlyList<string>? ReadAnswer(string json, int count)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
                return count == 1 ? [root.GetString()!] : null;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return null;
            if (count == 1 && root[0].ValueKind == JsonValueKind.String)
                return [root[0].GetString()!];
            var texts = root.EnumerateArray().Select(entry => entry.ValueKind switch
            {
                JsonValueKind.String => entry.GetString(),
                JsonValueKind.Array when entry.GetArrayLength() > 0 && entry[0].ValueKind == JsonValueKind.String => entry[0].GetString(),
                _ => null,
            }).ToList();
            return texts.Count == count && texts.All(t => t is not null) ? texts.Select(t => t!).ToList() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool NeedsTranslation(string text, string language) =>
        language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? OtherLetters().IsMatch(text) : LatinLetters().IsMatch(text);

    private async Task<IReadOnlyList<string>> RequestAsync(IReadOnlyList<string> batch, string language, CancellationToken cancellation)
    {
        var form = string.Join("&", batch.Select(t => "q=" + Uri.EscapeDataString(t)));
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint + Uri.EscapeDataString(language))
        {
            Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellation);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            throw new ModException($"The translation service could not be reached: {ex.Message}");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new ModException($"The translation service answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            return ReadAnswer(await response.Content.ReadAsStringAsync(cancellation), batch.Count)
                ?? throw new ModException($"The translation service gave an answer the app cannot read.");
        }
    }

    private static string Key(string language, string text) => language + "\n" + text;

    private Dictionary<string, string> Load()
    {
        try
        {
            return JsonFile.Read<Dictionary<string, string>>(cacheFile) is { } saved
                ? new Dictionary<string, string>(saved, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Saved translations could not be read, so they start again: {ex.Message}");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        lock (sync)
        {
            try
            {
                JsonFile.Write(cacheFile, cache);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Translations could not be saved: {ex.Message}");
            }
        }
    }

    [GeneratedRegex("[A-Za-z]")]
    private static partial Regex LatinLetters();

    [GeneratedRegex(@"[^\P{L}A-Za-z]")]
    private static partial Regex OtherLetters();

    [GeneratedRegex(@"幻(?:影之刃|影|刃)\s?(?:零式|零|0)|Phantom\s*Blade\s*(?:Zero|0)", RegexOptions.IgnoreCase)]
    private static partial Regex WrongGameNames();
}
