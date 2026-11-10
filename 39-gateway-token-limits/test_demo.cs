#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false
#:include GatewayCore.cs

return await GatewayDemo.MainAsync(["--self-test"]);
