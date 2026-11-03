#!/usr/bin/env bash
# Delete dedicated workshop resource groups, never every group in a subscription.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: ./removeAll.sh [--subscription ID_OR_NAME] [--execute] [--yes]
                      [--prefix PREFIX]... [--group NAME]... [--dry-run]

Defaults to a read-only preview in the currently selected Azure subscription.
Requires Bash, Azure CLI (az login), and jq.

Targets:
  * All discovered labs' root-runner names (rg-foundry-<lab>-demo).
  * Standalone runner names (rg-foundry-student-<lab>-demo).
  * Manual/historical group names documented in the labs.
  * Groups recorded in each lab's .azure/{lab-deployment,deployment}.json,
    but only when their subscription matches the selected subscription.
    Legacy output-only deployment.json files are reported and skipped because
    they do not identify a subscription or group; known names still apply.

--prefix adds another deployment prefix; it is NOT a wildcard match.
--group adds an exact group name for an unrecorded, manually renamed deployment.
--execute deletes the listed groups after confirmation; --yes skips that prompt.
--dry-run explicitly selects preview mode (the default).

Execution also IRREVERSIBLY purges workshop Foundry/Cognitive Services accounts
and API Management services, including those from groups already deleted.
Log Analytics workspaces are permanently deleted; already soft-deleted workspaces
must first be recovered (temporarily recreating their group if necessary).
Newly soft-deleted accounts/services are rediscovered after group deletion.
Purge is retried and verified, but Azure can take additional time to release names.

Deletion waits for each group and verifies its absence. Failures are reported,
remaining groups are still attempted, and any failure produces a nonzero exit.
ALL resources in each selected group are deleted, including unfinished training.
Resource locks/permissions can block deletion; locks are not removed automatically.
Tenant identities, local files, and GitHub configuration are NOT deleted.
Only the lab services with supported permanent-delete APIs above are purged;
this is not a general data-retention purge (for example Storage blob retention).
See labs 12, 14, and 23 for separate identity/configuration cleanup.
Run separately for every subscription in which you deployed labs.
EOF
}

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

subscription=''
execute=false
yes=false
prefixes=(rg-foundry rg-foundry-student)
extra_groups=()
while (($#)); do
    case "$1" in
        --subscription|--prefix|--group)
            (($# >= 2)) && [[ -n "$2" && "$2" != --* ]] || fail "$1 requires a value."
            case "$1" in
                --subscription) subscription=$2 ;;
                --prefix)
                    [[ "$2" =~ ^[a-zA-Z0-9_-]{1,50}$ ]] || fail "Invalid deployment prefix: $2"
                    prefixes+=("$2")
                    ;;
                --group) extra_groups+=("$2") ;;
            esac
            shift 2
            ;;
        --execute) execute=true; shift ;;
        --dry-run) execute=false; shift ;;
        --yes) yes=true; shift ;;
        --help|-h) usage; exit 0 ;;
        *) fail "Unknown argument: $1 (see --help)." ;;
    esac
done

command -v az >/dev/null 2>&1 || fail "Azure CLI is required; install it and run az login."
command -v jq >/dev/null 2>&1 || fail "jq is required to validate deployment records and Azure responses."
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
account_args=(account show --output json)
if [[ -n "$subscription" ]]; then
    account_args+=(--subscription "$subscription")
fi
account=$(az "${account_args[@]}")
subscription=$(jq -er '.id | select(type == "string" and length > 0) | ascii_downcase' <<< "$account")
account_name=$(jq -er '.name | select(type == "string" and length > 0)' <<< "$account")
scope=(--subscription "$subscription")

