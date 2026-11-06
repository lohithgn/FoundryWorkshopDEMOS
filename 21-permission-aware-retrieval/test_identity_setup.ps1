#Requires -Version 7.0
$ErrorActionPreference = "Stop"

# All identities and Graph responses below are synthetic offline fixtures.
$tenant = "11111111-1111-1111-1111-111111111111"
$operatorId = "22222222-2222-2222-2222-222222222222"
$newUserId = "33333333-3333-3333-3333-333333333333"
$roleId = "fe930be7-5e62-47db-91af-98c3a49a38b1"
$activate = Join-Path $PSScriptRoot "activate_user_admin.ps1"
$create = Join-Path $PSScriptRoot "create_test_user.ps1"

function Assert($Condition, [string] $Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Expect-Failure([scriptblock] $Action, [string] $Pattern) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_ }
    Assert ($null -ne $failure) "Expected failure matching '$Pattern'"
    Assert ($failure.Exception.Message -match $Pattern) "Unexpected error: $($failure.Exception.Message)"
}

function Reset-Fakes {
    $global:Lab21IdentityTest = @{
        mode = "eligible"
        posts = @()
        connections = 0
        disconnects = 0
        passwordPrompts = 0
        scopes = @()
        eligibility = @{
            principalId = $operatorId
            roleDefinitionId = $roleId
            directoryScopeId = "/"
            startDateTime = [DateTimeOffset]::UtcNow.AddDays(-1).ToString("o")
            endDateTime = [DateTimeOffset]::UtcNow.AddDays(1).ToString("o")
        }
    }
}

function Import-Module {
    [CmdletBinding()]
    param([string] $Name)
    Assert ($Name -eq "Microsoft.Graph.Authentication") "Unexpected module import"
}

function Connect-MgGraph {
    param($TenantId, $ContextScope, [switch] $NoWelcome, $Scopes)
    Assert ("$TenantId" -eq $tenant) "Explicit tenant required"
    Assert ($ContextScope -eq "Process") "Graph cache must be process-scoped"
    $global:Lab21IdentityTest.connections++
    $global:Lab21IdentityTest.scopes = $Scopes
}

function Get-MgContext {
    if ($global:Lab21IdentityTest.mode -eq "wrong-tenant") {
        return @{ TenantId = $newUserId; AuthType = "Delegated" }
    }
    return @{ TenantId = $tenant; AuthType = "Delegated" }
}

function Disconnect-MgGraph { $global:Lab21IdentityTest.disconnects++ }

function Read-Host {
    param($Prompt, [switch] $AsSecureString)
    Assert $AsSecureString "Password prompt must not echo"
    $global:Lab21IdentityTest.passwordPrompts++
    return ConvertTo-SecureString ("OfflineOnly!" + [guid]::NewGuid()) -AsPlainText -Force
}

function Invoke-MgGraphRequest {
    param($Method, $Uri, $ContentType, $Body)
    Assert ($Uri.StartsWith("https://graph.microsoft.com/")) "Only Graph requests allowed"
    if ($Method -eq "GET" -and $Uri -like "*/me?*") {
        return @{ id = $operatorId; userPrincipalName = "operator@example.invalid" }
    }
    if ($Method -eq "GET" -and $Uri -like "*roleEligibilityScheduleInstances?*") {
        $decoded = [uri]::UnescapeDataString($Uri)
        Assert ($decoded.Contains("principalId eq '$operatorId'")) "Only read caller eligibility"
        Assert ($decoded.Contains("roleDefinitionId eq '$roleId'")) "Only read User Administrator eligibility"
        if ($global:Lab21IdentityTest.mode -eq "no-eligibility") { return @{ value = @() } }
        if ($global:Lab21IdentityTest.mode -eq "pagination") {
            return @{ value = @(); "@odata.nextLink" = "https://graph.microsoft.com/v1.0/page2" }
        }
        if ($global:Lab21IdentityTest.mode -eq "hostile-pagination") {
            return @{ value = @(); "@odata.nextLink" = "https://example.invalid/page2" }
        }
        return @{ value = @($global:Lab21IdentityTest.eligibility) }
    }
    if ($Method -eq "GET" -and $Uri -eq "https://graph.microsoft.com/v1.0/page2") {
        return @{ value = @($global:Lab21IdentityTest.eligibility) }
    }
    if ($Method -eq "POST") {
        Assert ($ContentType -eq "application/json") "JSON content type required"
        $global:Lab21IdentityTest.posts += @{ Uri = $Uri; Body = ($Body | ConvertFrom-Json -AsHashtable) }
        if ($global:Lab21IdentityTest.mode -eq "forbidden") { throw "403 Authorization_RequestDenied" }
        if ($Uri.EndsWith("/roleAssignmentScheduleRequests")) {
            $status = if ($global:Lab21IdentityTest.mode -eq "pending") { "PendingApproval" } else { "Provisioned" }
            if ($global:Lab21IdentityTest.mode -eq "denied") { $status = "Denied" }
            return @{ id = "offline-request"; status = $status }
        }
        if ($Uri.EndsWith("/users")) {
            return @{ id = $newUserId; userPrincipalName = "lab21@example.invalid" }
        }
    }
    throw "Unexpected Graph operation: $Method $Uri"
}

