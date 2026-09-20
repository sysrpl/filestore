#!/bin/bash
set -e

dotnet publish -c Release -r linux-x64 --self-contained -o "$HOME/.local/share/s3-file-explorer"

mkdir -p "$HOME/.local/share/applications"

# App icon for the Mint menu and other launchers.
ICON_DIR="$HOME/.local/share/icons/hicolor/256x256/apps"
mkdir -p "$ICON_DIR"
cp resources/icon.png "$ICON_DIR/s3-file-explorer.png"

cat > "$HOME/.local/share/applications/s3-file-explorer.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=S3 File Explorer
Comment=Browse and manage S3 files
Exec=$HOME/.local/share/s3-file-explorer/filestore
Path=$HOME/.local/share/s3-file-explorer
Icon=s3-file-explorer
Terminal=false
Categories=Network;FileTransfer;Utility;
StartupNotify=true
EOF

chmod +x "$HOME/.local/share/s3-file-explorer/filestore"
chmod +x "$HOME/.local/share/applications/s3-file-explorer.desktop"
update-desktop-database "$HOME/.local/share/applications"
gtk-update-icon-cache -q -t "$HOME/.local/share/icons/hicolor" 2>/dev/null || true
