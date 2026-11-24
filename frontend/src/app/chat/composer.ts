import { Component, input, output, signal } from '@angular/core';

export const MAX_MESSAGE_LENGTH = 4000; // Conversations__MaxMessageLength in the System API

/** The question box: Enter sends, Shift+Enter is a new line; "Detener" while streaming. */
@Component({
  selector: 'app-composer',
  template: `
    <form (submit)="submit($event)">
      <textarea
        rows="2"
        [placeholder]="placeholder()"
        [value]="text()"
        (input)="text.set($any($event.target).value)"
        (keydown.enter)="onEnter($any($event))"
        [disabled]="disabled()"
        [attr.maxlength]="max"
        aria-label="Pregunta"
      ></textarea>
      @if (streaming()) {
        <button type="button" (click)="stop.emit()">Detener</button>
      } @else {
        <button class="primary" type="submit" [disabled]="disabled() || !text().trim()">Enviar</button>
      }
    </form>
    @if (text().length > max * 0.9) {
      <p class="count muted">{{ text().length }} / {{ max }}</p>
    }
  `,
  styles: `
    form {
      display: flex;
      gap: 8px;
      align-items: flex-end;
    }
    textarea {
      flex: 1;
      resize: vertical;
      min-height: 44px;
      max-height: 240px;
    }
    .count {
      margin: 4px 0 0;
      font-size: 0.75rem;
      text-align: right;
    }
  `,
})
export class Composer {
  readonly disabled = input(false);
  readonly streaming = input(false);
  readonly placeholder = input('Preguntá algo sobre los runbooks…');
  readonly send = output<string>();
  readonly stop = output<void>();

  protected readonly max = MAX_MESSAGE_LENGTH;
  protected readonly text = signal('');

  /** Puts back a text that could not be sent (400 before the stream). */
  restore(text: string): void {
    this.text.set(text);
  }

  protected onEnter(event: KeyboardEvent): void {
    if (event.shiftKey || event.isComposing) return;
    event.preventDefault();
    this.submit(event);
  }

  protected submit(event: Event): void {
    event.preventDefault();
    const content = this.text().trim();
    if (!content || this.disabled() || this.streaming()) return;
    this.send.emit(content);
    this.text.set('');
  }
}
