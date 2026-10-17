import json
import os
import sys
import threading
import unittest
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from aktor import Plugin, ToolError, bundle_directory  # noqa: E402

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))


def make_plugin():
    plugin = Plugin("test")

    @plugin.tool(description="Echo.", side_effects="read_only")
    def echo(args, ctx):
        return {"args": args, "key": ctx.idempotency_key, "config": ctx.config, "credentials": ctx.credentials}

    @plugin.tool()
    def broken(args, ctx):
        raise ToolError("nope", "bad")

    return plugin


class PluginProtocolTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = make_plugin().make_server("127.0.0.1", 0, "secret")
        cls.url = f"http://127.0.0.1:{cls.server.server_address[1]}"
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()

    def post(self, path, body, token="secret"):
        req = urllib.request.Request(self.url + path, data=json.dumps(body).encode(), method="POST",
                                     headers={"Content-Type": "application/json", "Authorization": f"Bearer {token}"})
        try:
            with urllib.request.urlopen(req) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def test_lists_tools_with_their_side_effects(self):
        status, body = self.post("/tools/list", {})
        self.assertEqual(200, status)
        tools = {t["name"]: t for t in body["tools"]}
        self.assertEqual("read_only", tools["echo"]["side_effects"])
        self.assertEqual("non_idempotent", tools["broken"]["side_effects"])

    def test_calls_carry_arguments_config_credentials_and_idempotency_key(self):
        status, body = self.post("/tools/call", {
            "tool": "echo", "arguments": {"x": 1}, "idempotency_key": "agent:call-1",
            "connection": {"name": "t", "config": {"region": "eu"}}, "credentials": {"api_key": "k"}
        })
        self.assertTrue(body["ok"])
        self.assertEqual({"x": 1}, body["result"]["args"])
        self.assertEqual("agent:call-1", body["result"]["key"])
        self.assertEqual({"region": "eu"}, body["result"]["config"])
        self.assertEqual({"api_key": "k"}, body["result"]["credentials"])

    def test_tool_errors_and_unknown_tools_are_clean_failures(self):
        self.assertEqual({"ok": False, "error": "nope", "code": "bad"}, self.post("/tools/call", {"tool": "broken"})[1])
        self.assertEqual("unknown_tool", self.post("/tools/call", {"tool": "missing"})[1]["code"])

    def test_refuses_calls_without_the_token(self):
        self.assertEqual(401, self.post("/tools/list", {}, token="wrong")[0])

    def test_validate_and_notify_defaults(self):
        self.assertTrue(self.post("/validate", {})[1]["ok"])
        self.assertFalse(self.post("/notify", {"text": "hi"})[1]["delivered"])
        self.assertFalse(self.post("/inbound", {"body": "x"})[1]["is_message"])


class BundleTests(unittest.TestCase):
    def test_bundles_the_sample_solution(self):
        bundle = bundle_directory(os.path.join(REPO, "samples", "solutions", "property-mgmt"))
        self.assertIn("name: property-mgmt", bundle["manifest"])
        self.assertIn("workspaces/maintenance-desk.yaml", bundle["files"])
        self.assertIn("skills/maintenance-triage/SKILL.md", bundle["files"])


if __name__ == "__main__":
    unittest.main()
