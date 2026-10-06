$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$appName = 'notepal-github-oidc'
$repo = 'TK11703/Notepal'
$subscription = 'fdadb01b-83a6-4002-9eca-5ff098cdd3bd'
$graphAppId = '00000003-0000-0000-c000-000000000000'
$applicationReadAll = '9a5d68dd-52b0-4cc2-bd40-abcf44ac3a30'

az account set --subscription $subscription

$appId = az ad app list --display-name $appName --query '[0].appId' -o tsv
if (-not $appId) {
    "Creating app registration $appName..."
    $appId = az ad app create --display-name $appName --sign-in-audience AzureADMyOrg --query appId -o tsv
}
$spId = az ad sp list --filter "appId eq '$appId'" --query '[0].id' -o tsv
if (-not $spId) {
    $spId = az ad sp create --id $appId --query id -o tsv
}

# GitHub OIDC subjects use immutable ids: repo:<owner>@<owner-id>/<repo>@<repo-id>:...
$ids = gh api "repos/$repo" --jq '.owner.id, .id'
$owner, $repoName = $repo -split '/'
$subjectRepo = "$owner@$($ids[0])/$repoName@$($ids[1])"

$subjects = [ordered]@{
    'gh-main-branch' = "repo:${subjectRepo}:ref:refs/heads/main"
    'gh-env-prod'    = "repo:${subjectRepo}:environment:production"
}
$existing = az ad app federated-credential list --id $appId --query '[].subject' -o tsv
foreach ($name in $subjects.Keys) {
    if ($existing -notcontains $subjects[$name]) {
        "Adding federated credential $name -> $($subjects[$name])"
        $file = New-TemporaryFile
        @{ name = $name; issuer = 'https://token.actions.githubusercontent.com'; subject = $subjects[$name]; audiences = @('api://AzureADTokenExchange') } |
            ConvertTo-Json | Set-Content -Path $file -Encoding utf8NoBOM
        az ad app federated-credential create --id $appId --parameters "@$file" | Out-Null
        Remove-Item $file
    }
}

$graphSpId = az ad sp show --id $graphAppId --query id -o tsv
$granted = az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$spId/appRoleAssignments" --query 'value[].appRoleId' -o tsv
if ($granted -notcontains $applicationReadAll) {
    'Granting Microsoft Graph Application.Read.All (admin consent)...'
    az ad app permission add --id $appId --api $graphAppId --api-permissions "$applicationReadAll=Role" 2>$null
    $body = @{ principalId = $spId; resourceId = $graphSpId; appRoleId = $applicationReadAll } | ConvertTo-Json -Compress
    $file = New-TemporaryFile
    Set-Content -Path $file -Value $body -Encoding utf8NoBOM
    az rest --method POST --url "https://graph.microsoft.com/v1.0/servicePrincipals/$spId/appRoleAssignments" --body "@$file" --output none
    Remove-Item $file
}

'Assigning roles...'
$scope = "/subscriptions/$subscription"
foreach ($role in 'Contributor', 'User Access Administrator') {
    $has = @(az role assignment list --assignee $spId --scope $scope --role $role --query '[].id' -o tsv)
    if ($has.Count -gt 0) { "  $role on subscription already assigned"; continue }
    az role assignment create --assignee-object-id $spId --assignee-principal-type ServicePrincipal --role $role --scope $scope --output none
    "  $role on subscription assigned"
}

''
"AZURE_CLIENT_ID       = $appId"
"AZURE_TENANT_ID       = $(az account show --query tenantId -o tsv)"
"AZURE_SUBSCRIPTION_ID = $subscription"
"Service principal     = $spId"
'Federated credentials:'
az ad app federated-credential list --id $appId --query '[].{name:name, subject:subject}' -o table
'Role assignments:'
az role assignment list --assignee $spId --all --query '[].{role:roleDefinitionName, scope:scope, condition:condition != null}' -o table
