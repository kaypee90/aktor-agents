/*
 * Aktor embed: a solution's assistant on your own site (docs/solutions.md#embed).
 *
 *   <script src="https://YOUR-AKTOR/embed.js"
 *           data-install="sol-…"
 *           data-token-url="/api/aktor-token"      // your backend: returns { "token": "aeu_…" }
 *           async></script>
 *
 * or call AktorChat.mount({ install, token | getToken, apiBase, open, container }) yourself.
 * The token comes from your backend (POST /api/v1/solutions/{id}/end-user-tokens with an API key),
 * after your own sign-in; the widget never sees an API key.
 */
(function () {
  "use strict";
  if (window.AktorChat) return;

  var script = document.currentScript;
  var defaultBase = script && script.src ? new URL(script.src).origin : "";

  var css = [
    ":host{all:initial;--bg:#ffffff;--fg:#1d2433;--muted:#6b7385;--line:#e3e6ec;--accent:#3b5bdb;--accent-fg:#fff;--me:#eef2ff;--shadow:0 12px 32px rgba(15,23,42,.18);",
    "font:14px/1.45 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;color:var(--fg)}",
    "@media (prefers-color-scheme:dark){:host{--bg:#171a21;--fg:#e7e9ee;--muted:#9aa2b1;--line:#2a2f3a;--accent:#7c95ff;--accent-fg:#0b0d12;--me:#232a40;--shadow:0 12px 32px rgba(0,0,0,.5)}}",
    ".launcher{position:fixed;right:20px;bottom:20px;width:56px;height:56px;border-radius:50%;border:0;background:var(--accent);color:var(--accent-fg);",
    "box-shadow:var(--shadow);cursor:pointer;display:flex;align-items:center;justify-content:center;z-index:2147483000}",
    ".launcher svg{width:26px;height:26px}",
    ".panel{position:fixed;right:20px;bottom:88px;width:min(380px,calc(100vw - 32px));height:min(560px,calc(100vh - 120px));background:var(--bg);",
    "border:1px solid var(--line);border-radius:14px;box-shadow:var(--shadow);display:flex;flex-direction:column;overflow:hidden;z-index:2147483000}",
    ".panel[hidden]{display:none}",
    ".inline{position:static;width:100%;height:100%;box-shadow:none}",
    "header{padding:12px 14px;border-bottom:1px solid var(--line);display:flex;align-items:center;gap:8px}",
    "header b{flex:1;font-weight:600}",
    "header button{background:none;border:0;color:var(--muted);cursor:pointer;font:inherit;padding:4px 6px;border-radius:6px}",
    "header button:hover{background:var(--me);color:var(--fg)}",
    ".log{flex:1;overflow-y:auto;padding:14px;display:flex;flex-direction:column;gap:10px}",
    ".msg{max-width:85%;padding:9px 12px;border-radius:12px;white-space:pre-wrap;word-wrap:break-word}",
    ".msg.user{align-self:flex-end;background:var(--accent);color:var(--accent-fg);border-bottom-right-radius:4px}",
    ".msg.assistant{align-self:flex-start;background:var(--me);border-bottom-left-radius:4px}",
    ".msg.note{align-self:center;background:none;color:var(--muted);font-size:12px;text-align:center}",
    ".typing{align-self:flex-start;color:var(--muted);font-size:13px;padding:4px 2px}",
    "form{display:flex;gap:8px;padding:10px;border-top:1px solid var(--line)}",
    "textarea{flex:1;resize:none;border:1px solid var(--line);border-radius:10px;padding:8px 10px;font:inherit;color:var(--fg);background:var(--bg);min-height:20px;max-height:120px}",
    "textarea:focus{outline:2px solid var(--accent);outline-offset:-1px}",
    "form button{border:0;border-radius:10px;background:var(--accent);color:var(--accent-fg);padding:0 14px;font:inherit;font-weight:600;cursor:pointer}",
    "form button:disabled{opacity:.5;cursor:default}"
  ].join("");

  function mount(options) {
    options = options || {};
    var install = options.install;
    if (!install) throw new Error("AktorChat: 'install' is required.");
    var apiBase = (options.apiBase || defaultBase).replace(/\/$/, "");
    var token = options.token || null;
    var getToken = options.getToken || (options.tokenUrl ? function () {
      return fetch(options.tokenUrl, { credentials: "include", method: "POST" })
        .then(function (r) { if (!r.ok) throw new Error("token " + r.status); return r.json(); })
        .then(function (j) { return j.token; });
    } : null);
    var storeKey = "aktor-chat:" + install;

    var host = document.createElement("div");
    if (options.container) { host.style.display = "block"; host.style.height = "100%"; }
    (options.container || document.body).appendChild(host);
    var root = host.attachShadow ? host.attachShadow({ mode: "open" }) : host;
    var style = document.createElement("style");
    style.textContent = css;
    root.appendChild(style);

    var inline = !!options.container;
    var launcher = null;
    if (!inline) {
      launcher = el("button", "launcher");
      launcher.setAttribute("aria-label", "Open chat");
      launcher.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>';
      root.appendChild(launcher);
    }

    var panel = el("div", "panel" + (inline ? " inline" : ""));
    if (!inline && !options.open) panel.hidden = true;
    var header = el("header");
    var title = el("b");
    title.textContent = "Assistant";
    var fresh = el("button");
    fresh.textContent = "New";
    fresh.title = "Start a new conversation";
    header.appendChild(title);
    header.appendChild(fresh);
    if (!inline) {
      var close = el("button");
      close.textContent = "✕";
      close.setAttribute("aria-label", "Close");
      close.onclick = function () { panel.hidden = true; };
      header.appendChild(close);
    }
    var log = el("div", "log");
    log.setAttribute("aria-live", "polite");
    var form = el("form");
    var input = el("textarea");
    input.rows = 1;
    input.placeholder = "Write a message…";
    var send = el("button");
    send.type = "submit";
    send.textContent = "Send";
    form.appendChild(input);
    form.appendChild(send);
    panel.appendChild(header);
    panel.appendChild(log);
    panel.appendChild(form);
    root.appendChild(panel);

    var state = { conversation: read(), greeting: null, polling: null, busy: false };

    function el(tag, cls) { var e = document.createElement(tag); if (cls) e.className = cls; return e; }
    function read() { try { return localStorage.getItem(storeKey); } catch (e) { return null; } }
    function write(v) { try { if (v) localStorage.setItem(storeKey, v); else localStorage.removeItem(storeKey); } catch (e) { /* private mode */ } }

    function ensureToken(force) {
      if (token && !force) return Promise.resolve(token);
      if (!getToken) return token ? Promise.resolve(token) : Promise.reject(new Error("No end-user token."));
      return Promise.resolve(getToken()).then(function (t) { token = t; return t; });
    }

    function api(method, path, body, retried) {
      return ensureToken(false).then(function (t) {
        return fetch(apiBase + "/api/v1/apps/" + encodeURIComponent(install) + path, {
          method: method,
          headers: { "Authorization": "Bearer " + t, "Content-Type": "application/json" },
          body: body ? JSON.stringify(body) : undefined
        });
      }).then(function (r) {
        if (r.status === 401 && !retried && getToken) return ensureToken(true).then(function () { return api(method, path, body, true); });
        return r.json().catch(function () { return {}; }).then(function (j) {
          if (!r.ok) throw new Error(j.error || ("HTTP " + r.status));
          return j;
        });
      });
    }

    function render(messages, answering) {
      log.textContent = "";
      if (state.greeting && (!messages || messages.length === 0)) bubble("assistant", state.greeting);
      (messages || []).forEach(function (m) { bubble(m.role, m.text); });
      if (answering) { var t = el("div", "typing"); t.textContent = "…"; log.appendChild(t); }
      log.scrollTop = log.scrollHeight;
    }

    function bubble(role, text) {
      var b = el("div", "msg " + role);
      b.textContent = text;
      log.appendChild(b);
    }

    function note(text) { var n = el("div", "msg note"); n.textContent = text; log.appendChild(n); log.scrollTop = log.scrollHeight; }

    function poll() {
      if (!state.conversation) return;
      api("GET", "/conversations/" + encodeURIComponent(state.conversation)).then(function (c) {
        render(c.messages, c.answering);
        setBusy(c.answering);
        clearTimeout(state.polling);
        if (c.answering) state.polling = setTimeout(poll, 1500);
      }).catch(function (e) {
        if (/conversation/i.test(e.message)) { state.conversation = null; write(null); render([], false); return; }
        note(e.message);
      });
    }

    function setBusy(busy) { state.busy = busy; send.disabled = busy; }

    form.onsubmit = function (ev) {
      ev.preventDefault();
      var text = input.value.trim();
      if (!text || state.busy) return;
      input.value = "";
      setBusy(true);
      bubble("user", text);
      var t = el("div", "typing"); t.textContent = "…"; log.appendChild(t); log.scrollTop = log.scrollHeight;
      var call = state.conversation
        ? api("POST", "/conversations/" + encodeURIComponent(state.conversation) + "/messages", { message: text })
        : api("POST", "/conversations", { message: text });
      call.then(function (r) {
        state.conversation = r.conversation_id;
        write(state.conversation);
        setTimeout(poll, 800);
      }).catch(function (e) { setBusy(false); t.remove(); note(e.message); });
    };

    input.onkeydown = function (ev) {
      if (ev.key === "Enter" && !ev.shiftKey) { ev.preventDefault(); form.requestSubmit ? form.requestSubmit() : form.onsubmit(ev); }
    };

    fresh.onclick = function () { state.conversation = null; write(null); clearTimeout(state.polling); setBusy(false); render([], false); input.focus(); };

    if (launcher) launcher.onclick = function () { panel.hidden = !panel.hidden; if (!panel.hidden) input.focus(); };

    api("GET", "").then(function (app) {
      title.textContent = app.assistant || app.app || "Assistant";
      state.greeting = app.greeting || null;
      if (state.conversation) poll(); else render([], false);
    }).catch(function (e) { note(e.message); });

    return {
      open: function () { panel.hidden = false; },
      close: function () { if (!inline) panel.hidden = true; },
      setToken: function (t) { token = t; },
      destroy: function () { clearTimeout(state.polling); host.remove(); }
    };
  }

  window.AktorChat = { mount: mount };

  if (script && script.dataset && script.dataset.install) {
    var start = function () {
      mount({
        install: script.dataset.install,
        token: script.dataset.token || null,
        tokenUrl: script.dataset.tokenUrl || null,
        apiBase: script.dataset.api || defaultBase,
        open: script.dataset.open === "true"
      });
    };
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
  }
})();
