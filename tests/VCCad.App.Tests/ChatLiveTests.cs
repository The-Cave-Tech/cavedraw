using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The end-to-end checks that need a live model, written so they can be run later
/// against an instance someone else started. <b>These tests never launch the desktop
/// app.</b> Point them at a running instance:
///
/// <code>
///   set VCCAD_LIVE_PORT=62652
///   dotnet test tests\VCCad.App.Tests --filter FullyQualifiedName~ChatLiveTests
/// </code>
///
/// To exercise the restart case, run the first two tests, restart the app against the
/// same diary directory, set <c>VCCAD_LIVE_RESTARTED=1</c> and run again: the third
/// test asks for the codeword and only an app that restored the conversation from the
/// diary can answer it.
///
/// With no <c>VCCAD_LIVE_PORT</c> the tests are no-ops, which is the same "skip
/// cleanly when the optional source is absent" convention the corpus tests use.
/// </summary>
public class ChatLiveTests
{
    private static string? Port => Environment.GetEnvironmentVariable("VCCAD_LIVE_PORT");

    private static bool Restarted =>
        Environment.GetEnvironmentVariable("VCCAD_LIVE_RESTARTED") is { Length: > 0 } and not "0";

    private static async Task<string> ChatAsync(string prompt)
    {
        string port = Port!;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        string body = JsonSerializer.Serialize(new { prompt, withScreenshot = false });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await http.PostAsync(
            $"http://127.0.0.1:{port}/api/v1/chat", content);

        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("result").GetProperty("reply").GetString() ?? string.Empty;
    }

    [Fact]
    public async Task TheAssistantDescribesTheWholeApplicationWithoutBeingToldItsScope()
    {
        if (string.IsNullOrWhiteSpace(Port))
        {
            return; // no running instance supplied: skip cleanly
        }

        // "pagination"/"text editing" are NOT in the question. A model that answers in
        // these terms is answering from the prompt.
        string reply = await ChatAsync("what can you do here? Answer in one short paragraph.");

        bool pagination = reply.Contains("pag", StringComparison.OrdinalIgnoreCase)
                          || reply.Contains("impos", StringComparison.OrdinalIgnoreCase)
                          || reply.Contains("page", StringComparison.OrdinalIgnoreCase);
        bool text = reply.Contains("text", StringComparison.OrdinalIgnoreCase)
                    || reply.Contains("typograph", StringComparison.OrdinalIgnoreCase);

        Assert.True(pagination, $"the reply did not mention pages/pagination: {reply}");
        Assert.True(text, $"the reply did not mention text: {reply}");
    }

    [Fact]
    public async Task TheCodewordSurvivesALongConversation()
    {
        if (string.IsNullOrWhiteSpace(Port))
        {
            return; // no running instance supplied: skip cleanly
        }

        await ChatAsync("Hello. Reply with just: ready");
        await ChatAsync("Remember this codeword exactly: KIWI-91");

        string shortTerm = await ChatAsync("What was the codeword?");
        Assert.Contains("KIWI-91", shortTerm, StringComparison.OrdinalIgnoreCase);

        // Long enough to force the conversation over any sane budget, so the middle —
        // where the codeword lives — has to be compressed to survive.
        string filler = string.Concat(Enumerable.Repeat(
            "This is stored reference material, not a question. " +
            string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 25)), 40));
        await ChatAsync("Please store this reference material and reply with just DONE:\n" + filler);

        string longTerm = await ChatAsync("What was the codeword?");

        // The old trimmer deleted this region and the answer was "no codeword was ever
        // given". A summary that retains what happened answers with it.
        Assert.Contains("KIWI-91", longTerm, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheConversationContinuesAfterARestart()
    {
        if (string.IsNullOrWhiteSpace(Port) || !Restarted)
        {
            return; // run this one after restarting the app: skip cleanly otherwise
        }

        string reply = await ChatAsync("Before the restart I asked you to remember a codeword. What was it?");

        Assert.Contains("KIWI-91", reply, StringComparison.OrdinalIgnoreCase);
    }
}
