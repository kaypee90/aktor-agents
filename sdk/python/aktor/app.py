import time
import urllib.parse

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
        return request(self.base_url, method, f"/api/v1/apps/{urllib.parse.quote(self.install_id, safe='')}{path}",
                       self.token, body, query, timeout=self.timeout)

    def describe(self):
        return self._call("GET", "")

    def conversations(self):
        return self._call("GET", "/conversations")

    def start(self, message):
        return self._call("POST", "/conversations", {"message": message})

    def get(self, conversation_id):
        return self._call("GET", f"/conversations/{urllib.parse.quote(conversation_id, safe='')}")

    def send(self, conversation_id, message):
        return self._call("POST", f"/conversations/{urllib.parse.quote(conversation_id, safe='')}/messages", {"message": message})

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
        return self._call("GET", f"/records/{urllib.parse.quote(entity, safe='')}", query={"limit": limit, "offset": offset})
