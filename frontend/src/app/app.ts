import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header class="topbar">
      <span class="brand">RunbookAI</span>
      <nav>
        <a routerLink="/chat" routerLinkActive="active">Chat</a>
        <a routerLink="/documents" routerLinkActive="active">Documentos</a>
      </nav>
    </header>
    <main class="page">
      <router-outlet />
    </main>
  `,
  styleUrl: './app.css',
})
export class App {}
