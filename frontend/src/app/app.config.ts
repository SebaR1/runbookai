import { registerLocaleData } from '@angular/common';
import { provideHttpClient, withFetch } from '@angular/common/http';
import localeEsAr from '@angular/common/locales/es-AR';
import { ApplicationConfig, LOCALE_ID, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { API_BASE_URL } from './api/system-api.client';
import { routes } from './app.routes';

registerLocaleData(localeEsAr);

/**
 * Base path of the System API. Same origin as the SPA: nginx (Compose) or the
 * ng serve proxy (local) forward /api/* to the System API without the prefix.
 * The prefix keeps API routes (/documents) apart from SPA routes (/documents).
 */
export const API_BASE = '/api';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withFetch()),
    { provide: API_BASE_URL, useValue: API_BASE },
    { provide: LOCALE_ID, useValue: 'es-AR' },
  ],
};
