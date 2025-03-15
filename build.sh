#!/bin/bash

# Check if inotifywait is installed
if [ -z "$(which inotifywait)" ]; then
    echo "Install the inotify-tools package and try again."
    exit 1
fi

# Directory to monitor for changes
dir="./Source"
MOD=$(basename $PWD)

# Define the path to your solution file
solutionPath="Source/${MOD}.sln"

# Define an array of configurations
configurations=("Release v1.2" "Release v1.3" "Release v1.4" "Release v1.5")

function build() {
    # Loop through each configuration and build it
    for config in "${configurations[@]}"; do
        echo "Building for configuration: $config"
        dotnet msbuild "$solutionPath" /p:Configuration="$config" &
    done

    echo "All builds completed!"
}

build
cp -avf /rimworld/1.3/Mods/${MOD}/1.3 /rimworld/1.2/Mods/${MOD}
cp -avf /rimworld/1.4/Mods/${MOD}/1.4 /rimworld/1.2/Mods/${MOD}
cp -avf /rimworld/1.5/Mods/${MOD}/1.5 /rimworld/1.2/Mods/${MOD}
cp -auvf /rimworld/1.2/Mods/${MOD} /rimworld/1.3/Mods/
cp -auvf /rimworld/1.2/Mods/${MOD} /rimworld/1.4/Mods/
cp -auvf /rimworld/1.2/Mods/${MOD} /rimworld/1.5/Mods/


# Watch for changes to .cs files in the directory and subdirectories
inotifywait --recursive --monitor --format "%e %w%f" \
    --event close_write,move,create,delete $dir \
    --include '\.cs$' |
    while read changed; do
        echo "Detected change in $changed"
        build

        cp -avf /rimworld/1.3/Mods/${MOD}/1.3 /rimworld/1.2/Mods/${MOD}
        cp -avf /rimworld/1.4/Mods/${MOD}/1.4 /rimworld/1.2/Mods/${MOD}
        cp -avf /rimworld/1.5/Mods/${MOD}/1.5 /rimworld/1.2/Mods/${MOD}
    done

