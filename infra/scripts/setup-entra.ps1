#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Creates and configures the Entra ID app registrations used by Notepal.

.DESCRIPTION
      ./setup-entra.ps1 register                          # once: creates the API + Web app registrations
      ./setup-entra.ps1 finalize <web-url> <principal-id> # after the first deployment: redirect URIs + federated credential
      ./setup-entra.ps1 dev-secret                        # optional: client secret for running the web app locally

    Requires PowerShell 7+ and the Azure CLI, signed in (az login) with permission to create app registrations.
    The app names and local URL can be overridden with the API_NAME, WEB_NAME and LOCAL_WEB_URL environment variables.

.EXAMPLE
    ./infra/scripts/setup-entra.ps1 finalize https://ca-notepal-web.<env>.azurecontainerapps.io 00000000-0000-0000-0000-000000000000
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('register', 'finalize', 'dev-secret')]
    [string] $Command,

    # finalize: the web app URL, e.g. https://ca-notepal-web.<env>.azurecontainerapps.io
    [Parameter(Position = 1)]
    [string] $WebUrl,

    # finalize: the web managed identity principal id (deployment output webIdentityPrincipalId)
    [Parameter(Position = 2)]
    [string] $PrincipalId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ApiName = if ($env:API_NAME) { $env:API_NAME } else { 'notepal-api' }
$WebName = if ($env:WEB_NAME) { $env:WEB_NAME } else { 'notepal-web' }
$LocalWebUrl = if ($env:LOCAL_WEB_URL) { $env:LOCAL_WEB_URL } else { 'https://localhost:7137' }
$ScopeName = 'access_as_user'
$GraphAppId = '00000003-0000-0000-c000-000000000000'
$GraphUserRead = 'e1fe6dd8-ba31-4d61-89e7-88639da4683d'
$GraphUserReadBasicAll = 'b340eb25-3456-403f-be2f-af7a0d370277'

# Runs the Azure CLI and fails on a non-zero exit code; returns trimmed output (e.g. from -o tsv).
function Invoke-Az {
    $output = & az @args
    if ($LASTEXITCODE -ne 0) {
        throw "az $($args -join ' ') failed with exit code $LASTEXITCODE."
    }
    if ($null -eq $output) { return '' }
    return ($output -join "`n").Trim()
}

# Sends a JSON body to Microsoft Graph through 'az rest'. The body is passed in a temporary file so that
# quoting works the same on Windows, macOS and Linux.
function Invoke-GraphPatch([string] $Uri, [object] $Body) {
    $file = New-TemporaryFile
    try {
        $Body | ConvertTo-Json -Depth 10 -Compress | Set-Content -Path $file -Encoding utf8NoBOM
        Invoke-Az rest --method PATCH --uri $Uri --headers 'Content-Type=application/json' --body "@$($file.FullName)" | Out-Null
    }
    finally {
        Remove-Item $file -Force -ErrorAction SilentlyContinue
    }
}

function Get-TenantId { Invoke-Az account show --query tenantId -o tsv }
function Get-AppId([string] $Name) { Invoke-Az ad app list --display-name $Name --query '[0].appId' -o tsv }
function Get-ObjectId([string] $AppId) { Invoke-Az ad app show --id $AppId --query id -o tsv }

