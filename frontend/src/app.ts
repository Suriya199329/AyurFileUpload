import { Component, DestroyRef, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse, HttpEventType, HttpParams } from '@angular/common/http';
import { Subscription } from 'rxjs';

interface LibraryFile { id: string; name: string; kind: string; size: number; uploadedAt: string; }
interface FilePage { items: LibraryFile[]; total: number; page: number; pageSize: number; }

const apiBaseUrl = (window as Window & { __API_BASE_URL__?: string }).__API_BASE_URL__ ?? '';

@Component({
  selector: 'app-root', standalone: true, imports: [DatePipe], templateUrl: './app.html'
})
export class AppComponent {
  private readonly http = inject(HttpClient);
  private listing?: Subscription;
  readonly files = signal<LibraryFile[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly loading = signal(true);
  readonly uploading = signal(false);
  readonly progress = signal(0);
  readonly selected = signal<File | null>(null);
  readonly error = signal('');
  readonly notice = signal('');
  readonly dragging = signal(false);
  search = '';
  kind = '';
  constructor() {
    this.load();
    inject(DestroyRef).onDestroy(() => this.listing?.unsubscribe());
  }
  load(page = 1) {
    this.listing?.unsubscribe();
    this.loading.set(true);
    this.error.set('');
    const params = new HttpParams().set('search', this.search).set('kind', this.kind).set('page', page);
    this.listing = this.http.get<FilePage>(`${apiBaseUrl}/api/files`, { params }).subscribe({
      next: result => { this.files.set(result.items); this.total.set(result.total); this.page.set(result.page); this.loading.set(false); },
      error: err => { this.files.set([]); this.total.set(0); this.loading.set(false); this.error.set(this.message(err, 'Cannot load files. Check that the API and database are running.')); }
    });
  }
  choose(event: Event) {
    const input = event.target as HTMLInputElement;
    this.select(input.files?.[0] ?? null);
    input.value = '';
  }
  drop(event: DragEvent) {
    event.preventDefault(); this.dragging.set(false);
    if (this.uploading()) return;
    if (event.dataTransfer?.files.length !== 1) { this.error.set('Please select one file at a time.'); return; }
    this.select(event.dataTransfer.files[0]);
  }
  select(file: File | null) {
    this.error.set(''); this.notice.set(''); this.selected.set(null);
    if (!file) return;
    if (!/\.(docx?|pdf|jpe?g|png|gif|webp)$/i.test(file.name)) { this.error.set('Choose a Word document, PDF, JPG, PNG, GIF or WebP image.'); return; }
    if (file.size === 0 || file.size > 20 * 1024 * 1024) { this.error.set('Choose a non-empty file up to 20 MB.'); return; }
    this.selected.set(file);
  }
  upload() {
    const file = this.selected();
    if (!file || this.uploading()) return;
    this.error.set(''); this.notice.set(''); this.uploading.set(true); this.progress.set(0);
    const body = new FormData(); body.append('file', file);
    this.http.post<LibraryFile>(`${apiBaseUrl}/api/files`, body, { observe: 'events', reportProgress: true }).subscribe({
      next: event => {
        if (event.type === HttpEventType.UploadProgress) this.progress.set(Math.round(100 * event.loaded / (event.total || file.size)));
        if (event.type === HttpEventType.Response) {
          this.uploading.set(false); this.selected.set(null); this.notice.set(`${file.name} uploaded successfully.`); this.load();
        }
      },
      error: err => { this.uploading.set(false); this.error.set(this.message(err, 'Upload failed. Please try again.')); }
    });
  }
  async download(file: LibraryFile) {
    this.error.set('');
    this.http.get(`${apiBaseUrl}/api/files/${file.id}/download`, { responseType: 'blob' }).subscribe({
      next: blob => {
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a'); link.href = url; link.download = file.name;
        document.body.appendChild(link); link.click(); link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 30000);
      },
      error: () => this.error.set('Download failed. The file may be unavailable. Please try again.')
    });
  }
  size(bytes: number) { return bytes < 1024 * 1024 ? `${Math.max(1, Math.round(bytes / 1024))} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`; }
  get pages() { return Math.max(1, Math.ceil(this.total() / 20)); }
  private message(error: HttpErrorResponse, fallback: string) {
    return error.status === 413 ? 'The file exceeds the 20 MB upload limit.' : error.error?.message || fallback;
  }
}
