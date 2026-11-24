# Entorno de desarrollo

Verificado en la Fase 0 (2026-11-23).

## Hardware

- AMD Ryzen 7 5700X (8 núcleos / 16 hilos), 16 GB de RAM.
- AMD Radeon RX 7600, 8 GB de VRAM (gfx1102).
- `C:` es el SSD M.2 de 954 GB. Ahí van el repo, el disco de Docker y los modelos de Ollama. El HDD (`E:`) no se usa.

## Herramientas

| Herramienta | Versión |
|---|---|
| Driver AMD Adrenalin | 32.0.32029.2010 |
| Ollama (nativo en Windows) | 0.40.17 |
| Docker Desktop (backend WSL2) | Docker 29.9.2 |
| WSL | 3.0.5, kernel 6.18 |
| .NET SDK | 10.0.102 (LTS) |
| Node.js | 26.13.1 (LTS; actualizado en la Fase 6, Angular 22 pide 22.22.3+, 24.15+ o 26+) |
| Angular CLI | 21.2.31 global; el proyecto usa su propio CLI 22.3.0 (`npx ng`) |
| Python | 3.13.2 |
| Git | 2.47.1 |

## Configuración del sistema (fuera del repo)

**`%USERPROFILE%\.wslconfig`**

```ini
[wsl2]
memory=7GB
processors=6
swap=4GB
```

Pone un tope a la VM de WSL2, que es donde corre Docker, para dejar RAM libre a Windows, Ollama y el IDE. Se aplica con `wsl --shutdown`. Para verificarlo: `docker info` muestra 6 CPUs y unos 6,8 GiB.

**Variables de entorno de usuario**

- `OLLAMA_MODELS=C:\ollama\models`. Tiene que estar definida *antes* de que arranque Ollama. Si no, los modelos se guardan en `%USERPROFILE%\.ollama\models`.
- `OLLAMA_HOST`: **sin definir a propósito**, así que Ollama escucha solo en `127.0.0.1:11434`. En la Fase 1 se verificó que Docker Desktop reenvía `host.docker.internal` al loopback del host, y los contenedores llegan igual. No hace falta exponer Ollama a la red ni abrir el firewall.

## Ollama y GPU

Ollama detecta la GPU con ROCm (en `%LOCALAPPDATA%\Ollama\server.log`):

```
inference compute library=ROCm compute=gfx1102 description="AMD Radeon RX 7600" total="8.0 GiB"
```

Modelo LLM de prueba: **`qwen3:8b`** (Q4, 5,2 GB en disco y 5,6 GB cargado con contexto de 4096).

| Métrica | Valor |
|---|---|
| `ollama ps` | 100% GPU |
| Generación | ~47 tokens/s |
| Prompt eval | ~130 tokens/s |
| Carga en frío | ~9 s |

Notas:

- Qwen3 tiene modo de razonamiento ("thinking"). Para RAG conviene apagarlo (`--think=false` en la CLI, `think: false` en la API nativa) o controlarlo desde el prompt. Se define en la Fase 2.
- **Contexto: `OLLAMA_CONTEXT_LENGTH=8192`** (variable de usuario). Por defecto, Ollama elegía 4096 según la VRAM y, si el prompt lo supera, **lo recorta sin avisar**. Con 8192, `qwen3:8b` ocupa 6,2 GB y `bge-m3` 0,7 GB, y los dos quedan 100% en GPU (verificado con `ollama ps`, sin desalojos durante la evaluación completa).
  - Si se carga `qwen3:8b` primero, Ollama puede estimar que no entra y desalojar `bge-m3` (`"evicting"` en `server.log`). Con el uso normal, que alterna embeddings y chat, conviven sin problema.
  - Margen: quedan ~1 GB de VRAM libres. Un LLM más grande o más contexto ya no entraría completo en la GPU.

## Modelo de embeddings (Fase 1)

**`bge-m3`** (1,2 GB en disco), multilingüe.

| Dato | Valor (medido por `/v1/embeddings`) |
|---|---|
| Dimensión | **1024** |
| Normalización | vectores unitarios (norma 1.0), así que conviene usar distancia coseno en Chroma |
| Coseno entre la misma pregunta en ES y en EN | 0.924 |

Con `qwen3:8b` y `bge-m3` cargados a la vez quedan los dos **100% en GPU** (5,6 GB + 0,7 GB).

## Infraestructura (Docker Compose, Fase 1)

| Servicio | Imagen | Host → contenedor | Healthcheck |
|---|---|---|---|
| mongo | `mongo:8.0.38` | `127.0.0.1:27017` | `mongosh` + `ping` |
| azurite | `mcr.microsoft.com/azure-storage/azurite:3.38.0` | `127.0.0.1:10000-10002` | `nc -z` al puerto Blob |
| chroma | `chromadb/chroma:1.5.9` | `127.0.0.1:8001` → 8000 | GET `/api/v2/heartbeat` por `/dev/tcp` (la imagen no trae curl) |

- Los puertos se publican solo en `127.0.0.1`, así que Mongo y compañía no quedan accesibles desde la red local.
- Mongo usa usuario y contraseña tomados de `.env`. Sin credenciales, los comandos fallan con `requires authentication`.
- Las versiones están fijas. Chroma en particular cambia de API entre versiones (hoy es la v2).
- Para borrar todos los datos locales: `docker compose down -v`.

## Comandos de verificación

```powershell
ollama ps                      # debe decir 100% GPU
docker run --rm hello-world
docker info --format 'CPUs={{.NCPU}} Mem={{.MemTotal}}'
docker compose ps              # los cinco servicios deben estar healthy
docker run --rm curlimages/curl -s http://host.docker.internal:11434/v1/models
```

## Build de las imágenes (Compose v5 + Bake)

Compose v5 construye las imágenes con Bake (buildx), que solo deja leer archivos dentro del directorio desde el que se ejecuta. Compara las rutas como texto, así que `c:\dev\RunbookAI` (con minúscula, como queda en Git Bash o en algunas terminales de VS Code) no coincide con `C:\dev\RunbookAI`, y el build falla con:

```
additional privileges requested: pass "--allow=fs.read=rag-service\Dockerfile" to grant requested privileges
```

Solución: ejecutar `docker compose up -d --build` desde una terminal ubicada en `C:\dev\RunbookAI` (con `C:` en mayúscula), por ejemplo después de `Set-Location C:\dev\RunbookAI`. No hace falta desactivar el chequeo.
