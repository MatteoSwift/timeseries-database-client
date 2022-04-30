ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd $ROOT
dotnet build -o ../dist/docker/net5.0

echo '
{
  "iTSDB": {
    "Driver": "TSDB.Core.IoTDriver",
    "TSDB.Core.IoTDriver": "iotdb://root:admin#123@10.10.0.14:6667/?appName=iTSDB",
    "TSDB.Core.MongoDriver": "mongodb://root:admin#123@10.10.0.16:27017/admin?appName=iTSDB&connectTimeoutMS=1200&serverSelectionTimeoutMS=1500"
  }
}' >../dist/docker/net5.0/appsettings.tsdb.json

echo '
{
  "$schema": "https://steeltoe.io/schema/latest/schema.json",
  "Spring": {
    "Application": {
      "Name": "itsdb"
    }
  },
  "Eureka": {
    "Client": {
      "EurekaServerServiceUrls": "http://10.10.0.5:8761/eureka/",
      "ShouldRegisterWithEureka": true,
      "ShouldFetchRegistry": true,
      "ValidateCertificates": false,
      "RegistryFetchIntervalSeconds": 10
    },
    "Instance": {
      "InstanceId": "10.10.188.10:18810", 
      "PreferIpAddress": true,
      "LeaseRenewalIntervalInSeconds": 5,
      "LeaseExpirationDurationInSeconds": 15
    }
  }
}' >../dist/docker/net5.0/appsettings.steeltoe.json

echo '
FROM mcr.microsoft.com/dotnet/sdk:5.0
WORKDIR /salvini
COPY . /salvini
EXPOSE 18810
ENTRYPOINT ["dotnet", "TSDB.Web.dll", "--urls=http://*:18810"]
' >../dist/docker/net5.0/Dockerfile

cp installer.sh ../dist/docker/installer.sh

cd ../dist/docker
tar -czvf itsdb-7.60.tar net5.0/*
rm -rf net5.0
cd $ROOT
