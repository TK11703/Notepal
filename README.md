# Notepal
This is a combination of .Net projects that will help you keep track of notes (hand-written or digital).

Capture pages with your camera, upload photos, PDFs or Word documents, and Notepal stores the original and uses an
**Azure AI Foundry agent** to transcribe the text. You can correct the transcription, view the original and the notes
side by side, and search everything you have captured. Every user only ever sees their own notes.

## Features

- **Capture**: live camera capture in the browser (`getUserMedia`), the device camera app on phones, or upload
  JPEG/PNG/WebP/GIF images, PDF and Word (`.docx`) files. Several files/photos become the pages of a single note.
- **Original storage**: the uploaded bytes are stored unchanged in PostgreSQL (`bytea`), alongside the extracted text.
- **AI OCR**: images (and scanned PDF pages) are sent to a Foundry agent (`gpt-4.1-mini` by default) that transcribes
  handwritten or printed text. Digital PDFs and Word files are parsed locally (PdfPig / Open XML SDK). Work runs in a
  background queue and resumes automatically after a restart.
- **Review & correct**: each page has tabs for **Original**, **Notes** and **Side by side**. Corrections are stored
  separately from the AI text, so you can revert or re-run extraction at any time.
- **Search**: PostgreSQL full-text search (stemmed, ranked, `websearch_to_tsquery` syntax: `"phrases"`, `or`, `-exclude`)
  over corrected text and titles, plus substring matching, with highlighted snippets.
- **Per-user isolation**: Entra ID sign-in. The API only accepts access tokens for its `access_as_user` scope and
  scopes every SQL statement to the caller's object id (`oid`).
- **Light / dark / auto theme** toggle (Bootstrap 5.3 colour modes, remembered per browser).

## Architecture

```
Browser ──(cookie, SignalR)──► Notepal.Web  (Blazor Web App, interactive server)
                                   │  OIDC sign-in, token cache, calls API on behalf of the user
                                   ▼  (internal ingress only, bearer token)
                               Notepal.Api  (ASP.NET Core 10 minimal API)
                                   │                     │
                         Npgsql (plain SQL)        Azure AI Foundry Agent Service
                                   ▼                     (managed identity)
                          PostgreSQL Flexible Server
```

| Project | Purpose |
| --- | --- |
| `src/Notepal.Web` | Blazor Web App: sign-in, upload/camera, viewer/editor, search, theme. Proxies original files from the API. |
| `src/Notepal.Api` | Minimal API with built-in validation, plain SQL data access (Npgsql) + embedded SQL migrations (`Data/Migrations/*.sql`), upload validation (extension **and** file signature), background text extraction, search. |
| `src/Notepal.Shared` | DTOs (with validation attributes) and limits shared by both apps. |
| `tests/Notepal.Api.Tests` | Integration tests (WebApplicationFactory + Testcontainers PostgreSQL) incl. cross-user isolation. |
| `infra/` | Bicep for Azure and the Entra ID setup script. |

### API

All endpoints (except `/healthz`) require a token with the `access_as_user` scope. Invalid input returns `400` with a
validation problem details body.

| Method & route | Description |
| --- | --- |
| `GET /api/notes?page=&pageSize=` | The caller's notes, newest first |
| `POST /api/notes` | `multipart/form-data` with `title` and one or more `files` (max 20 files, 20 MB each) |
| `GET /api/notes/{id}` | Note with its pages and text |
| `PUT /api/notes/{id}` | Rename |
| `DELETE /api/notes/{id}` | Delete note, pages and originals |
| `GET /api/notes/{id}/pages/{pageId}/original` | Original file |
| `PUT /api/notes/{id}/pages/{pageId}/text` | Save corrected text (sending the AI text back clears the correction) |
| `POST /api/notes/{id}/pages/{pageId}/reprocess` | Re-run text extraction |
| `GET /api/search?q=&page=&pageSize=` | Full-text search; matches in `snippet` are wrapped in `⟦ ⟧` |

## Run locally

Prerequisites: .NET 10 SDK, Docker, Azure CLI.

