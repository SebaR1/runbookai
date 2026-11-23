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
| Node.js | 22.14.0 |
| Angular CLI | 21.2.31 |
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
- `OLLAMA_HOST`: por ahora queda sin definir, así que Ollama escucha en `127.0.0.1:11434`. En la Fase 1 se prueba el acceso desde los contenedores por `host.docker.internal`.

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
- El contexto por defecto lo calcula Ollama según la VRAM (4096). Para RAG con varios chunks e historial puede hacer falta subirlo, cuidando que el modelo siga 100% en GPU.
- Modelo de embeddings: pendiente para la Fase 1.

## Comandos de verificación

```powershell
ollama ps                      # debe decir 100% GPU
docker run --rm hello-world
docker info --format 'CPUs={{.NCPU}} Mem={{.MemTotal}}'
```
