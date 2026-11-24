import type { MessageSource } from '../api/system-api.client';
import { applyChatEvent, ChatMessage, CONNECTION_LOST, finishChatStream, streamingAnswer } from './chat-reducer';
import type { ChatEvent } from './chat-stream';

const source: MessageSource = {
  index: 1, documentId: 'd1', chunkId: 'k1', title: 'runbook', page: 1, section: 's', distance: 0.3,
};

const start: ChatEvent = {
  type: 'start',
  data: { conversationId: 'c1', userMessageId: 'u1', assistantMessageId: 'a1' },
};
const token = (text: string): ChatEvent => ({ type: 'token', data: { text } });

const run = (events: ChatEvent[]): ChatMessage => events.reduce(applyChatEvent, streamingAnswer('local'));

describe('applyChatEvent', () => {
  it('builds a completed answer from start, sources, tokens and done', () => {
    const answer = run([
      start,
      { type: 'sources', data: [source] },
      token('Avisar en '),
      token('#data-oncall [1].'),
      { type: 'done', data: { messageId: 'a1', model: 'qwen3:8b', inputTokens: 10, outputTokens: 5 } },
    ]);
    expect(answer).toEqual({
      id: 'a1', role: 'assistant', content: 'Avisar en #data-oncall [1].',
      status: 'completed', sources: [source], model: 'qwen3:8b',
    });
  });

  it('keeps the partial text on error', () => {
    const answer = run([start, token('Parcial'), { type: 'error', data: { messageId: 'a1', message: 'El modelo no respondió.' } }]);
    expect(answer.status).toBe('error');
    expect(answer.content).toBe('Parcial');
    expect(answer.errorDetail).toBe('El modelo no respondió.');
  });

  it('ignores events after the answer ended', () => {
    const answer = run([start, { type: 'error', data: { messageId: 'a1', message: 'x' } }, token('tarde')]);
    expect(answer.content).toBe('');
  });

  it('does not mutate the previous state', () => {
    const before = streamingAnswer('local');
    applyChatEvent(before, token('a'));
    expect(before.content).toBe('');
  });
});

describe('finishChatStream', () => {
  const partial = run([start, token('Parcial')]);

  it('marks an aborted answer as cancelled, keeping its text', () => {
    expect(finishChatStream(partial, 'aborted')).toMatchObject({ status: 'cancelled', content: 'Parcial' });
  });

  it('marks a stream that ended without done as an error', () => {
    expect(finishChatStream(partial, 'ended')).toMatchObject({ status: 'error', errorDetail: CONNECTION_LOST });
    expect(finishChatStream(partial, 'failed', 'Failed to fetch')).toMatchObject({ errorDetail: 'Failed to fetch' });
  });

  it('leaves a finished answer as it is', () => {
    const done = applyChatEvent(partial, { type: 'done', data: { messageId: 'a1', model: 'm', inputTokens: 1, outputTokens: 1 } });
    expect(finishChatStream(done, 'aborted')).toBe(done);
  });
});
