#!/bin/bash

IS_WINDOWS=0
RIMWORLD1_2=/rimworld/1.2
RIMWORLD1_3=/rimworld/1.3

# Assume Windows (and NOT WSL2)...
if [ ! -z $WINDIR ]; then
    IS_WINDOWS=1
    RIMWORLD1_2=/c/RimWorld1-2-2900Win64
    RIMWORLD1_3=/c/RimWorld1-3-3162Win64
fi

for RIMHOME in $RIMWORLD1_2 $RIMWORLD1_3; do
	rsync -rcv --delete-after README.md About Defs Textures $RIMHOME/Mods/CryoRegenesis/
	unix2dos $RIMHOME/Mods/CryoRegenesis/README.md

    if [[ $IS_WINDOWS -eq 1 ]]; then
	    rsync -rcv --delete-after $RIMHOME/Mods/CryoRegenesis "/c/Program Files (x86)/Steam/steamapps/common/RimWorld/Mods/"
	fi
done


