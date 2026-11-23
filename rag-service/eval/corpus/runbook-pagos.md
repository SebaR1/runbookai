# Runbook: servicio de pagos (payments-api)

Documento ficticio para pruebas del sistema RAG.

## Descripción del servicio

`payments-api` procesa los cobros con tarjeta de la tienda online. Corre en el clúster de AKS `aks-prod-eastus2`, en el namespace de Kubernetes `payments`, con 4 réplicas como mínimo. Depende de la base de datos `payments-db` (Azure Database for PostgreSQL) y de la cola `payments-events` en Azure Service Bus.

El equipo dueño es **Equipo Checkout**. El canal de soporte es `#checkout-oncall`.

## Alertas y umbrales

- **Latencia alta:** se dispara cuando el p95 supera los 800 ms durante 5 minutos seguidos.
- **Tasa de errores:** se dispara cuando más del 2% de las respuestas son 5xx durante 10 minutos.
- **Cola atrasada:** se dispara cuando `payments-events` tiene más de 5.000 mensajes pendientes.

Una alerta de latencia sola suele indicar un problema con la base de datos. Si coincide con la alerta de cola atrasada, lo más probable es que el consumidor de eventos esté caído.

## Procedimiento: reinicio controlado del servicio

Usar este procedimiento cuando los pods responden pero la latencia sigue alta después de descartar problemas en la base de datos.

1. Confirmar el contexto del clúster: `kubectl config current-context` debe mostrar `aks-prod-eastus2`.
2. Verificar el estado de los pods: `kubectl get pods -n payments -l app=payments-api`.
3. Avisar en `#checkout-oncall` que se va a reiniciar el servicio.
4. Ejecutar el reinicio progresivo: `kubectl rollout restart deployment/payments-api -n payments`.
5. Seguir el avance con `kubectl rollout status deployment/payments-api -n payments --timeout=5m`.
6. Verificar en el dashboard de Grafana "Payments Overview" que el p95 baje de 800 ms en los 10 minutos siguientes.

Nunca usar `kubectl delete pods` para todos los pods a la vez: deja el servicio sin réplicas disponibles y corta los cobros en curso.

## Procedimiento: la base de datos no responde

1. Revisar en el portal de Azure el estado del servidor `payments-db`, en la sección "Resource health".
2. Si el uso de CPU supera el 90%, buscar consultas lentas con `pg_stat_activity`.
3. Si hay conexiones bloqueadas por más de 15 minutos, se pueden terminar con `pg_terminate_backend(pid)`, siempre con el visto bueno del DBA de guardia.
4. Si el servidor no responde en absoluto, escalar de inmediato al DBA de guardia por PagerDuty (servicio "DBA Primary").

## Procedimiento: cola de eventos atrasada

1. Verificar que el deployment `payments-consumer` tenga réplicas listas: `kubectl get deploy payments-consumer -n payments`.
2. Si tiene 0 réplicas listas, revisar los logs con `kubectl logs deploy/payments-consumer -n payments --tail=200`.
3. Si el atraso supera los 20.000 mensajes, escalar el consumidor a 6 réplicas: `kubectl scale deploy/payments-consumer -n payments --replicas=6`.
4. Volver a 2 réplicas cuando la cola baje de 1.000 mensajes.

## Escalamiento

- **Primer nivel:** guardia de Equipo Checkout (rotación semanal en PagerDuty, servicio "Checkout Primary").
- **Segundo nivel:** líder técnico de Checkout, si el incidente dura más de 30 minutos.
- **Incidente mayor:** si los cobros fallan para más del 10% de los clientes, declarar un incidente Sev1 en `#incidents` y convocar al incident commander.

## Después del incidente

Registrar el incidente en el sistema de tickets dentro de las 24 horas, con la línea de tiempo, la causa raíz si se conoce y las acciones tomadas. Los incidentes Sev1 y Sev2 requieren un postmortem sin culpables dentro de los 5 días hábiles.
