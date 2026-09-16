"""Offline checks for the OpenAPI-driven required-field handling in provision.py.

Uses the committed Dokploy OpenAPI snapshot if present, plus synthetic schemas,
so it needs no Dokploy instance. Point --spec at a spec dumped from your own
instance (GET /api/settings.getOpenApiDocument) to check against that exactly.
"""
import argparse
import importlib.util
import io
import json
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

spec_mod = importlib.util.spec_from_file_location(
    "prov", REPO_ROOT / "scripts" / "dokploy" / "provision.py"
)
prov = importlib.util.module_from_spec(spec_mod)
spec_mod.loader.exec_module(prov)

results = []


def check(label, ok, detail=""):
    results.append((ok, label, detail))


# --- schema_default -----------------------------------------------------------
CASES = [
    ({"anyOf": [{"type": "string"}, {"type": "null"}]}, None, "nullable string -> null"),
    ({"type": "string"}, "", "plain string -> empty"),
    ({"type": "boolean"}, False, "boolean -> False"),
    ({"type": "array", "items": {"type": "string"}}, [], "array -> []"),
    ({"type": "integer"}, 0, "integer -> 0"),
    ({"type": "object"}, {}, "object -> {}"),
    ({}, None, "unknown -> null"),
    ({"anyOf": [{"type": "boolean"}, {"type": "null"}]}, None, "nullable bool -> null"),
]
for schema, expected, label in CASES:
    got = prov.schema_default(schema)
    check(f"schema_default: {label}", got == expected and type(got) is type(expected),
          f"got {got!r}")


# --- required_fields + post() autofill ---------------------------------------
class Recorder(prov.Dokploy):
    """Captures payloads instead of sending them."""

    def __init__(self, spec):
        super().__init__("https://x.invalid", "k", dry_run=True)
        self.spec = spec
        self.sent = []

    def post(self, path, payload):
        out = super().post(path, payload)
        self.sent.append((path, dict(payload)))
        return out


SYNTHETIC = {
    "paths": {
        "/thing.save": {
            "post": {
                "requestBody": {
                    "content": {
                        "application/json": {
                            "schema": {
                                "required": ["id", "mode", "futureField", "futureFlag"],
                                "properties": {
                                    "id": {"type": "string"},
                                    "mode": {"type": "string"},
                                    "futureField": {
                                        "anyOf": [{"type": "string"}, {"type": "null"}]
                                    },
                                    "futureFlag": {"type": "boolean"},
                                },
                            }
                        }
                    }
                }
            }
        }
    }
}

buf = io.StringIO()
rec = Recorder(SYNTHETIC)
_stdout, sys.stdout = sys.stdout, buf
try:
    rec.post("/thing.save", {"id": "a", "mode": "dockerfile"})
finally:
    sys.stdout = _stdout

_, payload = rec.sent[0]
check("autofill adds missing required fields",
      payload.get("futureField") is None and payload.get("futureFlag") is False,
      f"payload={payload}")
check("autofill leaves supplied fields alone",
      payload["id"] == "a" and payload["mode"] == "dockerfile")
check("autofill is reported to the operator",
      "auto-filled futureField" in buf.getvalue(),
      buf.getvalue().strip().splitlines()[-1:] or "no output")

# Nothing missing -> nothing added, nothing printed.
buf2 = io.StringIO()
rec2 = Recorder(SYNTHETIC)
_stdout, sys.stdout = sys.stdout, buf2
try:
    rec2.post("/thing.save", {"id": "a", "mode": "m", "futureField": "x",
                              "futureFlag": True})
finally:
    sys.stdout = _stdout
check("no autofill when the payload is complete",
      "auto-filled" not in buf2.getvalue())

# No spec cached (dry run / selftest) -> autofill is a no-op, not a crash.
rec3 = Recorder(None)
buf3 = io.StringIO()
_stdout, sys.stdout = sys.stdout, buf3
try:
    rec3.post("/thing.save", {"id": "a"})
finally:
    sys.stdout = _stdout
check("no spec -> no autofill, no crash", rec3.sent[0][1] == {"id": "a"})

# Unknown path -> no-op.
check("unknown path -> no required fields",
      prov.Dokploy("https://x.invalid", "k", True).required_fields("/nope") == {})


# --- against a real spec ------------------------------------------------------
parser = argparse.ArgumentParser()
parser.add_argument("--spec", help="path to a Dokploy OpenAPI document")
args = parser.parse_args()

if args.spec:
    real = json.loads(Path(args.spec).read_text(encoding="utf-8"))
    client = prov.Dokploy("https://x.invalid", "k", True)
    client.spec = real
    for path in prov.REQUIRED_ENDPOINTS:
        req = client.required_fields(path)
        if not req:
            check(f"spec has {path}", False, "endpoint or schema missing")
        else:
            check(f"spec has {path}", True, f"{len(req)} required field(s)")
    build = client.required_fields("/application.saveBuildType")
    for field in ("herokuVersion", "railpackVersion"):
        if field in build:
            check(f"saveBuildType requires {field}", True,
                  f"default -> {prov.schema_default(build[field])!r}")
else:
    print("(no --spec given; skipped the live-schema checks)\n")

for ok, label, detail in results:
    print(f"{'PASS' if ok else 'FAIL':5} {label:52} {detail}")

failures = [r for r in results if not r[0]]
print(f"\n{len(results) - len(failures)}/{len(results)} passed")
sys.exit(1 if failures else 0)
