"""Guards the security properties of deploy/kubernetes/split-consensus.yaml (#185, #186)."""

import re
import unittest
from pathlib import Path

MANIFEST = Path(__file__).resolve().parents[2] / "deploy" / "kubernetes" / "split-consensus.yaml"


def documents():
    return [d for d in re.split(r"(?m)^---\s*$", MANIFEST.read_text()) if d.strip()]


def find(kind, name):
    for doc in documents():
        if re.search(rf"(?m)^kind: {kind}\s*$", doc) and re.search(rf"(?m)^  name: {re.escape(name)}\s*$", doc):
            return doc
    raise AssertionError(f"{kind}/{name} not found in the manifest")


class SplitConsensusManifest(unittest.TestCase):
    def test_redis_requires_a_password_and_every_client_sends_it(self):
        text = MANIFEST.read_text()
        self.assertIn("--requirepass", find("StatefulSet", "redis"))
        self.assertIn("redis-password:", find("Secret", "defense-secrets"))
        connection_strings = re.findall(r"DefenseEngine__Redis__ConnectionString\s*\n\s+value: (\S+)", text)
        self.assertEqual(3, len(connection_strings), "edge, escalation and tarpit all use Redis")
        for value in connection_strings:
            self.assertIn("password=$(REDIS_PASSWORD)", value)

    def test_redis_is_reachable_only_from_the_defense_pods(self):
        policy = find("NetworkPolicy", "redis-only-from-defense-pods")
        self.assertRegex(policy, r"(?m)^\s+app: redis\s*$")
        self.assertRegex(policy, r"values: \[edge-gateway, escalation-engine, tarpit-api\]")
        self.assertRegex(policy, r"port: 6379")

    def test_the_edge_is_not_exposed_in_cleartext(self):
        service = find("Service", "edge-gateway-public")
        self.assertIn("type: ClusterIP", service)
        self.assertNotIn("LoadBalancer", service)
        self.assertNotIn("LoadBalancer", find("Service", "edge-gateway-headless"))
        for ingress in ("edge-gateway-public", "edge-gateway-management"):
            doc = find("Ingress", ingress)
            self.assertIn("tls:", doc)
            self.assertIn('force-ssl-redirect: "true"', doc)

    def test_edge_pods_start_in_parallel_so_the_raft_quorum_can_form(self):
        doc = find("StatefulSet", "edge-gateway")
        self.assertRegex(doc, r"(?m)^  podManagementPolicy: Parallel\s*$")

    def test_management_routes_are_limited_to_an_allowlist(self):
        doc = find("Ingress", "edge-gateway-management")
        self.assertIn("path: /defense", doc)
        self.assertIn("whitelist-source-range", doc)
        self.assertNotIn("path: /\n", doc)


if __name__ == "__main__":
    unittest.main()
