# Runbook: servicio de autenticación (auth-api)

Documento ficticio para pruebas del sistema RAG.

## Descripción del servicio

`auth-api` emite y valida los tokens de acceso (JWT) de todas las aplicaciones. Corre en el clúster `aks-prod-eastus2`, en el namespace de Kubernetes `identity`, con 3 réplicas como mínimo. Guarda las sesiones en Redis (`redis-sessions`) y lee la clave de firma desde Azure Key Vault.

El equipo dueño es **Equipo Identidad**. El canal de soporte es `#identity-oncall`.

Los tokens de acceso duran 15 minutos y los refresh tokens duran 7 días.

## Alertas y umbrales

- **Latencia de login:** se dispara cuando el p95 de `/login` supera los 500 ms durante 5 minutos.
- **Fallos de login:** se dispara cuando más del 5% de los intentos fallan durante 10 minutos. Puede indicar un ataque de fuerza bruta.
- **Expiración de la clave de firma:** aviso cuando faltan 14 días para que venza la clave JWT.

## Procedimiento: reinicio del servicio

1. Confirmar el contexto: `kubectl config current-context` debe mostrar `aks-prod-eastus2`.
2. Avisar en `#identity-oncall`.
3. Ejecutar `kubectl rollout restart deployment/auth-api -n identity`.
4. Verificar que el p95 de `/login` vuelva a estar por debajo de 500 ms.

Reiniciar `auth-api` no cierra las sesiones de los usuarios, porque viven en Redis.

## Procedimiento: rotar la clave de firma JWT

La clave se rota cada 90 días o de inmediato si hay sospecha de filtración.

1. Generar una nueva versión del secreto `jwt-signing-key` en el Key Vault `kv-identity-prod`.
2. Esperar 10 minutos: `auth-api` recarga la clave automáticamente y durante ese tiempo acepta tokens firmados con las dos versiones.
3. Deshabilitar la versión anterior del secreto recién después de 24 horas, para no invalidar tokens en uso.

## Procedimiento: sospecha de fuerza bruta

1. Revisar en Log Analytics las IPs con más fallos de login en la última hora.
2. Bloquear las IPs sospechosas en la política de WAF de Azure Front Door (`waf-prod`).
3. Si el ataque apunta a cuentas concretas, forzar el reseteo de contraseña de esas cuentas.

## Escalamiento

- **Primer nivel:** guardia de Equipo Identidad (PagerDuty, servicio "Identity Primary").
- **Segundo nivel:** equipo de Seguridad, ante cualquier sospecha de compromiso de credenciales.
