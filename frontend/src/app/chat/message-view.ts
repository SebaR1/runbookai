import { DecimalPipe } from '@angular/common';
import { Component, computed, ElementRef, inject, input, signal } from '@angular/core';
import { ChatMessage } from '../core/chat-reducer';
import { citedIndexes, parseMessageText } from '../core/message-text';

/**
 * One message. For the assistant: the text with its citations as buttons, the
 * sources (the cited ones first and highlighted) and the status when it did not
 * complete. Rendered from segments, never with innerHTML: the text comes from an LLM.
 */
@Component({
  selector: 'app-message-view',
  imports: [DecimalPipe],
  template: `
    <div class="bubble" [class.user]="message().role === 'user'">
      <div class="text">
        @for (segment of segments(); track $index) {
          @switch (segment.kind) {
            @case ('text') {<span>{{ segment.text }}</span>}
            @case ('code') {
              @if (segment.block) {<pre class="code">{{ segment.text }}</pre>} @else {<code>{{ segment.text }}</code>}
            }
            @case ('citation') {<button
                type="button"
                class="cite"
                [class.selected]="selected() === segment.index"
                (click)="select(segment.index)"
                [attr.aria-label]="'Fuente ' + segment.index"
              >{{ segment.index }}</button>}
          }
        }
        @if (message().status === 'streaming') {
          <span class="cursor" aria-hidden="true"></span>
        }
      </div>

      @if (message().status === 'streaming' && !message().content) {
        <p class="muted small"><span class="spinner"></span> Buscando en los documentos…</p>
      }
      @if (message().status === 'error') {
        <p class="alert error small" role="alert">
          {{ message().errorDetail || 'La respuesta no se pudo completar.' }}
          @if (message().content) { Se muestra el texto parcial. }
        </p>
      }
      @if (message().status === 'cancelled') {
        <p class="small"><span class="badge cancelled">Cancelada</span> La respuesta se detuvo antes de terminar.</p>
      }

      @if (sources().length > 0) {
        <details class="sources" [open]="hasCitations()">
          <summary>Fuentes ({{ cited().length }} citadas de {{ sources().length }})</summary>
          <ol>
            @for (s of sources(); track s.index) {
              <li
                [id]="sourceId(s.index)"
                [class.cited]="citedSet().has(s.index)"
                [class.selected]="selected() === s.index"
              >
                <span class="num">{{ s.index }}</span>
                <span class="body">
                  <strong>{{ s.title }}</strong>
                  @if (s.section) { <span class="section">{{ s.section }}</span> }
                  <span class="muted small">Página {{ s.page }} · distancia {{ s.distance | number: '1.2-2' }}</span>
                </span>
              </li>
            }
          </ol>
        </details>
      }
    </div>
  `,
  styleUrl: './message-view.css',
})
export class MessageView {
  private readonly host = inject(ElementRef<HTMLElement>);

  readonly message = input.required<ChatMessage>();

  protected readonly selected = signal<number | null>(null);
  private readonly sourceIndexes = computed(() => new Set(this.message().sources.map((s) => s.index)));
  protected readonly segments = computed(() => parseMessageText(this.message().content, this.sourceIndexes()));
  protected readonly cited = computed(() => citedIndexes(this.message().content, this.sourceIndexes()));
  protected readonly citedSet = computed(() => new Set(this.cited()));
  protected readonly hasCitations = computed(() => this.cited().length > 0);
  /** Cited sources first, in citation order; then the rest, by index. */
  protected readonly sources = computed(() => {
    const order = new Map(this.cited().map((index, i) => [index, i]));
    return [...this.message().sources].sort(
      (a, b) => (order.get(a.index) ?? 1000 + a.index) - (order.get(b.index) ?? 1000 + b.index),
    );
  });

  protected sourceId(index: number): string {
    return `source-${this.message().id}-${index}`;
  }

  protected select(index: number): void {
    this.selected.set(this.selected() === index ? null : index);
    const host = this.host.nativeElement as HTMLElement;
    host.querySelector('details')?.setAttribute('open', '');
    host.querySelector(`#${CSS.escape(this.sourceId(index))}`)?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  }
}
