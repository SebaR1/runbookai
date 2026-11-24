import { SseEvent, SseParser } from './sse-parser';

function parseAll(chunks: string[]): SseEvent[] {
  const parser = new SseParser();
  const events = chunks.flatMap((c) => parser.feed(c));
  parser.end();
  return events;
}

const stream =
  'event: start\ndata: {"conversationId":"c1"}\n\n' +
  'event: token\ndata: {"text":"Hola"}\n\n' +
  'event: done\ndata: {"messageId":"m1"}\n\n';

describe('SseParser', () => {
  it('parses events with type and data', () => {
    expect(parseAll([stream])).toEqual([
      { type: 'start', data: '{"conversationId":"c1"}' },
      { type: 'token', data: '{"text":"Hola"}' },
      { type: 'done', data: '{"messageId":"m1"}' },
    ]);
  });

  it('gives the same events however the stream is split', () => {
    const expected = parseAll([stream]);
    // Every split point, and one character per chunk.
    for (let i = 1; i < stream.length; i++) {
      expect(parseAll([stream.slice(0, i), stream.slice(i)])).toEqual(expected);
    }
    expect(parseAll([...stream])).toEqual(expected);
  });

  it('accepts CRLF and CR line endings, also split between chunks', () => {
    const crlf = stream.replaceAll('\n', '\r\n');
    const expected = parseAll([stream]);
    expect(parseAll([crlf])).toEqual(expected);
    expect(parseAll([...crlf])).toEqual(expected);
    expect(parseAll([stream.replaceAll('\n', '\r')])).toEqual(expected);
  });

  it('joins multi-line data with newlines', () => {
    expect(parseAll(['data: line 1\ndata: line 2\ndata:\n\n'])).toEqual([
      { type: 'message', data: 'line 1\nline 2\n' },
    ]);
  });

  it('removes only one space after the colon', () => {
    expect(parseAll(['data:sin espacio\n\n', 'data:  dos\n\n'])).toEqual([
      { type: 'message', data: 'sin espacio' },
      { type: 'message', data: ' dos' },
    ]);
  });

  it('ignores comments, unknown fields and events without data', () => {
    expect(parseAll([': keep-alive\n\nretry: 1000\nfoo: bar\n\nevent: ping\n\ndata: x\n\n'])).toEqual([
      { type: 'message', data: 'x' },
    ]);
  });

  it('keeps the id field', () => {
    expect(parseAll(['id: 7\ndata: x\n\n'])).toEqual([{ type: 'message', data: 'x', id: '7' }]);
  });

  it('discards a last event without its closing empty line (cut connection)', () => {
    expect(parseAll(['event: token\ndata: {"text":"a"}\n\nevent: token\ndata: {"te'])).toEqual([
      { type: 'token', data: '{"text":"a"}' },
    ]);
    expect(parseAll(['event: done\ndata: {}\n'])).toEqual([]);
  });

  it('keeps accents and symbols intact', () => {
    expect(parseAll(['data: {"text":"¿Cómo está? ñ € 🚀"}\n\n'])).toEqual([
      { type: 'message', data: '{"text":"¿Cómo está? ñ € 🚀"}' },
    ]);
  });
});
