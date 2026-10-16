"""Import reported usage for the current chat, separately from node transport metrics."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re

from contracts import private_json
from remote_lab import REPOSITORY, private_directory

TOKEN_FIELDS = ["input_tokens", "cached_input_tokens", "cache_write_input_tokens", "output_tokens", "reasoning_output_tokens"]
RUN_NAME = re.compile(r"[a-z0-9][a-z0-9._-]{0,63}\Z")


def reported_usage(sessions: Path, thread: str) -> dict:
    import uuid
    identifier = str(uuid.UUID(thread))
    files = list(sessions.rglob("rollout-*" + identifier + ".jsonl"))
    if len(files) != 1:
        raise ValueError("Exactly one telemetry file for the selected current thread is required")
    latest = None
    with files[0].open() as stream:
        for line in stream:
            try:
                event = json.loads(line)
            except ValueError:
                continue
            payload = event.get("payload", {})
            if event.get("type") == "event_msg" and payload.get("type") == "token_count" and payload.get("info"):
                latest = payload["info"].get("total_token_usage")
    if not isinstance(latest, dict):
        raise ValueError("The client has not supplied token usage for this thread")
    for field in TOKEN_FIELDS:
        value = latest.get(field)
        if value is not None and (type(value) is not int or value < 0):
            raise ValueError("Client reported an invalid token counter")
    return {field: latest.get(field) for field in TOKEN_FIELDS}


def difference(before: dict, after: dict) -> dict:
    result = {}
    for field in TOKEN_FIELDS:
        first = before.get(field)
        last = after.get(field)
        result[field] = last - first if first is not None and last is not None and last >= first else None
    return result


def capture(directory: Path, command: str, name: str, thread: str, sessions: Path) -> dict:
    if not RUN_NAME.fullmatch(name):
        raise ValueError("Use a bounded run name without path separators")
    marker = directory / (name + "-begin.json")
    usage = reported_usage(sessions, thread)
    timestamp = datetime.now(timezone.utc).isoformat()
    if command == "begin":
        if marker.exists():
            raise ValueError("The existing run boundary must not be overwritten")
        result = {"thread": thread, "startedAt": timestamp, "usage": usage}
        private_json(marker, result)
        return {"success": True, "run": name, "boundary": "begin"}
    initial = json.loads(marker.read_text())
    if initial["thread"] != thread:
        raise ValueError("Run usage must come from the same client thread")
    elapsed = (datetime.fromisoformat(timestamp) - datetime.fromisoformat(initial["startedAt"])).total_seconds()
    result = {"success": True, "run": name, "thread": thread, "elapsedSeconds": elapsed,
              "usage": difference(initial["usage"], usage), "source": "client-reported codex token_count",
              "scope": "Interval through the last reported model request; active/final unreported requests remain excluded",
              "usageComplete": False, "performance": "inconclusive", "apiCharge": None, "savingsClaim": None}
    private_json(directory / (name + "-usage.json"), result)
    return result


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description="Read reported current-chat tokens; no model invocation and no estimates.")
    parser.add_argument("command", choices=["begin", "finish"])
    parser.add_argument("name")
    parser.add_argument("--thread", default=os.environ.get("CODEX_THREAD_ID"))
    parser.add_argument("--sessions", type=Path, default=Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "sessions")
    parser.add_argument("--directory", type=Path, default=REPOSITORY / ".artifacts/performance/chat-runs")
    arguments = parser.parse_args()
    try:
        if not arguments.thread:
            raise ValueError("Select the current authorized client thread")
        result = capture(private_directory(arguments.directory), arguments.command, arguments.name, arguments.thread, arguments.sessions)
        print(json.dumps(result))
        return 0
    except (ValueError, OSError, KeyError) as error:
        print(json.dumps({"success": False, "error": str(error)}))
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
