#!/bin/bash

if [ ! $1 ]; then
    echo "Error: The mod version is required."
    exit
fi

unix2dos /rimworld/1.2/Mods/CryoRegenesis/README.md
rm -vf /rimworld/1.2/Mods/CryoRegenesis-$1.zip
(cd /rimworld/1.2/Mods && zip -r CryoRegenesis-$1.zip CryoRegenesis && cp CryoRegenesis-$1.zip /tmp)

