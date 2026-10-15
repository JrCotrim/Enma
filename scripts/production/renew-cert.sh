#!/usr/bin/env bash
# Renews the tailnet edge certificate (D40, D41) and reloads Caddy without a
# restart. Public mode: nothing to do. `tailscale cert` writes the node's
# certificate for ENMA_PUBLIC_HOST; Tailscale itself decides when a new one is
# issued, so most runs find the same certificate and change nothing.
#
# This script owns the layout of ENMA_EDGE_TLS_DIR (root, mode 700 or 750):
#   generation.XXXXXXXXXX/   one issued pair: tls.crt (644) and tls.key (600);
#   current                  link to the generation in use;
#   tls.crt, tls.key         links to current/tls.crt and current/tls.key, the
#                            paths Caddyfile.tailnet loads.
# A new pair is written into a new generation on the same filesystem and
# checked (the key matches, the subjectAltName names ENMA_PUBLIC_HOST, not
# expired); renaming one new `current` link over the old one then publishes
# the certificate and the key together. Caddy reloads only when the
# certificate changed, and the edge must then serve the new one; otherwise
# `current` goes back to the previous generation. The previous generation is
# kept; older ones are removed. An unchanged certificate still reloads Caddy
# when the edge serves another one (a run stopped between the swap and the
# reload).
#
# Usage: sudo scripts/production/renew-cert.sh (enma-renew-cert.timer runs it daily)
# Environment: ENMA_ENV_FILE (default /etc/enma/production.env).

# shellcheck source=scripts/production/common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"

((EUID == 0)) || die "renew-cert.sh must run as root"
require_commands docker flock
assert_secure_file "$ENMA_ENV_FILE"
resolve_edge_mode
if [[ $ENMA_RESOLVED_EDGE_MODE != tailnet ]]; then
    log "public mode: skip"
    exit 0
fi

require_commands tailscale openssl mktemp timeout
host=$(require_env_value "$ENMA_ENV_FILE" ENMA_PUBLIC_HOST)
[[ $host =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$ ]] ||
    die "ENMA_PUBLIC_HOST must be the node's MagicDNS name in lower case"
dir=$(env_value "$ENMA_ENV_FILE" ENMA_EDGE_TLS_DIR)
address=$ENMA_RESOLVED_EDGE_ADDRESS

new=''
published=false
on_exit() {
    local status=$?
    trap - EXIT
    [[ $published == true || -z $new ]] || rm -rf -- "$new"
    rm -f -- "$dir/current.new"
    ((status == 0)) || log "CERTIFICATE RENEWAL FAIL (exit $status)"
    exit "$status"
}
trap on_exit EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

exec 9>/run/lock/enma-renew-cert.lock  # outside PrivateTmp, shared with manual runs
flock --nonblock 9 || die "another certificate renewal is running"

if [[ ! -e $dir && ! -L $dir ]]; then
    mkdir -m 700 -- "$dir"
    log "created $dir"
fi
[[ -d $dir && ! -L $dir ]] || die "$dir is not a directory"
owner_mode=$(stat -c '%u %g %a' -- "$dir")
[[ $owner_mode == "0 0 700" || $owner_mode == "0 0 750" ]] ||
    die "$dir must be root:root with mode 700 or 750 (found $owner_mode)"
for file in tls.crt tls.key; do
    [[ ! -e $dir/$file && ! -L $dir/$file ]] ||
        [[ -L $dir/$file && $(readlink -- "$dir/$file") == "current/$file" ]] ||
        die "$dir/$file is not a link to current/$file; move it out of $dir and run again"
done
previous=''
if [[ -L $dir/current ]]; then
    previous=$(readlink -- "$dir/current")
    [[ $previous =~ ^generation\.[A-Za-z0-9]{10}$ && -d $dir/$previous ]] ||
        die "$dir/current does not point to a generation"
elif [[ -e $dir/current ]]; then
    die "$dir/current is not a link"
fi

fingerprint() {
    local output
    output=$(openssl x509 -in "$1" -noout -fingerprint -sha256 2>/dev/null) || return 1
    printf '%s' "${output#*=}"
}

