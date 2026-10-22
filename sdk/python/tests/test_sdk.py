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


class AktorServerTests(unittest.TestCase):
    """The backend client against a fake apps API: the app key, idempotent run starts and waiting."""

    def setUp(self):
        import http.server

        self.requests = []
        runs = {"polls": 0}
        test = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def _answer(self, status, body):
                data = json.dumps(body).encode()
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(data)

            def do_POST(self):
                length = int(self.headers.get("Content-Length") or 0)
                test.requests.append(("POST", self.path, dict(self.headers), json.loads(self.rfile.read(length) or b"null")))
                if self.path.endswith("/runs"):
                    self._answer(202, {"run_id": "run-1", "workflow": "triage", "number": 1, "status": "started"})
                else:
                    self._answer(200, {"token": "aeu_x"})

            def do_GET(self):
                test.requests.append(("GET", self.path, dict(self.headers), None))
                runs["polls"] += 1
                self._answer(200, {"run_id": "run-1", "done": runs["polls"] > 1, "status": "Completed" if runs["polls"] > 1 else "Running"})

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.base = f"http://127.0.0.1:{self.server.server_address[1]}"

    def tearDown(self):
        self.server.shutdown()

    def test_issues_tokens_and_runs_workflows_with_the_app_key(self):
        from aktor import AktorServer, AktorError

        server = AktorServer("sol-1", "aks_0123456789ab_secretsecretsecret", base_url=self.base)
        self.assertEqual(server.issue_token("u1", name="Ada")["token"], "aeu_x")
        run = server.run("triage", {"subject": "Leak"})
        self.assertEqual(run["status"], "Completed")

        method, path, headers, body = self.requests[1]
        self.assertEqual(path, "/api/v1/apps/sol-1/runs")
        self.assertEqual(headers["Authorization"], "Bearer aks_0123456789ab_secretsecretsecret")
        self.assertTrue(headers.get("Idempotency-Key"))
        self.assertEqual(body, {"workflow": "triage", "data": {"subject": "Leak"}})
        self.assertIn("wait=", self.requests[2][1])
        with self.assertRaises(AktorError):
            AktorServer("sol-1", "ak_org_key")
