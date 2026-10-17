"""Remote plugins: an Aktor plugin as a small HTTP service, in Python (docs/plugins.md#remote-plugins).

    from aktor import Plugin, ToolError

    plugin = Plugin("crm")

    @plugin.tool(description="Find a customer by email.", side_effects="read_only",
                 input_schema={"type": "object", "properties": {"email": {"type": "string"}}, "required": ["email"]})
    def find_customer(args, ctx):
        return {"name": "Ada", "plan": "pro"}

    plugin.serve(port=8787, token=os.environ["PLUGIN_TOKEN"])

Add it to a workspace as a "Remote plugin" connection (its URL and the same token). The runtime
calls /tools/list, /tools/call, /validate, /notify and /inbound; this class answers them. Every
call carries the connection's config and credentials (from the vault) in ``ctx``, and tool calls
carry an ``idempotency_key`` to pass on to the system you call, so a repeated call can't act twice.
"""

import hmac
import json
import sys
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class ToolError(Exception):
    """Raise from a tool to report a clean failure to the agent (with an optional code)."""

    def __init__(self, message, code="tool_error"):
        super().__init__(message)
        self.code = code


class Context:
    """What the runtime sent with a call: the connection, its config and credentials, and for tool
    calls the idempotency key, agent and task."""

    def __init__(self, body):
        connection = body.get("connection") or {}
        self.connection_id = connection.get("id")
        self.connection_name = connection.get("name")
        self.workspace_id = connection.get("workspace_id")
        self.config = connection.get("config")
        self.credentials = body.get("credentials")
        self.idempotency_key = body.get("idempotency_key")
        self.agent_id = body.get("agent_id")
        self.task_id = body.get("task_id")


class Plugin:
    def __init__(self, name="plugin"):
        self.name = name
        self._tools = {}
        self._validate = None
        self._notify = None
        self._inbound = None

    # ---- registration ----

    def tool(self, name=None, description=None, input_schema=None, side_effects="non_idempotent"):
        """Registers a tool: ``fn(args, ctx) -> result`` (any JSON value). ``side_effects`` is
        "read_only", "idempotent" or "non_idempotent" (the safe default: never repeated blindly)."""
        if side_effects not in ("read_only", "idempotent", "non_idempotent"):
            raise ValueError("side_effects is read_only, idempotent or non_idempotent")

        def register(fn):
            self._tools[name or fn.__name__] = {
                "fn": fn,
                "description": description or (fn.__doc__ or fn.__name__).strip(),
                "input_schema": input_schema or {"type": "object", "properties": {}},
                "side_effects": side_effects,
            }
            return fn

        return register

    def validate(self, fn):
        """``fn(ctx) -> (ok, message)``: checks a new connection (e.g. its credentials)."""
        self._validate = fn
        return fn

    def notify(self, fn):
        """``fn(notification, ctx) -> None``: delivers a notification (raise to have it retried).
        ``notification`` has delivery_id, workspace, author, text and urgency."""
        self._notify = fn
        return fn

    def inbound(self, fn):
        """``fn(request, ctx) -> dict | None``: turns an inbound request into a message, e.g.
        ``{"sender_id": "+15550100", "text": "approve A1", "message_id": "..."}``; None ignores it."""
        self._inbound = fn
        return fn

    # ---- the protocol ----

    def handle(self, path, body):
        """Answers one call; returns (status, answer). Exposed for tests and other servers."""
        ctx = Context(body)
        if path == "/validate":
            if self._validate is None:
                return 200, {"ok": True, "message": f"{self.name}: {len(self._tools)} tool(s)."}
            ok, message = self._validate(ctx)
            return 200, {"ok": bool(ok), "message": message}
        if path == "/tools/list":
            return 200, {"tools": [
                {"name": n, "description": t["description"], "input_schema": t["input_schema"], "side_effects": t["side_effects"]}
                for n, t in self._tools.items()
            ]}
        if path == "/tools/call":
            tool = self._tools.get(body.get("tool"))
            if tool is None:
                return 200, {"ok": False, "error": f"No tool '{body.get('tool')}'.", "code": "unknown_tool"}
            try:
                return 200, {"ok": True, "result": tool["fn"](body.get("arguments") or {}, ctx)}
            except ToolError as err:
                return 200, {"ok": False, "error": str(err), "code": err.code}
        if path == "/notify":
            if self._notify is None:
                return 200, {"delivered": False, "error": "This plugin doesn't deliver notifications.", "retryable": False}
            try:
                self._notify(body, ctx)
                return 200, {"delivered": True}
            except ToolError as err:
                return 200, {"delivered": False, "error": str(err), "retryable": err.code != "permanent"}
        if path == "/inbound":
            if self._inbound is None:
                return 200, {"is_message": False}
            message = self._inbound(body, ctx)
            if not message:
                return 200, {"is_message": False}
            return 200, {"is_message": True, **message}
        return 404, {"error": f"No such endpoint '{path}'."}

    def serve(self, host="127.0.0.1", port=8787, token=None):
        """Serves the plugin until interrupted. ``token`` is the shared secret the connection was
        given; calls without it are refused."""
        if not token:
            raise ValueError("A shared token is required: the runtime sends it with every call.")
        server = self.make_server(host, port, token)
        print(f"{self.name} plugin on http://{host}:{server.server_address[1]} ({len(self._tools)} tools)", file=sys.stderr)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass
        finally:
            server.server_close()

    def make_server(self, host, port, token):
        plugin = self
        expected = ("Bearer " + token).encode("utf-8")

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, fmt, *args):  # quiet by default
                pass

            def _answer(self, status, body):
                data = json.dumps(body).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def do_POST(self):
                given = (self.headers.get("Authorization") or "").encode("utf-8")
                if not hmac.compare_digest(given, expected):
                    return self._answer(401, {"error": "Wrong or missing token."})
                try:
                    length = int(self.headers.get("Content-Length") or 0)
                    body = json.loads(self.rfile.read(length) or b"{}")
                except ValueError:
                    return self._answer(400, {"error": "The body isn't JSON."})
                try:
                    status, answer = plugin.handle(self.path.split("?", 1)[0].rstrip("/"), body)
                except Exception as err:  # a bug in the plugin: report it, keep serving
                    traceback.print_exc()
                    status, answer = 500, {"error": f"{type(err).__name__}: {err}"}
                self._answer(status, answer)

        return ThreadingHTTPServer((host, port), Handler)