# SHA-256 fingerprint of the certificate the edge serves for ENMA_PUBLIC_HOST.
served_fingerprint() {
    timeout 10 openssl s_client -connect "$address:443" -servername "$host" </dev/null 2>/dev/null |
        openssl x509 -noout -fingerprint -sha256 2>/dev/null | sed -n 's/^.*=//p'
}

reload_caddy() {
    compose exec -T caddy caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile --force
}

# Waits up to 30 s for the edge to serve the certificate with fingerprint $1.
edge_serves() {
    local deadline=$((SECONDS + 30))
    until [[ $(served_fingerprint || true) == "$1" ]]; do
        ((SECONDS < deadline)) || return 1
        sleep 2
    done
}

# Points `current` at a generation with one rename(2).
publish() {
    ln -s -- "$1" "$dir/current.new"
    mv -T -- "$dir/current.new" "$dir/current"
}

# Puts `current` back on the previous generation, if there is one; the new
# generation is then removed on exit.
rollback() {
    [[ -n $previous ]] || return 1
    publish "$previous"
    published=false
    log "$dir/current is back on the previous generation"
}

current_fingerprint=''
if [[ -n $previous ]] && check_certificate_pair "$dir/current/tls.crt" "$dir/current/tls.key" "$host" 0; then
    current_fingerprint=$(fingerprint "$dir/current/tls.crt")
elif [[ -n $previous ]]; then
    log "the current pair is not usable; it will be replaced"
fi

new=$(mktemp -d -- "$dir/generation.XXXXXXXXXX")
tailscale cert --cert-file "$new/tls.crt" --key-file "$new/tls.key" "$host" ||
    die "tailscale cert failed; the current pair is unchanged"
[[ -f $new/tls.crt && ! -L $new/tls.crt && -f $new/tls.key && ! -L $new/tls.key ]] ||
    die "tailscale cert did not write both files; the current pair is unchanged"
chown root:root -- "$new" "$new/tls.crt" "$new/tls.key"
chmod 700 -- "$new"
chmod 644 -- "$new/tls.crt"
chmod 600 -- "$new/tls.key"
check_certificate_pair "$new/tls.crt" "$new/tls.key" "$host" 0 ||
    die "the new certificate is not usable; the current pair is unchanged"
new_fingerprint=$(fingerprint "$new/tls.crt")

project=$(compose_project)
caddy=$(running_service_container "$project" caddy)
if [[ $new_fingerprint == "$current_fingerprint" && -L $dir/tls.crt && -L $dir/tls.key ]]; then
    if [[ -z $caddy || $(served_fingerprint || true) == "$new_fingerprint" ]]; then
        log "certificate unchanged (sha256 $new_fingerprint); no reload"
        exit 0
    fi
    log "certificate unchanged, but the edge serves another one; reloading caddy"
    reload_caddy || die "caddy reload failed"
    edge_serves "$new_fingerprint" || die "the edge does not serve the current certificate"
    log "caddy reloaded; the edge serves the current certificate"
    exit 0
fi

new_name=${new##*/}
# Set first: a signal right after the rename must not remove what `current`
# points to.
published=true
publish "$new_name"
for file in tls.crt tls.key; do
    if [[ ! -L $dir/$file ]]; then
        ln -s -- "current/$file" "$dir/$file.new"
        mv -T -- "$dir/$file.new" "$dir/$file"
    fi
done
log "new certificate published (sha256 $new_fingerprint, generation $new_name)"

if [[ -z $caddy ]]; then
    log "WARNING: caddy is not running; it loads the new certificate when it starts"
else
    # A failed reload leaves Caddy on the configuration it already runs.
    reload_caddy || { rollback || true; die "caddy reload failed"; }
    if ! edge_serves "$new_fingerprint"; then
        if rollback; then
            reload_caddy || log "WARNING: caddy reload with the previous generation failed"
        fi
        die "the edge does not serve the new certificate"
    fi
    log "caddy reloaded; the edge serves the new certificate"
fi

for generation in "$dir"/generation.*; do
    [[ -d $generation && ! -L $generation ]] || continue
    case ${generation##*/} in
        "$new_name" | "$previous") ;;
        *) rm -rf -- "$generation" ;;
    esac
done
log "CERTIFICATE RENEWAL PASS"
