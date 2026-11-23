# Runbook: base de datos de pedidos (orders-db)

Documento ficticio para pruebas del sistema RAG.

## Descripción

`orders-db` es un servidor de Azure Database for PostgreSQL (Flexible Server) con alta disponibilidad entre zonas. Lo usan `orders-api` y los procesos nocturnos de facturación. El DBA de guardia es el responsable.

El pool de conexiones de `orders-api` está limitado a 200 conexiones en total.

## Backups y retención

- Los backups automáticos se retienen 35 días.
- Además, cada domingo a las 03:00 se exporta un backup lógico a la cuenta de almacenamiento `stbackupsprod`, que se guarda 1 año.

## Procedimiento: restaurar a un punto en el tiempo

Usar cuando se borraron o corrompieron datos por error.

1. Identificar la hora exacta anterior al problema (en UTC).
2. Crear un servidor nuevo restaurado a ese momento: `az postgres flexible-server restore --resource-group rg-data-prod --name orders-db-restore --source-server orders-db --restore-time "<hora UTC>"`.
3. Validar los datos en el servidor restaurado antes de tocar producción.
4. Copiar solo las tablas afectadas al servidor de producción. Nunca reemplazar el servidor de producción completo sin aprobación del líder de datos.

La restauración no pisa el servidor original: siempre crea uno nuevo.

## Alertas y umbrales

- **Retraso de réplica:** se dispara cuando la réplica de lectura tiene más de 30 segundos de retraso.
- **Almacenamiento:** se dispara al 85% del disco. El almacenamiento crece solo, pero conviene revisar qué tabla creció.
- **Conexiones:** se dispara al llegar a 180 conexiones activas.

## Procedimiento: demasiadas conexiones

1. Ver quién ocupa las conexiones: `SELECT usename, application_name, count(*) FROM pg_stat_activity GROUP BY 1, 2 ORDER BY 3 DESC;`.
2. Si un proceso nocturno quedó colgado, terminarlo con `pg_terminate_backend(pid)`.
3. Si el problema es `orders-api`, revisar si hubo un despliegue reciente que haya cambiado el tamaño del pool.

## Procedimiento: failover manual

El failover automático ocurre solo. Para forzarlo (por ejemplo, ante degradación de la zona primaria):

1. Avisar en `#data-oncall`.
2. Ejecutar `az postgres flexible-server restart --resource-group rg-data-prod --name orders-db --failover Forced`.
3. El corte dura entre 60 y 120 segundos.
