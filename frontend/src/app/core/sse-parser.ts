/** One Server-Sent Event, already assembled from its `event:` and `data:` lines. */
export interface SseEvent {
  /** `event:` field; "message" when the server does not send one (as in the spec). */
  type: string;
  /** All `data:` lines of the event, joined with "\n". */
  data: string;
  id?: string;
}

/**
 * Incremental parser for the text/event-stream format (WHATWG HTML, "Server-sent events").
 *
 * `EventSource` only does GET, and the chat endpoint is a POST, so the stream is read
 * with fetch and parsed here. The network delivers arbitrary chunks: a line (or a
 * "\r\n" pair) can be split between two calls to `feed`, so the incomplete tail is
 * kept until the next chunk.
 *
 * Input is text: UTF-8 decoding (with characters split between chunks) is done
 * before, by a streaming TextDecoder.
 */
export class SseParser {
  private buffer = '';
  private skipLeadingLf = false; // the previous chunk ended in "\r", maybe half of "\r\n"
  private dataLines: string[] = [];
  private eventType = '';
  private eventId: string | undefined;

  /** Adds a chunk and returns the events it completed (zero or more). */
  feed(chunk: string): SseEvent[] {
    if (this.skipLeadingLf && chunk.startsWith('\n')) chunk = chunk.slice(1);
    this.skipLeadingLf = false;
    this.buffer += chunk;

    const events: SseEvent[] = [];
    // A line ends in "\r\n", "\n" or "\r".
    const lineEnd = /\r\n|\n|\r/g;
    let start = 0;
    let match: RegExpExecArray | null;
    while ((match = lineEnd.exec(this.buffer)) !== null) {
      // A "\r" at the very end may be the first half of "\r\n": it still ends the
      // line, but the "\n" that may come next must not count as an empty line.
      if (match[0] === '\r' && match.index === this.buffer.length - 1) this.skipLeadingLf = true;
      const event = this.processLine(this.buffer.slice(start, match.index));
      if (event) events.push(event);
      start = lineEnd.lastIndex;
    }
    this.buffer = this.buffer.slice(start);
    return events;
  }

  /**
   * End of the stream. As in the spec, an event that did not get its closing empty
   * line is discarded: it may be cut in half (the connection dropped mid-event).
   */
  end(): void {
    this.buffer = '';
    this.dataLines = [];
    this.eventType = '';
  }

  private processLine(line: string): SseEvent | null {
    if (line === '') return this.dispatch();
    if (line.startsWith(':')) return null; // comment (keep-alive)

    const colon = line.indexOf(':');
    const field = colon === -1 ? line : line.slice(0, colon);
    let value = colon === -1 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) value = value.slice(1);

    switch (field) {
      case 'event':
        this.eventType = value;
        break;
      case 'data':
        this.dataLines.push(value);
        break;
      case 'id':
        if (!value.includes('\0')) this.eventId = value;
        break;
      // "retry" only matters for EventSource reconnection; other fields are ignored.
    }
    return null;
  }

  private dispatch(): SseEvent | null {
    const hasData = this.dataLines.length > 0;
    const event: SseEvent = {
      type: this.eventType || 'message',
      data: this.dataLines.join('\n'),
      ...(this.eventId !== undefined ? { id: this.eventId } : {}),
    };
    this.dataLines = [];
    this.eventType = '';
    return hasData ? event : null; // an event without data is not dispatched
  }
}
