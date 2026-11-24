import { ApiException, type ProblemDetails } from '../api/system-api.client';

/**
 * The message to show for a failed call of the generated client.
 *
 * NSwag throws the parsed ProblemDetails for the status codes the contract declares
 * (409, 413...), and an ApiException for the rest (status 0 when the server cannot
 * be reached, 502 from nginx when the System API is down).
 */
export function apiErrorMessage(error: unknown, fallback: string): string {
  if (ApiException.isApiException(error)) {
    if (error.status === 0) return 'No se pudo conectar con el servidor.';
    const problem = tryParse(error.response);
    if (problem?.detail || problem?.title) return (problem.detail ?? problem.title)!;
    return error.status >= 500
      ? 'El servidor no está disponible. Probá de nuevo en un momento.'
      : `${fallback} (HTTP ${error.status}).`;
  }
  if (isProblem(error)) return error.detail ?? error.title ?? fallback;
  return fallback;
}

/** The ProblemDetails of the error, if there is one (to read extensions such as existingDocumentId). */
export function problemOf(error: unknown): ProblemDetails | undefined {
  if (ApiException.isApiException(error)) return tryParse(error.response);
  return isProblem(error) ? error : undefined;
}

function isProblem(value: unknown): value is ProblemDetails {
  return typeof value === 'object' && value !== null && ('title' in value || 'detail' in value) && 'status' in value;
}

function tryParse(text: string): ProblemDetails | undefined {
  try {
    const value: unknown = JSON.parse(text);
    return isProblem(value) ? value : undefined;
  } catch {
    return undefined;
  }
}
