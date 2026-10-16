#!/usr/bin/env bash
# Tailnet edge firewall (D51). Installs at the top of Docker's DOCKER-USER
# chain the rule described in common.sh (tailnet_firewall_rule): every
# forwarded packet whose original destination is ENMA_EDGE_BIND_ADDRESS is
# dropped unless it arrived on tailscale0. ufw does not filter the ports
# Docker publishes; without this rule only the Cloud Firewall keeps a packet
# for the bind address that arrives on the public interface away from Caddy.
# Public mode: nothing to do.
#
# Idempotent: a run leaves exactly one rule tagged enma-tailnet-edge, for the
# current ENMA_EDGE_BIND_ADDRESS, and removes the others (an earlier address).
# enma-tailnet-firewall.service runs it before Docker, at boot and on every
# Docker restart. It creates DOCKER-USER when Docker has not created it yet;
# Docker keeps an existing chain and adds its jump from FORWARD when it
# starts. deploy.sh refuses the tailnet mode while the rule is missing
# (check_tailnet_firewall).
#
# Usage: sudo scripts/production/tailnet-firewall.sh (enma-tailnet-firewall.service runs it)
# Environment: ENMA_ENV_FILE (default /etc/enma/production.env).

# shellcheck source=scripts/production/common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"

((EUID == 0)) || die "tailnet-firewall.sh must run as root"
require_commands flock
assert_secure_file "$ENMA_ENV_FILE"
resolve_edge_mode
if [[ $ENMA_RESOLVED_EDGE_MODE != tailnet ]]; then
    log "public mode: skip"
    exit 0
fi

require_commands iptables
on_exit() {
    local status=$?
    trap - EXIT
    ((status == 0)) || log "TAILNET FIREWALL FAIL (exit $status)"
    exit "$status"
}
trap on_exit EXIT

exec 9>/run/lock/enma-tailnet-firewall.lock  # shared with manual runs
flock --wait 30 9 || die "another tailnet firewall run did not finish within 30 s"

tailnet_firewall_rule
address=$ENMA_RESOLVED_EDGE_ADDRESS

if ! iptables -w -n -L DOCKER-USER >/dev/null 2>&1; then
    iptables -w -N DOCKER-USER || die "could not create the DOCKER-USER chain"
    log "created DOCKER-USER; Docker adds its jump from FORWARD when it starts"
fi
if ! iptables -w -C DOCKER-USER "${ENMA_TAILNET_FIREWALL_RULE[@]}" 2>/dev/null; then
    iptables -w -I DOCKER-USER 1 "${ENMA_TAILNET_FIREWALL_RULE[@]}" ||
        die "could not insert the rule into DOCKER-USER"
    log "rule inserted at the top of DOCKER-USER"
fi

# The current rule is in place before anything is removed. A tagged rule for
# another address goes (deleted by its own specification); a duplicate of the
# current rule goes too.
tagged_rules() {
    iptables -w -S DOCKER-USER | grep -F -- "--comment $ENMA_TAILNET_FIREWALL_COMMENT " || true
}
rules=$(tagged_rules)
current=0
while IFS= read -r line; do
    [[ -n $line ]] || continue
    if [[ " $line " == *" --ctorigdst $address "* || " $line " == *" --ctorigdst $address/32 "* ]]; then
        current=$((current + 1))
        continue
    fi
    read -r -a rule <<<"$line"
    rule[0]=-D
    # iptables-nft prints a /32 conntrack address without its mask, but
    # matches the rule back only with it.
    for index in "${!rule[@]}"; do
        if [[ ${rule[index]} == --ctorigdst && ${rule[index + 1]} != */* ]]; then
            rule[index + 1]+=/32
        fi
    done
    iptables -w "${rule[@]}" || die "could not remove an earlier tagged rule from DOCKER-USER"
    log "removed a tagged rule for an earlier bind address"
done <<<"$rules"
while ((current > 1)); do
    iptables -w -D DOCKER-USER "${ENMA_TAILNET_FIREWALL_RULE[@]}" || die "could not remove a duplicate rule"
    current=$((current - 1))
done

iptables -w -C DOCKER-USER "${ENMA_TAILNET_FIREWALL_RULE[@]}" 2>/dev/null ||
    die "the rule is not in DOCKER-USER after the run"
count=$(tagged_rules | grep -c -- '-j DROP' || true)
((count == 1)) || die "expected exactly one tagged rule in DOCKER-USER, found $count"
if ! iptables -w -C FORWARD -j DOCKER-USER 2>/dev/null; then
    log "FORWARD does not jump to DOCKER-USER yet; Docker adds the jump when it starts"
fi
log "TAILNET FIREWALL PASS"
