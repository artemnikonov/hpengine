#!/bin/sh
# Development helper (not part of the game): serves installed HP1 / HP2 game folders from this Mac over HTTPS, so the
# iOS app built by Xcode downloads a missing game on first launch (hpengine/ios/ContentDownload.cs).
#
#   content-server.sh setup [--hp1 <game dir>] [--hp2 <game dir>]   write config, certificate, file lists; (re)start
#   content-server.sh manifest                                      rebuild the file lists after the folders change
#   content-server.sh stop | status
#
# Everything lives in ~/Library/Application Support/HPEngine Content Server (never in the repository). The server is
# Caddy, run by a LaunchAgent at login, on port 8443. Requests need the bearer token in app-config.json; the app
# trusts exactly this server's self-signed certificate (SHA-256 pin), no system CA. The Xcode build phase copies
# app-config.json into the app bundle as content-server.json.
set -eu

base="$HOME/Library/Application Support/HPEngine Content Server"
content="$base/content"
tls="$base/tls"
port=8443
label=org.hpengine.content-server
agent="$HOME/Library/LaunchAgents/$label.plist"

manifests() {
    for game in hp1 hp2; do
        rm -f "$content/$game.manifest"
        [ -e "$content/$game" ] || continue
        # One "size<TAB>relative path" line per file; Finder metadata is not game data.
        (cd "$content/$game/" && find -L . -type f ! -name .DS_Store -print | sed 's|^\./||' | LC_ALL=C sort |
            while IFS= read -r f; do printf '%s\t%s\n' "$(stat -L -f %z "$f")" "$f"; done) > "$content/$game.manifest.new"
        mv "$content/$game.manifest.new" "$content/$game.manifest"
        echo "$game: $(wc -l < "$content/$game.manifest" | tr -d ' ') files"
    done
}

link_game() {   # <hp1|hp2> <dir>
    [ -d "$2/System" ] && [ -d "$2/Maps" ] || { echo "$2 has no System and Maps folders" >&2; exit 1; }
    rm -f "$content/$1"
    ln -s "$(CDPATH= cd -- "$2" && pwd)" "$content/$1"
}

case "${1:-}" in
setup)
    shift
    mkdir -p "$content" "$tls" "$HOME/Library/LaunchAgents"
    while [ $# -gt 0 ]; do
        case "$1" in
            --hp1|--hp2) link_game "${1#--}" "$2"; shift 2 ;;
            *) echo "unknown option $1" >&2; exit 2 ;;
        esac
    done

    host="$(scutil --get LocalHostName).local"
    ip=$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || :)
    [ -f "$base/token" ] || { umask 077; openssl rand -hex 32 > "$base/token"; }
    token=$(cat "$base/token")

    # Self-signed server certificate; the app pins its SHA-256. Renewed when missing, older than ~1 year or
    # when the Mac's name or address changes.
    san="DNS:$host,DNS:localhost,IP:127.0.0.1${ip:+,IP:$ip}"
    if [ ! -f "$tls/cert.pem" ] || [ "$(cat "$tls/san" 2>/dev/null)" != "$san" ] ||
        ! openssl x509 -in "$tls/cert.pem" -noout -checkend 2592000 >/dev/null; then
        (umask 077; openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 397 \
            -subj "/CN=$host" -addext "subjectAltName=$san" -addext "extendedKeyUsage=serverAuth" \
            -keyout "$tls/key.pem" -out "$tls/cert.pem" 2>/dev/null)
        printf '%s' "$san" > "$tls/san"
    fi
    pin=$(openssl x509 -in "$tls/cert.pem" -outform der | shasum -a 256 | cut -d' ' -f1)

    manifests

    cat > "$base/Caddyfile" <<EOF
{
	admin off
	auto_https off
	default_sni $host
}
https://:$port {
	tls "$tls/cert.pem" "$tls/key.pem"
	@authorized header Authorization "Bearer $token"
	handle @authorized {
		root * "$content"
		file_server
	}
	respond 403
}
EOF
    # The simulator reaches the Mac at 127.0.0.1; a device by name, then by address.
    urls="\"https://127.0.0.1:$port\", \"https://$host:$port\"${ip:+, \"https://$ip:$port\"}"
    (umask 077; printf '{ "urls": [%s], "token": "%s", "certSha256": "%s" }\n' "$urls" "$token" "$pin" > "$base/app-config.json")

    caddy=$(command -v caddy) || { echo "caddy is not installed (brew install caddy)" >&2; exit 1; }
    cat > "$agent" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>$label</string>
    <key>ProgramArguments</key>
    <array>
        <string>$caddy</string><string>run</string>
        <string>--config</string><string>$base/Caddyfile</string><string>--adapter</string><string>caddyfile</string>
    </array>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><true/>
    <key>StandardOutPath</key><string>$base/caddy.log</string>
    <key>StandardErrorPath</key><string>$base/caddy.log</string>
</dict>
</plist>
EOF
    launchctl bootout "gui/$(id -u)/$label" 2>/dev/null || :
    launchctl bootstrap "gui/$(id -u)" "$agent"
    echo "Serving https://$host:$port (and 127.0.0.1${ip:+, $ip}); app config: $base/app-config.json"
    echo "Rebuild the app in Xcode to bake in a new config."
    ;;
manifest) manifests ;;
stop) launchctl bootout "gui/$(id -u)/$label" ;;
status) launchctl print "gui/$(id -u)/$label" | grep -E '^\s*(state|pid|last exit code)' ;;
*) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
