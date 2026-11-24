using System.Text.RegularExpressions;
using RunbookAI.SystemApi.Rag;

namespace RunbookAI.SystemApi.Conversations;

// Pure functions of the conversations domain: no I/O, unit tested.
public static partial class ConversationRules
{
    public const int TitleMaxLength = 60;

    // Title from the first question: whitespace collapsed, cut on a word boundary.
    public static string TitleFrom(string question)
    {
        var text = Whitespace().Replace(question, " ").Trim();
        if (text.Length <= TitleMaxLength) return text;
        var cut = text.LastIndexOf(' ', TitleMaxLength - 1);
        return text[..(cut > TitleMaxLength / 2 ? cut : TitleMaxLength - 1)].TrimEnd() + "…";
    }

    // History for the RAG service from messages in chronological order: only
    // complete turns (a user message answered by a completed assistant message), and
    // only the last maxMessages of them. A failed or cancelled turn is left out
    // entirely: its question without an answer would leave two user messages in a
    // row, and a partial answer is not something the model should build on.
    public static IReadOnlyList<RagHistoryMessage> BuildHistory(IReadOnlyList<Message> chronological, int maxMessages)
    {
        if (maxMessages <= 0) return [];
        var turns = new List<RagHistoryMessage>();
        for (var i = 0; i < chronological.Count - 1; i++)
        {
            var (question, answer) = (chronological[i], chronological[i + 1]);
            if (question.Role == MessageRole.User && answer is { Role: MessageRole.Assistant, Status: MessageStatus.Completed })
            {
                turns.Add(new RagHistoryMessage(MessageRole.User, question.Content));
                turns.Add(new RagHistoryMessage(MessageRole.Assistant, answer.Content));
                i++;
            }
        }
        // Whole turns only: an odd limit must not start the history with an answer.
        var keep = maxMessages - maxMessages % 2;
        return turns.Count <= keep ? turns : turns[^keep..];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
