import json
import random
import time
import urllib.error
import urllib.parse
import urllib.request


class AktorError(Exception):
    """An error answer from the server: ``status`` is the HTTP status, ``body`` the parsed answer."""

    def __init__(self, message, status, body=None):
        super().__init__(message)
        self.status = status
        self.body = body

    @property
    def errors(self):
        """Every problem the server listed (validation answers list several)."""
        if isinstance(self.body, dict) and isinstance(self.body.get("errors"), list):
            return self.body["errors"]
        return [str(self)]


def request(base_url, method, path, token=None, body=None, query=None, headers=None, retries=0, timeout=120):
    """One JSON request. Requests that are safe to repeat are retried on 429, 5xx and network errors."""
    url = base_url.rstrip("/") + path
    if query:
        pairs = []
        for key, value in query.items():
            if value is None:
                continue
            for v in value if isinstance(value, (list, tuple)) else [value]:
                pairs.append((key, str(v).lower() if isinstance(v, bool) else str(v)))
        if pairs:
            url += ("&" if "?" in url else "?") + urllib.parse.urlencode(pairs)

    data = None if body is None else json.dumps(body).encode("utf-8")
    all_headers = {"Accept": "application/json"}
    if data is not None:
        all_headers["Content-Type"] = "application/json"
    if token:
        all_headers["Authorization"] = "Bearer " + token
    all_headers.update(headers or {})

    for attempt in range(retries + 1):
        req = urllib.request.Request(url, data=data, method=method, headers=all_headers)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as response:
                text = response.read().decode("utf-8")
                return json.loads(text) if text else None
        except urllib.error.HTTPError as err:
            text = err.read().decode("utf-8", "replace")
            try:
                parsed = json.loads(text) if text else None
            except ValueError:
                parsed = text
            if (err.code == 429 or err.code >= 500) and attempt < retries:
                time.sleep(_retry_after(err) or _backoff(attempt))
                continue
            message = parsed.get("error") if isinstance(parsed, dict) and parsed.get("error") else f"{method} {path} failed with HTTP {err.code}"
            raise AktorError(message, err.code, parsed) from None
        except urllib.error.URLError as err:
            if attempt < retries:
                time.sleep(_backoff(attempt))
                continue
            raise AktorError(f"Can't reach {base_url}: {err.reason}", 0) from None


def _backoff(attempt):
    return min(8.0, 0.25 * 2 ** attempt) + random.random() / 10


def _retry_after(err):
    try:
        return min(30.0, float(err.headers.get("Retry-After")))
    except (TypeError, ValueError):
        return None
