import { DatePipe } from '@angular/common';
import { Component, computed, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { DocumentsClient, type Document } from '../api/system-api.client';
import { apiErrorMessage, problemOf } from '../core/api-error';
import { canDelete, canReindex, formatBytes, isInProgress, namespacesOf, STATUS_LABELS } from '../core/documents';

const POLL_INTERVAL_MS = 2000;
const ACCEPTED_TYPES = '.md,.txt,.pdf'; // the ones the System API accepts
const MAX_FILE_SIZE_BYTES = 20 * 1024 * 1024; // Documents__MaxFileSizeBytes

@Component({
  selector: 'app-documents-page',
  imports: [FormsModule, DatePipe],
  templateUrl: './documents-page.html',
  styleUrl: './documents-page.css',
})
export class DocumentsPage implements OnInit {
  private readonly client = inject(DocumentsClient);

  protected readonly accept = ACCEPTED_TYPES;
  protected readonly labels = STATUS_LABELS;
  protected readonly formatBytes = formatBytes;
  protected readonly canDelete = canDelete;
  protected readonly canReindex = canReindex;
  protected readonly isInProgress = isInProgress;

  protected readonly documents = signal<Document[]>([]);
  protected readonly loading = signal(true);
  protected readonly listError = signal<string | null>(null);
  protected readonly filter = signal(''); // '' = every namespace
  protected readonly namespaces = computed(() => namespacesOf(this.documents()));
  protected readonly visible = computed(() =>
    this.filter() ? this.documents().filter((d) => d.namespace === this.filter()) : this.documents(),
  );

  // Upload form
  protected readonly file = signal<File | null>(null);
  protected title = '';
  protected namespace = 'default';
  protected readonly uploading = signal(false);
  protected readonly uploadError = signal<string | null>(null);
  protected readonly highlighted = signal<string | null>(null);

  protected readonly busy = signal<ReadonlySet<string>>(new Set()); // ids with an action running
  protected readonly rowError = signal<{ id: string; message: string } | null>(null);

  private polling = false;

  constructor() {
    // Polls GET /documents/{id} for each document still pending or indexing.
    const timer = setInterval(() => this.poll(), POLL_INTERVAL_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  ngOnInit(): void {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.listError.set(null);
    try {
      this.documents.set(await firstValueFrom(this.client.listDocuments()));
    } catch (error) {
      this.listError.set(apiErrorMessage(error, 'No se pudo cargar la lista de documentos'));
    } finally {
      this.loading.set(false);
    }
  }

  protected onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    this.uploadError.set(null);
    // Checked here too so a big file is not sent just to be rejected.
    if (file && file.size > MAX_FILE_SIZE_BYTES) {
      this.uploadError.set(`El archivo pesa ${formatBytes(file.size)} y el máximo es 20 MB.`);
      input.value = '';
      this.file.set(null);
      return;
    }
    this.file.set(file);
  }

  protected async upload(form: HTMLFormElement): Promise<void> {
    const file = this.file();
    if (!file) return;
    this.uploading.set(true);
    this.uploadError.set(null);
    try {
      const created = await firstValueFrom(
        this.client.uploadDocument(
          { data: file, fileName: file.name },
          this.namespace.trim() || undefined,
          this.title.trim() || undefined,
        ),
      );
      this.upsert(created);
      this.flash(created.id);
      form.reset();
      this.file.set(null);
      this.title = '';
    } catch (error) {
      this.uploadError.set(apiErrorMessage(error, 'No se pudo subir el documento'));
      // 409: the same content already exists in that namespace.
      const existing = problemOf(error)?.['existingDocumentId'];
      if (typeof existing === 'string') this.flash(existing);
    } finally {
      this.uploading.set(false);
    }
  }

  protected async reindex(document: Document): Promise<void> {
    await this.runAction(document, 'No se pudo reindexar', () => this.client.reindexDocument(document.id));
  }

  protected async remove(document: Document): Promise<void> {
    if (!confirm(`¿Borrar "${document.title}"? Se eliminan el archivo y sus fragmentos indexados.`)) return;
    const deleted = await this.runAction(document, 'No se pudo borrar', () => this.client.deleteDocument(document.id));
    if (deleted) this.documents.update((list) => list.filter((d) => d.id !== document.id));
  }

  private async runAction(
    document: Document,
    fallback: string,
    action: () => ReturnType<DocumentsClient['getDocument']>,
  ): Promise<boolean> {
    this.busy.update((s) => new Set(s).add(document.id));
    this.rowError.set(null);
    try {
      this.upsert(await firstValueFrom(action()));
      return true;
    } catch (error) {
      this.rowError.set({ id: document.id, message: apiErrorMessage(error, fallback) });
      return false;
    } finally {
      this.busy.update((s) => {
        const next = new Set(s);
        next.delete(document.id);
        return next;
      });
    }
  }

  private async poll(): Promise<void> {
    const inProgress = this.documents().filter(isInProgress);
    if (this.polling || inProgress.length === 0) return;
    this.polling = true; // no overlapping rounds if the API is slow
    try {
      const results = await Promise.allSettled(inProgress.map((d) => firstValueFrom(this.client.getDocument(d.id))));
      for (const result of results) {
        if (result.status === 'fulfilled') this.upsert(result.value);
      }
    } finally {
      this.polling = false;
    }
  }

  /** Replaces the document by id, or adds it first (newest first, like the API). */
  private upsert(document: Document): void {
    this.documents.update((list) =>
      list.some((d) => d.id === document.id)
        ? list.map((d) => (d.id === document.id ? document : d))
        : [document, ...list],
    );
  }

  private flash(id: string): void {
    this.highlighted.set(id);
    setTimeout(() => {
      if (this.highlighted() === id) this.highlighted.set(null);
    }, 4000);
  }
}
