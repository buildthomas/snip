#!/usr/bin/env bash
# Optional: creates a local self-signed code signing identity "Snip Local Signing" in the login keychain.
# With a stable identity, macOS keeps the Screen Recording permission across rebuilds; with ad-hoc signing every
# rebuild looks like a new app. install.sh picks this identity up automatically.
set -euo pipefail
NAME="Snip Local Signing"
if security find-identity -p codesigning | grep -q "$NAME"; then
    echo "'$NAME' already exists."
    exit 0
fi
TMP="$(mktemp -d)"
cat > "$TMP/cfg" <<EOF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $NAME
[ext]
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
basicConstraints = critical, CA:false
EOF
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$TMP/key.pem" -out "$TMP/cert.pem" -days 3650 -config "$TMP/cfg"
openssl pkcs12 -export -inkey "$TMP/key.pem" -in "$TMP/cert.pem" -out "$TMP/id.p12" -passout pass:snip -name "$NAME"
security import "$TMP/id.p12" -k "$HOME/Library/Keychains/login.keychain-db" -P snip -T /usr/bin/codesign
rm -rf "$TMP"
echo "Created '$NAME'."
echo "If codesign later reports it as not trusted: Keychain Access > login > Certificates > '$NAME' > Trust > Code Signing: Always Trust."
