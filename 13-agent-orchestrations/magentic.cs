#!/usr/bin/env dotnet
#:property PublishAot=false
#:include Orchestration.cs

using AgentOrchestrations;

return await Demo.MainAsync(args, "magentic");
