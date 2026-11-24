import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'chat' },
  {
    path: 'documents',
    title: 'Documentos · RunbookAI',
    loadComponent: () => import('./documents/documents-page').then((m) => m.DocumentsPage),
  },
  {
    // The layout (conversation list) stays while moving between conversations;
    // ConversationView is reused when only :id changes.
    path: 'chat',
    title: 'Chat · RunbookAI',
    loadComponent: () => import('./chat/chat-layout').then((m) => m.ChatLayout),
    children: [
      { path: '', loadComponent: () => import('./chat/new-conversation').then((m) => m.NewConversation) },
      { path: ':id', loadComponent: () => import('./chat/conversation-view').then((m) => m.ConversationView) },
    ],
  },
  { path: '**', redirectTo: 'chat' },
];
