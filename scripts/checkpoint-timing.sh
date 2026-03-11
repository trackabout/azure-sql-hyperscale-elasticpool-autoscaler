#!/usr/bin/env bash
set -euo pipefail

# Checkpoint Timing Test Script
# Measures CHECKPOINT execution time across all Hyperscale elastic pools
# in the specified Azure subscription to inform CommandTimeout values.
#
# Prerequisites:
#   - az login (Azure AD / Entra ID authentication)
#   - Your AAD identity must have db_owner or sufficient permissions on all pool databases
#   - sqlcmd, jq, python3 on PATH

SUBSCRIPTION="${AZURE_SUBSCRIPTION:-$(az account show --query name -o tsv)}"
TIMESTAMP=$(date +"%Y-%m-%d-%H%M%S")
CSV_FILE="checkpoint-timing-${TIMESTAMP}.csv"

# Check prerequisites
for cmd in az sqlcmd jq python3; do
    if ! command -v "$cmd" &>/dev/null; then
        echo "ERROR: $cmd is required but not found in PATH" >&2
        exit 1
    fi
done

# Verify az login
if ! az account show &>/dev/null; then
    echo "ERROR: Not logged in to Azure. Run 'az login' first." >&2
    exit 1
fi

# Set subscription
az account set --subscription "$SUBSCRIPTION"
echo "Using subscription: $SUBSCRIPTION"
echo ""

# CSV header
echo "Server,Region,Pool,Database,Duration_ms,Status" > "$CSV_FILE"

# Discover all SQL servers in the subscription
echo "Discovering SQL servers..."
SERVERS=$(az sql server list --query "[].{name:name, rg:resourceGroup, location:location}" -o json)
SERVER_COUNT=$(echo "$SERVERS" | jq length)
echo "Found $SERVER_COUNT SQL server(s)"
echo ""

# Iterate servers
echo "$SERVERS" | jq -c '.[]' | while read -r server; do
    SERVER_NAME=$(echo "$server" | jq -r '.name')
    RESOURCE_GROUP=$(echo "$server" | jq -r '.rg')
    REGION=$(echo "$server" | jq -r '.location')
    FQDN="${SERVER_NAME}.database.windows.net"

    echo "=== Server: $SERVER_NAME ($REGION) ==="

    # Discover elastic pools for this server
    POOLS=$(az sql elastic-pool list --server "$SERVER_NAME" --resource-group "$RESOURCE_GROUP" --query "[?sku.tier=='Hyperscale'].name" -o json)
    POOL_COUNT=$(echo "$POOLS" | jq length)
    echo "  Found $POOL_COUNT elastic pool(s)"

    echo "$POOLS" | jq -r '.[]' | while read -r POOL_NAME; do
        echo "  --- Pool: $POOL_NAME ---"

        # Discover databases in this pool (exclude master)
        DBS=$(az sql db list --server "$SERVER_NAME" --resource-group "$RESOURCE_GROUP" \
            --elastic-pool "$POOL_NAME" --query "[?name!='master'].name" -o json)
        DB_COUNT=$(echo "$DBS" | jq length)
        echo "    Found $DB_COUNT database(s)"

        echo "$DBS" | jq -r '.[]' | while read -r DB_NAME; do
            # Time the CHECKPOINT
            START_MS=$(python3 -c 'import time; print(int(time.time() * 1000))')

            SQLCMD_OUTPUT=$(timeout 120 sqlcmd -S "$FQDN" -d "$DB_NAME" -G -Q "CHECKPOINT" -h -1 -W 2>&1) && STATUS="OK" || STATUS="FAIL"

            END_MS=$(python3 -c 'import time; print(int(time.time() * 1000))')
            DURATION=$((END_MS - START_MS))

            # Check for read-only errors
            if [[ "$STATUS" == "FAIL" && "$SQLCMD_OUTPUT" == *"read-only"* ]]; then
                STATUS="READONLY"
            fi

            # Record result
            echo "    $DB_NAME: ${DURATION}ms ($STATUS)"
            echo "$SERVER_NAME,$REGION,$POOL_NAME,$DB_NAME,$DURATION,$STATUS" >> "$CSV_FILE"

            if [[ "$STATUS" == "FAIL" || "$STATUS" == "READONLY" ]]; then
                echo "      Error: $(echo "$SQLCMD_OUTPUT" | head -1)"
            fi
        done
    done
    echo ""
done

# Print summary table
echo ""
echo "=========================================="
echo "  CHECKPOINT TIMING SUMMARY"
echo "=========================================="
printf "%-20s %-12s %-25s %-30s %10s  %s\n" "SERVER" "REGION" "POOL" "DATABASE" "DURATION" "STATUS"
printf "%-20s %-12s %-25s %-30s %10s  %s\n" "------" "------" "----" "--------" "--------" "------"

# Re-read CSV (skip header) for summary
tail -n +2 "$CSV_FILE" | while IFS=',' read -r srv rgn pool db dur stat; do
    printf "%-20s %-12s %-25s %-30s %8sms  %s\n" "$srv" "$rgn" "$pool" "$db" "$dur" "$stat"
done

echo ""
echo "Results saved to: $CSV_FILE"
echo ""

# Print stats
TOTAL=$(tail -n +2 "$CSV_FILE" | wc -l | tr -d ' ')
OK_COUNT=$(grep -c ',OK$' "$CSV_FILE" || true)
FAIL_COUNT=$(grep -c ',FAIL$' "$CSV_FILE" || true)
READONLY_COUNT=$(grep -c ',READONLY$' "$CSV_FILE" || true)

echo "Total: $TOTAL databases | OK: $OK_COUNT | FAIL: $FAIL_COUNT | READONLY: $READONLY_COUNT"

if [[ "$OK_COUNT" -gt 0 ]]; then
    # Calculate min/max/avg from OK results only
    echo ""
    echo "Timing stats (OK only):"
    tail -n +2 "$CSV_FILE" | grep ',OK$' | awk -F',' '
        BEGIN { min=999999; max=0; sum=0; n=0 }
        {
            n++; sum+=$5;
            if ($5<min) min=$5;
            if ($5>max) max=$5
        }
        END {
            printf "  Min: %dms\n  Max: %dms\n  Avg: %dms\n", min, max, sum/n
        }'
fi
