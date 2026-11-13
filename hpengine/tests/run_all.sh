#!/bin/sh
# ALL HPEngine FRONTEND TESTS (hpengine/tests/*): build and run each project; exit 0 if all return 0, otherwise 1 with the name.
# No window (AudioTests SdlTest uses the dummy driver). --selftest-gpu is not included (needs a GPU: audit/selftest_gpu_redtest.sh).
#   sh hpengine/tests/run_all.sh
cd "$(dirname "$0")" || exit 2
bad=0; n=0
# One parallel build of all projects (AllTests.proj); on failure fall back to building one by one,
# which names the project that does not build.
prebuilt=0; dotnet build AllTests.proj -m -nologo -v q > /dev/null 2>&1 && prebuilt=1
for p in */; do
  t=${p%/}; [ -f "$t/$t.csproj" ] || continue
  n=$((n + 1))
  if [ $prebuilt = 0 ] && ! (cd "$t" && dotnet build -nologo -v q > /dev/null 2>&1); then echo "  BUILD FAIL $t"; bad=1; continue; fi
  out=$(cd "$t" && timeout 1200 dotnet run --no-build 2>&1); rc=$?
  if [ $rc -ne 0 ]; then echo "  FAIL $t (rc=$rc): $(printf '%s\n' "$out" | tail -1 | cut -c1-120)"; bad=1; fi
done
[ $bad = 0 ] && { echo "FRONTEND TESTS PASS ($n projects)"; exit 0; }
echo "FRONTEND TESTS FAIL"; exit 1