$pimArgs = @{
    TenantId = $tenant
    Justification = "Offline test"
    Confirm = $false
    WarningAction = "SilentlyContinue"
}
$userArgs = @{
    TenantId = $tenant
    DisplayName = "Offline fixture"
    UserPrincipalName = "lab21@example.invalid"
    Confirm = $false
}

foreach ($path in @($activate, $create, $PSCommandPath)) {
    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $path, [ref]$tokens, [ref]$parseErrors
    ) | Out-Null
    Assert ($parseErrors.Count -eq 0) "PowerShell syntax: $path"
}

Reset-Fakes
$result = & $activate @pimArgs -CheckOnly
Assert ($result.Eligible -and -not $result.ActivationRequested) "Read-only eligibility result"
Assert ($global:Lab21IdentityTest.posts.Count -eq 0) "CheckOnly must not activate"
Assert ($global:Lab21IdentityTest.scopes -notcontains "RoleAssignmentSchedule.ReadWrite.Directory") "No write consent in CheckOnly"
Assert ($global:Lab21IdentityTest.disconnects -eq 1) "Read-only session disconnected"

Reset-Fakes
& $activate @pimArgs -WhatIf | Out-Null
Assert ($global:Lab21IdentityTest.posts.Count -eq 0) "PIM WhatIf must not activate"

foreach ($mode in @("no-eligibility", "expired", "future", "scoped", "other-user", "other-role")) {
    Reset-Fakes
    $global:Lab21IdentityTest.mode = $mode
    switch ($mode) {
        "expired" { $global:Lab21IdentityTest.eligibility.endDateTime = [DateTimeOffset]::UtcNow.AddDays(-1).ToString("o") }
        "future" { $global:Lab21IdentityTest.eligibility.startDateTime = [DateTimeOffset]::UtcNow.AddDays(1).ToString("o") }
        "scoped" { $global:Lab21IdentityTest.eligibility.directoryScopeId = "/administrativeUnits/offline" }
        "other-user" { $global:Lab21IdentityTest.eligibility.principalId = $newUserId }
        "other-role" { $global:Lab21IdentityTest.eligibility.roleDefinitionId = $newUserId }
    }
    Expect-Failure { & $activate @pimArgs } "No current.*eligibility"
    Assert ($global:Lab21IdentityTest.posts.Count -eq 0) "Ineligible caller must not activate: $mode"
    Assert ($global:Lab21IdentityTest.disconnects -eq 1) "Ineligible caller session disconnected"
}

