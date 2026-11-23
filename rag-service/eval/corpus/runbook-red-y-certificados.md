# Runbook: red, DNS y certificados

Documento ficticio para pruebas del sistema RAG.

## Arquitectura

El tráfico público entra por Azure Front Door (`afd-prod`), que termina TLS y aplica el WAF. Detrás, cada clúster de AKS expone sus servicios con un Application Gateway. Los dominios públicos están en Azure DNS, en la zona `tienda.example.com`.

## Procedimiento: el sitio no resuelve

1. Probar la resolución desde fuera de la red corporativa: `nslookup www.tienda.example.com 8.8.8.8`.
2. Si no resuelve, revisar en Azure DNS que el registro CNAME apunte a `afd-prod.azurefd.net`.
3. Si resuelve pero no responde, revisar el estado de los orígenes en Front Door (sección "Origin groups").
4. Los cambios de DNS tardan hasta el TTL del registro en propagarse. El TTL de los registros públicos es de 300 segundos.

## Certificados

Los certificados TLS públicos los gestiona Front Door y se renuevan solos. Los certificados internos (mTLS entre servicios) se guardan en el Key Vault `kv-platform-prod` y se renuevan con cert-manager.

- **Alerta de vencimiento:** se dispara cuando a un certificado le quedan menos de 21 días.
- Si cert-manager no renovó a tiempo, revisar los eventos con `kubectl describe certificate <nombre> -n <namespace>`.
- Para forzar la renovación: `cmctl renew <nombre> -n <namespace>`.

## Procedimiento: latencia alta en todos los servicios a la vez

Si la latencia sube en todos los servicios al mismo tiempo, probablemente el problema no es de un servicio sino de red.

1. Revisar el dashboard "Front Door Health" en Azure Monitor.
2. Comparar la latencia desde distintas regiones con las pruebas sintéticas de Application Insights.
3. Si el problema está en una región de Azure, consultar Azure Status y considerar desviar el tráfico al clúster secundario (`aks-prod-westus2`) cambiando el peso del origen en Front Door.

## Escalamiento

El equipo dueño es **Equipo Plataforma** (`#platform-oncall`, PagerDuty "Platform Primary").
