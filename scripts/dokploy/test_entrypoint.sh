#!/bin/sh
# Exercises config/entrypoint.sh against the failure modes that actually bit us.
# Needs only sh, grep and envsubst (gettext). Run from the repo root:
#     sh scripts/dokploy/test_entrypoint.sh
set -u

SCRIPT=config/entrypoint.sh
PASS=0
FAIL=0
SKIP=0

# Are environment variable lookups case-sensitive here? They are on Linux (the
# deploy target) but NOT on Windows/MSYS, where envsubst resolves $log_Level
# from Log_Level and the case-mismatch behaviour cannot be demonstrated.
# Probe envsubst itself, not shell expansion: shell variables are
# case-sensitive everywhere, but envsubst.exe on Windows resolves names through
# the case-insensitive Win32 environment.
CASE_SENSITIVE_ENV=yes
probe=$(printf '%s' '$mavera_caseprobe' | MAVERA_CASEPROBE=resolved envsubst 2>/dev/null || true)
[ "$probe" = "resolved" ] && CASE_SENSITIVE_ENV=no

if ! command -v envsubst >/dev/null 2>&1; then
    echo "SKIP: envsubst (gettext) not installed"
    exit 0
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# The script execs `dotnet "$APP_DLL"`; stub it so the test can observe the run.
mkdir -p "$work/bin"
cat > "$work/bin/dotnet" <<'STUB'
#!/bin/sh
echo "STUB_DOTNET_RAN $*"
STUB
chmod +x "$work/bin/dotnet"

skip() {
    printf 'SKIP  %s\n      %s\n' "$1" "$2"
    SKIP=$((SKIP + 1))
}

report() {
    if [ "$2" = "0" ]; then
        printf 'PASS  %s\n' "$1"
        PASS=$((PASS + 1))
    else
        printf 'FAIL  %s\n      %s\n' "$1" "$3"
        FAIL=$((FAIL + 1))
    fi
}

setup() {
    d="$work/case$1"
    mkdir -p "$d/app"
    echo "$d"
}

# --- 1. the base file is rendered, and the app is started --------------------
d=$(setup 1)
printf '{"Logging":{"Level":"$Log_Level"}}' > "$d/app/appsettings.json"
# The script targets /app, which the test cannot create without root. Rewrite
# just those two paths to point at a throwaway directory; everything else about
# the script under test is unchanged.
sed 's#^BASE=/app/appsettings.json#BASE=$MAVERA_APP_DIR/appsettings.json#; s#/app/appsettings\*\.json#$MAVERA_APP_DIR/appsettings*.json#' \
    "$SCRIPT" > "$work/entrypoint.testable.sh"

run_testable() {
    appdir=$1
    shift
    env PATH="$work/bin:$PATH" MAVERA_APP_DIR="$appdir" APP_DLL=Test.dll "$@" \
        sh "$work/entrypoint.testable.sh" 2>&1 || true
}

out=$(run_testable "$d/app" Log_Level=Information)
echo "$out" | grep -q 'STUB_DOTNET_RAN Test.dll' \
    && echo "$out" | grep -q 'rendered:' \
    && grep -q '"Information"' "$d/app/appsettings.json"
report "base appsettings.json is rendered and the app starts" $? "$out"

# --- 2. THE regression: an environment overlay is rendered too ---------------
d=$(setup 2)
printf '{"Logging":{"Level":"$log_Level"}}' > "$d/app/appsettings.json"
printf '{"Logging":{"Level":"$Log_Level"}}' > "$d/app/appsettings.Production.json"
out=$(run_testable "$d/app" log_Level=Information Log_Level=Information)
grep -q '"Information"' "$d/app/appsettings.Production.json"
report "appsettings.Production.json is rendered, not just the base file" $? "$out"
echo "$out" | grep -q 'appsettings.Production.json'
report "the overlay is named in the rendered list" $? "$out"