# These are aliases beyond the two runners' generated names.
candidates=(
    foundry-lab14 foundry-lab15 foundry-lab16 foundry-lab26
    rg-lab17 rg-lab19 rg-lab21
    lab18-tracing lab23-evaluation lab24-lifecycle
    rg-foundry-native-memory-demo rg-browser-runbook
    rg-foundry-webiq-agent-live-20261026
)
if ((${#extra_groups[@]})); then
    candidates+=("${extra_groups[@]}")
fi
lab_count=0
for template in "$root"/[0-9]*-*/infra/main.bicep; do
    [[ -f "$template" ]] || continue
    lab_dir=${template%/infra/main.bicep}
    lab=${lab_dir##*/}
    lab_count=$((lab_count + 1))
    for prefix in "${prefixes[@]}"; do
        candidates+=("$prefix-${lab#*-}-demo")
    done
    for state in "$lab_dir/.azure/deployment.json" "$lab_dir/.azure/lab-deployment.json"; do
        [[ -f "$state" ]] || continue
        if [[ "$state" == "$lab_dir/.azure/deployment.json" ]] && jq -e '
            type == "object" and length > 0 and
            (has("subscription") | not) and (has("resourceGroup") | not) and
            all(.[];
                type == "object" and has("value") and
                (.type | type == "string" and length > 0))
        ' "$state" >/dev/null; then
            printf 'WARNING: Output-only deployment record has no subscription/group: %s. Using known group names; add --group for any custom name.\n' "$state" >&2
            continue
        fi
        if ! jq -e '
            type == "object" and
            (.subscription | type == "string" and test("^[A-Za-z0-9-]+$")) and
            (.resourceGroup | type == "string" and
                test("^[A-Za-z0-9_.()-]{1,90}$") and (endswith(".") | not))
        ' "$state" >/dev/null; then
            fail "Invalid deployment record: $state. No groups have been deleted."
        fi
        recorded_subscription=$(jq -r '.subscription | ascii_downcase' "$state")
        if [[ "$recorded_subscription" == "$subscription" ]]; then
            candidates+=("$(jq -r '.resourceGroup' "$state")")
        else
            printf 'Skipping record in another subscription (%s): %s\n' "$recorded_subscription" "$state" >&2
        fi
    done
done
((lab_count > 0)) || fail "No lab templates found beside this script."
for group in "${candidates[@]}"; do
    [[ "$group" =~ ^[a-zA-Z0-9_.\(\)-]{1,90}$ && "$group" != *. ]] ||
        fail "Invalid resource group name: $group"
done

candidate_names=$(printf '%s\n' "${candidates[@]}")
groups_json=$(az group list "${scope[@]}" --output json)
targets=$(jq -er --arg names "$candidate_names" '
    if type != "array" or any(.[]; (.name | type) != "string") then
        error("Invalid Azure resource group list")
    else
        ($names | split("\n") | map(ascii_downcase) | unique) as $allowed |
        [.[] | .name | select(ascii_downcase as $name | $allowed | index($name))] |
        unique_by(ascii_downcase) | sort_by(ascii_downcase) | join("\n")
    end
' <<< "$groups_json")

# Deleted-service IDs do not always contain the original resource group.
# APIM exposes the original ARM ID separately in properties.serviceId.
scoped_resources() {
    jq -ce --arg names "$candidate_names" --arg subscription "$subscription" --arg kind "$1" '
        ($names | split("\n") | map(ascii_downcase) | unique) as $allowed |
        if type != "array" then error("Invalid resource list") else
            [ .[] |
                (if $kind == "apim" then .properties.serviceId else .id end) as $id |
                if ($id | type) != "string" then error("Resource has no original ARM ID") else
                    ([$id | capture("^/subscriptions/(?<subscription>[^/]+)/.*?resourceGroups/(?<group>[^/]+)/"; "i")] |
                        if length == 1 then .[0] else error("Invalid original ARM ID") end) as $origin |
                    select(($origin.subscription | ascii_downcase) == $subscription) |
                    select($origin.group | ascii_downcase as $group | $allowed | index($group)) |
                    if (.name | type) != "string" or (.location | type) != "string" then
                        error("Resource has no name/location")
                    else
                        {kind: $kind, group: $origin.group, name: .name,
                         location: (.location | ascii_downcase | gsub(" "; ""))} |
                        if (.group | test("^[A-Za-z0-9_.()-]{1,90}$")) and
                           (.name | test("^[A-Za-z0-9_.()-]{1,90}$")) and
                           (.location | test("^[a-z0-9]+$")) then .
                        else error("Invalid resource name/group/location") end
                    end
                end
            ] | unique_by([(.group | ascii_downcase), (.name | ascii_downcase)])
        end
    '
}

discover_resources() {
    local kind=$1 response
    case "$kind" in
        cognitive) response=$(az cognitiveservices account list-deleted "${scope[@]}" --output json) || return 1 ;;
        apim) response=$(az apim deletedservice list "${scope[@]}" --output json) || return 1 ;;
        workspace) response=$(az monitor log-analytics workspace list "${scope[@]}" --output json) || return 1 ;;
        deleted-workspace) response=$(az monitor log-analytics workspace list-deleted-workspaces "${scope[@]}" --output json) || return 1 ;;
        *) fail "Unknown resource kind: $kind" ;;
    esac
    scoped_resources "$kind" <<< "$response"
}

