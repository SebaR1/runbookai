using Microsoft.AspNetCore.Http.HttpResults;

namespace RunbookAI.SystemApi.Conversations;

public sealed record CreateConversationRequest(string? Namespace, string? Title);

public sealed record SendMessageRequest(string? Content);

public static class ConversationEndpoints
{
    public static RouteGroupBuilder MapConversations(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/conversations").WithTags("Conversations");

        group.MapPost("/", CreateAsync).WithName("createConversation")
            .WithSummary("Create a conversation. The namespace is fixed for all its questions.")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapGet("/", ListAsync).WithName("listConversations")
            .WithSummary("List conversations, most recent activity first.");
        group.MapGet("/{id:guid}", GetAsync).WithName("getConversation")
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/{id:guid}/messages", ListMessagesAsync).WithName("listMessages")
            .WithSummary("Messages of the conversation in chronological order.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/messages", SendAsync).WithName("sendMessage")
            .WithSummary("Ask a question. Answers with Server-Sent Events: start, sources, token (many), and done or error.")
            .WithDescription("The assistant message is saved when the stream ends: completed, error (the RAG failed) or cancelled (the client disconnected).")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<Results<Created<Conversation>, ProblemHttpResult>> CreateAsync(
        CreateConversationRequest? request, ConversationService service, CancellationToken ct)
    {
        var outcome = await service.CreateAsync(request?.Namespace, request?.Title, ct);
        return outcome.Conversation is { } conversation
            ? TypedResults.Created($"/conversations/{conversation.Id}", conversation)
            : Problem(outcome.Error!.Value, outcome.Detail);
    }

    private static async Task<Ok<IReadOnlyList<Conversation>>> ListAsync(
        ConversationService service, CancellationToken ct, string? @namespace = null) =>
        TypedResults.Ok(await service.ListAsync(@namespace, ct));

    private static async Task<Results<Ok<Conversation>, ProblemHttpResult>> GetAsync(
        Guid id, ConversationService service, CancellationToken ct) =>
        await service.GetAsync(id, ct) is { } conversation ? TypedResults.Ok(conversation) : NotFound();

    private static async Task<Results<Ok<IReadOnlyList<Message>>, ProblemHttpResult>> ListMessagesAsync(
        Guid id, ConversationService service, CancellationToken ct) =>
        await service.GetAsync(id, ct) is null ? NotFound() : TypedResults.Ok(await service.ListMessagesAsync(id, ct));

    private static async Task<Results<ServerSentEventsResult<object>, ProblemHttpResult>> SendAsync(
        Guid id, SendMessageRequest request, ConversationService service, ChatService chat,
        HttpContext http, CancellationToken ct)
    {
        // Everything that can fail with a status code is checked before the stream:
        // once the 200 and the first event are sent, errors can only go in-band.
        if (await service.GetAsync(id, ct) is not { } conversation) return NotFound();
        if (service.ValidateMessage(request.Content, out var detail) is { } error) return Problem(error, detail);
        // Tells nginx (the front's proxy, and ingress-nginx in AKS) not to buffer the
        // stream, so each token reaches the browser as it is generated.
        http.Response.Headers["X-Accel-Buffering"] = "no";
        return TypedResults.ServerSentEvents(chat.SendAsync(conversation, request.Content!, ct));
    }

    private static ProblemHttpResult NotFound() =>
        Problem(ConversationError.NotFound, "No existe una conversación con ese id.");

    // ProblemDetails (RFC 9457) with title/detail in Spanish, shown to the user.
    private static ProblemHttpResult Problem(ConversationError error, string? detail)
    {
        var (status, title) = error switch
        {
            ConversationError.NotFound => (StatusCodes.Status404NotFound, "Conversación no encontrada"),
            ConversationError.InvalidNamespace => (StatusCodes.Status400BadRequest, "Namespace inválido"),
            ConversationError.InvalidTitle => (StatusCodes.Status400BadRequest, "Título inválido"),
            ConversationError.EmptyMessage => (StatusCodes.Status400BadRequest, "Mensaje vacío"),
            ConversationError.MessageTooLong => (StatusCodes.Status400BadRequest, "Mensaje demasiado largo"),
            _ => (StatusCodes.Status500InternalServerError, "Error"),
        };
        return TypedResults.Problem(detail, statusCode: status, title: title);
    }
}
