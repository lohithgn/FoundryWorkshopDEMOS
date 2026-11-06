#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "High")]
param(
    [Parameter(Mandatory)]
    [guid] $TenantId,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $DisplayName,
    [Parameter(Mandatory)]
    [ValidatePattern("^[A-Za-z0-9'.!#^~_-]+@[A-Za-z0-9.-]+$")]
    [string] $UserPrincipalName
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($DisplayName)) {
    throw "A non-blank display name is required."
}
if (-not $PSCmdlet.ShouldProcess(
    "$UserPrincipalName in tenant $TenantId",
    "Create a new enabled test user (no license, groups, or administrator roles)"
)) {
    return
}

Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
$connected = $false
$password = $null
$passwordPointer = [IntPtr]::Zero
$plainPassword = $null
$body = $null
try {
    Connect-MgGraph -TenantId $TenantId -ContextScope Process -NoWelcome -Scopes @(
        "User.Read", "User.Create"
    )
    $connected = $true
    $context = Get-MgContext
    if ($context.TenantId -ne "$TenantId" -or $context.AuthType -ne "Delegated") {
        throw "Expected a delegated user session in the requested tenant."
    }
    $me = Invoke-MgGraphRequest -Method GET `
        -Uri 'https://graph.microsoft.com/v1.0/me?$select=id,userPrincipalName'
    Write-Host "Creating the test user as $($me.userPrincipalName) in tenant $TenantId."
    $password = Read-Host "Temporary password (tenant policy applies; never recorded by this script)" -AsSecureString
    if ($password.Length -eq 0) {
        throw "The temporary password cannot be empty."
    }
    $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
    $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    $body = @{
        accountEnabled = $true
        displayName = $DisplayName
        mailNickname = $UserPrincipalName.Split("@")[0]
        userPrincipalName = $UserPrincipalName
        passwordProfile = @{
            forceChangePasswordNextSignIn = $true
            password = $plainPassword
        }
    }
    $created = Invoke-MgGraphRequest -Method POST -Uri "https://graph.microsoft.com/v1.0/users" `
        -ContentType "application/json" -Body ($body | ConvertTo-Json -Depth 4)
    if (-not $created.id -or $created.userPrincipalName -ne $UserPrincipalName) {
        throw "Unexpected creation response. Check Entra users before retrying; the account may exist."
    }
    [pscustomobject]@{
        TenantId = "$TenantId"
        Id = $created.id
        UserPrincipalName = $created.userPrincipalName
    }
    Write-Host "User created. Share the temporary password securely; the user must change it at first sign-in."
}
finally {
    if ($passwordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    }
    if ($password) {
        $password.Dispose()
    }
    $plainPassword = $null
    $body = $null
    if ($connected) {
        Disconnect-MgGraph | Out-Null
    }
}
