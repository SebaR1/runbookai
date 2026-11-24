using System.Diagnostics;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using RunbookAI.SystemApi.Rag;

namespace RunbookAI.SystemApi.Conversations;

// SSE events the System API sends to its clients. Our own contract, not the RAG's:
// they carry the Mongo ids, and the RAG can change without breaking the front.
public sealed record ChatStartEvent(Guid ConversationId, Guid UserMessageId, Guid AssistantMessageId);
public sealed record ChatTokenEvent(string Text);
public sealed record ChatDoneEvent(Guid MessageId, string? Model, int? InputTokens, int? OutputTokens);
public sealed record ChatErrorEvent(Guid MessageId, string Message);

// One turn of a conversation: save the question, ask the RAG service with the
// history, forward the stream, and save the answer when it ends, however it ends.
public sealed class ChatService(ConversationService conversations, RagStreamClient rag, ILogger<ChatService> log)
{
    public const string UserErrorMessage = "No se pudo generar la respuesta. Probá de nuevo en unos segundos.";

    public async IAsyncEnumerable<SseItem<object>> SendAsync(
        Conversation conversation, string content, [EnumeratorCancellation] CancellationToken ct)
    {
        // History first, so it does not include the question being asked (that one
        // goes as `question`).
        var history = await conversations.LoadHistoryAsync(conversation.Id, ct);
        var question = await conversations.AddUserMessageAsync(conversation, content, ct);
        var answerId = Guid.CreateVersion7(); // known now, inserted once at the end
        yield return Event("start", new ChatStartEvent(conversation.Id, question.Id, answerId));

        var started = Stopwatch.GetTimestamp();
        var text = new StringBuilder();
        IReadOnlyList<MessageSource>? sources = null;
        RagEvent.Done? done = null;
        string? error = null;
        try
        {
            await foreach (var ev in rag.AskAsync(new RagAskRequest(question.Content, conversation.Namespace, history), ct))
            {
                switch (ev)
                {
                    case RagEvent.Sources s:
                        sources = s.Items.Select(MessageSource.From).ToList();
                        yield return Event("sources", sources);
                        break;
                    case RagEvent.Token t:
                        text.Append(t.Text);
                        yield return Event("token", new ChatTokenEvent(t.Text));
                        break;
                    case RagEvent.Done d:
                        done = d;
                        break;
                    case RagEvent.Error e:
                        error = e.Message;
                        break;
                }
            }
        }
        finally
        {
            // Runs when the stream ends, when the RAG fails and also when the client
            // disconnects: ASP.NET disposes this enumerator, which runs the finally.
            // CancellationToken.None because the request token may already be cancelled.
            var status = done is not null ? MessageStatus.Completed
                : error is not null ? MessageStatus.Error
                : ct.IsCancellationRequested ? MessageStatus.Cancelled
                : MessageStatus.Error;
            if (status == MessageStatus.Error) error ??= "Turn interrupted by an unexpected exception";

            await conversations.AddAssistantMessageAsync(
                answerId, conversation.Id, text.ToString(), status, error, sources,
                done?.Model, done?.InputTokens, done?.OutputTokens, CancellationToken.None);
            log.LogInformation(
                "Chat turn {ConversationId}/{MessageId} ended {Status} in {ElapsedMs} ms ({Chars} chars, {Sources} sources, history {History}) {Error}",
                conversation.Id, answerId, status, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                text.Length, sources?.Count ?? 0, history.Count, error);
        }

        // Only reached when the RAG stream ended (done or error), after the answer is saved.
        yield return done is not null
            ? Event("done", new ChatDoneEvent(answerId, done.Model, done.InputTokens, done.OutputTokens))
            : Event("error", new ChatErrorEvent(answerId, UserErrorMessage));
    }

    private static SseItem<object> Event(string type, object data) => new(data, type);
}
