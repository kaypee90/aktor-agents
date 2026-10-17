import os
import time
import urllib.parse

from ._http import AktorError, request

_MANIFESTS = ("aktor.yaml", "aktor.yml", "aktor.json")
_TEXT = {".yaml", ".yml", ".json", ".md", ".txt", ".csv", ".py", ".js", ".html", ".sql"}
_SKIP = {"node_modules", ".git", "bin", "obj", ".aktor", "__pycache__"}


def _q(value):
    return urllib.parse.quote(str(value), safe="")


def bundle_directory(directory="."):
    """A solution package on disk (a folder with aktor.yaml) as the bundle the API takes."""
    root = os.path.abspath(directory)
    manifest = next((os.path.join(root, n) for n in _MANIFESTS if os.path.isfile(os.path.join(root, n))), None)
    if manifest is None:
        raise FileNotFoundError(f"No aktor.yaml in {root}")
    files = {}
    for current, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if not d.startswith(".") and d not in _SKIP]
        for name in names:
            path = os.path.join(current, name)
            if name.startswith(".") or path == manifest or os.path.splitext(name)[1].lower() not in _TEXT:
                continue
            with open(path, encoding="utf-8") as f:
                files[os.path.relpath(path, root).replace(os.sep, "/")] = f.read()
    with open(manifest, encoding="utf-8") as f:
        return {"manifest": f.read(), "files": files}


class _Group:
    def __init__(self, client):
        self._c = client


class Aktor:
    """The platform API, with an API key from Settings → API keys.

        aktor = Aktor(api_key=os.environ["AKTOR_API_KEY"], base_url="http://localhost:5080")
        task = aktor.tasks.run("Research the market for ...")      # start and wait
        aktor.solutions.deploy("./my-app")                          # install or upgrade
        aktor.solutions.records.create("my-app", "Ticket", {"subject": "Printer jam"})
    """

    def __init__(self, api_key=None, base_url=None, retries=2, timeout=120):
        self.api_key = api_key or os.environ.get("AKTOR_API_KEY")
        self.base_url = (base_url or os.environ.get("AKTOR_URL") or "http://localhost:5080").rstrip("/")
        self.retries = retries
        self.timeout = timeout
        self.tasks = _Tasks(self)
        self.workspaces = _Workspaces(self)
        self.solutions = _Solutions(self)
        self.registry = _Registry(self)

    def request(self, method, path, body=None, query=None, headers=None, idempotent=None):
        safe = idempotent if idempotent is not None else method in ("GET", "PUT", "DELETE")
        return request(self.base_url, method, path, self.api_key, body, query, headers, self.retries if safe else 0, self.timeout)

    def me(self):
        return self.request("GET", "/api/v1/auth/me")


class _Tasks(_Group):
    def start(self, goal, budget=None, workspace_id=None, callback_url=None, correlation_id=None):
        body = {"goal": goal, "budget": budget, "workspace_id": workspace_id, "callback_url": callback_url, "correlation_id": correlation_id}
        return self._c.request("POST", "/api/v1/tasks", {k: v for k, v in body.items() if v is not None})

    def get(self, task_id):
        return self._c.request("GET", f"/api/v1/tasks/{_q(task_id)}")

    def wait(self, task_id, timeout_seconds=600):
        """Waits until the task is done (or the timeout), and returns it."""
        deadline = time.time() + timeout_seconds
        while True:
            left = max(1, min(60, int(deadline - time.time())))
            task = self._c.request("GET", f"/api/v1/tasks/{_q(task_id)}/wait", query={"timeout_seconds": left})
            if task.get("done") or time.time() >= deadline:
                return task

    def result(self, task_id):
        return self._c.request("GET", f"/api/v1/tasks/{_q(task_id)}/result")

    def run(self, goal, timeout_seconds=600, **options):
        """Starts a task, waits for it and returns its result."""
        task = self.start(goal, **options)
        self.wait(task["task_id"], timeout_seconds)
        return self.result(task["task_id"])

    def cancel(self, task_id):
        return self._c.request("POST", f"/api/v1/tasks/{_q(task_id)}/cancel", idempotent=True)


class _Workspaces(_Group):
    def list(self):
        return self._c.request("GET", "/api/v1/workspaces")

    def get(self, workspace_id):
        return self._c.request("GET", f"/api/v1/workspaces/{_q(workspace_id)}")

    def start_run(self, workspace_id, input, data=None):
        """Starts a run of the workspace's pipeline; action and script stages see ``data``."""
        body = {"input": input}
        if data is not None:
            body["data"] = data
        return self._c.request("POST", f"/api/v1/workspaces/{_q(workspace_id)}/runs", body)


