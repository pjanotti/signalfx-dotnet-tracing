#!/bin/bash
# Modified by SignalFx
set -euxo pipefail

buildConfiguration=Release
VER=0.1.2
mkdir -p ~/tmp/sfx.net-$VER/

git clean -fdx

docker-compose run build
docker-compose run Profiler
docker-compose run package

cp ./deploy/linux/* ~/tmp/sfx.net-$VER/

git clean -fdx

docker-compose run build
docker-compose run Profiler.Alpine
docker-compose run package.alpine

cp ./deploy/linux/* ~/tmp/sfx.net-$VER/
