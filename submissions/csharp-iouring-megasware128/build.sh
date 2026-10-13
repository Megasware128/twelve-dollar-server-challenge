#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet publish -c Release -r linux-x64 --disable-build-servers -p:UseSharedCompilation=false -p:RestoreLockedMode=true -p:TreatWarningsAsErrors=true -o bin/publish
bash sqlite/build.sh
cc -O3 -march=native -fPIC -shared transport/uring.c -luring -o bin/publish/libtwelve_transport.so
