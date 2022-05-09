ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd $ROOT

if [[ `uname` == 'Darwin' ]];then
  if [ ! -d $LOGGER ]; then
    mkdir -p '/Users/Shared/Logs/itsdb'
  fi
fi

if [[ `uname` != 'Darwin' ]];then
  if [ ! -d $LOGGER ]; then
    mkdir -p '/home/log4net/itsdb'
  fi
fi

if [ ! -d 'net6.0' ]; then
  tar -xvf itsdb-7.60.tar
fi

if [ ! -d 'net6.0' ]; then
  echo '缺少项目程序文件..'
  exit
fi

cd net5.0
docker stop itsdb
docker rm itsdb
docker rmi salvini/itsdb:1.530
docker build -t salvini/itsdb:1.530 .
if [[ `uname` == 'Darwin' ]];then
  docker run -d --name itsdb --restart always -p 1010:1010 --network network --ip 10.10.11.11 -v /Users/Shared/Logs/itsdb:/salvini/log4net salvini/itsdb:1.530
fi
if [[ `uname` != 'Darwin' ]];then
  docker run -d --name itsdb --restart always -p 1010:1010 --network network --ip 10.10.11.11 -v /home/log4net/itsdb:/salvini/log4net salvini/itsdb:1.530
fi
sleep 5
docker ps -a | grep itsdb
