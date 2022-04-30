ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd $ROOT

LOGGER='/home/log4net/itsdb'
if [[ `uname` == 'Darwin' ]];then
  LOGGER='/Users/Shared/Logs/itsdb'
fi

if [ ! -d $LOGGER ]; then
  mkdir -p $LOGGER 
fi

OUTPUT='net5.0'
if [ ! -d $OUTPUT ]; then
  tar -xvf itsdb-7.60.tar
fi

if [ ! -d $OUTPUT ]; then
  echo '缺少项目程序文件..'
  exit
fi

cd net5.0
docker stop itsdb
docker rm itsdb
docker rmi itsdb
docker build -t itsdb .
if [[ `uname` == 'Darwin' ]];then
  docker run -d --name itsdb --restart always -p 18810:18810 --network network --ip 10.10.188.10 -v /Users/Shared/Logs/itsdb:/salvini/log4net itsdb
fi
if [[ `uname` != 'Darwin' ]];then
  docker run -d --name itsdb --restart always -p 18810:18810 --network network --ip 10.10.188.10 -v /home/log4net/itsdb:/salvini/log4net itsdb
fi
sleep 5
docker ps -a | grep itsdb
