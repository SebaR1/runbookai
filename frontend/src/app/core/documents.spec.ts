import type { Document, DocumentStatus } from '../api/system-api.client';
import { canDelete, canReindex, formatBytes, isInProgress, namespacesOf } from './documents';

const doc = (status: DocumentStatus, namespace = 'default') => ({ status, namespace }) as Document;

describe('document rules', () => {
  it('polls only documents in progress', () => {
    expect(['pending', 'indexing', 'indexed', 'error', 'deleted'].map((s) => isInProgress(doc(s as DocumentStatus))))
      .toEqual([true, true, false, false, false]);
  });

  it('allows delete except while indexing or already deleted', () => {
    expect(['pending', 'indexing', 'indexed', 'error', 'deleted'].map((s) => canDelete(doc(s as DocumentStatus))))
      .toEqual([true, false, true, true, false]);
  });

  it('allows reindex only from a final state', () => {
    expect(['pending', 'indexing', 'indexed', 'error', 'deleted'].map((s) => canReindex(doc(s as DocumentStatus))))
      .toEqual([false, false, true, true, false]);
  });
});

describe('formatBytes', () => {
  it('uses B, KB and MB with a decimal comma', () => {
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(2048)).toBe('2,0 KB');
    expect(formatBytes(5 * 1024 * 1024 + 300_000)).toBe('5,3 MB');
  });
});

describe('namespacesOf', () => {
  it('returns distinct namespaces, with default always present', () => {
    expect(namespacesOf([doc('indexed', 'ops'), doc('error', 'ops')])).toEqual(['default', 'ops']);
    expect(namespacesOf([])).toEqual(['default']);
  });
});