class _Records(_Group):
    def list(self, solution, entity, where=None, filters=None, order_by=None, desc=None, limit=None, offset=None, end_user=None):
        """``where`` matches fields exactly; ``filters`` are like "ends_on<=today+30d", "rent>=1000", "name~smith"."""
        query = {f"where.{k}": v for k, v in (where or {}).items()}
        query.update({"filter": list(filters or []), "order_by": order_by, "desc": desc, "limit": limit, "offset": offset, "end_user": end_user})
        return self._c.request("GET", f"/api/v1/solutions/{_q(solution)}/records/{_q(entity)}", query=query)

    def get(self, solution, entity, record_id):
        return self._c.request("GET", f"/api/v1/solutions/{_q(solution)}/records/{_q(entity)}/{_q(record_id)}")

    def create(self, solution, entity, fields, idempotency_key=None):
        """With ``idempotency_key``, a retried create returns the record the first one made."""
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._c.request("POST", f"/api/v1/solutions/{_q(solution)}/records/{_q(entity)}", fields, headers=headers, idempotent=bool(idempotency_key))

    def update(self, solution, entity, record_id, fields, expected_version=None):
        """Only the fields given change (None clears one). With ``expected_version``, a stale write fails with 409."""
        headers = {"If-Match": f'"{expected_version}"'} if expected_version is not None else None
        return self._c.request("PATCH", f"/api/v1/solutions/{_q(solution)}/records/{_q(entity)}/{_q(record_id)}", fields, headers=headers, idempotent=True)

    def delete(self, solution, entity, record_id):
        return self._c.request("DELETE", f"/api/v1/solutions/{_q(solution)}/records/{_q(entity)}/{_q(record_id)}")


class _Solutions(_Group):
    def __init__(self, client):
        super().__init__(client)
        self.records = _Records(client)

    def validate(self, package):
        """``package`` is a directory or a bundle dict."""
        return self._c.request("POST", "/api/v1/solutions/validate", _bundle(package), idempotent=True)

    def list(self, all=False):
        return self._c.request("GET", "/api/v1/solutions", query={"all": all})

    def get(self, id_or_name):
        return self._c.request("GET", f"/api/v1/solutions/{_q(id_or_name)}")

    def install(self, package, inputs=None, secrets=None):
        return self._c.request("POST", "/api/v1/solutions", {"bundle": _bundle(package), "inputs": inputs or {}, "secrets": secrets or {}})

    def upgrade(self, id_or_name, package, inputs=None, secrets=None, force=False, dry_run=False):
        body = {"bundle": _bundle(package), "inputs": inputs or {}, "secrets": secrets or {}, "force": force, "dry_run": dry_run}
        return self._c.request("PUT", f"/api/v1/solutions/{_q(id_or_name)}", body, idempotent=False)

    def deploy(self, package, inputs=None, secrets=None, force=False):
        """Installs the package, or upgrades the installed version of it."""
        bundle = _bundle(package)
        name = _manifest_name(bundle["manifest"])
        try:
            existing = self.get(name) if name else None
        except AktorError as err:
            if err.status != 404:
                raise
            existing = None
        if existing and existing.get("status") == "Active":
            return self.upgrade(existing["install_id"], bundle, inputs, secrets, force)
        return self.install(bundle, inputs, secrets)

    def uninstall(self, id_or_name, purge=False):
        return self._c.request("DELETE", f"/api/v1/solutions/{_q(id_or_name)}", query={"purge": purge})

    def run_evals(self, id_or_name, names=None):
        return self._c.request("POST", f"/api/v1/solutions/{_q(id_or_name)}/evals", {"names": names}, idempotent=False)

    def install_package(self, reference, inputs=None, secrets=None):
        """Installs a registry package: "name", "name@1.2.0" or "name@latest"."""
        return self._c.request("POST", "/api/v1/solutions", {"package": reference, "inputs": inputs or {}, "secrets": secrets or {}})

    def usage(self, id_or_name, by="end_user", start=None, end=None):
        """What the solution cost, by end_user, workspace or day (this calendar month by default)."""
        return self._c.request("GET", f"/api/v1/solutions/{_q(id_or_name)}/usage", query={"by": by, "from": start, "to": end})

    def end_user_token(self, id_or_name, user_id, name=None, ttl_minutes=None):
        """A token for one of your end users: call this from your backend after your own sign-in."""
        body = {"user_id": user_id, "name": name, "ttl_minutes": ttl_minutes}
        return self._c.request("POST", f"/api/v1/solutions/{_q(id_or_name)}/end-user-tokens", {k: v for k, v in body.items() if v is not None})


class _Registry(_Group):
    """The solution registry: publish your packages, find others' public ones."""

    def publish(self, package, public=False):
        return self._c.request("POST", "/api/v1/registry/packages", {"bundle": _bundle(package), "visibility": "public" if public else "organization"})

    def search(self, query=None):
        return self._c.request("GET", "/api/v1/registry/packages", query={"q": query})

    def versions(self, name):
        return self._c.request("GET", f"/api/v1/registry/packages/{_q(name)}")

    def get(self, name, version="latest"):
        return self._c.request("GET", f"/api/v1/registry/packages/{_q(name)}/{_q(version)}")

    def yank(self, name, version, reason=None, undo=False):
        return self._c.request("POST", f"/api/v1/registry/packages/{_q(name)}/{_q(version)}/yank", {"yank": not undo, "reason": reason}, idempotent=True)


def _bundle(package):
    return package if isinstance(package, dict) else bundle_directory(package)


def _manifest_name(text):
    for line in text.splitlines():
        if line.startswith("name:"):
            return line.split(":", 1)[1].split("#", 1)[0].strip().strip("'\"")
    return None
