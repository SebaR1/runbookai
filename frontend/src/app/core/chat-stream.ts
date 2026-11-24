import type { MessageSource, ProblemDetails } from '../api/system-api.client';
import { SseParser } from './sse-parser';

// The SSE contract of POST /conversations/{id}/messages (system-api/README.md,
// "Chat streaming"). The generated NSwag client cannot read a stream, so these
// event types are written by hand; the DTOs inside them do come from the client.
export type ChatEvent =
  | { type: 'start'; data: { conversationId: string; userMessageId: string; assistantMessageId: string } }
  | { type: 'sources'; data: MessageSource[] }
  | { type: 'token'; data: { text: string } }
  | { type: 'done'; data: { messageId: string; model: string; inputTokens: number; outputTokens: number } }
  | { type: 'error'; data: { messageId: string; message: string } };

const CHAT_EVENT_TYPES = new Set<string>(['start', 'sources', 'token', 'done', 'error']);

/** The request failed before the stream started (400, 404, proxy error...). Nothing was saved. */
export class ChatRequestError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly problem?: ProblemDetails,
  ) {
    super(message);
    this.name = 'ChatRequestError';
  }
}

/** Reads the events of an SSE response body. Unknown event types are skipped. */
export async function* readChatEvents(body: ReadableStream<Uint8Array>): AsyncGenerator<ChatEvent> {
  const reader = body.getReader();
  // stream: true keeps a multi-byte character (á, ñ, emoji) split between two
  // network chunks until its last byte arrives.
  const decoder = new TextDecoder('utf-8');
  const parser = new SseParser();
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      for (const event of parser.feed(decoder.decode(value, { stream: true }))) {
        if (CHAT_EVENT_TYPES.has(event.type)) {
          yield { type: event.type, data: JSON.parse(event.data) } as ChatEvent;
        }
      }
    }
    parser.feed(decoder.decode());
    parser.end();
  } finally {
    reader.releaseLock();
  }
}

/**
 * Sends a question and yields the answer's events as they arrive.
 *
 * Aborting `signal` (the "Detener" button) cancels the request: the generator throws
 * an AbortError, and the System API sees the disconnect and saves the answer as
 * `cancelled`.
 */
export async function* sendChatMessage(
  url: string,
  content: string,
  signal: AbortSignal,
  fetchFn: typeof fetch = fetch,
): AsyncGenerator<ChatEvent> {
  const response = await fetchFn(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
    body: JSON.stringify({ content }),
    signal,
  });

  if (!response.ok || !response.body) {
    throw await toRequestError(response);
  }
  yield* readChatEvents(response.body);
}

async function toRequestError(response: Response): Promise<ChatRequestError> {
  let problem: ProblemDetails | undefined;
  try {
    problem = (await response.json()) as ProblemDetails;
  } catch {
    // Not JSON: nginx error page when the System API is down, for example.
  }
  const message =
    problem?.detail ??
    problem?.title ??
    (response.status >= 500
      ? 'El servidor no está disponible. Probá de nuevo en un momento.'
      : `No se pudo enviar el mensaje (HTTP ${response.status}).`);
  return new ChatRequestError(response.status, message, problem);
}

export function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}
