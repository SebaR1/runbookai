using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using RunbookAI.SystemApi.Conversations;
using RunbookAI.SystemApi.Documents;

namespace RunbookAI.SystemApi.Tests;

[Trait("Category", "Integration")]
public class ConversationsIntegrationTests(SystemApiFixture fx) : IClassFixture<SystemApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireStorage() => Assert.SkipWhen(fx.StorageUnavailable is not null, fx.StorageUnavailable ?? "");

    private void RequireAll()
    {
        RequireStorage();
        Assert.SkipWhen(fx.RagUnavailable is not null, fx.RagUnavailable ?? "");
    }

    // ---- With the RAG service: the "Lista cuando" of Phase 5 ----

    [Fact]
    public async Task Answer_streams_is_saved_and_a_follow_up_is_understood_in_context()
    {
        RequireAll();
        var (conversation, documentId) = await ConversationWithRunbookAsync();

        // 1. Explicit question: the SSE contract and what gets saved.
        const string firstQuestion = "¿Cuándo se dispara la alerta de latencia alta de payments-api?";
        var first = await SendAsync(conversation.Id, firstQuestion);
        Assert.Equal("start", first[0].Type);
        Assert.Equal("sources", first[1].Type);
        Assert.Equal("done", first[^1].Type);
        Assert.Contains(first, e => e.Type == "token");

        var start = first[0].Data;
        var done = first[^1].Data;
        Assert.Equal(start.GetProperty("assistantMessageId").GetGuid(), done.GetProperty("messageId").GetGuid());

        var messages = await MessagesAsync(conversation.Id);
        Assert.Equal(2, messages.Count);
        var (question, answer) = (messages[0], messages[1]);
        Assert.Equal(MessageRole.User, question.Role);
        Assert.Equal(start.GetProperty("userMessageId").GetGuid(), question.Id);
        Assert.Null(question.Status);
        Assert.Equal(MessageRole.Assistant, answer.Role);
        Assert.Equal(MessageStatus.Completed, answer.Status);
        Assert.Equal(Tokens(first), answer.Content);
        Assert.Contains("800", answer.Content);
        Assert.NotNull(answer.Model);
        Assert.True(answer.OutputTokens > 0);
        Assert.All(answer.Sources!, s => Assert.Equal(documentId, s.DocumentId));
        Assert.Equal(Enumerable.Range(1, answer.Sources!.Count), answer.Sources!.Select(s => s.Index));

        var titled = await fx.Client.GetFromJsonAsync<Conversation>($"/conversations/{conversation.Id}", SystemApiFixture.Json, Ct);
        Assert.Equal(ConversationRules.TitleFrom(firstQuestion), titled!.Title); // 63 chars: cut to 60

        // 2. Follow-up that does not stand alone: the history goes to the RAG, which
        // rewrites it to search. Deterministic check on the retrieval, not on the
        // wording of the answer: the restart procedure must be among the sources.
        var followUp = await SendAsync(conversation.Id, "¿Y cómo lo reinicio?");
        Assert.Equal("done", followUp[^1].Type);
        var followUpSources = followUp.Single(e => e.Type == "sources").Data.Deserialize<List<MessageSource>>(SystemApiFixture.Json)!;
        Assert.Contains(followUpSources, s => s.Section.Contains("reinicio", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, (await MessagesAsync(conversation.Id)).Count);
    }

    [Fact]
    public async Task Client_disconnect_saves_the_partial_answer_as_cancelled()
    {
        RequireAll();
        var (conversation, _) = await ConversationWithRunbookAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using (var response = await PostMessageAsync(conversation.Id, "¿Cómo hago un reinicio controlado de payments-api? Detallá cada paso.", cts.Token))
        {
            await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
            await foreach (var item in SseParser.Create(body).EnumerateAsync(cts.Token))
            {
                if (item.EventType == "token") break; // the answer started: hang up
            }
            cts.Cancel();
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        Message? answer;
        while ((answer = (await MessagesAsync(conversation.Id)).FirstOrDefault(m => m.Role == MessageRole.Assistant)) is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "The assistant message was not saved after the disconnect");
            await Task.Delay(250, Ct);
        }
        Assert.Equal(MessageStatus.Cancelled, answer.Status);
        Assert.Null(answer.Model);
    }

    // ---- Without the RAG service ----

    [Fact]
    public async Task Create_get_and_list_conversations()
    {
        RequireStorage();
        var ns = SystemApiFixture.NewNamespace();

        var created = await fx.Client.PostAsJsonAsync("/conversations", new { @namespace = ns, title = "Guardia" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversation = (await created.Content.ReadFromJsonAsync<Conversation>(SystemApiFixture.Json, Ct))!;
        Assert.Equal($"/conversations/{conversation.Id}", created.Headers.Location?.OriginalString);
        Assert.Equal(ns, conversation.Namespace);
        Assert.Equal("Guardia", conversation.Title);
        Assert.Null(conversation.UserId);

        var untitled = await CreateAsync(ns);
        Assert.Null(untitled.Title);

        var listed = await fx.Client.GetFromJsonAsync<List<Conversation>>($"/conversations?namespace={ns}", SystemApiFixture.Json, Ct);
        Assert.Equal([untitled.Id, conversation.Id], listed!.Select(c => c.Id)); // most recent first
        Assert.Empty((await MessagesAsync(conversation.Id)));

        var defaultNamespace = await fx.Client.PostAsJsonAsync("/conversations", new { }, Ct);
        Assert.Equal("default", (await defaultNamespace.Content.ReadFromJsonAsync<Conversation>(SystemApiFixture.Json, Ct))!.Namespace);
    }

    [Fact]
    public async Task Invalid_requests_are_rejected_before_streaming()
    {
        RequireStorage();
        var conversation = await CreateAsync(SystemApiFixture.NewNamespace());

        var badNamespace = await fx.Client.PostAsJsonAsync("/conversations", new { @namespace = "no válido" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badNamespace.StatusCode);

        var unknown = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.NotFound, (await fx.Client.GetAsync($"/conversations/{unknown}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fx.Client.GetAsync($"/conversations/{unknown}/messages", Ct)).StatusCode);

        var toUnknown = await PostMessageAsync(unknown, "hola", Ct);
        Assert.Equal(HttpStatusCode.NotFound, toUnknown.StatusCode);
        Assert.Equal("application/problem+json", toUnknown.Content.Headers.ContentType?.MediaType);

        var empty = await PostMessageAsync(conversation.Id, "   ", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var tooLong = await PostMessageAsync(conversation.Id, new string('x', 4001), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        Assert.Empty(await MessagesAsync(conversation.Id)); // nothing saved for rejected messages
    }

    // ---- Helpers ----

    private sealed record Sse(string Type, JsonElement Data);

    private async Task<(Conversation Conversation, Guid DocumentId)> ConversationWithRunbookAsync()
    {
        var ns = SystemApiFixture.NewNamespace();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(SystemApiFixture.SampleRunbook, Ct));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        var form = new MultipartFormDataContent { { file, "file", "runbook-pagos.md" }, { new StringContent(ns), "namespace" } };
        var upload = await fx.Client.PostAsync("/documents", form, Ct);
        var document = (await upload.Content.ReadFromJsonAsync<Document>(SystemApiFixture.Json, Ct))!;
        Assert.Equal(DocumentStatus.Indexed, (await fx.WaitForSettledAsync(document.Id)).Status);
        return (await CreateAsync(ns), document.Id);
    }

    private async Task<Conversation> CreateAsync(string ns)
    {
        var response = await fx.Client.PostAsJsonAsync("/conversations", new { @namespace = ns }, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Conversation>(SystemApiFixture.Json, Ct))!;
    }

    // ResponseHeadersRead: the test reads the events as they arrive, like a browser.
    private Task<HttpResponseMessage> PostMessageAsync(Guid conversationId, string content, CancellationToken ct) =>
        fx.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/conversations/{conversationId}/messages")
            {
                Content = JsonContent.Create(new { content }),
            },
            HttpCompletionOption.ResponseHeadersRead, ct);

    private async Task<List<Sse>> SendAsync(Guid conversationId, string content)
    {
        using var response = await PostMessageAsync(conversationId, content, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = new List<Sse>();
        await using var body = await response.Content.ReadAsStreamAsync(Ct);
        await foreach (var item in SseParser.Create(body).EnumerateAsync(Ct))
            events.Add(new Sse(item.EventType, JsonDocument.Parse(item.Data).RootElement.Clone()));
        return events;
    }

    private static string Tokens(List<Sse> events) =>
        string.Concat(events.Where(e => e.Type == "token").Select(e => e.Data.GetProperty("text").GetString()));

    private async Task<List<Message>> MessagesAsync(Guid conversationId) =>
        (await fx.Client.GetFromJsonAsync<List<Message>>($"/conversations/{conversationId}/messages", SystemApiFixture.Json, Ct))!;
}
