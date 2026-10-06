# Commands for working on splitit. `just` lists them; `just <recipe>` runs one.
# Needs: .NET SDK (global.json), Docker, d2 (brew install d2), just (brew install just).

project := "src/SplitIt"
psql := "docker compose exec postgres psql -U splitit -d splitit"

# List the recipes
default:
    @just --list --unsorted

# ── Everyday ────────────────────────────────────────────────────────────────────

# Build the solution (warnings are errors)
build:
    dotnet build

# Run the tests; extra arguments go to dotnet test, e.g. `just test --filter RecordExpense`
test *args:
    dotnet test {{args}}

# Validate the event model and render docs/event-model/happy-path.d2 and .png
model:
    .venv/bin/python docs/event-model/generate.py

# Validate the event model only, writing nothing
model-check:
    .venv/bin/python docs/event-model/generate.py --check

# Start the local PostgreSQL container and wait until it is healthy
db:
    docker compose up -d --wait

# Run the app over HTTPS (the auth cookie is Secure), starting the database first;
# opens the sign-in screen once the app is healthy
run: db
    #!/usr/bin/env bash
    set -euo pipefail
    url=https://localhost:7194
    # /healthz answers 200 only when the app is up and reaches both databases.
    (
        for _ in $(seq 1 120); do
            if curl -skf "$url/healthz" > /dev/null 2>&1; then open "$url/sign-in"; exit 0; fi
            sleep 1
        done
        echo "the app did not become healthy within 2 minutes; not opening the browser" >&2
    ) &
    exec dotnet run --project {{project}} --launch-profile https

# Like `run`, but with dotnet watch: live browser reload on CSS/Razor changes, restart on C# changes
watch: db
    dotnet watch run --project {{project}} --launch-profile https

# Build the container image the way the codex platform does
image:
    docker build -t splitit:dev .

# ── Database ────────────────────────────────────────────────────────────────────

# Stop the database container, keeping its data
db-stop:
    docker compose stop

# Follow the database container's logs (Ctrl+C to stop)
logs:
    docker compose logs -f postgres

# Open a psql shell on the development database
psql:
    {{psql}}

# Show the event stream, oldest first
events:
    {{psql}} -c "select seq_id, stream_id, version, type, data from ledger.mt_events order by seq_id;"

# Rebuild stored projections (the group ledger) from the events, after changing how one folds
rebuild-projections: db
    dotnet run --project {{project}} -- projections rebuild

# Drop all domain data (events, projections, documents) — dev only; the app recreates the schema on its next start
[confirm("Drop the ledger schema and every event in it?")]
reset-ledger: db
    {{psql}} -c "drop schema if exists ledger cascade;"

# Add an EF Core migration for Identity (users, sign-in codes, keys), e.g. `just migration AddThing`
migration name:
    dotnet ef migrations add {{name}} --project {{project}} --context IdentityDb --output-dir Infrastructure/Identity/Migrations

# ── Inspecting the app ──────────────────────────────────────────────────────────

# List the HTTP endpoints Wolverine discovered, and the app's configuration
describe: db
    dotnet run --project {{project}} -- describe

# List the stored projections and their lifecycles
projections: db
    dotnet run --project {{project}} -- projections list

# ── One-off setup ───────────────────────────────────────────────────────────────

# First-time setup: .NET tools (dotnet-ef), the generator's Python venv, the HTTPS dev certificate
setup:
    dotnet tool restore
    python3 -m venv .venv
    .venv/bin/pip install --quiet pyyaml
    dotnet dev-certs https --trust

# Renew the HTTPS dev certificate (it expires yearly)
renew-cert:
    dotnet dev-certs https --clean
    dotnet dev-certs https --trust
