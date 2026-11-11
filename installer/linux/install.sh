#!/bin/sh
# Installs Nexus for the current user (feature 9): binary in ~/.local/opt/nexus, a launcher in ~/.local/bin and the
# desktop entry. No root. The database and its keys (libsecret) stay in ~/.local/share/Nexus and the user's keyring;
# uninstalling (rm -r ~/.local/opt/nexus) never removes them.
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
target="$HOME/.local/opt/nexus"

if ! command -v secret-tool >/dev/null 2>&1 && ! ldconfig -p 2>/dev/null | grep -q libsecret-1; then
    echo "Aviso: libsecret não encontrado. O Nexus guarda as chaves do banco no Secret Service (pacote libsecret-1-0)." >&2
fi

mkdir -p "$target" "$HOME/.local/bin" "$HOME/.local/share/applications"
cp -R "$here"/. "$target"/
rm -f "$target/install.sh"
chmod 755 "$target/erp_inteligente"
ln -sf "$target/erp_inteligente" "$HOME/.local/bin/nexus"
sed "s|@TARGET@|$target|g" "$here/nexus.desktop" > "$HOME/.local/share/applications/nexus.desktop"

echo "Nexus instalado em $target. Abra pelo menu de aplicativos ou com o comando: nexus"
