"""A remote plugin with two tools and a notification channel, for trying the protocol.

    PLUGIN_TOKEN=change-me python examples/crm_plugin.py

Then add a "Remote plugin" connection named crm to a workspace: URL http://localhost:8787,
shared secret change-me. Agents get crm__find_customer and crm__add_note.
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from aktor import Plugin, ToolError  # noqa: E402

plugin = Plugin("crm")
CUSTOMERS = {"ada@example.com": {"name": "Ada Lovelace", "plan": "pro"}}
NOTES = {}


@plugin.tool(description="Find a customer by email.", side_effects="read_only",
             input_schema={"type": "object", "properties": {"email": {"type": "string"}}, "required": ["email"]})
def find_customer(args, ctx):
    customer = CUSTOMERS.get(args.get("email", "").lower())
    if customer is None:
        raise ToolError("No customer with that email.", "not_found")
    return customer


@plugin.tool(description="Add a note to a customer.", side_effects="idempotent",
             input_schema={"type": "object", "properties": {"email": {"type": "string"}, "note": {"type": "string"}}, "required": ["email", "note"]})
def add_note(args, ctx):
    # The idempotency key makes a repeated call (after a crash) add the note once.
    NOTES[ctx.idempotency_key] = (args["email"], args["note"])
    return {"notes": sum(1 for e, _ in NOTES.values() if e == args["email"])}


@plugin.notify
def notify(notification, ctx):
    print(f"[{notification['urgency']}] {notification['workspace']}: {notification['text']}")


if __name__ == "__main__":
    plugin.serve(port=int(os.environ.get("PORT", "8787")), token=os.environ.get("PLUGIN_TOKEN", "change-me"))
