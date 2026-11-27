#!/usr/bin/env bash
set -euo pipefail

if (( EUID == 0 )); then
    printf 'Run this installer as the desktop user.\n' >&2
    exit 1
fi

source_dir="$(cd -- "$(dirname -- "$0")" && pwd -P)"
app_dir="$HOME/.local/opt/deck-remote"
desktop_dir="$HOME/.local/share/applications"
install -d "$app_dir" "$desktop_dir"
release="$(mktemp -d "$app_dir/.release.XXXXXX")"
link="$release.link"
trap 'rm -f -- "$link"' EXIT
cp -R -- "$source_dir/app/." "$release/"
chmod 755 "$release" "$release/DeckRemote"
ln -s -- "$(basename -- "$release")" "$link"
mv -Tf -- "$link" "$app_dir/current"

install -m 644 "$source_dir/deck-remote.png" "$app_dir/deck-remote.png"
cat > "$desktop_dir/deck-remote.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Deck Remote
Exec="$app_dir/current/DeckRemote"
Icon=$app_dir/deck-remote.png
Terminal=false
Categories=Game;
EOF

printf 'Deck Remote: %s/current/DeckRemote\n' "$app_dir"
