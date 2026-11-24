#!/bin/bash
# Installs Kapeyn for the current user (no admin password needed):
#   - builds Kapeyn.app
#   - copies it to ~/Applications
#   - turns on "Start at login"
#   - starts it
#
# Run:        bash install.sh
# Uninstall:  bash install.sh --uninstall
set -euo pipefail
cd "$(dirname "$0")"

DEST="$HOME/Applications/Kapeyn.app"
AGENT="$HOME/Library/LaunchAgents/com.kapeyn.Kapeyn.login.plist"

# Stop any running copy.
pkill -x Kapeyn 2>/dev/null || true
sleep 0.5

if [[ "${1:-}" == "--uninstall" ]]; then
    rm -f "$AGENT"
    rm -rf "$DEST"
    echo "Kapeyn uninstalled. (Settings remain; remove with:  defaults delete com.kapeyn.Kapeyn)"
    exit 0
fi

bash ./build.sh

mkdir -p "$HOME/Applications" "$HOME/Library/LaunchAgents"
rm -rf "$DEST"
ditto "dist/Kapeyn.app" "$DEST"

cat > "$AGENT" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.kapeyn.Kapeyn.login</string>
    <key>ProgramArguments</key>
    <array>
        <string>/usr/bin/open</string>
        <string>-a</string>
        <string>$DEST</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
</dict>
</plist>
PLIST

open "$DEST"
echo "Kapeyn installed to $DEST and started. It will also start automatically when you log in."
echo "Look for the coffee cup in the menu bar."
