#!/bin/bash
set -e

dotnet publish -c Release -r linux-x64 --self-contained -o "$HOME/.local/share/s3-file-explorer"

mkdir -p "$HOME/.local/share/applications"

cat > "$HOME/.local/share/applications/s3-file-explorer.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=S3 File Explorer
Comment=Browse and manage S3 files
Exec=$HOME/.local/share/s3-file-explorer/filestore
Path=$HOME/.local/share/s3-file-explorer
Icon=folder-remote
Terminal=false
Categories=Network;FileTransfer;Utility;
StartupNotify=true
EOF

chmod +x "$HOME/.local/share/s3-file-explorer/filestore"
chmod +x "$HOME/.local/share/applications/s3-file-explorer.desktop"
update-desktop-database "$HOME/.local/share/applications"
