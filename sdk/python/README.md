# aktor (Python)

The Aktor Agents platform from Python, with no dependencies (the standard library only):

- **`Aktor`**: the platform API with an API key: tasks, workspaces, and solutions (deploy,
  records, evals, end-user tokens).
- **`AktorApp`**: the end-user API, for your customers' side of a solution.
- **`Plugin`**: a remote plugin, meaning your own integration written in Python and served over HTTP.

```bash
pip install ./sdk/python        # from this repository
```

## Build on the platform

Solutions can be built and deployed entirely in the dashboard (**Solutions → New solution**). This
client is for doing the same from code: CI, scripts, or your own backend.

```python
from aktor import Aktor

aktor = Aktor(api_key="ak_...", base_url="http://localhost:5080")   # or AKTOR_API_KEY / AKTOR_URL

# One-off work: a governed team of agents, with the result when it's done.
result = aktor.tasks.run("Compare the three biggest property-management SaaS products.")

# A solution: install the package, or upgrade the installed version.
aktor.solutions.deploy("samples/solutions/property-mgmt")

# Its records, from your backend. Writes are checked against the data model and fire record triggers.
ticket = aktor.solutions.records.create("property-mgmt", "Ticket", {"issue": "No hot water"}, idempotency_key="req-81")
leases = aktor.solutions.records.list("property-mgmt", "Lease", filters=["ends_on<=today+60d"], where={"status": "active"})

# Run its evals for real (CI):
report = aktor.solutions.run_evals("property-mgmt")
assert report["failed"] == 0, report

# What it cost per end user this month, for billing your customers.
usage = aktor.solutions.usage("property-mgmt", by="end_user")

# The registry: share a signed version, install someone else's.
aktor.registry.publish("samples/solutions/helpdesk", public=True)
aktor.solutions.install_package("helpdesk@1.0.0")
```

## Your end users

Your backend issues a short-lived token for a signed-in user, and their app (or the
`/embed.js` widget) uses it. The token only reaches that user's conversations and records.

```python
token = aktor.solutions.end_user_token("property-mgmt", user_id="resident-4b", name="Sam")["token"]

from aktor import AktorApp
app = AktorApp(install_id, token, base_url="http://localhost:5080")
c = app.start("The kitchen tap is leaking")
reply = app.wait_for_reply(c["conversation_id"])
print(reply["messages"][-1]["text"])
```

## Remote plugins

A plugin can run out of process, in any language, as an HTTP service the runtime calls
(docs/plugins.md#remote-plugins). This package includes the server:

```python
from aktor import Plugin, ToolError

plugin = Plugin("crm")

@plugin.tool(description="Find a customer by email.", side_effects="read_only",
             input_schema={"type": "object", "properties": {"email": {"type": "string"}}, "required": ["email"]})
def find_customer(args, ctx):
    # ctx.config and ctx.credentials come from the connection (credentials from the vault).
    return {"name": "Ada", "plan": "pro"}

plugin.serve(port=8787, token="shared-secret")
```

Add it to a workspace as a **Remote plugin** connection with its URL and the same shared secret.
Agents then see `crm__find_customer`. Declare `side_effects` honestly: read-only tools may be
re-run after a crash, and others get `ctx.idempotency_key` to pass to the system they call.
See `examples/crm_plugin.py`.

## Tests

```bash
python -m unittest discover -s sdk/python/tests
```
