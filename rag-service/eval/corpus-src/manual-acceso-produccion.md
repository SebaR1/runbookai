# Manual de acceso a producción

Documento ficticio para pruebas del sistema RAG.

## Principios

Nadie tiene acceso permanente a producción. El acceso es temporal, se pide cuando hace falta y queda auditado. Se usa Microsoft Entra Privileged Identity Management (PIM).

## Requisitos previos

- Tener MFA configurado en la cuenta corporativa.
- Estar conectado a la VPN corporativa (perfil "Prod-Access").
- Haber completado el curso de seguridad del año en curso.

## Cómo pedir acceso

1. Entrar al portal de Azure, a la sección "Privileged Identity Management".
2. Elegir el rol que se necesita, por ejemplo "AKS Prod Operator" o "Database Prod Reader".
3. Indicar el motivo y el número de ticket o incidente.
4. Elegir la duración. El máximo es de 4 horas.

Los roles de lectura se aprueban automáticamente. Los roles de escritura requieren la aprobación de otra persona del equipo, salvo durante un incidente Sev1, en el que la guardia primaria puede autoaprobarse.

## Acceso de emergencia (break-glass)

Si PIM no funciona durante un incidente Sev1, existen dos cuentas de emergencia cuyas credenciales están en una caja fuerte física. Usarlas dispara una alerta al equipo de Seguridad, y hay que justificar su uso en el postmortem.

## Auditoría

Todas las activaciones de roles quedan registradas y se revisan una vez por mes. Quien active un rol sin un motivo válido pierde el acceso a PIM hasta revisar el caso con Seguridad.
