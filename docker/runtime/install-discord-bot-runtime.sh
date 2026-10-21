#!/bin/sh
set -eu

if [ ! -x /usr/local/bin/node ]; then
	echo "ERROR: the Node.js runtime is missing."
	exit 1
fi

if [ ! -d /app/tools/discord-bot/node_modules ]; then
	echo "ERROR: Discord bot production dependencies are missing."
	exit 1
fi

apt-get update
apt-get install -y --no-install-recommends ca-certificates libatomic1 libcap2
node --version
cd /app/tools/discord-bot
node --input-type=module -e "await import('discord.js'); await import('@microsoft/signalr')"

rm -rf /var/lib/apt/lists/* \
	/usr/share/man \
	/usr/share/info \
	/usr/share/doc
find /tmp -mindepth 1 -maxdepth 1 ! -name listenarr-runtime -exec rm -rf {} +