# --- 3. the quiet failure: a placeholder whose variable is not set -----------
d=$(setup 3)
printf '{"a":"$Log_Level","b":"$Totally_Absent"}' > "$d/app/appsettings.json"
out=$(run_testable "$d/app" Log_Level=Information)
echo "$out" | grep -q 'UNSET' && echo "$out" | grep -q 'Totally_Absent'
report "an unset placeholder is named in the UNSET report" $? "$out"
echo "$out" | grep -q '\$Log_Level$'
if [ $? -eq 0 ]; then
    report "a SET placeholder is not reported as unset" 1 "$out"
else
    report "a SET placeholder is not reported as unset" 0 ""
fi
grep -q '"b":""' "$d/app/appsettings.json"
report "an unset placeholder renders as an empty string, not a literal" $? "$out"

# --- 4. case sensitivity is real --------------------------------------------
d=$(setup 4)
printf '{"a":"$log_Level"}' > "$d/app/appsettings.json"
out=$(run_testable "$d/app" Log_Level=Information)
if [ "$CASE_SENSITIVE_ENV" = yes ]; then
    grep -q '"a":""' "$d/app/appsettings.json"
    report "wrong-case variable does NOT satisfy a placeholder" $? "$out"
else
    skip "wrong-case variable does NOT satisfy a placeholder" \
         "environment lookups are case-insensitive here; this is a Linux behaviour"
fi
# The fallback uses shell parameter expansion, which is case-sensitive even
# where the environment is not, so it engages on either platform.
echo "$out" | grep -q 'case-matched:.*log_Level<-Log_Level'
report "the mismatch is resolved from the other spelling, not left unset" $? "$out"
echo "$out" | grep -q 'UNSET'
if [ $? -eq 0 ]; then
    report "a case-matched placeholder is not also reported UNSET" 1 "$out"
else
    report "a case-matched placeholder is not also reported UNSET" 0 ""
fi

# --- 5. capitalisation is reconciled from the other spelling -----------------
# The repos disagree: some templates say $log_Level, others $Log_Level, and
# x-placeholders can only define one. The unset spelling must be satisfied from
# the other one rather than rendering empty.
d=$(setup 5b)
printf '{"a":"$log_Level"}' > "$d/app/appsettings.json"
out=$(run_testable "$d/app" Log_Level=Information)
grep -q '"a":"Information"' "$d/app/appsettings.json"
report "lower-first placeholder satisfied by the upper-first variable" $? "$out"
echo "$out" | grep -q 'case-matched'
report "the case match is reported" $? "$out"

d=$(setup 5c)
printf '{"a":"$Bucket_Region"}' > "$d/app/appsettings.json"
out=$(run_testable "$d/app" bucket_Region=eu-west-1)
grep -q '"a":"eu-west-1"' "$d/app/appsettings.json"
report "upper-first placeholder satisfied by the lower-first variable" $? "$out"

d=$(setup 5d)
printf '{"a":"$Neither_Spelling"}' > "$d/app/appsettings.json"
out=$(run_testable "$d/app" Unrelated=x)
echo "$out" | grep -q 'Neither_Spelling'
report "a placeholder with neither spelling set is still reported UNSET" $? "$out"

# --- 6. hard failures -------------------------------------------------------
d=$(setup 5)
out=$(run_testable "$d/app" Log_Level=x)
echo "$out" | grep -q 'FATAL'
report "a missing appsettings.json is fatal" $? "$out"
echo "$out" | grep -q 'STUB_DOTNET_RAN'
if [ $? -eq 0 ]; then
    report "the app is NOT started when rendering failed" 1 "$out"
else
    report "the app is NOT started when rendering failed" 0 ""
fi

printf '\n%s/%s passed' "$PASS" "$((PASS + FAIL))"
[ "$SKIP" -gt 0 ] && printf ', %s skipped' "$SKIP"
printf '\n'
[ "$CASE_SENSITIVE_ENV" = no ] && printf 'note: run this on Linux for full coverage\n'
[ "$FAIL" -eq 0 ]
