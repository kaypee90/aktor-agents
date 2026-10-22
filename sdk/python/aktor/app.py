import time
import urllib.parse
import uuid

from ._http import AktorError, request


class AktorApp:
    """The end-user side of a solution: what your customers' app calls, with an end-user token
    your backend got from ``Aktor.solutions.end_user_token``. It reaches that user's own
    conversations and records only.

        app = AktorApp(install_id, token)
        c = app.start("My sink is leaking")
        print(app.wait_for_reply(c["conversation_id"])["messages"][-1]["text"])
    """

    def __init__(self, install_id, token, base_url="http://localhost:5080", timeout=60):
        self.install_id = install_id
        self.token = token
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

    def _call(self, method, path, body=None, query=None):
        return request(self.base_url, method, f"/api/v1/apps/{_q(self.install_id)}{path}",
                       self.token, body, query, timeout=self.timeout)

    def describe(self):
        return self._call("GET", "")

    def conversations(self):
        return self._call("GET", "/conversations")

    def start(self, message):
        return self._call("POST", "/conversations", {"message": message})

    def get(self, conversation_id):
        return self._call("GET", f"/conversations/{_q(conversation_id)}")

    def send(self, conversation_id, message):
        return self._call("POST", f"/conversations/{_q(conversation_id)}/messages", {"message": message})

    def wait_for_reply(self, conversation_id, timeout_seconds=120, interval=1.0):
        deadline = time.time() + timeout_seconds
        while True:
            conversation = self.get(conversation_id)
            if not conversation.get("answering"):
                return conversation
            if time.time() > deadline:
                raise AktorError("Timed out waiting for the reply", 408)
            time.sleep(interval)

    def records(self, entity, limit=None, offset=None):
        return self._call("GET", f"/records/{_q(entity)}", query={"limit": limit, "offset": offset})


class AktorServer:
    """For your backend: one installed solution, reached with its app key (``aks_…``, from the
    solution's Integrate tab). It issues tokens for your signed-in users, reads and writes the
    solution's records, and runs its workflows. An app key reaches nothing else; keep it in a
    server-side secret, never in a web page.

        server = AktorServer(install_id, os.environ["AKTOR_APP_KEY"])
        token = server.issue_token("customer-42", name="Ada")["token"]
        run = server.run("triage", {"subject": "Leak under the sink"})
        print(run["status"], run["summary"])
    """

    def __init__(self, install_id, app_key, base_url="http://localhost:5080", timeout=90, retries=2):
        if not str(app_key or "").startswith("aks_"):
            raise AktorError("app_key should be a solution app key (aks_…), from the solution's Integrate tab.", 0)
        self.install_id = install_id
        self.app_key = app_key
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout
        self.retries = retries

    def _call(self, method, path, body=None, query=None, headers=None):
        # Reads, and writes with an idempotency key, are safe to repeat.
        safe = method == "GET" or "Idempotency-Key" in (headers or {})
        return request(self.base_url, method, f"/api/v1/apps/{_q(self.install_id)}{path}", self.app_key, body, query,
                       headers=headers, retries=self.retries if safe else 0, timeout=self.timeout)

    def describe(self):
        """What the key can use: the solution's workflows, entities and assistant."""
        return self._call("GET", "")

    def issue_token(self, user_id, name=None, ttl_minutes=None):
        """A short-lived token for one of your users; return ``{"token": …}`` from your token endpoint."""
        return self._call("POST", "/end-user-tokens", {"user_id": user_id, "name": name, "ttl_minutes": ttl_minutes})

    def list_records(self, entity, where=None, filter=None, order_by=None, desc=None, limit=None, offset=None, end_user=None):
        query = {f"where.{k}": v for k, v in (where or {}).items()}
        query.update({"filter": filter, "order_by": order_by, "desc": desc, "limit": limit, "offset": offset, "end_user": end_user})
        return self._call("GET", f"/records/{_q(entity)}", query=query)

    def get_record(self, entity, record_id):
        return self._call("GET", f"/records/{_q(entity)}/{_q(record_id)}")

    def create_record(self, entity, fields, idempotency_key=None):
        return self._call("POST", f"/records/{_q(entity)}", fields, headers={"Idempotency-Key": idempotency_key} if idempotency_key else None)

    def update_record(self, entity, record_id, fields, if_match=None):
        return self._call("PATCH", f"/records/{_q(entity)}/{_q(record_id)}", fields, headers={"If-Match": str(if_match)} if if_match is not None else None)

    def delete_record(self, entity, record_id):
        return self._call("DELETE", f"/records/{_q(entity)}/{_q(record_id)}")

    def start_run(self, workflow, input, data=None, idempotency_key=None):
        """Starts a workflow run. ``input`` is text, or a dict the stages read as ``input``."""
        if not isinstance(input, str) and data is not None:
            raise AktorError("Pass the run's data either as the input dict or as data= with a text input, not both.", 0)
        body = {"workflow": workflow, "input": input, "data": data} if isinstance(input, str) else {"workflow": workflow, "data": input}
        return self._call("POST", "/runs", body, headers={"Idempotency-Key": idempotency_key or uuid.uuid4().hex})

    def get_run(self, run_id, wait_seconds=None):
        return self._call("GET", f"/runs/{_q(run_id)}", query={"wait": wait_seconds})

    def run(self, workflow, input, data=None, idempotency_key=None, timeout_seconds=300):
        """Runs a workflow and waits for its result (status, summary and each stage's output)."""
        run_id = self.start_run(workflow, input, data, idempotency_key)["run_id"]
        deadline = time.time() + timeout_seconds
        while True:
            left = int(deadline - time.time())
            run = self.get_run(run_id, wait_seconds=max(1, min(30, left)))
            if run.get("done"):
                return run
            if time.time() >= deadline:
                raise AktorError(f"Run {run_id} didn't finish in time (it's {run.get('status')}).", 408, run)


def _q(value):
    return urllib.parse.quote(value, safe="")