cognitive=$(discover_resources cognitive) || fail "Could not inventory soft-deleted Foundry accounts. Nothing was deleted."
apim=$(discover_resources apim) || fail "Could not inventory soft-deleted API Management services. Nothing was deleted."
workspaces=$(discover_resources workspace) || fail "Could not inventory Log Analytics workspaces. Nothing was deleted."
deleted_workspaces=$(discover_resources deleted-workspace) || fail "Could not inventory deleted Log Analytics workspaces. Nothing was deleted."
purge_preview=$(printf '%s\n' "$cognitive" "$apim" "$workspaces" "$deleted_workspaces" |
    jq -sr 'add | .[] | "\(.kind): \(.group)/\(.name) (\(.location))"')

printf '\nSubscription: %s (%s)\nReviewed labs: %s\n' "$account_name" "$subscription" "$lab_count"
if [[ -z "$targets" && -z "$purge_preview" ]]; then
    printf 'No matching workshop resource groups or purgeable resources exist. Nothing to delete.\n'
    exit 0
fi
if [[ -n "$targets" ]]; then
    printf '\nThe following groups and ALL their resources are selected:\n%s\n' "$targets"
fi
if [[ -n "$purge_preview" ]]; then
    printf '\nSelected for IRREVERSIBLE permanent deletion/purge:\n%s\n' "$purge_preview"
fi
printf '\nNewly soft-deleted Foundry/APIM resources in workshop groups will also be purged.\n'
printf 'Deleted Log Analytics workspaces are recovered only to permanently delete them;\n'
printf 'their resource group is temporarily recreated if needed and deleted afterward.\n'
printf '\nTenant objects and external/local configuration will not be deleted.\n'
if ! "$execute"; then
    printf '\nDRY RUN ONLY. Rerun with --execute to delete groups and permanently purge resources.\n'
    exit 0
fi
if ! "$yes"; then
    printf '\nType DELETE to confirm deletion AND IRREVERSIBLE PURGE in subscription %s: ' "$subscription"
    IFS= read -r confirmation || fail "Confirmation input unavailable. Nothing was deleted."
    [[ "$confirmation" == DELETE ]] || fail "Confirmation did not match. Nothing was deleted."
fi

failures=0
workspace_rows=$(printf '%s\n' "$workspaces" "$deleted_workspaces" |
    jq -sr 'add | .[] | [.kind, .group, .name, .location] | @tsv')
while IFS=$'\t' read -r kind group name location; do
    [[ -n "$kind" ]] || continue
    if [[ "$kind" == deleted-workspace ]]; then
        if ! exists=$(az group exists --name "$group" "${scope[@]}" --output tsv); then
            printf 'ERROR: Cannot check group for workspace recovery: %s.\n' "$group" >&2
            failures=$((failures + 1))
            continue
        fi
        if [[ "$exists" == false ]]; then
            printf '\nTemporarily recreating %s to recover and permanently delete %s.\n' "$group" "$name"
            if ! az group create --name "$group" --location "$location" "${scope[@]}" \
                --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess --output none; then
                printf 'ERROR: Cannot recreate group for workspace recovery: %s.\n' "$group" >&2
                failures=$((failures + 1))
                continue
            fi
            targets="${targets:+$targets$'\n'}$group"
        elif [[ "$exists" != true ]]; then
            printf 'ERROR: Invalid group existence response for %s: %s.\n' "$group" "$exists" >&2
            failures=$((failures + 1))
            continue
        fi
        if ! az monitor log-analytics workspace recover --resource-group "$group" \
            --workspace-name "$name" "${scope[@]}" --output none; then
            printf 'ERROR: Could not recover %s/%s for permanent deletion.\n' "$group" "$name" >&2
            failures=$((failures + 1))
            continue
        fi
    fi
    printf '\nPermanently deleting Log Analytics workspace %s/%s ...\n' "$group" "$name"
    if ! az monitor log-analytics workspace delete --resource-group "$group" \
        --workspace-name "$name" "${scope[@]}" --force true --yes --output none; then
        printf 'ERROR: Permanent workspace deletion failed: %s/%s.\n' "$group" "$name" >&2
        failures=$((failures + 1))
    fi
