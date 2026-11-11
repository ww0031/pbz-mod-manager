using System.Net;
using System.Text;
using UEManager.Core.Mods;

namespace UEManager.Core.Tests;

/// <summary>The free translation of mod texts: the service's answers, the size of each request, and the saved translations.</summary>
public class FreeTranslatorTests
{
    [Fact]
    public void An_answer_has_one_text_or_several()
    {
        Assert.Equal(["地图收藏品标记"], FreeTranslator.ReadAnswer("""["地图收藏品标记","en"]""", 1));
        Assert.Equal(["一", "二"], FreeTranslator.ReadAnswer("""[["一","en"],["二","en"]]""", 2));
        Assert.Null(FreeTranslator.ReadAnswer("""[["一","en"]]""", 2));
        Assert.Null(FreeTranslator.ReadAnswer("<html>", 1));
    }

    [Fact]
    public void Requests_stay_short_and_keep_the_order()
    {
        string[] texts = [new('a', 3000), new('b', 900), new('c', 200), new('d', 5000)];

        var batches = FreeTranslator.Batches(texts).ToList();

        Assert.Equal([[texts[0], texts[1]], [texts[2]], [texts[3]]], batches);
    }

    [Theory]
    [InlineData("Map Collectible Markers", "zh-CN", true)]
    [InlineData("明末无常女音", "zh-CN", false)]
    [InlineData("女声", "en", true)]
    [InlineData("female voice", "en", false)]
    public void Only_a_text_in_another_language_is_sent(string text, string language, bool sent) =>
        Assert.Equal(sent, FreeTranslator.NeedsTranslation(text, language));

    [Theory]
    [InlineData("UE4SS 幻影之刃零", "zh-CN", "UE4SS 影之刃零")]
    [InlineData("幻影零式 - 更好的 HUD", "zh-CN", "影之刃零 - 更好的 HUD")]
    [InlineData("在《幻刃零》中可以闪避。", "zh-TW", "在《影之刃零》中可以闪避。")]
    [InlineData("适用于《Phantom Blade Zero》的 UE4SS", "zh-CN", "适用于《影之刃零》的 UE4SS")]
    [InlineData("UE4SS cho Phantom Blade Zero", "vi", "UE4SS cho Phantom Blade Zero")]
    public void The_game_keeps_its_Chinese_name(string translated, string language, string shown) =>
        Assert.Equal(shown, FreeTranslator.GameName(translated, language));

    [Fact]
    public async Task Each_text_is_sent_once_and_Chinese_stays_as_it_is()
    {
        using var folder = new TestFolder();
        var file = folder.Combine("translations.json");
        var service = new Service(HttpStatusCode.OK, """[["你好","en"],["世界","en"]]""");

        var first = await new FreeTranslator(new HttpClient(service), file).TranslateAsync(["Hello", "已经是中文", "World"], "zh-CN");
        var again = await new FreeTranslator(new HttpClient(service), file).TranslateAsync(["World", "Hello"], "zh-CN");

        Assert.Equal(["你好", "已经是中文", "世界"], first);
        Assert.Equal(["世界", "你好"], again);
        Assert.Equal(1, service.Requests);
        Assert.Equal("q=Hello&q=World", service.LastBody);
    }

    [Fact]
    public async Task An_empty_answer_shows_the_original_and_is_asked_again()
    {
        using var folder = new TestFolder();
        var file = folder.Combine("translations.json");
        var service = new Service(HttpStatusCode.OK, """[["","en"],["世界","en"]]""");

        var first = await new FreeTranslator(new HttpClient(service), file).TranslateAsync(["Hello", "World"], "zh-TW");
        service.Answer = """["你好","en"]""";
        var again = await new FreeTranslator(new HttpClient(service), file).TranslateAsync(["Hello", "World"], "zh-TW");

        Assert.Equal(["Hello", "世界"], first);
        Assert.Equal(["你好", "世界"], again);
        Assert.Equal(2, service.Requests);
        Assert.Equal("q=Hello", service.LastBody);
    }

    [Fact]
    public async Task A_refusal_is_an_error_and_nothing_is_kept()
    {
        using var folder = new TestFolder();
        var translator = new FreeTranslator(new HttpClient(new Service(HttpStatusCode.TooManyRequests, "")), folder.Combine("translations.json"));

        await Assert.ThrowsAsync<ModException>(() => translator.TranslateAsync(["Hello"], "zh-CN"));
    }

    private sealed class Service(HttpStatusCode status, string answer) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public string Answer { get; set; } = answer;

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(Answer, Encoding.UTF8, "application/json") };
        }
    }
}
