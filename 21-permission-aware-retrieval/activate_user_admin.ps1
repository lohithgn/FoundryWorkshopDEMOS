#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "High")]
param(
    [Parameter(Mandatory)]
    [guid] $TenantId,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Justification,
    [ValidateRange(1, 480)]
    [int] $DurationMinutes = 60,
    [string] $TicketNumber,
    [string] $TicketSystem,
    [switch] $CheckOnly
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Justification)) {
    throw "A non-blank PIM justification is required."
}
if ([bool]$TicketNumber -ne [bool]$TicketSystem) {
    throw "Supply both TicketNumber and TicketSystem, or neither."
}

Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
$roleId = "fe930be7-5e62-47db-91af-98c3a49a38b1" # Built-in User Administrator.
$scopes = @("User.Read", "RoleEligibilitySchedule.Read.Directory")
if (-not $CheckOnly) {
    $scopes += "RoleAssignmentSchedule.ReadWrite.Directory"
}
$connected = $false
try {
    Connect-MgGraph -TenantId $TenantId -ContextScope Process -NoWelcome -Scopes $scopes
    $connected = $true
    $context = Get-MgContext
    if ($context.TenantId -ne "$TenantId" -or $context.AuthType -ne "Delegated") {
        throw "Expected a delegated user session in the requested tenant."
    }
    $me = Invoke-MgGraphRequest -Method GET `
        -Uri 'https://graph.microsoft.com/v1.0/me?$select=id,userPrincipalName'
    $principalId = ([guid]$me.id).ToString()
    if ($principalId -eq [guid]::Empty.ToString()) {
        throw "Graph did not return a valid signed-in user."
    }
    $filter = [uri]::EscapeDataString("principalId eq '$principalId' and roleDefinitionId eq '$roleId'")
    $next = "https://graph.microsoft.com/v1.0/roleManagement/directory/roleEligibilityScheduleInstances?`$filter=$filter"
    $eligible = @()
    $pages = 0
    $now = [DateTimeOffset]::UtcNow
    while ($next) {
        $uri = [uri]$next
        if ($uri.Scheme -ne "https" -or $uri.Authority -ne "graph.microsoft.com" -or ++$pages -gt 100) {
            throw "Unexpected Graph pagination URL or excessive pagination."
        }
        $page = Invoke-MgGraphRequest -Method GET -Uri $next
        if ($null -eq $page.value) {
            throw "Malformed PIM eligibility response."
        }
        $eligible += @($page.value | Where-Object {
            $_.principalId -eq $principalId -and $_.roleDefinitionId -eq $roleId -and
            $_.directoryScopeId -eq "/" -and -not $_.appScopeId -and
            $_.startDateTime -and ([DateTimeOffset]$_.startDateTime) -le $now -and
            (-not $_.endDateTime -or ([DateTimeOffset]$_.endDateTime) -gt $now)
        })
        $next = $page.'@odata.nextLink'
    }
    if ($eligible.Count -eq 0) {
        throw ("No current, direct, tenant-wide User Administrator eligibility for this user. " +
            "Global Reader/subscription Owner is insufficient. Ask your PIM administrator; " +
            "group-based or administrative-unit eligibility must be handled through the tenant's normal process.")
    }
    if ($CheckOnly) {
        [pscustomobject]@{
            TenantId = "$TenantId"
            PrincipalId = $principalId
            Role = "User Administrator"
            Eligible = $true
            ActivationRequested = $false
        }
        return
    }
    if (-not $PSCmdlet.ShouldProcess(
        "$($me.userPrincipalName) in tenant $TenantId",
        "Request User Administrator through PIM for $DurationMinutes minutes"
    )) {
        return
    }
    $body = @{
        action = "selfActivate"
        principalId = $principalId
        roleDefinitionId = $roleId
        directoryScopeId = "/"
        justification = $Justification
        scheduleInfo = @{
            startDateTime = [DateTimeOffset]::UtcNow.ToString("o")
            expiration = @{ type = "afterDuration"; duration = "PT${DurationMinutes}M" }
        }
    }
    if ($TicketNumber) {
        $body.ticketInfo = @{ ticketNumber = $TicketNumber; ticketSystem = $TicketSystem }
    }
    $request = Invoke-MgGraphRequest -Method POST `
        -Uri "https://graph.microsoft.com/v1.0/roleManagement/directory/roleAssignmentScheduleRequests" `
        -ContentType "application/json" -Body ($body | ConvertTo-Json -Depth 6)
    if (-not $request.id -or -not $request.status) {
        throw "PIM returned no request ID/status. Check the portal before retrying."
    }
    if ($request.status -in @("Denied", "Failed", "Canceled", "Revoked")) {
        throw "PIM request $($request.id) ended with status $($request.status)."
    }
    [pscustomobject]@{
        RequestId = $request.id
        Status = $request.status
        PrincipalId = $principalId
        Role = "User Administrator"
    }
    Write-Warning ("Request submission is not proof of effective permissions. " +
        "Check PIM My roles/My requests for activation or required approval; wait for propagation " +
        "before running create_test_user.ps1. MFA/Conditional Access failures must be resolved " +
        "through normal interactive authentication or the Entra portal, never bypassed.")
}
finally {
    if ($connected) {
        Disconnect-MgGraph | Out-Null
    }
}
