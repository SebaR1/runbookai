import type { Message, MessageSource, MessageStatus } from '../api/system-api.client';
import type { ChatEvent } from './chat-stream';

/** `streaming` only exists in the browser; the System API saves one of the other three. */
export type ChatMessageStatus = MessageStatus | 'streaming';

/** A message as the chat shows it: saved (from GET /messages) or being streamed. */
export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  status?: ChatMessageStatus;
  errorDetail?: string;
  sources: MessageSource[];
  model?: string;
}

export const CONNECTION_LOST = 'Se cortó la conexión antes de terminar la respuesta.';

export function fromApi(message: Message): ChatMessage {
  return {
    id: message.id,
    role: message.role,
    content: message.content,
    status: message.status,
    errorDetail: message.errorDetail ?? undefined,
    sources: message.sources ?? [],
    model: message.model ?? undefined,
  };
}

export function streamingAnswer(localId: string): ChatMessage {
  return { id: localId, role: 'assistant', content: '', status: 'streaming', sources: [] };
}

/** Applies one SSE event to the answer being streamed. Pure: returns a new object. */
export function applyChatEvent(answer: ChatMessage, event: ChatEvent): ChatMessage {
  if (answer.status !== 'streaming') return answer; // late events after the end are ignored
  switch (event.type) {
    case 'start':
      return { ...answer, id: event.data.assistantMessageId };
    case 'sources':
      return { ...answer, sources: event.data };
    case 'token':
      return { ...answer, content: answer.content + event.data.text };
    case 'done':
      return { ...answer, status: 'completed', model: event.data.model };
    case 'error':
      return { ...answer, status: 'error', errorDetail: event.data.message };
  }
}

/**
 * Closes an answer whose stream ended without `done` or `error`:
 * - `aborted`: the user pressed "Detener" (the System API saves it as cancelled).
 * - `ended`: the stream closed early (connection lost, proxy timeout).
 * - `failed`: fetch threw while reading (network error).
 * An answer that already ended keeps its status.
 */
export function finishChatStream(
  answer: ChatMessage,
  reason: 'aborted' | 'ended' | 'failed',
  detail?: string,
): ChatMessage {
  if (answer.status !== 'streaming') return answer;
  if (reason === 'aborted') return { ...answer, status: 'cancelled' };
  return { ...answer, status: 'error', errorDetail: detail ?? CONNECTION_LOST };
}
