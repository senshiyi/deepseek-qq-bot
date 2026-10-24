#!/system/bin/sh
# Operator-only, fixed guest script. Never expose an ADB/root/shell tool to the model.
set -eu
[ "$(id -u)" = 0 ] || { echo 'Guest root is required for network policy.' >&2; exit 1; }
command -v iptables >/dev/null
command -v ip6tables >/dev/null
ensure_first_jump() {
    tool="$1"
    source_chain="$2"
    target_chain="$3"
    # Remove only our exact jumps, including duplicates, then put ours before any ACCEPT.
    while "$tool" -w -C "$source_chain" -j "$target_chain" 2>/dev/null; do
        "$tool" -w -D "$source_chain" -j "$target_chain"
    done
    "$tool" -w -I "$source_chain" 1 -j "$target_chain"
    first_rule=$("$tool" -w -S "$source_chain" | sed -n '/^-A /{p;q;}')
    [ "$first_rule" = "-A $source_chain -j $target_chain" ] || {
        echo "Network jump is not first: $source_chain" >&2
        exit 1
    }
}
iptables -w -N BOT_PHONE_OUT 2>/dev/null || true
iptables -w -F BOT_PHONE_OUT
iptables -w -A BOT_PHONE_OUT -o lo -j ACCEPT
iptables -w -A BOT_PHONE_OUT -d 10.0.2.3/32 -p udp --dport 53 -j ACCEPT
iptables -w -A BOT_PHONE_OUT -d 10.0.2.3/32 -p tcp --dport 53 -j ACCEPT
iptables -w -A BOT_PHONE_OUT -d 255.255.255.255/32 -p udp --sport 68 --dport 67 -j ACCEPT
for network in 0.0.0.0/8 10.0.0.0/8 100.64.0.0/10 127.0.0.0/8 169.254.0.0/16 172.16.0.0/12 192.0.0.0/24 192.0.2.0/24 192.168.0.0/16 198.18.0.0/15 198.51.100.0/24 203.0.113.0/24 224.0.0.0/4 240.0.0.0/4; do
    iptables -w -A BOT_PHONE_OUT -d "$network" -j REJECT
done
iptables -w -A BOT_PHONE_OUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
iptables -w -A BOT_PHONE_OUT -p tcp -m multiport --dports 80,443 -j ACCEPT
iptables -w -A BOT_PHONE_OUT -p udp --dport 123 -j ACCEPT
iptables -w -A BOT_PHONE_OUT -j REJECT
ensure_first_jump iptables OUTPUT BOT_PHONE_OUT
iptables -w -N BOT_PHONE_IN 2>/dev/null || true
iptables -w -F BOT_PHONE_IN
iptables -w -A BOT_PHONE_IN -i lo -j ACCEPT
iptables -w -A BOT_PHONE_IN -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
iptables -w -A BOT_PHONE_IN -p udp --sport 67 --dport 68 -j ACCEPT
iptables -w -A BOT_PHONE_IN -j DROP
ensure_first_jump iptables INPUT BOT_PHONE_IN
for direction in OUTPUT INPUT FORWARD; do
    ip6tables -w -N "BOT_PHONE6_$direction" 2>/dev/null || true
    ip6tables -w -F "BOT_PHONE6_$direction"
    if [ "$direction" = OUTPUT ]; then ip6tables -w -A "BOT_PHONE6_$direction" -o lo -j ACCEPT; fi
    if [ "$direction" = INPUT ]; then ip6tables -w -A "BOT_PHONE6_$direction" -i lo -j ACCEPT; fi
    ip6tables -w -A "BOT_PHONE6_$direction" -j DROP
    ensure_first_jump ip6tables "$direction" "BOT_PHONE6_$direction"
done
# Readback proves the rules were accepted, not that all real connectivity tests passed.
iptables -w -C OUTPUT -j BOT_PHONE_OUT
iptables -w -C BOT_PHONE_OUT -d 10.0.0.0/8 -j REJECT
iptables -w -C BOT_PHONE_OUT -d 192.168.0.0/16 -j REJECT
ip6tables -w -C OUTPUT -j BOT_PHONE6_OUTPUT
iptables -w -S BOT_PHONE_OUT
ip6tables -w -S BOT_PHONE6_OUTPUT
echo 'BOT_PHONE_POLICY_READBACK_OK'
