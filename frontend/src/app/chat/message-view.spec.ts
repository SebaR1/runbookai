import { TestBed } from '@angular/core/testing';
import type { MessageSource } from '../api/system-api.client';
import type { ChatMessage } from '../core/chat-reducer';
import { MessageView } from './message-view';

const source = (index: number, section: string): MessageSource => ({
  index, documentId: 'd1', chunkId: `k${index}`, title: 'runbook-base-de-datos', page: 1, section, distance: 0.36,
});

async function render(message: ChatMessage): Promise<HTMLElement> {
  const fixture = TestBed.createComponent(MessageView);
  fixture.componentRef.setInput('message', message);
  await fixture.whenStable();
  return fixture.nativeElement as HTMLElement;
}

const answer: ChatMessage = {
  id: 'a1',
  role: 'assistant',
  content: 'Te llamás Sebastián [conversación previa]. Ejecutá `az login` [2] y avisá [1][7].',
  status: 'completed',
  sources: [source(1, 'Escalamiento'), source(2, 'Failover'), source(3, 'Descripción')],
};

describe('MessageView', () => {
  it('renders only [n] with a matching source as citation buttons', async () => {
    const el = await render(answer);
    const cites = [...el.querySelectorAll('button.cite')].map((b) => b.textContent?.trim());
    expect(cites).toEqual(['2', '1']);
    const text = el.querySelector('.text')!.textContent!;
    // Exact text: the template must not add whitespace (the text is pre-wrap).
    expect(text).toBe('Te llamás Sebastián [conversación previa]. Ejecutá az login 2 y avisá 1[7].');
    expect(el.querySelector('code')?.textContent).toBe('az login');
  });

  it('lists cited sources first and marks them', async () => {
    const el = await render(answer);
    const items = [...el.querySelectorAll('.sources li')];
    expect(items.map((li) => li.querySelector('.num')?.textContent)).toEqual(['2', '1', '3']);
    expect(items.map((li) => li.classList.contains('cited'))).toEqual([true, true, false]);
    expect(el.querySelector('.sources summary')?.textContent).toContain('2 citadas de 3');
  });

  it('selects the source when its citation is clicked', async () => {
    const fixture = TestBed.createComponent(MessageView);
    fixture.componentRef.setInput('message', answer);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    Element.prototype.scrollIntoView ??= () => {}; // not implemented by jsdom

    (el.querySelector('button.cite') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(el.querySelector('.sources li.selected .num')?.textContent).toBe('2');
  });

  it('shows the status of an answer that did not complete', async () => {
    const error = await render({ ...answer, status: 'error', errorDetail: 'El modelo no respondió.' });
    expect(error.querySelector('.alert.error')?.textContent).toContain('El modelo no respondió.');

    const cancelled = await render({ ...answer, status: 'cancelled' });
    expect(cancelled.querySelector('.badge.cancelled')?.textContent).toBe('Cancelada');
  });
});
