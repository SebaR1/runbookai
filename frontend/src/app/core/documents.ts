import type { Document, DocumentStatus } from '../api/system-api.client';

export const STATUS_LABELS: Record<DocumentStatus, string> = {
  pending: 'Pendiente',
  indexing: 'Indexando',
  indexed: 'Indexado',
  error: 'Error',
  deleted: 'Borrado',
};

/** Still moving through pending → indexing: the page keeps polling it. */
export function isInProgress(document: Document): boolean {
  return document.status === 'pending' || document.status === 'indexing';
}

/** The System API answers 409 while indexing (the chunks are being written). */
export function canDelete(document: Document): boolean {
  return document.status !== 'indexing' && document.status !== 'deleted';
}

/** Reindex only from a final state (same rule as the System API). */
export function canReindex(document: Document): boolean {
  return document.status === 'indexed' || document.status === 'error';
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1).replace('.', ',')} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1).replace('.', ',')} MB`;
}

/** Distinct namespaces, always including "default", sorted. */
export function namespacesOf(documents: readonly Document[]): string[] {
  return [...new Set(['default', ...documents.map((d) => d.namespace)])].sort();
}
