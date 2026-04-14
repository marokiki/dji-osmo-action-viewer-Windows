#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:-0.2.0.0}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_EXE="/mnt/c/Users/segre/dotnet-sdk/dotnet.exe"
MSIX_TOOLKIT="/mnt/c/Users/segre/AppData/Local/Microsoft/WinGet/Packages/Microsoft.MSIX-Toolkit_Microsoft.Winget.Source_8wekyb3d8bbwe/MSIX-Toolkit.x64"
MAKEAPPX_WIN="$(wslpath -w "$MSIX_TOOLKIT/MakeAppx.exe")"
SIGNTOOL_WIN="$(wslpath -w "$MSIX_TOOLKIT/signtool.exe")"
TIMESTAMP_URL="${MSIX_TIMESTAMP_URL:-http://timestamp.digicert.com}"

DIST_DIR="$ROOT/dist"
PUBLISH_DIR="$DIST_DIR/msix-publish"
PACKAGE_DIR="$DIST_DIR/msix-root"
OUTPUT_DIR="$DIST_DIR/msix"
LOCAL_MSIX_WIN="C:\\Users\\segre\\AppData\\Local\\Temp\\DJI-Osmo-Action-Viewer-${VERSION}.msix"
MSIX_PATH="$OUTPUT_DIR/DJI-Osmo-Action-Viewer-${VERSION}.msix"
PROJECT_WIN="$(wslpath -w "$ROOT/OsmoActionViewer/OsmoActionViewer.csproj")"
PUBLISH_WIN="$(wslpath -w "$PUBLISH_DIR")"

PFX_PATH="${MSIX_SIGN_PFX_PATH:-}"
PFX_PASSWORD="${MSIX_SIGN_PFX_PASSWORD:-}"

if [[ -z "$PFX_PATH" || -z "$PFX_PASSWORD" ]]; then
  echo "MSIX_SIGN_PFX_PATH and MSIX_SIGN_PFX_PASSWORD must be set for production signing." >&2
  exit 1
fi

if [[ ! -f "$PFX_PATH" ]]; then
  echo "Signing certificate not found: $PFX_PATH" >&2
  exit 1
fi

PFX_WIN="$(wslpath -w "$PFX_PATH")"

rm -rf "$PUBLISH_DIR" "$PACKAGE_DIR" "$OUTPUT_DIR"
mkdir -p "$PUBLISH_DIR" "$PACKAGE_DIR/Assets" "$OUTPUT_DIR"

"$DOTNET_EXE" publish "$PROJECT_WIN" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$PUBLISH_WIN"

cp "$PUBLISH_DIR/OsmoActionViewer.exe" "$PACKAGE_DIR/OsmoActionViewer.exe"
cp -r "$ROOT/Packaging/MSIX/Assets/." "$PACKAGE_DIR/Assets/"
sed "s/Version=\"0.2.0.0\"/Version=\"${VERSION}\"/" "$ROOT/Packaging/MSIX/AppxManifest.xml" > "$PACKAGE_DIR/AppxManifest.xml"

cat > /tmp/build_msix_inner.ps1 <<PS
\$pack = Start-Process -FilePath '$MAKEAPPX_WIN' -ArgumentList @('pack','/d','$(wslpath -w "$PACKAGE_DIR")','/p','$LOCAL_MSIX_WIN','/o') -Wait -PassThru -NoNewWindow
if (\$pack.ExitCode -ne 0) { exit \$pack.ExitCode }
\$sign = Start-Process -FilePath '$SIGNTOOL_WIN' -ArgumentList @('sign','/fd','SHA256','/f','$PFX_WIN','/p','$PFX_PASSWORD','/tr','$TIMESTAMP_URL','/td','SHA256','$LOCAL_MSIX_WIN') -Wait -PassThru -NoNewWindow
if (\$sign.ExitCode -ne 0) { exit \$sign.ExitCode }
PS

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w /tmp/build_msix_inner.ps1)"
cp "/mnt/c/Users/segre/AppData/Local/Temp/DJI-Osmo-Action-Viewer-${VERSION}.msix" "$MSIX_PATH"

echo "Built MSIX: $MSIX_PATH"
echo "Signed with certificate: $PFX_PATH"
