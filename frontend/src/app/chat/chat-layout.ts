import { DatePipe } from '@angular/common';
import { Component, inject, OnInit } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ConversationsStore } from './conversations-store';

@Component({
  selector: 'app-chat-layout',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, DatePipe],
  template: `
    <aside class="sidebar">
      <a class="new" routerLink="/chat" [routerLinkActiveOptions]="{ exact: true }" routerLinkActive="active">
        + Nueva conversación
      </a>
      @if (store.error(); as message) {
        <p class="alert error">{{ message }}</p>
      }
      <ul>
        @for (c of store.conversations(); track c.id) {
          <li>
            <a [routerLink]="['/chat', c.id]" routerLinkActive="active">
              <span class="title">{{ c.title || 'Sin título' }}</span>
              <span class="meta">
                <span class="badge">{{ c.namespace }}</span>
                {{ c.updatedAt | date: 'short' }}
              </span>
            </a>
          </li>
        } @empty {
          @if (!store.loading()) {
            <li class="muted empty">Todavía no hay conversaciones.</li>
          }
        }
      </ul>
    </aside>
    <section class="content">
      <router-outlet />
    </section>
  `,
  styles: `
    :host {
      display: flex;
      height: 100%;
    }
    .sidebar {
      width: 290px;
      flex: none;
      border-right: 1px solid var(--border);
      background: var(--surface);
      overflow-y: auto;
      padding: 12px;
    }
    .content {
      flex: 1;
      min-width: 0;
      display: flex;
      flex-direction: column;
    }
    ul {
      list-style: none;
      margin: 12px 0 0;
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    a {
      display: block;
      padding: 8px 10px;
      border-radius: var(--radius);
      color: inherit;
      text-decoration: none;
    }
    a:hover {
      background: var(--hover);
    }
    a.active {
      background: var(--accent-soft);
    }
    .new {
      border: 1px dashed var(--border);
      text-align: center;
      font-weight: 600;
    }
    .title {
      display: block;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .meta {
      display: flex;
      gap: 8px;
      align-items: center;
      font-size: 0.75rem;
      color: var(--muted);
      margin-top: 2px;
    }
    .empty {
      padding: 8px 10px;
      font-size: 0.9rem;
    }
    @media (max-width: 700px) {
      .sidebar {
        width: 200px;
      }
    }
  `,
})
export class ChatLayout implements OnInit {
  protected readonly store = inject(ConversationsStore);

  ngOnInit(): void {
    void this.store.refresh();
  }
}
