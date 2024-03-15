#!/bin/bash

if [ ! $1 ]; then
    echo "Error: The mod version is required."
    exit
fi

rm -rf /rimworld/1.2/Mods/CryoRegenesis

unix2dos README.md
rsync -rcv --delete-after README.md CryoRegenesis/About CryoRegenesis/Defs CryoRegenesis/Textures  /rimworld/1.2/Mods/CryoRegenesis/
rsync -rcv --delete-after README.md CryoRegenesis/About CryoRegenesis/Defs CryoRegenesis/Textures  /rimworld/1.3/Mods/CryoRegenesis/
rsync -rcv --delete-after README.md CryoRegenesis/About CryoRegenesis/Defs CryoRegenesis/Textures  /rimworld/1.4/Mods/CryoRegenesis/
rsync -rcv --delete-after README.md CryoRegenesis/About CryoRegenesis/Defs CryoRegenesis/Textures  /rimworld/1.5/Mods/CryoRegenesis/
rsync -rcv --delete-after README.md CryoRegenesis/About CryoRegenesis/Defs CryoRegenesis/Textures  $HOME/.steam/steam/steamapps/common/RimWorld/Mods/CryoRegenesis/
rsync -rcv /rimworld/1.2/Mods/CryoRegenesis/1.2 $HOME/.steam/steam/steamapps/common/RimWorld/Mods/CryoRegenesis/
rsync -rcv /rimworld/1.3/Mods/CryoRegenesis/1.3 $HOME/.steam/steam/steamapps/common/RimWorld/Mods/CryoRegenesis/
rsync -rcv /rimworld/1.4/Mods/CryoRegenesis/1.4 $HOME/.steam/steam/steamapps/common/RimWorld/Mods/CryoRegenesis/
rsync -rcv /rimworld/1.4/Mods/CryoRegenesis/1.5 $HOME/.steam/steam/steamapps/common/RimWorld/Mods/CryoRegenesis/

rsync -rcv --delete-after $HOME/.steam/steam/steamapps/common/RimWorld/Mods/CryoRegenesis /rimworld/1.2/Mods
dos2unix README.md
(cd /rimworld/1.2/Mods && zip -r CryoRegenesis-$1.zip CryoRegenesis && cp CryoRegenesis-$1.zip /tmp)

