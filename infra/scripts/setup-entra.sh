#!/usr/bin/env bash
# Creates and configures the Entra ID app registrations used by Notepal.
#
#   ./setup-entra.sh register                         # once: creates the API + Web app registrations
#   ./setup-entra.sh finalize <web-url> <principal-id> # after the first deployment: redirect URIs + federated credential
#   ./setup-entra.sh dev-secret                       # optional: client secret for running the web app locally
#
# Requires the Azure CLI, signed in (az login) with permission to create app registrations.
set -euo pipefail

API_NAME="${API_NAME:-notepal-api}"
WEB_NAME="${WEB_NAME:-notepal-web}"
LOCAL_WEB_URL="${LOCAL_WEB_URL:-https://localhost:7137}"
SCOPE_NAME="access_as_user"

tenant_id() { az account show --query tenantId -o tsv; }
app_id() { az ad app list --display-name "$1" --query "[0].appId" -o tsv; }
object_id() { az ad app show --id "$1" --query id -o tsv; }

register() {
  local api_id web_id scope_id api_object
  api_id="$(app_id "$API_NAME")"
  if [[ -z "$api_id" ]]; then
    echo "Creating API app registration '$API_NAME'..."
    api_id="$(az ad app create --display-name "$API_NAME" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
    az ad sp create --id "$api_id" >/dev/null
  fi
  api_object="$(object_id "$api_id")"

  scope_id="$(az ad app show --id "$api_id" --query "api.oauth2PermissionScopes[?value=='$SCOPE_NAME'].id | [0]" -o tsv)"
  if [[ -z "$scope_id" ]]; then
    scope_id="$(cat /proc/sys/kernel/random/uuid 2>/dev/null || uuidgen)"
  fi
  az ad app update --id "$api_id" --identifier-uris "api://$api_id"
  local scope_json="{\"id\":\"$scope_id\",\"value\":\"$SCOPE_NAME\",\"type\":\"User\",\"isEnabled\":true,\"adminConsentDisplayName\":\"Access Notepal\",\"adminConsentDescription\":\"Allows the app to read and write the signed-in user's notes.\",\"userConsentDisplayName\":\"Access your notes\",\"userConsentDescription\":\"Allows the app to read and write your notes.\"}"

  # Expose the access_as_user scope and issue v2 access tokens.
  echo "Exposing scope api://$api_id/$SCOPE_NAME..."
  az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$api_object" \
    --headers "Content-Type=application/json" \
    --body "{\"api\":{\"requestedAccessTokenVersion\":2,\"oauth2PermissionScopes\":[$scope_json]}}"

  web_id="$(app_id "$WEB_NAME")"
  if [[ -z "$web_id" ]]; then
    echo "Creating web app registration '$WEB_NAME'..."
    web_id="$(az ad app create --display-name "$WEB_NAME" --sign-in-audience AzureADMyOrg \
      --web-redirect-uris "$LOCAL_WEB_URL/signin-oidc" --query appId -o tsv)"
    az ad sp create --id "$web_id" >/dev/null
  fi

  # The web app requests the API scope (plus Microsoft Graph User.Read for sign-in).
  az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$(object_id "$web_id")" \
    --headers "Content-Type=application/json" \
    --body "{\"requiredResourceAccess\":[{\"resourceAppId\":\"$api_id\",\"resourceAccess\":[{\"id\":\"$scope_id\",\"type\":\"Scope\"}]},{\"resourceAppId\":\"00000003-0000-0000-c000-000000000000\",\"resourceAccess\":[{\"id\":\"e1fe6dd8-ba31-4d61-89e7-88639da4683d\",\"type\":\"Scope\"}]}]}"

  # Pre-authorise the web app so users are not asked to consent to the API scope separately.
  az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$api_object" \
    --headers "Content-Type=application/json" \
    --body "{\"api\":{\"requestedAccessTokenVersion\":2,\"oauth2PermissionScopes\":[$scope_json],\"preAuthorizedApplications\":[{\"appId\":\"$web_id\",\"delegatedPermissionIds\":[\"$scope_id\"]}]}}"

  echo
  echo "Tenant id:      $(tenant_id)"
  echo "API client id:  $api_id"
  echo "Web client id:  $web_id"
  echo "API scope:      api://$api_id/$SCOPE_NAME"
}

finalize() {
  local web_url="${1:?web url required, e.g. https://ca-notepal-web.<env>.azurecontainerapps.io}"
  local principal_id="${2:?web managed identity principal id required (deployment output webIdentityPrincipalId)}"
  local web_id web_object tenant
  web_id="$(app_id "$WEB_NAME")"
  web_object="$(object_id "$web_id")"
  tenant="$(tenant_id)"
  web_url="${web_url%/}"

  echo "Adding redirect URIs for $web_url..."
  az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$web_object" \
    --headers "Content-Type=application/json" \
    --body "{\"web\":{\"redirectUris\":[\"$LOCAL_WEB_URL/signin-oidc\",\"$web_url/signin-oidc\"],\"logoutUrl\":\"$web_url/signout-callback-oidc\"}}"

  if [[ -z "$(az ad app federated-credential list --id "$web_id" --query "[?subject=='$principal_id'].id | [0]" -o tsv)" ]]; then
    echo "Trusting the web app's managed identity (no client secret needed in Azure)..."
    az ad app federated-credential create --id "$web_id" --parameters "{\"name\":\"notepal-web-managed-identity\",\"issuer\":\"https://login.microsoftonline.com/$tenant/v2.0\",\"subject\":\"$principal_id\",\"audiences\":[\"api://AzureADTokenExchange\"]}" >/dev/null
  fi

  echo "Done."
}

dev_secret() {
  local web_id secret
  web_id="$(app_id "$WEB_NAME")"
  secret="$(az ad app credential reset --id "$web_id" --append --display-name "local-dev" --years 1 --query password -o tsv)"
  echo "Run from src/Notepal.Web:"
  echo "  dotnet user-secrets set \"AzureAd:ClientSecret\" \"$secret\""
}

case "${1:-}" in
  register) register ;;
  finalize) shift; finalize "$@" ;;
  dev-secret) dev_secret ;;
  *) sed -n '2,9p' "$0"; exit 1 ;;
esac
