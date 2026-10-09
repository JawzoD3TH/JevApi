"""Raw MCP-over-stdio smoke test for the JevMcp server.

Framework-free on purpose: it speaks newline-delimited JSON-RPC directly, so it
can run anywhere Python does and needs nothing but the server executable.
Useful as a keyless sanity check (health / validate_questions / examples) and as
a reference client for debugging transport-level issues — this is the harness
that caught the PublishAot runtimeconfig regression.

Usage:
    python mcp_stdio_smoke.py [path-to-JevMcp.exe] [--live]

Default (keyless): initialize -> tools/list -> health -> validate_questions -> examples.
--live additionally spends one evaluation against the Jev API.
Exit code 0 = all checks passed, 1 = a check failed.
"""
import json
import os
import queue
import subprocess
import sys
import threading
import time

failures = 0


def fail(msg):
    global failures
    failures += 1
    print(f"        FAIL: {msg}")


def find_default_server():
    here = os.path.dirname(os.path.abspath(__file__))
    for _ in range(8):
        for cfg in ("Debug", "Release"):
            candidate = os.path.join(here, "MCP", "bin", cfg, "net10.0", "JevMcp.exe")
            if os.path.exists(candidate):
                return candidate
        here = os.path.dirname(here)
    return None


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    live = "--live" in sys.argv
    exe = args[0] if args else os.environ.get("JEVMCP_EXE") or find_default_server()
    if not exe or not os.path.exists(exe):
        print(f"JevMcp server not found at: {exe!r}. Pass its path as arg 1 or set JEVMCP_EXE.")
        return 1

    print(f"[smoke] server: {exe}")
    print(f"[smoke] mode:   {'LIVE (spends evaluations)' if live else 'keyless'}")
    print()

    proc = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
    out_q, err_q = queue.Queue(), queue.Queue()
    threading.Thread(target=lambda: [out_q.put(l) for l in proc.stdout], daemon=True).start()
    threading.Thread(target=lambda: [err_q.put(l) for l in proc.stderr], daemon=True).start()

    _id = 0

    def request(method, params=None):
        nonlocal _id
        _id += 1
        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": _id, "method": method,
                                     "params": params or {}}) + "\n")
        proc.stdin.flush()
        return _id

    def collect(want_id, timeout=30):
        end = time.time() + timeout
        while time.time() < end:
            try:
                line = out_q.get(timeout=max(0.1, end - time.time()))
            except queue.Empty:
                break
            line = line.strip()
            if not line:
                continue
            msg = json.loads(line)
            if msg.get("id") == want_id:
                return msg
            if "method" in msg and "id" in msg:
                # server->client request (MCPSharp's ping watchdog) — must reply
                proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": {}}) + "\n")
                proc.stdin.flush()
        return None

    def call(name, arguments, timeout=30):
        rid = request("tools/call", {"name": name, "arguments": arguments})
        r = collect(rid, timeout)
        if r is None:
            fail(f"{name}: no response")
            return None
        if "error" in r:
            fail(f"{name}: {r['error']}")
            return None
        text = r["result"]["content"][0]["text"]
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            fail(f"{name}: non-JSON response: {text[:120]}")
            return None

    # ---- initialize + tools/list ----
    print("[check] initialize + tools/list")
    rid = request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                 "clientInfo": {"name": "mcp-stdio-smoke", "version": "1.0"}})
    if collect(rid, timeout=30) is None:
        print("        FAIL: no response to initialize — is the server muted? "
              "(check runtimeconfig for JsonSerializer.IsReflectionEnabledByDefault=false)")
        return 1
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
    proc.stdin.flush()

    rid = request("tools/list")
    r = collect(rid)
    names = [t["name"] for t in r["result"]["tools"]] if r else []
    missing = set(("evaluate", "evaluate_batch", "validate_questions", "examples", "health")) - set(names)
    if missing:
        fail(f"missing tools: {sorted(missing)}")
    else:
        print(f"        OK: all 5 tools present ({', '.join(names)})")

    # ---- health ----
    print("[check] health")
    h = call("health", {})
    if h:
        print(f"        {h}")
        if not h.get("api_key_configured"):
            print("        NOTE: no API key configured (evaluate/evaluate_batch will return 'unauthorized')")

    # ---- validate_questions ----
    print("[check] validate_questions (noul with 'and' must PASS; compound choice must FAIL)")
    v = call("validate_questions", {"questions": [
        {"id": "claim_ok", "type": "noul",
         "statement": "The source explicitly states that the payout failed and that support was notified."},
        {"id": "bad", "type": "choice",
         "question": "Is this relevant and recent and cheap?", "options": ["yes", "no"]},
    ]})
    if v:
        results = v["results"]
        if results[0]["valid"] and not results[1]["valid"]:
            print("        OK: noul accepted, compound flagged")
        else:
            fail(f"expected noul valid / choice invalid, got "
                 f"{results[0]['valid']}/{results[1]['valid']}")

    # ---- list-envelope tolerance (interop regression guard) ----
    # Some MCP clients wrap list arguments in {"contents": [...]} per their advertised schema.
    # The server must accept both the bare array and the envelope. Keyless: uses validate_questions.
    print("[check] list argument shapes: bare array AND {contents:[...]} envelope both accepted")
    v_env = call("validate_questions", {"questions": {"contents": [
        {"id": "env_ok", "type": "noul", "statement": "The source explicitly states X."},
    ]}})
    if v_env and v_env.get("valid"):
        print("        OK: contents-envelope accepted")
    else:
        fail(f"contents-envelope rejected: {v_env}")

    # ---- examples ----
    print("[check] examples")
    ex = call("examples", {})
    if ex:
        print(f"        patterns: {', '.join(ex.keys())}")
        print("        OK")

    # ---- live evaluation (optional) ----
    if live:
        print("[check] evaluate (LIVE — one call, three question types)")
        state = ("Acme Corp reported Q3 revenue of $12.4 million on Tuesday, "
                 "citing strong demand in the European market.")
        ev = call("evaluate", {
            "state": state,
            "questions": [
                {"id": "rev_check", "type": "noul",
                 "statement": "The source explicitly states that Acme Corp reported Q3 revenue of $12.4 million."},
                {"id": "topic", "type": "choice", "question": "What kind of content is this?",
                 "options": ["earnings report", "product announcement", "opinion piece"]},
                {"id": "positivity", "type": "score", "question": "How positive is the tone of this statement?",
                 "levels": ["negative", "neutral", "positive"]},
            ],
        }, timeout=90)
        if ev:
            for res in ev["results"]:
                print(f"        {res['id']}: {json.dumps(res)}")
            print(f"        latency_ms: {ev['latency_ms']}")
            print("        OK")

    proc.terminate()
    proc.wait(timeout=10)
    print()
    print("[smoke] ALL CHECKS PASSED" if failures == 0 else f"[smoke] {failures} CHECK(S) FAILED")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
