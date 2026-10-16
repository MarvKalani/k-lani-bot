"""Provider usage is recorded incrementally; absent breakdowns stay unknown."""
from dataclasses import dataclass, field
from decimal import Decimal
import json
from pathlib import Path
from contracts import Route, digest

TOKEN_FIELDS = ("input_tokens", "cached_input_tokens", "output_tokens", "reasoning_tokens", "image_tokens")
BYTE_FIELDS = ("description_bytes", "result_bytes", "image_bytes", "images", "tool_calls", "native_calls", "authoring_calls", "interventions")


@dataclass
# The host grants private budgets and retains provider evidence across interrupted attempts.
class Ledger:
    path: Path
    max_tokens: int
    max_calls: int
    max_cost: Decimal
    usage: list[dict] = field(default_factory=list)
    pending: bool = False
    requests: int = 0
    seen: set[str] = field(default_factory=set)
    stopped: bool = False
    metrics: dict = field(default_factory=lambda: {name: 0 for name in BYTE_FIELDS})
    metric_seen: dict = field(default_factory=lambda: {name: 0 for name in BYTE_FIELDS})

    def record(self, event: dict):
        with self.path.open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(event, ensure_ascii=False) + "\n")
            stream.flush()
        self.path.chmod(0o600)

    def authorize(self, event: dict) -> bool:
        try:
            self.record(event)
            allowance = event.get("reservation", {})
            tokens = allowance.get("tokens")
            cost = allowance.get("reference_cost")
            if self.pending or self.stopped or self.requests >= self.max_calls:
                return False
            if type(tokens) is not int or tokens <= 0 or cost is None:
                return False
            try:
                reserved_cost = Decimal(str(cost))
            except ArithmeticError:
                return False
            if not reserved_cost.is_finite() or reserved_cost <= 0:
                return False
            totals = self.totals()
            used_tokens = sum(item["input_tokens"] + item["output_tokens"] for item in self.usage)
            if used_tokens + tokens > self.max_tokens or Decimal(totals["reference_cost"] or "0") + reserved_cost > self.max_cost:
                return False
            self.pending = True
            self.requests += 1
            return True
        except (OSError, ValueError, TypeError, AttributeError, ArithmeticError):
            return False

    def observe(self, event: dict):
        self.record(event)
        if event["type"] == "usage":
            self.observe_usage(event)
        elif event["type"] == "metrics":
            for name in BYTE_FIELDS:
                if name not in event:
                    continue
                value = event[name]
                if type(value) is not int or value < 0:
                    raise ValueError("Invalid measured counter")
                self.metrics[name] += value
                self.metric_seen[name] += 1

    def observe_usage(self, event):
        identifier = event.get("request_id")
        if not self.pending or not isinstance(identifier, str) or not identifier or identifier in self.seen:
            raise ValueError("Usage requires one new authorized provider request")
        for name in TOKEN_FIELDS:
            value = event.get(name)
            if value is not None and (type(value) is not int or value < 0):
                raise ValueError("Invalid provider token count")
        if event.get("input_tokens") is None or event.get("output_tokens") is None:
            raise ValueError("Provider did not supply core usage")
        cost = Decimal(str(event.get("reference_cost", "NaN")))
        if not cost.is_finite() or cost < 0:
            raise ValueError("Provider reference cost is unavailable")
        self.seen.add(identifier)
        if event.get("cached_input_tokens", 0) is not None and event.get("cached_input_tokens", 0) > event["input_tokens"]:
            raise ValueError("Cached input is a subset of input tokens")
        if event.get("reasoning_tokens", 0) is not None and event.get("reasoning_tokens", 0) > event["output_tokens"]:
            raise ValueError("Reasoning is a subset of output tokens")
        self.usage.append(event)
        self.pending = False
        used_tokens = sum(item["input_tokens"] + item["output_tokens"] for item in self.usage)
        used_cost = sum(Decimal(str(item["reference_cost"])) for item in self.usage)
        if used_tokens > self.max_tokens or used_cost > self.max_cost:
            self.stopped = True
            raise ValueError("Provider usage exceeded the private budget")

    def totals(self, since: int = 0, request_start: int = 0, metrics_start: dict | None = None) -> dict:
        totals = {}
        for name in TOKEN_FIELDS:
            values = [item.get(name) for item in self.usage[since:]]
            totals[name] = sum(values) if values and all(value is not None for value in values) and not self.pending else None
        costs = [Decimal(str(item["reference_cost"])) for item in self.usage[since:]]
        totals["reference_cost"] = str(sum(costs)) if costs and not self.pending else None
        totals["authorized_provider_requests"] = self.requests - request_start
        totals["model_calls"] = len(self.usage[since:]) if not self.pending else None
        for name, value in self.metrics.items():
            start = metrics_start or {}
            observed = self.metric_seen[name] > start.get("__seen__", {}).get(name, 0)
            totals[name] = value - start.get(name, 0) if observed else None
        totals["known_input_tokens"] = sum(item["input_tokens"] for item in self.usage[since:]) if self.usage[since:] else None
        totals["known_output_tokens"] = sum(item["output_tokens"] for item in self.usage[since:]) if self.usage[since:] else None
        return totals


def validate_provenance(route: Route, provenance: dict):
    required = ("provider", "version", "tool", "actions", "schema_sha256", "documentation", "implementation")
    if any(not provenance.get(name) for name in required):
        raise ValueError("Incomplete route provenance")
    if route == Route.COMPUTER and provenance["implementation"] != "authentic_provider":
        raise ValueError("A screenshot adapter is not authentic Computer Use")
    if not isinstance(provenance["actions"], list) or not all(isinstance(action, str) for action in provenance["actions"]):
        raise ValueError("The actual provider action set is required")
    if not provenance["documentation"].startswith("https://") or len(provenance["schema_sha256"]) != 64:
        raise ValueError("Provider documentation and exact schema fingerprint are required")


def prompt_hash(prompt: str) -> str:
    return digest(prompt.encode("utf-8"))
