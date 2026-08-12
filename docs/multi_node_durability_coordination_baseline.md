# Multi-node durability and Raft coordination

Block and unblock mutations can be protected by a durable Raft quorum. This is distinct from `PeerSync`: peer sync shares bounded external signals with eventual consistency, while Raft serializes authoritative local blocklist mutations.

## Guarantees

- Commands are acknowledged only after a majority commits them.
- Followers forward mutations to the elected leader through the authenticated internal command endpoint.
- Every node keeps a write-through, integrity-checked WAL on its own persistent volume.
- The materialized Redis mutation is applied from the committed log on each node.
- Command IDs make forwarding retries idempotent during normal operation and replay.
- Block commands store an absolute expiration; replay applies only the remaining TTL and never resurrects an expired historical block.
- A node fails startup if its persisted membership differs from configuration. It does not discard or silently replace cluster history.
- `/health` is not ready without a leader, and `/defense/consensus/status` reports the local endpoint, leader, term, and member count.

With three voting nodes, one node may fail while writes continue. With fewer than two available nodes, block/unblock writes fail visibly. Reads of the Redis materialized view can continue, but operators should treat the cluster as degraded.

## Configuration

Each member uses the same ordered-independent member set and its own advertised host:

```text
DefenseEngine__Consensus__Enabled=true
DefenseEngine__Consensus__ListenAddress=0.0.0.0
DefenseEngine__Consensus__AdvertisedHost=edge-gateway-0.edge-gateway-headless
DefenseEngine__Consensus__Port=3262
DefenseEngine__Consensus__StoragePath=/app/data/raft
DefenseEngine__Consensus__SharedSecret=<random value of at least 32 characters>
DefenseEngine__Consensus__Members__0__RaftEndpoint=edge-gateway-0.edge-gateway-headless:3262
DefenseEngine__Consensus__Members__0__ApiBaseUrl=http://edge-gateway-0.edge-gateway-headless:8080
```

Configure either one node for development or an odd production quorum of at least three nodes. Every advertised endpoint must appear exactly once in `Members`.

## Security and operations

Raft TCP/3262 has no public listener requirement. Restrict it to edge members using a NetworkPolicy/security group; the supplied Kubernetes manifest does so. Follower-to-leader HTTP uses a constant-time bearer-secret comparison. Rotate that secret during a controlled rollout while writes are quiesced.

Membership is static in this release. Do not edit the member list against an existing WAL PVC. Back up each node's persistent volume independently, monitor WAL growth, and alert on loss of leader or quorum. The implementation retains the full command log so a node can reconstruct state without accepting an incomplete snapshot.

Redis AOF and durable relational audit storage remain required. Raft protects mutation ordering and recovery; it does not turn a single Redis or PostgreSQL instance into a highly available service. Use managed/clustered data stores for production availability.

## Recovery checks

1. Confirm the same member set and unique advertised host on every pod.
2. Restore each WAL only to its original node identity.
3. Bring up a majority and wait for `/health` to report a leader.
4. Verify an active block retains its original expiration after restart.
5. Verify an expired block is not recreated during replay.
6. Verify mutations submitted to a follower are committed and visible through another node.
