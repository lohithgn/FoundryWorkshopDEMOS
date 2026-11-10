#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false
#:include GuardrailCore.cs

return await GuardrailDemo.MainAsync(args);
