# Kubernetes split runtime with Raft consensus

`split-consensus.yaml` is a production-oriented starting point for three edge nodes, two escalation runtimes, two public tarpit runtimes, persistent Redis/PostgreSQL, and a durable Raft write-ahead log on a per-edge PVC.

Before applying it:

1. Build and publish the Dockerfile's `edge`, `escalation`, and `tarpit` targets as three images, then replace the `ghcr.io/your-org/...` image names.
2. Replace every value in `defense-secrets`; a secret manager or sealed-secret controller is preferred over committing real values.
3. Replace `https://tarpit.example.com` with the public tarpit origin. Ordinary application traffic stays on the edge origin; only suspicious requests receive a `307` redirect to this origin.
4. Choose a storage class and size the edge PVCs for the Raft WAL. Do not share or copy WAL directories between pods.
5. If Cloudflare fronts either public service, configure `TrustedProxy` mode with only the actual proxy/CDN ranges, enable the Cloudflare integration, and forward `CF-Connecting-IP`. A missing or malformed origin header resolves to `unknown`; the Cloudflare edge address is never used as a block target.

Apply with `kubectl apply -f deploy/kubernetes/split-consensus.yaml`. Wait until all three edge pods report a leader through the authenticated `/defense/consensus/status` endpoint. The cluster remains available for writes with one failed edge; two failed edges remove quorum and block/unblock mutations fail visibly.

Raft transport is intended only for the private cluster network. The included NetworkPolicy admits TCP/3262 only from other edge pods. If the cluster CNI does not enforce NetworkPolicy, enforce the same rule with the platform firewall before enabling consensus.

Membership is deliberately static in this release. Change membership with a documented migration, one member at a time, while quorum is healthy; changing `Consensus:Members` against an existing PVC fails startup instead of silently replacing persisted membership.
