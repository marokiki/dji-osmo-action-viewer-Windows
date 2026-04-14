#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:-0.2.0.0}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_EXE="/mnt/c/Users/segre/dotnet-sdk/dotnet.exe"
MSIX_TOOLKIT="/mnt/c/Users/segre/AppData/Local/Microsoft/WinGet/Packages/Microsoft.MSIX-Toolkit_Microsoft.Winget.Source_8wekyb3d8bbwe/MSIX-Toolkit.x64"
MAKEAPPX_WIN="$(wslpath -w "$MSIX_TOOLKIT/MakeAppx.exe")"
SIGNTOOL_WIN="$(wslpath -w "$MSIX_TOOLKIT/signtool.exe")"

DIST_DIR="$ROOT/dist"
PUBLISH_DIR="$DIST_DIR/msix-publish"
PACKAGE_DIR="$DIST_DIR/msix-root"
OUTPUT_DIR="$DIST_DIR/msix"
LOCAL_MSIX_WIN="C:\\Users\\segre\\AppData\\Local\\Temp\\DJI-Osmo-Action-Viewer-${VERSION}.msix"
MSIX_PATH="$OUTPUT_DIR/DJI-Osmo-Action-Viewer-${VERSION}.msix"
PFX_PATH="$ROOT/Packaging/MSIX/DJIOsmoActionViewer-TestCert.pfx"
CER_PATH="$ROOT/Packaging/MSIX/DJIOsmoActionViewer-TestCert.cer"
PROJECT_WIN="$(wslpath -w "$ROOT/OsmoActionViewer/OsmoActionViewer.csproj")"
PUBLISH_WIN="$(wslpath -w "$PUBLISH_DIR")"

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
\$publisher = 'CN=marokiki'
\$passwordPlain = 'dji-osmo-action-viewer'
\$password = ConvertTo-SecureString \$passwordPlain -AsPlainText -Force
\$cert = Get-ChildItem Cert:\\CurrentUser\\My | Where-Object { \$_.Subject -eq \$publisher } | Sort-Object NotAfter -Descending | Select-Object -First 1
if (\$null -eq \$cert) {
    \$cert = New-SelfSignedCertificate -Type Custom -Subject \$publisher -FriendlyName 'DJI Osmo Action Viewer Test Certificate' -KeyUsage DigitalSignature -CertStoreLocation 'Cert:\\CurrentUser\\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
}
Export-PfxCertificate -Cert \$cert -FilePath '$(wslpath -w "$PFX_PATH")' -Password \$password | Out-Null
Export-Certificate -Cert \$cert -FilePath '$(wslpath -w "$CER_PATH")' -Force | Out-Null
\$pack = Start-Process -FilePath '$MAKEAPPX_WIN' -ArgumentList @('pack','/d','$(wslpath -w "$PACKAGE_DIR")','/p','$LOCAL_MSIX_WIN','/o') -Wait -PassThru -NoNewWindow
if (\$pack.ExitCode -ne 0) { exit \$pack.ExitCode }
\$sign = Start-Process -FilePath '$SIGNTOOL_WIN' -ArgumentList @('sign','/fd','SHA256','/f','$(wslpath -w "$PFX_PATH")','/p',\$passwordPlain,'$LOCAL_MSIX_WIN') -Wait -PassThru -NoNewWindow
if (\$sign.ExitCode -ne 0) { exit \$sign.ExitCode }
PS

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w /tmp/build_msix_inner.ps1)"
cp "/mnt/c/Users/segre/AppData/Local/Temp/DJI-Osmo-Action-Viewer-${VERSION}.msix" "$MSIX_PATH"

echo "Built MSIX: $MSIX_PATH"
echo "Certificate: $CER_PATH"