Reset-Fakes
$global:Lab21IdentityTest.mode = "pagination"
$result = & $activate @pimArgs -DurationMinutes 30 -TicketNumber "offline-1" -TicketSystem "Offline"
$body = $global:Lab21IdentityTest.posts[0].Body
Assert ($body.action -eq "selfActivate") "Never grant eligibility or admin-assign a role"
Assert ($body.principalId -eq $operatorId -and $body.roleDefinitionId -eq $roleId) "Only self-activate User Administrator"
Assert ($body.directoryScopeId -eq "/") "Use matched eligibility scope"
Assert ($body.scheduleInfo.expiration.type -eq "afterDuration") "Activation must expire"
Assert ($body.scheduleInfo.expiration.duration -eq "PT30M") "Requested activation duration"
Assert ($body.ticketInfo.ticketNumber -eq "offline-1") "Required ticket preserved"
Assert ($result.Status -eq "Provisioned" -and $global:Lab21IdentityTest.posts.Count -eq 1) "Return actual PIM status"
Assert ($global:Lab21IdentityTest.disconnects -eq 1) "Activation session disconnected"

Reset-Fakes
$global:Lab21IdentityTest.mode = "pending"
$result = & $activate @pimArgs
Assert ($result.Status -eq "PendingApproval") "Pending approval must not become an activation success"

Reset-Fakes
$global:Lab21IdentityTest.mode = "denied"
Expect-Failure { & $activate @pimArgs } "Denied"
Assert ($global:Lab21IdentityTest.posts.Count -eq 1) "Never retry denied activation"

Reset-Fakes
$global:Lab21IdentityTest.mode = "hostile-pagination"
Expect-Failure { & $activate @pimArgs } "Unexpected Graph pagination"
Assert ($global:Lab21IdentityTest.posts.Count -eq 0) "Reject foreign pagination origin"

foreach ($scriptPath in @($activate, $create)) {
    Reset-Fakes
    $global:Lab21IdentityTest.mode = "wrong-tenant"
    $arguments = if ($scriptPath -eq $activate) { $pimArgs } else { $userArgs }
    Expect-Failure { & $scriptPath @arguments } "delegated user session"
    Assert ($global:Lab21IdentityTest.posts.Count -eq 0 -and $global:Lab21IdentityTest.disconnects -eq 1) "Wrong tenant fails closed"
}

Reset-Fakes
Expect-Failure { & $activate @pimArgs -TicketNumber "offline-1" } "both TicketNumber"
Assert ($global:Lab21IdentityTest.connections -eq 0) "Invalid ticket rejected before authentication"
Expect-Failure { & $activate @pimArgs -DurationMinutes 0 } "DurationMinutes"

Reset-Fakes
& $create @userArgs -WhatIf | Out-Null
Assert ($global:Lab21IdentityTest.connections -eq 0 -and $global:Lab21IdentityTest.passwordPrompts -eq 0) "User WhatIf must not authenticate/prompt"
Assert ($global:Lab21IdentityTest.posts.Count -eq 0) "User WhatIf must not create"

Reset-Fakes
$result = & $create @userArgs
$body = $global:Lab21IdentityTest.posts[0].Body
Assert ($global:Lab21IdentityTest.posts.Count -eq 1 -and $global:Lab21IdentityTest.posts[0].Uri.EndsWith("/users")) "Exactly one user creation"
Assert ($global:Lab21IdentityTest.scopes -contains "User.Create") "Use narrow delegated user-creation permission"
Assert ($body.accountEnabled -and $body.passwordProfile.forceChangePasswordNextSignIn) "Enabled account must change password"
Assert ($body.mailNickname -eq "lab21" -and $body.passwordProfile.password) "Required user fields"
Assert ($result.Id -eq $newUserId -and $result.UserPrincipalName -eq $userArgs.UserPrincipalName) "Return created identity"
Assert (-not ($result | ConvertTo-Json).Contains($body.passwordProfile.password)) "Never return password"
Assert ($global:Lab21IdentityTest.passwordPrompts -eq 1 -and $global:Lab21IdentityTest.disconnects -eq 1) "Password prompt/session lifecycle"

Reset-Fakes
$global:Lab21IdentityTest.mode = "forbidden"
Expect-Failure { & $create @userArgs } "403 Authorization_RequestDenied"
Assert ($global:Lab21IdentityTest.posts.Count -eq 1 -and $global:Lab21IdentityTest.disconnects -eq 1) "Graph failure is explicit, disconnected, not retried"

Remove-Variable Lab21IdentityTest -Scope Global
Write-Host "PASS: offline identity setup tests (syntax, eligibility, scope, pagination, PIM status, WhatIf, password handling, Graph errors)."