function Invoke-Register {
    $apiId = Get-AppId $ApiName
    if (-not $apiId) {
        Write-Host "Creating API app registration '$ApiName'..."
        $apiId = Invoke-Az ad app create --display-name $ApiName --sign-in-audience AzureADMyOrg --query appId -o tsv
        Invoke-Az ad sp create --id $apiId | Out-Null
    }
    $apiObject = Get-ObjectId $apiId

    $scopeId = Invoke-Az ad app show --id $apiId --query "api.oauth2PermissionScopes[?value=='$ScopeName'].id | [0]" -o tsv
    if (-not $scopeId) {
        $scopeId = [guid]::NewGuid().ToString()
    }
    Invoke-Az ad app update --id $apiId --identifier-uris "api://$apiId" | Out-Null
    $scope = [ordered]@{
        id                      = $scopeId
        value                   = $ScopeName
        type                    = 'User'
        isEnabled               = $true
        adminConsentDisplayName = 'Access Notepal'
        adminConsentDescription = "Allows the app to read and write the signed-in user's notes."
        userConsentDisplayName  = 'Access your notes'
        userConsentDescription  = 'Allows the app to read and write your notes.'
    }

    # Expose the access_as_user scope and issue v2 access tokens.
    Write-Host "Exposing scope api://$apiId/$ScopeName..."
    Invoke-GraphPatch "https://graph.microsoft.com/v1.0/applications/$apiObject" @{
        api = @{ requestedAccessTokenVersion = 2; oauth2PermissionScopes = @($scope) }
    }

    $webId = Get-AppId $WebName
    if (-not $webId) {
        Write-Host "Creating web app registration '$WebName'..."
        $webId = Invoke-Az ad app create --display-name $WebName --sign-in-audience AzureADMyOrg `
            --web-redirect-uris "$LocalWebUrl/signin-oidc" --query appId -o tsv
        Invoke-Az ad sp create --id $webId | Out-Null
    }

    # The web app requests the API scope plus Microsoft Graph User.Read (sign-in) and User.ReadBasic.All
    # (searching people in the directory when sharing a note).
    Invoke-GraphPatch "https://graph.microsoft.com/v1.0/applications/$(Get-ObjectId $webId)" @{
        requiredResourceAccess = @(
            @{ resourceAppId = $apiId; resourceAccess = @(@{ id = $scopeId; type = 'Scope' }) },
            @{ resourceAppId = $GraphAppId; resourceAccess = @(
                    @{ id = $GraphUserRead; type = 'Scope' },
                    @{ id = $GraphUserReadBasicAll; type = 'Scope' }) }
        )
    }

    # Consent to the Graph scopes for the organization, so people search works without an extra consent prompt.
    # Needs an admin role (e.g. Cloud Application Administrator); otherwise sharing still works by typing email addresses.
    Write-Host 'Granting Microsoft Graph User.Read User.ReadBasic.All for the organization...'
    try {
        Invoke-Az ad app permission grant --id $webId --api $GraphAppId --scope 'User.Read User.ReadBasic.All' | Out-Null
    }
    catch {
        Write-Host "  Could not grant consent - ask an administrator to grant consent for '$WebName' in the Entra admin center."
    }

    # Pre-authorize the web app so users are not asked to consent to the API scope separately.
    Invoke-GraphPatch "https://graph.microsoft.com/v1.0/applications/$apiObject" @{
        api = @{
            requestedAccessTokenVersion = 2
            oauth2PermissionScopes      = @($scope)
            preAuthorizedApplications   = @(@{ appId = $webId; delegatedPermissionIds = @($scopeId) })
        }
    }

    Write-Host ''
    Write-Host "Tenant id:      $(Get-TenantId)"
    Write-Host "API client id:  $apiId"
    Write-Host "Web client id:  $webId"
    Write-Host "API scope:      api://$apiId/$ScopeName"
}

function Invoke-Finalize {
    if (-not $WebUrl) { throw 'web url required, e.g. https://ca-notepal-web.<env>.azurecontainerapps.io' }
    if (-not $PrincipalId) { throw 'web managed identity principal id required (deployment output webIdentityPrincipalId)' }

    $webId = Get-AppId $WebName
    $webObject = Get-ObjectId $webId
    $tenant = Get-TenantId
    $url = $WebUrl.TrimEnd('/')

    Write-Host "Adding redirect URIs for $url..."
    Invoke-GraphPatch "https://graph.microsoft.com/v1.0/applications/$webObject" @{
        web = @{
            redirectUris = @("$LocalWebUrl/signin-oidc", "$url/signin-oidc")
            logoutUrl    = "$url/signout-callback-oidc"
        }
    }

    $credentialName = 'notepal-web-managed-identity'
    $existing = Invoke-Az ad app federated-credential list --id $webId --query "[?subject=='$PrincipalId'].id | [0]" -o tsv
    if (-not $existing) {
        Write-Host "Trusting the web app's managed identity (no client secret needed in Azure)..."
        # A credential with this name may still trust a previous (replaced) web identity.
        $stale = Invoke-Az ad app federated-credential list --id $webId --query "[?name=='$credentialName'].id | [0]" -o tsv
        $file = New-TemporaryFile
        try {
            @{
                name      = $credentialName
                issuer    = "https://login.microsoftonline.com/$tenant/v2.0"
                subject   = $PrincipalId
                audiences = @('api://AzureADTokenExchange')
            } | ConvertTo-Json -Compress | Set-Content -Path $file -Encoding utf8NoBOM
            if ($stale) {
                Invoke-Az ad app federated-credential update --id $webId --federated-credential-id $stale --parameters $file.FullName | Out-Null
            }
            else {
                Invoke-Az ad app federated-credential create --id $webId --parameters $file.FullName | Out-Null
            }
        }
        finally {
            Remove-Item $file -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host 'Done.'
}

function Invoke-DevSecret {
    $webId = Get-AppId $WebName
    $secret = Invoke-Az ad app credential reset --id $webId --append --display-name 'local-dev' --years 1 --query password -o tsv
    Write-Host 'Run from src/Notepal.Web:'
    Write-Host "  dotnet user-secrets set `"AzureAd:ClientSecret`" `"$secret`""
}

switch ($Command) {
    'register' { Invoke-Register }
    'finalize' { Invoke-Finalize }
    'dev-secret' { Invoke-DevSecret }
    default {
        Get-Help $PSCommandPath -Detailed
        exit 1
    }
}
