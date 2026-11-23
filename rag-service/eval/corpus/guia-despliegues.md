# Guía de despliegues a producción

Documento ficticio para pruebas del sistema RAG.

## Reglas generales

- Todo cambio a producción pasa por un pull request con al menos 2 aprobaciones.
- Hay congelamiento de despliegues los viernes después de las 15:00 y durante todo el fin de semana. Solo se permiten hotfixes con aprobación del líder técnico.
- En diciembre rige un congelamiento total entre el 20 y el 31, salvo incidentes Sev1.

## Despliegue canary

Los servicios con tráfico de clientes se despliegan en modo canary con Helm y Flagger:

1. La versión nueva recibe primero el 10% del tráfico durante 30 minutos.
2. Si la tasa de errores se mantiene por debajo del 1% y el p95 no empeora más de un 20%, pasa al 50% durante 15 minutos.
3. Después pasa al 100%.

Flagger revierte solo si alguna de esas métricas se sale del límite.

## Rollback manual

Si un despliegue ya llegó al 100% y hay problemas:

1. Ver el historial: `helm history <release> -n <namespace>`.
2. Volver a la revisión anterior: `helm rollback <release> <revisión> -n <namespace>`.
3. Avisar en `#deploys` con el motivo del rollback.

Un rollback no revierte migraciones de base de datos. Por eso las migraciones tienen que ser compatibles hacia atrás: primero se agrega la columna nueva, y se borra la vieja recién en un despliegue posterior.

## Ventana y comunicación

- Los despliegues de servicios críticos (pagos, autenticación, pedidos) se hacen de lunes a jueves entre las 10:00 y las 16:00.
- Antes de empezar se avisa en `#deploys` con el link al pull request.
