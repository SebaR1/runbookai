# Política de guardias (on-call)

Documento ficticio para pruebas del sistema RAG.

## Rotación

Cada equipo dueño de un servicio crítico tiene una guardia primaria y una secundaria. La rotación es semanal y el traspaso se hace los lunes a las 10:00, en una reunión de 15 minutos donde la guardia saliente cuenta los incidentes abiertos.

Nadie puede hacer más de 2 semanas de guardia primaria por mes.

## Tiempos de respuesta

Los tiempos se miden desde que PagerDuty envía la alerta hasta que alguien la reconoce.

- Sev1: reconocer en 15 minutos.
- Sev2: reconocer en 30 minutos.
- Sev3: se atiende en horario laboral, al día hábil siguiente.

Si la guardia primaria no reconoce en ese tiempo, PagerDuty escala automáticamente a la guardia secundaria, y 15 minutos después al líder del equipo.

## Niveles de severidad

- Sev1: el servicio está caído o afecta a más del 10% de los clientes.
- Sev2: hay una degradación importante, pero existe una alternativa.
- Sev3: hay un problema menor, sin impacto visible para los clientes.

## Compensación

Cada semana de guardia primaria se compensa con 1 día libre, que se puede tomar dentro de los 30 días siguientes. Las horas trabajadas de noche por un incidente (entre las 22:00 y las 07:00) se compensan además al doble.

## Herramientas

Durante la guardia hay que tener la app de PagerDuty instalada, acceso a la VPN y una laptop con batería cargada. El acceso a producción se pide con PIM, como se explica en el manual de acceso.
