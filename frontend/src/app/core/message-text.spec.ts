import { citedIndexes, parseMessageText } from './message-text';

const sources = new Set([1, 2, 3, 12]);

describe('parseMessageText', () => {
  it('splits text and citations', () => {
    expect(parseMessageText('Avisar en el canal [1].', sources)).toEqual([
      { kind: 'text', text: 'Avisar en el canal ' },
      { kind: 'citation', index: 1 },
      { kind: 'text', text: '.' },
    ]);
  });

  it('handles consecutive and multi-digit citations', () => {
    expect(parseMessageText('Dura 60 s [2][3][12]', sources)).toEqual([
      { kind: 'text', text: 'Dura 60 s ' },
      { kind: 'citation', index: 2 },
      { kind: 'citation', index: 3 },
      { kind: 'citation', index: 12 },
    ]);
  });

  it('keeps non-numeric brackets as text', () => {
    expect(parseMessageText('Te llamás Sebastián [conversación previa].', sources)).toEqual([
      { kind: 'text', text: 'Te llamás Sebastián [conversación previa].' },
    ]);
  });

  it('keeps a citation without a matching source as text', () => {
    expect(parseMessageText('Valor [7] y [1]', sources)).toEqual([
      { kind: 'text', text: 'Valor [7] y ' },
      { kind: 'citation', index: 1 },
    ]);
  });

  it('returns a single text segment when there are no citations', () => {
    expect(parseMessageText('No encontré información.', new Set())).toEqual([
      { kind: 'text', text: 'No encontré información.' },
    ]);
    expect(parseMessageText('', sources)).toEqual([]);
  });

  it('extracts inline code and does not look for citations inside it', () => {
    expect(parseMessageText('Ejecutar `kubectl get pods [1]` [1]', sources)).toEqual([
      { kind: 'text', text: 'Ejecutar ' },
      { kind: 'code', text: 'kubectl get pods [1]', block: false },
      { kind: 'text', text: ' ' },
      { kind: 'citation', index: 1 },
    ]);
  });

  it('extracts fenced code blocks, with or without a language', () => {
    expect(parseMessageText('Pasos:\n```bash\naz login\naz account show\n```\nListo [2]', sources)).toEqual([
      { kind: 'text', text: 'Pasos:\n' },
      { kind: 'code', text: 'az login\naz account show', block: true },
      { kind: 'text', text: '\nListo ' },
      { kind: 'citation', index: 2 },
    ]);
  });

  it('leaves an unclosed backtick as text (partial answer while streaming)', () => {
    expect(parseMessageText('Ejecutar `az postgres', sources)).toEqual([
      { kind: 'text', text: 'Ejecutar `az postgres' },
    ]);
  });
});

describe('citedIndexes', () => {
  it('lists each cited source once, in order of appearance', () => {
    expect(citedIndexes('a [3] b [1][3] `[2]` [9]', sources)).toEqual([3, 1]);
  });
});
