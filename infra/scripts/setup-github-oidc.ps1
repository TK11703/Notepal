$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$appName = 'notepal-github'
$repo = 'TK11703/Notepal'
$subscription = 'fdadb01b-83a6-4002-9eca-5ff098cdd3bd'
$appRg = 'rg-notepal'
$sharedRg = 'rg-common'
$location = 'eastus'
$acrPull = '7f951dda-4ed3-4ba8-8aa3-0b5a6f6e5ba5'
$foundryUser = '53ca6127-db72-4b80-b1b0-d745d6d5456d'

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
$owner, $name = $repo -split '/'
$subjectRepo = "$owner@$($ids[0])/$name@$($ids[1])"

$subjects = [ordered]@{
    'github-main-v2'       = "repo:${subjectRepo}:ref:refs/heads/main"
    'github-production-v2' = "repo:${subjectRepo}:environment:production"
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

if ((az group exists --name $appRg) -ne 'true') {
    "Creating resource group $appRg..."
    az group create --name $appRg --location $location --output none
}

function Grant([string] $role, [string] $rg, [string] $condition) {
    $scope = "/subscriptions/$subscription/resourceGroups/$rg"
    $has = @(az role assignment list --assignee $spId --scope $scope --role $role --query '[].id' -o tsv)
    if ($has.Count -gt 0) { "  $role on $rg already assigned"; return }
    $azArgs = @('role', 'assignment', 'create', '--assignee-object-id', $spId, '--assignee-principal-type', 'ServicePrincipal', '--role', $role, '--scope', $scope, '--output', 'none')
    if ($condition) { $azArgs += @('--condition', $condition, '--condition-version', '2.0') }
    az @azArgs
    "  $role on $rg assigned"
}

# Only lets the pipeline create or delete AcrPull and Foundry User assignments.
$allowed = "{$acrPull, $foundryUser}"
$condition = "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals $allowed)) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals $allowed))"

'Assigning roles...'
Grant 'Contributor' $appRg $null
Grant 'Contributor' $sharedRg $null
Grant 'Role Based Access Control Administrator' $sharedRg $condition

''
"AZURE_CLIENT_ID       = $appId"
"AZURE_TENANT_ID       = $(az account show --query tenantId -o tsv)"
"AZURE_SUBSCRIPTION_ID = $subscription"
"Service principal     = $spId"
'Federated credentials:'
az ad app federated-credential list --id $appId --query '[].{name:name, subject:subject}' -o table
'Role assignments:'
az role assignment list --assignee $spId --all --query '[].{role:roleDefinitionName, scope:scope, condition:condition != null}' -o table
