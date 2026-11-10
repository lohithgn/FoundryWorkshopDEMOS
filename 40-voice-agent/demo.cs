#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false
#:include VoiceCore.cs

return await VoiceDemo.MainAsync(args);
