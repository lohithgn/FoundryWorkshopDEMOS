#!/usr/bin/env dotnet
#:property PublishAot=false
#:include LabLifecycle.cs

using AgentOrchestrations;

return LabLifecycle.CleanupMain(args, Directory.GetCurrentDirectory());