1. Start PostgreSQL: `docker compose up -d`
2. Create the app registrations (once): `./infra/scripts/setup-entra.sh register` and note the printed ids.
3. Create a client secret for local development: `./infra/scripts/setup-entra.sh dev-secret`
4. Configure user secrets:
   ```bash
   cd src/Notepal.Api
   dotnet user-secrets set "AzureAd:TenantId" "<tenant-id>"
   dotnet user-secrets set "AzureAd:ClientId" "<api-client-id>"
   # optional – enables OCR (needs `az login` and the "Azure AI User" role on the Foundry project)
   dotnet user-secrets set "Ocr:ProjectEndpoint" "https://<account>.services.ai.azure.com/api/projects/<project>"

   cd ../Notepal.Web
   dotnet user-secrets set "AzureAd:TenantId" "<tenant-id>"
   dotnet user-secrets set "AzureAd:ClientId" "<web-client-id>"
   dotnet user-secrets set "AzureAd:ClientSecret" "<secret from step 3>"
   dotnet user-secrets set "NotepalApi:Scopes:0" "api://<api-client-id>/access_as_user"
   ```
5. Run both apps (`dotnet run --launch-profile https` in `src/Notepal.Api` and `src/Notepal.Web`) and open
   <https://localhost:7137>. The API applies pending SQL migrations on start-up.

Without `Ocr:ProjectEndpoint` everything works except image OCR: such pages are marked *Failed* with an explanation
and you can type the notes yourself.

Run the tests (Docker required): `dotnet test`

## Deploy to Azure

The Bicep template (`infra/main.bicep`) uses the cheapest options that fit the workload:

| Resource | SKU | Notes |
| --- | --- | --- |
| Container Apps environment | Consumption | Both apps: 0.25 vCPU / 0.5 GiB, **scale to zero** (min 0, max 1 replica). The API uses internal ingress only. |
| PostgreSQL Flexible Server | Burstable **B1ms**, 32 GB, no HA, 7-day LRS backups | Public access limited to Azure services, TLS required. |
| Container Registry | Basic | Images pulled with managed identities (no admin user). |
| Azure AI Foundry | AIServices S0 + project, `gpt-4.1-mini` **GlobalStandard** | Pay per token; the OCR agent is created automatically on first use. Key auth disabled. |
| Log Analytics | PerGB2018, 30 days, 1 GB/day cap | |

The web app authenticates to Entra ID with its managed identity (federated credential) – no client secrets are stored
in Azure. The API reaches Foundry with its own managed identity (`Azure AI User` role).

### First-time setup

1. `./infra/scripts/setup-entra.sh register` – note the API/Web client ids.
2. Create an Entra app/service principal for GitHub Actions with a federated credential for this repository and grant it
   **Contributor** and **Role Based Access Control Administrator** (the template creates role assignments) on the
   subscription or target resource group.
3. Configure the repository (Settings → Secrets and variables → Actions):
   - Secrets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `POSTGRES_ADMIN_PASSWORD`
   - Variables: `AZURE_RESOURCE_GROUP`, `AZURE_LOCATION`, `NOTEPAL_API_CLIENT_ID`, `NOTEPAL_WEB_CLIENT_ID`,
     optionally `AZURE_FOUNDRY_LOCATION` (a region offering the model as GlobalStandard).
4. Run the **Deploy to Azure** workflow. It deploys the infrastructure, builds both images in ACR and deploys the
   container apps.
5. Once, after the first deployment, run the command printed in the workflow summary:
   `./infra/scripts/setup-entra.sh finalize <web-url> <web-identity-principal-id>` – this registers the redirect URI
   and trusts the web app's managed identity.

### Operational notes

- Scale to zero means the first request after idle time has a cold start of a few seconds. After a restart the
  in-memory token cache and data-protection keys are gone, so users are sent through Entra sign-in again
  (normally silent with SSO).
- Background OCR runs inside the API container; unfinished pages are re-queued automatically when it starts again.
- Legacy binary Word files (`.doc`) are not supported – save them as `.docx` or PDF. Scanned PDFs are OCR'd from the
  largest image embedded on each page.
