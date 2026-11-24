import { Component, inject, OnInit, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { DocumentsClient } from '../api/system-api.client';
import { apiErrorMessage } from '../core/api-error';
import { namespacesOf } from '../core/documents';
import { Composer } from './composer';
import { ConversationsStore } from './conversations-store';

/**
 * /chat: choose the namespace and ask the first question. The conversation is
 * created only then (no empty conversations), and its title is that question.
 */
@Component({
  selector: 'app-new-conversation',
  imports: [FormsModule, Composer],
  template: `
    <div class="center">
      <div class="intro">
        <h1>Nueva conversación</h1>
        <p class="muted">
          Las respuestas salen de los documentos del namespace elegido y citan sus fuentes. El namespace queda fijo para
          toda la conversación.
        </p>
        <label class="inline">
          Namespace
          <select [ngModel]="namespace()" (ngModelChange)="namespace.set($event)" [disabled]="creating()">
            @for (ns of namespaces(); track ns) {
              <option [value]="ns">{{ ns }}</option>
            }
          </select>
        </label>
        @if (error(); as message) {
          <p class="alert error" role="alert">{{ message }}</p>
        }
      </div>
      <app-composer [disabled]="creating()" (send)="start($event)" />
    </div>
  `,
  styles: `
    .center {
      max-width: 760px;
      width: 100%;
      margin: auto;
      padding: 24px 20px;
      display: flex;
      flex-direction: column;
      gap: 20px;
    }
    label.inline {
      flex-direction: row;
      align-items: center;
      gap: 8px;
    }
  `,
})
export class NewConversation implements OnInit {
  private readonly store = inject(ConversationsStore);
  private readonly documents = inject(DocumentsClient);
  private readonly router = inject(Router);

  protected readonly namespaces = signal(['default']);
  protected readonly namespace = signal('default');
  protected readonly creating = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly composer = viewChild.required(Composer);

  async ngOnInit(): Promise<void> {
    try {
      // The namespaces that have documents (the conversation would find nothing elsewhere).
      this.namespaces.set(namespacesOf(await firstValueFrom(this.documents.listDocuments())));
    } catch {
      // Keeps "default": the question can still be asked.
    }
  }

  protected async start(content: string): Promise<void> {
    this.creating.set(true);
    this.error.set(null);
    try {
      const conversation = await this.store.create(this.namespace(), content);
      await this.router.navigate(['/chat', conversation.id]);
    } catch (error) {
      this.error.set(apiErrorMessage(error, 'No se pudo crear la conversación'));
      this.creating.set(false);
      this.composer().restore(content); // the question is not lost
    }
  }
}
