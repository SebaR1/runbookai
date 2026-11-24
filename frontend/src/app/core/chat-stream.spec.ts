import { ChatEvent, ChatRequestError, readChatEvents, sendChatMessage } from './chat-stream';

function bodyOf(chunks: Uint8Array[]): ReadableStream<Uint8Array> {
  return new ReadableStream({
    start(controller) {
      chunks.forEach((c) => controller.enqueue(c));
      controller.close();
    },
  });
}

async function collect(events: AsyncIterable<ChatEvent>): Promise<ChatEvent[]> {
  const result: ChatEvent[] = [];
  for await (const e of events) result.push(e);
  return result;
}

const sse =
  'event: start\ndata: {"conversationId":"c1","userMessageId":"u1","assistantMessageId":"a1"}\n\n' +
  'event: sources\ndata: [{"index":1,"documentId":"d1","chunkId":"k1","title":"t","page":1,"section":"s","distance":0.3}]\n\n' +
  'event: token\ndata: {"text":"¿Cómo"}\n\n' +
  'event: token\ndata: {"text":" está? [1]"}\n\n' +
  'event: done\ndata: {"messageId":"a1","model":"qwen3:8b","inputTokens":10,"outputTokens":4}\n\n';

describe('readChatEvents', () => {
  it('decodes accents split between network chunks at the byte level', async () => {
    const bytes = new TextEncoder().encode(sse);
    const whole = await collect(readChatEvents(bodyOf([bytes])));
    // One byte per chunk: "ó" (2 bytes) always arrives split.
    const split = await collect(readChatEvents(bodyOf([...bytes].map((b) => Uint8Array.of(b)))));

    expect(split).toEqual(whole);
    expect(whole.map((e) => e.type)).toEqual(['start', 'sources', 'token', 'token', 'done']);
    expect(whole[2]).toEqual({ type: 'token', data: { text: '¿Cómo' } });
  });

  it('skips unknown event types', async () => {
    const body = bodyOf([new TextEncoder().encode('event: ping\ndata: {}\n\n' + sse)]);
    expect((await collect(readChatEvents(body)))[0].type).toBe('start');
  });
});

describe('sendChatMessage', () => {
  it('posts the content as JSON and streams the events', async () => {
    const fetchFn = vi.fn(async () => new Response(sse, { headers: { 'Content-Type': 'text/event-stream' } }));
    const events = await collect(sendChatMessage('/api/conversations/c1/messages', 'hola', new AbortController().signal, fetchFn));

    expect(events).toHaveLength(5);
    const [url, init] = fetchFn.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/conversations/c1/messages');
    expect(init.method).toBe('POST');
    expect(init.body).toBe('{"content":"hola"}');
  });

  it('throws the ProblemDetails detail when the request is rejected', async () => {
    const problem = { title: 'Mensaje vacío', status: 400, detail: 'El mensaje no puede estar vacío.' };
    const fetchFn = vi.fn(async () => Response.json(problem, { status: 400 }));

    const error = await collect(sendChatMessage('/x', '', new AbortController().signal, fetchFn)).catch((e) => e);
    expect(error).toBeInstanceOf(ChatRequestError);
    expect(error.status).toBe(400);
    expect(error.message).toBe('El mensaje no puede estar vacío.');
  });

  it('gives a readable message when the proxy answers with HTML', async () => {
    const fetchFn = vi.fn(async () => new Response('<html>502 Bad Gateway</html>', { status: 502 }));
    const error = await collect(sendChatMessage('/x', 'hola', new AbortController().signal, fetchFn)).catch((e) => e);
    expect(error.message).toBe('El servidor no está disponible. Probá de nuevo en un momento.');
  });
});
