# Evaluación del RAG (Fase 3)

Medido el 2026-11-23 con `qwen3:8b` (temperatura 0) y `bge-m3`, sobre Ollama en la RX 7600.

## Cómo se evalúa

- **Corpus** (`rag-service/eval/corpus/`): 8 documentos ficticios.
  - 5 runbooks en markdown en español (pagos, autenticación, base de datos, despliegues, red).
  - 1 runbook en inglés (Redis).
  - 2 PDF de 5 páginas (política de guardias, acceso a producción), generados con `scripts/make_pdfs.py`.
  - Hay datos parecidos entre documentos a propósito (umbrales de latencia, comandos de reinicio, equipos de escalamiento).
- **Preguntas** (`rag-service/eval/questions.jsonl`): 19 en total.
  - 7 de dato exacto (2 sobre PDF)
  - 4 de procedimiento
  - 3 ambiguas entre documentos
  - 2 entre idiomas
  - 3 sin respuesta en el corpus

  Cada pregunta trae la **evidencia** (el texto que tiene que estar en algún chunk recuperado) y los **datos clave** que tiene que contener la respuesta.
- **Script:** `uv run python -m scripts.run_eval [--strategy] [--chunk-size] [--k] [--prompt] [--max-distance]`.
  - Ingesta el corpus en un namespace aislado, corre las preguntas, lo borra al terminar y compara contra `eval/baseline.json`.
  - Los resultados detallados, con cada respuesta, quedan en `eval/results/` (no versionado).
- **Métricas deterministas, sin LLM-as-judge.** Son más toscas, pero repetibles: el mismo resultado da el mismo puntaje.

## Resultados

La línea base es la configuración de la Fase 2: chunking por párrafos, 1000 caracteres, `k=5` y prompt v1. Cada experimento cambia una sola variable, salvo los combinados del final.

| Config | hit@k | MRR | Datos clave | Correctas | Abst. correcta | Abst. con frase fija | Latencia (ms) | Tokens de entrada |
|---|---|---|---|---|---|---|---|---|
| **línea base** (párrafo, 1000, k5, v1) | 1.00 | 0.81 | 1.00 | 1.00 | 1.00 | 0.00 | 1826 | 1181 |
| chunking por títulos | 1.00 | **0.97** | 1.00 | 1.00 | 1.00 | 0.00 | 2138 | 837 |
| chunk de 500 | 1.00 | 0.89 | 1.00 | 1.00 | 1.00 | 0.00 | 2316 | 784 |
| chunk de 1500 | 1.00 | 0.86 | 1.00 | 1.00 | 1.00 | 0.00 | 3195 | 1595 |
| k=3 | 1.00 | 0.81 | 1.00 | 1.00 | 1.00 | 0.00 | 2358 | 772 |
| k=8 | 1.00 | 0.81 | 1.00 | 1.00 | 1.00 | 0.00 | 3209 | 1810 |
| prompt v2 | 1.00 | 0.81 | 1.00 | 1.00 | 1.00 | **1.00** | 2313 | 1257 |
| umbral de distancia 0.5 | 1.00 | 0.81 | 1.00 | 1.00 | 1.00 | 0.33 | 1859 | 869 |
| títulos + v2 | 1.00 | 0.97 | 1.00 | 1.00 | 1.00 | 1.00 | 1994 | 913 |
| títulos + v2 + k=3 | 1.00 | 0.97 | 1.00 | 1.00 | 1.00 | 1.00 | 1389 | 634 |
| títulos + v2 + 500 + k=3 | 1.00 | 0.97 | 1.00 | 1.00 | 1.00 | 1.00 | 1476 | 567 |

La latencia tiene ruido de unos ±500 ms entre corridas (depende de si el modelo estaba cargado y de otras cargas de la GPU). Sirve para ver tendencias, no diferencias chicas.

## Qué aprendimos

1. **El chunking por títulos es la mejora más clara de retrieval.** El MRR sube de 0.81 a 0.97: el chunk con la evidencia pasa a estar casi siempre primero. Es porque cada chunk es una sección completa y arranca con su ruta ("Runbook pagos > Alertas y umbrales"), así que el embedding sabe de qué servicio habla. Además usa un 30% menos de tokens.
2. **El prompt v2 hace que la abstención sea detectable.** Los dos prompts se abstienen bien cuando no hay respuesta, pero el v1 lo redacta distinto cada vez ("el contexto no incluye...", "no está disponible...") y el v2 usa siempre la frase fija (3/3 contra 0/3). Eso es lo que necesita la UI para mostrar un estado de "sin respuesta". Al principio, el detector de la evaluación contaba mal las abstenciones del v1: tuvimos que ampliarlo dos veces. Es el mismo problema que tendría el front.
3. **El umbral de distancia no sirve como mecanismo principal.** El mejor chunk de las preguntas con respuesta está entre 0.26 y 0.455, y el de las preguntas sin respuesta entre 0.460 y 0.537. La separación es de 0.005: un umbral ajustado a este set sería sobreajuste. Queda desactivado (`max_distance=None`).
4. **Más contexto no mejora y cuesta más.** Con k=8 se gastan 53% más tokens y la latencia sube, sin ganar nada. Con chunks de 1500 pasa lo mismo.
5. **Las métricas de generación están saturadas** (todo da 1.00). Con estas preguntas el modelo responde bien en cualquier configuración razonable, así que hoy **lo único que discrimina es el retrieval (MRR) y el costo**. Para medir mejor la generación hace falta un set más difícil: preguntas que combinen documentos, reformuladas sin las palabras del texto, con números para comparar y más preguntas sin respuesta pero "cercanas" al corpus.

## Configuración elegida (defaults en `app/settings.py`)

| Parámetro | Valor | Por qué |
|---|---|---|
| `chunk_strategy` | `heading` | Mejor MRR y menos tokens |
| `chunk_size` / `chunk_overlap` | 1000 / 150 | Con chunking por títulos casi todas las secciones entran enteras; 500 no mejoró la calidad |
| `top_k` | 5 | k=3 rindió igual en este set, pero el set está saturado. Se deja margen para las preguntas de seguimiento de la Fase 5, que pueden necesitar más contexto |
| `prompt_version` | `v2` | Abstención con frase fija, detectable |
| `max_distance` | `None` | El umbral no separa de forma robusta |

## Otros datos

- Con k=5 se usan ~900-1200 tokens de entrada, lejos del contexto de 4096 que Ollama usa por defecto en esta GPU. Cuando la Fase 5 agregue historial conviene subirlo con `OLLAMA_CONTEXT_LENGTH=8192`, porque Ollama recorta el prompt sin avisar si lo supera.
- PDF: `pypdf` extrae bien el texto por página, pero pierde los saltos de párrafo y los títulos, así que los PDF siempre usan el chunking por párrafos. Descartamos PyMuPDF por su licencia AGPL.
