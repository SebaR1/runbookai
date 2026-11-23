# Runbook: cache cluster (redis-cache)

Fictional document for RAG system testing.

## Overview

`redis-cache` is an Azure Cache for Redis instance (Premium tier, 2 shards) used by `catalog-api` to cache product pages. Losing the cache does not lose data, but the catalog database receives the full load and becomes slow.

Owner: **Catalog Team** (`#catalog-oncall`).

## Alerts and thresholds

- **Memory usage:** fires when used memory exceeds 85% for 15 minutes.
- **Evictions:** fires when there are more than 1,000 evicted keys per minute.
- **Server load:** fires above 80% for 10 minutes.

The eviction policy is `allkeys-lru`: when memory is full, the least recently used keys are removed.

## Procedure: high memory usage

1. Check the biggest keys with `redis-cli --bigkeys` from the jump host.
2. If a single key pattern grew unexpectedly, check the latest `catalog-api` deployment.
3. If memory keeps growing, scale up to the next Premium size in the Azure portal. Scaling is online but takes about 20 minutes.

## Procedure: reboot or failover

1. Announce it in `#catalog-oncall`.
2. Reboot the primary node only: `az redis force-reboot --name redis-cache --resource-group rg-cache-prod --reboot-type PrimaryNode`.
3. Expect a cache miss spike for about 10 minutes.

Never run `FLUSHALL` in production: it empties the whole cache and the catalog database cannot handle the resulting load.