done <<< "$workspace_rows"

targets=$(printf '%s\n' "$targets" | LC_ALL=C sort -fu)
while IFS= read -r group; do
    [[ -n "$group" ]] || continue
    printf '\nDeleting %s ...\n' "$group"
    if ! az group delete --name "$group" "${scope[@]}" --yes --output none; then
        printf 'ERROR: Deletion failed for %s.\n' "$group" >&2
        failures=$((failures + 1))
        continue
    fi
    if ! exists=$(az group exists --name "$group" "${scope[@]}" --output tsv); then
        printf 'ERROR: Could not verify deletion of %s.\n' "$group" >&2
        failures=$((failures + 1))
    elif [[ "$exists" != false ]]; then
        printf 'ERROR: Expected %s to be absent; Azure returned %s.\n' "$group" "$exists" >&2
        failures=$((failures + 1))
    else
        printf 'Verified deleted: %s\n' "$group"
    fi
done <<< "$targets"

purge_resources() {
    local kind=$1 attempt pending rows group name location
    # Repeat discovery to handle eventual consistency and verify successful purges.
    for attempt in 1 2 3 4 5 6 7; do
        pending=$(discover_resources "$kind") || {
            printf 'ERROR: Could not list %s resources for purge verification.\n' "$kind" >&2
            return 1
        }
        [[ "$pending" != '[]' ]] || return 0
        if ((attempt == 7)); then
            printf 'ERROR: Soft-deleted %s resources remain after six purge attempts:\n%s\n' "$kind" "$pending" >&2
            return 1
        fi
        rows=$(jq -r '.[] | [.group, .name, .location] | @tsv' <<< "$pending") || return 1
        while IFS=$'\t' read -r group name location; do
            printf '\nPurging %s %s/%s (attempt %s/6) ...\n' "$kind" "$group" "$name" "$attempt"
            case "$kind" in
                cognitive)
                    if ! az cognitiveservices account purge --resource-group "$group" \
                        --name "$name" --location "$location" "${scope[@]}" --output none; then
                        printf 'ERROR: Foundry purge failed for %s/%s; will retry.\n' "$group" "$name" >&2
                    fi
                    ;;
                apim)
                    if ! az apim deletedservice purge --service-name "$name" --location "$location" \
                        "${scope[@]}" --output none; then
                        printf 'ERROR: APIM purge failed for %s/%s; will retry.\n' "$group" "$name" >&2
                    fi
                    ;;
            esac
        done <<< "$rows"
        sleep 10
    done
}

for kind in cognitive apim; do
    if ! purge_resources "$kind"; then
        failures=$((failures + 1))
    fi
done
for kind in workspace deleted-workspace; do
    if ! remaining=$(discover_resources "$kind"); then
        printf 'ERROR: Cannot verify permanent deletion of %s resources.\n' "$kind" >&2
        failures=$((failures + 1))
    elif [[ "$remaining" != '[]' ]]; then
        printf 'ERROR: Workshop %s resources remain:\n%s\n' "$kind" "$remaining" >&2
        failures=$((failures + 1))
    fi
done
if ((failures)); then
    fail "$failures cleanup operation(s) failed or could not be verified. See errors above."
fi
printf '\nWorkshop groups are absent and supported soft-deleted resources are purged.\n'
printf 'Azure may take additional time to release names. Local deployment records were retained.\n'
