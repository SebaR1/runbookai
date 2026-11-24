using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RunbookAI.SystemApi.Configuration;
using RunbookAI.SystemApi.Conversations;

namespace RunbookAI.SystemApi.Rag;

// Request of POST /ask. Own DTOs instead of the generated AskRequest: the generated
// HistoryMessageRole relies on [EnumMember], which System.Text.Json ignores, so it
// would send "User" and FastAPI would answer 422.
public sealed record RagAskRequest(string Question, string Namespace, IReadOnlyList<RagHistoryMessage> History);

public sealed record RagHistoryMessage(MessageRole Role, string Content);

// The SSE events of the RAG service, typed.
public abstract record RagEvent
{
    public sealed record Sources(IReadOnlyList<Source> Items) : RagEvent;
    public sealed record Token(string Text) : RagEvent;
    public sealed record Done(string? Model, int? InputTokens, int? OutputTokens) : RagEvent;
    // Also used for transport failures (RAG down, HTTP error, timeout, cut stream),
    // so the caller handles every failure the same way.
    public sealed record Error(string Message) : RagEvent;
}

// Streaming client for /ask. The NSwag client cannot do it: it buffers the whole
// body and deserializes it as AskResponse. Here the response is read as it
// arrives (ResponseHeadersRead) and parsed with SseParser (System.Net.ServerSentEvents).
public sealed class RagStreamClient(HttpClient http, IOptions<RagOptions> options, ILogger<RagStreamClient> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // Transport failures become an Error event; only cancellation by the caller
    // (the client closed the connection) is thrown, as OperationCanceledException.
    public async IAsyncEnumerable<RagEvent> AskAsync(RagAskRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // HttpClient.Timeout only covers until the headers arrive when reading with
        // ResponseHeadersRead, so the limit for the whole answer is this token.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.AskTimeout);

        HttpResponseMessage? response = null;
        string? failure = null;
        try
        {
            response = await http.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "ask") { Content = JsonContent.Create(request, options: Json) },
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                failure = $"RAG /ask returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(timeout.Token)}";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            failure = Describe(ex);
        }

        if (failure is not null)
        {
            response?.Dispose();
            log.LogWarning("RAG /ask failed before streaming: {Failure}", failure);
            yield return new RagEvent.Error(failure);
            yield break;
        }

        using (response)
        {
            await using var body = await response!.Content.ReadAsStreamAsync(timeout.Token);
            await using var items = SseParser.Create(body).EnumerateAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
            var finished = false;
            while (true)
            {
                // yield is not allowed inside a try with catch: move inside, yield outside.
                RagEvent? next;
                try
                {
                    if (!await items.MoveNextAsync()) break;
                    next = Parse(items.Current);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    failure = Describe(ex);
                    break;
                }
                if (next is null) continue;
                yield return next;
                if (next is RagEvent.Done or RagEvent.Error)
                {
                    finished = true;
                    break;
                }
            }

            if (!finished)
            {
                failure ??= "RAG stream ended without done or error";
                log.LogWarning("RAG /ask stream interrupted: {Failure}", failure);
                yield return new RagEvent.Error(failure);
            }
        }

        string Describe(Exception ex) => ex is OperationCanceledException
            ? $"RAG /ask timed out after {options.Value.AskTimeout}"
            : $"RAG /ask failed: {ex.GetType().Name}: {ex.Message}";
    }

    private static RagEvent? Parse(SseItem<string> item) => item.EventType switch
    {
        "sources" => new RagEvent.Sources(JsonSerializer.Deserialize<List<Source>>(item.Data, Json) ?? []),
        "token" => new RagEvent.Token(JsonSerializer.Deserialize<TokenData>(item.Data, Json)?.Text ?? ""),
        "done" => JsonSerializer.Deserialize<DoneData>(item.Data, Json) is { } d
            ? new RagEvent.Done(d.Model, d.InputTokens, d.OutputTokens)
            : new RagEvent.Done(null, null, null),
        "error" => new RagEvent.Error(JsonSerializer.Deserialize<ErrorData>(item.Data, Json)?.Message ?? "RAG error"),
        _ => null, // unknown events are ignored, so the RAG can add new ones
    };

    private sealed record TokenData(string Text);
    private sealed record DoneData(string? Model, int? InputTokens, int? OutputTokens);
    private sealed record ErrorData(string Message);
}
