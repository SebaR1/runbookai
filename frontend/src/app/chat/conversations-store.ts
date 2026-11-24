import { inject, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ConversationsClient, type Conversation } from '../api/system-api.client';
import { apiErrorMessage } from '../core/api-error';

/**
 * The conversation list, shared by the sidebar and the views that change it
 * (a new conversation, a first question that sets the title).
 */
@Injectable({ providedIn: 'root' })
export class ConversationsStore {
  private readonly client = inject(ConversationsClient);

  readonly conversations = signal<Conversation[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  /**
   * The question typed in "Nueva conversación", handed to the conversation view
   * after the conversation is created and the router moves to /chat/:id.
   */
  private pending: { conversationId: string; content: string } | null = null;

  async refresh(): Promise<void> {
    this.loading.set(true);
    try {
      this.conversations.set(await firstValueFrom(this.client.listConversations()));
      this.error.set(null);
    } catch (error) {
      this.error.set(apiErrorMessage(error, 'No se pudieron cargar las conversaciones'));
    } finally {
      this.loading.set(false);
    }
  }

  async create(namespace: string, firstQuestion: string): Promise<Conversation> {
    const conversation = await firstValueFrom(this.client.createConversation({ namespace, title: undefined }));
    this.conversations.update((list) => [conversation, ...list]);
    this.pending = { conversationId: conversation.id, content: firstQuestion };
    return conversation;
  }

  /** Returns (once) the question waiting to be sent in that conversation. */
  takePendingQuestion(conversationId: string): string | null {
    if (this.pending?.conversationId !== conversationId) return null;
    const { content } = this.pending;
    this.pending = null;
    return content;
  }
}
