# Trusted TLS fingerprints

The edge gateway accepts JA3/JA4 only when `ClientIpResolutionMode` is
`TrustedProxy` and the immediate peer matches `TrustedProxies` or
`TrustedCdnProxies`. The capture middleware derives `Verified`; JSON callers do
not establish trust by setting that property.

For direct Envoy termination, use `deploy/envoy-tls-fingerprint/envoy.yaml`.
It enables the TLS inspector and overwrites `X-ASD-TLS-JA3`,
`X-ASD-TLS-JA4`, and `X-ASD-TLS-Source`. Keep the gateway unreachable except
from that Envoy CIDR.

Cloudflare JA3/JA4 require Enterprise Bot Management. They are Workers fields
(`request.cf.botManagement.ja3Hash` and `ja4`), not automatic origin headers. A
Worker must delete visitor-supplied `CF-JA3-Hash`/`CF-JA4` values and copy the
Workers fields into the origin request. The origin must enforce account-scoped
Authenticated Origin Pulls, Cloudflare Tunnel, or validated Cloudflare CIDRs.
If another ingress sits between Cloudflare and the gateway, it must validate
Cloudflare and translate the values into overwritten `X-ASD-TLS-*` headers with
source `cloudflare`.

Split-topology calls and MCP calls use the shared
`DefenseEngine:TlsFingerprints:AttestationKey` (environment variable
`DefenseEngine__TlsFingerprints__AttestationKey`). It must be at least 32 random
characters and match request-guard-mcp's `TLS_FINGERPRINT_ATTESTATION_KEY`.
Tokens use `v1:<unix-seconds>:<HMAC-SHA256>` and bind client IP, method, exact
path, normalized JA3/JA4, and source. The receiver re-derives verification and
rejects stale or context-mismatched tokens.

For rolling rotation, first deploy the new current key and retain the old key
temporarily as `DefenseEngine:TlsFingerprints:PreviousAttestationKey`
(`DefenseEngine__TlsFingerprints__PreviousAttestationKey`) to downstream
consumers, MCP before split escalation. Only after all consumers accept both
should upstream producers switch. Producers sign only with the current key and
consumers accept either. Remove the previous key after all producers have
rolled and at least the configured maximum token lifetime has elapsed.

References: [Envoy TLS inspector](https://www.envoyproxy.io/docs/envoy/latest/api-v3/extensions/filters/listener/tls_inspector/v3/tls_inspector.proto.html),
[Cloudflare Bot Management variables](https://developers.cloudflare.com/bots/reference/bot-management-variables/), and
[Cloudflare Authenticated Origin Pulls](https://developers.cloudflare.com/ssl/origin-configuration/authenticated-origin-pull/).
