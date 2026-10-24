#!/usr/bin/env bash
# Offline contract test: exercises only ensure_first_jump with shell function fakes.
# It never executes the guest policy or a real iptables/ip6tables/id command.
set -euo pipefail
script_dir=$(cd -- "$(dirname -- "$0")" && pwd)
eval "$(sed -n '/^ensure_first_jump()/,/^}/p' "$script_dir/guest-network-policy.sh")"
declare -A fake_rules
fake_misplace=false
fake_iptables() {
    local tool=$1; shift
    [ "$1" = -w ] && shift
    local operation=$1 chain=$2; shift 2
    local key="$tool|$chain" rule row removed=false new_rules=''
    case "$operation" in
        -C|-D)
            rule="-A $chain $*"
            while IFS= read -r row; do
                if [ "$row" = "$rule" ] && [ "$removed" = false ]; then removed=true; else
                    [ -z "$row" ] || new_rules+="${new_rules:+$'\n'}$row"
                fi
            done <<< "${fake_rules[$key]-}"
            [ "$removed" = true ] || return 1
            if [ "$operation" = -D ]; then fake_rules[$key]=$new_rules; fi
            ;;
        -I)
            [ "$1" = 1 ] || return 1; shift
            rule="-A $chain $*"
            if [ "$fake_misplace" = true ]; then
                fake_rules[$key]="${fake_rules[$key]-}${fake_rules[$key]:+$'\n'}$rule"
            else
                fake_rules[$key]="$rule${fake_rules[$key]:+$'\n'}${fake_rules[$key]-}"
            fi
            ;;
        -S) printf '%s\n' "-P $chain ACCEPT" "${fake_rules[$key]-}" ;;
        *) printf 'Unexpected fake operation: %s\n' "$operation" >&2; return 2 ;;
    esac
}
iptables() { fake_iptables iptables "$@"; }
ip6tables() { fake_iptables ip6tables "$@"; }

fake_rules['iptables|OUTPUT']=$'-A OUTPUT -j ACCEPT\n-A OUTPUT -j BOT_PHONE_OUT\n-A OUTPUT -j BOT_PHONE_OUT'
ensure_first_jump iptables OUTPUT BOT_PHONE_OUT
[ "${fake_rules['iptables|OUTPUT']}" = $'-A OUTPUT -j BOT_PHONE_OUT\n-A OUTPUT -j ACCEPT' ]
printf 'PASS existing ACCEPT and duplicate IPv4 jumps reordered safely\n'

fake_rules['ip6tables|OUTPUT']=$'-A OUTPUT -j ACCEPT\n-A OUTPUT -j BOT_PHONE6_OUTPUT'
ensure_first_jump ip6tables OUTPUT BOT_PHONE6_OUTPUT
[ "${fake_rules['ip6tables|OUTPUT']}" = $'-A OUTPUT -j BOT_PHONE6_OUTPUT\n-A OUTPUT -j ACCEPT' ]
printf 'PASS IPv6 jump placed before ACCEPT\n'

fake_misplace=true
if (ensure_first_jump iptables OUTPUT BOT_PHONE_OUT) >/dev/null 2>&1; then
    printf 'FAIL missing first-position enforcement\n' >&2; exit 1
fi
printf 'PASS readback refuses a jump that was not actually inserted first\n'
printf 'Offline network jump tests passed: 3. No real firewall or Android device was used.\n'
