#!/bin/sh
# Usage: sh scripts/check-linux-launch.sh /path/to/dotnet [published-directory]
# Run in a graphical Linux session. Exit 0 means the app survived the smoke interval.
runtime=${1:-dotnet}
published=${2:-artifacts/linux-x64}
timeout 5s "$runtime" "$published/DC.dll"
result=$?
if [ "$result" -eq 124 ]; then
    echo 'PASS: Linux GUI process remained running for 5 seconds without a startup crash.'
    exit 0
fi
echo "FAIL: Linux GUI process exited early (status $result)."
exit 1
