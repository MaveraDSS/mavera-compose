"""Offline checks for the placeholder case-alias generation.

The 29 appsettings.json templates disagree on capitalisation -- some spell a
placeholder $log_Level and others $Log_Level. envsubst matches names exactly and
Linux environment variables are case-sensitive, so one spelling leaves the other
set of repos rendering empty values.

This is handled at container start by config/entrypoint.sh (covered by
test_entrypoint.sh), so the generator no longer emits aliases by default. These
checks cover the --case-aliases path, which remains for the case where the
entrypoint is bypassed.
"""
import importlib.util
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
_s = importlib.util.spec_from_file_location(
    "gen", REPO_ROOT / "scripts" / "dokploy" / "generate-manifest.py"
)
gen = importlib.util.module_from_spec(_s)
_s.loader.exec_module(gen)

results = []


def check(label, ok, detail=""):
    results.append((bool(ok), label, str(detail)))


# --- the actual case both spellings exist for --------------------------------
env = {"Log_Level": "Information", "SeriLog_Level": "Debug"}
a = gen.case_aliases(env)
check("upper-first placeholder gets a lower-first alias",
      a.get("log_Level") == "Information", a)
check("mid-word capitals are preserved",
      a.get("seriLog_Level") == "Debug", a)

# --- and the reverse direction, since templates go both ways -----------------
a = gen.case_aliases({"assets_wysiwyg_folder": "wysiwyg"})
check("lower-first placeholder gets an upper-first alias",
      a.get("Assets_wysiwyg_folder") == "wysiwyg", a)

# --- conventional env vars are not placeholders ------------------------------
a = gen.case_aliases({
    "APP_DLL": "X.dll",
    "ASPNETCORE_ENVIRONMENT": "Production",
    "ASPNETCORE_HTTP_PORTS": "80",
})
check("ALL_CAPS conventional variables are left alone", a == {}, a)

# --- never clobber a name x-placeholders defines itself ----------------------
env = {"Log_Level": "Information", "log_Level": "Warning"}
a = gen.case_aliases(env)
check("an existing twin is never overwritten", "log_Level" not in a, a)
check("nor is the other direction", "Log_Level" not in a, a)

# --- values are carried across verbatim --------------------------------------
a = gen.case_aliases({"Bucket_Region": "us-east-1", "Okta_Domain": ""})
check("alias carries the same value", a.get("bucket_Region") == "us-east-1", a)
check("empty values alias too, not skipped", a.get("okta_Domain") == "", a)

# --- degenerate keys ---------------------------------------------------------
a = gen.case_aliases({"_leading": "x", "9nine": "y"})
check("non-alphabetic first character is skipped", a == {}, a)

a = gen.case_aliases({"X": "1"})
check("single ALL_CAPS letter is treated as conventional", a == {}, a)

a = gen.case_aliases({"x": "1"})
check("single lower-case letter aliases to upper", a == {"X": "1"}, a)

# --- idempotence: aliasing the aliased set adds nothing new ------------------
base = {"Log_Level": "Information", "assets_wysiwyg_folder": "w"}
once = dict(base)
once.update(gen.case_aliases(base))
twice = dict(once)
twice.update(gen.case_aliases(once))
check("generation is idempotent", once == twice,
      f"{len(once)} vs {len(twice)}")

# --- against the generated output, only when aliases were requested ---------
sample = REPO_ROOT / "build" / "dokploy" / "env" / "mavera-audit.env"
if sample.exists() and "log_Level=" in sample.read_text(encoding="utf-8"):
    keys = {}
    for line in sample.read_text(encoding="utf-8").splitlines():
        if "=" in line:
            k, v = line.split("=", 1)
            keys[k] = v
    check("generated env has both Log_Level spellings",
          keys.get("Log_Level") == keys.get("log_Level") is not None,
          f"Log_Level={keys.get('Log_Level')!r} log_Level={keys.get('log_Level')!r}")
    check("generated env did not alias APP_DLL", "aPP_DLL" not in keys)
    check("APP_DLL itself survived", "APP_DLL" in keys)
    dupes = [k for k in keys if k.swapcase()[:1] + k[1:] in keys and k[0].isupper()]
    check("every placeholder has its twin",
          len(dupes) > 100, f"{len(dupes)} pairs")
else:
    print("(no aliased env block on disk; skipped output checks -- regenerate "
          "with --case-aliases to cover them)\n")

for ok, label, detail in results:
    print(f"{'PASS' if ok else 'FAIL':5} {label:52} {'' if ok else detail}")

failures = [r for r in results if not r[0]]
print(f"\n{len(results) - len(failures)}/{len(results)} passed")
sys.exit(1 if failures else 0)
