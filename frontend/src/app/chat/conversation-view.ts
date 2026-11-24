import { Component, DestroyRef, effect, ElementRef, inject, input, signal, untracked, viewChild } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { API_BASE_URL, ConversationsClient, type Conversation } from '../api/system-api.client';
import { apiErrorMessage } from '../core/api-error';
import {
  applyChatEvent,
  ChatMessage,
  finishChatStream,
  fromApi,
  streamingAnswer,
} from '../core/chat-reducer';
import { ChatRequestError, isAbortError, sendChatMessage } from '../core/chat-stream';
import { Composer } from './composer';
import { ConversationsStore } from './conversations-store';
import { MessageView } from './message-view';

/** /chat/:id. Reused by the router when only :id changes, so it reacts to the input. */
@Component({
  selector: 'app-conversation-view',
  imports: [Composer, MessageView],
  templateUrl: './conversation-view.html',
  styleUrl: './conversation-view.css',
})
export class ConversationView {
  private readonly client = inject(ConversationsClient);
  private readonly store = inject(ConversationsStore);
  private readonly baseUrl = inject(API_BASE_URL);

  /** Route parameter (withComponentInputBinding). */
  readonly id = input.required<string>();

  protected readonly conversation = signal<Conversation | null>(null);
  protected readonly messages = signal<ChatMessage[]>([]);
  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly sendError = signal<string | null>(null);
  protected readonly streaming = signal(false);

  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');
  private readonly composer = viewChild(Composer);
  private abort: AbortController | null = null;
  private loadVersion = 0;

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => void this.open(id));
    });
    // Leaving the page aborts the answer in progress (saved as cancelled).
    inject(DestroyRef).onDestroy(() => this.abort?.abort());
  }

  protected stop(): void {
    this.abort?.abort();
  }

  protected async send(content: string): Promise<void> {
    const conversation = this.conversation();
    if (!conversation || this.streaming()) return;

    this.sendError.set(null);
    const localId = crypto.randomUUID();
    let answer = streamingAnswer(`local-answer-${localId}`);
    const question: ChatMessage = { id: `local-question-${localId}`, role: 'user', content, sources: [] };
    this.messages.update((list) => [...list, question, answer]);
    this.streaming.set(true);
    this.scrollToBottom();

    const update = (next: ChatMessage) => {
      const previousId = answer.id;
      answer = next;
      this.messages.update((list) => list.map((m) => (m.id === previousId ? next : m)));
    };

    const abort = new AbortController();
    this.abort = abort;
    const url = `${this.baseUrl}/conversations/${conversation.id}/messages`;
    try {
      for await (const event of sendChatMessage(url, content, abort.signal)) {
        update(applyChatEvent(answer, event));
        this.scrollToBottom();
      }
      update(finishChatStream(answer, 'ended'));
    } catch (error) {
      if (isAbortError(error)) {
        update(finishChatStream(answer, 'aborted'));
      } else if (error instanceof ChatRequestError) {
        // Rejected before the stream (400/404/502): nothing was saved. The question
        // goes back to the box so it can be fixed and sent again.
        this.messages.update((list) => list.filter((m) => m.id !== question.id && m.id !== answer.id));
        this.sendError.set(error.message);
        this.composer()?.restore(content);
      } else {
        update(finishChatStream(answer, 'failed', 'Se perdió la conexión con el servidor.'));
      }
    } finally {
      if (this.abort === abort) this.abort = null;
      this.streaming.set(false);
      void this.store.refresh(); // title (first question) and order (updatedAt)
    }
  }

  private async open(id: string): Promise<void> {
    this.abort?.abort(); // moving to another conversation stops the current answer
    const version = ++this.loadVersion;
    this.loading.set(true);
    this.loadError.set(null);
    this.sendError.set(null);
    this.messages.set([]);
    try {
      const [conversation, messages] = await Promise.all([
        firstValueFrom(this.client.getConversation(id)),
        firstValueFrom(this.client.listMessages(id)),
      ]);
      if (version !== this.loadVersion) return; // another conversation was opened meanwhile
      this.conversation.set(conversation);
      this.messages.set(messages.map(fromApi));
    } catch (error) {
      if (version !== this.loadVersion) return;
      this.conversation.set(null);
      this.loadError.set(apiErrorMessage(error, 'No se pudo abrir la conversación'));
      return;
    } finally {
      if (version === this.loadVersion) this.loading.set(false);
    }

    this.scrollToBottom();
    const pending = this.store.takePendingQuestion(id);
    if (pending) void this.send(pending);
  }

  private scrollToBottom(): void {
    // After the DOM update: the new tokens must be rendered before measuring.
    requestAnimationFrame(() => {
      const el = this.scroller()?.nativeElement;
      if (!el) return;
      // Only follows the answer if the user did not scroll up to read.
      const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 160;
      if (nearBottom || this.messages().length <= 2) el.scrollTop = el.scrollHeight;
    });
  }
}
