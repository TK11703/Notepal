#!/usr/bin/env bash
# Prints each resource's state while a resource group deployment runs; fails if the deployment fails.
# Usage: watch-deployment.sh <resource-group> <deployment-name>
set -euo pipefail
rg=$1
name=$2
declare -A seen

while true; do
  state=$(az deployment group show --resource-group "$rg" --name "$name" --query properties.provisioningState --output tsv)

  while IFS=$'\t' read -r type resource op_state; do
    key="$type/$resource"
    if [ "${seen[$key]:-}" != "$op_state" ]; then
      seen[$key]=$op_state
      printf '%-10s %s (%s)\n' "$op_state" "$resource" "$type"
    fi
  done < <(az deployment operation group list --resource-group "$rg" --name "$name" \
    --query "[?properties.targetResource].[properties.targetResource.resourceType, properties.targetResource.resourceName, properties.provisioningState]" \
    --output tsv)

  case $state in
    Succeeded)
      exit 0 ;;
    Failed | Canceled)
      echo "::error::Deployment $name $state"
      az deployment operation group list --resource-group "$rg" --name "$name" \
        --query "[?properties.provisioningState=='Failed'].{resource: properties.targetResource.resourceName, error: properties.statusMessage}" \
        --output json
      exit 1 ;;
  esac
  sleep 10
done
