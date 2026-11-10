#!/usr/bin/env dotnet
#:property PublishAot=false
#:include LabLifecycle.cs

using AgentOrchestrations;

return await LabLifecycle.DeployMainAsync(args, Directory.GetCurrentDirectory());
