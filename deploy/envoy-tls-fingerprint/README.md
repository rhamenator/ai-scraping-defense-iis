# Trusted TLS fingerprint collector

This sample terminates direct client TLS in Envoy, computes JA3 and JA4 from
the ClientHello, overwrites any client-supplied fingerprint headers, and sends
the validated values to the defense origin.

- Mount the certificate and key at `/etc/envoy/tls/tls.crt` and
  `/etc/envoy/tls/tls.key`.
- Resolve `defense-origin` to the protected service or change that cluster
  address.
- Configure the origin to trust only the Envoy address/CIDR as a proxy.
- Do not expose the origin directly, because direct clients must not be able to
  assert `X-ASD-TLS-JA3` or `X-ASD-TLS-JA4`.

Do not place this direct-termination sample unchanged behind Cloudflare; it
would fingerprint Cloudflare's connection. Cloudflare does not automatically
send JA3/JA4 origin headers. Follow `docs/tls_fingerprint_attestation.md` for
the Worker and origin-authentication boundary.
