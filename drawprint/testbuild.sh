#!/bin/sh
# 시험 빌드: 명령 이름 뒤에 T<n>, GUID 새로 → Rhino를 끄지 않고 새로 불러 볼 수 있음
# 사용: sh testbuild.sh 2   → 출력: build-test/drawprint_t2/drawprintT2.rhp
set -e
N="$1"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${2:-$HERE/build-test}/drawprint_t$N"
SRC="$OUT-src"
rm -rf "$OUT" "$SRC"; mkdir -p "$SRC" "$OUT"
cp "$HERE"/src/*.cs "$HERE"/src/*.csproj "$HERE"/src/*.jsx "$HERE"/src/*.json "$SRC"/
GUID=$(node -e 'console.log(require("crypto").randomUUID())')
sed -i "s/public const string Sfx = \"\";/public const string Sfx = \"T$N\";/; s/public const string Id = \"[^\"]*\";/public const string Id = \"$GUID\";/" "$SRC/Plugin.cs"
sed -i "s#<AssemblyName>drawprint</AssemblyName>#<AssemblyName>drawprintT$N</AssemblyName>#" "$SRC/DrawPrint.csproj"
cd "$SRC" && dotnet build -c Release -o "$OUT" 2>&1 | grep -E " error |오류 [1-9]|Build succeeded|경고 [0-9]+개|오류 [0-9]+개" | sort -u
ls "$OUT"/*.rhp
